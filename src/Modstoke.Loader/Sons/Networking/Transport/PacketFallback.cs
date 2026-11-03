// SPDX-License-Identifier: GPL-3.0-or-later
// Compatibility packet handling adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using Bolt;
using global::Sons.Multiplayer;
using SonsSdk.Networking;
using UdpKit;
using SdkPackets = SonsSdk.Networking.Packets;

namespace Modstoke.Loader;

internal sealed class PacketFallback : ISdkPacketFallbackDriver
{
    private static readonly PacketFallback Driver = new();

    internal static ISdkPacketFallbackDriver Create() => Driver;

    public void Initialize()
    {
        global::Modstoke.Loader.Packets.Instance.Initialize();
    }

    public void Register(SdkPackets.NetEvent ev)
    {
        global::Modstoke.Loader.Packets.Instance.Register(ev);
    }

    public bool Unregister(SdkPackets.NetEvent ev)
    {
        return global::Modstoke.Loader.Packets.Instance.Unregister(ev);
    }

    // Preserve the writer-based clone required by the Il2Cpp packet wrapper.
    public UdpPacket ClonePacket(UdpPacket packet)
    {
        return global::Modstoke.Loader.Packets.ClonePacket(packet);
    }

    public void HandlePacket(UdpPacket packet, BoltConnection fromConnection)
    {
        global::Modstoke.Loader.Packets.Instance.HandlePacket(packet, fromConnection);
    }

    public void SendPacket(SdkPackets.EventPacket packet, uint? skipConn)
    {
        global::Modstoke.Loader.Packets.Instance.SendPacket(packet, skipConn);
    }

    public SdkPackets.EventPacket GetPacket(int typeId, int size, GlobalTargets targets)
    {
        return global::Modstoke.Loader.Packets.Instance.GetPacket(typeId, size, targets);
    }

    public SdkPackets.EventPacket GetPacket(int typeId, int size, uint targetConnectionId)
    {
        return global::Modstoke.Loader.Packets.Instance.GetPacket(typeId, size, targetConnectionId);
    }

    public bool PacketReceived(BoltConnection __instance, UdpPacket udpPacket)
    {
        return global::Modstoke.Loader.Packets.ReceivePacket(udpPacket, __instance);
    }

    public bool PacketDelivered(BoltConnection __instance, Packet packet)
    {
        return global::Modstoke.Loader.Packets.DeliverPacket(packet);
    }

    public bool ChatEventPatch(CoopServerCallbacks __instance, ChatEvent evnt)
    {
        return global::Modstoke.Loader.Packets.ReceiveChatEvent(evnt);
    }

    public void Patch()
    {
        global::Modstoke.Loader.Packets.Instance.Initialize();
    }
}
