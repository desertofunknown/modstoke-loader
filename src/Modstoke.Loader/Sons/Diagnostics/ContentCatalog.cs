// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Modstoke.Loader;
using Sons.Gameplay;
using Sons.Items.Core;
using Sons.Player;
using TheForest.Utils;
using UnityEngine;
using Types = Sons.Items.Core.Types;

namespace Modstoke.Sons
{
    /// <summary>
    /// A one-shot report of the game's loot and printer content: every container loot table with its weighted items,
    /// the containers in the world that use each table, and the 3D printer blueprints. Mods pick loot targets from it.
    /// It also lists every equippable item as a pose donor for held models.
    /// Armed by <c>content-catalog-next-launch.flag</c> in the user data folder; written once a world is up, to
    /// <c>content-catalog-&lt;time&gt;.json</c> beside it.
    /// </summary>
    internal static class ContentCatalog
    {
        private const string ArmName = "content-catalog-next-launch.flag";
        private const float SettleSeconds = 20f;
        private static bool? _armed;
        private static float _inWorldSince = -1f;

        internal static void Tick()
        {
            if (_armed == false) return;
            try
            {
                _armed ??= File.Exists(Path.Combine(LoaderPaths.UserDataDirectory, ArmName));
                if (_armed != true) return;
                if (!GameRuntime.IsInWorld) { _inWorldSince = -1f; return; }
                if (_inWorldSince < 0f) _inWorldSince = Time.realtimeSinceStartup;
                // Streamed world cells bring their containers in after the player appears.
                if (Time.realtimeSinceStartup - _inWorldSince < SettleSeconds) return;
                _armed = false;
                File.Delete(Path.Combine(LoaderPaths.UserDataDirectory, ArmName));
                string path = Write();
                RuntimeEntryPoint.LogSource?.LogInfo("Content catalog written to " + path + ".");
            }
            catch (Exception error)
            {
                _armed = false;
                RuntimeEntryPoint.LogSource?.LogWarning("The content catalog could not be written: " + error.Message);
            }
        }

        private static string Write()
        {
            string path = Path.Combine(LoaderPaths.UserDataDirectory,
                "content-catalog-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
            using var stream = File.Create(path);
            using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            json.WriteStartObject();
            json.WriteString("format", "modstoke.content-catalog");
            json.WriteNumber("schemaVersion", 1);
            WriteLootTables(json);
            WritePrinterBlueprints(json);
            WriteHeldDonors(json);
            json.WriteEndObject();
            return path;
        }

        // Every equippable item as a pose donor for held models: its hand, pose data, held controller, render layer
        // and the local player's hand locator for it.
        private static void WriteHeldDonors(Utf8JsonWriter json)
        {
            var props = LocalPlayer.Inventory != null ? LocalPlayer.Inventory._InventoryProps_k__BackingField : null;
            var definitions = props != null ? props._propDefinitions : null;
            var items = ItemDatabaseManager._instance != null ? ItemDatabaseManager._instance._itemDataList : null;
            var mapHeld = new List<int>();
            json.WriteStartArray("heldDonors");
            for (int i = 0; items != null && i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || (item._type & Types.Equippable) == 0) continue;
                json.WriteStartObject();
                json.WriteNumber("id", item._id);
                json.WriteString("name", item._name ?? "");
                json.WriteString("slot", item._equipmentSlot.ToString());
                json.WriteBoolean("bothHands", item._requiresBothHands);
                json.WriteNumber("wideBlend", item._leftHandWidePoseBlend);
                json.WriteBoolean("hasFirstLook", item._hasFirstLook);
                json.WriteStartArray("animVars");
                var poses = item._equippedAnimVars;
                for (int p = 0; poses != null && p < poses.Length; p++)
                {
                    json.WriteStringValue(poses[p].ToString());
                    if (poses[p] == AnimatorVariables.mapHeld && !mapHeld.Contains(item._id)) mapHeld.Add(item._id);
                }
                json.WriteEndArray();
                var held = item._heldPrefab;
                json.WriteString("heldPrefab", held != null ? held.name : "");
                json.WriteString("controller", HeldItemTemplates.ControllerName(item));
                json.WriteNumber("renderLayer", held != null ? HeldItemTemplates.RenderLayer(item) : -1);
                WriteLocator(json, definitions, item._id);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartArray("mapHeldItems");
            foreach (var id in mapHeld) json.WriteNumberValue(id);
            json.WriteEndArray();
        }

        private static void WriteLocator(Utf8JsonWriter json, Il2CppSystem.Collections.Generic.List<InventoryProps.PropDefinition> definitions, int itemId)
        {
            for (int i = 0; definitions != null && i < definitions.Count; i++)
            {
                var definition = definitions[i];
                if (definition == null || definition._itemId != itemId) continue;
                var locator = definition._heldLocator;
                if (locator == null)
                {
                    json.WriteString("locator", "");
                    return;
                }
                var path = locator.name;
                for (var parent = locator.parent; parent != null && path.Length < 400; parent = parent.parent) path = parent.name + "/" + path;
                json.WriteString("locator", path);
                var position = locator.localPosition;
                var rotation = locator.localEulerAngles;
                json.WriteString("locatorPosition", position.x.ToString("F3") + " " + position.y.ToString("F3") + " " + position.z.ToString("F3"));
                json.WriteString("locatorRotation", rotation.x.ToString("F1") + " " + rotation.y.ToString("F1") + " " + rotation.z.ToString("F1"));
                return;
            }
            json.WriteNull("locator");
        }

        private static void WriteLootTables(Utf8JsonWriter json)
        {
            // Which containers use each table, by table pointer.
            var users = new Dictionary<IntPtr, List<ContainerItemSpawner>>();
            foreach (var spawner in Resources.FindObjectsOfTypeAll<ContainerItemSpawner>())
            {
                var data = spawner == null ? null : spawner._spawnData;
                if (data == null) continue;
                if (!users.TryGetValue(data.Pointer, out var list)) users[data.Pointer] = list = new List<ContainerItemSpawner>();
                list.Add(spawner);
            }

            json.WriteStartArray("lootTables");
            foreach (var table in Resources.FindObjectsOfTypeAll<ContainerItemSpawnerData>())
            {
                if (table == null) continue;
                json.WriteStartObject();
                json.WriteString("name", table.name);
                var range = table._countRange;
                json.WriteNumber("countMin", range.x);
                json.WriteNumber("countMax", range.y);
                json.WriteNumber("emptyChance", table._emptyChance);
                json.WriteNumber("maxWeight", table._maxWeight);
                json.WriteBoolean("noDuplicates", table._doNotAllowDuplicates);
                json.WriteStartArray("items");
                var items = table._items;
                for (int i = 0; items != null && i < items.Count; i++)
                {
                    var entry = items[i];
                    if (entry == null) continue;
                    json.WriteStartObject();
                    json.WriteNumber("itemId", entry._id);
                    json.WriteString("pickup", entry._item != null ? entry._item.name : "");
                    json.WriteNumber("weight", entry._weight);
                    json.WriteNumber("maxCount", entry._maxCount);
                    json.WriteEndObject();
                }
                json.WriteEndArray();

                users.TryGetValue(table.Pointer, out var containers);
                json.WriteNumber("containers", containers?.Count ?? 0);
                json.WriteStartArray("containerSamples");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                if (containers != null)
                    foreach (var container in containers)
                    {
                        string name = ContainerName(container);
                        if (seen.Count >= 12 || !seen.Add(name)) continue;
                        json.WriteStartObject();
                        json.WriteString("name", name);
                        json.WriteBoolean("inScene", container.gameObject.scene.handle != 0);
                        var position = container.transform.position;
                        json.WriteString("position", position.x.ToString("F0") + " " + position.y.ToString("F0") + " " + position.z.ToString("F0"));
                        json.WriteEndObject();
                    }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            json.WriteEndArray();
        }

        // The container's own object name says little ("Spawner"); its top-level parent names the prop.
        private static string ContainerName(ContainerItemSpawner container)
        {
            var transform = container.transform;
            var root = transform.root;
            return root == transform ? transform.name : root.name + "/" + transform.name;
        }

        private static void WritePrinterBlueprints(Utf8JsonWriter json)
        {
            json.WriteStartArray("printerBlueprints");
            var seen = new HashSet<int>();
            foreach (var laptop in Resources.FindObjectsOfTypeAll<Resin3dPrinterLaptop>())
            {
                var available = laptop == null ? null : laptop._availableBlueprints;
                for (int i = 0; available != null && i < available.Count; i++)
                {
                    var blueprint = available[i];
                    if (blueprint == null || !seen.Add(blueprint.Id)) continue;
                    json.WriteStartObject();
                    json.WriteNumber("id", blueprint.Id);
                    json.WriteString("name", blueprint.Name ?? "");
                    json.WriteString("asset", blueprint.name);
                    json.WriteNumber("resin", blueprint.RequiredResin);
                    json.WriteNumber("seconds", blueprint.TotalPrintTime);
                    var printed = blueprint._printedItem;
                    json.WriteString("printedItem", printed != null ? printed.name : "");
                    var pickup = printed != null ? printed.GetComponentInChildren<PickUp>(true) : null;
                    json.WriteNumber("printedItemId", pickup != null ? pickup._itemId : -1);
                    json.WriteNumber("listIndex", i);
                    json.WriteEndObject();
                }
            }
            json.WriteEndArray();
        }
    }
}
