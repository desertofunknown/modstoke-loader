// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Inventory;
using TheForest.Player.Actions;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        private InventoryConsumption.Presentation _consumptionPresentation;
        private PlayerConsumptionFrame _presentationFrame;
        private PlayerConsumeItemAction._ConsumeItemRoutine_d__16 _presentationRoutine;
        private bool _presentationStarted, _presentationEntryPermit, _presentationEntryActive;
        private bool _presentationRoutineActive, _presentationRoutineDone, _presentationStatsSeen;
        private bool _presentationCompleteActive, _presentationCompleteDone, _presentationEdibleActive, _presentationEdibleDone;
        private bool _presentationClearActive, _presentationCleared, _presentationEmptySeen, _presentationExpectsEmpty;
        private bool _presentationInterrupted;
        private int _presentationRoutinePhase, _presentationCompleteDepth, _presentationEdibleDepth;
        private int _presentationMidActionPhases;

        internal bool HasPendingConsumptionPresentation => _presentationStarted
            && _intakePending && _intakeReply.Action.Kind != PlayerInventoryMessage.ConsumeApplied;

        // Call in RequestPlainIntake after selecting the exact source, before assigning a request.
        // Admission intentionally excludes held and world-view paths, whose stash/restore is a
        // separate equipment transaction. It does not pretend every edible is supported.
        private bool PrepareConsumptionPresentation(InventoryEdibleItem edible, ItemInstance item, SourceItem selected)
        {
            if (!_consumptionPresentationHooksInstalled || _presentationStarted || selected == null || !Stowed(selected.Entry)
                || _statProjection == null || _statProjection.IsDisposed || _statProjection.FailureReason != null) return false;
            return InventoryConsumption.Presentation.TryCapture(_inventory, edible, item, out _consumptionPresentation);
        }

        private bool ConsumptionPresentationReady(in PlayerConsumptionFrame frame)
            => !_presentationStarted && _consumptionPresentation != null && _consumptionPresentation.CanStart()
                && _consumptionPresentation.Item.Pointer == _intakeItem?.Pointer
                && _consumptionPresentation.Edible.Pointer == _intakeEdible?.Pointer
                && frame.Debit.IsValid && frame.Debit.HeldSlot < 0
                && frame.Debit.UsesVolume == (_consumptionPresentation.Volume != null);

        // ConsumeRemoved remains the retry response while animation is in progress.
        // Unconfirmed means failure, never "still animating". An immutable repeated
        // Project cannot restart presentation.
        private void BeginConsumptionPresentation(in PlayerConsumptionFrame frame)
        {
            if (_presentationStarted)
            {
                if (frame == _presentationFrame) SendIntake(_intakeReply);
                return;
            }
            if (frame.Action.Kind != PlayerInventoryMessage.ConsumeProject || _authority != null || !_intakePending
                || _intakeReply.Action.Kind != PlayerInventoryMessage.ConsumeRemoved
                || !_intakeCommand.SameReservation(frame)) return;
            _intakeCommand = frame;
            _presentationStarted = true;
            _presentationFrame = frame;
            try
            {
                if (!PresentationLifetime() || InventoryFrozen || _writerDepth != 0
                    || !_consumptionPresentation.CanStart() || !Unchanged() || !IntakeSourceCurrent(frame.Before)
                    || _intakeCounters.GetConsumed(frame.Action.ItemId) != _intakeCount)
                    throw new InvalidOperationException("Consumption projection or retained native presentation is unavailable.");
                if (!PlayerLifecycle.TryYieldSourceInputHoldForAcceptedWork(_bridge, out var holdDetail))
                    throw new InvalidOperationException("Accepted consumption could not yield the source body hold: " + holdDetail);
                if (!ProjectConsumedStats(frame))
                    throw new InvalidOperationException("Authoritative consumption stat projection failed.");
                _presentationEntryPermit = true;
                _consumptionPresentation.Invoke();
                if (_presentationEntryPermit || IsFaulted)
                    throw new InvalidOperationException("Native ConsumeItem did not enter its exact receipt permit.");
                Detail = "Authoritative intake projected; native animation and callbacks are pending.";
            }
            catch (Exception error) { FailConsumptionPresentation(error.Message); SendIntake(_intakeReply); }
            finally { _presentationEntryPermit = false; }
        }

        // Run from the regular inventory pump before PumpIntake, even while custody is pending.
        // Never acknowledge in a callback: the native caller still owns cleanup after it returns.
        private void PumpConsumptionPresentation()
        {
            if (!HasPendingConsumptionPresentation || IsFaulted || IsDisposed) return;
            try
            {
                if (!PresentationLifetime() || !_consumptionPresentation.NativeIdentityCurrent())
                    throw new InvalidOperationException("Consumption presentation lost its exact retained lifetime.");
                if (!_presentationCompleteDone || !_presentationEdibleDone || !_presentationStatsSeen
                    || (_consumptionPresentation.Animate && (!_presentationRoutineDone || !_presentationCleared
                        || !_presentationFadeInDone || !_presentationFadeOutDone))
                    || _presentationEntryActive || _presentationRoutineActive || _presentationCompleteActive
                    || _presentationEdibleActive || _presentationClearActive || _writerDepth != 0) return;
                if (!_consumptionPresentation.IsVisuallySettled()) return;
                if (!Unchanged() || !ConsumedStatsCurrent()
                    || _intakeCounters.GetConsumed(_presentationFrame.Action.ItemId) != _intakeCount + 1)
                    throw new InvalidOperationException("Native consumption completion changed authoritative custody or effects.");
                _consumptionPresentation.ReleaseObserver();
                _intakeReply = _presentationFrame.WithKind(PlayerInventoryMessage.ConsumeApplied);
                Detail = "Native intake callback, counter, prop cleanup and animation completed once.";
                SendIntake(_intakeReply);
            }
            catch (Exception error) { FailConsumptionPresentation(error.Message); SendIntake(_intakeReply); }
        }

        private bool PresentationLifetime()
            => OnThread && !IsDisposed && !IsFaulted && IsArmed && !InventoryFrozen && _authority == null && _intakePending
                && _presentationStarted && !_presentationInterrupted && Current()
                && _consumptionPresentation != null && _consumptionPresentation.ContextCurrent()
                && _presentationFrame == _intakeCommand.WithKind(PlayerInventoryMessage.ConsumeProject);

        private bool PresentationEffectsCurrent()
            => PresentationLifetime() && _consumptionPresentation.NativeIdentityCurrent()
                && ConsumedStatsCurrent();

        private bool PresentationEffectPhase()
            => _consumptionPresentation.Animate
                ? _presentationRoutineActive && _presentationRoutinePhase == 3 && _presentationCleared
                : _presentationEntryActive;

        private void FailConsumptionPresentation(string detail)
        {
            if (_presentationStarted && _intakePending)
                _intakeReply = _presentationFrame.WithKind(PlayerInventoryMessage.ConsumeUnconfirmed);
            Fail("Native consumption remains unconfirmed: " + detail);
        }

        // Parent calls after marking Fail, and before Dispose drops native/bridge receivers.
        // Keep uncertain custody and the attempt retained; retirement never permits a retry.
        private void RetireConsumptionPresentation()
        {
            _presentationEntryPermit = false;
            bool pending = _presentationStarted && _intakeReply.Action.Kind != PlayerInventoryMessage.ConsumeApplied;
            if (pending)
            {
                _presentationInterrupted = true;
                if (_intakePending) _intakeReply = _presentationFrame.WithKind(PlayerInventoryMessage.ConsumeUnconfirmed);
            }
            try { _consumptionPresentation?.Retire(pending); }
            catch (Exception error) { Detail += " Native consumption retirement is unconfirmed: " + error.Message; }
        }

        // Called by the existing Empty prefix AFTER EnterWriter, BEFORE its ModulePaused check.
        // Return true means the call belongs to this callback and its original MUST be skipped.
        // The actual Empty/event already ran once in ApplyIntakeDebit, including a final portion.
        private bool SuppressPresentedVolumeDebit(VolumeContainerItemInstanceModule module, float amount, bool runOriginal)
        {
            if (!_presentationEdibleActive || module?.Pointer != _consumptionPresentation?.Volume?.Pointer) return false;
            if (!runOriginal || _presentationEmptySeen || !_presentationExpectsEmpty
                || _writerDepth != _presentationEdibleDepth + 1 || !PresentationEffectsCurrent()
                || amount != _presentationFrame.Debit.Portion
                || !InventoryConsumption.ConfirmsVolumeDebit(_intakeItem, _presentationFrame.Debit))
                FailConsumptionPresentation("Edible callback attempted an unexpected second volume debit.");
            else _presentationEmptySeen = true;
            return true;
        }

        // Settled/rejected paths call this before dropping their retained item/stat references.
        // Pending native work is never silently reset to make a second attempt possible.
        private void ReleaseConsumptionPresentation()
        {
            if (_presentationStarted && _intakeReply.Action.Kind != PlayerInventoryMessage.ConsumeApplied)
                throw new InvalidOperationException("Unconfirmed consumption cannot release its native attempt.");
            _consumptionPresentation?.ReleaseObserver();
            _consumptionPresentation = null;
            _presentationFrame = default;
            _presentationRoutine = null;
            _presentationFadeIn = _presentationFadeOut = null;
            _presentationFadeInDone = _presentationFadeOutDone = false;
            _presentationFadeInStarted = _presentationFadeOutStarted = false;
            _presentationStarted = _presentationEntryPermit = _presentationEntryActive = false;
            _presentationRoutineActive = _presentationRoutineDone = _presentationStatsSeen = false;
            _presentationCompleteActive = _presentationCompleteDone = _presentationEdibleActive = _presentationEdibleDone = false;
            _presentationClearActive = _presentationCleared = _presentationEmptySeen = _presentationExpectsEmpty = false;
            _presentationInterrupted = false;
            _presentationInterruptActive = false;
            _presentationMidActionPhases = 0;
        }
    }
}
