// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Modstoke.Sons
{
    /// <summary>One earlier save of a slot, as kept under the Modstoke save data.</summary>
    public sealed class SaveHistoryEntry
    {
        internal SaveHistoryEntry(string directory, string slotDirectory, DateTimeOffset saved, int? day, long sizeBytes,
            string reason, string hash, string problem, bool undo, bool pinned)
        {
            Directory = directory;
            SlotDirectory = slotDirectory;
            Id = Path.GetFileName(directory);
            Saved = saved;
            Day = day;
            SizeBytes = sizeBytes;
            Reason = reason;
            Hash = hash;
            Problem = problem;
            Undo = undo;
            Pinned = pinned;
        }

        public string Id { get; }
        /// <summary>When the game wrote this save, from the save itself.</summary>
        public DateTimeOffset Saved { get; }
        /// <summary>The in-game day the save reached; null when it could not be read.</summary>
        public int? Day { get; }
        public long SizeBytes { get; }
        /// <summary>
        /// Why the copy was kept: <c>save</c>, <c>load</c> (the world as it was loaded), <c>restore</c> (the slot
        /// as it was just before a restore) or <c>game-backup</c> (one of the game's own backups).
        /// </summary>
        public string Reason { get; }
        /// <summary>Why the copy failed the damage check; null for a whole save.</summary>
        internal string Problem { get; }
        internal bool Damaged => Problem != null;
        internal string Directory { get; }
        /// <summary>The slot folder the copy was taken from.</summary>
        internal string SlotDirectory { get; }
        internal string Hash { get; }
        internal bool Undo { get; }
        internal bool Pinned { get; }
    }

    /// <summary>
    /// What the damage check found in a save's files. <see cref="Unread"/> means they couldn't be read just then, as
    /// when another program holds them, which says nothing about damage; try again later.
    /// </summary>
    internal sealed record SaveCheck(string Problem, int? Day, DateTimeOffset? Saved, bool Unread = false)
    {
        internal bool Ok => Problem == null;
        internal bool Damaged => Problem != null && !Unread;
    }

    /// <summary>How much of a slot's history is kept.</summary>
    internal sealed record SaveHistoryLimits(int Recent, int Days, long MaxBytes, int Damaged = 3);

    /// <summary>
    /// The files behind save history: copies of a save slot, with its mods' sidecar files, under the Modstoke save
    /// data; the check that tells a whole save from a damaged one; what is kept; and putting a copy back. Plain file
    /// work for a worker thread, apart from the game.
    /// </summary>
    internal static class SaveHistoryStore
    {
        internal const string ArchiveName = "SaveData.zip";
        internal const string GameStateName = "GameStateSaveData.json";
        private const string MainName = "SaveData.json";
        private const string EntryFile = "entry.json";
        private const string SlotFolder = "slot";
        private const string ModsFolder = "mods";
        private const string DamagedFolder = "damaged";
        private const string IncomingPrefix = ".incoming-";
        private const string ImportedFile = "game-backups.txt";
        private const string MissingFile = "slot-missing.txt";
        private const string RestoreSuffix = ".modstoke-restore";
        private const long MaxEntryBytes = 1L << 30;
        private static readonly string[] Kinds = { "SinglePlayer", "Multiplayer", "MultiplayerClient" };
        private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

        /// <summary>
        /// Where a slot's history lives: root/profile/kind/slot, for a slot folder the game names
        /// Saves/profile/kind/id. Null for anything else, such as Temp or QuickSave.
        /// </summary>
        internal static string HistoryDirectory(string root, string slotDirectory)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(slotDirectory))
                return null;
            var slot = Normalize(slotDirectory);
            var kindPath = Path.GetDirectoryName(slot);
            var profile = Path.GetFileName(Path.GetDirectoryName(kindPath ?? "") ?? "");
            var kind = Array.Find(Kinds, name => string.Equals(name, Path.GetFileName(kindPath), StringComparison.OrdinalIgnoreCase));
            var id = Path.GetFileName(slot);
            if (kind == null || string.IsNullOrEmpty(profile) || !IsSlotName(id))
                return null;
            return Path.Combine(root, profile, kind, id);
        }

        internal static string Normalize(string directory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

        // Slot folders are the numeric save id, zero-padded to ten digits.
        private static bool IsSlotName(string name)
            => name.Length is > 0 and <= 10 && name.All(char.IsAsciiDigit)
                && uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out _);

        /// <summary>
        /// The slot's files, and the sidecar files mods keep for it under <paramref name="modsRoot"/>, copied into a
        /// new folder in <paramref name="history"/>. Null when the slot has no files. Sources are opened so the game
        /// is never kept from writing them; a copy whose slot changed while it was read comes back unstable.
        /// </summary>
        internal static SaveCopy CopyIn(string history, string slotDirectory, string modsRoot)
        {
            if (!Directory.Exists(slotDirectory))
                return null;
            var files = Directory.GetFiles(slotDirectory);
            if (files.Length == 0)
                return null;
            var incoming = NewIncoming(history);
            try
            {
                var slot = Directory.CreateDirectory(Path.Combine(incoming, SlotFolder)).FullName;
                var before = Stamp(files);
                string hash = null;
                foreach (var file in files)
                {
                    var name = Path.GetFileName(file);
                    if (name.EndsWith(RestoreSuffix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var archive = IsArchive(name);
                    using var sha = archive ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
                    if (!TryCopy(file, Path.Combine(slot, name), sha))
                        continue;
                    if (archive)
                        hash = Convert.ToHexStringLower(sha.GetHashAndReset());
                }

                // Every capture carries a mods folder, empty or not; a restore then knows to bring sidecars back in step.
                var mods = Directory.CreateDirectory(Path.Combine(incoming, ModsFolder)).FullName;
                var key = Path.GetFileName(Normalize(slotDirectory));
                if (!string.IsNullOrEmpty(modsRoot) && Directory.Exists(modsRoot))
                {
                    foreach (var mod in Directory.GetDirectories(modsRoot))
                    {
                        var sidecars = Path.Combine(mod, "Slots", key);
                        if (!Directory.Exists(sidecars))
                            continue;
                        var target = Directory.CreateDirectory(Path.Combine(mods, Path.GetFileName(mod))).FullName;
                        foreach (var file in Directory.GetFiles(sidecars, "*.json"))
                            TryCopy(file, Path.Combine(target, Path.GetFileName(file)), null);
                    }
                }

                var stable = before == Stamp(Directory.GetFiles(slotDirectory));
                return new SaveCopy(incoming, Normalize(slotDirectory), hash ?? "", stable);
            }
            catch
            {
                DeleteQuietly(incoming);
                throw;
            }
        }

        /// <summary>
        /// Keeps a copy made by <see cref="CopyIn"/>: checked, filed with the whole saves or apart with the damaged ones,
        /// and the slot's history trimmed. A copy identical to one already kept is dropped and that entry returned with
        /// <paramref name="duplicate"/> set; for <paramref name="undo"/> it becomes the one a restore can go back to.
        /// A copy that can't be read just then is dropped with an IOException, for the caller to try again. The entry
        /// <paramref name="keepId"/> names survives the trim, as the one a restore is about to put back.
        /// </summary>
        internal static SaveHistoryEntry Commit(string history, SaveCopy copy, string reason, bool undo,
            SaveHistoryLimits limits, DateTimeOffset now, out bool duplicate, string keepId = null)
        {
            duplicate = false;
            if (copy.Hash.Length > 0)
            {
                var same = Entries(history).Concat(Entries(history, true)).FirstOrDefault(entry => entry.Hash == copy.Hash);
                if (same != null)
                {
                    DeleteQuietly(copy.Directory);
                    duplicate = true;
                    return undo && !same.Undo ? Rewrite(same, undo: true) : same;
                }
            }

            var slot = Path.Combine(copy.Directory, SlotFolder);
            var check = Check(slot);
            if (check.Unread)
            {
                DeleteQuietly(copy.Directory);
                throw new IOException(check.Problem);
            }
            var saved = check.Saved ?? ArchiveTime(slot) ?? now;
            var json = new JsonObject
            {
                ["version"] = 1,
                ["saved"] = saved.ToString("O", CultureInfo.InvariantCulture),
                ["kept"] = now.ToString("O", CultureInfo.InvariantCulture),
                ["day"] = check.Day,
                ["sizeBytes"] = FolderSize(copy.Directory),
                ["reason"] = reason,
                ["slot"] = copy.Slot,
                ["hash"] = copy.Hash,
                ["problem"] = check.Problem,
                ["undo"] = undo,
                ["pinned"] = false
            };
            File.WriteAllText(Path.Combine(copy.Directory, EntryFile), json.ToJsonString());

            var parent = check.Ok ? history : Directory.CreateDirectory(Path.Combine(history, DamagedFolder)).FullName;
            var target = Path.Combine(parent, now.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture));
            for (var n = 2; Directory.Exists(target); n++)
                target = Path.Combine(parent, now.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture) + "-" + n);
            Directory.Move(copy.Directory, target);
            var entry = Read(target);
            Prune(history, limits, now, keepId);
            return entry;
        }

        /// <summary>The slot's whole earlier saves, newest first, or its damaged ones with <paramref name="damaged"/>.</summary>
        internal static List<SaveHistoryEntry> Entries(string history, bool damaged = false)
        {
            var list = new List<SaveHistoryEntry>();
            var folder = damaged ? Path.Combine(history, DamagedFolder) : history;
            if (!Directory.Exists(folder))
                return list;
            foreach (var directory in Directory.GetDirectories(folder))
            {
                var name = Path.GetFileName(directory);
                if (name.StartsWith('.') || string.Equals(name, DamagedFolder, StringComparison.OrdinalIgnoreCase))
                    continue;
                var entry = Read(directory);
                if (entry != null)
                    list.Add(entry);
            }
            list.Sort((a, b) => b.Saved != a.Saved ? b.Saved.CompareTo(a.Saved) : string.CompareOrdinal(b.Id, a.Id));
            return list;
        }

        /// <summary>
        /// Trims a slot's history to <paramref name="limits"/>, drops the damaged copies past their own count and
        /// clears folders an interrupted copy left behind.
        /// </summary>
        internal static void Prune(string history, SaveHistoryLimits limits, DateTimeOffset now, string keepId = null)
        {
            var entries = Entries(history);
            var keep = Keep(entries, limits, now, keepId);
            foreach (var entry in entries)
                if (!keep.Contains(entry))
                    DeleteQuietly(entry.Directory);
            foreach (var entry in Entries(history, true).Skip(limits.Damaged))
                DeleteQuietly(entry.Directory);
            foreach (var directory in Directory.GetDirectories(history, IncomingPrefix + "*"))
                if (Directory.GetCreationTimeUtc(directory) < now.UtcDateTime.AddHours(-1))
                    DeleteQuietly(directory);
        }

        /// <summary>
        /// Which entries stay: the newest few, the newest of each recent day, the copy a restore can be undone to and
        /// the last good copy before a load failed, and <paramref name="keepId"/>'s entry. Over the size cap the oldest
        /// go first, never one of those or the newest of all.
        /// </summary>
        internal static HashSet<SaveHistoryEntry> Keep(IReadOnlyList<SaveHistoryEntry> newestFirst, SaveHistoryLimits limits,
            DateTimeOffset now, string keepId = null)
        {
            var keep = new HashSet<SaveHistoryEntry>();
            for (var i = 0; i < newestFirst.Count && i < limits.Recent; i++)
                keep.Add(newestFirst[i]);
            var today = now.ToLocalTime().Date;
            var days = new HashSet<DateTime>();
            foreach (var entry in newestFirst)
            {
                var day = entry.Saved.ToLocalTime().Date;
                if ((today - day).TotalDays < limits.Days && days.Add(day))
                    keep.Add(entry);
            }
            var undo = newestFirst.FirstOrDefault(entry => entry.Undo);
            var pinned = newestFirst.FirstOrDefault(entry => entry.Pinned);
            var asked = keepId == null ? null : newestFirst.FirstOrDefault(entry => entry.Id == keepId);
            foreach (var entry in new[] { undo, pinned, asked })
                if (entry != null)
                    keep.Add(entry);

            var total = keep.Sum(entry => entry.SizeBytes);
            for (var i = newestFirst.Count - 1; i > 0 && total > limits.MaxBytes; i--)
            {
                var entry = newestFirst[i];
                if (entry == undo || entry == pinned || entry == asked || !keep.Remove(entry))
                    continue;
                total -= entry.SizeBytes;
            }
            return keep;
        }

        internal static void Discard(SaveCopy copy) => DeleteQuietly(copy.Directory);

        /// <summary>A copy of an entry's folder at <paramref name="target"/>, out of reach of the slot's trimming.</summary>
        internal static SaveHistoryEntry Clone(SaveHistoryEntry entry, string target)
        {
            DeleteQuietly(target);
            foreach (var file in Directory.GetFiles(entry.Directory, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(target, Path.GetRelativePath(entry.Directory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(file, to);
            }
            return Read(target);
        }

        /// <summary>Every slot folder with history under <paramref name="root"/>: root/profile/kind/slot.</summary>
        internal static IEnumerable<string> Histories(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                yield break;
            // Only the game's kinds of save, so the staged restore's folder beside the profiles is never taken for one.
            foreach (var profile in Directory.GetDirectories(root))
                foreach (var kind in Directory.GetDirectories(profile))
                    if (Array.Exists(Kinds, name => string.Equals(name, Path.GetFileName(kind), StringComparison.OrdinalIgnoreCase)))
                        foreach (var slot in Directory.GetDirectories(kind))
                            yield return slot;
        }

        /// <summary>
        /// Drops the history of slots deleted from a save folder that is still here, <paramref name="grace"/> after
        /// the deletion was first seen. History of another profile's saves, whose folder is missing, is left alone.
        /// </summary>
        internal static int SweepDeleted(string root, TimeSpan grace, DateTimeOffset now)
        {
            var removed = 0;
            foreach (var history in Histories(root).ToList())
            {
                var slot = Entries(history).Concat(Entries(history, true)).FirstOrDefault()?.SlotDirectory;
                var marker = Path.Combine(history, MissingFile);
                if (slot == null || Directory.Exists(slot) || !Directory.Exists(Path.GetDirectoryName(slot)))
                {
                    if (File.Exists(marker))
                        File.Delete(marker);
                    continue;
                }
                if (!File.Exists(marker))
                {
                    File.WriteAllText(marker, now.ToString("O", CultureInfo.InvariantCulture));
                    continue;
                }
                if (DateTimeOffset.TryParse(File.ReadAllText(marker), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var seen)
                    && now - seen < grace)
                    continue;
                DeleteQuietly(history);
                removed++;
            }
            return removed;
        }

        /// <summary>
        /// Marks the copy to keep after a load of the slot failed: the newest whole one whose bytes are neither what
        /// the slot holds nor any that already failed to load, since those would fail the same way.
        /// </summary>
        internal static SaveHistoryEntry Pin(string history, string slotDirectory, IReadOnlyCollection<string> failed)
        {
            var good = Differing(Entries(history), slotDirectory, failed);
            return good == null || good.Pinned ? good : Rewrite(good, pinned: true);
        }

        /// <summary>
        /// The newest of <paramref name="newestFirst"/> whose SaveData.zip is not the one the slot holds now, nor one of
        /// the <paramref name="failed"/> hashes.
        /// </summary>
        internal static SaveHistoryEntry Differing(IEnumerable<SaveHistoryEntry> newestFirst, string slotDirectory,
            IReadOnlyCollection<string> failed = null)
        {
            var current = ArchiveHash(slotDirectory);
            return newestFirst.FirstOrDefault(entry => entry.Hash != current && (failed == null || !failed.Contains(entry.Hash)));
        }

        /// <summary>The SHA-256 of a slot's SaveData.zip as entries record it, or empty when there is none.</summary>
        internal static string ArchiveHash(string slotDirectory)
        {
            var archive = Path.Combine(slotDirectory, ArchiveName);
            if (!File.Exists(archive))
                return "";
            using var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexStringLower(SHA256.HashData(file));
        }

        /// <summary>
        /// Puts an entry's files back in the slot folder, file by file through a temporary name in the same folder so
        /// each one is replaced whole; SaveData.zip goes last, so until it is in place the slot still loads as it was.
        /// Files the entry doesn't have are removed, and the mods' sidecar files follow the entry when it has them.
        /// Restored files are stamped now, so Steam Cloud takes them as the newest change.
        /// </summary>
        internal static void Restore(SaveHistoryEntry entry, string slotDirectory, string modsRoot, DateTime nowUtc)
        {
            var source = Path.Combine(entry.Directory, SlotFolder);
            var check = Check(source);
            if (check.Unread)
                throw new IOException(check.Problem);
            if (!check.Ok)
                throw new InvalidDataException("The earlier save is damaged: " + check.Problem);
            var names = Directory.GetFiles(source).Select(Path.GetFileName).ToList();
            names.Sort((a, b) => IsArchive(a).CompareTo(IsArchive(b)));
            Directory.CreateDirectory(slotDirectory);
            foreach (var name in names)
            {
                var staged = Path.Combine(slotDirectory, name + RestoreSuffix);
                File.Copy(Path.Combine(source, name), staged, true);
                File.SetLastWriteTimeUtc(staged, nowUtc);
                File.Move(staged, Path.Combine(slotDirectory, name), true);
            }
            foreach (var file in Directory.GetFiles(slotDirectory))
                if (!names.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
                    File.Delete(file);

            // Copies imported from the game's own backups never had sidecars, so the current ones stay as they are.
            var saved = Path.Combine(entry.Directory, ModsFolder);
            if (!Directory.Exists(saved) || string.IsNullOrEmpty(modsRoot))
                return;
            var key = Path.GetFileName(Normalize(slotDirectory));
            var mods = new HashSet<string>(Directory.GetDirectories(saved).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(modsRoot))
                foreach (var mod in Directory.GetDirectories(modsRoot))
                    if (Directory.Exists(Path.Combine(mod, "Slots", key)))
                        mods.Add(Path.GetFileName(mod));
            foreach (var mod in mods)
            {
                var copy = Path.Combine(saved, mod);
                var live = Path.Combine(modsRoot, mod, "Slots", key);
                var wanted = Directory.Exists(copy) ? Directory.GetFiles(copy, "*.json").Select(Path.GetFileName).ToList() : new List<string>();
                if (wanted.Count > 0)
                    Directory.CreateDirectory(live);
                foreach (var name in wanted)
                {
                    var staged = Path.Combine(live, name + RestoreSuffix);
                    File.Copy(Path.Combine(copy, name), staged, true);
                    File.Move(staged, Path.Combine(live, name), true);
                }
                if (Directory.Exists(live))
                    foreach (var file in Directory.GetFiles(live, "*.json"))
                        if (!wanted.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
                            File.Delete(file);
            }
        }

        /// <summary>
        /// Files the game's own backups of the slot (id_backup_time.zip beside it, three at most) as history entries,
        /// once each, so a world played before Modstoke still has earlier saves. Returns how many were added.
        /// </summary>
        internal static int ImportGameBackups(string history, string slotDirectory, SaveHistoryLimits limits, DateTimeOffset now)
        {
            var slot = Normalize(slotDirectory);
            var id = Path.GetFileName(slot);
            var parent = Path.GetDirectoryName(slot);
            if (parent == null || !Directory.Exists(parent))
                return 0;
            var backups = Directory.GetFiles(parent, id + "_backup_*.zip");
            if (backups.Length == 0)
                return 0;
            Directory.CreateDirectory(history);
            var listed = Path.Combine(history, ImportedFile);
            var seen = File.Exists(listed)
                ? new HashSet<string>(File.ReadAllLines(listed), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var added = 0;
            Array.Sort(backups, StringComparer.OrdinalIgnoreCase);
            foreach (var backup in backups)
            {
                if (!seen.Add(Path.GetFileName(backup)))
                    continue;
                var copy = Extract(history, backup, id);
                if (copy == null)
                    continue;
                Commit(history, copy, "game-backup", false, limits, now, out var duplicate);
                if (!duplicate)
                    added++;
            }
            File.WriteAllLines(listed, seen);
            return added;
        }

        // The game zips the slot folder with its name as the base: id/SaveData.zip and so on.
        private static SaveCopy Extract(string history, string backup, string id)
        {
            var incoming = NewIncoming(history);
            var slot = Directory.CreateDirectory(Path.Combine(incoming, SlotFolder)).FullName;
            try
            {
                string hash = null;
                using (var file = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Read))
                {
                    foreach (var item in zip.Entries)
                    {
                        var parts = item.FullName.Replace('\\', '/').Split('/');
                        if (parts.Length != 2 || parts[0] != id || parts[1].Length == 0 || item.Length > MaxEntryBytes
                            || parts[1].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                            continue;
                        var archive = IsArchive(parts[1]);
                        using var sha = archive ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
                        using (var from = item.Open())
                        using (var to = new FileStream(Path.Combine(slot, parts[1]), FileMode.CreateNew, FileAccess.Write))
                            Pump(from, to, sha);
                        if (archive)
                            hash = Convert.ToHexStringLower(sha.GetHashAndReset());
                    }
                }
                if (hash == null)
                {
                    DeleteQuietly(incoming);
                    return null;
                }
                return new SaveCopy(incoming, Normalize(Path.Combine(Path.GetDirectoryName(backup), id)), hash, true);
            }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                DeleteQuietly(incoming);
                return null;
            }
        }

        /// <summary>
        /// Whether the save in <paramref name="directory"/> is whole: SaveData.zip opens, it has the main and game
        /// state files, and every entry has content, reads to its full length with a matching CRC and, for JSON, parses.
        /// Saves from before the archive, as loose JSON files, get the same check per file.
        /// </summary>
        internal static SaveCheck Check(string directory)
        {
            var archive = Path.Combine(directory, ArchiveName);
            try
            {
                if (File.Exists(archive))
                    return CheckArchive(archive);
                if (File.Exists(Path.Combine(directory, GameStateName)))
                    return CheckLoose(directory);
                return new SaveCheck("There is no " + ArchiveName + ".", null, null);
            }
            catch (Exception error) when (IsReadFailure(error))
            {
                return new SaveCheck("The save could not be read: " + error.Message, null, null, true);
            }
            catch (Exception error) when (error is InvalidDataException or IOException or JsonException
                or InvalidOperationException or FormatException)
            {
                return new SaveCheck(error.Message, null, null);
            }
        }

        // Another program holding a file, or a file going away mid-read, is a failed read rather than damage. A cut-off
        // archive is an IOException too (end of stream), so only these are told apart: sharing and lock violations.
        private static bool IsReadFailure(Exception error)
            => error is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException
                || error is IOException && (error.HResult & 0xFFFF) is 32 or 33;

        private static SaveCheck CheckArchive(string path)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length == 0)
                return new SaveCheck(ArchiveName + " is empty.", null, null);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            var main = false;
            SaveCheck state = null;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                    continue;
                if (entry.Length == 0)
                    return new SaveCheck(entry.FullName + " is empty.", null, null);
                if (entry.Length > MaxEntryBytes)
                    return new SaveCheck(entry.FullName + " is larger than a save file can be.", null, null);
                var buffer = ArrayPool<byte>.Shared.Rent((int)entry.Length + 1);
                try
                {
                    int length;
                    // .NET checks each entry's CRC32 as it is read to the end.
                    using (var stream = entry.Open())
                        length = stream.ReadAtLeast(buffer.AsSpan(0, (int)entry.Length + 1), (int)entry.Length + 1, false);
                    if (length != entry.Length)
                        return new SaveCheck(entry.FullName + " does not have the length the archive records.", null, null);
                    var problem = CheckContent(entry.FullName, buffer.AsSpan(0, length), ref main, ref state);
                    if (problem != null)
                        return new SaveCheck(problem, null, null);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            if (!main || state == null)
                return new SaveCheck(ArchiveName + " is missing " + (main ? GameStateName : MainName) + ".", null, null);
            return state;
        }

        private static SaveCheck CheckLoose(string directory)
        {
            var main = false;
            SaveCheck state = null;
            foreach (var path in Directory.GetFiles(directory, "*.json"))
            {
                var bytes = File.ReadAllBytes(path);
                var name = Path.GetFileName(path);
                if (bytes.Length == 0)
                    return new SaveCheck(name + " is empty.", null, null);
                var problem = CheckContent(name, bytes, ref main, ref state);
                if (problem != null)
                    return new SaveCheck(problem, null, null);
            }
            return state ?? new SaveCheck(GameStateName + " is missing.", null, null);
        }

        private static string CheckContent(string name, ReadOnlySpan<byte> bytes, ref bool main, ref SaveCheck state)
        {
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                return null;
            if (bytes.StartsWith(Utf8Bom))
                bytes = bytes[Utf8Bom.Length..];
            try
            {
                var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 1024 });
                while (reader.Read())
                {
                }
                if (reader.TokenType == JsonTokenType.None)
                    return name + " is empty.";
            }
            catch (JsonException error)
            {
                return name + " is not whole JSON: " + error.Message;
            }
            if (string.Equals(name, MainName, StringComparison.OrdinalIgnoreCase))
                main = true;
            else if (string.Equals(name, GameStateName, StringComparison.OrdinalIgnoreCase))
            {
                state = ReadGameState(bytes);
                return state.Problem;
            }
            return null;
        }

        // The game state is a JSON string inside the file's Data. GameDays is left out on day 0.
        private static SaveCheck ReadGameState(ReadOnlySpan<byte> bytes)
        {
            var text = JsonNode.Parse(bytes)?["Data"]?["GameState"]?.GetValue<string>();
            if (text == null || JsonNode.Parse(text) is not JsonObject game)
                return new SaveCheck(GameStateName + " has no game state.", null, null);
            var day = game["GameDays"] is JsonValue days ? days.GetValue<int>() : 0;
            DateTimeOffset? saved = game["SaveTime"] is JsonValue time
                && DateTimeOffset.TryParse(time.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed
                    : null;
            return new SaveCheck(null, day, saved);
        }

        internal static SaveHistoryEntry Read(string directory)
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(Path.Combine(directory, EntryFile))) is not JsonObject json)
                    return null;
                return new SaveHistoryEntry(
                    directory,
                    (string)json["slot"],
                    DateTimeOffset.Parse((string)json["saved"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    (int?)json["day"],
                    (long?)json["sizeBytes"] ?? 0,
                    (string)json["reason"] ?? "save",
                    (string)json["hash"] ?? "",
                    (string)json["problem"],
                    (bool?)json["undo"] ?? false,
                    (bool?)json["pinned"] ?? false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                or FormatException or InvalidOperationException or ArgumentException)
            {
                return null;
            }
        }

        private static SaveHistoryEntry Rewrite(SaveHistoryEntry entry, bool? undo = null, bool? pinned = null)
        {
            var path = Path.Combine(entry.Directory, EntryFile);
            var json = (JsonObject)JsonNode.Parse(File.ReadAllText(path));
            if (undo != null)
                json["undo"] = undo.Value;
            if (pinned != null)
                json["pinned"] = pinned.Value;
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, json.ToJsonString());
            File.Move(temporary, path, true);
            return Read(entry.Directory);
        }

        private static string NewIncoming(string history)
            => Directory.CreateDirectory(Path.Combine(history, IncomingPrefix + Guid.NewGuid().ToString("N"))).FullName;

        // A file that vanished between listing and opening is left out; the stamp check catches the change.
        private static bool TryCopy(string from, string to, IncrementalHash hash)
        {
            try
            {
                using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    81920, FileOptions.SequentialScan);
                using var target = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920);
                Pump(source, target, hash);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
        }

        private static void Pump(Stream from, Stream to, IncrementalHash hash)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                int read;
                while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
                {
                    to.Write(buffer, 0, read);
                    hash?.AppendData(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static string Stamp(string[] files)
        {
            var parts = new List<string>(files.Length);
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                parts.Add(info.Exists ? info.Name + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks : info.Name + "|gone");
            }
            parts.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join("\n", parts);
        }

        private static DateTimeOffset? ArchiveTime(string slot)
        {
            var archive = new FileInfo(Path.Combine(slot, ArchiveName));
            return archive.Exists ? new DateTimeOffset(archive.LastWriteTimeUtc, TimeSpan.Zero) : null;
        }

        private static long FolderSize(string directory)
            => new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);

        private static bool IsArchive(string name) => string.Equals(name, ArchiveName, StringComparison.OrdinalIgnoreCase);

        private static void DeleteQuietly(string directory)
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Left for the next prune.
            }
        }
    }

    /// <summary>A slot copied into history but not yet kept.</summary>
    internal sealed record SaveCopy(string Directory, string Slot, string Hash, bool Stable);
}
