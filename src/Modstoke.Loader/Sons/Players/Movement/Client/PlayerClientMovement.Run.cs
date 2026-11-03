// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using HarmonyLib;
using UnityEngine;
using GameInput = global::Sons.Input.InputSystem;

namespace Modstoke.Sons
{
    public sealed partial class PlayerClientMovement
    {
        [ThreadStatic] private static PlayerClientMovement _runObservation;
        private uint _runPressSequence;
        private int _runPressFrame = -1;
        private bool _runButtonObserved;

        private static void InstallRunInput(HarmonyLib.Harmony harmony)
        {
            var update = AccessTools.DeclaredMethod(typeof(FirstPersonCharacter),
                nameof(FirstPersonCharacter.UpdateRun), Type.EmptyTypes);
            var down = AccessTools.DeclaredMethod(typeof(GameInput), nameof(GameInput.GetButtonDown),
                new[] { typeof(GameInput.Action) });
            if (update == null || update.IsStatic || update.ReturnType != typeof(void)
                || down == null || !down.IsStatic || down.ReturnType != typeof(bool))
                throw new MissingMethodException("Recognized native Run input methods are unavailable.");
            harmony.Patch(update,
                prefix: new HarmonyMethod(typeof(PlayerClientMovement), nameof(BeforeRunInput)) { priority = Priority.Last },
                finalizer: new HarmonyMethod(typeof(PlayerClientMovement), nameof(FinishRunInput)));
            harmony.Patch(down,
                postfix: new HarmonyMethod(typeof(PlayerClientMovement), nameof(ObserveRunButton)) { priority = Priority.Last });
        }

        private static void BeforeRunInput(FirstPersonCharacter __instance, bool __runOriginal,
            out PlayerClientMovement __state)
        {
            __state = null;
            var active = For(__instance);
            if (!__runOriginal || active == null || active._transition != null || active._nativeMode != null) return;
            try
            {
                if (_runObservation != null || !active.Current())
                { active.Stop("Native Run input lost its exclusive retained actor boundary."); return; }
                active._runButtonObserved = false;
                _runObservation = __state = active;
            }
            catch (Exception error) { active.Stop("Run input observation failed (" + error.GetType().Name + ")."); }
        }

        private static void ObserveRunButton(GameInput.Action __0, bool __result, bool __runOriginal)
        {
            var active = _runObservation;
            if (active == null || !__runOriginal || !__result) return;
            try
            {
                // Observe the native read; never query input again or infer a press from
                // effective sprint, which also includes auto-run and toggle state.
                if (__0 == GameInput.Actions.Run) active._runButtonObserved = true;
            }
            catch (Exception error) { active.Stop("Run button observation failed (" + error.GetType().Name + ")."); }
        }

        private static void FinishRunInput(FirstPersonCharacter __instance,
            PlayerClientMovement __state, bool __runOriginal, Exception __exception)
        {
            if (__state == null) return;
            var active = __state;
            try
            {
                if (!ReferenceEquals(_runObservation, active) || !__runOriginal || __exception != null
                    || !active.Current())
                { active.Stop("The retained native Run update did not finish successfully."); return; }
                // Toggle-off does not rearm CanRun. The matched UpdateRun writes both
                // fields on the observed button-down path that activates running.
                if (!active._runButtonObserved || !__instance._runInputActive || !__instance._canRun
                    || active._runPressFrame == Time.frameCount) return;
                bool currentSample = active._insideFixed && active._sampled;
                if (!currentSample && active._sequence == uint.MaxValue)
                { active.Stop("Run input sequence exhausted."); return; }
                active._runPressSequence = currentSample ? active._sample.Sequence : active._sequence + 1;
                active._runPressFrame = Time.frameCount;
            }
            catch (Exception error) { active.Stop("Run input completion failed (" + error.GetType().Name + ")."); }
            finally { if (ReferenceEquals(_runObservation, active)) _runObservation = null; }
        }
    }
}
