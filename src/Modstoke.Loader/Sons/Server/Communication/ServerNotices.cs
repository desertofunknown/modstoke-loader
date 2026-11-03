// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using Bolt;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    /// <summary>Short, targeted notices for clients with Modstoke installed.</summary>
    public static partial class ServerNotices
    {
        private static readonly Dictionary<IntPtr, Peer> ServerPeers = new();
        // Frames this process sent. Bolt routes a local send to the remote channel inside Dispatch, which
        // BeforeDispatch would otherwise consume like any reserved frame.
        private static readonly HashSet<IntPtr> OwnFrames = new();
        private static readonly HashSet<(uint ConnectionId, HandshakeReport Report)> ReportedHandshakes = new();
        private static IReadOnlyList<NoticePeer> _peers = Array.Empty<NoticePeer>();
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedFailure;
        private static bool _hooksInstalled;
        private static bool _clientReady;
        private static bool _clientSupportsReceipts;
        private static IntPtr _clientServer;
        private static uint _clientServerConnectionId;
        private static byte[] _clientToken;
        private static int _clientHelloRetries;
        private static long _nextClientHello;
        private static long _clientFirstHelloAt;
        private static bool _clientReportedOutcome;
        private static bool _clientReportedIgnored;
        private static long _nextSessionCheck;
        private static bool _ticking;
        private const int MaxClientHelloRetries = 4;
        private const int MaxRepeatAnswers = 4;
        private const int MaxHandshakeRestarts = 2;

        public const int MaxMessageBytes = NoticeProtocol.MaxMessageBytes;

        /// <summary>Native hook registration succeeded; this does not prove network delivery.</summary>
        public static bool HooksInstalled => Volatile.Read(ref _hooksInstalled);

        /// <summary>The current server echoed this connection's private capability token.</summary>
        public static bool ClientReady => HooksInstalled && Volatile.Read(ref _clientReady);

        /// <summary>The current server explicitly negotiated notice receipt support.</summary>
        public static bool ClientSupportsReceipts => ClientReady && Volatile.Read(ref _clientSupportsReceipts);

        /// <summary>Server-side capability snapshots. Retained handles become invalid on disconnect.</summary>
        public static IReadOnlyList<NoticePeer> Peers => HooksInstalled
            ? Volatile.Read(ref _peers) : Array.Empty<NoticePeer>();

        /// <summary>Runs on receipt of an accepted notice, not after HUD display.</summary>
        public static event Action<ServerNotice> Received;

        public static NoticeSendResult Send(NoticePeer recipient, string message)
        {
            if (!HooksInstalled)
                return NoticeSendResult.Unavailable;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return NoticeSendResult.WrongThread;
            if (!NoticeProtocol.IsMessageValid(message))
                return NoticeSendResult.InvalidMessage;

            var sendAttempted = false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return NoticeSendResult.NotServer;
                var availability = ResolvePeer(recipient, out var peer, out var connection);
                if (availability != NoticeSendResult.Queued)
                    return availability;

                var text = NoticeProtocol.Encode("notice", peer.Token, message);
                SendPayload(ChatEvent.Create(connection, ReliabilityModes.ReliableOrdered), text, ref sendAttempted);
                return NoticeSendResult.Queued;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return sendAttempted ? NoticeSendResult.SendUncertain : NoticeSendResult.Failed;
            }
        }

        /// <summary>Targets an exact session that also negotiated notice support. No handshake is initiated here.</summary>
        public static NoticeSendResult SendToSession(ServerSession recipient, string message)
        {
            if (!HooksInstalled || !ServerSessions.HooksInstalled)
                return NoticeSendResult.Unavailable;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return NoticeSendResult.WrongThread;
            if (!NoticeProtocol.IsMessageValid(message))
                return NoticeSendResult.InvalidMessage;

            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return NoticeSendResult.NotServer;
                if (recipient == null)
                    return NoticeSendResult.PeerUnavailable;

                foreach (var peer in ServerPeers.Values)
                {
                    if (ReferenceEquals(peer.Snapshot.Session, recipient))
                        return Send(peer.Snapshot, message);
                }
                return NoticeSendResult.PeerUnavailable;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return NoticeSendResult.Failed;
            }
        }

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
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

                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-server-notices");
                harmony.Patch(disconnect, prefix: new HarmonyMethod(typeof(ServerNotices), nameof(BeforeDisconnect)));
                harmony.Patch(update, postfix: new HarmonyMethod(typeof(ServerNotices), nameof(Tick)));
                ServerDispatchGate.Register(DispatchRoute.Notices);
                GameRuntime.SceneChanged += OnMessengerSceneChanged;
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
                TickMessenger();
                RefreshHostCommandSession();
                DispatchClientCommandCompletions();
                if (!HooksInstalled)
                {
                    ClearClient(ClientCommandProgress.Unavailable);
                    ClearServer(NoticeDeliveryStatus.Unavailable);
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
                    NoticeDisplay.Tick();
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
                // Shutdown discards queued frames without dispatching them; connection ids restart with Bolt.
                OwnFrames.Clear();
                ReportedHandshakes.Clear();
                return;
            }

            if (BoltNetwork.isServer)
            {
                ClearClient();
                List<IntPtr> stale = null;
                foreach (var pair in ServerPeers)
                {
                    if (pair.Value.Snapshot.Session?.ClosedAt != null
                        || FindClient(pair.Key, pair.Value.Snapshot.ConnectionId) == null)
                        (stale ??= new List<IntPtr>()).Add(pair.Key);
                }
                if (stale != null)
                    foreach (var pointer in stale)
                        ForgetConnection(pointer);
                return;
            }

            ClearServer();
            var server = BoltNetwork.isClient ? BoltNetwork.server : null;
            if (server == null || !BoltNetwork.isConnected)
            {
                ClearClient();
                return;
            }
            if (_clientServer == server.Pointer && _clientServerConnectionId == server.ConnectionId)
            {
                RetryClientHello();
                return;
            }

            ClearClient();
            _clientServer = server.Pointer;
            _clientServerConnectionId = server.ConnectionId;
            _clientToken = RandomNumberGenerator.GetBytes(32);
            _clientCommandSessionId = Guid.NewGuid();
            BeginClientMessenger();
            SendClientHello();
        }

        // Ask again while no answer has arrived or the answer has no commands, usually because it came before this
        // player's session existed. A host without Modstoke never answers and gets a few requests per connection.
        private static void RetryClientHello()
        {
            if (Volatile.Read(ref _clientSupportsCommands) || Environment.TickCount64 < _nextClientHello)
                return;
            if (_clientHelloRetries < MaxClientHelloRetries)
            {
                _clientHelloRetries++;
                SendClientHello();
                return;
            }
            if (_clientReportedOutcome)
                return;
            _clientReportedOutcome = true;
            RuntimeEntryPoint.LogSource?.LogInfo(ClientReady
                ? "Server notices: the server answered but did not enable commands for this connection after "
                    + (_clientHelloRetries + 1) + " requests."
                : "Server notices: no answer after " + (_clientHelloRetries + 1)
                    + " requests; the server runs no Modstoke notices, or an older build that cannot answer.");
        }

        private static void SendClientHello()
        {
            if (_clientHelloRetries == 0)
                _clientFirstHelloAt = Environment.TickCount64;
            _nextClientHello = Environment.TickCount64 + (3000L << _clientHelloRetries);
            int? nativeMods = null, legacyMods = null;
            if (LoadedMods.TryCapture(out var mods) == LoadedModsReadStatus.Success)
            {
                nativeMods = legacyMods = 0;
                foreach (var mod in mods.Entries)
                {
                    if (mod.Loader == "modstoke") nativeMods++;
                    else legacyMods++;
                }
            }
            var text = NoticeProtocol.EncodeHello(_clientToken, "modstoke", RuntimeEntryPoint.Version,
                nativeMods, legacyMods);
            SendPayload(ChatEvent.Create(GlobalTargets.OnlyServer, ReliabilityModes.ReliableOrdered), text);
        }

        internal static DispatchVerdict RouteDispatch(Bolt.Event source, bool runOriginal)
        {
            if (!runOriginal)
            {
                // An earlier policy rejected the event and owns its storage.
                if (OwnFrames.Count > 0 && Environment.CurrentManagedThreadId == _ownerThreadId)
                    OwnFrames.Remove(source.Pointer);
                return DispatchVerdict.Retain;
            }
            var chat = source.TryCast<ChatEvent>();
            if (chat == null)
                return DispatchVerdict.Forward;

            var text = chat.Message;
            if (text == null || !text.StartsWith(NoticeProtocol.Prefix, StringComparison.Ordinal))
            {
                // Another loader's or mod's chat tunnel, as far as the first character can tell.
                if (text is { Length: > 0 } && char.IsControl(text[0]) && chat.RaisedBy != null && HooksInstalled
                    && Environment.CurrentManagedThreadId == _ownerThreadId && BoltNetwork.isServer)
                    ClientSignals.RecordTunnelChat(chat.RaisedBy, text);
                return DispatchVerdict.Forward;
            }

            // Our own frame on its way out: native routing queues it on the remote channel.
            if (chat.RaisedBy == null && Environment.CurrentManagedThreadId == _ownerThreadId
                && OwnFrames.Remove(chat.Pointer) && IsOutboundRoute(chat))
                return DispatchVerdict.Forward;

            // Once recognized, protocol frames never fall through to game callbacks or relay.
            try
            {
                if (HooksInstalled && chat.RaisedBy != null && Environment.CurrentManagedThreadId == _ownerThreadId)
                {
                    if (!(chat.Sender.IsZero && chat.Reliability == ReliabilityModes.ReliableOrdered
                        && NoticeProtocol.TryDecode(text, out var frame, out var token)))
                    {
                        if (BoltNetwork.isServer)
                            ClientSignals.RecordMalformedNotice(chat.RaisedBy);
                        else
                            ReportIgnoredServerFrame("it was malformed");
                    }
                    else if (BoltNetwork.isServer)
                    {
                        if (frame.Kind == "receipt")
                            ReceiveReceipt(chat, frame, token);
                        else if (frame.Kind == "command")
                            ReceiveClientCommand(chat, frame, token);
                        else if (frame.Kind == "messenger-request")
                            ReceiveMessengerRequest(chat, frame, token);
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
            // Dispatch normally releases locally consumed event storage; the gate releases it instead.
            return DispatchVerdict.Reject;
        }

        internal static void ReportReleaseFailure(Exception error) => ReportFailure(error);

        private static void ReceiveHello(ChatEvent chat, NoticeProtocol.Frame frame, byte[] token)
        {
            if (frame.Kind != "hello")
            {
                ClientSignals.RecordUnexpectedNotice(chat.RaisedBy);
                return;
            }
            if (chat.Targets != (int)GlobalTargets.OnlyServer)
            {
                ClientSignals.RecordMalformedNotice(chat.RaisedBy);
                ReportServerHandshake(chat.RaisedBy.ConnectionId, HandshakeReport.Ignored, "Server notices: connection "
                    + chat.RaisedBy.ConnectionId + " sent a request that was not addressed to the server; ignored it.");
                return;
            }

            var source = chat.RaisedBy;
            if (FindClient(source.Pointer, source.ConnectionId) == null)
            {
                ReportServerHandshake(source.ConnectionId, HandshakeReport.Ignored, "Server notices: connection "
                    + source.ConnectionId + " is not a current client; ignored its request.");
                return;
            }

            var session = ServerSessions.ResolveSource(source);
            if (session != null)
                ClientSignals.RecordHello(session, frame);
            bool? inheritedMessenger = null;
            var restarts = 0;
            if (ServerPeers.TryGetValue(source.Pointer, out var previous))
            {
                // On the same connection a repeat completes a handshake that had no session yet, replaces a
                // reservation whose answer was never sent, answers again when the client missed the answer, or
                // starts over when the client did, a few times at most.
                if (previous.Snapshot.ConnectionId == source.ConnectionId)
                {
                    restarts = previous.Restarts;
                    if (previous.Acknowledged && (previous.Snapshot.Session != null || session == null))
                    {
                        if (NoticeProtocol.Matches(previous.Token, token))
                        {
                            if (previous.RepeatAnswers < MaxRepeatAnswers)
                            {
                                previous.RepeatAnswers++;
                                SendAck(source, previous);
                            }
                            return;
                        }
                        if (previous.Restarts >= MaxHandshakeRestarts)
                            return;
                        restarts++;
                    }
                    else
                        inheritedMessenger = previous.Snapshot.SupportsMessenger;
                }
                ForgetConnection(source.Pointer);
            }

            ulong? registeredSteamId = null;
            if (GameServerManager.GetRegisteredClient(source, out var steamId) && steamId.m_SteamID != 0)
                registeredSteamId = steamId.m_SteamID;

            var supportsReceipts = frame.ReceiptVersion == 1;
            var supportsCommands = frame.CommandVersion == 1 && session != null && Commands.HooksInstalled;
            var supportsCommandReplies = supportsCommands && frame.CommandReplyVersion == 1;
            // The client retires an unnegotiated messenger, so a repeat on the same connection never adds it.
            var supportsMessenger = inheritedMessenger != false && frame.MessengerVersion == 1 && session != null
                && Messenger.HasHandler;
            var peer = new Peer(new NoticePeer(source.Pointer, source.ConnectionId, registeredSteamId,
                session, supportsReceipts, supportsCommands, supportsCommandReplies, supportsMessenger), token)
            {
                Restarts = restarts
            };
            if (supportsMessenger && WorldRecords.TryGetCurrent(out var messengerWorld, out _))
                peer.Messenger.UseWorld(messengerWorld);
            // Reserve before sending: one handshake per connection and session, even if sending fails.
            ServerPeers.Add(source.Pointer, peer);
            SendAck(source, peer);
            if (!ServerPeers.TryGetValue(source.Pointer, out var current) || !ReferenceEquals(current, peer))
                return;
            peer.Acknowledged = true;
            PublishPeers();
            EnqueuePeerChange(peer, available: true);
            ReportServerHandshake(source.ConnectionId, supportsCommands ? HandshakeReport.Commands : HandshakeReport.Answered,
                "Server notices: answered connection " + source.ConnectionId + " (commands " + OnOff(supportsCommands)
                + ", replies " + OnOff(supportsCommandReplies) + ", messenger " + OnOff(supportsMessenger) + ")"
                + (session == null ? "; commands wait for the player's session." : "."));
        }

        private static void SendAck(BoltConnection connection, Peer peer)
        {
            var negotiated = peer.Snapshot;
            SendFrame(connection, "ack", peer.Token, receiptVersion: negotiated.SupportsReceipts ? 1 : 0,
                commandVersion: negotiated.SupportsCommands ? 1 : 0,
                commandReplyVersion: negotiated.SupportsCommandReplies ? 1 : 0,
                messengerVersion: negotiated.SupportsMessenger ? 1 : 0);
        }

        private static void ReceiveNotice(ChatEvent chat, NoticeProtocol.Frame frame, byte[] token)
        {
            if (!BoltNetwork.isRunning || !BoltNetwork.isConnected || !BoltNetwork.isClient || BoltNetwork.isServer)
                return;
            var server = BoltNetwork.server;
            if (server == null || _clientServer != server.Pointer || chat.RaisedBy.Pointer != _clientServer
                || server.ConnectionId != _clientServerConnectionId || chat.RaisedBy.ConnectionId != _clientServerConnectionId
                || chat.Targets != Bolt.Event.GLOBAL_SPECIFIC_CONNECTION)
            {
                ReportIgnoredServerFrame("it was not addressed to this connection");
                return;
            }
            if (!NoticeProtocol.Matches(_clientToken, token))
            {
                ReportIgnoredServerFrame("it answered a different request");
                return;
            }

            if (frame.Kind == "ack")
            {
                if (ClientReady)
                {
                    // A repeated handshake may add command support; it never removes any.
                    if (frame.CommandVersion == 1 && !Volatile.Read(ref _clientSupportsCommands))
                    {
                        Volatile.Write(ref _clientSupportsCommands, true);
                        Volatile.Write(ref _clientSupportsCommandReplies, frame.CommandReplyVersion == 1);
                        ReportClientAnswer();
                    }
                    return;
                }
                Volatile.Write(ref _clientSupportsReceipts, frame.ReceiptVersion == 1);
                Volatile.Write(ref _clientSupportsCommands, frame.CommandVersion == 1);
                Volatile.Write(ref _clientSupportsCommandReplies, frame.CommandVersion == 1 && frame.CommandReplyVersion == 1);
                Volatile.Write(ref _clientSupportsMessenger, frame.MessengerVersion == 1 && _clientMessengerId != Guid.Empty);
                Volatile.Write(ref _clientReady, true);
                ReportClientAnswer();
                return;
            }
            if (!ClientReady)
            {
                ReportIgnoredServerFrame("it arrived before the server answered this connection's request");
                return;
            }
            if (frame.Kind is "messenger-message" or "messenger-result")
            {
                ReceiveMessengerFrame(frame);
                return;
            }
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
                NoticeDisplay.Enqueue(frame.Message,
                    outcome => SendDisplayReceipt(serverPointer, clientToken, noticeId, outcome));
                SendClientReceipt(serverPointer, clientToken, noticeId, "accepted");
            }
            else
            {
                NoticeDisplay.Enqueue(frame.Message);
            }
            var handlers = Received;
            if (handlers != null)
            {
                var notice = new ServerNotice(frame.Message, DateTimeOffset.UtcNow, frame.NoticeId);
                Modding.Dispatch("ServerNotices.Received", handlers, notice);
            }
        }

        private static void SendFrame(BoltConnection connection, string kind, byte[] token, string message = null,
            int receiptVersion = 0, Guid? noticeId = null, int commandVersion = 0,
            long commandSequence = 0, string outcome = null, int commandReplyVersion = 0,
            int messengerVersion = 0)
        {
            var text = NoticeProtocol.Encode(kind, token, message, receiptVersion, noticeId, outcome,
                commandVersion, commandSequence, commandReplyVersion, messengerVersion);
            var chat = ChatEvent.Create(connection, ReliabilityModes.ReliableOrdered);
            SendPayload(chat, text);
        }

        private static void SendClientFrame(string kind, byte[] token, int receiptVersion = 0,
            Guid? noticeId = null, string outcome = null, int commandVersion = 0,
            long commandSequence = 0, string message = null, int commandReplyVersion = 0,
            int messengerVersion = 0)
        {
            var text = NoticeProtocol.Encode(kind, token, message, receiptVersion,
                noticeId, outcome, commandVersion, commandSequence, commandReplyVersion, messengerVersion);
            var chat = ChatEvent.Create(GlobalTargets.OnlyServer, ReliabilityModes.ReliableOrdered);
            SendPayload(chat, text);
        }

        private static void SendPayload(ChatEvent chat, string text)
        {
            var sendAttempted = false;
            SendPayload(chat, text, ref sendAttempted);
        }

        private static void SendPayload(ChatEvent chat, string text, ref bool sendAttempted)
        {
            try
            {
                chat.Sender = new NetworkId(0);
                chat.Message = text;
                sendAttempted = true;
                SendOwnFrame(chat);
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

        // Entries leave at dispatch, or when Bolt stops and discards its queue.
        private static void SendOwnFrame(ChatEvent chat)
        {
            var pointer = chat.Pointer;
            OwnFrames.Add(pointer);
            try { chat.Send(); }
            catch
            {
                OwnFrames.Remove(pointer);
                throw;
            }
        }

        // Routes that only queue the frame on a remote channel and never raise it in this process.
        private static bool IsOutboundRoute(ChatEvent chat) => BoltNetwork.isServer
            ? chat.Targets == Bolt.Event.GLOBAL_SPECIFIC_CONNECTION && chat.TargetConnection != null
            : BoltNetwork.isClient && chat.Targets == (int)GlobalTargets.OnlyServer && BoltNetwork.server != null;

        private static void ReportClientAnswer()
        {
            var commands = Volatile.Read(ref _clientSupportsCommands);
            RuntimeEntryPoint.LogSource?.LogInfo("Server notices: the server answered after " + (_clientHelloRetries + 1)
                + " request(s) in " + (Environment.TickCount64 - _clientFirstHelloAt) + " ms (commands " + OnOff(commands)
                + ", replies " + OnOff(Volatile.Read(ref _clientSupportsCommandReplies)) + ", messenger "
                + OnOff(Volatile.Read(ref _clientSupportsMessenger)) + ")"
                + (commands ? "." : "; asking again for commands."));
        }

        private static void ReportIgnoredServerFrame(string reason)
        {
            if (_clientReportedIgnored)
                return;
            _clientReportedIgnored = true;
            RuntimeEntryPoint.LogSource?.LogInfo("Server notices: ignored a frame from the server because " + reason + ".");
        }

        private static void ReportServerHandshake(uint connectionId, HandshakeReport report, string message)
        {
            if (ReportedHandshakes.Count >= 512)
                ReportedHandshakes.Clear();
            if (ReportedHandshakes.Add((connectionId, report)))
                RuntimeEntryPoint.LogSource?.LogInfo(message);
        }

        private static string OnOff(bool value) => value ? "on" : "off";

        private static BoltConnection FindClient(IntPtr pointer, uint connectionId)
        {
            // Enumerate directly; ForEach would marshal a new native delegate on every lookup.
            var clients = BoltNetwork.clients.GetEnumerator();
            try
            {
                var iterator = clients.Cast<Il2CppSystem.Collections.IEnumerator>();
                while (iterator.MoveNext())
                {
                    var connection = clients.Current;
                    if (connection != null && connection.Pointer == pointer && connection.ConnectionId == connectionId)
                        return connection;
                }
                return null;
            }
            finally { clients.Cast<Il2CppSystem.IDisposable>().Dispose(); }
        }

        private static void BeforeDisconnect(BoltConnection __instance)
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                // Do not race game-thread state or carry its capability handles into another session.
                Volatile.Write(ref _hooksInstalled, false);
                Volatile.Write(ref _clientReady, false);
                Volatile.Write(ref _peers, Array.Empty<NoticePeer>());
                ReportFailure(new InvalidOperationException());
                return;
            }
            // Native disconnect cleanup must still run if this bookkeeping fails.
            try { ForgetConnection(__instance.Pointer); }
            catch (Exception error) { ReportFailure(error); }
        }

        private static void ForgetConnection(IntPtr pointer)
        {
            if (ServerPeers.Remove(pointer, out var peer))
            {
                CompletePending(peer, NoticeDeliveryStatus.PeerUnavailable);
                peer.Messenger.ClearWorld();
                CryptographicOperations.ZeroMemory(peer.Token);
                PublishPeers();
                EnqueuePeerChange(peer, available: false);
            }
            if (_clientServer == pointer)
                ClearClient();
        }

        private static void PublishPeers()
        {
            if (ServerPeers.Count == 0)
            {
                Volatile.Write(ref _peers, Array.Empty<NoticePeer>());
                return;
            }

            var peers = new List<NoticePeer>(ServerPeers.Count);
            foreach (var peer in ServerPeers.Values)
            {
                if (peer.Acknowledged)
                    peers.Add(peer.Snapshot);
            }
            Volatile.Write(ref _peers, peers.AsReadOnly());
        }

        private static void ClearClient(
            ClientCommandProgress progress = ClientCommandProgress.ServerUnavailable)
        {
            ClearClientMessenger();
            CompleteClientCommands(progress);
            _nextClientCommandSequence = 0;
            Volatile.Write(ref _clientSupportsCommands, false);
            Volatile.Write(ref _clientSupportsCommandReplies, false);
            _clientCommandSessionId = Guid.Empty;
            if (_clientToken != null)
                CryptographicOperations.ZeroMemory(_clientToken);
            _clientToken = null;
            _clientServer = IntPtr.Zero;
            _clientServerConnectionId = 0;
            _clientHelloRetries = 0;
            _clientFirstHelloAt = 0;
            _clientReportedOutcome = false;
            _clientReportedIgnored = false;
            Volatile.Write(ref _clientReady, false);
            Volatile.Write(ref _clientSupportsReceipts, false);
            NoticeDisplay.Clear();
        }

        private static void ClearServer(
            NoticeDeliveryStatus status = NoticeDeliveryStatus.PeerUnavailable)
        {
            if (ServerPeers.Count == 0)
                return;
            foreach (var peer in ServerPeers.Values)
            {
                CompletePending(peer, status);
                peer.Messenger.ClearWorld();
                CryptographicOperations.ZeroMemory(peer.Token);
                EnqueuePeerChange(peer, available: false);
            }
            ServerPeers.Clear();
            Volatile.Write(ref _peers, Array.Empty<NoticePeer>());
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Optional server notices encountered "
                    + error.GetType().FullName + ". Further errors are suppressed; no message or token is logged.");
            }
            catch (Exception) { }
        }

        private sealed class Peer
        {
            internal Peer(NoticePeer snapshot, byte[] token)
            {
                Snapshot = snapshot;
                Token = token;
            }
            internal NoticePeer Snapshot { get; }
            internal byte[] Token { get; }
            internal bool Acknowledged { get; set; }
            internal int RepeatAnswers { get; set; }
            internal int Restarts { get; set; }
            internal long LastCommandSequence { get; set; }
            internal Dictionary<Guid, NoticeDelivery> Pending { get; } = new();
            internal MessengerPeerState Messenger { get; } = new();
        }

        private enum HandshakeReport
        {
            Ignored,
            Answered,
            Commands
        }
    }

    public enum NoticeSendResult
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

    public sealed class NoticePeer
    {
        internal NoticePeer(IntPtr connection, uint connectionId, ulong? registeredSteamId,
            ServerSession session, bool supportsReceipts, bool supportsCommands, bool supportsCommandReplies,
            bool supportsMessenger)
        {
            Connection = connection;
            ConnectionId = connectionId;
            RegisteredSteamId = registeredSteamId;
            Session = session;
            SupportsReceipts = supportsReceipts;
            SupportsCommands = supportsCommands;
            SupportsCommandReplies = supportsCommandReplies;
            SupportsMessenger = supportsMessenger;
        }
        internal IntPtr Connection { get; }
        public uint ConnectionId { get; }
        /// <summary>The game's connection registration, not a permission or separate authentication verdict.</summary>
        public ulong? RegisteredSteamId { get; }
        /// <summary>Session observed during the handshake, or null when session tracking was unavailable.</summary>
        public ServerSession Session { get; }
        /// <summary>This capability handshake negotiated receipt version 1, not trusted client attestation.</summary>
        public bool SupportsReceipts { get; }
        /// <summary>Negotiated command transport, not a permission grant or client attestation.</summary>
        public bool SupportsCommands { get; }
        /// <summary>The command handshake negotiated optional inline reply snapshots, not delivery or state attestation.</summary>
        public bool SupportsCommandReplies { get; }
        /// <summary>Negotiated private messenger transport, not server policy, membership, or delivery proof.</summary>
        public bool SupportsMessenger { get; }
    }

    public sealed class ServerNotice
    {
        internal ServerNotice(string message, DateTimeOffset receivedAt, Guid? noticeId = null)
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
