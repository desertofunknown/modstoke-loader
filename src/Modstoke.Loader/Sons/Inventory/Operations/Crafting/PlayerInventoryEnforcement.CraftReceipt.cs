// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Inventory;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        internal void FailCraftMutation(string detail)
        {
            if (!OnThread || _authority == null)
                throw new InvalidOperationException("Stop native crafting only on its server inventory thread.");
            Fail(detail);
        }

        // Initialization may fail between modules. Keep every surviving module rooted and stop
        // its native updater without pretending the partially initialized result was delivered.
        internal void RetainUncertainCraftResult(ItemInstance item)
        {
            if (!OnThread || (_authority == null && (!ReferenceEquals(_guard, this) || !ProtectsInventory(_inventory)))
                || item == null || item.WasCollected
                || item._modules == null || item._modules.WasCollected)
                throw new InvalidOperationException("Uncertain native craft result modules are unavailable.");
            foreach (var module in item._modules)
            {
                if (module == null || module.WasCollected)
                    throw new InvalidOperationException("Uncertain native craft module disappeared.");
                _pausedModules.TryAdd(module.Pointer, module);
            }
            if (_authority != null)
                foreach (var module in item._modules) UnregisterDetachedModule(module);
        }

        // Only consult the receiver's authenticated, reconciled result. Send/queue success,
        // native callback occurrence and a matching transaction number alone are not receipts.
        internal bool TryReadCraftMutationReceipt(in PlayerInventoryFrame command,
            out bool completed, out string detail)
        {
            completed = false;
            detail = "Craft mutation has no exact current native receiver.";
            if (!OnThread || _busy || IsDisposed || IsFaulted || !IsArmed || _authority == null || !Current()
                || command.Kind is not (PlayerInventoryMessage.Remove or PlayerInventoryMessage.GrantPrepare)
                || command.MigrationId != _baseline.Plan.Id || command.Generation != _baseline.Plan.Generation
                || command.NetworkId != _bridge.NetworkId || command.Fingerprint != _baseline.Fingerprint) return false;
            if (LastCompletedTransaction < command.Transaction)
            {
                if (!_hasPending || command != (_pending with { Kind = command.Kind })
                    || (command.Kind == PlayerInventoryMessage.Remove ? _pending.Kind != PlayerInventoryMessage.Remove
                        : _pending.Kind is not (PlayerInventoryMessage.GrantPrepare or PlayerInventoryMessage.GrantPoll))) return false;
                detail = "Exact native ingredient/result receipt pending; no second write is authorized.";
                return true;
            }
            var expected = new PlayerInventoryReceipt(command.Transaction, command.Key,
                command.ItemId, command.Quantity, command.Kind == PlayerInventoryMessage.GrantPrepare, true);
            if (_hasPending || LastCompletedTransaction != command.Transaction || LastReceipt != expected)
            {
                detail = "Craft mutation was rejected, overtaken, or differs from its exact native receipt.";
                return false;
            }
            completed = true;
            detail = "Exact native custody mutation acknowledged by the authenticated receiver.";
            return true;
        }
    }
}
