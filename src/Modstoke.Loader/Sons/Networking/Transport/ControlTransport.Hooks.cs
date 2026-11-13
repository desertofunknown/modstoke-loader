// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Loader;
using UdpKit;

namespace Modstoke.Sons
{
    public static partial class ControlTransport
    {
        private static bool InstallHooks()
        {
            if (_patchAttempted)
                return _hooksInstalled;
            _patchAttempted = true;
            if (_support == null || !_support.SafeForHarmonyPatches)
                return false;
            try
            {
                var begin = AccessTools.Method(typeof(BoltCore), "BeginStart", new[] { typeof(Bolt.ControlCommandStart) });
                var channels = AccessTools.Method(typeof(BoltInternal.GlobalEventListenerBase), "BoltStartBeginInvoke", Type.EmptyTypes);
                var connected = AccessTools.Method(typeof(BoltInternal.GlobalEventListenerBase), "ConnectedInvoke", new[] { typeof(BoltConnection) });
                var disconnected = AccessTools.Method(typeof(BoltInternal.GlobalEventListenerBase), "DisconnectedInvoke", new[] { typeof(BoltConnection) });
                var shutdown = AccessTools.Method(typeof(BoltCore), "BeginShutdown", new[] { typeof(Bolt.ControlCommandShutdown) });
                var immediate = AccessTools.Method(typeof(BoltCore), "ShutdownImmediate", Type.EmptyTypes);
                var block = AccessTools.Method(typeof(UdpChannelStreamer), "OnBlockReceived",
                    new[] { typeof(Il2CppStructArray<byte>), typeof(int), typeof(int) });
                var update = AccessTools.Method(typeof(BoltPoll), nameof(BoltPoll.Update), Type.EmptyTypes);
                if (begin == null || channels == null || connected == null || disconnected == null
                    || shutdown == null || immediate == null || block == null || update == null)
                    throw new MissingMethodException("A connection negotiation hook is unavailable.");

                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-control-transport");
                harmony.Patch(begin, prefix: Hook(nameof(BeforeStart)));
                harmony.Patch(channels, prefix: Hook(nameof(CreateChannel)));
                // Session tracking's first-priority prefix runs before this postfix.
                harmony.Patch(connected, postfix: Hook(nameof(AfterConnected)));
                harmony.Patch(disconnected, prefix: Hook(nameof(BeforeDisconnected)));
                harmony.Patch(shutdown, prefix: Hook(nameof(BeforeShutdown)));
                harmony.Patch(immediate, prefix: Hook(nameof(BeforeShutdown)));
                harmony.Patch(block, prefix: Hook(nameof(BeforeBlock)));
                harmony.Patch(update, postfix: Hook(nameof(Tick)));
                _hooksInstalled = true;
                return true;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return false;
            }
        }

        private static HarmonyMethod Hook(string name) => new(typeof(ControlTransport), name)
        {
            priority = Priority.First
        };

        private static bool OnOwnerThread => Environment.CurrentManagedThreadId == _ownerThreadId;

        private static void BeforeStart(Bolt.ControlCommandStart __0)
        {
            if (!_hooksInstalled || !OnOwnerThread)
                return;
            BeforeShutdown();
            // The last word on this start's selection, after any plugin chose its own. Bolt sets its mode from
            // this command only after BeginStart runs, so the role comes from the command itself.
            try { ContentAgreement.PrepareForStart(__0 != null && __0.Mode == BoltNetworkModes.Server); }
            catch (Exception error) { ReportFailure(error); }
        }

        /// <summary>Installs the start hooks ahead of any selection, so the content check can join a later start.</summary>
        internal static bool PrepareHooks() => OnOwnerThread && InstallHooks();

        // Selection from inside the start hook: the socket exists but the negotiation channel does not yet.
        internal static bool SelectForStart()
        {
            if (!_hooksInstalled || !OnOwnerThread) return false;
            lock (Gate)
            {
                if (_run != null) return false;
                _selected = true;
                _state = ControlState.Selected;
            }
            return true;
        }

        private static void CreateChannel()
        {
            if (!_hooksInstalled || !OnOwnerThread || !_selected)
                return;
            try
            {
                var socket = BoltCore._udpSocket;
                if (socket == null || socket.WasCollected || socket.State != UdpSocketState.Created)
                    throw new InvalidOperationException("The negotiation channel must be registered before Bolt starts.");
                lock (Gate)
                {
                    if (_run != null)
                        throw new InvalidOperationException("The negotiation channel was already registered for this start.");
                }
                var channel = BoltNetwork.CreateStreamChannel(ChannelName, UdpChannelMode.Reliable, 1);
                lock (Gate)
                {
                    _run = new Run(socket.Pointer, channel);
                    if (channel.Id != 1)
                        FailRun();
                    else
                        _state = ControlState.Active;
                }
                if (State == ControlState.Active)
                {
                    try { BoltMigrationTransport.RegisterForStart(); }
                    catch (Exception migrationError)
                    {
                        BoltMigrationTransport.ResetForStart();
                        ReportFailure(migrationError);
                    }
                }
            }
            catch (Exception error)
            {
                lock (Gate) FailRun();
                ReportFailure(error);
            }
        }

        private static void AfterConnected(BoltConnection __0, bool __runOriginal)
        {
            if (!_hooksInstalled || !OnOwnerThread || !__runOriginal || __0 == null)
                return;
            try
            {
                Run run;
                lock (Gate) run = _run;
                if (run == null || run.Failed || !BoltNetwork.isRunning)
                    return;
                ServerSession session = null;
                if (BoltNetwork.isServer)
                {
                    session = ServerSessions.ResolveSource(__0);
                    if (session == null)
                    {
                        // Paused session tracking is not a rejection: bind this connection once it resumes.
                        if (ServerSessions.Recovering)
                            Defer(run, __0);
                        return;
                    }
                }
                else
                {
                    var server = BoltNetwork.isClient ? BoltNetwork.server : null;
                    if (server == null || server.Pointer != __0.Pointer || server.ConnectionId != __0.ConnectionId)
                        return;
                }
                Bind(run, __0, session);
            }
            catch (Exception error)
            {
                lock (Gate) FailRun();
                ReportFailure(error);
            }
        }

        private static void Bind(Run run, BoltConnection connection, ServerSession session)
        {
            var udp = connection._udp;
            if (udp == null || udp.WasCollected || udp.ConnectionId != connection.ConnectionId)
                return;
            var socket = udp.Socket;
            if (socket == null || socket.WasCollected || socket.Pointer != run.SocketPointer)
                return;
            lock (Gate)
            {
                if (!ReferenceEquals(run, _run) || run.Failed)
                    return;
                run.Deferred.Remove(connection.Pointer);
                PruneClosedPeers();
                if (run.Peers.TryGetValue(udp.Pointer, out var previous))
                    Retire(run, previous);
                else if (run.Peers.Count >= MaxPeers)
                {
                    FailRun();
                    return;
                }
                var peer = new ControlPeer(run.Id, connection.Pointer, udp.Pointer, socket.Pointer,
                    connection.ConnectionId, session);
                run.Peers[udp.Pointer] = new PeerRecord(peer);
                run.PeerSnapshot = null;
                if (!EnqueueEvent(peer, true))
                    FailRun();
            }
        }

        private static void Defer(Run run, BoltConnection connection)
        {
            lock (Gate)
            {
                if (ReferenceEquals(run, _run) && !run.Failed
                    && (run.Deferred.ContainsKey(connection.Pointer) || run.Deferred.Count < MaxPeers))
                    run.Deferred[connection.Pointer] = connection.ConnectionId;
            }
        }

        // Binds connections that opened while session tracking was paused, once their sessions are current.
        private static void BindDeferred(Run run)
        {
            Dictionary<IntPtr, uint> deferred;
            lock (Gate)
            {
                if (run.Deferred.Count == 0)
                    return;
                if (!ServerSessions.HooksInstalled)
                {
                    // Suspended tracking resumes only at the next start, which begins a new run.
                    if (!ServerSessions.Recovering)
                        run.Deferred.Clear();
                    return;
                }
                deferred = new Dictionary<IntPtr, uint>(run.Deferred);
                run.Deferred.Clear();
            }
            if (!BoltNetwork.isServer)
                return;
            foreach (var session in ServerSessions.Sessions)
            {
                if (!deferred.TryGetValue(session.ConnectionPointer, out var id) || id != session.ConnectionId)
                    continue;
                var connection = ServerSessions.ResolveCurrent(session);
                if (connection != null)
                    Bind(run, connection, session);
            }
        }

        private static void BeforeDisconnected(BoltConnection __0)
        {
            if (!_hooksInstalled || !OnOwnerThread || __0 == null)
                return;
            try
            {
                ClientEnrollment.DisconnectingForGate(__0);
                lock (Gate)
                {
                    if (_run == null)
                        return;
                    _run.Deferred.Remove(__0.Pointer);
                    // Do not depend on a native transport that may already be tearing down.
                    foreach (var record in _run.Peers.Values)
                        if (record.Peer.ConnectionPointer == __0.Pointer)
                            Retire(_run, record);
                }
            }
            catch (Exception error) { ReportFailure(error); }
        }

        private static void BeforeShutdown()
        {
            if (!_hooksInstalled || !OnOwnerThread)
                return;
            lock (Gate)
            {
                if (_run != null)
                    foreach (var record in _run.Peers.Values) Retire(_run, record);
                _run = null;
                Incoming.Clear();
                _state = _selected ? ControlState.Selected : ControlState.Disabled;
            }
            // A managed failure must not escape into native Bolt start or shutdown, or stop the other resets.
            ResetForStart(ClientEnrollment.ResetForStart);
            ResetForStart(BoltMigrationTransport.ResetForStart);
            ResetForStart(PlayerBridge.ResetForStart);
            ResetForStart(PlayerRoles.ResetForStart);
        }

        private static void ResetForStart(Action reset)
        {
            try { reset(); }
            catch (Exception error) { ReportFailure(error); }
        }

        private static bool BeforeBlock(UdpChannelStreamer __instance, Il2CppStructArray<byte> __0, int __1, int __2)
        {
            if (!_hooksInstalled)
                return true;
            var owned = false;
            try
            {
                var channel = __instance.Channel.Config.ChannelName;
                owned = string.Equals(channel.Name, ChannelName, StringComparison.Ordinal);
                if (!owned)
                    return true;

                // The native receive buffer is borrowed. Never retain, dispose, or return it to a pool.
                var connection = __instance.Connection;
                var socket = connection.Socket;
                PeerRecord record;
                Run run;
                lock (Gate)
                {
                    run = _run;
                    if (run == null || run.Failed || socket.Pointer != run.SocketPointer || channel.Id != run.Channel.Id
                        || !run.Peers.TryGetValue(connection.Pointer, out record) || !record.Open
                        || connection.ConnectionId != record.Peer.ConnectionId
                        || connection.State != UdpConnectionState.Connected)
                        return false;
                    if (record.Pending >= MaxPendingPerPeer
                        || Incoming.Count >= MaxPendingFrames)
                    {
                        Retire(run, record);
                        return false;
                    }
                }

                var valid = ControlWire.TryReadBlock(__0, __1, __2, out var payload);
                lock (Gate)
                {
                    if (!ReferenceEquals(run, _run) || Find(record.Peer) != record || !record.Open)
                        return false;
                    var modAdmission = valid && record.ModAdmissionBudget && ModAdmissionWire.MatchesMagic(payload);
                    var exhausted = modAdmission ? record.ModAdmissionReceives >= MaxModAdmissionFramesPerPeer
                        : record.Receives >= MaxReceivesPerPeer;
                    if (!valid || exhausted || record.Pending >= MaxPendingPerPeer
                        || Incoming.Count >= MaxPendingFrames)
                        Retire(run, record);
                    else
                    {
                        if (modAdmission) record.ModAdmissionReceives++;
                        else record.Receives++;
                        record.Pending++;
                        Incoming.Enqueue(new Frame(record.Peer, payload));
                    }
                }
                // Own this channel's receive path, including after retirement. No native reassembly is created.
                return false;
            }
            catch (Exception error)
            {
                if (owned || _selected)
                {
                    lock (Gate) FailRun();
                    ReportFailure(error);
                    return false;
                }
                return true;
            }
        }

        private static void Tick()
        {
            if (!_hooksInstalled || !OnOwnerThread || _ticking)
                return;
            _ticking = true;
            try
            {
                Run run;
                lock (Gate) run = _run;
                if (run != null)
                {
                    var socket = BoltCore._udpSocket;
                    if (socket == null || socket.WasCollected || socket.Pointer != run.SocketPointer
                        || socket.State == UdpSocketState.Shutdown)
                        BeforeShutdown();
                    else
                    {
                        // A peer held by paused session tracking is checked again on a later frame.
                        foreach (var peer in GetPeers())
                            if (Resolve(peer, out var held) == null && !held) FailPeer(peer);
                        BindDeferred(run);
                    }
                }
                // Snapshot the work count so reentrant subscribers cannot extend this frame indefinitely.
                int eventCount;
                int frameCount;
                lock (Gate) { eventCount = Events.Count; frameCount = Incoming.Count; }
                for (var i = 0; i < eventCount; i++)
                {
                    PeerEvent item;
                    lock (Gate)
                    {
                        if (Events.Count == 0) break;
                        item = Events.Dequeue();
                    }
                    if (!item.Opened || Resolve(item.Peer) != null)
                        PublishPeerEvent(item);
                }
                for (var i = 0; i < frameCount; i++)
                {
                    Frame frame;
                    lock (Gate)
                    {
                        if (Incoming.Count == 0) break;
                        frame = Incoming.Dequeue();
                        var record = Find(frame.Peer);
                        if (record != null) record.Pending--;
                    }
                    if (Resolve(frame.Peer) != null)
                        PublishMessage(frame);
                }
                lock (Gate) PruneClosedPeers();
                ClientEnrollment.Tick();
                ModAdmission.Tick();
                ClientEnrollment.TickGate();
                Datagrams.Tick();
                ModuleStorage.Tick();
            }
            catch (Exception error)
            {
                lock (Gate) FailRun();
                ReportFailure(error);
            }
            finally { _ticking = false; }
        }
    }
}
