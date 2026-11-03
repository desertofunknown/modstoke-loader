// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;

namespace Modstoke.Sons.Combat
{
    /// <summary>
    /// Suppresses legacy combat events from one acquired transport session and identified native melee routes to it.
    /// Retirement of combat or its owning mod does not release this gate; reconnect to leave ownership.
    /// </summary>
    public sealed class StrictCombatServerHitBarrier
    {
        internal StrictCombatServerHitBarrier(ControlPeer peer, BoltConnection connection)
        { Peer = peer; Connection = connection; }
        public ControlPeer Peer { get; }
        internal BoltConnection Connection { get; }

        /// <summary>Game-thread availability and observed session liveness, not proof of interception.</summary>
        public bool IsCurrent => StrictCombatServerHits.IsAvailable && Peer.ServerSession.IsCurrent;
    }

    /// <summary>
    /// Server-side ownership of legacy combat ingress and identified remote-player melee callbacks.
    /// It does not authenticate reported mods or cover every native damage route.
    /// </summary>
    public static class StrictCombatServerHits
    {
        private const int MaximumSessions = 32;
        private static StrictCombatServerHitBarrier[] _owners = Array.Empty<StrictCombatServerHitBarrier>();
        private static bool _acquiring;

        public static bool IsAvailable => ServerPlayerHits.IsOwnerThread
            && ServerPlayerHits.State.Status == PlayerHitHookStatus.Installed
            && ServerMeleeImpacts.IsOwnerThread
            && ServerMeleeImpacts.State.Status == MeleeImpactHookStatus.Installed
            && ServerSessions.HooksInstalled;

        /// <summary>
        /// Acquires once for an exact enrolled connection before server combat starts its life.
        /// Every HitPlayer, VailHitEvent and DismemberHitEvent from that resolved session is suppressed,
        /// regardless of its claimed cause or target. These events cannot stand in for an accepted combat intent.
        /// Identified native MeleeWeapon-to-player callbacks targeting that connection are also suppressed,
        /// while qualified observations remain available for server-owned damage. Other routes remain unchanged.
        /// </summary>
        public static bool TryAcquire(ControlPeer peer, out StrictCombatServerHitBarrier barrier)
        {
            barrier = null;
            if (!IsAvailable || _acquiring) return false;
            _acquiring = true;
            try
            {
                if (!BoltNetwork.isServer || !Eligible(peer)
                    || !WorldRecords.TryGetCurrent(out var world, out _) || !world.IsCurrent) return false;
                var connection = ControlTransport.Resolve(peer);
                if (connection == null) return false;
                var owners = Volatile.Read(ref _owners);
                var retained = new List<StrictCombatServerHitBarrier>(MaximumSessions);
                StrictCombatServerHitBarrier existing = null;
                foreach (var owner in owners)
                {
                    // Tracking failure is not closure. Only an observed close releases capacity.
                    if (owner.Peer.ServerSession.ClosedAt.HasValue) continue;
                    retained.Add(owner);
                    if (!ReferenceEquals(owner.Peer.ServerSession, peer.ServerSession)) continue;
                    if (!ReferenceEquals(owner.Peer, peer) || owner.Connection.Pointer != connection.Pointer) return false;
                    existing = owner;
                }
                if (existing == null && retained.Count >= MaximumSessions) return false;
                var currentConnection = ControlTransport.Resolve(peer);
                if (!IsAvailable || !world.IsCurrent || !Eligible(peer)
                    || currentConnection == null || currentConnection.Pointer != connection.Pointer
                    || !ReferenceEquals(owners, Volatile.Read(ref _owners))) return false;
                if (existing != null)
                {
                    barrier = existing;
                    return true;
                }

                var candidate = new StrictCombatServerHitBarrier(peer, connection);
                retained.Add(candidate);
                Volatile.Write(ref _owners, retained.ToArray());
                barrier = candidate;
                return true;
            }
            catch (Exception) { return false; }
            finally { _acquiring = false; }
        }

        internal static bool HasOwners => Volatile.Read(ref _owners).Length != 0;

        internal static bool Blocks(BoltConnection connection, uint? connectionId, ServerSession source)
        {
            foreach (var owner in Volatile.Read(ref _owners))
            {
                if (source != null)
                {
                    if (ReferenceEquals(source, owner.Peer.ServerSession)) return true;
                    // A newly resolved lifetime does not inherit a previous session's ownership.
                    continue;
                }
                // Losing session tracking must not reopen an already acquired native connection.
                // Retain its wrapper as well as the ID; numeric connection IDs alone can be reused.
                if (connection is not null && !connection.WasCollected
                    && connection.Pointer == owner.Connection.Pointer && connectionId == owner.Peer.ConnectionId)
                    return true;
            }
            return false;
        }

        private static bool Eligible(ControlPeer peer)
        {
            const ulong required = StrictCombatProtocol.RequiredCapabilities;
            return peer?.ServerSession != null && peer.ServerSession.IsCurrent && peer.IsCurrent
                && ClientEnrollment.TryRead(peer, out var enrollment) == EnrollmentReadStatus.Success
                && ReferenceEquals(enrollment.Peer, peer) && enrollment.State == EnrollmentState.Enrolled
                && (enrollment.RequiredCapabilities & required) == required
                && (enrollment.OfferedCapabilities & required) == required;
        }
    }
}
