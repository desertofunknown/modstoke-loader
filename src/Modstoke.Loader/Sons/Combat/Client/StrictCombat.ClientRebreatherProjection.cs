// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    public enum StrictCombatRebreatherProjectionStatus
    {
        Unavailable, Applied, AlreadyCurrent, WrongThread, BarrierRequired, LifeChanged,
        UnsupportedContext, Busy, MutationUncertain
    }

    public sealed partial class StrictCombatClient
    {
        private bool _projectingRebreather;
        private bool _projectingRebreatherActivation;

        /// <summary>
        /// Projects accepted tank air and capacity into the owned module and its breathing-data mirror.
        /// Does not refill native inventory, change lung air or activate the rebreather.
        /// Applied confirms local field readback, not continuous enforcement or client integrity.
        /// </summary>
        public StrictCombatRebreatherProjectionStatus ProjectLocalRebreather()
            => ProjectLocalRebreather(includeActivation: false);

        /// <summary>
        /// Projects accepted tank values and uses accepted IsWorn to enable or disable
        /// native breathing's use of the tank. Requires negotiated wear receipts.
        /// Does not change lung air, equip an item or animate the mouthpiece.
        /// Applied confirms field readback, not authoritative lung simulation.
        /// </summary>
        public StrictCombatRebreatherProjectionStatus ProjectLocalRebreatherActivation()
            => ProjectLocalRebreather(includeActivation: true);

        private StrictCombatRebreatherProjectionStatus ProjectLocalRebreather(bool includeActivation)
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return StrictCombatRebreatherProjectionStatus.WrongThread;
            if (_projectingRebreather || _updatingEquipment || _projectingArmour || _projectingAmmunition
                || _projectingHealth || _projectingLife || _placingRespawn || _readingWeaponInput || _presentingWeapon)
                return StrictCombatRebreatherProjectionStatus.Busy;
            _projectingRebreather = true;
            _projectingRebreatherActivation = includeActivation;
            _updatingEquipment = true;
            try
            {
                if (!TryReadOwnedRebreather(out var binding, out var state, out var air) || !air.IsProvisioned)
                    return StrictCombatRebreatherProjectionStatus.Unavailable;
                if (!RebreatherProjectionAllowed(air, includeActivation))
                    return StrictCombatRebreatherProjectionStatus.UnsupportedContext;
                var barrier = _nativeRebreatherBarrier;
                if (barrier == null || barrier.IsRetired || !ReferenceEquals(barrier.Owner, this)
                    || !ReferenceEquals(barrier.Capture.Life, _nativeLifeBarrier) || _nativeLifeBarrier.IsRetired)
                    return StrictCombatRebreatherProjectionStatus.BarrierRequired;
                if (state.LifeGeneration == 0 || state.LifeGeneration != barrier.Capture.LifeGeneration
                    || state.LifeGeneration != barrier.Capture.Life.LifeGeneration)
                    return StrictCombatRebreatherProjectionStatus.LifeChanged;
                if (!barrier.Capture.IsCurrent(in binding, in state))
                    return StrictCombatRebreatherProjectionStatus.UnsupportedContext;
                int frame = UnityEngine.Time.frameCount;
                return includeActivation
                    ? barrier.Capture.ProjectActivation(this, barrier, in binding, in state, air, frame)
                    : barrier.Capture.ProjectAir(this, barrier, in binding, in state, air, frame);
            }
            catch (Exception) { return StrictCombatRebreatherProjectionStatus.UnsupportedContext; }
            finally
            {
                _updatingEquipment = false;
                _projectingRebreatherActivation = false;
                _projectingRebreather = false;
            }
        }

        internal bool IsCurrentRebreatherProjection(StrictCombatLocalRebreather capture,
            StrictCombatRebreatherBarrier barrier, in StrictCombatBinding binding,
            in StrictCombatSnapshot state, StrictCombatOwnedRebreatherState air, int frame,
            bool includeActivation = false)
        {
            if (!_projectingRebreather || !_updatingEquipment || Environment.CurrentManagedThreadId != _ownerThreadId
                || _projectingRebreatherActivation != includeActivation
                || !ReferenceEquals(barrier, _nativeRebreatherBarrier) || barrier.IsRetired
                || !ReferenceEquals(capture, barrier.Capture) || !ReferenceEquals(barrier.Owner, this)
                || !ReferenceEquals(capture.Life, _nativeLifeBarrier) || capture.Life.IsRetired
                || !RebreatherProjectionAllowed(air, includeActivation)
                || !RebreatherSnapshotMatches(in binding, in state, air, frame)
                || !capture.IsCurrent(in binding, in state)) return false;
            return RebreatherSnapshotMatches(in binding, in state, air, frame);
        }

        private bool RebreatherProjectionAllowed(StrictCombatOwnedRebreatherState air, bool includeActivation)
            => air != null && (includeActivation
                ? _requiresRebreatherWearReceipts && air.HasWearState
                : !_requiresRebreatherWearReceipts || (air.HasWearState && air.IsWorn));

        private bool RebreatherSnapshotMatches(in StrictCombatBinding binding, in StrictCombatSnapshot state,
            StrictCombatOwnedRebreatherState air, int frame)
            => TryReadOwnedRebreather(out var currentBinding, out var currentState, out var currentAir)
                && ReferenceEquals(air, currentAir) && air.IsProvisioned
                && binding.SameBinding(in currentBinding)
                && state.Revision == currentState.Revision && state.LifeGeneration == currentState.LifeGeneration
                && state.LifeGeneration != 0 && state.LifeStage == StrictCombatLifeStage.Alive
                && UnityEngine.Time.frameCount == frame;
    }
}
