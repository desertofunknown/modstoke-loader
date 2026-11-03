// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using global::Sons.Gameplay;
using global::Sons.Inventory;
using global::Sons.Items.Core;

namespace Modstoke.Sons
{
    internal sealed partial class InventoryStorage
    {
        private WorldVariant _variant;
        internal bool IsWorldVariant => _variant != null;

        // The live donor and the detached transport/ledger copy have separate lifetimes.
        // Never unregister a world donor's updater just to prepare a player grant.
        internal sealed class WorldItemSnapshot
        {
            internal readonly ItemInstance Native;
            internal readonly string Modules;
            internal readonly PlayerInventoryModuleState ModuleState;
            private readonly ItemData _data;
            private readonly nint _list;
            private readonly ItemInstanceModule[] _modules;

            internal WorldItemSnapshot(ItemInstance item)
            {
                if (!StorableWorldItem(item)) throw new InvalidOperationException("World donor has no complete native custody.");
                Native = item;
                _data = item.Data;
                _list = item._modules.Pointer;
                _modules = new ItemInstanceModule[item._modules.Count];
                var seen = new HashSet<nint>();
                for (int i = 0; i < _modules.Length; i++)
                {
                    var module = item._modules[i];
                    if (module == null || module.WasCollected || !seen.Add(module.Pointer))
                        throw new InvalidOperationException("World donor has an unavailable or duplicate module.");
                    PlayerInventoryEnforcement.ValidateWorldModule(module);
                    _modules[i] = module;
                }
                Modules = PlayerInventoryBaseline.CaptureModules(item);
                ModuleState = PlayerInventoryBaseline.CaptureModuleState(item);
            }

            internal bool Current()
            {
                if (!StorableWorldItem(Native) || !Same(Native.Data, _data)
                    || Native._modules.Pointer != _list || Native._modules.Count != _modules.Length) return false;
                for (int i = 0; i < _modules.Length; i++)
                    if (!Same(Native._modules[i], _modules[i])) return false;
                return Matches(Native);
            }

            internal bool Matches(ItemInstance item) => StorableWorldItem(item) && item._itemID == Native._itemID
                && Same(item.Data, _data)
                && ModuleState == PlayerInventoryBaseline.CaptureModuleState(item)
                && PlayerInventoryBaseline.JsonEqual(Modules,
                    PlayerInventoryBaseline.CaptureModules(item));

            internal ItemInstance CreateDetached(out ItemInstance allocated) => PlayerInventoryBaseline.RestoreInstance(
                new PlayerInventoryBaseline.Entry
                { ItemId = Native._itemID, Quantity = 1, Modules = Modules, ModuleState = ModuleState }, out allocated);
        }

        private sealed class WorldVariant(WorldItemSnapshot donor, WorldItemSnapshot escrow,
            InWorldLayoutItemGroup group)
        {
            internal readonly WorldItemSnapshot Donor = donor, Escrow = escrow;
            internal readonly InWorldLayoutItemGroup Group = group;
            internal Il2CppSystem.Collections.Generic.List<ItemInstance> UniqueList;
            internal ItemInstance[] UniqueBefore;
            internal Il2CppSystem.Func<ItemInstance, bool> Placement;
            internal Il2CppSystem.Action<int, int> Added, Removed;
            internal ItemInstance DestinationItem;
            internal WorldItemSnapshot Destination;
            internal bool ArrowMatching, PlacementAttempted, PlacementAccepted;
            internal bool RemovePermit, AddPermit;
            internal int RemoveDepth, AddDepth;
        }

        internal static bool StorableWorldItem(ItemInstance item) => item != null && !item.WasCollected
            && item._itemID > 0 && item._itemID <= ServerInventory.MaxItemId
            && Live(item.Data) && item.Data.Id == item._itemID && !item.Data._canNotBeStored && item.Data._maxAmount > 0
            && item._modules != null && !item._modules.WasCollected && item._modules.Count is >= 0 and <= 32
            && (item.Data._eachInstanceIsUnique || item._modules.Count == 0);

        internal static bool TryDescribeVariantPickup(PickUp pickup, bool server,
            out ulong world, out WorldItemSnapshot donor)
        {
            world = 0;
            donor = null;
            if (!Live(pickup) || !PlainPickupShape(pickup, pickup._itemId)
                || !pickup.gameObject.activeInHierarchy || pickup._forceDisabled || pickup._isCollected
                || pickup._isBeingDestroyed || pickup._pendingDestroyThroughBolt || pickup._Used_k__BackingField
                || pickup._remainingItemCountNotCollected != 0 || !IsUnpooledPickup(pickup)) return false;
            var entity = pickup.GetComponent<BoltEntity>();
            var dynamicPickup = pickup.GetComponent<CoopDynamicPickUp>();
            if (!Live(entity) || !entity.isAttached || entity.isOwner != server || entity.networkId.PackedValue == 0
                || !Same(entity.gameObject, pickup.gameObject) || !Same(pickup._destroyTarget, pickup.gameObject)
                || !Same(BoltNetwork.FindEntity(entity.networkId), entity) || !Live(dynamicPickup)
                || !Same(dynamicPickup.entity, entity) || dynamicPickup._bundleCount is < 0 or > 1
                || !StorableWorldItem(pickup._ItemInstance_k__BackingField)
                || pickup._ItemInstance_k__BackingField._itemID != pickup._itemId) return false;
            donor = new(pickup._ItemInstance_k__BackingField);
            world = entity.networkId.PackedValue;
            return true;
        }

        internal static bool WorldGroupCurrent(InWorldLayoutItemGroup group, bool deposit)
            => Live(group) && group.gameObject.activeInHierarchy
                && group.GetIl2CppType().FullName == "Sons.Inventory.InWorldLayoutItemGroup"
                && !group._disableInteraction && !group._bypassPlayerInventoryUpdate && !group._forceEquip
                && group._transferAmount == 1 && (!deposit || !group._blockAddItemInteraction)
                && Live(group._storage) && Live(group._storage._itemInstanceManager);

        internal static bool TryReadWorldBucket(ItemInstanceManager manager, int id,
            out ItemInstanceManager.Items bucket)
        {
            bucket = null;
            if (!Live(manager) || manager._itemsMap == null || manager._itemsMap.WasCollected
                || !manager._itemsMap.TryGetValue(id, out bucket) || bucket == null || bucket.WasCollected
                || bucket.Count < 0 || bucket.MaxCount < 1 || bucket.Count > bucket.MaxCount) return false;
            var data = ItemDatabaseManager.ItemById(id);
            if (!Live(data) || data.Id != id || data._canNotBeStored) return false;
            return data._eachInstanceIsUnique
                ? bucket.UniqueInstances != null && !bucket.UniqueInstances.WasCollected
                    && bucket.UniqueInstances.Count == bucket.Count && bucket.Count <= 4096
                : NoUniqueItems(bucket);
        }

        internal static bool TrySelectWorldDonor(InWorldLayoutItemGroup group, ItemInstanceManager player,
            bool deposit, out ItemInstance donor)
        {
            donor = null;
            if (!WorldGroupCurrent(group, deposit)
                || !TryReadWorldBucket(group._storage._itemInstanceManager, group._itemId, out var stored)) return false;
            var manager = deposit ? player : group._storage._itemInstanceManager;
            if (!TryReadWorldBucket(manager, group._itemId, out var source) || source.Count == 0
                || deposit && stored.Count >= stored.MaxCount) return false;
            if (deposit && group._validateArrowType && stored.Count > 0)
            {
                // Native AddItem uses the first stored arrow and List.Find, not GetSingleInstance.
                if (stored.UniqueInstances == null || stored.UniqueInstances.Count == 0
                    || source.UniqueInstances == null || source.UniqueInstances.WasCollected
                    || !TryReadArrowType(stored.UniqueInstances[0], out var type)) return false;
                for (int i = 0; i < source.UniqueInstances.Count; i++)
                {
                    var item = source.UniqueInstances[i];
                    if (TryReadArrowType(item, out var candidate) && candidate == type) { donor = item; break; }
                }
            }
            else if (source.UniqueInstances != null && source.UniqueInstances.Count > 0)
                donor = source.UniqueInstances[source.UniqueInstances.Count - 1];
            else if (!TryPlainItem(group._itemId, out donor, out _)) return false;
            return StorableWorldItem(donor) && donor._itemID == group._itemId;
        }

        private static bool TryReadArrowType(ItemInstance item, out ArrowUpgradeItemData.Type type)
        {
            type = default;
            if (!StorableWorldItem(item) || !item.TryGetModule<ArrowUpgradeItemInstanceModule>(out var arrow)
                || arrow == null || arrow.WasCollected || arrow._data == null || arrow._data.WasCollected) return false;
            // Do not call the lazy GetData getter during capture: it can initialize and change a live donor.
            type = arrow._data.Type;
            return true;
        }

        internal static bool TryResolveWorldVariant(in PlayerWorldIntent intent, ItemInstance playerDonor,
            out WorldItemSnapshot donor, out InWorldLayoutItemGroup group)
        {
            donor = null;
            group = null;
            if (intent.Action == TransferKind.Drop) return false;
            var world = BoltNetwork.FindEntity(new NetworkId(intent.World));
            if (!Live(world) || !world.isAttached || !world.isOwner || IsWorldHeld(world)) return false;
            if (intent.Action == TransferKind.Pickup)
                return TryDescribeVariantPickup(world.GetComponentInChildren<PickUp>(), true, out ulong id, out donor)
                    && id == intent.World && donor.Native._itemID == intent.ItemId
                    && PlayerInventoryWire.HashModules(donor.Modules) == intent.ModuleFingerprint;
            var groups = world.GetComponentsInChildren<InWorldLayoutItemGroup>(true);
            if (groups == null || groups.Length > 128) return false;
            foreach (var found in groups)
            {
                if (!Live(found) || found._itemId != intent.ItemId || !Live(found._storage)
                    || found._storage.StorageSlotIndex != intent.Slot || !Same(found._storage._boltEntity, world)) continue;
                if (group != null) return false;
                group = found;
            }
            bool deposit = intent.Action == TransferKind.Deposit;
            if (!WorldGroupCurrent(group, deposit)
                || !TryReadWorldBucket(group._storage._itemInstanceManager, intent.ItemId, out var bucket)) return false;
            ItemInstance selected;
            if (deposit)
            {
                if (!StorableWorldItem(playerDonor) || bucket.Count >= bucket.MaxCount) return false;
                selected = playerDonor;
                if (group._validateArrowType && bucket.Count > 0
                    && (bucket.UniqueInstances == null || bucket.UniqueInstances.Count == 0
                        || !TryReadArrowType(bucket.UniqueInstances[0], out var storedType)
                        || !TryReadArrowType(selected, out var donorType) || storedType != donorType)) return false;
            }
            else
            {
                if (!TrySelectWorldDonor(group, null, false, out selected)) return false;
                if (selected.TryGetModule<ArrowUpgradeItemInstanceModule>(out _))
                {
                    // The native server withdrawal resolves the token's arrow type with
                    // List.Find and removes that real donor, not the token's detached copy.
                    if (!TryReadArrowType(selected, out var requestedType) || bucket.UniqueInstances == null) return false;
                    selected = null;
                    for (int i = 0; i < bucket.UniqueInstances.Count; i++)
                    {
                        var item = bucket.UniqueInstances[i];
                        if (TryReadArrowType(item, out var type) && type == requestedType) { selected = item; break; }
                    }
                    if (selected == null) return false;
                }
            }
            donor = new(selected);
            return selected._itemID == intent.ItemId && PlayerInventoryWire.HashModules(donor.Modules) == intent.ModuleFingerprint;
        }

        internal static bool TryReserveWorldVariant(PlayerBridge bridge,
            PlayerInventoryAuthority authority, in PlayerInventoryFrame action,
            in PlayerWorldIntent intent, WorldItemSnapshot donor, ItemInstance escrow, InWorldLayoutItemGroup group,
            out InventoryStorage transfer, out string detail)
        {
            transfer = null;
            detail = "Exact world donor and reserved module-bearing transaction are required.";
            if (bridge == null || authority == null || !Cursors.TryGetValue(authority, out var cursor)
                || cursor.Router == null || !cursor.Router.Current() || cursor.Active != null
                || action.Transaction <= cursor.LastTransaction || authority.LastTransaction != action.Transaction
                || action.Transaction == 0 || action.Quantity != 1 || action.ItemId != intent.ItemId
                || action.MigrationId != bridge.Plan.Id || action.Generation != bridge.Plan.Generation
                || action.NetworkId != bridge.NetworkId || action.Fingerprint != authority.AcceptedBaselineFingerprint
                || !bridge.TryReadCurrentTarget(out var actor) || !Live(actor)
                || donor == null || !donor.Current() || !donor.Matches(escrow)
                || PlayerInventoryWire.HashModules(donor.Modules) != intent.ModuleFingerprint
                || (intent.IsGrant ? action.Kind != PlayerInventoryMessage.GrantPrepare || action.Key != action.Transaction
                        || action.ModuleFingerprint != intent.ModuleFingerprint
                    : action.Kind != PlayerInventoryMessage.Remove || action.Key != intent.Key)) return false;
            var candidate = new InventoryStorage(bridge, authority, action, intent.Action,
                actor, escrow, donor.Modules, cursor);
            candidate._variant = new(donor, new(escrow), group);
            bool captured = intent.Action == TransferKind.Pickup
                ? candidate.CaptureWorld(BoltNetwork.FindEntity(new NetworkId(intent.World))) && candidate.CapturePickup()
                : candidate.CaptureStorage(intent.World, intent.Slot);
            if (!captured || !candidate.AccessCurrent() || !candidate.Register()) return false;
            transfer = candidate;
            detail = "Exact native donor/modules reserved; no player or world mutation has occurred.";
            return true;
        }

        private bool WorldItemCurrent() => IsWorldVariant ? _variant.Escrow.Current() : ItemCurrent();

        internal sealed partial class RequestRouter
        {
            internal bool RouteWorldIntent(ControlPeer peer, in PlayerWorldFrame request,
                out InventoryStorage transfer, out string detail)
            {
                if (request.Intent.Action == TransferKind.Drop) return RouteIntent(peer, request, out transfer, out detail);
                transfer = null;
                detail = "World variant does not identify the authenticated current actor.";
                if (!CanRouteWorldIntent(peer, request)) return false;
                _routing = true;
                try { return _authority.TryQueueNativeWorldVariantIntent(request, out transfer, out detail); }
                finally { _routing = false; }
            }
        }
    }
}
