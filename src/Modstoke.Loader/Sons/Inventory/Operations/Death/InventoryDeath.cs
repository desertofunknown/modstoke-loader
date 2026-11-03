// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Gameplay;
using global::Sons.Inventory;
using TheForest.Items.Inventory;
using TheForest.Player.Actions;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>These are distinct boundaries, not interchangeable death notifications.</summary>
    public enum InventoryDeathBoundary
    {
        DeathRemoveAll,
        DropBag,
        Retrieval,
        PlayerSave,
        Deserialize
    }

    /// <summary>
    /// In-memory preservation for the original client inventory and its separate death-bag
    /// manager. This does not execute death deletion, bag copy/retrieval, or write a save.
    /// Actual server-authorized removal/bag transfer/restore is a separate authority transaction
    /// in Death.Transfers. This preservation helper is not evidence of death gameplay coverage.
    /// The lifecycle router must hold those native entry points before their first write.
    /// In the recognized native implementation bag copy/delete and retrieval are not atomic;
    /// RemoveAllItemsOnDeath is a separate destructive entry point.
    /// </summary>
    public static class InventoryDeath
    {
        internal static bool Valid(InventoryDeathBoundary boundary)
            => boundary is InventoryDeathBoundary.DeathRemoveAll
                or InventoryDeathBoundary.DropBag
                or InventoryDeathBoundary.Retrieval
                or InventoryDeathBoundary.PlayerSave
                or InventoryDeathBoundary.Deserialize;

        internal static bool Same(Il2CppObjectBase left, Il2CppObjectBase right)
            => left == null ? right == null : !left.WasCollected && right != null
                && !right.WasCollected && left.Pointer == right.Pointer;

        internal static bool Live(UnityEngine.Object value)
            => value != null && !value.WasCollected && value;

        internal static bool NextBridge(PlayerBridge oldBridge, PlayerBridge next, bool server)
            => oldBridge != null && oldBridge.IsDisposed && next != null && !next.IsDisposed
                && next.IsServer == server && ReferenceEquals(oldBridge.Peer, next.Peer)
                && next.Peer.IsCurrent && next.Plan.IsValid && next.Plan.Id != oldBridge.Plan.Id
                && next.Plan.OldNetworkId == oldBridge.NetworkId
                && next.Plan.Generation == oldBridge.Plan.Generation
                && next.Plan.PrefabId == oldBridge.Plan.PrefabId
                && next.Plan.StateTypeId == oldBridge.Plan.StateTypeId;

        // Read the original native serializer into memory only. In particular, never substitute
        // the server world serializer or call either inventory OnDeserialize to repair a hold.
        internal static string CapturePlayerSave(PlayerInventory inventory)
        {
            var data = inventory.Sons_Save_ISaveGameSerializer_TheForest_Items_Inventory_PlayerInventory_PlayerInventorySaveData__OnSerialize();
            if (data == null || data.WasCollected) throw new InvalidOperationException("Client inventory serializer returned no data.");
            var settings = new Newtonsoft.Json.JsonSerializerSettings
            {
                TypeNameHandling = Newtonsoft.Json.TypeNameHandling.None,
                MetadataPropertyHandling = Newtonsoft.Json.MetadataPropertyHandling.Ignore,
                MissingMemberHandling = Newtonsoft.Json.MissingMemberHandling.Error
            };
            var converters = new Il2CppSystem.Collections.Generic.List<Newtonsoft.Json.JsonConverter>();
            converters.Add(new ItemInstanceModuleSaveDataJsonConverter());
            settings.Converters = converters.Cast<Il2CppSystem.Collections.Generic.IList<Newtonsoft.Json.JsonConverter>>();
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(data, settings);
            if (string.IsNullOrEmpty(json) || json.Length > PlayerInventoryBaseline.MaximumBytes)
                throw new InvalidOperationException("Client inventory preservation exceeds its bounded snapshot size.");
            return json;
        }

        internal sealed class BagSnapshot
        {
            private readonly PlayerRetrieveDroppedInventoryAction _action;
            private readonly ItemInstanceManager _manager;
            private readonly IntPtr _map;
            private readonly GameObject _pickup;
            private readonly DroppedInventoryItemsPickup _pickupComponent;
            private readonly Vector3 _position, _rotation;
            private readonly bool _dropped;
            private readonly List<BagStack> _stacks = new();
            internal readonly List<ItemInstance> Instances = new();
            internal bool HasContents { get; }
            internal ItemInstanceManager Manager => _manager;
            internal DroppedInventoryItemsPickup Pickup => _pickupComponent;

            internal BagSnapshot(PlayerRetrieveDroppedInventoryAction action, PlayerInventory inventory)
            {
                if (!Live(action) || !Live(inventory) || !Live(action.transform)
                    || !Live(inventory.transform) || action.transform.root.Pointer != inventory.transform.root.Pointer
                    || action._loadInventoryFromSave || action._onRetrievalComplete != null || action._targetTransform != null)
                    throw new InvalidOperationException("An idle original client bag action is required; pending retrieval/load cannot be reconstructed.");
                _action = action;
                _manager = action._itemInstanceManager;
                if (!Live(_manager) || Same(_manager, inventory._itemInstanceManager)
                    || _manager._itemsMap == null || _manager._itemsMap.WasCollected
                    || _manager._itemsMap.Count > PlayerInventoryBaseline.MaximumEntries)
                    throw new InvalidOperationException("The death-bag manager must be available and distinct from player inventory.");
                _map = _manager._itemsMap.Pointer;
                _pickup = action._pickupObject;
                if (_pickup != null)
                {
                    if (!Live(_pickup)) throw new InvalidOperationException("Retained death-bag pickup expired.");
                    _pickupComponent = _pickup.GetComponent<DroppedInventoryItemsPickup>();
                    if (!Live(_pickupComponent)) throw new InvalidOperationException("Retained death-bag pickup component is unavailable.");
                }
                _dropped = action._isInventoryDropped;
                bool hasContents = _dropped;
                _position = action._droppedInventoryPosition;
                _rotation = action._droppedInventoryRotation;
                if (_dropped && !Live(_pickup)) throw new InvalidOperationException("Dropped bag has no live retained pickup.");
                int entries = 0;
                int remainingModuleCharacters = PlayerInventoryBaseline.MaximumBytes;
                var identities = new HashSet<IntPtr>();
                foreach (var pair in _manager._itemsMap)
                {
                    var stack = pair.Value;
                    if (stack == null || stack.WasCollected || stack.UniqueInstances == null || stack.UniqueInstances.WasCollected
                        || stack.Count < 0 || stack.MaxCount < stack.Count
                        || stack.UniqueInstances.Count > PlayerInventoryBaseline.MaximumEntries - entries)
                        throw new InvalidOperationException("Death-bag item data is unavailable or exceeds the preservation bound.");
                    entries += stack.UniqueInstances.Count;
                    hasContents |= stack.Count != 0 || stack.UniqueInstances.Count != 0;
                    _stacks.Add(new BagStack(pair.Key, stack, ref remainingModuleCharacters));
                    foreach (var instance in stack.UniqueInstances)
                    {
                        if (!identities.Add(instance.Pointer))
                            throw new InvalidOperationException("A unique death-bag instance has multiple custody entries.");
                        Instances.Add(instance);
                    }
                }
                HasContents = hasContents;
            }

            internal bool Matches(PlayerRetrieveDroppedInventoryAction action)
            {
                if (!Live(_action) || !Same(action, _action) || !Live(_manager)
                    || !Same(_manager, action._itemInstanceManager) || _manager._itemsMap?.Pointer != _map
                    || _manager._itemsMap.Count != _stacks.Count || action._loadInventoryFromSave
                    || action._onRetrievalComplete != null || action._targetTransform != null
                    || action._isInventoryDropped != _dropped || !Same(_pickup, action._pickupObject)
                    || (_pickup != null && (!Live(_pickup) || !Live(_pickupComponent)
                        || !Same(_pickupComponent, _pickup.GetComponent<DroppedInventoryItemsPickup>())))
                    || !action._droppedInventoryPosition.Equals(_position)
                    || !action._droppedInventoryRotation.Equals(_rotation)) return false;
                foreach (var stack in _stacks)
                    if (!_manager._itemsMap.TryGetValue(stack.Id, out var current) || !stack.Matches(current)) return false;
                return true;
            }

            private sealed class BagStack
            {
                internal readonly int Id;
                private readonly ItemInstanceManager.Items _stack;
                private readonly IntPtr _list;
                private readonly int _count, _maximum;
                private readonly ItemInstance[] _instances;
                private readonly string[] _modules;
                private readonly IntPtr[] _moduleLists;
                private readonly ItemInstanceModule[][] _moduleIdentities;

                internal BagStack(int id, ItemInstanceManager.Items stack, ref int remainingModuleCharacters)
                {
                    Id = id; _stack = stack; _list = stack.UniqueInstances.Pointer;
                    _count = stack.Count; _maximum = stack.MaxCount;
                    _instances = new ItemInstance[stack.UniqueInstances.Count];
                    _modules = new string[_instances.Length];
                    _moduleLists = new IntPtr[_instances.Length];
                    _moduleIdentities = new ItemInstanceModule[_instances.Length][];
                    for (int i = 0; i < _instances.Length; i++)
                    {
                        var item = stack.UniqueInstances[i];
                        if (item == null || item.WasCollected || item._itemID != id)
                            throw new InvalidOperationException("Death-bag unique instance identity differs.");
                        _instances[i] = item;
                        _modules[i] = PlayerInventoryBaseline.CaptureModules(item);
                        if (_modules[i].Length > remainingModuleCharacters)
                            throw new InvalidOperationException("Death-bag module data exceeds the preservation bound; nothing was truncated.");
                        remainingModuleCharacters -= _modules[i].Length;
                        _moduleLists[i] = item._modules.Pointer;
                        _moduleIdentities[i] = new ItemInstanceModule[item._modules.Count];
                        for (int j = 0; j < _moduleIdentities[i].Length; j++)
                            _moduleIdentities[i][j] = item._modules[j];
                    }
                }

                internal bool Matches(ItemInstanceManager.Items stack)
                {
                    if (!Same(stack, _stack) || stack.Count != _count || stack.MaxCount != _maximum
                        || stack.UniqueInstances?.Pointer != _list || stack.UniqueInstances.Count != _instances.Length) return false;
                    for (int i = 0; i < _instances.Length; i++)
                    {
                        if (!Same(_instances[i], stack.UniqueInstances[i]) || _instances[i]._itemID != Id
                            || _instances[i]._modules?.Pointer != _moduleLists[i]
                            || _instances[i]._modules.Count != _moduleIdentities[i].Length) return false;
                        for (int j = 0; j < _moduleIdentities[i].Length; j++)
                            if (!Same(_moduleIdentities[i][j], _instances[i]._modules[j])) return false;
                        if (!PlayerInventoryBaseline.JsonEqual(_modules[i],
                            PlayerInventoryBaseline.CaptureModules(_instances[i]))) return false;
                    }
                    return true;
                }
            }
        }
    }
}
