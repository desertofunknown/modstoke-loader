// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Bolt;
using Endnight.Extensions;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using global::Sons.Multiplayer;
using SonsSdk;
using SonsSdk.Networking;
using UdpKit;
using SdkPackets = SonsSdk.Networking.Packets;

namespace Modstoke.Loader
{
    internal sealed class Packets : ISdkPacketDriver
    {
        internal static readonly global::Modstoke.Loader.Packets Instance = new();
        public static bool EnforcesClientRelayPolicy => true;
        private const ushort PacketMarker = 6969;
        private static int _reportedReceiveFailure;
        private static bool _initialized;

        private Packets() { }

        public void Initialize()
        {
            if (_initialized)
                return;
            global::SonsSdk.SdkEntryPoint.Harmony.PatchAll(typeof(NetworkingPatches));
            _initialized = true;
        }

        public void Register(SdkPackets.NetEvent netEvent)
        {
            ArgumentNullException.ThrowIfNull(netEvent);
            var hash = netEvent.GetIdHash();
            if (!SdkPackets.RegisteredEvents.TryAdd(hash, new(hash, netEvent.Read, netEvent.AllowClientRelay)))
                throw new Exception($"Net Event with id '{netEvent.Id}' already registered");
        }

        public bool Unregister(SdkPackets.NetEvent netEvent)
        {
            ArgumentNullException.ThrowIfNull(netEvent);
            var hash = netEvent.GetIdHash();
            return SdkPackets.RegisteredEvents.TryGetValue(hash, out var registration)
                && ReferenceEquals(registration.ReadFunc.Target, netEvent)
                && SdkPackets.RegisteredEvents.Remove(hash);
        }

        public void HandlePacket(UdpPacket packet, BoltConnection fromConnection)
        {
            if (!TryReadHeader(packet, out var target, out var targetConnection, out var id))
                return;
            var registered = SdkPackets.RegisteredEvents.TryGetValue(id, out var registration);
            if (BoltNetwork.isServer && id != global::Modstoke.Sons.Datagrams.EventId)
                global::Modstoke.Sons.ClientSignals.RecordSdkPacket(fromConnection, id, registered);
            if (!registered)
                return;

            if (BoltNetwork.isServer)
            {
                // A client cannot disguise an authoritative update as a targeted
                // or broadcast packet and have the server forward it unchanged.
                if (!registration.AllowClientRelay && target != GlobalTargets.OnlyServer)
                    return;
                if (target is GlobalTargets.OnlyServer or GlobalTargets.Everyone or GlobalTargets.Others)
                    registration.ReadFunc(packet, fromConnection);

                if (target == GlobalTargets.OnlyServer)
                    return;

                var relay = new SdkPackets.EventPacket(ClonePacket(packet), target, targetConnection);
                SendPacket(relay, target == GlobalTargets.Others ? fromConnection.ConnectionId : null);
                return;
            }

            registration.ReadFunc(packet, fromConnection);
        }

        internal static bool TryReadHeader(UdpPacket packet, out GlobalTargets target,
            out uint targetConnection, out int id)
        {
            target = default;
            targetConnection = 0;
            id = 0;
            if (!HasReadableBytes(packet, 5)) return false;
            var targetByte = packet.ReadByte();
            if (targetByte == 0)
            {
                if (!HasReadableBytes(packet, 8)) return false;
                target = GlobalTargets.OnlySelf;
                targetConnection = packet.ReadUInt();
            }
            else
            {
                target = (GlobalTargets)targetByte;
                if (target is not (GlobalTargets.Everyone or GlobalTargets.Others
                    or GlobalTargets.AllClients or GlobalTargets.OnlyServer or GlobalTargets.OnlySelf))
                    return false;
            }
            id = packet.ReadInt();
            return true;
        }

        private static bool HasReadableBytes(UdpPacket packet, int count)
        {
            if (packet == null || packet.WasCollected) return false;
            var data = packet.Data;
            // Size and Ptr are bit counts, while Data.Length is a byte count.
            var size = packet.Size;
            var position = packet.Ptr;
            return data != null && !data.WasCollected && size >= 0 && size <= data.Length * 8L
                && position >= 0 && (position & 7) == 0
                && (long)position + count * 8L <= size;
        }

        internal static UdpPacket ClonePacket(UdpPacket packet)
        {
            if (packet == null || packet.WasCollected)
                throw new ArgumentException("A live packet is required.", nameof(packet));
            return ClonePacket(packet, packet.Size);
        }

        private static UdpPacket ClonePacket(UdpPacket packet, int size)
        {
            var data = packet.Data;
            if (data == null || data.WasCollected || size < 0 || size > data.Length * 8L)
                throw new ArgumentException("Packet size exceeds its readable buffer.", nameof(packet));
            // Keep the native writer-based clone, but never relay unused buffer capacity.
            int bytes = (int)((size + 7L) / 8);
            var clone = CreatePacketBuffer(bytes, 0);
            clone.WriteByteArray(data, 0, bytes);
            clone.Ptr = size;
            return clone;
        }

        internal static UdpPacket CreatePacketBuffer(int payloadBytes, int headerBytes)
        {
            // UdpPacket stores its length and cursor as signed bit counts.
            if (payloadBytes < 0 || payloadBytes > int.MaxValue / 8 - headerBytes)
                throw new ArgumentOutOfRangeException(nameof(payloadBytes));
            return new UdpPacket(new Il2CppStructArray<byte>(size: payloadBytes + headerBytes));
        }

        public void SendPacket(SdkPackets.EventPacket packet, uint? skipConnection)
        {
            if (!BoltNetwork.isRunning)
                return;

            ArgumentNullException.ThrowIfNull(packet);
            if (packet.Packet == null || packet.Packet.WasCollected)
                throw new ArgumentException("A live packet is required.", nameof(packet));

            if (BoltNetwork.isClient)
            {
                SendToConnection(BoltNetwork.server, packet.Packet);
                return;
            }

            var broadcast = packet.Targets is GlobalTargets.Everyone or GlobalTargets.Others or GlobalTargets.AllClients;
            if (!broadcast && packet.Targets != GlobalTargets.OnlySelf) return;
            var clients = BoltNetwork.clients.GetEnumerator();
            try
            {
                var iterator = clients.Cast<Il2CppSystem.Collections.IEnumerator>();
                while (iterator.MoveNext())
                {
                    var connection = clients.Current;
                    if (broadcast)
                    {
                        if (skipConnection != null && connection.ConnectionId == skipConnection.Value) continue;
                        SendToConnection(connection, packet.Packet);
                    }
                    else if (connection.ConnectionId == packet.TargetConnectionId)
                        SendToConnection(connection, packet.Packet);
                }
            }
            finally { clients.Cast<Il2CppSystem.IDisposable>().Dispose(); }
        }

        private static void SendToConnection(BoltConnection connection, UdpPacket packet)
        {
            // Native Send queues the exact mutable packet; each recipient owns a separate copy.
            connection._udp.Send(ClonePacket(packet, packet.Ptr));
        }

        public SdkPackets.EventPacket GetPacket(int typeId, int size, GlobalTargets targets)
        {
            var packet = CreatePacketBuffer(size, 7);
            packet.WriteUShort(PacketMarker);
            packet.WriteByte((byte)targets);
            packet.WriteInt(typeId);
            return new(packet, targets, 0);
        }

        public SdkPackets.EventPacket GetPacket(int typeId, int size, uint targetConnectionId)
        {
            var packet = CreatePacketBuffer(size, 11);
            packet.WriteUShort(PacketMarker);
            packet.WriteByte(0);
            packet.WriteUInt(targetConnectionId);
            packet.WriteInt(typeId);
            return new(packet, GlobalTargets.OnlySelf, targetConnectionId);
        }

        private static bool HasMarker(UdpPacket packet)
        {
            if (packet == null || packet.WasCollected) return false;
            var position = packet.Ptr;
            try
            {
                packet.Ptr = 0;
                return HasReadableBytes(packet, 2) && packet.ReadUShort() == PacketMarker;
            }
            finally { packet.Ptr = position; }
        }

        internal static bool ReceivePacket(UdpPacket packet, BoltConnection connection)
        {
            if (packet == null || packet.WasCollected) return false;
            bool owned = false;
            try
            {
                if (!HasMarker(packet)) return true;
                owned = true;
                packet.Ptr = 16;
                SdkPackets.HandlePacket(packet, connection);
                return false;
            }
            catch (Exception error)
            {
                ReportReceiveFailure(error);
                return !owned;
            }
            finally
            {
                // Suppressing PacketReceived skips the native Packet.Acquire disposal scope.
                if (owned)
                {
                    try { if (!packet.WasCollected && !packet.IsPooled) packet.Dispose(); }
                    catch (Exception error) { ReportReceiveFailure(error); }
                }
            }
        }

        internal static bool DeliverPacket(Packet packet)
        {
            if (packet == null || packet.WasCollected) return false;
            return !HasMarker(packet.UdpPacket);
        }

        internal static bool ReceiveChatEvent(ChatEvent evnt)
        {
            var handlers = SdkPackets.ChatHandlers;
            if (handlers == null)
                return true;

            // A throwing compatibility handler must not skip the others or the native chat handler.
            foreach (var handler in Delegate.EnumerateInvocationList(handlers))
            {
                try
                {
                    if (handler(evnt))
                        return false;
                }
                catch (Exception error) { ReportReceiveFailure(error); }
            }
            return true;
        }

        private static void ReportReceiveFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedReceiveFailure, 1) != 0) return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("SDK packet receive failed ("
                    + error.GetType().FullName + "). Further packet failures will not be logged.");
            }
            catch (Exception) { }
        }

        private sealed class NetworkingPatches
        {
            [HarmonyPatch(typeof(BoltConnection), nameof(BoltConnection.PacketReceived), typeof(UdpPacket))]
            [HarmonyPrefix]
            private static bool PacketReceived(BoltConnection __instance, UdpPacket udpPacket, bool __runOriginal)
            {
                return __runOriginal && ReceivePacket(udpPacket, __instance);
            }

            [HarmonyPatch(typeof(BoltConnection), nameof(BoltConnection.PacketDelivered), typeof(Packet))]
            [HarmonyPrefix]
            private static bool PacketDelivered(BoltConnection __instance, Packet packet, bool __runOriginal)
            {
                return __runOriginal && DeliverPacket(packet);
            }

            [HarmonyPatch(typeof(CoopServerCallbacks), nameof(CoopServerCallbacks.OnEvent), typeof(ChatEvent))]
            [HarmonyPrefix]
            private static bool ChatEventPatch(CoopServerCallbacks __instance, ChatEvent evnt)
            {
                return ReceiveChatEvent(evnt);
            }
        }
    }
}
