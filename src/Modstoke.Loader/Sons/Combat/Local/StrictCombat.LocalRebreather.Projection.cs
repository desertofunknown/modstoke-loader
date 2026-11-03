// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    internal sealed partial class StrictCombatLocalRebreather
    {
        internal StrictCombatRebreatherProjectionStatus ProjectAir(StrictCombatClient owner,
            StrictCombatRebreatherBarrier barrier, in StrictCombatBinding binding,
            in StrictCombatSnapshot state, StrictCombatOwnedRebreatherState air, int frame)
        {
            return Project(owner, barrier, in binding, in state, air, frame, false);
        }

        internal StrictCombatRebreatherProjectionStatus ProjectActivation(StrictCombatClient owner,
            StrictCombatRebreatherBarrier barrier, in StrictCombatBinding binding,
            in StrictCombatSnapshot state, StrictCombatOwnedRebreatherState air, int frame)
        {
            return Project(owner, barrier, in binding, in state, air, frame, true);
        }

        private StrictCombatRebreatherProjectionStatus Project(StrictCombatClient owner,
            StrictCombatRebreatherBarrier barrier, in StrictCombatBinding binding,
            in StrictCombatSnapshot state, StrictCombatOwnedRebreatherState air, int frame, bool includeActivation)
        {
            bool mutationStarted = false;
            try
            {
                if (!owner.IsCurrentRebreatherProjection(this, barrier, in binding, in state, air, frame, includeActivation))
                    return StrictCombatRebreatherProjectionStatus.Unavailable;
                if (!air.IsProvisioned || (includeActivation && !air.HasWearState)
                    || !double.IsFinite(air.Capacity) || air.Capacity <= 0
                    || air.Capacity > float.MaxValue || !double.IsFinite(air.Air)
                    || air.Air < 0 || air.Air > air.Capacity)
                    return StrictCombatRebreatherProjectionStatus.UnsupportedContext;

                float capacity = (float)air.Capacity;
                if ((double)capacity > air.Capacity) capacity = MathF.BitDecrement(capacity);
                if (capacity <= 0) return StrictCombatRebreatherProjectionStatus.UnsupportedContext;
                float current = (float)air.Air;
                if ((double)current > air.Air) current = MathF.BitDecrement(current);
                current = Math.Min(current, capacity);

                if (!owner.IsCurrentRebreatherProjection(this, barrier, in binding, in state, air, frame, includeActivation))
                    return StrictCombatRebreatherProjectionStatus.Unavailable;
                bool matches = AirFieldsMatch(capacity, current, includeActivation, air.IsWorn);
                if (!owner.IsCurrentRebreatherProjection(this, barrier, in binding, in state, air, frame, includeActivation))
                    return StrictCombatRebreatherProjectionStatus.Unavailable;
                if (matches) return StrictCombatRebreatherProjectionStatus.AlreadyCurrent;

                // Project accepted air directly. Native values never grant resources,
                // and each write must still belong to the same captured life.
                mutationStarted = true;
                if (includeActivation && !air.IsWorn)
                {
                    Breathing.RebreatherIsActive = false;
                    if (!owner.IsCurrentRebreatherProjection(this, barrier, in binding, in state, air, frame, includeActivation))
                        return UncertainAir(barrier);
                }
                Module._MaxVolume_k__BackingField = capacity;
                if (!owner.IsCurrentRebreatherProjection(this, barrier, in binding, in state, air, frame, includeActivation))
                    return UncertainAir(barrier);
                Module._CurrentVolume_k__BackingField = current;
                if (!owner.IsCurrentRebreatherProjection(this, barrier, in binding, in state, air, frame, includeActivation))
                    return UncertainAir(barrier);
                Breathing.MaxRebreatherAirCapacity = capacity;
                if (!owner.IsCurrentRebreatherProjection(this, barrier, in binding, in state, air, frame, includeActivation))
                    return UncertainAir(barrier);
                Breathing.CurrentRebreatherAir = current;
                if (!owner.IsCurrentRebreatherProjection(this, barrier, in binding, in state, air, frame, includeActivation))
                    return UncertainAir(barrier);

                if (includeActivation && air.IsWorn)
                {
                    // Verify the complete air projection before enabling native breathing.
                    matches = AirFieldsMatch(capacity, current, false, false);
                    if (!owner.IsCurrentRebreatherProjection(this, barrier, in binding, in state, air, frame, includeActivation)
                        || !matches) return UncertainAir(barrier);
                    Breathing.RebreatherIsActive = true;
                    if (!owner.IsCurrentRebreatherProjection(this, barrier, in binding, in state, air, frame, includeActivation))
                        return UncertainAir(barrier);
                }

                matches = AirFieldsMatch(capacity, current, includeActivation, air.IsWorn);
                if (!owner.IsCurrentRebreatherProjection(this, barrier, in binding, in state, air, frame, includeActivation)
                    || !matches) return UncertainAir(barrier);
                return StrictCombatRebreatherProjectionStatus.Applied;
            }
            catch (Exception)
            {
                return mutationStarted ? UncertainAir(barrier)
                    : StrictCombatRebreatherProjectionStatus.UnsupportedContext;
            }
        }

        private bool AirFieldsMatch(float capacity, float current, bool includeActivation, bool isWorn)
        {
            // Read all four fields before comparing their exact projected bits.
            float moduleCapacity = Module._MaxVolume_k__BackingField;
            float moduleAir = Module._CurrentVolume_k__BackingField;
            float breathingCapacity = Breathing.MaxRebreatherAirCapacity;
            float breathingAir = Breathing.CurrentRebreatherAir;
            bool activationMatches = !includeActivation || Breathing.RebreatherIsActive == isWorn;
            int capacityBits = BitConverter.SingleToInt32Bits(capacity);
            int airBits = BitConverter.SingleToInt32Bits(current);
            return BitConverter.SingleToInt32Bits(moduleCapacity) == capacityBits
                && BitConverter.SingleToInt32Bits(moduleAir) == airBits
                && BitConverter.SingleToInt32Bits(breathingCapacity) == capacityBits
                && BitConverter.SingleToInt32Bits(breathingAir) == airBits
                && activationMatches;
        }

        private StrictCombatRebreatherProjectionStatus UncertainAir(StrictCombatRebreatherBarrier barrier)
        {
            // A changed owner cannot safely receive rollback or retry writes.
            barrier.Retire();
            Life.Retire();
            return StrictCombatRebreatherProjectionStatus.MutationUncertain;
        }
    }
}
