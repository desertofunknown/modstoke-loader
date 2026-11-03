// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Modstoke.Loader;
using global::Sons.Gameplay.GameSetup;
using global::Sons.Save;
using SonsSdk;

namespace Modstoke.Sons;

internal static class ModuleStoragePersistence
{
    internal const string SerializedName = "ModstokeModuleInventory";
    private static int _ownerThread;
    private static bool _initializationAttempted, _ready, _selected, _gameStarted;
    private static bool _contextReady, _hasData, _registrationVerified;
    private static volatile bool _faulted;
    private static IntPtr _manager;
    private static IntPtr _registeredManager;
    private static Action<string> _warning;
    // Native registrations are weak references; root both managed wrappers.
    private static ModuleStorageSerializer _serializer;
    private static ISaveGameSerializer<ModSaveData> _nativeSerializer;
    private static HarmonyLib.Harmony _harmony;
    private static LoadFrame _load;
    private static SaveFrame _save;

    internal static bool Ready
    {
        get
        {
            if (!OnOwnerThread || !_ready || _faulted) return false;
            try
            {
                if (_contextReady && RequireManager().Pointer != _manager)
                    throw new InvalidOperationException("The loaded player save manager changed.");
                EnsureRegistered();
                return true;
            }
            catch (Exception error)
            {
                _faulted = true;
                Warn("Module inventory serializer registration is unavailable", error);
                return false;
            }
        }
    }

    internal static void Initialize(HarmonySupportSnapshot support, Action<string> warning)
    {
        if (_initializationAttempted) return;
        _initializationAttempted = true;
        _ownerThread = Environment.CurrentManagedThreadId;
        _warning = warning;
        if (RedLoader.Utils.LoaderEnvironment.IsDedicatedServer) return;
        if (support?.SafeForHarmonyPatches != true)
        {
            Warn("Module inventory persistence is unavailable; native saves cannot be guarded without Harmony support.");
            return;
        }
        try
        {
            GameRuntime.SceneChanged += OnScene;
            SdkEvents.OnGameStart.Subscribe(OnGameStarted);
            SdkEvents.OnWorldExited.Subscribe(OnWorldExited);
            var load = AccessTools.Method(typeof(SaveGameManager), nameof(SaveGameManager.Load),
                new[] { typeof(string), typeof(SaveGameType) });
            var save = AccessTools.Method(typeof(SaveGameManager), nameof(SaveGameManager.Save),
                new[] { typeof(string), typeof(string), typeof(bool) });
            if (load == null || load.ReturnType != typeof(bool) || save == null || save.ReturnType != typeof(void))
                throw new MissingMethodException("The native save/load signatures are unavailable.");

            // Install the save guard first; partial setup only permits independently module-free sessions.
            _harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-module-storage-persistence");
            _harmony.Patch(save,
                prefix: Hook(nameof(BeforeSave), Priority.First + 100),
                finalizer: Hook(nameof(SaveFinalizer), Priority.Last - 100));
            _harmony.Patch(load,
                prefix: Hook(nameof(BeforeLoad), Priority.First + 100),
                finalizer: Hook(nameof(LoadFinalizer), Priority.Last - 100));

            ClassInjector.RegisterTypeInIl2Cpp<ModSaveData>();
            ClassInjector.RegisterTypeInIl2Cpp<ModuleStorageSerializer>(new()
            {
                Interfaces = new[] { typeof(ISaveGameSerializer<ModSaveData>) }
            });
            _serializer = new ModuleStorageSerializer();
            _nativeSerializer = new ISaveGameSerializer<ModSaveData>(_serializer.Pointer);
            EnsureRegistered();
            _ready = true;
        }
        catch (Exception error)
        {
            Warn("Module inventory persistence initialization failed; only proven module-free vanilla saves can pass an installed guard", error);
        }
    }

    // Observation is always installed. Selection only permits creation of new extension data.
    internal static bool Select()
    {
        if (!Ready || _load != null || _save != null
            || RedLoader.Utils.LoaderEnvironment.IsDedicatedServer) return false;
        try
        {
            if (SaveGameManager.TryGetInstance(out var manager) && manager != null && !manager.WasCollected
                && manager && manager._loadingInProgress) return false;
            if (!_contextReady && (_gameStarted || GameRuntime.IsGameScene)) EstablishNewGame();
            // Startup selection does not establish a session. EnsureLocal must call again before store use.
            _selected = true;
            return true;
        }
        catch (Exception error)
        {
            Warn("Module inventory activation requires a settled, observed player context", error);
            return false;
        }
    }

    private static bool OnOwnerThread => _ownerThread != 0 && Environment.CurrentManagedThreadId == _ownerThread;

    private static HarmonyMethod Hook(string name, int hookPriority) => new(typeof(ModuleStoragePersistence), name)
    {
        priority = hookPriority
    };

    private static void RequireThread()
    {
        if (!OnOwnerThread) throw new InvalidOperationException("Module inventory persistence requires its initialization thread.");
    }

    private static SaveGameManager RequireManager()
    {
        if (!SaveGameManager.TryGetInstance(out var manager) || manager == null || manager.WasCollected || !manager)
            throw new InvalidOperationException("The native player save manager is unavailable.");
        return manager;
    }

    private static void EnsureRegistered()
    {
        if (_serializer == null || _serializer.WasCollected || _nativeSerializer == null || _nativeSerializer.WasCollected)
            throw new InvalidOperationException("The module inventory serializer is unavailable.");
        var hasManager = SaveGameManager.TryGetInstance(out var manager) && manager != null && !manager.WasCollected && manager;
        var pointer = hasManager ? manager.Pointer : IntPtr.Zero;
        if (_registrationVerified && _registeredManager == pointer) return;
        _registrationVerified = false;
        CheckNames(SaveGameManager._serializerQueue);
        if (hasManager) CheckNames(manager._serializers);
        SaveGameManager.RegisterSerializer(_nativeSerializer);
        if (SaveGameManager.TryGetInstance(out manager) && manager != null && !manager.WasCollected && manager)
        {
            if (manager.Pointer != pointer || !ContainsSerializer(manager._serializers))
                throw new InvalidOperationException("The current player save manager did not retain the module inventory registration.");
        }
        else if (pointer != IntPtr.Zero || !ContainsSerializer(SaveGameManager._serializerQueue))
            throw new InvalidOperationException("The native registration queue did not retain the module inventory serializer.");
        _registeredManager = pointer;
        _registrationVerified = true;
    }

    private static bool ContainsSerializer(Il2CppSystem.Collections.Generic.List<IRegisteredSerializer> serializers)
    {
        if (serializers == null) return false;
        for (var index = 0; index < serializers.Count; index++)
        {
            var entry = serializers[index];
            if (entry != null && !entry.WasCollected && entry.HasTarget && entry.Is(_serializer)
                && string.Equals(entry.Name, SerializedName, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static void CheckNames(Il2CppSystem.Collections.Generic.List<IRegisteredSerializer> serializers)
    {
        if (serializers == null) return;
        for (var index = 0; index < serializers.Count; index++)
        {
            var entry = serializers[index];
            if (entry != null && !entry.WasCollected && entry.HasTarget
                && string.Equals(entry.Name, SerializedName, StringComparison.Ordinal) && !entry.Is(_serializer))
                throw new InvalidOperationException("Another serializer uses the ModstokeModuleInventory save name.");
        }
    }

    private static void ResetSession()
    {
        RequireThread();
        if (_load != null || _save != null)
            throw new InvalidOperationException("A player save or load is still active during a session reset.");
        _manager = IntPtr.Zero;
        _registeredManager = IntPtr.Zero;
        _registrationVerified = false;
        _contextReady = false;
        _hasData = false;
        _gameStarted = false;
        // Keep the session closed if the callback fails to discard the old world's state.
        _faulted = true;
        ModuleStorage.ResetForLoad();
        _faulted = false;
    }

    private static void OnScene(global::Modstoke.Sons.SceneEvent scene)
    {
        if (scene.Phase == "Loaded" && (scene.SceneKind == SceneKind.Title
            || scene.SceneKind == SceneKind.Loading)) OnWorldExited();
    }

    private static void OnWorldExited()
    {
        try { ResetSession(); }
        catch (Exception error) { _faulted = true; Warn("Module inventory session reset failed", error); }
    }

    private static void OnGameStarted()
    {
        try
        {
            RequireThread();
            _gameStarted = true;
        }
        catch (Exception error) { _faulted = true; Warn("Module inventory session could not start", error); }
    }

    private static void EstablishNewGame(bool requireRegistration = true)
    {
        if (_faulted || (!_gameStarted && !GameRuntime.IsGameScene) || GameSetupManager.GameLoadedFromSave())
            throw new InvalidOperationException("This player's load was not observed; reload before saving module inventory.");
        if (requireRegistration) EnsureRegistered();
        var manager = RequireManager();
        if (manager._loadingInProgress)
            throw new InvalidOperationException("The native player load is still active.");
        _manager = manager.Pointer;
        try { ModuleStorage.CompleteLoad(true, true); }
        catch { _faulted = true; throw; }
        _contextReady = true;
        _hasData = false;
    }

    private static bool BeforeLoad(string dir, bool __runOriginal, ref bool __result, out LoadFrame __state)
    {
        __state = null;
        if (!OnOwnerThread || _load != null || _save != null)
        {
            // A refused nested invocation never owns the outer operation's state or callback gate.
            __state = new LoadFrame { Completed = true };
            __result = false;
            Warn("Native player load refused on the wrong thread or during another save/load.");
            return false;
        }
        try
        {
            ResetSession();
            __state = new LoadFrame();
            _load = __state;
            if (!__runOriginal) throw new InvalidOperationException("The native player load was skipped.");
            __state.Manager = RequireManager().Pointer;
            __state.Absent = RecordsProvenAbsent(dir);
            if (!_ready)
            {
                if (_selected || !__state.Absent)
                    throw new InvalidOperationException("Unavailable module persistence cannot load selected or possibly saved extension data.");
                __state.VanillaOnly = true;
            }
            else EnsureRegistered();
            return true;
        }
        catch (Exception error)
        {
            _faulted = true;
            if (__state != null) __state.Faulted = true;
            __result = false;
            Warn("Native player load refused to protect module inventory", error);
            return false;
        }
    }

    private static Exception LoadFinalizer(Exception __exception, bool __runOriginal, bool __result, LoadFrame __state)
    {
        if (__state?.Completed == true) return __exception;
        if (!OnOwnerThread)
        {
            _faulted = true;
            Warn("Module inventory load finalized on the wrong thread; reload is required.");
            return __exception;
        }
        if (__state == null)
        {
            // An earlier prefix can skip ours. Do not leave the preceding world's state usable.
            if (_load != null || _save != null) return __exception;
            try { ResetSession(); ModuleStorage.CompleteLoad(false, false); }
            catch (Exception error) { Warn("Skipped module inventory load could not be reset", error); }
            _faulted = true;
            return __exception;
        }
        __state.Completed = true;
        var success = false;
        try
        {
            success = ReferenceEquals(_load, __state) && !_faulted && !__state.Faulted
                && __exception == null && __runOriginal && __result
                && RequireManager().Pointer == __state.Manager
                && (__state.Staged || (__state.Absent && !__state.Seen));
        }
        catch (Exception error) { Warn("The native player load manager could not be validated", error); }
        try
        {
            _manager = __state.Manager;
            ModuleStorage.CompleteLoad(success, success && __state.Absent && !__state.Seen);
            if (!success) Warn("Native player load did not establish module inventory; saving is blocked until a fresh load.");
        }
        catch (Exception error)
        {
            success = false;
            Warn("Module inventory load completion failed", error);
        }
        finally
        {
            _contextReady = success;
            _hasData = success && __state.Staged;
            _faulted = !success;
            if (ReferenceEquals(_load, __state)) _load = null;
        }
        return __exception;
    }

    internal static void CaptureLoadInput(string json)
    {
        try
        {
            RequireThread();
            var frame = _load ?? throw new InvalidOperationException("Module inventory input arrived outside a native load.");
            if (frame.Seen || frame.Absent || frame.Faulted || RequireManager().Pointer != frame.Manager)
                throw new InvalidOperationException("Module inventory input is duplicated or does not match the inspected load.");
            frame.Seen = true;
            if (string.IsNullOrWhiteSpace(json)) throw new FormatException("The module inventory carrier is missing.");
            frame.RawCarrier = json;
        }
        catch (Exception error)
        {
            _faulted = true;
            if (OnOwnerThread && _load != null) _load.Faulted = true;
            Warn("Module inventory input was rejected", error);
        }
    }

    internal static void DeserializeNative()
    {
        try
        {
            RequireThread();
            var frame = _load ?? throw new InvalidOperationException("Module inventory deserialization has no native load.");
            if (frame.Faulted || _faulted || !frame.Seen || frame.Staged || frame.RawCarrier == null
                || RequireManager().Pointer != frame.Manager)
                throw new InvalidOperationException("Module inventory deserialization is not current.");
            var raw = frame.RawCarrier;
            frame.RawCarrier = null;
            var carrier = SaveGameManager.Deserialize<ModSaveData>(raw);
            if (carrier == null) throw new FormatException("The native module inventory carrier is missing.");
            string json = carrier.ModData.Get();
            if (string.IsNullOrWhiteSpace(json)) throw new FormatException("The module inventory payload is missing.");
            // Stage even while unselected: the owner must retain existing data without activating stores.
            ModuleStorage.StageLoad(json);
            frame.Staged = true;
        }
        catch (Exception error)
        {
            _faulted = true;
            if (OnOwnerThread && _load != null) _load.Faulted = true;
            Warn("Saved module inventory could not be staged", error);
        }
    }

    private static bool BeforeSave(string dir, bool __runOriginal, out SaveFrame __state)
    {
        __state = null;
        if (!__runOriginal) return true;
        try
        {
            RequireThread();
            if (_faulted) throw new InvalidOperationException("Module inventory persistence is not ready for saving.");
            if (_load != null || _save != null)
                throw new InvalidOperationException("A native player save or load is already active.");
            if (!_ready && (_selected || _hasData))
                throw new InvalidOperationException("Unavailable module persistence cannot save activated or existing extension data.");
            if (_ready) EnsureRegistered();
            if (!_contextReady) EstablishNewGame(_ready);
            var manager = RequireManager();
            if (manager.Pointer != _manager || manager._loadingInProgress)
                throw new InvalidOperationException("The player save manager changed or is loading.");
            if (!_ready && !RecordsProvenAbsent(dir))
                throw new InvalidOperationException("A vanilla fallback cannot overwrite possibly saved module inventory.");
            __state = new SaveFrame { Manager = manager.Pointer };
            _save = __state;
            if (_selected || _hasData)
            {
                // The callback holds its mutation gate until EndSave, including when capture throws.
                __state.CaptureEntered = true;
                var json = ModuleStorage.CaptureSave();
                if (string.IsNullOrWhiteSpace(json))
                    throw new InvalidOperationException("Module inventory capture returned no payload.");
                __state.Carrier = new ModSaveData(json);
            }
            return true;
        }
        catch (Exception error)
        {
            if (__state != null) __state.Refused = true;
            Warn("Native player save refused to protect module inventory", error);
            return false;
        }
    }

    private static Exception SaveFinalizer(Exception __exception, bool __runOriginal, SaveFrame __state)
    {
        if (__state == null || __state.Completed) return __exception;
        if (!OnOwnerThread)
        {
            _faulted = true;
            Warn("Module inventory save finalized on the wrong thread; the save gate remains closed.");
            return __exception;
        }
        __state.Completed = true;
        try
        {
            if (!ReferenceEquals(_save, __state) || RequireManager().Pointer != __state.Manager)
                throw new InvalidOperationException("The native save did not finish on its original player save manager.");
            if (!__state.Refused && __runOriginal && __exception == null && __state.Carrier != null && !__state.Serialized)
                throw new InvalidOperationException("The native save skipped the captured module inventory serializer.");
            if (__exception != null) Warn("The native player save threw; module inventory persistence was not confirmed.");
        }
        catch (Exception error) { _faulted = true; Warn("Module inventory save completion is uncertain", error); }
        finally
        {
            try
            {
                if (__state.CaptureEntered) ModuleStorage.EndSave();
            }
            catch (Exception error) { _faulted = true; Warn("Module inventory save gate could not be released", error); }
            if (ReferenceEquals(_save, __state)) _save = null;
        }
        return __exception;
    }

    internal static bool ShouldSerializeNative => OnOwnerThread && !_faulted
        && ((_load != null && !_load.VanillaOnly) || (_save != null && !_save.Refused && _save.Carrier != null));

    internal static ModSaveData SerializeNative()
    {
        RequireThread();
        var frame = _save;
        if (_faulted || frame == null || frame.Refused || frame.Carrier == null || frame.Manager != _manager
            || RequireManager().Pointer != frame.Manager)
            throw new InvalidOperationException("No captured module inventory save is active.");
        frame.Serialized = true;
        // Native serializer participation is not a durable disk receipt or a crash-atomicity guarantee.
        return frame.Carrier;
    }

    private static bool RecordsProvenAbsent(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new IOException("The native player save directory cannot be inspected.");
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.Directory) == 0)
                throw new IOException("The native player save path is not a directory.");
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        var uniqueName = SerializedName + "SaveData.json";
        var matches = ExistsForInspection(Path.Combine(directory, uniqueName)) ? 1 : 0;
        var archivePath = Path.Combine(directory, "SaveData.zip");
        if (ExistsForInspection(archivePath))
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var unique = FindUniqueEntry(archive, uniqueName);
            if (unique != null) matches++;
            var main = FindUniqueEntry(archive, "SaveData.json");
            if (main != null)
            {
                using var stream = main.Open();
                if (EnvelopeContainsRecord(stream, main.Length)) matches++;
            }
        }
        // Native ZIP failures can fall back to loose files; inspect both before declaring absence.
        var looseMain = Path.Combine(directory, "SaveData.json");
        if (ExistsForInspection(looseMain))
        {
            using var stream = File.OpenRead(looseMain);
            if (EnvelopeContainsRecord(stream, stream.Length)) matches++;
        }
        if (matches > 1) throw new IOException("Module inventory appears in multiple native save sources.");
        return matches == 0;
    }

    private static ZipArchiveEntry FindUniqueEntry(ZipArchive archive, string name)
    {
        ZipArchiveEntry found = null;
        foreach (var entry in archive.Entries)
        {
            var basename = entry.FullName.AsSpan();
            basename = basename.Slice(basename.LastIndexOfAny('\\', '/') + 1);
            if (!basename.Equals(name.AsSpan(), StringComparison.OrdinalIgnoreCase)) continue;
            if (found != null || !string.Equals(entry.FullName, name, StringComparison.Ordinal))
                throw new IOException("The native archive has an ambiguous module inventory or main save entry.");
            found = entry;
        }
        return found;
    }

    private static bool ExistsForInspection(string path)
    {
        // File.Exists hides access errors. Only not-found exceptions establish absence.
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new IOException("A native save file path refers to a directory.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool EnvelopeContainsRecord(Stream stream, long length)
    {
        if (length <= 0 || length > 67108864) throw new FormatException("The native save envelope has an invalid size.");
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new FormatException("The native save envelope is not an object.");
        var foundData = false;
        var foundRecord = false;
        foreach (var field in document.RootElement.EnumerateObject())
        {
            if (field.Name != "Data") continue;
            if (foundData || field.Value.ValueKind != JsonValueKind.Object)
                throw new FormatException("The native save envelope has ambiguous data.");
            foundData = true;
            foreach (var record in field.Value.EnumerateObject())
            {
                if (!string.Equals(record.Name, SerializedName, StringComparison.Ordinal)) continue;
                if (foundRecord) throw new FormatException("The native save envelope duplicates module inventory.");
                foundRecord = true;
            }
        }
        if (!foundData) throw new FormatException("The native save envelope has no data object.");
        return foundRecord;
    }

    private static void Warn(string message, Exception error = null)
    {
        // Do not include saved payloads or allow a diagnostic callback to break finalizer cleanup.
        try { _warning?.Invoke(error == null ? message : message + ": " + error.GetType().Name); }
        catch (Exception) { }
    }

    private sealed class LoadFrame
    {
        internal IntPtr Manager;
        internal bool Completed, Faulted, Absent, Seen, Staged, VanillaOnly;
        internal string RawCarrier;
    }

    private sealed class SaveFrame
    {
        internal IntPtr Manager;
        internal bool Completed, Refused, CaptureEntered, Serialized;
        internal ModSaveData Carrier;
    }
}

internal sealed class ModuleStorageSerializer : Il2CppSystem.Object
{
    public ModuleStorageSerializer(IntPtr pointer) : base(pointer) { }
    public ModuleStorageSerializer() : base(ClassInjector.DerivedConstructorPointer<ModuleStorageSerializer>()) =>
        ClassInjector.DerivedConstructorBody(this);
    public string SerializedName => ModuleStoragePersistence.SerializedName;
    public bool UniqueFile => true;
    public bool IncludeInPlayerSave => true;
    public bool ShouldSerialize => ModuleStoragePersistence.ShouldSerializeNative;
    public int ExecutionOrder => 6971;
    private Il2CppSystem.Func<string, ModSaveData> DeserializeOverrideAction => (Il2CppSystem.Func<string, ModSaveData>)Capture;
    private ModSaveData Capture(string json) { ModuleStoragePersistence.CaptureLoadInput(json); return null; }
    private void OnDeserialize(ModSaveData ignored) => ModuleStoragePersistence.DeserializeNative();
    private ModSaveData OnSerialize() => ModuleStoragePersistence.SerializeNative();
}
