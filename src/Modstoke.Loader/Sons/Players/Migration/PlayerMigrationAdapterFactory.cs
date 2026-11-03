// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;

namespace Modstoke.Sons
{
    /// <summary>
    /// Explicit server policy for one immutable observed inventory. Ready requires a separately
    /// approved fingerprint and reason; Pending performs no native writes and is not approval.
    /// </summary>
    public delegate BoltMigrationAdmission PlayerMigrationInventoryApproval(
        ControlPeer peer, PlayerInventoryBaseline baseline,
        out string approvedFingerprint, out string reason, out string detail);

    /// <summary>Explicit approval of carried-only item custody, catalog capacities and captured settings.</summary>
    public delegate BoltMigrationAdmission PlayerMigrationHeldOnlyApproval(
        ControlPeer peer, PlayerHeldOnlyAuthority.Snapshot baseline,
        out string approvedFingerprint, out string reason, out string detail);

    /// <summary>Independent approval of worn conditions, depletion order and native armour configuration.</summary>
    public delegate BoltMigrationAdmission PlayerMigrationArmourApproval(
        ControlPeer peer, PlayerArmourAuthority.Snapshot baseline,
        out string approvedFingerprint, out string reason, out string detail);

    /// <summary>Independent server approval of the exact source physics observations, not inventory or native coverage.</summary>
    public delegate BoltMigrationAdmission PlayerMigrationPhysicsApproval(
        ControlPeer peer, BoltMigrationPlan plan,
        PlayerCarrierProfile profile, out string reason, out string detail);

    /// <summary>Approval of an exact initial survival observation, independently of item or movement approval.</summary>
    public delegate BoltMigrationAdmission PlayerMigrationSurvivalApproval(
        ControlPeer peer, BoltMigrationPlan plan,
        PlayerSurvivalSnapshot survival, out string reason, out string detail);

    /// <summary>Qualifies the captured water configuration against the server's loaded scene and providers.</summary>
    public delegate bool PlayerMigrationWaterApproval(ControlPeer peer,
        BoltMigrationPlan plan, PlayerSurvivalSnapshot survival,
        PlayerWaterConfiguration configuration);

    /// <summary>
    /// Explicit coordinator factory and ownership ledger. Construction never selects enrollment,
    /// transports or capabilities. Readiness is evaluated against a concrete participant, not
    /// inferred from construction or policy callbacks. Dispose never thaws an uncertain player.
    /// </summary>
    public sealed class PlayerMigrationAdapterFactory : IBoltMigrationCoordinatorFactory, IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly Func<ControlPeer, byte, bool> _supportsDispatcher;
        private readonly Func<BoltMigrationSession> _readSession;
        private readonly Dictionary<(Guid Run, Guid Peer, ulong OldId), Entry> _adapters = new();
        internal PlayerMigrationInventoryApproval ApproveInventory { get; }
        internal PlayerMigrationHeldOnlyApproval ApproveHeldOnly { get; }
        internal PlayerMigrationArmourApproval ApproveArmour { get; }
        internal PlayerMigrationPhysicsApproval ApprovePhysics { get; }
        internal PlayerMigrationSurvivalApproval ApproveSurvival { get; }
        internal PlayerMigrationWaterApproval ApproveWater { get; }
        public PlayerClientCorrectionLimits CorrectionLimits { get; }
        public bool IsDisposed { get; private set; }
        public string Detail { get; private set; } = "Factory constructed; full player readiness has not been established.";

        public PlayerMigrationAdapterFactory(
            Func<ControlPeer, byte, bool> supportsDispatcher,
            PlayerMigrationInventoryApproval approveInventory,
            PlayerMigrationHeldOnlyApproval approveHeldOnly,
            PlayerMigrationArmourApproval approveArmour,
            PlayerMigrationPhysicsApproval approvePhysics,
            PlayerMigrationSurvivalApproval approveSurvival,
            PlayerMigrationWaterApproval approveWater,
            PlayerClientCorrectionLimits correctionLimits,
            Func<BoltMigrationSession> readSession)
        {
            _supportsDispatcher = supportsDispatcher ?? throw new ArgumentNullException(nameof(supportsDispatcher));
            ApproveInventory = approveInventory ?? throw new ArgumentNullException(nameof(approveInventory));
            ApproveHeldOnly = approveHeldOnly ?? throw new ArgumentNullException(nameof(approveHeldOnly));
            ApproveArmour = approveArmour ?? throw new ArgumentNullException(nameof(approveArmour));
            ApprovePhysics = approvePhysics ?? throw new ArgumentNullException(nameof(approvePhysics));
            ApproveSurvival = approveSurvival ?? throw new ArgumentNullException(nameof(approveSurvival));
            ApproveWater = approveWater ?? throw new ArgumentNullException(nameof(approveWater));
            _readSession = readSession ?? throw new ArgumentNullException(nameof(readSession));
            if (!Range(correctionLimits.Position, 0.001f, 1) || !Range(correctionLimits.Velocity, 0, 10)
                || !Range(correctionLimits.Clearance, 0.001f, 0.1f))
                throw new ArgumentOutOfRangeException(nameof(correctionLimits));
            CorrectionLimits = correctionLimits;
        }

        /// <summary>
        /// An idle factory cannot certify native player coverage before a concrete participant
        /// exists. Pending is not authorization to advertise migration or player-bridge capabilities.
        /// </summary>
        public BoltMigrationAdmission ReadReadiness(out string detail)
        {
            detail = "The factory is disposed or is being used from a different thread.";
            if (IsDisposed || Environment.CurrentManagedThreadId != _thread)
                return BoltMigrationAdmission.Rejected;
            Detail = detail = "Readiness requires an exact peer, player, role and transaction; no factory-wide gameplay capability is inferred.";
            return BoltMigrationAdmission.Pending;
        }

        public BoltMigrationAdmission ReadReadiness(BoltMigrationRole role,
            ControlPeer peer, BoltMigrationPlan plan, BoltEntity original,
            BoltMigrationSnapshot snapshot, out string detail)
        {
            if (!TryCreateAdapter(role, peer, plan, out var adapter, out detail))
                return BoltMigrationAdmission.Rejected;
            var result = ((PlayerMigrationAdapter)adapter).ReadAdmission(original, role, snapshot, out detail);
            Detail = detail;
            return result;
        }

        public bool SupportsDispatcher(ControlPeer peer, byte prepareSourceVersion)
        {
            if (!Current(peer) || prepareSourceVersion != BoltMigrationProtocol.PrepareSourceVersion) return false;
            try { return _supportsDispatcher(peer, prepareSourceVersion) && Current(peer); }
            catch (Exception error) { Detail = "Dispatcher policy rejected: " + error.Message; return false; }
        }

        public bool TryCreateAdapter(BoltMigrationRole role, ControlPeer peer,
            BoltMigrationPlan plan, out IBoltMigrationAdapter adapter, out string detail)
        {
            adapter = null;
            detail = "A current exact peer, player contract and known participant role are required.";
            if (!Current(peer) || !PlayerRoles.IsAvailable || !plan.IsValid
                || plan.ContractId != PlayerMigrationAdapter.PlayerContractId
                || plan.ContractVersion != PlayerMigrationAdapter.PlayerContractVersion
                || plan.PrefabId != BoltPrefabs.PlayerNetwork.Value
                || role is not (BoltMigrationRole.Source or BoltMigrationRole.Destination
                    or BoltMigrationRole.Observer)
                || (role == BoltMigrationRole.Destination) != (peer.ServerSession != null)) return false;
            var key = (peer.RunId, peer.PeerId, plan.OldNetworkId);
            if (_adapters.TryGetValue(key, out var prior))
            {
                // Do not create fresh handles to evade a consumed capture or uncertain mutation.
                if (!ReferenceEquals(prior.Peer, peer) || prior.Request != plan || prior.Adapter.Role != role)
                { detail = "This exact player lifetime already belongs to another recorded transaction."; return false; }
                adapter = prior.Adapter;
                detail = "Existing exact adapter returned; no capture, restore or mutation was replayed.";
                return true;
            }
            if (_adapters.Count >= BoltMigrationCoordinator.MaxTransactionsPerRun)
            { detail = "The bounded player factory ledger is full; dispose only after its run has ended."; return false; }
            var value = new PlayerMigrationAdapter(this, role, peer, plan);
            _adapters.Add(key, new(peer, plan, value));
            adapter = value;
            detail = "Player adapter created without native mutation; its admission and factory readiness remain mandatory.";
            return true;
        }

        /// <summary>
        /// Destination convenience for TryRequestMigration, whose coordinator assigns its request ID.
        /// Uses the live native schema, never an assumed PlayerState type number.
        /// </summary>
        public bool TryCreateDestinationAdapter(ControlPeer peer, ulong oldNetworkId, ulong generation,
            out PlayerMigrationAdapter adapter, out string detail)
        {
            adapter = null;
            detail = "A current server-side remote player and nonzero generation are required.";
            if (!Current(peer) || peer.ServerSession == null || generation == 0
                || !BoltMigrationNative.TryCapture(peer, oldNetworkId, false, out var native, out detail)) return false;
            var request = new BoltMigrationPlan(Guid.NewGuid(), generation, oldNetworkId,
                native.PrefabId, native.StateTypeId, PlayerMigrationAdapter.PlayerContractId,
                PlayerMigrationAdapter.PlayerContractVersion);
            if (!TryCreateAdapter(BoltMigrationRole.Destination, peer, request, out var created, out detail)) return false;
            adapter = (PlayerMigrationAdapter)created;
            return true;
        }

        /// <summary>
        /// Exact terminal result only. Binds inventory custody, not movement or player activation.
        /// Completed does not imply that this separate operation succeeded or its handshake is armed.
        /// </summary>
        public bool TryBindCompletedInventory(BoltMigrationSession session,
            BoltMigrationResult result, out string detail)
        {
            detail = "The exact recorded player session and terminal result are required.";
            if (!TryFind(session, out var adapter) || result == null || !ReferenceEquals(session.LastResult, result)) return false;
            if (result.Status == BoltMigrationStatus.Unconfirmed)
            {
                adapter.RetainUnconfirmed(result.Detail);
                Detail = detail = result.Detail;
                return false;
            }
            bool activated = adapter.TryBindCompletedInventory(session, result, out detail);
            Detail = detail;
            return activated;
        }

        internal BoltMigrationSession ReadSession()
            => !IsDisposed && Environment.CurrentManagedThreadId == _thread ? _readSession() : null;

        private bool TryFind(BoltMigrationSession session, out PlayerMigrationAdapter adapter)
        {
            adapter = null;
            if (session?.Snapshot == null || !Current(session.Peer)) return false;
            var peer = session.Peer;
            if (!_adapters.TryGetValue((peer.RunId, peer.PeerId, session.Snapshot.Plan.OldNetworkId), out var entry)
                || !ReferenceEquals(peer, entry.Peer) || !entry.Adapter.SessionMatches(session)) return false;
            adapter = entry.Adapter;
            return true;
        }

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("Dispose the player factory on its owner thread.");
            if (IsDisposed) return;
            foreach (var entry in _adapters.Values)
                if (!entry.Adapter.IsCancelled) entry.Adapter.RetainUnconfirmed("Factory disposed; retained native custody was not released.");
            IsDisposed = true;
            Detail = "Factory disposed; cancellation and native lifetime teardown remain explicit, never inferred from disposal.";
        }

        private bool Current(ControlPeer peer) => !IsDisposed && Environment.CurrentManagedThreadId == _thread
            && peer != null && peer.IsCurrent;
        private static bool Range(float value, float minimum, float maximum)
            => float.IsFinite(value) && value >= minimum && value <= maximum;
        private sealed record Entry(ControlPeer Peer, BoltMigrationPlan Request,
            PlayerMigrationAdapter Adapter);
    }
}
