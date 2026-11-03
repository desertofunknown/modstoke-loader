// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    /// <summary>Copied pickup context and a synchronous decision window, with no retained native objects.</summary>
    public abstract class PickupRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal PickupRequest(Bolt.Event source, ulong pickupNetworkId)
        {
            var connection = source.RaisedBy;
            SourceConnectionId = connection == null ? null : connection.ConnectionId;
            if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId)
                && steamId.m_SteamID != 0)
                RegisteredSteamId = steamId.m_SteamID;
            SourceSession = ServerSessions.ResolveSource(connection);
            RequestedPickupNetworkId = pickupNetworkId;
            ReceivedAt = DateTimeOffset.UtcNow;
            ReceivedUnityFrame = UnityEngine.Time.frameCount;
            ReceivedBoltFrame = BoltNetwork.frame;
        }

        /// <summary>Immediate transport source, or null for local events. Not the original sender across relays.</summary>
        public uint? SourceConnectionId { get; }
        /// <summary>Exact source lifetime when available. Its metadata can change after dispatch.</summary>
        public ServerSession SourceSession { get; }
        /// <summary>Event-time game registration, not independent authentication or permission.</summary>
        public ulong? RegisteredSteamId { get; }
        /// <summary>Raw target ID, including zero or unresolved IDs; not proof of ownership or live presence.</summary>
        public ulong RequestedPickupNetworkId { get; }
        public DateTimeOffset ReceivedAt { get; }
        /// <summary>Unity frame at dispatch, for checking that a read such as TryReadPlayerPose ran in the same frame.</summary>
        public int ReceivedUnityFrame { get; }
        /// <summary>Host BoltNetwork.frame at dispatch: a fixed-step count, not the sender's frame or a Unity frame.</summary>
        public int ReceivedBoltFrame { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);

        /// <summary>
        /// Skips this receiver only, without undoing earlier inventory grants or other listeners.
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

        internal bool IsDispatchThread => Environment.CurrentManagedThreadId == _dispatchThreadId;
        internal bool IsDispatching => IsDispatchThread && _acceptingDecisions;
    }

    /// <summary>Unvalidated DestroyPickUp payload; the claimed player is separate from the transport source.</summary>
    public sealed class PickupDestroyRequest : global::Modstoke.Sons.PickupRequest
    {
        internal PickupDestroyRequest(DestroyPickUp source)
            : base(source, source.GetPickUpEntityNetworkIdValue().PackedValue)
        {
            ClaimedPlayerNetworkId = source.GetPickUpPlayerNetworkIdValue().PackedValue;
            RequestedItemId = source.ItemId;
            RequestedSiblingId = source.SibblingId;
            RequestedFakeDrop = source.FakeDrop;

            object pickup = null;
            var pickupReadSucceeded = false;
            try
            {
                pickup = source.PickUpEntity;
                pickupReadSucceeded = true;
            }
            catch (Exception) { }
            var pickupDescription = CaptureDescription(pickup, pickupReadSucceeded);
            RequestedPickupEntityTypeName = pickupDescription.TypeName;
            RequestedPickupEntityTypeNameReadSucceeded = pickupDescription.TypeNameReadSucceeded;
            RequestedPickupEntityText = pickupDescription.Text;
            RequestedPickupEntityTextReadSucceeded = pickupDescription.TextReadSucceeded;

            object player = null;
            var playerReadSucceeded = false;
            try
            {
                player = source.PickUpPlayer;
                playerReadSucceeded = true;
            }
            catch (Exception) { }
            var playerDescription = CaptureDescription(player, playerReadSucceeded);
            ClaimedPlayerEntityTypeName = playerDescription.TypeName;
            ClaimedPlayerEntityTypeNameReadSucceeded = playerDescription.TypeNameReadSucceeded;
            ClaimedPlayerEntityText = playerDescription.Text;
            ClaimedPlayerEntityTextReadSucceeded = playerDescription.TextReadSucceeded;
        }

        private static (string TypeName, bool TypeNameReadSucceeded, string Text, bool TextReadSucceeded)
            CaptureDescription(object value, bool sourceReadSucceeded)
        {
            if (!sourceReadSucceeded)
                return (null, false, null, false);
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

        /// <summary>Raw payload player ID, including zero. Forwarded cleanup may omit this field.</summary>
        public ulong ClaimedPlayerNetworkId { get; }
        /// <summary>Requested item ID, not the target pickup's verified item identity.</summary>
        public int RequestedItemId { get; }
        /// <summary>Raw SibblingId field; the corrected property spelling does not change its value.</summary>
        public int RequestedSiblingId { get; }
        /// <summary>Raw native FakeDrop branch selector, not proof of a grant, drop or violation.</summary>
        public bool RequestedFakeDrop { get; }

        /// <summary>Copied managed wrapper type name, not an item identity or ownership verdict.</summary>
        public string RequestedPickupEntityTypeName { get; }
        /// <summary>True for a successfully read managed-null pickup or successful type-name capture.</summary>
        public bool RequestedPickupEntityTypeNameReadSucceeded { get; }
        /// <summary>Unmodified pickup ToString result, not a network-ID substitute.</summary>
        public string RequestedPickupEntityText { get; }
        /// <summary>True for a successfully read managed-null pickup or successful ToString call, including null results.</summary>
        public bool RequestedPickupEntityTextReadSucceeded { get; }

        /// <summary>Copied managed type name for the claimed player, not authenticated source identity.</summary>
        public string ClaimedPlayerEntityTypeName { get; }
        /// <summary>True for a successfully read managed-null player field or successful type-name capture.</summary>
        public bool ClaimedPlayerEntityTypeNameReadSucceeded { get; }
        /// <summary>Unmodified claimed-player ToString result, without normalization or truncation.</summary>
        public string ClaimedPlayerEntityText { get; }
        /// <summary>True for a successfully read managed-null player field or successful ToString call, including null results.</summary>
        public bool ClaimedPlayerEntityTextReadSucceeded { get; }

        /// <summary>
        /// Reads the requested pickup with ServerWorld.TryReadPickup before the native receiver forwards or destroys it.
        /// Valid only during synchronous Destroying dispatch, like Reject: returns WrongThread on another thread and
        /// Unavailable afterwards. The native FakeDrop branch grants RequestedItemId without comparing it to the
        /// pickup, so a RequestedFakeDrop request whose RequestedItemId differs from ComponentItemId asks for an item
        /// this pickup does not hold. Other comparisons are weaker, and none is proof of intent.
        /// </summary>
        public bool TryReadTargetPickup(out PickupSnapshot snapshot, out PickupReadStatus status)
        {
            snapshot = null;
            if (!IsDispatchThread)
            {
                status = PickupReadStatus.WrongThread;
                return false;
            }
            if (!IsDispatching)
            {
                status = PickupReadStatus.Unavailable;
                return false;
            }
            return ServerWorld.TryReadPickup(RequestedPickupNetworkId, out snapshot, out status);
        }
    }

    /// <summary>Unvalidated DisablePickup payload. This event has no player or item-count field.</summary>
    public sealed class PickupDisableRequest : global::Modstoke.Sons.PickupRequest
    {
        internal PickupDisableRequest(DisablePickup source)
            : base(source, source.GetEntityNetworkIdValue().PackedValue)
        {
            RequestedNum = source.Num;
        }

        /// <summary>Raw sibling index used by SetPickupUsed, not an inventory quantity.</summary>
        public int RequestedNum { get; }
    }
}
