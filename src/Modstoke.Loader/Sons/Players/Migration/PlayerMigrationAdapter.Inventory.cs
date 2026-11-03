// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons
{
    public sealed partial class PlayerMigrationAdapter
    {
        private PlayerBridge _bridge;
        private PlayerInventoryAuthority _serverInventory;
        private InventoryConsumption _serverConsumption;
        private PlayerStatProjection _statProjection;
        private PlayerLifeRoute _lifeRoute;
        private PlayerArmourRoute _armourRoute;
        private PlayerHeldOnlyRoute _heldOnlyRoute;
        private bool _inventoryBindingAttempted, _inventoryBound;
        private PlayerClientMovement _sourceMovement;
        private bool _runtimePreparationAttempted, _runtimePrepared;
        private bool _runtimeActivationBusy, _runtimeActivated;
        private bool _sourceLifecycleReleaseAttempted, _sourceLifecycleReleased;

        public PlayerBridge Bridge => _bridge;
        public PlayerInventoryAuthority ServerInventory => _serverInventory;
        public PlayerInventoryEnforcement SourceInventory => _sourceInventory;
        public PlayerClientMovement SourceMovement => _sourceMovement;

        /// <summary>
        /// Bind the completed migration's inventory endpoints once. This neither resumes the
        /// player nor starts a motor. Native source custody stays held while its exact baseline
        /// handshake is pending. The observer never creates an input or inventory bridge.
        /// </summary>
        internal bool TryBindCompletedInventory(BoltMigrationSession session,
            BoltMigrationResult result, out string detail)
        {
            detail = "Inventory binding requires this session's exact completion result.";
            return session != null && ReferenceEquals(session.LastResult, result)
                && result.Status == BoltMigrationStatus.Completed
                && TryBindInventory(session, false, out detail);
        }

        private bool TryBindInventory(BoltMigrationSession session, bool prepared, out string detail)
        {
            if (!Enter(out detail)) return false;
            try
            {
                Require(SessionMatches(session) && session.PlayerBridgeLifetimeCurrent
                    && (prepared || session.Status == BoltMigrationStatus.Completed)
                    && session.NewNetworkId == _replacementId && ReplacementCurrent(_replacement),
                    "Inventory binding requires this adapter's exact repaired kernel session and live replacement.");
                Require(RetainExactSession(session), "The inventory belongs to a different retained kernel session.");
                Require(Role != BoltMigrationRole.Observer,
                    "Observers have no inventory custody or player input endpoint.");
                if (_inventoryBound)
                {
                    Require(_bridge != null && !_bridge.IsDisposed, "The bound inventory bridge is no longer current.");
                    detail = "Exact inventory endpoints already bound; no native operation was repeated.";
                    return true;
                }
                Require(!_inventoryBindingAttempted && _referencesRepaired,
                    "Inventory binding cannot replay a failed attempt or precede reference repair.");
                if (Role == BoltMigrationRole.Source)
                    Require(_rebound && _lifecycle.CallbacksRestored && _sourceInventory != null
                        && !_sourceInventory.IsDisposed && !_sourceInventory.IsFaulted
                        && _sourceEquipment != null && !_sourceEquipment.IsDisposed && !_sourceEquipment.IsFaulted,
                        "Retained source inventory and repaired callback ownership are required.");
                else
                    Require(_restored && _carrier != null && _approvedFingerprint == _snapshot.Inventory.Fingerprint
                        && ValidReason(_inventoryApprovalReason) && ValidReason(_physicsApprovalReason)
                        && ValidReason(_survivalApprovalReason)
                        && _approvedHeldFingerprint == _snapshot.HeldOnly.Fingerprint && ValidReason(_heldApprovalReason)
                        && _approvedArmourFingerprint == _snapshot.Armour.Fingerprint && ValidReason(_armourApprovalReason),
                        "Destination inventory requires exact prior baseline and separate physics and survival approval.");

                _inventoryBindingAttempted = true;
                bool bound = prepared
                    ? PlayerBridge.TryCreatePrepared(session, out _bridge, out detail)
                    : PlayerBridge.TryCreate(session, out _bridge, out detail);
                if (!bound)
                { _unconfirmed = true; return false; }
                _bridge.Retired += OnInventoryBridgeRetired;
                if (Role == BoltMigrationRole.Destination
                    && (!prepared || !_carrier.TryBindPrepared(_bridge, out detail)))
                { FailInventoryBinding(detail); return false; }
                if (Role == BoltMigrationRole.Source)
                {
                    if (!_sourceInventory.TryBindSource(_bridge, out detail)
                        || !_sourceEquipment.TryBindSource(_bridge, out detail)
                        || !_sourceHeldOnly.TryBind(_bridge, _sourceInventory, out detail))
                    { FailInventoryBinding(detail); return false; }
                }
                else if (!PlayerInventoryAuthority.TryCreateServer(_bridge, out _serverInventory, out detail)
                    || !_serverInventory.TryAcceptUnverifiedExistingInventory(_snapshot.Inventory,
                        _approvedFingerprint, _inventoryApprovalReason, out detail)
                    || !_serverInventory.TryEnableMutationEnforcement(out detail)
                    || !PlayerArmourAuthority.TryAcceptUnverifiedExisting(_bridge, _serverInventory,
                        _snapshot.Inventory, _snapshot.Armour, _approvedArmourFingerprint, _armourApprovalReason,
                        out _serverArmour, out detail)
                    || !PlayerHeldOnlyAuthority.TryAcceptUnverifiedExisting(_bridge, _snapshot.HeldOnly,
                        _approvedHeldFingerprint, _heldApprovalReason, out _serverHeldOnly, out detail)
                    || !_serverInventory.MutationEnforcement.TryBindHeldOnlyAuthority(_bridge, _serverHeldOnly, out detail))
                { FailInventoryBinding(detail); return false; }
                RequireContext();
                Require(_bridge.TryReadCurrentTarget(out var entity) && ReplacementCurrent(entity),
                    "Player replacement changed while inventory endpoints were bound.");
                _inventoryBound = true;
                detail = "Exact retained/server inventory endpoints bound; baseline handshake and gameplay activation are separate.";
                return true;
            }
            catch (Exception error)
            {
                detail = Failure("Player inventory binding rejected or unconfirmed", error);
                if (_inventoryBindingAttempted) FailInventoryBinding(detail);
                return false;
            }
            finally { Exit(detail); }
        }

        internal BoltMigrationAdmission ReadRuntimePreparation(
            BoltMigrationSession session, BoltMigrationBarrier barrier, out string detail)
        {
            detail = "Runtime preparation requires the exact repaired player session.";
            if (session == null || !session.Peer.IsCurrent || !RetainExactSession(session))
                return BoltMigrationAdmission.Rejected;
            if (Role == BoltMigrationRole.Observer)
                return TryPrepareRuntimeAnimation(out detail) ? BoltMigrationAdmission.Ready
                    : BoltMigrationAdmission.Rejected;
            if (!_inventoryBound && (!session.TryPreparePlayerBridge(barrier, out detail)
                || !TryBindInventory(session, true, out detail)))
                return BoltMigrationAdmission.Rejected;
            var inventory = ReadInventoryReadiness(out detail);
            if (inventory != BoltMigrationAdmission.Ready) return inventory;
            if (_runtimePrepared) return BoltMigrationAdmission.Ready;
            if (_runtimePreparationAttempted)
            { detail = "Runtime preparation previously failed; held endpoints cannot be recreated."; return BoltMigrationAdmission.Rejected; }
            _runtimePreparationAttempted = true;
            bool heldBound = Role == BoltMigrationRole.Destination
                ? PlayerHeldOnlyRoute.TryBindServer(_bridge, _serverInventory.MutationEnforcement,
                    _serverHeldOnly, _carrier, out _heldOnlyRoute, out detail)
                : PlayerHeldOnlyRoute.TryBindSource(_sourceHeldOnly, out _heldOnlyRoute, out detail);
            if (!heldBound)
            {
                FailInventoryBinding(detail);
                return BoltMigrationAdmission.Rejected;
            }
            if (Role == BoltMigrationRole.Destination)
            {
                if (!_snapshot.Survival.Water.TryValidateNativeMovement(_bridge, _carrier, _serverWater, out detail)
                    || !ApproveRestoredWater(_carrier, _snapshot.Survival, _snapshot.Survival.Water))
                {
                    FailInventoryBinding(detail);
                    return BoltMigrationAdmission.Rejected;
                }
                if (!_carrier.TryReadRestoredServerStats(out var stats))
                {
                    detail = "Prepared consumption requires this carrier's approved restored stats.";
                    FailInventoryBinding(detail);
                    return BoltMigrationAdmission.Rejected;
                }
                if (!_serverInventory.TryBindConsumptionDefinitions(out detail)
                    || !InventoryConsumption.TryPrepareServer(_bridge, _serverInventory,
                        stats, stats._vitals, out _serverConsumption, out detail))
                {
                    FailInventoryBinding(detail);
                    return BoltMigrationAdmission.Rejected;
                }
                if (!TryPrepareServerMovement(out detail)
                    || !PlayerWaterPresentation.TryPrepareServer(_bridge, _serverWater,
                        out _waterPresentation, out detail))
                {
                    FailInventoryBinding(detail);
                    return BoltMigrationAdmission.Rejected;
                }
                if (!PlayerStatProjection.TryPrepareServer(_bridge, _carrier, _serverConsumption,
                        out _statProjection, out detail)
                    || !PlayerLifeRoute.TryPrepareServer(_bridge, _serverLife, _statProjection,
                        out _lifeRoute, out detail)
                    || !_serverDynamics.TryBindSwimmingTransitions(_bridge, _serverLife, _lifeRoute,
                        _waterPresentation, out detail))
                {
                    FailInventoryBinding(detail);
                    return BoltMigrationAdmission.Rejected;
                }
            }
            if (Role == BoltMigrationRole.Source
                && (!PlayerWaterPresentation.TryPrepareSource(_bridge, _sourceWater,
                        out _waterPresentation, out detail)
                    || !PlayerClientMovement.TryPrepare(_bridge, _movementRig,
                        _factory.CorrectionLimits, SourceLocomotionCurrent, out _sourceMovement, out detail)
                    || !_sourceMovement.TryBindWater(_bridge, _waterPresentation, out detail)
                    || !_sourceMovement.TryEnableSwimming(out detail)))
            {
                FailInventoryBinding(detail);
                return BoltMigrationAdmission.Rejected;
            }
            if (Role == BoltMigrationRole.Source
                && !PlayerStatProjection.TryPrepareSource(_bridge, _sourceInventory, out _statProjection, out detail))
            {
                FailInventoryBinding(detail);
                return BoltMigrationAdmission.Rejected;
            }
            if (Role == BoltMigrationRole.Source
                && (!PlayerLifeRoute.TryPrepareSource(_bridge, _sourceInventory, _statProjection,
                        out _lifeRoute, out detail)
                    || !_sourceMovement.TryBindLife(_bridge, _lifeRoute.Source, out detail)
                    || !_sourceMovement.TryBindSwimmingTransitions(_bridge, _lifeRoute, out detail)))
            {
                FailInventoryBinding(detail);
                return BoltMigrationAdmission.Rejected;
            }
            bool armourPrepared = Role == BoltMigrationRole.Destination
                ? PlayerArmourRoute.TryPrepareServer(_bridge, _serverArmour, _serverInventory,
                    out _armourRoute, out detail)
                : PlayerArmourRoute.TryPrepareSource(_bridge, _sourceInventory, _sourceArmour,
                    out _armourRoute, out detail);
            if (!armourPrepared)
            {
                FailInventoryBinding(detail);
                return BoltMigrationAdmission.Rejected;
            }
            if (Role == BoltMigrationRole.Source
                && (!_sourceInventory.TryBindWaterActionOwner(_bridge, _waterPresentation)
                    || !_sourceMovement.TryArmSourceInputHold(_bridge, _waterPresentation, _lifecycle,
                        ReadSourceHoldAcceptedWork, out detail)))
            {
                FailInventoryBinding(detail);
                return BoltMigrationAdmission.Rejected;
            }
            if (!TryPrepareRuntimeAnimation(out detail))
                return BoltMigrationAdmission.Rejected;
            _runtimePrepared = true;
            detail = "Inventory and retained movement endpoints prepared; aggregate gameplay admission is still required.";
            return BoltMigrationAdmission.Ready;
        }

        private bool SourceLocomotionCurrent() => !_cancelled && !_unconfirmed && _peer.IsCurrent
            && _bridge != null && !_bridge.IsDisposed && _sourceInventory?.IsArmed == true
            && !_sourceInventory.IsDisposed && !_sourceInventory.IsFaulted
            && _sourceEquipment != null && !_sourceEquipment.IsDisposed && !_sourceEquipment.IsFaulted;

        private BoltMigrationAdmission ReadSourceHoldAcceptedWork(out string detail)
        {
            detail = "Source freshness hold requires the exact live inventory, equipment and life owners.";
            var life = _lifeRoute?.Source;
            if (Role != BoltMigrationRole.Source || !SourceLocomotionCurrent()
                || !_bridge.IsGameplayActive || _sourceHeldOnly == null || _sourceHeldOnly.IsDisposed
                || _sourceHeldOnly.FailureReason != null || !_sourceHeldOnly.IsNativeGuardHeld
                || _lifeRoute == null || _lifeRoute.IsDisposed || _lifeRoute.FailureReason != null
                || _armourRoute == null || _armourRoute.IsDisposed || _armourRoute.FailureReason != null
                || _waterPresentation == null || _waterPresentation.IsDisposed || _waterPresentation.FailureReason != null
                || life == null || life.IsDisposed || life.FailureReason != null)
                return BoltMigrationAdmission.Rejected;
            if (_armourRoute.HasPendingCustody || _waterPresentation.HasPendingSwimmingTransition
                || _waterPresentation.SwimmingNativeCallbackActive
                || _sourceInventory.HasPendingSwimmingStow || _sourceInventory.HasPendingCustody || _sourceInventory.HasPendingHeldOnlyCustody
                || _sourceInventory.HasPendingNativeCraftPresentation || _sourceEquipment.PendingCount != 0
                || _sourceHeldOnly.HasPendingCustody || _sourceHeldOnly.HasUnownedForcedCallback
                || life.HasPendingNativeWork || life.NativeCallbackActive)
            {
                detail = "Accepted inventory, held-item, equipment or life work must drain before the source body is held.";
                return BoltMigrationAdmission.Pending;
            }
            detail = "The adapter's accepted action owners are idle; native movement/action observation still owns its boundary.";
            return BoltMigrationAdmission.Ready;
        }

        private bool ApproveRestoredWater(PlayerCarrier carrier,
            PlayerSurvivalSnapshot survival, PlayerWaterConfiguration configuration)
            => ReferenceEquals(carrier, _carrier) && ReferenceEquals(survival, _snapshot.Survival)
                && ReferenceEquals(configuration, survival.Water)
                && Role == BoltMigrationRole.Destination && AnimationLifetimeCurrent()
                && _factory.ApproveWater(_peer, _snapshot.Plan, survival, configuration)
                && AnimationLifetimeCurrent();

        internal BoltMigrationAdmission ReadRuntimeActivation(
            BoltMigrationSession session, out string detail)
        {
            detail = "Runtime activation requires the exact completed player session.";
            if (_runtimeActivationBusy || Environment.CurrentManagedThreadId != _thread
                || session == null || !ReferenceEquals(ReadExactSession(), session)
                || session.Status != BoltMigrationStatus.Completed
                || !AnimationLifetimeCurrent()) return BoltMigrationAdmission.Rejected;
            if (UnityEngine.Time.inFixedTimeStep) return BoltMigrationAdmission.Pending;
            if (Role == BoltMigrationRole.Observer)
                return TryActivateRuntimeAnimation(out detail) ? BoltMigrationAdmission.Ready
                    : BoltMigrationAdmission.Rejected;
            _runtimeActivationBusy = true;
            try
            {
                Require(_runtimePrepared && _inventoryBound && _bridge != null
                    && _lifeRoute != null && _statProjection != null && _waterPresentation != null
                    && _waterPresentation.SwimmingTransitionsPrepared
                    && _armourRoute != null && !_armourRoute.IsDisposed && _armourRoute.FailureReason == null,
                    "All concrete player runtime owners must be prepared before activation.");
                if (_runtimeActivated)
                {
                    Require(_bridge.IsGameplayActive && _waterPresentation.IsActive
                        && (Role == BoltMigrationRole.Destination
                            ? _serverMovement?.IsActive == true && _serverWater?.IsActive == true
                                && _serverSurvival?.IsActive == true
                            : _sourceMovement?.IsActive == true),
                        "The active player runtime lost one of its exact owners.");
                    Require(TryActivateRuntimeAnimation(out detail), detail);
                    return BoltMigrationAdmission.Ready;
                }
                Require(_bridge.TryActivateCompleted(out detail), detail);
                if (Role == BoltMigrationRole.Destination)
                {
                    Require(TryActivateServerMovement(out detail), detail);
                    Require(_waterPresentation.TryActivate(out detail), detail);
                    Require(_carrier.TryResumePresentation(_bridge, out detail), detail);
                }
                else
                {
                    if (!_sourceLifecycleReleased)
                    {
                        Require(!_sourceLifecycleReleaseAttempted && _lifecycle?.IsPaused == true
                            && _sourceMovement?.HasSourceInputHold == true,
                            "The original player must retain its initial pause and prepared live hold until the first water observation.");
                        if (!_waterPresentation.TryActivate(out detail))
                        {
                            Require(_waterPresentation.FailureReason == null && !_waterPresentation.IsDisposed,
                                _waterPresentation.FailureReason ?? detail);
                            return BoltMigrationAdmission.Pending;
                        }
                        // Release once, then hand ownership to the prepared live hold on
                        // this same stack, before any native consumer can run.
                        _sourceLifecycleReleaseAttempted = true;
                        var released = _lifecycle.TryResumeRebound(_replacement, _replacementId, out detail);
                        Require(released is BoltMigrationMutationResult.Applied
                            or BoltMigrationMutationResult.Unchanged, detail);
                        Require(_sourceMovement.TryAcknowledgeSourceLifecycleRelease(_lifecycle, released, out detail), detail);
                        _sourceLifecycleReleased = true;
                    }
                    var held = _sourceMovement.PumpSourceInputHold(out detail);
                    if (held == BoltMigrationAdmission.Pending) return held;
                    Require(held == BoltMigrationAdmission.Ready, detail);
                    Require(_sourceMovement.TryActivate(out detail), detail);
                }
                Require(TryActivateRuntimeAnimation(out detail), detail);
                _runtimeActivated = true;
                detail = "Prepared player runtime activated after its exact water observation and owned writer release.";
                return BoltMigrationAdmission.Ready;
            }
            catch (Exception error)
            {
                detail = Failure("Completed player runtime activation rejected or unconfirmed", error);
                FailInventoryBinding(detail);
                return BoltMigrationAdmission.Rejected;
            }
            finally { _runtimeActivationBusy = false; }
        }

        public BoltMigrationAdmission ReadInventoryReadiness(out string detail)
        {
            if (!Enter(out detail)) return BoltMigrationAdmission.Rejected;
            try
            {
                var endpoint = Role == BoltMigrationRole.Source ? _sourceInventory : _serverInventory?.MutationEnforcement;
                if (!_inventoryBound || endpoint == null)
                { detail = "The exact completed inventory endpoints have not been bound."; return BoltMigrationAdmission.Pending; }
                if (_bridge.IsDisposed || endpoint.IsDisposed || endpoint.IsFaulted || _serverInventory?.IsFaulted == true)
                { detail = endpoint.Detail; return BoltMigrationAdmission.Rejected; }
                if (!endpoint.IsArmed)
                { detail = endpoint.Detail; return BoltMigrationAdmission.Pending; }
                detail = "Exact baseline handshake armed; this is inventory endpoint readiness, not complete gameplay coverage.";
                return BoltMigrationAdmission.Ready;
            }
            catch (Exception error) { detail = Failure("Inventory readiness rejected", error); return BoltMigrationAdmission.Rejected; }
            finally { Exit(detail); }
        }

        private void FailInventoryBinding(string detail)
        {
            _unconfirmed = true;
            Detail = detail;
            // Endpoint disposal keeps retained native guards in force. It must never stand in
            // for pre-retirement cancellation, or permit a new bridge with reset sequences.
            _bridge?.Dispose();
        }

        private void OnInventoryBridgeRetired()
        {
            _inventoryBound = false;
            _runtimePrepared = false;
            _runtimeActivated = false;
            _unconfirmed = true;
            _armourRoute?.Dispose();
            _lifeRoute?.Dispose();
            _heldOnlyRoute?.Dispose();
            _statProjection?.Dispose();
            RetireRuntimeAnimation();
            _sourceMovement?.Dispose();
            _waterPresentation?.Dispose();
            RetireServerMovement();
            _sourceEquipment?.Dispose();
            _sourceHeldOnly?.Dispose();
            _sourceInventory?.Dispose();
            _serverConsumption?.Dispose();
            _serverHeldOnly?.Dispose();
            _serverArmour?.Dispose();
            _serverInventory?.Dispose();
            Detail = "Player bridge retired; native source custody and lifecycle holds were not released.";
        }
    }
}
