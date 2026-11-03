// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using Bolt;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Loader;
using UdpKit;
using SdkPackets = SonsSdk.Networking.Packets;

namespace Modstoke.Sons
{
    /// <summary>
    /// Bounded, unreliable datagrams between enrolled peers. Applications must supply their own
    /// versioning, receipts, replay handling and authority rules. No client claim is trusted here.
    /// </summary>
    public static partial class Datagrams
    {
        public const int MaxPayloadBytes = 256;
        public const int MaxOutstandingSends = 256;
        public const int MaxOutstandingSendsPerPeer = 8;
        private const int MaxReceivesPerTick = 256;
        private const int MaxReceivesPerPeerPerTick = 8;
        private const ushort PacketMarker = 6969;
        internal const int EventId = 0x4D534447;
        private static readonly object SendGate = new();
        private static readonly Dictionary<IntPtr, SendTicket> Pending = new();
        private static readonly Dictionary<Guid, int> PeerSends = new();
        private static readonly Queue<Datagram> Incoming = new();
        private static readonly Dictionary<Guid, int> PeerReceives = new();
        private static HarmonySupportSnapshot _support;
        private static SdkPackets.NetRegistration _registration;
        private static int _ownerThreadId;
        private static int _outstandingSends;
        private static int _receivedThisTick;
        private static int _reportedFailure;
        private static bool _patchAttempted;
        private static bool _hooksInstalled;
        private static bool _ticking;
        private static volatile bool _selected;

        /// <summary>Copied messages delivered on the game thread after current-peer validation.</summary>
        public static event Action<Datagram> Received;

        /// <summary>
        /// Includes retired peers and uncertain native sends. Only native completion releases a slot;
        /// a stalled or failed native transport can keep sending unavailable until process restart.
        /// </summary>
        public static int OutstandingSends { get { lock (SendGate) return _outstandingSends; } }

        internal static void Initialize(HarmonySupportSnapshot support)
        {
            if (Interlocked.CompareExchange(ref _ownerThreadId, Environment.CurrentManagedThreadId, 0) == 0)
                _support = support;
        }

        /// <summary>
        /// Select after gated enrollment has been selected, before Bolt starts, on both peers.
        /// Default initialization does not register an event or install hooks.
        /// </summary>
        public static ControlResult SelectForNextStart(bool selected)
        {
            if (_ownerThreadId == 0) return ControlResult.Unavailable;
            if (!OnOwnerThread) return ControlResult.WrongThread;
            try
            {
                var socket = BoltCore._udpSocket;
                if (BoltNetwork.isRunning || (socket != null && !socket.WasCollected
                    && socket.State != UdpSocketState.Shutdown))
                    return ControlResult.AlreadyStarting;
                if (selected)
                {
                    if (!ClientEnrollment.GameplayGateSelected || !ReserveEvent())
                        return ControlResult.Unavailable;
                    if (!InstallHooks())
                    {
                        if (OwnsEvent()) SdkPackets.RegisteredEvents.Remove(EventId);
                        _registration = null;
                        return ControlResult.Unavailable;
                    }
                }
                _selected = selected;
                ClearIncoming();
                return ControlResult.Success;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return ControlResult.Failed;
            }
        }

        /// <summary>
        /// Queues one datagram to an exact current, enrolled peer. Queued is not delivery or an
        /// application receipt. Loss is not retried. A failed native call may still hold a send slot.
        /// </summary>
        public static DatagramSendResult Send(ControlPeer peer, ReadOnlySpan<byte> payload)
        {
            if (_ownerThreadId == 0) return DatagramSendResult.Unavailable;
            if (!OnOwnerThread) return DatagramSendResult.WrongThread;
            if (payload.Length < 1 || payload.Length > MaxPayloadBytes)
                return DatagramSendResult.InvalidPayload;
            if (!_selected || !_hooksInstalled || !OwnsEvent())
                return DatagramSendResult.Unavailable;

            SendTicket ticket = null;
            bool reserved = false;
            bool registered = false;
            bool nativeAttempted = false;
            try
            {
                var connection = ControlTransport.Resolve(peer);
                if (connection == null) return DatagramSendResult.PeerUnavailable;
                if (!ClientEnrollment.AllowsGameTraffic(peer))
                    return DatagramSendResult.NotEnrolled;
                var udp = connection._udp;
                bool server = BoltNetwork.isServer;
                if (!server && !BoltNetwork.isClient) return DatagramSendResult.Unavailable;
                int packetBytes = payload.Length + (server ? 11 : 7);
                var pipe = udp.PacketPipe;
                var socket = udp.Socket;
                if (pipe == null || pipe.WasCollected || socket == null || socket.WasCollected)
                    return DatagramSendResult.Unavailable;
                var config = pipe.Config;
                var buffer = socket.sendBuffer;
                if (buffer == null || buffer.WasCollected || config.SequenceBytes < 0 || config.AckBytes < 0
                    || config.DatagramSize <= 0)
                    return DatagramSendResult.Unavailable;
                long nativeHeaderBytes = 2L * config.SequenceBytes + config.AckBytes + 3;
                if (nativeHeaderBytes != config.HeaderSize
                    || nativeHeaderBytes + packetBytes > config.DatagramSize
                    || nativeHeaderBytes + packetBytes > buffer.Length)
                    return DatagramSendResult.InvalidPayload;
                lock (SendGate)
                {
                    PeerSends.TryGetValue(peer.PeerId, out var count);
                    if (_outstandingSends >= MaxOutstandingSends || count >= MaxOutstandingSendsPerPeer)
                        return DatagramSendResult.WindowFull;
                    _outstandingSends++;
                    PeerSends[peer.PeerId] = count + 1;
                    reserved = true;
                }

                // Reserve before allocating. Direct packets are unpooled and retained by native queues.
                var data = new Il2CppStructArray<byte>(size: packetBytes);
                var packet = new UdpPacket(data);
                packet.WriteUShort(PacketMarker);
                packet.WriteByte(server ? (byte)0 : (byte)GlobalTargets.OnlyServer);
                if (server) packet.WriteUInt(connection.ConnectionId);
                packet.WriteInt(EventId);
                payload.CopyTo(data.AsSpan().Slice(packetBytes - payload.Length));
                packet.Ptr = packetBytes * 8;
                GC.KeepAlive(data);
                ticket = new SendTicket(peer.PeerId, udp.Pointer, packet);
                lock (SendGate)
                {
                    Pending.Add(packet.Pointer, ticket);
                    registered = true;
                }
                nativeAttempted = true;
                udp.Send(packet);
                return DatagramSendResult.Queued;
            }
            catch (Exception error)
            {
                // Once Send was entered, neither an exception nor elapsed time proves queue release.
                if (reserved && !nativeAttempted)
                {
                    lock (SendGate)
                    {
                        if (registered) Pending.Remove(ticket.Packet.Pointer);
                        ReleaseSlot(peer.PeerId);
                    }
                }
                ReportFailure(error);
                return DatagramSendResult.Failed;
            }
        }

        private static bool OnOwnerThread => Environment.CurrentManagedThreadId == _ownerThreadId;

        private static bool ReserveEvent()
        {
            if (_registration != null) return OwnsEvent();
            var registration = new SdkPackets.NetRegistration(EventId, IgnoreLegacyDispatch);
            if (!SdkPackets.RegisteredEvents.TryAdd(EventId, registration)) return false;
            _registration = registration;
            return true;
        }

        private static bool OwnsEvent() => _registration != null
            && SdkPackets.RegisteredEvents.TryGetValue(EventId, out var current)
            && ReferenceEquals(current, _registration);

        private static void IgnoreLegacyDispatch(UdpPacket packet, BoltConnection connection) { }

        private static bool BeforeDatagram(BoltConnection __instance, UdpPacket __0, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (__0 == null || __0.WasCollected) return true;
            bool owned = false;
            int position = 0;
            bool savedPosition = false;
            try
            {
                if (!OwnsEvent()) return true;
                var data = __0.Data;
                // UdpPacket.Size is a bit count, including the SDK header.
                int sizeBits = __0.Size;
                if (data == null || data.WasCollected || sizeBits < 7 * 8
                    || sizeBits > data.Length * 8L) return true;
                position = __0.Position;
                savedPosition = true;
                __0.Position = 0;
                if (__0.ReadUShort() != PacketMarker) return true;
                var target = __0.ReadByte();
                int headerSize = target == 0 ? 11 : 7;
                if (sizeBits < headerSize * 8) return true;
                if (target == 0) __0.ReadUInt();
                if (__0.ReadInt() != EventId) return true;
                owned = true;
                if (!OnOwnerThread || !_selected || !_hooksInstalled || !BoltNetwork.isRunning)
                    return false;
                // Never relay this protocol through the legacy global-target route.
                if (BoltNetwork.isServer ? target != (byte)GlobalTargets.OnlyServer
                    : !BoltNetwork.isClient || target != 0)
                    return false;
                int length = sizeBits / 8 - headerSize;
                if ((sizeBits & 7) != 0 || length < 1 || length > MaxPayloadBytes || _receivedThisTick >= MaxReceivesPerTick
                    || Incoming.Count >= MaxReceivesPerTick)
                    return false;
                var peer = ControlTransport.FindPeer(__instance);
                if (peer == null || !ClientEnrollment.AllowsGameTraffic(peer)) return false;
                PeerReceives.TryGetValue(peer.PeerId, out var count);
                if (count >= MaxReceivesPerPeerPerTick) return false;
                _receivedThisTick++;
                PeerReceives[peer.PeerId] = count + 1;
                var bytes = data.AsSpan().Slice(headerSize, length).ToArray();
                GC.KeepAlive(data);
                Incoming.Enqueue(new Datagram(peer, bytes));
                return false;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return !owned;
            }
            finally
            {
                try
                {
                    if (owned)
                    {
                        // Suppression happens before native Packet.Acquire and its disposal scope.
                        if (!__0.WasCollected && !__0.IsPooled) __0.Dispose();
                    }
                    else if (savedPosition) __0.Position = position;
                }
                catch (Exception error) { ReportFailure(error); }
            }
        }

        private static bool BeforeCompletion(UdpEvent __0, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            bool owned = false;
            try
            {
                var packet = __0.Object1?.TryCast<UdpPacket>();
                if (packet == null || packet.WasCollected) return true;
                lock (SendGate)
                {
                    if (!Pending.TryGetValue(packet.Pointer, out var ticket)) return true;
                    owned = true;
                    var connection = __0.Object0?.TryCast<UdpConnection>();
                    if (connection == null || connection.WasCollected || connection.Pointer != ticket.ConnectionPointer)
                        return false;
                    Pending.Remove(packet.Pointer);
                    ReleaseSlot(ticket.PeerId);
                    // The packet is unpooled. Dropping our reference allows GC after native dispatch.
                    GC.KeepAlive(ticket.Packet);
                    return false;
                }
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return !owned;
            }
        }

        // Caller holds SendGate so the global and per-peer reservations change together.
        private static void ReleaseSlot(Guid peerId)
        {
            _outstandingSends--;
            var count = PeerSends[peerId] - 1;
            if (count == 0) PeerSends.Remove(peerId);
            else PeerSends[peerId] = count;
        }

        internal static void Tick()
        {
            if (!OnOwnerThread || _ticking) return;
            _ticking = true;
            try
            {
                if (!_selected || !_hooksInstalled || !OwnsEvent())
                {
                    ClearIncoming();
                    return;
                }
                int count = Incoming.Count;
                for (var i = 0; i < count && Incoming.Count != 0; i++)
                {
                    var message = Incoming.Dequeue();
                    if (!ClientEnrollment.AllowsGameTraffic(message.Peer)) continue;
                    var handlers = Received;
                    if (handlers == null) continue;
                    foreach (var handler in Delegate.EnumerateInvocationList(handlers))
                    {
                        // A preceding callback can retire the peer or change selection/registration.
                        if (!_selected || !OwnsEvent()
                            || !ClientEnrollment.AllowsGameTraffic(message.Peer)) break;
                        try { handler(message); }
                        catch (Exception error) { ReportFailure(error); }
                    }
                }
            }
            catch (Exception error)
            {
                ClearIncoming();
                ReportFailure(error);
            }
            finally
            {
                _receivedThisTick = 0;
                PeerReceives.Clear();
                _ticking = false;
            }
        }

        private static void ClearIncoming()
        {
            Incoming.Clear();
            PeerReceives.Clear();
            _receivedThisTick = 0;
            // Outstanding sends survive selection changes, disconnects and restarts. Native queues
            // may still retain them; lifecycle notifications do not release a reservation.
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0) return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Modstoke gameplay datagram failed ("
                    + error.GetType().FullName + "). Further failures will not be logged.");
            }
            catch (Exception) { }
        }

        private sealed class SendTicket
        {
            internal SendTicket(Guid peerId, IntPtr connectionPointer, UdpPacket packet)
            {
                PeerId = peerId;
                ConnectionPointer = connectionPointer;
                Packet = packet;
            }
            internal readonly Guid PeerId;
            internal readonly IntPtr ConnectionPointer;
            internal readonly UdpPacket Packet;
        }
    }
}
