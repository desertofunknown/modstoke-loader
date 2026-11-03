// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Numerics;
using System.Threading;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    /// <summary>Unvalidated world-placement copies and a synchronous decision window, without retained native objects.</summary>
    public abstract class WorldPlacementRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal WorldPlacementRequest(Bolt.Event source, UnityEngine.Vector3 position,
            UnityEngine.Quaternion rotation)
        {
            var connection = source.RaisedBy;
            SourceConnectionId = connection == null ? null : connection.ConnectionId;
            if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId)
                && steamId.m_SteamID != 0)
                RegisteredSteamId = steamId.m_SteamID;
            SourceSession = ServerSessions.ResolveSource(connection);
            RequestedPosition = new Vector3(position.x, position.y, position.z);
            RequestedRotation = new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w);
            ReceivedAt = DateTimeOffset.UtcNow;
            ReceivedUnityFrame = UnityEngine.Time.frameCount;
            ReceivedBoltFrame = BoltNetwork.frame;
        }

        /// <summary>Immediate transport source, or null for local events. IDs may be reused between sessions.</summary>
        public uint? SourceConnectionId { get; }
        /// <summary>Exact source lifetime when available. Its live metadata may change after dispatch.</summary>
        public ServerSession SourceSession { get; }
        /// <summary>Event-time game registration, not independent authentication or permission.</summary>
        public ulong? RegisteredSteamId { get; }
        /// <summary>Sender-requested position, copied without validation or modification.</summary>
        public Vector3 RequestedPosition { get; }
        /// <summary>Sender-requested rotation, copied without validation or normalization.</summary>
        public Quaternion RequestedRotation { get; }
        public DateTimeOffset ReceivedAt { get; }
        /// <summary>Unity frame at dispatch, for checking that a read such as TryReadPlayerPose ran in the same frame.</summary>
        public int ReceivedUnityFrame { get; }
        /// <summary>Host BoltNetwork.frame at dispatch: a fixed-step count, not the sender's frame or a Unity frame.</summary>
        public int ReceivedBoltFrame { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);

        /// <summary>
        /// Skips this receiver only, without refunding or undoing prior world or inventory changes.
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

    /// <summary>Copied PlaceDryingFood payload, not proof of an owned item, valid rack or completed placement.</summary>
    public sealed class DryingFoodPlacementRequest : WorldPlacementRequest
    {
        internal DryingFoodPlacementRequest(PlaceDryingFood source)
            : base(source, source.Position, source.Rotation)
        {
            var prefab = source.PrefabId;
            RequestedPrefabId = prefab.Value;
            var description = CaptureDescription(prefab);
            RequestedPrefabIdTypeName = description.TypeName;
            RequestedPrefabIdTypeNameReadSucceeded = description.TypeNameReadSucceeded;
            RequestedPrefabIdText = description.Text;
            RequestedPrefabIdTextReadSucceeded = description.TextReadSucceeded;

            // The native receiver ignores these fields; a diagnostic read must not add a rejection rule.
            try { RequestedParentNetworkId = source.GetParentNetworkIdValue().PackedValue; }
            catch (Exception) { }
            try { RequestedDecayState = source.DecayState; }
            catch (Exception) { }
        }

        private static (string TypeName, bool TypeNameReadSucceeded, string Text, bool TextReadSucceeded)
            CaptureDescription(object value)
        {
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

        /// <summary>Raw requested prefab ID, not a validated prefab, item ID or ownership claim.</summary>
        public int RequestedPrefabId { get; }
        /// <summary>Managed type name of the boxed native prefab-ID value, or null when its read failed.</summary>
        public string RequestedPrefabIdTypeName { get; }
        public bool RequestedPrefabIdTypeNameReadSucceeded { get; }
        /// <summary>Unmodified prefab-ID ToString result, or null when that call failed or returned null.</summary>
        public string RequestedPrefabIdText { get; }
        public bool RequestedPrefabIdTextReadSucceeded { get; }
        /// <summary>
        /// Raw serialized parent ID, including zero, or null on read failure. No live entity is resolved.
        /// The native receiver ignores this field; it does not establish a rack or ownership.
        /// </summary>
        public ulong? RequestedParentNetworkId { get; }
        /// <summary>Raw decay value, or null on read failure. The native receiver does not apply this field.</summary>
        public int? RequestedDecayState { get; }
    }

    /// <summary>Copied DropVailActorEvent payload; native distance, type and spawn checks still run if allowed.</summary>
    public sealed class ActorDropRequest : WorldPlacementRequest
    {
        internal ActorDropRequest(DropVailActorEvent source)
            : base(source, source.Position, source.Rotation)
        {
            RequestedTypeId = source.TypeId;
            RequestedVariationId = source.VariationId;
            RequestedState = source.State;
            RequestedAreaMask = source.AreaMask;
        }

        /// <summary>Raw actor type requested by the sender, not a validated type or owned actor.</summary>
        public int RequestedTypeId { get; }
        /// <summary>Raw actor variation, without validation or modification.</summary>
        public int RequestedVariationId { get; }
        /// <summary>Raw state ID, not a resolved or validated native actor state.</summary>
        public int RequestedState { get; }
        /// <summary>Raw area mask, without conversion to a native enum or validation.</summary>
        public int RequestedAreaMask { get; }
    }
}
