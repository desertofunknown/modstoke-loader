// SPDX-License-Identifier: GPL-3.0-or-later
// SDK initialization fallback adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RedLoader;
using RedLoader.Utils;
using global::Sons.Loading;
using SonsSdk;
using SonsSdk.AssetImporting;
using SonsSdk.Building;
using SonsSdk.Networking;
using SonsSdk.Private;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;
using Color = System.Drawing.Color;

namespace Modstoke.Loader;

internal sealed class SdkInitializationFallback : ISdkInitializationDriver
{
    private static readonly SdkInitializationFallback Driver = new();
    private static bool _dedicatedInitializationStarted;
    private static bool _completionStarted;

    internal static ISdkInitializationDriver Create() => Driver;

    public bool InitializationFailed => MainInitializer._initializationFailed;

    public void InitTitleScene()
    {
        if (LoaderEnvironment.IsDedicatedServer)
            return;

        if (MainInitializer._initializationStarted || SdkEvents.OnSdkInitializationCompleted.Disposed)
        {
            return;
        }
        MainInitializer._initializationStarted = true;

        try
        {
            RunInitialization(InitCoro()).RunCoro();
        }
        catch (Exception exception)
        {
            MainInitializer.ReportInitializationFailure(exception);
        }
    }

    public void InitDedicatedServer()
    {
        if (MainInitializer._initializationStarted || SdkEvents.OnSdkInitializationCompleted.Disposed)
        {
            return;
        }

        MainInitializer._initializationStarted = true;
        _dedicatedInitializationStarted = true;
        try
        {
            // Keep synchronous service setup inside the server bootstrap prefix.
            var services = InitSystems();
            if (!services.IsCompleted)
            {
                RunInitialization(WaitForServices(services)).RunCoro();
                return;
            }

            services.GetAwaiter().GetResult();
            if (!MainInitializer._initializationFailed)
            {
                MainInitializer._initializationFinished = true;
                TryCompleteInitialization();
            }
        }
        catch (Exception exception)
        {
            MainInitializer.ReportInitializationFailure(exception);
            throw;
        }
    }

    public void CompleteEventSetup()
    {
        MainInitializer._eventSetupCompleted = true;
        TryCompleteInitialization();
    }

    private static void TryCompleteInitialization()
    {
        if (!MainInitializer._eventSetupCompleted || !MainInitializer._initializationFinished ||
            MainInitializer._initializationFailed || _completionStarted || SdkEvents.OnSdkInitializationCompleted.Disposed)
            return;

        // Claim completion before calling mod code, which may reenter event setup.
        _completionStarted = true;
        try
        {
            if (_dedicatedInitializationStarted)
            {
                var subscribers = SdkEvents.OnSdkInitialized.BeginInvocation();
                foreach (var subscriber in subscribers)
                {
                    if (MainInitializer._initializationFailed || SdkEvents.OnSdkInitializationCompleted.Disposed)
                        return;

                    SdkEvents.OnSdkInitialized.InvokeSubscriber(subscriber, callback => callback());
                }

                if (MainInitializer._initializationFailed || SdkEvents.OnSdkInitializationCompleted.Disposed)
                    return;

                SdkEvents.OnSdkLateInitialized.Invoke();
            }

            if (!MainInitializer._initializationFailed && !SdkEvents.OnSdkInitializationCompleted.Disposed)
                SdkEvents.OnSdkInitializationCompleted.Invoke(null);
        }
        catch (Exception exception)
        {
            MainInitializer.ReportInitializationFailure(exception);
        }
    }

    public void ReportInitializationFailure(Exception exception)
    {
        if (SdkEvents.OnSdkInitializationCompleted.Disposed)
        {
            return;
        }

        MainInitializer._initializationFailed = true;
        SdkEvents.OnSdkInitializationCompleted.Invoke(exception);
        try
        {
            RLog.Error($"Shared SDK initialization failed: {exception}");
        }
        catch
        {
            // Diagnostic listeners cannot prevent startup failure cleanup.
        }

        try
        {
            if (!LoaderEnvironment.IsDedicatedServer)
                GlobalOverlays.ToggleOverlay(false);
        }
        catch
        {
            // The overlay may be only partially constructed; keep the original failure.
        }
    }

    private static IEnumerator RunInitialization(IEnumerator initialization)
    {
        // Advance this SDK's managed child routines here so their failures reach the host.
        var routines = new Stack<IEnumerator>();
        routines.Push(initialization);
        try
        {
            while (routines.Count > 0)
            {
                if (MainInitializer._initializationFailed)
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
                catch (Exception exception)
                {
                    MainInitializer.ReportInitializationFailure(exception);
                    yield break;
                }

                yield return current;
            }

            if (!MainInitializer._initializationFailed)
            {
                MainInitializer._initializationFinished = true;
                TryCompleteInitialization();
            }
        }
        finally
        {
            while (routines.Count > 0)
            {
                var routine = routines.Pop();
                try
                {
                    (routine as IDisposable)?.Dispose();
                }
                catch (Exception exception)
                {
                    MainInitializer.ReportInitializationFailure(exception);
                }
            }

            if (!MainInitializer._initializationFinished && !SdkEvents.OnSdkInitializationCompleted.Disposed)
            {
                MainInitializer.ReportInitializationFailure(new OperationCanceledException("Shared SDK initialization stopped before completion."));
            }
        }
    }

    private static IEnumerator AddProgress(float amount)
    {
        GlobalOverlays.ProgressBar.AddProgress(amount);
        yield return null;
    }

    private static IEnumerator LoadAllCatalogs()
    {
        foreach (var mod in SonsMod.RegisteredMods.ToArray())
        {
            if (!mod.Registered)
                continue;

            if (mod.CatalogLoader != null)
            {
                yield return mod.CatalogLoader(mod);
                continue;
            }

            string[] catalogs;
            try
            {
                var modDataFolder = LoaderEnvironment.GetModDataPath(mod);
                if (!modDataFolder.DirectoryExists())
                {
                    continue;
                }

                catalogs = modDataFolder.GetFiles("catalog*.json").ToArray();
            }
            catch (Exception exception)
            {
                AssetLoaders.ReportAssetError($"Couldn't enumerate addressables catalogs for {mod.ModAssembly.FullName}.", exception);
                continue;
            }

            foreach (var catalog in catalogs)
            {
                if (!mod.Registered)
                    break;
                yield return LoadCatalog(catalog);
            }
        }
    }

    private static IEnumerator LoadCatalog(string catalog)
    {
        AsyncOperationHandle<IResourceLocator> operation;
        try
        {
            RLog.Msg(Color.Orange, "Loading addressables catalog " + catalog);
            operation = Addressables.LoadContentCatalogAsync(catalog, false);
        }
        catch (Exception exception)
        {
            AssetLoaders.ReportAssetError($"Couldn't start loading addressables catalog {catalog}.", exception);
            yield break;
        }

        try
        {
            while (true)
            {
                yield return null;
                try
                {
                    if (!operation.IsValid())
                    {
                        AssetLoaders.ReportAssetError($"Addressables catalog {catalog} returned an invalid operation handle.");
                        break;
                    }

                    if (!operation.IsDone)
                    {
                        continue;
                    }

                    if (operation.Status != AsyncOperationStatus.Succeeded)
                    {
                        AssetLoaders.ReportAssetError($"Couldn't load addressables catalog {catalog}: {operation.OperationException}");
                    }
                }
                catch (Exception exception)
                {
                    AssetLoaders.ReportAssetError($"Couldn't observe addressables catalog {catalog}.", exception);
                }

                break;
            }
        }
        finally
        {
            try
            {
                if (operation != null && operation.IsValid())
                {
                    Addressables.Release(operation);
                }
            }
            catch (Exception exception)
            {
                AssetLoaders.ReportAssetError($"Couldn't release the operation for addressables catalog {catalog}.", exception);
            }
        }
    }

    private static IEnumerator InitCoro()
    {
        yield return LoadAllModBundles();
        var loadCatalogsTask = LoadAllCatalogs();
        
        GameResources.Load();

        yield return SdkInitialization.InitializeUiPrefabs();
        
        yield return AddProgress(0.15f);

        yield return loadCatalogsTask;

        yield return WaitForServices(InitSystems());
        
        yield return AddProgress(0.15f);

        var subs = SdkEvents.OnSdkInitialized.BeginInvocation();
        for (var i = 0; i < subs.Length; i++)
        {
            SdkEvents.OnSdkInitialized.InvokeSubscriber(subs[i], callback => callback());
            
            yield return AddProgress(0.4f / subs.Length);
        }

        SdkEvents.OnSdkLateInitialized.Invoke();
        GlobalOverlays.ToggleOverlay(false);
    }

    private static Task InitSystems()
    {
        if (!LoaderEnvironment.IsDedicatedServer)
        {
            global::SonsSdk.GlobalInput.Init();
            GenericModalDialog.Setup();
            TooltipProvider.Setup();
            SonsUiTools.Init();
        }

        SonsSaveTools.Init();
        global::Modstoke.Sons.WorldRecords.Initialize();
        global::SonsSdk.GameManagers.Init();
        global::SonsSdk.Networking.Packets.Init();
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

    private static IEnumerator LoadAllModBundles()
    {
        var bundles = SonsMod.RegisteredMods.SelectMany(x => x.AssetBundleAttrs).ToArray();

        for (var i = 0; i < bundles.Length; i++)
        {
            var bundle = bundles[i];
            try
            {
                if (bundle.Mod.Registered)
                    bundle.LoadBundle();
            }
            catch (Exception exception)
            {
                AssetLoaders.ReportAssetError($"Couldn't initialize asset bundle {bundle.AssetBundleName} for {bundle.Mod.ModAssembly.FullName}.", exception);
            }

            yield return AddProgress(0.3f / bundles.Length);
        }
    }
}
