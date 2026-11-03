// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using TheForest.Utils;

namespace Modstoke.Sons.Combat
{
    public enum StrictCombatBreathingStatus
    {
        Success, Unavailable, WrongThread, AlreadyInWorld, NativeActionObserved,
        UnsupportedContext, AlreadyOwned, Failed, Busy
    }

    public enum StrictCombatBreathingProjectionStatus
    {
        Unavailable, Applied, AlreadyCurrent, WrongThread, BarrierRequired,
        UnsupportedContext, Busy, MutationUncertain
    }

    internal sealed class StrictCombatBreathingBarrier
    {
        internal StrictCombatBreathingBarrier(StrictCombatLifeBarrier life,
            in StrictCombatBinding binding, BreathingData breathing, float capacity,
            bool allowsCompletedNativeActivity)
        {
            Life = life;
            Binding = binding;
            Breathing = breathing;
            StatsPointer = life.Stats.Pointer;
            VitalsPointer = life.Vitals.Pointer;
            BreathingPointer = breathing.Pointer;
            LifeGeneration = life.LifeGeneration;
            Capacity = capacity;
            AllowsCompletedNativeActivity = allowsCompletedNativeActivity;
        }

        internal StrictCombatLifeBarrier Life { get; }
        internal StrictCombatClient Owner => Life.Owner;
        internal StrictCombatBinding Binding { get; }
        internal BreathingData Breathing { get; }
        internal nint StatsPointer { get; }
        internal nint VitalsPointer { get; }
        internal nint BreathingPointer { get; }
        internal ulong LifeGeneration { get; }
        internal float Capacity { get; }
        internal bool AllowsCompletedNativeActivity { get; }
        private int _retired;
        internal bool IsRetired => Volatile.Read(ref _retired) != 0;
        internal void Retire() => Interlocked.Exchange(ref _retired, 1);

        internal bool NativeObjectsCurrent()
        {
            if (IsRetired || !Owner.IsCurrentNativeHealthOwner(Life)
                || Life.LifeGeneration != LifeGeneration || LifeGeneration == 0) return false;
            var stats = Life.Stats;
            var vitals = Life.Vitals;
            var localStats = LocalPlayer.Stats;
            if (stats == null || stats.WasCollected || !stats
                || localStats == null || localStats.WasCollected || !localStats
                || stats.Pointer != StatsPointer || localStats.Pointer != StatsPointer
                || vitals == null || vitals.WasCollected || vitals.Pointer != VitalsPointer
                || Breathing == null || Breathing.WasCollected || Breathing.Pointer != BreathingPointer)
                return false;
            var statsVitals = stats._vitals;
            var current = vitals.LungBreathing;
            return statsVitals != null && !statsVitals.WasCollected && statsVitals.Pointer == VitalsPointer
                && current != null && !current.WasCollected && current.Pointer == BreathingPointer
                && stats._dyingState == PlayerStats.DyingState.Alive && !stats._playerIsDowned
                && !stats._respawning && !vitals._hasDied;
        }

        internal bool SameObjects(StrictCombatBreathingBarrier other)
            => other != null && ReferenceEquals(Life, other.Life)
                && StatsPointer == other.StatsPointer && VitalsPointer == other.VitalsPointer
                && BreathingPointer == other.BreathingPointer;

        internal static float Represent(double value)
        {
            float result = (float)value;
            return (double)result > value ? MathF.BitDecrement(result) : result;
        }

        internal StrictCombatBreathingProjectionStatus Project(in StrictCombatBinding binding,
            in StrictCombatSnapshot state, StrictCombatOwnedBreathingState air, int frame)
        {
            bool wrote = false;
            try
            {
                if (!Owner.IsCurrentBreathingOperation(this, in binding, in state, air, frame))
                    return StrictCombatBreathingProjectionStatus.Unavailable;
                float current = Math.Min(Represent(air.Air), Capacity);
                bool matches = FieldsMatch(current);
                if (!Owner.IsCurrentBreathingOperation(this, in binding, in state, air, frame))
                    return StrictCombatBreathingProjectionStatus.Unavailable;
                if (matches) return StrictCombatBreathingProjectionStatus.AlreadyCurrent;

                // No native resource mutator or temporary setter exemption is needed.
                // The capacity getter uses this owner's accepted capacity, so global
                // difficulty settings cannot rescale the server's lung account.
                wrote = true;
                Breathing.MaxLungAirCapacity = Capacity;
                if (!Owner.IsCurrentBreathingOperation(this, in binding, in state, air, frame))
                    return Uncertain();
                Breathing.CurrentLungAir = current;
                matches = FieldsMatch(current);
                if (!Owner.IsCurrentBreathingOperation(this, in binding, in state, air, frame) || !matches)
                    return Uncertain();
                return StrictCombatBreathingProjectionStatus.Applied;
            }
            catch (Exception)
            {
                return wrote ? Uncertain() : StrictCombatBreathingProjectionStatus.UnsupportedContext;
            }
        }

        private bool FieldsMatch(float current)
        {
            float maximum = Breathing.MaxLungAirCapacity;
            float remaining = Breathing.CurrentLungAir;
            float effectiveMaximum = Breathing.MaxLungAirCapacityFinal;
            int capacityBits = BitConverter.SingleToInt32Bits(Capacity);
            return BitConverter.SingleToInt32Bits(maximum) == capacityBits
                && BitConverter.SingleToInt32Bits(effectiveMaximum) == capacityBits
                && BitConverter.SingleToInt32Bits(remaining) == BitConverter.SingleToInt32Bits(current);
        }

        private StrictCombatBreathingProjectionStatus Uncertain()
        {
            Retire();
            Life.Retire();
            return StrictCombatBreathingProjectionStatus.MutationUncertain;
        }
    }
}
