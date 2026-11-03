// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using TheForest.Items.Inventory;

namespace Modstoke.Sons
{
    public enum EquipmentIntentSubmission { Native, Deferred, Submitted, Refused, Unconfirmed }
    public enum EquipmentIntentState { Pending, Accepted, Settled, Refused, Unconfirmed }

    public sealed partial class PlayerInventoryEnforcement
    {
        /// <summary>
        /// One exact source request, not permission to mutate inventory. Poll on the game thread.
        /// Accepted still owns pending custody; only Settled confirms the existing server transaction.
        /// Dropping this handle does not cancel, refund or repeat that transaction.
        /// </summary>
        public sealed class EquipmentIntent
        {
            internal readonly PlayerInventoryEnforcement Owner;
            internal readonly PlayerInventoryFrame RequestFrame;
            internal readonly ulong ReceiptFloor;
            internal EquipmentIntentState StateValue;

            public ItemInstance Item { get; }
            public int ItemId => RequestFrame.ItemId;
            public ulong InstanceKey => RequestFrame.Key;
            public ulong Request => RequestFrame.Transaction;
            public EquipmentSlot Slot => (EquipmentSlot)RequestFrame.BeforeQuantity;
            public bool IsStow => RequestFrame.Kind == PlayerInventoryMessage.StowRequest;
            public PlayerInventoryReceipt? Receipt { get; internal set; }
            public string Detail { get; internal set; } = "Exact equipment request awaits server acceptance.";

            internal EquipmentIntent(PlayerInventoryEnforcement owner, ItemInstance item,
                in PlayerInventoryFrame request, ulong receiptFloor)
            {
                Owner = owner;
                Item = item;
                RequestFrame = request;
                ReceiptFloor = receiptFloor;
            }

            public EquipmentIntentState ReadState()
            {
                if (!Owner.OnThread) throw new InvalidOperationException("Read equipment intent on its game thread.");
                try { Owner.RefreshEquipmentIntent(this); }
                catch (Exception error)
                {
                    StateValue = EquipmentIntentState.Unconfirmed;
                    Detail = "Equipment receipt lost its native source lifetime: " + error.Message;
                }
                return StateValue;
            }

            internal bool Terminal => StateValue is EquipmentIntentState.Settled
                or EquipmentIntentState.Refused or EquipmentIntentState.Unconfirmed;
        }

        private EquipmentIntent _equipmentIntent;

        /// <summary>Includes retained/faulted and lifecycle-held inventories; never a device exemption.</summary>
        public static bool IsEquipmentAuthorityHeld(PlayerInventory inventory) => ProtectsInventory(inventory);

        /// <summary>
        /// Submit an exact stored instance, or resolve one unambiguous retained instance by item ID.
        /// Native means no authority guard owns this inventory: the caller retains its original native path.
        /// Deferred has submitted nothing. Submitted must not be retried, including after an uncertain result.
        /// </summary>
        public static EquipmentIntentSubmission RequestEquipmentEquip(PlayerInventory inventory,
            ItemInstance instance, int itemId, out EquipmentIntent intent, out string detail)
            => RequestEquipmentIntent(inventory, instance, itemId, false, out intent, out detail);

        /// <summary>Request storage of this exact held item through the existing server stow transaction.</summary>
        public static EquipmentIntentSubmission RequestEquipmentStow(PlayerInventory inventory,
            ItemInstance instance, out EquipmentIntent intent, out string detail)
            => RequestEquipmentIntent(inventory, instance,
                instance == null || instance.WasCollected ? 0 : instance._itemID, true, out intent, out detail);

        private static EquipmentIntentSubmission RequestEquipmentIntent(PlayerInventory inventory,
            ItemInstance instance, int itemId, bool stow, out EquipmentIntent intent, out string detail)
        {
            intent = null;
            detail = "Equipment intent requires the exact live source inventory and item.";
            if (inventory == null || inventory.WasCollected || !inventory || itemId <= 0)
                return EquipmentIntentSubmission.Refused;
            if (!ProtectsInventory(inventory)) return EquipmentIntentSubmission.Native;
            var owner = _guard;
            if (owner == null || owner._inventory?.Pointer != inventory.Pointer
                || IsNativeInventoryLifecycleHeld(inventory))
            { detail = "Equipment ingress is held by the player lifecycle."; return EquipmentIntentSubmission.Deferred; }
            if (!owner.OnThread || owner.IsDisposed || owner.IsFaulted || owner._authority != null)
            { detail = owner.Detail; return EquipmentIntentSubmission.Refused; }
            try
            {
                owner.RefreshEquipmentIntent();
                if (!owner.Current()) return EquipmentIntentSubmission.Refused;
                if (owner._equipmentIntent?.StateValue == EquipmentIntentState.Unconfirmed)
                { detail = owner._equipmentIntent.Detail; return EquipmentIntentSubmission.Refused; }
                if (_writerDepth != 0 || owner._busy || !owner.IsArmed || owner._bridge?.IsGameplayActive != true
                    || owner.HasPendingCustody || owner.HasPendingNativeCraftPresentation
                    || owner._equipmentIntent is { Terminal: false }
                    || PlayerLifecycle.IsInventoryIngressFenced(inventory, out _))
                { detail = "Equipment waits for the current authority transaction or lifecycle fence."; return EquipmentIntentSubmission.Deferred; }
                SourceItem selected = null;
                foreach (var candidate in owner._sourceItems)
                {
                    if (candidate.Quantity != 1 || candidate.Entry.ItemId != itemId
                        || candidate.Instance == null || candidate.Instance.WasCollected
                        || (instance != null && (instance.WasCollected || candidate.Instance.Pointer != instance.Pointer))
                        || (stow ? !NativeHandSlot(candidate.Entry.EquipmentSlot) : !Stowed(candidate.Entry))) continue;
                    if (selected != null)
                    { detail = "Item ID does not identify one exact retained equipment instance."; return EquipmentIntentSubmission.Refused; }
                    selected = candidate;
                }
                if (selected == null || (stow && instance == null) || !SupportsHeldStow(selected.Instance.Data))
                    return EquipmentIntentSubmission.Refused;
                ulong floor = owner._lastReceived;
                ulong previousRequest = stow ? owner._nextStowRequest : owner._nextEquipRequest;
                // Enter the ordinary guarded hooks. They alone capture requests, at real writer depth;
                // no permit, synthetic transaction, authority callback or local custody write is added here.
                if (stow)
                    inventory.UnequipItemAtSlot((EquipmentSlot)selected.Entry.EquipmentSlot, false, true, false, false, false, false);
                else
                    inventory.TryEquip(selected.Instance, false, false, new Il2CppSystem.Nullable<EquipmentSlot>(), false, false);
                var request = stow ? owner._nativeStowRequest : owner._nativeEquipRequest;
                if (request.Transaction <= previousRequest || request.Key != selected.Entry.Key || request.ItemId != itemId
                    || !(stow ? owner._requestingStow : owner._requestingEquip))
                { detail = "The guarded equipment path declined this request before submission."; return EquipmentIntentSubmission.Refused; }
                intent = new EquipmentIntent(owner, selected.Instance, in request, floor);
                owner._equipmentIntent = intent;
                detail = intent.Detail;
                return EquipmentIntentSubmission.Submitted;
            }
            catch (Exception error)
            {
                owner.Fail("Equipment intent could not retain exact request custody: " + error.Message);
                detail = owner.Detail;
                return EquipmentIntentSubmission.Unconfirmed;
            }
        }

        // Also called at shared receipt boundaries so subsequent vanilla transactions cannot
        // overwrite LastReceipt before Client's next frame. Client polling uses the same path.
        internal void RefreshEquipmentIntent()
        {
            _equipmentIntent?.ReadState();
        }

        private void RefreshEquipmentIntent(EquipmentIntent intent)
        {
            if (!OnThread || intent.Terminal) return;
            var request = intent.RequestFrame;
            var currentRequest = intent.IsStow ? _nativeStowRequest : _nativeEquipRequest;
            var command = intent.IsStow ? _sourceStowFrame : _sourceEquipFrame;
            bool commandMatches = command.Transaction > intent.ReceiptFloor
                && command.MigrationId == request.MigrationId && command.Generation == request.Generation
                && command.NetworkId == request.NetworkId && command.Fingerprint == request.Fingerprint
                && command.Key == request.Key && command.ItemId == request.ItemId && command.Quantity == request.Quantity
                && command.BeforeQuantity == request.BeforeQuantity && command.ModuleFingerprint == request.ModuleFingerprint
                && command.EquipmentOptions == request.EquipmentOptions;
            if (commandMatches && LastReceipt is { } receipt && receipt.Transaction == command.Transaction
                && receipt.InstanceKey == intent.InstanceKey && receipt.ItemId == intent.ItemId && receipt.Quantity == 1
                && !receipt.IsGrant && (intent.IsStow ? receipt.IsEquipmentStow : receipt.IsEquipmentEquip))
            {
                intent.Receipt = receipt;
                intent.StateValue = receipt.Applied ? EquipmentIntentState.Settled : EquipmentIntentState.Refused;
                intent.Detail = receipt.Applied ? "Exact native custody and server equipment publication settled."
                    : "Equipment was refused before the reserved native mutation.";
                return;
            }
            if (IsDisposed || IsFaulted || !ReferenceEquals(_guard, this) || !Current()
                || intent.Item == null || intent.Item.WasCollected || intent.Item._itemID != intent.ItemId
                || currentRequest != request)
            {
                intent.StateValue = EquipmentIntentState.Unconfirmed;
                intent.Detail = "Equipment settlement is unconfirmed; no local retry or compensation is authorized. " + Detail;
                return;
            }
            bool requesting = intent.IsStow ? _requestingStow : _requestingEquip;
            bool reserved = intent.IsStow ? _nativeStowReserved : _nativeEquipReserved;
            if (commandMatches && !requesting && !reserved && !(intent.IsStow ? SourceStowPending : SourceEquipPending))
            {
                intent.StateValue = EquipmentIntentState.Unconfirmed;
                intent.Detail = "The equipment operation ended without a retained matching settlement receipt.";
            }
            else if (commandMatches || (reserved && !requesting))
            {
                intent.StateValue = EquipmentIntentState.Accepted;
                intent.Detail = "Server accepted this exact equipment intent; settlement is pending.";
            }
            else if (!requesting)
            {
                intent.StateValue = EquipmentIntentState.Refused;
                intent.Detail = "Server declined this exact equipment request without changing custody.";
            }
        }
    }
}
