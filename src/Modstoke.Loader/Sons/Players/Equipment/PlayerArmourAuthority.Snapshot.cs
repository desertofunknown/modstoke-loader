// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Security.Cryptography;
using Modstoke.Loader;
using Modstoke.Sons.Combat;
using global::Sons.Wearable.Clothing;
using TheForest.Utils;
using UnityEngine;
using Environment = System.Environment;

namespace Modstoke.Sons
{
    public sealed partial class PlayerArmourAuthority
    {
        /// <summary>
        /// Immutable, untrusted observation tied to the full inventory baseline. Export is
        /// pointer-free; decoding and a matching digest are not server approval. Local retained
        /// identities are used only to revalidate the original paused source before retirement.
        /// </summary>
        public sealed class Snapshot
        {
            private const uint Magic = 0x314D5241; // ARM1
            private const int HeaderBytes = 120;
            private const int MaximumSlots = 12; // WearableSlots domain, not equipped capacity.
            public const int MaximumBytes = HeaderBytes + MaximumSlots * 24;
            private readonly byte[] _bytes;
            private readonly StrictCombatArmour _native;
            private readonly PlayerStats _sourceStats;
            private readonly PlayerClothingSystem _sourceClothing;
            private readonly int _thread = Environment.CurrentManagedThreadId;
            private readonly int _load = SaveLifecycle.State.BeforeLoadCount;

            public BoltMigrationPlan Plan { get; }
            public State Armour { get; }
            public string InventoryFingerprint { get; }
            public string Fingerprint { get; }

            private Snapshot(BoltMigrationPlan plan, State armour, string inventoryFingerprint,
                StrictCombatArmour native = null, PlayerStats stats = null, PlayerClothingSystem clothing = null)
            {
                Plan = plan; Armour = armour; InventoryFingerprint = inventoryFingerprint;
                _native = native; _sourceStats = stats; _sourceClothing = clothing;
                Validate();
                using var stream = new MemoryStream(MaximumBytes);
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                {
                    writer.Write(Magic);
                    writer.Write(plan.Id.ToByteArray()); writer.Write(plan.Generation); writer.Write(plan.OldNetworkId);
                    writer.Write(plan.PrefabId); writer.Write(plan.StateTypeId); writer.Write(plan.ContractId.ToByteArray());
                    writer.Write(plan.ContractVersion); writer.Write(Convert.FromHexString(inventoryFingerprint));
                    writer.Write(armour.SlotCount); writer.Write(armour.OrderCount); writer.Write(armour.NextAvailableOrderIndex);
                    writer.Write(armour.WearingGoldenClothing ? 1 : 0);
                    writer.Write(armour.GoldenGeneralMultiplier); writer.Write(armour.GoldenDemonicMultiplier);
                    for (int i = 0; i < armour.OrderCount; i++) writer.Write(armour.GetOrderSlot(i));
                    for (int i = 0; i < armour.SlotCount; i++)
                    {
                        var slot = armour.GetSlot(i);
                        writer.Write(slot.Index); writer.Write(slot.InventoryKey); writer.Write(slot.ItemId);
                        writer.Write(slot.RemainingPoints);
                    }
                }
                _bytes = stream.ToArray();
                Fingerprint = Convert.ToHexString(SHA256.HashData(_bytes));
            }

            public byte[] Export() => (byte[])_bytes.Clone();

            internal bool TryRetainSourceGraph(PlayerStats stats, PlayerInventoryBaseline inventory,
                out StrictCombatArmour native)
            {
                native = null;
                if (_native == null || Environment.CurrentManagedThreadId != _thread
                    || _load != SaveLifecycle.State.BeforeLoadCount || !Same(stats, _sourceStats)
                    || !Same(stats._armourSystem, _native.System) || !Same(_sourceClothing, LocalPlayer.ClothingSystem)
                    || !Same(_native.System._clothingSystem, _sourceClothing) || !MatchesInventory(inventory)
                    || !StrictCombatArmour.TryCapture(_native.System, out var current, out _)
                    || !_native.Matches(current)) return false;
                native = _native;
                return true;
            }

            public static bool TryReadObservation(BoltMigrationPlan expectedPlan,
                PlayerInventoryBaseline inventory, byte[] bytes, out Snapshot snapshot, out string detail)
            {
                snapshot = null;
                detail = "A complete armour observation and its exact inventory baseline are required.";
                if (!expectedPlan.IsValid || inventory?.Plan != expectedPlan || bytes == null
                    || bytes.Length < HeaderBytes || bytes.Length > MaximumBytes) return false;
                try
                {
                    using var stream = new MemoryStream((byte[])bytes.Clone(), false);
                    using var reader = new BinaryReader(stream);
                    Require(reader.ReadUInt32() == Magic, "Unknown armour observation format.");
                    var plan = new BoltMigrationPlan(new Guid(reader.ReadBytes(16)), reader.ReadUInt64(),
                        reader.ReadUInt64(), reader.ReadInt32(), reader.ReadInt32(), new Guid(reader.ReadBytes(16)), reader.ReadUInt32());
                    Require(plan == expectedPlan, "Armour migration identity does not match.");
                    string inventoryFingerprint = Convert.ToHexString(reader.ReadBytes(32));
                    int slotCount = reader.ReadInt32(), orderCount = reader.ReadInt32(), next = reader.ReadInt32();
                    int golden = reader.ReadInt32();
                    float general = reader.ReadSingle(), demonic = reader.ReadSingle();
                    Require(slotCount is >= 0 and <= MaximumSlots && orderCount is >= 0 and <= MaximumSlots
                        && golden is 0 or 1 && bytes.Length == HeaderBytes + orderCount * 4 + slotCount * 20,
                        "Invalid armour observation layout.");
                    var order = new int[orderCount];
                    for (int i = 0; i < order.Length; i++) order[i] = reader.ReadInt32();
                    var slots = new Slot[slotCount];
                    for (int i = 0; i < slots.Length; i++)
                        slots[i] = new(reader.ReadInt32(), reader.ReadUInt64(), reader.ReadInt32(), reader.ReadSingle());
                    var candidate = new Snapshot(plan, new State(slots, order, next, golden != 0, general, demonic), inventoryFingerprint);
                    Require(candidate.MatchesInventory(inventory) && candidate._bytes.AsSpan().SequenceEqual(bytes),
                        "Armour and inventory observations disagree or encoding is noncanonical.");
                    snapshot = candidate;
                    detail = "Armour observation decoded; no custody, native owner or combat readiness was accepted.";
                    return true;
                }
                catch (Exception error) { detail = "Armour observation refused: " + error.Message; return false; }
            }

            /// <summary>
            /// Read only the explicitly supplied paused SOURCE actor. LocalPlayer comparison
            /// establishes the native golden-armour owner predicate; it is never a server
            /// substitute. Reuses the existing retained native armour reader and its finite
            /// observation contract. No receiver, clothing or armour helper is initialized.
            /// </summary>
            public static bool TryCaptureSource(BoltMigrationPlan plan,
                PlayerLifecycle lifecycle, PlayerStats stats, PlayerInventoryBaseline inventory,
                out Snapshot snapshot, out string detail)
            {
                snapshot = null;
                detail = "The exact paused local source, live armour graph and current inventory baseline are required.";
                if (!GameBuild.IsRecognized || !PlayerRoles.IsAvailable
                    || !plan.IsValid || inventory?.Plan != plan || lifecycle?.IsPaused != true) return false;
                try
                {
                    int frame = Time.frameCount;
                    Require(SourceCurrent(plan, lifecycle, stats), detail);
                    Require(inventory.TryValidateLocal(out detail), detail);
                    var system = stats._armourSystem;
                    var clothing = system._clothingSystem;
                    Require(Alive(clothing) && Same(clothing, LocalPlayer.ClothingSystem)
                        && clothing.transform.IsChildOf(lifecycle.Root.transform), "Source armour clothing owner is unavailable.");
                    Require(StrictCombatArmour.TryCapture(system, out var native, out var status),
                        "Retained native armour capture refused: " + status + ".");
                    bool golden = clothing.IsWearingItem(572);
                    var slots = new Slot[native.SlotCount];
                    var order = new int[native.OrderCount];
                    for (int i = 0; i < order.Length; i++)
                    {
                        Require(native.TryGetOrderSlot(i, out int slot), "Configured armour order unavailable.");
                        order[i] = slot;
                    }
                    for (int i = 0; i < slots.Length; i++)
                    {
                        Require(native.TryGetSlot(i, out var slot), "Native armour slot unavailable.");
                        ulong key = 0;
                        if (slot.IsEquipped)
                        {
                            foreach (var item in inventory.Entries)
                                if (item.ArmourSlot == i)
                                {
                                    Require(key == 0 && item.ItemId == slot.ItemId
                                        && SameFloat(item.ArmourPoints, slot.RemainingPoints) && item.DefinitionOnly,
                                        "Worn armour definition or condition disagrees with inventory.");
                                    key = item.Key;
                                }
                            Require(key != 0, "Worn armour lacks its inventory custody key.");
                        }
                        slots[i] = new(i, key, slot.ItemId, slot.RemainingPoints);
                    }
                    var candidate = new Snapshot(plan, new State(slots, order, native.NextAvailableOrderIndex,
                        golden, native.GoldenGeneralMultiplier, native.GoldenDemonicMultiplier), inventory.Fingerprint,
                        native, stats, clothing);
                    Require(candidate.MatchesInventory(inventory), "Source armour and clothing differ from the inventory baseline.");
                    Require(StrictCombatArmour.TryCapture(system, out var current, out _)
                        && native.Matches(current) && Same(stats._armourSystem, system)
                        && Same(system._clothingSystem, clothing) && Same(LocalPlayer.ClothingSystem, clothing)
                        && clothing.IsWearingItem(572) == golden && SourceCurrent(plan, lifecycle, stats)
                        && inventory.TryValidateLocal(out detail) && frame == Time.frameCount,
                        "Source armour identity or condition changed during capture.");
                    snapshot = candidate;
                    detail = "Owner-qualified slot order, worn conditions, cursor and golden configuration captured; approval is still required.";
                    return true;
                }
                catch (Exception error) { detail = "Armour capture refused: " + error.Message; return false; }
            }

            public bool TryValidateSource(PlayerLifecycle lifecycle,
                PlayerInventoryBaseline inventory, out string detail)
            {
                detail = "Only the original same-world source capture can validate retained native identities.";
                if (_native == null || Environment.CurrentManagedThreadId != _thread
                    || _load != SaveLifecycle.State.BeforeLoadCount) return false;
                if (!TryCaptureSource(Plan, lifecycle, _sourceStats, inventory, out var current, out detail)) return false;
                if (Fingerprint == current.Fingerprint && _native.Matches(current._native)
                    && Same(_sourceStats, current._sourceStats) && Same(_sourceClothing, current._sourceClothing)) return true;
                detail = "Armour source changed; this observation cannot authorize retirement.";
                return false;
            }

            internal bool MatchesInventory(PlayerInventoryBaseline inventory)
            {
                if (inventory?.Plan != Plan || inventory.Fingerprint != InventoryFingerprint
                    || Armour.WearingGoldenClothing != (Array.IndexOf(inventory.Clothing, 572) >= 0)) return false;
                for (int i = 0; i < inventory.Armour.Length; i++)
                    if (inventory.Armour[i] != (i < Armour.SlotCount ? Armour.GetSlot(i).ItemId : 0)) return false;
                int occupied = 0, entries = 0;
                for (int i = 0; i < Armour.SlotCount; i++)
                {
                    var slot = Armour.GetSlot(i);
                    if (!slot.IsEquipped) continue;
                    if (i >= inventory.Armour.Length) return false;
                    occupied++;
                }
                foreach (var item in inventory.Entries)
                {
                    if (item.ArmourSlot < 0) continue;
                    if (item.ArmourSlot >= Armour.SlotCount || !item.DefinitionOnly || item.Quantity != 1) return false;
                    var slot = Armour.GetSlot(item.ArmourSlot);
                    if (slot.InventoryKey != item.Key || slot.ItemId != item.ItemId
                        || !SameFloat(slot.RemainingPoints, item.ArmourPoints)) return false;
                    entries++;
                }
                return occupied == entries;
            }

            private void Validate()
            {
                Require(Plan.IsValid && InventoryFingerprint?.Length == 64 && Convert.FromHexString(InventoryFingerprint).Length == 32,
                    "Invalid armour baseline identity.");
                Require(Armour != null && Armour.SlotCount <= MaximumSlots && Armour.OrderCount <= MaximumSlots
                    && float.IsFinite(Armour.GoldenGeneralMultiplier) && float.IsFinite(Armour.GoldenDemonicMultiplier),
                    "Invalid native armour configuration.");
                bool occupied = false;
                for (int i = 0; i < Armour.SlotCount; i++)
                {
                    var slot = Armour.GetSlot(i);
                    Require(slot.Index == i && slot.ItemId >= 0 && (slot.ItemId == 0) == (slot.InventoryKey == 0)
                        && float.IsFinite(slot.RemainingPoints), "Invalid armour slot observation.");
                    occupied |= slot.IsEquipped;
                    for (int j = 0; j < i; j++)
                        Require(slot.InventoryKey == 0 || slot.InventoryKey != Armour.GetSlot(j).InventoryKey,
                            "Repeated armour custody key.");
                }
                Require(!occupied || Armour.OrderCount != 0, "Occupied armour has no native depletion order.");
                for (int i = 0; i < Armour.OrderCount; i++)
                {
                    int slot = Armour.GetOrderSlot(i);
                    Require(slot >= 0 && slot < Armour.SlotCount, "Armour order references an absent slot.");
                    for (int j = 0; j < i; j++)
                        Require(slot != Armour.GetOrderSlot(j), "Repeated configured armour order slot.");
                }
            }

            private static bool SourceCurrent(BoltMigrationPlan plan,
                PlayerLifecycle lifecycle, PlayerStats stats)
                => lifecycle.IsPaused && lifecycle.OriginalNetworkId == plan.OldNetworkId
                    && Alive(lifecycle.Root) && Alive(lifecycle.OriginalEntity) && lifecycle.OriginalEntity.isAttached
                    && lifecycle.OriginalEntity.isOwner && Same(lifecycle.OriginalEntity, LocalPlayer.Entity)
                    && Alive(stats) && Same(stats, LocalPlayer.Stats) && stats.transform.IsChildOf(lifecycle.Root.transform)
                    && Alive(stats._armourSystem) && stats._armourSystem.transform.IsChildOf(lifecycle.Root.transform);

            private static bool Alive(UnityEngine.Object value)
                => value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero && value;
            private static void Require(bool condition, string detail)
            { if (!condition) throw new InvalidOperationException(detail); }
        }
    }
}
