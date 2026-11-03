// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using HarmonyLib;
using global::Sons.Gameplay.ZipLines;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.InputSystem;
using NativeZiplineHook = global::Sons.Gameplay.ZipLines.ZiplineHook;

namespace Modstoke.Sons
{
    public sealed partial class PlayerClientMovement
    {
        private static void InstallZiplineActions(HarmonyLib.Harmony harmony)
        {
            Patch(typeof(ZipLineController), nameof(ZipLineController.OnActivateZipLine),
                new[] { typeof(InputAction.CallbackContext) }, nameof(BeforeZiplineActivation));
            Patch(typeof(PlayerZipLineAction), nameof(PlayerZipLineAction.EnterZipLineMode),
                new[] { typeof(Transform), typeof(ZipLineController) }, nameof(BeforeZiplineEntry));
            Patch(typeof(PlayerZipLineAction), nameof(PlayerZipLineAction.DetachFromZipLine),
                new[] { typeof(InputAction.CallbackContext) }, nameof(BeforeZiplineDetachInput));
            Patch(typeof(ZipLineController), nameof(ZipLineController.DetachRiderFromHook),
                new[] { typeof(Transform) }, nameof(BeforeZiplineControllerDetach));
            Patch(typeof(PlayerZipLineAction), nameof(PlayerZipLineAction.ExitZipLineMode),
                new[] { typeof(NativeZiplineHook) }, nameof(BeforeZiplineExit), nameof(AfterZiplineExit));
            Patch(typeof(NativeZiplineHook), nameof(NativeZiplineHook.LateUpdate), Type.EmptyTypes, nameof(BeforeSourceZiplineLate));
            var move = AccessTools.DeclaredMethod(typeof(PlayerZipLineAction._PreserveExitVelocity_d__17),
                nameof(PlayerZipLineAction._PreserveExitVelocity_d__17.MoveNext), Type.EmptyTypes);
            if (move == null || move.ReturnType != typeof(bool) || move.IsStatic)
                throw new MissingMethodException("Native zipline exit-velocity completion is unavailable.");
            harmony.Patch(move,
                prefix: new HarmonyMethod(typeof(PlayerClientMovement), nameof(BeforeZiplineExitVelocity)) { priority = Priority.Last },
                postfix: new HarmonyMethod(typeof(PlayerClientMovement), nameof(AfterZiplineExitVelocity)) { priority = Priority.Last });

            void Patch(Type type, string name, Type[] parameters, string prefix, string postfix = null)
            {
                var method = AccessTools.DeclaredMethod(type, name, parameters);
                if (method == null || method.IsStatic || method.ReturnType != typeof(void))
                    throw new MissingMethodException("Native zipline boundary unavailable: " + name);
                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(PlayerClientMovement), prefix) { priority = Priority.Last },
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(PlayerClientMovement), postfix) { priority = Priority.Last });
            }
        }

        private static PlayerClientMovement ZiplineSource()
        {
            var active = _active;
            return active != null && !active._disposed && active._activated && active._ziplineEnabled
                && !active._bridge.IsServer && Environment.CurrentManagedThreadId == _hookThread
                && Same(LocalPlayer.Transform, active._rig.Root.transform) ? active : null;
        }

        private static bool BeforeZiplineActivation(ZipLineController __instance, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            var active = ZiplineSource();
            if (active == null) return true;
            if (active.CartMigrationHeld) return false;
            try
            {
                // The native held-only branch sends cargo, not the player. Its existing
                // custody owner remains responsible; never turn it into a player entry.
                if (LocalPlayer.Inventory.HeldOnlyItemController != null) return true;
                active.QueueZiplineEntry(__instance);
            }
            catch (Exception error) { active.Stop("Zipline input capture failed (" + error.GetType().Name + ")."); }
            return false;
        }

        private static bool BeforeZiplineEntry(PlayerZipLineAction __instance, Transform __0,
            ZipLineController __1, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            var active = ZiplineSource();
            if (active == null || !active.OwnsAction(__instance)) return true;
            return !active.CartMigrationHeld && active._applyingZipline && active._transition?.Entering == true
                && Same(active._zipline?.Action, __instance) && Same(active._zipline?.Controller, __1)
                && Same(__0, __1._connectionPoint);
        }

        private static bool BeforeZiplineDetachInput(PlayerZipLineAction __instance, InputAction.CallbackContext __0,
            bool __runOriginal)
        {
            if (!__runOriginal) return false;
            var active = ZiplineSource();
            if (active == null || !active.OwnsAction(__instance)) return true;
            if (active.CartMigrationHeld) return false;
            try
            {
                if (__0.performed && Same(active._zipline?.Action, __instance)) active.QueueZiplineExit();
            }
            catch (Exception error) { active.Stop("Zipline detach capture failed (" + error.GetType().Name + ")."); }
            return false;
        }

        private static bool BeforeZiplineControllerDetach(ZipLineController __instance, Transform __0, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            var active = ZiplineSource();
            if (active == null || !Same(__0, active._rig.Root.transform)) return true;
            if (active.CartMigrationHeld || !Same(__instance, active._zipline?.Controller)) return false;
            if (active._applyingZipline && active._transition?.Entering == false && active._ziplineExitReceipt != null) return true;
            active.QueueZiplineExit();
            return false;
        }

        private static bool BeforeZiplineExit(PlayerZipLineAction __instance, NativeZiplineHook __0, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            var active = ZiplineSource();
            if (active == null || !active.OwnsAction(__instance)) return true;
            if (!active.CartMigrationHeld && active._applyingZipline && active._transition?.Entering == false
                && Same(active._zipline?.Action, __instance) && Same(active._zipline?.Hook, __0)
                && active._ziplineExitReceipt?.Identity == active._zipline.Identity && !active._ziplineExitCallback) return true;
            active.Stop("Zipline exit callback arrived outside its exact approved source detach.");
            return false;
        }

        private static void AfterZiplineExit(PlayerZipLineAction __instance, NativeZiplineHook __0, bool __runOriginal)
        {
            var active = ZiplineSource();
            if (active == null || !active._applyingZipline || !Same(active._zipline?.Action, __instance)
                || !Same(active._zipline?.Hook, __0)) return;
            if (!__runOriginal || __instance.IsOnZipLine)
            { active.Stop("The approved native zipline callback did not finish."); return; }
            active._ziplineExitCallback = true;
        }

        private static bool BeforeSourceZiplineLate(NativeZiplineHook __instance, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            var active = ZiplineSource();
            return active == null || !Same(active._zipline?.Hook, __instance);
        }

        private static bool BeforeZiplineExitVelocity(PlayerZipLineAction._PreserveExitVelocity_d__17 __instance,
            bool __runOriginal)
        {
            if (!__runOriginal) return false;
            var active = ZiplineSource();
            if (active == null || !Same(__instance.__4__this, active._zipline?.Action)) return true;
            if (active._ziplineExitReceipt is not { } receipt
                || (active._transition?.Entering != false && !IsRetainedZiplineExit(active._rig.Character)))
            { active.Stop("Zipline exit velocity started outside its approved source transaction."); return false; }
            if (active._ziplineExitRoutine == null)
            {
                if (!active._applyingZipline || !Exact(__instance.exitVelocity, receipt.Velocity))
                { active.Stop("Zipline exit velocity does not match the authenticated server receipt."); return false; }
                active._ziplineExitRoutine = __instance;
            }
            if (active._ziplineExitRoutine.Pointer == __instance.Pointer)
            {
                // Align the retained native iterator at its real resume boundary. Never
                // restart it from the detach velocity when server motion has advanced.
                if (__instance.__1__state == 1 && active._ziplineLatestExitMotion is { } motion
                    && motion.MotionSequence > active._ziplineExitForceSequence)
                {
                    if (!motion.ExitForceActive || motion.ExitElapsed >= __instance._t_5__2)
                    {
                        __instance._t_5__2 = motion.ExitForceActive ? motion.ExitElapsed : 1f;
                        __instance._force_5__3 = motion.ExitMultiplier;
                        __instance.__4__this._exitForce = motion.ExitForce;
                    }
                    active._ziplineExitForceSequence = motion.MotionSequence;
                }
                return true;
            }
            active.Stop("A second zipline exit iterator attempted to replace the retained callback.");
            return false;
        }

        private static void AfterZiplineExitVelocity(PlayerZipLineAction._PreserveExitVelocity_d__17 __instance,
            bool __result, bool __runOriginal)
        {
            var active = ZiplineSource();
            if (active == null || active._ziplineExitRoutine?.Pointer != __instance.Pointer) return;
            if (!__runOriginal) { active.Stop("The native zipline exit iterator was suppressed."); return; }
            if (!__result) active._ziplineExitCompleted = true;
        }
    }
}
