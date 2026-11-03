// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    public enum StrictCombatEquipmentStatus
    {
        Unavailable, Pending, Equipped, Cancelling, Returned, Busy, WrongThread,
        UnsupportedContext, MutationUncertain
    }

    public sealed partial class StrictCombatClient
    {
        private StrictCombatEquipmentOperation _equipmentOperation;
        private StrictCombatLocalWeapon _equipmentRecoverySource;
        private bool _updatingEquipment;

        public StrictCombatEquipmentStatus EquipmentStatus
            => _equipmentOperation?.Status ?? StrictCombatEquipmentStatus.Unavailable;
        public bool EquipmentCleanupPending => _equipmentOperation?.RequiresCleanup == true;
        internal bool HasUnfinishedEquipmentOperation => _equipmentOperation is not null && !_equipmentOperation.IsFinished;
        private bool EquipmentBlocksRespawn => _equipmentOperation is not null && !_equipmentOperation.IsReturned;

        /// <summary>
        /// Starts one explicit attempt to restore the exact pistol retained by a completed
        /// owned stash. Requires accepted ownership/equip state and completed local get-up.
        /// Does not grant items or enable strict combat. Continue UpdateLocalEquipment to
        /// advance preparation. The SDK also drains cancellation while its runtime ticks,
        /// including after client retirement; explicit caller updates remain supported.
        /// Cleanup is not guaranteed when the process quits. Returns Busy if the bounded
        /// cleanup retention is full, without starting a new attempt.
        /// </summary>
        public bool TryBeginLocalEquipment(out StrictCombatEquipmentStatus status)
        {
            status = StrictCombatEquipmentStatus.WrongThread;
            if (Environment.CurrentManagedThreadId != _ownerThreadId) return false;
            status = StrictCombatEquipmentStatus.Busy;
            if (EquipmentMutationBusy()) return false;
            if (_equipmentOperation is not null && !_equipmentOperation.IsReturned)
            {
                status = _equipmentOperation.Status;
                return false;
            }
            _updatingEquipment = true;
            try
            {
                status = StrictCombatEquipmentStatus.UnsupportedContext;
                if (_localLife is null || !_localLife.IsReadyForEquipment()
                    || !TryRead(out var binding, out var state) || !state.OwnsPistol || !state.Equipped)
                    return false;
                var source = _equipmentRecoverySource;
                if (source is null && !_localLife.TryReadStashedEquipment(out source)) return false;
                if (!ReferenceEquals(source.Owner, this) || !ReferenceEquals(source.Life, _nativeLifeBarrier)
                    || !source.MatchesEquipmentBinding(in binding)) return false;
                status = StrictCombatEquipmentStatus.Busy;
                if (!StrictCombatEquipmentCleanupPump.TryRetain(this, _ownerThreadId)) return false;
                ClearWeaponInput();
                ClearWeaponShots();
                _equipmentOperation = new StrictCombatEquipmentOperation(source, in binding, in state);
                status = _equipmentOperation.Advance();
                return status is StrictCombatEquipmentStatus.Pending or StrictCombatEquipmentStatus.Equipped;
            }
            catch (Exception)
            {
                _equipmentOperation?.RequestCancellation();
                status = _equipmentOperation?.Status ?? StrictCombatEquipmentStatus.UnsupportedContext;
                return false;
            }
            finally { _updatingEquipment = false; }
        }

        /// <summary>
        /// Advances one retained equipment attempt on the game thread. Cancelling includes
        /// deferred resource destruction. MutationUncertain must not be retried as an item
        /// grant or treated as a successful return. No new attempt starts automatically.
        /// The SDK also advances cancellation while its runtime ticks, but does not advance
        /// ordinary preparation or guarantee cleanup at process quit.
        /// </summary>
        public StrictCombatEquipmentStatus UpdateLocalEquipment()
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId) return StrictCombatEquipmentStatus.WrongThread;
            if (EquipmentMutationBusy()) return StrictCombatEquipmentStatus.Busy;
            if (_equipmentOperation is null) return StrictCombatEquipmentStatus.Unavailable;
            _updatingEquipment = true;
            try { return _equipmentOperation.Advance(); }
            finally { _updatingEquipment = false; }
        }

        public bool CancelLocalEquipment()
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId || _equipmentOperation is null
                || _equipmentOperation.IsFinished) return false;
            _equipmentOperation.RequestCancellation();
            ClearWeaponInput();
            ClearWeaponShots();
            return true;
        }

        private bool EquipmentMutationBusy()
            => _updatingEquipment || _readingWeaponInput || _projectingHealth || _projectingLife
                || _placingRespawn || _presentingWeapon || _projectingArmour || _projectingAmmunition;

        internal bool IsEquipmentOperationCurrent(StrictCombatEquipmentOperation operation)
            => Environment.CurrentManagedThreadId == _ownerThreadId && ReferenceEquals(operation, _equipmentOperation);

        internal bool IsAdoptedEquipmentCurrent(StrictCombatEquipmentOperation operation)
        {
            var barrier = _nativeWeaponBarrier;
            return IsEquipmentOperationCurrent(operation) && operation.IsActive
                && barrier is not null && !barrier.IsRetired
                && ReferenceEquals(barrier.Owner, this) && operation.Reservation.CanRepresent(barrier.Capture)
                && TryRead(out var binding, out var state)
                && barrier.Capture.IsHeldCurrent(in binding, in state, requireIdle: false);
        }

        internal bool TryAdoptEquipment(StrictCombatEquipmentOperation operation)
        {
            if (!_updatingEquipment || !IsEquipmentOperationCurrent(operation) || !operation.CanAdopt
                || _projectingArmour || _projectingAmmunition || !TryRead(out var binding, out var state)
                || !StrictCombatLocalWeapon.TryCapture(this, _nativeLifeBarrier, in binding, in state, out var capture)
                || !operation.Reservation.CanAdopt(capture)
                || !StrictCombatWeaponHooks.TryAcquire(capture, out var barrier, out _)) return false;
            _nativeWeaponBarrier?.Retire();
            _nativeWeaponBarrier = barrier;
            ClearWeaponInput();
            ClearWeaponShots();
            _projectingAmmunition = true;
            try
            {
                var result = capture.ProjectAmmunition(this, barrier, in binding, in state, UnityEngine.Time.frameCount);
                if (result is not (StrictCombatAmmoProjectionStatus.Applied or StrictCombatAmmoProjectionStatus.AlreadyCurrent))
                { barrier.Retire(); return false; }
                _equipmentRecoverySource = capture;
                _weaponInputStatus = StrictCombatWeaponInputStatus.Ready;
                return true;
            }
            finally { _projectingAmmunition = false; }
        }

        // Called before native downed/death presentation changes inventory enable/state.
        // A changed generation or uncertain inventory result cannot authorize recovery.
        private StrictCombatLocalLifeStatus? PrepareEquipmentLifeTransition(in StrictCombatSnapshot state)
        {
            if (_equipmentOperation is null || _equipmentOperation.IsReturned) return null;
            if (state.LifeStage == StrictCombatLifeStage.Alive && !_equipmentOperation.IsUncertain) return null;
            _equipmentOperation.RequestCancellation();
            _updatingEquipment = true;
            try
            {
                var result = _equipmentOperation.Advance();
                if (result == StrictCombatEquipmentStatus.Returned) return null;
                return result == StrictCombatEquipmentStatus.MutationUncertain
                    ? StrictCombatLocalLifeStatus.MutationUncertain : StrictCombatLocalLifeStatus.Pending;
            }
            finally { _updatingEquipment = false; }
        }
    }
}
