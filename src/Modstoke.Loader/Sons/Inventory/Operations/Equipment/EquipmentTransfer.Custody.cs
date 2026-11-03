// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    internal sealed partial class EquipmentTransfer
    {
        private bool ContextCurrent(bool requireReservation)
        {
            if (!OnThread || _checking || _load != SaveLifecycle.State.BeforeLoadCount
                || !PlayerRoles.IsAvailable || _bridge.IsDisposed || _bridge.IsServer || !_bridge.IsGameplayActive
                || State is Stage.Unconfirmed or Stage.Cancelled or Stage.Published) return false;
            _checking = true;
            try
            {
                var expectedStage = State;
                if (requireReservation && (_reservationCurrent == null || !_reservationCurrent())) return false;
                if (State != expectedStage || _bridge.IsDisposed) return false;
                if (requireReservation && State != Stage.Admitted
                    && (!Reservations.TryGetValue(_inventory.Pointer, out var reservation) || !ReferenceEquals(reservation, this))) return false;
                // These globals identify the one retained source presentation. They do not grant
                // possession or make native presentation methods suitable for remote server actors.
                return BoltNetwork.isClient && !BoltNetwork.isServer
                    && _bridge.NetworkId == _networkId && _bridge.TryReadCurrentTarget(out var target)
                    && target.Pointer == _entity && Same(target, LocalPlayer.Entity)
                    && Live(LocalPlayer.GameObject) && LocalPlayer.GameObject.Pointer == _root
                    && Live(_inventory) && _inventory.isActiveAndEnabled && Same(_inventory, LocalPlayer.Inventory)
                    && Live(_manager) && Same(_manager, _inventory._itemInstanceManager)
                    && Live(_props) && Same(_props, _inventory._InventoryProps_k__BackingField)
                    && _nativeState.Current(_inventory, _props)
                    && Same(_inventory, _props._playerInventory) && Alive(_item) && Live(_data)
                    && Same(_data, _item._data) && _item._itemID == _itemId && _data._id == _itemId
                    && Pointer(_data._heldPrefab) == _heldPrefab && Pointer(_data._pickupPrefab) == _pickupPrefab
                    && Live(LocalPlayer.Stats) && Same(LocalPlayer.Stats._inventory, _inventory)
                    && !LocalPlayer.Stats._playerIsDowned && LocalPlayer.Stats._dyingState == PlayerStats.DyingState.Alive
                    && !LocalPlayer.Stats._healing && !LocalPlayer.Stats._isHealing && !LocalPlayer.Stats._respawning;
            }
            finally { _checking = false; }
        }

        private bool ProfileCurrent() => _data._equipmentSlot == _slot && HandSlot(_slot)
            && _data._eachInstanceIsUnique && !_data._canNotBeStored && !_data._alwaysDropOnUnequip
            && ((int)_data._type & 0x10) != 0 && _data._maxAmount > 0
            && Alive(_data._ownedStatEffect) && _data._ownedStatEffect.Length == 0
            && Alive(_data._equippedStatEffect) && _data._equippedStatEffect.Length == 0;

        private static bool HandSlot(EquipmentSlot slot) => slot is EquipmentSlot.RightHand or EquipmentSlot.LeftHand;

        private bool CanPreserveOtherHand()
        {
            var other = _inventory._equipmentSlots[(int)(_slot == EquipmentSlot.RightHand ? EquipmentSlot.LeftHand : EquipmentSlot.RightHand)];
            if (other == null) return true;
            // TryEquip stashes the opposite hand before debit for two-handed items.
            // First-look controllers can also require it; those need a compound reservation.
            return Alive(other) && Live(other._data) && !_data._requiresBothHands
                && !_data._firstLookRequiresBothHands && !other._data._requiresBothHands;
        }

        private bool PropCurrent() => Alive(_prop) && Live(_locator) && _prop._itemId == ItemId
            && Same(_prop._heldLocator, _locator) && Live(_data._heldPrefab)
            && Same(_inventory.GetPropDefinitionForItem(ItemId, _defaultSlot), _prop);

        private bool Idle()
        {
            if (_inventory._currentView != PlayerInventory.PlayerViews.World
                || _inventory._currentEquipState != PlayerInventory.HandEquipState.Idle
                || _inventory._equipSequenceRight != null || _props._leftHandWideBlendCoroutine != null
                || _inventory._IsThrowing_k__BackingField
                || _inventory._IsInQuickSelect_k__BackingField || _inventory.IsWeaponBurning
                || _inventory.HeldOnlyItemController?.HasItem == true) return false;
            var next = _inventory._equipmentSlotsNext;
            if (!Alive(next) || next.Length != 6 || !Alive(_inventory._projectileItemInstances)
                || _inventory._projectileItemInstances.Count != 0) return false;
            for (int i = 0; i < next.Length; i++) if (next[i] != null) return false;
            return true;
        }

        private bool ModulesCurrent()
        {
            if (!Alive(_item._modules) || _item._modules.Pointer != _moduleList
                || _item._modules.Count != _moduleIdentities.Length) return false;
            for (int i = 0; i < _moduleIdentities.Length; i++)
                if (!Alive(_item._modules[i]) || _item._modules[i].Pointer != _moduleIdentities[i]) return false;
            return PlayerInventoryBaseline.JsonEqual(_modules,
                PlayerInventoryBaseline.CaptureModules(_item));
        }

        private bool ConfirmsCustody(int storedDelta, int heldSlot)
        {
            if (!ModulesCurrent() || !_before.Matches(Custody.Capture(_inventory), _item, storedDelta, heldSlot)) return false;
            if (heldSlot >= 0)
                return Live(_item.ItemObject) && _item.ItemObject.activeInHierarchy
                    && _props._gameObjectInstances.TryGetValue(ItemId, out var held) && Same(held, _item.ItemObject);
            return !_props._gameObjectInstances.ContainsKey(ItemId)
                && (!Live(_item.ItemObject) || (Kind == Operation.DropStored && Same(_item.ItemObject, _dropOutput)));
        }

        private bool ConfirmsEquipInFlight()
        {
            int slot = (int)_slot;
            var held = _inventory._equipmentSlots[slot];
            var pending = _inventory._equipmentSlotsNext[slot];
            if ((held != null && !Same(held, _item)) || (pending != null && !Same(pending, _item))) return false;
            return _before.Matches(Custody.Capture(_inventory), _item, -1,
                held != null ? slot : -1, pending != null ? slot : -1);
        }

        private static bool Alive(Il2CppObjectBase value) => value != null && !value.WasCollected && value.Pointer != 0;
        private static bool Live(UnityEngine.Object value) => Alive(value) && value;
        private static bool Same(Il2CppObjectBase left, Il2CppObjectBase right)
            => Alive(left) && Alive(right) && left.Pointer == right.Pointer;
        private static nint Pointer(Il2CppObjectBase value) => value == null ? 0
            : !value.WasCollected ? value.Pointer : throw new InvalidOperationException("A captured native wrapper expired.");
        private static void Require(bool condition, string detail)
        { if (!condition) throw new InvalidOperationException(detail); }

        // Direct reads only: GetSingleInstance/GetAllItemsOfType can construct native items.
        // Preserve every untouched count, instance order, bucket, slot and held-object mapping.
        internal sealed class Custody
        {
            private readonly Dictionary<int, Bucket> Storage = new();
            internal readonly Dictionary<int, nint> Objects = new();
            internal readonly nint[] Slots = new nint[6], Next = new nint[6], Previous = new nint[6];
            private readonly int[] _slotIds = new int[6];
            private nint _map, _slots, _next, _previous, _ids, _objects;

            internal static Custody Capture(PlayerInventory inventory)
            {
                var manager = inventory._itemInstanceManager;
                var slots = inventory._equipmentSlots;
                var next = inventory._equipmentSlotsNext;
                var previous = inventory._equipmentSlotsPrevious;
                var ids = inventory._equipmentSlotsIds;
                var objects = inventory._InventoryProps_k__BackingField._gameObjectInstances;
                Require(Live(manager) && Alive(manager._itemsMap) && Alive(slots) && Alive(next)
                    && Alive(previous) && Alive(ids) && Alive(objects) && slots.Length == 6 && next.Length == 6
                    && previous.Length == 6 && ids.Length == 6, "Native inventory layout is unavailable.");
                var snapshot = new Custody { _map = manager._itemsMap.Pointer, _slots = slots.Pointer,
                    _next = next.Pointer, _previous = previous.Pointer, _ids = ids.Pointer, _objects = objects.Pointer };
                Require(manager._itemsMap.Count <= PlayerInventoryBaseline.MaximumEntries
                    && objects.Count <= PlayerInventoryBaseline.MaximumEntries, "Native inventory exceeds the capture bound.");
                int total = 0;
                var unique = new HashSet<nint>();
                foreach (int id in manager._itemsMap.Keys)
                {
                    var entry = manager._itemsMap[id];
                    Require(id > 0 && Alive(entry) && Alive(entry.UniqueInstances) && entry.Count >= 0
                        && entry.UniqueInstances.Count <= entry.Count, "Invalid native storage bucket.");
                    total = checked(total + entry.UniqueInstances.Count);
                    Require(total <= PlayerInventoryBaseline.MaximumEntries, "Native instance capture exceeds its bound.");
                    var values = new nint[entry.UniqueInstances.Count];
                    for (int i = 0; i < values.Length; i++)
                    {
                        var instance = entry.UniqueInstances[i];
                        Require(Alive(instance) && instance._itemID == id && unique.Add(instance.Pointer), "Ambiguous stored instance custody.");
                        values[i] = instance.Pointer;
                    }
                    snapshot.Storage.Add(id, new Bucket(entry.Pointer, entry.UniqueInstances.Pointer, entry.Count, entry.MaxCount, values));
                }
                for (int i = 0; i < slots.Length; i++)
                {
                    snapshot.Slots[i] = Pointer(slots[i]);
                    snapshot.Next[i] = Pointer(next[i]);
                    snapshot.Previous[i] = Pointer(previous[i]);
                    snapshot._slotIds[i] = ids[i];
                    Require(ids[i] == (slots[i]?._itemID ?? 0), "Native held ID and instance disagree.");
                    if (slots[i] != null) Require(unique.Add(slots[i].Pointer), "Instance occupies multiple custody locations.");
                }
                foreach (int id in objects.Keys) snapshot.Objects.Add(id, Pointer(objects[id]));
                return snapshot;
            }

            internal int Occurrences(nint item)
            {
                int count = 0;
                foreach (var bucket in Storage.Values) foreach (nint value in bucket.Instances) if (value == item) count++;
                return count;
            }

            internal bool HasItem(int id) => Storage.TryGetValue(id, out var entry) && entry.Count > 0;
            internal int StoredCount(int id) => Storage.TryGetValue(id, out var entry) ? entry.Count : 0;
            internal bool IsUniqueStorage(int id) => Storage.TryGetValue(id, out var entry) && entry.Count == entry.Instances.Length;
            internal bool IsCountedStorage(int id) => Storage.TryGetValue(id, out var entry)
                && entry.Count > 0 && entry.Instances.Length == 0;
            internal bool HasHeldItem(int id) => Array.IndexOf(_slotIds, id) >= 0 || Objects.ContainsKey(id);
            internal bool IsDetached(nint instance) => Occurrences(instance) == 0
                && Array.IndexOf(Slots, instance) < 0 && Array.IndexOf(Next, instance) < 0
                && Array.IndexOf(Previous, instance) < 0;
            internal bool HasStorageSpace(int id, int maximum) => !Storage.TryGetValue(id, out var entry)
                || (entry.Count < maximum && (entry.Maximum < 0 || entry.Count < entry.Maximum)
                    && entry.Count == entry.Instances.Length);

            internal bool CountedMatches(Custody after, int id, int delta)
            {
                if ((delta != 0 && delta != -1) || !IsCountedStorage(id)) return false;
                if (after._map != _map || after._slots != _slots || after._next != _next || after._previous != _previous
                    || after._ids != _ids || after._objects != _objects || after.Storage.Count != Storage.Count
                    || after.Objects.Count != Objects.Count || !Slots.AsSpan().SequenceEqual(after.Slots)
                    || !Next.AsSpan().SequenceEqual(after.Next) || !Previous.AsSpan().SequenceEqual(after.Previous)
                    || !_slotIds.AsSpan().SequenceEqual(after._slotIds)) return false;
                foreach (var pair in Storage)
                {
                    if (!after.Storage.TryGetValue(pair.Key, out var current)) return false;
                    var old = pair.Value;
                    int difference = pair.Key == id ? delta : 0;
                    if (old.Pointer != current.Pointer || old.List != current.List || old.Maximum != current.Maximum
                        || current.Count != old.Count + difference
                        || !old.Instances.AsSpan().SequenceEqual(current.Instances)) return false;
                }
                foreach (var pair in Objects)
                {
                    if (!after.Objects.TryGetValue(pair.Key, out nint value) || value != pair.Value) return false;
                }
                return true;
            }

            internal bool Matches(Custody after, ItemInstance item, int delta, int heldSlot, int pendingSlot = -1)
            {
                if (after._map != _map || after._slots != _slots || after._next != _next || after._previous != _previous
                    || after._ids != _ids || after._objects != _objects || !Previous.AsSpan().SequenceEqual(after.Previous)) return false;
                bool changed = delta != 0;
                int id = item._itemID;
                bool created = changed && delta == 1 && !Storage.ContainsKey(id);
                if (after.Storage.Count != Storage.Count + (created ? 1 : 0)) return false;
                foreach (var pair in Storage)
                {
                    if (!after.Storage.TryGetValue(pair.Key, out var current)) return false;
                    var old = pair.Value;
                    int difference = pair.Key == id ? delta : 0;
                    if (old.Pointer != current.Pointer || old.List != current.List || old.Maximum != current.Maximum
                        || current.Count != old.Count + difference) return false;
                    if (difference == 0)
                    { if (!old.Instances.AsSpan().SequenceEqual(current.Instances)) return false; }
                    else if (!InstancesMatch(old.Instances, current.Instances, item.Pointer, difference)) return false;
                }
                if (created && (!after.Storage.TryGetValue(id, out var added) || added.Count != 1
                    || added.Instances.Length != 1 || added.Instances[0] != item.Pointer)) return false;
                for (int i = 0; i < Slots.Length; i++)
                {
                    nint expected = !changed ? Slots[i] : i == heldSlot ? item.Pointer : Slots[i] == item.Pointer ? 0 : Slots[i];
                    int expectedId = !changed ? _slotIds[i] : i == heldSlot ? id : Slots[i] == item.Pointer ? 0 : _slotIds[i];
                    nint expectedNext = !changed ? Next[i] : i == pendingSlot ? item.Pointer : 0;
                    if (after.Slots[i] != expected || after._slotIds[i] != expectedId || after.Next[i] != expectedNext) return false;
                }
                int expectedObjects = Objects.Count;
                if (changed && Objects.ContainsKey(id)) expectedObjects--;
                if (changed && heldSlot >= 0) expectedObjects++;
                if (after.Objects.Count != expectedObjects) return false;
                foreach (var pair in Objects)
                {
                    if (changed && pair.Key == id) continue;
                    if (!after.Objects.TryGetValue(pair.Key, out nint value) || value != pair.Value) return false;
                }
                return !changed || (heldSlot >= 0 ? after.Objects.ContainsKey(id) : !after.Objects.ContainsKey(id));
            }

            private static bool InstancesMatch(nint[] before, nint[] after, nint item, int delta)
            {
                if (after.Length != before.Length + delta) return false;
                if (delta == 1)
                    return Array.IndexOf(before, item) < 0 && after[^1] == item
                        && before.AsSpan().SequenceEqual(after.AsSpan(0, before.Length));
                int index = Array.IndexOf(before, item);
                if (index < 0 || Array.LastIndexOf(before, item) != index) return false;
                return before.AsSpan(0, index).SequenceEqual(after.AsSpan(0, index))
                    && before.AsSpan(index + 1).SequenceEqual(after.AsSpan(index));
            }

            private sealed record Bucket(nint Pointer, nint List, int Count, int Maximum, nint[] Instances);
        }
    }
}
