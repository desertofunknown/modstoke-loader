// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Numerics;
using System.Threading;
using Sons.Multiplayer;

namespace OpenACAI.Sons
{
    /// <summary>Managed transport context and a synchronous policy window, separate from claimed player/vehicle IDs.</summary>
    public abstract class OpenACAISonsVehicleRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal OpenACAISonsVehicleRequest(Bolt.Event source)
        {
            var connection = source.RaisedBy;
            SourceConnectionId = connection == null ? null : connection.ConnectionId;
            SourceSession = OpenACAISonsServerSessions.ResolveSource(connection);
            if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId)
                && steamId.m_SteamID != 0)
                RegisteredSteamId = steamId.m_SteamID;
            RoutingTargets = source.Targets;
            var target = source.TargetEntity;
            RoutedEntityNetworkId = target == null ? null : target.NetworkId.PackedValue;
            var destination = source.TargetConnection;
            RoutedConnectionId = destination == null ? null : destination.ConnectionId;
            ReceivedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Immediate transport source; null for a local event. Not original authorship of relayed traffic.</summary>
        public uint? SourceConnectionId { get; }
        public OpenACAISonsServerSession SourceSession { get; }
        /// <summary>Event-time game registration, not independent authentication or permission.</summary>
        public ulong? RegisteredSteamId { get; }
        public int RoutingTargets { get; }
        /// <summary>Routing target, used as the golf-cart target. Null means no routing entity was available.</summary>
        public ulong? RoutedEntityNetworkId { get; }
        public uint? RoutedConnectionId { get; }
        public DateTimeOffset ReceivedAt { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);

        /// <summary>Stops this server dispatch without undoing earlier prediction. False outside the synchronous window.</summary>
        public bool Reject()
        {
            if (Environment.CurrentManagedThreadId != _dispatchThreadId || !_acceptingDecisions)
                return false;
            Volatile.Write(ref _rejected, true);
            return true;
        }

        internal void Close() => _acceptingDecisions = false;
    }

    public sealed class OpenACAISonsSledGrabRequest : OpenACAISonsVehicleRequest
    {
        internal OpenACAISonsSledGrabRequest(SledGrab source) : base(source)
        {
            RequestedPlayerNetworkId = source.GetPlayerNetworkIdValue().PackedValue;
            RequestedSledNetworkId = source.GetSledNetworkIdValue().PackedValue;
        }

        /// <summary>Claimed player, not a resolved or authenticated source player.</summary>
        public ulong RequestedPlayerNetworkId { get; }
        public ulong RequestedSledNetworkId { get; }
    }

    public sealed class OpenACAISonsSledReleaseRequest : OpenACAISonsVehicleRequest
    {
        internal OpenACAISonsSledReleaseRequest(SledRelease source) : base(source)
        {
            RequestedPlayerNetworkId = source.GetPlayerNetworkIdValue().PackedValue;
            RequestedSledNetworkId = source.GetSledNetworkIdValue().PackedValue;
            var position = source.Position;
            RequestedPosition = new Vector3(position.x, position.y, position.z);
            var rotation = source.Rotation;
            RequestedRotation = new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w);
        }

        public ulong RequestedPlayerNetworkId { get; }
        public ulong RequestedSledNetworkId { get; }
        /// <summary>Raw payload, not applied by the inspected native release receiver.</summary>
        public Vector3 RequestedPosition { get; }
        public Quaternion RequestedRotation { get; }
    }

    public sealed class OpenACAISonsGolfCartShoveRequest : OpenACAISonsVehicleRequest
    {
        internal OpenACAISonsGolfCartShoveRequest(GolfCartShoveEvent source) : base(source)
        {
            var vector = source.ShoveVector;
            RequestedShoveVector = new Vector3(vector.x, vector.y, vector.z);
        }

        /// <summary>Raw vector, not a validated direction, applied force or movement result.</summary>
        public Vector3 RequestedShoveVector { get; }
    }

    public sealed class OpenACAISonsGolfCartSlotRequest : OpenACAISonsVehicleRequest
    {
        internal OpenACAISonsGolfCartSlotRequest(GolfCartConnectSlotEvent source) : base(source)
        {
            RequestedSlotIndex = source.SlotIndex;
            RequestedOccupantNetworkId = source.GetOccupiedEntityNetworkIdValue().PackedValue;
            RequestedDisconnect = source.Disconnect;
        }

        public int RequestedSlotIndex { get; }
        /// <summary>Claimed occupant, which can be a player or AI entity. It is not necessarily the sender.</summary>
        public ulong RequestedOccupantNetworkId { get; }
        public bool RequestedDisconnect { get; }
    }
}
