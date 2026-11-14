// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using Modstoke.Diagnostics;
using Sons.Items.Core;
using Sons.Save;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Loader
{
    /// <summary>
    /// "game" commands for a player's game, from the desktop console only: a session can be opened, driven and
    /// closed from outside the game for test runs, without touching the menus.
    /// </summary>
    internal static class HostGame
    {
        internal static void Run(ConsoleSubmission request)
        {
            var arguments = request.Arguments.Trim();
            var split = arguments.IndexOf(' ');
            var verb = split < 0 ? arguments : arguments[..split];
            var rest = split < 0 ? "" : arguments[(split + 1)..].Trim();
            switch (verb)
            {
                case "continue":
                    Continue(request, rest);
                    break;
                case "new" when Array.IndexOf(Modes, rest) >= 0:
                    if (!AtTitle(request)) break;
                    GameMenus.NewGame(SaveGameType.SinglePlayer, rest);
                    HostCommands.Respond(request, true, "Starting a new " + rest + " single player game.");
                    break;
                case "inventory" when rest is "open" or "close":
                {
                    var inventory = LocalPlayer.Inventory;
                    if (inventory == null)
                    {
                        HostCommands.Respond(request, false, "There is no local player in a world.", error: "not-ready");
                        break;
                    }
                    if (rest == "open") inventory.Open();
                    else inventory.Close(true);
                    HostCommands.Respond(request, true, rest == "open" ? "Inventory opened." : "Inventory closed.");
                    break;
                }
                case "equip" when int.TryParse(rest, out var itemId):
                {
                    // A held mod item goes through the SDK's equip, as a mod brings it out.
                    if (Modstoke.Sons.HeldItems.IsHeldModelItem(itemId))
                    {
                        var equipped = Modstoke.Sons.HeldItems.TryEquip(itemId, out var heldStatus);
                        HostCommands.Respond(request, equipped, "Held item " + itemId + ": " + heldStatus);
                        break;
                    }
                    var inventory = LocalPlayer.Inventory;
                    if (inventory == null)
                    {
                        HostCommands.Respond(request, false, "There is no local player in a world.", error: "not-ready");
                        break;
                    }
                    inventory.ToggleRightHandItem(itemId);
                    HostCommands.Respond(request, true, "Toggled item " + itemId + " in the right hand.");
                    break;
                }
                case "toggle" when int.TryParse(rest, out var toggleId):
                    Toggle(request, toggleId);
                    break;
                case "unequip" when rest is "left" or "right":
                    Unequip(request, rest == "left" ? EquipmentSlot.LeftHand : EquipmentSlot.RightHand);
                    break;
                case "held" when rest.Length == 0:
                    HostCommands.Respond(request, true, Modstoke.Sons.HeldItems.DescribeLocal());
                    break;
                case "held" when rest == "animator":
                    HostCommands.Respond(request, true, Modstoke.Sons.HeldItems.DescribeAnimator());
                    break;
                case "give":
                    Give(request, rest);
                    break;
                case "gearpage" when rest is "on" or "off":
                    HostCommands.Respond(request, Modstoke.Sons.Inventory.TrySetGearPage(rest == "on"),
                        "Gear page " + rest + " (the inventory must be open and carry mod gear).");
                    break;
                case "remove" when int.TryParse(rest, out var removeId):
                {
                    var removed = Modstoke.Sons.Inventory.TryRemoveItem(removeId, 1, out var result);
                    HostCommands.Respond(request, removed, removed ? "Removed one of item " + removeId + "."
                        : "Item " + removeId + " was not removed: " + result?.Detail);
                    break;
                }
                case "quit" when rest.Length == 0:
                    HostCommands.Respond(request, true, "Quitting the game without saving.");
                    Application.Quit();
                    break;
                default:
                    HostCommands.Respond(request, false, "Usage: game continue [latest|<slot>] | game new <"
                        + string.Join("|", Modes) + "> | game inventory open|close | game gearpage on|off | game equip <item id>"
                        + " | game toggle <item id> | game unequip left|right | game held [animator] | game give <item id> [count]"
                        + " | game remove <item id> | game quit",
                        error: "invalid-arguments");
                    break;
            }
        }

        // Toggles an item in its own hand, as the game's keys for the GPS or flashlight do.
        private static void Toggle(ConsoleSubmission request, int itemId)
        {
            if (Modstoke.Sons.HeldItems.IsHeldModelItem(itemId))
            {
                var toggled = Modstoke.Sons.HeldItems.TryToggle(itemId, out var status);
                HostCommands.Respond(request, toggled, "Held item " + itemId + ": " + status);
                return;
            }
            var inventory = LocalPlayer.Inventory;
            if (inventory == null || !ItemDatabaseManager.TryFindItemById(itemId, out var data) || data == null)
            {
                HostCommands.Respond(request, false, "There is no local player, or no item " + itemId + ".", error: "not-ready");
                return;
            }
            var left = data._equipmentSlot == EquipmentSlot.LeftHand;
            if (left) inventory.ToggleLeftHandItem(itemId);
            else inventory.ToggleRightHandItem(itemId);
            HostCommands.Respond(request, true, "Toggled item " + itemId + " in the " + (left ? "left" : "right") + " hand.");
        }

        private static void Unequip(ConsoleSubmission request, EquipmentSlot slot)
        {
            var inventory = LocalPlayer.Inventory;
            if (inventory == null)
            {
                HostCommands.Respond(request, false, "There is no local player in a world.", error: "not-ready");
                return;
            }
            var itemId = inventory.TryGetItemInSlot(slot, out var item) && item != null ? item._itemID : -1;
            if (itemId < 0)
            {
                HostCommands.Respond(request, false, "The " + (slot == EquipmentSlot.LeftHand ? "left" : "right") + " hand is empty.");
                return;
            }
            if (Modstoke.Sons.HeldItems.IsHeldModelItem(itemId))
            {
                var stashed = Modstoke.Sons.HeldItems.TryPutAway(itemId, out var status);
                HostCommands.Respond(request, stashed, "Held item " + itemId + ": " + status);
                return;
            }
            inventory.UnequipItemAtSlot(slot, false, true, false);
            HostCommands.Respond(request, true, "Put away item " + itemId + ".");
        }

        // Host and single player only: a client's inventory belongs to its server.
        private static void Give(ConsoleSubmission request, string arguments)
        {
            var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var count = 1;
            if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], out var itemId)
                || parts.Length == 2 && (!int.TryParse(parts[1], out count) || count < 1))
            {
                HostCommands.Respond(request, false, "Usage: game give <item id> [count]", error: "invalid-arguments");
                return;
            }
            if (BoltNetwork.isRunning && !BoltNetwork.isServer)
            {
                HostCommands.Respond(request, false, "Only the host or a single player game can give items.", error: "not-allowed");
                return;
            }
            var given = Modstoke.Sons.Inventory.TryAddItem(itemId, count, out var result, preventAutoEquip: true);
            HostCommands.Respond(request, given, given
                ? "Gave " + count + " of item " + itemId + "; carried " + result.AfterCount + "."
                : "Item " + itemId + " was not given: " + result?.Detail);
        }

        // The single player modes as the game's own mode buttons name them.
        private static readonly string[] Modes = { "Normal", "Hard", "HardSurvival", "Peaceful", "Creative", "Custom" };

        private static bool AtTitle(ConsoleSubmission request)
        {
            if (GameMenus.Bind() && !GameMenus.OtherSceneOpen) return true;
            HostCommands.Respond(request, false, "A game can only be started from the title screen.", error: "not-ready");
            return false;
        }

        // The title screen's own load path, as pressing Continue on a saved game does.
        private static void Continue(ConsoleSubmission request, string slot)
        {
            if (!AtTitle(request)) return;
            uint id;
            if (slot.Length == 0 || slot == "latest")
            {
                id = Latest();
                if (id == 0)
                {
                    HostCommands.Respond(request, false, "There is no single player save to continue.", error: "not-found");
                    return;
                }
            }
            else if (!uint.TryParse(slot, out id) || !GameMenus.SaveIds(SaveGameType.SinglePlayer).Contains(id))
            {
                HostCommands.Respond(request, false, "No single player save has slot " + slot + ".", error: "not-found");
                return;
            }
            GameMenus.ContinueGame(SaveGameType.SinglePlayer, id);
            HostCommands.Respond(request, true, "Continuing single player save " + id + ".", new { slot = id });
        }

        // Saves are a folder per slot holding SaveData.zip (or SaveData.json in older builds); the newest folder wins.
        private static uint Latest()
        {
            var folder = SaveGameManager.GetSaveGameFolder(SaveGameType.SinglePlayer);
            uint latest = 0;
            var newest = DateTime.MinValue;
            foreach (var id in GameMenus.SaveIds(SaveGameType.SinglePlayer))
            {
                var slot = new DirectoryInfo(Path.Combine(folder ?? "", id.ToString()));
                if (!slot.Exists || slot.LastWriteTimeUtc <= newest) continue;
                newest = slot.LastWriteTimeUtc;
                latest = id;
            }
            return latest;
        }
    }
}
