// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using HarmonyLib;
using Modstoke.Loader;
using UdpKit;

namespace Modstoke.Sons
{
    public static partial class ClientEnrollment
    {
        private static HarmonySupportSnapshot _gateSupport;
        private static bool _gatePatchAttempted;
        private static bool _gateHooksInstalled;
        private static volatile bool _gateSelected;
        private static IntPtr _disconnectClientPointer;
        private static uint _disconnectClientConnectionId;
        private static Guid _clientPeerId;
        private static bool _clientDisconnectAttempted;

        internal static bool GameplayGateSelected => _gateSelected;

        internal static bool AllowsGameTraffic(ControlPeer peer)
        {
            if (!_gateSelected || !OnOwnerThread) return false;
            var connection = ControlTransport.Resolve(peer);
            // SDK traffic needs an enrolled peer; a released connection only resumes ordinary Bolt gameplay.
            return connection != null && ReadGate(connection) == GateDecision.Allow
                && Entries.TryGetValue(peer.PeerId, out var entry) && entry.State == EnrollmentState.Enrolled;
        }

        /// <summary>
        /// Selects enrollment with a temporary gameplay-packet gate before Bolt starts.
        /// Servers reject client game packets until enrollment; clients pause ordinary Bolt
        /// serialization while still receiving server state. Rejection requests disconnect. A host
        /// that sends no enrollment within the negotiation window releases the client instead.
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
                ReportFailure(error);
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
                ReportFailure(error);
                return false;
            }
        }

        private static bool BeforeGamePacket(BoltConnection __instance, UdpPacket __0, bool __runOriginal)
        {
            if (!__runOriginal)
                return false;
            if (!_gateSelected)
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
                    if (decision == GateDecision.Reject)
                        DisconnectServerPeer(__instance);
                }
            }
            catch (Exception error) { ReportFailure(error); }

            // The original owns disposal only when it runs. Do not retry disposal after an exception.
            try
            {
                if (__0 != null && !__0.WasCollected && !__0.IsPooled)
                    __0.Dispose();
            }
            catch (Exception error) { ReportFailure(error); }
            return false;
        }

        private static bool BeforeGameSend(BoltConnection __instance, bool __runOriginal)
        {
            if (!__runOriginal)
                return false;
            if (!_gateSelected)
                return true;
            try
            {
                if (!OnOwnerThread || !BoltNetwork.isRunning)
                    return false;
                if (BoltNetwork.isServer)
                    return true;
                if (!BoltNetwork.isClient || !IsCurrentServer(__instance))
                    return false;
                // A listener can attempt a send before Connected's postfix observes its control peer.
                // Hold that send; the normal update reconciles setup before deciding to disconnect.
                var peer = ControlTransport.FindPeer(__instance);
                if (peer == null)
                    return false;
                ObserveClientGeneration(__instance, peer);
                var decision = ReadGate(__instance, peer);
                if (decision == GateDecision.Reject)
                    DisconnectClient(__instance);
                else if (decision == GateDecision.Allow && Entries.TryGetValue(peer.PeerId, out var entry)
                    && ReferenceEquals(entry.Peer, peer) && entry.State == EnrollmentState.HostNotParticipating)
                    entry.SentWhileReleased = true;
                return decision == GateDecision.Allow;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return false;
            }
        }

        internal static void TickGate()
        {
            if (!_gateSelected || !OnOwnerThread)
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
            catch (Exception error) { ReportFailure(error); }
        }

        private static GateDecision ReadGate(BoltConnection connection, ControlPeer peer = null)
        {
            if (!_gateHooksInstalled || !_selected || _faulted
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
            Refresh(entry, Now);
            return entry.State switch
            {
                EnrollmentState.Pending => GateDecision.Hold,
                EnrollmentState.HostNotParticipating => GateDecision.Allow,
                EnrollmentState.Enrolled => ContentAgreement.Admits(peer) switch
                {
                    null => GateDecision.Hold,
                    false => GateDecision.Reject,
                    _ => ModAdmission.ReadGate(peer) switch
                    {
                        ModAdmissionState.Pending => GateDecision.Hold,
                        ModAdmissionState.Admitted => GateDecision.Allow,
                        _ => GateDecision.Reject
                    }
                },
                _ => GateDecision.Reject
            };
        }

        private static void DisconnectServerPeer(BoltConnection connection)
        {
            var session = ServerSessions.ResolveSource(connection);
            if (session != null)
                Refuse(session, connection);
        }

        // A server with mod content says why; other gate refusals keep the plain disconnect.
        private static void Refuse(ServerSession session, BoltConnection connection)
        {
            if (ServerSessions.HasDisconnectAttempt(session)) return;
            var notice = ContentAgreement.RefusalNotice(ControlTransport.FindPeer(connection));
            if (notice == null || ServerSessions.DisconnectWithNotice(session, notice) != DisconnectResult.Requested)
                ServerSessions.Disconnect(session);
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
            connection.Disconnect();
        }

        private static void ResetGateConnection()
        {
            _disconnectClientPointer = IntPtr.Zero;
            _disconnectClientConnectionId = 0;
            _clientPeerId = Guid.Empty;
            _clientDisconnectAttempted = false;
        }

        internal static void DisconnectingForGate(BoltConnection connection)
        {
            if (_gateSelected && OnOwnerThread && connection != null
                && connection.Pointer == _disconnectClientPointer
                && connection.ConnectionId == _disconnectClientConnectionId)
                ResetGateConnection();
        }

        private enum GateDecision { Hold, Allow, Reject }
    }
}
