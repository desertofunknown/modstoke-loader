// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;
using PathologicalGames;
using global::Sons;
using global::Sons.Animation.PlayerControl;
using global::Sons.Multiplayer;
using global::Sons.Save;
using UnityEngine;
using NativeGameState = global::Sons.Save.GameState;

namespace Modstoke.Loader
{
    internal static class GameLifecycleFixes
    {
        private static int _installationAttempted;
        private static int _reportedFailure;
        private static bool _enabled;
        [ThreadStatic] private static bool _bulkNamedStateSync;

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                warning?.Invoke("World lifecycle fixes are unavailable: Harmony support is not ready.");
                return;
            }

            var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-game-lifecycle");
            try
            {
                if (!GameBuild.IsRecognized)
                {
                    // The dedicated-server startup warning already covers client-pinned fixes.
                    if (!GameBuild.IsDedicatedServer)
                        warning?.Invoke("World lifecycle fixes skipped for an unrecognized game build.");
                    return;
                }

                var destroy = AccessTools.DeclaredMethod(typeof(GreeblePlugin), nameof(GreeblePlugin.Destroy),
                    new[] { typeof(GameObject) }) ?? throw new MissingMethodException(nameof(GreeblePlugin.Destroy));
                var enable = AccessTools.DeclaredMethod(typeof(DistanceActivatedGroup), nameof(DistanceActivatedGroup.OnEnable),
                    Type.EmptyTypes) ?? throw new MissingMethodException(nameof(DistanceActivatedGroup.OnEnable));
                var afterLoad = AccessTools.DeclaredMethod(typeof(NamedIntDataLoadBoltSync), nameof(NamedIntDataLoadBoltSync.AfterLoadCallback),
                    Type.EmptyTypes) ?? throw new MissingMethodException(nameof(NamedIntDataLoadBoltSync.AfterLoadCallback));
                var update = AccessTools.DeclaredMethod(typeof(NamedIntDataBoltExtensions), nameof(NamedIntDataBoltExtensions.UpdateInBoltNetwork),
                    new[] { typeof(NamedIntData), typeof(bool) }) ?? throw new MissingMethodException(nameof(NamedIntDataBoltExtensions.UpdateInBoltNetwork));
                harmony.Patch(destroy, prefix: new HarmonyMethod(typeof(GameLifecycleFixes), nameof(BeforeGreebleDestroy)));
                harmony.Patch(enable, prefix: new HarmonyMethod(typeof(GameLifecycleFixes), nameof(BeforeDistanceGroupEnable)));
                harmony.Patch(update, prefix: new HarmonyMethod(typeof(GameLifecycleFixes), nameof(BeforeNamedIntUpdate)));
                harmony.Patch(afterLoad,
                    prefix: new HarmonyMethod(typeof(GameLifecycleFixes), nameof(BeforeNamedIntLoad)),
                    finalizer: new HarmonyMethod(typeof(GameLifecycleFixes), nameof(AfterNamedIntLoad)));
                _enabled = true;
                RuntimeEntryPoint.LogSource?.LogInfo("World lifecycle fixes registered for the recognized game build.");
            }
            catch (Exception error)
            {
                _enabled = false;
                harmony.UnpatchSelf();
                warning?.Invoke("World lifecycle fixes were not installed: " + error);
            }
        }

        private static bool BeforeGreebleDestroy(GameObject __0, bool __runOriginal)
        {
            if (!_enabled || !__runOriginal)
                return true;
            try
            {
                if (!__0 || !NativeGameState.IsQuitting)
                    return true;
                if (PoolManager.Pools.TryGetValue("Greebles", out var pool) && pool)
                    return true;

                // Pool teardown unregisters before destroying its instances. Finish the same cleanup
                // if a layer is disabled afterward, so its own reference clearing can still run.
                UnityEngine.Object.Destroy(__0);
                return false;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return true;
            }
        }

        private static bool BeforeDistanceGroupEnable(bool __runOriginal)
        {
            if (!_enabled || !__runOriginal)
                return true;
            try
            {
                // Keep normal startup and all OnDisable cleanup; only refuse work after world exit.
                return !NativeGameState.IsQuitting || WorkScheduler._instance;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return true;
            }
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("World lifecycle correction failed; native behavior retained: " + error);
            }
            catch (Exception) { }
        }

        private static void BeforeNamedIntLoad(bool __runOriginal, out bool __state)
        {
            __state = _bulkNamedStateSync;
            if (_enabled && __runOriginal)
                _bulkNamedStateSync = true;
        }

        private static void AfterNamedIntLoad(bool __state)
        {
            _bulkNamedStateSync = __state;
        }

        private static bool BeforeNamedIntUpdate(NamedIntData __0, bool __1, bool __runOriginal)
        {
            if (!_enabled || !__runOriginal || !_bulkNamedStateSync || !__1 || __0 is null)
                return true;
            try
            {
                // The after-load callback walks every saved value, including local-only keys.
                // Keep its native enumeration/completion and all other callers' diagnostics.
                return NativeGameState.IsNetworked(__0);
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return true;
            }
        }
    }
}
