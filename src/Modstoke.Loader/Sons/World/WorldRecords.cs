// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Modstoke.Loader;
using global::Sons.Gameplay.GameSetup;
using global::Sons.Save;

namespace Modstoke.Sons;

/// <summary>Server-owned JSON records carried inside the game's world save.</summary>
public static partial class WorldRecords
{
    public const int MaxRecordBytes = 524288;
    public const int MaxDocumentBytes = 4194304;
    public const int MaxRecords = 128;
    private const int SchemaVersion = 1;
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 32 };
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 32 };
    private static int _ownerThread;
    private static long _generation;
    private static WorldRecordContext _current;
    private static Dictionary<string, string> _records = new(StringComparer.Ordinal);
    private static long _revision;
    private static IntPtr _manager;
    private static bool _gameStarted;
    private static bool _faulted;
    private static string _preservedData;
    private static string _lastProblem;
    private static WorldRecordLoad _load;
    private static WorldRecordSave _save;

    /// <summary>Last diagnostic on the owner thread. No player or record contents are included.</summary>
    public static string LastProblem => _lastProblem;

    /// <summary>Last native serialization observation; it is not a disk-write receipt.</summary>
    public static WorldRecordSaveStatus LastSaveStatus { get; private set; }

    public static bool TryGetCurrent(out WorldRecordContext context,
        out WorldRecordStatus status)
    {
        context = null;
        status = CheckThread();
        if (status != WorldRecordStatus.Success) return false;
        try
        {
            if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
            {
                status = WorldRecordStatus.NotServer;
                return false;
            }
            if (!SaveGameManager.TryGetInstance(out var manager) || manager is null || manager.WasCollected || !manager)
            {
                status = WorldRecordStatus.WorldUnavailable;
                return false;
            }
            if (_manager != IntPtr.Zero && manager.Pointer != _manager)
                Reset("The save manager changed.");
            if (_load != null || manager._loadingInProgress)
            {
                status = WorldRecordStatus.Loading;
                return false;
            }
            if (_faulted)
            {
                status = WorldRecordStatus.InvalidSavedData;
                return false;
            }
            if (_current == null && _gameStarted && !GameSetupManager.GameLoadedFromSave()
                && GameSetupManager.GetSaveGameType() != SaveGameType.MultiplayerClient)
            {
                EnsureRegistered();
                _manager = manager.Pointer;
                _current = new WorldRecordContext(Guid.NewGuid(), _generation);
            }
            if (_current == null)
            {
                status = WorldRecordStatus.WorldUnavailable;
                return false;
            }
            context = _current;
            status = WorldRecordStatus.Success;
            return true;
        }
        catch (Exception error)
        {
            ReportProblem("World record context is unavailable", error);
            status = WorldRecordStatus.Unavailable;
            return false;
        }
    }

    /// <summary>Returns a detached JSON string. Callers own authorization and payload schema validation.</summary>
    public static WorldRecordStatus ReadJson(WorldRecordContext context,
        string owner, string name, out string json)
    {
        json = null;
        var status = CheckContext(context);
        if (status != WorldRecordStatus.Success) return status;
        if (!TryKey(owner, name, out var key)) return WorldRecordStatus.InvalidRecord;
        return _records.TryGetValue(key, out json)
            ? WorldRecordStatus.Success : WorldRecordStatus.NotFound;
    }

    /// <summary>
    /// Replaces a record in memory. The next native world save captures it; success does not mean persisted.
    /// The SDK provides isolation by current world lifetime, not permissions between trusted in-process mods.
    /// </summary>
    public static WorldRecordStatus WriteJson(WorldRecordContext context,
        string owner, string name, string json)
    {
        var status = CheckContext(context);
        if (status != WorldRecordStatus.Success) return status;
        if (!TryKey(owner, name, out var key) || !ValidJson(json)) return WorldRecordStatus.InvalidRecord;
        if ((!_records.ContainsKey(key) && _records.Count == MaxRecords) || _revision == long.MaxValue)
            return WorldRecordStatus.LimitReached;
        var candidate = new Dictionary<string, string>(_records, StringComparer.Ordinal) { [key] = json };
        if (Encoding.UTF8.GetByteCount(Encode(_current.WorldId, _revision + 1, candidate)) > MaxDocumentBytes)
            return WorldRecordStatus.LimitReached;
        _records = candidate;
        _revision++;
        return WorldRecordStatus.Success;
    }

    public static WorldRecordStatus Remove(WorldRecordContext context, string owner, string name)
    {
        var status = CheckContext(context);
        if (status != WorldRecordStatus.Success) return status;
        if (!TryKey(owner, name, out var key)) return WorldRecordStatus.InvalidRecord;
        if (!_records.ContainsKey(key)) return WorldRecordStatus.NotFound;
        if (_revision == long.MaxValue) return WorldRecordStatus.LimitReached;
        _records.Remove(key);
        _revision++;
        return WorldRecordStatus.Success;
    }

    internal static bool IsCurrent(WorldRecordContext context) =>
        CheckContext(context) == WorldRecordStatus.Success;

    private static WorldRecordStatus CheckThread() => _ownerThread == 0 || _serializer == null
        || !SaveLifecycle.State.HarmonyPatchesInstalled
        ? WorldRecordStatus.Unavailable
        : Environment.CurrentManagedThreadId != _ownerThread
            ? WorldRecordStatus.WrongThread : WorldRecordStatus.Success;

    private static WorldRecordStatus CheckContext(WorldRecordContext context)
    {
        if (!TryGetCurrent(out var current, out var status)) return status;
        return ReferenceEquals(context, current) && context.Generation == _generation
            ? WorldRecordStatus.Success : WorldRecordStatus.StaleWorld;
    }

    private static bool TryKey(string owner, string name, out string key)
    {
        key = null;
        if (!ValidId(owner) || !ValidId(name)) return false;
        key = owner + ":" + name;
        return true;
    }

    private static bool ValidId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 96) return false;
        foreach (var c in value)
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')) return false;
        return true;
    }

    private static bool ValidJson(string value)
    {
        if (value == null || value.Length > MaxRecordBytes || Encoding.UTF8.GetByteCount(value) > MaxRecordBytes) return false;
        try { using var document = JsonDocument.Parse(value, DocumentOptions); return true; }
        catch (JsonException) { return false; }
    }

    private static string Encode(Guid worldId, long revision, Dictionary<string, string> records) =>
        JsonSerializer.Serialize(new { Version = SchemaVersion, WorldId = worldId, Revision = revision, Records = records }, JsonOptions);

    private static void Decode(string json, out Guid worldId, out long revision, out Dictionary<string, string> records)
    {
        worldId = Guid.Empty;
        revision = 0;
        records = new Dictionary<string, string>(StringComparer.Ordinal);
        if (json == null || json.Length > MaxDocumentBytes || Encoding.UTF8.GetByteCount(json) > MaxDocumentBytes)
            throw new JsonException("World record document is missing or exceeds its limit.");
        using var document = JsonDocument.Parse(json, DocumentOptions);
        var root = document.RootElement;
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
        {
            var name = field.Name;
            if (!fields.Add(name)) throw new JsonException("Duplicate world record field.");
            switch (name)
            {
                case "Version":
                    if (field.Value.GetInt32() != SchemaVersion) throw new JsonException("Unsupported world record version.");
                    break;
                case "WorldId":
                    worldId = field.Value.GetGuid();
                    if (worldId == Guid.Empty) throw new JsonException("Missing world identity.");
                    break;
                case "Revision":
                    revision = field.Value.GetInt64();
                    if (revision < 0) throw new JsonException("Invalid world revision.");
                    break;
                case "Records":
                    foreach (var record in field.Value.EnumerateObject())
                    {
                        var key = record.Name;
                        var parts = key.Split(':');
                        var value = record.Value.GetString();
                        if (records.Count == MaxRecords || parts.Length != 2
                            || !ValidId(parts[0]) || !ValidId(parts[1])
                            || !ValidJson(value) || !records.TryAdd(key, value))
                            throw new JsonException("Invalid world record.");
                    }
                    break;
                default: throw new JsonException("Unknown world record field.");
            }
        }
        if (fields.Count != 4) throw new JsonException("Incomplete world record document.");
    }

    private static void Reset(string reason)
    {
        _current = null;
        _records = new Dictionary<string, string>(StringComparer.Ordinal);
        _revision = 0;
        _manager = IntPtr.Zero;
        _gameStarted = false;
        _faulted = false;
        _preservedData = null;
        _load = null;
        _save = null;
        _lastProblem = reason;
        LastSaveStatus = WorldRecordSaveStatus.None;
        if (_generation < long.MaxValue) _generation++;
        else _ownerThread = 0;
    }

    private static void ReportProblem(string operation, Exception error)
    {
        _lastProblem = operation + " (" + error.GetType().Name + ").";
        try { RuntimeEntryPoint.LogSource?.LogWarning(_lastProblem); }
        catch (Exception) { }
    }
}

/// <summary>One live world lifetime. Restoring the same save creates a different handle.</summary>
public sealed class WorldRecordContext
{
    internal WorldRecordContext(Guid worldId, long generation) { WorldId = worldId; Generation = generation; }
    public Guid WorldId { get; }
    public long Generation { get; }
    public bool IsCurrent => WorldRecords.IsCurrent(this);
}

public enum WorldRecordStatus
{
    Success, NotFound, Unavailable, WrongThread, NotServer, WorldUnavailable, Loading,
    InvalidSavedData, StaleWorld, InvalidRecord, LimitReached
}

public enum WorldRecordSaveStatus
{
    None,
    /// <summary>The native serializer captured records. Native disk completion remains unconfirmed.</summary>
    Serialized,
    /// <summary>Unknown or invalid record JSON was retained unchanged instead of overwriting it.</summary>
    Preserved,
    /// <summary>Saving was refused because a world record could not be retained safely.</summary>
    Refused
}
