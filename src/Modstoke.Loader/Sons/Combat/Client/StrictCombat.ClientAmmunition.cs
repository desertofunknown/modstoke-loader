// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    public enum StrictCombatAmmoProjectionStatus
    {
        Unavailable, Applied, AlreadyCurrent, WrongThread, BarrierRequired, LifeChanged,
        UnsupportedContext, Busy, MutationUncertain
    }

    public sealed partial class StrictCombatClient
    {
        private bool _projectingAmmunition;

        /// <summary>
        /// Projects the latest accepted magazine count into the captured held pistol.
        /// Does not modify reserve items, refill ammunition, equip a weapon or enable combat.
        /// Applied confirms local field readback, not continuous protection or client integrity.
        /// </summary>
        public StrictCombatAmmoProjectionStatus ProjectLocalAmmunition()
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return StrictCombatAmmoProjectionStatus.WrongThread;
            if (_updatingEquipment || _projectingArmour || _projectingAmmunition || _projectingHealth || _projectingLife || _placingRespawn
                || _readingWeaponInput || _presentingWeapon)
                return StrictCombatAmmoProjectionStatus.Busy;
            _projectingAmmunition = true;
            try
            {
                if (!TryRead(out var binding, out var state))
                    return StrictCombatAmmoProjectionStatus.Unavailable;
                var barrier = _nativeWeaponBarrier;
                if (barrier == null || barrier.IsRetired || !ReferenceEquals(barrier.Owner, this)
                    || !ReferenceEquals(barrier.Capture.Life, _nativeLifeBarrier)
                    || _nativeLifeBarrier.IsRetired)
                    return StrictCombatAmmoProjectionStatus.BarrierRequired;
                if (state.LifeGeneration == 0 || state.LifeGeneration != barrier.Capture.Life.LifeGeneration)
                    return StrictCombatAmmoProjectionStatus.LifeChanged;
                if (!barrier.Capture.IsHeldCurrent(in binding, in state, requireIdle: false))
                    return StrictCombatAmmoProjectionStatus.UnsupportedContext;
                int frame = UnityEngine.Time.frameCount;
                return barrier.Capture.ProjectAmmunition(this, barrier, in binding, in state, frame);
            }
            catch (Exception) { return StrictCombatAmmoProjectionStatus.UnsupportedContext; }
            finally { _projectingAmmunition = false; }
        }

        internal bool IsCurrentAmmoProjection(StrictCombatLocalWeapon capture,
            StrictCombatWeaponBarrier barrier, in StrictCombatBinding binding,
            in StrictCombatSnapshot state, int frame)
        {
            // Bracket native context reads with the accepted revision. No caller-supplied
            // snapshot can authorize a write, and a reentrant receipt cannot retarget one.
            if (!_projectingAmmunition || Environment.CurrentManagedThreadId != _ownerThreadId
                || !ReferenceEquals(barrier, _nativeWeaponBarrier) || barrier.IsRetired
                || !ReferenceEquals(capture, barrier.Capture) || !ReferenceEquals(barrier.Owner, this)
                || !ReferenceEquals(capture.Life, _nativeLifeBarrier) || capture.Life.IsRetired
                || !AmmoSnapshotMatches(in binding, in state, frame)
                || !capture.IsHeldCurrent(in binding, in state, requireIdle: false)) return false;
            return AmmoSnapshotMatches(in binding, in state, frame);
        }

        private bool AmmoSnapshotMatches(in StrictCombatBinding binding, in StrictCombatSnapshot state, int frame)
            => IsCurrent(out _) && _state.HasBinding && _state.HasSnapshot
                && binding.SameBinding(_state.Binding)
                && state.Revision == _state.Snapshot.Revision
                && state.LifeGeneration == _state.Snapshot.LifeGeneration
                && state.LifeGeneration != 0 && state.LifeStage == StrictCombatLifeStage.Alive
                && state.OwnsPistol && state.Equipped
                && state.Magazine == _state.Snapshot.Magazine
                && UnityEngine.Time.frameCount == frame;
    }
}
