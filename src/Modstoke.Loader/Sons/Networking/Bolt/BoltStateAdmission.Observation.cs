// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Bolt;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    public static partial class BoltStateAdmission
    {
        private static int _reportedObservation;
        [ThreadStatic] private static bool _observing;

        /// <summary>
        /// Runs before an entity update header has native side effects, after any Receiving policy admitted
        /// it. Never aborts or disconnects, and does not enable the receive guard. Passive evidence only:
        /// it records what the wire header claimed and what the host's tables held at entry, not intent or
        /// an applied write. Not raised for updates a policy rejected, for reads nested inside a policy or
        /// observer, or off the game thread, and live only while State reports Installed.
        /// Handler exceptions are isolated and logged once. Unsubscribe on unload.
        /// </summary>
        public static event Action<BoltStateAdmissionObservation> Observed;

        private static void Observe(EntityChannel channel, Packet update, BoltConnection source, bool guarded,
            Action<BoltStateAdmissionObservation> observers)
        {
            if (_observing || _deciding || Environment.CurrentManagedThreadId != Volatile.Read(ref _ownerThread)) return;
            BoltStateAdmissionObservation observation = null;
            _observing = true;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer || source == null || source.WasCollected
                    || update == null) return;
                var status = BoltStateClaimStatus.Unparsed;
                ulong? claimed = null;
                BoltEntitySnapshot existing = null, local = null;
                var packet = update.UdpPacket;
                if (packet != null && !packet.WasCollected
                    && PeekId(packet, out bool hasUpdate, out uint connection, out uint entity))
                {
                    status = BoltStateClaimStatus.NoUpdate;
                    if (hasUpdate)
                    {
                        var id = new NetworkId(connection, entity);
                        claimed = id.PackedValue;
                        EntityProxy proxy = null;
                        var incoming = channel._incommingDict;
                        if (incoming != null && incoming.TryGetValue(id, out proxy) && proxy?.Entity != null)
                        {
                            existing = Snapshot(proxy.Entity);
                            status = BoltStateClaimStatus.KnownIncoming;
                        }
                        else
                        {
                            status = BoltStateClaimStatus.UnknownIncoming;
                            var found = BoltNetwork.FindEntity(id);
                            var native = found is null || found.WasCollected || !found ? null : found._entity;
                            if (native != null && !native.WasCollected && native.IsAttached && native.IsOwner
                                && native.NetworkId.PackedValue == id.PackedValue)
                            {
                                local = Snapshot(native);
                                status = BoltStateClaimStatus.ClaimsLocallyOwnedEntity;
                            }
                        }
                    }
                }
                observation = new(source, status, claimed, existing, local, guarded);
                foreach (var handler in Delegate.EnumerateInvocationList(observers))
                {
                    try { handler(observation); }
                    catch (Exception error) { ReportObservation(error); }
                }
            }
            catch (Exception error) { ReportObservation(error); }
            finally { observation?.Close(); _observing = false; }
        }

        private static void ReportObservation(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedObservation, 1) != 0) return;
            try { RuntimeEntryPoint.LogSource?.LogWarning("Replication observation failed ("
                + error.GetType().FullName + "). Further observation failures will not be logged."); } catch (Exception) { }
        }
    }

    public enum BoltStateClaimStatus
    {
        /// <summary>The header marker carried no entity update.</summary>
        NoUpdate,
        /// <summary>The claimed ID names an existing incoming proxy of the source connection.</summary>
        KnownIncoming,
        /// <summary>
        /// No incoming proxy of the source and no host-owned entity: a creation, a stale update after a local
        /// detach, a reused ID or another connection's entity. Not proof of an invalid claim.
        /// </summary>
        UnknownIncoming,
        /// <summary>
        /// No incoming proxy of the source, but the ID resolves to an entity this host owns. The header named
        /// that ID; this does not prove host state changed, since Bolt then takes its unknown-proxy path.
        /// </summary>
        ClaimsLocallyOwnedEntity,
        /// <summary>The header could not be read within the packet bounds. Observation never disconnects for this.</summary>
        Unparsed
    }

    /// <summary>
    /// Immutable copies of one entity update header claim and the host's tables at entry, with no packet
    /// or entity retained. Connection IDs can be reused and are not an authenticated identity.
    /// This is passive evidence for counters or for enabling policies, never a strike on its own.
    /// </summary>
    public sealed class BoltStateAdmissionObservation
    {
        private BoltConnection _source;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private bool _sessionResolved;
        private ServerSession _session;

        internal BoltStateAdmissionObservation(BoltConnection source, BoltStateClaimStatus status,
            ulong? claimedNetworkId, BoltEntitySnapshot existingIncomingEntity,
            BoltEntitySnapshot localEntityForClaim, bool guardActive)
        {
            _source = source;
            SourceConnectionId = source.ConnectionId;
            Status = status;
            ClaimedNetworkId = claimedNetworkId;
            ExistingIncomingEntity = existingIncomingEntity;
            LocalEntityForClaim = localEntityForClaim;
            GuardActive = guardActive;
        }

        public uint SourceConnectionId { get; }
        public BoltStateClaimStatus Status { get; }
        /// <summary>The wire header's client-written claim, or null for NoUpdate and Unparsed.</summary>
        public ulong? ClaimedNetworkId { get; }
        /// <summary>The source connection's incoming proxy at entry, or null.</summary>
        public BoltEntitySnapshot ExistingIncomingEntity { get; }
        /// <summary>The host-owned entity the claimed ID resolves to, only for ClaimsLocallyOwnedEntity.</summary>
        public BoltEntitySnapshot LocalEntityForClaim { get; }
        /// <summary>
        /// True when the receive guard covered this packet, so a Receiving policy could have rejected it.
        /// It does not mean any policy ran or inspected this update.
        /// </summary>
        public bool GuardActive { get; }

        /// <summary>Resolves the exact source session. Valid only on the game thread during the Observed callback.</summary>
        public bool TryGetSourceSession(out ServerSession session)
        {
            session = null;
            try
            {
                var source = _source;
                if (source == null || Environment.CurrentManagedThreadId != _thread || source.WasCollected
                    || source.ConnectionId != SourceConnectionId) return false;
                if (!_sessionResolved) { _session = ServerSessions.ResolveSource(source); _sessionResolved = true; }
                if (_session == null || !_session.IsCurrent) return false;
                session = _session;
                return true;
            }
            catch (Exception) { return false; }
        }

        internal void Close() { _source = null; }
    }
}
