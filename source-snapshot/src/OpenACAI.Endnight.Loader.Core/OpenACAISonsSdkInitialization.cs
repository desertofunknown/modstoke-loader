// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Owned startup sequencing over the existing SDK services and public events.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RedLoader;
using RedLoader.Utils;
using Sons.Loading;
using SonsSdk;
using SonsSdk.AssetImporting;
using SonsSdk.Building;
using SonsSdk.Networking;
using SonsSdk.Private;
using UnityEngine.SceneManagement;

namespace OpenACAI.Endnight.Loader
{
    internal sealed class OpenACAISonsSdkInitialization : ISdkInitializationDriver
    {
        internal static readonly OpenACAISonsSdkInitialization Instance = new();

        private bool _started;
        private bool _finished;
        private bool _eventSetupCompleted;

        private OpenACAISonsSdkInitialization() { }

        public bool InitializationFailed { get; private set; }

        internal bool IsCompleted { get; private set; }
        internal event Action<Exception> Completed;

        public void InitTitleScene()
        {
            if (_started || IsCompleted)
                return;
            _started = true;

            try
            {
                OpenACAILegacyRedLoaderCompatibility.StartObservedCoroutine(
                    RunInitialization(InitializeClient()), ReportInitializationFailure);
            }
            catch (Exception error) { ReportInitializationFailure(error); }
        }

        public void InitDedicatedServer()
        {
            if (_started || IsCompleted)
                return;
            _started = true;

            try
            {
                // Keep synchronous service setup inside the server bootstrap prefix.
                var services = InitializeServices();
                if (!services.IsCompleted)
                {
                    OpenACAILegacyRedLoaderCompatibility.StartObservedCoroutine(
                        RunInitialization(WaitForServices(services)), ReportInitializationFailure);
                    return;
                }

                services.GetAwaiter().GetResult();
                if (!InitializationFailed)
                {
                    _finished = true;
                    TryCompleteInitialization();
                }
            }
            catch (Exception error)
            {
                ReportInitializationFailure(error);
                throw;
            }
        }

        public void CompleteEventSetup()
        {
            _eventSetupCompleted = true;
            TryCompleteInitialization();
        }

        private void TryCompleteInitialization()
        {
            if (!IsCompleted && _eventSetupCompleted && _finished)
                PublishCompletion(null);
        }

        private void PublishCompletion(Exception exception)
        {
            IsCompleted = true;
            var completed = Completed;
            Completed = null;

            // Claim compatibility delivery before callbacks, without using it as startup state.
            var subscribers = SdkEvents.OnSdkInitializationCompleted.BeginInvocation();
            if (completed != null)
            {
                foreach (Action<Exception> callback in completed.GetInvocationList())
                {
                    try { callback(exception); }
                    catch (Exception error) { ReportError("SDK completion observer failed", error); }
                }
            }

            foreach (var subscriber in subscribers)
                SdkEvents.OnSdkInitializationCompleted.InvokeSubscriber(subscriber, callback => callback(exception));
        }

        public void ReportInitializationFailure(Exception exception)
        {
            if (IsCompleted)
                return;

            InitializationFailed = true;
            PublishCompletion(exception);
            ReportError("Shared SDK initialization failed", exception);

            try { GlobalOverlays.ToggleOverlay(false); }
            catch { /* Preserve the startup failure if the overlay is only partially constructed. */ }
        }

        private IEnumerator RunInitialization(IEnumerator initialization)
        {
            // Own managed children so startup failures and disposal reach catalog handles.
            var routines = new Stack<IEnumerator>();
            routines.Push(initialization);
            try
            {
                while (routines.Count > 0)
                {
                    if (InitializationFailed)
                        yield break;

                    object current;
                    try
                    {
                        var routine = routines.Peek();
                        if (!routine.MoveNext())
                        {
                            routines.Pop();
                            (routine as IDisposable)?.Dispose();
                            continue;
                        }

                        current = routine.Current;
                        if (current is IEnumerator child)
                        {
                            routines.Push(child);
                            continue;
                        }
                    }
                    catch (Exception error)
                    {
                        ReportInitializationFailure(error);
                        yield break;
                    }

                    yield return current;
                }

                if (!InitializationFailed)
                {
                    _finished = true;
                    TryCompleteInitialization();
                }
            }
            finally
            {
                while (routines.Count > 0)
                {
                    var routine = routines.Pop();
                    try { (routine as IDisposable)?.Dispose(); }
                    catch (Exception error) { ReportInitializationFailure(error); }
                }

                if (!_finished && !IsCompleted)
                    ReportInitializationFailure(new OperationCanceledException("Shared SDK initialization stopped before completion."));
            }
        }

        private static IEnumerator InitializeClient()
        {
            yield return LoadBundles();
            var catalogs = LoadCatalogs();

            GameResources.Load();
            SceneManager.LoadScene(SonsSceneManager.OptionsMenuSceneName, LoadSceneMode.Additive);
            yield return null;
            SUI.SUI.InitPrefabs();
            SceneManager.UnloadScene(SonsSceneManager.OptionsMenuSceneName);

            yield return AddProgress(0.15f);
            yield return catalogs;
            yield return WaitForServices(InitializeServices());
            yield return AddProgress(0.15f);

            var subscribers = SdkEvents.OnSdkInitialized.BeginInvocation();
            for (var i = 0; i < subscribers.Length; i++)
            {
                SdkEvents.OnSdkInitialized.InvokeSubscriber(subscribers[i], callback => callback());
                yield return AddProgress(0.4f / subscribers.Length);
            }

            SdkEvents.OnSdkLateInitialized.Invoke();
            GlobalOverlays.ToggleOverlay(false);
        }

        private static Task InitializeServices()
        {
            if (!LoaderEnvironment.IsDedicatedServer)
            {
                GlobalInput.Init();
                GenericModalDialog.Setup();
                TooltipProvider.Setup();
                SonsUiTools.Init();
            }

            SonsSaveTools.Init();
            GameManagers.Init();
            Packets.Init();
            EntityManager.Init();
            AssetImportingInitializer.Init();
            return CustomBlueprintManager.Init();
        }

        private static IEnumerator WaitForServices(Task services)
        {
            while (!services.IsCompleted)
                yield return null;

            services.GetAwaiter().GetResult();
        }

        private static IEnumerator LoadBundles()
        {
            var bundles = SonsMod.RegisteredMods.SelectMany(mod => mod.AssetBundleAttrs).ToArray();
            for (var i = 0; i < bundles.Length; i++)
            {
                var bundle = bundles[i];
                try
                {
                    if (bundle.Mod.Registered)
                        bundle.LoadBundle();
                }
                catch (Exception error)
                {
                    ReportError("Couldn't initialize asset bundle " + bundle.AssetBundleName
                        + " for " + bundle.Mod.ModAssembly.FullName + ".", error);
                }

                yield return AddProgress(0.3f / bundles.Length);
            }
        }

        private static IEnumerator LoadCatalogs()
        {
            foreach (var mod in SonsMod.RegisteredMods.ToArray())
            {
                if (!mod.Registered)
                    continue;

                yield return mod.CatalogLoader != null
                    ? mod.CatalogLoader(mod)
                    : OpenACAIRedLoaderCatalogs.LoadCatalogs(mod);
            }
        }

        private static IEnumerator AddProgress(float amount)
        {
            GlobalOverlays.ProgressBar.AddProgress(amount);
            yield return null;
        }

        private static void ReportError(string message, Exception error)
        {
            try { RLog.Error(message + ": " + error); }
            catch { /* Diagnostic listeners cannot change startup or cleanup outcomes. */ }
        }
    }
}
