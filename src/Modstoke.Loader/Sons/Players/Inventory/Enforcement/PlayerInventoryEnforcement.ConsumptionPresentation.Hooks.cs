// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using HarmonyLib;
using global::Sons.Inventory;
using TheForest.Player.Actions;
using TheForest.Utils;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        private static bool _consumptionPresentationHooksInstalled;
        private bool _presentationInterruptActive;
        private enum ConsumptionHook { Entry, Stats, Complete, Edible, Clear, Routine, Interrupt, FadeIn, FadeOut }
        private readonly record struct ConsumptionHookState(bool Entered, PlayerInventoryEnforcement Owner,
            ConsumptionHook Kind, bool Admitted, bool Busy);

        // Installed within the inventory hook owner's rollback scope. The four main
        // consumption boundaries retain their NativeBoundaries pins and use these
        // typed prefixes and finalizer without competing patches.
        private static void InstallConsumptionPresentationHooks(HarmonyLib.Harmony harmony)
        {
            var routine = AccessTools.Method(typeof(PlayerConsumeItemAction._ConsumeItemRoutine_d__16), "MoveNext", Type.EmptyTypes);
            var clear = AccessTools.Method(typeof(PlayerConsumeItemAction), nameof(PlayerConsumeItemAction.ClearPlayerState), Type.EmptyTypes);
            var interrupted = AccessTools.Method(typeof(PlayerConsumeItemAction), nameof(PlayerConsumeItemAction.OnInterrupted), Type.EmptyTypes);
            var midAction = AccessTools.Method(typeof(FirstPersonCharacter), nameof(FirstPersonCharacter.SetIsInMidAction), new[] { typeof(bool) });
            QualifyBoundary(routine, (typeof(bool), 0x30AD6E0, 2432, "E6E1453A56375FA57F7AB069F06ECF2ACE480AAA5FB9139EDE6BFC73F806EACF"));
            QualifyBoundary(clear, (typeof(void), 0x30AB850, 2288, "9B78AF5BFCAFE6770E24A655D4CFAC1A08BF40F2876B8D2888B722C902E9D0C9"));
            QualifyBoundary(interrupted, (typeof(void), 0x30AB7D0, 112, "28128DB420218F513332A0C37EF290643DB9A535031613A0D20A58F499077D04"));
            QualifyBoundary(midAction, (typeof(void), 0x2F7CE70, 400, "53BEC25FB29B64EB90571424D7EA0CF817A63382961D5138BE3FD4001677BEFE"));
            harmony.Patch(routine,
                prefix: new HarmonyMethod(typeof(PlayerInventoryEnforcement), nameof(BeforeConsumptionRoutine)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(PlayerInventoryEnforcement), nameof(AfterConsumptionRoutine)) { priority = Priority.Last });
            harmony.Patch(clear,
                prefix: new HarmonyMethod(typeof(PlayerInventoryEnforcement), nameof(BeforeConsumptionClear)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(PlayerInventoryEnforcement), nameof(AfterConsumptionPresentation)) { priority = Priority.Last });
            harmony.Patch(interrupted,
                prefix: new HarmonyMethod(typeof(PlayerInventoryEnforcement), nameof(BeforeConsumptionInterrupted)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(PlayerInventoryEnforcement), nameof(AfterConsumptionPresentation)) { priority = Priority.Last });
            harmony.Patch(midAction,
                prefix: new HarmonyMethod(typeof(PlayerInventoryEnforcement), nameof(BeforeConsumptionMidAction)) { priority = Priority.First });
            InstallConsumptionFadeHooks(harmony);
            _consumptionPresentationHooksInstalled = true;
        }

        private static PlayerInventoryEnforcement EnterConsumption(ConsumptionHook kind, out ConsumptionHookState state)
        {
            EnterWriter(out bool entered);
            var owner = _guard;
            state = new(entered, owner, kind, false, owner?._busy ?? false);
            return owner;
        }

        private static bool MatchesConsumptionAction(PlayerInventoryEnforcement owner, PlayerConsumeItemAction action)
            => owner != null && owner._consumptionPresentation != null && action != null && !action.WasCollected
                && action.Pointer == owner._consumptionPresentation.Action.Pointer;

        private static bool ProtectsConsumptionPresentation(PlayerInventoryEnforcement owner)
            => ProtectsInventory(LocalPlayer.Inventory) || owner?._presentationStarted == true;

        private static void AdmitConsumption(PlayerInventoryEnforcement owner, ref ConsumptionHookState state)
        {
            state = state with { Admitted = true };
            owner._busy = true;
        }

        private static bool BeforeConsumptionEntry(PlayerConsumeItemAction __instance, ItemInstance __0, bool __1,
            PlayerConsumeItemAction.OnConsumeComplete __2, bool __runOriginal, out ConsumptionHookState __state)
        {
            var owner = EnterConsumption(ConsumptionHook.Entry, out __state);
            if (!ProtectsConsumptionPresentation(owner)) return true;
            try
            {
                if (!__runOriginal || !__state.Entered || !MatchesConsumptionAction(owner, __instance)
                    || !owner.PresentationLifetime() || !owner._presentationEntryPermit || owner._presentationEntryActive
                    || _writerDepth != 1 || __0?.Pointer != owner._intakeItem?.Pointer
                    || __1 != owner._consumptionPresentation.Animate || __2?.Pointer != owner._consumptionPresentation.Callback.Pointer)
                    return false;
                owner._presentationEntryPermit = false;
                owner._presentationEntryActive = true;
                AdmitConsumption(owner, ref __state);
                return true;
            }
            catch (Exception error) { owner?.FailConsumptionPresentation(error.Message); return false; }
        }

        private static bool BeforeConsumptionStats(PlayerConsumeItemAction __instance, ItemInstance __0,
            bool __runOriginal, out ConsumptionHookState __state)
        {
            var owner = EnterConsumption(ConsumptionHook.Stats, out __state);
            if (!ProtectsConsumptionPresentation(owner)) return true;
            try
            {
                if (!__runOriginal || !__state.Entered || !MatchesConsumptionAction(owner, __instance)
                    || !owner.PresentationEffectsCurrent() || !owner.PresentationEffectPhase()
                    || owner._presentationStatsSeen || __0?.Pointer != owner._intakeItem?.Pointer || _writerDepth != 2)
                    return false;
                owner._presentationStatsSeen = true;
                AdmitConsumption(owner, ref __state);
            }
            catch (Exception error) { owner?.FailConsumptionPresentation(error.Message); }
            // Only the authoritative projection owns numbers. Never run the LocalPlayer
            // ItemUtils/Vitals dispatcher, even for an otherwise admitted native action.
            return false;
        }

        private static bool BeforeConsumptionComplete(PlayerConsumeItemAction __instance,
            bool __runOriginal, out ConsumptionHookState __state)
        {
            var owner = EnterConsumption(ConsumptionHook.Complete, out __state);
            if (!ProtectsConsumptionPresentation(owner)) return true;
            try
            {
                if (!__runOriginal || !__state.Entered || !MatchesConsumptionAction(owner, __instance)
                    || !owner.PresentationEffectsCurrent() || !owner.PresentationEffectPhase()
                    || !owner._presentationStatsSeen || owner._presentationCompleteActive || owner._presentationCompleteDone
                    || _writerDepth != 2 || owner._intakeCounters.GetConsumed(owner._presentationFrame.Action.ItemId) != owner._intakeCount)
                    return false;
                owner._presentationCompleteActive = true;
                owner._presentationCompleteDepth = _writerDepth;
                AdmitConsumption(owner, ref __state);
                return true;
            }
            catch (Exception error) { owner?.FailConsumptionPresentation(error.Message); return false; }
        }

        private static bool BeforeConsumptionEdibleComplete(InventoryEdibleItem __instance,
            bool __runOriginal, out ConsumptionHookState __state)
        {
            var owner = EnterConsumption(ConsumptionHook.Edible, out __state);
            if (!ProtectsConsumptionPresentation(owner)) return true;
            try
            {
                if (!__runOriginal || !__state.Entered || owner == null || !owner.PresentationEffectsCurrent()
                    || __instance?.Pointer != owner._intakeEdible?.Pointer || !owner._presentationCompleteActive
                    || owner._presentationEdibleActive || owner._presentationEdibleDone
                    || _writerDepth != owner._presentationCompleteDepth + 1) return false;
                owner._presentationExpectsEmpty = owner._consumptionPresentation.Volume != null
                    && !owner._consumptionPresentation.Volume.IsEmpty;
                owner._presentationEdibleActive = true;
                owner._presentationEdibleDepth = _writerDepth;
                AdmitConsumption(owner, ref __state);
                return true;
            }
            catch (Exception error) { owner?.FailConsumptionPresentation(error.Message); return false; }
        }

        private static bool BeforeConsumptionRoutine(PlayerConsumeItemAction._ConsumeItemRoutine_d__16 __instance,
            bool __runOriginal, ref bool __result, out ConsumptionHookState __state)
        {
            var owner = EnterConsumption(ConsumptionHook.Routine, out __state);
            if (!ProtectsConsumptionPresentation(owner)) return true;
            __result = false;
            try
            {
                if (!__runOriginal || !__state.Entered || __instance == null || __instance.WasCollected
                    || !MatchesConsumptionAction(owner, __instance.__4__this) || !owner.PresentationEffectsCurrent()
                    || !owner._consumptionPresentation.Animate || owner._presentationRoutineActive || owner._presentationRoutineDone)
                    return false;
                if (owner._presentationRoutine == null)
                {
                    if (!owner._presentationEntryActive || __instance.__1__state != 0 || _writerDepth != 2) return false;
                    owner._presentationRoutine = __instance;
                }
                else if (owner._presentationRoutine.Pointer != __instance.Pointer || _writerDepth != 1) return false;
                // The prop loader may finish after the animation. Keep this same native
                // state-machine phase pending rather than acknowledge an unresolved callback.
                if (__instance.__1__state == 3 && (!owner._consumptionPresentation.PropInitialized || !owner._presentationFadeInDone))
                { __result = true; return false; }
                owner._presentationRoutinePhase = __instance.__1__state;
                owner._presentationRoutineActive = true;
                AdmitConsumption(owner, ref __state);
                return true;
            }
            catch (Exception error) { owner?.FailConsumptionPresentation(error.Message); return false; }
        }

        private static bool BeforeConsumptionClear(PlayerConsumeItemAction __instance,
            bool __runOriginal, out ConsumptionHookState __state)
        {
            var owner = EnterConsumption(ConsumptionHook.Clear, out __state);
            if (!ProtectsConsumptionPresentation(owner)) return true;
            try
            {
                if (!__runOriginal || !__state.Entered || !MatchesConsumptionAction(owner, __instance)
                    || !owner.PresentationEffectsCurrent() || owner._presentationClearActive
                    || !(owner._presentationInterruptActive || (owner._presentationRoutineActive
                        && owner._presentationRoutinePhase == 3 && !owner._presentationCleared))) return false;
                owner._presentationClearActive = true;
                AdmitConsumption(owner, ref __state);
                return true;
            }
            catch (Exception error) { owner?.FailConsumptionPresentation(error.Message); return false; }
        }

        private static bool BeforeConsumptionInterrupted(PlayerConsumeItemAction __instance,
            bool __runOriginal, out ConsumptionHookState __state)
        {
            var owner = EnterConsumption(ConsumptionHook.Interrupt, out __state);
            if (!ProtectsConsumptionPresentation(owner)) return true;
            try
            {
                if (!__runOriginal || !__state.Entered || !MatchesConsumptionAction(owner, __instance)
                    || !owner.PresentationEffectsCurrent() || owner._presentationInterruptActive
                    || owner._presentationCompleteDone) return false;
                owner._presentationInterruptActive = true;
                AdmitConsumption(owner, ref __state);
                return true; // Native interruption cleans up, but does not call ConsumeComplete.
            }
            catch (Exception error) { owner?.FailConsumptionPresentation(error.Message); return false; }
        }

        private static bool BeforeConsumptionMidAction(FirstPersonCharacter __instance, bool __0, bool __runOriginal)
        {
            if (__runOriginal && PlayerLifePresentation.TryHandleNativeLifeMidAction(__instance, __0)) return false;
            if (__runOriginal && TryHandleHeldOnlyMidAction(__instance, __0)) return false;
            var owner = _guard;
            if (owner?._consumptionPresentation == null
                || !(owner._presentationEntryActive || owner._presentationRoutineActive || owner._presentationClearActive
                    || owner._presentationCompleteActive || owner._presentationEdibleActive || owner._presentationInterruptActive)) return true;
            try
            {
                if (!__runOriginal || !owner.PresentationLifetime()
                    || __instance?.Pointer != owner._consumptionPresentation.Character.Pointer)
                { owner.FailConsumptionPresentation("Native mid-action lost its exact source character."); return false; }
                int phase = __0
                    ? !owner._consumptionPresentation.Animate || _writerDepth != 1 ? 0
                        : owner._presentationRoutineActive && owner._presentationRoutinePhase == 1 ? 2
                        : owner._presentationEntryActive ? 1 : 0
                    : owner._presentationEdibleActive && _writerDepth == owner._presentationEdibleDepth ? 16
                        : owner._presentationClearActive && _writerDepth == 2 ? 4
                        : owner._presentationCompleteActive && _writerDepth == owner._presentationCompleteDepth ? 8 : 0;
                if (phase == 0 || (owner._presentationMidActionPhases & phase) != 0)
                    owner.FailConsumptionPresentation("Unexpected or repeated native mid-action phase.");
                else
                {
                    owner._presentationMidActionPhases |= phase;
                    __instance._isInMidAction = __0;
                }
            }
            catch (Exception error) { owner.FailConsumptionPresentation(error.Message); }
            // The native body additionally writes IPlayerState. Only the actor authority may
            // publish that state; retained source presentation changes this local field only.
            return false;
        }

        private static Exception AfterConsumptionRoutine(bool __runOriginal, bool __result,
            Exception __exception, ConsumptionHookState __state)
        {
            var owner = __state.Owner;
            try
            {
                if (__state.Admitted)
                {
                    if (!__runOriginal || __exception != null || !owner.PresentationEffectsCurrent())
                        throw new InvalidOperationException("Native consumption routine did not return normally.");
                    if (owner._presentationRoutinePhase == 1 && (owner._presentationMidActionPhases & 2) == 0)
                        throw new InvalidOperationException("Native consumption animation did not enter its mid-action phase.");
                    if (!__result)
                    {
                        if (owner._presentationRoutinePhase != 4 || !owner._presentationCompleteDone || !owner._presentationEdibleDone)
                            throw new InvalidOperationException("Consumption routine ended without its native completion callbacks.");
                        owner._presentationRoutineDone = true;
                    }
                }
            }
            catch (Exception error) { owner?.FailConsumptionPresentation(error.Message); }
            finally
            {
                if (__state.Admitted) { owner._presentationRoutineActive = false; owner._busy = __state.Busy; }
                if (__state.Entered) _writerDepth--;
            }
            return __exception;
        }

        // The four consumption boundaries and clear/interrupt hooks share this finalizer, not AfterWriter.
        private static Exception AfterConsumptionPresentation(bool __runOriginal, Exception __exception, ConsumptionHookState __state)
        {
            var owner = __state.Owner;
            try
            {
                if (__state.Admitted)
                {
                    if (__exception != null || (__state.Kind != ConsumptionHook.Stats && !__runOriginal)
                        || !owner.PresentationEffectsCurrent())
                        throw new InvalidOperationException("Native consumption phase did not return normally.");
                    switch (__state.Kind)
                    {
                        case ConsumptionHook.Entry:
                            if (owner._consumptionPresentation.Animate ? owner._presentationRoutine == null : !owner._presentationCompleteDone)
                                throw new InvalidOperationException("ConsumeItem did not start its native routine or immediate completion.");
                            if (owner._consumptionPresentation.Animate && (owner._presentationMidActionPhases & 1) == 0)
                                throw new InvalidOperationException("Native consumption did not enter its mid-action phase.");
                            break;
                        case ConsumptionHook.Clear:
                            if ((owner._presentationMidActionPhases & 4) == 0)
                                throw new InvalidOperationException("Native consumption cleanup did not clear mid-action.");
                            if (!owner._presentationInterruptActive) owner._presentationCleared = true;
                            break;
                        case ConsumptionHook.Edible:
                            if (owner._presentationEmptySeen != owner._presentationExpectsEmpty
                                || (owner._presentationMidActionPhases & 16) == 0)
                                throw new InvalidOperationException("Native edible callback did not confirm its exact already-debited portion.");
                            owner._presentationEdibleDone = true;
                            break;
                        case ConsumptionHook.Complete:
                            if (!owner._presentationEdibleDone
                                || (owner._presentationMidActionPhases & 8) == 0
                                || owner._intakeCounters.GetConsumed(owner._presentationFrame.Action.ItemId) != owner._intakeCount + 1)
                                throw new InvalidOperationException("Native consumed callback/counter did not complete exactly once.");
                            owner._presentationCompleteDone = true;
                            break;
                        case ConsumptionHook.Interrupt:
                            owner._presentationInterrupted = true;
                            owner.FailConsumptionPresentation("Native action was interrupted; no completion ACK or replay is authorized.");
                            break;
                    }
                }
            }
            catch (Exception error) { owner?.FailConsumptionPresentation(error.Message); }
            finally
            {
                if (__state.Admitted)
                {
                    switch (__state.Kind)
                    {
                        case ConsumptionHook.Entry: owner._presentationEntryActive = false; break;
                        case ConsumptionHook.Complete: owner._presentationCompleteActive = false; break;
                        case ConsumptionHook.Edible: owner._presentationEdibleActive = false; break;
                        case ConsumptionHook.Clear: owner._presentationClearActive = false; break;
                        case ConsumptionHook.Interrupt: owner._presentationInterruptActive = false; break;
                    }
                    owner._busy = __state.Busy;
                }
                if (__state.Entered) _writerDepth--;
            }
            return __exception;
        }
    }
}
