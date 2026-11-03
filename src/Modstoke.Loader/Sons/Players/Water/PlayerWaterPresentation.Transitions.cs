// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;
using UnityEngine;
using Held = Modstoke.Sons.PlayerHeldOnlyAuthority;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerWaterPresentation
    {
        private PlayerGroundMovement _swimmingMotor;
        private PlayerLifeAuthority _swimmingLife;
        private PlayerLifeRoute _swimmingRoute;
        private PlayerWaterTransitionKey _swimmingCommit, _swimmingApplied;
        private bool _swimmingAckDue, _swimmingNativeAttempted, _swimmingNativeApplied, _swimmingSourceReleased;
        private long _swimmingSentAt;

        internal bool HasPendingSwimmingTransition => _swimmingCommit.IsValid
            && (_bridge.IsServer ? _swimmingCommit != _swimmingApplied
                : !_swimmingSourceReleased || _swimmingAckDue || _swimmingRoute?.HasPendingForcedTransition == true);
        internal bool SwimmingNativeCallbackActive { get; private set; }
        internal bool MatchesSourceActionOwner(PlayerBridge bridge)
            => Environment.CurrentManagedThreadId == _thread && !IsDisposed && FailureReason == null
                && ReferenceEquals(bridge, _bridge) && !_bridge.IsServer && _source != null;
        internal bool CanRetainSwimmingCommit => !IsDisposed && FailureReason == null
            && (!_swimmingCommit.IsValid || _swimmingCommit == _swimmingApplied);
        internal bool SwimmingTransitionsPrepared => _swimmingRoute != null
            && (_bridge.IsServer ? _swimmingMotor != null : _nativeSwimmingPrepared);

        internal bool TryBindServerSwimming(PlayerBridge bridge,
            PlayerGroundMovement motor,
            PlayerWaterContactAuthority.ServerContinuation contacts,
            PlayerLifeAuthority life, PlayerLifeRoute route, out string detail)
        {
            detail = "Bind the inactive publisher to its exact motor and paired life route.";
            if (!ReferenceEquals(bridge, _bridge) || !_bridge.IsServer || _bridge.IsGameplayActive
                || IsDisposed || _active || FailureReason != null || _swimmingRoute != null
                || !ReferenceEquals(contacts, _server) || motor?.IsPrepared != true
                || life == null || route == null || route.Source != null || route.IsDisposed
                || route.FailureReason != null || !route.MatchesWaterOwner(bridge, life)
                || Environment.CurrentManagedThreadId != _thread) return false;
            _swimmingMotor = motor; _swimmingLife = life; _swimmingRoute = route;
            return true;
        }

        internal bool TryBindSourceSwimming(PlayerBridge bridge,
            PlayerClientMovement movement, PlayerLifeRoute route,
            out string detail)
        {
            detail = "Bind the exact source life receipt owner and qualified entry-only native executor while held.";
            if (!ReferenceEquals(bridge, _bridge) || _bridge.IsServer || _bridge.IsGameplayActive
                || IsDisposed || _active || FailureReason != null || _swimmingRoute != null
                || !ReferenceEquals(movement, _sourceMovement) || movement?.IsPrepared != true
                || route?.Source == null || route.IsDisposed || route.FailureReason != null
                || !route.Source.ForcedInventory.TryReadStatProjectionActor(bridge, out _)
                || !route.MatchesWaterOwner(bridge, null)
                || Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep) return false;
            if (!PrepareNativeSwimming(out detail)) return false;
            _swimmingRoute = route;
            return true;
        }

        internal bool RetainSwimmingCommit(PlayerGroundMovement motor,
            PlayerWaterTransition edge, out string detail)
        {
            detail = "Publish only the exact motor's completed authoritative swimming edge.";
            if (!ReferenceEquals(motor, _swimmingMotor) || !CanRetainSwimmingCommit || edge == null
                || !edge.Key.Matches(_bridge) || edge.Key.LifeGeneration != _swimmingLife.LifeGeneration
                || edge.Key.Ordinal != _swimmingApplied.Ordinal + 1 || Environment.CurrentManagedThreadId != _thread) return false;
            _swimmingCommit = edge.Key; _swimmingSentAt = 0;
            return true;
        }

        internal bool IsSwimmingCommitApplied(PlayerWaterTransitionKey key) => _swimmingApplied == key;

        private void ReceiveSwimmingTransition(Datagram datagram)
        {
            if (!PlayerWaterTransitionFrame.TryRead(datagram.Payload.Span, out var frame)
                || !frame.Key.Matches(_bridge) || _swimmingRoute == null) return;
            if (_bridge.IsServer)
            {
                if (frame.Kind == PlayerWaterTransitionMessage.Applied && frame.Key == _swimmingCommit)
                    _swimmingApplied = frame.Key;
                return;
            }
            if (frame.Kind != PlayerWaterTransitionMessage.Committed) return;
            if (frame.Key == _swimmingApplied)
            { _swimmingAckDue = true; return; }
            if (frame.Key.Ordinal <= _swimmingApplied.Ordinal) return;
            if (_swimmingCommit.IsValid && !_swimmingSourceReleased)
            {
                if (_swimmingCommit != frame.Key)
                    throw new InvalidOperationException("An unfinished swimming commit cannot be replaced.");
                return;
            }
            if (frame.Key.Ordinal != _swimmingApplied.Ordinal + 1)
                throw new InvalidOperationException("Swimming commits must arrive in retained edge order after paired release.");
            _swimmingCommit = frame.Key;
            _swimmingNativeAttempted = _swimmingNativeApplied = _swimmingSourceReleased = false;
        }

        private bool PumpSwimmingTransitions(out string detail)
        {
            detail = "No retained swimming commit is due.";
            if (_swimmingRoute == null) return true;
            if (_swimmingRoute.IsDisposed || _swimmingRoute.FailureReason != null)
                throw new InvalidOperationException(_swimmingRoute.FailureReason ?? "Swimming life route retired.");
            if (_bridge.IsServer)
            {
                if (!_swimmingMotor.PumpSwimmingEntryRelease(out detail)) return false;
                if (_swimmingCommit.IsValid && _swimmingCommit != _swimmingApplied
                    && (_swimmingSentAt == 0 || Stopwatch.GetTimestamp() - _swimmingSentAt >= HeartbeatInterval))
                    SendSwimmingTransition(PlayerWaterTransitionMessage.Committed, _swimmingCommit, out _swimmingSentAt);
                return true;
            }
            if (!_swimmingCommit.IsValid) return true;
            if (!_swimmingNativeApplied)
            {
                if (!_swimmingRoute.Source.CanPrepareForcedDrop(_swimmingCommit.LifeGeneration,
                        Held.MutationKind.SwimmingDrop, _swimmingCommit.Ordinal)
                    || !_swimmingRoute.HasSettledForcedDrop(Held.MutationKind.SwimmingDrop, _swimmingCommit.Ordinal))
                { detail = "The committed swimming edge awaits its exact settled source held-drop receipt."; return true; }
                if (Time.inFixedTimeStep || _consumerDepth != 0
                    || !_swimmingNativeAttempted && !_sourceMovement.CanDispatchSwimmingEntry)
                { detail = "Retain swimming until the outer source native-entry boundary."; return true; }
                if (!PlayerLifecycle.TryYieldSourceInputHoldForAcceptedWork(_bridge, out detail))
                    throw new InvalidOperationException(detail);
                RequireSourceScheduleCurrent();
                if (!_swimmingRoute.Source.CanPrepareForcedDrop(_swimmingCommit.LifeGeneration,
                        Held.MutationKind.SwimmingDrop, _swimmingCommit.Ordinal)
                    || !_swimmingRoute.HasSettledForcedDrop(Held.MutationKind.SwimmingDrop, _swimmingCommit.Ordinal))
                    throw new InvalidOperationException("The exact swimming settlement changed during source hold yield.");
                var inventory = _swimmingRoute.Source.ForcedInventory;
                if (!_swimmingNativeAttempted)
                {
                    var admission = inventory.TryBeginSwimmingEntry(_swimmingCommit, _swimmingRoute, out detail);
                    if (admission == BoltMigrationAdmission.Rejected)
                        throw new InvalidOperationException(detail);
                    if (admission == BoltMigrationAdmission.Pending) return true;
                    _swimmingNativeAttempted = true;
                    SwimmingNativeCallbackActive = true;
                    bool returned = false;
                    try
                    {
                        InvokeNativeSwimmingEntry();
                        RequireSourceScheduleCurrent();
                        RequireNativeSwimmingCurrent();
                        if (!_sourceAnimation.IsSwimming || !_sourceAnimation.waterBlock)
                            throw new InvalidOperationException("Native swimming entry did not read back its latches.");
                        returned = true;
                    }
                    finally
                    {
                        SwimmingNativeCallbackActive = false;
                        if (!inventory.EndSwimmingEntry(_swimmingCommit, returned, out detail))
                            throw new InvalidOperationException(detail);
                    }
                }
                var equipment = inventory.TryPumpSwimmingEntry(_swimmingCommit, out detail);
                if (equipment == BoltMigrationAdmission.Rejected)
                    throw new InvalidOperationException(detail);
                if (equipment == BoltMigrationAdmission.Pending) return true;
                RequireNativeSwimmingCurrent();
                if (!_sourceAnimation.IsSwimming)
                    throw new InvalidOperationException("Swimming latch changed before equipment settlement.");
                _swimmingNativeApplied = true;
            }
            if (!_swimmingSourceReleased)
            {
                if (!_swimmingRoute.TryReleaseForcedDrop(Held.MutationKind.SwimmingDrop, _swimmingCommit.Ordinal, out detail)) return true;
                _swimmingSourceReleased = true;
                _swimmingApplied = _swimmingCommit; _swimmingAckDue = true;
            }
            if (_swimmingAckDue)
            {
                SendSwimmingTransition(PlayerWaterTransitionMessage.Applied, _swimmingApplied, out long sent);
                if (sent != 0) _swimmingAckDue = false;
            }
            return true;
        }

        private void SendSwimmingTransition(PlayerWaterTransitionMessage kind, PlayerWaterTransitionKey key, out long sent)
        {
            sent = 0;
            Span<byte> bytes = stackalloc byte[PlayerWaterTransitionFrame.Length];
            new PlayerWaterTransitionFrame(kind, key).Write(bytes);
            var result = Datagrams.Send(_bridge.Peer, bytes);
            if (result == DatagramSendResult.WindowFull) return;
            if (result != DatagramSendResult.Queued)
                throw new InvalidOperationException("Swimming edge transport unavailable: " + result);
            sent = Stopwatch.GetTimestamp();
        }
    }
}
