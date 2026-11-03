// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Crafting;
using global::Sons.Inventory;

namespace Modstoke.Sons
{
    public sealed partial class ModuleStorageContainer
    {
        /// <summary>Stages the exact native item from a completed withdrawal on this container and world.</summary>
        /// <remarks>
        /// Supply its actual visible inventory group and row after native inventory entry;
        /// pending or unconfirmed operations cannot stage items. Uses the existing native
        /// selection/transfer path once, not a direct mat insertion or grant. A false result
        /// after Attempted must not be automatically retried or compensated. Use
        /// Crafting.TryBeginCurrentCraft for the native mat's recipe, and
        /// Crafting.TryReturnIngredient to return an ingredient before depositing it.
        /// </remarks>
        public bool TryStageWithdrawnIngredient(ModuleStorageOperation operation,
            InventoryLayoutItemGroup group, InventoryLayoutItem source,
            out CraftingTransferResult result)
        {
            result = null;
            bool nativeCallEntered = false;
            try
            {
                if (!CanBegin(out var detail))
                    return RefuseStage(0, detail, out result);
                if (operation == null || !ReferenceEquals(operation.Container, this) || operation.Deposit
                    || operation.Epoch != ModuleStorage.CurrentEpoch
                    || operation.State != ModuleStorageOperationState.Completed)
                    return RefuseStage(0, "Staging requires a completed withdrawal belonging to this container and current world.", out result);
                var item = operation.Item;
                if (item is null || item.WasCollected || !Crafting.CraftExecutionLive(source)
                    || source.ItemInstance is not { } displayed || displayed.WasCollected || displayed.Pointer != item.Pointer)
                    return RefuseStage(0, "The visible source must contain the exact withdrawn native instance.", out result);
                nativeCallEntered = true;
                return Crafting.TryStageIngredient(group, source, out result);
            }
            catch (Exception error)
            {
                if (nativeCallEntered)
                {
                    result = new CraftingTransferResult(CraftingTransferKind.StageIngredient,
                        true, true, 0, CraftingExecutionOutcome.Unconfirmed, null, false,
                        "Native staging escaped without a receipt. Do not retry or compensate. " + error.Message);
                    return false;
                }
                return RefuseStage(0, error.Message, out result);
            }
        }

        /// <summary>Collects a unique native result and submits that exact instance for deposit.</summary>
        /// <remarks>
        /// Collection uses native StashToInventory. This is two operations, not
        /// an atomic craft/store transaction. A refused deposit leaves the collected item in
        /// native inventory; an attempted, unconfirmed deposit must never be retried/refunded.
        /// True may return a Pending host-approved transfer, not completed storage custody.
        /// Collection failure returns no storage operation and never fabricates a replacement.
        /// </remarks>
        public bool TryCollectAndDeposit(CraftingResultLayoutItem source,
            out ModuleStorageOperation operation, out string detail)
        {
            var receipt = CollectAndDepositWithReceipt(source);
            operation = receipt.Operation;
            detail = receipt.Detail;
            return receipt.DepositSubmitted;
        }

        /// <summary>Collects one native result and reports its separate module deposit submission.</summary>
        /// <remarks>
        /// Uses the Unity-thread and local-owner checks of the native crafting and storage APIs.
        /// Collection and deposit are not atomic. A confirmed collection can have no deposit,
        /// and a submitted deposit can remain Pending. Never automatically retry, refund or
        /// replace an attempted or unconfirmed transfer. The receipt describes this call;
        /// observe Operation for later storage completion, not Collection.TransferredInstance.
        /// </remarks>
        public ModuleStorageCraftingReceipt CollectAndDepositWithReceipt(CraftingResultLayoutItem source)
        {
            CraftingTransferResult collection = null;
            ModuleStorageOperation operation = null;
            int itemId = 0;
            bool collectionCallEntered = false;
            bool exactInstanceConfirmed = false;
            bool depositCallEntered = false;
            bool depositSubmitted = false;
            string detail = "Collection could not be prepared.";

            ModuleStorageCraftingReceipt Receipt()
            {
                var transfer = collection ?? new CraftingTransferResult(CraftingTransferKind.CollectResult,
                    collectionCallEntered, false, itemId, collectionCallEntered
                        ? CraftingExecutionOutcome.Unconfirmed : CraftingExecutionOutcome.Refused,
                    null, false, detail);
                return new ModuleStorageCraftingReceipt(transfer, collection != null,
                    exactInstanceConfirmed, depositCallEntered, depositSubmitted, operation, detail);
            }

            try
            {
                if (!CanBegin(out detail)) return Receipt();
                if (!Crafting.CraftExecutionLive(source) || !source.isActiveAndEnabled)
                {
                    detail = "A live visible native crafted result is required.";
                    return Receipt();
                }
                var item = source.ItemInstance;
                if (item is null || item.WasCollected || !Crafting.CraftExecutionLive(item.Data)
                    || !item.Data._eachInstanceIsUnique)
                {
                    detail = "Collection into module storage requires an actual unique native result instance.";
                    return Receipt();
                }
                itemId = item._itemID;
                if (!ItemModuleSnapshot.TryCapture(item, out _, out detail)) return Receipt();
                // Recheck after native snapshot callbacks, before invoking the real collection.
                if (!CanBegin(out detail)) return Receipt();
                if (source.ItemInstance?.Pointer != item.Pointer)
                {
                    detail = "The native result changed while preparing collection; nothing was collected.";
                    return Receipt();
                }
                collectionCallEntered = true;
                bool complete = Crafting.TryCollectCraftedResult(source, out collection);
                if (collection == null)
                {
                    detail = "Native collection returned without a receipt and is unconfirmed. No module deposit was submitted; do not retry or grant a replacement.";
                    return Receipt();
                }
                if (!complete || !collection.Completed)
                {
                    detail = collection.Detail + (collection.Attempted && !collection.Completed
                        ? " Collection is unconfirmed; do not retry or grant a replacement." : " No module deposit was submitted.");
                    return Receipt();
                }
                var actual = collection.TransferredInstance;
                if (!collection.ExactInstancePreserved || actual is null || actual.WasCollected || actual.Pointer != item.Pointer)
                {
                    detail = "Native collection did not retain the exact result identity. No deposit or replacement was submitted.";
                    return Receipt();
                }
                exactInstanceConfirmed = true;
                depositCallEntered = true;
                bool submitted = TryDeposit(actual, out operation, out detail);
                if (submitted && operation == null)
                {
                    detail = "Native collection completed; deposit returned without an operation and its submission is unconfirmed. Do not retry, refund or grant a replacement.";
                    return Receipt();
                }
                depositSubmitted = submitted;
                if (!submitted && (operation == null || operation.State == ModuleStorageOperationState.Refused))
                    detail = "Native collection completed; deposit was refused and no return to the mat was attempted. " + detail;
                return Receipt();
            }
            catch (Exception error)
            {
                detail = (collection?.Completed == true ? "Native collection completed. "
                    : collection?.Attempted == true || (collection == null && collectionCallEntered)
                        ? "Native collection is unconfirmed. " : "Collection was refused before native transfer. ")
                    + (depositCallEntered ? "Deposit submission or later storage custody is unconfirmed. "
                        : "No module deposit was submitted. ")
                    + error.Message + " No automatic retry, refund or replacement was performed.";
                return Receipt();
            }
        }

        private static bool RefuseStage(int itemId, string detail, out CraftingTransferResult result)
        {
            result = new CraftingTransferResult(CraftingTransferKind.StageIngredient,
                false, false, itemId, CraftingExecutionOutcome.Refused, null, false, detail);
            return false;
        }
    }

    /// <summary>Immutable collection and submission evidence; Operation remains a live storage receipt.</summary>
    public sealed class ModuleStorageCraftingReceipt
    {
        internal ModuleStorageCraftingReceipt(CraftingTransferResult collection, bool collectionReceiptAvailable,
            bool exactInstanceConfirmed, bool depositCallEntered, bool depositSubmitted,
            ModuleStorageOperation operation, string detail)
        {
            Collection = collection;
            CollectionReceiptAvailable = collectionReceiptAvailable;
            ExactInstanceConfirmed = exactInstanceConfirmed;
            DepositCallEntered = depositCallEntered;
            DepositSubmitted = depositSubmitted;
            Operation = operation;
            Detail = detail;
        }

        /// <summary>
        /// Always present. Without a native collection receipt, Attempted is conservatively true
        /// if the collection API was entered, and Outcome is Unconfirmed, not proof of native invocation.
        /// </summary>
        public CraftingTransferResult Collection { get; }
        /// <summary>False for preparation refusals and synthesized evidence after a missing collection receipt.</summary>
        public bool CollectionReceiptAvailable { get; }
        public bool CollectionConfirmed => Collection.Completed;
        /// <summary>The collected instance matched the original live result before deposit entry, not current custody.</summary>
        public bool ExactInstanceConfirmed { get; }
        /// <summary>TryDeposit was entered; this does not establish a native removal or accepted submission.</summary>
        public bool DepositCallEntered { get; }
        /// <summary>TryDeposit returned true with an operation; Pending is not completed storage custody.</summary>
        public bool DepositSubmitted { get; }
        /// <summary>May be null, including after unconfirmed submission; when present, inspect its current State.</summary>
        public ModuleStorageOperation Operation { get; }
        public string Detail { get; }
    }
}
