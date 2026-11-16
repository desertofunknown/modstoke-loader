// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using HarmonyLib;
using Modstoke.Loader;
using UdpKit;

namespace Modstoke.Sons
{
    public static partial class ClientEnrollment
    {
        private static readonly Dictionary<(IntPtr, uint), HeldTraffic> Held = new();
        // Once admitted, a connection stays open for the rest of its life: a later transport fault or a failed
        // check elsewhere must never turn into dropped gameplay for players already in. A client latches the same
        // way, once admitted or once released by a host that doesn't take part.
        private static readonly HashSet<(IntPtr, uint)> AdmittedConnections = new();
        private static HarmonySupportSnapshot _gateSupport;
        private static bool _gatePatchAttempted;
        private static bool _gateHooksInstalled;
        private static volatile bool _gateSelected;
        private static volatile bool _contentGate;
        private static IntPtr _disconnectClientPointer;
        private static uint _disconnectClientConnectionId;
        private static Guid _clientPeerId;
        private static bool _clientDisconnectAttempted;
        private static (IntPtr, uint) _clientAdmitted;
        private static (IntPtr, uint) _clientReleased;
        private static bool _clientSentWhileReleased;
        private static int _clientHeldSends;

        internal static bool GameplayGateSelected => _gateSelected || _contentGate;

        internal static bool AllowsGameTraffic(ControlPeer peer)
        {
            if (!GameplayGateSelected || !OnOwnerThread) return false;
            var connection = ControlTransport.Resolve(peer);
            // SDK traffic needs an enrolled peer; a released connection only resumes ordinary Bolt gameplay.
            return connection != null && ReadGate(connection) == GateDecision.Allow
                && Entries.TryGetValue(peer.PeerId, out var entry) && entry.State == EnrollmentState.Enrolled;
        }

        /// <summary>Whether this client may send gameplay to its server: no gate, or the server let it in.</summary>
        internal static bool ClientReleased(BoltConnection server)
            => !GameplayGateSelected || OnOwnerThread && server != null && ReadGate(server) == GateDecision.Allow;

        /// <summary>Whether the current server admitted this client through the negotiation.</summary>
        internal static bool ClientWasAdmitted => _clientAdmitted.Item1 != IntPtr.Zero;

        /// <summary>
        /// Selects enrollment with a temporary gameplay-packet gate before Bolt starts.
        /// Servers hold client game packets until admission; clients pause ordinary Bolt
        /// serialization while still receiving server state. Rejection requests disconnect. A host
        /// that says it does not take part, or never said it runs the negotiation, releases the client; one
        /// that said it does but stays silent through the negotiation window makes the client leave.
        /// This does not enforce combat or make client capability claims trustworthy.
        /// </summary>
        public static ControlResult SelectGatedForNextStart(bool selected,
            ulong localCapabilities, ulong requiredClientCapabilities)
        {
            if (_ownerThreadId == 0)
                return ControlResult.Unavailable;
            if (!OnOwnerThread)
                return ControlResult.WrongThread;
            try
            {
                // Check startup before installing additional hooks in a running session.
                var socket = BoltCore._udpSocket;
                if (BoltNetwork.isRunning || (socket != null && !socket.WasCollected
                    && socket.State != UdpSocketState.Shutdown))
                    return ControlResult.AlreadyStarting;
                if (selected && !ServerSessions.HooksInstalled)
                    return ControlResult.Unavailable;
                if (selected && !InstallGateHooks())
                    return ControlResult.Unavailable;
                var result = SelectForNextStart(selected, localCapabilities, requiredClientCapabilities);
                if (result == ControlResult.Success)
                    _gateSelected = selected;
                return result;
            }
            catch (Exception error)
            {
                NetworkFailures.Report("Modstoke client enrollment", error);
                return ControlResult.Failed;
            }
        }

        private static bool InstallGateHooks()
        {
            if (_gatePatchAttempted)
                return _gateHooksInstalled;
            _gatePatchAttempted = true;
            if (_gateSupport == null || !_gateSupport.SafeForHarmonyPatches)
                return false;
            try
            {
                var receive = AccessTools.Method(typeof(BoltConnection), nameof(BoltConnection.PacketReceived),
                    new[] { typeof(UdpPacket) });
                var send = AccessTools.Method(typeof(BoltConnection), nameof(BoltConnection.Send), Type.EmptyTypes);
                if (receive == null || send == null)
                    throw new MissingMethodException("A client enrollment packet hook is unavailable.");
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-enrollment-gate");
                harmony.Patch(receive, prefix: new HarmonyMethod(typeof(ClientEnrollment), nameof(BeforeGamePacket))
                    { priority = Priority.First });
                harmony.Patch(send, prefix: new HarmonyMethod(typeof(ClientEnrollment), nameof(BeforeGameSend))
                    { priority = Priority.First });
                _gateHooksInstalled = true;
                return true;
            }
            catch (Exception error)
            {
                NetworkFailures.Report("Modstoke client enrollment", error);
                return false;
            }
        }

        private static bool BeforeGamePacket(BoltConnection __instance, UdpPacket __0, bool __runOriginal)
        {
            if (!__runOriginal)
                return false;
            if (!GameplayGateSelected)
                return true;
            try
            {
                // Keep server-to-client initialization and replication intact while the client waits.
                if (OnOwnerThread && BoltNetwork.isRunning && BoltNetwork.isClient)
                    return true;
                if (OnOwnerThread && BoltNetwork.isRunning && BoltNetwork.isServer)
                {
                    var decision = ReadGate(__instance);
                    if (decision == GateDecision.Allow)
                        return true;
                    // UdpKit acknowledged this packet before any hook ran, so the sender will never resend it. SDK
                    // packets are unreliable by contract; a Bolt game packet means the sender is not holding as a
                    // negotiating client does, and its reliable streams would be missing this packet for good.
                    if (Hold(__instance, __0) || decision == GateDecision.Reject)
                        DisconnectServerPeer(__instance);
                }
            }
            catch (Exception error)
            {
                NetworkFailures.Report("Modstoke enrollment gate", error);
                // The packet is dropped below either way, so the connection must not carry on without it.
                try { if (BoltNetwork.isServer) __instance.Disconnect(); }
                catch (Exception disconnectError) { NetworkFailures.Report("Modstoke enrollment gate", disconnectError); }
            }

            // The original owns disposal only when it runs. Do not retry disposal after an exception.
            try
            {
                if (__0 != null && !__0.WasCollected && !__0.IsPooled)
                    __0.Dispose();
            }
            catch (Exception error) { NetworkFailures.Report("Modstoke enrollment gate", error); }
            return false;
        }

        // Counts a held packet; true when it was gameplay, which ends the connection's negotiation.
        private static bool Hold(BoltConnection connection, UdpPacket packet)
        {
            var key = (connection.Pointer, connection.ConnectionId);
            if (!Held.TryGetValue(key, out var held))
                Held[key] = held = new HeldTraffic();
            if (global::Modstoke.Loader.Packets.IsSdkPacket(packet))
            {
                held.Sdk++;
                return false;
            }
            held.Game++;
            var entry = FindEntry(connection);
            if (entry != null) Retire(entry, EnrollmentEndReason.GameplayBeforeAdmission);
            return true;
        }

        private static bool BeforeGameSend(BoltConnection __instance, bool __runOriginal)
        {
            if (!__runOriginal)
                return false;
            if (!GameplayGateSelected)
                return true;
            try
            {
                if (!OnOwnerThread || !BoltNetwork.isRunning)
                    return false;
                if (BoltNetwork.isServer)
                    return true;
                if (!BoltNetwork.isClient || !IsCurrentServer(__instance))
                    return false;
                // Ahead of the peer lookup: a negotiation peer lost after the latch must not freeze a player in.
                var key = (__instance.Pointer, __instance.ConnectionId);
                if (_clientAdmitted == key)
                    return true;
                if (_clientReleased == key)
                {
                    _clientSentWhileReleased = true;
                    return true;
                }
                // A listener can attempt a send before Connected's postfix observes its control peer.
                // Hold that send; the normal update reconciles setup before deciding to disconnect.
                var peer = ControlTransport.FindPeer(__instance);
                if (peer == null)
                {
                    _clientHeldSends++;
                    return false;
                }
                ObserveClientGeneration(__instance, peer);
                var decision = ReadGate(__instance, peer);
                if (decision == GateDecision.Reject)
                    DisconnectClient(__instance);
                else if (decision == GateDecision.Allow && _clientReleased == key)
                    _clientSentWhileReleased = true;
                if (decision != GateDecision.Allow)
                    _clientHeldSends++;
                return decision == GateDecision.Allow;
            }
            catch (Exception error)
            {
                NetworkFailures.Report("Modstoke enrollment gate", error);
                return false;
            }
        }

        internal static void TickGate()
        {
            if (!GameplayGateSelected || !OnOwnerThread)
                return;
            try
            {
                if (!BoltNetwork.isRunning)
                    return;
                if (BoltNetwork.isServer)
                {
                    foreach (var session in ServerSessions.Sessions)
                    {
                        var connection = ServerSessions.ResolveCurrent(session);
                        if (connection != null && ReadGate(connection) == GateDecision.Reject)
                            Refuse(session, connection);
                    }
                }
                else if (BoltNetwork.isClient && BoltNetwork.isConnected)
                {
                    var server = BoltNetwork.server;
                    if (server == null || server.WasCollected)
                        return;
                    var peer = ControlTransport.FindPeer(server);
                    ObserveClientGeneration(server, peer);
                    if (ReadGate(server, peer) == GateDecision.Reject)
                        DisconnectClient(server);
                }
            }
            catch (Exception error) { NetworkFailures.Report("Modstoke enrollment gate", error); }
        }

        private static GateDecision ReadGate(BoltConnection connection, ControlPeer peer = null)
        {
            var key = (connection.Pointer, connection.ConnectionId);
            if (BoltNetwork.isServer ? AdmittedConnections.Contains(key) : _clientAdmitted == key || _clientReleased == key)
                return GateDecision.Allow;
            if (!_gateHooksInstalled || !Selected || _faulted
                || ControlTransport.State != ControlState.Active)
                return GateDecision.Reject;
            peer ??= ControlTransport.FindPeer(connection);
            var entry = Observe(peer);
            // Hold a server connection while session tracking recovers and until a peer deferred meanwhile is bound.
            if (entry == null)
                return BoltNetwork.isServer && (ServerSessions.Recovering || ControlTransport.AwaitsPeer(connection))
                    ? GateDecision.Hold : GateDecision.Reject;
            if (peer.ServerSession != null
                ? ServerSessions.HasDisconnectAttempt(peer.ServerSession)
                : _clientDisconnectAttempted && _disconnectClientPointer == connection.Pointer
                    && _disconnectClientConnectionId == connection.ConnectionId)
                return GateDecision.Reject;
            Refresh(entry);
            switch (entry.State)
            {
                case EnrollmentState.Pending:
                    return GateDecision.Hold;
                case EnrollmentState.HostNotParticipating:
                    _clientReleased = key;
                    return GateDecision.Allow;
                case EnrollmentState.Enrolled when peer.ServerSession != null:
                    return entry.Admitted ? GateDecision.Allow : TryAdmit(entry);
                case EnrollmentState.Enrolled:
                    if (!entry.Admitted)
                        return GateDecision.Hold;
                    // Mod admission is the host's to require; when it did not, its Admitted is the whole answer.
                    if ((entry.RequiredCapabilities & ModAdmission.ProtocolCapability) != 0)
                    {
                        var admission = ModAdmission.ReadGate(peer);
                        if (admission != ModAdmissionState.Admitted)
                            return admission == ModAdmissionState.Pending ? GateDecision.Hold : GateDecision.Reject;
                    }
                    _clientAdmitted = key;
                    return GateDecision.Allow;
                default:
                    return GateDecision.Reject;
            }
        }

        // The host lets a client in once enrollment, the content check and mod admission all pass, and tells it so;
        // the client sends no gameplay before that, so nothing arrives while the host would still be holding.
        private static GateDecision TryAdmit(Enrollment entry)
        {
            var decision = ContentAgreement.Admits(entry.Peer) switch
            {
                null => GateDecision.Hold,
                false => GateDecision.Reject,
                _ => ModAdmission.ReadGate(entry.Peer) switch
                {
                    ModAdmissionState.Pending => GateDecision.Hold,
                    ModAdmissionState.Admitted => GateDecision.Allow,
                    _ => GateDecision.Reject
                }
            };
            if (decision != GateDecision.Allow)
                return decision;
            var connection = ControlTransport.Resolve(entry.Peer);
            if (connection == null)
                return GateDecision.Hold;
            if (!Send(entry, EnrollmentMessageKind.Admitted))
                return GateDecision.Reject;
            entry.Admitted = true;
            entry.Deadline = _clock + HelloWatchSeconds;
            var key = (connection.Pointer, connection.ConnectionId);
            AdmittedConnections.Add(key);
            Held.TryGetValue(key, out var held);
            RuntimeEntryPoint.LogSource?.LogInfo("Admitted connection " + entry.Peer.ConnectionId + " after "
                + Milliseconds(entry.OpenedAt) + " ms (hello at " + (long)((entry.HelloAt - entry.OpenedAt) * 1000)
                + " ms, enrolled at " + (long)((entry.EnrolledAt - entry.OpenedAt) * 1000) + " ms, mod content "
                + ContentAgreement.Read(entry.Peer) + (ModAdmission.Selected ? ", mod admission passed" : "") + "); "
                + (held?.Sdk ?? 0) + " SDK packets were dropped while it was held.");
            return GateDecision.Allow;
        }

        private static void DisconnectServerPeer(BoltConnection connection)
        {
            var session = ServerSessions.ResolveSource(connection);
            if (session != null)
            {
                Refuse(session, connection);
                return;
            }
            // Paused session tracking leaves no notice to give, but a connection missing a packet still has to end.
            if (!Held.TryGetValue((connection.Pointer, connection.ConnectionId), out var held) || held.Disconnected)
                return;
            held.Disconnected = true;
            RuntimeEntryPoint.LogSource?.LogInfo("Refused connection " + connection.ConnectionId + " while session "
                + "tracking was paused, with no notice. Held " + held.Game + " game and " + held.Sdk + " SDK packets.");
            connection.Disconnect();
        }

        // Every refusal is logged once with its reason, and the player is told why whenever there is a reason to give.
        private static void Refuse(ServerSession session, BoltConnection connection)
        {
            if (ServerSessions.HasDisconnectAttempt(session)) return;
            var peer = ControlTransport.FindPeer(connection);
            var entry = FindEntry(connection);
            Held.TryGetValue((connection.Pointer, connection.ConnectionId), out var held);
            var refused = "Refused connection " + connection.ConnectionId
                + (entry == null ? "" : " after " + Milliseconds(entry.OpenedAt) + " ms") + ": " + Describe(peer, entry)
                + ". Held " + (held?.Game ?? 0) + " game and " + (held?.Sdk ?? 0) + " SDK packets.";
            var notice = RefusalNotice(peer, entry) is { } text ? ServerSessions.FitNotice(text) : null;
            var told = notice == null ? (DisconnectResult?)null : ServerSessions.DisconnectWithNotice(session, notice);
            if (told != DisconnectResult.Requested)
                ServerSessions.Disconnect(session);
            RuntimeEntryPoint.LogSource?.LogInfo(refused + (told == null ? ""
                : told == DisconnectResult.Requested ? " Told the player: " + notice
                : " The notice could not be sent (" + told + "): " + notice));
        }

        // In this server's language: the notice travels in the disconnect token, and only the player's game knows theirs.
        private static string RefusalNotice(ControlPeer peer, Enrollment entry)
        {
            var text = LocalizationStartup.Text;
            // Vanilla and a Modstoke without mod admission look alike here, so a mod admission host names both steps.
            if (entry == null || entry.HelloAt == 0)
                return ContentAgreement.NotTakingPartNotice()
                    ?? text.Get(ModAdmission.Selected ? "kick.installModAdmission" : "kick.installModstoke");
            switch (entry.Reason)
            {
                case EnrollmentEndReason.GameplayBeforeAdmission:
                    return text.Get("kick.outOfStep");
                case EnrollmentEndReason.IncompatibleCapabilities:
                case EnrollmentEndReason.RemoteRejected:
                    // The hello's offer names what the client lacks; it is only a claim, so it only picks the wording.
                    return text.Get((entry.RequiredCapabilities & ~entry.OfferedCapabilities & ModAdmission.ProtocolCapability) != 0
                        ? "kick.turnOnModAdmission" : "kick.otherVersion");
                case EnrollmentEndReason.ProtocolMismatch:
                    return text.Get("kick.otherVersion");
            }
            return ContentAgreement.RefusalNotice(peer) ?? ModAdmission.RefusalNotice(peer)
                ?? (entry.State == EnrollmentState.Retired ? text.Get("kick.joinUnfinished", ("reason", entry.Reason)) : null);
        }

        private static string Describe(ControlPeer peer, Enrollment entry)
        {
            if (entry == null)
                return "it never opened Modstoke negotiation";
            if (entry.HelloAt == 0)
                return entry.Reason == EnrollmentEndReason.GameplayBeforeAdmission
                    ? "it sent gameplay without opening Modstoke negotiation (vanilla, another Modstoke version, or a "
                        + "join that did not know this server checks mods)"
                    : "it never opened Modstoke negotiation";
            return entry.Reason switch
            {
                EnrollmentEndReason.None => ContentAgreement.Describe(peer) ?? "mod admission refused it",
                EnrollmentEndReason.GameplayBeforeAdmission => "it sent gameplay before it was admitted",
                EnrollmentEndReason.Expired => "negotiation did not finish in " + NegotiationSeconds + " s",
                EnrollmentEndReason.RemoteRejected => "it cannot meet this server's requirements ("
                    + entry.RemoteRejection + ")",
                EnrollmentEndReason.ProtocolMismatch => "it speaks another Modstoke negotiation version",
                _ => "negotiation ended: " + entry.Reason
            };
        }

        private static Enrollment FindEntry(BoltConnection connection)
        {
            foreach (var entry in Entries.Values)
                if (entry.Peer.ConnectionPointer == connection.Pointer && entry.Peer.ConnectionId == connection.ConnectionId)
                    return entry;
            return null;
        }

        private static bool IsCurrentServer(BoltConnection connection)
        {
            var server = BoltNetwork.server;
            return connection != null && !connection.WasCollected && server != null && !server.WasCollected
                && server.Pointer == connection.Pointer && server.ConnectionId == connection.ConnectionId;
        }

        private static void ObserveClientGeneration(BoltConnection connection, ControlPeer peer)
        {
            if (_disconnectClientPointer != connection.Pointer || _disconnectClientConnectionId != connection.ConnectionId
                || (peer != null && _clientPeerId != peer.PeerId))
            {
                _disconnectClientPointer = connection.Pointer;
                _disconnectClientConnectionId = connection.ConnectionId;
                _clientPeerId = peer?.PeerId ?? Guid.Empty;
                _clientDisconnectAttempted = false;
            }
        }

        private static void DisconnectClient(BoltConnection connection)
        {
            if (_clientDisconnectAttempted || !IsCurrentServer(connection))
                return;
            // A throw does not prove that the native disconnect event was never queued.
            _clientDisconnectAttempted = true;
            // A lost peer or transport says nothing new, and must not replace a reason the content check already gave.
            var entry = FindEntry(connection);
            if (entry is { State: EnrollmentState.Retired } && entry.Reason != EnrollmentEndReason.HostAnsweredLate)
                KickReason.Expect(KickTitle.CouldNotJoin, LocalizationStartup.Text.Get(LeavingReasonKey(entry)),
                    LocalizationStartup.Text.Get("kick.rejoinHelp"));
            connection.Disconnect();
        }

        // What the player reads when this client leaves on its own; a refusal the host sends first replaces it.
        private static string LeavingReasonKey(Enrollment entry) => entry.Reason switch
        {
            EnrollmentEndReason.Expired => "kick.checkTimedOut",
            EnrollmentEndReason.IncompatibleCapabilities or EnrollmentEndReason.RemoteRejected => "kick.checkNeedsMore",
            EnrollmentEndReason.InvalidMessage or EnrollmentEndReason.ProtocolMismatch => "kick.checkMismatch",
            _ => "kick.checkBrokeOff"
        };

        private static void ResetGateConnection()
        {
            _disconnectClientPointer = IntPtr.Zero;
            _disconnectClientConnectionId = 0;
            _clientPeerId = Guid.Empty;
            _clientDisconnectAttempted = false;
        }

        private static void ResetAdmissions()
        {
            Held.Clear();
            AdmittedConnections.Clear();
            ResetClientLatch();
        }

        private static void ResetClientLatch()
        {
            _clientAdmitted = _clientReleased = default;
            _clientSentWhileReleased = false;
            _clientHeldSends = 0;
        }

        internal static void DisconnectingForGate(BoltConnection connection)
        {
            if (!OnOwnerThread || connection == null)
                return;
            var key = (connection.Pointer, connection.ConnectionId);
            Held.Remove(key);
            AdmittedConnections.Remove(key);
            if (connection.Pointer == _disconnectClientPointer && connection.ConnectionId == _disconnectClientConnectionId)
            {
                ResetGateConnection();
                ResetClientLatch();
            }
        }

        private enum GateDecision { Hold, Allow, Reject }

        private sealed class HeldTraffic
        {
            internal int Sdk;
            internal int Game;
            internal bool Disconnected;
        }
    }
}
