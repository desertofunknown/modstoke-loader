// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons
{
    public sealed partial class PlayerHeldOnlyAuthority
    {
        internal bool MatchesSettledFingerprint(string fingerprint)
            => Current() && _pending == null && RepresentativeStateCurrent()
                && MakeSnapshot(_state, _instance).Fingerprint == fingerprint;

        internal bool CanRouteDropModules => Current() && RepresentativeStateCurrent()
            && _representative != null && !_representative.WasCollected
            && _representative._modules != null && !_representative._modules.WasCollected
            && _representative._modules.Count == 0;

        // Drops do not call GainStrength. Do not replay the peer's numeric observation.
        // The live server stat owner must still be the restored actor at publication.
        internal bool TryPublishDropEquipment(Mutation mutation, NativeReceipt receipt,
            PlayerCarrier carrier, out string detail)
        {
            detail = "Held drop publication requires exact native custody and the actual restored server actor.";
            if (!Current() || !Owns(mutation) || !ReferenceEquals(mutation.Receipt, receipt)
                || mutation.Kind is not (MutationKind.PutDown or MutationKind.DropAll) && !IsForcedDrop(mutation.Kind) || !mutation.Drop
                || receipt.StrengthGain != 0 || carrier == null || !carrier.TryReadRestoredServerStats(out var stats)
                || !Alive(stats) || stats._playerIsDowned || stats._dyingState != PlayerStats.DyingState.Alive
                || !_bridge.TryReadCurrentTarget(out var target) || carrier.Entity?.Pointer != target.Pointer) return false;
            try
            {
                var state = target.GetState<IPlayerState>();
                if (state == null || state.WasCollected || state.HeldOnlyItemId != mutation.Before.ItemId
                    || state.HeldOnlyItemCount < 0 || state.HeldOnlyItemCount > mutation.Before.Count) return false;
                state.HeldOnlyItemId = mutation.After.ItemId;
                state.HeldOnlyItemCount = receipt.BoltRendererCount;
                if (state.HeldOnlyItemId != mutation.After.ItemId || state.HeldOnlyItemCount != receipt.BoltRendererCount
                    || !Current() || !carrier.TryReadRestoredServerStats(out var current) || current.Pointer != stats.Pointer)
                    throw new InvalidOperationException("Held renderer publication lost its exact native state.");
                detail = "Native drop's zero-strength stat path and actual server held renderer publication confirmed.";
                return true;
            }
            catch (Exception error) { HoldUnconfirmed(mutation, error.Message); detail = FailureReason; return false; }
        }
    }
}
