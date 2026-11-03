// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Armour = Modstoke.Sons.PlayerArmourAuthority;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerArmourRoute : IDisposable
    {
        private static readonly ConditionalWeakTable<PlayerBridge, PlayerArmourRoute> Used = new();
        private static readonly long RetryTicks = Math.Max(1, Stopwatch.Frequency / 4);
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerBridge _bridge;
        private readonly Armour _server;
        private readonly PlayerInventoryAuthority _inventory;
        private readonly PlayerArmourSource _source;
        private readonly string _baselineFingerprint;
        private Command _command, _completed;
        private Armour.ArmourSettlement _settlement, _completedSettlement;
        private Frame _assembling;
        private byte[] _assembly;
        private bool[] _fragments;
        private int _receivedFragments, _sendOffset;
        private bool _peerReady, _applied, _committed, _completedAckDue, _registered, _pumping;
        private long _sent;
        internal bool IsDisposed { get; private set; }
        internal string FailureReason { get; private set; }
        internal bool HasPendingCustody => _command != null || _assembly != null || _completedAckDue || _source?.HasPendingCustody == true;
        internal bool IsPeerPrepared => _peerReady && !IsDisposed && FailureReason == null;

        private PlayerArmourRoute(PlayerBridge bridge, Armour server,
            PlayerInventoryAuthority inventory, PlayerArmourSource source, string fingerprint)
        { _bridge = bridge; _server = server; _inventory = inventory; _source = source; _baselineFingerprint = fingerprint; }

        internal static bool TryPrepareServer(PlayerBridge bridge, Armour armour,
            PlayerInventoryAuthority inventory, out PlayerArmourRoute route, out string detail)
        {
            route = null; detail = "Armour route requires this inactive bridge's exact approved armour/inventory owners.";
            if (bridge?.IsServer != true || bridge.IsDisposed || bridge.IsGameplayActive || armour == null || inventory == null
                || !armour.MatchesPreparedRoute(bridge, inventory) || Used.TryGetValue(bridge, out _)) return false;
            var candidate = new PlayerArmourRoute(bridge, armour, inventory, null, armour.AcceptedBaselineFingerprint);
            if (!inventory.TryBindArmourRoute(candidate, bridge)) return false;
            candidate.Register(); route = candidate;
            detail = "Server armour command/receipt route prepared without consuming combat ingress.";
            return true;
        }

        internal static bool TryPrepareSource(PlayerBridge bridge,
            PlayerInventoryEnforcement inventory, Armour.Snapshot snapshot,
            out PlayerArmourRoute route, out string detail)
        {
            route = null; detail = "Armour source requires the retained local capture, not a decoded observation.";
            if (bridge == null || Used.TryGetValue(bridge, out _)) return false;
            bool prepared = PlayerArmourSource.TryPrepare(bridge, inventory, snapshot, out var source, out detail);
            if (source == null) return false;
            var candidate = new PlayerArmourRoute(bridge, null, null, source, snapshot.Fingerprint);
            route = candidate;
            if (!prepared) return false;
            candidate.Register();
            return true;
        }

        private void Register()
        {
            Used.Add(_bridge, this);
            Datagrams.Received += Receive; _bridge.Retired += Dispose; _registered = true;
        }

        // The parent retains the AcceptedHit. This entry reserves exactly one armour batch,
        // marks its one source dispatch, and subsequently retransmits only identical bytes.
        internal bool TryDispatch(Armour.Calculation calculation, out string detail)
        {
            detail = FailureReason ?? "Armour dispatch awaits a prepared peer and idle exact owners.";
            if (_server == null || IsDisposed || FailureReason != null || _pumping || !_peerReady
                || !_bridge.IsGameplayActive || Environment.CurrentManagedThreadId != _thread || HasPendingCustody
                || calculation == null || !ReferenceEquals(calculation.Owner, _server)) return false;
            try
            {
                RequireCurrent();
                var command = Command.Capture(calculation);
                if (!_server.TryReserveArmour(calculation, out detail)) return false;
                _command = command; _applied = _committed = false; _settlement = null; _sendOffset = 0; _sent = 0;
                if (!_inventory.TryBeginArmourSource(calculation, out var fingerprint, out detail) || fingerprint != command.Fingerprint)
                    throw new InvalidOperationException("Exact armour source dispatch could not be marked.");
                detail = "Armour custody reserved and exact source command retained; native and publication receipts are pending.";
                return true;
            }
            catch (Exception error) { Fail(error.Message); detail = FailureReason; return false; }
        }

        internal bool ConfirmsSourceEffects(PlayerInventoryAuthority inventory, Armour.Calculation calculation, string fingerprint)
            => Environment.CurrentManagedThreadId == _thread && !IsDisposed && FailureReason == null && _pumping
                && ReferenceEquals(inventory, _inventory) && _applied && _command != null
                && ReferenceEquals(_command.Numbers, calculation) && _command.Fingerprint == fingerprint;

        internal bool TryReadSettlement(Armour.Calculation calculation, out Armour.ArmourSettlement settlement)
        {
            settlement = null;
            if (_server == null || IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread
                || !_bridge.IsGameplayActive || !ReferenceEquals(_completed?.Numbers, calculation)) return false;
            try { RequireCurrent(); settlement = _completedSettlement; return settlement != null; }
            catch (Exception error) { Fail(error.Message); return false; }
        }

        internal bool TryPump(out string detail)
        {
            detail = FailureReason ?? "Armour transport awaits activation.";
            if (IsDisposed || _pumping || Environment.CurrentManagedThreadId != _thread
                || !_bridge.IsGameplayActive || UnityEngine.Time.inFixedTimeStep) return false;
            _pumping = true;
            try
            {
                RequireTransport();
                if (FailureReason != null)
                {
                    if (_command != null) Send(Frame.For(Message.Unconfirmed, _command).Write());
                    return false;
                }
                RequireCurrent();
                if (_server != null)
                {
                    if (_command == null) { detail = "Armour route is idle; no hit was consumed."; return true; }
                    if (_applied && !_committed)
                    {
                        if (!_inventory.TryAcknowledgeArmourSource(this, _bridge.Peer, _command.Numbers.Transaction,
                            _command.Fingerprint, out detail)) throw new InvalidOperationException(detail);
                        if (!_server.TrySettleArmour(_command.Numbers, out _settlement, out detail))
                            throw new InvalidOperationException(detail);
                        _committed = true; _sent = 0;
                    }
                    if (Due())
                    {
                        if (_committed) { if (Send(Frame.For(Message.Commit, _command).Write())) _sent = Stopwatch.GetTimestamp(); }
                        else SendCommand();
                    }
                }
                else
                {
                    if (_command != null)
                    {
                        if (!_source.HasPendingCustody && !_applied)
                        {
                            _source.TryBegin(_command, out detail);
                            if (_source.FailureReason != null) throw new InvalidOperationException(_source.FailureReason);
                        }
                        if (_source.HasPendingCustody && !_applied)
                            _applied = _source.TryConfirmEffects(_command, out detail);
                        if (_committed && _applied && _source.TryComplete(_command, out detail))
                        {
                            _completed = _command; _command = null; _completedAckDue = true; _sent = 0;
                        }
                        if (_source.FailureReason != null) throw new InvalidOperationException(_source.FailureReason);
                        if (_command != null && _applied && Due() && Send(Frame.For(Message.Applied, _command).Write()))
                            _sent = Stopwatch.GetTimestamp();
                    }
                    if (_completedAckDue && Send(Frame.For(Message.Completed, _completed).Write())) _completedAckDue = false;
                    if (_command == null && _completed == null && Due() && Send(ReadyFrame.Write())) _sent = Stopwatch.GetTimestamp();
                }
                detail = "Armour delivery retained; only native effects and exact final publication release this stage.";
                return true;
            }
            catch (Exception error) { Fail(error.Message); detail = FailureReason; return false; }
            finally { _pumping = false; }
        }

        private Frame ReadyFrame => new(Message.Ready, _bridge.Plan.Id, _bridge.Plan.Generation, _bridge.NetworkId, 0, 0, _baselineFingerprint);
        private bool Due() => _sent == 0 || Stopwatch.GetTimestamp() - _sent >= RetryTicks;

        private void SendCommand()
        {
            while (_sendOffset < _command.Bytes.Length)
            {
                int count = Math.Min(Frame.ChunkBytes, _command.Bytes.Length - _sendOffset);
                var frame = Frame.For(Message.Command, _command) with { Offset = (ushort)_sendOffset, Total = (ushort)_command.Bytes.Length };
                if (!Send(frame.Write(_command.Bytes.Slice(_sendOffset, count)))) return;
                _sendOffset += count;
            }
            _sendOffset = 0; _sent = Stopwatch.GetTimestamp();
        }

        private bool Send(byte[] bytes)
        {
            var result = Datagrams.Send(_bridge.Peer, bytes);
            if (result == DatagramSendResult.WindowFull) return false;
            if (result != DatagramSendResult.Queued)
                throw new InvalidOperationException("Armour transport is uncertain: " + result);
            return true;
        }

        private void Receive(Datagram datagram)
        {
            if (IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread || datagram == null
                || !ReferenceEquals(datagram.Peer, _bridge.Peer) || !_bridge.Peer.IsCurrent || !_bridge.IsGameplayActive
                || !Frame.TryRead(datagram.Payload.Span, out var frame) || frame.Migration != _bridge.Plan.Id
                || frame.Generation != _bridge.Plan.Generation || frame.Actor != _bridge.NetworkId) return;
            try
            {
                RequireCurrent();
                if (_server != null)
                {
                    if (frame == ReadyFrame) { _peerReady = true; return; }
                    if (_command == null || !frame.SameCommand(Frame.For(frame.Kind, _command))) return;
                    if (frame.Kind == Message.Unconfirmed) throw new InvalidOperationException("Source reports uncertain armour effects; accepted hit remains retained.");
                    if (frame.Kind == Message.Applied) { _applied = true; _sent = 0; }
                    else if (frame.Kind == Message.Completed && _committed)
                    {
                        _completed = _command; _completedSettlement = _settlement;
                        _command = null; _sent = 0;
                    }
                    return;
                }
                if (_completed != null && frame.SameCommand(Frame.For(frame.Kind, _completed)))
                {
                    if (frame.Kind is Message.Command or Message.Commit) _completedAckDue = true;
                    return;
                }
                if (frame.Kind == Message.Command) { ReceiveFragment(frame, datagram.Payload.Span.Slice(Frame.HeaderBytes)); return; }
                if (_command == null || !frame.SameCommand(Frame.For(frame.Kind, _command))) return;
                if (frame.Kind == Message.Unconfirmed) throw new InvalidOperationException("Server retained uncertain armour custody.");
                if (frame.Kind == Message.Commit)
                {
                    if (!_applied || !_source.EffectsApplied) throw new InvalidOperationException("Armour publication arrived without this source's native receipt.");
                    _committed = true;
                }
            }
            catch (Exception error) { Fail(error.Message); }
        }

        private void ReceiveFragment(Frame frame, ReadOnlySpan<byte> bytes)
        {
            if (_command != null)
            {
                if (frame.Transaction != _command.Numbers.Transaction || frame.Revision != _command.Numbers.BeforeRevision) return;
                if (!frame.SameCommand(Frame.For(Message.Command, _command)) || frame.Total != _command.Bytes.Length
                    || !_command.Bytes.Slice(frame.Offset, bytes.Length).SequenceEqual(bytes))
                    throw new InvalidOperationException("An accepted armour command changed during retry.");
                if (_applied) _sent = 0;
                return;
            }
            if (frame.Revision != _source.Revision || frame.Transaction <= _source.LastTransaction) return;
            if (_assembly == null)
            {
                _assembling = frame; _assembly = new byte[frame.Total];
                _fragments = new bool[(frame.Total + Frame.ChunkBytes - 1) / Frame.ChunkBytes]; _receivedFragments = 0;
            }
            if (!_assembling.SameCommand(frame) || _assembling.Total != frame.Total)
                throw new InvalidOperationException("An unfinished armour command changed identity.");
            int index = frame.Offset / Frame.ChunkBytes;
            if (_fragments[index])
            {
                if (!_assembly.AsSpan(frame.Offset, bytes.Length).SequenceEqual(bytes))
                    throw new InvalidOperationException("An armour fragment changed on retry.");
                return;
            }
            bytes.CopyTo(_assembly.AsSpan(frame.Offset)); _fragments[index] = true; _receivedFragments++;
            if (_receivedFragments != _fragments.Length) return;
            var command = Command.Read(_assembly);
            if (command.Plan != _bridge.Plan || command.Actor != _bridge.NetworkId || command.Fingerprint != frame.Fingerprint
                || command.Numbers.Transaction != frame.Transaction || command.Numbers.BeforeRevision != frame.Revision
                || command.ArmourFingerprint != _baselineFingerprint)
                throw new InvalidOperationException("Armour command does not match its authenticated envelope/baseline.");
            _command = command; _applied = _committed = false; _sent = 0; _assembly = null; _fragments = null;
        }

        private void RequireTransport()
        {
            if (_bridge.IsDisposed || !_bridge.Peer.IsCurrent || !_bridge.TryReadCurrentTarget(out _))
                throw new InvalidOperationException("Exact armour bridge/peer retired.");
        }
        private void RequireCurrent()
        {
            RequireTransport();
            if (_server != null ? !_server.MatchesPreparedRoute(_bridge, _inventory) : !_source.Current())
                throw new InvalidOperationException("Exact armour participant retired or faulted.");
        }
        private void Fail(string detail)
        {
            FailureReason ??= detail;
            if (_server != null && _command != null) _inventory.RetainUnconfirmedArmour(_command.Numbers, FailureReason);
            if (_server != null) _inventory.RetainArmourRouteFailure(this, FailureReason);
            _source?.Fail(FailureReason);
        }

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("Armour routing belongs to its game thread.");
            if (IsDisposed) return;
            if (HasPendingCustody) Fail("Armour route retired with accepted work retained; do not replay or refund.");
            IsDisposed = true;
            if (_registered) { Datagrams.Received -= Receive; _bridge.Retired -= Dispose; _registered = false; }
            _source?.Dispose();
        }
    }
}
