// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    public enum StrictCombatHealthProjectionStatus
    {
        Unavailable, Applied, AlreadyCurrent, WrongThread, BarrierRequired, LifeChanged,
        NativeStateUnavailable, Busy, MutationUncertain
    }

    internal sealed partial class StrictCombatLocalPlayer
    {
        internal StrictCombatHealthProjectionStatus ProjectHealth(StrictCombatClient owner,
            StrictCombatLifeBarrier barrier, in StrictCombatBinding binding,
            in StrictCombatSnapshot state, int frame)
        {
            bool mutationStarted = false;
            try
            {
                if (!owner.IsCurrentHealthProjection(this, barrier, in binding, in state, frame))
                    return StrictCombatHealthProjectionStatus.Unavailable;

                // Only the native lower bound constrains representation. Damaged or nonfinite
                // current/target values may be replaced; they are not input to server health.
                float minimum = _health._min;
                if (!float.IsFinite(minimum) || minimum > state.Health || minimum > state.MaximumHealth)
                    return StrictCombatHealthProjectionStatus.NativeStateUnavailable;
                if (HealthMatches(in state))
                    return owner.IsCurrentHealthProjection(this, barrier, in binding, in state, frame)
                        ? StrictCombatHealthProjectionStatus.AlreadyCurrent
                        : StrictCombatHealthProjectionStatus.Unavailable;

                if (!owner.IsCurrentHealthProjection(this, barrier, in binding, in state, frame))
                    return StrictCombatHealthProjectionStatus.Unavailable;
                // Native health mutators are suppressed for this capture. Only this writer
                // applies accepted values; it does not open a reentrant setter bypass.
                mutationStarted = true;
                if (!SameBits(_health._max, state.MaximumHealth))
                    _health._max = state.MaximumHealth;
                if (!owner.IsCurrentHealthProjection(this, barrier, in binding, in state, frame))
                    return Uncertain(barrier);
                if (!SameBits(_health._currentValue, state.Health))
                    _health._currentValue = state.Health;
                if (!owner.IsCurrentHealthProjection(this, barrier, in binding, in state, frame))
                    return Uncertain(barrier);

                // Vitals.SetHealth clamps against local sickness. The accepted server value
                // already determines health here; preserve sickness rather than clearing it.
                _vitals._targetHealth = state.Health;
                if (!owner.IsCurrentHealthProjection(this, barrier, in binding, in state, frame)
                    || !HealthMatches(in state))
                    return Uncertain(barrier);
                return StrictCombatHealthProjectionStatus.Applied;
            }
            catch (Exception)
            {
                return mutationStarted ? Uncertain(barrier) : StrictCombatHealthProjectionStatus.NativeStateUnavailable;
            }
        }

        private bool HealthMatches(in StrictCombatSnapshot state)
            => SameBits(_health._max, state.MaximumHealth)
                && SameBits(_health._currentValue, state.Health)
                && SameBits(_vitals._targetHealth, state.Health);

        private static bool SameBits(float first, float second)
            => BitConverter.SingleToInt32Bits(first) == BitConverter.SingleToInt32Bits(second);

        private static StrictCombatHealthProjectionStatus Uncertain(StrictCombatLifeBarrier barrier)
        {
            // Never roll back into a player or life that may have changed during a native call.
            barrier.Retire();
            return StrictCombatHealthProjectionStatus.MutationUncertain;
        }
    }
}
