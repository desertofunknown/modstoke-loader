// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;
using NativeJson = Newtonsoft.Json.JsonConvert;

namespace Modstoke.Sons
{
    /// <summary>
    /// An untrusted, immutable migration observation, not proof of possession. Capture while
    /// inventory writers (including module updates) are suspended, before retiring the source.
    /// Transport Export() through an authenticated migration channel; vanilla IDs cannot carry it.
    /// No capture/import operation adds, removes, equips or deserializes items into a player.
    /// </summary>
    public sealed class PlayerInventoryBaseline
    {
        public const int MaximumBytes = 1024 * 1024;
        public const int MaximumEntries = 8192;
        public const int FormatVersion = 2;
        private readonly Document _document;
        private readonly byte[] _bytes;
        private readonly IntPtr[] _localIdentities;
        public BoltMigrationPlan Plan => _document.Plan;
        public string Fingerprint { get; }
        public int EntryCount => _document.Items.Length;
        internal IReadOnlyList<Entry> Entries => _document.Items;
        internal int[] Held => _document.Held;
        internal int[] Clothing => _document.Clothing;
        internal int[] Armour => _document.Armour;

        private PlayerInventoryBaseline(Document document, IntPtr[] localIdentities = null)
        {
            _document = document;
            _localIdentities = localIdentities;
            _bytes = JsonSerializer.SerializeToUtf8Bytes(document);
            Require(_bytes.Length <= MaximumBytes, "Inventory exceeds the migration payload limit; nothing was truncated.");
            Fingerprint = Convert.ToHexString(SHA256.HashData(_bytes));
        }

        public byte[] Export() => (byte[])_bytes.Clone();

        /// <summary>Inspection copies for the approval UI. These are observations, not accepted ownership.</summary>
        public PlayerInventoryObservation[] GetItems()
        {
            var result = new PlayerInventoryObservation[_document.Items.Length];
            for (int i = 0; i < result.Length; i++)
            {
                var item = _document.Items[i];
                result[i] = new(item.Key, item.ItemId, item.Quantity, item.EquipmentSlot, item.ClothingSlots,
                    item.ArmourSlot, item.ArmourPoints, item.DefinitionOnly, item.Modules)
                    { ModuleState = item.ModuleState };
            }
            return result;
        }

        /// <summary>Decode only. Even a locally captured baseline requires explicit server acceptance.</summary>
        public static bool TryReadObservation(BoltMigrationPlan expectedPlan, byte[] bytes,
            out PlayerInventoryBaseline baseline, out string detail)
        {
            baseline = null;
            detail = "A complete baseline for this exact migration is required.";
            try
            {
                Require(bytes != null && bytes.Length is > 0 and <= MaximumBytes, detail);
                using (var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }))
                    ValidateJsonValue(parsed.RootElement);
                var document = JsonSerializer.Deserialize<Document>(bytes, new JsonSerializerOptions
                {
                    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
                });
                Require(document != null && document.Version == FormatVersion && document.Plan == expectedPlan
                    && expectedPlan.IsValid, detail);
                Validate(document);
                baseline = new(document);
                detail = "Complete client observation decoded; no server ownership has been accepted.";
                return true;
            }
            catch (Exception error) { detail = "Inventory observation rejected: " + error.Message; return false; }
        }

        public static bool TryCaptureLocal(BoltMigrationPlan plan,
            out PlayerInventoryBaseline baseline, out string detail)
        {
            baseline = null;
            detail = "Capture requires the current local player on the game thread before source retirement.";
            if (!PlayerRoles.IsAvailable || !plan.IsValid) return false;
            try
            {
                var source = LocalPlayer.Entity;
                Require(Live(source) && source.isAttached && source.networkId.PackedValue == plan.OldNetworkId, detail);
                var inventory = LocalPlayer.Inventory;
                var first = Capture(plan);
                var second = Capture(plan);
                Require(Live(source) && LocalPlayer.Entity?.Pointer == source.Pointer
                    && LocalPlayer.Inventory?.Pointer == inventory.Pointer
                    && first.LocalMatches(second), "Inventory changed during capture; keep writers suspended and recapture.");
                baseline = first;
                detail = "Exact stored instances, counts, native module saves and equipped slots captured; still untrusted.";
                return true;
            }
            catch (Exception error) { detail = "Inventory capture rejected: " + error.Message; return false; }
        }

        /// <summary>Recheck before retirement. This does not suspend inventory writers.</summary>
        public bool TryValidateLocal(out string detail)
        {
            detail = "Only the original local capture can verify retained instance identities.";
            if (_localIdentities == null) return false;
            if (!TryCaptureLocal(Plan, out var current, out detail)) return false;
            if (LocalMatches(current)) return true;
            detail = "The captured inventory is no longer current; migration must not retire this source.";
            return false;
        }

        internal bool LocalMatches(PlayerInventoryBaseline other)
            => Fingerprint == other.Fingerprint && _localIdentities != null && other._localIdentities != null
                && _localIdentities.AsSpan().SequenceEqual(other._localIdentities);

        internal bool ConfirmsModuleChange(PlayerInventoryBaseline after, int changedIndex, string modules,
            PlayerInventoryModuleState expectedModuleState = null)
        {
            if (after == null || after.Plan != Plan || changedIndex < 0 || changedIndex >= Entries.Count
                || Entries.Count != after.Entries.Count || _localIdentities == null || after._localIdentities == null
                || !_localIdentities.AsSpan().SequenceEqual(after._localIdentities)
                || !Held.AsSpan().SequenceEqual(after.Held) || !Clothing.AsSpan().SequenceEqual(after.Clothing)
                || !Armour.AsSpan().SequenceEqual(after.Armour)) return false;
            for (int i = 0; i < Entries.Count; i++)
            {
                var before = Entries[i];
                var next = after.Entries[i];
                string expected = i == changedIndex ? modules : before.Modules;
                var runtime = i == changedIndex && expectedModuleState != null ? expectedModuleState : before.ModuleState;
                if (before.Key != next.Key || before.ItemId != next.ItemId || before.Quantity != next.Quantity
                    || before.EquipmentSlot != next.EquipmentSlot || before.ClothingSlots != next.ClothingSlots
                    || before.ArmourSlot != next.ArmourSlot || before.ArmourPoints != next.ArmourPoints
                    || before.DefinitionOnly != next.DefinitionOnly || runtime != next.ModuleState
                    || (expected == null ? next.Modules != null : next.Modules == null || !JsonEqual(expected, next.Modules))) return false;
            }
            return true;
        }

        // The completed bridge has a new entity ID but retains these exact native inventories.
        internal static PlayerInventoryBaseline CaptureRetained(BoltMigrationPlan plan)
            => Capture(plan, retained: true);

        // Only the exact server-commanded stow may anticipate this one replicated ID removal.
        // Normal capture remains strict; the transaction cannot complete until it matches again.
        internal PlayerInventoryBaseline CaptureAfterHeldStow(int slot)
        {
            int id = 0;
            foreach (var entry in Entries) if (entry.EquipmentSlot == slot) id = entry.ItemId;
            Require(id > 0, "The commanded slot has no captured instance.");
            var expected = (int[])Held.Clone();
            int index = Array.IndexOf(expected, id);
            Require(index >= 0 && Array.LastIndexOf(expected, id) == index, "Held state identity is ambiguous.");
            expected[index] = 0;
            var state = LocalPlayer.Entity.GetState<IPlayerState>();
            Require(state != null && !state.WasCollected, "Retained player state unavailable.");
            var observed = ReadIds(state.HeldItems);
            Require((observed.AsSpan().SequenceEqual(Held) || observed.AsSpan().SequenceEqual(expected))
                && ReadIds(state.PlayerClothingIds).AsSpan().SequenceEqual(Clothing)
                && ReadIds(state.PlayerArmourIds).AsSpan().SequenceEqual(Armour), "Unrelated equipment publication changed during stow.");
            return Capture(Plan, expected, retained: true);
        }

        internal bool ReplicatedEquipmentMatches()
        {
            var state = LocalPlayer.Entity.GetState<IPlayerState>();
            return state != null && !state.WasCollected && ReadIds(state.HeldItems).AsSpan().SequenceEqual(Held)
                && ReadIds(state.PlayerClothingIds).AsSpan().SequenceEqual(Clothing)
                && ReadIds(state.PlayerArmourIds).AsSpan().SequenceEqual(Armour);
        }

        internal PlayerInventoryBaseline CaptureAfterHeldEquip(int itemId)
        {
            Require(itemId > 0 && Array.IndexOf(Held, itemId) < 0, "The equipped ID was already held.");
            var expected = (int[])Held.Clone();
            int index = Array.IndexOf(expected, 0);
            Require(index >= 0, "Held publication has no vacant entry.");
            expected[index] = itemId;
            var state = LocalPlayer.Entity.GetState<IPlayerState>();
            Require(state != null && !state.WasCollected, "Retained player state unavailable.");
            var observed = ReadIds(state.HeldItems);
            Require((observed.AsSpan().SequenceEqual(Held) || observed.AsSpan().SequenceEqual(expected))
                && ReadIds(state.PlayerClothingIds).AsSpan().SequenceEqual(Clothing)
                && ReadIds(state.PlayerArmourIds).AsSpan().SequenceEqual(Armour), "Unrelated equipment publication changed during equip.");
            return Capture(Plan, expected, retained: true);
        }

        internal bool ConfirmsHeldEquip(PlayerInventoryBaseline after, int slot, int storedIndex,
            ItemInstance instance)
            // The same exact instance relocation as stow, in reverse. No bucket, unrelated
            // instance, module identity or quantity may be created or removed by this path.
            => after != null && after.ConfirmsHeldStow(this, slot, storedIndex, instance, IntPtr.Zero);

        internal bool ConfirmsRemoval(PlayerInventoryBaseline after, int entryIndex,
            int quantity, ItemInstance removedInstance, IntPtr addedStorage = default)
        {
            if (after == null || after.Plan != Plan || _localIdentities == null || after._localIdentities == null
                || entryIndex < 0 || entryIndex >= Entries.Count || quantity <= 0
                || !Held.AsSpan().SequenceEqual(after.Held) || !Clothing.AsSpan().SequenceEqual(after.Clothing)
                || !Armour.AsSpan().SequenceEqual(after.Armour)) return false;
            var removed = Entries[entryIndex];
            if (removed.Quantity < quantity || removed.EquipmentSlot >= 0 || removed.ClothingSlots != 0
                || removed.ArmourSlot >= 0 || removed.DefinitionOnly) return false;
            int destination = 0;
            for (int i = 0; i < Entries.Count; i++)
            {
                var before = Entries[i];
                int remaining = before.Quantity - (i == entryIndex ? quantity : 0);
                if (remaining == 0) continue;
                if (destination >= after.Entries.Count) return false;
                var next = after.Entries[destination++];
                if (before.ItemId != next.ItemId || remaining != next.Quantity
                    || before.EquipmentSlot != next.EquipmentSlot || before.ClothingSlots != next.ClothingSlots
                    || before.ArmourSlot != next.ArmourSlot || before.ArmourPoints != next.ArmourPoints
                    || before.DefinitionOnly != next.DefinitionOnly || before.ModuleState != next.ModuleState
                    || (before.Modules == null ? next.Modules != null : next.Modules == null || !JsonEqual(before.Modules, next.Modules)))
                    return false;
            }
            if (destination != after.Entries.Count) return false;
            // Capture records each unique instance followed by its module list and module pointers.
            // Removing it may remove only that contiguous identity block; survivors cannot be replaced.
            var identities = new List<IntPtr>(_localIdentities);
            if (removedInstance != null)
            {
                if (removed.Modules == null || quantity != 1 || removedInstance.WasCollected
                    || removedInstance._modules == null) return false;
                int start = identities.IndexOf(removedInstance.Pointer);
                int count = 2 + removedInstance._modules.Count;
                if (start < 0 || count > identities.Count - start) return false;
                identities.RemoveRange(start, count);
            }
            // Reverse verification of an addition may remove one newly created storage bucket.
            if (addedStorage != IntPtr.Zero)
            {
                int start = identities.IndexOf(addedStorage);
                if (start < 0 || identities.Count - start < 3) return false;
                identities.RemoveRange(start, 3);
            }
            return CollectionsMarshal.AsSpan(identities).SequenceEqual(after._localIdentities);
        }

        internal bool ConfirmsHeldStow(PlayerInventoryBaseline after, int slot, int storedIndex,
            ItemInstance instance, IntPtr addedStorage)
        {
            if (after == null || after.Plan != Plan || _localIdentities == null || after._localIdentities == null
                || slot < 0 || slot >= 6 || after.Held.Length != Held.Length
                || storedIndex < 0 || storedIndex >= after.Entries.Count || instance == null || instance.WasCollected
                || !Clothing.AsSpan().SequenceEqual(after.Clothing) || !Armour.AsSpan().SequenceEqual(after.Armour)) return false;
            int heldIndex = -1;
            for (int i = 0; i < Entries.Count; i++) if (Entries[i].EquipmentSlot == slot) { heldIndex = i; break; }
            if (heldIndex < 0) return false;
            var held = Entries[heldIndex];
            var stored = after.Entries[storedIndex];
            if (held.Quantity != 1 || held.ItemId != instance._itemID || held.Modules == null
                || stored.ItemId != held.ItemId || stored.Quantity != 1 || stored.EquipmentSlot != -1
                || stored.ClothingSlots != 0 || stored.ArmourSlot != -1 || stored.DefinitionOnly
                || stored.ModuleState != held.ModuleState
                || stored.Modules == null || !JsonEqual(held.Modules, stored.Modules)) return false;
            int stateIndex = Array.IndexOf(Held, held.ItemId);
            if (stateIndex < 0 || Array.LastIndexOf(Held, held.ItemId) != stateIndex) return false;
            for (int i = 0; i < Held.Length; i++)
                if (after.Held[i] != (i == stateIndex ? 0 : Held[i])) return false;
            if (Entries.Count != after.Entries.Count) return false;
            int next = 0;
            for (int i = 0; i < Entries.Count; i++)
            {
                if (i == heldIndex) continue;
                if (next == storedIndex) next++;
                if (next >= after.Entries.Count) return false;
                var before = Entries[i];
                var remaining = after.Entries[next++];
                if (before.ItemId != remaining.ItemId || before.Quantity != remaining.Quantity
                    || before.EquipmentSlot != remaining.EquipmentSlot || before.ClothingSlots != remaining.ClothingSlots
                    || before.ArmourSlot != remaining.ArmourSlot || before.ArmourPoints != remaining.ArmourPoints
                    || before.DefinitionOnly != remaining.DefinitionOnly || before.ModuleState != remaining.ModuleState
                    || (before.Modules == null ? remaining.Modules != null
                        : remaining.Modules == null || !JsonEqual(before.Modules, remaining.Modules))) return false;
            }
            int count = 2 + instance._modules.Count;
            var original = new List<IntPtr>(_localIdentities);
            var current = new List<IntPtr>(after._localIdentities);
            int from = original.IndexOf(instance.Pointer), to = current.IndexOf(instance.Pointer);
            if (from < 0 || to < 0 || original.Count - from < count || current.Count - to < count) return false;
            for (int i = 0; i < count; i++) if (original[from + i] != current[to + i]) return false;
            original.RemoveRange(from, count);
            current.RemoveRange(to, count);
            if (addedStorage != IntPtr.Zero)
            {
                int index = current.IndexOf(addedStorage);
                if (index < 0 || current.Count - index < 3) return false;
                current.RemoveRange(index, 3);
            }
            return CollectionsMarshal.AsSpan(original).SequenceEqual(CollectionsMarshal.AsSpan(current));
        }

        private static PlayerInventoryBaseline Capture(BoltMigrationPlan plan,
            int[] expectedHeld = null, bool retained = false)
        {
            var inventory = LocalPlayer.Inventory;
            var clothing = LocalPlayer.ClothingSystem;
            var armour = LocalPlayer.Stats?.ArmourSystem;
            Require(Live(inventory) && Live(clothing) && Live(armour), "Local inventory/wearable systems are unavailable.");
            var view = inventory._currentView;
            var cutscene = inventory._inventoryCutscene;
            bool inventoryView = retained && view == PlayerInventory.PlayerViews.Inventory;
            Require((view == PlayerInventory.PlayerViews.World || (inventoryView && RetainedInventoryView(cutscene)))
                && inventory._equipSequenceRight == null,
                "Inventory view or equip transition is active; its transient custody is not settled.");
            if (inventory.HeldOnlyItemController?.HasItem == true)
                Require(PlayerInventoryEnforcement.TryReadHeldOnlyBaselineCustody(plan,
                    inventory, out _, out _), "Carried-only items lack their separate retained custody owner.");
            var next = inventory._equipmentSlotsNext;
            var slots = inventory._equipmentSlots;
            var slotIds = inventory._equipmentSlotsIds;
            Require(next != null && slots != null && slotIds != null && slots.Length == 6
                && next.Length == slots.Length && slotIds.Length == slots.Length, "Unknown equipment slot layout.");
            for (int i = 0; i < next.Length; i++) Require(next[i] == null, "An equipment transition still owns a next-slot instance.");
            Require(inventory._projectileItemInstances != null && inventory._projectileItemInstances.Count == 0,
                "Projectile instance custody must be resolved before inventory migration.");
            Require(clothing._itemIdsToAddBackToInventory != null && clothing._itemIdsToAddBackToInventory.Count == 0
                && clothing._itemInstancesToAddBackToInventory != null && clothing._itemInstancesToAddBackToInventory.Count == 0
                && clothing._clothingIdsCurrentlyLoading != null && clothing._clothingIdsCurrentlyLoading.Count == 0
                && clothing._cachedClothingPriorToGoldenArmour != null && clothing._cachedClothingPriorToGoldenArmour.Count == 0,
                "Pending clothing return/load or golden-armour clothing custody is unsupported.");
            var manager = inventory._itemInstanceManager;
            Require(Live(manager) && manager._itemsMap != null, "Stored inventory is unavailable.");
            // Local-only identities detect replacement/reordering of otherwise identical copies.
            // Native addresses never leave this process and never establish server trust.
            var identities = new List<IntPtr> { LocalPlayer.GameObject.Pointer, inventory.Pointer, manager.Pointer,
                manager._itemsMap.Pointer, slots.Pointer, next.Pointer, slotIds.Pointer, clothing.Pointer, armour.Pointer };
            var entries = new List<Entry>();
            var pointers = new HashSet<IntPtr>();
            var ids = new List<int>();
            foreach (int id in manager._itemsMap.Keys) ids.Add(id);
            ids.Sort();
            foreach (int id in ids)
            {
                var stored = manager._itemsMap[id];
                var data = ItemDatabaseManager.ItemById(id);
                Require(Live(data) && data.Id == id && stored != null && !stored.WasCollected
                    && stored.Count >= 0 && stored.UniqueInstances != null, "Invalid stored item definition or count.");
                identities.Add(stored.Pointer); identities.Add(stored.UniqueInstances.Pointer); identities.Add(data.Pointer);
                if (data._eachInstanceIsUnique)
                {
                    Require(stored.Count == stored.UniqueInstances.Count, "Unique instance count disagrees with native storage.");
                    foreach (var item in stored.UniqueInstances)
                        AddInstance(entries, pointers, identities, item, id, -1, 0);
                }
                else
                {
                    Require(stored.UniqueInstances.Count == 0, "A nonunique stack has unexplained instance references.");
                    if (stored.Count > 0) entries.Add(new Entry { ItemId = id, Quantity = stored.Count });
                }
                Require(entries.Count <= MaximumEntries, "Inventory entry limit exceeded; nothing was truncated.");
            }
            for (int slot = 0; slot < slots.Length; slot++)
            {
                var item = slots[slot];
                Require(slotIds[slot] == (item?._itemID ?? 0), "Equipment slot ID and instance disagree.");
                if (item != null) AddInstance(entries, pointers, identities, item, item._itemID, slot, 0);
            }
            Require(clothing._clothingHookupData != null && armour._armourSlotData != null, "Wearable slots unavailable.");
            identities.Add(clothing._clothingHookupData.Pointer); identities.Add(armour._armourSlotData.Pointer);
            foreach (var hookup in clothing._clothingHookupData)
            {
                Require(hookup != null && !hookup.WasCollected, "Clothing slot unavailable.");
                identities.Add(hookup.Pointer);
                if (hookup.ClothingPiece == null)
                {
                    Require(hookup.ItemInstance == null, "Clothing instance has no definition.");
                    continue;
                }
                Require(Live(hookup.ClothingPiece), "Clothing definition expired.");
                identities.Add(hookup.ClothingPiece.Pointer);
                int id = hookup.ClothingPiece._itemId;
                int mask = (int)hookup.Slot;
                Require(mask == (int)hookup.ClothingPiece._slot, "Clothing slot mask and definition disagree.");
                if (hookup.ItemInstance != null) AddInstance(entries, pointers, identities, hookup.ItemInstance, id, -1, mask);
                else entries.Add(new Entry { ItemId = id, Quantity = 1, ClothingSlots = mask, DefinitionOnly = true });
            }
            foreach (var slot in armour._armourSlotData)
            {
                Require(slot != null && !slot.WasCollected, "Armour slot unavailable.");
                identities.Add(slot.Pointer);
                if (slot.ArmourPiece == null) continue;
                Require(Live(slot.ArmourPiece), "Armour definition expired.");
                identities.Add(slot.ArmourPiece.Pointer);
                // Worn armour is a native definition plus slot/condition, not an ItemInstance.
                entries.Add(new Entry { ItemId = slot.ArmourPiece._itemId, Quantity = 1,
                    ArmourSlot = (int)slot.Slot, ArmourPoints = slot.RemainingArmourpoints, DefinitionOnly = true });
            }
            for (int i = 0; i < entries.Count; i++) entries[i].Key = (ulong)i + 1;
            var state = LocalPlayer.Entity.GetState<IPlayerState>();
            Require(state != null && !state.WasCollected, "Source player state unavailable.");
            var document = new Document { Version = FormatVersion, Plan = plan, Items = entries.ToArray(), Held = expectedHeld ?? ReadIds(state.HeldItems),
                Clothing = ReadIds(state.PlayerClothingIds), Armour = ReadIds(state.PlayerArmourIds) };
            Validate(document);
            Require(LocalPlayer.Inventory?.Pointer == inventory.Pointer && inventory._currentView == view
                && (!inventoryView || (inventory._inventoryCutscene?.Pointer == cutscene.Pointer
                    && RetainedInventoryView(cutscene))), "Inventory view changed during custody capture.");
            return new(document, identities.ToArray());
        }

        private static bool RetainedInventoryView(global::Sons.Cutscenes.InventoryCutscene cutscene)
            => Live(cutscene) && cutscene._initialized && cutscene._isPlayingLocally
                && !cutscene._isShuttingDown && !cutscene._isCleaningUp
                && !cutscene._inQuickSelectMode && !cutscene._inConstructionMaterialMode;

        private static void AddInstance(List<Entry> entries, HashSet<IntPtr> pointers, List<IntPtr> identities, ItemInstance item,
            int id, int slot, int clothing)
        {
            Require(item != null && !item.WasCollected && item._itemID == id && pointers.Add(item.Pointer),
                "An instance is missing or has ambiguous custody in multiple slots/collections.");
            Require(item._modules != null && !item._modules.WasCollected, "Native module list is unavailable.");
            identities.Add(item.Pointer); identities.Add(item._modules.Pointer);
            foreach (var module in item._modules)
            {
                Require(module != null && !module.WasCollected, "Native module identity is unavailable.");
                identities.Add(module.Pointer);
            }
            entries.Add(new Entry { ItemId = id, Quantity = 1, EquipmentSlot = slot, ClothingSlots = clothing,
                Modules = CaptureModules(item), ModuleState = CaptureModuleState(item) });
        }

        internal static PlayerInventoryModuleState CaptureModuleState(ItemInstance item)
        {
            Require(item != null && !item.WasCollected, "Native item is unavailable.");
            float? volume = null;
            bool? fresh = null;
            uint? blood = null;
            if (item.TryGetModule<VolumeContainerItemInstanceModule>(out var container))
            {
                Require(container != null && !container.WasCollected, "Container module is unavailable.");
                volume = container._CurrentVolume_k__BackingField;
            }
            if (item.TryGetModule<DismemberedLimbItemInstanceModule>(out var limb))
            {
                Require(limb != null && !limb.WasCollected, "Limb module is unavailable.");
                fresh = limb._isFresh;
                var color = limb._bloodReveal;
                blood = (uint)color.r | (uint)color.g << 8 | (uint)color.b << 16 | (uint)color.a << 24;
            }
            var result = new PlayerInventoryModuleState(volume, fresh, blood);
            ValidateModuleState(result);
            return result;
        }

        // Baseline admission must use the entry overload, not the save-only action helper.
        // Restore only detached ledger instances; never mutate the retained source inventory.
        internal static ItemInstance RestoreInstance(Entry entry)
            => RestoreInstance(entry, out _);

        internal static ItemInstance RestoreInstance(Entry entry, out ItemInstance allocated)
        {
            allocated = null;
            Require(entry != null && entry.Modules != null && entry.ModuleState != null,
                "A complete instance baseline is required.");
            ValidateModuleState(entry.ModuleState);
            var item = RestoreInstance(entry.ItemId, entry.Modules, out allocated);
            var shape = CaptureModuleState(item);
            var state = entry.ModuleState;
            Require(shape.ContainerVolume.HasValue == state.ContainerVolume.HasValue
                && shape.LimbIsFresh.HasValue == state.LimbIsFresh.HasValue,
                "Supplemental module state does not match the restored module set.");
            if (state.ContainerVolume is { } volume)
            {
                Require(item.TryGetModule<VolumeContainerItemInstanceModule>(out var container), "Container module missing.");
                container._CurrentVolume_k__BackingField = volume;
            }
            if (state.LimbIsFresh is { } fresh)
            {
                Require(item.TryGetModule<DismemberedLimbItemInstanceModule>(out var limb), "Limb module missing.");
                limb._isFresh = fresh;
                uint blood = state.LimbBloodReveal.Value;
                limb._bloodReveal = new Color32((byte)(blood & 255), (byte)((blood >> 8) & 255),
                    (byte)((blood >> 16) & 255), (byte)(blood >> 24));
            }
            Require(MatchesInstance(entry, item),
                "Restored instance did not preserve its complete baseline.");
            return item;
        }

        internal static bool MatchesInstance(Entry entry, ItemInstance item)
            => entry != null && item != null && !item.WasCollected && entry.ItemId == item._itemID
                && entry.Modules != null && entry.ModuleState != null
                && entry.ModuleState == CaptureModuleState(item) && JsonEqual(entry.Modules, CaptureModules(item));

        private static void ValidateModuleState(PlayerInventoryModuleState state)
        {
            Require(state != null && (state.ContainerVolume == null
                || (float.IsFinite(state.ContainerVolume.Value) && state.ContainerVolume.Value >= 0))
                && state.LimbIsFresh.HasValue == state.LimbBloodReveal.HasValue,
                "Invalid supplemental native module state.");
        }

        internal static string CaptureModules(ItemInstance item)
        {
            Require(item != null && !item.WasCollected && item._modules != null && !item._modules.WasCollected
                && item._modules.Count is >= 0 and <= 32,
                "Item modules unavailable or too numerous.");
            var save = item.GetModuleSaveData();
            Require(save != null && !save.WasCollected && save.Count == item._modules.Count,
                "Native save omitted an instance module.");
            var kinds = new HashSet<int>();
            for (int i = 0; i < save.Count; i++)
                Require(item._modules[i] != null && !item._modules[i].WasCollected
                    && save[i] != null && !save[i].WasCollected && kinds.Add(save[i].ModuleId),
                    "A native module save is missing or ambiguous.");
            string json = NativeJson.SerializeObject(save, Settings());
            ValidateModuleJson(json);
            return json;
        }

        internal static ItemInstance RestoreInstance(int id, string json)
            => RestoreInstance(id, json, out _);

        internal static ItemInstance RestoreInstance(int id, string json, out ItemInstance allocated)
        {
            allocated = null;
            ValidateModuleJson(json);
            var save = NativeJson.DeserializeObject<Il2CppSystem.Collections.Generic.List<ItemInstanceModule.SaveData>>(json, Settings());
            Require(save != null && save.Count <= 32
                && JsonEqual(json, NativeJson.SerializeObject(save, Settings())), "Native module reader did not preserve every saved value.");
            var item = new ItemInstance(id);
            // Keep the native allocation reachable for caller cleanup if restoration fails.
            allocated = item;
            Require(item._modules != null && item._modules.Count == save.Count,
                "Native item construction does not reproduce this module set; no partial inventory will be accepted.");
            var consumed = new HashSet<int>();
            foreach (var module in item._modules)
            {
                Require(module != null && !module.WasCollected, "Native item module unavailable.");
                var identity = module.GetSaveData();
                Require(identity != null && consumed.Add(identity.ModuleId), "Native module identity is ambiguous.");
                ItemInstanceModule.SaveData source = null;
                foreach (var entry in save) if (entry != null && entry.ModuleId == identity.ModuleId) source = entry;
                Require(source != null, "Native module set differs from the captured set.");
                module.LoadFromSave(source);
                // Native SetState restarts nonpositive timers. Restore the saved value AFTER
                // its initialization/callbacks, including an already-expired or terminal timer.
                if (module.TryCast<PerishableItemInstanceModule>() is { } perishable
                    && source.TryCast<PerishableItemInstanceModule.PerishableSaveData>() is { } decay)
                {
                    perishable._remainingSecondsUntilDecay = decay.TimeRemainingInState;
                    perishable._PauseDecay_k__BackingField = decay.PauseDecay;
                }
            }
            Require(JsonEqual(json, CaptureModules(item)), "Native item module restoration was lossy; baseline rejected in full.");
            return item;
        }

        private static Newtonsoft.Json.JsonSerializerSettings Settings()
        {
            var settings = new Newtonsoft.Json.JsonSerializerSettings
            {
                TypeNameHandling = Newtonsoft.Json.TypeNameHandling.None,
                MetadataPropertyHandling = Newtonsoft.Json.MetadataPropertyHandling.Ignore,
                MissingMemberHandling = Newtonsoft.Json.MissingMemberHandling.Error
            };
            var converters = new Il2CppSystem.Collections.Generic.List<Newtonsoft.Json.JsonConverter>();
            converters.Add(new ItemInstanceModuleSaveDataJsonConverter());
            settings.Converters = converters.Cast<Il2CppSystem.Collections.Generic.IList<Newtonsoft.Json.JsonConverter>>();
            return settings;
        }

        internal static bool JsonEqual(string left, string right) => JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right));
        internal static bool OnlyModuleChanged(string before, string after, int moduleIndex)
        {
            ValidateModuleJson(before);
            ValidateModuleJson(after);
            var left = JsonNode.Parse(before) as JsonArray;
            var right = JsonNode.Parse(after) as JsonArray;
            if (left == null || right == null || left.Count != right.Count || moduleIndex < 0 || moduleIndex >= left.Count) return false;
            for (int i = 0; i < left.Count; i++)
            {
                if (!JsonNode.DeepEquals(left[i]["ModuleId"], right[i]["ModuleId"])) return false;
                if (i != moduleIndex && !JsonNode.DeepEquals(left[i], right[i])) return false;
            }
            return true;
        }
        private static void ValidateModuleJson(string json)
        {
            Require(json != null && Encoding.UTF8.GetByteCount(json) <= 65536, "Missing/oversized native module save.");
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            Require(parsed.RootElement.ValueKind == JsonValueKind.Array && parsed.RootElement.GetArrayLength() <= 32,
                "Native module data must be a complete bounded array.");
            ValidateJsonValue(parsed.RootElement);
            var ids = new HashSet<int>();
            foreach (var module in parsed.RootElement.EnumerateArray())
                Require(module.ValueKind == JsonValueKind.Object
                    && module.TryGetProperty("ModuleId", out var id) && id.TryGetInt32(out int value) && ids.Add(value),
                    "Native module identities are missing or duplicated.");
        }

        private static void ValidateJsonValue(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    Require(!property.Name.StartsWith('$') && names.Add(property.Name), "JSON type metadata or duplicate field rejected.");
                    ValidateJsonValue(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var child in value.EnumerateArray()) ValidateJsonValue(child);
        }

        private static void Validate(Document document)
        {
            Require(document.Items != null && document.Items.Length <= MaximumEntries, "Missing/oversized inventory.");
            ValidateIds(document.Held); ValidateIds(document.Clothing); ValidateIds(document.Armour);
            var keys = new HashSet<ulong>();
            var slots = new HashSet<int>();
            var armour = new HashSet<int>();
            var heldIds = new List<int>();
            var clothingIds = new List<int>();
            int clothingMask = 0;
            foreach (var item in document.Items)
            {
                Require(item != null && item.Key > 0 && item.Key <= MaximumEntries && keys.Add(item.Key) && item.ItemId > 0 && item.Quantity > 0
                    && item.EquipmentSlot is >= -1 and < 6 && item.ClothingSlots is >= 0 and < 128
                    && item.ArmourSlot >= -1 && item.ArmourSlot < document.Armour.Length
                    && float.IsFinite(item.ArmourPoints) && item.ArmourPoints >= 0, "Invalid inventory entry.");
                int locations = (item.EquipmentSlot >= 0 ? 1 : 0) + (item.ClothingSlots != 0 ? 1 : 0) + (item.ArmourSlot >= 0 ? 1 : 0);
                Require(locations <= 1 && (locations == 0 || item.Quantity == 1)
                    && (item.Modules == null || item.Quantity == 1)
                    && (!item.DefinitionOnly || (item.Modules == null && (item.ClothingSlots != 0 || item.ArmourSlot >= 0)))
                    && (item.EquipmentSlot < 0 || item.Modules != null)
                    && (item.ArmourSlot < 0 || item.DefinitionOnly), "Ambiguous item custody or module ownership.");
                Require((item.Modules == null) == (item.ModuleState == null), "Incomplete instance module baseline.");
                if (item.Modules != null) { ValidateModuleJson(item.Modules); ValidateModuleState(item.ModuleState); }
                if (item.EquipmentSlot >= 0)
                {
                    Require(slots.Add(item.EquipmentSlot), "Duplicate equipment slot.");
                    heldIds.Add(item.ItemId);
                }
                if (item.ClothingSlots != 0)
                {
                    Require((clothingMask & item.ClothingSlots) == 0, "Overlapping clothing custody.");
                    clothingMask |= item.ClothingSlots;
                    clothingIds.Add(item.ItemId);
                }
                if (item.ArmourSlot >= 0)
                    Require(armour.Add(item.ArmourSlot) && document.Armour[item.ArmourSlot] == item.ItemId,
                        "Armour instance and replicated slot disagree.");
            }
            Require(SameIds(heldIds, document.Held) && SameIds(clothingIds, document.Clothing),
                "Equipped instances do not exactly account for the replicated equipment IDs.");
            for (int i = 0; i < document.Armour.Length; i++)
                Require(document.Armour[i] == 0 || armour.Contains(i), "A replicated armour slot has no native condition record.");
        }

        private static bool SameIds(List<int> expected, int[] observed)
        {
            var nonzero = new List<int>();
            foreach (int id in observed) if (id != 0) nonzero.Add(id);
            expected.Sort(); nonzero.Sort();
            return expected.Count == nonzero.Count
                && CollectionsMarshal.AsSpan(expected).SequenceEqual(CollectionsMarshal.AsSpan(nonzero));
        }
        private static void ValidateIds(int[] ids)
        { Require(ids != null && ids.Length is > 0 and <= 32, "Invalid equipment state shape."); foreach (int id in ids) Require(id >= 0, "Invalid equipment ID."); }
        private static int[] ReadIds(Bolt.NetworkArray_Integer ids)
        {
            Require(ids != null && !ids.WasCollected && ids.Length is > 0 and <= 32, "Equipment state unavailable.");
            var result = new int[ids.Length]; for (int i = 0; i < result.Length; i++) result[i] = ids[i]; return result;
        }
        private static bool Live(UnityEngine.Object value) => value != null && !value.WasCollected && value;
        internal static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }

        internal sealed class Document
        {
            public Document() { }
            public int Version { get; set; }
            public BoltMigrationPlan Plan { get; set; }
            public Entry[] Items { get; set; }
            public int[] Held { get; set; }
            public int[] Clothing { get; set; }
            public int[] Armour { get; set; }
        }
        internal sealed class Entry
        {
            public Entry() { }
            public ulong Key { get; set; }
            public int ItemId { get; set; }
            public int Quantity { get; set; }
            public string Modules { get; set; }
            public PlayerInventoryModuleState ModuleState { get; set; }
            public int EquipmentSlot { get; set; } = -1;
            public int ClothingSlots { get; set; }
            public int ArmourSlot { get; set; } = -1;
            public float ArmourPoints { get; set; }
            public bool DefinitionOnly { get; set; }
        }
    }

    public readonly record struct PlayerInventoryObservation(ulong InstanceKey, int ItemId,
        int Quantity, int EquipmentSlot, int ClothingSlots, int ArmourSlot, float ArmourPoints,
        bool DefinitionOnly, string NativeModuleSaveData)
    {
        public PlayerInventoryModuleState ModuleState { get; init; }
    }

    /// <summary>Native fields omitted or quantized by module save data; not an ownership claim.</summary>
    public sealed record PlayerInventoryModuleState(float? ContainerVolume,
        bool? LimbIsFresh, uint? LimbBloodReveal);
}
