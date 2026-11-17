// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
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
        // A gated client's SDK packets would only be counted and dropped by a server still holding it, so they wait
        // here, bounded, until it is admitted. The host keeps the same number for a client that hasn't shown a loader.
        private const int MaxHeldPackets = 32;
        // A Modstoke client shows its loader within a second or so of connecting; one silent this long never will.
        private const long LoaderWaitMilliseconds = 30_000;
        // Each client's allowance of SDK packets on the host, generous next to any mod's real traffic, so one client
        // flooding a channel can't take the host's frame with it. A drop is evidence only, never a strike.
        private const double BudgetPerSecond = 600, BudgetBurst = 1200;
        private const long BudgetReportMilliseconds = 10_000, BudgetForgetMilliseconds = 60_000;
        private static readonly Queue<UdpPacket> HeldSends = new();
        private static readonly Dictionary<int, string> DefaultedRelay = new();
        private static readonly HashSet<int> DefaultedRelayLogged = new();
        private static readonly Dictionary<(IntPtr, uint), Budget> Budgets = new();
        private static readonly Dictionary<(IntPtr, uint), AwaitingLoader> Awaiting = new();
        private static (IntPtr, uint) _heldFor;
        private static long _heldSince;
        private static int _heldDropped;
        private static bool _initialized;

        private sealed class Budget
        {
            internal double Tokens = BudgetBurst;
            internal long Refilled = Environment.TickCount64, NextReport;
            internal int Dropped;
        }

        private sealed class AwaitingLoader(BoltConnection connection)
        {
            internal readonly BoltConnection Connection = connection;
            internal readonly Queue<UdpPacket> Packets = new();
            internal readonly long Since = Environment.TickCount64;
            internal int Dropped;
            internal bool GaveUp;
        }

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
            var relay = netEvent.AllowClientRelay && !DefaultsRelayOff(netEvent);
            if (!SdkPackets.RegisteredEvents.TryAdd(hash, new(hash, netEvent.Read, relay)))
                throw new Exception($"Net Event with id '{netEvent.Id}' already registered");
            if (relay != netEvent.AllowClientRelay)
                DefaultedRelay[hash] = netEvent.Id;
        }

        public bool Unregister(SdkPackets.NetEvent netEvent)
        {
            ArgumentNullException.ThrowIfNull(netEvent);
            var hash = netEvent.GetIdHash();
            if (!SdkPackets.RegisteredEvents.TryGetValue(hash, out var registration)
                || !ReferenceEquals(registration.ReadFunc.Target, netEvent) || !SdkPackets.RegisteredEvents.Remove(hash))
                return false;
            DefaultedRelay.Remove(hash);
            return true;
        }

        // SonsSdk lets a client relay through the host unless the event says otherwise, and RedLoader mods rely on
        // that. A Modstoke mod's event that never said is server-authoritative instead, like all of ours.
        private static bool DefaultsRelayOff(SdkPackets.NetEvent netEvent)
        {
            var type = netEvent.GetType();
            if (type.GetProperty(nameof(SdkPackets.NetEvent.AllowClientRelay))?.GetMethod?.DeclaringType
                != typeof(SdkPackets.NetEvent))
                return false;
            if (type.Assembly == typeof(Packets).Assembly)
                return true;
            string location;
            try { location = type.Assembly.IsDynamic ? "" : type.Assembly.Location; }
            catch (NotSupportedException) { return false; }
            foreach (var candidate in ModHost.GetCandidates())
                if (location.Length > 0 && string.Equals(candidate.AssemblyPath, location, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        public void HandlePacket(UdpPacket packet, BoltConnection fromConnection)
        {
            if (!TryReadHeader(packet, out var target, out var targetConnection, out var id))
                return;
            if (BoltNetwork.isServer && !WithinBudget(fromConnection))
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
                {
                    // Off only by default, the channel is still the host's to read, as it was; just the forward stops.
                    if (!DefaultedRelay.TryGetValue(id, out var name)) return;
                    var read = target is GlobalTargets.Everyone or GlobalTargets.Others;
                    if (DefaultedRelayLogged.Add(id))
                        RuntimeEntryPoint.LogSource?.LogInfo("Did not relay a packet on " + name + " from connection "
                            + fromConnection.ConnectionId + " to other clients" + (read ? " (this host read it)" : "")
                            + ": a Modstoke mod's packet channel relays only when it sets AllowClientRelay.");
                    if (read) registration.ReadFunc(packet, fromConnection);
                    return;
                }
                if (target is GlobalTargets.OnlyServer or GlobalTargets.Everyone or GlobalTargets.Others)
                    registration.ReadFunc(packet, fromConnection);

                if (target == GlobalTargets.OnlyServer)
                    return;

                var relay = new SdkPackets.EventPacket(ClonePacket(packet), target, targetConnection);
                Send(relay, target == GlobalTargets.Others ? fromConnection.ConnectionId : null, fromConnection);
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

        public void SendPacket(SdkPackets.EventPacket packet, uint? skipConnection) => Send(packet, skipConnection, null);

        // A relayed packet costs the client that sent it one packet of its budget per recipient, on top of the one
        // its receipt cost, so one client can't turn its allowance into that many times the host's upload. The relay
        // goes to everyone or no one: one that reached only the clients early in the list would leave the rest out
        // of step, and a refused large relay leaves the sender's own small packets their allowance.
        private static void Send(SdkPackets.EventPacket packet, uint? skipConnection, BoltConnection relayedFor)
        {
            if (!BoltNetwork.isRunning)
                return;

            ArgumentNullException.ThrowIfNull(packet);
            if (packet.Packet == null || packet.Packet.WasCollected)
                throw new ArgumentException("A live packet is required.", nameof(packet));
            RequireFits(packet.Packet);

            if (BoltNetwork.isClient)
            {
                var server = BoltNetwork.server;
                if (!global::Modstoke.Sons.ClientEnrollment.ClientReleased(server))
                {
                    Hold(server, packet.Packet);
                    return;
                }
                FlushHeld();
                SendToConnection(server, packet.Packet);
                return;
            }

            var broadcast = packet.Targets is GlobalTargets.Everyone or GlobalTargets.Others or GlobalTargets.AllClients;
            if (!broadcast && packet.Targets != GlobalTargets.OnlySelf) return;
            if (relayedFor != null)
            {
                var recipients = CountRecipients(packet, skipConnection, broadcast);
                if (recipients > 0 && !WithinBudget(relayedFor, recipients))
                    return;
            }
            var clients = BoltNetwork.clients.GetEnumerator();
            try
            {
                var iterator = clients.Cast<Il2CppSystem.Collections.IEnumerator>();
                while (iterator.MoveNext())
                {
                    var connection = clients.Current;
                    if (!Addressed(packet, connection, skipConnection, broadcast)) continue;
                    if (!ShowsLoader(connection))
                    {
                        AwaitLoader(connection, packet.Packet);
                        continue;
                    }
                    if (Awaiting.Count > 0) SendAwaiting(connection);
                    SendToConnection(connection, packet.Packet);
                }
            }
            finally { clients.Cast<Il2CppSystem.IDisposable>().Dispose(); }
        }

        private static bool Addressed(SdkPackets.EventPacket packet, BoltConnection connection, uint? skipConnection, bool broadcast)
            => broadcast ? skipConnection == null || connection.ConnectionId != skipConnection.Value
                : connection.ConnectionId == packet.TargetConnectionId;

        // The clients a send reaches or holds for, leaving out ones the host gave up holding for.
        private static int CountRecipients(SdkPackets.EventPacket packet, uint? skipConnection, bool broadcast)
        {
            int count = 0;
            var clients = BoltNetwork.clients.GetEnumerator();
            try
            {
                var iterator = clients.Cast<Il2CppSystem.Collections.IEnumerator>();
                while (iterator.MoveNext())
                {
                    var connection = clients.Current;
                    if (!Addressed(packet, connection, skipConnection, broadcast)) continue;
                    if (!ShowsLoader(connection) && Awaiting.TryGetValue((connection.Pointer, connection.ConnectionId), out var waiting)
                        && waiting.GaveUp) continue;
                    count++;
                }
            }
            finally { clients.Cast<Il2CppSystem.IDisposable>().Dispose(); }
            return count;
        }

        // A client without a loader hands an SDK packet to Bolt as a game packet, so the host sends them only to
        // clients that showed one (see ClientSignals.ShowsLoader); one that can't be told gets them as before.
        private static bool ShowsLoader(BoltConnection connection)
            => global::Modstoke.Sons.ClientSignals.ShowsLoader(connection) != false;

        // Until a client shows a loader its latest packets wait here, so a mod's welcome sent as it joins still
        // arrives. One that stays silent (vanilla, or RedLoader with mods that only listen) gets none.
        private static void AwaitLoader(BoltConnection connection, UdpPacket packet)
        {
            var key = (connection.Pointer, connection.ConnectionId);
            if (!Awaiting.TryGetValue(key, out var waiting))
            {
                Awaiting[key] = waiting = new AwaitingLoader(connection);
                RuntimeEntryPoint.LogSource?.LogInfo("Holding SDK packets for connection " + connection.ConnectionId
                    + " until it shows a Modstoke Loader (enrollment, a notices hello or an SDK packet of its own).");
            }
            if (waiting.GaveUp || GiveUpOn(waiting))
                return;
            if (waiting.Packets.Count >= MaxHeldPackets)
            {
                waiting.Packets.Dequeue();
                waiting.Dropped++;
            }
            waiting.Packets.Enqueue(ClonePacket(packet, packet.Ptr));
        }

        private static bool GiveUpOn(AwaitingLoader waiting)
        {
            if (Environment.TickCount64 - waiting.Since < LoaderWaitMilliseconds)
                return false;
            waiting.GaveUp = true;
            RuntimeEntryPoint.LogSource?.LogInfo("Dropped " + (waiting.Packets.Count + waiting.Dropped) + " SDK packets "
                + "held for connection " + waiting.Connection.ConnectionId + ": it showed no Modstoke Loader in "
                + LoaderWaitMilliseconds / 1000 + " s (a vanilla client, or RedLoader with mods that only listen). It "
                + "gets them again once it shows one, for example by sending an SDK packet of its own.");
            waiting.Packets.Clear();
            waiting.Dropped = 0;
            return true;
        }

        private static void SendAwaiting(BoltConnection connection)
        {
            if (!Awaiting.Remove((connection.Pointer, connection.ConnectionId), out var waiting))
                return;
            var sent = waiting.Packets.Count;
            while (waiting.Packets.Count > 0)
                connection._udp.Send(waiting.Packets.Dequeue());
            RuntimeEntryPoint.LogSource?.LogInfo("Connection " + connection.ConnectionId + " showed a Modstoke Loader "
                + (Environment.TickCount64 - waiting.Since) + " ms after its first held SDK packet; sent it " + sent
                + " held" + (waiting.Dropped == 0 ? "." : ", " + waiting.Dropped + " older ones were dropped over the "
                    + "limit of " + MaxHeldPackets + "."));
        }

        private static void Hold(BoltConnection server, UdpPacket packet)
        {
            if (server == null || server.WasCollected)
                return;
            var key = (server.Pointer, server.ConnectionId);
            if (_heldFor != key)
            {
                DropHeld();
                _heldFor = key;
                _heldSince = Environment.TickCount64;
                RuntimeEntryPoint.LogSource?.LogInfo("Holding SDK packets until the server admits this client.");
            }
            if (HeldSends.Count >= MaxHeldPackets)
            {
                HeldSends.Dequeue();
                _heldDropped++;
            }
            HeldSends.Enqueue(ClonePacket(packet, packet.Ptr));
        }

        /// <summary>
        /// Sends what a gated client held once its server admitted it, and what the host held for a client once it
        /// showed a loader; drops what waited for a connection that ended or stayed silent too long.
        /// </summary>
        internal static void FlushHeld()
        {
            if (Awaiting.Count > 0)
                FlushAwaiting();
            if (_heldFor == default)
                return;
            var server = BoltNetwork.isClient ? BoltNetwork.server : null;
            if (server == null || server.WasCollected || (server.Pointer, server.ConnectionId) != _heldFor)
            {
                DropHeld();
                return;
            }
            if (!global::Modstoke.Sons.ClientEnrollment.ClientReleased(server))
                return;
            var sent = HeldSends.Count;
            while (HeldSends.Count > 0)
                server._udp.Send(HeldSends.Dequeue());
            RuntimeEntryPoint.LogSource?.LogInfo("Sent " + sent + " SDK packets held until the server admitted this "
                + "client, " + (Environment.TickCount64 - _heldSince) + " ms after the first" + (_heldDropped == 0 ? "."
                    : "; " + _heldDropped + " older ones were dropped over the limit of " + MaxHeldPackets + "."));
            _heldFor = default;
            _heldDropped = 0;
        }

        private static void DropHeld()
        {
            if (HeldSends.Count + _heldDropped > 0)
                RuntimeEntryPoint.LogSource?.LogInfo("Dropped " + (HeldSends.Count + _heldDropped) + " SDK packets held "
                    + "for a server connection that ended before admitting this client.");
            HeldSends.Clear();
            _heldFor = default;
            _heldDropped = 0;
        }

        private static void FlushAwaiting()
        {
            List<(IntPtr, uint)> gone = null;
            List<AwaitingLoader> shown = null;
            foreach (var pair in Awaiting)
            {
                var waiting = pair.Value;
                if (waiting.Connection.WasCollected)
                    (gone ??= new()).Add(pair.Key);
                else if (!waiting.GaveUp && ShowsLoader(waiting.Connection))
                    (shown ??= new()).Add(waiting);
                else if (!waiting.GaveUp)
                    GiveUpOn(waiting);
            }
            if (gone != null)
                foreach (var key in gone) Awaiting.Remove(key);
            if (shown != null)
                foreach (var waiting in shown) SendAwaiting(waiting.Connection);
        }

        /// <summary>Forgets what the host held for a client that left before showing a loader.</summary>
        internal static void ConnectionClosed(BoltConnection connection)
        {
            if (!Awaiting.Remove((connection.Pointer, connection.ConnectionId), out var waiting) || waiting.GaveUp)
                return;
            if (waiting.Packets.Count + waiting.Dropped > 0)
                RuntimeEntryPoint.LogSource?.LogInfo("Dropped " + (waiting.Packets.Count + waiting.Dropped) + " SDK "
                    + "packets held for connection " + connection.ConnectionId + ", which left before showing a Modstoke "
                    + "Loader.");
        }

        internal static void ResetForStart()
        {
            DropHeld();
            Awaiting.Clear();
        }

        // Takes all of cost or nothing; a refusal counts as one dropped packet.
        private static bool WithinBudget(BoltConnection connection, int cost = 1)
        {
            var now = Environment.TickCount64;
            var key = (connection.Pointer, connection.ConnectionId);
            if (!Budgets.TryGetValue(key, out var budget))
            {
                if (Budgets.Count >= 64)
                    foreach (var entry in Budgets)
                        if (now - entry.Value.Refilled > BudgetForgetMilliseconds) Budgets.Remove(entry.Key);
                Budgets[key] = budget = new Budget();
            }
            budget.Tokens = System.Math.Min(BudgetBurst, budget.Tokens + (now - budget.Refilled) * BudgetPerSecond / 1000);
            budget.Refilled = now;
            if (budget.Tokens >= cost)
            {
                budget.Tokens -= cost;
                return true;
            }
            budget.Dropped++;
            global::Modstoke.Sons.ClientSignals.RecordOverBudgetSdkPacket(connection);
            if (now >= budget.NextReport)
            {
                budget.NextReport = now + BudgetReportMilliseconds;
                RuntimeEntryPoint.LogSource?.LogWarning("Connection " + connection.ConnectionId + " is over its SDK packet "
                    + "budget (" + BudgetPerSecond + " a second, bursts of " + BudgetBurst + "); dropped " + budget.Dropped
                    + " since the last report. Recorded as evidence only.");
                budget.Dropped = 0;
            }
            return false;
        }

        // UdpKit writes a packet whole behind its pipe header into the socket's send buffer and never splits it, so
        // one over the datagram size can't go out; it is refused here with the sizes rather than failing in native code.
        private static void RequireFits(UdpPacket packet)
        {
            var socket = BoltCore._udpSocket;
            if (socket == null || socket.WasCollected)
                return;
            var config = socket.PacketPipeConfig;
            var buffer = socket.sendBuffer;
            if (buffer == null || buffer.WasCollected || config.DatagramSize <= 0 || config.SequenceBytes < 0 || config.AckBytes < 0)
                return;
            long limit = System.Math.Min(config.DatagramSize, buffer.Length) - (2L * config.SequenceBytes + config.AckBytes + 3);
            long bytes = (packet.Ptr + 7L) / 8;
            if (bytes > limit)
                throw new ArgumentException("This SDK packet is " + bytes + " bytes, but one datagram carries at most "
                    + limit + " on this connection, so it was not sent. Send less per packet.", nameof(packet));
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

        /// <summary>Whether a received packet is an SDK packet or datagram rather than a Bolt game packet.</summary>
        internal static bool IsSdkPacket(UdpPacket packet) => HasMarker(packet);

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

        // An SDK packet goes out on the connection's socket directly, so it carries none of Bolt's own packet records.
        // Bolt is told when it arrives or is lost all the same, and its channels throw on the missing record: once
        // per packet, caught and logged inside Bolt. Neither notice means anything to Bolt, so both stop here.
        internal static bool DeliverPacket(Packet packet)
        {
            if (packet == null || packet.WasCollected) return false;
            return !HasMarker(packet.UdpPacket);
        }

        internal static bool LosePacket(Packet packet) => packet != null && !packet.WasCollected;

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

        // A mod's Read throwing lands here too, so the first stack names the mod.
        private static void ReportReceiveFailure(Exception error)
            => global::Modstoke.Sons.NetworkFailures.Report("SDK packet receive", error);

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

            [HarmonyPatch(typeof(BoltConnection), nameof(BoltConnection.PacketLost), typeof(Packet))]
            [HarmonyPrefix]
            private static bool PacketLost(BoltConnection __instance, Packet packet, bool __runOriginal)
            {
                return __runOriginal && LosePacket(packet);
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
