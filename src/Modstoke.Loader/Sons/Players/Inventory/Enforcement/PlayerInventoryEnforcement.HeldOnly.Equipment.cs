// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using HeldAuthority = Modstoke.Sons.PlayerHeldOnlyAuthority;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        internal PlayerHeldOnlyRoute HeldOnlyRoute;
        private bool _heldOnlyEquipAdmission;

        // Add to CanQueue. Only the exact server equip request is admitted during
        // this short scope; source _busy remains held throughout the compound action.
        internal bool AllowsHeldOnlyQueue => HeldOnlyRoute?.HasPendingCustody != true
            || _heldOnlyEquipAdmission && _authority != null && _writerDepth == 0 && _receivedEquipRequest
                && HeldOnlyRoute.MatchesRestoreRequest(_serverEquipRequest);

        // Pump calls this instead of its other processors while the compound is pending.
        // Source progression is already driven by HeldOnlyRoute.Pump at that call site.
        internal void PumpHeldOnlyEquipment()
        {
            if (!OnThread || _busy || IsDisposed || IsFaulted || _authority == null
                || HeldOnlyRoute?.HasPendingCustody != true || !Current()) return;
            try
            {
                if (_receivedEquipRequest && HeldOnlyRoute.MatchesRestoreRequest(_serverEquipRequest))
                {
                    _heldOnlyEquipAdmission = true;
                    try { ProcessNativeEquipRequest(); }
                    finally { _heldOnlyEquipAdmission = false; }
                }
                if (!_hasPending) return;
                if (_pending.Kind is not (PlayerInventoryMessage.EquipHeld or PlayerInventoryMessage.EquipCommitted))
                    throw new InvalidOperationException("Unrelated inventory command entered held compound custody.");
                _busy = true;
                long now = Stopwatch.GetTimestamp();
                if (now - _started >= TimeoutTicks)
                    throw new InvalidOperationException("Compound equipment receipt timed out; custody retained.");
                if (_sent == 0 || now - _sent >= RetryTicks) Send(_pending);
            }
            catch (Exception error) { Fail(error.Message); }
            finally { _busy = false; }
        }

        // Call at the start of Receive, before its general _busy check. This endpoint
        // consumes only the exact existing equip protocol, without clearing _busy.
        internal bool ReceiveHeldOnlyEquipment(Datagram message)
        {
            var route = HeldOnlyRoute;
            if (route == null || !route.HasPendingCustody || message == null || !OnThread
                || !ReferenceEquals(message.Peer, _bridge?.Peer)) return false;
            // Consume other inventory protocol families here too. The datagram event
            // still reaches the independent held route; this is not a transport veto.
            if (!PlayerInventoryWire.TryRead(message.Payload.Span, out var frame, out _)) return true;
            if (frame.MigrationId != _baseline.Plan.Id || frame.Generation != _baseline.Plan.Generation
                || frame.NetworkId != _bridge.NetworkId || frame.Fingerprint != _baseline.Fingerprint) return true;
            if (IsDisposed || IsFaulted || !Current() || !IsArmed || !_bridge.IsGameplayActive
                || _writerDepth != 0 || _authority != null && _busy) return true;
            bool previousBusy = _busy;
            _busy = true;
            try
            {
                if (_authority != null)
                {
                    if (route.MatchesRestoreRequest(frame)) ReceiveNativeEquipRequest(frame);
                    else if (_hasPending && frame == (_pending with { Kind = frame.Kind })
                        && route.MatchesRestoreCommand(frame)) ReceiveServerEquip(frame);
                }
                else
                {
                    var source = _heldOnlySource;
                    if (source?.InventoryOwner != this || !source.CompoundFrameMatches(frame)) return true;
                    if (!ReceiveSourceEquip(frame)) ReceiveNativeEquipRequest(frame);
                }
            }
            catch (Exception error) { Fail("Compound held equipment receipt failed: " + error.Message); }
            finally { _busy = previousBusy; }
            return true;
        }

        internal bool HeldOnlyWorldGeometry(UnityEngine.Transform prefab, UnityEngine.Vector3 position)
            => OnThread && _authority != null && _worldRouter?.CheckDrop(prefab, position) == true;

        internal bool HeldOnlyServerReady(PlayerBridge bridge)
            => HeldOnlyServerBindingReady(bridge) && bridge.IsGameplayActive && _worldRouter != null && !_worldRetired;

        internal bool HeldOnlyServerBindingReady(PlayerBridge bridge)
            => ReferenceEquals(_bridge, bridge) && _authority != null
                && OnThread && !IsDisposed && !IsFaulted && IsArmed && Current() && _authority.CanQueueNativeMutation
                && !HasPendingCustody && !HasPendingNativeWorldIntent && !HasPendingNativeCraftPresentation
                && !_authority.HasPendingNativeCraftEffects;

        internal bool HeldOnlySourceReady => OnThread && CanQueue && !HasPendingCustody
            && !HasPendingNativeWorldIntent && !HasPendingNativeCraftPresentation;

        internal bool TryReadCompoundServerEquip(ulong key, int id, out PlayerInventoryReceipt receipt)
        {
            receipt = default;
            if (!OnThread || IsDisposed || IsFaulted || _authority == null || !Current()
                || LastReceipt is not { IsEquipmentEquip: true, Applied: true } current
                || current.InstanceKey != key || current.ItemId != id || current.Quantity != 1) return false;
            receipt = current;
            return true;
        }

        public sealed partial class HeldOnlySource
        {
            private ItemInstance _compoundPrevious;
            private HeldAuthority.RepresentativeIdentity _compoundPreviousIdentity;
            private ulong _compoundKey;
            private EquipmentIntent _compoundEquip;
            private bool _compoundPrepared;
            private bool _compoundRestoreRequested, _compoundPreviousCleared;
            private IntPtr _compoundPreviousArray;
            private bool CompoundRequestReady => _compoundPrepared && _inventoryBusyHeld && InventoryOwner._busy
                && Command == null && !InventoryOwner._hasPending && !InventoryOwner._hasIncomingGrant
                && !InventoryOwner._intakePending && !InventoryOwner.SourceEquipPending && !InventoryOwner.SourceStowPending
                && !InventoryOwner._requestingEquip && !InventoryOwner._requestingStow
                && !InventoryOwner._nativeEquipReserved && !InventoryOwner._nativeStowReserved
                && _writerDepth == 0 && InventoryOwner.IsArmed && InventoryOwner.Current();
            internal ulong CompoundKey => _compoundKey;
            internal int CompoundItemId => _compoundPrevious?._itemID ?? 0;
            internal string CompoundModules { get; private set; }
            internal bool CompoundEquipmentSettled => _forcedHands != null ? ForcedEquipmentSettled
                : !_compoundPrepared || CompoundPreviousCurrent() && (_compoundPrevious == null
                    || _compoundPreviousCleared && Inventory.RightHandItem?.Pointer == _compoundPrevious.Pointer
                        && _compoundEquip?.ReadState() == EquipmentIntentState.Settled);

            private bool CompoundPreviousCurrent()
                => !_compoundRestoreRequested || Inventory._equipmentSlotsPrevious?.Pointer == _compoundPreviousArray
                    && Inventory._equipmentSlotsPrevious[(int)EquipmentSlot.RightHand]?.Pointer
                        == (_compoundPreviousCleared ? null : _compoundPrevious?.Pointer)
                    && (_compoundPrevious == null ? Inventory.Owns(402, null)
                        : _compoundPreviousIdentity.Matches(_compoundPrevious));

            internal bool TryPrepareCompoundRestore(bool restore, out string detail)
            {
                detail = "Compound drop requires a module-free held donor and exact idle previous-hand custody.";
                if (!Lifetime(true) || Command != null || _compoundPrepared || !InventoryOwner.HeldOnlySourceReady
                    || Inventory._currentView != TheForest.Items.Inventory.PlayerInventory.PlayerViews.World) return false;
                var held = Controller._heldItemInstance;
                if (held == null || held.WasCollected || held._modules == null || held._modules.WasCollected
                    || held._modules.Count != 0) return false;
                if (restore)
                {
                    var previous = Inventory._equipmentSlotsPrevious;
                    if (previous == null || previous.WasCollected || previous.Length != 6 || Inventory.RightHandItem != null
                        || Inventory._equipmentSlotsNext == null || Inventory._equipmentSlotsNext.WasCollected
                        || Inventory._equipmentSlotsNext.Length != 6
                        || Inventory._equipmentSlotsNext[(int)EquipmentSlot.LeftHand] != null
                        || Inventory._equipmentSlotsNext[(int)EquipmentSlot.RightHand] != null) return false;
                    // Remove(equipPrevious:true) restores left before right. This ordinary
                    // path owns only right; do not silently omit a real left-hand restore.
                    if (Inventory.LeftHandItem == null && previous[(int)EquipmentSlot.LeftHand] != null)
                    { detail = "Last-item previous-left restoration needs the two-hand compound owner."; return false; }
                    if (Inventory.LeftHandItem != null && (!Live(Inventory.LeftHandItem.Data)
                        || Inventory.LeftHandItem.Data._requiresBothHands)) return false;
                    var item = previous[(int)EquipmentSlot.RightHand];
                    if (item == null && !Inventory.Owns(402, null))
                    { detail = "Native previous-right fallback has no concrete held equipment owner."; return false; }
                    if (item != null)
                    {
                        SourceItem selected = null;
                        foreach (var candidate in InventoryOwner._sourceItems)
                            if (candidate.Instance?.Pointer == item.Pointer && candidate.Quantity == 1 && Stowed(candidate.Entry))
                            { if (selected != null) return false; selected = candidate; }
                        if (selected == null || !SupportsHeldStow(item.Data)
                            || item.Data._requiresBothHands || item.Data._firstLookRequiresBothHands
                            || !PlayerInventoryBaseline.MatchesInstance(selected.Entry, item)) return false;
                        _compoundPrevious = item;
                        _compoundPreviousIdentity = new(item);
                        _compoundKey = selected.Entry.Key;
                        CompoundModules = PlayerInventoryWire.HashModules(selected.Entry.Modules);
                    }
                    _compoundPreviousArray = previous.Pointer;
                    _compoundRestoreRequested = true;
                }
                _compoundPrepared = true;
                InventoryOwner._busy = _inventoryBusyHeld = true;
                detail = "Exact previous hand retained; no equipment or inventory guard was opened.";
                return true;
            }

            internal bool TryPumpCompoundRestore(out PlayerInventoryReceipt? receipt, out string detail)
            {
                receipt = null;
                detail = FailureReason ?? "Compound equipment awaits its exact server/native receipt.";
                if (!_compoundPrepared || !TryCaptureReceipt(out _, out detail) || Command == null) return false;
                try
                {
                    if (!CompoundPreviousCurrent())
                        throw new InvalidOperationException("Previous-right custody changed during its compound drop.");
                    if (_compoundPrevious == null) return true;
                    if (!_compoundPreviousIdentity.Matches(_compoundPrevious) || Command.After.State.Count != 0)
                        throw new InvalidOperationException("Previous-hand identity changed or held load is not empty.");
                    var owner = InventoryOwner;
                    if (_compoundEquip == null)
                    {
                        if (!owner._busy || !owner.Unchanged() || _writerDepth != 0 || owner.SourceEquipPending
                            || owner.SourceStowPending || owner._requestingEquip || owner._nativeEquipReserved
                            || owner._hasIncomingGrant || owner._intakePending || owner._nextEquipRequest == ulong.MaxValue
                            || !owner.TryReadNativeEquipSlot(_compoundPrevious, (int)EquipmentSlot.RightHand, out var slot)) return false;
                        var selected = owner._sourceItems.Find(item => item.Entry.Key == _compoundKey);
                        if (selected == null || !Stowed(selected.Entry) || selected.Quantity != 1
                            || selected.Instance?.Pointer != _compoundPrevious.Pointer
                            || !PlayerInventoryBaseline.MatchesInstance(selected.Entry, _compoundPrevious)) return false;
                        owner._nativeEquipRequest = new(owner._baseline.Plan.Id, owner._baseline.Plan.Generation,
                            Bridge.NetworkId, owner._baseline.Fingerprint, ++owner._nextEquipRequest, _compoundKey,
                            selected.Entry.ItemId, 1, slot, PlayerInventoryMessage.EquipRequest, CompoundModules);
                        owner._requestingEquip = true;
                        owner._equipRequestStarted = Stopwatch.GetTimestamp();
                        owner._equipRequestSent = 0;
                        _compoundEquip = new(owner, _compoundPrevious, owner._nativeEquipRequest, owner._lastReceived);
                        owner._equipmentIntent = _compoundEquip;
                    }
                    // Only these accepted equipment phases advance while the outer held
                    // transaction keeps ordinary inventory busy, including during callbacks.
                    owner.PumpNativeEquipRequest();
                    owner.PumpEquip();
                    if (owner.SourceEquipPending && Stopwatch.GetTimestamp() - owner._sent >= RetryTicks)
                        owner.Send(owner._lastReply);
                    var state = _compoundEquip.ReadState();
                    if (state is EquipmentIntentState.Refused or EquipmentIntentState.Unconfirmed)
                        throw new InvalidOperationException(_compoundEquip.Detail);
                    if (state != EquipmentIntentState.Settled) return false;
                    receipt = _compoundEquip.Receipt;
                    if (receipt is not { Applied: true, IsEquipmentEquip: true }
                        || Inventory.RightHandItem?.Pointer != _compoundPrevious.Pointer) return false;
                    // Native EquipPreviousRightHandItem clears this slot on success.
                    // Its real equipment owner has now released its pre-action snapshot.
                    if (!_compoundPreviousCleared)
                    {
                        Inventory._equipmentSlotsPrevious[(int)EquipmentSlot.RightHand] = null;
                        _compoundPreviousCleared = true;
                        if (Inventory._equipmentSlotsPrevious[(int)EquipmentSlot.RightHand] != null)
                            throw new InvalidOperationException("Previous-right memory did not clear after equipment settlement.");
                    }
                    return true;
                }
                catch (Exception error) { Fault(error.Message); detail = FailureReason; return false; }
            }

            internal bool CompoundFrameMatches(in PlayerInventoryFrame frame)
            {
                if (Lifetime(true) && Command != null && HeldAuthority.IsForcedDrop(Command.Kind)) return ForcedFrameMatches(in frame);
                if (!Lifetime(true) || Command == null || _compoundEquip == null) return false;
                var request = _compoundEquip.RequestFrame;
                if (frame.Key != request.Key || frame.ItemId != request.ItemId || frame.Quantity != 1
                    || frame.BeforeQuantity != request.BeforeQuantity || frame.ModuleFingerprint != request.ModuleFingerprint
                    || frame.EquipmentOptions != 0) return false;
                return frame.Kind is PlayerInventoryMessage.EquipRequestAccepted or PlayerInventoryMessage.EquipRequestRejected
                    ? frame == (request with { Kind = frame.Kind })
                    : frame.Kind is PlayerInventoryMessage.EquipHeld or PlayerInventoryMessage.EquipCommitted
                        && (InventoryOwner._sourceEquipFrame.Transaction > _compoundEquip.ReceiptFloor
                            ? frame.Transaction == InventoryOwner._sourceEquipFrame.Transaction
                            : frame.Transaction > _compoundEquip.ReceiptFloor);
            }

            internal void CancelCompoundBeforeDispatch()
            { if (Command == null && _compoundEquip == null) { ReleaseInventoryBusy(); ClearCompoundEquipment(); } }

            private void ClearCompoundEquipment()
            {
                ClearForcedEquipment();
                _compoundPrevious = null; _compoundPreviousIdentity = null; _compoundKey = 0;
                _compoundEquip = null; _compoundPrepared = false; CompoundModules = null;
                _compoundPreviousArray = IntPtr.Zero; _compoundRestoreRequested = _compoundPreviousCleared = false;
            }
        }
    }
}
