// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Sons
{
    internal readonly record struct PlayerWaterTransitionKey(Guid Migration, ulong Generation,
        ulong Actor, ulong LifeGeneration, ulong Ordinal)
    {
        internal bool IsValid => Migration != Guid.Empty && Generation != 0 && Actor != 0
            && LifeGeneration != 0 && Ordinal != 0;

        internal bool Matches(PlayerBridge bridge) => bridge != null
            && Migration == bridge.Plan.Id && Generation == bridge.Plan.Generation && Actor == bridge.NetworkId;
    }

    // Accepted geometry is evidence for this edge, never a later force-step sample.
    internal sealed record PlayerWaterTransition(PlayerWaterTransitionKey Key,
        PlayerWaterContactSample Sample, Vector3 Position,
        bool Grounded, bool Jumping, double FixedTime);

    internal enum PlayerWaterTransitionMessage : byte { Committed = 1, Applied = 2 }
}
