// SPDX-License-Identifier: GPL-3.0-or-later
// Custom save registration and serialization adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using Il2CppInterop.Runtime.Injection;
using RedLoader;
using global::Sons.Gameplay.GameSetup;
using global::Sons.Save;
using SonsSdk;

namespace Modstoke.Loader;

internal sealed class SdkSave : ISdkSaveDriver
{
    private static readonly SdkSave Driver = new();
    private static readonly object SyncRoot = new();
    private RedloaderModSerializer _serializer;
    private ISaveGameSerializer<ModSaveData> _nativeSerializer;
    // Last good payload per name for the current save, reused when a saveable throws.
    private readonly Dictionary<string, string> _lastPayloads = new(StringComparer.Ordinal);
    private uint? _lastPayloadSaveId;

    internal static ISdkSaveDriver Create() => Driver;

    public void Register<T>(ICustomSaveable<T> saveable)
    {
        ArgumentNullException.ThrowIfNull(saveable);
        var name = saveable.Name;
        lock (SyncRoot)
            SonsSaveTools.CustomSaveables.Add(name, new CustomRegisteredSerializers<T>(saveable));
    }

    public void Unregister<T>(ICustomSaveable<T> saveable)
    {
        ArgumentNullException.ThrowIfNull(saveable);
        lock (SyncRoot)
        {
            // The object's current name need not be its registered name.
            List<string> names = null;
            foreach (var entry in SonsSaveTools.CustomSaveables)
            {
                if (entry.Value.Is(saveable))
                    (names ??= new()).Add(entry.Key);
            }
            if (names != null)
                foreach (var name in names)
                    SonsSaveTools.CustomSaveables.Remove(name);
        }
    }

    private static Dictionary<string, ICustomRegisteredSerializer> CaptureRegistrations()
    {
        lock (SyncRoot)
            return new Dictionary<string, ICustomRegisteredSerializer>(SonsSaveTools.CustomSaveables);
    }

    private static bool IsCurrent(string name, ICustomRegisteredSerializer registration)
    {
        lock (SyncRoot)
            return SonsSaveTools.CustomSaveables.TryGetValue(name, out var current)
                && ReferenceEquals(current, registration);
    }

    public void Initialize()
    {
        // The carrier must exist before the generic native interface is initialized.
        ClassInjector.RegisterTypeInIl2Cpp<ModSaveData>();
        ClassInjector.RegisterTypeInIl2Cpp<RedloaderModSerializer>(new()
        {
            LogSuccess = true,
            Interfaces = new[] { typeof(ISaveGameSerializer<ModSaveData>) }
        });

        if (_serializer == null)
        {
            if (RedloaderModSerializer.Instance != null)
                throw new InvalidOperationException("The shared mod save serializer already has another owner.");
            _serializer = new RedloaderModSerializer();
            RedloaderModSerializer.Instance = _serializer;
        }
        // SaveGameManager retains weak references; keep the interface wrapper rooted too.
        _nativeSerializer ??= new ISaveGameSerializer<ModSaveData>(_serializer.Pointer);
        RegisterSerializer();
    }

    public void RegisterSerializer()
    {
        if (_serializer == null || _serializer.WasCollected || _nativeSerializer == null
            || _nativeSerializer.WasCollected || !ReferenceEquals(RedloaderModSerializer.Instance, _serializer))
            throw new InvalidOperationException("The shared mod save serializer is unavailable or its owner changed.");
        CheckName(SaveGameManager._serializerQueue);
        if (SaveGameManager.TryGetInstance(out var manager) && manager != null && !manager.WasCollected && manager)
            CheckName(manager._serializers);
        SaveGameManager.RegisterSerializer(_nativeSerializer);
    }

    private void CheckName(Il2CppSystem.Collections.Generic.List<IRegisteredSerializer> serializers)
    {
        if (serializers == null) return;
        for (var index = 0; index < serializers.Count; index++)
        {
            var entry = serializers[index];
            if (entry != null && !entry.WasCollected && entry.HasTarget
                && string.Equals(entry.Name, "Mod", StringComparison.Ordinal) && !entry.Is(_serializer))
                throw new InvalidOperationException("Another serializer uses the Mod save name.");
        }
    }

    public string Serialize<T>(CustomRegisteredSerializers<T> registration)
    {
        var value = registration.Serializer.Save();
        return registration.Serializer is ICustomJsonSaveable<T> codec
            ? codec.Serialize(value)
            : LoaderUtils.JsonSerialize(value);
    }

    public void Deserialize<T>(CustomRegisteredSerializers<T> registration, string json)
    {
        var data = registration.Serializer is ICustomJsonSaveable<T> codec
            ? codec.Deserialize(json)
            : LoaderUtils.JsonDeserialize<T>(json);
        registration.Serializer.Load(data);
    }

    public ModSaveData Serialize()
    {
        LogMessage(Color.Yellow, new string('=', 50));
        LogMessage(Color.Yellow, "SERIALIZING MOD DATA");

        var sw = Stopwatch.StartNew();
        var saves = new List<NamedSaveData>();
        // An unknown save never reuses earlier payloads.
        var saveId = TryGetSelectedSaveId();
        if (saveId == null || saveId != _lastPayloadSaveId)
        {
            _lastPayloads.Clear();
            _lastPayloadSaveId = saveId;
        }

        // This runs inside the native save callback; throwing would drop every mod's data.
        foreach (var (name, saveable) in CaptureRegistrations())
        {
            if (!IsCurrent(name, saveable))
                continue;
            try
            {
                var save = new NamedSaveData();
                save.Name = name;
                save.Data = saveable.Serialize();
                if (IsCurrent(name, saveable))
                {
                    saves.Add(save);
                    _lastPayloads[name] = save.Data;
                }
                LogMessage(Color.Orange, $"Serializing {name} took {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception e)
            {
                LogError($"Error serializing {name}", e);
                if (_lastPayloads.TryGetValue(name, out var previous))
                {
                    var save = new NamedSaveData();
                    save.Name = name;
                    save.Data = previous;
                    saves.Add(save);
                    LogMessage(Color.Orange, $"Kept the last good save data for {name}");
                }
            }

            sw.Restart();
        }

        var data = new ModSaveData(ModSaveJson.Serialize(saves));
        sw.Stop();
        LogMessage(Color.Orange, $"Finishing up took {sw.ElapsedMilliseconds}ms");
        LogMessage(Color.Yellow, new string('=', 50));
        return data;
    }

    public void Deserialize(ref string pendingData)
    {
        LogMessage(Color.Yellow, new string('=', 50));
        LogMessage(Color.Yellow, "DESERIALIZING MOD DATA");

        if (string.IsNullOrWhiteSpace(pendingData))
            throw new FormatException("The native mod save payload is missing.");

        var sw = Stopwatch.StartNew();
        var data = SaveGameManager.Deserialize<ModSaveData>(pendingData);
        if (data == null)
            throw new FormatException("The native mod save carrier is missing.");

        LogMessage(Color.Orange, $"SaveGameManager deserializing took {sw.ElapsedMilliseconds}ms");
        sw.Restart();

        List<NamedSaveData> saves;
        try
        {
            saves = ModSaveJson.Deserialize(data.ModData.Get());
            if (saves == null)
                throw new FormatException("The embedded mod save list is missing.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var save in saves)
            {
                if (save == null || save.Name == null)
                    throw new FormatException("The embedded mod save list contains an unnamed entry.");
                if (!names.Add(save.Name))
                    throw new FormatException("The embedded mod save list contains duplicate names.");
                if (string.IsNullOrWhiteSpace(save.Data))
                    throw new FormatException("The embedded mod save list contains a missing payload.");
            }
        }
        catch (Exception e)
        {
            LogError("Error in deserializing mod data", e);
            throw;
        }

        _lastPayloads.Clear();
        foreach (var save in saves)
            _lastPayloads[save.Name] = save.Data;
        _lastPayloadSaveId = TryGetSelectedSaveId();

        var registrations = CaptureRegistrations();
        // Preserve unparsed input, but never replay partially applied owner callbacks.
        pendingData = null;
        LogMessage(Color.Orange, $"Constructing mod list took {sw.ElapsedMilliseconds}ms");
        sw.Restart();
        List<Exception> errors = null;

        foreach (var save in saves)
        {
            if (registrations.TryGetValue(save.Name, out var saveable))
            {
                if (!IsCurrent(save.Name, saveable))
                    continue;
                try
                {
                    saveable.Deserialize(save.Data);
                    LogMessage(Color.Orange, $"Deserializing {save.Name} took {sw.ElapsedMilliseconds}ms");
                }
                catch (Exception e)
                {
                    (errors ??= new()).Add(e);
                    LogError($"Error deserializing {save.Name}", e);
                }

                sw.Restart();
                continue;
            }

            LogError($"Could not find saveable with name {save.Name}");
        }

        sw.Stop();
        LogMessage(Color.Yellow, new string('=', 50));
        if (errors != null)
            throw new AggregateException("One or more mod data load callbacks failed.", errors);
    }

    private static uint? TryGetSelectedSaveId()
    {
        try { return GameSetupManager.GetSelectedSaveId(); }
        catch (Exception) { return null; }
    }

    private static void LogMessage(Color color, string message)
    {
        try { RLog.Msg(color, message); }
        catch (Exception) { }
    }

    private static void LogError(string message, Exception error = null)
    {
        try { RLog.Error(error == null ? message : message + ": " + error); }
        catch (Exception) { }
    }
}
