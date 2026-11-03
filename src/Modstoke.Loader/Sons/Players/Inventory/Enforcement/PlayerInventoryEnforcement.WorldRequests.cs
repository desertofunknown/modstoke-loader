// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;
using UnityEngine;

namespace Modstoke.Sons
{
    public enum NativeWorldIntentState { Pending, Reserved, Applied, Rejected, Unconfirmed }

    public readonly record struct NativeWorldIntentReceipt(ulong Request,
        NativeWorldIntentState State, ulong Transaction, ulong WorldNetworkId, string Detail);

    public sealed partial class PlayerInventoryEnforcement
    {
        private InventoryStorage.RequestRouter _worldRouter;
        private InventoryStorage _worldIntentTransfer;
        private PlayerWorldFrame _worldSourceRequest, _worldSourceReply, _worldServerRequest, _worldServerReply;
        private bool _worldSourcePending, _worldServerPending, _worldReserving, _worldRetired;
        private ulong _worldNextRequest, _worldLastRequest, _worldProgress;
        private long _worldStarted, _worldSent;
        private NativeWorldIntentReceipt? _worldIntentReceipt;

        public bool HasPendingNativeWorldIntent => _worldSourcePending || _worldServerPending;
        public ulong LastNativeWorldIntentRequest => _worldNextRequest;
        internal bool BlocksNativeWorldMutation => HasPendingArmourSourceCustody
            || HasPendingNativeWorldIntent && !_worldReserving;

        internal static bool RejectNativeWorldRequest(Bolt.Event request)
        {
            if (!BoltNetwork.isServer || !InventoryStorage.IsLegacyWorldRequest(request)) return false;
            if (InventoryStorage.RejectCompetingWorldRequest(request, out _)) return true;
            var sender = request.RaisedBy;
            if (sender == null || sender.WasCollected) return false;
            var session = ServerSessions.ResolveSource(sender);
            if (session?.IsCurrent != true) return false;
            foreach (var endpoint in Active)
            {
                var peer = endpoint._bridge?.Peer;
                if (endpoint._authority == null || peer == null || !peer.IsCurrent
                    || !ReferenceEquals(peer.ServerSession, session)
                    || peer.ConnectionPointer != sender.Pointer || peer.ConnectionId != sender.ConnectionId) continue;
                if (endpoint.IsDisposed || endpoint.IsFaulted) return true;
                return endpoint.RouteNativeWorldRequest(request, out _)
                    != InventoryStorage.RequestDisposition.Unrelated;
            }
            return false;
        }

        // Parent selects this endpoint by the exact authenticated native sender FIRST.
        // All-actor competing custody checks remain before this selected-actor route.
        internal InventoryStorage.RequestDisposition RouteNativeWorldRequest(Bolt.Event request, out string detail)
        {
            if (_worldRouter != null) return _worldRouter.Route(request, 0, out detail);
            detail = "Native cleanup cannot authorize inventory; the typed world route is not bound.";
            return InventoryStorage.IsLegacyWorldRequest(request)
                ? InventoryStorage.RequestDisposition.Rejected
                : InventoryStorage.RequestDisposition.Unrelated;
        }

        internal bool TryBindNativeWorldRequests(CapsuleCollider approvedBody,
            PlayerCarrierProfile approvedProfile,
            InventoryStorage.ReadInteractionPose readInteractionPose, float maximumReach,
            float clearanceRadius, int obstructionLayers, out string detail)
        {
            detail = "An idle active server inventory endpoint and approved physics policy are required.";
            if (!OnThread || _busy || _authority == null || _worldRouter != null || _worldRetired) return false;
            return InventoryStorage.TryBindRequests(_bridge, _authority, approvedBody,
                approvedProfile, readInteractionPose, maximumReach, clearanceRadius, obstructionLayers,
                out _worldRouter, out detail);
        }

        public bool TryReadNativeWorldIntentReceipt(ulong request, out NativeWorldIntentReceipt receipt)
        {
            receipt = default;
            if (!OnThread || _worldIntentReceipt is not { } current || request != current.Request) return false;
            if (_worldSourcePending && (IsDisposed || IsFaulted || _worldRetired))
                current = current with { State = NativeWorldIntentState.Unconfirmed, Detail = Detail };
            receipt = current;
            return true;
        }

        // Parent calls before other NEW request processors and outside _busy, never from Receive.
        internal void PumpNativeWorldIntent()
        {
            if (!OnThread || _busy || _worldRetired || !HasPendingNativeWorldIntent) return;
            if (IsDisposed || IsFaulted) { RetireNativeWorldIntent(); return; }
            try
            {
                if (!IsArmed || _bridge?.IsGameplayActive != true || !Current())
                { FailWorldIntent("World intent lost its exact active actor lifetime."); return; }
                if (_authority != null && _worldServerReply.Kind == 0)
                {
                    // Retain the request while armour owns the canonical item records.
                    // A temporary reservation is not a rejection of this later intent.
                    if (_authority.HasPendingArmourCustody) return;
                    if (_worldRouter == null) RejectWorldIntent("No approved server reach/clearance route is bound.");
                    else
                    {
                        bool reserved;
                        _worldReserving = true;
                        try
                        {
                            reserved = _worldRouter.RouteWorldIntent(_bridge.Peer, _worldServerRequest,
                                out _worldIntentTransfer, out var detail);
                            if (!reserved && !_authority.IsFaulted && _worldIntentTransfer == null) RejectWorldIntent(detail);
                        }
                        finally { _worldReserving = false; }
                        if (!reserved && (_authority.IsFaulted || _worldIntentTransfer != null))
                        { FailWorldIntent("World reservation is uncertain; no second source action is allowed."); return; }
                        if (reserved)
                        {
                            _worldServerReply = _worldServerRequest with { Kind = PlayerWorldMessage.Reserved,
                                Transaction = _worldIntentTransfer.Action.Transaction };
                            _worldStarted = Stopwatch.GetTimestamp();
                            _worldSent = 0;
                        }
                    }
                }
                if (_authority != null && _worldServerReply.Kind == PlayerWorldMessage.Reserved)
                {
                    var state = _worldIntentTransfer.State;
                    if (state == InventoryStorage.TransferState.Unconfirmed)
                    { FailWorldIntent(_worldIntentTransfer.Detail); return; }
                    if (state is InventoryStorage.TransferState.Committed
                        or InventoryStorage.TransferState.Cancelled)
                    {
                        bool applied = state == InventoryStorage.TransferState.Committed;
                        if (_hasPending || LastCompletedTransaction != _worldServerReply.Transaction
                            || !WorldMutationReceiptMatches(_worldServerReply, applied))
                        { FailWorldIntent("World terminal state has no matching inventory receipt."); return; }
                        _worldServerReply = _worldServerReply with
                        {
                            Kind = applied ? PlayerWorldMessage.Applied : PlayerWorldMessage.Rejected,
                            ResultWorld = applied ? _worldIntentTransfer.WorldNetworkId : 0
                        };
                        _worldStarted = Stopwatch.GetTimestamp();
                        _worldSent = 0;
                    }
                }
                // The world receipt can arrive before the last native receipt on another channel.
                if (_authority == null && _worldSourceReply.Kind is PlayerWorldMessage.Applied or PlayerWorldMessage.Rejected)
                    TrySettleWorldSource();
                if (!HasPendingNativeWorldIntent) return;
                long now = Stopwatch.GetTimestamp();
                ulong progress = LastCompletedTransaction;
                if (progress != _worldProgress) { _worldProgress = progress; _worldStarted = now; }
                if (now - _worldStarted >= TimeoutTicks)
                { FailWorldIntent("World intent acknowledgement timed out; custody is held without replay or refund."); return; }
                if (_worldSent == 0 || now - _worldSent >= RetryTicks)
                {
                    _worldSent = now;
                    var frame = _authority == null
                        ? _worldSourceReply.Kind == PlayerWorldMessage.Settled ? _worldSourceReply : _worldSourceRequest
                        : _worldServerReply;
                    if (frame.Kind != 0) SendWorld(frame);
                }
            }
            catch (Exception error) { FailWorldIntent("World intent progression failed: " + error.Message); }
        }

        private void RejectWorldIntent(string detail)
        {
            _worldServerReply = _worldServerRequest with { Kind = PlayerWorldMessage.Rejected };
            _worldSent = 0;
            Detail = detail;
        }

        // Parent routes this before generic MPIV decoding, with no second datagram subscription.
        internal bool ReceiveNativeWorldIntent(Datagram message)
        {
            if (message == null || !PlayerWorldWire.IsFrame(message.Payload.Span)) return false;
            if (!OnThread || IsDisposed || _busy || _worldRetired || _bridge?.IsGameplayActive != true || !IsArmed
                || (_authority == null && _writerDepth != 0) || !ReferenceEquals(message.Peer, _bridge.Peer)
                || !PlayerWorldWire.TryRead(message.Payload.Span, out var frame)
                || frame.MigrationId != _baseline.Plan.Id || frame.Generation != _baseline.Plan.Generation
                || frame.NetworkId != _bridge.NetworkId || frame.Fingerprint != _baseline.Fingerprint) return true;
            _busy = true;
            try
            {
                if (!Current()) { FailWorldIntent("World endpoint lost its exact bridge identity."); return true; }
                if (_authority != null) ReceiveWorldServer(frame);
                else ReceiveWorldSource(frame);
            }
            catch (Exception error) { FailWorldIntent("World intent receive failed: " + error.Message); }
            finally { _busy = false; }
            return true;
        }

        private void ReceiveWorldServer(in PlayerWorldFrame frame)
        {
            if (frame.Kind == PlayerWorldMessage.Settled)
            {
                if (_worldServerReply.Kind is PlayerWorldMessage.Applied or PlayerWorldMessage.Rejected
                    && frame == (_worldServerReply with { Kind = PlayerWorldMessage.Settled }))
                {
                    _worldServerPending = false;
                    _worldIntentTransfer = null;
                    Detail = "Exact world action settled by both inventory endpoints.";
                    SendWorld(frame with { Kind = PlayerWorldMessage.Closed });
                }
                return;
            }
            if (frame.Kind != PlayerWorldMessage.Request || IsFaulted) return;
            if (frame.Request == _worldLastRequest)
            {
                if (!frame.SameRequest(_worldServerRequest))
                { FailWorldIntent("World request retry changed its immutable selection."); return; }
                if (_worldServerReply.Kind != 0) SendWorld(_worldServerReply);
                return;
            }
            if (frame.Request < _worldLastRequest || _worldServerPending) return;
            // Pending native work must finish before this request can reserve a transaction.
            // Receive itself owns _busy; do not use HasPendingCustody here.
            if (_hasPending || HasPendingIntake || HasPendingNativeCraftRequest || HasPendingNativeCraftPresentation
                || HasNativePowerCustody || _receivedStowRequest || _receivedEquipRequest
                || _authority.HasNativeCraftReservation || _authority.HasNativeConsumptionReservation
                || _authority.HasNativeDeathTransfer) return;
            _worldLastRequest = frame.Request;
            _worldServerRequest = frame;
            _worldServerReply = default;
            _worldServerPending = true;
            _worldIntentTransfer = null;
            _worldStarted = Stopwatch.GetTimestamp();
            _worldProgress = LastCompletedTransaction;
            _worldSent = 0;
        }

        private void ReceiveWorldSource(in PlayerWorldFrame frame)
        {
            if (!frame.SameRequest(_worldSourceRequest) || frame.Kind is PlayerWorldMessage.Request or PlayerWorldMessage.Settled) return;
            if (frame.Kind == PlayerWorldMessage.Closed)
            {
                if (_worldSourceReply.Kind == PlayerWorldMessage.Settled
                    && frame == (_worldSourceReply with { Kind = PlayerWorldMessage.Closed })) _worldSourcePending = false;
                return;
            }
            if (_worldSourceReply.Kind == PlayerWorldMessage.Settled)
            {
                if (frame.Kind is PlayerWorldMessage.Applied or PlayerWorldMessage.Rejected
                    && frame.Transaction == _worldSourceReply.Transaction && frame.ResultWorld == _worldSourceReply.ResultWorld)
                    SendWorld(_worldSourceReply);
                return;
            }
            if (!_worldSourcePending)
            {
                return;
            }
            if (_worldSourceReply.Kind != 0 && frame.Transaction != _worldSourceReply.Transaction)
            { FailWorldIntent("World reply changed its reserved transaction."); return; }
            if (frame.Kind == PlayerWorldMessage.Unconfirmed)
            { FailWorldIntent("Server reports unresolved world custody; native action remains fenced."); return; }
            if (_worldSourceReply.Kind is PlayerWorldMessage.Applied or PlayerWorldMessage.Rejected)
            {
                if (frame.Kind == PlayerWorldMessage.Reserved) return;
                if (frame != _worldSourceReply) FailWorldIntent("World reply changed its terminal result.");
                return;
            }
            if (_worldSourceReply.Kind == 0) _worldStarted = Stopwatch.GetTimestamp();
            _worldSourceReply = frame;
            if (frame.Kind == PlayerWorldMessage.Reserved)
                _worldIntentReceipt = new(frame.Request, NativeWorldIntentState.Reserved,
                    frame.Transaction, frame.Intent.World, "Exact server reservation pending native custody receipts.");
            // Settled only by Pump outside this receive callback's busy scope.
        }

        private void TrySettleWorldSource()
        {
            var frame = _worldSourceReply;
            bool applied = frame.Kind == PlayerWorldMessage.Applied;
            if (_hasIncomingGrant || _grantReplyQueued || SourceStowPending || SourceEquipPending) return;
            if (frame.Transaction != 0 && LastCompletedTransaction < frame.Transaction) return;
            if (!Unchanged() || (frame.Transaction != 0 && (LastCompletedTransaction != frame.Transaction
                || !WorldMutationReceiptMatches(frame, applied))) || !WorldSourceQuantityMatches(frame, applied))
            { FailWorldIntent("World completion disagrees with exact retained source custody."); return; }
            _worldSourceReply = frame with { Kind = PlayerWorldMessage.Settled };
            _worldStarted = Stopwatch.GetTimestamp();
            _worldSent = 0;
            Detail = applied ? "World source/destination and native inventory receipts agree."
                : "World action declined before mutation; source custody is unchanged.";
            _worldIntentReceipt = new(frame.Request, applied ? NativeWorldIntentState.Applied
                : NativeWorldIntentState.Rejected, frame.Transaction, frame.ResultWorld, Detail);
            SendWorld(_worldSourceReply);
        }

        private bool WorldMutationReceiptMatches(in PlayerWorldFrame frame, bool applied)
            => LastReceipt is { } receipt && receipt.Transaction == frame.Transaction
                && receipt.InstanceKey == (frame.Intent.IsGrant ? frame.Transaction : frame.Intent.Key)
                && receipt.ItemId == frame.Intent.ItemId && receipt.Quantity == 1 && receipt.IsGrant == frame.Intent.IsGrant
                && receipt.Applied == applied && !receipt.IsEquipmentStow && !receipt.IsEquipmentEquip
                && !receipt.IsPassiveProgression && !receipt.IsConsumption;

        private bool WorldSourceQuantityMatches(in PlayerWorldFrame frame, bool applied)
        {
            long quantity = 0;
            foreach (var item in _sourceItems)
            {
                if (frame.Intent.IsGrant)
                {
                    if (item.Entry.ItemId == frame.Intent.ItemId && Stowed(item.Entry)) quantity += item.Quantity;
                }
                else if (item.Entry.Key == frame.Intent.Key)
                {
                    if (item.Entry.ItemId != frame.Intent.ItemId || !Stowed(item.Entry)) return false;
                    quantity = item.Quantity;
                }
            }
            return quantity == (long)frame.Intent.BeforeQuantity + (applied ? frame.Intent.IsGrant ? 1 : -1 : 0);
        }

        private void SendWorld(in PlayerWorldFrame frame)
        {
            if (_bridge?.IsGameplayActive != true || _worldRetired) return;
            Span<byte> bytes = stackalloc byte[PlayerWorldWire.Length];
            PlayerWorldWire.Write(frame, bytes);
            var result = Datagrams.Send(_bridge.Peer, bytes);
            if (result is not (DatagramSendResult.Queued or DatagramSendResult.WindowFull))
                FailWorldIntent("World intent transport unavailable; custody remains held.");
        }

        private void FailWorldIntent(string reason)
        {
            _worldIntentTransfer?.HoldUnconfirmed(reason);
            if (_worldServerPending && _worldServerReply.Kind != PlayerWorldMessage.Unconfirmed)
            {
                _worldServerReply = _worldServerRequest with { Kind = PlayerWorldMessage.Unconfirmed,
                    Transaction = _worldIntentTransfer?.Action.Transaction ?? _worldServerReply.Transaction };
                if (!_worldRetired && _bridge?.IsGameplayActive == true)
                {
                    // Best effort only, without recursively faulting if transport itself failed.
                    try
                    {
                        Span<byte> bytes = stackalloc byte[PlayerWorldWire.Length];
                        PlayerWorldWire.Write(_worldServerReply, bytes);
                        Datagrams.Send(_bridge.Peer, bytes);
                    }
                    catch (Exception) { }
                }
            }
            if (_worldSourcePending)
                _worldIntentReceipt = new(_worldSourceRequest.Request, NativeWorldIntentState.Unconfirmed,
                    _worldSourceReply.Transaction, _worldSourceRequest.Intent.World, reason);
            Fail(reason);
        }

        // Parent calls before endpoint/authority teardown. No uncertain hold is automatically released.
        internal void RetireNativeWorldIntent()
        {
            if (!OnThread || _worldRetired) return;
            _worldRetired = true;
            if (HasPendingNativeWorldIntent) FailWorldIntent("World intent endpoint retired with unconfirmed settlement.");
            _worldRouter?.Dispose();
        }
    }
}
