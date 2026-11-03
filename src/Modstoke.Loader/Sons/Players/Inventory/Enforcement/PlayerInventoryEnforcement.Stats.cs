// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using TheForest.Utils;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        private PlayerStatProjection _statProjection;

        internal bool TryReadStatProjectionActor(PlayerBridge bridge, out PlayerStats stats)
        {
            stats = null;
            if (!OnThread || IsDisposed || IsFaulted || _authority != null || !ReferenceEquals(bridge, _bridge)
                || !Current() || !SourceLifetime()) return false;
            var candidate = LocalPlayer.Stats;
            if (candidate == null || candidate.WasCollected || !candidate || candidate.enabled
                || candidate._vitals == null || candidate._vitals.WasCollected || !candidate._vitals
                || candidate._vitals.enabled || !candidate.transform.IsChildOf(LocalPlayer.GameObject.transform)) return false;
            stats = candidate;
            return true;
        }

        internal bool TryBindStatProjection(PlayerBridge bridge, PlayerStatProjection projection)
        {
            if (_statProjection != null || projection == null || projection.IsDisposed || _busy || _writerDepth != 0
                || bridge.IsGameplayActive || !TryReadStatProjectionActor(bridge, out _)) return false;
            _statProjection = projection;
            return true;
        }

        internal bool StatProjectionCanApply => OnThread && !IsDisposed && !IsFaulted && IsArmed
            && _authority == null && !_busy && _writerDepth == 0 && !InventoryFrozen && !HasPendingArmourSourceCustody
            && !HasPendingSwimmingStow && !_requestingIntake && (!_intakePending || _presentationStarted);

        internal void FailStatProjection(PlayerStatProjection projection, string detail)
        {
            if (OnThread && ReferenceEquals(projection, _statProjection) && !IsDisposed)
                Fail("Authoritative stat projection failed: " + detail);
        }

        private bool ProjectConsumedStats(in PlayerConsumptionFrame frame)
            => _statProjection != null && !_statProjection.IsDisposed
                && _statProjection.TryProjectIntake(frame);

        private bool ConsumedStatsCurrent()
            => _statProjection != null && !_statProjection.IsDisposed
                && _statProjection.SourceNumbersCurrent()
                && IntakeSourceCurrent(_statProjection.ExpectedIntake);
    }
}
