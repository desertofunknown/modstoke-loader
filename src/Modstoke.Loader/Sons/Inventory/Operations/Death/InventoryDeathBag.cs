// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using global::Sons.Inventory;
using global::Sons.Items.Core;

namespace Modstoke.Sons
{
    /// <summary>
    /// Exact server-owned bag storage. Creation, world interaction fencing and save ownership
    /// belong to the actor lifecycle owner. This never calls a LocalPlayer death/bag routine.
    /// </summary>
    public sealed class InventoryDeathBag
    {
        private static readonly List<InventoryDeathBag> Bound = new();
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly int _load = SaveLifecycle.State.BeforeLoadCount;
        private readonly PlayerInventoryAuthority _owner;
        private readonly BoltEntity _entity;
        private readonly ItemInstanceManager _manager;
        private readonly int _entityInstance, _managerInstance;
        private StorageSnapshot _snapshot;
        private bool _writing;
        internal bool Released { get; private set; }
        public ulong NetworkId { get; }

        internal InventoryDeathBag(PlayerInventoryAuthority owner,
            BoltEntity entity, ItemInstanceManager manager)
        {
            _owner = owner; _entity = entity; _manager = manager;
            NetworkId = entity.networkId.PackedValue;
            _entityInstance = entity.GetInstanceID(); _managerInstance = manager.GetInstanceID();
            Require(Current(owner), "Bag must be a distinct live server-owned entity with its own child storage.");
            _snapshot = new StorageSnapshot(manager);
            Require(_snapshot.IsEmpty, "Existing native bag contents require explicit custody admission, not adoption.");
            Bound.RemoveAll(bag => bag.Released || bag._load != SaveLifecycle.State.BeforeLoadCount);
            foreach (var bag in Bound)
                Require(!InventoryDeath.Same(bag._entity, entity)
                    && !InventoryDeath.Same(bag._manager, manager), "Bag already has a custody owner.");
            Bound.Add(this);
        }

        /// <summary>Fence all competing native writers/collectors, not only migrated players.</summary>
        public static bool IsHeld(BoltEntity entity)
        {
            foreach (var bag in Bound)
                if (!bag.Released && bag._load == SaveLifecycle.State.BeforeLoadCount
                    && InventoryDeath.Same(entity, bag._entity)) return true;
            return false;
        }

        internal bool Current(PlayerInventoryAuthority owner)
            => Environment.CurrentManagedThreadId == _thread && !Released && ReferenceEquals(owner, _owner)
                && _load == SaveLifecycle.State.BeforeLoadCount && BoltNetwork.isRunning && BoltNetwork.isServer
                && InventoryDeath.Live(_entity) && _entity.GetInstanceID() == _entityInstance
                && _entity.isAttached && _entity.isOwner && _entity.networkId.PackedValue == NetworkId && NetworkId != 0
                && InventoryDeath.Live(_manager) && _manager.GetInstanceID() == _managerInstance
                && InventoryDeath.Live(_entity.transform)
                && InventoryDeath.Live(_manager.transform)
                && _manager.transform.IsChildOf(_entity.transform);

        internal bool Unchanged(PlayerInventoryAuthority owner)
            => Current(owner) && !_writing && _snapshot.Matches(new StorageSnapshot(_manager));

        internal void CheckCapacity(IReadOnlyDictionary<int, int> additions)
        {
            Require(Unchanged(_owner), "Bag custody changed before capacity reservation.");
            foreach (var pair in additions)
            {
                var data = ItemDatabaseManager.ItemById(pair.Key);
                Require(InventoryDeath.Live(data) && data.Id == pair.Key
                    && !data._canNotBeStored && pair.Value > 0, "Death item has no storable native definition.");
                int count = 0, maximum = data._maxAmount;
                if (_snapshot.Stacks.TryGetValue(pair.Key, out var stack)) { count = stack.Count; maximum = stack.Maximum; }
                Require(maximum >= 0 && (long)count + pair.Value <= maximum,
                    "Complete death contents do not fit this bag; no client removal was queued.");
            }
        }

        internal void Add(int id, int quantity, ItemInstance instance) => Mutate(id, quantity, instance, true);
        internal void Take(int id, ItemInstance instance) => Mutate(id, 1, instance, false);

        private void Mutate(int id, int quantity, ItemInstance instance, bool add)
        {
            Require(Unchanged(_owner) && quantity > 0 && (instance == null || quantity == 1),
                "Native bag mutation requires unchanged exact custody.");
            var before = _snapshot;
            _writing = true;
            try
            {
                bool applied = add
                    ? instance == null ? _manager.TryAddItems(id, quantity, false) : _manager.TryAddItem(instance, false)
                    : instance == null ? _manager.RemoveItems(id, quantity, false) == quantity : _manager.TryRemoveItem(instance);
                Require(applied && Current(_owner), "Native bag write failed or its owning entity changed; do not retry.");
                var after = new StorageSnapshot(_manager);
                Require(before.Confirms(after, id, add ? quantity : -quantity, instance),
                    "Native bag readback differs from the one authorized custody change; do not retry.");
                _snapshot = after;
            }
            finally { _writing = false; }
        }

        internal void Release(PlayerInventoryAuthority owner)
        {
            Require(Unchanged(owner) && _snapshot.IsEmpty, "Only a confirmed empty bag can release its world fence.");
            Released = true;
            Bound.Remove(this);
        }

        private static void Require(bool condition, string detail)
        { if (!condition) throw new InvalidOperationException(detail); }

        private sealed class StorageSnapshot
        {
            private readonly IntPtr _map;
            internal readonly Dictionary<int, Stack> Stacks = new();
            internal bool IsEmpty { get; } = true;
            internal StorageSnapshot(ItemInstanceManager manager)
            {
                Require(manager._itemsMap != null && !manager._itemsMap.WasCollected
                    && manager._itemsMap.Count <= PlayerInventoryBaseline.MaximumEntries,
                    "Bag storage is missing or exceeds its custody bound.");
                _map = manager._itemsMap.Pointer;
                int entries = 0, moduleBytes = PlayerInventoryBaseline.MaximumBytes;
                var identities = new HashSet<IntPtr>();
                foreach (var pair in manager._itemsMap)
                {
                    var stack = new Stack(pair.Key, pair.Value, ref moduleBytes);
                    entries = checked(entries + stack.Instances.Length);
                    Require(entries <= PlayerInventoryBaseline.MaximumEntries, "Too many unique bag instances.");
                    foreach (var item in stack.Instances)
                        Require(identities.Add(item.Pointer), "A native instance appears in more than one bag stack.");
                    Stacks.Add(pair.Key, stack);
                    if (stack.Count != 0) IsEmpty = false;
                }
            }

            internal bool Matches(StorageSnapshot other)
            {
                if (_map != other._map || Stacks.Count != other.Stacks.Count) return false;
                foreach (var pair in Stacks)
                    if (!other.Stacks.TryGetValue(pair.Key, out var next) || !pair.Value.Matches(next)) return false;
                return true;
            }

            internal bool Confirms(StorageSnapshot after, int id, int delta, ItemInstance instance)
            {
                if (_map != after._map) return false;
                foreach (var pair in Stacks)
                    if (pair.Key != id && (!after.Stacks.TryGetValue(pair.Key, out var next) || !pair.Value.Matches(next))) return false;
                foreach (int key in after.Stacks.Keys) if (key != id && !Stacks.ContainsKey(key)) return false;
                Stacks.TryGetValue(id, out var before);
                after.Stacks.TryGetValue(id, out var result);
                int expected = (before?.Count ?? 0) + delta;
                if (expected < 0 || (result?.Count ?? 0) != expected) return false;
                if (result == null) return expected == 0 && before != null
                    && (instance == null ? before.Instances.Length == 0 : before.Instances.Length == 1
                        && InventoryDeath.Same(before.Instances[0], instance));
                if (before != null && (before.Identity != result.Identity || before.List != result.List
                    || before.Definition != result.Definition || before.Maximum != result.Maximum)) return false;
                var expectedItems = new List<ItemInstance>(before?.Instances ?? Array.Empty<ItemInstance>());
                if (instance != null)
                {
                    if (delta == 1) expectedItems.Add(instance);
                    else if (delta != -1 || expectedItems.RemoveAll(item => InventoryDeath.Same(item, instance)) != 1) return false;
                }
                if (expectedItems.Count != result.Instances.Length) return false;
                for (int i = 0; i < expectedItems.Count; i++)
                    if (!InventoryDeath.Same(expectedItems[i], result.Instances[i])) return false;
                if (before != null)
                    for (int i = 0; i < before.Instances.Length; i++)
                    {
                        if (delta < 0 && InventoryDeath.Same(before.Instances[i], instance)) continue;
                        int next = Array.FindIndex(result.Instances, item => InventoryDeath.Same(item, before.Instances[i]));
                        if (next < 0 || !before.SameModules(i, result, next)) return false;
                    }
                return true;
            }
        }

        private sealed class Stack
        {
            internal readonly IntPtr Identity, List, Definition;
            internal readonly int Count, Maximum;
            internal readonly ItemInstance[] Instances;
            private readonly string[] _modules;
            private readonly IntPtr[][] _moduleIdentities;
            internal Stack(int id, ItemInstanceManager.Items stack, ref int remaining)
            {
                var data = ItemDatabaseManager.ItemById(id);
                Require(id > 0 && InventoryDeath.Live(data) && data.Id == id
                    && stack != null && !stack.WasCollected && stack.UniqueInstances != null && !stack.UniqueInstances.WasCollected
                    && stack.Count >= 0 && stack.MaxCount >= stack.Count
                    && stack.UniqueInstances.Count <= PlayerInventoryBaseline.MaximumEntries
                    && (data._eachInstanceIsUnique ? stack.Count == stack.UniqueInstances.Count : stack.UniqueInstances.Count == 0),
                    "Native bag stack identity, definition or quantity is invalid.");
                Identity = stack.Pointer; List = stack.UniqueInstances.Pointer; Definition = data.Pointer;
                Count = stack.Count; Maximum = stack.MaxCount;
                Instances = new ItemInstance[stack.UniqueInstances.Count];
                _modules = new string[Instances.Length]; _moduleIdentities = new IntPtr[Instances.Length][];
                for (int i = 0; i < Instances.Length; i++)
                {
                    var item = stack.UniqueInstances[i];
                    Require(item != null && !item.WasCollected && item._itemID == id, "Bag instance ID differs from its stack.");
                    Instances[i] = item;
                    _modules[i] = PlayerInventoryBaseline.CaptureModules(item);
                    remaining -= _modules[i].Length;
                    Require(remaining >= 0, "Bag module snapshot exceeds its custody bound.");
                    _moduleIdentities[i] = new IntPtr[item._modules.Count + 1];
                    _moduleIdentities[i][0] = item._modules.Pointer;
                    for (int j = 0; j < item._modules.Count; j++) _moduleIdentities[i][j + 1] = item._modules[j].Pointer;
                }
            }
            internal bool SameModules(int index, Stack other, int next)
                => _moduleIdentities[index].AsSpan().SequenceEqual(other._moduleIdentities[next])
                    && PlayerInventoryBaseline.JsonEqual(_modules[index], other._modules[next]);
            internal bool Matches(Stack other)
            {
                if (Identity != other.Identity || List != other.List || Definition != other.Definition
                    || Count != other.Count || Maximum != other.Maximum || Instances.Length != other.Instances.Length) return false;
                for (int i = 0; i < Instances.Length; i++)
                    if (!InventoryDeath.Same(Instances[i], other.Instances[i]) || !SameModules(i, other, i)) return false;
                return true;
            }
        }
    }
}
