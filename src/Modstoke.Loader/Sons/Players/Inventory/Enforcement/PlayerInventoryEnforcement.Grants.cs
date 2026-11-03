// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;
using Il2CppInterop.Runtime;
using global::Sons.Inventory;
using global::Sons.Items.Core;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        private PlayerInventoryFrame _incomingGrant;
        private PlayerInventoryModuleState _incomingGrantModuleState;
        private bool _hasIncomingGrant, _grantReplyQueued;
        private bool _inventoryAddPermit, _managerAddPermit, _grantUnique;
        private int _grantDepth;
        private ItemInstance _grantNative;

        private bool ReceiveServerGrant(in PlayerInventoryFrame frame)
        {
            if (frame.Kind is not (PlayerInventoryMessage.GrantReady or PlayerInventoryMessage.GrantApplied
                or PlayerInventoryMessage.GrantRejected or PlayerInventoryMessage.GrantUnconfirmed)) return false;
            if (IsFaulted || !IsArmed || !_hasPending
                || _pending.Kind is not (PlayerInventoryMessage.GrantPrepare or PlayerInventoryMessage.GrantPoll)
                || frame != (_pending with { Kind = frame.Kind })) return true;
            if (frame.Kind == PlayerInventoryMessage.GrantReady)
            {
                if (_pending.Kind == PlayerInventoryMessage.GrantPoll) return true;
                // Advance before Send: duplicate readiness can never send the native event twice.
                _pending = _pending with { Kind = PlayerInventoryMessage.GrantPoll };
                _started = Stopwatch.GetTimestamp();
                _sent = 0;
                if (!_authority.DeliverEnforcedGrant(out var delivery)) Fail(delivery);
                else Detail = delivery;
                return true;
            }
            if (frame.Kind == PlayerInventoryMessage.GrantUnconfirmed)
            { Fail("Native grant is unconfirmed; no delivery retry, credit or refund is authorized."); return true; }
            if (!_authority.CompleteEnforcedGrant(frame, out var detail)) { Fail(detail); return true; }
            LastCompletedTransaction = frame.Transaction;
            LastReceipt = new(frame.Transaction, frame.Key, frame.ItemId, frame.Quantity, true,
                frame.Kind == PlayerInventoryMessage.GrantApplied);
            _hasPending = false;
            Detail = detail;
            return true;
        }

        private bool ReceiveSourceGrant(in PlayerInventoryFrame frame, PlayerInventoryModuleState moduleState)
        {
            if (frame.Kind is not (PlayerInventoryMessage.GrantPrepare or PlayerInventoryMessage.GrantPoll)) return false;
            if (moduleState == null) { Fail("Grant omitted authoritative supplemental module custody."); return true; }
            if ((_hasIncomingGrant && frame == (_incomingGrant with { Kind = frame.Kind })
                || _lastReply.Kind is PlayerInventoryMessage.GrantApplied or PlayerInventoryMessage.GrantRejected or PlayerInventoryMessage.GrantUnconfirmed
                    && frame == (_lastReply with { Kind = frame.Kind })) && moduleState != _incomingGrantModuleState)
            { Fail("Grant retry changed authoritative supplemental module custody."); return true; }
            if (_lastReply.Kind is PlayerInventoryMessage.GrantApplied or PlayerInventoryMessage.GrantRejected or PlayerInventoryMessage.GrantUnconfirmed
                && frame == (_lastReply with { Kind = frame.Kind }))
            { Send(_lastReply); return true; }
            if (_hasIncomingGrant && frame == (_incomingGrant with { Kind = frame.Kind }))
            { Send(_incomingGrant with { Kind = PlayerInventoryMessage.GrantReady }); return true; }
            if (frame.Kind != PlayerInventoryMessage.GrantPrepare || IsFaulted || !IsArmed
                || _intakePending || _hasIncomingGrant || SourceStowPending || SourceEquipPending || frame.Transaction <= _lastReceived) return true;
            _lastReceived = frame.Transaction;
            _incomingGrantModuleState = moduleState;
            _lastReply = frame with { Kind = PlayerInventoryMessage.GrantUnconfirmed };
            if (!Unchanged())
            { Fail("Source inventory changed before grant preparation."); Send(_lastReply); return true; }
            if (PlayerLifecycle.IsInventoryIngressFenced(_inventory, out _) || !GrantCapacity(frame, out _))
            {
                _lastReply = frame with { Kind = PlayerInventoryMessage.GrantRejected };
                LastCompletedTransaction = frame.Transaction;
                LastReceipt = new(frame.Transaction, frame.Key, frame.ItemId, frame.Quantity, true, false);
                Send(_lastReply);
                return true;
            }
            _incomingGrant = frame;
            _hasIncomingGrant = true;
            // A pending grant has no completion receipt yet.
            _lastReply = default;
            _started = Stopwatch.GetTimestamp();
            Send(frame with { Kind = PlayerInventoryMessage.GrantReady });
            return true;
        }

        private bool GrantCapacity(in PlayerInventoryFrame frame, out bool unique)
        {
            unique = false;
            if (frame.ItemId <= 0 || frame.ItemId > ServerInventory.MaxItemId
                || _sourceItems.Count >= PlayerInventoryBaseline.MaximumEntries) return false;
            var data = ItemDatabaseManager.ItemById(frame.ItemId);
            if (data == null || data.WasCollected || !data || data.Id != frame.ItemId
                || data._canNotBeStored || data._maxAmount < 1) return false;
            long total = 0, stowed = 0;
            foreach (var item in _sourceItems)
            {
                if (item.Entry.Key == frame.Key) return false;
                if (item.Quantity == 0 || item.Entry.ItemId != frame.ItemId) continue;
                total += item.Quantity;
                if (Stowed(item.Entry)) stowed += item.Quantity;
            }
            if (stowed != frame.BeforeQuantity || total >= data._maxAmount
                || _inventory.AmountOf(frame.ItemId, true, true) >= data._maxAmount) return false;
            if (!TryReadCraftPhysicalStoredCount(frame.ItemId, stowed, out long physical)) return false;
            if (_manager._itemsMap.TryGetValue(frame.ItemId, out var stored))
            {
                if (stored == null || stored.WasCollected || stored.Count != physical
                    || stored.Count == int.MaxValue || (stored.MaxCount >= 0 && stowed >= stored.MaxCount)) return false;
            }
            else if (physical != 0) return false;
            unique = data._eachInstanceIsUnique;
            return true;
        }

        // Called only by the qualified native PlayerAddItem callback. Never run its default
        // auto-equip/overflow-drop path against a retained inventory.
        private void AcceptNativeGrant(PlayerAddItem message)
        {
            if (!OnThread || _writerDepth != 1 || _busy || IsDisposed || IsFaulted || !IsArmed
                || !_hasIncomingGrant || !Current()) return;
            var connection = ControlTransport.Resolve(_bridge.Peer);
            if (connection == null || message.RaisedBy == null || message.RaisedBy.Pointer != connection.Pointer) return;
            _busy = true;
            var frame = _incomingGrant;
            try
            {
                var token = message.ItemInstance;
                if (message.ItemId != frame.ItemId || message.Amount != 1 || token == null || token.WasCollected
                    || IL2CPP.il2cpp_object_get_class(token.Pointer) != Il2CppClassPointerStore<ItemInstanceBoltDataToken>.NativeClassPtr)
                    throw new InvalidOperationException("Native grant does not match its prepared server transaction.");
                var received = token.Cast<ItemInstanceBoltDataToken>().ItemInstance;
                if (received == null || received.WasCollected || received._itemID != frame.ItemId)
                    throw new InvalidOperationException("Native grant instance is unavailable.");
                string modules = PlayerInventoryBaseline.CaptureModules(received);
                if (PlayerInventoryWire.HashModules(modules) != frame.ModuleFingerprint || !Unchanged())
                    throw new InvalidOperationException("Native grant modules or retained inventory differ from preparation.");
                if (InventoryFrozen)
                {
                    FinishSourceGrant(frame, PlayerInventoryMessage.GrantRejected,
                        "Prepared grant refused before addition because inventory lifecycle is frozen.");
                    return;
                }
                if (!GrantCapacity(frame, out bool unique))
                {
                    FinishSourceGrant(frame, PlayerInventoryMessage.GrantRejected,
                        "Grant refused before addition because native capacity is unavailable.");
                    return;
                }
                // A token's detached data is not inventory custody. Only a verified AddItem can establish it.
                var native = PlayerInventoryBaseline.RestoreInstance(new PlayerInventoryBaseline.Entry
                {
                    ItemId = frame.ItemId, Modules = modules,
                    ModuleState = _incomingGrantModuleState
                });
                if (!unique && native._modules.Count != 0)
                    throw new InvalidOperationException("Nonunique native storage cannot retain grant modules.");
                bool hadStorage = _manager._itemsMap.ContainsKey(frame.ItemId);
                _grantNative = native;
                _grantUnique = unique;
                _grantDepth = _writerDepth;
                _inventoryAddPermit = _managerAddPermit = true;
                bool added = _inventory.AddItem(frame.ItemId, 1, true, false, native);
                if (!added || _inventoryAddPermit || _managerAddPermit || !Current())
                    throw new InvalidOperationException("Native grant did not complete its exact authorized writer path.");
                var after = PlayerInventoryBaseline.CaptureRetained(_baseline.Plan);
                int index = FindStoredEntry(after, frame.ItemId, unique ? native : null);
                IntPtr bucket = hadStorage ? IntPtr.Zero : _manager._itemsMap[frame.ItemId].Pointer;
                if (index < 0 || (unique && (!PlayerInventoryBaseline.JsonEqual(modules, after.Entries[index].Modules)
                    || after.Entries[index].ModuleState != _incomingGrantModuleState))
                    || !after.ConfirmsRemoval(_currentSnapshot, index, 1, unique ? native : null, bucket))
                    throw new InvalidOperationException("Native grant changed custody beyond the one authorized addition.");
                if (unique) RetainPausedModules(native);
                _sourceItems.Add(new SourceItem(new PlayerInventoryBaseline.Entry
                {
                    Key = frame.Key, ItemId = frame.ItemId, Quantity = 1, Modules = unique ? modules : null,
                    ModuleState = unique ? PlayerInventoryBaseline.CaptureModuleState(native) : null
                }, unique ? native : null));
                _currentSnapshot = after;
                FinishSourceGrant(frame, PlayerInventoryMessage.GrantApplied,
                    "Exact native grant applied; all prior instances, stacks, modules and equipment preserved.");
            }
            catch (Exception error)
            {
                FinishSourceGrant(frame, PlayerInventoryMessage.GrantUnconfirmed, "Native grant is unconfirmed: " + error.Message);
                Fail(Detail);
            }
            finally
            {
                _inventoryAddPermit = _managerAddPermit = false;
                _grantNative = null;
                _busy = false;
            }
        }

        private void FinishSourceGrant(in PlayerInventoryFrame frame, PlayerInventoryMessage kind, string detail)
        {
            _hasIncomingGrant = false;
            _lastReply = frame with { Kind = kind };
            _grantReplyQueued = true;
            if (kind != PlayerInventoryMessage.GrantUnconfirmed)
            {
                LastCompletedTransaction = frame.Transaction;
                LastReceipt = new(frame.Transaction, frame.Key, frame.ItemId, frame.Quantity, true,
                    kind == PlayerInventoryMessage.GrantApplied);
            }
            Detail = detail;
        }

        private int FindStoredEntry(PlayerInventoryBaseline snapshot, int id, ItemInstance instance)
        {
            int ordinal = 0;
            if (instance != null)
            {
                if (instance.WasCollected || !_manager._itemsMap.TryGetValue(id, out var stored) || stored == null) return -1;
                ordinal = -1;
                for (int i = 0; i < stored.UniqueInstances.Count; i++)
                    if (stored.UniqueInstances[i]?.Pointer == instance.Pointer) { ordinal = i; break; }
                if (ordinal < 0) return -1;
            }
            for (int i = 0; i < snapshot.Entries.Count; i++)
            {
                var entry = snapshot.Entries[i];
                if (entry.ItemId != id || !Stowed(entry)) continue;
                if ((instance == null) != (entry.Modules == null)) return -1;
                if (ordinal-- == 0) return i;
            }
            return -1;
        }
    }
}
