// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Il2CppInterop.Runtime.Injection;
using global::Sons.Gameplay.GameSetup;
using global::Sons.Save;
using SonsSdk;

namespace Modstoke.Sons;

public static partial class WorldRecords
{
    internal const string SerializedName = "ModstokeWorld";
    private static bool _initializationAttempted;
    // The game keeps weak references. Both wrappers must remain strongly rooted.
    private static WorldRecordSerializer _serializer;
    private static ISaveGameSerializer<ModSaveData> _nativeSerializer;

    internal static void Initialize()
    {
        if (_initializationAttempted) return;
        _initializationAttempted = true;
        try
        {
            _ownerThread = Environment.CurrentManagedThreadId;
            ClassInjector.RegisterTypeInIl2Cpp<ModSaveData>();
            ClassInjector.RegisterTypeInIl2Cpp<WorldRecordSerializer>(new()
            {
                Interfaces = new[] { typeof(ISaveGameSerializer<ModSaveData>) }
            });
            _serializer = new WorldRecordSerializer();
            _nativeSerializer = new ISaveGameSerializer<ModSaveData>(_serializer.Pointer);
            EnsureRegistered();
            GameRuntime.SceneChanged += OnScene;
            SdkEvents.OnGameStart.Subscribe(OnGameStarted);
            SdkEvents.OnWorldExited.Subscribe(OnWorldExited);
        }
        catch (Exception error)
        {
            _ownerThread = 0;
            ReportProblem("World record initialization failed", error);
        }
    }

    private static void EnsureRegistered()
    {
        if (_nativeSerializer == null) throw new InvalidOperationException("World record serializer is unavailable.");
        CheckNames(SaveGameManager._serializerQueue);
        if (SaveGameManager.TryGetInstance(out var manager) && manager != null && !manager.WasCollected && manager)
            CheckNames(manager._serializers);
        // Registration deduplicates by serializer identity; persistent=true does not retain it across managers.
        SaveGameManager.RegisterSerializer(_nativeSerializer);
    }

    private static void CheckNames(Il2CppSystem.Collections.Generic.List<IRegisteredSerializer> serializers)
    {
        if (serializers == null) return;
        for (var index = 0; index < serializers.Count; index++)
        {
            var entry = serializers[index];
            if (entry != null && entry.HasTarget && string.Equals(entry.Name, SerializedName, StringComparison.Ordinal)
                && !entry.Is(_serializer))
                throw new InvalidOperationException("Another serializer uses the ModstokeWorld save name.");
        }
    }

    private static void OnScene(global::Modstoke.Sons.SceneEvent scene)
    {
        if (Environment.CurrentManagedThreadId == _ownerThread && scene.Phase == "Loaded"
            && (scene.SceneKind == SceneKind.Title || scene.SceneKind == SceneKind.Loading))
            Reset(null);
    }

    private static void OnGameStarted()
    {
        if (CheckThread() != WorldRecordStatus.Success) return;
        try
        {
            // Scene entry and native Load own resets. This notification can occur
            // after deserialization and must not discard an already loaded world.
            _gameStarted = true;
        }
        catch (Exception error) { ReportProblem("World start could not be observed", error); }
    }

    private static void OnWorldExited()
    {
        if (Environment.CurrentManagedThreadId == _ownerThread) Reset(null);
    }

    internal static WorldRecordLoad BeforeNativeLoad(string directory, SaveGameType type)
    {
        if (CheckThread() != WorldRecordStatus.Success) return null;
        Reset(null);
        if (type == SaveGameType.MultiplayerClient) return null;
        var frame = new WorldRecordLoad();
        _load = frame;
        try
        {
            EnsureRegistered();
            if (!SaveGameManager.TryGetInstance(out var manager) || manager == null || manager.WasCollected || !manager)
                throw new InvalidOperationException("Save manager is unavailable.");
            frame.Manager = manager.Pointer;
            frame.AbsenceProven = RecordsProvenAbsent(directory);
        }
        catch (Exception error) { frame.Faulted = true; ReportProblem("World record load could not start", error); }
        return frame;
    }

    internal static void AfterNativeLoad(WorldRecordLoad frame, bool success)
    {
        if (frame == null || Environment.CurrentManagedThreadId != _ownerThread) return;
        // Harmony can finalize an already completed load after a later postfix throws.
        if (frame.Completed) return;
        frame.Completed = true;
        if (!ReferenceEquals(frame, _load))
        {
            Reset("Overlapping world loads cannot share records.");
            _faulted = true;
            return;
        }
        _load = null;
        try
        {
            if (!success || !SaveGameManager.TryGetInstance(out var manager) || manager == null
                || manager.WasCollected || !manager || manager.Pointer != frame.Manager)
            {
                _faulted = true;
                _lastProblem = "The native world load did not complete on its original save manager.";
                return;
            }
            _manager = frame.Manager;
            if (!frame.Seen && !frame.AbsenceProven)
            {
                _faulted = true;
                _lastProblem = "The game skipped a possibly existing Modstoke world record. Recover or reload the world before saving.";
                return;
            }
            _faulted = frame.Faulted;
            _preservedData = frame.Faulted ? frame.RawData : null;
            if (frame.Faulted) return;
            // A missing serializer entry is an older world with no records, not the previous world's data.
            _records = frame.Records ?? new Dictionary<string, string>(StringComparer.Ordinal);
            _revision = frame.Revision;
            _current = new WorldRecordContext(frame.Seen ? frame.WorldId : Guid.NewGuid(), _generation);
        }
        catch (Exception error) { _faulted = true; ReportProblem("World record load could not finish", error); }
    }

    internal static bool BeforeNativeSave(bool playerOnly, out WorldRecordSave frame)
    {
        frame = null;
        if (playerOnly || !_initializationAttempted) return true;
        try
        {
            if (CheckThread() != WorldRecordStatus.Success)
                throw new InvalidOperationException("World record save hooks are unavailable on this thread.");
            if (_load != null || _save != null) throw new InvalidOperationException("A world save or load is already active.");
            if (_faulted && _preservedData == null)
                throw new InvalidOperationException("Unreadable world records must be recovered before saving.");
            EnsureRegistered();
            if (!SaveGameManager.TryGetInstance(out var manager) || manager == null || manager.WasCollected || !manager)
                throw new InvalidOperationException("The world save manager is unavailable.");
            if (_manager != IntPtr.Zero && manager.Pointer != _manager)
                throw new InvalidOperationException("The world save manager changed.");
            if (!_faulted && _current == null)
            {
                if (GameSetupManager.GameLoadedFromSave())
                    throw new InvalidOperationException("This world's load was not observed; reload before saving its records.");
                // Native world saves also include single player. Public mutations still require a server.
                _manager = manager.Pointer;
                _current = new WorldRecordContext(Guid.NewGuid(), _generation);
            }
            frame = new WorldRecordSave { Manager = manager.Pointer, Generation = _generation };
            _save = frame;
            LastSaveStatus = WorldRecordSaveStatus.None;
            return true;
        }
        catch (Exception error)
        {
            LastSaveStatus = WorldRecordSaveStatus.Refused;
            ReportProblem("Native world save refused to avoid losing Modstoke records", error);
            return false;
        }
    }

    internal static void AfterNativeSave(WorldRecordSave frame)
    {
        if (frame != null && ReferenceEquals(frame, _save)) _save = null;
    }

    internal static bool ShouldSerializeNative => Environment.CurrentManagedThreadId == _ownerThread
        && (_load != null || _save != null);

    internal static ModSaveData SerializeNative()
    {
        if (_save == null || _save.Generation != _generation || _save.Manager != _manager)
            throw new InvalidOperationException("No current native world save is active.");
        var json = _faulted ? _preservedData : Encode(_current.WorldId, _revision, _records);
        if (json == null) throw new InvalidOperationException("World records cannot be serialized.");
        var data = new ModSaveData(json);
        LastSaveStatus = _faulted ? WorldRecordSaveStatus.Preserved : WorldRecordSaveStatus.Serialized;
        return data;
    }

    internal static void DeserializeNative(string json)
    {
        var frame = _load;
        if (frame == null || Environment.CurrentManagedThreadId != _ownerThread) return;
        if (frame.Seen)
        {
            frame.Faulted = true;
            // One retained payload cannot preserve multiple conflicting inputs.
            frame.RawData = null;
            _lastProblem = "Duplicate world record serializer input. Reload or recover the world before saving.";
            return;
        }
        frame.Seen = true;
        try
        {
            var data = SaveGameManager.Deserialize<ModSaveData>(json);
            if (data == null) throw new InvalidOperationException("Missing native world record carrier.");
            frame.RawData = data.ModData.Get();
            Decode(frame.RawData, out frame.WorldId, out frame.Revision, out frame.Records);
        }
        catch (Exception error)
        {
            frame.Faulted = true;
            ReportProblem("Saved Modstoke world records could not be read", error);
        }
    }

    private static bool RecordsProvenAbsent(string directory)
    {
        // Native Load can succeed after skipping an unreadable serializer entry.
        // Only an independently confirmed absence permits a new empty record set.
        // Check loose files too: the game can fall back to them after a ZIP error.
        try
        {
            if (string.IsNullOrWhiteSpace(directory)) return false;
            var uniqueName = SerializedName + "SaveData.json";
            if (ExistsForInspection(Path.Combine(directory, uniqueName))) return false;
            var archivePath = Path.Combine(directory, "SaveData.zip");
            if (ExistsForInspection(archivePath))
            {
                using var archive = ZipFile.OpenRead(archivePath);
                if (archive.GetEntry(uniqueName) != null) return false;
                var main = archive.GetEntry("SaveData.json");
                if (main != null)
                {
                    using var stream = main.Open();
                    if (!EnvelopeProvesAbsence(stream, main.Length)) return false;
                }
            }
            var looseMain = Path.Combine(directory, "SaveData.json");
            if (ExistsForInspection(looseMain))
            {
                using var stream = File.OpenRead(looseMain);
                if (!EnvelopeProvesAbsence(stream, stream.Length)) return false;
            }
            return true;
        }
        catch (Exception error)
        {
            ReportProblem("Existing world record presence could not be established", error);
            return false;
        }
    }

    private static bool ExistsForInspection(string path)
    {
        // File.Exists also returns false for access errors, which is not absence.
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool EnvelopeProvesAbsence(Stream stream, long length)
    {
        // The stock main envelope can hold unrelated serializers. Avoid loading
        // an unbounded document merely to establish that our key is absent.
        if (length <= 0 || length > 67108864) return false;
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
        var foundData = false;
        foreach (var field in document.RootElement.EnumerateObject())
        {
            if (field.Name != "Data") continue;
            if (foundData || field.Value.ValueKind != JsonValueKind.Object) return false;
            foundData = true;
            foreach (var record in field.Value.EnumerateObject())
                if (record.Name == SerializedName) return false;
        }
        return foundData;
    }
}

internal sealed class WorldRecordLoad
{
    internal IntPtr Manager;
    internal bool Completed;
    internal bool Seen;
    internal bool AbsenceProven;
    internal bool Faulted;
    internal string RawData;
    internal Guid WorldId;
    internal long Revision;
    internal Dictionary<string, string> Records;
}

internal sealed class WorldRecordSave
{
    internal IntPtr Manager;
    internal long Generation;
    internal bool Refused;
}

internal sealed class WorldRecordSerializer : Il2CppSystem.Object
{
    private string _pending;
    public WorldRecordSerializer(IntPtr pointer) : base(pointer) { }
    public WorldRecordSerializer() : base(ClassInjector.DerivedConstructorPointer<WorldRecordSerializer>()) =>
        ClassInjector.DerivedConstructorBody(this);
    public string SerializedName => WorldRecords.SerializedName;
    public bool UniqueFile => true;
    public bool ShouldSerialize => WorldRecords.ShouldSerializeNative;
    public bool IncludeInPlayerSave => false;
    public int ExecutionOrder => 6970;
    private Il2CppSystem.Func<string, ModSaveData> DeserializeOverrideAction => (Il2CppSystem.Func<string, ModSaveData>)Capture;
    private ModSaveData OnSerialize() => WorldRecords.SerializeNative();
    private ModSaveData Capture(string json) { _pending = json; return null; }
    private void OnDeserialize(ModSaveData ignored)
    {
        var json = _pending;
        _pending = null;
        WorldRecords.DeserializeNative(json);
    }
}
