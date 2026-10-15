// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned per-mod custom save registry backed by sidecar JSON files.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;

namespace OpenACAI.Sons
{
    public static class OpenACAISonsSaveRegistry
    {
        public const string ServiceKey = "openacai.sons.saveRegistry";

        internal const string StorageModel = "openacai-userdata-sidecar-json";
        internal const string EmbeddedStorageStatus = "not-installed";
        internal const string EmbeddedStorageDetail = "No verified Sons save-file embed hook is installed yet; embedded-preferred saveables fall back to sidecar JSON and embedded-required saveables are skipped.";
        private const string SlotDirectoryName = "Slots";
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, RegisteredSaveable> Saveables = new Dictionary<string, RegisteredSaveable>(StringComparer.OrdinalIgnoreCase);
        private static OpenACAISonsSaveRegistrySnapshot _state = OpenACAISonsSaveRegistrySnapshot.CreateUnknown();
        private static bool _initialized;
        private static int _saveOperationCount;
        private static int _loadOperationCount;
        private static int _writeCount;
        private static int _readCount;
        private static int _errorCount;
        private static int _sidecarFallbackCount;
        private static int _embeddedRequiredSkipCount;

        public static OpenACAISonsSaveRegistrySnapshot State
        {
            get
            {
                lock (SyncRoot)
                {
                    return _state;
                }
            }
        }

        public static IDisposable Register(IOpenACAISonsSaveable saveable)
        {
            if (saveable == null)
            {
                throw new ArgumentNullException(nameof(saveable));
            }

            var key = CreateKey(saveable.ModId, saveable.Name);
            if (string.IsNullOrWhiteSpace(saveable.ModId))
            {
                throw new ArgumentException("A saveable ModId is required.", nameof(saveable));
            }

            if (string.IsNullOrWhiteSpace(saveable.Name))
            {
                throw new ArgumentException("A saveable Name is required.", nameof(saveable));
            }

            lock (SyncRoot)
            {
                Saveables[key] = new RegisteredSaveable(key, saveable);
                RefreshStateLocked("register", null, null, null);
            }

            return new Registration(key);
        }

        public static IDisposable RegisterJson<T>(
            string modId,
            string name,
            Func<T> save,
            Action<T> load,
            bool includeInPlayerSave = true,
            JsonSerializerOptions jsonOptions = null,
            OpenACAISonsSaveStorageMode storageMode = OpenACAISonsSaveStorageMode.SidecarJson)
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
                    ? OpenACAISonsSaveStorageMode.EmbeddedRequired
                    : OpenACAISonsSaveStorageMode.EmbeddedPreferred);
        }

        public static bool Unregister(string modId, string name)
        {
            var key = CreateKey(modId, name);
            lock (SyncRoot)
            {
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
            var registerProvider = false;
            lock (SyncRoot)
            {
                if (_initialized)
                {
                    return;
                }

                _initialized = true;
                RefreshStateLocked("initialize", null, null, null);
                registerProvider = true;
            }

            OpenACAISonsSaveLifecycle.Lifecycle += OnSaveLifecycle;
            if (registerProvider && !OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke SDK save registry provider could not be registered.");
            }
        }

        private static void OnSaveLifecycle(OpenACAISonsSaveLifecycleEvent evt)
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

            if (string.Equals(evt.Operation, "Load", StringComparison.OrdinalIgnoreCase))
            {
                LoadRegistered(evt);
            }
        }

        private static void SaveRegistered(OpenACAISonsSaveLifecycleEvent evt)
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
                if (evt.SavePlayerOnly.GetValueOrDefault() && !registered.Saveable.IncludeInPlayerSave)
                {
                    continue;
                }

                try
                {
                    if (!ShouldUseSidecarStorage("save", registered))
                    {
                        continue;
                    }

                    var path = GetSaveablePath(registered.Saveable, slotKey);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, registered.Saveable.Save() ?? string.Empty);
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

        private static void LoadRegistered(OpenACAISonsSaveLifecycleEvent evt)
        {
            RegisteredSaveable[] saveables;
            lock (SyncRoot)
            {
                saveables = CopySaveablesLocked();
                _loadOperationCount++;
            }

            var slotKey = CreateSlotKey(evt);
            var loaded = 0;
            foreach (var registered in saveables)
            {
                try
                {
                    if (!ShouldUseSidecarStorage("load", registered))
                    {
                        continue;
                    }

                    var path = GetSaveablePath(registered.Saveable, slotKey);
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    registered.Saveable.Load(File.ReadAllText(path));
                    loaded++;
                }
                catch (Exception ex)
                {
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
            var storageMode = GetStorageMode(registered.Saveable);
            if (storageMode == OpenACAISonsSaveStorageMode.SidecarJson)
            {
                return true;
            }

            if (storageMode == OpenACAISonsSaveStorageMode.EmbeddedPreferred)
            {
                lock (SyncRoot)
                {
                    _sidecarFallbackCount++;
                }

                return true;
            }

            lock (SyncRoot)
            {
                _embeddedRequiredSkipCount++;
            }

            OpenACAIEndnightRuntime.LogSource?.LogWarning(
                "OpenACAI save registry skipped " + operation + " for " + registered.Key + ": embedded save storage is not installed.");
            return false;
        }

        private static string GetSaveablePath(IOpenACAISonsSaveable saveable, string slotKey)
        {
            OpenACAISonsSaveData.EnsureModDirectory(saveable.ModId);
            var relativePath = Path.Combine(
                SlotDirectoryName,
                OpenACAISonsSaveData.NormalizeModId(slotKey),
                OpenACAISonsSaveData.NormalizeModId(saveable.Name) + ".json");
            return OpenACAISonsSaveData.GetModFilePath(saveable.ModId, relativePath);
        }

        private static string CreateSlotKey(OpenACAISonsSaveLifecycleEvent evt)
        {
            if (evt.SelectedSaveId.HasValue && evt.SelectedSaveId.Value > 0)
            {
                return "save-" + evt.SelectedSaveId.Value;
            }

            if (!string.IsNullOrWhiteSpace(evt.Directory))
            {
                var trimmed = evt.Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var directoryName = Path.GetFileName(trimmed);
                if (!string.IsNullOrWhiteSpace(directoryName))
                {
                    return directoryName;
                }
            }

            if (!string.IsNullOrWhiteSpace(evt.GameName))
            {
                return evt.GameName;
            }

            return "unknown-save";
        }

        private static void RecordError(string operation, RegisteredSaveable registered, Exception ex)
        {
            lock (SyncRoot)
            {
                _errorCount++;
                RefreshStateLocked(operation + "-error", null, null, registered.Key);
            }

            OpenACAIEndnightRuntime.LogSource?.LogWarning(
                "OpenACAI save registry " + operation + " failed for " + registered.Key + ": " + ex.Message);
        }

        private static RegisteredSaveable[] CopySaveablesLocked()
        {
            var copy = new RegisteredSaveable[Saveables.Count];
            Saveables.Values.CopyTo(copy, 0);
            return copy;
        }

        private static void RefreshStateLocked(string lastOperation, string lastSlotKey, int? lastAffectedCount, string lastDetail)
        {
            var entries = new List<OpenACAISonsSaveRegistryEntrySnapshot>(Saveables.Count);
            foreach (var saveable in Saveables.Values)
            {
                entries.Add(saveable.CreateState());
            }

            var sidecarOnlyCount = entries.Count(entry => string.Equals(entry.StorageMode, FormatStorageMode(OpenACAISonsSaveStorageMode.SidecarJson), StringComparison.OrdinalIgnoreCase));
            var embeddedPreferredCount = entries.Count(entry => string.Equals(entry.StorageMode, FormatStorageMode(OpenACAISonsSaveStorageMode.EmbeddedPreferred), StringComparison.OrdinalIgnoreCase));
            var embeddedRequiredCount = entries.Count(entry => string.Equals(entry.StorageMode, FormatStorageMode(OpenACAISonsSaveStorageMode.EmbeddedRequired), StringComparison.OrdinalIgnoreCase));

            _state = new OpenACAISonsSaveRegistrySnapshot(
                DateTimeOffset.UtcNow.ToString("O"),
                ServiceKey,
                StorageModel,
                false,
                OpenACAISonsSaveRegistry.EmbeddedStorageStatus,
                OpenACAISonsSaveRegistry.EmbeddedStorageDetail,
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
                entries.ToArray());
            OpenACAIModding.SetService(ServiceKey, _state);
        }

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            var snapshot = State;
            return new OpenACAIMenuContentSnapshot(
                "Sons Save Registry",
                "OpenACAI-owned per-mod sidecar JSON save registry for Sons Of The Forest.",
                new[]
                {
                    CreateRegistrySection(snapshot),
                    CreateActivitySection(snapshot),
                    CreateEntriesSection(snapshot)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateRegistrySection(OpenACAISonsSaveRegistrySnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Registry",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, RegistryStatus(snapshot), "Public OpenACAI custom-save registry service."),
                    Row("Storage", snapshot.StorageModel, "READY", "Per-mod sidecar JSON under OpenACAI UserData; this is intentionally not a RedLoader plugin path."),
                    Row("Embedded Store", snapshot.EmbeddedStorageSupported ? "Available" : "Deferred", EmbeddedStatus(snapshot), snapshot.EmbeddedStorageDetail),
                    Row("Storage Intents", FormatStorageIntents(snapshot), StorageIntentStatus(snapshot), "Registered sidecar, embedded-preferred, and embedded-required saveables."),
                    Row("Registered", snapshot.RegisteredCount.ToString(), snapshot.RegisteredCount > 0 ? "READY" : "INFO", "Registered OpenACAI/Sons saveable objects."),
                    Row("Errors", snapshot.ErrorCount.ToString(), snapshot.ErrorCount == 0 ? "READY" : "CHECK", "Save/load errors recorded by the registry."),
                    Row("Last Operation", string.IsNullOrWhiteSpace(snapshot.LastOperation) ? "None" : snapshot.LastOperation, "INFO", string.IsNullOrWhiteSpace(snapshot.LastDetail) ? "Most recent registry operation." : snapshot.LastDetail)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateActivitySection(OpenACAISonsSaveRegistrySnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
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

        private static OpenACAIMenuSectionSnapshot CreateEntriesSection(OpenACAISonsSaveRegistrySnapshot snapshot)
        {
            var entries = snapshot.Entries ?? Array.Empty<OpenACAISonsSaveRegistryEntrySnapshot>();
            var rows = entries.Count == 0
                ? new[] { Row("Entries", "None", "INFO", "No mod saveables have registered with OpenACAI yet.") }
                : entries
                    .OrderBy(entry => entry.ModId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(entry => Row(entry.ModId + "/" + entry.Name, entry.StorageMode, EntryStatus(entry), entry.Key + " registered " + entry.RegisteredUtc + "; " + (entry.IncludeInPlayerSave ? "included in player-save events" : "world-save events only")))
                    .ToArray();

            return new OpenACAIMenuSectionSnapshot("Registered Saveables", rows);
        }

        private static string RegistryStatus(OpenACAISonsSaveRegistrySnapshot snapshot)
        {
            if (snapshot == null)
            {
                return "MISSING";
            }

            return snapshot.ErrorCount == 0 ? "READY" : "CHECK";
        }

        private static string EmbeddedStatus(OpenACAISonsSaveRegistrySnapshot snapshot)
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

        private static string StorageIntentStatus(OpenACAISonsSaveRegistrySnapshot snapshot)
        {
            if (snapshot == null)
            {
                return "MISSING";
            }

            return snapshot.EmbeddedRequiredCount > 0 && !snapshot.EmbeddedStorageSupported ? "CHECK" : "READY";
        }

        private static string EntryStatus(OpenACAISonsSaveRegistryEntrySnapshot entry)
        {
            return string.Equals(entry.StorageMode, FormatStorageMode(OpenACAISonsSaveStorageMode.EmbeddedRequired), StringComparison.OrdinalIgnoreCase)
                ? "CHECK"
                : "READY";
        }

        private static string FormatStorageIntents(OpenACAISonsSaveRegistrySnapshot snapshot)
        {
            return snapshot.SidecarOnlyCount
                + " sidecar | "
                + snapshot.EmbeddedPreferredCount
                + " preferred | "
                + snapshot.EmbeddedRequiredCount
                + " required";
        }

        private static OpenACAIMenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new OpenACAIMenuRowSnapshot(label, value, status, detail);
        }

        private static string CreateKey(string modId, string name)
        {
            return OpenACAISonsSaveData.NormalizeModId(modId) + ":" + OpenACAISonsSaveData.NormalizeModId(name);
        }

        private static OpenACAISonsSaveStorageMode GetStorageMode(IOpenACAISonsSaveable saveable)
        {
            if (saveable is IOpenACAISonsSaveStoragePreference preference)
            {
                return preference.StorageMode;
            }

            return OpenACAISonsSaveStorageMode.SidecarJson;
        }

        private static string FormatStorageMode(OpenACAISonsSaveStorageMode mode)
        {
            switch (mode)
            {
                case OpenACAISonsSaveStorageMode.EmbeddedPreferred:
                    return "embedded-preferred";
                case OpenACAISonsSaveStorageMode.EmbeddedRequired:
                    return "embedded-required";
                default:
                    return "sidecar-json";
            }
        }

        private sealed class RegisteredSaveable
        {
            internal RegisteredSaveable(string key, IOpenACAISonsSaveable saveable)
            {
                Key = key;
                Saveable = saveable;
                RegisteredUtc = DateTimeOffset.UtcNow;
            }

            internal string Key { get; }
            internal IOpenACAISonsSaveable Saveable { get; }
            private DateTimeOffset RegisteredUtc { get; }

            internal OpenACAISonsSaveRegistryEntrySnapshot CreateState()
            {
                return new OpenACAISonsSaveRegistryEntrySnapshot(
                    Key,
                    Saveable.ModId,
                    Saveable.Name,
                    Saveable.IncludeInPlayerSave,
                    FormatStorageMode(GetStorageMode(Saveable)),
                    RegisteredUtc.ToString("O"));
            }
        }

        private sealed class Registration : IDisposable
        {
            private readonly string _key;
            private bool _disposed;

            internal Registration(string key)
            {
                _key = key;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                lock (SyncRoot)
                {
                    if (Saveables.Remove(_key))
                    {
                        RefreshStateLocked("dispose", null, null, _key);
                    }
                }
            }
        }

        private sealed class JsonSaveable<T> : IOpenACAISonsSaveable, IOpenACAISonsSaveStoragePreference
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
                OpenACAISonsSaveStorageMode storageMode)
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
            public OpenACAISonsSaveStorageMode StorageMode { get; }

            public string Save()
            {
                return JsonSerializer.Serialize(_save(), _jsonOptions);
            }

            public void Load(string data)
            {
                _load(JsonSerializer.Deserialize<T>(data, _jsonOptions));
            }
        }

        private sealed class Provider : IOpenACAIMenuContentProvider
        {
            public string Id => OpenACAIMenuProviderIds.SonsSaveRegistry;
            public string DisplayName => "Sons Save Registry";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 724;
            public string Source => "Modstoke Loader";
            public string Description => "Show OpenACAI-owned per-mod save registry state.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAISonsSaveRegistry.CreateContent();
            }
        }
    }

    public interface IOpenACAISonsSaveable
    {
        string ModId { get; }
        string Name { get; }
        bool IncludeInPlayerSave { get; }
        string Save();
        void Load(string data);
    }

    public interface IOpenACAISonsSaveStoragePreference
    {
        OpenACAISonsSaveStorageMode StorageMode { get; }
    }

    public enum OpenACAISonsSaveStorageMode
    {
        SidecarJson = 0,
        EmbeddedPreferred = 1,
        EmbeddedRequired = 2
    }

    public sealed class OpenACAISonsSaveRegistrySnapshot
    {
        internal OpenACAISonsSaveRegistrySnapshot(
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
            IReadOnlyCollection<OpenACAISonsSaveRegistryEntrySnapshot> entries)
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
            Entries = entries;
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
        public IReadOnlyCollection<OpenACAISonsSaveRegistryEntrySnapshot> Entries { get; }

        internal static OpenACAISonsSaveRegistrySnapshot CreateUnknown()
        {
            return new OpenACAISonsSaveRegistrySnapshot(
                DateTimeOffset.UtcNow.ToString("O"),
                OpenACAISonsSaveRegistry.ServiceKey,
                OpenACAISonsSaveRegistry.StorageModel,
                false,
                OpenACAISonsSaveRegistry.EmbeddedStorageStatus,
                OpenACAISonsSaveRegistry.EmbeddedStorageDetail,
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
                Array.Empty<OpenACAISonsSaveRegistryEntrySnapshot>());
        }
    }

    public sealed class OpenACAISonsSaveRegistryEntrySnapshot
    {
        internal OpenACAISonsSaveRegistryEntrySnapshot(
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
