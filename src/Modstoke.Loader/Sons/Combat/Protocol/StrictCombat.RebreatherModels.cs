// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    public enum StrictCombatRebreatherRefillMode { None, Inventory, Worn }

    /// <summary>Immutable server-owned rebreather resources.</summary>
    public sealed class StrictCombatOwnedRebreatherState
    {
        public static StrictCombatOwnedRebreatherState Unprovisioned { get; } =
            new(false, 0, 0, 0, 0, StrictCombatRebreatherRefillMode.None, 0, 0, false, false);

        private StrictCombatOwnedRebreatherState(bool isProvisioned, ulong revision, double capacity,
            double air, int canisters, StrictCombatRebreatherRefillMode mode, double remaining, ulong lastSequence,
            bool hasWearState, bool isWorn)
        {
            IsProvisioned = isProvisioned;
            HasWearState = hasWearState;
            IsWorn = isWorn;
            Revision = revision;
            Capacity = capacity;
            Air = air == 0 ? 0 : air;
            Canisters = canisters;
            RefillMode = mode;
            RemainingRefillAir = remaining == 0 ? 0 : remaining;
            LastRefillSequence = lastSequence;
        }

        public bool IsProvisioned { get; }
        public bool HasWearState { get; }
        public bool IsWorn { get; }
        public ulong Revision { get; }
        public ulong LastRefillSequence { get; }
        public double Capacity { get; }
        public double Air { get; }
        public double RemainingRefillAir { get; }
        public int Canisters { get; }
        public StrictCombatRebreatherRefillMode RefillMode { get; }

        public static bool TryCreate(ulong revision, double capacity, double air, int canisters,
            StrictCombatRebreatherRefillMode mode, double remaining, ulong lastSequence,
            out StrictCombatOwnedRebreatherState state)
            => TryCreate(revision, capacity, air, canisters, mode, remaining, lastSequence,
                false, false, out state);

        public static bool TryCreate(ulong revision, double capacity, double air, int canisters,
            StrictCombatRebreatherRefillMode mode, double remaining, ulong lastSequence, bool isWorn,
            out StrictCombatOwnedRebreatherState state)
            => TryCreate(revision, capacity, air, canisters, mode, remaining, lastSequence,
                true, isWorn, out state);

        private static bool TryCreate(ulong revision, double capacity, double air, int canisters,
            StrictCombatRebreatherRefillMode mode, double remaining, ulong lastSequence,
            bool hasWearState, bool isWorn, out StrictCombatOwnedRebreatherState state)
        {
            state = null;
            if (!double.IsFinite(capacity) || capacity <= 0 || !double.IsFinite(air)
                || air < 0 || air > capacity || canisters < 0
                || !double.IsFinite(remaining) || remaining < 0
                || mode < StrictCombatRebreatherRefillMode.None || mode > StrictCombatRebreatherRefillMode.Worn
                || (mode == StrictCombatRebreatherRefillMode.None ? remaining != 0
                    : remaining <= 0 || air >= capacity || lastSequence == 0)) return false;
            state = new StrictCombatOwnedRebreatherState(true, revision, capacity, air, canisters,
                mode, remaining, lastSequence, hasWearState, isWorn);
            return true;
        }

        public bool SameContents(StrictCombatOwnedRebreatherState other)
            => other != null && IsProvisioned == other.IsProvisioned && Revision == other.Revision
                && LastRefillSequence == other.LastRefillSequence && Canisters == other.Canisters
                && HasWearState == other.HasWearState && IsWorn == other.IsWorn
                && RefillMode == other.RefillMode
                && BitConverter.DoubleToInt64Bits(Capacity) == BitConverter.DoubleToInt64Bits(other.Capacity)
                && BitConverter.DoubleToInt64Bits(Air) == BitConverter.DoubleToInt64Bits(other.Air)
                && BitConverter.DoubleToInt64Bits(RemainingRefillAir) == BitConverter.DoubleToInt64Bits(other.RemainingRefillAir);
    }
}
