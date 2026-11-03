// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Numerics;
using System.Threading;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    /// <summary>Copied transport context and a synchronous decision window, without retained native objects.</summary>
    public abstract class TransportRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal TransportRequest(Bolt.Event source)
        {
            var connection = source.RaisedBy;
            SourceConnectionId = connection == null ? null : connection.ConnectionId;
            if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId)
                && steamId.m_SteamID != 0)
                RegisteredSteamId = steamId.m_SteamID;
            SourceSession = ServerSessions.ResolveSource(connection);
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
        public DateTimeOffset ReceivedAt { get; }
        /// <summary>Unity frame at dispatch, for checking that a read such as TryReadPlayerPose ran in the same frame.</summary>
        public int ReceivedUnityFrame { get; }
        /// <summary>Host BoltNetwork.frame at dispatch: a fixed-step count, not the sender's frame or a Unity frame.</summary>
        public int ReceivedBoltFrame { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);

        /// <summary>
        /// Skips this receiver only, without undoing earlier actions or refunding items.
        /// Returns false after dispatch or on another thread. Rejection cannot be undone.
        /// </summary>
        public bool Reject()
        {
            if (Environment.CurrentManagedThreadId != _dispatchThreadId || !_acceptingDecisions)
                return false;
            Volatile.Write(ref _rejected, true);
            return true;
        }

        internal void Close() => _acceptingDecisions = false;

        internal static Vector3 Copy(UnityEngine.Vector3 value) => new(value.x, value.y, value.z);
        internal static Quaternion Copy(UnityEngine.Quaternion value) => new(value.x, value.y, value.z, value.w);
    }

    /// <summary>Raw zipline request copies, not proof of an item, zipline component or completed attachment.</summary>
    public sealed class ZiplineItemRequest : TransportRequest
    {
        internal ZiplineItemRequest(AttachItemToZiplineEvent source) : base(source)
        {
            RequestedZiplineNetworkId = source.GetTargetZiplineNetworkIdValue().PackedValue;

            // Native handling reads these only after finding a ZipLineController. Do not add a prerequisite.
            try { RequestedItemId = source.ItemId; }
            catch (Exception) { }
            try { RequestedPosition = Copy(source.Position); }
            catch (Exception) { }
            try { RequestedDirection = Copy(source.Direction); }
            catch (Exception) { }
        }

        /// <summary>Raw serialized target ID, including zero. No live entity or component is resolved.</summary>
        public ulong RequestedZiplineNetworkId { get; }
        /// <summary>Raw item ID, including zero, or null on read failure. Not a validated or owned item.</summary>
        public int? RequestedItemId { get; }
        /// <summary>Unvalidated requested position, or null on read failure.</summary>
        public Vector3? RequestedPosition { get; }
        /// <summary>Unvalidated and unnormalized requested direction, or null on read failure.</summary>
        public Vector3? RequestedDirection { get; }
    }

    /// <summary>Glider pickup/drop payload copies; native ownership, spawn, force and drag handling remain downstream.</summary>
    public sealed class GliderRequest : TransportRequest
    {
        internal GliderRequest(PickupGlider source) : base(source)
        {
            IsDrop = source.dropGlider;
            if (IsDrop)
            {
                var prefab = source.gliderId;
                RequestedPrefabId = prefab.Value;
                PrefabDescription = TransportDescription.Capture(prefab);
                RequestedDropPosition = Copy(source.dropPos);
                RequestedDropRotation = Copy(source.dropRot);

                // The drop branch does not resolve targetEntity.
                try { RequestedTargetNetworkId = source.GettargetEntityNetworkIdValue().PackedValue; }
                catch (Exception) { }
            }
            else
            {
                RequestedTargetNetworkId = source.GettargetEntityNetworkIdValue().PackedValue;

                // Pickup ignores all drop fields. Keep their diagnostic failures separate from dispatch.
                try
                {
                    var prefab = source.gliderId;
                    PrefabDescription = TransportDescription.Capture(prefab);
                    RequestedPrefabId = prefab.Value;
                }
                catch (Exception) { }
                try { RequestedDropPosition = Copy(source.dropPos); }
                catch (Exception) { }
                try { RequestedDropRotation = Copy(source.dropRot); }
                catch (Exception) { }
            }

            // Native drop reads this only after instantiation and applies it as a force, not a velocity assignment.
            try { RequestedTargetVelocity = Copy(source.targetVelocity); }
            catch (Exception) { }
            try { TargetEntityDescription = TransportDescription.Capture(source.targetEntity); }
            catch (Exception) { }
        }

        /// <summary>True selects the native drop branch; false selects pickup.</summary>
        public bool IsDrop { get; }
        /// <summary>Raw serialized entity ID, including zero; null if an optional drop-branch read failed.</summary>
        public ulong? RequestedTargetNetworkId { get; }
        /// <summary>Raw prefab ID, not an item ID; null if an optional pickup-branch read failed.</summary>
        public int? RequestedPrefabId { get; }
        /// <summary>Unvalidated drop position, or null if an optional pickup-branch read failed.</summary>
        public Vector3? RequestedDropPosition { get; }
        /// <summary>Unvalidated drop rotation, or null if an optional pickup-branch read failed.</summary>
        public Quaternion? RequestedDropRotation { get; }
        /// <summary>
        /// Raw targetVelocity payload, or null on read failure. Native drop passes it to AddForce
        /// after spawning; it does not establish actual velocity or successful force application.
        /// </summary>
        public Vector3? RequestedTargetVelocity { get; }
        public TransportDescription PrefabDescription { get; } = TransportDescription.Unread;
        public TransportDescription TargetEntityDescription { get; } = TransportDescription.Unread;
    }

    /// <summary>Independent copied getter, type-name and text outcomes. Contains no retained native value.</summary>
    public sealed class TransportDescription
    {
        internal static TransportDescription Unread { get; } = new(false, false, null, false, null, false);

        private TransportDescription(bool valueReadSucceeded, bool isNull, string typeName,
            bool typeNameReadSucceeded, string text, bool textReadSucceeded)
        {
            ValueReadSucceeded = valueReadSucceeded;
            IsNull = isNull;
            TypeName = typeName;
            TypeNameReadSucceeded = typeNameReadSucceeded;
            Text = text;
            TextReadSucceeded = textReadSucceeded;
        }

        internal static TransportDescription Capture(object value)
        {
            if (value is null)
                return new TransportDescription(true, true, null, false, null, false);

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
            return new TransportDescription(true, false, typeName, typeNameReadSucceeded, text, textReadSucceeded);
        }

        /// <summary>Whether the original value getter succeeded, independently of its type and text reads.</summary>
        public bool ValueReadSucceeded { get; }
        /// <summary>Whether the successfully read value was managed null. Meaningful only when ValueReadSucceeded.</summary>
        public bool IsNull { get; }
        /// <summary>Managed FullName, falling back to Name, or null on read failure.</summary>
        public string TypeName { get; }
        public bool TypeNameReadSucceeded { get; }
        /// <summary>Unmodified ToString result; null may be a successful result. Not an identity or ownership claim.</summary>
        public string Text { get; }
        public bool TextReadSucceeded { get; }
    }
}
