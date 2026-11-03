// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerLifeRoute : IDisposable
    {
        private static readonly long RetryTicks = Math.Max(1, Stopwatch.Frequency / 4);
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerBridge _bridge;
        private readonly PlayerLifeAuthority _server;
        private readonly PlayerLifePresentation _source;
        private PlayerLifeFrame _command, _acknowledgement, _completed;
        private PlayerLifeValuesFrame _values, _completedValues;
        private PlayerLifeBurnFrame _burnReceipt, _completedBurnReceipt;
        private bool _hasBurnReceipt;
        private bool _hasValues;
        private bool _hasCommand, _begun, _hasAcknowledgement, _ackDue, _registered, _pumping;
        private long _sentAt;
        internal bool IsDisposed { get; private set; }
        internal string FailureReason { get; private set; }
        internal PlayerLifePresentation Source => _source;
        internal bool MatchesWaterOwner(PlayerBridge bridge, PlayerLifeAuthority server)
            => Environment.CurrentManagedThreadId == _thread && !IsDisposed && FailureReason == null
                && ReferenceEquals(bridge, _bridge) && ReferenceEquals(server, _server)
                && (server != null || _source != null);
        private PlayerInventoryEnforcement ForcedInventory => _server != null ? _server.ForcedInventory : _source.ForcedInventory;

        private PlayerLifeRoute(PlayerBridge bridge,
            PlayerLifeAuthority server, PlayerLifePresentation source)
        { _bridge = bridge; _server = server; _source = source; }

        internal static bool TryPrepareServer(PlayerBridge bridge,
            PlayerLifeAuthority authority, PlayerStatProjection projection,
            out PlayerLifeRoute route, out string detail)
        {
            route = null; detail = "Life routing requires this bridge's exact prepared life and stat owners.";
            if (bridge?.IsServer != true || bridge.IsDisposed || bridge.IsGameplayActive || authority == null
                || projection == null) return false;
            var candidate = new PlayerLifeRoute(bridge, authority, null);
            if (!authority.BindSourceRoute(bridge, candidate, projection) || !projection.BindLife(bridge, authority))
            { candidate.Dispose(); return false; }
            candidate.Register(); route = candidate;
            detail = "Exact server life route prepared without callbacks or publication.";
            return true;
        }

        internal static bool TryPrepareSource(PlayerBridge bridge,
            PlayerInventoryEnforcement inventory, PlayerStatProjection projection,
            out PlayerLifeRoute route, out string detail)
        {
            route = null; detail = "Source life routing requires the exact retained actor and inventory owner.";
            if (bridge == null || bridge.IsServer || bridge.IsDisposed || bridge.IsGameplayActive
                || !PlayerLifePresentation.TryPrepare(bridge, inventory, projection, out var owner, out detail))
                return false;
            var candidate = new PlayerLifeRoute(bridge, null, owner);
            candidate.Register(); route = candidate;
            return true;
        }

        private void Register()
        {
            Datagrams.Received += Receive;
            _bridge.Retired += Dispose;
            _registered = true;
        }

        internal bool TryPump(out string detail)
        {
            detail = FailureReason ?? "Life route is not active.";
            if (IsDisposed || FailureReason != null || _pumping || Environment.CurrentManagedThreadId != _thread
                || !_bridge.IsGameplayActive || UnityEngine.Time.inFixedTimeStep) return false;
            _pumping = true;
            try
            {
                RequireCurrent();
                PumpForcedDrop();
                if (_bridge.IsServer)
                {
                    if (_hasBurnReceipt)
                    {
                        if (!_server.TryAcknowledgeBurning(this, _burnReceipt))
                            throw new InvalidOperationException("Burning acknowledgement lost its exact server commit.");
                        _hasBurnReceipt = false;
                    }
                    if (_hasAcknowledgement)
                    {
                        if (!_server.TryAcknowledgeSourceCommand(this, _acknowledgement, out detail))
                        {
                            if (_server.FailureReason != null) throw new InvalidOperationException(_server.FailureReason);
                        }
                        else { _hasAcknowledgement = false; _hasCommand = false; _sentAt = 0; }
                    }
                    if (!_hasCommand && _server.TryReadSourceCommand(out var next))
                    {
                        if (!_server.TryReadSourceValues(next, out _values))
                            throw new InvalidOperationException("The exact committed life snapshot is unavailable.");
                        _command = next; _hasCommand = true;
                    }
                    if (_hasCommand && (_sentAt == 0 || Stopwatch.GetTimestamp() - _sentAt >= RetryTicks))
                    {
                        SendValues(_values, out long numbersSent);
                        if (numbersSent != 0) Send(_command, out _sentAt);
                        if (_server.TryReadBurningCommit(_command, out var burning)) SendBurning(burning, out _);
                    }
                }
                else
                {
                    if (_hasCommand && _hasValues && !_begun)
                    {
                        if (!_values.MatchesCommand(_command))
                            throw new InvalidOperationException("Life command and committed snapshot disagree.");
                        _begun = _source.TryBegin(_command, _values, out detail);
                        if (_source.FailureReason != null) throw new InvalidOperationException(_source.FailureReason);
                        if (_begun && _command.Action == PlayerLifeSourceAction.StartDeath
                            && !TryReleaseForcedDrop(PlayerHeldOnlyAuthority.MutationKind.KnockDownDrop,
                                _command.Sequence, out detail))
                            throw new InvalidOperationException(detail);
                    }
                    if (_begun && _hasBurnReceipt && !_source.TryAcceptBurning(_burnReceipt, out detail))
                        throw new InvalidOperationException(_source.FailureReason ?? detail);
                    if (!_source.TryPump(out detail))
                        throw new InvalidOperationException(_source.FailureReason ?? detail);
                    if (_hasCommand && _begun && _source.CompletedSequence == _command.Sequence)
                    {
                        _completed = _command; _completedValues = _values;
                        _completedBurnReceipt = _source.TryReadBurningCompletion(out var receipt) ? receipt : default;
                        _hasBurnReceipt = false;
                        _hasCommand = false; _hasValues = false; _begun = false; _ackDue = true;
                    }
                    if (_ackDue)
                    {
                        if (_completedBurnReceipt.IsValid)
                        {
                            SendBurning(_completedBurnReceipt, out long burnSent);
                            if (burnSent == 0) return true;
                        }
                        Send(_completed with { Kind = PlayerLifeMessage.Acknowledgement }, out long sent);
                        if (sent != 0) _ackDue = false;
                    }
                }
                detail = "Ordered life delivery retained; only native completion advances its acknowledgement.";
                return true;
            }
            catch (Exception error) { FailureReason ??= error.Message; detail = FailureReason; return false; }
            finally { _pumping = false; }
        }

        private void Send(in PlayerLifeFrame frame, out long sentAt)
        {
            sentAt = 0;
            Span<byte> bytes = stackalloc byte[PlayerLifeFrame.Length];
            frame.Write(bytes);
            sentAt = SendPayload(bytes, "Life transport unavailable: ");
        }

        private void SendValues(in PlayerLifeValuesFrame values, out long sentAt)
        {
            sentAt = 0;
            Span<byte> bytes = stackalloc byte[PlayerLifeValuesFrame.Length];
            values.Write(bytes);
            sentAt = SendPayload(bytes, "Life snapshot transport unavailable: ");
        }

        private void SendBurning(in PlayerLifeBurnFrame receipt, out long sentAt)
        {
            sentAt = 0;
            Span<byte> bytes = stackalloc byte[PlayerLifeBurnFrame.Length];
            receipt.Write(bytes);
            sentAt = SendPayload(bytes, "Burning receipt transport unavailable: ");
        }

        private long SendPayload(ReadOnlySpan<byte> bytes, string failureMessage)
        {
            var result = Datagrams.Send(_bridge.Peer, bytes);
            if (result == DatagramSendResult.WindowFull) return 0;
            if (result != DatagramSendResult.Queued)
                throw new InvalidOperationException(failureMessage + result);
            return Stopwatch.GetTimestamp();
        }

        private void Receive(Datagram datagram)
        {
            if (IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread
                || datagram == null || !ReferenceEquals(datagram.Peer, _bridge.Peer)
                || !_bridge.IsGameplayActive) return;
            bool numbers = PlayerLifeValuesFrame.Matches(datagram.Payload.Span);
            bool burning = PlayerLifeBurnFrame.Matches(datagram.Payload.Span);
            bool drop = PlayerLifeDropFrame.Matches(datagram.Payload.Span);
            if (!numbers && !burning && !drop && !PlayerLifeFrame.Matches(datagram.Payload.Span)) return;
            try
            {
                RequireCurrent();
                if (drop) { ReceiveForcedDrop(datagram.Payload.Span); return; }
                if (burning)
                {
                    if (!PlayerLifeBurnFrame.TryRead(datagram.Payload.Span, out var receipt)
                        || receipt.MigrationId != _bridge.Plan.Id || receipt.Generation != _bridge.Plan.Generation
                        || receipt.NetworkId != _bridge.NetworkId) return;
                    if (_bridge.IsServer)
                    {
                        if (receipt.Kind != PlayerLifeBurnMessage.Acknowledgement || !_hasCommand
                            || !receipt.MatchesCommand(_command)
                            || !_server.TryReadBurningCommit(_command, out var commit) || !commit.SameCommit(receipt)) return;
                    }
                    else
                    {
                        if (receipt.Kind != PlayerLifeBurnMessage.Commit) return;
                        if (_completed.Sequence != 0 && receipt.Sequence == _completed.Sequence)
                        {
                            if (!_completedBurnReceipt.IsValid || !_completedBurnReceipt.SameCommit(receipt))
                                throw new InvalidOperationException("A completed burning receipt changed on retry.");
                            _ackDue = true; return;
                        }
                        if (receipt.Sequence != _source.CompletedSequence + 1) return;
                    }
                    if (_hasBurnReceipt && _burnReceipt != receipt)
                        throw new InvalidOperationException("An unfinished burning receipt changed on retry.");
                    _burnReceipt = receipt; _hasBurnReceipt = true; return;
                }
                if (numbers)
                {
                    if (_bridge.IsServer || !PlayerLifeValuesFrame.TryRead(datagram.Payload.Span, out var values)
                        || values.MigrationId != _bridge.Plan.Id || values.Generation != _bridge.Plan.Generation
                        || values.NetworkId != _bridge.NetworkId) return;
                    if (values.Sequence == _completed.Sequence && _completed.Sequence != 0)
                    {
                        if (values != _completedValues) throw new InvalidOperationException("A completed life snapshot changed on retry.");
                        return;
                    }
                    if (values.Sequence != _source.CompletedSequence + 1) return;
                    if (_hasValues && values != _values)
                        throw new InvalidOperationException("An unfinished life snapshot changed on retry.");
                    _values = values; _hasValues = true;
                    return;
                }
                if (!PlayerLifeFrame.TryRead(datagram.Payload.Span, out var frame)
                    || frame.MigrationId != _bridge.Plan.Id || frame.Generation != _bridge.Plan.Generation
                    || frame.NetworkId != _bridge.NetworkId) return;
                if (_bridge.IsServer)
                {
                    if (frame.Kind != PlayerLifeMessage.Acknowledgement || !_hasCommand
                        || !_command.SameCommand(frame)) return;
                    _acknowledgement = frame; _hasAcknowledgement = true;
                    return;
                }
                if (frame.Kind != PlayerLifeMessage.Command) return;
                if (frame.Sequence == _completed.Sequence && _completed.Sequence != 0)
                {
                    if (!_completed.SameCommand(frame)) throw new InvalidOperationException("A completed life command changed on retry.");
                    _ackDue = true;
                    return;
                }
                if (frame.Sequence != _source.CompletedSequence + 1) return;
                if (_hasCommand && !_command.SameCommand(frame))
                    throw new InvalidOperationException("An unfinished life command changed on retry.");
                _command = frame; _hasCommand = true;
            }
            catch (Exception error) { FailureReason ??= error.Message; }
        }

        private void RequireCurrent()
        {
            if (_bridge.IsDisposed || !_bridge.TryReadCurrentTarget(out _)
                || (_bridge.IsServer ? _server.IsDisposed || _server.FailureReason != null
                    : _source.IsDisposed || _source.FailureReason != null))
                throw new InvalidOperationException("The exact life route or actor retired.");
        }

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("Dispose life routing on the game thread.");
            if (IsDisposed) return;
            IsDisposed = true;
            if (_registered)
            { Datagrams.Received -= Receive; _bridge.Retired -= Dispose; _registered = false; }
            _source?.Dispose();
            // Retiring never acknowledges or restarts an unfinished native action.
        }
    }
}
