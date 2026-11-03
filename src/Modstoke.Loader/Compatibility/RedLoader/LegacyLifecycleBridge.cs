// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Modstoke-owned lifecycle bridge for legacy RedLoader/SonsSdk event contracts.

using System;
using Modstoke.Loader;
using Modstoke.Sons;
using RedLoader;
using RedLoader.Support.Preferences;
using SonsSdk;

namespace Modstoke.Loader
{
    internal static class LegacyLifecycleBridge
    {
        internal static global::SonsSdk.SdkEntryPoint CreateSdkEntryPoint()
        {
            return new global::SonsSdk.SdkEntryPoint();
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
            // Mods can register native save interfaces before the title-scene SDK pass.
            Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<ModSaveData>();
            RuntimeEntryPoint.TraceCompat("RegisterTypeInIl2Cpp.SetReady");
            RegisterTypeInIl2Cpp.SetReady();
            RuntimeEntryPoint.TraceCompat("GlobalEvents.MelonHarmonyInit.Invoke");
            GlobalEvents.MelonHarmonyInit.Invoke();
            RuntimeEntryPoint.TraceCompat("UnityMappers.RegisterMappers");
            UnityMappers.RegisterMappers();
        }

        internal static void InitializeSonsSdkAfterUnity(global::SonsSdk.SdkEntryPoint processor, RedLoaderHostResult hostResult, Action<string> addStartupWarning)
        {
            string initializationError = null;
            LegacyRedLoaderCompatibility.PublishSonsSdkShimState(hostResult, null);
            if (hostResult != null && hostResult.InitializeSonsSdk)
            {
                var ownsInitialization = !string.Equals(hostResult.Mode, "upstream-redloader", StringComparison.Ordinal);
                void OnInitializationCompleted(Exception exception)
                {
                    if (LoaderCapabilities.StartupFailed)
                    {
                        return;
                    }

                    var failure = exception == null ? null : "Shared SonsSdk initialization failed: " + DescribeFailure(exception);
                    LegacyRedLoaderCompatibility.PublishSonsSdkShimState(hostResult, failure, initializationCompleted: true);
                    try
                    {
                        if (failure != null)
                        {
                            addStartupWarning?.Invoke(failure);
                        }
                    }
                    finally
                    {
                        SaveLifecycle.RefreshLegacySonsSdkRelay();
                        RuntimeEntryPoint.RequestLoaderStateRefresh("sonssdk-initialization-completed");
                    }
                }

                if (ownsInitialization)
                    SdkInitialization.Instance.Completed += OnInitializationCompleted;
                else
                    SdkEvents.OnSdkInitializationCompleted.Subscribe(OnInitializationCompleted, int.MinValue);

                var createModManagerUi = LegacyRedLoaderCompatibilityPatches.IsRedLoaderModsButtonEnabled();
                RuntimeEntryPoint.TraceCompat("processor.InitAfterUnity legacy-manager=" + createModManagerUi);
                using (Timings.Measure("loader.sonssdk", "post-unity-init"))
                {
                    TryRun("SonsSdk post-Unity initialization", () =>
                    {
                        if (ownsInitialization)
                            MainInitializer.SetInitializationDriver(SdkInitialization.Instance);
                        processor.InitAfterUnity(createModManagerUi);
                    }, warning =>
                    {
                        initializationError = warning;
                        var completed = ownsInitialization
                            ? SdkInitialization.Instance.IsCompleted
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
                RuntimeEntryPoint.TraceCompat("processor.InitAfterUnity skipped by Modstoke host mode");
            }

            if (initializationError != null)
            {
                LegacyRedLoaderCompatibility.PublishSonsSdkShimState(hostResult, initializationError);
            }
        }

        internal static void PublishApplicationLateStart()
        {
            GlobalEvents.OnApplicationLateStart.Invoke();
        }

        internal static void PublishUpdate()
        {
            UnityLifecycle.InvokeLifecycle("RedLoader.Coroutines", () => LegacyRedLoaderCompatibility.DrainQueuedCoroutines());
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
                RuntimeEntryPoint.LogSource.LogWarning("RedLoader preferences save failed: " + ex);
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

        private static string DescribeFailure(Exception error)
        {
            var cause = error.GetBaseException();
            return error.Message + (ReferenceEquals(error, cause) ? string.Empty
                : " Cause: " + cause.GetType().Name + ": " + cause.Message);
        }

        private static void OnObservedSonsSdkGameStart()
        {
            RuntimeEntryPoint.TraceCompat("Observed SonsSdk OnGameStart from compatibility event source.");
        }

        private static bool _sonsSdkGameStartObserverInstalled;
    }
}
