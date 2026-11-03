// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

namespace Modstoke.Sons.Combat
{
    /// <summary>Negotiated refill and wear requests that permit intervening timed resource progress.</summary>
    public static class StrictCombatRebreatherProgressProtocol
    {
        public const byte Version = 2;
        public const ulong Capability = 1UL << 11;
        public const ulong RequiredCapabilities = StrictCombatRebreatherRefillProtocol.RequiredCapabilities
            | StrictCombatRebreatherWearProtocol.RequiredCapabilities | Capability;
    }
}
