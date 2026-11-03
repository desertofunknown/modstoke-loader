// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    public sealed partial class StrictCombatClient
    {
        private StrictCombatRebreatherBarrier _nativeRebreatherBarrier;

        /// <summary>Prepares rebreather interception from the menu before world/network startup.</summary>
        public static StrictCombatRebreatherStatus PrepareNativeRebreatherBarrierForNextStart()
            => StrictCombatRebreatherHooks.PrepareForNextStart();

        /// <summary>
        /// Retains native mutation suppression for an existing idle, equipped rebreather.
        /// Requires an owned local life and preparation before the first native refill or
        /// consumption. Does not grant air, deliver server air, or enable oxygen enforcement.
        /// Suppression remains after retirement. A new life requires the explicit handoff;
        /// reacquisition cannot transfer an earlier life or adopt replacement equipment.
        /// </summary>
        public bool TryAcquireNativeRebreatherBarrier(out StrictCombatRebreatherStatus status)
        {
            status = StrictCombatRebreatherStatus.WrongThread;
            if (Environment.CurrentManagedThreadId != _ownerThreadId) return false;
            status = StrictCombatRebreatherStatus.UnsupportedContext;
            if (_readingWeaponInput || _projectingHealth || _projectingLife || _placingRespawn
                || _presentingWeapon || _projectingArmour || _projectingAmmunition || _updatingEquipment) return false;
            // Reuse the equipment scope so every existing projection/input gate also
            // rejects reentrant acquisition while native object identities are captured.
            _updatingEquipment = true;
            try
            {
                var life = _nativeLifeBarrier;
                if (life == null || life.IsRetired || !ReferenceEquals(life.Owner, this)
                    || !ReferenceEquals(life.Player, _localPlayer)
                    || !TryRead(out var binding, out var state)
                    || state.LifeGeneration != life.LifeGeneration
                    || !StrictCombatLocalRebreather.TryCapture(this, life, in binding, in state, out var capture))
                    return false;
                if (!StrictCombatRebreatherHooks.TryAcquire(capture, out var barrier, out status)) return false;
                if (!ReferenceEquals(barrier, _nativeRebreatherBarrier))
                {
                    _nativeRebreatherBarrier?.Retire();
                    _nativeRebreatherBarrier = barrier;
                }
                return true;
            }
            catch (Exception) { status = StrictCombatRebreatherStatus.Failed; return false; }
            finally { _updatingEquipment = false; }
        }
    }
}
