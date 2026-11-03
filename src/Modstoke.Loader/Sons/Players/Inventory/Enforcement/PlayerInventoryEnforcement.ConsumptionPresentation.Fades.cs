// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using HarmonyLib;
using TheForest.Player.Actions;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        private Il2CppSystem.Object _presentationFadeIn, _presentationFadeOut;
        private bool _presentationFadeInDone, _presentationFadeOutDone;
        private bool _presentationFadeInStarted, _presentationFadeOutStarted;
        private readonly record struct ConsumptionFadeFactoryState(
            PlayerInventoryEnforcement Owner, bool FadeIn, float BlendTime, int Layer);

        private static void InstallConsumptionFadeHooks(HarmonyLib.Harmony harmony)
        {
            var fadeIn = AccessTools.Method(typeof(PlayerConsumeItemAction._FadeInLayer_d__22), "MoveNext", Type.EmptyTypes);
            var fadeOut = AccessTools.Method(typeof(PlayerConsumeItemAction._FadeOutLayer_d__21), "MoveNext", Type.EmptyTypes);
            var factoryIn = AccessTools.Method(typeof(PlayerConsumeItemAction), nameof(PlayerConsumeItemAction.FadeInLayer), new[] { typeof(float), typeof(int) });
            var factoryOut = AccessTools.Method(typeof(PlayerConsumeItemAction), nameof(PlayerConsumeItemAction.FadeOutLayer), new[] { typeof(float), typeof(int) });
            QualifyBoundary(factoryIn, (typeof(Il2CppSystem.Collections.IEnumerator), 0x30AC3A0, 96, "4C6A72464FD7C1913EEE2A0DE12FC0DFE940C3BD7CB7B464DAA9D5900E1FF396"), isStatic: true);
            QualifyBoundary(factoryOut, (typeof(Il2CppSystem.Collections.IEnumerator), 0x30AC340, 96, "AE1AF4AF8EC8D85656A4100FAEC2D09F37392C238B9D6A04AEB215286E630B6A"), isStatic: true);
            QualifyBoundary(fadeIn, (typeof(bool), 0x30AE0A0, 800, "2215B741F90B0069CB8A652E70FC471E9920ECF50D20533A68F0DB1FAC572159"));
            QualifyBoundary(fadeOut, (typeof(bool), 0x30AE400, 800, "546A2216CB406D927FC3FE24F47A1766D58DD1B39237A01C81C8E9F3B8711FB5"));
            harmony.Patch(fadeIn,
                prefix: new HarmonyMethod(typeof(PlayerInventoryEnforcement), nameof(BeforeConsumptionFadeIn)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(PlayerInventoryEnforcement), nameof(AfterConsumptionFade)) { priority = Priority.Last });
            harmony.Patch(fadeOut,
                prefix: new HarmonyMethod(typeof(PlayerInventoryEnforcement), nameof(BeforeConsumptionFadeOut)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(PlayerInventoryEnforcement), nameof(AfterConsumptionFade)) { priority = Priority.Last });
            harmony.Patch(factoryIn,
                prefix: new HarmonyMethod(typeof(PlayerInventoryEnforcement), nameof(BeforeConsumptionFadeInFactory)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(PlayerInventoryEnforcement), nameof(AfterConsumptionFadeFactory)) { priority = Priority.Last });
            harmony.Patch(factoryOut,
                prefix: new HarmonyMethod(typeof(PlayerInventoryEnforcement), nameof(BeforeConsumptionFadeOutFactory)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(PlayerInventoryEnforcement), nameof(AfterConsumptionFadeFactory)) { priority = Priority.Last });
        }

        private static void BeforeConsumptionFadeInFactory(float __0, int __1, out ConsumptionFadeFactoryState __state)
            => CaptureConsumptionFadeFactory(true, __0, __1, out __state);

        private static void BeforeConsumptionFadeOutFactory(float __0, int __1, out ConsumptionFadeFactoryState __state)
            => CaptureConsumptionFadeFactory(false, __0, __1, out __state);

        private static void CaptureConsumptionFadeFactory(bool fadeIn, float blendTime, int layer,
            out ConsumptionFadeFactoryState state)
        {
            state = default;
            var owner = _guard;
            if (owner == null || !(fadeIn ? owner._presentationRoutineActive && owner._presentationRoutinePhase == 1
                : owner._presentationClearActive)) return;
            state = new(owner, fadeIn, blendTime, layer);
        }

        private static Exception AfterConsumptionFadeFactory(Il2CppSystem.Collections.IEnumerator __result,
            bool __runOriginal, Exception __exception, ConsumptionFadeFactoryState __state)
        {
            var owner = __state.Owner;
            if (owner == null) return __exception;
            try
            {
                // The native factories are static: capture their returned object in the
                // exact action scope, rather than guessing an owner at its first MoveNext.
                var fadeIn = __result?.TryCast<PlayerConsumeItemAction._FadeInLayer_d__22>();
                var fadeOut = __result?.TryCast<PlayerConsumeItemAction._FadeOutLayer_d__21>();
                bool exact = __state.FadeIn
                    ? fadeIn != null && fadeIn.__1__state == 0 && fadeIn.targetLayer == __state.Layer
                        && fadeIn.blendTime == __state.BlendTime && owner._presentationFadeIn == null
                    : fadeOut != null && fadeOut.__1__state == 0 && fadeOut.targetLayer == __state.Layer
                        && fadeOut.blendTime == __state.BlendTime && owner._presentationFadeOut == null;
                if (!__runOriginal || __exception != null || !exact || !owner.PresentationEffectsCurrent()
                    || __state.Layer != 9 || __state.BlendTime != owner._consumptionPresentation.Action._inventoryUpperBodyBlendTime
                    || _writerDepth != (__state.FadeIn ? 1 : 2))
                    throw new InvalidOperationException("Native fade factory did not return the exact receipt-owned iterator.");
                if (__state.FadeIn) owner._presentationFadeIn = fadeIn;
                else owner._presentationFadeOut = fadeOut;
            }
            catch (Exception error) { owner.FailConsumptionPresentation(error.Message); }
            return __exception;
        }

        private static bool BeforeConsumptionFadeIn(PlayerConsumeItemAction._FadeInLayer_d__22 __instance,
            bool __runOriginal, ref bool __result, out ConsumptionHookState __state)
            => BeforeConsumptionFade(__instance, __instance.__1__state, __instance.targetLayer, __instance.blendTime,
                true, __runOriginal, ref __result, out __state);

        private static bool BeforeConsumptionFadeOut(PlayerConsumeItemAction._FadeOutLayer_d__21 __instance,
            bool __runOriginal, ref bool __result, out ConsumptionHookState __state)
            => BeforeConsumptionFade(__instance, __instance.__1__state, __instance.targetLayer, __instance.blendTime,
                false, __runOriginal, ref __result, out __state);

        private static bool BeforeConsumptionFade(Il2CppSystem.Object routine, int phase, int layer, float blendTime,
            bool fadeIn, bool runOriginal, ref bool result, out ConsumptionHookState state)
        {
            var owner = EnterConsumption(fadeIn ? ConsumptionHook.FadeIn : ConsumptionHook.FadeOut, out state);
            if (!ProtectsConsumptionPresentation(owner)) return true;
            result = false;
            try
            {
                if (!runOriginal || !state.Entered || owner == null || !owner.PresentationEffectsCurrent()
                    || !owner._consumptionPresentation.Animate || routine == null || routine.WasCollected || layer != 9
                    || blendTime != owner._consumptionPresentation.Action._inventoryUpperBodyBlendTime) return false;
                var retained = fadeIn ? owner._presentationFadeIn : owner._presentationFadeOut;
                bool done = fadeIn ? owner._presentationFadeInDone : owner._presentationFadeOutDone;
                if (done) return false;
                if (retained == null || retained.Pointer != routine.Pointer) return false;
                bool started = fadeIn ? owner._presentationFadeInStarted : owner._presentationFadeOutStarted;
                if (!started)
                {
                    bool expected = fadeIn ? owner._presentationRoutineActive && owner._presentationRoutinePhase == 1
                        : owner._presentationClearActive;
                    if (!expected || phase != 0 || _writerDepth != (fadeIn ? 2 : 3)) return false;
                    if (fadeIn) owner._presentationFadeInStarted = true;
                    else owner._presentationFadeOutStarted = true;
                }
                else if (phase == 0 || _writerDepth != 1) return false;
                AdmitConsumption(owner, ref state);
                return true;
            }
            catch (Exception error) { owner?.FailConsumptionPresentation(error.Message); return false; }
        }

        private static Exception AfterConsumptionFade(bool __runOriginal, bool __result,
            Exception __exception, ConsumptionHookState __state)
        {
            var owner = __state.Owner;
            try
            {
                if (__state.Admitted)
                {
                    if (!__runOriginal || __exception != null || !owner.PresentationEffectsCurrent())
                        throw new InvalidOperationException("Native consumption layer fade did not return normally.");
                    if (!__result)
                    {
                        if (__state.Kind == ConsumptionHook.FadeIn) owner._presentationFadeInDone = true;
                        else owner._presentationFadeOutDone = true;
                    }
                }
            }
            catch (Exception error) { owner?.FailConsumptionPresentation(error.Message); }
            finally
            {
                if (__state.Admitted) owner._busy = __state.Busy;
                if (__state.Entered) _writerDepth--;
            }
            return __exception;
        }
    }
}
