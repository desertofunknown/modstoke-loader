// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modstoke.Loader;
using global::Sons.Gameplay.GameSetup;
using global::Sons.Inventory;

namespace Modstoke.Sons;

/// <summary>
/// Opt-in per-player compact module inventory. Native item ownership stays with its local
/// player; the host approves transitions and mirrors revisions, not remote native objects.
/// Client reports are not anti-cheat evidence. This is not a shared world chest.
/// </summary>
public static partial class ModuleStorage
{
    public const ulong ProtocolCapability = 1UL << 62;
    public const int MaximumContainers = 16;
    public const int MaximumSaveBytes = 2 * 1024 * 1024;
    internal static readonly ModuleStackLimits Limits = new(maxSerializedBytes: 256 * 1024);
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };
    private static readonly Dictionary<string, ModuleStorageContainer> Containers = new(StringComparer.Ordinal);
    private static Dictionary<string, ModuleStackSnapshot> _saved = new(StringComparer.Ordinal);
    private static int _thread;
    private static bool _enabled, _loading, _loadComplete, _saving;
    private static string _fault;
    private static Guid _profile = Guid.NewGuid(), _epoch = Guid.NewGuid();
    private static Action<string> _warning;
    private static ModuleStorageTransport _transport;
    internal static bool OnThread => _thread != 0 && Environment.CurrentManagedThreadId == _thread;
    internal static Guid CurrentEpoch => _epoch;
    internal static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    internal static void Initialize(HarmonySupportSnapshot support, Action<string> warning)
    {
        if (_thread != 0) return;
        _thread = Environment.CurrentManagedThreadId;
        _warning = warning;
        ModuleStoragePersistence.Initialize(support, warning);
        SonsSdk.SdkEvents.OnWorldExited.Subscribe(ResetForLoad);
        SonsSdk.SdkEvents.OnGameStart.Subscribe(() =>
        {
            if (OnThread && _fault == null && !GameSetupManager.GameLoadedFromSave())
            { _loading = false; _loadComplete = true; }
        });
        ControlTransport.Closed += peer => RetirePeer(peer, "Connection closed.");
    }

    /// <summary>
    /// Call on every participant before Bolt starts. Adds the loader protocol requirement
    /// without replacing existing package requirements. Does not enable player migration.
    /// Offline storage needs no network selection. Runtime installation remains unchanged.
    /// </summary>
    public static ControlResult EnableForNextStart()
    {
        if (!OnThread) return ControlResult.WrongThread;
        var result = ClientEnrollment.RequireModuleStorageForNextStart();
        if (result != ControlResult.Success) return result;
        result = Datagrams.SelectForNextStart(true);
        if (result != ControlResult.Success) return result;
        _enabled = true;
        _transport ??= new ModuleStorageTransport(Receive, RetirePeer);
        return result;
    }

    /// <summary>Register the same stable mod/container IDs on host and clients during mod startup.</summary>
    public static ModuleStorageContainer RegisterContainer(string modId, string containerId)
    {
        Require(OnThread, "Module inventory registration requires the initialized Unity thread.");
        string key = Key(modId, containerId);
        if (Containers.TryGetValue(key, out var existing)) return existing;
        Require(!_saving && !_loading && Containers.Count < MaximumContainers, "Module container registration is unavailable.");
        var container = new ModuleStorageContainer(key);
        Containers.Add(key, container);
        return container;
    }

    public static bool TryOpen(string modId, string containerId, out ModuleStorageContainer container,
        out string detail)
    {
        container = null;
        try
        {
            var registered = RegisterContainer(modId, containerId);
            EnsureLocal(registered);
            container = registered;
            detail = "Local module inventory opened.";
            return true;
        }
        catch (Exception error) { detail = error.Message; return false; }
    }

    internal static void EnsureLocal(ModuleStorageContainer container)
    {
        Require(OnThread && !_loading && !_saving, "Module inventory is loading, saving or on the wrong thread.");
        Require(_fault == null, _fault ?? "Module inventory is unavailable.");
        Require(ModuleStoragePersistence.Ready, "The native module inventory serializer is unavailable.");
        Require(GameRuntime.IsGameScene && TheForest.Utils.LocalPlayer.IsInWorld, "A local player in a loaded world is required.");
        if (!_loadComplete)
        {
            Require(!GameSetupManager.GameLoadedFromSave(), "The native load was not observed; reload before using module inventory.");
            _loadComplete = true;
        }
        Require(ModuleStoragePersistence.Select(), "Native persistence must be active before module inventory can be used.");
        if (BoltNetwork.isRunning)
            Require(_enabled && ClientEnrollment.ModuleStorageSelected,
                "Select loader-required module storage before starting multiplayer on every participant.");
        if (container.Store == null)
        {
            _saved.TryGetValue(container.Key, out var restored);
            container.Store = ModuleStackStore.CreateLocalOwner(restored, Limits);
        }
        Require(container.Store.TryExportJson(out _, out var detail), detail);
    }

    internal static bool Begin(ModuleStorageContainer container, bool deposit,
        ModuleStorageNative.NativeTransfer transfer,
        out ModuleStorageOperation operation, out string detail)
    {
        operation = null;
        ModuleStackStore.PreparedChange change = null;
        try
        {
            Require(container.CanBegin(out detail), detail);
            change = container.Store.PrepareTransfer(deposit, transfer.Snapshot, container.Store.ReadSnapshot().Revision);
            // Reserve aggregate save capacity before any native action can occur.
            EncodeSave(container.Key, change.After);
            operation = new ModuleStorageOperation(container, deposit, transfer, change, _epoch);
            container.Pending = operation;
            if (!BoltNetwork.isRunning || BoltNetwork.isServer)
                Execute(operation);
            else
            {
                var peer = ServerPeer();
                operation.Peer = peer;
                Send(peer, new Wire("prepare", _epoch, container.Key, operation.Id,
                    change.Before.Revision, deposit, transfer.Snapshot, null));
            }
            detail = operation.Detail;
            return operation.State is ModuleStorageOperationState.Pending or ModuleStorageOperationState.Completed;
        }
        catch (Exception error)
        {
            change?.Cancel();
            if (operation != null)
            {
                operation.State = ModuleStorageOperationState.Refused;
                operation.Detail = error.Message;
                container.Pending = null;
            }
            detail = error.Message;
            return false;
        }
    }

    private static void Execute(ModuleStorageOperation operation)
    {
        bool attempted = false;
        try
        {
            Require(!_saving && !_loading && _fault == null && operation.Epoch == _epoch,
                "The transfer's world or save boundary changed.");
            operation.Change.Validate();
            if (!operation.Transfer.TryExecute(out attempted, out var detail))
                throw new InvalidOperationException(detail);
            // From this point neither a callback failure nor a lost receipt can authorize a replay.
            attempted = true;
            operation.Change.Commit();
            operation.LocalCommitted = true;
            operation.Item = operation.Transfer.Item;
            operation.Detail = "Native transfer and local module state committed.";
            if (operation.Peer == null) Complete(operation);
            else Send(operation.Peer, OperationWire("commit", operation));
        }
        catch (Exception error)
        {
            if (!attempted && !operation.LocalCommitted)
            {
                operation.Change.Cancel();
                operation.State = ModuleStorageOperationState.Refused;
                operation.Container.Pending = null;
            }
            else
            {
                if (!operation.LocalCommitted) _fault = "Native module transfer is uncertain: " + error.Message;
                operation.State = ModuleStorageOperationState.Unconfirmed;
            }
            operation.Detail = error.Message + " No automatic retry or replacement was performed.";
            if (!attempted && !operation.LocalCommitted && operation.Peer != null)
            {
                try { Send(operation.Peer, OperationWire("cancel", operation)); }
                catch (Exception sendError) { RetirePeer(operation.Peer, sendError.Message); }
            }
        }
    }

    private static void Complete(ModuleStorageOperation operation)
    {
        operation.State = ModuleStorageOperationState.Completed;
        operation.Detail = "Native transfer and module state committed" + (operation.Peer == null ? "." : "; host revision acknowledged.");
        operation.Container.Pending = null;
    }

    internal static string CaptureSave()
    {
        Require(OnThread && !_loading && !_saving && _fault == null, _fault ?? "Module inventory has an overlapping save/load.");
        foreach (var container in Containers.Values)
            if (container.Pending is { Peer: not null } pending && !pending.Peer.IsCurrent)
                RetirePeer(pending.Peer, "The connection ended before saving.");
        Require(!Containers.Values.Any(c => c.Pending != null && !c.Pending.LocalCommitted),
            "Finish the pending module inventory transfer before saving.");
        foreach (var container in Containers.Values)
            container.Store?.ValidateOwnerSave();
        string json = EncodeSave(null, null);
        _saving = true;
        return json;
    }

    internal static void EndSave() { if (OnThread) _saving = false; }

    internal static void ResetForLoad()
    {
        if (!OnThread) return;
        foreach (var container in Containers.Values)
        {
            if (container.Pending != null)
            {
                container.Pending.State = ModuleStorageOperationState.Unconfirmed;
                container.Pending.Detail = "The world ended before the transfer was acknowledged; do not replay it.";
            }
            container.Store = null;
            container.Pending = null;
        }
        _transport?.Dispose();
        _transport = null;
        Remotes.Clear();
        Announced.Clear();
        RetiredPeers.Clear();
        Outgoing.Clear();
        _outgoingBytes = 0;
        _saved = new(StringComparer.Ordinal);
        _profile = Guid.NewGuid();
        _epoch = Guid.NewGuid();
        _loading = true;
        _loadComplete = false;
        _saving = false;
        _fault = null;
    }

    internal static void StageLoad(string json)
    {
        Require(OnThread && _loading, "No module inventory load is active.");
        Require(json != null && json.Length <= MaximumSaveBytes && Utf8.GetByteCount(json) <= MaximumSaveBytes,
            "Module inventory save is missing or too large.");
        var data = JsonSerializer.Deserialize<SaveDocument>(json, JsonOptions);
        Require(data != null && data.Version == 1 && data.Profile != Guid.Empty && data.Containers != null
            && data.Containers.Count <= MaximumContainers, "Unsupported module inventory save.");
        var staged = new Dictionary<string, ModuleStackSnapshot>(StringComparer.Ordinal);
        foreach (var entry in data.Containers)
        {
            ValidateKey(entry.Key);
            Require(ModuleStackStore.TryParseSnapshot(entry.Value, out var snapshot, out var detail, Limits), detail);
            staged.Add(entry.Key, snapshot);
        }
        _profile = data.Profile;
        _saved = staged;
    }

    internal static void CompleteLoad(bool success, bool absent)
    {
        if (!OnThread) return;
        _loading = false;
        _loadComplete = success;
        if (!success) _fault = "Module inventory load could not be verified; reload before using or saving it.";
        if (success && absent) _saved.Clear();
    }

    private static string EncodeSave(string replacementKey, ModuleStackSnapshot replacement)
    {
        var data = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in _saved) data.Add(entry.Key, entry.Value.ExportJson());
        foreach (var container in Containers.Values)
        {
            if (container.Store == null) continue;
            string value = container.Store.ReadSnapshot().ExportJson();
            if (replacementKey != null)
            {
                var candidate = container.Key == replacementKey ? replacement
                    : container.Pending is { LocalCommitted: false } pending ? pending.Change.After : null;
                if (candidate != null)
                {
                    string after = candidate.ExportJson();
                    // Reserve the larger escaped payload: other containers may independently cancel.
                    if (JsonEncodedText.Encode(after, JsonOptions.Encoder).EncodedUtf8Bytes.Length
                        > JsonEncodedText.Encode(value, JsonOptions.Encoder).EncodedUtf8Bytes.Length) value = after;
                }
            }
            data[container.Key] = value;
        }
        Require(data.Count <= MaximumContainers, "The module inventory container limit is exceeded.");
        var json = JsonSerializer.Serialize(new SaveDocument(1, _profile, data), JsonOptions);
        Require(Utf8.GetByteCount(json) <= MaximumSaveBytes, "The module inventory save byte limit is exceeded.");
        return json;
    }

    private static string Key(string modId, string containerId)
    {
        Require(ValidName(modId) && ValidName(containerId), "Use 1-64 ASCII letters, digits, '.', '_' or '-' for each stable container identity.");
        return modId + "/" + containerId;
    }
    private static bool ValidName(string value) => value is { Length: > 0 and <= 64 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    private static void ValidateKey(string key)
    {
        var split = key?.Split('/');
        Require(split is { Length: 2 } && ValidName(split[0]) && ValidName(split[1]), "Invalid module container identity.");
    }
    internal static void Require(bool condition, string detail)
    { if (!condition) throw new InvalidOperationException(detail); }

    private sealed record SaveDocument(int Version, Guid Profile, SortedDictionary<string, string> Containers);
}

public enum ModuleStorageOperationState { Pending, Completed, Refused, Unconfirmed }

/// <summary>A single transfer receipt. Pending/Unconfirmed must never be automatically resubmitted.</summary>
public sealed class ModuleStorageOperation
{
    internal ModuleStorageOperation(ModuleStorageContainer container, bool deposit,
        ModuleStorageNative.NativeTransfer transfer, ModuleStackStore.PreparedChange change, Guid epoch)
    { Container = container; Deposit = deposit; Transfer = transfer; Change = change; Epoch = epoch; }
    public Guid Id { get; } = Guid.NewGuid();
    public ModuleStorageOperationState State { get; internal set; }
    public string Detail { get; internal set; } = "Waiting for host approval.";
    public ItemInstance Item { get; internal set; }
    internal ModuleStorageContainer Container { get; }
    internal readonly bool Deposit;
    internal readonly Guid Epoch;
    internal readonly ModuleStorageNative.NativeTransfer Transfer;
    internal readonly ModuleStackStore.PreparedChange Change;
    internal readonly double Started = ModuleStorage.Now;
    internal bool LocalCommitted;
    internal ControlPeer Peer;
}

public sealed partial class ModuleStorageContainer
{
    internal ModuleStorageContainer(string key) => Key = key;
    public string Key { get; }
    internal ModuleStackStore Store;
    internal ModuleStorageOperation Pending;

    public ModuleStackSnapshot ReadSnapshot()
    { ModuleStorage.EnsureLocal(this); return Store.ReadSnapshot(); }

    internal bool CanBegin(out string detail)
    {
        try
        {
            ModuleStorage.EnsureLocal(this);
            ModuleStorage.Require(Pending == null, "A transfer is already pending or requires recovery.");
            ModuleStorage.Require(ModuleStorage.IsSynchronized(this), "The host has not synchronized this container yet.");
            detail = "";
            return true;
        }
        catch (Exception error) { detail = error.Message; return false; }
    }

    public bool TryDeposit(ItemInstance item, out ModuleStorageOperation operation, out string detail)
    {
        operation = null;
        if (!CanBegin(out detail) || !ModuleStorageNative.TryPrepareDeposit(item, out var transfer, out detail)) return false;
        return ModuleStorage.Begin(this, true, transfer, out operation, out detail);
    }

    public bool TryWithdraw(out ModuleStorageOperation operation, out string detail)
    {
        operation = null;
        if (!CanBegin(out detail)) return false;
        var state = Store.ReadSnapshot();
        if (state.Runs.Count == 0) { detail = "Module storage is empty."; return false; }
        if (!ModuleStorageNative.TryPrepareWithdraw(state.Runs[^1].Snapshot, out var transfer, out detail)) return false;
        return ModuleStorage.Begin(this, false, transfer, out operation, out detail);
    }
}
