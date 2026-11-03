// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Modstoke.Sons;

public static partial class ModuleStorage
{
    private const double OperationTimeout = 180;
    private const int MaximumRemoteBytes = 32 * 1024 * 1024;
    private static readonly Dictionary<(Guid Peer, string Key), Remote> Remotes = new();
    private static readonly Dictionary<string, Announcement> Announced = new(StringComparer.Ordinal);
    private static readonly Dictionary<Guid, ControlPeer> RetiredPeers = new();
    private static readonly Dictionary<Guid, Outbox> Outgoing = new();
    private static int _outgoingBytes;
    private static double _nextPeerCheck;

    /// <summary>Host-only readback of an exact current peer's reported inventory, not trusted native custody.</summary>
    public static bool TryReadPeer(ControlPeer peer, string modId, string containerId,
        out ModuleStackSnapshot snapshot)
    {
        snapshot = null;
        if (!OnThread || !BoltNetwork.isServer || peer == null || !peer.IsCurrent) return false;
        if (!Remotes.TryGetValue((peer.PeerId, Key(modId, containerId)), out var remote)
            || !ReferenceEquals(remote.Peer, peer)) return false;
        snapshot = remote.Snapshot;
        return true;
    }

    internal static bool IsSynchronized(ModuleStorageContainer container)
    {
        if (!BoltNetwork.isRunning || BoltNetwork.isServer) return true;
        return Announced.TryGetValue(container.Key, out var state) && state.Acknowledged
            && state.Peer.IsCurrent && state.Epoch == _epoch && state.Revision == container.Store.ReadSnapshot().Revision;
    }

    private static ControlPeer ServerPeer()
    {
        ControlPeer found = null;
        foreach (var peer in ControlTransport.GetPeers())
            if (peer.ServerSession == null && Eligible(peer))
            {
                Require(found == null, "The server connection is ambiguous.");
                found = peer;
            }
        Require(found != null, "No compatible enrolled module-storage server is available.");
        return found;
    }

    private static bool Eligible(ControlPeer peer)
        => peer != null && peer.IsCurrent && !RetiredPeers.ContainsKey(peer.PeerId)
            && ClientEnrollment.ModuleStorageSelected
            && ClientEnrollment.AllowsGameTraffic(peer)
            && ClientEnrollment.TryRead(peer, out var enrollment) == EnrollmentReadStatus.Success
            && (enrollment.RequiredCapabilities & ProtocolCapability) != 0
            && (enrollment.OfferedCapabilities & ProtocolCapability) != 0;

    internal static void Tick()
    {
        try { TickCore(); }
        catch (Exception error)
        {
            _fault = "Module inventory processing stopped: " + error.Message;
            _transport?.Dispose();
            _transport = null;
            try { _warning?.Invoke(_fault); }
            catch (Exception) { }
        }
    }

    private static void TickCore()
    {
        if (!OnThread || !_enabled) return;
        if (_transport == null && _fault == null) _transport = new ModuleStorageTransport(Receive, RetirePeer);
        if (_transport == null) return;
        _transport.Tick();
        FlushOutgoing();
        if (Now < _nextPeerCheck) return;
        _nextPeerCheck = Now + .25;
        foreach (var entry in RetiredPeers.Where(entry => !entry.Value.IsCurrent).ToArray()) RetiredPeers.Remove(entry.Key);
        foreach (var remote in Remotes.Values.ToArray())
        {
            if (!remote.Peer.IsCurrent) { RetirePeer(remote.Peer, "Connection expired."); continue; }
            if (remote.Pending != null && Now - remote.Pending.Started > OperationTimeout)
            { RetirePeer(remote.Peer, "The reserved module transfer expired."); }
        }
        foreach (var container in Containers.Values)
        {
            if (container.Pending is { } pending && Now - pending.Started > OperationTimeout
                && pending.State == ModuleStorageOperationState.Pending)
            {
                if (pending.Peer != null) RetirePeer(pending.Peer, "Module transfer acknowledgement timed out.");
            }
        }
        if (_loading || _saving || _fault != null || !BoltNetwork.isClient || !GameRuntime.IsGameScene
            || !TheForest.Utils.LocalPlayer.IsInWorld) return;
        ControlPeer server;
        try { server = ServerPeer(); }
        catch (InvalidOperationException) { return; }
        foreach (var container in Containers.Values)
        {
            try
            {
                EnsureLocal(container);
                if (Announced.TryGetValue(container.Key, out var old) && ReferenceEquals(old.Peer, server)) continue;
                var snapshot = container.Store.ReadSnapshot();
                Send(server, new Wire("sync", _epoch, container.Key, Guid.Empty, snapshot.Revision, false, null, snapshot.ExportJson()));
                Announced[container.Key] = new(server, _epoch, snapshot.Revision);
            }
            catch (Exception error) { RetirePeer(server, error.Message); break; }
        }
    }

    private static void Receive(ControlPeer peer, byte[] payload)
    {
        if (!OnThread || !_enabled || !Eligible(peer)) return;
        try
        {
            Require(payload != null && payload.Length <= MaximumSaveBytes, "Module message exceeds its bound.");
            var wire = JsonSerializer.Deserialize<Wire>(payload, JsonOptions);
            Require(wire != null && wire.Version == 1 && wire.Epoch != Guid.Empty && wire.Revision >= 0,
                "Unsupported module inventory message.");
            ValidateKey(wire.Container);
            Require(Containers.ContainsKey(wire.Container), "The receiving mod has not registered this container.");
            if (BoltNetwork.isServer && peer.ServerSession != null) ReceiveClient(peer, wire);
            else if (BoltNetwork.isClient && peer.ServerSession == null) ReceiveServer(peer, wire);
            else throw new InvalidOperationException("Module message has the wrong network direction.");
        }
        catch (Exception error)
        {
            RetirePeer(peer, "Module protocol rejected a message: " + error.Message);
        }
    }

    private static void ReceiveClient(ControlPeer peer, Wire wire)
    {
        var key = (peer.PeerId, wire.Container);
        if (wire.Kind == "sync")
        {
            Require(wire.Operation == Guid.Empty && wire.Item == null && !wire.Deposit, "Invalid initial state message.");
            Require(ModuleStackStore.TryParseSnapshot(wire.Data, out var snapshot, out var detail, Limits), detail);
            Require(snapshot.Revision == wire.Revision, "Initial state revision disagrees with its payload.");
            if (Remotes.TryGetValue(key, out var existing))
                Require(ReferenceEquals(existing.Peer, peer) && existing.Epoch == wire.Epoch
                    && existing.Pending == null && existing.Snapshot.ExportJson() == snapshot.ExportJson(),
                    "An active peer cannot overwrite its acknowledged module inventory.");
            else
            {
                Require(Remotes.Count < ControlTransport.MaxPeers * MaximumContainers,
                    "Module peer storage capacity is exhausted.");
                long peerBytes = 0;
                foreach (var entry in Remotes.Values)
                    if (ReferenceEquals(entry.Peer, peer)) peerBytes += ReservedBytes(entry);
                Require(peerBytes + snapshot.SerializedByteCount <= MaximumSaveBytes, "The peer module inventory is too large.");
                Require(RemoteBytes() + snapshot.SerializedByteCount <= MaximumRemoteBytes, "The host module inventory memory budget is exhausted.");
                Remotes.Add(key, new Remote(peer, wire.Epoch, snapshot));
            }
            Send(peer, Reply("synced", wire, snapshot.Revision));
            return;
        }
        Require(Remotes.TryGetValue(key, out var remote) && ReferenceEquals(remote.Peer, peer)
            && remote.Epoch == wire.Epoch && wire.Operation != Guid.Empty, "Module operation has no current synchronized owner.");
        if (wire.Kind == "prepare")
        {
            Require(wire.Data == null && wire.Item != null && wire.Revision == remote.Snapshot.Revision,
                "Module operation is stale or incomplete.");
            var item = wire.Item.Decode();
            if (remote.Pending != null)
            {
                Require(remote.Pending.Id == wire.Operation && remote.Pending.Deposit == wire.Deposit
                    && remote.Pending.Item.SameState(item), "A different transfer is already reserved.");
                Send(peer, Reply("permit", wire, wire.Revision));
                return;
            }
            Require(wire.Operation != remote.LastOperation, "An already completed transfer cannot be prepared again.");
            var after = ModuleStackStore.ChangedSnapshot(remote.Snapshot, wire.Deposit, item, Limits);
            long peerBytes = Math.Max(after.SerializedByteCount, remote.Snapshot.SerializedByteCount);
            foreach (var entry in Remotes.Values)
                if (ReferenceEquals(entry.Peer, peer) && !ReferenceEquals(entry, remote)) peerBytes += ReservedBytes(entry);
            Require(peerBytes <= MaximumSaveBytes, "The peer module inventory byte limit would be exceeded.");
            Require(RemoteBytes() + after.SerializedByteCount <= MaximumRemoteBytes, "The host transfer memory budget is exhausted.");
            remote.Pending = new RemoteChange(wire.Operation, wire.Deposit, item, after);
            Send(peer, Reply("permit", wire, wire.Revision));
        }
        else if (wire.Kind == "commit")
        {
            Require(wire.Item == null && wire.Data == null, "Commit must reference its reserved state, not supply replacement data.");
            if (remote.LastOperation == wire.Operation)
            {
                Require(remote.LastCommitted && wire.Revision + 1 == remote.Snapshot.Revision,
                    "The repeated receipt does not match its completed operation.");
                Send(peer, Reply("committed", wire, remote.Snapshot.Revision));
                return;
            }
            var pending = remote.Pending;
            Require(pending != null && pending.Id == wire.Operation && pending.Deposit == wire.Deposit
                && wire.Revision == remote.Snapshot.Revision, "Commit does not match its reserved transfer.");
            remote.Snapshot = pending.After;
            remote.LastOperation = pending.Id;
            remote.LastCommitted = true;
            remote.Pending = null;
            Send(peer, Reply("committed", wire, remote.Snapshot.Revision));
        }
        else if (wire.Kind == "cancel")
        {
            Require(remote.Pending != null && remote.Pending.Id == wire.Operation
                && wire.Revision == remote.Snapshot.Revision && wire.Item == null && wire.Data == null,
                "Cancellation does not match its transfer.");
            remote.LastOperation = wire.Operation;
            remote.LastCommitted = false;
            remote.Pending = null;
            Send(peer, Reply("cancelled", wire, remote.Snapshot.Revision));
        }
        else throw new InvalidOperationException("Unknown client module operation.");
    }

    private static void ReceiveServer(ControlPeer peer, Wire wire)
    {
        Require(wire.Epoch == _epoch && wire.Data == null && wire.Item == null, "The server response belongs to another inventory lifetime.");
        var container = Containers[wire.Container];
        if (wire.Kind == "synced")
        {
            Announced.TryGetValue(wire.Container, out var announced);
            Require(wire.Operation == Guid.Empty && announced != null
                && ReferenceEquals(announced.Peer, peer) && announced.Revision == wire.Revision,
                "The server acknowledged an unknown inventory baseline.");
            announced.Acknowledged = true;
            if (container.Pending is { LocalCommitted: true } old && container.Store.ReadSnapshot().Revision == wire.Revision)
                Complete(old);
            return;
        }
        if (wire.Kind == "cancelled") return; // The native action was never attempted.
        var operation = container.Pending;
        Require(operation != null && operation.Id == wire.Operation && ReferenceEquals(operation.Peer, peer)
            && operation.Deposit == wire.Deposit, "The response has no matching native transfer.");
        if (wire.Kind == "permit")
        {
            Require(wire.Revision == operation.Change.Before.Revision, "The host approved another revision.");
            if (operation.LocalCommitted) { Send(peer, OperationWire("commit", operation)); return; }
            Require(operation.State == ModuleStorageOperationState.Pending, "The native operation cannot be repeated.");
            Execute(operation);
        }
        else if (wire.Kind == "committed")
        {
            Require(operation.LocalCommitted && operation.Change.After.Revision == wire.Revision, "The host acknowledged an uncommitted local state.");
            if (Announced.TryGetValue(container.Key, out var announcement)) announcement.Revision = wire.Revision;
            Complete(operation);
        }
        else throw new InvalidOperationException("Unknown server module response.");
    }

    private static Wire OperationWire(string kind, ModuleStorageOperation operation)
        => new(kind, operation.Epoch, operation.Container.Key, operation.Id, operation.Change.Before.Revision,
            operation.Deposit, null, null);
    private static Wire Reply(string kind, Wire request, long revision)
        => new(kind, request.Epoch, request.Container, request.Operation, revision, request.Deposit, null, null);
    private static void Send(ControlPeer peer, Wire wire)
    {
        Require(_transport != null && Eligible(peer), "The module transport peer is unavailable.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(wire, JsonOptions);
        Require(bytes.Length <= MaximumSaveBytes, "The module protocol envelope exceeds its bound.");
        if (!Outgoing.TryGetValue(peer.PeerId, out var queue))
        {
            Require(Outgoing.Count < ControlTransport.MaxPeers, "The module message peer limit is reached.");
            queue = new(peer);
            Outgoing.Add(peer.PeerId, queue);
        }
        Require(ReferenceEquals(queue.Peer, peer) && queue.Messages.Count < 32
            && _outgoingBytes + bytes.Length <= 8 * 1024 * 1024, "The module application message queue is full.");
        queue.Messages.Enqueue(new(bytes, Now));
        _outgoingBytes += bytes.Length;
    }

    private static void FlushOutgoing()
    {
        if (Outgoing.Count == 0) return;
        foreach (var entry in Outgoing.ToArray())
        {
            var queue = entry.Value;
            if (!Eligible(queue.Peer)) { RetirePeer(queue.Peer, "Module message peer is no longer enrolled."); continue; }
            for (int i = 0; i < 2 && queue.Messages.Count > 0; i++)
            {
                var message = queue.Messages.Peek();
                if (Now - message.Queued > 120) { RetirePeer(queue.Peer, "Module application message expired."); break; }
                if (!_transport.TrySend(queue.Peer, message.Bytes, out var detail))
                {
                    // Backpressure retries this immutable envelope, never its native action.
                    if (detail != "QueueWindowFull") RetirePeer(queue.Peer, detail);
                    break;
                }
                queue.Messages.Dequeue();
                _outgoingBytes -= message.Bytes.Length;
            }
            if (queue.Messages.Count == 0) Outgoing.Remove(entry.Key);
        }
    }

    private static void RetirePeer(ControlPeer peer, string detail)
    {
        if (peer == null || !OnThread || !_enabled) return;
        RetiredPeers[peer.PeerId] = peer;
        _transport?.Forget(peer);
        if (Outgoing.Remove(peer.PeerId, out var queue))
            foreach (var message in queue.Messages) _outgoingBytes -= message.Bytes.Length;
        foreach (var key in Remotes.Keys.Where(key => key.Peer == peer.PeerId).ToArray()) Remotes.Remove(key);
        foreach (var key in Announced.Where(entry => ReferenceEquals(entry.Value.Peer, peer)).Select(entry => entry.Key).ToArray())
            Announced.Remove(key);
        foreach (var container in Containers.Values)
        {
            var pending = container.Pending;
            if (pending == null || !ReferenceEquals(pending.Peer, peer)) continue;
            if (!pending.LocalCommitted && pending.State == ModuleStorageOperationState.Pending)
            {
                pending.Change.Cancel();
                pending.State = ModuleStorageOperationState.Refused;
                pending.Detail = detail + " The native transfer was not attempted.";
                container.Pending = null;
            }
            else
            {
                pending.State = ModuleStorageOperationState.Unconfirmed;
                pending.Detail = detail + " Do not replay this native transfer.";
            }
        }
    }

    private static long RemoteBytes()
    {
        long bytes = 0;
        foreach (var remote in Remotes.Values)
            bytes += remote.Snapshot.SerializedByteCount + (remote.Pending?.After.SerializedByteCount ?? 0);
        return bytes;
    }

    private static int ReservedBytes(Remote remote) => Math.Max(remote.Snapshot.SerializedByteCount,
        remote.Pending?.After.SerializedByteCount ?? 0);

    private sealed class Announcement(ControlPeer peer, Guid epoch, long revision)
    {
        internal readonly ControlPeer Peer = peer;
        internal readonly Guid Epoch = epoch;
        internal long Revision = revision;
        internal bool Acknowledged;
    }
    private sealed class Outbox(ControlPeer peer)
    {
        internal readonly ControlPeer Peer = peer;
        internal readonly Queue<QueuedMessage> Messages = new();
    }
    private sealed record QueuedMessage(byte[] Bytes, double Queued);
    private sealed class Remote(ControlPeer peer, Guid epoch, ModuleStackSnapshot snapshot)
    {
        internal readonly ControlPeer Peer = peer;
        internal readonly Guid Epoch = epoch;
        internal ModuleStackSnapshot Snapshot = snapshot;
        internal RemoteChange Pending;
        internal Guid LastOperation;
        internal bool LastCommitted;
    }
    private sealed record RemoteChange(Guid Id, bool Deposit, ItemModuleSnapshot Item,
        ModuleStackSnapshot After)
    { internal readonly double Started = Now; }

    private sealed record ItemPayload(int ItemId, string Modules, PlayerInventoryModuleState State)
    {
        internal ItemModuleSnapshot Decode() => ItemModuleSnapshot.FromSerialized(ItemId, Modules, State);
        internal static ItemPayload Encode(ItemModuleSnapshot item)
            => item == null ? null : new(item.ItemId, item.NativeModulesJson, item.SupplementalState);
    }
    private sealed record Wire(int Version, string Kind, Guid Epoch, string Container, Guid Operation,
        long Revision, bool Deposit, ItemPayload Item, string Data)
    {
        internal Wire(string kind, Guid epoch, string container, Guid operation, long revision,
            bool deposit, ItemModuleSnapshot item, string data)
            : this(1, kind, epoch, container, operation, revision, deposit, ItemPayload.Encode(item), data) { }
    }
}
