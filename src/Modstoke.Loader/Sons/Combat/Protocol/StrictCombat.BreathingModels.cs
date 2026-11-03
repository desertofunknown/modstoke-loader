// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    /// <summary>Immutable server-owned breathing resources.</summary>
    public sealed class StrictCombatOwnedBreathingState
    {
        public static StrictCombatOwnedBreathingState Unprovisioned { get; } =
            new(false, 0, 0, 0);

        private StrictCombatOwnedBreathingState(bool isProvisioned, ulong revision, double capacity,
            double air)
        {
            IsProvisioned = isProvisioned;
            Revision = revision;
            Capacity = capacity;
            Air = air == 0 ? 0 : air;
        }

        public bool IsProvisioned { get; }
        public ulong Revision { get; }
        public double Capacity { get; }
        public double Air { get; }

        public static bool TryCreate(ulong revision, double capacity, double air,
            out StrictCombatOwnedBreathingState state)
        {
            state = null;
            if (!double.IsFinite(capacity) || capacity < float.Epsilon || capacity > float.MaxValue
                || !double.IsFinite(air) || air < 0 || air > capacity) return false;
            state = new StrictCombatOwnedBreathingState(true, revision, capacity, air);
            return true;
        }

        public bool SameContents(StrictCombatOwnedBreathingState other)
            => other != null && IsProvisioned == other.IsProvisioned && Revision == other.Revision
                && BitConverter.DoubleToInt64Bits(Capacity) == BitConverter.DoubleToInt64Bits(other.Capacity)
                && BitConverter.DoubleToInt64Bits(Air) == BitConverter.DoubleToInt64Bits(other.Air);
    }
}
