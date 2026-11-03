// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        internal void FailPassive(string detail) => Fail(detail);

        private bool ReceiveServerPerishable(in PlayerInventoryFrame frame)
        {
            if (frame.Kind is not (PlayerInventoryMessage.PerishableApplied or PlayerInventoryMessage.PerishableUnconfirmed)) return false;
            if (IsFaulted || !IsArmed || !_hasPending || _pending.Kind != PlayerInventoryMessage.Perishable
                || frame != (_pending with { Kind = frame.Kind })) return true;
            if (frame.Kind == PlayerInventoryMessage.PerishableUnconfirmed)
            { Fail("Retained perishable readback is unconfirmed; no replay or client value adoption is authorized."); return true; }
            if (!_authority.CompletePassive(frame, out var detail)) { Fail(detail); return true; }
            LastCompletedTransaction = frame.Transaction;
            LastReceipt = new(frame.Transaction, frame.Key, frame.ItemId, 1, false, true) { IsPassiveProgression = true };
            _hasPending = false;
            Detail = detail;
            return true;
        }

        private bool ReceiveSourcePerishable(in PlayerInventoryFrame frame)
        {
            if (frame.Kind != PlayerInventoryMessage.Perishable) return false;
            if (_lastReply.Kind is PlayerInventoryMessage.PerishableApplied or PlayerInventoryMessage.PerishableUnconfirmed
                && frame == (_lastReply with { Kind = PlayerInventoryMessage.Perishable }))
            { Send(_lastReply); return true; }
            if (IsFaulted || !IsArmed || _intakePending || _hasIncomingGrant || SourceStowPending || SourceEquipPending || frame.Transaction <= _lastReceived) return true;
            // Do not replace a pending absolute snapshot, advance its ID, or emit an
            // unconfirmed receipt merely because the lifecycle is temporarily frozen.
            if (InventoryFrozen) return true;
            _lastReceived = frame.Transaction;
            _lastReply = frame with { Kind = PlayerInventoryMessage.PerishableUnconfirmed };
            try
            {
                if (!ApplyPerishable(frame)) throw new InvalidOperationException("The complete native inventory did not match the exact module projection.");
                _lastReply = frame with { Kind = PlayerInventoryMessage.PerishableApplied };
                LastCompletedTransaction = frame.Transaction;
                LastReceipt = new(frame.Transaction, frame.Key, frame.ItemId, 1, false, true) { IsPassiveProgression = true };
                Detail = "Native perishable state projected without replacing its item, modules or local inventory.";
            }
            catch (Exception error) { Fail("Perishable projection failed: " + error.Message); }
            Send(_lastReply);
            return true;
        }

        private bool ApplyPerishable(in PlayerInventoryFrame frame)
        {
            if (InventoryFrozen || !Unchanged()) return false;
            if (TryApplyCraftMatPerishable(frame, out bool craftApplied)) return craftApplied;
            SourceItem selected = null;
            foreach (var item in _sourceItems) if (item.Entry.Key == frame.Key) { selected = item; break; }
            if (selected == null || selected.Quantity != 1 || selected.Entry.ItemId != frame.ItemId
                || selected.Instance == null || selected.Instance.WasCollected) return false;
            int index = FindCurrentEntry(selected);
            if (index < 0) return false;
            string before = PlayerInventoryBaseline.CaptureModules(selected.Instance);
            if (PlayerInventoryWire.HashModules(before) != frame.BeforeModuleFingerprint) return false;
            var module = FindPerishable(selected.Instance);
            ProjectPerishable(module, frame);
            string modules = PlayerInventoryBaseline.CaptureModules(selected.Instance);
            if (PlayerInventoryWire.HashModules(modules) != frame.ModuleFingerprint || !Current()) return false;
            var after = PlayerInventoryBaseline.CaptureRetained(_baseline.Plan);
            if (!_currentSnapshot.ConfirmsModuleChange(after, index, modules)) return false;
            // Source entries may still reference the immutable approved baseline. Replace this
            // tracking entry, never mutate the originally approved observation or fingerprint.
            int source = _sourceItems.IndexOf(selected);
            var entry = selected.Entry;
            _sourceItems[source] = new SourceItem(new PlayerInventoryBaseline.Entry
            {
                Key = entry.Key, ItemId = entry.ItemId, Quantity = selected.Quantity, Modules = modules, ModuleState = entry.ModuleState,
                EquipmentSlot = entry.EquipmentSlot, ClothingSlots = entry.ClothingSlots,
                ArmourSlot = entry.ArmourSlot, ArmourPoints = entry.ArmourPoints, DefinitionOnly = entry.DefinitionOnly
            }, selected.Instance);
            _currentSnapshot = after;
            return true;
        }

        private int FindCurrentEntry(SourceItem item)
        {
            if (Stowed(item.Entry)) return FindStoredEntry(_currentSnapshot, item.Entry.ItemId, item.Instance);
            for (int i = 0; i < _currentSnapshot.Entries.Count; i++)
            {
                var entry = _currentSnapshot.Entries[i];
                if (entry.ItemId == item.Entry.ItemId && entry.EquipmentSlot == item.Entry.EquipmentSlot
                    && entry.ClothingSlots == item.Entry.ClothingSlots && entry.ArmourSlot == item.Entry.ArmourSlot) return i;
            }
            return -1;
        }
    }
}
