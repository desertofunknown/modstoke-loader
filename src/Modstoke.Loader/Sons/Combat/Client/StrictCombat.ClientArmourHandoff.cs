// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    public sealed partial class StrictCombatClient
    {
        /// <summary>
        /// Transfers armour ownership after a correlated respawn and an explicit new-life
        /// server grant. Retains native identities; never equips, grants or restores armour.
        /// </summary>
        public bool TryHandoffNativeArmourBarrier(out StrictCombatLocalArmourBinding binding,
            out StrictCombatArmourStatus status)
        {
            binding = null;
            status = StrictCombatArmourStatus.WrongThread;
            if (Environment.CurrentManagedThreadId != _ownerThreadId) return false;
            status = StrictCombatArmourStatus.Busy;
            if (_acquiringArmour || _readingArmour || _projectingArmour || _projectingLife
                || _projectingHealth || _placingRespawn || _presentingWeapon || _readingWeaponInput
                || _projectingAmmunition || _updatingEquipment) return false;
            if (!BeginArmourRead(out var readStatus))
            {
                status = readStatus == StrictCombatArmourReadStatus.LimitReached
                    ? StrictCombatArmourStatus.LimitReached : StrictCombatArmourStatus.Unavailable;
                return false;
            }
            _acquiringArmour = _projectingArmour = true;
            StrictCombatLocalArmourBinding candidate = null;
            StrictCombatArmourBarrier acquired = null;
            bool published = false;
            try
            {
                status = StrictCombatArmourStatus.LimitReached;
                // Initial capture, two publication checks and final readback share the
                // ordinary read budget. Reserve before changing ownership.
                if (_armourReads > 61) return false;
                _armourReads += 3;
                var previous = _nativeArmourBarrier;
                var life = _nativeLifeBarrier;
                status = StrictCombatArmourStatus.Unavailable;
                if (previous == null || !ReferenceEquals(previous.Capture, _localArmourBinding)) return false;
                status = StrictCombatArmourStatus.Retired;
                if (previous.IsRetired) return false;
                status = StrictCombatArmourStatus.LifeBarrierRequired;
                if (life == null || life.IsRetired || !ReferenceEquals(previous.Life, life)
                    || !ReferenceEquals(life.Owner, this) || !ReferenceEquals(life.Player, _localPlayer)) return false;
                status = StrictCombatArmourStatus.RespawnRequired;
                if (!TryRead(out var acceptedBinding, out var state)
                    || !HasCommittedNativeRespawn(_respawnRequest.LifeGeneration, in acceptedBinding, in state)
                    || state.LifeGeneration != life.LifeGeneration) return false;
                status = StrictCombatArmourStatus.StaleObservation;
                if (state.LifeStage != StrictCombatLifeStage.Alive) return false;
                if (!TryReadArmourBindingInputs(out var owned, out var native, out var bindingStatus))
                {
                    status = bindingStatus == StrictCombatArmourBindingStatus.Unprovisioned
                        ? StrictCombatArmourStatus.Unprovisioned : StrictCombatArmourStatus.StaleObservation;
                    return false;
                }
                if (previous.Capture.LifeGeneration == state.LifeGeneration)
                {
                    if (!ValidateLocalArmourBinding(previous.Capture, owned, native, out _)
                        || !IsCurrentArmourProjection(previous, owned, native)) return false;
                    binding = previous.Capture;
                    status = StrictCombatArmourStatus.Success;
                    return true;
                }
                if (previous.Capture.LifeGeneration != _respawnRequest.LifeGeneration) return false;
                if (native.NextAvailableOrderIndex != previous.ProjectedCursor
                    || !previous.Capture.CanRetainNativeForNextLife(native, out _))
                { previous.Retire(); return false; }
                // The new server grant must cover exactly the still-equipped pieces.
                // A deferred destroyed orphan can settle before a later attempt.
                if (!StrictCombatLocalArmourBinding.TryCreate(this, owned, native, out candidate, out _)) return false;
                if (!StrictCombatArmourHooks.TryHandoff(this, previous, candidate, life, owned, native,
                        out acquired, out status)) return false;
                _nativeArmourBarrier = acquired;
                _localArmourBinding = candidate;
                published = true;
                if (!TryRefreshArmourProjection(acquired, owned, native, out _))
                {
                    acquired.Retire();
                    status = StrictCombatArmourStatus.StaleObservation;
                    return false;
                }
                binding = candidate;
                status = StrictCombatArmourStatus.Success;
                return true;
            }
            catch (Exception)
            {
                if (published) acquired?.Retire();
                status = StrictCombatArmourStatus.Failed;
                return false;
            }
            finally
            {
                if (candidate != null && !ReferenceEquals(candidate, _localArmourBinding)) candidate.Retire();
                _projectingArmour = _acquiringArmour = _readingArmour = false;
            }
        }

        internal bool IsCurrentArmourHandoffContext(StrictCombatArmourBarrier previous,
            StrictCombatLocalArmourBinding candidate, StrictCombatLifeBarrier life,
            StrictCombatOwnedArmourState owned, StrictCombatArmourSnapshot native)
        {
            return _projectingArmour && _acquiringArmour && _readingArmour
                && previous != null && !previous.IsRetired && candidate != null && !candidate.IsRetired
                && life != null && !life.IsRetired && native != null
                && ReferenceEquals(previous, _nativeArmourBarrier)
                && ReferenceEquals(previous.Capture, _localArmourBinding)
                && ReferenceEquals(previous.Owner, this) && ReferenceEquals(candidate.Owner, this)
                && ReferenceEquals(candidate.Original, native) && ReferenceEquals(candidate.InitialState, owned)
                && ReferenceEquals(life, _nativeLifeBarrier) && ReferenceEquals(life, previous.Life)
                && ReferenceEquals(life.Player, _localPlayer) && ReferenceEquals(native.Player.Player, _localPlayer)
                && candidate.LifeGeneration == life.LifeGeneration
                && native.NextAvailableOrderIndex == previous.ProjectedCursor
                && HasCommittedNativeRespawn(previous.Capture.LifeGeneration, native.Binding, native.ServerState)
                && previous.Capture.CanRetainNativeForNextLife(native, out _)
                && candidate.Matches(owned, native, out _)
                && IsCurrentArmourSnapshot(native, out _) && IsCurrentNativeHealthOwner(life)
                && HasCurrentArmourBindingInputs(owned, native, out _)
                && !previous.IsRetired && !candidate.IsRetired;
        }

        private bool HasCommittedNativeRespawn(ulong previousLife, in StrictCombatBinding binding,
            in StrictCombatSnapshot state)
        {
            return previousLife != 0 && previousLife != ulong.MaxValue
                && _respawnStatus == StrictCombatRespawnClientStatus.Committed
                && _respawnRequest.IsValid && _respawnRequest.LifeGeneration == previousLife
                && _respawnOutcome.IsValid && _respawnOutcome.Result == StrictCombatRespawnResult.Committed
                && _respawnRequest.SameRequest(_respawnOutcome.Request)
                && _respawnOffer.IsValid && _respawnOutcome.OperationId == _respawnOffer.OperationId
                && _respawnOutcome.State.LifeGeneration == previousLife + 1
                && state.LifeGeneration == previousLife + 1 && MatchesRespawnBinding(in binding);
        }

        private bool RetainsArmourAcrossRespawn(StrictCombatLocalArmourBinding capture,
            in StrictCombatBinding binding, in StrictCombatSnapshot state)
        {
            var armour = _nativeArmourBarrier;
            var life = _nativeLifeBarrier;
            if (capture == null || capture.IsRetired || armour == null || armour.IsRetired
                || life == null || life.IsRetired || !ReferenceEquals(capture, armour.Capture)
                || !ReferenceEquals(armour.Life, life) || !capture.Binding.SameBinding(binding)
                || capture.LifeGeneration == ulong.MaxValue || state.LifeGeneration != capture.LifeGeneration + 1)
                return false;
            return (life.LifeGeneration == capture.LifeGeneration && RetainsRespawnContainment(life, in state))
                || (life.LifeGeneration == state.LifeGeneration
                    && HasCommittedNativeRespawn(capture.LifeGeneration, in binding, in state));
        }
    }
}
