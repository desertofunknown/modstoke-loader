// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Loader;
using global::Sons.Crafting;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using TheForest.Items.Inventory;
using TheForest.Items.Special;
using TheForest.Utils;
using UnityEngine.SceneManagement;
using ModuleType = global::Sons.Inventory.ItemInstance.ItemInstanceModuleType;

namespace Modstoke.Sons
{
    internal static class ModuleStorageNative
    {
        private const int MaximumStorageEntries = 4096;

        internal static bool TryPrepareDeposit(ItemInstance item, out NativeTransfer transfer, out string detail)
        {
            transfer = null;
            bool entered = false;
            try
            {
                Crafting.EnterModuleStorageNativeCall();
                entered = true;
                Require(item is not null && !item.WasCollected, "An actual native inventory instance is required.");
                var context = new NativeContext(item._itemID);
                var stored = context.ReadStorage();
                Require(stored.Contains(item.Pointer), "The exact item is not stored in this local inventory.");
                context.RequireUnambiguous(item);
                var identity = new ModuleIdentity(item, context);
                Require(ItemModuleSnapshot.TryCapture(item, out var snapshot, out detail), detail);
                identity.RequireCurrent();
                context.RequireUnambiguous(item);
                stored.RequireSame(context.ReadStorage());
                context.RequireCurrent();
                transfer = new NativeTransfer(context, stored, identity, snapshot, deposit: true);
                detail = "Exact stored instance and module state prepared; no inventory item was removed.";
                return true;
            }
            catch (Exception error) { detail = error.Message; return false; }
            finally { if (entered) Crafting.ExitModuleStorageNativeCall(); }
        }

        internal static bool TryPrepareWithdraw(ItemModuleSnapshot snapshot,
            out NativeTransfer transfer, out string detail)
        {
            transfer = null;
            bool entered = false;
            try
            {
                Crafting.EnterModuleStorageNativeCall();
                entered = true;
                Require(snapshot != null, "A stored module snapshot is required.");
                var context = new NativeContext(snapshot.ItemId);
                var stored = context.ReadStorage();
                Require(stored.Count < MaximumStorageEntries && context.Inventory.HasRoomFor(snapshot.ItemId, 1),
                    "Native unique-instance inventory capacity is unavailable.");
                var records = snapshot.CreateNativeRecords();
                stored.RequireSame(context.ReadStorage());
                context.RequireCurrent();

                // Match native DeserializeItemsFromExternalData: construct detached, restore
                // exactly the saved module families, then enter inventory only in TryExecute.
                var item = new ItemInstance(snapshot.ItemId);
                var list = item._modules;
                Require(list is not null && !list.WasCollected && list.Count == 0,
                    "The detached native constructor did not produce an empty module list.");
                var modules = new List<ItemInstanceModule>(records.Count);
                RequireDetached();
                foreach (var record in records)
                {
                    var module = CreateSavedModule(item, record.ModuleId);
                    modules.Add(module);
                    RequireDetached();
                    Require(ItemRegistration.NativeModuleRecordMatches(module, record),
                        "A saved record does not match its exact built-in native module class.");
                    module.LoadFromSave(record);
                    RequireDetached();
                }
                snapshot.RestoreSupplemental(item, records);
                RequireDetached();
                var identity = new ModuleIdentity(item, context);
                RequireSnapshot(item, snapshot);
                RequireDetached();
                identity.RequireCurrent();
                transfer = new NativeTransfer(context, stored, identity, snapshot, deposit: false);
                detail = "Detached native instance restored with exact snapshot readback; inventory custody is unchanged.";
                return true;

                void RequireDetached()
                {
                    context.RequireCurrent();
                    context.RequireItem(item);
                    Require(item.ItemObject is null && item._modules?.Pointer == list.Pointer
                        && !list.WasCollected && list.Count == modules.Count,
                        "The reconstructed item acquired presentation custody or changed its module list.");
                    for (int i = 0; i < modules.Count; i++)
                        Require(modules[i] is not null && !modules[i].WasCollected
                            && list[i]?.Pointer == modules[i].Pointer,
                            "Detached native module identity changed during restoration.");
                    context.RequireUnambiguous(item);
                    stored.RequireSame(context.ReadStorage());
                    context.RequireCurrent();
                }
            }
            catch (Exception error) { detail = error.Message; return false; }
            finally { if (entered) Crafting.ExitModuleStorageNativeCall(); }
        }

        internal sealed class NativeTransfer
        {
            private readonly NativeContext _context;
            private readonly StoredItems _stored;
            private readonly ModuleIdentity _identity;
            private readonly bool _deposit;
            private int _consumed;
            private volatile bool _attempted;

            internal NativeTransfer(NativeContext context, StoredItems stored, ModuleIdentity identity,
                ItemModuleSnapshot snapshot, bool deposit)
            {
                _context = context;
                _stored = stored;
                _identity = identity;
                Snapshot = snapshot;
                _deposit = deposit;
            }

            internal ItemModuleSnapshot Snapshot { get; }
            internal ItemInstance Item => _identity.Item;

            // A preparation is single-use even when its final preflight refuses. In particular,
            // the attempted bit survives reentry/repeated calls and is never a refund signal.
            internal bool TryExecute(out bool attempted, out string detail)
            {
                attempted = _attempted;
                if (Interlocked.Exchange(ref _consumed, 1) != 0)
                {
                    detail = "This prepared native transfer was already consumed. Do not retry or compensate it.";
                    return false;
                }
                bool entered = false;
                try
                {
                    Crafting.EnterModuleStorageNativeCall();
                    entered = true;
                    _context.RequireCurrent();
                    _identity.RequireCurrent();
                    _context.RequireUnambiguous(Item);
                    _stored.RequireSame(_context.ReadStorage());
                    Require(_stored.Contains(Item.Pointer) == _deposit,
                        "The prepared instance has unexpected native storage custody.");
                    if (!_deposit)
                        Require(Item.ItemObject is null && _context.Inventory.HasRoomFor(Snapshot.ItemId, 1),
                            "The detached instance acquired presentation custody or native capacity changed.");
                    RequireSnapshot(Item, Snapshot);
                    _identity.RequireCurrent();
                    _context.RequireUnambiguous(Item);
                    _stored.RequireSame(_context.ReadStorage());
                    _context.RequireCurrent();

                    _attempted = attempted = true;
                    bool accepted = _deposit
                        ? _context.Inventory.RemoveItem(Snapshot.ItemId, 1, false, false, true, Item, false)
                        : _context.Inventory.AddItem(Snapshot.ItemId, 1, true, false, Item);

                    _context.RequireCurrent();
                    _identity.RequireCurrent();
                    _context.RequireUnambiguous(Item);
                    var after = _context.ReadStorage();
                    _stored.RequireDelta(after, Item.Pointer, _deposit ? -1 : 1);
                    RequireSnapshot(Item, Snapshot);
                    _identity.RequireCurrent();
                    _context.RequireUnambiguous(Item);
                    after.RequireSame(_context.ReadStorage());
                    _context.RequireCurrent();
                    Require(accepted, "The original native inventory call returned false.");
                    detail = _deposit
                        ? "The exact native instance was removed once; count, remaining identities and transferred module snapshot were preserved."
                        : "The reconstructed native instance was added once; exact stored identity, count and module snapshot were confirmed.";
                    return true;
                }
                catch (Exception error)
                {
                    attempted = _attempted;
                    detail = (attempted ? "UNCERTAIN native transfer; do not retry, refund or grant a replacement. "
                        : "No native inventory transfer was invoked. ") + error.Message;
                    return false;
                }
                finally { if (entered) Crafting.ExitModuleStorageNativeCall(); }
            }
        }

        internal sealed class NativeContext
        {
            internal readonly PlayerInventory Inventory;
            private readonly LocalPlayer _player;
            private readonly CraftingSystem _crafting;
            private readonly ItemInstanceManager _manager;
            private readonly ItemDatabase _database;
            private readonly ItemData _data;
            private readonly IHeldOnlyItemController _heldController;
            private readonly int _itemId, _loadCount, _saveCount, _sceneEvents, _sceneHandle;
            private readonly bool _networked;
            private readonly IntPtr _entity, _socket, _map, _cache;
            private readonly PlayerInventory.PlayerViews _view;

            internal NativeContext(int itemId)
            {
                Require(Crafting.OnCraftExecutionThread && GameBuild.IsRecognized,
                    "Native transfers require the initialized Unity thread and recognized game build.");
                _itemId = itemId;
                _player = LocalPlayer._instance;
                Inventory = LocalPlayer.Inventory;
                _crafting = CraftingSystem._Instance_k__BackingField;
                Require(Live(_player) && Live(Inventory) && Live(_crafting), "A live local player, inventory and crafting system are required.");
                _manager = Inventory._itemInstanceManager;
                _heldController = Inventory._HeldOnlyItemController_k__BackingField;
                _database = ItemDatabaseManager._instance;
                var cache = ItemDatabaseManager._itemsCache;
                Require(Live(_manager) && _manager._itemsMap is not null && !_manager._itemsMap.WasCollected
                    && Live(_database) && cache is not null && !cache.WasCollected && itemId > 0
                    && cache.TryGetValue(itemId, out _data) && Live(_data) && _data.Id == itemId && _data._eachInstanceIsUnique,
                    "Native module transfers require an existing unique item definition and live storage; counted native reentry is unsupported.");
                _map = _manager._itemsMap.Pointer;
                _cache = cache.Pointer;
                _loadCount = SaveLifecycle.State.BeforeLoadCount;
                _saveCount = SaveLifecycle.State.BeforeSaveCount;
                var state = GameRuntime.State;
                _sceneEvents = state.SceneEventCount;
                var scene = SceneManager.GetSceneByName(state.SceneName);
                Require(scene.IsValid() && scene.isLoaded, "A loaded game scene is required.");
                _sceneHandle = scene.handle;
                _networked = BoltNetwork.isRunning;
                _entity = _networked ? LocalPlayer.Entity?.Pointer ?? IntPtr.Zero : IntPtr.Zero;
                _socket = BoltCore._udpSocket?.Pointer ?? IntPtr.Zero;
                _view = Inventory._currentView;
                RequireCurrent();
            }

            internal void RequireCurrent()
            {
                Require(Crafting.OnCraftExecutionThread, "Native transfers require the initialized Unity thread.");
                Require(GameRuntime.IsGameScene && !GameRuntime.Environment.IsDedicatedServer && LocalPlayer.IsInWorld
                    && SaveLifecycle.State.BeforeLoadCount == _loadCount
                    && SaveLifecycle.State.BeforeSaveCount == _saveCount
                    && GameRuntime.State.SceneEventCount == _sceneEvents
                    && Crafting.CraftExecutionContextCurrent(_player, Inventory, _crafting, _loadCount, _networked, _entity),
                    "The local player, world, save boundary or native ownership changed.");
                var scene = SceneManager.GetSceneByName(GameRuntime.State.SceneName);
                var socket = BoltCore._udpSocket;
                Require(scene.IsValid() && scene.isLoaded && scene.handle == _sceneHandle
                    && (socket?.Pointer ?? IntPtr.Zero) == _socket && (socket is null || !socket.WasCollected)
                    && (_networked ? socket is not null && socket.State != UdpKit.UdpSocketState.Shutdown
                        : socket is null || socket.State == UdpKit.UdpSocketState.Shutdown),
                    "The loaded scene or Bolt session changed or is transitioning.");
                Require(Inventory._currentView == _view
                    && (_view == PlayerInventory.PlayerViews.World || _view == PlayerInventory.PlayerViews.Inventory)
                    && Inventory._currentEquipState == PlayerInventory.HandEquipState.Idle
                    && !LocalPlayer.IsInMidAction && !_crafting._inCreativeMode,
                    "Native transfers require an idle local player in world or inventory view outside creative mode.");
                Require((_heldController?.Pointer ?? IntPtr.Zero)
                    == (Inventory._HeldOnlyItemController_k__BackingField?.Pointer ?? IntPtr.Zero)
                    && (_heldController is null || !_heldController.WasCollected),
                    "The native held-only controller changed.");
                var proxy = _crafting._craftingAnimationEventsProxy;
                Require(!Live(proxy) || proxy.CurrentRecipe == null, "A native craft animation is pending.");
                var cache = ItemDatabaseManager._itemsCache;
                Require(Live(_manager) && Inventory._itemInstanceManager?.Pointer == _manager.Pointer
                    && _manager._itemsMap is not null && !_manager._itemsMap.WasCollected && _manager._itemsMap.Pointer == _map
                    && Live(_database) && ItemDatabaseManager._instance?.Pointer == _database.Pointer
                    && cache is not null && !cache.WasCollected && cache.Pointer == _cache
                    && cache.TryGetValue(_itemId, out var data) && Live(data) && data.Pointer == _data.Pointer
                    && data.Id == _itemId && data._eachInstanceIsUnique,
                    "The native manager, item map or unique item definition changed.");
            }

            internal void RequireItem(ItemInstance item)
            {
                Require(item is not null && !item.WasCollected && item._itemID == _itemId
                    && Live(item.Data) && item.Data.Pointer == _data.Pointer,
                    "The exact item's native identity or definition changed.");
            }

            internal StoredItems ReadStorage()
            {
                RequireCurrent();
                var map = _manager._itemsMap;
                Require(map.Count <= MaximumStorageEntries, "The native item map exceeds the transfer inspection bound.");
                if (!map.TryGetValue(_itemId, out var row)) return new StoredItems(null, null, Array.Empty<ItemInstance>());
                Require(row is not null && !row.WasCollected && row.Count >= 0 && row.Count <= MaximumStorageEntries
                    && row.UniqueInstances is not null && !row.UniqueInstances.WasCollected && row.UniqueInstances.Count == row.Count,
                    "Stored unique-instance count and custody are inconsistent or exceed the transfer bound.");
                var list = row.UniqueInstances;
                var items = new ItemInstance[row.Count];
                var pointers = new HashSet<IntPtr>();
                for (int i = 0; i < items.Length; i++)
                {
                    var item = list[i];
                    RequireItem(item);
                    Require(pointers.Add(item.Pointer), "Native storage contains a duplicate item identity.");
                    items[i] = item;
                }
                return new StoredItems(row, list, items);
            }

            internal void RequireUnambiguous(ItemInstance item)
            {
                RequireCurrent();
                RequireItem(item);
                RequireNoEquipmentReference(Inventory._equipmentSlots, item);
                RequireNoEquipmentReference(Inventory._equipmentSlotsNext, item);
                if (_heldController is not null)
                {
                    // Do not invoke a virtual controller query after verifying the snapshot.
                    Require(IL2CPP.il2cpp_object_get_class(_heldController.Pointer)
                        == Il2CppClassPointerStore<HeldOnlyItemController>.NativeClassPtr,
                        "An unsupported held-only controller prevents exact native custody inspection.");
                    var controller = _heldController.Cast<HeldOnlyItemController>();
                    var catalog = controller._heldOnlyItems;
                    Require(Live(controller) && controller._player?.Pointer == Inventory.Pointer
                        && catalog is not null && !catalog.WasCollected && catalog.Length <= MaximumStorageEntries
                        && controller._heldItemId != _itemId,
                        "Held-only custody is unavailable or owns the selected item type.");
                    RequireNotSame(controller._heldItemInstance, item, "The selected instance also has held-only custody.");
                    foreach (var entry in catalog)
                        Require(entry is not null && !entry.WasCollected && entry._itemId != _itemId,
                            "The held-only catalog owns this item type; native inventory transfer is unsupported.");
                }
                var projectiles = Inventory._projectileItemInstances;
                Require(projectiles is not null && !projectiles.WasCollected && projectiles.Count <= MaximumStorageEntries,
                    "Native projectile custody is unavailable or exceeds the transfer bound.");
                foreach (var entry in projectiles)
                    RequireNotSame(entry.Value, item, "The selected instance also has projectile custody.");
                var ingredients = _crafting._ingredientLayoutItemGroups;
                var results = _crafting._craftingResultLayoutItemGroups;
                Require(ingredients is not null && !ingredients.WasCollected && ingredients.Count <= Crafting.MaximumCraftingMatEntries
                    && results is not null && !results.WasCollected && results.Count <= Crafting.MaximumCraftingMatEntries,
                    "Native crafting custody is unavailable or exceeds the transfer bound.");
                int budget = MaximumStorageEntries;
                foreach (var group in ingredients)
                {
                    Require(Live(group), "A native ingredient group is unavailable.");
                    RequireNoMatReference(group._layoutItems, item, group._itemId, false, ref budget);
                }
                foreach (var group in results)
                {
                    Require(Live(group), "A native result group is unavailable.");
                    RequireNoMatReference(group._layoutItems, item, group._itemId, true, ref budget);
                }
                // Reject a corrupt alias in another stored row, not only in the selected ID.
                var map = _manager._itemsMap;
                Require(map.Count <= MaximumStorageEntries, "Native storage exceeds the transfer bound.");
                budget = MaximumStorageEntries;
                foreach (var entry in map)
                {
                    if (entry.Key == _itemId) continue;
                    var row = entry.Value;
                    Require(row is not null && !row.WasCollected && row.UniqueInstances is not null
                        && !row.UniqueInstances.WasCollected && row.UniqueInstances.Count <= budget,
                        "Another native storage row is unavailable or exceeds the transfer bound.");
                    budget -= row.UniqueInstances.Count;
                    foreach (var other in row.UniqueInstances)
                        RequireNotSame(other, item, "Another native stored row also references this instance.");
                }
                RequireCurrent();
            }
        }

        internal sealed class StoredItems(ItemInstanceManager.Items row,
            Il2CppSystem.Collections.Generic.List<ItemInstance> list, ItemInstance[] items)
        {
            internal int Count => items.Length;
            internal bool Contains(IntPtr pointer)
            {
                foreach (var item in items)
                    if (item.Pointer == pointer) return true;
                return false;
            }

            internal void RequireSame(StoredItems other)
            {
                Require((row?.Pointer ?? IntPtr.Zero) == (other.Row?.Pointer ?? IntPtr.Zero)
                    && (list?.Pointer ?? IntPtr.Zero) == (other.List?.Pointer ?? IntPtr.Zero)
                    && Count == other.Count, "Native storage row, instance list or count changed.");
                for (int i = 0; i < Count; i++)
                    Require(!items[i].WasCollected && items[i].Pointer == other.Items[i].Pointer,
                        "Native stored instance identity or ordering changed.");
            }

            private ItemInstanceManager.Items Row => row;
            private Il2CppSystem.Collections.Generic.List<ItemInstance> List => list;
            private ItemInstance[] Items => items;

            internal void RequireDelta(StoredItems other, IntPtr target, int delta)
            {
                Require(other.Count == Count + delta && Contains(target) == (delta < 0)
                    && other.Contains(target) == (delta > 0), "The native transfer did not preserve exact count and instance custody.");
                if (row is not null)
                    Require(other.Row?.Pointer == row.Pointer && other.List?.Pointer == list.Pointer,
                        "The native transfer replaced its existing storage row or instance list.");
                int index = 0;
                foreach (var prior in items)
                {
                    if (delta < 0 && prior.Pointer == target) continue;
                    Require(!prior.WasCollected && index < other.Count && prior.Pointer == other.Items[index++].Pointer,
                        "The native transfer changed another stored instance or its order.");
                }
                if (delta > 0)
                    Require(index == other.Count - 1 && other.Items[index].Pointer == target,
                        "Native storage did not append the exact reconstructed instance.");
            }
        }

        internal sealed class ModuleIdentity
        {
            internal readonly ItemInstance Item;
            private readonly NativeContext _context;
            private readonly Il2CppSystem.Collections.Generic.List<ItemInstanceModule> _list;
            private readonly ItemInstanceModule[] _modules;

            internal ModuleIdentity(ItemInstance item, NativeContext context)
            {
                Item = item;
                _context = context;
                context.RequireItem(item);
                _list = item._modules;
                Require(_list is not null && !_list.WasCollected && _list.Count is >= 1 and <= 10,
                    "The instance must contain one to ten supported native modules.");
                _modules = new ItemInstanceModule[_list.Count];
                for (int i = 0; i < _modules.Length; i++) _modules[i] = _list[i];
                RequireCurrent();
            }

            internal void RequireCurrent()
            {
                _context.RequireItem(Item);
                Require(!_list.WasCollected && Item._modules?.Pointer == _list.Pointer && _list.Count == _modules.Length,
                    "The exact module list changed.");
                for (int i = 0; i < _modules.Length; i++)
                    Require(_modules[i] is not null && !_modules[i].WasCollected && _list[i]?.Pointer == _modules[i].Pointer,
                        "A native module identity changed.");
                Require(ItemRegistration.ValidateAuthoredModuleInstance(Item, false, out var detail), detail);
            }
        }

        private static ItemInstanceModule CreateSavedModule(ItemInstance item, int moduleId)
        {
            switch ((ModuleType)moduleId)
            {
                case ModuleType.BloodReveal: item.GetOrAddModule<BloodRevealItemInstanceModule>(out var blood); return blood;
                case ModuleType.DismemberedLimb: item.GetOrAddModule<DismemberedLimbItemInstanceModule>(out var limb); return limb;
                case ModuleType.GpsLocator: item.GetOrAddModule<GPSLocatorItemInstanceModule>(out var gps); return gps;
                case ModuleType.Perishable: item.GetOrAddModule<PerishableItemInstanceModule>(out var perishable); return perishable;
                case ModuleType.RangedWeapon: item.GetOrAddModule<RangedWeaponItemInstanceModule>(out var ranged); return ranged;
                case ModuleType.StructureElement: item.GetOrAddModule<StructureElementItemInstanceModule>(out var structure); return structure;
                case ModuleType.VisualVariant: item.GetOrAddModule<VisualVariantItemInstanceModule>(out var visual); return visual;
                case ModuleType.VolumeContainer: item.GetOrAddModule<VolumeContainerItemInstanceModule>(out var volume); return volume;
                case ModuleType.ItemPlating: item.GetOrAddModule<ItemPlatingItemInstanceModule>(out var plating); return plating;
                case ModuleType.ArrowUpgrade: item.GetOrAddModule<ArrowUpgradeItemInstanceModule>(out var arrow); return arrow;
                default: throw new InvalidOperationException("The snapshot contains an unsupported native module family.");
            }
        }

        private static void RequireNoEquipmentReference(Il2CppReferenceArray<ItemInstance> slots, ItemInstance item)
        {
            Require(slots is not null && !slots.WasCollected && slots.Length <= Crafting.MaximumCraftingMatEntries,
                "Native equipment custody is unavailable or exceeds the transfer bound.");
            foreach (var slot in slots)
                RequireNotSame(slot, item, "The selected instance also has equipped or next-slot custody.");
        }

        private static void RequireNoMatReference<T>(Il2CppSystem.Collections.Generic.List<T> rows,
            ItemInstance item, int groupItemId, bool resultRows, ref int budget) where T : LayoutItem
        {
            Require(rows is not null && !rows.WasCollected && rows.Count <= budget,
                "Native mat rows are unavailable or exceed the transfer bound.");
            budget -= rows.Count;
            foreach (var row in rows)
            {
                Require(Live(row), "A native mat row is unavailable.");
                // Collected result rows retain a cached instance after deactivation, not custody.
                bool active = row.gameObject.activeSelf;
                if (resultRows && !active) continue;
                var other = row.ItemInstance;
                Require(!active || groupItemId != item._itemID || other is not null,
                    "A visible native mat row of this item type has lost its instance.");
                RequireNotSame(other, item, "The selected instance also has crafting-mat custody.");
            }
        }

        private static void RequireNotSame(ItemInstance other, ItemInstance item, string detail)
            => Require(other is null || (!other.WasCollected && other.Pointer != item.Pointer), detail);

        private static void RequireSnapshot(ItemInstance item, ItemModuleSnapshot expected)
        {
            Require(ItemModuleSnapshot.TryCapture(item, out var observed, out var detail), detail);
            Require(expected.SameState(observed), "The complete native module snapshot changed or could not be restored exactly.");
        }

        private static bool Live(UnityEngine.Object value) => Crafting.CraftExecutionLive(value);
        private static void Require(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException(detail);
        }
    }

    public static partial class Crafting
    {
        // Share the native crafting callback guard without activating the migration pipeline.
        internal static void EnterModuleStorageNativeCall()
        {
            RequireCraftTransfer(OnCraftExecutionThread, "Native module storage requires the initialized Unity thread.");
            RequireCraftTransfer(!_craftExecutionCallInProgress, "A native crafting or module-storage call is already in progress.");
            RefreshActiveCraftExecution();
            RequireCraftTransfer(_activeCraftExecution == null, "A craft is pending; observe completion before moving native items.");
            _craftExecutionCallInProgress = true;
        }

        internal static void ExitModuleStorageNativeCall() => _craftExecutionCallInProgress = false;
    }
}
