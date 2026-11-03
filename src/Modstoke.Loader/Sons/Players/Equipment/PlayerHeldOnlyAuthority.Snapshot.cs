// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modstoke.Loader;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using TheForest.Items.Inventory;
using TheForest.Items.Special;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerHeldOnlyAuthority
    {
        public readonly record struct CatalogItem([property: JsonRequired] int ItemId,
            [property: JsonRequired] int HeldCapacity, [property: JsonRequired] bool EachInstanceIsUnique);

        /// <summary>Native source values requiring the same independent approval as held custody.</summary>
        public readonly record struct DropConfiguration([property: JsonRequired] float Force,
            [property: JsonRequired] float UpForce);

        /// <summary>Amount is native gameplay amount, not physical custody or the renderer-clamped Bolt count.</summary>
        public readonly record struct HeldState([property: JsonRequired] int ItemId,
            [property: JsonRequired] int Count, [property: JsonRequired] int Amount,
            [property: JsonRequired] bool InfiniteHack, [property: JsonRequired] bool PlaceFullLoadHack)
        {
            [JsonIgnore]
            public bool HasItem => Count > 0;
        }

        /// <summary>Untrusted observation. The catalog and settings also require explicit server approval.</summary>
        public sealed class Snapshot
        {
            public const int MaximumBytes = PlayerInventoryBaseline.MaximumBytes;
            private readonly Document _document;
            private readonly byte[] _bytes;
            private readonly IntPtr[] _identities;
            private readonly int _thread, _load;
            public BoltMigrationPlan Plan => _document.Plan;
            public HeldState State => _document.State;
            public DropConfiguration DropProfile => _document.DropProfile;
            public string Fingerprint { get; }
            public string NativeModuleSaveData => _document.Modules;
            public PlayerInventoryModuleState ModuleState => _document.ModuleState;

            internal Snapshot(Document document, IntPtr[] identities = null)
            {
                Validate(document);
                _document = document;
                _identities = identities;
                _thread = Environment.CurrentManagedThreadId;
                _load = SaveLifecycle.State.BeforeLoadCount;
                _bytes = JsonSerializer.SerializeToUtf8Bytes(document);
                Require(_bytes.Length <= MaximumBytes, "Held-only snapshot exceeds its bound; nothing was truncated.");
                Fingerprint = Convert.ToHexString(SHA256.HashData(_bytes));
            }

            public byte[] Export() => (byte[])_bytes.Clone();
            public CatalogItem[] GetCatalog() => (CatalogItem[])_document.Catalog.Clone();

            public static bool TryReadObservation(BoltMigrationPlan plan, byte[] bytes,
                out Snapshot snapshot, out string detail)
            {
                snapshot = null;
                detail = "A complete held-only observation for the exact migration is required.";
                try
                {
                    Require(plan.IsValid && bytes != null && bytes.Length is > 0 and <= MaximumBytes, detail);
                    using (var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 }))
                        ValidateJson(parsed.RootElement);
                    var document = JsonSerializer.Deserialize<Document>(bytes, JsonReadOptions);
                    Require(document != null && document.Plan == plan, detail);
                    snapshot = new Snapshot(document);
                    detail = "Held-only observation decoded; no custody has been accepted.";
                    return true;
                }
                catch (Exception error) { detail = "Held-only observation rejected: " + error.Message; return false; }
            }

            /// <summary>
            /// Capture the explicitly supplied source inventory under its actual migration freeze.
            /// Resolves entries through native GetItemCache (which can populate its lazy cache).
            /// Does not start a controller, change custody, scan a remote carrier, or substitute
            /// the host's LocalPlayer.
            /// </summary>
            public static bool TryCaptureSource(BoltMigrationPlan plan,
                PlayerLifecycle lifecycle, PlayerInventory inventory,
                out Snapshot snapshot, out string detail)
            {
                snapshot = null;
                detail = "An exact paused source lifecycle and its native held-only controller are required.";
                if (!PlayerRoles.IsAvailable || !GameBuild.IsRecognized) return false;
                try
                {
                    Require(plan.IsValid && lifecycle != null && lifecycle.IsPaused
                        && lifecycle.OriginalNetworkId == plan.OldNetworkId && Alive(lifecycle.Root)
                        && Alive(inventory) && inventory.transform.IsChildOf(lifecycle.Root.transform)
                        && PlayerLifecycle.IsInventoryIngressFenced(inventory, out bool frozen)
                        && frozen, detail);
                    var controller = inventory.HeldOnlyItemController?.TryCast<HeldOnlyItemController>();
                    Require(Alive(controller) && controller._player?.Pointer == inventory.Pointer
                        && controller.transform.IsChildOf(lifecycle.Root.transform), detail);
                    var first = Capture(plan, inventory, controller);
                    var second = Capture(plan, inventory, controller);
                    Require(lifecycle.IsPaused && first.LocalMatches(second), "Held-only custody changed during capture.");
                    snapshot = first;
                    detail = "Raw held count, native Amount, initialized catalog and representative instance captured; still untrusted.";
                    return true;
                }
                catch (Exception error) { detail = "Held-only capture rejected: " + error.Message; return false; }
            }

            public bool TryValidateSource(PlayerLifecycle lifecycle, PlayerInventory inventory,
                out string detail)
            {
                detail = "Only the original same-world source observation can validate native identities.";
                if (_identities == null || Environment.CurrentManagedThreadId != _thread
                    || _load != SaveLifecycle.State.BeforeLoadCount) return false;
                if (!TryCaptureSource(Plan, lifecycle, inventory, out var current, out detail)) return false;
                if (LocalMatches(current)) return true;
                detail = "Held-only source identity or state changed; do not retire the source.";
                return false;
            }

            /// <summary>
            /// Capture-only integration endpoint after an exact source native action has settled.
            /// LocalPlayer is used solely on the verified non-server retained-rig bridge. This
            /// observation neither grants permission to execute a native action nor proves its
            /// callbacks, delayed effects or destination custody completed.
            /// </summary>
            internal static bool TryCaptureRetained(PlayerBridge bridge, PlayerInventory inventory,
                HeldOnlyItemController controller, out Snapshot snapshot, out string detail)
            {
                snapshot = null;
                detail = "The exact current source bridge, retained inventory and held-only controller are required.";
                if (!PlayerRoles.IsAvailable || !GameBuild.IsRecognized
                    || bridge == null || bridge.IsServer || !bridge.TryReadCurrentTarget(out var target)) return false;
                try
                {
                    Require(Alive(inventory) && Alive(controller) && Alive(LocalPlayer.GameObject)
                        && LocalPlayer.Inventory?.Pointer == inventory.Pointer
                        && inventory.transform.IsChildOf(LocalPlayer.GameObject.transform)
                        && controller.transform.IsChildOf(LocalPlayer.GameObject.transform)
                        && inventory.HeldOnlyItemController?.Pointer == controller.Pointer
                        && controller._player?.Pointer == inventory.Pointer, detail);
                    var first = Capture(bridge.Plan, inventory, controller);
                    var second = Capture(bridge.Plan, inventory, controller);
                    Require(first.LocalMatches(second) && bridge.TryReadCurrentTarget(out var current)
                        && current.Pointer == target.Pointer && LocalPlayer.Inventory?.Pointer == inventory.Pointer,
                        "Retained held-only identity changed during observation.");
                    snapshot = first;
                    detail = "Exact retained held-only observation captured; native-effect completion is a separate requirement.";
                    return true;
                }
                catch (Exception error) { detail = "Retained held-only observation rejected: " + error.Message; return false; }
            }

            private bool LocalMatches(Snapshot other) => Fingerprint == other.Fingerprint
                && _identities != null && other._identities != null
                && _identities.AsSpan().SequenceEqual(other._identities);

            internal bool MatchesLocalCustody(Snapshot other) => other != null && LocalMatches(other);

            internal static bool TryCaptureGuarded(BoltMigrationPlan plan, PlayerInventory inventory,
                HeldOnlyItemController controller, out Snapshot snapshot, out string detail)
            {
                snapshot = null;
                detail = "Guarded capture requires the actual frozen local controller and unchanged native identities.";
                if (!PlayerRoles.IsAvailable || !GameBuild.IsRecognized || !plan.IsValid
                    || !Alive(inventory) || !Alive(controller) || LocalPlayer.Inventory?.Pointer != inventory.Pointer
                    || !PlayerLifecycle.IsInventoryIngressFenced(inventory, out bool frozen) || !frozen) return false;
                try
                {
                    var first = Capture(plan, inventory, controller);
                    var second = Capture(plan, inventory, controller);
                    if (!first.LocalMatches(second)) return false;
                    snapshot = first;
                    return true;
                }
                catch (Exception error) { detail = "Guarded held capture rejected: " + error.Message; return false; }
            }

            private static Snapshot Capture(BoltMigrationPlan plan, PlayerInventory inventory,
                HeldOnlyItemController controller)
            {
                var nativeCatalog = controller._heldOnlyItems;
                Require(nativeCatalog != null && !nativeCatalog.WasCollected
                    && nativeCatalog.Length is > 0 and <= 256, "Native held-only catalog is unavailable.");
                var catalog = new CatalogItem[nativeCatalog.Length];
                var identities = new List<IntPtr> { inventory.Pointer, controller.Pointer, nativeCatalog.Pointer };
                for (int i = 0; i < catalog.Length; i++)
                {
                    var entry = nativeCatalog[i];
                    Require(entry != null && !entry.WasCollected, "Native controller catalog entry is unavailable.");
                    var data = entry.GetItemCache();
                    var held = entry._held;
                    Require(Alive(data) && entry._itemCache?.Pointer == data.Pointer && data.Id == entry._itemId
                        && ItemDatabaseManager.ItemById(data.Id)?.Pointer == data.Pointer
                        && held != null && !held.WasCollected && held.Length is > 0 and <= 8192,
                        "Native controller catalog is not initialized or does not match ItemDatabaseManager.");
                    catalog[i] = new(data.Id, held.Length, data._eachInstanceIsUnique);
                    identities.Add(entry.Pointer); identities.Add(data.Pointer); identities.Add(held.Pointer);
                    for (int j = 0; j < held.Length; j++)
                    {
                        Require(Alive(held[j]), "A native held renderer object is unavailable.");
                        identities.Add(held[j].Pointer);
                    }
                }
                var item = controller._heldItemInstance;
                string modules = null;
                PlayerInventoryModuleState moduleState = null;
                if (item != null)
                {
                    Require(!item.WasCollected && item._itemID == controller._heldItemId
                        && Alive(item.Data) && ItemDatabaseManager.ItemById(item._itemID)?.Pointer == item.Data.Pointer,
                        "Held representative instance does not match its native definition.");
                    modules = PlayerInventoryBaseline.CaptureModules(item);
                    moduleState = PlayerInventoryBaseline.CaptureModuleState(item);
                    identities.Add(item.Pointer); identities.Add(item._modules.Pointer);
                    foreach (var module in item._modules) identities.Add(module.Pointer);
                }
                var state = new HeldState(controller._heldItemId, controller._heldCount, controller.Amount,
                    controller.InfiniteHack, controller.PlaceFullLoadHack);
                Require(controller.HasItem == state.HasItem && controller.HeldItem?.Pointer == item?.Pointer,
                    "Native held-only getters disagree with their captured fields.");
                Require(inventory.HeldOnlyItemController?.Pointer == controller.Pointer
                    && controller._player?.Pointer == inventory.Pointer, "Controller ownership changed during capture.");
                return new Snapshot(new Document { Plan = plan, Catalog = catalog, State = state,
                    DropProfile = new(controller._logDropForce, controller._logDropUpForce),
                    Modules = modules, ModuleState = moduleState }, identities.ToArray());
            }

            internal PlayerInventoryBaseline.Entry InstanceEntry() => _document.Modules == null ? null
                : new PlayerInventoryBaseline.Entry { ItemId = State.ItemId,
                    Modules = _document.Modules, ModuleState = _document.ModuleState };

            internal bool Matches(CatalogItem[] catalog, HeldState state,
                PlayerInventoryBaseline.Entry instance)
                => State == state && _document.Catalog.AsSpan().SequenceEqual(catalog)
                    && (instance == null ? _document.Modules == null : _document.Modules != null
                        && instance.ModuleState == _document.ModuleState
                        && PlayerInventoryBaseline.JsonEqual(instance.Modules, _document.Modules));

            private static void Validate(Document document)
            {
                Require(document != null && document.Version == 2 && document.Plan.IsValid
                    && document.Catalog != null && document.Catalog.Length is > 0 and <= 256,
                    "Invalid held-only snapshot header/catalog.");
                Require(float.IsFinite(document.DropProfile.Force) && document.DropProfile.Force >= 0
                    && float.IsFinite(document.DropProfile.UpForce), "Invalid native held drop force configuration.");
                var ids = new HashSet<int>();
                foreach (var item in document.Catalog)
                    Require(item.ItemId is > 0 and <= ServerInventory.MaxItemId
                        && item.HeldCapacity is > 0 and <= 8192 && ids.Add(item.ItemId),
                        "Held-only catalog contains invalid or ambiguous definitions.");
                var state = document.State;
                int capacity = Capacity(document.Catalog, state.ItemId);
                Require(state.ItemId >= -1 && (state.ItemId <= 0 || capacity > 0)
                    && state.Count >= 0 && state.Count <= capacity
                    && state.Amount == EffectiveAmount(document.Catalog, state.ItemId, state.Count, state.PlaceFullLoadHack)
                    && (document.Modules == null) == (document.ModuleState == null)
                    && (state.Count == 0 || document.Modules != null)
                    && (document.Modules == null || capacity > 0), "Invalid held-only state or missing representative instance.");
                if (document.Modules != null)
                {
                    Require(document.Modules.Length <= MaximumBytes, "Held-only module data exceeds its bound.");
                    using var modules = JsonDocument.Parse(document.Modules, new JsonDocumentOptions { MaxDepth = 32 });
                    ValidateJson(modules.RootElement);
                    Require(modules.RootElement.ValueKind == JsonValueKind.Array, "Invalid held-only native module data.");
                    var supplemental = document.ModuleState;
                    Require((supplemental.ContainerVolume == null || float.IsFinite(supplemental.ContainerVolume.Value)
                            && supplemental.ContainerVolume.Value >= 0)
                        && supplemental.LimbIsFresh.HasValue == supplemental.LimbBloodReveal.HasValue,
                        "Invalid held-only supplemental module data.");
                }
            }

            internal static void ValidateJson(JsonElement value)
            {
                if (value.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject())
                    { Require(names.Add(property.Name), "Duplicate snapshot property."); ValidateJson(property.Value); }
                }
                else if (value.ValueKind == JsonValueKind.Array)
                    foreach (var child in value.EnumerateArray()) ValidateJson(child);
            }
        }

        internal sealed class Document
        {
            public Document() { }
            [JsonRequired]
            public int Version { get; set; } = 2;
            [JsonRequired]
            public BoltMigrationPlan Plan { get; set; }
            [JsonRequired]
            public CatalogItem[] Catalog { get; set; }
            [JsonRequired]
            public HeldState State { get; set; }
            [JsonRequired]
            public DropConfiguration DropProfile { get; set; }
            [JsonRequired]
            public string Modules { get; set; }
            [JsonRequired]
            public PlayerInventoryModuleState ModuleState { get; set; }
        }

        private static int Capacity(CatalogItem[] catalog, int itemId)
        {
            foreach (var item in catalog) if (item.ItemId == itemId) return item.HeldCapacity;
            return 0;
        }

        // Matched native get_Amount (RVA 30D73D0): full-load returns controlled Held.Length,
        // including a retained controlled ID at raw count zero. HasItem remains raw count > 0.
        private static int EffectiveAmount(CatalogItem[] catalog, int itemId, int count, bool fullLoad)
            => fullLoad ? Capacity(catalog, itemId) : count;
        private static bool Alive(UnityEngine.Object value) => value != null && !value.WasCollected && value;
        private static void Require(bool value, string detail)
        { if (!value) throw new InvalidOperationException(detail); }
    }
}
