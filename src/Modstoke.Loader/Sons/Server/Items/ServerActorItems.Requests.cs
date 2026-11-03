// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using global::Sons.Inventory;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    public enum ActorItemOperation { Give, Request }

    /// <summary>Unvalidated actor-item request copies and a synchronous decision window, without retained native payloads.</summary>
    public sealed class ActorItemRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal ActorItemRequest(PlayerGiveActorItem source)
            : this(source, ActorItemOperation.Give, source.GetActorNetworkIdValue().PackedValue,
                source.ItemId, source.Amount)
        {
            var token = source.ItemInstance;
            HasItemInstanceToken = token != null;
            RequestedInstanceItemId = token?.TryCast<ItemInstanceBoltDataToken>()?.ItemInstance?._itemID;

            var description = CaptureDescription(token);
            RequestedItemInstanceTypeName = description.TypeName;
            RequestedItemInstanceTypeNameReadSucceeded = description.TypeNameReadSucceeded;
            RequestedItemInstanceText = description.Text;
            RequestedItemInstanceTextReadSucceeded = description.TextReadSucceeded;
        }

        internal ActorItemRequest(PlayerRequestActorItem source)
            : this(source, ActorItemOperation.Request, source.GetActorNetworkIdValue().PackedValue,
                source.ItemId, source.Amount)
        {
        }

        private ActorItemRequest(Bolt.Event source, ActorItemOperation operation,
            ulong actorNetworkId, int itemId, int amount)
        {
            var connection = source.RaisedBy;
            SourceConnectionId = connection == null ? null : connection.ConnectionId;
            if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId)
                && steamId.m_SteamID != 0)
                RegisteredSteamId = steamId.m_SteamID;
            SourceSession = ServerSessions.ResolveSource(connection);
            Operation = operation;
            RequestedActorNetworkId = actorNetworkId;
            RequestedItemId = itemId;
            RequestedAmount = amount;
            ReceivedAt = DateTimeOffset.UtcNow;
        }

        private static (string TypeName, bool TypeNameReadSucceeded, string Text, bool TextReadSucceeded)
            CaptureDescription(object value)
        {
            if (value is null)
                return (null, true, null, true);

            string typeName = null;
            var typeNameReadSucceeded = false;
            try
            {
                var type = value.GetType();
                typeName = type.FullName ?? type.Name;
                typeNameReadSucceeded = true;
            }
            catch (Exception) { }

            string text = null;
            var textReadSucceeded = false;
            try
            {
                text = value.ToString();
                textReadSucceeded = true;
            }
            catch (Exception) { }
            return (typeName, typeNameReadSucceeded, text, textReadSucceeded);
        }

        /// <summary>Event kind, not a verified transfer or inventory delta.</summary>
        public ActorItemOperation Operation { get; }
        /// <summary>Immediate transport source, or null for a local event. IDs may be reused.</summary>
        public uint? SourceConnectionId { get; }
        /// <summary>Exact source lifetime when available. Its metadata may change after dispatch.</summary>
        public ServerSession SourceSession { get; }
        /// <summary>Event-time game registration, not independent authentication or permission.</summary>
        public ulong? RegisteredSteamId { get; }
        /// <summary>Raw actor ID, including zero or unresolved IDs; not ownership or live-presence evidence.</summary>
        public ulong RequestedActorNetworkId { get; }
        /// <summary>Raw payload item ID or selector, separate from the give token's contained item ID.</summary>
        public int RequestedItemId { get; }
        /// <summary>Raw payload amount. The request-back receiver does not use it as a returned quantity.</summary>
        public int RequestedAmount { get; }
        /// <summary>True when a give event carries a token, even without a readable item. False for request-back.</summary>
        public bool HasItemInstanceToken { get; }
        /// <summary>Contained give-token item ID, or null if absent or incompatible. Does not validate ownership.</summary>
        public int? RequestedInstanceItemId { get; }
        /// <summary>Managed type name of the give protocol wrapper, not its nested item instance. Null for request-back.</summary>
        public string RequestedItemInstanceTypeName { get; }
        /// <summary>True for a captured type name or a successfully read null token. False for request-back.</summary>
        public bool RequestedItemInstanceTypeNameReadSucceeded { get; }
        /// <summary>Unmodified give-token text, not item identity or ownership evidence. Null for request-back.</summary>
        public string RequestedItemInstanceText { get; }
        /// <summary>True when token text was captured, including a successful null. False for request-back.</summary>
        public bool RequestedItemInstanceTextReadSucceeded { get; }
        public DateTimeOffset ReceivedAt { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);

        /// <summary>
        /// Skips this receiver, including its actor changes and replies, without a refund or rollback.
        /// Returns false after synchronous dispatch or on another thread. Rejection cannot be undone.
        /// </summary>
        public bool Reject()
        {
            if (Environment.CurrentManagedThreadId != _dispatchThreadId || !_acceptingDecisions)
                return false;
            Volatile.Write(ref _rejected, true);
            return true;
        }

        internal void Close() => _acceptingDecisions = false;
    }
}
