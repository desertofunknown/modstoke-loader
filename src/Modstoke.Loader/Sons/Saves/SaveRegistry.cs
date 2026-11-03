// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    public static class SaveRegistry
    {
        public const string ServiceKey = "openacai.sons.saveRegistry";

        internal const string StorageModel = "openacai-userdata-sidecar-json";
        internal const string EmbeddedStorageStatus = "not-installed";
        internal const string EmbeddedStorageDetail = "No verified Sons save-file embed hook is installed yet; embedded-preferred saveables fall back to sidecar JSON and embedded-required saveables are skipped.";
        private const string SlotDirectoryName = "Slots";
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, RegisteredSaveable> Saveables = new Dictionary<string, RegisteredSaveable>(StringComparer.OrdinalIgnoreCase);
        private static SaveRegistrySnapshot _state = SaveRegistrySnapshot.CreateUnknown();
        private static bool _initialized;
        private static int _saveOperationCount;
        private static int _loadOperationCount;
        private static int _writeCount;
        private static int _readCount;
        private static int _errorCount;
        private static int _sidecarFallbackCount;
        private static int _embeddedRequiredSkipCount;

        public static SaveRegistrySnapshot State
        {
            get
            {
                lock (SyncRoot)
                {
                    return _state;
                }
            }
        }

        public static IDisposable Register(ISaveable saveable)
        {
            if (saveable == null)
            {
                throw new ArgumentNullException(nameof(saveable));
            }

            var modId = saveable.ModId;
            var name = saveable.Name;
            if (string.IsNullOrWhiteSpace(modId))
            {
                throw new ArgumentException("A saveable ModId is required.", nameof(saveable));
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A saveable Name is required.", nameof(saveable));
            }

            var key = CreateKey(modId, name);
            // Read mod-provided metadata before publication, never while enumerating the registry.
            var registered = new RegisteredSaveable(key, saveable, modId, name);
            lock (SyncRoot)
            {
                if (Saveables.TryGetValue(key, out var existing))
                {
                    if (!MatchesIdentity(existing, modId, name))
                        throw new ArgumentException("The saveable identity collides with another registered save path.", nameof(saveable));
                    registered.FailedLoadSlots.UnionWith(existing.FailedLoadSlots);
                }
                Saveables[key] = registered;
                RefreshStateLocked("register", null, null, null);
            }

            return new Registration(registered);
        }

        public static IDisposable RegisterJson<T>(
            string modId,
            string name,
            Func<T> save,
            Action<T> load,
            bool includeInPlayerSave = true,
            JsonSerializerOptions jsonOptions = null,
            SaveStorageMode storageMode = SaveStorageMode.SidecarJson)
        {
            return Register(new JsonSaveable<T>(modId, name, save, load, includeInPlayerSave, jsonOptions, storageMode));
        }

        public static IDisposable RegisterEmbeddedJson<T>(
            string modId,
            string name,
            Func<T> save,
            Action<T> load,
            bool includeInPlayerSave = true,
            JsonSerializerOptions jsonOptions = null,
            bool requireEmbeddedStorage = false)
        {
            return RegisterJson(
                modId,
                name,
                save,
                load,
                includeInPlayerSave,
                jsonOptions,
                requireEmbeddedStorage
                    ? SaveStorageMode.EmbeddedRequired
                    : SaveStorageMode.EmbeddedPreferred);
        }

        public static bool Unregister(string modId, string name)
        {
            var key = CreateKey(modId, name);
            lock (SyncRoot)
            {
                if (!Saveables.TryGetValue(key, out var registered) || !MatchesIdentity(registered, modId, name))
                    return false;
                var removed = Saveables.Remove(key);
                if (removed)
                {
                    RefreshStateLocked("unregister", null, null, null);
                }

                return removed;
            }
        }

        internal static void Initialize()
        {
            lock (SyncRoot)
            {
                if (_initialized)
                {
                    return;
                }

                _initialized = true;
                RefreshStateLocked("initialize", null, null, null);
            }

            SaveLifecycle.Lifecycle += OnSaveLifecycle;
            if (!MenuRegistry.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Modstoke SDK save registry provider could not be registered.");
            }
        }

        private static void OnSaveLifecycle(SaveLifecycleEvent evt)
        {
            if (evt == null || !string.Equals(evt.Phase, "After", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(evt.Operation, "Save", StringComparison.OrdinalIgnoreCase))
            {
                SaveRegistered(evt);
                return;
            }

            if (string.Equals(evt.Operation, "Load", StringComparison.OrdinalIgnoreCase) && evt.Success == true)
            {
                LoadRegistered(evt);
            }
        }

        private static void SaveRegistered(SaveLifecycleEvent evt)
        {
            RegisteredSaveable[] saveables;
            lock (SyncRoot)
            {
                saveables = CopySaveablesLocked();
                _saveOperationCount++;
            }

            var slotKey = CreateSlotKey(evt);
            var saved = 0;
            foreach (var registered in saveables)
            {
                if (!IsCurrent(registered) || (evt.SavePlayerOnly.GetValueOrDefault() && !registered.IncludeInPlayerSave))
                {
                    continue;
                }

                try
                {
                    if (!ShouldUseSidecarStorage("save", registered))
                    {
                        continue;
                    }

                    var path = GetSaveablePath(registered, slotKey);
                    EnsureLoadSucceeded(registered, slotKey);
                    var contents = registered.Saveable.Save();
                    // A save callback may dispose or replace its own registration, or return null to skip this save.
                    if (contents == null || !IsCurrent(registered))
                        continue;
                    EnsureLoadSucceeded(registered, slotKey);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    WriteSidecar(path, contents);
                    saved++;
                }
                catch (Exception ex)
                {
                    RecordError("save", registered, ex);
                }
            }

            lock (SyncRoot)
            {
                _writeCount += saved;
                RefreshStateLocked("save", slotKey, saved, evt.Directory);
            }
        }

        private static void WriteSidecar(string path, string contents)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var created = false;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true))
                    {
                        writer.Write(contents);
                        writer.Flush();
                    }
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(path))
                    File.Replace(temporary, path, null);
                else
                    File.Move(temporary, path);
                created = false;
            }
            finally
            {
                if (created)
                {
                    try { File.Delete(temporary); }
                    catch (Exception) { /* Retain the temporary file if cleanup is unavailable. */ }
                }
            }
        }

        private static void LoadRegistered(SaveLifecycleEvent evt)
        {
            RegisteredSaveable[] saveables;
            lock (SyncRoot)
            {
                saveables = CopySaveablesLocked();
                _loadOperationCount++;
            }

            var slotKey = CreateSlotKey(evt);
            var legacySlotKey = CreateLegacySlotKey(evt, slotKey);
            var loaded = 0;
            foreach (var registered in saveables)
            {
                if (!IsCurrent(registered))
                    continue;

                try
                {
                    if (!ShouldUseSidecarStorage("load", registered))
                    {
                        continue;
                    }

                    // The next save writes the slot key, which migrates a legacy sidecar forward.
                    if (!TryReadSidecar(GetSaveablePath(registered, slotKey), out var contents)
                        && (legacySlotKey == null || !TryReadSidecar(GetSaveablePath(registered, legacySlotKey), out contents)))
                    {
                        SetLoadFailed(registered, slotKey, false);
                        continue;
                    }

                    if (!IsCurrent(registered))
                        continue;
                    // A reentrant save must not capture partially restored owner state.
                    SetLoadFailed(registered, slotKey, true);
                    registered.Saveable.Load(contents);
                    SetLoadFailed(registered, slotKey, false);
                    loaded++;
                }
                catch (Exception ex)
                {
                    SetLoadFailed(registered, slotKey, true);
                    RecordError("load", registered, ex);
                }
            }

            lock (SyncRoot)
            {
                _readCount += loaded;
                RefreshStateLocked("load", slotKey, loaded, evt.Directory);
            }
        }

        private static bool ShouldUseSidecarStorage(string operation, RegisteredSaveable registered)
        {
            var storageMode = registered.StorageMode;
            if (storageMode == SaveStorageMode.SidecarJson)
            {
                return true;
            }

            if (storageMode == SaveStorageMode.EmbeddedPreferred)
            {
                lock (SyncRoot)
                {
                    _sidecarFallbackCount++;
                }

                return true;
            }

            bool firstSkip;
            lock (SyncRoot)
            {
                _embeddedRequiredSkipCount++;
                firstSkip = !registered.EmbeddedSkipLogged;
                registered.EmbeddedSkipLogged = true;
            }

            if (firstSkip)
            {
                RuntimeEntryPoint.LogSource?.LogWarning(
                    "Modstoke save registry skipped " + operation + " for " + registered.Key + ": embedded save storage is not installed. Further skips for this saveable are counted but not logged.");
            }

            return false;
        }

        private static string GetSaveablePath(RegisteredSaveable saveable, string slotKey)
        {
            var relativePath = Path.Combine(
                SlotDirectoryName,
                SaveData.NormalizeModId(slotKey),
                SaveData.NormalizeModId(saveable.Name) + ".json");
            return SaveData.GetModFilePath(saveable.ModId, relativePath);
        }

        private static void EnsureLoadSucceeded(RegisteredSaveable registered, string slotKey)
        {
            lock (SyncRoot)
                if (registered.FailedLoadSlots.Contains(SaveData.NormalizeModId(slotKey)))
                    throw new InvalidOperationException("The sidecar load has not succeeded; its saved data will not be overwritten.");
        }

        private static void SetLoadFailed(RegisteredSaveable registered, string slotKey, bool failed)
        {
            var normalizedSlot = SaveData.NormalizeModId(slotKey);
            lock (SyncRoot)
            {
                if (failed)
                    registered.FailedLoadSlots.Add(normalizedSlot);
                else
                    registered.FailedLoadSlots.Remove(normalizedSlot);
            }
        }

        private static bool TryReadSidecar(string path, out string contents)
        {
            try
            {
                contents = File.ReadAllText(path);
                return true;
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }

            contents = null;
            return false;
        }

        // Save events carry no selected save id, so the save directory name keys both operations.
        private static string CreateSlotKey(SaveLifecycleEvent evt)
        {
            if (!string.IsNullOrWhiteSpace(evt.Directory))
            {
                var trimmed = evt.Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var directoryName = Path.GetFileName(trimmed);
                if (!string.IsNullOrWhiteSpace(directoryName))
                {
                    return directoryName;
                }
            }

            if (evt.SelectedSaveId.HasValue && evt.SelectedSaveId.Value > 0)
            {
                return "save-" + evt.SelectedSaveId.Value;
            }

            if (!string.IsNullOrWhiteSpace(evt.GameName))
            {
                return evt.GameName;
            }

            return "unknown-save";
        }

        private static string CreateLegacySlotKey(SaveLifecycleEvent evt, string slotKey)
        {
            if (!evt.SelectedSaveId.HasValue || evt.SelectedSaveId.Value == 0)
            {
                return null;
            }

            var legacy = "save-" + evt.SelectedSaveId.Value;
            return string.Equals(SaveData.NormalizeModId(legacy), SaveData.NormalizeModId(slotKey), StringComparison.OrdinalIgnoreCase)
                ? null
                : legacy;
        }

        private static void RecordError(string operation, RegisteredSaveable registered, Exception ex)
        {
            lock (SyncRoot)
            {
                _errorCount++;
                RefreshStateLocked(operation + "-error", null, null, registered.Key);
            }

            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning(
                    "Modstoke save registry " + operation + " failed for " + registered.Key + ": " + ex.Message);
            }
            catch
            {
                // A diagnostic listener must not prevent other owners from saving or loading.
            }
        }

        private static bool IsCurrent(RegisteredSaveable registered)
        {
            lock (SyncRoot)
                return Saveables.TryGetValue(registered.Key, out var current) && ReferenceEquals(current, registered);
        }

        private static bool MatchesIdentity(RegisteredSaveable registered, string modId, string name) =>
            string.Equals(registered.ModId, modId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(registered.Name, name, StringComparison.OrdinalIgnoreCase);

        private static RegisteredSaveable[] CopySaveablesLocked()
        {
            var copy = new RegisteredSaveable[Saveables.Count];
            Saveables.Values.CopyTo(copy, 0);
            return copy;
        }

        private static void RefreshStateLocked(string lastOperation, string lastSlotKey, int? lastAffectedCount, string lastDetail)
        {
            var entries = new List<SaveRegistryEntrySnapshot>(Saveables.Count);
            var sidecarOnlyCount = 0;
            var embeddedPreferredCount = 0;
            var embeddedRequiredCount = 0;
            foreach (var saveable in Saveables.Values)
            {
                entries.Add(saveable.CreateState());
                switch (saveable.StorageMode)
                {
                    case SaveStorageMode.EmbeddedPreferred:
                        embeddedPreferredCount++;
                        break;
                    case SaveStorageMode.EmbeddedRequired:
                        embeddedRequiredCount++;
                        break;
                    default:
                        sidecarOnlyCount++;
                        break;
                }
            }

            _state = new SaveRegistrySnapshot(
                DateTimeOffset.UtcNow.ToString("O"),
                ServiceKey,
                StorageModel,
                false,
                SaveRegistry.EmbeddedStorageStatus,
                SaveRegistry.EmbeddedStorageDetail,
                sidecarOnlyCount,
                embeddedPreferredCount,
                embeddedRequiredCount,
                Saveables.Count,
                _saveOperationCount,
                _loadOperationCount,
                _writeCount,
                _readCount,
                _errorCount,
                _sidecarFallbackCount,
                _embeddedRequiredSkipCount,
                lastOperation,
                lastSlotKey,
                lastAffectedCount,
                lastDetail,
                entries);
            Modding.SetService(ServiceKey, _state);
        }

        private static MenuContentSnapshot CreateContent()
        {
            var snapshot = State;
            return new MenuContentSnapshot(
                "Sons Save Registry",
                "Per-mod sidecar JSON save registry for Sons Of The Forest.",
                new[]
                {
                    CreateRegistrySection(snapshot),
                    CreateActivitySection(snapshot),
                    CreateEntriesSection(snapshot)
                });
        }

        private static MenuSectionSnapshot CreateRegistrySection(SaveRegistrySnapshot snapshot)
        {
            return new MenuSectionSnapshot(
                "Registry",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, RegistryStatus(snapshot), "Public Modstoke custom-save registry service."),
                    Row("Storage", snapshot.StorageModel, "READY", "Per-mod sidecar JSON under Modstoke UserData; this is intentionally not a RedLoader plugin path."),
                    Row("Embedded Store", snapshot.EmbeddedStorageSupported ? "Available" : "Deferred", EmbeddedStatus(snapshot), snapshot.EmbeddedStorageDetail),
                    Row("Storage Intents", FormatStorageIntents(snapshot), StorageIntentStatus(snapshot), "Registered sidecar, embedded-preferred, and embedded-required saveables."),
                    Row("Registered", snapshot.RegisteredCount.ToString(), snapshot.RegisteredCount > 0 ? "READY" : "INFO", "Registered mod saveable objects."),
                    Row("Errors", snapshot.ErrorCount.ToString(), snapshot.ErrorCount == 0 ? "READY" : "CHECK", "Save/load errors recorded by the registry."),
                    Row("Last Operation", string.IsNullOrWhiteSpace(snapshot.LastOperation) ? "None" : snapshot.LastOperation, "INFO", string.IsNullOrWhiteSpace(snapshot.LastDetail) ? "Most recent registry operation." : snapshot.LastDetail)
                });
        }

        private static MenuSectionSnapshot CreateActivitySection(SaveRegistrySnapshot snapshot)
        {
            return new MenuSectionSnapshot(
                "Activity",
                new[]
                {
                    Row("Save Ops", snapshot.SaveOperationCount.ToString(), "INFO", "Sons save events handled by the registry."),
                    Row("Load Ops", snapshot.LoadOperationCount.ToString(), "INFO", "Sons load events handled by the registry."),
                    Row("Writes", snapshot.WriteCount.ToString(), "INFO", "Sidecar JSON files written."),
                    Row("Reads", snapshot.ReadCount.ToString(), "INFO", "Sidecar JSON files read."),
                    Row("Sidecar Fallbacks", snapshot.SidecarFallbackCount.ToString(), snapshot.SidecarFallbackCount > 0 ? "INFO" : "READY", "Embedded-preferred saveables routed to sidecar JSON because embedded storage is not installed."),
                    Row("Embedded Skips", snapshot.EmbeddedRequiredSkippedCount.ToString(), snapshot.EmbeddedRequiredSkippedCount == 0 ? "READY" : "CHECK", "Embedded-required saveables skipped because embedded storage is not installed."),
                    Row("Last Slot", string.IsNullOrWhiteSpace(snapshot.LastSlotKey) ? "None" : snapshot.LastSlotKey, string.IsNullOrWhiteSpace(snapshot.LastSlotKey) ? "PENDING" : "INFO", "Resolved save slot key for the most recent save/load registry event.")
                });
        }

        private static MenuSectionSnapshot CreateEntriesSection(SaveRegistrySnapshot snapshot)
        {
            var entries = snapshot.Entries ?? Array.Empty<SaveRegistryEntrySnapshot>();
            var rows = entries.Count == 0
                ? new[] { Row("Entries", "None", "INFO", "No mod saveables have registered yet.") }
                : entries
                    .OrderBy(entry => entry.ModId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(entry => Row(entry.ModId + "/" + entry.Name, entry.StorageMode, EntryStatus(entry), entry.Key + " registered " + entry.RegisteredUtc + "; " + (entry.IncludeInPlayerSave ? "included in player-save events" : "world-save events only")))
                    .ToArray();

            return new MenuSectionSnapshot("Registered Saveables", rows);
        }

        private static string RegistryStatus(SaveRegistrySnapshot snapshot)
        {
            if (snapshot == null)
            {
                return "MISSING";
            }

            return snapshot.ErrorCount == 0 ? "READY" : "CHECK";
        }

        private static string EmbeddedStatus(SaveRegistrySnapshot snapshot)
        {
            if (snapshot == null)
            {
                return "MISSING";
            }

            if (snapshot.EmbeddedStorageSupported)
            {
                return "READY";
            }

            return snapshot.EmbeddedRequiredCount > 0 ? "CHECK" : "PENDING";
        }

        private static string StorageIntentStatus(SaveRegistrySnapshot snapshot)
        {
            if (snapshot == null)
            {
                return "MISSING";
            }

            return snapshot.EmbeddedRequiredCount > 0 && !snapshot.EmbeddedStorageSupported ? "CHECK" : "READY";
        }

        private static string EntryStatus(SaveRegistryEntrySnapshot entry)
        {
            return string.Equals(entry.StorageMode, FormatStorageMode(SaveStorageMode.EmbeddedRequired), StringComparison.OrdinalIgnoreCase)
                ? "CHECK"
                : "READY";
        }

        private static string FormatStorageIntents(SaveRegistrySnapshot snapshot)
        {
            return snapshot.SidecarOnlyCount
                + " sidecar | "
                + snapshot.EmbeddedPreferredCount
                + " preferred | "
                + snapshot.EmbeddedRequiredCount
                + " required";
        }

        private static MenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new MenuRowSnapshot(label, value, status, detail);
        }

        private static string CreateKey(string modId, string name)
        {
            return SaveData.NormalizeModId(modId) + ":" + SaveData.NormalizeModId(name);
        }

        private static SaveStorageMode GetStorageMode(ISaveable saveable)
        {
            if (saveable is ISaveStoragePreference preference)
            {
                return preference.StorageMode;
            }

            return SaveStorageMode.SidecarJson;
        }

        private static string FormatStorageMode(SaveStorageMode mode)
        {
            switch (mode)
            {
                case SaveStorageMode.EmbeddedPreferred:
                    return "embedded-preferred";
                case SaveStorageMode.EmbeddedRequired:
                    return "embedded-required";
                default:
                    return "sidecar-json";
            }
        }

        private sealed class RegisteredSaveable
        {
            internal RegisteredSaveable(string key, ISaveable saveable, string modId, string name)
            {
                Key = key;
                Saveable = saveable;
                ModId = modId;
                Name = name;
                IncludeInPlayerSave = saveable.IncludeInPlayerSave;
                StorageMode = GetStorageMode(saveable);
                if (!Enum.IsDefined(StorageMode))
                    throw new ArgumentOutOfRangeException(nameof(saveable), "The save storage mode is not supported.");
                RegisteredUtc = DateTimeOffset.UtcNow;
            }

            internal string Key { get; }
            internal object Identity { get; } = new object();
            internal ISaveable Saveable { get; }
            internal string ModId { get; }
            internal string Name { get; }
            internal bool IncludeInPlayerSave { get; }
            internal SaveStorageMode StorageMode { get; }
            internal HashSet<string> FailedLoadSlots { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal bool EmbeddedSkipLogged { get; set; }
            private DateTimeOffset RegisteredUtc { get; }

            internal SaveRegistryEntrySnapshot CreateState()
            {
                return new SaveRegistryEntrySnapshot(
                    Key,
                    ModId,
                    Name,
                    IncludeInPlayerSave,
                    FormatStorageMode(StorageMode),
                    RegisteredUtc.ToString("O"));
            }
        }

        private sealed class Registration : IDisposable
        {
            private readonly string _key;
            private object _identity;

            internal Registration(RegisteredSaveable registered)
            {
                _key = registered.Key;
                _identity = registered.Identity;
            }

            public void Dispose()
            {
                var identity = Interlocked.Exchange(ref _identity, null);
                if (identity == null)
                {
                    return;
                }

                lock (SyncRoot)
                {
                    if (Saveables.TryGetValue(_key, out var current) && ReferenceEquals(current.Identity, identity))
                    {
                        Saveables.Remove(_key);
                        RefreshStateLocked("dispose", null, null, _key);
                    }
                }
            }
        }

        private sealed class JsonSaveable<T> : ISaveable, ISaveStoragePreference
        {
            private readonly Func<T> _save;
            private readonly Action<T> _load;
            private readonly JsonSerializerOptions _jsonOptions;

            internal JsonSaveable(
                string modId,
                string name,
                Func<T> save,
                Action<T> load,
                bool includeInPlayerSave,
                JsonSerializerOptions jsonOptions,
                SaveStorageMode storageMode)
            {
                ModId = modId;
                Name = name;
                _save = save ?? throw new ArgumentNullException(nameof(save));
                _load = load ?? throw new ArgumentNullException(nameof(load));
                IncludeInPlayerSave = includeInPlayerSave;
                _jsonOptions = jsonOptions;
                StorageMode = storageMode;
            }

            public string ModId { get; }
            public string Name { get; }
            public bool IncludeInPlayerSave { get; }
            public SaveStorageMode StorageMode { get; }

            public string Save()
            {
                return JsonSerializer.Serialize(_save(), _jsonOptions);
            }

            public void Load(string data)
            {
                _load(JsonSerializer.Deserialize<T>(data, _jsonOptions));
            }
        }

        private sealed class Provider : IMenuContentProvider
        {
            public string Id => MenuProviderIds.SonsSaveRegistry;
            public string DisplayName => "Sons Save Registry";
            public string Kind => MenuProviderKinds.Status;
            public int Priority => 724;
            public string Source => "Modstoke Loader";
            public string Description => "Show per-mod save registry state.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public MenuContentSnapshot CreateContent()
            {
                return SaveRegistry.CreateContent();
            }
        }
    }

    public interface ISaveable
    {
        string ModId { get; }
        string Name { get; }
        bool IncludeInPlayerSave { get; }
        /// <summary>Returns the data to write, or null to skip this save and keep the existing sidecar.</summary>
        string Save();
        void Load(string data);
    }

    public interface ISaveStoragePreference
    {
        SaveStorageMode StorageMode { get; }
    }

    public enum SaveStorageMode
    {
        SidecarJson = 0,
        EmbeddedPreferred = 1,
        EmbeddedRequired = 2
    }

    public sealed class SaveRegistrySnapshot
    {
        internal SaveRegistrySnapshot(
            string generatedUtc,
            string serviceKey,
            string storageModel,
            bool embeddedStorageSupported,
            string embeddedStorageStatus,
            string embeddedStorageDetail,
            int sidecarOnlyCount,
            int embeddedPreferredCount,
            int embeddedRequiredCount,
            int registeredCount,
            int saveOperationCount,
            int loadOperationCount,
            int writeCount,
            int readCount,
            int errorCount,
            int sidecarFallbackCount,
            int embeddedRequiredSkippedCount,
            string lastOperation,
            string lastSlotKey,
            int? lastAffectedCount,
            string lastDetail,
            IReadOnlyCollection<SaveRegistryEntrySnapshot> entries)
        {
            GeneratedUtc = generatedUtc;
            ServiceKey = serviceKey;
            StorageModel = storageModel;
            EmbeddedStorageSupported = embeddedStorageSupported;
            EmbeddedStorageStatus = embeddedStorageStatus;
            EmbeddedStorageDetail = embeddedStorageDetail;
            SidecarOnlyCount = sidecarOnlyCount;
            EmbeddedPreferredCount = embeddedPreferredCount;
            EmbeddedRequiredCount = embeddedRequiredCount;
            RegisteredCount = registeredCount;
            SaveOperationCount = saveOperationCount;
            LoadOperationCount = loadOperationCount;
            WriteCount = writeCount;
            ReadCount = readCount;
            ErrorCount = errorCount;
            SidecarFallbackCount = sidecarFallbackCount;
            EmbeddedRequiredSkippedCount = embeddedRequiredSkippedCount;
            LastOperation = lastOperation;
            LastSlotKey = lastSlotKey;
            LastAffectedCount = lastAffectedCount;
            LastDetail = lastDetail;
            Entries = Array.AsReadOnly(entries?.ToArray() ?? Array.Empty<SaveRegistryEntrySnapshot>());
        }

        public string GeneratedUtc { get; }
        public string ServiceKey { get; }
        public string StorageModel { get; }
        public bool EmbeddedStorageSupported { get; }
        public string EmbeddedStorageStatus { get; }
        public string EmbeddedStorageDetail { get; }
        public int SidecarOnlyCount { get; }
        public int EmbeddedPreferredCount { get; }
        public int EmbeddedRequiredCount { get; }
        public int RegisteredCount { get; }
        public int SaveOperationCount { get; }
        public int LoadOperationCount { get; }
        public int WriteCount { get; }
        public int ReadCount { get; }
        public int ErrorCount { get; }
        public int SidecarFallbackCount { get; }
        public int EmbeddedRequiredSkippedCount { get; }
        public string LastOperation { get; }
        public string LastSlotKey { get; }
        public int? LastAffectedCount { get; }
        public string LastDetail { get; }
        public IReadOnlyCollection<SaveRegistryEntrySnapshot> Entries { get; }

        internal static SaveRegistrySnapshot CreateUnknown()
        {
            return new SaveRegistrySnapshot(
                DateTimeOffset.UtcNow.ToString("O"),
                SaveRegistry.ServiceKey,
                SaveRegistry.StorageModel,
                false,
                SaveRegistry.EmbeddedStorageStatus,
                SaveRegistry.EmbeddedStorageDetail,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                "unknown",
                null,
                null,
                null,
                Array.Empty<SaveRegistryEntrySnapshot>());
        }
    }

    public sealed class SaveRegistryEntrySnapshot
    {
        internal SaveRegistryEntrySnapshot(
            string key,
            string modId,
            string name,
            bool includeInPlayerSave,
            string storageMode,
            string registeredUtc)
        {
            Key = key;
            ModId = modId;
            Name = name;
            IncludeInPlayerSave = includeInPlayerSave;
            StorageMode = storageMode;
            RegisteredUtc = registeredUtc;
        }

        public string Key { get; }
        public string ModId { get; }
        public string Name { get; }
        public bool IncludeInPlayerSave { get; }
        public string StorageMode { get; }
        public string RegisteredUtc { get; }
    }
}
