// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    public sealed partial class StrictCombatClient
    {
        private StrictCombatArmourBarrier _nativeArmourBarrier;
        private bool _acquiringArmour;

        /// <summary>Prepares known native armour mutation hooks before world/network startup.</summary>
        public static StrictCombatArmourStatus PrepareNativeArmourBarrierForNextStart()
            => StrictCombatArmourHooks.PrepareForNextStart();

        /// <summary>
        /// Suppresses known native armour writes for an exact resource binding and owned life.
        /// The binding must originate in an Alive state; acquisition can finish after that
        /// same life becomes Downed or Dead if the retained native pieces remain current.
        /// Does not project durability, break pieces, select enrollment or enable strict combat.
        /// Retirement keeps suppression; this API does not hand ownership to a later life.
        /// </summary>
        public bool TryAcquireNativeArmourBarrier(StrictCombatLocalArmourBinding capture,
            out StrictCombatArmourStatus status)
        {
            status = StrictCombatArmourStatus.WrongThread;
            if (Environment.CurrentManagedThreadId != _ownerThreadId) return false;
            status = StrictCombatArmourStatus.Busy;
            if (_acquiringArmour || _readingArmour || _projectingHealth || _projectingLife
                || _placingRespawn || _presentingWeapon || _projectingArmour || _projectingAmmunition || _updatingEquipment)
                return false;
            _acquiringArmour = true;
            try
            {
                var life = _nativeLifeBarrier;
                status = StrictCombatArmourStatus.LifeBarrierRequired;
                if (life == null || life.IsRetired || !ReferenceEquals(life.Owner, this)
                    || !ReferenceEquals(life.Player, _localPlayer)) return false;
                if (!StrictCombatArmourHooks.TryAcquire(this, capture, life, out var barrier, out status))
                    return false;
                if (!ReferenceEquals(barrier, _nativeArmourBarrier))
                {
                    _nativeArmourBarrier?.Retire();
                    _nativeArmourBarrier = barrier;
                }
                return true;
            }
            catch (Exception)
            {
                _nativeArmourBarrier?.Retire();
                status = StrictCombatArmourStatus.Failed;
                return false;
            }
            finally { _acquiringArmour = false; }
        }

        internal bool IsCurrentArmourBarrierContext(StrictCombatLocalArmourBinding capture,
            StrictCombatLifeBarrier life)
        {
            return _acquiringArmour && capture != null && life != null && !life.IsRetired
                && ReferenceEquals(capture, _localArmourBinding) && !capture.IsRetired
                && ReferenceEquals(life, _nativeLifeBarrier) && ReferenceEquals(life.Owner, this)
                && ReferenceEquals(life.Player, _localPlayer)
                && ReferenceEquals(life.Player, capture.Original.Player.Player)
                && capture.Original.ServerState.LifeStage == StrictCombatLifeStage.Alive
                && TryRead(out var binding, out var state)
                && state.LifeStage is (StrictCombatLifeStage.Alive or StrictCombatLifeStage.Downed or StrictCombatLifeStage.Dead)
                && state.LifeGeneration == capture.LifeGeneration && state.LifeGeneration == life.LifeGeneration
                && binding.SameBinding(capture.Binding) && IsCurrentNativeHealthOwner(life)
                && IsCurrentLocalArmourBinding(capture, out _);
        }

        private void ReleaseNativeArmourBarrier()
        {
            _nativeArmourBarrier?.Retire();
            _nativeArmourBarrier = null;
        }
    }
}
