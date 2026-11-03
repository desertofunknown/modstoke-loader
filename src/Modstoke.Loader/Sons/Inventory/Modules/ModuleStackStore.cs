// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine.SceneManagement;

namespace Modstoke.Sons
{
    /// <summary>Per-store limits. These bound data, not native inventory capacity or peak allocations.</summary>
    public sealed class ModuleStackLimits
    {
        public const int MaximumCount = 1000000;
        public const int MaximumRuns = 4096;
        public const int MaximumSerializedBytes = 4 * 1024 * 1024;
        public static ModuleStackLimits Default { get; } = new();

        public ModuleStackLimits(int maxCount = MaximumCount, int maxRuns = MaximumRuns,
            int maxSerializedBytes = MaximumSerializedBytes)
        {
            if (maxCount is < 1 or > MaximumCount) throw new ArgumentOutOfRangeException(nameof(maxCount));
            if (maxRuns is < 1 or > MaximumRuns) throw new ArgumentOutOfRangeException(nameof(maxRuns));
            if (maxSerializedBytes is < 256 or > MaximumSerializedBytes)
                throw new ArgumentOutOfRangeException(nameof(maxSerializedBytes));
            MaxCount = maxCount;
            MaxRuns = maxRuns;
            MaxSerializedBytes = maxSerializedBytes;
        }

        public int MaxCount { get; }
        public int MaxRuns { get; }
        public int MaxSerializedBytes { get; }
    }

    /// <summary>A consecutive run of identical immutable item data; not native item custody.</summary>
    public sealed class ModuleStackRun
    {
        internal ModuleStackRun(ItemModuleSnapshot snapshot, int count)
        {
            Snapshot = snapshot;
            Count = count;
        }

        public ItemModuleSnapshot Snapshot { get; }
        public int Count { get; }
    }

    /// <summary>
    /// Immutable managed readback, also available to clients through TryParseSnapshot.
    /// Runs are oldest/bottom first, newest/top last. Does not prove a current world,
    /// trusted sender, native ownership, running module timers or live replication.
    /// </summary>
    public sealed class ModuleStackSnapshot
    {
        private readonly string _json;

        internal ModuleStackSnapshot(long revision, int totalCount,
            ModuleStackRun[] runs, string json, int serializedByteCount)
        {
            Revision = revision;
            TotalCount = totalCount;
            Runs = Array.AsReadOnly(runs);
            _json = json;
            SerializedByteCount = serializedByteCount;
        }

        public int Version => ModuleStackStore.FormatVersion;
        public long Revision { get; }
        public int TotalCount { get; }
        public IReadOnlyList<ModuleStackRun> Runs { get; }
        public int SerializedByteCount { get; }
        public string ExportJson() => _json;
    }

    /// <summary>
    /// Opt-in mod-owned LIFO data storage, not an ItemInstanceManager replacement.
    /// No native items are retained, removed, created, granted or cloned. Push/pop
    /// change only data; a caller must separately establish and transact native custody.
    /// No save registration, lifecycle hooks, migration or transport is installed.
    /// Use one store per independently ordered stack; mixed item IDs share one LIFO order.
    /// </summary>
    public sealed partial class ModuleStackStore
    {
        public const int FormatVersion = 1;
        private readonly object _gate = new();
        private readonly WorldBinding _world;
        private readonly bool _localOwner;
        private ModuleStackSnapshot _state;
        private bool _expired;

        private ModuleStackStore(WorldBinding world, ModuleStackLimits limits, bool localOwner = false)
        {
            _world = world;
            _localOwner = localOwner;
            Limits = limits;
            _state = CreateSnapshot(0, 0, Array.Empty<ModuleStackRun>(), limits);
        }

        public ModuleStackLimits Limits { get; }

        /// <summary>Any-thread immutable data readback. May outlive its world; not a liveness check.</summary>
        public ModuleStackSnapshot ReadSnapshot()
        {
            lock (_gate) return _state;
        }

        /// <summary>
        /// Requires the initialized Unity lifecycle thread and a loaded game scene, offline
        /// or on the Bolt server. Scene events, before-load count and socket identity bind
        /// this store's lifetime. Create after scene initialization; any subsequent scene
        /// publication conservatively expires it. Client parsing creates no mutable store.
        /// </summary>
        public static bool TryCreateHost(out ModuleStackStore store, out string detail,
            ModuleStackLimits limits = null)
        {
            store = null;
            try
            {
                var world = CaptureWorld();
                var created = new ModuleStackStore(world, limits ?? ModuleStackLimits.Default);
                created.RequireCurrent();
                store = created;
                detail = "Host data store created; no native items or lifecycle hooks changed.";
                return true;
            }
            catch (Exception error) { detail = error.Message; return false; }
        }

        /// <summary>
        /// Adds data at the top, merging only the immediately preceding exact snapshot.
        /// Count must be positive. Expected revision belongs to this exact store instance.
        /// Success says nothing about removal of any native source items.
        /// </summary>
        public bool TryPush(ItemModuleSnapshot snapshot, int count, long expectedRevision,
            out string detail)
        {
            lock (_gate)
            {
                try
                {
                    RequireMutation(expectedRevision);
                    Require(snapshot != null && count > 0, "A snapshot and positive count are required.");
                    Require(count <= Limits.MaxCount - _state.TotalCount, "The store count limit would be exceeded.");
                    var runs = new List<ModuleStackRun>(_state.Runs);
                    if (runs.Count > 0 && runs[^1].Snapshot.SameState(snapshot))
                        runs[^1] = new(snapshot, runs[^1].Count + count);
                    else
                    {
                        Require(runs.Count < Limits.MaxRuns, "The store run limit would be exceeded.");
                        runs.Add(new(snapshot, count));
                    }
                    var next = CreateSnapshot(_state.Revision + 1, _state.TotalCount + count, runs.ToArray(), Limits);
                    RequireMutation(expectedRevision);
                    _state = next;
                    detail = "Snapshot data pushed; native custody is unchanged.";
                    return true;
                }
                catch (Exception error) { detail = error.Message; return false; }
            }
        }

        /// <summary>
        /// Removes exactly count entries or nothing. Returned runs are top-first in pop
        /// order and may split the last retained run. Does not apply, create or grant items.
        /// Failed validation, stale revisions and exhausted limits leave data unchanged.
        /// </summary>
        public bool TryPop(int count, long expectedRevision,
            out IReadOnlyList<ModuleStackRun> removed, out string detail)
        {
            removed = Array.Empty<ModuleStackRun>();
            lock (_gate)
            {
                try
                {
                    RequireMutation(expectedRevision);
                    Require(count > 0 && count <= _state.TotalCount, "Pop requires a positive available count.");
                    var runs = new List<ModuleStackRun>(_state.Runs);
                    var taken = new List<ModuleStackRun>();
                    int remaining = count;
                    while (remaining > 0)
                    {
                        var top = runs[^1];
                        int amount = Math.Min(remaining, top.Count);
                        taken.Add(new(top.Snapshot, amount));
                        if (amount == top.Count) runs.RemoveAt(runs.Count - 1);
                        else runs[^1] = new(top.Snapshot, top.Count - amount);
                        remaining -= amount;
                    }
                    var result = Array.AsReadOnly(taken.ToArray());
                    var next = CreateSnapshot(_state.Revision + 1, _state.TotalCount - count, runs.ToArray(), Limits);
                    RequireMutation(expectedRevision);
                    _state = next;
                    removed = result;
                    detail = "Snapshot data popped top-first; native custody is unchanged.";
                    return true;
                }
                catch (Exception error) { detail = error.Message; return false; }
            }
        }

        /// <summary>
        /// Explicit one-time initial state restore into a pristine, current host store only.
        /// Even a store emptied by pop cannot be imported over. Entire input is validated
        /// before publication; success advances to imported revision + 1, never rolls back.
        /// Caller must bind the payload to its mod, container and exact world/save slot.
        /// Parsing is not authentication and import does not establish native custody.
        /// </summary>
        public bool TryImportInitial(string json, long expectedRevision, out string detail)
        {
            lock (_gate)
            {
                try
                {
                    RequireMutation(expectedRevision);
                    Require(_state.Revision == 0 && _state.TotalCount == 0,
                        "Initial import requires a pristine store that has never been mutated or imported.");
                    var imported = ParseSnapshot(json, Limits);
                    Require(imported.Revision < long.MaxValue, "The imported revision is exhausted.");
                    var runs = new ModuleStackRun[imported.Runs.Count];
                    for (int i = 0; i < runs.Length; i++) runs[i] = imported.Runs[i];
                    var next = CreateSnapshot(imported.Revision + 1, imported.TotalCount, runs, Limits);
                    RequireMutation(expectedRevision);
                    _state = next;
                    detail = "Initial snapshot data imported; world/save binding and native custody remain caller-owned.";
                    return true;
                }
                catch (Exception error) { detail = error.Message; return false; }
            }
        }

        /// <summary>
        /// Current-world, host-thread export for caller-owned save registration. Does not
        /// write files or pair atomically with a native save. ReadSnapshot().ExportJson()
        /// instead exports historical managed data without a native liveness check.
        /// </summary>
        public bool TryExportJson(out string json, out string detail)
        {
            json = null;
            lock (_gate)
            {
                try
                {
                    RequireCurrent();
                    json = _state.ExportJson();
                    detail = "Current host data exported; not proof of native persistence or replication.";
                    return true;
                }
                catch (Exception error) { detail = error.Message; return false; }
            }
        }

        private void RequireMutation(long expectedRevision)
        {
            RequireCurrent();
            Require(_reservation == null, "A native transfer has reserved this store.");
            Require(expectedRevision == _state.Revision, "The expected store revision is stale.");
            Require(_state.Revision < long.MaxValue, "The store revision is exhausted.");
        }

        private void RequireCurrent()
        {
            // Wrong-thread calls must not read native state or expire an otherwise live store.
            Require(Crafting.OnCraftExecutionThread,
                "Module stack stores require the initialized Unity lifecycle thread.");
            Require(!_expired, "The store's world lifetime has expired; create a new host store.");
            try
            {
                Require(CaptureWorld(_localOwner) == _world,
                    "The store's scene, load or Bolt session changed; create a new host store.");
            }
            catch
            {
                _expired = true;
                throw;
            }
        }

        private static WorldBinding CaptureWorld(bool localOwner = false)
        {
            Require(Crafting.OnCraftExecutionThread,
                "Module stack stores require the initialized Unity lifecycle thread.");
            var state = GameRuntime.State;
            Require(GameRuntime.IsGameScene && (localOwner || !BoltNetwork.isClient),
                "A loaded host/offline game scene is required; clients cannot mutate module stores.");
            var scene = SceneManager.GetSceneByName(state.SceneName);
            Require(scene.IsValid() && scene.isLoaded, "The bound game scene is not loaded.");
            bool networked = BoltNetwork.isRunning;
            var socket = BoltCore._udpSocket;
            Require(socket == null || !socket.WasCollected, "The Bolt socket is unavailable.");
            if (networked)
                Require((BoltNetwork.isServer || localOwner && BoltNetwork.isClient) && socket != null && socket.Pointer != IntPtr.Zero
                    && socket.State != UdpKit.UdpSocketState.Shutdown, "A running host Bolt socket is required.");
            else
                Require(socket == null || socket.State == UdpKit.UdpSocketState.Shutdown,
                    "Bolt is transitioning; create or use the store in a settled session.");
            if (localOwner)
            {
                Require(TheForest.Utils.LocalPlayer.IsInWorld
                    && Crafting.CraftExecutionLive(TheForest.Utils.LocalPlayer._instance)
                    && Crafting.CraftExecutionLive(TheForest.Utils.LocalPlayer.Inventory),
                    "The local inventory owner is not in-world.");
                if (networked)
                {
                    var owner = TheForest.Utils.LocalPlayer.Entity;
                    Require(owner is not null && !owner.WasCollected && owner && owner.isAttached && owner.isOwner,
                        "A locally owned native player is required.");
                }
            }
            return new(SaveLifecycle.State.BeforeLoadCount, state.SceneEventCount,
                scene.handle, networked, socket?.Pointer ?? IntPtr.Zero,
                localOwner ? TheForest.Utils.LocalPlayer._instance.Pointer : IntPtr.Zero,
                localOwner ? TheForest.Utils.LocalPlayer.Inventory.Pointer : IntPtr.Zero);
        }

        private readonly record struct WorldBinding(int BeforeLoadCount, int SceneEventCount,
            int SceneHandle, bool Networked, IntPtr Socket, IntPtr LocalPlayer, IntPtr Inventory);

        private static void Require(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException(detail);
        }
    }
}
