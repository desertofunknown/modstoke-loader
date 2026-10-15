// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Bolt;
using Endnight.Extensions;
using HarmonyLib;
using Sons.Multiplayer;
using SonsSdk;
using SonsSdk.Networking;
using UdpKit;
using SdkPackets = SonsSdk.Networking.Packets;

namespace OpenACAI.Endnight.Loader
{
    internal sealed class OpenACAISonsPackets : ISdkPacketDriver
    {
        internal static readonly OpenACAISonsPackets Instance = new();
        private const ushort PacketMarker = 6969;

        private OpenACAISonsPackets() { }

        public void Initialize()
        {
            SdkEntryPoint.Harmony.PatchAll(typeof(NetworkingPatches));
        }

        public void Register(SdkPackets.NetEvent netEvent)
        {
            var hash = netEvent.GetIdHash();
            if (!SdkPackets.RegisteredEvents.TryAdd(hash, new(hash, netEvent.Read)))
                throw new Exception($"Net Event with id '{netEvent.Id}' already registered");
        }

        public bool Unregister(SdkPackets.NetEvent netEvent)
        {
            return SdkPackets.RegisteredEvents.Remove(netEvent.GetIdHash());
        }

        public void HandlePacket(UdpPacket packet, BoltConnection fromConnection)
        {
            var targetByte = packet.ReadByte();
            var target = (GlobalTargets)targetByte;
            uint targetConnection = 0;
            if (targetByte == 0)
                targetConnection = packet.ReadUInt();

            var id = packet.ReadInt();
            if (!SdkPackets.RegisteredEvents.TryGetValue(id, out var registration))
                return;

            if (BoltNetwork.isServer)
            {
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

        private static UdpPacket ClonePacket(UdpPacket packet)
        {
            // Keep the native writer-based clone used by the SDK relay path.
            var clone = new UdpPacket(new byte[packet.Data.Length]);
            clone.WriteByteArray(packet.Data);
            return clone;
        }

        public void SendPacket(SdkPackets.EventPacket packet, uint? skipConnection)
        {
            if (!BoltNetwork.isRunning)
                return;

            if (BoltNetwork.isClient)
            {
                BoltNetwork.server._udp.Send(packet.Packet);
                return;
            }

            if (packet.Targets is GlobalTargets.Everyone or GlobalTargets.Others or GlobalTargets.AllClients)
            {
                BoltNetwork.clients.ForEach((Action<BoltConnection>)(connection =>
                {
                    if (skipConnection != null && connection.ConnectionId == skipConnection.Value)
                        return;

                    connection._udp.Send(packet.Packet);
                }));
            }

            if (packet.Targets == GlobalTargets.OnlySelf)
            {
                BoltNetwork.clients.ForEach((Action<BoltConnection>)(connection =>
                {
                    if (connection.ConnectionId == packet.TargetConnectionId)
                        connection._udp.Send(packet.Packet);
                }));
            }
        }

        public SdkPackets.EventPacket GetPacket(int typeId, int size, GlobalTargets targets)
        {
            var packet = new UdpPacket(new byte[size + 7]);
            packet.WriteUShort(PacketMarker);
            packet.WriteByte((byte)targets);
            packet.WriteInt(typeId);
            return new(packet, targets, 0);
        }

        public SdkPackets.EventPacket GetPacket(int typeId, int size, uint targetConnectionId)
        {
            var packet = new UdpPacket(new byte[size + 11]);
            packet.WriteUShort(PacketMarker);
            packet.WriteByte(0);
            packet.WriteUInt(targetConnectionId);
            packet.WriteInt(typeId);
            return new(packet, GlobalTargets.OnlySelf, targetConnectionId);
        }

        private sealed class NetworkingPatches
        {
            [HarmonyPatch(typeof(BoltConnection), nameof(BoltConnection.PacketReceived), typeof(UdpPacket))]
            [HarmonyPrefix]
            private static bool PacketReceived(BoltConnection __instance, UdpPacket udpPacket)
            {
                if (udpPacket.Size < 7 || udpPacket.ReadUShort() != PacketMarker)
                {
                    udpPacket.Position = 0;
                    return true;
                }

                Instance.HandlePacket(udpPacket, __instance);
                return false;
            }

            [HarmonyPatch(typeof(BoltConnection), nameof(BoltConnection.PacketDelivered), typeof(Packet))]
            [HarmonyPrefix]
            private static bool PacketDelivered(BoltConnection __instance, Packet packet)
            {
                if (packet == null)
                    return false;
                if (packet.UdpPacket == null)
                    return true;
                if (packet.UdpPacket.Size < 2 || packet.UdpPacket.ReadUShort() != PacketMarker)
                {
                    packet.UdpPacket.Position = 0;
                    return true;
                }
                return false;
            }

            [HarmonyPatch(typeof(CoopServerCallbacks), nameof(CoopServerCallbacks.OnEvent), typeof(ChatEvent))]
            [HarmonyPrefix]
            private static bool ChatEventPatch(CoopServerCallbacks __instance, ChatEvent evnt)
            {
                var handlers = SdkPackets.ChatHandlers;
                if (handlers == null)
                    return true;

                foreach (Func<ChatEvent, bool> handler in handlers.GetInvocationList())
                {
                    if (handler(evnt))
                        return false;
                }
                return true;
            }
        }
    }
}
