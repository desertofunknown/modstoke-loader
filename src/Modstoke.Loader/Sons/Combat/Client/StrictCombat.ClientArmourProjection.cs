// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    public enum StrictCombatArmourProjectionStatus
    {
        Unavailable, Applied, AlreadyCurrent, WrongThread, BarrierRequired, LifeChanged,
        StateUnavailable, NativeStateUnavailable, Busy, MutationUncertain, LimitReached
    }

    public sealed partial class StrictCombatClient
    {
        private bool _projectingArmour;

        /// <summary>
        /// Projects current accepted armour onto its owned native pieces. Breaks are attempted
        /// once; an uncertain native result retires authority without releasing suppression.
        /// </summary>
        public StrictCombatArmourProjectionStatus ProjectLocalArmour()
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return StrictCombatArmourProjectionStatus.WrongThread;
            if (_projectingArmour || _acquiringArmour || _readingArmour || _projectingLife
                || _projectingHealth || _placingRespawn || _presentingWeapon || _readingWeaponInput
                || _projectingAmmunition || _updatingEquipment)
                return StrictCombatArmourProjectionStatus.Busy;
            if (!BeginArmourRead(out var readStatus))
                return readStatus == StrictCombatArmourReadStatus.LimitReached
                    ? StrictCombatArmourProjectionStatus.LimitReached : StrictCombatArmourProjectionStatus.Unavailable;
            _projectingArmour = true;
            bool mutationStarted = false;
            var barrier = _nativeArmourBarrier;
            try
            {
                if (barrier == null || barrier.IsRetired || !ReferenceEquals(barrier.Capture, _localArmourBinding)
                    || !ReferenceEquals(barrier.Life, _nativeLifeBarrier))
                    return StrictCombatArmourProjectionStatus.BarrierRequired;
                if (!TryReadArmourBindingInputs(out var owned, out var native, out _))
                    return StrictCombatArmourProjectionStatus.StateUnavailable;
                if (native.ServerState.LifeGeneration != barrier.Capture.LifeGeneration
                    || native.ServerState.LifeGeneration != barrier.Life.LifeGeneration)
                    return StrictCombatArmourProjectionStatus.LifeChanged;
                if (!ValidateLocalArmourBinding(barrier.Capture, owned, native, out _)
                    || !IsCurrentArmourProjection(barrier, owned, native))
                    return StrictCombatArmourProjectionStatus.NativeStateUnavailable;
                var accepted = native;
                var capture = barrier.Capture;
                if (!ReserveArmourProjectionReads(owned, capture))
                    return StrictCombatArmourProjectionStatus.LimitReached;

                // Native damage consumes the configured order from the end. Broken IDs
                // remain in accepted state, including when intermediate receipts were skipped.
                for (int i = owned.Count - 1; i >= 0; i--)
                {
                    if (!owned.TryGetPiece(i, out var resource))
                        return ArmourProjectionFailure(barrier, mutationStarted);
                    if (!resource.IsBroken || capture.HasCompletedBreak(i)) continue;
                    if (!TryRefreshArmourProjection(barrier, owned, accepted, out native)
                        || !capture.Original.TryGetSlot(resource.Slot, out var originalSlot))
                        return ArmourProjectionFailure(barrier, mutationStarted);
                    // Consumed inside TryBreak before entering native code. A false result
                    // may follow mutation, so no rollback or second attempt is permitted.
                    mutationStarted = true;
                    if (!StrictCombatArmourHooks.TryBreak(barrier, i))
                        return ArmourProjectionFailure(barrier, true);
                    if (!TryReadArmourSnapshot(out native, out _)
                        || !IsCurrentArmourProjection(barrier, owned, accepted)
                        || !HasCurrentArmourBindingInputs(owned, native, out _)
                        || !capture.TryCompleteBreak(owned, native, i, out _)
                        || !IsCurrentArmourProjection(barrier, owned, accepted))
                        return ArmourProjectionFailure(barrier, true);
                    barrier.CompleteBreakCursor(originalSlot.OrderIndex);
                    // Native damage advances this cursor after each successful break,
                    // before the next piece's callbacks can observe the armour system.
                    barrier.System._nextAvailableArmourSlotIndex = barrier.ProjectedCursor;
                    if (!TryRefreshArmourProjection(barrier, owned, accepted, out native)
                        || native.NextAvailableOrderIndex != barrier.ProjectedCursor)
                        return ArmourProjectionFailure(barrier, true);
                }

                for (int i = 0; i < owned.Count; i++)
                {
                    if (!owned.TryGetPiece(i, out var resource)
                        || !TryRefreshArmourProjection(barrier, owned, accepted, out native)
                        || !native.TryGetSlot(resource.Slot, out var slot))
                        return ArmourProjectionFailure(barrier, mutationStarted);
                    if (StrictCombatOwnedArmourState.SameFloat(slot.RemainingPoints, resource.RemainingPoints)) continue;
                    var data = capture.Original.Native.GetRetainedSlotData(resource.Slot);
                    if (data == null || !IsCurrentArmourProjection(barrier, owned, accepted))
                        return ArmourProjectionFailure(barrier, mutationStarted);
                    mutationStarted = true;
                    data.RemainingArmourpoints = resource.RemainingPoints;
                    if (!IsCurrentArmourProjection(barrier, owned, accepted))
                        return ArmourProjectionFailure(barrier, true);
                }

                if (!TryRefreshArmourProjection(barrier, owned, accepted, out native))
                    return ArmourProjectionFailure(barrier, mutationStarted);
                if (native.NextAvailableOrderIndex != barrier.ProjectedCursor)
                {
                    mutationStarted = true;
                    barrier.System._nextAvailableArmourSlotIndex = barrier.ProjectedCursor;
                    if (!IsCurrentArmourProjection(barrier, owned, accepted))
                        return ArmourProjectionFailure(barrier, true);
                }
                if (!TryRefreshArmourProjection(barrier, owned, accepted, out native)
                    || native.NextAvailableOrderIndex != barrier.ProjectedCursor)
                    return ArmourProjectionFailure(barrier, mutationStarted);
                for (int i = 0; i < owned.Count; i++)
                {
                    if (!owned.TryGetPiece(i, out var resource) || !native.TryGetSlot(resource.Slot, out var slot)
                        || !StrictCombatOwnedArmourState.SameFloat(slot.RemainingPoints, resource.RemainingPoints)
                        || (resource.IsBroken && !capture.HasCompletedBreak(i)))
                        return ArmourProjectionFailure(barrier, mutationStarted);
                }
                return mutationStarted ? StrictCombatArmourProjectionStatus.Applied : StrictCombatArmourProjectionStatus.AlreadyCurrent;
            }
            catch (Exception) { return ArmourProjectionFailure(barrier, mutationStarted); }
            finally { _projectingArmour = false; _readingArmour = false; }
        }

        private bool ReserveArmourProjectionReads(StrictCombatOwnedArmourState owned,
            StrictCombatLocalArmourBinding capture)
        {
            // The initial read is already charged. Reserve every remaining capture
            // before native effects so the frame budget cannot interrupt a break.
            int reads = 2 + owned.Count;
            for (int i = 0; i < owned.Count; i++)
                if (owned.TryGetPiece(i, out var piece) && piece.IsBroken && !capture.HasCompletedBreak(i))
                    reads += 3;
            if (reads > 64 - _armourReads) return false;
            _armourReads += reads;
            return true;
        }

        private bool TryRefreshArmourProjection(StrictCombatArmourBarrier barrier,
            StrictCombatOwnedArmourState owned, StrictCombatArmourSnapshot accepted,
            out StrictCombatArmourSnapshot native)
        {
            native = null;
            return IsCurrentArmourProjection(barrier, owned, accepted)
                && TryReadArmourSnapshot(out native, out _)
                && ValidateLocalArmourBinding(barrier.Capture, owned, native, out _)
                && IsCurrentArmourProjection(barrier, owned, accepted);
        }

        private bool IsCurrentArmourProjection(StrictCombatArmourBarrier barrier,
            StrictCombatOwnedArmourState owned, StrictCombatArmourSnapshot accepted)
        {
            return _projectingArmour && _readingArmour && barrier != null && !barrier.IsRetired
                && ReferenceEquals(barrier, _nativeArmourBarrier) && ReferenceEquals(barrier.Owner, this)
                && ReferenceEquals(barrier.Capture, _localArmourBinding)
                && ReferenceEquals(barrier.Life, _nativeLifeBarrier)
                && ReferenceEquals(barrier.Life.Player, _localPlayer)
                && ReferenceEquals(barrier.Capture.Original.Player.Player, _localPlayer)
                && accepted.ServerState.LifeStage != StrictCombatLifeStage.Uninitialized
                && accepted.ServerState.LifeGeneration == barrier.Capture.LifeGeneration
                && accepted.ServerState.LifeGeneration == barrier.Life.LifeGeneration
                && UnityEngine.Time.frameCount == accepted.Frame
                && IsCurrentNativeHealthOwner(barrier.Life)
                && HasCurrentArmourBindingInputs(owned, accepted, out _);
        }

        private static StrictCombatArmourProjectionStatus ArmourProjectionFailure(
            StrictCombatArmourBarrier barrier, bool mutationStarted)
        {
            if (!mutationStarted) return StrictCombatArmourProjectionStatus.NativeStateUnavailable;
            barrier?.Retire();
            return StrictCombatArmourProjectionStatus.MutationUncertain;
        }
    }
}
