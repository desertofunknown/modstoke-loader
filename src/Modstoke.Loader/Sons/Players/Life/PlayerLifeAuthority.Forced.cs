// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Settings;
using Held = Modstoke.Sons.PlayerHeldOnlyAuthority;

namespace Modstoke.Sons
{
    public sealed partial class PlayerLifeAuthority
    {
        internal PlayerInventoryEnforcement ForcedInventory => _inventory.MutationEnforcement;
        internal bool HasPendingDowningDrop => _sourceRoute?.HasPendingDowningDrop == true;

        internal bool TrySettleDowningDrop(out string detail)
            => TrySettleForcedHeldDrop(Held.MutationKind.KnockDownDrop, checked((uint)(_transitionSequence + 1)), out detail);

        // Water owns its ordered edge ordinal and actual state write. The same
        // prelude is reusable by a future retained combat owner before lethal numbers.
        internal bool TrySettleForcedHeldDrop(Held.MutationKind kind, ulong transition, out string detail)
        {
            detail = FailureReason ?? "The current alive life owner and exact forced-drop route are required.";
            if (IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread
                || Phase != PlayerLifePhase.Alive || _sourceRoute == null || _deathTransaction != 0
                || HasPendingSourceAction || !Held.IsForcedDrop(kind)
                || kind == Held.MutationKind.KnockDownDrop && transition != _transitionSequence + 1) return false;
            RequireCurrent();
            if (kind == Held.MutationKind.KnockDownDrop && HasPendingDowningDrop && Cheats.GodModeEnabled)
                return Fault("GodMode changed after the lethal transition was accepted; retain its damage and forced-drop work.", out detail);
            if (_vitals._hasDied || _stats._playerIsDowned || _stats._dyingState != PlayerStats.DyingState.Alive)
                return Fault("Forced drop was requested after an unowned native life latch.", out detail);
            return _sourceRoute.TrySettleForcedDrop(kind, transition, out detail);
        }

        internal bool TryReleaseSwimmingDrop(ulong transition, out string detail)
        {
            detail = "The water owner must commit its exact settled swimming transition before releasing it.";
            return _sourceRoute != null && _sourceRoute.TryReleaseForcedDrop(Held.MutationKind.SwimmingDrop, transition, out detail);
        }
    }
}
