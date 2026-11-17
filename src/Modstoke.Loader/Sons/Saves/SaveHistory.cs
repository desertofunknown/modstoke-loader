// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Modstoke.Loader;
using global::Sons.Gui;
using global::Sons.Save;

namespace Modstoke.Sons
{
    /// <summary>
    /// Earlier saves of every world. After each save, and when a world loads, a copy of its slot and of the sidecar
    /// files mods keep for it goes under the Modstoke save data, is checked for damage, and the slot's history is
    /// trimmed. A damaged save is kept apart and the player is told. A kept copy can be put back while its world
    /// isn't loaded, or on a dedicated server when it next starts.
    /// </summary>
    public static class SaveHistory
    {
        private const string FolderName = "History";
        private const string StagedFile = "restore.json";
        private const string StagedFolder = "staged";
        private const int GameSaveWaitMs = 2000;
        private const int GameSaveLimitMs = 30000;
        private const int CopyAttempts = 3;
        private const float ToastSeconds = 10f;
        private const long ToastLifetimeMs = 120000;

        private static readonly object Sync = new object();
        // Held while a copy reads a slot. The game's save waits on it, so the two never touch the slot at once.
        private static readonly SemaphoreSlim Reading = new SemaphoreSlim(1, 1);
        private static readonly HashSet<string> FailedLoads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Per slot, every archive the game refused to load this session, so no offer goes back to one of them.
        private static readonly Dictionary<string, HashSet<string>> FailedArchives = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, (string Stamp, SaveCheck Check)> Checks = new(StringComparer.OrdinalIgnoreCase);
        // Restores asked for and not finished, by slot; that slot's checks wait behind them.
        private static readonly Dictionary<string, int> PendingRestores = new(StringComparer.OrdinalIgnoreCase);
        // One per slot: a check reading SaveData.zip and a restore replacing it take turns.
        private static readonly Dictionary<string, object> SlotGates = new(StringComparer.OrdinalIgnoreCase);
        private static Task _queue = Task.CompletedTask;
        // Damage checks only read the live slot, so they wait on each other but not on history work.
        private static Task _checks = Task.CompletedTask;
        private static long _gameSaveStarted;
        private static bool _initialized;
        private static string _toast;
        private static long _toastUntil;
        private static Timer _toastTimer;

        /// <summary>Raised on the game thread after a slot was put back to one of its earlier saves.</summary>
        public static event Action<SaveRestoredEvent> Restored;

        /// <summary>A slot's whole earlier saves, newest first. Reads the disk, so call it off the game thread.</summary>
        public static IReadOnlyList<SaveHistoryEntry> List(string slotDirectory)
        {
            var history = HistoryOf(slotDirectory);
            return history == null ? Array.Empty<SaveHistoryEntry>() : SaveHistoryStore.Entries(history);
        }

        internal static string Root
        {
            get
            {
                var saves = SaveData.State.ModstokeSaveDataDirectory;
                return string.IsNullOrEmpty(saves) ? null : Path.Combine(saves, FolderName);
            }
        }

        private static string ModsRoot => SaveData.State.ModstokeModSaveDataDirectory;
        private static string StagedPath => Root == null ? null : Path.Combine(Root, StagedFile);

        internal static string HistoryOf(string slotDirectory) => SaveHistoryStore.HistoryDirectory(Root, slotDirectory);

        internal static void Initialize()
        {
            if (_initialized)
                return;
            _initialized = true;
            SaveLifecycle.Lifecycle += OnLifecycle;
            // Before any world loads, so the game reads the slot as it was restored.
            ApplyStaged();
            Background("Clearing history of deleted saves", () =>
            {
                var removed = SaveHistoryStore.SweepDeleted(Root, TimeSpan.FromDays(Math.Max(Limits().Days, 7)), DateTimeOffset.Now);
                if (removed > 0)
                    RuntimeEntryPoint.LogSource?.LogInfo("Removed the earlier saves of " + removed + " deleted save slot(s).");
            });
        }

        // Runs inside the game's save and load calls, so it only records and queues.
        private static void OnLifecycle(SaveLifecycleEvent evt)
        {
            if (evt?.Directory == null)
                return;
            if (evt.Operation == "Save")
            {
                if (evt.Phase == "Before")
                {
                    Interlocked.Exchange(ref _gameSaveStarted, Environment.TickCount64);
                    // A copy part-way through reading the slot finishes before the game rewrites it.
                    if (Reading.Wait(GameSaveWaitMs))
                        Reading.Release();
                    return;
                }
                Interlocked.Exchange(ref _gameSaveStarted, 0);
            }
            if (evt.Phase != "After" || HistoryOf(evt.Directory) == null)
                return;

            var directory = SaveHistoryStore.Normalize(evt.Directory);
            var slot = Path.GetFileName(directory);
            if (evt.Operation == "Save")
            {
                Background("Keeping a copy of save " + slot, () => Capture(directory, "save", false));
                return;
            }
            if (evt.Success == true)
            {
                lock (Sync)
                    FailedLoads.Remove(directory);
                Background("Keeping a copy of save " + slot, () =>
                {
                    // Bytes that load are no longer kept from an offer.
                    var loaded = Capture(directory, "load", false)?.Hash ?? SaveHistoryStore.ArchiveHash(directory);
                    lock (Sync)
                        if (FailedArchives.TryGetValue(directory, out var failed))
                            failed.Remove(loaded);
                });
                return;
            }

            Background("Keeping the last good copy of save " + slot, () =>
            {
                // An empty slot, as on a first join of a server, has nothing to lose.
                if (!File.Exists(Path.Combine(directory, SaveHistoryStore.ArchiveName))
                    && !File.Exists(Path.Combine(directory, SaveHistoryStore.GameStateName)))
                    return;
                var refused = SaveHistoryStore.ArchiveHash(directory);
                lock (Sync)
                {
                    FailedLoads.Add(directory);
                    if (!FailedArchives.TryGetValue(directory, out var failed))
                        FailedArchives[directory] = failed = new HashSet<string>(StringComparer.Ordinal);
                    failed.Add(refused);
                }
                RuntimeEntryPoint.LogSource?.LogWarning("The game could not load save " + slot + ". "
                    + (Host.IsServer ? "'saves history " + slot + "' lists its earlier saves." : "Its earlier saves are kept."));
                SaveHistoryStore.Pin(HistoryOf(directory), directory, FailedArchivesOf(directory));
                if (!Host.IsServer)
                    Tell(IsClientSave(directory) ? "saves.clientRefusedToast" : "saves.refusedToast");
            });
        }

        private static IReadOnlyCollection<string> FailedArchivesOf(string directory)
        {
            lock (Sync)
                return FailedArchives.TryGetValue(directory, out var failed) ? new List<string>(failed) : Array.Empty<string>();
        }

        private static object GateOf(string directory)
        {
            lock (Sync)
            {
                if (!SlotGates.TryGetValue(directory, out var gate))
                    SlotGates[directory] = gate = new object();
                return gate;
            }
        }

        // A player's own saves for servers they join aren't in the title screen's lists, so their toasts only say
        // that earlier copies are kept.
        private static bool IsClientSave(string directory)
            => string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), "MultiplayerClient", StringComparison.OrdinalIgnoreCase);

        /// <summary>A slot's folder from the game's save folders, or null before the game has set them up. Game thread.</summary>
        internal static string SlotDirectory(SaveGameType type, uint id)
        {
            var folder = type switch
            {
                SaveGameType.SinglePlayer => SaveGameManager._singlePlayerSaveGameFolder,
                SaveGameType.Multiplayer => SaveGameManager._multiplayerSaveGameFolder,
                SaveGameType.MultiplayerClient => SaveGameManager._multiplayerClientSaveGameFolder,
                _ => null
            };
            return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, id.ToString("D10", CultureInfo.InvariantCulture));
        }

        /// <summary>Whether the game failed to load this slot during this session, with no restore or good load since.</summary>
        internal static bool LoadFailed(string slotDirectory)
        {
            var directory = SaveHistoryStore.Normalize(slotDirectory);
            lock (Sync)
                return FailedLoads.Contains(directory);
        }

        /// <summary>
        /// Checks the save in a slot, after the checks asked for before it, or straight away when the player is
        /// waiting on it; a slot with a restore pending is checked once the restore is done. A verdict is kept until
        /// the slot's archive changes; a failed read is not kept.
        /// </summary>
        internal static Task<SaveCheck> CheckAsync(string slotDirectory, bool now = false)
        {
            var directory = SaveHistoryStore.Normalize(slotDirectory);
            SaveCheck Run()
            {
                var archive = new FileInfo(Path.Combine(directory, SaveHistoryStore.ArchiveName));
                var stamp = archive.Exists ? archive.Length + ":" + archive.LastWriteTimeUtc.Ticks : "";
                lock (Sync)
                    if (Checks.TryGetValue(directory, out var known) && known.Stamp == stamp)
                        return known.Check;
                SaveCheck check;
                lock (GateOf(directory))
                    check = SaveHistoryStore.Check(directory);
                if (!check.Unread)
                    lock (Sync)
                        Checks[directory] = (stamp, check);
                return check;
            }
            bool pending;
            lock (Sync)
                pending = PendingRestores.ContainsKey(directory);
            return pending ? Queue(Run) : now ? Task.Run(Run) : Then(ref _checks, Run);
        }

        /// <summary>A slot's earlier saves, newest first, after filing any of the game's own backups of it not seen yet.</summary>
        internal static Task<IReadOnlyList<SaveHistoryEntry>> ListAsync(string slotDirectory)
            => Queue<IReadOnlyList<SaveHistoryEntry>>(() => ListNow(slotDirectory));

        /// <summary>
        /// The copy to offer for a slot that is damaged or that the game would not load: the newest whole one whose
        /// files are neither what the slot holds nor any the game already refused, as those would fail again. Null
        /// when there is none.
        /// </summary>
        internal static Task<SaveHistoryEntry> GoodCopyAsync(string slotDirectory) => Queue(() =>
        {
            var failed = FailedArchivesOf(SaveHistoryStore.Normalize(slotDirectory));
            return SaveHistoryStore.Differing(ListNow(slotDirectory), slotDirectory, failed);
        });

        private static IReadOnlyList<SaveHistoryEntry> ListNow(string slotDirectory)
        {
            var history = HistoryOf(slotDirectory);
            if (history == null)
                return Array.Empty<SaveHistoryEntry>();
            try
            {
                SaveHistoryStore.ImportGameBackups(history, slotDirectory, Limits(), DateTimeOffset.Now);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The game's own backups of save " + Path.GetFileName(slotDirectory)
                    + " could not be read: " + error.Message);
            }
            return SaveHistoryStore.Entries(history);
        }

        /// <summary>A slot's damaged copies, kept apart, newest first. Reads the disk.</summary>
        internal static IReadOnlyList<SaveHistoryEntry> Damaged(string slotDirectory)
        {
            var history = HistoryOf(slotDirectory);
            return history == null ? Array.Empty<SaveHistoryEntry>() : SaveHistoryStore.Entries(history, true);
        }

        /// <summary>
        /// Puts an earlier save back now, from the title screen only: a loaded world would save over it. Elsewhere,
        /// <see cref="StageAsync"/> puts it back at the next start.
        /// </summary>
        internal static Task RestoreAsync(string slotDirectory, SaveHistoryEntry entry)
        {
            if (!GameRuntime.IsTitleScene)
                return Task.FromException(new InvalidOperationException("Saves are only restored from the title screen."));
            var directory = SaveHistoryStore.Normalize(slotDirectory);
            lock (Sync)
                PendingRestores[directory] = PendingRestores.GetValueOrDefault(directory) + 1;
            return Queue(() =>
            {
                try
                {
                    // Asked again when its turn comes: a world may have started loading meanwhile.
                    if (!GameRuntime.IsTitleScene)
                        throw new InvalidOperationException("Saves are only restored from the title screen.");
                    RestoreNow(directory, entry);
                    return true;
                }
                finally
                {
                    lock (Sync)
                        if (--PendingRestores[directory] == 0)
                            PendingRestores.Remove(directory);
                }
            });
        }

        /// <summary>Whether a restore asked for at the title is still waiting or running.</summary>
        internal static bool Restoring
        {
            get
            {
                lock (Sync)
                    return PendingRestores.Count > 0;
            }
        }

        /// <summary>The restore waiting for the next start, or null.</summary>
        internal static StagedRestore Staged
        {
            get
            {
                try
                {
                    var path = StagedPath;
                    if (path == null || !File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject json)
                        return null;
                    var entry = SaveHistoryStore.Read((string)json["entry"]);
                    return entry == null ? null : new StagedRestore((string)json["slot"], entry,
                        DateTimeOffset.Parse((string)json["staged"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                    or FormatException or InvalidOperationException or ArgumentException)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Sets an earlier save to be put back the next time the game or server starts, before any world loads. The
        /// entry is copied aside, so the slot's trimming can't remove it meanwhile. One restore waits at a time.
        /// </summary>
        internal static Task<StagedRestore> StageAsync(string slotDirectory, SaveHistoryEntry entry) => Queue(() =>
        {
            var copy = SaveHistoryStore.Clone(entry, Path.Combine(Root, StagedFolder, entry.Id));
            var now = DateTimeOffset.Now;
            var json = new JsonObject
            {
                ["slot"] = SaveHistoryStore.Normalize(slotDirectory),
                ["entry"] = copy.Directory,
                ["staged"] = now.ToString("O", CultureInfo.InvariantCulture)
            };
            var temporary = StagedPath + ".tmp";
            File.WriteAllText(temporary, json.ToJsonString());
            File.Move(temporary, StagedPath, true);
            ClearStagedCopies(copy.Directory);
            return new StagedRestore(SaveHistoryStore.Normalize(slotDirectory), copy, now);
        });

        /// <summary>Drops the restore waiting for the next start; false when there was none.</summary>
        internal static Task<bool> UnstageAsync() => Queue(() =>
        {
            var had = File.Exists(StagedPath);
            File.Delete(StagedPath);
            ClearStagedCopies(null);
            return had;
        });

        private static void ApplyStaged()
        {
            var staged = Staged;
            if (staged == null)
                return;
            var slot = Path.GetFileName(staged.SlotDirectory);
            try
            {
                RestoreNow(staged.SlotDirectory, staged.Entry);
                File.Delete(StagedPath);
                ClearStagedCopies(null);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The restore staged for save " + slot + " did not apply and stays staged: "
                    + error.Message);
            }
        }

        private static void ClearStagedCopies(string keep)
        {
            var folder = Path.Combine(Root, StagedFolder);
            if (!Directory.Exists(folder))
                return;
            foreach (var directory in Directory.GetDirectories(folder))
                if (keep == null || !string.Equals(directory, keep, StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(directory, true);
        }

        private static void RestoreNow(string slotDirectory, SaveHistoryEntry entry)
        {
            var directory = SaveHistoryStore.Normalize(slotDirectory);
            // What the slot holds now becomes an earlier save first, so a restore can always be undone. That capture's
            // trim spares the entry about to be put back, so a restore that fails still leaves it in history.
            Capture(directory, "restore", true, entry.Id);
            // Dropped before the swap as well as after, so no check of the old files outlives it.
            lock (Sync)
                Checks.Remove(directory);
            lock (GateOf(directory))
                SaveHistoryStore.Restore(entry, directory, ModsRoot, DateTime.UtcNow);
            lock (Sync)
            {
                FailedLoads.Remove(directory);
                Checks.Remove(directory);
            }
            RuntimeEntryPoint.LogSource?.LogMessage("Save " + Path.GetFileName(directory) + " was restored to its copy from "
                + entry.Saved.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture) + ".");
            var evt = new SaveRestoredEvent(directory, entry);
            Host.Post(() => Modding.Dispatch("OpenACAISonsSaveHistory.Restored", Restored, evt));
        }

        private static SaveHistoryEntry Capture(string directory, string reason, bool undo, string keepId = null)
        {
            if (!undo && !RuntimePreferences.ReadBoolean("saveHistory", true))
                return null;
            var history = HistoryOf(directory);
            var limits = Limits();
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var copy = CopyIn(history, directory);
                    if (copy == null)
                        return null;
                    if (copy.Stable)
                    {
                        var entry = SaveHistoryStore.Commit(history, copy, reason, undo, limits, DateTimeOffset.Now, out var duplicate, keepId);
                        // A world loaded from a damaged save is reported every time; a damaged save only when it is new.
                        if (entry.Damaged && (reason == "load" || reason == "save" && !duplicate))
                            ReportDamage(directory, reason, entry);
                        return entry;
                    }
                    SaveHistoryStore.Discard(copy);
                    if (attempt == CopyAttempts)
                        throw new IOException("the slot kept changing while it was copied.");
                }
                // A file another program holds for a moment, such as a virus scanner or a cloud client, is tried again.
                catch (IOException) when (attempt < CopyAttempts)
                {
                }
                Thread.Sleep(500);
            }
        }

        private static SaveCopy CopyIn(string history, string directory)
        {
            while (true)
            {
                Reading.Wait();
                try
                {
                    if (!GameSaving)
                        return SaveHistoryStore.CopyIn(history, directory, ModsRoot);
                }
                finally
                {
                    Reading.Release();
                }
                Thread.Sleep(250);
            }
        }

        // A save whose After never came (it threw) stops counting after the same limit the host uses.
        private static bool GameSaving
        {
            get
            {
                var started = Interlocked.Read(ref _gameSaveStarted);
                return started != 0 && Environment.TickCount64 - started < GameSaveLimitMs;
            }
        }

        private static void ReportDamage(string directory, string reason, SaveHistoryEntry entry)
        {
            var slot = Path.GetFileName(directory);
            RuntimeEntryPoint.LogSource?.LogWarning("Save " + slot + " looks damaged: " + entry.Problem
                + " The copy is kept apart from its earlier saves"
                + (Host.IsServer ? "; 'saves history " + slot + "' lists them." : "."));
            HostEvents.Emit("save-damaged", new JsonObject { ["slot"] = slot, ["reason"] = reason, ["problem"] = entry.Problem });
            if (!Host.IsServer)
                Tell(IsClientSave(directory) ? "saves.clientDamagedToast" : "saves.damagedToast");
        }

        // Shown once the player can see the world; damage is found a moment after the save, and a failed load
        // reports before the world is up.
        private static void Tell(string key)
        {
            lock (Sync)
            {
                _toast = key;
                _toastUntil = Environment.TickCount64 + ToastLifetimeMs;
                _toastTimer ??= new Timer(_ => Host.Post(ShowToast));
                _toastTimer.Change(0, 2000);
            }
        }

        private static void ShowToast()
        {
            string key;
            lock (Sync)
            {
                key = _toast;
                if (key == null || Environment.TickCount64 > _toastUntil)
                {
                    _toast = null;
                    _toastTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    return;
                }
            }
            var hud = HudGui._instance;
            if (!GameRuntime.IsInWorld || hud is null || hud.WasCollected || !hud)
                return;
            hud.DisplayGeneralMessage(LocalizationStartup.Text[key], ToastSeconds, null);
            lock (Sync)
            {
                if (_toast != key)
                    return;
                _toast = null;
                _toastTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        private static SaveHistoryLimits Limits() => new SaveHistoryLimits(
            Setting("saveHistoryRecent", 10, 1, 100),
            Setting("saveHistoryDays", 7, 0, 60),
            Setting("saveHistoryMaxMB", 512, 16, 1 << 20) * 1024L * 1024L);

        private static int Setting(string key, int fallback, int min, int max)
            => int.TryParse(RuntimePreferences.ReadText(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? Math.Clamp(value, min, max)
                : fallback;

        // History work runs one job at a time, in order, off the game thread.
        private static Task<T> Queue<T>(Func<T> work) => Then(ref _queue, work);

        private static Task<T> Then<T>(ref Task tail, Func<T> work)
        {
            lock (Sync)
            {
                var next = tail.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                tail = next;
                return next;
            }
        }

        private static void Background(string what, Action work) => Queue(() =>
        {
            try
            {
                work();
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning(what + " failed: " + error.Message);
            }
            return true;
        });
    }

    /// <summary>A slot that was put back to one of its earlier saves.</summary>
    public sealed class SaveRestoredEvent
    {
        internal SaveRestoredEvent(string slotDirectory, SaveHistoryEntry entry)
        {
            SlotDirectory = slotDirectory;
            Entry = entry;
        }

        public string SlotDirectory { get; }
        public SaveHistoryEntry Entry { get; }
    }

    /// <summary>A restore set for the next start.</summary>
    internal sealed record StagedRestore(string SlotDirectory, SaveHistoryEntry Entry, DateTimeOffset StagedAt);
}
