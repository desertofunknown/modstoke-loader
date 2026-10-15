// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using Bolt;
using Endnight.Extensions;
using HarmonyLib;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;
using Sons.Multiplayer;

namespace OpenACAI.Sons
{
    /// <summary>Short, targeted notices for clients with OpenACAI installed.</summary>
    public static partial class OpenACAISonsServerNotices
    {
        private static readonly Dictionary<IntPtr, Peer> ServerPeers = new();
        private static IReadOnlyList<OpenACAISonsNoticePeer> _peers = Array.Empty<OpenACAISonsNoticePeer>();
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedFailure;
        private static bool _hooksInstalled;
        private static bool _clientReady;
        private static bool _clientSupportsReceipts;
        private static IntPtr _clientServer;
        private static uint _clientServerConnectionId;
        private static byte[] _clientToken;
        private static long _nextSessionCheck;
        private static bool _ticking;

        public const int MaxMessageBytes = OpenACAISonsNoticeProtocol.MaxMessageBytes;

        /// <summary>Native hook registration succeeded; this does not prove network delivery.</summary>
        public static bool HooksInstalled => Volatile.Read(ref _hooksInstalled);

        /// <summary>The current server echoed this connection's private capability token.</summary>
        public static bool ClientReady => HooksInstalled && Volatile.Read(ref _clientReady);

        /// <summary>The current server explicitly negotiated notice receipt support.</summary>
        public static bool ClientSupportsReceipts => ClientReady && Volatile.Read(ref _clientSupportsReceipts);

        /// <summary>Server-side capability snapshots. Retained handles become invalid on disconnect.</summary>
        public static IReadOnlyList<OpenACAISonsNoticePeer> Peers => HooksInstalled
            ? Volatile.Read(ref _peers) : Array.Empty<OpenACAISonsNoticePeer>();

        /// <summary>Runs on receipt of an accepted notice, not after HUD display.</summary>
        public static event Action<OpenACAISonsServerNotice> Received;

        public static OpenACAISonsNoticeSendResult Send(OpenACAISonsNoticePeer recipient, string message)
        {
            if (!HooksInstalled)
                return OpenACAISonsNoticeSendResult.Unavailable;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return OpenACAISonsNoticeSendResult.WrongThread;
            if (!OpenACAISonsNoticeProtocol.IsMessageValid(message))
                return OpenACAISonsNoticeSendResult.InvalidMessage;

            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return OpenACAISonsNoticeSendResult.NotServer;
                var availability = ResolvePeer(recipient, out var peer, out var connection);
                if (availability != OpenACAISonsNoticeSendResult.Queued)
                    return availability;

                SendFrame(connection, "notice", peer.Token, message);
                return OpenACAISonsNoticeSendResult.Queued;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return OpenACAISonsNoticeSendResult.Failed;
            }
        }

        /// <summary>Targets an exact session that also negotiated notice support. No handshake is initiated here.</summary>
        public static OpenACAISonsNoticeSendResult SendToSession(OpenACAISonsServerSession recipient, string message)
        {
            if (!HooksInstalled || !OpenACAISonsServerSessions.HooksInstalled)
                return OpenACAISonsNoticeSendResult.Unavailable;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return OpenACAISonsNoticeSendResult.WrongThread;
            if (!OpenACAISonsNoticeProtocol.IsMessageValid(message))
                return OpenACAISonsNoticeSendResult.InvalidMessage;

            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return OpenACAISonsNoticeSendResult.NotServer;
                if (recipient == null)
                    return OpenACAISonsNoticeSendResult.PeerUnavailable;

                foreach (var peer in ServerPeers.Values)
                {
                    if (ReferenceEquals(peer.Snapshot.Session, recipient))
                        return Send(peer.Snapshot, message);
                }
                return OpenACAISonsNoticeSendResult.PeerUnavailable;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return OpenACAISonsNoticeSendResult.Failed;
            }
        }

        internal static void InstallHarmonyPatches(OpenACAIHarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                addStartupWarning?.Invoke("Optional server notices were not installed: " + (support?.Detail ?? "Harmony support is unavailable."));
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            try
            {
                var dispatch = AccessTools.Method(typeof(EventDispatcher), nameof(EventDispatcher.Dispatch), new[] { typeof(Bolt.Event) });
                var disconnect = AccessTools.Method(typeof(BoltConnection), nameof(BoltConnection.DisconnectedInternal), Type.EmptyTypes);
                var update = AccessTools.Method(typeof(BoltPoll), nameof(BoltPoll.Update), Type.EmptyTypes);
                if (dispatch == null || disconnect == null || update == null)
                    throw new MissingMethodException("A required Bolt dispatch, disconnect or update method is unavailable.");

                var harmony = new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + ".sons-server-notices");
                harmony.Patch(disconnect, prefix: new HarmonyMethod(typeof(OpenACAISonsServerNotices), nameof(BeforeDisconnect)));
                harmony.Patch(update, postfix: new HarmonyMethod(typeof(OpenACAISonsServerNotices), nameof(Tick)));
                harmony.Patch(dispatch, prefix: new HarmonyMethod(typeof(OpenACAISonsServerNotices), nameof(BeforeDispatch))
                {
                    priority = HarmonyLib.Priority.First
                });
                Volatile.Write(ref _hooksInstalled, true);
            }
            catch (Exception error)
            {
                addStartupWarning?.Invoke("Optional server notice hooks failed; no handshake or sending is enabled: " + error);
            }
        }

        internal static void Tick()
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId || _ticking)
                return;
            _ticking = true;
            try
            {
                // Drain an earlier batch before native maintenance, which may fail or queue more completions.
                DispatchClientCommandCompletions();
                if (!HooksInstalled)
                {
                    ClearClient(OpenACAISonsClientCommandProgress.Unavailable);
                    ClearServer(OpenACAISonsNoticeDeliveryStatus.Unavailable);
                }
                else
                {
                    var now = Environment.TickCount64;
                    if (now >= _nextSessionCheck)
                    {
                        _nextSessionCheck = now + 1000;
                        ExpireTrackedNotices(now);
                        ExpireClientCommands(now);
                        RefreshSession();
                    }
                    OpenACAISonsNoticeDisplay.Tick();
                }
                DispatchPeerChanges();
            }
            catch (Exception error)
            {
                ReportFailure(error);
            }
            finally { _ticking = false; }
        }

        private static void RefreshSession()
        {
            if (!BoltNetwork.isRunning)
            {
                ClearClient();
                ClearServer();
                return;
            }

            if (BoltNetwork.isServer)
            {
                ClearClient();
                var stale = new List<IntPtr>();
                foreach (var pair in ServerPeers)
                {
                    if (pair.Value.Snapshot.Session?.ClosedAt != null
                        || FindClient(pair.Key, pair.Value.Snapshot.ConnectionId) == null)
                        stale.Add(pair.Key);
                }
                foreach (var pointer in stale)
                    ForgetConnection(pointer);
                return;
            }

            ClearServer();
            var server = BoltNetwork.isClient ? BoltNetwork.server : null;
            if (server == null)
            {
                ClearClient();
                return;
            }
            if (_clientServer == server.Pointer && _clientServerConnectionId == server.ConnectionId)
                return;

            ClearClient();
            if (!BoltNetwork.isConnected)
                return;

            _clientServer = server.Pointer;
            _clientServerConnectionId = server.ConnectionId;
            _clientToken = RandomNumberGenerator.GetBytes(32);
            SendClientFrame("hello", _clientToken, receiptVersion: 1, commandVersion: 1);
        }

        private static bool BeforeDispatch(Bolt.Event __0)
        {
            var chat = __0?.TryCast<ChatEvent>();
            if (chat == null || chat.RaisedBy == null)
                return true;

            var text = chat.Message;
            if (text == null || !text.StartsWith(OpenACAISonsNoticeProtocol.Prefix, StringComparison.Ordinal))
                return true;

            // Once recognized, protocol frames never fall through to game callbacks or relay.
            try
            {
                if (HooksInstalled && Environment.CurrentManagedThreadId == _ownerThreadId
                    && chat.Sender.IsZero && chat.Reliability == ReliabilityModes.ReliableOrdered
                    && OpenACAISonsNoticeProtocol.TryDecode(text, out var frame, out var token))
                {
                    if (BoltNetwork.isServer)
                    {
                        if (frame.Kind == "receipt")
                            ReceiveReceipt(chat, frame, token);
                        else if (frame.Kind == "command")
                            ReceiveClientCommand(chat, frame, token);
                        else
                            ReceiveHello(chat, frame, token);
                    }
                    else if (BoltNetwork.isClient)
                        ReceiveNotice(chat, frame, token);
                }
            }
            catch (Exception error)
            {
                ReportFailure(error);
            }
            finally
            {
                // Dispatch normally releases locally consumed event storage; it is skipped here.
                try { chat.FreeStorage(); }
                catch (Exception error) { ReportFailure(error); }
            }
            return false;
        }

        private static void ReceiveHello(ChatEvent chat, OpenACAISonsNoticeProtocol.Frame frame, byte[] token)
        {
            if (frame.Kind != "hello" || chat.Targets != (int)GlobalTargets.OnlyServer)
                return;

            var source = chat.RaisedBy;
            if (FindClient(source.Pointer, source.ConnectionId) == null)
                return;

            var session = OpenACAISonsServerSessions.ResolveSource(source);
            if (ServerPeers.TryGetValue(source.Pointer, out var previous))
            {
                if (session == null || previous.Snapshot.Session == null || ReferenceEquals(previous.Snapshot.Session, session))
                    return;
                ForgetConnection(source.Pointer);
            }

            ulong? registeredSteamId = null;
            if (GameServerManager.GetRegisteredClient(source, out var steamId) && steamId.m_SteamID != 0)
                registeredSteamId = steamId.m_SteamID;

            var supportsReceipts = frame.ReceiptVersion == 1;
            var supportsCommands = frame.CommandVersion == 1 && session != null && OpenACAISonsCommands.HooksInstalled;
            var peer = new Peer(new OpenACAISonsNoticePeer(source.Pointer, source.ConnectionId, registeredSteamId,
                session, supportsReceipts, supportsCommands), token);
            // Reserve before sending: a connection gets only one handshake attempt, even if sending fails.
            ServerPeers.Add(source.Pointer, peer);
            SendFrame(source, "ack", token, receiptVersion: supportsReceipts ? 1 : 0,
                commandVersion: supportsCommands ? 1 : 0);
            if (!ServerPeers.TryGetValue(source.Pointer, out var current) || !ReferenceEquals(current, peer))
                return;
            peer.Acknowledged = true;
            PublishPeers();
            EnqueuePeerChange(peer, available: true);
        }

        private static void ReceiveNotice(ChatEvent chat, OpenACAISonsNoticeProtocol.Frame frame, byte[] token)
        {
            var server = BoltNetwork.server;
            if (server == null || _clientServer != server.Pointer || chat.RaisedBy.Pointer != _clientServer
                || server.ConnectionId != _clientServerConnectionId || chat.RaisedBy.ConnectionId != _clientServerConnectionId
                || chat.Targets != Bolt.Event.GLOBAL_SPECIFIC_CONNECTION
                || !OpenACAISonsNoticeProtocol.Matches(_clientToken, token))
                return;

            if (frame.Kind == "ack")
            {
                if (ClientReady)
                    return;
                Volatile.Write(ref _clientSupportsReceipts, frame.ReceiptVersion == 1);
                Volatile.Write(ref _clientSupportsCommands, frame.CommandVersion == 1);
                Volatile.Write(ref _clientReady, true);
                return;
            }
            if (!ClientReady)
                return;
            if (frame.Kind == "command-result")
            {
                ReceiveCommandResult(frame);
                return;
            }
            if (frame.Kind != "notice")
                return;

            if (frame.NoticeId.HasValue)
            {
                if (!ClientSupportsReceipts)
                    return;
                var noticeId = frame.NoticeId.Value;
                var serverPointer = _clientServer;
                var clientToken = _clientToken;
                OpenACAISonsNoticeDisplay.Enqueue(frame.Message,
                    outcome => SendDisplayReceipt(serverPointer, clientToken, noticeId, outcome));
                SendClientReceipt(serverPointer, clientToken, noticeId, "accepted");
            }
            else
            {
                OpenACAISonsNoticeDisplay.Enqueue(frame.Message);
            }
            var notice = new OpenACAISonsServerNotice(frame.Message, DateTimeOffset.UtcNow, frame.NoticeId);
            OpenACAIModding.Dispatch("OpenACAISonsServerNotices.Received", Received, notice);
        }

        private static void SendFrame(BoltConnection connection, string kind, byte[] token, string message = null,
            int receiptVersion = 0, Guid? noticeId = null, int commandVersion = 0,
            long commandSequence = 0, string outcome = null)
        {
            var text = OpenACAISonsNoticeProtocol.Encode(kind, token, message, receiptVersion, noticeId, outcome,
                commandVersion, commandSequence);
            var chat = ChatEvent.Create(connection, ReliabilityModes.ReliableOrdered);
            SendPayload(chat, text);
        }

        private static void SendClientFrame(string kind, byte[] token, int receiptVersion = 0,
            Guid? noticeId = null, string outcome = null, int commandVersion = 0,
            long commandSequence = 0, string message = null)
        {
            var text = OpenACAISonsNoticeProtocol.Encode(kind, token, message, receiptVersion,
                noticeId, outcome, commandVersion, commandSequence);
            var chat = ChatEvent.Create(GlobalTargets.OnlyServer, ReliabilityModes.ReliableOrdered);
            SendPayload(chat, text);
        }

        private static void SendPayload(ChatEvent chat, string text)
        {
            var sendAttempted = false;
            try
            {
                chat.Sender = new NetworkId(0);
                chat.Message = text;
                sendAttempted = true;
                chat.Send();
            }
            finally
            {
                if (!sendAttempted)
                {
                    try { chat.FreeStorage(); }
                    catch (Exception error) { ReportFailure(error); }
                }
            }
        }

        private static BoltConnection FindClient(IntPtr pointer, uint connectionId)
        {
            BoltConnection found = null;
            BoltNetwork.clients.ForEach((Action<BoltConnection>)(connection =>
            {
                if (connection != null && connection.Pointer == pointer && connection.ConnectionId == connectionId)
                    found = connection;
            }));
            return found;
        }

        private static void BeforeDisconnect(BoltConnection __instance)
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                // Do not race game-thread state or carry its capability handles into another session.
                Volatile.Write(ref _hooksInstalled, false);
                Volatile.Write(ref _clientReady, false);
                Volatile.Write(ref _peers, Array.Empty<OpenACAISonsNoticePeer>());
                ReportFailure(new InvalidOperationException());
                return;
            }
            ForgetConnection(__instance.Pointer);
        }

        private static void ForgetConnection(IntPtr pointer)
        {
            if (ServerPeers.Remove(pointer, out var peer))
            {
                CompletePending(peer, OpenACAISonsNoticeDeliveryStatus.PeerUnavailable);
                CryptographicOperations.ZeroMemory(peer.Token);
                PublishPeers();
                EnqueuePeerChange(peer, available: false);
            }
            if (_clientServer == pointer)
                ClearClient();
        }

        private static void PublishPeers()
        {
            var peers = new List<OpenACAISonsNoticePeer>(ServerPeers.Count);
            foreach (var peer in ServerPeers.Values)
            {
                if (peer.Acknowledged)
                    peers.Add(peer.Snapshot);
            }
            Volatile.Write(ref _peers, peers.AsReadOnly());
        }

        private static void ClearClient(
            OpenACAISonsClientCommandProgress progress = OpenACAISonsClientCommandProgress.ServerUnavailable)
        {
            CompleteClientCommands(progress);
            _nextClientCommandSequence = 0;
            Volatile.Write(ref _clientSupportsCommands, false);
            if (_clientToken != null)
                CryptographicOperations.ZeroMemory(_clientToken);
            _clientToken = null;
            _clientServer = IntPtr.Zero;
            _clientServerConnectionId = 0;
            Volatile.Write(ref _clientReady, false);
            Volatile.Write(ref _clientSupportsReceipts, false);
            OpenACAISonsNoticeDisplay.Clear();
        }

        private static void ClearServer(
            OpenACAISonsNoticeDeliveryStatus status = OpenACAISonsNoticeDeliveryStatus.PeerUnavailable)
        {
            if (ServerPeers.Count == 0)
                return;
            foreach (var peer in ServerPeers.Values)
            {
                CompletePending(peer, status);
                CryptographicOperations.ZeroMemory(peer.Token);
                EnqueuePeerChange(peer, available: false);
            }
            ServerPeers.Clear();
            Volatile.Write(ref _peers, Array.Empty<OpenACAISonsNoticePeer>());
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Optional server notices encountered "
                    + error.GetType().FullName + ". Further errors are suppressed; no message or token is logged.");
            }
            catch (Exception) { }
        }

        private sealed class Peer
        {
            internal Peer(OpenACAISonsNoticePeer snapshot, byte[] token)
            {
                Snapshot = snapshot;
                Token = token;
            }
            internal OpenACAISonsNoticePeer Snapshot { get; }
            internal byte[] Token { get; }
            internal bool Acknowledged { get; set; }
            internal long LastCommandSequence { get; set; }
            internal Dictionary<Guid, OpenACAISonsNoticeDelivery> Pending { get; } = new();
        }
    }

    public enum OpenACAISonsNoticeSendResult
    {
        Queued,
        Unavailable,
        WrongThread,
        NotServer,
        PeerUnavailable,
        InvalidMessage,
        Failed,
        ReceiptsUnsupported,
        PendingLimitReached,
        SendUncertain
    }

    public sealed class OpenACAISonsNoticePeer
    {
        internal OpenACAISonsNoticePeer(IntPtr connection, uint connectionId, ulong? registeredSteamId,
            OpenACAISonsServerSession session, bool supportsReceipts, bool supportsCommands)
        {
            Connection = connection;
            ConnectionId = connectionId;
            RegisteredSteamId = registeredSteamId;
            Session = session;
            SupportsReceipts = supportsReceipts;
            SupportsCommands = supportsCommands;
        }
        internal IntPtr Connection { get; }
        public uint ConnectionId { get; }
        /// <summary>The game's connection registration, not a permission or separate authentication verdict.</summary>
        public ulong? RegisteredSteamId { get; }
        /// <summary>Session observed during the handshake, or null when session tracking was unavailable.</summary>
        public OpenACAISonsServerSession Session { get; }
        /// <summary>This capability handshake negotiated receipt version 1, not trusted client attestation.</summary>
        public bool SupportsReceipts { get; }
        /// <summary>Negotiated command transport, not a permission grant or client attestation.</summary>
        public bool SupportsCommands { get; }
    }

    public sealed class OpenACAISonsServerNotice
    {
        internal OpenACAISonsServerNotice(string message, DateTimeOffset receivedAt, Guid? noticeId = null)
        {
            Message = message;
            ReceivedAt = receivedAt;
            NoticeId = noticeId;
        }
        public string Message { get; }
        public DateTimeOffset ReceivedAt { get; }
        /// <summary>Present for a tracked notice; null for the original untracked protocol.</summary>
        public Guid? NoticeId { get; }
    }
}
