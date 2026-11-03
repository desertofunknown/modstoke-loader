// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Loader;
using UdpKit;

namespace Modstoke.Sons
{
    /// <summary>
    /// An opt-in, bounded channel for connection negotiation. It does not authenticate accounts,
    /// admit gameplay, or establish that a client runs unmodified software.
    /// </summary>
    public static partial class ControlTransport
    {
        public const int MaxPayloadBytes = ControlWire.MaxPayloadBytes;
        public const int MaxPeers = 128;
        public const int MaxSendsPerPeer = 32;
        public const int MaxReceivesPerPeer = 64;
        internal const int MaxModAdmissionFramesPerPeer = 512;

        private const string ChannelName = "Modstoke.ClientNegotiation.v1";
        private const int MaxPendingPerPeer = 8;
        private const int MaxPendingFrames = 256;
        private const int MaxPendingEvents = MaxPeers * 2;
        private static readonly object Gate = new();
        private static readonly Queue<Frame> Incoming = new();
        private static readonly Queue<PeerEvent> Events = new();
        private static HarmonySupportSnapshot _support;
        private static Action<string> _warning;
        private static Run _run;
        private static int _ownerThreadId;
        private static int _reportedFailure;
        private static volatile bool _selected;
        private static bool _ticking;
        private static bool _patchAttempted;
        private static volatile bool _hooksInstalled;
        private static ControlState _state;

        /// <summary>Local setup state only; Active does not prove remote compatibility or delivery.</summary>
        public static ControlState State { get { lock (Gate) return _state; } }

        /// <summary>
        /// Game-thread notifications for observed connection lifetimes. Notifications are bounded;
        /// consumers must check State and peer validity rather than depend on receiving every close event.
        /// </summary>
        public static event Action<ControlPeer> Opened;
        public static event Action<ControlPeer> Closed;

        /// <summary>
        /// Runs on the game thread after source revalidation. Delivery may repeat; the negotiation
        /// protocol must bind its challenge and replay handling to Peer, not a claimed player ID.
        /// </summary>
        public static event Action<ControlMessage> Received;

        internal static void Initialize(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.CompareExchange(ref _ownerThreadId, Environment.CurrentManagedThreadId, 0) == 0)
            {
                _support = support;
                _warning = warning;
            }
        }

        /// <summary>
        /// Select before Bolt starts, on both peers. Selection reserves the first stream channel
        /// before game listeners; subsequent channel registrations must have compatible ordering.
        /// No channel is registered by default. Selection does not enable strict combat.
        /// </summary>
        public static ControlResult SelectForNextStart(bool selected)
        {
            if (_ownerThreadId == 0)
                return ControlResult.Unavailable;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return ControlResult.WrongThread;
            try
            {
                var socket = BoltCore._udpSocket;
                if (BoltNetwork.isRunning || (socket != null && !socket.WasCollected
                    && socket.State != UdpSocketState.Shutdown))
                    return ControlResult.AlreadyStarting;
                if (selected && !InstallHooks())
                    return ControlResult.Unavailable;
                lock (Gate)
                {
                    _selected = selected;
                    _state = selected ? ControlState.Selected : ControlState.Disabled;
                }
                return ControlResult.Success;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return ControlResult.Failed;
            }
        }

        /// <summary>
        /// Returns a read-only snapshot of observed open peers on the game thread.
        /// Membership snapshots may be reused; revalidate through Send before using peers.
        /// </summary>
        public static IReadOnlyList<ControlPeer> GetPeers()
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return Array.Empty<ControlPeer>();
            lock (Gate)
            {
                if (_run == null || _run.Failed || _run.Peers.Count == 0)
                    return Array.Empty<ControlPeer>();
                if (_run.PeerSnapshot != null)
                    return _run.PeerSnapshot;
                var peers = new List<ControlPeer>(_run.Peers.Count);
                foreach (var record in _run.Peers.Values)
                    if (record.Open) peers.Add(record.Peer);
                return _run.PeerSnapshot = peers.AsReadOnly();
            }
        }

        /// <summary>
        /// Queues one negotiation message. Success means the native send call returned, not receipt.
        /// The lifetime send budget never refills, bounding native operations even without acknowledgments.
        /// This channel is unsuitable for continuous gameplay traffic.
        /// </summary>
        public static ControlResult Send(ControlPeer peer, ReadOnlySpan<byte> payload)
            => SendCore(peer, payload, false);

        internal static ControlResult SendModAdmission(ControlPeer peer, ReadOnlySpan<byte> payload)
            => ModAdmissionWire.MatchesMagic(payload)
                ? SendCore(peer, payload, true) : ControlResult.InvalidPayload;

        internal static bool EnableModAdmissionBudget(ControlPeer peer)
        {
            if (!OnOwnerThread || Resolve(peer) == null) return false;
            lock (Gate)
            {
                var record = Find(peer);
                if (record == null || !record.Open) return false;
                // Repeated negotiation cannot reset either lifetime counter.
                record.ModAdmissionBudget = true;
                return true;
            }
        }

        private static ControlResult SendCore(ControlPeer peer,
            ReadOnlySpan<byte> payload, bool modAdmission)
        {
            if (_ownerThreadId == 0)
                return ControlResult.Unavailable;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return ControlResult.WrongThread;
            if (payload.Length < 1 || payload.Length > MaxPayloadBytes)
                return ControlResult.InvalidPayload;
            try
            {
                var connection = Resolve(peer);
                if (connection == null)
                    return ControlResult.PeerUnavailable;
                var udp = connection._udp;
                var pipe = udp.StreamPipe;
                if (pipe == null || pipe.WasCollected)
                    return ControlResult.Unavailable;
                var config = pipe.Config;
                if (config.SequenceBytes < 0 || config.AckBytes < 0)
                    return ControlResult.IncompatibleConfiguration;
                var blockSize = (long)udp.Socket.StreamPipeConfig.DatagramSize
                    - 2L * config.SequenceBytes - config.AckBytes - 3;
                if (blockSize < payload.Length || blockSize > int.MaxValue)
                    return ControlResult.IncompatibleConfiguration;

                UdpChannelName channel;
                lock (Gate)
                {
                    var record = Find(peer);
                    if (record == null || !record.Open)
                        return ControlResult.PeerUnavailable;
                    if (modAdmission && !record.ModAdmissionBudget)
                        return ControlResult.Unavailable;
                    if (modAdmission ? record.ModAdmissionSends >= MaxModAdmissionFramesPerPeer : record.Sends >= MaxSendsPerPeer)
                        return ControlResult.SendLimitReached;
                    // Reserve before native allocation/send; an exception cannot prove no work was queued.
                    if (modAdmission) record.ModAdmissionSends++;
                    else record.Sends++;
                    channel = _run.Channel;
                }
                // Snapshot caller storage before native allocation can reenter.
                Span<byte> copy = stackalloc byte[payload.Length];
                payload.CopyTo(copy);
                var data = new Il2CppStructArray<byte>(size: payload.Length);
                try
                {
                    copy.CopyTo(data.AsSpan());
                    connection.StreamBytes(channel, data);
                }
                finally { GC.KeepAlive(data); }
                return ControlResult.Success;
            }
            catch (Exception error)
            {
                FailPeer(peer);
                ReportFailure(error);
                return ControlResult.Failed;
            }
        }

        internal static bool IsCurrent(ControlPeer peer)
        {
            lock (Gate)
            {
                var record = Find(peer);
                return record != null && record.Open;
            }
        }

        private static PeerRecord Find(ControlPeer peer) => peer != null && _run != null
            && !_run.Failed && _run.Id == peer.RunId
            && _run.Peers.TryGetValue(peer.TransportPointer, out var record)
            && ReferenceEquals(record.Peer, peer) ? record : null;

        internal static ControlPeer FindPeer(BoltConnection connection)
        {
            if (!OnOwnerThread || connection == null || connection.WasCollected)
                return null;
            var udp = connection._udp;
            if (udp == null || udp.WasCollected)
                return null;
            ControlPeer peer;
            lock (Gate)
            {
                if (_run == null || _run.Failed || !_run.Peers.TryGetValue(udp.Pointer, out var record)
                    || !record.Open || record.Peer.ConnectionPointer != connection.Pointer
                    || record.Peer.ConnectionId != connection.ConnectionId)
                    return null;
                peer = record.Peer;
            }
            return Resolve(peer) != null ? peer : null;
        }

        // A server connection that opened while session tracking was paused, whose peer is bound on recovery.
        internal static bool AwaitsPeer(BoltConnection connection)
        {
            if (!OnOwnerThread || connection == null || connection.WasCollected)
                return false;
            lock (Gate)
                return _run != null && !_run.Failed && _run.Deferred.TryGetValue(connection.Pointer, out var id)
                    && id == connection.ConnectionId;
        }

        internal static BoltConnection Resolve(ControlPeer peer) => Resolve(peer, out _);

        // Held: server session tracking is recovering and could not check this peer, which is not a departure.
        internal static BoltConnection Resolve(ControlPeer peer, out bool held)
        {
            held = false;
            if (!IsCurrent(peer) || !BoltNetwork.isRunning)
                return null;
            var session = peer.ServerSession;
            var connection = session != null
                ? ServerSessions.ResolveCurrent(session)
                : BoltNetwork.isClient ? BoltNetwork.server : null;
            // Paused session tracking keeps the peer; check its exact native connection instead.
            if (connection == null && session != null && ServerSessions.Recovering)
                connection = ServerSessions.ResolveRecovering(session, out held);
            if (connection == null || connection.WasCollected || connection.Pointer != peer.ConnectionPointer
                || connection.ConnectionId != peer.ConnectionId)
                return null;
            var udp = connection._udp;
            if (udp == null || udp.WasCollected || udp.Pointer != peer.TransportPointer
                || udp.ConnectionId != peer.ConnectionId || udp.State != UdpConnectionState.Connected)
                return null;
            var socket = udp.Socket;
            if (socket == null || socket.WasCollected || socket.Pointer != peer.SocketPointer)
                return null;
            var currentSocket = BoltCore._udpSocket;
            if (currentSocket == null || currentSocket.WasCollected || currentSocket.Pointer != peer.SocketPointer)
                return null;
            return IsCurrent(peer) ? connection : null;
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            var message = "Modstoke connection negotiation failed (" + error.GetType().FullName + ").";
            try
            {
                if (Environment.CurrentManagedThreadId == _ownerThreadId)
                    _warning?.Invoke(message);
                else
                    RuntimeEntryPoint.LogSource?.LogWarning(message);
            }
            catch (Exception) { }
        }
    }

    public enum ControlState { Disabled, Selected, Active, Failed }
    public enum ControlResult
    {
        Success, Unavailable, WrongThread, AlreadyStarting, InvalidPayload, PeerUnavailable,
        IncompatibleConfiguration, SendLimitReached, Failed
    }

    /// <summary>Copied connection identity, not account authentication or permission. Contains no native wrapper.</summary>
    public sealed class ControlPeer
    {
        internal ControlPeer(Guid runId, IntPtr connection, IntPtr transport, IntPtr socket,
            uint connectionId, global::Modstoke.Sons.ServerSession serverSession)
        {
            RunId = runId;
            ConnectionPointer = connection;
            TransportPointer = transport;
            SocketPointer = socket;
            ConnectionId = connectionId;
            ServerSession = serverSession;
        }

        internal Guid RunId { get; }
        internal IntPtr ConnectionPointer { get; }
        internal IntPtr TransportPointer { get; }
        internal IntPtr SocketPointer { get; }
        public Guid PeerId { get; } = Guid.NewGuid();
        public uint ConnectionId { get; }
        /// <summary>Exact remote-client handle on a server; null for a client's connection to its server.</summary>
        public global::Modstoke.Sons.ServerSession ServerSession { get; }
        /// <summary>Last-observed local lifetime, not a network liveness guarantee.</summary>
        public bool IsCurrent => ControlTransport.IsCurrent(this);
    }

    public sealed class ControlMessage
    {
        private readonly byte[] _payload;
        internal ControlMessage(ControlPeer peer, byte[] payload)
        {
            Peer = peer;
            _payload = payload;
        }
        public ControlPeer Peer { get; }
        public int Length => _payload.Length;
        internal ReadOnlySpan<byte> PayloadSpan => _payload;
        /// <summary>Returns a copy; callers cannot mutate the transport's queued data.</summary>
        public byte[] GetPayload() => (byte[])_payload.Clone();
    }
}
