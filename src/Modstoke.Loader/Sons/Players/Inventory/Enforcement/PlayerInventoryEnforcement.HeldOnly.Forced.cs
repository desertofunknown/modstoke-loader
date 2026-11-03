// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using TheForest.Items.Inventory;
using TheForest.Items.Special;
using Held = Modstoke.Sons.PlayerHeldOnlyAuthority;
using Route = Modstoke.Sons.PlayerHeldOnlyRoute;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        internal static ulong HeldOnlyIntentSequence => _heldIntentSequence;
        internal static HeldOnlyIntent PeekHeldOnlyIntent(HeldOnlySource source, ulong through)
            => ReferenceEquals(source, _heldOnlySource) && _heldIntent != null && _heldIntent.Sequence <= through ? _heldIntent : null;

        // LifeRoute owns the shared ordered prepare/execute/receipt exchange. These
        // endpoint calls never authorize a native life/water latch by themselves.
        internal bool TryRequestHeldOnlyForcedDrop(Held.MutationKind kind, ulong transition, out string detail)
        {
            detail = "The concrete held route is not bound; retain the life/water transition.";
            return HeldOnlyRoute != null && HeldOnlyRoute.TryRequestForcedDrop(kind, transition, out detail);
        }

        internal bool TryReadHeldOnlyForcedDropSettlement(Held.MutationKind kind, ulong transition, out string detail)
        {
            detail = "The concrete held route is not bound; empty observation alone is not forced-drop settlement.";
            return HeldOnlyRoute != null && HeldOnlyRoute.TryReadForcedDropSettlement(kind, transition, out detail);
        }

        // Invoke FIRST in the shared EquipPreviousLeftHandItem/RightHandItem prefix.
        // Returning true means this exact callback was consumed; skip its original.
        // It is not a general equipment permit and never calls TryEquip recursively.
        internal static bool TryHandleHeldOnlyPreviousEquipment(PlayerInventory inventory, bool left, bool option)
        {
            var source = _heldOnlySource;
            if (source == null || source.Inventory?.Pointer != inventory?.Pointer
                || _heldCall?.Source != source || source.Command == null
                || !Held.IsForcedDrop(source.Command.Kind)) return false;
            try
            {
                int bit = left ? 1 : 2;
                if (!source.Lifetime(true) || option || _heldCall.Kind != HeldHook.Remove
                    || _heldCall.Parent?.Kind != HeldHook.ForcedDrop || _heldCall.BeforeCount != 1
                    || source.Controller._heldCount != 0 || (source._forcedExpectedCalls & bit) == 0
                    || (source._forcedCalls & bit) != 0
                    || !left && (source._forcedCalls & 1) != (source._forcedExpectedCalls & 1)
                    || !source.ForcedHandIdentitiesCurrent())
                    throw new InvalidOperationException("Forced drop reached an unqualified previous-hand callback.");
                source._forcedCalls |= bit;
            }
            catch (Exception error) { source.Fault(error.Message); }
            return true;
        }

        public sealed partial class HeldOnlySource
        {
            private sealed class ForcedHand
            {
                internal Route.ForcedHandRequest Request;
                internal ItemInstance Item;
                internal Held.RepresentativeIdentity Identity;
                internal EquipmentIntent Equip;
                internal bool PreviousCleared;
            }

            private ForcedHand[] _forcedHands;
            private ItemInstance _forcedUnchangedLeft;
            private Held.RepresentativeIdentity _forcedUnchangedLeftIdentity;
            private IntPtr _forcedPreviousArray;
            internal int _forcedCalls, _forcedExpectedCalls;
            private bool _unownedForcedCallback;
            private ulong _forcedCallbackEpoch, _dispatchedForcedCallbackEpoch;
            internal bool HasUnownedForcedCallback => _unownedForcedCallback;
            internal bool ForcedCallbacksComplete => _forcedHands != null && _forcedCalls == _forcedExpectedCalls;
            internal bool ForcedEquipmentSettled
            {
                get
                {
                    if (!ForcedCallbacksComplete || !ForcedHandIdentitiesCurrent()) return false;
                    foreach (var hand in _forcedHands)
                    {
                        if (hand.Request.Slot == (int)EquipmentSlot.LeftHand && _forcedUnchangedLeft != null) continue;
                        if (Inventory._equipmentSlots[hand.Request.Slot]?.Pointer != hand.Item?.Pointer
                            || hand.Item != null && (hand.Equip?.ReadState() != EquipmentIntentState.Settled
                                || !hand.PreviousCleared)) return false;
                    }
                    return true;
                }
            }

            internal void NoteUnownedForcedCallback(HeldOnlyItemController controller)
            {
                if (controller?.Pointer == Controller.Pointer && controller._heldCount > 0)
                {
                    if (_forcedCallbackEpoch == ulong.MaxValue)
                    { Fault("Unowned forced callback epoch exhausted; retain its custody."); return; }
                    _forcedCallbackEpoch++;
                    _unownedForcedCallback = true;
                }
            }

            internal bool TryPrepareForcedRestore(out Route.ForcedHandRequest[] requests, out string detail)
            {
                requests = null;
                detail = "Forced drop requires exact previous hands and their existing equipment owners.";
                if (!Lifetime(true) || _forcedHands != null || Controller._heldCount <= 0
                    || Inventory.RightHandItem != null || Inventory._equipmentSlotsNext == null
                    || Inventory._equipmentSlotsNext.Length != 6
                    || Inventory._equipmentSlotsNext[(int)EquipmentSlot.LeftHand] != null
                    || Inventory._equipmentSlotsNext[(int)EquipmentSlot.RightHand] != null) return false;
                var previous = Inventory._equipmentSlotsPrevious;
                if (previous == null || previous.WasCollected || previous.Length != 6) return false;
                // RVA 30EB810 has a real fallback path when previous-right is absent
                // and Owns(402, null) is false. GetSingleInstance may create custody;
                // that fallback needs its own concrete owner, not an invented item.
                if (previous[(int)EquipmentSlot.RightHand] == null && !Inventory.Owns(402, null))
                { detail = "Native previous-right fallback has no concrete held equipment owner."; return false; }
                var hands = new ForcedHand[2];
                var currentLeft = Inventory.LeftHandItem;
                if (currentLeft != null && (currentLeft.WasCollected || !Live(currentLeft.Data)
                    || currentLeft.Data._requiresBothHands)) return false;
                for (int index = 0; index < hands.Length; index++)
                {
                    int slot = (int)(index == 0 ? EquipmentSlot.LeftHand : EquipmentSlot.RightHand);
                    var item = index == 0 && currentLeft != null ? null : previous[slot];
                    SourceItem selected = null;
                    if (item != null)
                    {
                        foreach (var candidate in InventoryOwner._sourceItems)
                        {
                            if (candidate.Entry.ItemId != item._itemID || !Stowed(candidate.Entry) || candidate.Quantity <= 0) continue;
                            if (selected != null || candidate.Instance?.Pointer != item.Pointer || candidate.Quantity != 1) return false;
                            selected = candidate;
                        }
                        if (selected == null || !SupportsHeldStow(item.Data) || (int)item.Data._equipmentSlot != slot
                            || item.Data._requiresBothHands || item.Data._firstLookRequiresBothHands
                            || !PlayerInventoryBaseline.MatchesInstance(selected.Entry, item)) return false;
                    }
                    hands[index] = new ForcedHand { Item = item, Identity = item == null ? null : new(item),
                        Request = new(slot, selected?.Entry.Key ?? 0, item?._itemID ?? 0,
                            selected == null ? null : PlayerInventoryWire.HashModules(selected.Entry.Modules)) };
                }
                if (!TryPrepareCompoundRestore(false, out detail)) return false;
                _forcedPreviousArray = previous.Pointer;
                _forcedUnchangedLeft = currentLeft;
                _forcedUnchangedLeftIdentity = currentLeft == null ? null : new(currentLeft);
                _forcedHands = hands;
                _forcedCalls = 0; _forcedExpectedCalls = currentLeft == null ? 3 : 2;
                requests = new[] { hands[0].Request, hands[1].Request };
                return true;
            }

            internal bool ForcedHandIdentitiesCurrent()
            {
                if (_forcedHands == null || Inventory._equipmentSlotsPrevious?.Pointer != _forcedPreviousArray) return false;
                if (_forcedHands[1].Item == null && !Inventory.Owns(402, null)) return false;
                if (_forcedUnchangedLeft != null && (_forcedUnchangedLeftIdentity?.Matches(_forcedUnchangedLeft) != true
                    || Inventory.LeftHandItem?.Pointer != _forcedUnchangedLeft.Pointer)) return false;
                foreach (var hand in _forcedHands)
                {
                    if (hand.Request.Slot == (int)EquipmentSlot.LeftHand && _forcedUnchangedLeft != null) continue;
                    if (Inventory._equipmentSlotsPrevious[hand.Request.Slot]?.Pointer != (hand.PreviousCleared ? null : hand.Item?.Pointer)
                        || hand.Item != null && hand.Identity?.Matches(hand.Item) != true) return false;
                }
                return true;
            }

            internal bool TryPumpForcedRestore(out ulong[] transactions, out string detail)
            {
                transactions = null;
                detail = FailureReason ?? "Forced previous hands await exact equipment receipts.";
                if (!ForcedCallbacksComplete || !TryCaptureReceipt(out _, out detail) || !ForcedHandIdentitiesCurrent()) return false;
                Span<ulong> completed = stackalloc ulong[_forcedHands.Length];
                completed.Clear();
                for (int index = 0; index < _forcedHands.Length; index++)
                {
                    var hand = _forcedHands[index];
                    if (hand.Item == null) continue;
                    var owner = InventoryOwner;
                    if (hand.Equip == null)
                    {
                        if (!owner._busy || !owner.Unchanged() || _writerDepth != 0 || owner.SourceEquipPending
                            || owner.SourceStowPending || owner._requestingEquip || owner._nativeEquipReserved
                            || owner._hasIncomingGrant || owner._intakePending || owner._nextEquipRequest == ulong.MaxValue
                            || !owner.TryReadNativeEquipSlot(hand.Item, hand.Request.Slot, out var slot)) return false;
                        var selected = owner._sourceItems.Find(item => item.Entry.Key == hand.Request.Key);
                        if (selected == null || !Stowed(selected.Entry) || selected.Quantity != 1
                            || selected.Instance?.Pointer != hand.Item.Pointer
                            || !PlayerInventoryBaseline.MatchesInstance(selected.Entry, hand.Item)) return false;
                        owner._nativeEquipRequest = new(owner._baseline.Plan.Id, owner._baseline.Plan.Generation,
                            Bridge.NetworkId, owner._baseline.Fingerprint, ++owner._nextEquipRequest, hand.Request.Key,
                            hand.Request.ItemId, 1, slot, PlayerInventoryMessage.EquipRequest, hand.Request.Modules);
                        owner._requestingEquip = true;
                        owner._equipRequestStarted = Stopwatch.GetTimestamp(); owner._equipRequestSent = 0;
                        hand.Equip = new(owner, hand.Item, owner._nativeEquipRequest, owner._lastReceived);
                        owner._equipmentIntent = hand.Equip;
                    }
                    if (hand.Equip.ReadState() != EquipmentIntentState.Settled)
                    {
                        owner.PumpNativeEquipRequest(); owner.PumpEquip();
                        if (owner.SourceEquipPending && Stopwatch.GetTimestamp() - owner._sent >= RetryTicks) owner.Send(owner._lastReply);
                    }
                    var state = hand.Equip.ReadState();
                    if (state is EquipmentIntentState.Refused or EquipmentIntentState.Unconfirmed)
                        throw new InvalidOperationException(hand.Equip.Detail);
                    if (state != EquipmentIntentState.Settled) return false;
                    if (hand.Equip.Receipt is not { Applied: true, IsEquipmentEquip: true } receipt
                        || Inventory._equipmentSlots[hand.Request.Slot]?.Pointer != hand.Item.Pointer)
                        throw new InvalidOperationException("Forced hand receipt disagrees with its native instance.");
                    completed[index] = receipt.Transaction;
                }
                var settledTransactions = completed.ToArray();
                // Both native previous-hand success branches clear their previous slot
                // (30EB710/256, 30EB810/864). Apply that exact memory result only AFTER
                // both real equipment owners release their native-state snapshots.
                foreach (var hand in _forcedHands)
                {
                    if (hand.Item == null || hand.PreviousCleared) continue;
                    if (Inventory._equipmentSlotsPrevious[hand.Request.Slot]?.Pointer != hand.Item.Pointer)
                        throw new InvalidOperationException("Previous-hand custody changed before its settled native memory write.");
                    Inventory._equipmentSlotsPrevious[hand.Request.Slot] = null;
                    hand.PreviousCleared = true;
                    if (Inventory._equipmentSlotsPrevious[hand.Request.Slot] != null)
                        throw new InvalidOperationException("Native previous-hand memory did not clear after settlement.");
                }
                transactions = settledTransactions;
                return true;
            }

            private bool ForcedFrameMatches(in PlayerInventoryFrame frame)
            {
                if (_forcedHands == null) return false;
                foreach (var hand in _forcedHands)
                {
                    if (hand.Equip == null) continue;
                    var request = hand.Equip.RequestFrame;
                    if (frame.Key != request.Key || frame.ItemId != request.ItemId || frame.Quantity != 1
                        || frame.BeforeQuantity != request.BeforeQuantity || frame.ModuleFingerprint != request.ModuleFingerprint
                        || frame.EquipmentOptions != 0) continue;
                    if (frame.Kind is PlayerInventoryMessage.EquipRequestAccepted or PlayerInventoryMessage.EquipRequestRejected)
                        return frame == (request with { Kind = frame.Kind });
                    if (hand.Equip.Receipt is { } settled)
                        return frame.Kind is PlayerInventoryMessage.EquipHeld or PlayerInventoryMessage.EquipCommitted
                            && frame.Transaction == settled.Transaction;
                    return frame.Kind is PlayerInventoryMessage.EquipHeld or PlayerInventoryMessage.EquipCommitted
                        && (InventoryOwner._sourceEquipFrame.Transaction > hand.Equip.ReceiptFloor
                            ? frame.Transaction == InventoryOwner._sourceEquipFrame.Transaction
                            : frame.Transaction > hand.Equip.ReceiptFloor);
                }
                return false;
            }

            private void ClearForcedEquipment()
            {
                _forcedHands = null; _forcedUnchangedLeft = null; _forcedUnchangedLeftIdentity = null;
                _forcedPreviousArray = IntPtr.Zero; _forcedCalls = _forcedExpectedCalls = 0;
            }
        }
    }
}
