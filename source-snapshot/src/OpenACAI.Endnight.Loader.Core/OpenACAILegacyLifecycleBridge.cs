// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned lifecycle bridge for legacy RedLoader/SonsSdk event contracts.

using System;
using OpenACAI.Loader;
using OpenACAI.Sons;
using RedLoader;
using RedLoader.Support.Preferences;
using SonsSdk;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenACAILegacyLifecycleBridge
    {
        internal static SdkEntryPoint CreateSdkEntryPoint()
        {
            return new SdkEntryPoint();
        }

        internal static void PublishPreModsLoaded()
        {
            GlobalEvents.OnPreModsLoaded.Invoke();
        }

        internal static void PublishApplicationStart()
        {
            GlobalEvents.OnApplicationStart.Invoke();
        }

        internal static void PreparePostUnityCompatibility()
        {
            OpenACAIEndnightRuntime.TraceCompat("RegisterTypeInIl2Cpp.SetReady");
            RegisterTypeInIl2Cpp.SetReady();
            OpenACAIEndnightRuntime.TraceCompat("GlobalEvents.MelonHarmonyInit.Invoke");
            GlobalEvents.MelonHarmonyInit.Invoke();
            OpenACAIEndnightRuntime.TraceCompat("UnityMappers.RegisterMappers");
            UnityMappers.RegisterMappers();
        }

        internal static void InitializeSonsSdkAfterUnity(SdkEntryPoint processor, RedLoaderHostResult hostResult, Action<string> addStartupWarning)
        {
            string initializationError = null;
            OpenACAILegacyRedLoaderCompatibility.PublishSonsSdkShimState(hostResult, null);
            if (hostResult != null && hostResult.InitializeSonsSdk)
            {
                var ownsInitialization = string.Equals(hostResult.Mode, "openacai-managed", StringComparison.Ordinal);
                void OnInitializationCompleted(Exception exception)
                {
                    if (OpenACAILoaderCapabilities.StartupFailed)
                    {
                        return;
                    }

                    var failure = exception == null ? null : "Shared SonsSdk initialization failed: " + exception.Message;
                    OpenACAILegacyRedLoaderCompatibility.PublishSonsSdkShimState(hostResult, failure, initializationCompleted: true);
                    try
                    {
                        if (failure != null)
                        {
                            addStartupWarning?.Invoke(failure);
                        }
                    }
                    finally
                    {
                        OpenACAISonsSaveLifecycle.RefreshLegacySonsSdkRelay();
                        OpenACAIEndnightRuntime.RequestLoaderStateRefresh("sonssdk-initialization-completed");
                    }
                }

                if (ownsInitialization)
                    OpenACAISonsSdkInitialization.Instance.Completed += OnInitializationCompleted;
                else
                    SdkEvents.OnSdkInitializationCompleted.Subscribe(OnInitializationCompleted, int.MinValue);

                var createModManagerUi = OpenACAILegacyRedLoaderCompatibilityPatches.IsRedLoaderModsButtonEnabled();
                OpenACAIEndnightRuntime.TraceCompat("processor.InitAfterUnity legacy-manager=" + createModManagerUi);
                using (OpenACAITimings.Measure("loader.sonssdk", "post-unity-init"))
                {
                    TryRun("SonsSdk post-Unity initialization", () =>
                    {
                        if (ownsInitialization)
                            MainInitializer.SetInitializationDriver(OpenACAISonsSdkInitialization.Instance);
                        processor.InitAfterUnity(createModManagerUi);
                    }, warning =>
                    {
                        initializationError = warning;
                        var completed = ownsInitialization
                            ? OpenACAISonsSdkInitialization.Instance.IsCompleted
                            : SdkEvents.OnSdkInitializationCompleted.Disposed;
                        if (!completed)
                        {
                            addStartupWarning?.Invoke(warning);
                        }
                    });
                }

                if (initializationError == null && !_sonsSdkGameStartObserverInstalled)
                {
                    SdkEvents.OnGameStart.Subscribe(OnObservedSonsSdkGameStart, int.MinValue);
                    _sonsSdkGameStartObserverInstalled = true;
                }
            }
            else
            {
                OpenACAIEndnightRuntime.TraceCompat("processor.InitAfterUnity skipped by OpenACAI host mode");
            }

            if (initializationError != null)
            {
                OpenACAILegacyRedLoaderCompatibility.PublishSonsSdkShimState(hostResult, initializationError);
            }
        }

        internal static void PublishApplicationLateStart()
        {
            GlobalEvents.OnApplicationLateStart.Invoke();
        }

        internal static void PublishUpdate()
        {
            OpenACAIUnityLifecycle.InvokeLifecycle("RedLoader.Coroutines", () => OpenACAILegacyRedLoaderCompatibility.DrainQueuedCoroutines());
            GlobalEvents.OnUpdate.Invoke();
        }

        internal static void PublishFixedUpdate()
        {
            GlobalEvents.OnFixedUpdate.Invoke();
        }

        internal static void PublishLateUpdate()
        {
            GlobalEvents.OnLateUpdate.Invoke();
        }

        internal static void PublishGui()
        {
            GlobalEvents.OnGUI.Invoke();
        }

        internal static void PublishApplicationQuit()
        {
            GlobalEvents.OnApplicationQuit.Invoke();
        }

        internal static void PublishApplicationDefiniteQuit()
        {
            GlobalEvents.OnApplicationDefiniteQuit.Invoke();
        }

        internal static void PublishSceneLoaded(int buildIndex, string name)
        {
            GlobalEvents.OnSceneWasLoaded.Invoke(buildIndex, name);
        }

        internal static void PublishSceneUnloaded(int buildIndex, string name)
        {
            GlobalEvents.OnSceneWasUnloaded.Invoke(buildIndex, name);
        }

        internal static void PublishSceneInitialized(int buildIndex, string name)
        {
            GlobalEvents.OnSceneWasInitialized.Invoke(buildIndex, name);
        }

        internal static void PublishBeforeLoadSave()
        {
            TryRun("SonsSdk BeforeLoadSave", () => SdkEvents.BeforeLoadSave.Invoke(), null);
        }

        internal static void PublishAfterLoadSave()
        {
            TryRun("SonsSdk AfterLoadSave", () => SdkEvents.AfterLoadSave.Invoke(), null);
        }

        internal static void PublishBeforeSave()
        {
            TryRun("SonsSdk BeforeSaveLoading", () => SdkEvents.BeforeSaveLoading.Invoke(), null);
        }

        internal static void PublishAfterSave(bool savePlayerOnly)
        {
            TryRun("SonsSdk AfterSaveLoading", () => SdkEvents.AfterSaveLoading.Invoke(savePlayerOnly), null);
        }

        internal static void SavePreferences()
        {
            try
            {
                ConfigSystem.Save();
            }
            catch (Exception ex)
            {
                OpenACAIEndnightRuntime.LogSource.LogWarning("RedLoader preferences save failed: " + ex.Message);
            }
        }

        private static void TryRun(string operationName, Action operation, Action<string> addStartupWarning)
        {
            try
            {
                operation();
            }
            catch (Exception ex)
            {
                addStartupWarning?.Invoke(operationName + " failed: " + ex.Message);
            }
        }

        private static void OnObservedSonsSdkGameStart()
        {
            OpenACAIEndnightRuntime.TraceCompat("Observed SonsSdk OnGameStart from compatibility event source.");
        }

        private static bool _sonsSdkGameStartObserverInstalled;
    }
}
