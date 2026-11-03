// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using HarmonyLib;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerLifecycle
    {
        private static bool _sourceHoldHooksAttempted, _sourceHoldHooksReady;

        private static bool TryInstallSourceHoldHooks(out string detail)
        {
            detail = "Exact source motor/collision hold hooks are unavailable.";
            if (_sourceHoldHooksAttempted) return _sourceHoldHooksReady;
            _sourceHoldHooksAttempted = true;
            HarmonyLib.Harmony harmony = null;
            try
            {
                var late = Qualify(typeof(FirstPersonCharacter), "LateUpdate", 0x2F7E530);
                var water = Qualify(typeof(PlayerAnimatorControl), "UpdateInWaterControl", 0x35B9DB0);
                var invoke = QualifyNative(typeof(MonoBehaviour), "InvokeDelayed", 0x44F1700, true,
                    typeof(void), typeof(MonoBehaviour), typeof(string), typeof(float), typeof(float));
                var accepted = new[]
                {
                    Qualify(typeof(FirstPersonCharacter), "OnCollisionEnterProxied", 0x2F819B0, typeof(Collision)),
                    Qualify(typeof(FirstPersonCharacter), "OnCollisionStayProxied", 0x2F81E20, typeof(Collision)),
                    Qualify(typeof(FirstPersonCharacter), "RbAddForce", 0x2F86F90, typeof(Vector3), typeof(ForceMode)),
                    Qualify(typeof(FirstPersonCharacter), "OnJumpInput", 0x2F7F450, typeof(bool))
                };
                harmony = new HarmonyLib.Harmony(global::Modstoke.Loader.RuntimeEntryPoint.PluginGuid + ".sons-live-source-hold");
                // LateUpdate clears collision history and subframe counters. Only the
                // live settled hold gates it; initial migration behavior is unchanged.
                harmony.Patch(late, prefix: Hook(nameof(BeforeSourceMotorWriter)), finalizer: Hook(nameof(FinishSourceWriter)));
                // This state-derived water update can also run outside PAC.Update. It
                // must not mutate swimming flags while the settled live motor is held.
                harmony.Patch(water, prefix: Hook(nameof(BeforeSourceMotorWriter)), finalizer: Hook(nameof(FinishSourceWriter)));
                foreach (var method in accepted)
                    harmony.Patch(method, prefix: Hook(nameof(BeforeSourceAcceptedWriter)), finalizer: Hook(nameof(FinishSourceWriter)));
                harmony.Patch(invoke, prefix: Hook(nameof(BeforeSourceInvoke)), finalizer: Hook(nameof(FinishSourceWriter)));
                _sourceHoldHooksReady = true;
                return true;
            }
            catch (Exception error)
            {
                try { harmony?.UnpatchSelf(); } catch (Exception) { }
                detail = "Source motor hold hooks refused: " + error.Message;
                return false;
            }
        }

        private static bool BeforeSourceMotorWriter(Component __instance, bool __runOriginal, out NativeActionCall __state)
        {
            __state = default;
            if (!__runOriginal) return false;
            if (_sourceInputHold?.HoldsWriter(__instance) == true) return false;
            if (ActionHooksAvailable && _sourceInputHold?.Owns(__instance) == true)
            { __state = new NativeActionCall(false); _nativeActionDepth++; }
            return true;
        }

        private static void BeforeSourceInvoke(MonoBehaviour __0, bool __runOriginal, out NativeActionCall __state)
            => BeforeSourceAcceptedWriter(__0, __runOriginal, out __state);

        private static void BeforeSourceAcceptedWriter(Component __instance, bool __runOriginal, out NativeActionCall __state)
        {
            __state = default;
            if (!__runOriginal || !ActionHooksAvailable || _sourceInputHold?.Owns(__instance) != true) return;
            YieldSourceHoldForActor(__instance);
            __state = new NativeActionCall(false);
            _nativeActionDepth++;
        }

        private static void FinishSourceWriter(NativeActionCall __state, Exception __exception)
        {
            if (__state.Entered && __exception != null)
                _sourceInputHold?.Fail("An accepted source motor callback failed: " + __exception.Message);
            FinishCoreWriter(__state);
        }

        private static void YieldSourceHoldForActor(Component actor)
        {
            var hold = _sourceInputHold;
            if (hold == null || !hold.Owns(actor)) return;
            // Never suppress or replay incoming accepted work. Failed restoration
            // remains explicit and never turns into a fresh-water thaw later.
            try { hold.TryYield(out _); }
            catch (Exception error) { hold.Fail("Accepted source writer yield failed: " + error.Message); }
        }
    }
}
