// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;
using Held = Modstoke.Sons.PlayerHeldOnlyAuthority;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerLifeRoute
    {
        private PlayerLifeDropFrame _drop, _completedDrop;
        private PlayerLifeDropMessage _dropReceived, _dropReply;
        private bool _dropPending, _dropLocalSettled, _dropReleased, _dropReleaseRequested, _completedDropReply;
        private long _dropSentAt;
        private ulong _completedKnockDownDrop, _completedSwimmingDrop;

        internal bool HasPendingForcedTransition => _dropPending;
        internal bool HasPendingDowningDrop => _dropPending && _drop.DropKind == Held.MutationKind.KnockDownDrop;

        // Called only by the serialized server life/water owner BEFORE its latch.
        // False retains the same request. There is no cancellation/replacement path.
        internal bool TrySettleForcedDrop(Held.MutationKind kind, ulong transition, out string detail)
        {
            detail = FailureReason ?? "Forced transition awaits both endpoint receipts and the real world route.";
            if (_server == null || IsDisposed || FailureReason != null || _pumping
                || Environment.CurrentManagedThreadId != _thread || !_bridge.IsGameplayActive
                || !Held.IsForcedDrop(kind) || transition == 0) return false;
            RequireCurrent();
            var request = new PlayerLifeDropFrame(PlayerLifeDropMessage.Prepare, _bridge.Plan.Id,
                _bridge.Plan.Generation, _bridge.NetworkId, _server.LifeGeneration, transition, kind);
            if (!_dropPending)
            {
                if (transition <= (kind == Held.MutationKind.KnockDownDrop ? _completedKnockDownDrop : _completedSwimmingDrop))
                { detail = "This forced transition was already consumed; it cannot authorize another mutation."; return false; }
                var route = ForcedInventory?.HeldOnlyRoute;
                if (route == null || !route.TryReadForcedDropAdmission(out detail)
                    || !route.TryRequestForcedDrop(kind, transition, out detail)) return false;
                BeginForcedDrop(request);
            }
            if (!_drop.SameTransition(request) || _dropReleaseRequested || _dropReleased) return false;
            if ((byte)_dropReceived < (byte)PlayerLifeDropMessage.Settled || !_dropLocalSettled) return false;
            return ForcedInventory.TryReadHeldOnlyForcedDropSettlement(kind, transition, out detail);
        }

        // After the REAL owning boundary commits, each endpoint releases its local
        // hold. Source water calls this after its native swimming edge; life does so
        // automatically after accepting the exact StartDeath command.
        internal bool TryReleaseForcedDrop(Held.MutationKind kind, ulong transition, out string detail)
        {
            detail = FailureReason ?? "Release requires the exact settled forced transition and its owning commit.";
            if (IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread
                || !_dropPending || _drop.DropKind != kind || _drop.Transition != transition
                || !_dropLocalSettled || _server != null && (byte)_dropReceived < (byte)PlayerLifeDropMessage.Settled) return false;
            RequireCurrent();
            if (!_dropReleased && !ForcedInventory.HeldOnlyRoute.TryReleaseForcedDrop(kind, transition, out detail)) return false;
            _dropReleased = true;
            if (_server != null) _dropReleaseRequested = true;
            _dropSentAt = 0;
            return true;
        }

        internal bool HasSettledForcedDrop(Held.MutationKind kind, ulong transition)
            => _source != null && !IsDisposed && FailureReason == null && Environment.CurrentManagedThreadId == _thread
                && _dropPending && !_dropReleased && _drop.DropKind == kind && _drop.Transition == transition && _dropLocalSettled
                && ForcedInventory.TryReadHeldOnlyForcedDropSettlement(kind, transition, out _);

        private void BeginForcedDrop(in PlayerLifeDropFrame request)
        {
            _drop = request with { Kind = PlayerLifeDropMessage.Prepare };
            _dropPending = true; _dropLocalSettled = _dropReleased = _dropReleaseRequested = false;
            _dropReceived = _dropReply = 0; _dropSentAt = 0;
            if (_source != null) _source.HasPendingForcedWork = true;
        }

        private void PumpForcedDrop()
        {
            var inventory = ForcedInventory;
            var route = inventory?.HeldOnlyRoute;
            if (_dropPending && (route == null || inventory.IsDisposed || inventory.IsFaulted || route.ForcedFailure != null))
                throw new InvalidOperationException(route?.ForcedFailure ?? "Forced transition lost its exact inventory endpoint.");
            if (_completedDropReply)
            {
                SendForcedDrop(_completedDrop with { Kind = PlayerLifeDropMessage.Released }, out long sent);
                if (sent != 0) _completedDropReply = false;
            }
            if (!_dropPending) return;
            if (_server != null)
            {
                if (_dropReleaseRequested)
                {
                    if (_dropReceived == PlayerLifeDropMessage.Released) { CompleteForcedDrop(); return; }
                    _dropReply = PlayerLifeDropMessage.Release;
                }
                else if ((byte)_dropReceived >= (byte)PlayerLifeDropMessage.Prepared
                    && route.TryArmForcedDrop(_drop.DropKind, _drop.Transition, out _))
                {
                    _dropLocalSettled = route.TryReadForcedDropSettlement(_drop.DropKind, _drop.Transition, out _);
                    _dropReply = PlayerLifeDropMessage.Execute;
                }
                else _dropReply = PlayerLifeDropMessage.Prepare;
            }
            else
            {
                if (_dropReleased)
                {
                    if (_dropReceived != PlayerLifeDropMessage.Release) return;
                    SendForcedDrop(_drop with { Kind = PlayerLifeDropMessage.Released }, out long sent);
                    if (sent != 0) CompleteForcedDrop();
                    return;
                }
                if (!route.TryRequestForcedDrop(_drop.DropKind, _drop.Transition, out var detail))
                    throw new InvalidOperationException(detail);
                if ((byte)_dropReceived >= (byte)PlayerLifeDropMessage.Execute)
                {
                    if (!route.TryArmForcedDrop(_drop.DropKind, _drop.Transition, out _)) return;
                    _dropLocalSettled = route.TryReadForcedDropSettlement(_drop.DropKind, _drop.Transition, out _);
                    _dropReply = _dropLocalSettled ? PlayerLifeDropMessage.Settled : PlayerLifeDropMessage.Prepared;
                }
                else if (route.TryReadForcedDropPrepared(_drop.DropKind, _drop.Transition, out _))
                    _dropReply = PlayerLifeDropMessage.Prepared;
            }
            if (_dropReply == 0 || _dropSentAt != 0 && Stopwatch.GetTimestamp() - _dropSentAt < RetryTicks) return;
            SendForcedDrop(_drop with { Kind = _dropReply }, out _dropSentAt);
        }

        private void CompleteForcedDrop()
        {
            _completedDrop = _drop; _dropPending = false; _dropSentAt = 0;
            if (_drop.DropKind == Held.MutationKind.KnockDownDrop) _completedKnockDownDrop = _drop.Transition;
            else _completedSwimmingDrop = _drop.Transition;
            if (_source != null) _source.HasPendingForcedWork = false;
        }

        private void ReceiveForcedDrop(ReadOnlySpan<byte> bytes)
        {
            if (!PlayerLifeDropFrame.TryRead(bytes, out var frame) || frame.MigrationId != _bridge.Plan.Id
                || frame.Generation != _bridge.Plan.Generation || frame.NetworkId != _bridge.NetworkId) return;
            if (_server != null)
            {
                if (!_dropPending || !_drop.SameTransition(frame)
                    || frame.Kind is not (PlayerLifeDropMessage.Prepared or PlayerLifeDropMessage.Settled or PlayerLifeDropMessage.Released)
                    || frame.Kind == PlayerLifeDropMessage.Settled && (byte)_dropReply < (byte)PlayerLifeDropMessage.Execute
                    || frame.Kind == PlayerLifeDropMessage.Released && !_dropReleaseRequested) return;
            }
            else
            {
                if (frame.Kind is not (PlayerLifeDropMessage.Prepare or PlayerLifeDropMessage.Execute or PlayerLifeDropMessage.Release)) return;
                if (_completedDrop.IsValid && _completedDrop.SameTransition(frame))
                { _completedDropReply = true; return; }
                if (frame.Transition <= (frame.DropKind == Held.MutationKind.KnockDownDrop
                    ? _completedKnockDownDrop : _completedSwimmingDrop)) return;
                if (!_dropPending)
                {
                    if (frame.Kind != PlayerLifeDropMessage.Prepare
                        || !_source.CanPrepareForcedDrop(frame.LifeGeneration, frame.DropKind, frame.Transition)) return;
                    if (ForcedInventory.HeldOnlyRoute == null
                        || !ForcedInventory.HeldOnlyRoute.TryRequestForcedDrop(frame.DropKind, frame.Transition, out _))
                        throw new InvalidOperationException("The ordered forced transition lost its source held endpoint.");
                    BeginForcedDrop(frame);
                }
                if (!_drop.SameTransition(frame))
                    throw new InvalidOperationException("An unfinished forced transition cannot be replaced by another life/water edge.");
                if (frame.Kind == PlayerLifeDropMessage.Release && !_dropLocalSettled) return;
            }
            if ((byte)frame.Kind > (byte)_dropReceived) { _dropReceived = frame.Kind; _dropSentAt = 0; }
        }

        private void SendForcedDrop(in PlayerLifeDropFrame frame, out long sentAt)
        {
            sentAt = 0;
            Span<byte> bytes = stackalloc byte[PlayerLifeDropFrame.Length];
            frame.Write(bytes);
            sentAt = SendPayload(bytes, "Forced life/water transport unavailable: ");
        }
    }
}
