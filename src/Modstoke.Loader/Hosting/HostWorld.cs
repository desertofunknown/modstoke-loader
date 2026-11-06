// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using Modstoke.Diagnostics;
using Modstoke.Sons;
using global::Sons.Gameplay.GameSetup;
using global::Sons.Multiplayer;
using global::Sons.Save;

namespace Modstoke.Loader
{
    /// <summary>
    /// World state for the hosting agent: the running save slot, the in-game day and clock, day changes and
    /// the save slots on disk. Game reads happen on the game thread; file reads never do.
    /// </summary>
    internal static class HostWorld
    {
        private const int TickIntervalMs = 1000;
        private const int ObservedLimitMs = 5000;
        private const int MaxSlots = 100;
        private const int MaxCandidates = 1000;
        private const int MaxMetadataBytes = 1024 * 1024;
        private const int MaxFiles = 10000;
        private const string ArchiveName = "SaveData.zip";
        private const string GameStateName = "GameStateSaveData.json";

        private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };
        private static readonly EnumerationOptions Walk = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MaxRecursionDepth = 4
        };

        // Game thread only.
        private static long _nextTick;
        private static long _lastTicks = -1;
        private static bool? _running;
        private static long _runningAt;
        private static int? _day;
        private static string _daySlot;
        private static long? _dayStartedAt;
        private static bool _configFailed;

        private static World _last;

        /// <summary>The in-game day from the last game-thread reading. Safe from any thread.</summary>
        internal static int? Day => Volatile.Read(ref _last)?.Day;

        /// <summary>The running slot from the last game-thread reading. Safe from any thread.</summary>
        internal static string ActiveSlot => Volatile.Read(ref _last)?.Slot;

        /// <summary>
        /// The world fields of the host status. On the game thread this reads the game; elsewhere it returns
        /// the last reading.
        /// </summary>
        internal static JsonObject Snapshot()
        {
            World world;
            if (Host.IsMainThread)
            {
                world = Compose(Read(), Environment.TickCount64);
                Volatile.Write(ref _last, world);
            }
            else
            {
                world = Volatile.Read(ref _last);
            }
            // JsonObject keeps the nulls, which say "not known" to the agent.
            return new JsonObject
            {
                ["activeSlot"] = world?.Slot,
                ["inGameDay"] = world?.Day,
                ["timeOfDay"] = world?.Minute,
                ["dayStartedAt"] = world?.DayStartedAt,
                ["dayCycleWhenEmpty"] = world?.IdleSpeed,
                ["clockRunning"] = world?.ClockRunning ?? false,
                ["saving"] = HostSave.Saving
            };
        }

        /// <summary>Watches the clock about once a second and emits "day" when the in-game day moves on.</summary>
        internal static void Tick()
        {
            var now = Environment.TickCount64;
            if (now < _nextTick || !Host.IsMainThread)
                return;
            _nextTick = now + TickIntervalMs;

            var reading = Read();
            var sameWorld = reading.Day != null && _day != null
                && string.Equals(reading.Slot, _daySlot, StringComparison.Ordinal);

            // Two readings a second apart show whether the clock is moving.
            if (sameWorld && _lastTicks >= 0)
            {
                _running = reading.Ticks != _lastTicks;
                _runningAt = now;
            }
            else
            {
                _running = null;
            }
            _lastTicks = reading.Ticks;

            if (sameWorld && reading.Day > _day)
            {
                var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _dayStartedAt = at;
                HostEvents.Emit("day", new JsonObject { ["slot"] = reading.Slot, ["day"] = reading.Day, ["at"] = at });
            }
            else if (!sameWorld || reading.Day < _day)
            {
                // A different world, or a day set back; when this day began is not known.
                _dayStartedAt = null;
            }
            _day = reading.Day;
            _daySlot = reading.Slot;
            Volatile.Write(ref _last, Compose(reading, now));
        }

        /// <summary>
        /// The "saves" command: the save slots on disk with their day, size and age. Folders are taken from the
        /// game on the game thread; the slots are read in the background.
        /// </summary>
        internal static void RunSaves(ConsoleSubmission request)
        {
            var folders = new List<(string Path, string Kind)>();
            string activeSlot, activeKind;
            string dataPath = null;
            if (Host.IsMainThread)
            {
                ReadSlot(out activeSlot, out activeKind);
                try
                {
                    if (SaveGameManager._setupSavePaths)
                    {
                        AddFolder(folders, SaveGameManager._multiplayerSaveGameFolder, "multiplayer");
                        AddFolder(folders, SaveGameManager._singlePlayerSaveGameFolder, "singleplayer");
                    }
                    if (folders.Count == 0)
                        dataPath = UnityEngine.Application.persistentDataPath;
                }
                catch (Exception)
                {
                    // Falls back to the folders found from the last save or the data path.
                }
            }
            else
            {
                var last = Volatile.Read(ref _last);
                activeSlot = last?.Slot;
                activeKind = last?.Kind;
            }
            var lastDirectory = SaveLifecycle.State.LastDirectory;

            Task.Run(() =>
            {
                try
                {
                    if (folders.Count == 0)
                        FindFolders(folders, lastDirectory, dataPath);
                    var body = ListSaves(folders, activeSlot, activeKind, out var human);
                    HostCommands.Respond(request, true, human, body);
                }
                catch (Exception error)
                {
                    HostCommands.Respond(request, false, "Listing saves failed: " + error.Message);
                }
            });
        }

        /// <summary>A path relative to the game folder with '/' separators, or the full path when it is elsewhere.</summary>
        internal static string GamePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            try
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                var root = Paths.GameRootPath;
                if (string.IsNullOrEmpty(root))
                    return full;
                var relative = Path.GetRelativePath(root, full);
                if (Path.IsPathRooted(relative) || relative == ".."
                    || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                    return full;
                return relative.Replace('\\', '/');
            }
            catch (Exception)
            {
                return path;
            }
        }

        /// <summary>The slot id of a save directory, which is its folder name.</summary>
        internal static string SlotOf(string directory) =>
            string.IsNullOrEmpty(directory) ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));

        /// <summary>Total bytes under a directory, or null when it cannot be read. Does file IO; not for the game thread.</summary>
        internal static long? DirectorySize(string directory)
        {
            if (string.IsNullOrEmpty(directory))
                return null;
            try
            {
                long total = 0;
                var count = 0;
                foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", Walk))
                {
                    if (++count > MaxFiles)
                        break;
                    total += file.Length;
                }
                return total;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or System.Security.SecurityException)
            {
                return null;
            }
        }

        private static Reading Read()
        {
            ReadSlot(out var slot, out var kind);
            int? day = null, minute = null;
            long ticks = -1;
            if (EnvironmentReads.TryReadClock(out var clock, out _) && clock.GameTime.Ticks >= 0)
            {
                // The underlying clock: its day count is what the save stores as GameDays.
                var time = clock.GameTime;
                ticks = time.Ticks;
                day = time.Days;
                minute = time.Hours * 60 + time.Minutes;
            }
            return new Reading(slot, kind, day, minute, ticks, IdleSpeed());
        }

        private static World Compose(Reading reading, long now)
        {
            bool running;
            if (reading.Day == null)
                running = false;
            else if (_running is bool observed && now - _runningAt <= ObservedLimitMs)
                running = observed;
            else
                running = Expected(reading.IdleSpeed);
            return new World(reading.Slot, reading.Kind, reading.Day, reading.Minute,
                reading.Day == null ? null : _dayStartedAt, reading.IdleSpeed, running);
        }

        // Before the clock has been watched for a second, assume the server's own rule: time stops while
        // the game is paused, or while nobody is on and IdleDayCycleSpeed is 0.
        private static bool Expected(double? idleSpeed)
        {
            try
            {
                if (UnityEngine.Time.timeScale <= 0f)
                    return false;
                if (!Host.IsServer || idleSpeed == null || idleSpeed.Value != 0)
                    return true;
                var sessions = ServerSessions.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                    if (sessions[i].IsCurrent)
                        return true;
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        // The slot the game has selected, named like its folder. On a dedicated server that has not selected
        // one yet, the slot from dedicatedserver.cfg.
        private static void ReadSlot(out string slot, out string kind)
        {
            slot = null;
            kind = null;
            try
            {
                if (GameSetupManager.TryGetInstance(out var setup) && setup is not null && !setup.WasCollected && setup
                    && setup._saveId != 0)
                {
                    slot = SlotName(setup._saveId);
                    kind = KindName(setup._saveGameType);
                    return;
                }
            }
            catch (Exception)
            {
                // Read as no selected slot.
            }
            var config = Config();
            if (config != null && config.SaveSlot != 0)
            {
                slot = SlotName(config.SaveSlot);
                kind = "multiplayer";
            }
        }

        private static double? IdleSpeed()
        {
            var config = Config();
            if (config == null)
                return null;
            double speed = config.IdleDayCycleSpeed;
            return double.IsFinite(speed) ? Math.Round(speed, 4) : null;
        }

        /// <summary>MaxPlayers from the parsed dedicatedserver.cfg, or 0. Game thread only.</summary>
        internal static int ConfiguredMaxPlayers()
        {
            try
            {
                var config = Config();
                return config == null ? 0 : Math.Max(0, config.MaxPlayers);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // The game's parsed dedicatedserver.cfg. The cached instance is read so a missing one is never loaded here.
        private static ServerDedicatedConfig Config()
        {
            if (_configFailed || !Host.IsServer)
                return null;
            try
            {
                var config = ServerDedicatedConfig._instance;
                return config is null || config.WasCollected ? null : config;
            }
            catch (Exception)
            {
                _configFailed = true;
                return null;
            }
        }

        private static string SlotName(uint id) => id.ToString("D10", CultureInfo.InvariantCulture);

        private static string KindName(SaveGameType type) => type switch
        {
            SaveGameType.Multiplayer => "multiplayer",
            SaveGameType.SinglePlayer => "singleplayer",
            SaveGameType.MultiplayerClient => "client",
            _ => null
        };

        private static void AddFolder(List<(string Path, string Kind)> folders, string path, string kind)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            foreach (var folder in folders)
                if (string.Equals(folder.Path, path, StringComparison.OrdinalIgnoreCase))
                    return;
            folders.Add((path, kind));
        }

        // Without the game's own folders: the folder of the last loaded or saved slot, then <data>/Saves/*/.
        private static void FindFolders(List<(string Path, string Kind)> folders, string lastDirectory, string dataPath)
        {
            var parent = string.IsNullOrEmpty(lastDirectory)
                ? null : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(lastDirectory));
            var kind = FolderKind(Path.GetFileName(parent ?? ""));
            if (kind != null)
                AddFolder(folders, parent, kind);
            var saves = string.IsNullOrEmpty(dataPath) ? null : Path.Combine(dataPath, "Saves");
            if (saves == null || !Directory.Exists(saves))
                return;
            foreach (var profile in Directory.EnumerateDirectories(saves))
            {
                AddFolder(folders, Path.Combine(profile, "Multiplayer"), "multiplayer");
                AddFolder(folders, Path.Combine(profile, "SinglePlayer"), "singleplayer");
            }
        }

        private static string FolderKind(string name) =>
            string.Equals(name, "Multiplayer", StringComparison.OrdinalIgnoreCase) ? "multiplayer"
            : string.Equals(name, "SinglePlayer", StringComparison.OrdinalIgnoreCase) ? "singleplayer"
            : null;

        private static JsonObject ListSaves(List<(string Path, string Kind)> folders, string activeSlot,
            string activeKind, out string human)
        {
            var candidates = new List<(string Id, string Kind, string Directory, DateTime Modified)>();
            var skipped = new JsonArray();
            var truncated = false;
            foreach (var (folder, kind) in folders)
            {
                if (!Directory.Exists(folder))
                    continue;
                try
                {
                    foreach (var directory in Directory.EnumerateDirectories(folder))
                    {
                        var id = Path.GetFileName(directory);
                        if (!IsSlotId(id))
                            continue;
                        if (candidates.Count >= MaxCandidates)
                        {
                            truncated = true;
                            break;
                        }
                        candidates.Add((id, kind, directory, Directory.GetLastWriteTimeUtc(directory)));
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    skipped.Add((JsonNode)Skipped(null, kind, GamePath(folder) + " could not be read: " + error.Message));
                }
            }
            candidates.Sort((a, b) => b.Modified.CompareTo(a.Modified));
            if (candidates.Count > MaxSlots)
            {
                truncated = true;
                candidates.RemoveRange(MaxSlots, candidates.Count - MaxSlots);
            }

            var saves = new JsonArray();
            var text = new StringBuilder();
            foreach (var (id, kind, directory, _) in candidates)
            {
                if (!TryDescribe(directory, out var day, out var modified, out var size, out var problem))
                {
                    skipped.Add((JsonNode)Skipped(id, kind, problem));
                    continue;
                }
                var active = id == activeSlot && kind == activeKind;
                saves.Add((JsonNode)new JsonObject
                {
                    ["id"] = id,
                    ["kind"] = kind,
                    ["inGameDay"] = day,
                    ["lastModified"] = modified,
                    ["sizeBytes"] = size,
                    ["isActive"] = active
                });
                text.Append('\n').Append(id).Append(' ').Append(kind).Append(" · day ").Append(day)
                    .Append(" · ").Append(DateTimeOffset.FromUnixTimeMilliseconds(modified).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                    .Append(" UTC");
                if (size is long bytes)
                    text.Append(" · ").Append((bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)).Append(" MB");
                if (active)
                    text.Append(" · running");
            }

            human = saves.Count == 0 ? "No save slots were found." : saves.Count + (saves.Count == 1 ? " save:" : " saves:") + text;
            if (skipped.Count > 0)
                human += "\n" + skipped.Count + " skipped.";
            return new JsonObject { ["saves"] = saves, ["skipped"] = skipped, ["truncated"] = truncated };
        }

        private static JsonObject Skipped(string id, string kind, string reason) =>
            new JsonObject { ["id"] = id, ["kind"] = kind, ["reason"] = reason };

        // Slot folders are the numeric save id, zero-padded to ten digits; "Temp" and backups are not slots.
        private static bool IsSlotId(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 10)
                return false;
            foreach (var c in name)
                if (c is < '0' or > '9')
                    return false;
            return uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out _);
        }

        private static bool TryDescribe(string directory, out int day, out long modified, out long? size,
            out string problem)
        {
            day = 0;
            modified = 0;
            size = null;
            problem = null;
            try
            {
                var archive = new FileInfo(Path.Combine(directory, ArchiveName));
                var loose = new FileInfo(Path.Combine(directory, GameStateName));
                if (!archive.Exists && !loose.Exists)
                {
                    problem = "No " + ArchiveName + " or " + GameStateName + ".";
                    return false;
                }
                var bytes = archive.Exists ? ReadFromArchive(archive.FullName, out problem) : ReadBounded(loose, out problem);
                if (bytes == null || !TryReadDay(bytes, out day, out problem))
                    return false;
                modified = new DateTimeOffset((archive.Exists ? archive : loose).LastWriteTimeUtc).ToUnixTimeMilliseconds();
                size = DirectorySize(directory);
                return true;
            }
            catch (Exception error)
            {
                problem = "The slot could not be read: " + error.Message;
                return false;
            }
        }

        // Opened shared, so a save that replaces the archive meanwhile is not blocked.
        private static byte[] ReadFromArchive(string path, out string problem)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);
            var entry = archive.GetEntry(GameStateName);
            if (entry == null)
            {
                problem = ArchiveName + " has no " + GameStateName + ".";
                return null;
            }
            if (entry.Length > MaxMetadataBytes)
            {
                problem = GameStateName + " is larger than 1 MiB.";
                return null;
            }
            using var stream = entry.Open();
            return ReadBounded(stream, out problem);
        }

        private static byte[] ReadBounded(FileInfo file, out string problem)
        {
            if (file.Length > MaxMetadataBytes)
            {
                problem = GameStateName + " is larger than 1 MiB.";
                return null;
            }
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ReadBounded(stream, out problem);
        }

        private static byte[] ReadBounded(Stream stream, out string problem)
        {
            problem = null;
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > MaxMetadataBytes)
                {
                    problem = GameStateName + " is larger than 1 MiB.";
                    return null;
                }
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }

        // GameStateSaveData.json wraps the game state as a JSON string. GameDays is left out on day 0.
        private static bool TryReadDay(byte[] bytes, out int day, out string problem)
        {
            day = 0;
            problem = null;
            var json = bytes.AsSpan();
            if (json.StartsWith(Utf8Bom))
                json = json[Utf8Bom.Length..];
            try
            {
                var state = JsonNode.Parse(json)?["Data"]?["GameState"]?.GetValue<string>();
                if (state == null || JsonNode.Parse(state) is not JsonObject game)
                {
                    problem = GameStateName + " has no game state.";
                    return false;
                }
                if (game["GameDays"] is JsonValue days)
                    day = days.GetValue<int>();
                return true;
            }
            catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException
                or FormatException)
            {
                problem = GameStateName + " could not be parsed: " + error.Message;
                return false;
            }
        }

        private sealed record Reading(string Slot, string Kind, int? Day, int? Minute, long Ticks, double? IdleSpeed);

        private sealed record World(string Slot, string Kind, int? Day, int? Minute, long? DayStartedAt,
            double? IdleSpeed, bool ClockRunning);
    }
}
