// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;

namespace Modstoke.Sons
{
    public enum BoltMigrationRole { Source, Destination, Observer }

    /// <summary>
    /// Explicit, game-thread-only cooperative migration. The mode owns reliable snapshot delivery,
    /// peer authorization, complete type adapters and the all-peer barrier. No background work or
    /// automatic retry is installed. An Unconfirmed session must not be replaced by a fresh attempt.
    /// </summary>
    public sealed class BoltMigrationSession
    {
        [ThreadStatic] private static bool _busy;
        private static readonly Dictionary<ulong, Guid> Reservations = new();
        private static Guid _run;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly ControlPeer _peer;
        private readonly IBoltMigrationAdapter _adapter;
        private readonly IBoltMigrationReferenceRepair _references;
        private readonly Guid _reservation;
        private readonly ulong _oldId;
        private BoltMigrationNative _native;
        private BoltMigrationNative _replacement;
        private bool _mutationAttempted;
        private bool _replicaQuiesced;
        private bool _playerBridgePrepared, _playerIdentityPrepared;
        private bool _resumeAuthorization;
        private ControlPeer _resumeSender;
        private BoltMigrationReceipt _resumeReceipt;
        private BoltMigrationBarrier _resumeBarrier;

        public BoltMigrationRole Role { get; }
        public BoltMigrationSnapshot Snapshot { get; private set; }
        public BoltMigrationResult LastResult { get; private set; }
        public BoltMigrationStatus Status => LastResult.Status;
        public ulong NewNetworkId => _replacement?.NetworkId ?? 0;
        public BoltMigrationReceipt ReadyReceipt { get; private set; }
        public BoltMigrationReceipt LocalRepairReceipt { get; private set; }
        internal ControlPeer Peer => _peer;

        internal bool PlayerBridgeLifetimeCurrent => Status == BoltMigrationStatus.Completed
            || (_playerBridgePrepared && (Role == BoltMigrationRole.Destination
                ? Status == BoltMigrationStatus.Recreated
                : Status == BoltMigrationStatus.ReferencesRepaired));

        internal bool TryPreparePlayerBridge(BoltMigrationBarrier barrier, out string detail)
        {
            detail = "Player endpoints require exact local repair before writer resumption.";
            if (!Begin(out _)) return false;
            try
            {
                if (_adapter is not PlayerMigrationAdapter
                    || Role == BoltMigrationRole.Observer || !ContractCurrent()
                    || _replacement == null || !_replacement.IsCurrent() || !_native.IsRetired()
                    || LocalRepairReceipt == null || !LocalRepairReceipt.Matches(Snapshot)
                    || LocalRepairReceipt.Kind != BoltMigrationReceiptKind.Repaired
                    || LocalRepairReceipt.NewNetworkId != NewNetworkId
                    || (Role == BoltMigrationRole.Destination
                        ? Status != BoltMigrationStatus.Recreated
                        : Status != BoltMigrationStatus.ReferencesRepaired)) return false;
                if (Role == BoltMigrationRole.Destination)
                {
                    if (!BarrierMatches(barrier) || !barrier.IsRepaired(NewNetworkId)
                        || !EnsurePreparedPlayerIdentity(barrier, out detail)) return false;
                }
                else if (barrier != null) return false;
                _playerBridgePrepared = true;
                detail = "Inventory-only endpoints authorized for the exact held, locally repaired player.";
                return true;
            }
            catch (Exception error)
            { detail = "Player endpoint preparation failed (" + error.GetType().Name + ")."; return false; }
            finally { _busy = false; }
        }

        private bool EnsurePreparedPlayerIdentity(BoltMigrationBarrier barrier, out string detail)
        {
            if (!_playerIdentityPrepared)
            {
                if (!ServerWorld.CommitPreparedPlayerIdentity(this, barrier, out detail)) return false;
                _playerIdentityPrepared = true;
                return true;
            }
            detail = "The prepared server identity or repair barrier changed.";
            return BarrierMatches(barrier) && barrier.IsRepaired(NewNetworkId)
                && ServerWorld.TryCapturePlayerBinding(_peer.ServerSession, out var binding, out _)
                && binding.NetworkId == NewNetworkId && binding.Player?.Pointer == _replacement.Entity.Pointer
                && ServerWorld.IsCurrentMigratedPlayerBinding(binding, _peer);
        }

        internal bool TryReadCompletedPlayerReplica(out BoltEntity entity, out string detail)
        {
            entity = null;
            detail = "A completed observer session and its exact current player replica are required.";
            if (!Begin(out _)) return false;
            try
            {
                if (Status != BoltMigrationStatus.Completed
                    || Role != BoltMigrationRole.Observer || _peer.ServerSession != null
                    || _native.PrefabId != BoltPrefabs.PlayerNetwork.Value
                    || _adapter is not PlayerMigrationAdapter
                    || !PlayerRoles.IsAvailable || !ContractCurrent()
                    || _replacement == null || !_native.IsRetired() || !_replacement.IsCurrent()) return false;
                entity = _replacement.Entity;
                detail = "The completed observer's exact replacement remains current.";
                return true;
            }
            catch (Exception error)
            { detail = "Observer replica validation failed (" + error.GetType().Name + ")."; return false; }
            finally { _busy = false; }
        }

        internal bool TryReadPlayerBridgeTarget(out BoltEntity entity, out string detail, bool allowPrepared = false)
        {
            entity = null;
            detail = "A completed player migration on its enrolled game thread is required.";
            if (!Begin(out _)) return false;
            try
            {
                if ((Status != BoltMigrationStatus.Completed
                        && !(allowPrepared && _playerBridgePrepared && PlayerBridgeLifetimeCurrent))
                    || _native.PrefabId != BoltPrefabs.PlayerNetwork.Value
                    || _adapter is not IBoltPlayerMigrationAdapter
                    || !PlayerRoles.IsAvailable
                    || !((Role == BoltMigrationRole.Source && _native.RetainsLocalPlayerRig
                            && _peer.ServerSession == null)
                        || (Role == BoltMigrationRole.Destination && _peer.ServerSession != null))
                    || _replacement == null || !ContractCurrent()
                    || !_native.IsRetired() || !_replacement.IsCurrent()) return false;
                entity = _replacement.Entity;
                detail = "The exact player replacement and retained migration lifetimes are current.";
                return true;
            }
            catch (Exception error)
            {
                detail = "Player bridge validation failed (" + error.GetType().Name + ").";
                return false;
            }
            finally { _busy = false; }
        }

        internal bool TryReadDestinationForIdentity(ServerSession playerSession,
            BoltMigrationBarrier barrier, out BoltEntity entity)
        {
            entity = null;
            if (Environment.CurrentManagedThreadId != _thread || Role != BoltMigrationRole.Destination
                || !ReferenceEquals(_peer.ServerSession, playerSession) || playerSession == null
                || (Status != BoltMigrationStatus.Recreated && Status != BoltMigrationStatus.Completed)
                || _replacement == null || !_replacement.IsCurrent() || !_native.IsRetired()
                || !BarrierMatches(barrier) || !barrier.IsRepaired(NewNetworkId)) return false;
            entity = _replacement.Entity;
            return true;
        }

        private BoltMigrationSession(ControlPeer peer,
            BoltMigrationRole role, BoltMigrationNative native,
            IBoltMigrationAdapter adapter, IBoltMigrationReferenceRepair references,
            Guid reservation)
        {
            _peer = peer; Role = role; _native = native; _adapter = adapter;
            _references = references; _reservation = reservation; _oldId = native.NetworkId;
            LastResult = new(BoltMigrationStatus.Preparing, false, "Preparing migration.");
        }

        public static bool TryPrepareSource(ControlPeer destination, ulong entityId,
            ulong generation, IBoltMigrationAdapter adapter,
            out BoltMigrationSession session, out BoltMigrationResult result)
            => Prepare(destination, entityId, generation, null, adapter, BoltMigrationRole.Source,
                out session, out result);

        public static bool TryPrepareDestination(ControlPeer source,
            BoltMigrationSnapshot snapshot, IBoltMigrationAdapter adapter,
            out BoltMigrationSession session, out BoltMigrationResult result)
            => Prepare(source, snapshot?.Plan.OldNetworkId ?? 0, 0, snapshot, adapter,
                BoltMigrationRole.Destination, out session, out result);

        public static bool TryPrepareObserver(ControlPeer server,
            BoltMigrationSnapshot snapshot, IBoltMigrationAdapter adapter,
            out BoltMigrationSession session, out BoltMigrationResult result)
            => Prepare(server, snapshot?.Plan.OldNetworkId ?? 0, 0, snapshot, adapter,
                BoltMigrationRole.Observer, out session, out result);

        private static bool Prepare(ControlPeer peer, ulong entityId, ulong generation,
            BoltMigrationSnapshot snapshot, IBoltMigrationAdapter adapter,
            BoltMigrationRole role, out BoltMigrationSession session,
            out BoltMigrationResult result)
        {
            session = null;
            result = new(BoltMigrationStatus.Aborted, false,
                "A current enrolled peer, complete adapter and non-reentrant game-thread context are required.");
            if (!Enter(peer)) return false;
            try
            {
                bool source = role == BoltMigrationRole.Source;
                string detail = "The type contract or migration identity is invalid.";
                if (adapter == null || adapter.ContractId == Guid.Empty || adapter.ContractVersion == 0
                    || (source ? generation == 0 : snapshot == null)
                    || (role == BoltMigrationRole.Observer && peer.ServerSession != null)
                    || (!source && (snapshot.Plan.ContractId != adapter.ContractId
                        || snapshot.Plan.ContractVersion != adapter.ContractVersion)))
                    return RejectPreparation(detail, out result);
                var contractId = adapter.ContractId;
                var contractVersion = adapter.ContractVersion;
                if (!BoltMigrationNative.TryCapture(peer, entityId, source, out var native, out detail)
                    || (!source && (native.PrefabId != snapshot.Plan.PrefabId
                        || native.StateTypeId != snapshot.Plan.StateTypeId))
                    || !ValidatePlayerLifecycle(peer, native, adapter, role, out detail)
                    || !adapter.CanMigrate(native.Entity, out detail)
                    || (!source && !adapter.TryValidateSnapshot(snapshot, out detail))
                    || (role != BoltMigrationRole.Observer && !native.TryValidatePrefab(out detail))
                    || !adapter.TryPrepareReferences(entityId, out var references, out detail)
                    || references == null || !native.IsCurrent() || adapter.ContractId != contractId
                    || adapter.ContractVersion != contractVersion)
                    return RejectPreparation(detail, out result);

                if (_run != peer.RunId) { Reservations.Clear(); _run = peer.RunId; }
                if (Reservations.ContainsKey(entityId))
                    return RejectPreparation("This entity already has a pending or uncertain migration in this run.", out result);
                var token = Guid.NewGuid();
                Reservations.Add(entityId, token);
                session = new(peer, role, native, adapter, references, token);
                var plan = source
                    ? new BoltMigrationPlan(token, generation, entityId,
                        native.PrefabId, native.StateTypeId, contractId, contractVersion)
                    : snapshot.Plan;
                if (adapter is IBoltMigrationPlanParticipant participant
                    && (!participant.TryBeginPreparation(plan, role, peer, out detail) || !native.IsCurrent()
                        || adapter.ContractId != contractId || adapter.ContractVersion != contractVersion))
                {
                    session.Release();
                    session = null;
                    return RejectPreparation(detail, out result);
                }
                if (source)
                {
                    session._mutationAttempted = true;
                    if (!adapter.TryQuiesce(native.Entity, out detail) || !native.IsCurrent()
                        || !adapter.TryCapture(native.Entity, out var payload, out detail) || payload == null
                        || !native.IsCurrent()
                        || !BoltMigrationNative.TryCapture(peer, entityId, true, out var frozen, out detail)
                        || frozen.RetainsLocalPlayerRig != native.RetainsLocalPlayerRig)
                        return session.RejectFrozenPreparation(detail, out result);
                    if (frozen.PrefabId != plan.PrefabId || frozen.StateTypeId != plan.StateTypeId)
                        return session.RejectFrozenPreparation("The frozen native schema differs from the prepared plan.", out result);
                    if (!BoltMigrationSnapshot.TryCreate(plan, frozen.Position, frozen.Rotation,
                            payload, out snapshot))
                        return session.RejectFrozenPreparation("The captured payload or pose is outside the migration contract.", out result);
                    session._native = frozen;
                    session.Snapshot = snapshot;
                    if (!adapter.TryValidateSnapshot(snapshot, out detail) || !session.ContractCurrent()
                        || !frozen.IsCurrent()) return session.RejectFrozenPreparation(detail, out result);
                }
                session.Snapshot = snapshot;
                if (!source && adapter is IBoltMigrationReplicaLifecycle replica)
                {
                    session._mutationAttempted = true;
                    session._replicaQuiesced = true;
                    if (!replica.TryQuiesceReplica(native.Entity, role, out detail) || !native.IsCurrent()
                        || !session.ContractCurrent())
                        return session.RejectFrozenPreparation(detail, out result);
                }
                // A player's destination cannot advertise readiness without reserving the exact
                // original client identity. This reservation also prevents competing handoffs.
                if (!ServerWorld.PreparePlayerIdentityForSession(session, out detail))
                {
                    if (session._mutationAttempted)
                        return session.RejectFrozenPreparation(detail, out result);
                    session.Release();
                    session = null;
                    return RejectPreparation(detail, out result);
                }
                session.ReadyReceipt = BoltMigrationReceipt.Create(snapshot,
                    BoltMigrationReceiptKind.Ready);
                return session.Succeed(BoltMigrationStatus.Prepared,
                    "Local preparation completed; all-peer readiness is still required.", session.ReadyReceipt, out result);
            }
            catch (Exception error)
            {
                string detail = "Migration preparation failed (" + error.GetType().Name + ").";
                if (session != null && session._mutationAttempted) return session.RejectFrozenPreparation(detail, out result);
                if (session != null)
                {
                    if (!ServerWorld.CancelPreparedPlayerIdentity(session))
                        return session.Unconfirmed("Player identity preparation cleanup is unconfirmed.", out result);
                    session.Release();
                    session = null;
                }
                return RejectPreparation(detail, out result);
            }
            finally { _busy = false; }
        }

        /// <summary>
        /// Source only. A server uses its exact current barrier and a null sender. A client accepts
        /// only its enrolled server's aggregate readiness receipt. Native retirement is never replayed.
        /// </summary>
        public bool TryRetire(ControlPeer sender, BoltMigrationReceipt ready,
            BoltMigrationBarrier barrier, out BoltMigrationResult result)
        {
            if (!Begin(out result)) return false;
            try
            {
                if (Role != BoltMigrationRole.Source
                    || !Aggregate(sender, ready, barrier, BoltMigrationReceiptKind.Ready))
                    return Reject("The exact all-peer readiness authorization is required.", out result);
                if (Status == BoltMigrationStatus.Retired)
                    return Cached(out result);
                if (Status != BoltMigrationStatus.Prepared || !_native.IsCurrent())
                    return Reject("The original prepared source is no longer available for retirement.", out result);
                // Quiescence is an adapter obligation. Re-capture once at the destructive boundary
                // so a stale snapshot cannot silently replace changed state or pose.
                if (!_adapter.TryCapture(_native.Entity, out var payload, out var detail) || payload == null
                    || !_native.IsCurrent()
                    || !BoltMigrationNative.TryCapture(_peer, _oldId, true, out var current, out detail)
                    || current.RetainsLocalPlayerRig != _native.RetainsLocalPlayerRig
                    || !BoltMigrationSnapshot.TryCreate(Snapshot.Plan, current.Position, current.Rotation,
                        payload, out var recaptured) || !recaptured.MatchesDigest(Snapshot.DigestSpan)
                    || !ContractCurrent())
                    return Reject("The prepared source state changed or could not be recaptured; cancel before retrying.", out result);
                if (!Aggregate(sender, ready, barrier, BoltMigrationReceiptKind.Ready))
                    return Reject("The readiness cohort changed during source validation.", out result);
                if (_adapter is PlayerMigrationAdapter player
                    && !player.TrySealSourceEnvironment(this, out detail))
                    return Reject("Player environment handoff could not be sealed: " + detail, out result);
                var effect = _native.Retire(out detail);
                if (effect == BoltMigrationMutationResult.Rejected) return Reject(detail, out result);
                if (effect != BoltMigrationMutationResult.Applied) return Unconfirmed(detail, out result);
                return Succeed(BoltMigrationStatus.Retired, detail,
                    BoltMigrationReceipt.Create(Snapshot, BoltMigrationReceiptKind.Retired), out result);
            }
            catch (Exception error) { return Unconfirmed("Retirement failed (" + error.GetType().Name + ").", out result); }
            finally { _busy = false; }
        }

        /// <summary>
        /// Destination only. Wait for ordinary removal of the old replica, then create a fresh owner,
        /// restore the explicit type contract and repair local references. It remains quiescent.
        /// InitializingReplacement means creation succeeded but startup is still pending; call
        /// TryFinishRecreation with the same readiness barrier, never create another entity.
        /// </summary>
        public bool TryRecreate(ControlPeer sender, BoltMigrationReceipt retired,
            BoltMigrationBarrier barrier, out BoltMigrationResult result)
        {
            if (!Begin(out result)) return false;
            try
            {
                if (Role != BoltMigrationRole.Destination || !FromPeer(sender, retired,
                        BoltMigrationReceiptKind.Retired) || !ReadyBarrier(barrier))
                    return Reject("The original owner retirement and current server readiness barrier are required.", out result);
                if (Status == BoltMigrationStatus.Recreated
                    || Status == BoltMigrationStatus.InitializingReplacement) return Cached(out result);
                if (Status != BoltMigrationStatus.Prepared)
                    return Reject("This session cannot recreate again.", out result);
                if (!_native.IsRetired())
                    return Reject("Waiting for the old identity and incoming proxy to finish ordinary native retirement.", out result);
                if (!ContractCurrent() || !ServerWorld.HasPreparedPlayerIdentity(this)
                    || !_adapter.TryValidateSnapshot(Snapshot, out var detail)
                    || !ReadyBarrier(barrier))
                    return Reject("The type contract, player identity reservation or readiness cohort changed before recreation.", out result);
                var effect = _native.Recreate(Snapshot, out var entity, out detail);
                if (effect == BoltMigrationMutationResult.Rejected) return Reject(detail, out result);
                _mutationAttempted = true;
                if (effect != BoltMigrationMutationResult.Applied
                    || !CaptureReplacement(entity, true, out detail)) return Unconfirmed(detail, out result);
                if (_adapter is IBoltMigrationReplacementAdmission admission)
                {
                    if (!admission.TryQuiesceReplacement(entity, Snapshot, out detail)
                        || !_replacement.IsCurrent() || !ContractCurrent() || !ReadyBarrier(barrier))
                        return Unconfirmed("Replacement startup hold is unconfirmed: " + detail, out result);
                    Succeed(BoltMigrationStatus.InitializingReplacement,
                        "Replacement created once; waiting for native initialization under its writer hold.",
                        null, out result);
                    return FinishRecreation(barrier, out result);
                }
                if (!_adapter.CanMigrate(entity, out detail) || !_replacement.IsCurrent()
                    || !_adapter.TryQuiesce(entity, out detail) || !_replacement.IsCurrent())
                    return Unconfirmed(detail, out result);
                return RestoreReplacement(barrier, out result);
            }
            catch (Exception error)
            {
                return Unconfirmed("Recreation failed (" + error.GetType().Name + ").", out result);
            }
            finally { _busy = false; }
        }

        /// <summary>Poll startup, then restore and repair once. Pending never repeats native creation or its hold.</summary>
        public bool TryFinishRecreation(BoltMigrationBarrier barrier,
            out BoltMigrationResult result)
        {
            if (!Begin(out result)) return false;
            try
            {
                if (Role != BoltMigrationRole.Destination || !ReadyBarrier(barrier))
                    return Reject("The original destination readiness barrier is required.", out result);
                if (Status == BoltMigrationStatus.Recreated) return Cached(out result);
                if (Status != BoltMigrationStatus.InitializingReplacement)
                    return Reject("No newly created replacement is waiting for initialization.", out result);
                return FinishRecreation(barrier, out result);
            }
            catch (Exception error)
            { return Unconfirmed("Replacement initialization failed (" + error.GetType().Name + ").", out result); }
            finally { _busy = false; }
        }

        private bool FinishRecreation(BoltMigrationBarrier barrier,
            out BoltMigrationResult result)
        {
            if (!ContractCurrent() || !_native.IsRetired() || _replacement == null
                || !_replacement.IsCurrent() || !ReadyBarrier(barrier)
                || _adapter is not IBoltMigrationReplacementAdmission admission)
                return Unconfirmed("The held replacement, contract or readiness barrier changed.", out result);
            var readiness = admission.ReadReplacementAdmission(_replacement.Entity, Role, Snapshot, out var detail);
            if (!ContractCurrent() || !_native.IsRetired() || !_replacement.IsCurrent() || !ReadyBarrier(barrier))
                return Unconfirmed("Replacement initialization changed its native lifetime or cohort.", out result);
            if (readiness == BoltMigrationAdmission.Pending)
                return Succeed(BoltMigrationStatus.InitializingReplacement, detail, null, out result);
            if (readiness != BoltMigrationAdmission.Ready)
                return Unconfirmed("Replacement initialization rejected: " + detail, out result);
            if (!_adapter.CanMigrate(_replacement.Entity, out detail) || !_replacement.IsCurrent()
                || !ContractCurrent() || !ReadyBarrier(barrier)) return Unconfirmed(detail, out result);
            return RestoreReplacement(barrier, out result);
        }

        private bool RestoreReplacement(BoltMigrationBarrier barrier,
            out BoltMigrationResult result)
        {
            if (!ContractCurrent() || !_native.IsRetired() || _replacement == null
                || !_replacement.IsCurrent() || !ReadyBarrier(barrier))
                return Unconfirmed("Replacement restoration lost its native lifetime or readiness authority.", out result);
            var entity = _replacement.Entity;
            if (!_adapter.TryRestore(entity, Snapshot, out var detail) || !_replacement.IsCurrent()
                || !ContractCurrent() || !ReadyBarrier(barrier)) return Unconfirmed(detail, out result);
            var effect = _references.TryRepair(entity, out detail);
            if (!Successful(effect) || !_native.IsRetired() || !_replacement.IsCurrent()
                || !ContractCurrent() || !ReadyBarrier(barrier))
                return Unconfirmed(detail, out result);
            if (_adapter is IBoltMigrationReplicaLifecycle replica
                && (!replica.TryRebindReplica(entity, Snapshot, out detail) || !_replacement.IsCurrent()
                    || !ContractCurrent() || !ReadyBarrier(barrier)))
                return Unconfirmed("Local replica rebinding is unconfirmed: " + detail, out result);
            LocalRepairReceipt = BoltMigrationReceipt.Create(Snapshot,
                BoltMigrationReceiptKind.Repaired, NewNetworkId);
            return Succeed(BoltMigrationStatus.Recreated,
                "Replacement state and local references restored; awaiting all-peer reference repair before resuming.",
                BoltMigrationReceipt.Create(Snapshot,
                    BoltMigrationReceiptKind.Recreated, NewNetworkId), out result);
        }

        /// <summary>Source or observer: resolve the new replica and repair only explicitly captured references.</summary>
        public bool TryRepairReferences(ControlPeer sender, BoltMigrationReceipt recreated,
            out BoltMigrationResult result)
        {
            if (!Begin(out result)) return false;
            try
            {
                if (Role == BoltMigrationRole.Destination
                    || !FromPeer(sender, recreated, BoltMigrationReceiptKind.Recreated))
                    return Reject("The authorized replacement receipt is required.", out result);
                if (Status == BoltMigrationStatus.Completed
                    || Status == BoltMigrationStatus.ReferencesRepaired)
                    return recreated.NewNetworkId == NewNetworkId ? Cached(out result)
                        : Reject("The replacement identity conflicts with the recorded repair.", out result);
                var expected = Role == BoltMigrationRole.Source
                    ? BoltMigrationStatus.Retired : BoltMigrationStatus.Prepared;
                if (Status != expected || !_native.IsRetired())
                    return Reject("The original entity has not completed retirement on this peer.", out result);
                if (!ContractCurrent()) return Unconfirmed("The reference contract changed after retirement.", out result);
                if (_adapter is IBoltMigrationReplacementIdentity identity
                    && (!identity.TryAuthorizeReplacementIdentity(recreated.NewNetworkId, out var identityDetail)
                        || !ContractCurrent()))
                    return Unconfirmed("Replacement identity authorization failed: " + identityDetail, out result);
                var entity = BoltNetwork.FindEntity(new NetworkId(recreated.NewNetworkId));
                if (!CaptureReplacement(entity, false, out var detail)) return Reject(detail, out result);
                if (!ContractCurrent()) return Unconfirmed("The reference contract changed after retirement.", out result);
                if (_adapter is IBoltMigrationReplacementAdmission admission
                    && admission.ReadReplacementAdmission(entity, Role, Snapshot, out detail)
                        != BoltMigrationAdmission.Ready)
                    return Reject("Replacement references are not ready: " + detail, out result);
                if (!_replacement.IsCurrent() || !ContractCurrent())
                    return Unconfirmed("Replacement readiness changed the reference contract or native lifetime.", out result);
                _mutationAttempted = true;
                var effect = _references.TryRepair(entity, out detail);
                if (!Successful(effect) || !_replacement.IsCurrent()) return Unconfirmed(detail, out result);
                bool retainedPlayer = Role == BoltMigrationRole.Source && _native.RetainsLocalPlayerRig;
                if (retainedPlayer
                    && (_adapter is not IBoltPlayerMigrationAdapter playerAdapter
                        || !ContractCurrent() || !_native.IsRetired()
                        || !playerAdapter.TryRebindRetainedPlayer(_native.Entity, entity, Snapshot, out detail)
                        || !_native.IsRetired() || !_replacement.IsCurrent() || !ContractCurrent()))
                    return Unconfirmed("Retained player rebinding is unconfirmed: " + detail, out result);
                var replica = _adapter as IBoltMigrationReplicaLifecycle;
                if (replica != null
                    && (!replica.TryRebindReplica(entity, Snapshot, out detail)
                        || !_native.IsRetired() || !_replacement.IsCurrent() || !ContractCurrent()))
                    return Unconfirmed("Peer-local replica rebinding is unconfirmed: " + detail, out result);
                LocalRepairReceipt = BoltMigrationReceipt.Create(Snapshot,
                    BoltMigrationReceiptKind.Repaired, NewNetworkId);
                if (retainedPlayer || replica != null)
                    return Succeed(BoltMigrationStatus.ReferencesRepaired,
                        "Local references and retained objects rebound; writers remain quiescent until aggregate repair authorization.",
                        LocalRepairReceipt, out result);
                Release();
                return Succeed(BoltMigrationStatus.Completed,
                    "Local references repaired; the new owner still requires aggregate authorization to resume.",
                    LocalRepairReceipt, out result);
            }
            catch (Exception error) { return Unconfirmed("Reference repair failed (" + error.GetType().Name + ").", out result); }
            finally { _busy = false; }
        }

        /// <summary>Retained source only: resume the local rig after every participant has repaired references.</summary>
        public bool TryResumeRetainedSource(ControlPeer sender, BoltMigrationReceipt repaired,
            BoltMigrationBarrier barrier, out BoltMigrationResult result)
        {
            if (Role != BoltMigrationRole.Source || !_native.RetainsLocalPlayerRig)
                return Reject("Only the original retained player source can use this entry point.", out result);
            return TryResumeParticipant(sender, repaired, barrier, out result);
        }

        /// <summary>Resume retained source or observer objects only after every participant has repaired references.</summary>
        public bool TryResumeParticipant(ControlPeer sender, BoltMigrationReceipt repaired,
            BoltMigrationBarrier barrier, out BoltMigrationResult result)
        {
            if (!Begin(out result)) return false;
            try
            {
                bool retainedPlayer = Role == BoltMigrationRole.Source && _native.RetainsLocalPlayerRig;
                var replica = _adapter as IBoltMigrationReplicaLifecycle;
                if (Role == BoltMigrationRole.Destination || (!retainedPlayer && replica == null)
                    || !Aggregate(sender, repaired, barrier, BoltMigrationReceiptKind.Repaired)
                    || repaired.NewNetworkId != NewNetworkId)
                    return Reject("The current all-peer repair authorization must identify this participant's replacement.", out result);
                if (Status == BoltMigrationStatus.Completed) return Cached(out result);
                if (Status != BoltMigrationStatus.ReferencesRepaired)
                    return Reject("No verified, rebound participant is ready to resume.", out result);
                string detail = "The retained participant contract or native lifetimes changed before resumption.";
                if ((retainedPlayer && (_adapter is not IBoltPlayerMigrationAdapter
                        || !PlayerRoles.IsAvailable)) || !ContractCurrent()
                    || !_native.IsRetired() || _replacement == null || !_replacement.IsCurrent())
                    return Unconfirmed(detail, out result);
                _mutationAttempted = true;
                SetResumeAuthorization(sender, repaired, barrier);
                if ((retainedPlayer && !((IBoltPlayerMigrationAdapter)_adapter)
                        .TryResumeRetainedPlayer(_native.Entity, _replacement.Entity, Snapshot, out detail))
                    || (replica != null && !replica.TryResumeReplica(_replacement.Entity, Snapshot, out detail))
                    || !_native.IsRetired() || !_replacement.IsCurrent() || !ContractCurrent()
                    || (retainedPlayer && !PlayerRoles.IsAvailable)
                    || !Aggregate(sender, repaired, barrier, BoltMigrationReceiptKind.Repaired))
                    return Unconfirmed("Retained participant resumption or cohort continuity is unconfirmed: " + detail, out result);
                Release();
                return Succeed(BoltMigrationStatus.Completed,
                    "Retained participant writers resumed after complete peer and local reference repair.",
                    LocalRepairReceipt, out result);
            }
            catch (Exception error)
            {
                return Unconfirmed("Retained participant resumption failed (" + error.GetType().Name + ").", out result);
            }
            finally { ClearResumeAuthorization(); _busy = false; }
        }

        /// <summary>Destination only: resume after the server has collected every participant's repair receipt.</summary>
        public bool TryComplete(ControlPeer sender, BoltMigrationReceipt repaired,
            BoltMigrationBarrier barrier, out BoltMigrationResult result)
        {
            if (!Begin(out result)) return false;
            try
            {
                if (Role != BoltMigrationRole.Destination
                    || !Aggregate(sender, repaired, barrier, BoltMigrationReceiptKind.Repaired)
                    || repaired.NewNetworkId != NewNetworkId)
                    return Reject("The current all-peer repair authorization must identify this replacement.", out result);
                if (Status == BoltMigrationStatus.Completed) return Cached(out result);
                if (Status != BoltMigrationStatus.Recreated || !_replacement.IsCurrent())
                    return Reject("No verified, quiescent replacement is ready to resume.", out result);
                // Native resume callbacks may resolve this character through its original client.
                // Publish a reserved identity only after the repair barrier, before those callbacks.
                string detail = "The migration contract changed.";
                SetResumeAuthorization(sender, repaired, barrier);
                if (!ContractCurrent()
                    || !EnsurePreparedPlayerIdentity(barrier, out detail)
                    || !_replacement.IsCurrent()
                    || !_adapter.TryResume(_replacement.Entity, out detail)
                    || !_replacement.IsCurrent()
                    || !Aggregate(sender, repaired, barrier, BoltMigrationReceiptKind.Repaired))
                    return Unconfirmed("Replacement resumption or cohort continuity is unconfirmed: " + detail, out result);
                if (_adapter is IBoltMigrationReplicaLifecycle replica
                    && (!replica.TryResumeReplica(_replacement.Entity, Snapshot, out detail)
                        || !_replacement.IsCurrent() || !ContractCurrent()
                        || !Aggregate(sender, repaired, barrier, BoltMigrationReceiptKind.Repaired)))
                    return Unconfirmed("Destination replica resumption is unconfirmed: " + detail, out result);
                Release();
                return Succeed(BoltMigrationStatus.Completed,
                    "Replacement resumed after complete peer and local reference repair.", LocalRepairReceipt, out result);
            }
            catch (Exception error) { return Unconfirmed("Resumption failed (" + error.GetType().Name + ").", out result); }
            finally { ClearResumeAuthorization(); _busy = false; }
        }

        private void SetResumeAuthorization(ControlPeer sender,
            BoltMigrationReceipt receipt, BoltMigrationBarrier barrier)
        { _resumeSender = sender; _resumeReceipt = receipt; _resumeBarrier = barrier; _resumeAuthorization = true; }

        private void ClearResumeAuthorization()
        { _resumeAuthorization = false; _resumeSender = null; _resumeReceipt = null; _resumeBarrier = null; }

        // Read-only within this session's already authenticated resume operation. Do not
        // enter the global mutation gate again from a nested occupied-player callback.
        internal bool TryAuthorizeCartPlayerRebind(PlayerBridge bridge,
            CartPlayerMigrationContext context,
            PlayerClientMovement.NativeModeKey expected,
            out PlayerClientMovement.NativeModeKey replacement, out string detail)
        {
            replacement = default;
            detail = "Cart rebind requires this exact authenticated all-peer repair/resume scope.";
            if (Environment.CurrentManagedThreadId != _thread || !_busy || !_resumeAuthorization
                || context == null || Snapshot == null || context.Plan != Snapshot.Plan || context.Role != Role
                || _adapter is not VehiclePreservation.GolfCartMigrationAdapter
                || bridge == null || bridge.IsDisposed || bridge.Peer.RunId != _peer.RunId
                || bridge.IsServer != (Role == BoltMigrationRole.Destination)
                || !bridge.Peer.IsCurrent || !_peer.IsCurrent
                || (!bridge.IsServer && !ReferenceEquals(bridge.Peer, _peer))
                || !ContractCurrent() || _native == null || !_native.IsRetired()
                || _replacement == null || !_replacement.IsCurrent()
                || LocalRepairReceipt == null || !LocalRepairReceipt.Matches(Snapshot)
                || LocalRepairReceipt.NewNetworkId != NewNetworkId
                || LocalRepairReceipt.Kind != BoltMigrationReceiptKind.Repaired
                || !Aggregate(_resumeSender, _resumeReceipt, _resumeBarrier, BoltMigrationReceiptKind.Repaired)
                || _resumeReceipt.NewNetworkId != NewNetworkId
                || Status != (Role == BoltMigrationRole.Destination
                    ? BoltMigrationStatus.Recreated : BoltMigrationStatus.ReferencesRepaired)
                || !PlayerBridgeProtocol.IsCartMode(expected.Mode)
                || expected.AnchorNetworkId != Snapshot.Plan.OldNetworkId
                || expected.TransitionSequence == uint.MaxValue || NewNetworkId == 0
                || NewNetworkId == expected.AnchorNetworkId) return false;
            CartMigrationPlayer matched = null;
            foreach (var player in context.Players)
                if (player.PlayerNetworkId == bridge.NetworkId)
                {
                    if (matched != null) return false;
                    matched = player;
                }
            if (matched == null || matched.Slot != expected.SeatIndex
                || (matched.Slot == 0) != (expected.Mode == PlayerClientMovement.NativeMode.GolfCartDriver)
                || !bridge.TryReadCurrentTarget(out var target) || matched.Entity == null
                || matched.Entity.WasCollected || matched.Entity.Pointer != target.Pointer) return false;
            replacement = expected with { AnchorNetworkId = NewNetworkId,
                TransitionSequence = expected.TransitionSequence + 1 };
            detail = "Exact occupied player and replacement cart authorized by this active repaired session.";
            return true;
        }

        /// <summary>
        /// Cancel only while the original identity is intact. The mode must first cancel its readiness
        /// barrier and revoke distributed readiness; cancellation is not a rollback of native retirement.
        /// </summary>
        public bool TryAbort(out BoltMigrationResult result)
        {
            if (!Begin(out result)) return false;
            try
            {
                if (Status == BoltMigrationStatus.Aborted) return Cached(out result);
                if (Status != BoltMigrationStatus.Prepared || !_native.IsCurrent())
                    return Reject("Only an intact prepared migration can be cancelled.", out result);
                if (Role == BoltMigrationRole.Source
                    && (!ContractCurrent() || !_adapter.TryResume(_native.Entity, out var detail) || !_native.IsCurrent()))
                    return Unconfirmed("The original owner's resumption is unconfirmed.", out result);
                if (_replicaQuiesced && (!((IBoltMigrationReplicaLifecycle)_adapter)
                        .TryCancelReplica(_native.Entity, out _) || !_native.IsCurrent()))
                    return Unconfirmed("The original replica's resumption is unconfirmed.", out result);
                if (!ServerWorld.CancelPreparedPlayerIdentity(this))
                    return Unconfirmed("The prepared player identity could not be cancelled.", out result);
                Release();
                return Succeed(BoltMigrationStatus.Aborted, "Local preparation cancelled.",
                    BoltMigrationReceipt.Create(Snapshot, BoltMigrationReceiptKind.Aborted), out result);
            }
            catch (Exception error) { return Unconfirmed("Cancellation failed (" + error.GetType().Name + ").", out result); }
            finally { _busy = false; }
        }

        private bool CaptureReplacement(BoltEntity entity, bool localOwner, out string detail)
        {
            detail = "Waiting for the exact replacement with the expected owner, prefab and serializer.";
            if (!BoltMigrationNative.MatchesReplacement(_peer, entity, Snapshot, localOwner)) return false;
            if (_replacement != null)
                return _replacement.Entity.Pointer == entity.Pointer && _replacement.IsCurrent();
            return BoltMigrationNative.TryCapture(_peer, entity.networkId.PackedValue, localOwner,
                out _replacement, out detail);
        }

        private static bool ValidatePlayerLifecycle(ControlPeer peer, BoltMigrationNative native,
            IBoltMigrationAdapter adapter, BoltMigrationRole role, out string detail)
        {
            detail = "Built-in player migration supports only a retained client rig transferring to the server, with client observers.";
            if (native.PrefabId != BoltPrefabs.PlayerNetwork.Value) return true;
            bool supportedDirection = role switch
            {
                BoltMigrationRole.Source => peer.ServerSession == null && native.RetainsLocalPlayerRig,
                BoltMigrationRole.Destination => peer.ServerSession != null,
                BoltMigrationRole.Observer => peer.ServerSession == null,
                _ => false
            };
            if (!supportedDirection) return false;
            detail = "Player migration requires an explicit player lifecycle adapter and available native player roles.";
            return adapter is IBoltPlayerMigrationAdapter playerAdapter
                && PlayerRoles.IsAvailable
                && playerAdapter.TryValidatePlayerLifecycle(native.Entity, role, out detail);
        }

        private bool ContractCurrent() => Snapshot != null && _adapter.ContractId == Snapshot.Plan.ContractId
            && _adapter.ContractVersion == Snapshot.Plan.ContractVersion;

        private bool FromPeer(ControlPeer sender, BoltMigrationReceipt receipt,
            BoltMigrationReceiptKind kind) => ReferenceEquals(sender, _peer)
                && receipt != null && receipt.Kind == kind && receipt.Matches(Snapshot);

        private bool Aggregate(ControlPeer sender, BoltMigrationReceipt receipt,
            BoltMigrationBarrier barrier, BoltMigrationReceiptKind kind)
        {
            if (receipt == null || receipt.Kind != kind || !receipt.Matches(Snapshot)) return false;
            if (_peer.ServerSession == null) return barrier == null && ReferenceEquals(sender, _peer);
            return sender == null && BarrierMatches(barrier) && (kind == BoltMigrationReceiptKind.Ready
                ? barrier.IsReady : barrier.IsRepaired(receipt.NewNetworkId));
        }

        private bool ReadyBarrier(BoltMigrationBarrier barrier) => _peer.ServerSession == null
            ? barrier == null : BarrierMatches(barrier) && barrier.IsReady;

        private bool BarrierMatches(BoltMigrationBarrier barrier) => barrier != null
            && barrier.Snapshot != null && barrier.Snapshot.Plan == Snapshot.Plan
            && barrier.Snapshot.MatchesDigest(Snapshot.DigestSpan) && barrier.IsCurrent();

        private bool Begin(out BoltMigrationResult result)
        {
            var status = Status;
            bool mutationAttempted = _mutationAttempted;
            if (Environment.CurrentManagedThreadId == _thread && Enter(_peer))
            {
                result = null;
                return true;
            }
            result = new(status, mutationAttempted, "Use the original non-reentrant game thread and enrolled peer.");
            return false;
        }

        private static bool Enter(ControlPeer peer)
        {
            if (_busy || peer == null) return false;
            _busy = true;
            try
            {
                if (ClientEnrollment.TryRead(peer, out var enrolled) == EnrollmentReadStatus.Success
                    && enrolled != null && ReferenceEquals(enrolled.Peer, peer) && peer.IsCurrent
                    && enrolled.State == EnrollmentState.Enrolled
                    && (enrolled.RequiredCapabilities & BoltMigrationProtocol.Capability) != 0
                    && (enrolled.OfferedCapabilities & BoltMigrationProtocol.Capability) != 0)
                    return true;
            }
            catch (Exception) { }
            _busy = false;
            return false;
        }

        private static bool Successful(BoltMigrationMutationResult effect)
            => effect == BoltMigrationMutationResult.Applied || effect == BoltMigrationMutationResult.Unchanged;

        private void Release()
        {
            if (_run == _peer.RunId && Reservations.TryGetValue(_oldId, out var value) && value == _reservation)
                Reservations.Remove(_oldId);
        }

        // No snapshot or readiness has escaped Prepare yet. Undo the local pause only while
        // the original native attachment is intact; never treat this as retirement rollback.
        private bool RejectFrozenPreparation(string reason, out BoltMigrationResult result)
        {
            try
            {
                if (!_native.IsCurrent())
                    return Unconfirmed(reason + " Original attachment could not be confirmed for resumption.", out result);
                string resumeDetail = "No native pause was recorded.";
                bool resumed = Role == BoltMigrationRole.Source
                    ? _adapter.TryResume(_native.Entity, out resumeDetail)
                    : _replicaQuiesced && ((IBoltMigrationReplicaLifecycle)_adapter)
                        .TryCancelReplica(_native.Entity, out resumeDetail);
                if (!resumed || !_native.IsCurrent())
                    return Unconfirmed(reason + " Original resumption is unconfirmed: " + resumeDetail, out result);
                if (!ServerWorld.CancelPreparedPlayerIdentity(this))
                    return Unconfirmed(reason + " Prepared identity cleanup is unconfirmed.", out result);
                Release();
                LastResult = result = new(BoltMigrationStatus.Aborted, _mutationAttempted,
                    reason + " Preparation cancelled; original owner resumed before any retirement.");
                return false;
            }
            catch (Exception error)
            {
                return Unconfirmed(reason + " Preparation cleanup failed (" + error.GetType().Name + ").", out result);
            }
        }

        private bool Succeed(BoltMigrationStatus status, string detail,
            BoltMigrationReceipt receipt, out BoltMigrationResult result)
        {
            LastResult = result = new(status, _mutationAttempted, detail, receipt);
            return true;
        }

        private bool Unconfirmed(string detail, out BoltMigrationResult result)
        {
            LastResult = result = new(BoltMigrationStatus.Unconfirmed, _mutationAttempted,
                string.IsNullOrWhiteSpace(detail) ? "The migration has partial or unconfirmed effects; automatic retry is disabled." : detail);
            return false;
        }

        private bool Reject(string detail, out BoltMigrationResult result)
        {
            result = new(Status, _mutationAttempted, detail);
            return false;
        }

        private static bool RejectPreparation(string detail, out BoltMigrationResult result)
        {
            result = new(BoltMigrationStatus.Aborted, false, detail);
            return false;
        }

        private bool Cached(out BoltMigrationResult result) { result = LastResult; return true; }
    }
}
