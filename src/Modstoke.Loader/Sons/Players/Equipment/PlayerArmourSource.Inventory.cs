// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using TheForest.Utils;
using Command = Modstoke.Sons.PlayerArmourRoute.Command;

namespace Modstoke.Sons
{
    // Narrow access to the existing source account, not another inventory ledger.
    // Shared pump/admission gates are integrated by its owner in the existing partials.
    public sealed partial class PlayerInventoryEnforcement
    {
        private PlayerArmourSource _armourSource;
        private Command _armourSourcePending;
        private SourceItem[] _armourSourceRecords, _armourSourceAfter;
        private int[] _armourSourceIndices;
        private Il2CppObjectBase[] _armourInventoryIdentities;
        private bool _armourSourceApplied;

        internal bool HasPendingArmourSourceCustody => _armourSourcePending != null;

        internal bool TryBindArmourSource(PlayerArmourSource source, PlayerBridge bridge)
        {
            if (source == null || _armourSource != null || bridge.IsGameplayActive || _busy || _writerDepth != 0
                || !TryReadStatProjectionActor(bridge, out _) || !Unchanged()) return false;
            _armourSource = source;
            return true;
        }

        internal bool ArmourSourceCurrent(PlayerArmourSource source)
            => OnThread && !IsDisposed && !IsFaulted && ReferenceEquals(source, _armourSource)
                && _authority == null && Current() && SourceLifetime();

        internal bool TryReserveArmourSource(PlayerArmourSource source, Command command)
        {
            if (!ArmourSourceCurrent(source) || !IsArmed || _busy || _writerDepth != 0 || _armourSourcePending != null
                || !ArmourSourceOtherExecutionIdle() || _bridge?.IsGameplayActive != true
                || command.Plan != _baseline.Plan || command.Actor != _bridge.NetworkId
                || command.InventoryFingerprint != _baseline.Fingerprint) return false;
            if (command.Numbers.BeforeInventoryTransaction != LastCompletedTransaction
                || _lastReceived != LastCompletedTransaction || !Unchanged())
                throw new InvalidOperationException("Source armour inventory transaction floor or retained snapshot changed.");
            var numbers = command.Numbers;
            var records = new SourceItem[numbers.ChangeCount];
            var replacements = new SourceItem[numbers.ChangeCount];
            var indices = new int[numbers.ChangeCount];
            for (int i = 0; i < numbers.Before.SlotCount; i++)
            {
                var slot = numbers.Before.GetSlot(i);
                int found = 0;
                foreach (var item in _sourceItems)
                {
                    if (item.Quantity == 0 || item.Entry.ArmourSlot != i) continue;
                    if (item.Entry.Key != slot.InventoryKey || item.Entry.ItemId != slot.ItemId || item.Quantity != 1
                        || !item.Entry.DefinitionOnly || item.Instance != null || item.Entry.Modules != null
                        || !Command.SameFloat(item.Entry.ArmourPoints, slot.RemainingPoints))
                        throw new InvalidOperationException("Reserved source armour record does not match the command's exact item identity/condition.");
                    found++;
                }
                if (found != (slot.IsEquipped ? 1 : 0))
                    throw new InvalidOperationException("Source armour slot does not have exactly its approved worn record.");
            }
            for (int i = 0; i < numbers.ChangeCount; i++)
            {
                var change = numbers.GetChange(i);
                int index = _sourceItems.FindIndex(item => item.Quantity != 0 && item.Entry.Key == change.Before.InventoryKey);
                if (index < 0) throw new InvalidOperationException("Source armour change lost its approved inventory key.");
                var item = _sourceItems[index]; records[i] = item; indices[i] = index;
                replacements[i] = new SourceItem(new PlayerInventoryBaseline.Entry
                {
                    Key = item.Entry.Key, ItemId = item.Entry.ItemId, Quantity = change.Broken ? 0 : 1,
                    ArmourSlot = change.Broken ? -1 : change.After.Index,
                    ArmourPoints = change.Broken ? 0 : change.After.RemainingPoints, DefinitionOnly = true
                }, null);
            }
            _armourInventoryIdentities = ReadArmourUnchangedIdentities();
            _armourSourceRecords = records; _armourSourceAfter = replacements; _armourSourceIndices = indices;
            _armourSourcePending = command;
            // Refuse before transaction/effects if the parent has not wired account-wide
            // custody gates. Never borrow _busy as an across-frame reservation.
            if (!HasPendingCustody || CanQueue || StatProjectionCanApply || !BlocksNativeWorldMutation)
            {
                _armourSourcePending = null;
                throw new InvalidOperationException("Wire HasPendingArmourSourceCustody into inventory, world and stat admission before armour execution.");
            }
            _armourSourceApplied = false;
            _lastReceived = numbers.Transaction;
            return true;
        }

        // Sent but not yet reserved requests have no custody. They wait behind this
        // server-ordered command; refusing them here would deadlock both peers.
        private bool ArmourSourceOtherExecutionIdle()
            => !_hasPending && !_hasIncomingGrant && !SourceStowPending && !SourceEquipPending
                && !_nativeStowReserved && !_nativeEquipReserved && !_intakePending && !HasNativePowerCustody
                && !HasPendingNativeCraftPresentation && (!_craftSourcePending || _craftSourceReply.Kind == 0)
                && (!_worldSourcePending || _worldSourceReply.Kind == 0)
                && _retainedHeldOnlySource?.Command == null
                && _retainedHeldOnlySource?.HasUnownedForcedCallback != true
                && HeldOnlyRoute?.HoldsForcedTransition != true;

        internal void ApplyArmourSourceRecords(PlayerArmourSource source, Command command)
        {
            if (!ArmourSourceCurrent(source) || !ReferenceEquals(command, _armourSourcePending) || _armourSourceApplied
                || _lastReceived != command.Numbers.Transaction || !ArmourInventoryIdentitiesMatch())
                throw new InvalidOperationException("Reserved source armour inventory changed during native effects.");
            for (int i = 0; i < _armourSourceIndices.Length; i++)
                if (!ReferenceEquals(_sourceItems[_armourSourceIndices[i]], _armourSourceRecords[i]))
                    throw new InvalidOperationException("Reserved worn source record was replaced.");
            for (int i = 0; i < _armourSourceIndices.Length; i++)
                _sourceItems[_armourSourceIndices[i]] = _armourSourceAfter[i];
            _armourSourceApplied = true;
            // The strict snapshot cannot advance until the server publishes its broken IDs.
            // It remains retained under custody, not silently recaptured or used for admission.
        }

        internal bool TryCompleteArmourSource(PlayerArmourSource source, Command command)
        {
            if (!ArmourSourceCurrent(source) || !ReferenceEquals(command, _armourSourcePending)
                || !_armourSourceApplied || _lastReceived != command.Numbers.Transaction || !ArmourInventoryIdentitiesMatch())
                throw new InvalidOperationException("Source armour publication lost its exact inventory participant.");
            var state = LocalPlayer.Entity.GetState<IPlayerState>();
            bool waiting = false;
            if (state?.PlayerArmourIds == null || state.PlayerArmourIds.Length != _currentSnapshot.Armour.Length)
                throw new InvalidOperationException("Source armour publication layout changed.");
            for (int i = 0; i < state.PlayerArmourIds.Length; i++)
            {
                int expected = i < command.Numbers.After.SlotCount ? command.Numbers.After.GetSlot(i).ItemId : 0;
                int actual = state.PlayerArmourIds[i];
                if (actual == expected) continue;
                if (actual != _currentSnapshot.Armour[i]) throw new InvalidOperationException("Unrelated armour publication entered the reserved transaction.");
                waiting = true;
            }
            if (waiting) return false;
            var after = PlayerInventoryBaseline.CaptureRetained(_baseline.Plan);
            if (!ArmourInventoryIdentitiesMatch() || !ConfirmsArmourSnapshot(_currentSnapshot, after, command))
                throw new InvalidOperationException("Armour snapshot changed unrelated native inventory custody.");
            _currentSnapshot = after;
            LastCompletedTransaction = command.Numbers.Transaction;
            // No single-item receipt can describe a multi-slot batch. The armour route owns it.
            LastReceipt = null;
            _armourSourcePending = null;
            Detail = "Native armour effects and exact source inventory/publication reconciled; full combat is separate.";
            return true;
        }

        internal void FailArmourSource(PlayerArmourSource source, string detail)
        {
            if (OnThread && ReferenceEquals(source, _armourSource)) Fail("Armour source retained without replay: " + detail);
        }

        private static bool ConfirmsArmourSnapshot(PlayerInventoryBaseline before,
            PlayerInventoryBaseline after, Command command)
        {
            if (before.Plan != after.Plan || !before.Held.AsSpan().SequenceEqual(after.Held)
                || !before.Clothing.AsSpan().SequenceEqual(after.Clothing)) return false;
            int next = 0;
            foreach (var entry in before.Entries)
            {
                if (entry.ArmourSlot >= 0 && !command.Numbers.After.GetSlot(entry.ArmourSlot).IsEquipped) continue;
                if (next >= after.Entries.Count) return false;
                var current = after.Entries[next++];
                float points = entry.ArmourSlot >= 0 ? command.Numbers.After.GetSlot(entry.ArmourSlot).RemainingPoints : entry.ArmourPoints;
                // Capture keys are positional; retained SourceItems keep the approved keys.
                if (entry.ItemId != current.ItemId || entry.Quantity != current.Quantity
                    || entry.EquipmentSlot != current.EquipmentSlot || entry.ClothingSlots != current.ClothingSlots
                    || entry.ArmourSlot != current.ArmourSlot || entry.DefinitionOnly != current.DefinitionOnly
                    || !Command.SameFloat(points, current.ArmourPoints) || entry.ModuleState != current.ModuleState
                    || (entry.Modules == null ? current.Modules != null : current.Modules == null
                        || !JsonNode.DeepEquals(JsonNode.Parse(entry.Modules), JsonNode.Parse(current.Modules)))) return false;
            }
            return next == after.Entries.Count;
        }

        private bool ArmourInventoryIdentitiesMatch()
        {
            var current = ReadArmourUnchangedIdentities();
            if (_armourInventoryIdentities == null || current.Length != _armourInventoryIdentities.Length) return false;
            for (int i = 0; i < current.Length; i++)
            {
                var original = _armourInventoryIdentities[i];
                var retained = current[i];
                if (original == null || retained == null)
                {
                    if (original != null || retained != null) return false;
                    continue;
                }
                if (original.WasCollected || retained.WasCollected || original.Pointer != retained.Pointer) return false;
            }
            return true;
        }

        // Same structural custody covered by baseline capture, excluding the armour
        // definition references that this transaction intentionally destroys/removes.
        private Il2CppObjectBase[] ReadArmourUnchangedIdentities()
        {
            var clothing = LocalPlayer.ClothingSystem;
            var list = new List<Il2CppObjectBase> { LocalPlayer.GameObject, _inventory, _manager, _manager._itemsMap,
                _inventory._equipmentSlots, _inventory._equipmentSlotsNext, _inventory._equipmentSlotsIds,
                clothing, clothing._clothingHookupData, LocalPlayer.Stats._armourSystem };
            void Instance(ItemInstance item)
            {
                list.Add(item); list.Add(item._modules);
                foreach (var module in item._modules) list.Add(module);
            }
            var ids = new List<int>(); foreach (int id in _manager._itemsMap.Keys) ids.Add(id); ids.Sort();
            foreach (int id in ids)
            {
                var stored = _manager._itemsMap[id];
                var unique = stored.UniqueInstances;
                // Retain the absent-list slot too; null and an allocated empty list are not the same identity.
                list.Add(stored); list.Add(unique); list.Add(ItemDatabaseManager.ItemById(id));
                if (unique != null) foreach (var item in unique) Instance(item);
            }
            foreach (var item in _inventory._equipmentSlots) if (item != null) Instance(item);
            foreach (var hookup in clothing._clothingHookupData)
            {
                list.Add(hookup);
                if (hookup.ClothingPiece != null) list.Add(hookup.ClothingPiece);
                if (hookup.ItemInstance != null) Instance(hookup.ItemInstance);
            }
            return list.ToArray();
        }
    }
}
