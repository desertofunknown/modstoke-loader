// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using Modstoke.Diagnostics;
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
                        + string.Join("|", Modes) + "> | game inventory open|close | game equip <item id> | game remove <item id> | game quit",
                        error: "invalid-arguments");
                    break;
            }
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
