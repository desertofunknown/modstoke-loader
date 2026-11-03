// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    internal sealed partial class StrictCombatLocalWeapon
    {
        internal StrictCombatAmmoProjectionStatus ProjectAmmunition(StrictCombatClient owner,
            StrictCombatWeaponBarrier barrier, in StrictCombatBinding binding,
            in StrictCombatSnapshot state, int frame)
        {
            bool mutationStarted = false;
            try
            {
                if (!owner.IsCurrentAmmoProjection(this, barrier, in binding, in state, frame))
                    return StrictCombatAmmoProjectionStatus.Unavailable;
                if (state.Magazine < 0 || state.Magazine > 12 || !Live(_ammo)
                    || _ammo._type != 362 || !_ammo._usesInternalAmmoStorage || _ammo._maxCount != 12)
                    return StrictCombatAmmoProjectionStatus.UnsupportedContext;

                // The native count is only a projection comparison, never ammunition authority.
                if (_ammo._currentCount == state.Magazine)
                    return owner.IsCurrentAmmoProjection(this, barrier, in binding, in state, frame)
                        ? StrictCombatAmmoProjectionStatus.AlreadyCurrent
                        : StrictCombatAmmoProjectionStatus.Unavailable;

                if (!owner.IsCurrentAmmoProjection(this, barrier, in binding, in state, frame))
                    return StrictCombatAmmoProjectionStatus.Unavailable;
                // Write only the captured count field; native ammo mutators remain suppressed.
                mutationStarted = true;
                _ammo._currentCount = state.Magazine;
                if (!owner.IsCurrentAmmoProjection(this, barrier, in binding, in state, frame))
                    return UncertainAmmunition(barrier);
                if (_ammo._currentCount != state.Magazine
                    || !owner.IsCurrentAmmoProjection(this, barrier, in binding, in state, frame))
                    return UncertainAmmunition(barrier);
                return StrictCombatAmmoProjectionStatus.Applied;
            }
            catch (Exception)
            {
                return mutationStarted ? UncertainAmmunition(barrier)
                    : StrictCombatAmmoProjectionStatus.UnsupportedContext;
            }
        }

        private StrictCombatAmmoProjectionStatus UncertainAmmunition(StrictCombatWeaponBarrier barrier)
        {
            // A possibly changed weapon or life cannot safely receive rollback or retry writes.
            barrier.Retire();
            Life.Retire();
            return StrictCombatAmmoProjectionStatus.MutationUncertain;
        }
    }
}
