// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;
using TheForest.Player.Actions;
using TheForest.Utils;
using NativeGameState = global::Sons.Save.GameState;

namespace Modstoke.Loader
{
    internal static class GatheringLifecycleFix
    {
        private static int _installationAttempted;
        private static int _reportedFailure;
        private static bool _enabled;

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0) return;
            if (support == null || !support.SafeForHarmonyPatches) return;
            var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-gathering-lifecycle");
            try
            {
                if (!GameBuild.IsRecognized) return;
                var target = AccessTools.DeclaredMethod(typeof(PlayerGatheringAction), nameof(PlayerGatheringAction.OnDestroy),
                    Type.EmptyTypes) ?? throw new MissingMethodException("PlayerGatheringAction.OnDestroy");
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(GatheringLifecycleFix), nameof(BeforeDestroy)));
                _enabled = true;
            }
            catch (Exception error)
            {
                _enabled = false;
                try { harmony.UnpatchSelf(); } catch (Exception cleanup) { warning?.Invoke("Gathering lifecycle unpatch failed: " + cleanup); }
                warning?.Invoke("Gathering lifecycle fix was not installed: " + error);
            }
        }

        private static bool BeforeDestroy(PlayerGatheringAction __instance, bool __runOriginal)
        {
            if (!_enabled || !__runOriginal || __instance is null) return true;
            try { if (!NativeGameState.IsQuitting) return true; }
            catch (Exception error) { ReportFailure(error); return true; }

            // Native OnDestroy performs these two operations in this order. SetHeldItem inlines
            // replication, so call its existing local cleanup and replication parts separately.
            ClearHand(__instance, PlayerGatheringAction.LocatorSide.Left);
            ClearHand(__instance, PlayerGatheringAction.LocatorSide.Right);
            return false;
        }

        private static void ClearHand(PlayerGatheringAction instance, PlayerGatheringAction.LocatorSide side)
        {
            // Preserve native early return, deactivation, deferred destruction and reference clearing.
            // If cleanup throws, do not rerun it by falling back to the original OnDestroy.
            instance.OnHeldItemChanged(side, GatheringPrefabsDefinition.Prefab.None);
            bool unavailable = false;
            try
            {
                if (!BoltNetwork.isRunning || instance._isRemote) return;
                if (NativeGameState.IsQuitting)
                {
                    var entity = LocalPlayer.Entity;
                    unavailable = entity is null || entity.WasCollected || !entity || !entity.isAttached;
                    if (!unavailable)
                    {
                        var attached = entity._entity;
                        unavailable = attached is null || attached.Serializer is null;
                    }
                }
            }
            catch (Exception error) { ReportFailure(error); }
            if (unavailable) return;

            // A live but incompatible serializer keeps its native diagnostic. Never call GetState
            // as an availability probe: it can log or throw after the attachment has gone away.
            instance.ReplicateHeldItem(side, GatheringPrefabsDefinition.Prefab.None);
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0) return;
            try { RuntimeEntryPoint.LogSource?.LogWarning("Gathering shutdown readiness could not be read; native behavior retained: " + error); }
            catch (Exception) { }
        }
    }
}
