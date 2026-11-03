// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    public sealed partial class StrictCombatClient
    {
        /// <summary>Prepares native lung interception before world/network startup.</summary>
        public static StrictCombatBreathingStatus PrepareNativeBreathingBarrierForNextStart()
            => StrictCombatLifeHooks.PrepareBreathingForNextStart();

        /// <summary>
        /// Owns the existing local lungs after life ownership and accepted lung delivery.
        /// No tank is required. Suppresses mapped native drain, recovery and drowning,
        /// including rebreather transfers. Call ProjectLocalBreathing to apply accepted
        /// air. Does not supply server recovery/drowning policy or enable strict mode.
        /// </summary>
        public bool TryAcquireNativeBreathingBarrier(out StrictCombatBreathingStatus status)
            => AcquireNativeBreathing(false, false, out status);

        /// <summary>
        /// Takes over healthy owned lungs after completed native breathing calls.
        /// Requires pre-start preparation, owned native life and accepted server air.
        /// In-flight calls, incomplete history and native lifecycle transitions are not
        /// reconciled. Does not adopt native air; project accepted air after success.
        /// </summary>
        public bool TryTakeOverNativeBreathingBarrier(out StrictCombatBreathingStatus status)
            => AcquireNativeBreathing(false, true, out status);

        /// <summary>
        /// Retains the same native lungs after a committed respawn and accepted new-life
        /// resources. Replacement objects are rejected. Does not grant or project air.
        /// </summary>
        public bool TryHandoffNativeBreathingBarrier(out StrictCombatBreathingStatus status)
            => AcquireNativeBreathing(true, false, out status);

        private bool BreathingOperationBusy => _updatingEquipment || _acquiringArmour || _readingArmour
            || _projectingArmour || _projectingAmmunition || _projectingHealth || _projectingLife
            || _placingRespawn || _readingWeaponInput || _presentingWeapon;

        private bool AcquireNativeBreathing(bool handoff, bool allowCompletedNativeActivity,
            out StrictCombatBreathingStatus status)
        {
            status = StrictCombatBreathingStatus.WrongThread;
            if (Environment.CurrentManagedThreadId != _ownerThreadId) return false;
            status = StrictCombatBreathingStatus.UnsupportedContext;
            if (BreathingOperationBusy)
            {
                if (allowCompletedNativeActivity) status = StrictCombatBreathingStatus.Busy;
                return false;
            }
            _updatingEquipment = true;
            StrictCombatBreathingBarrier candidate = null;
            try
            {
                var life = _nativeLifeBarrier;
                if (life == null || life.IsRetired || !IsCurrentNativeHealthOwner(life)
                    || !TryReadOwnedBreathing(out var binding, out var state, out var air)
                    || !air.IsProvisioned || state.LifeStage != StrictCombatLifeStage.Alive
                    || state.LifeGeneration != life.LifeGeneration) return false;
                int frame = UnityEngine.Time.frameCount;
                var previous = life.Breathing;
                if (previous != null && previous.LifeGeneration == state.LifeGeneration)
                {
                    if (handoff && !HasCommittedNativeRespawn(_respawnRequest.LifeGeneration, in binding, in state)) return false;
                    if (!IsCurrentBreathingOperation(previous, in binding, in state, air, frame)) return false;
                    status = StrictCombatBreathingStatus.Success;
                    return true;
                }
                if ((previous != null) != handoff) return false;
                var breathing = life.Vitals.LungBreathing;
                if (breathing == null || breathing.WasCollected) return false;
                float capacity = StrictCombatBreathingBarrier.Represent(air.Capacity);
                if (!float.IsFinite(capacity) || capacity <= 0) return false;
                candidate = new StrictCombatBreathingBarrier(life, in binding, breathing, capacity,
                    previous?.AllowsCompletedNativeActivity ?? allowCompletedNativeActivity);
                return StrictCombatLifeHooks.TryAcquireBreathing(candidate, previous,
                    in binding, in state, air, frame, out status);
            }
            catch (Exception)
            {
                if (candidate != null && ReferenceEquals(candidate.Life.Breathing, candidate)) candidate.Retire();
                status = StrictCombatBreathingStatus.Failed;
                return false;
            }
            finally { _updatingEquipment = false; }
        }

        /// <summary>
        /// Applies the latest accepted lung capacity and air to the exact owned life.
        /// Applied means field readback succeeded; it is not continuous enforcement
        /// against direct memory writes. Native tank fields and global settings are unchanged.
        /// </summary>
        public StrictCombatBreathingProjectionStatus ProjectLocalBreathing()
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return StrictCombatBreathingProjectionStatus.WrongThread;
            if (BreathingOperationBusy) return StrictCombatBreathingProjectionStatus.Busy;
            _updatingEquipment = true;
            try
            {
                if (!TryReadOwnedBreathing(out var binding, out var state, out var air) || !air.IsProvisioned)
                    return StrictCombatBreathingProjectionStatus.Unavailable;
                var barrier = _nativeLifeBarrier?.Breathing;
                if (barrier == null || barrier.IsRetired) return StrictCombatBreathingProjectionStatus.BarrierRequired;
                return barrier.Project(in binding, in state, air, UnityEngine.Time.frameCount);
            }
            catch (Exception) { return StrictCombatBreathingProjectionStatus.UnsupportedContext; }
            finally { _updatingEquipment = false; }
        }

        internal bool IsCurrentBreathingOperation(StrictCombatBreathingBarrier barrier,
            in StrictCombatBinding binding, in StrictCombatSnapshot state, StrictCombatOwnedBreathingState air, int frame)
            => barrier != null && ReferenceEquals(barrier.Life.Breathing, barrier)
                && BreathingContextCurrent(barrier, in binding, in state, air, frame);

        internal bool IsCurrentBreathingAcquisition(StrictCombatBreathingBarrier candidate,
            StrictCombatBreathingBarrier previous, in StrictCombatBinding binding,
            in StrictCombatSnapshot state, StrictCombatOwnedBreathingState air, int frame)
        {
            if (!BreathingContextCurrent(candidate, in binding, in state, air, frame)
                || !ReferenceEquals(candidate.Life.Breathing, previous)) return false;
            if (previous == null) return true;
            return !previous.IsRetired && candidate.SameObjects(previous)
                && previous.Binding.SameBinding(binding)
                && previous.LifeGeneration != 0 && previous.LifeGeneration != ulong.MaxValue
                && state.LifeGeneration == previous.LifeGeneration + 1
                && HasCommittedNativeRespawn(previous.LifeGeneration, in binding, in state);
        }

        private bool BreathingContextCurrent(StrictCombatBreathingBarrier barrier,
            in StrictCombatBinding binding, in StrictCombatSnapshot state, StrictCombatOwnedBreathingState air, int frame)
        {
            if (!_updatingEquipment || Environment.CurrentManagedThreadId != _ownerThreadId
                || barrier == null || !ReferenceEquals(barrier.Owner, this)
                || !ReferenceEquals(barrier.Life, _nativeLifeBarrier)
                || air == null || !air.IsProvisioned || !barrier.Binding.SameBinding(binding)
                || state.LifeStage != StrictCombatLifeStage.Alive || state.LifeGeneration != barrier.LifeGeneration
                || barrier.Capacity != StrictCombatBreathingBarrier.Represent(air.Capacity)
                || !BreathingSnapshotCurrent(in binding, in state, air, frame)
                || !barrier.NativeObjectsCurrent()) return false;
            return BreathingSnapshotCurrent(in binding, in state, air, frame);
        }

        private bool BreathingSnapshotCurrent(in StrictCombatBinding binding, in StrictCombatSnapshot state,
            StrictCombatOwnedBreathingState air, int frame)
            => TryReadOwnedBreathing(out var currentBinding, out var currentState, out var currentAir)
                && ReferenceEquals(air, currentAir) && binding.SameBinding(currentBinding)
                && state.Revision == currentState.Revision && state.LifeGeneration == currentState.LifeGeneration
                && currentState.LifeStage == StrictCombatLifeStage.Alive && UnityEngine.Time.frameCount == frame;
    }
}
