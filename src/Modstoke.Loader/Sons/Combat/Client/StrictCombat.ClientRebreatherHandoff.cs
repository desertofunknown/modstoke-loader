// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    public sealed partial class StrictCombatClient
    {
        /// <summary>
        /// Transfers the same native rebreather after a committed respawn and accepted
        /// new-life resources. Does not equip, refill, grant or project air. Call the
        /// ordinary projection method afterward; replaced equipment is not adopted.
        /// </summary>
        public bool TryHandoffNativeRebreatherBarrier(out StrictCombatRebreatherStatus status)
        {
            status = StrictCombatRebreatherStatus.WrongThread;
            if (Environment.CurrentManagedThreadId != _ownerThreadId) return false;
            status = StrictCombatRebreatherStatus.UnsupportedContext;
            if (_updatingEquipment || _projectingRebreather || _acquiringArmour || _readingArmour
                || _projectingArmour || _projectingAmmunition || _projectingHealth || _projectingLife
                || _placingRespawn || _readingWeaponInput || _presentingWeapon) return false;
            _updatingEquipment = true;
            StrictCombatRebreatherBarrier acquired = null;
            bool published = false;
            try
            {
                var previous = _nativeRebreatherBarrier;
                var life = _nativeLifeBarrier;
                if (previous == null || previous.IsRetired || !ReferenceEquals(previous.Owner, this)
                    || life == null || life.IsRetired || !ReferenceEquals(previous.Capture.Life, life)
                    || !ReferenceEquals(life.Owner, this) || !ReferenceEquals(life.Player, _localPlayer)
                    || !TryReadOwnedRebreather(out var binding, out var state, out var owned)
                    || !owned.IsProvisioned || state.LifeStage != StrictCombatLifeStage.Alive
                    || state.LifeGeneration != life.LifeGeneration
                    || !HasCommittedNativeRespawn(_respawnRequest.LifeGeneration, in binding, in state)) return false;
                int frame = UnityEngine.Time.frameCount;
                if (previous.Capture.LifeGeneration == state.LifeGeneration)
                {
                    // Repeating a completed handoff validates the current capture;
                    // it does not replace ownership again or change any resources.
                    if (!previous.Capture.IsCurrent(in binding, in state)
                        || !RebreatherSnapshotMatches(in binding, in state, owned, frame)
                        || previous.IsRetired) return false;
                    status = StrictCombatRebreatherStatus.Success;
                    return true;
                }
                if (previous.Capture.LifeGeneration != _respawnRequest.LifeGeneration
                    || !StrictCombatLocalRebreather.TryCapture(this, life, in binding, in state, out var candidate)
                    || !previous.Capture.CanRetainNativeForNextLife(candidate)) return false;
                if (!StrictCombatRebreatherHooks.TryHandoff(this, previous, candidate, owned,
                        in binding, in state, frame, out acquired, out status))
                {
                    acquired?.Retire();
                    return false;
                }

                _nativeRebreatherBarrier = acquired;
                published = true;
                if (acquired.IsRetired || !candidate.IsCurrent(in binding, in state)
                    || !RebreatherSnapshotMatches(in binding, in state, owned, frame) || acquired.IsRetired)
                {
                    acquired.Retire();
                    status = StrictCombatRebreatherStatus.UnsupportedContext;
                    return false;
                }
                status = StrictCombatRebreatherStatus.Success;
                return true;
            }
            catch (Exception)
            {
                if (published) acquired?.Retire();
                status = StrictCombatRebreatherStatus.Failed;
                return false;
            }
            finally { _updatingEquipment = false; }
        }

        internal bool IsCurrentRebreatherHandoffContext(StrictCombatRebreatherBarrier previous,
            StrictCombatLocalRebreather candidate, StrictCombatOwnedRebreatherState owned,
            in StrictCombatBinding binding, in StrictCombatSnapshot state, int frame)
        {
            return _updatingEquipment && !_projectingRebreather
                && Environment.CurrentManagedThreadId == _ownerThreadId
                && previous != null && !previous.IsRetired && candidate != null
                && ReferenceEquals(previous, _nativeRebreatherBarrier)
                && ReferenceEquals(previous.Owner, this) && ReferenceEquals(candidate.Owner, this)
                && ReferenceEquals(candidate.Life, _nativeLifeBarrier)
                && candidate.Life != null && !candidate.Life.IsRetired
                && ReferenceEquals(candidate.Life.Player, _localPlayer)
                && previous.Capture.CanRetainNativeForNextLife(candidate)
                && HasCommittedNativeRespawn(previous.Capture.LifeGeneration, in binding, in state)
                && RebreatherSnapshotMatches(in binding, in state, owned, frame)
                && candidate.IsCurrent(in binding, in state)
                && RebreatherSnapshotMatches(in binding, in state, owned, frame)
                && !previous.IsRetired;
        }
    }
}
