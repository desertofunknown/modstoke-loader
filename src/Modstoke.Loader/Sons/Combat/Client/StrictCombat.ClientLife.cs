// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    public enum StrictCombatLocalLifeStatus
    {
        Unavailable, Pending, Presented, AlreadyCurrent, WrongThread, BarrierRequired,
        LifeChanged, UnsupportedContext, RespawnRequired, Busy, MutationUncertain
    }

    public sealed partial class StrictCombatClient
    {
        private StrictCombatLocalLife _localLife;
        private bool _projectingLife;

        /// <summary>
        /// Advances the captured player's ordinary downed/get-up presentation from accepted
        /// server state. Call on the game thread each frame. Does not request revival, respawn,
        /// restore weapons, select a mode or enable combat. Presented is local state, not a
        /// server acknowledgement or evidence of an honest client.
        /// </summary>
        public StrictCombatLocalLifeStatus UpdateLocalLife()
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return StrictCombatLocalLifeStatus.WrongThread;
            if (_projectingLife || _projectingHealth || _placingRespawn || _presentingWeapon || _projectingArmour || _projectingAmmunition || _updatingEquipment) return StrictCombatLocalLifeStatus.Busy;
            _projectingLife = true;
            try
            {
                if (!TryRead(out var binding, out var state)) return StrictCombatLocalLifeStatus.Unavailable;
                var barrier = _nativeLifeBarrier;
                if (barrier == null || barrier.IsRetired || !ReferenceEquals(barrier.Player, _localPlayer))
                    return StrictCombatLocalLifeStatus.BarrierRequired;
                if (state.LifeGeneration == 0 || state.LifeGeneration != barrier.LifeGeneration)
                    return StrictCombatLocalLifeStatus.LifeChanged;
                var equipment = PrepareEquipmentLifeTransition(in state);
                if (equipment.HasValue) return equipment.Value;
                if (_localLife == null || !_localLife.OwnedBy(barrier))
                {
                    if (!StrictCombatLocalLife.TryCapture(barrier, out _localLife))
                        return StrictCombatLocalLifeStatus.UnsupportedContext;
                }
                return _localLife.Advance(in binding, in state);
            }
            catch (Exception)
            {
                _nativeLifeBarrier?.Retire();
                return StrictCombatLocalLifeStatus.Unavailable;
            }
            finally { _projectingLife = false; }
        }
    }
}
