// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Text;
using Modstoke.Loader;
using Sons.Items.Core;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Modstoke.Sons
{
    /// <summary>
    /// Adds mod items to the 3D printer's blueprint list, after the game's own. Printer laptops pick and sync a blueprint
    /// by its id and list position, so every peer needs the same list: register at mod start in every process. The id
    /// comes from the mod id and key, so a save that was printing one finds it again.
    /// </summary>
    public static class PrinterBlueprints
    {
        private const int FirstId = 1000, IdRange = 30000, MaximumBlueprints = 24;
        private static readonly List<Registration> Registrations = new();
        private static float _nextCheck;
        private static bool _loggedFailure;

        private sealed class Registration
        {
            internal string ModId, Key, Name;
            internal int ItemId, Resin, Id;
            internal float Seconds;
            internal PrintedItemBlueprint Blueprint;
        }

        /// <summary>A blueprint that prints one <paramref name="itemId"/> from <paramref name="resin"/> resin (the game's
        /// prints take 50 to 1000) in <paramref name="seconds"/>. The item needs a world pickup, which modeled items
        /// registered with canBeSpawned get. <paramref name="name"/> shows on the laptop when the item's own name
        /// is not in the active language.</summary>
        public static bool TryRegister(string modId, string key, int itemId, string name, int resin, float seconds, out string problem)
        {
            problem = null;
            if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(key)) problem = "A mod id and a key are required.";
            else if (!ItemIds.TryGetOwner(itemId, out var owner) || !string.Equals(owner.ModId, modId.Trim(), StringComparison.Ordinal))
                problem = "Item " + itemId + " is not reserved by " + modId + ".";
            else if (resin is < 1 or > 2000) problem = "The resin must be between 1 and 2000.";
            else if (!float.IsFinite(seconds) || seconds is < 1f or > 600f) problem = "The print time must be between 1 and 600 seconds.";
            if (problem != null) return false;
            var registration = new Registration
            {
                ModId = modId.Trim(), Key = key.Trim(), ItemId = itemId, Resin = resin, Seconds = seconds,
                Name = string.IsNullOrWhiteSpace(name) ? "Item " + itemId : name.Trim()
            };
            registration.Id = StableId(registration.ModId + ":" + registration.Key);
            lock (Registrations)
            {
                if (Registrations.Count >= MaximumBlueprints) { problem = "The blueprint limit is reached."; return false; }
                foreach (var existing in Registrations)
                    if (existing.Id == registration.Id || existing.ItemId == itemId)
                    {
                        problem = existing.ItemId == itemId ? "Item " + itemId + " already has a blueprint." : "That key's blueprint id is taken.";
                        return false;
                    }
                Registrations.Add(registration);
                Registrations.Sort((a, b) => a.Id.CompareTo(b.Id));
            }
            return true;
        }

        // Laptops come with the bunker and base scenes, and a new one starts from the game's list, so each check
        // fills any laptop that lacks the mod blueprints.
        internal static void Tick()
        {
            if (Registrations.Count == 0 || Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 1f;
            if (!GameRuntime.IsGameScene) return;
            try
            {
                Registration[] registrations;
                lock (Registrations) registrations = Registrations.ToArray();
                int added = 0;
                foreach (var laptop in Resources.FindObjectsOfTypeAll<Resin3dPrinterLaptop>())
                {
                    var list = laptop == null ? null : laptop._availableBlueprints;
                    if (list == null || list.Count == 0) continue;
                    foreach (var registration in registrations)
                    {
                        if (Contains(list, registration.Id)) continue;
                        var blueprint = Blueprint(registration, list);
                        if (blueprint == null) continue;
                        list.Add(blueprint);
                        added++;
                    }
                }
                if (added > 0)
                    RuntimeEntryPoint.LogSource?.LogInfo(added + " mod printer blueprint" + (added == 1 ? "" : "s") + " added to the 3D printer.");
            }
            catch (Exception error)
            {
                if (_loggedFailure) return;
                _loggedFailure = true;
                RuntimeEntryPoint.LogSource?.LogWarning("Mod printer blueprints could not be added: " + error.Message);
            }
        }

        private static bool Contains(Il2CppSystem.Collections.Generic.List<PrintedItemBlueprint> list, int id)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i]._id == id) return true;
            return false;
        }

        // A copy of one of the game's blueprints for its print bed settings, printing one of the mod item.
        private static PrintedItemBlueprint Blueprint(Registration registration, Il2CppSystem.Collections.Generic.List<PrintedItemBlueprint> list)
        {
            if (registration.Blueprint != null && !registration.Blueprint.WasCollected) return registration.Blueprint;
            if (!ItemDatabaseManager.TryFindItemById(registration.ItemId, out var item) || item == null || item._pickupPrefab == null) return null;
            PrintedItemBlueprint template = null;
            for (int i = 0; i < list.Count && template == null; i++)
                if (list[i] != null && list[i]._positions != null && list[i]._positions.Count > 0) template = list[i];
            if (template == null) return null;
            var blueprint = Object.Instantiate(template);
            blueprint.name = registration.ModId + ":" + registration.Key + " blueprint";
            blueprint.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            blueprint._id = registration.Id;
            blueprint._name = registration.Name;
            blueprint._translationKey = "MODSTOKE_CUSTOM_ITEM_" + registration.ItemId;
            blueprint._printedItem = item._pickupPrefab.gameObject;
            blueprint._requiredResinVolume = registration.Resin;
            blueprint._totalPrintTime = registration.Seconds;
            var icon = item._uiData != null ? item._uiData._icon : null;
            if (icon != null) blueprint._iconTexture = icon;
            // One item per print, on the template's first spot.
            var first = template._positions[0];
            blueprint._positions = new();
            blueprint._positions.Add(first);
            registration.Blueprint = blueprint;
            return blueprint;
        }

        // FNV-1a over the mod id and key, into a band above the game's own blueprint ids.
        private static int StableId(string text)
        {
            uint hash = 2166136261;
            foreach (var value in Encoding.UTF8.GetBytes(text)) hash = (hash ^ value) * 16777619;
            return FirstId + (int)(hash % IdRange);
        }
    }
}
