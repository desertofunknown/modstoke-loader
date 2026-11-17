// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Modstoke.Diagnostics;
using Modstoke.Sons;
using global::Sons.Save;

namespace Modstoke.Loader
{
    /// <summary>
    /// "saves history" and "saves restore": a slot's earlier saves, and putting one back. On a dedicated server, or
    /// while a world is loaded, a restore is staged for the next start, because the running world would save over
    /// it and stop saves it on the way out; staged, it lands before the game reads the slot, with whatever the
    /// slot held by then kept as an earlier save. From a player's title screen it happens at once.
    /// </summary>
    internal static class HostSaveHistory
    {
        private static readonly char[] Spaces = { ' ', '\t' };
        // A number in "saves restore" is a line of the list "saves history" printed last for that slot, so autosaves
        // landing in between can't shift it onto another save. Keyed by slot folder.
        private static readonly Dictionary<string, string[]> Printed = new(StringComparer.OrdinalIgnoreCase);

        internal static void RunHistory(ConsoleSubmission request)
        {
            var words = request.Arguments.Split(Spaces, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 2)
            {
                Usage(request);
                return;
            }
            var place = Place.Read(words.Length == 2 ? words[1] : null);
            Task.Run(async () =>
            {
                var directory = place.Find(out var problem);
                if (directory == null)
                {
                    HostCommands.Respond(request, false, problem, error: "invalid-arguments");
                    return;
                }
                var entries = await SaveHistory.ListAsync(directory);
                var damaged = SaveHistory.Damaged(directory);
                var staged = StagedFor(directory);
                var slot = Path.GetFileName(directory);

                var text = new StringBuilder();
                text.Append(entries.Count == 0 ? "No earlier saves of " + slot + " are kept yet." : "Earlier saves of " + slot + " (" + Kind(directory) + "), newest first:");
                var list = new JsonArray();
                for (var i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    text.Append('\n').Append(i + 1).Append("  ").Append(Line(entry)).Append(" · id ").Append(entry.Id);
                    list.Add((JsonNode)new JsonObject
                    {
                        ["number"] = i + 1,
                        ["id"] = entry.Id,
                        ["savedAt"] = entry.Saved.ToUnixTimeMilliseconds(),
                        ["day"] = entry.Day,
                        ["sizeBytes"] = entry.SizeBytes,
                        ["reason"] = entry.Reason,
                        ["undo"] = entry.Undo
                    });
                }
                if (damaged.Count > 0)
                    text.Append('\n').Append(damaged.Count).Append(damaged.Count == 1 ? " damaged copy is" : " damaged copies are")
                        .Append(" kept apart. The newest, from ").Append(Utc(damaged[0].Saved)).Append(": ").Append(damaged[0].Problem);
                if (staged != null)
                    text.Append("\nStaged for the next start: the save from ").Append(Utc(staged.Entry.Saved)).Append('.');
                else if (entries.Count > 0)
                    text.Append("\n'saves restore <number or id>").Append(place.Named ? " " + slot : "").Append("' puts one back")
                        .Append(place.Immediate ? "." : Host.IsServer ? " when the server next starts." : " when the game next starts.")
                        .Append(" A number means a line of this list.");
                lock (Printed)
                    Printed[directory] = entries.Select(entry => entry.Id).ToArray();

                HostCommands.Respond(request, true, text.ToString(), new JsonObject
                {
                    ["slot"] = slot,
                    ["kind"] = Kind(directory),
                    ["entries"] = list,
                    ["damaged"] = new JsonArray(damaged.Select(entry => (JsonNode)new JsonObject
                    {
                        ["id"] = entry.Id,
                        ["savedAt"] = entry.Saved.ToUnixTimeMilliseconds(),
                        ["sizeBytes"] = entry.SizeBytes,
                        ["problem"] = entry.Problem
                    }).ToArray()),
                    ["staged"] = Staged(staged)
                });
            }).ContinueWith(task => Failed(request, task, "Listing earlier saves"), TaskContinuationOptions.OnlyOnFaulted);
        }

        internal static void RunRestore(ConsoleSubmission request)
        {
            if (request.Origin is not (ConsoleOrigin.ServerConsole or ConsoleOrigin.DesktopConsole))
            {
                HostCommands.Respond(request, false, "\"saves restore\" can only be run from the server or desktop console.",
                    error: "unavailable");
                return;
            }
            var words = request.Arguments.Split(Spaces, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 2 && words[1] == "--cancel")
            {
                SaveHistory.UnstageAsync().ContinueWith(task =>
                {
                    if (task.IsCompletedSuccessfully)
                        HostCommands.Respond(request, true, task.Result ? "The staged restore was dropped." : "No restore was staged.",
                            new JsonObject { ["dropped"] = task.Result });
                    else
                        Failed(request, task, "Dropping the staged restore");
                });
                return;
            }
            if (words.Length is < 2 or > 3)
            {
                Usage(request);
                return;
            }
            var place = Place.Read(words.Length == 3 ? words[2] : null);
            var which = words[1];
            Task.Run(async () =>
            {
                var directory = place.Find(out var problem);
                if (directory == null)
                {
                    HostCommands.Respond(request, false, problem, error: "invalid-arguments");
                    return;
                }
                var slot = Path.GetFileName(directory);
                var id = which;
                if (int.TryParse(which, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                {
                    string[] printed;
                    lock (Printed)
                        Printed.TryGetValue(directory, out printed);
                    if (printed == null || number < 1 || number > printed.Length)
                    {
                        HostCommands.Respond(request, false, (printed == null
                                ? "A number means a line of the list 'saves history " + slot + "' prints; run that first,"
                                : "The last 'saves history " + slot + "' has no line " + number + ";")
                            + " or give the save's id.", error: "invalid-arguments");
                        return;
                    }
                    id = printed[number - 1];
                }
                var entries = await SaveHistory.ListAsync(directory);
                var entry = entries.FirstOrDefault(candidate => candidate.Id == id);
                if (entry == null)
                {
                    HostCommands.Respond(request, false, "Save " + slot + " no longer keeps earlier save " + id
                        + ". 'saves history " + slot + "' lists what it keeps.", error: "invalid-arguments");
                    return;
                }
                var body = new JsonObject
                {
                    ["slot"] = slot,
                    ["id"] = entry.Id,
                    ["savedAt"] = entry.Saved.ToUnixTimeMilliseconds(),
                    ["day"] = entry.Day
                };
                if (place.Immediate)
                {
                    await SaveHistory.RestoreAsync(directory, entry);
                    body["restored"] = true;
                    HostCommands.Respond(request, true, "Save " + slot + " is back to " + Line(entry)
                        + ". What it held before is kept as an earlier save.", body);
                    return;
                }
                var staged = await SaveHistory.StageAsync(directory, entry);
                body["restored"] = false;
                body["staged"] = Staged(staged);
                HostCommands.Respond(request, true, "Save " + slot + " goes back to " + Line(entry) + " when "
                    + (Host.IsServer ? "the server" : "the game") + " next starts. What it holds then is kept as an earlier save."
                    + " 'saves restore --cancel' drops this.", body);
            }).ContinueWith(task => Failed(request, task, "The restore"), TaskContinuationOptions.OnlyOnFaulted);
        }

        // Read on the game thread: the slot to use and the game's own save folders.
        private sealed class Place
        {
            private string _slot;
            private string _last;
            private readonly List<string> _folders = new();

            internal bool Named { get; private set; }
            /// <summary>A restore happens now: a player's game at its title screen, with no world to save over it.</summary>
            internal bool Immediate { get; private set; }

            internal static Place Read(string slot)
            {
                var place = new Place
                {
                    Named = slot != null,
                    _last = SaveLifecycle.State.LastDirectory,
                    Immediate = !Host.IsServer && GameRuntime.IsTitleScene
                };
                // "1" names slot 0000000001, as the server config does.
                place._slot = slot != null && uint.TryParse(slot, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    ? id.ToString("D10", CultureInfo.InvariantCulture)
                    : slot ?? HostWorld.ActiveSlot ?? HostWorld.SlotOf(place._last);
                try
                {
                    if (SaveGameManager._setupSavePaths)
                        foreach (var folder in new[] { SaveGameManager._multiplayerSaveGameFolder, SaveGameManager._singlePlayerSaveGameFolder,
                                     SaveGameManager._multiplayerClientSaveGameFolder })
                            if (!string.IsNullOrEmpty(folder))
                                place._folders.Add(folder);
                }
                catch (Exception)
                {
                    // The folder recorded with the slot's history is used instead.
                }
                return place;
            }

            // The running or last used slot, then the game's save folders, then the folder a history was taken from.
            internal string Find(out string problem)
            {
                problem = null;
                if (_slot == null)
                {
                    problem = "No save is running. Name the slot: saves history <slot>.";
                    return null;
                }
                if (_last != null && HostWorld.SlotOf(_last) == _slot)
                    return SaveHistoryStore.Normalize(_last);
                foreach (var folder in _folders)
                {
                    var directory = Path.Combine(folder, _slot);
                    if (Directory.Exists(directory))
                        return SaveHistoryStore.Normalize(directory);
                }
                foreach (var history in SaveHistoryStore.Histories(SaveHistory.Root))
                {
                    if (Path.GetFileName(history) != _slot)
                        continue;
                    var directory = SaveHistoryStore.Entries(history).Concat(SaveHistoryStore.Entries(history, true))
                        .Select(entry => entry.SlotDirectory).FirstOrDefault(path => path != null);
                    if (directory != null)
                        return directory;
                }
                problem = "No save slot " + _slot + " was found.";
                return null;
            }
        }

        private static StagedRestore StagedFor(string directory)
        {
            var staged = SaveHistory.Staged;
            return staged != null && string.Equals(staged.SlotDirectory, directory, StringComparison.OrdinalIgnoreCase) ? staged : null;
        }

        private static JsonObject Staged(StagedRestore staged) => staged == null ? null : new JsonObject
        {
            ["id"] = staged.Entry.Id,
            ["savedAt"] = staged.Entry.Saved.ToUnixTimeMilliseconds(),
            ["stagedAt"] = staged.StagedAt.ToUnixTimeMilliseconds()
        };

        private static string Line(SaveHistoryEntry entry)
        {
            var line = new StringBuilder(Utc(entry.Saved));
            if (entry.Day is int day)
                line.Append(" · day ").Append(day);
            line.Append(" · ").Append((entry.SizeBytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)).Append(" MB");
            // A restore can reuse an identical copy already kept; the flag, not the reason, marks the one to undo to.
            var reason = entry.Undo ? "kept before a restore" : entry.Reason switch
            {
                "restore" => "kept before a restore",
                "load" => "as the world was loaded",
                "game-backup" => "the game's own backup",
                _ => null
            };
            return reason == null ? line.ToString() : line.Append(" · ").Append(reason).ToString();
        }

        private static string Utc(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

        private static string Kind(string directory) => Path.GetFileName(Path.GetDirectoryName(directory)) switch
        {
            "SinglePlayer" => "singleplayer",
            "MultiplayerClient" => "client",
            _ => "multiplayer"
        };

        private static void Usage(ConsoleSubmission request) => HostCommands.Respond(request, false,
            "Usage: saves history [<slot>] | saves restore <number|id> [<slot>] | saves restore --cancel", error: "invalid-arguments");

        private static void Failed(ConsoleSubmission request, Task task, string what)
        {
            var error = task.Exception?.GetBaseException();
            HostCommands.Respond(request, false, what + " failed: " + error?.Message, error: "failed");
        }
    }
}
