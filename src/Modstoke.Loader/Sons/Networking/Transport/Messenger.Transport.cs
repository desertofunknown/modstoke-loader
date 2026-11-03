// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using Bolt;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    public static partial class ServerNotices
    {
        private const int MessengerRecentLimit = 128;
        private const long MessengerTimeoutMilliseconds = 15000;
        private static readonly Dictionary<Guid, MessengerPending> MessengerPendingRequests = new();
        private static readonly MessengerRecentIds ClientMessengerMessages = new();
        private static readonly MessengerRate ClientMessengerRequestRate = new(8);
        private static readonly MessengerRate ClientMessengerReceiveRate = new(32);
        private static Queue<MessengerDelivery> _messengerReceived = new();
        private static Queue<MessengerCompletion> _messengerCompleted = new();
        private static bool _clientSupportsMessenger;
        private static Guid _clientMessengerId;
        private static bool _clientMessengerEnteredWorld;
        private static long _nextMessengerRequestSequence;
        private static long _lastMessengerDeliverySequence;
        private static HostMessengerContext _hostMessenger;
        private static int _activeHostMessengerRequests;
        private static int _dispatchingMessengerCompletions;

        internal static bool MessengerAvailable
        {
            get
            {
                if (!HooksInstalled) return false;
                if (Environment.CurrentManagedThreadId != _ownerThreadId)
                    return ClientReady && Volatile.Read(ref _clientSupportsMessenger);
                return TryGetCurrentMessengerSession(out _, freshIdentity: false);
            }
        }

        internal static bool TryGetCurrentMessengerSession(out Guid sessionId, bool freshIdentity = true)
        {
            sessionId = Guid.Empty;
            if (!HooksInstalled || Environment.CurrentManagedThreadId != _ownerThreadId) return false;
            try
            {
                RefreshHostMessenger(freshIdentity);
                if (_hostMessenger != null && Messenger.HasHandler)
                {
                    sessionId = _hostMessenger.Id;
                    return true;
                }
                if (!ClientMessengerCurrent()) return false;
                sessionId = _clientMessengerId;
                return true;
            }
            catch (Exception error) { ReportFailure(error); return false; }
        }

        private static void BeginClientMessenger() => _clientMessengerId = Guid.NewGuid();

        private static void OnMessengerSceneChanged(global::Modstoke.Sons.SceneEvent scene)
        {
            if (scene.Phase != "Loaded" || scene.SceneKind is not (SceneKind.Title or SceneKind.Loading))
                return;
            // The initial loading scene can precede the first playable frame of a new connection.
            if (scene.SceneKind == SceneKind.Loading && !_clientMessengerEnteredWorld) return;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                Volatile.Write(ref _clientSupportsMessenger, false);
                return;
            }
            ClearClientMessenger();
            _hostMessenger = null;
            _messengerReceived.Clear();
        }

        private static bool ClientMessengerCurrent()
        {
            if (!ClientReady || !_clientSupportsMessenger || _clientMessengerId == Guid.Empty
                || !BoltNetwork.isRunning || !BoltNetwork.isClient || BoltNetwork.isServer || !BoltNetwork.isConnected)
                return false;
            var server = BoltNetwork.server;
            return server != null && server.Pointer == _clientServer && server.ConnectionId == _clientServerConnectionId;
        }

        private static void RefreshHostMessenger(bool freshIdentity = false)
        {
            if (!HooksInstalled || !BoltNetwork.isRunning || !BoltNetwork.isServer
                || !GameServerManager.HasDedicatedState || GameServerManager.IsDedicatedServer)
            {
                _hostMessenger = null;
                return;
            }
            var current = _hostMessenger;
            if (current != null && HostIdentityCurrent(current.Player, current.World,
                ref current.NextIdentityCheck, freshIdentity))
                return;
            _hostMessenger = null;
            if (TryCaptureHostIdentity(out var host, out var world))
                _hostMessenger = new HostMessengerContext(host, world);
        }

        internal static bool SubmitMessenger(string channel, string target, string text, out Guid id, out string detail)
        {
            id = Guid.Empty;
            detail = "Messenger is unavailable.";
            if (!HooksInstalled) return false;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                detail = "Messenger requests require the game thread.";
                return false;
            }
            var candidateId = Guid.NewGuid();
            var payload = new MessengerWire.Payload
            {
                Id = candidateId, Channel = channel, Target = target, Text = text
            };
            if (!MessengerWire.TryEncode("messenger-request", payload, out var json))
            {
                detail = "The channel, target, or message is invalid or exceeds its byte limit.";
                return false;
            }
            try
            {
                // Host submissions share the wire budget even though their handler executes locally.
                NoticeProtocol.Encode("messenger-request", new byte[32], json, messengerSequence: long.MaxValue);
                RefreshHostMessenger(freshIdentity: true);
                ExpireMessengerRequests();
                // Detached completions still occupy admission slots until their dispatch begins.
                if (MessengerPendingRequests.Count + _messengerCompleted.Count + _activeHostMessengerRequests
                    + _dispatchingMessengerCompletions
                    >= Messenger.MaxPendingRequests)
                {
                    detail = "Too many messenger requests are pending.";
                    return false;
                }
                if (_hostMessenger != null)
                {
                    var context = _hostMessenger;
                    if (!Messenger.HasHandler) return false;
                    if (!context.RequestRate.Take())
                    {
                        detail = "Messenger request rate limit reached.";
                        return false;
                    }
                    id = candidateId;
                    _activeHostMessengerRequests++;
                    try
                    {
                        var request = new MessengerRequest(id, null, context.Player, context.World,
                            channel, target ?? string.Empty, text);
                        var result = Messenger.Handle(request);
                        if (!context.Player.IsCurrent || !context.World.IsCurrent)
                            result = MessengerUnavailable(id);
                        _messengerCompleted.Enqueue(new MessengerCompletion(context.Id, result));
                    }
                    finally { _activeHostMessengerRequests--; }
                    detail = "Submitted to the server handler; completion is queued.";
                    return true;
                }
                if (!ClientMessengerCurrent()) return false;
                if (_nextMessengerRequestSequence == long.MaxValue)
                {
                    detail = "The messenger sequence is exhausted for this connection.";
                    return false;
                }
                // Encode before reserving a request or creating native event storage.
                var sequence = _nextMessengerRequestSequence + 1;
                var frame = NoticeProtocol.Encode("messenger-request", _clientToken, json,
                    messengerSequence: sequence);
                if (!ClientMessengerRequestRate.Take())
                {
                    detail = "Messenger request rate limit reached.";
                    return false;
                }
                _nextMessengerRequestSequence = sequence;
                id = candidateId;
                MessengerPendingRequests.Add(id, new MessengerPending(sequence, _clientMessengerId,
                    Environment.TickCount64 + MessengerTimeoutMilliseconds));
                SendPayload(ChatEvent.Create(GlobalTargets.OnlyServer, ReliabilityModes.ReliableOrdered), frame);
                detail = "Queued for server handling.";
                return true;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                if (id != Guid.Empty && MessengerPendingRequests.ContainsKey(id))
                {
                    // Native sending may already have queued the request. Keep its one pending correlation; never retry.
                    detail = "Send outcome is uncertain; awaiting server handling without retrying.";
                    return true;
                }
                id = Guid.Empty;
                detail = "Messenger request could not be queued.";
                return false;
            }
        }

        private static void ReceiveMessengerRequest(ChatEvent chat, NoticeProtocol.Frame frame, byte[] token)
        {
            if (chat.Targets != (int)GlobalTargets.OnlyServer) return;
            var source = chat.RaisedBy;
            var payload = frame.MessengerPayload;
            if (!ServerPeers.TryGetValue(source.Pointer, out var peer) || !peer.Snapshot.SupportsMessenger
                || peer.Snapshot.Session == null || source.ConnectionId != peer.Snapshot.ConnectionId
                || !NoticeProtocol.Matches(peer.Token, token)
                || ResolvePeer(peer.Snapshot, out _, out _) != NoticeSendResult.Queued
                || frame.MessengerSequence <= peer.Messenger.LastRequestSequence || payload == null) return;

            var state = peer.Messenger;
            // This high-water mark survives world changes for the entire private-token lifetime.
            state.LastRequestSequence = frame.MessengerSequence;
            if (!state.RequestRate.Take()) return;
            MessengerResult result;
            if (!WorldRecords.TryGetCurrent(out var world, out _) || !world.IsCurrent || !state.UseWorld(world))
                result = MessengerUnavailable(payload.Id);
            else if (!state.RequestIds.Add(payload.Id))
                result = new MessengerResult(payload.Id, false, "Duplicate messenger request.");
            else
            {
                var request = new MessengerRequest(payload.Id, peer.Snapshot.Session, null, world,
                    payload.Channel, payload.Target ?? string.Empty, payload.Text);
                result = Messenger.Handle(request);
                if (!world.IsCurrent || !peer.Snapshot.Session.IsCurrent)
                    result = MessengerUnavailable(payload.Id);
            }
            if (!HooksInstalled || !BoltNetwork.isRunning || !BoltNetwork.isServer
                || ResolvePeer(peer.Snapshot, out _, out var connection) != NoticeSendResult.Queued) return;
            if (!MessengerWire.TryEncode("messenger-result", new MessengerWire.Payload
            {
                Id = result.Id, Accepted = result.Accepted, Detail = result.Detail
            }, out var json)) return;
            var response = NoticeProtocol.Encode("messenger-result", peer.Token, json,
                messengerSequence: frame.MessengerSequence);
            SendPayload(ChatEvent.Create(connection, ReliabilityModes.ReliableOrdered), response);
        }

        private static void ReceiveMessengerFrame(NoticeProtocol.Frame frame)
        {
            var payload = frame.MessengerPayload;
            if (!ClientMessengerCurrent() || payload == null) return;
            if (frame.Kind == "messenger-result")
            {
                if (!MessengerPendingRequests.TryGetValue(payload.Id, out var pending)
                    || pending.Scope != _clientMessengerId || pending.Sequence != frame.MessengerSequence) return;
                MessengerPendingRequests.Remove(payload.Id);
                var result = Environment.TickCount64 >= pending.ExpiresAt
                    ? MessengerTimedOut(payload.Id)
                    : new MessengerResult(payload.Id, payload.Accepted, payload.Detail);
                _messengerCompleted.Enqueue(new MessengerCompletion(pending.Scope, result));
                return;
            }
            if (frame.Kind != "messenger-message" || frame.MessengerSequence <= _lastMessengerDeliverySequence) return;
            _lastMessengerDeliverySequence = frame.MessengerSequence;
            if (!ClientMessengerReceiveRate.Take() || !ClientMessengerMessages.Add(payload.Id)
                || _messengerReceived.Count >= MessengerRecentLimit) return;
            _messengerReceived.Enqueue(new MessengerDelivery(_clientMessengerId,
                new MessengerMessage(payload.Id, payload.Channel, payload.Conversation,
                    payload.Sender, payload.Name, payload.Text)));
        }

        internal static NoticeSendResult SendMessengerToSession(ServerSession recipient,
            MessengerMessage message)
        {
            if (!HooksInstalled || !ServerSessions.HooksInstalled) return NoticeSendResult.Unavailable;
            if (Environment.CurrentManagedThreadId != _ownerThreadId) return NoticeSendResult.WrongThread;
            if (!MessengerWire.TryEncode("messenger-message", MessengerWire.FromMessage(message), out var json))
                return NoticeSendResult.InvalidMessage;
            var sendAttempted = false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer) return NoticeSendResult.NotServer;
                if (recipient == null || !recipient.IsCurrent) return NoticeSendResult.PeerUnavailable;
                if (!WorldRecords.TryGetCurrent(out var world, out _) || !world.IsCurrent)
                    return NoticeSendResult.Unavailable;
                Peer found = null;
                foreach (var candidate in ServerPeers.Values)
                    if (ReferenceEquals(candidate.Snapshot.Session, recipient)) { found = candidate; break; }
                if (found == null || !found.Snapshot.SupportsMessenger) return NoticeSendResult.PeerUnavailable;
                var status = ResolvePeer(found.Snapshot, out var peer, out var connection);
                if (status != NoticeSendResult.Queued) return status;
                var state = peer.Messenger;
                if (!state.UseWorld(world)) return NoticeSendResult.Unavailable;
                if (state.NextDeliverySequence == long.MaxValue || state.SentIds.Contains(message.Id))
                    return NoticeSendResult.InvalidMessage;
                var sequence = state.NextDeliverySequence + 1;
                var frame = NoticeProtocol.Encode("messenger-message", peer.Token, json,
                    messengerSequence: sequence);
                if (!state.DeliveryRate.Take()) return NoticeSendResult.PendingLimitReached;
                state.NextDeliverySequence = sequence;
                state.SentIds.Add(message.Id);
                sendAttempted = true;
                SendPayload(ChatEvent.Create(connection, ReliabilityModes.ReliableOrdered), frame);
                return NoticeSendResult.Queued;
            }
            catch (ArgumentException) when (!sendAttempted) { return NoticeSendResult.InvalidMessage; }
            catch (Exception error)
            {
                ReportFailure(error);
                return sendAttempted ? NoticeSendResult.SendUncertain : NoticeSendResult.Failed;
            }
        }

        internal static NoticeSendResult SendMessengerToHost(HostPlayer recipient,
            WorldRecordContext world, MessengerMessage message)
        {
            if (!HooksInstalled) return NoticeSendResult.Unavailable;
            if (Environment.CurrentManagedThreadId != _ownerThreadId) return NoticeSendResult.WrongThread;
            if (!MessengerWire.TryEncode("messenger-message", MessengerWire.FromMessage(message), out var json))
                return NoticeSendResult.InvalidMessage;
            try
            {
                RefreshHostMessenger(freshIdentity: true);
                var context = _hostMessenger;
                if (context == null || recipient == null || world == null || !recipient.IsCurrent || !world.IsCurrent
                    || !ReferenceEquals(context.World, world) || context.Player.SteamId != recipient.SteamId)
                    return NoticeSendResult.PeerUnavailable;
                // Apply the same encoded budget to host and network deliveries.
                NoticeProtocol.Encode("messenger-message", new byte[32], json, messengerSequence: long.MaxValue);
                if (context.MessageIds.Contains(message.Id)) return NoticeSendResult.InvalidMessage;
                if (_messengerReceived.Count >= MessengerRecentLimit || !context.DeliveryRate.Take())
                    return NoticeSendResult.PendingLimitReached;
                context.MessageIds.Add(message.Id);
                _messengerReceived.Enqueue(new MessengerDelivery(context.Id, message));
                return NoticeSendResult.Queued;
            }
            catch (ArgumentException) { return NoticeSendResult.InvalidMessage; }
            catch (Exception error) { ReportFailure(error); return NoticeSendResult.Failed; }
        }

        private static void TickMessenger()
        {
            // Detach both batches before callbacks; synchronous submissions always complete on a later tick.
            var deliveries = _messengerReceived.Count == 0 ? null : _messengerReceived;
            var completions = _messengerCompleted.Count == 0 ? null : _messengerCompleted;
            if (deliveries != null) _messengerReceived = new Queue<MessengerDelivery>();
            if (completions != null) _messengerCompleted = new Queue<MessengerCompletion>();
            _dispatchingMessengerCompletions = completions?.Count ?? 0;
            try
            {
                RefreshHostMessenger();
                if (_clientMessengerId != Guid.Empty && _clientReady && !ClientMessengerCurrent()) ClearClientMessenger();
                if (_clientMessengerId != Guid.Empty && GameRuntime.IsInWorld) _clientMessengerEnteredWorld = true;
                ExpireMessengerRequests();
                foreach (var peer in ServerPeers.Values)
                    if (peer.Messenger.World != null && !peer.Messenger.World.IsCurrent) peer.Messenger.ClearWorld();
            }
            catch (Exception error)
            {
                _hostMessenger = null;
                ClearClientMessenger();
                ReportFailure(error);
            }
            try
            {
                if (deliveries != null)
                    foreach (var delivery in deliveries)
                        Messenger.DispatchReceived(delivery.Message, delivery.Scope);
                if (completions != null)
                    foreach (var completion in completions)
                    {
                        _dispatchingMessengerCompletions--;
                        Messenger.DispatchCompleted(MessengerScopeCurrent(completion.Scope)
                            ? completion.Result : MessengerUnavailable(completion.Result.Id));
                    }
            }
            finally { _dispatchingMessengerCompletions = 0; }
        }

        internal static bool MessengerScopeCurrent(Guid scope)
        {
            if (!HooksInstalled || scope == Guid.Empty) return false;
            try
            {
                if (scope == _clientMessengerId) return ClientMessengerCurrent();
                var context = _hostMessenger;
                return context != null && context.Id == scope && BoltNetwork.isRunning && BoltNetwork.isServer
                    && context.Player.IsCurrent && context.World.IsCurrent;
            }
            catch (Exception error) { ReportFailure(error); return false; }
        }

        private static void ExpireMessengerRequests()
        {
            if (MessengerPendingRequests.Count == 0) return;
            List<Guid> expired = null;
            var now = Environment.TickCount64;
            foreach (var pair in MessengerPendingRequests)
                if (now >= pair.Value.ExpiresAt) (expired ??= new List<Guid>()).Add(pair.Key);
            if (expired == null) return;
            foreach (var id in expired)
            {
                var pending = MessengerPendingRequests[id];
                MessengerPendingRequests.Remove(id);
                _messengerCompleted.Enqueue(new MessengerCompletion(pending.Scope, MessengerTimedOut(id)));
            }
        }

        private static void ClearClientMessenger()
        {
            Volatile.Write(ref _clientSupportsMessenger, false);
            foreach (var pair in MessengerPendingRequests)
                _messengerCompleted.Enqueue(new MessengerCompletion(pair.Value.Scope, MessengerUnavailable(pair.Key)));
            MessengerPendingRequests.Clear();
            _clientMessengerId = Guid.Empty;
            _clientMessengerEnteredWorld = false;
            _nextMessengerRequestSequence = 0;
            _lastMessengerDeliverySequence = 0;
            ClientMessengerMessages.Clear();
            ClientMessengerRequestRate.Clear();
            ClientMessengerReceiveRate.Clear();
        }

        private static MessengerResult MessengerUnavailable(Guid id) =>
            new(id, false, "The messenger session or world is unavailable.");
        private static MessengerResult MessengerTimedOut(Guid id) =>
            new(id, false, "Server handling was not confirmed before the timeout; the request was not retried.");

        private sealed record MessengerPending(long Sequence, Guid Scope, long ExpiresAt);
        private sealed record MessengerCompletion(Guid Scope, MessengerResult Result);
        private sealed record MessengerDelivery(Guid Scope, MessengerMessage Message);

        private sealed class HostMessengerContext(HostPlayer player, WorldRecordContext world)
        {
            internal Guid Id { get; } = Guid.NewGuid();
            internal HostPlayer Player { get; } = player;
            internal WorldRecordContext World { get; } = world;
            internal long NextIdentityCheck = Environment.TickCount64 + HostIdentityObservationMilliseconds;
            internal MessengerRate RequestRate { get; } = new(8);
            internal MessengerRate DeliveryRate { get; } = new(32);
            internal MessengerRecentIds MessageIds { get; } = new();
        }

        private sealed class MessengerPeerState
        {
            internal WorldRecordContext World { get; private set; }
            private bool _worldRetired;
            internal long LastRequestSequence { get; set; }
            internal long NextDeliverySequence { get; set; }
            internal MessengerRate RequestRate { get; } = new(8);
            internal MessengerRate DeliveryRate { get; } = new(32);
            internal MessengerRecentIds RequestIds { get; } = new();
            internal MessengerRecentIds SentIds { get; } = new();
            internal bool UseWorld(WorldRecordContext world)
            {
                if (_worldRetired) return false;
                if (World == null) World = world;
                if (ReferenceEquals(world, World) && world.IsCurrent) return true;
                ClearWorld();
                return false;
            }
            internal void ClearWorld()
            {
                if (World != null) _worldRetired = true;
                World = null;
                RequestIds.Clear();
                SentIds.Clear();
            }
        }

        private sealed class MessengerRecentIds
        {
            private readonly HashSet<Guid> _ids = new();
            private readonly Queue<Guid> _order = new();
            internal bool Contains(Guid id) => _ids.Contains(id);
            internal bool Add(Guid id)
            {
                if (!_ids.Add(id)) return false;
                _order.Enqueue(id);
                if (_order.Count > MessengerRecentLimit) _ids.Remove(_order.Dequeue());
                return true;
            }
            internal void Clear() { _ids.Clear(); _order.Clear(); }
        }

        private sealed class MessengerRate(int maximum)
        {
            private long _window;
            private int _count;
            internal bool Take()
            {
                var now = Environment.TickCount64;
                if (now - _window >= 10000) { _window = now; _count = 0; }
                if (_count >= maximum) return false;
                _count++;
                return true;
            }
            internal void Clear() { _window = 0; _count = 0; }
        }
    }
}
