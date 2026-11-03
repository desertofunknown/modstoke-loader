// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Gameplay;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerLifecycle
    {
        private readonly List<GenericInteraction> _interactions = new();
        private PlayerStats _stats;
        private PlayerEventRouting.ProtectionLease _protectionEvents;

        /// <summary>Protection rejects new frozen ingress; no gameplay effect is deferred for replay.</summary>
        public int DeferredIngressCount => 0;

        private static bool CanFenceProtectedIngress(out string detail)
        {
            detail = "Player protection requires selected native dispatch-cohort observation before preparation.";
            return PlayerEventRouting.IsProtectionSelected;
        }

        private static void InstallIngressHooks(HarmonyLib.Harmony harmony)
        {
            // These are concrete native entries, not the shared empty hit-reaction stubs.
            var hooks = new[]
            {
                (Qualify(typeof(PlayerStats), nameof(PlayerStats.OnNetworkHit), 0x3112A60,
                    typeof(int), typeof(ImpactType), typeof(float), typeof(Vector3), typeof(float)), nameof(BeforeNetworkHit)),
                (Qualify(typeof(PlayerStats), nameof(PlayerStats.Hit), 0x3113CF0,
                    typeof(float), typeof(bool), typeof(ImpactType), typeof(float), typeof(bool), typeof(Vector3),
                    typeof(bool), typeof(bool)), nameof(BeforeHit)),
                (Qualify(typeof(PlayerAnimatorControl), nameof(PlayerAnimatorControl.AddMovement), 0x35B3C20,
                    typeof(Vector2)), nameof(BeforeAddMovement)),
                (Qualify(typeof(PlayerAnimatorControl), nameof(PlayerAnimatorControl.InterruptAction), 0x35B3E40), nameof(BeforeInterruptAction)),
                (Qualify(typeof(PlayerAnimatorControl), nameof(PlayerAnimatorControl.SetSpineEnabledSmoothCoroutines), 0x35AA200,
                    typeof(bool)), nameof(BeforeSpineAction)),
                (Qualify(typeof(PlayerAnimatorControl), nameof(PlayerAnimatorControl.cancelAnimatorActions), 0x35B0AC0), nameof(BeforeCancelAnimatorActions)),
                (Qualify(typeof(CoopMecanimReplicator), nameof(CoopMecanimReplicator.CrossFade), 0x30FE1D0,
                    typeof(string), typeof(float), typeof(int)), nameof(BeforeLocalCrossFade))
            };
            var input = QualifyNative(typeof(GenericInteraction), nameof(GenericInteraction.InputActionTriggered),
                0x332C880, false, typeof(bool));
            var checkInput = Qualify(typeof(GenericInteraction), nameof(GenericInteraction.CheckInputAction), 0x332C530);
            foreach (var hook in hooks)
                harmony.Patch(hook.Item1, prefix: Hook(hook.Item2), finalizer: Hook(nameof(FinishIngress)));
            harmony.Patch(input, prefix: Hook(nameof(BeforeInteractionInput)));
            harmony.Patch(checkInput, prefix: Hook(nameof(BeforeInteractionCheck)), finalizer: Hook(nameof(FinishCoreWriter)));
        }

        private void CaptureInteractionBoundaries()
        {
            _interactions.Clear();
            foreach (var interaction in Resources.FindObjectsOfTypeAll<GenericInteraction>())
                if (Alive(interaction)) _interactions.Add(interaction);
        }

        private BoltMigrationAdmission ReadInteractionAdmission(out string detail)
        {
            foreach (var interaction in _interactions)
                if (Alive(interaction) && (Alive(interaction._proxyTriggerSource) || interaction._playerNetworkInteractionWaiting))
                {
                    detail = "A committed local interaction proxy or network interaction is still pending.";
                    return BoltMigrationAdmission.Pending;
                }
            detail = "Captured local interaction proxy and network waits are drained.";
            return BoltMigrationAdmission.Ready;
        }

        private bool IsCapturedInputPlayer() => Same(LocalPlayer.GameObject, Root)
            && Same(LocalPlayer.Stats, _stats) && OwnsActionIdentity(_stats);

        private static bool BeforeInteractionCheck(GenericInteraction __instance, bool __runOriginal, out NativeActionCall __state)
        {
            __state = default;
            if (!__runOriginal) return false;
            if (!ActionHooksAvailable) return true;
            var fence = _actionFence;
            // Hold the native check BEFORE it clears a proxy. Existing proxy work runs while
            // draining and will be checked again normally after resume, not generically replayed.
            if (fence != null && fence._freezeStarted && fence.IsCapturedInputPlayer()) return false;
            var liveHold = _sourceInputHold;
            if (liveHold?.IsInputPlayer == true && Alive(__instance)
                && (Alive(__instance._proxyTriggerSource) || __instance._playerNetworkInteractionWaiting))
                liveHold.TryYield(out _);
            __state = new NativeActionCall(false);
            _nativeActionDepth++;
            return true;
        }

        private static bool BeforeInteractionInput(bool __runOriginal, ref bool __result)
        {
            if (!__runOriginal) return false;
            var fence = _actionFence;
            if ((fence == null || !fence.IsCapturedInputPlayer()) && _sourceInputHold?.IsInputPlayer != true) return true;
            // CheckInputAction's committed proxy branch bypasses this input-only method.
            // Never clear that proxy, disconnect an interaction, or replay its completion.
            __result = false;
            return false;
        }

        private static bool BeforeNetworkHit(PlayerStats __instance, int __0, ImpactType __1, float __2,
            Vector3 __3, float __4, bool __runOriginal, out NativeActionCall __state)
        {
            __state = default;
            if (!__runOriginal) return false;
            return AdmitProtectedIngress(__instance, out __state);
        }

        private static bool BeforeHit(PlayerStats __instance, float __0, bool __1, ImpactType __2, float __3,
            bool __4, Vector3 __5, bool __6, bool __7, bool __runOriginal, out NativeActionCall __state)
        {
            __state = default;
            if (!__runOriginal) return false;
            return AdmitProtectedIngress(__instance, out __state);
        }

        private static bool BeforeAddMovement(PlayerAnimatorControl __instance, Vector2 __0,
            bool __runOriginal, out NativeActionCall __state)
        {
            __state = default;
            if (!__runOriginal) return false;
            return AdmitProtectedIngress(__instance, out __state);
        }

        private static bool BeforeInterruptAction(PlayerAnimatorControl __instance,
            bool __runOriginal, out NativeActionCall __state)
        {
            __state = default;
            if (!__runOriginal) return false;
            return AdmitProtectedIngress(__instance, out __state);
        }

        private static bool BeforeSpineAction(PlayerAnimatorControl __instance, bool __0,
            bool __runOriginal, out NativeActionCall __state)
        {
            __state = default;
            if (!__runOriginal) return false;
            return AdmitProtectedIngress(__instance, out __state);
        }

        private static bool BeforeCancelAnimatorActions(PlayerAnimatorControl __instance,
            bool __runOriginal, out NativeActionCall __state)
        {
            __state = default;
            if (!__runOriginal) return false;
            return AdmitProtectedIngress(__instance, out __state);
        }

        private static bool BeforeLocalCrossFade(CoopMecanimReplicator __instance, string __0, float __1, int __2,
            bool __runOriginal, out NativeActionCall __state)
        {
            __state = default;
            if (!__runOriginal) return false;
            return AdmitProtectedIngress(__instance, out __state);
        }

        private static bool AdmitProtectedIngress(Component actor, out NativeActionCall state)
        {
            state = default;
            if (!ActionHooksAvailable) return true;
            var fence = _actionFence;
            // Drain accepted native calls before the snapshot. Once frozen, the explicit
            // migration protection rejects new effects instead of replaying them on a new owner.
            if (fence != null && fence._freezeStarted && fence.OwnsActionIdentity(actor)) return false;
            YieldSourceHoldForActor(actor);
            state = new NativeActionCall(false);
            _nativeActionDepth++;
            return true;
        }

        private static void FinishIngress(NativeActionCall __state, bool __runOriginal, Exception __exception)
        {
            if (!__state.Entered) return;
            FinishCoreWriter(__state);
        }
    }
}
