// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Bolt;
using Il2CppInterop.Runtime.InteropTypes;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// One exact player transaction. Composes the retained rig, native lifecycle, inventory
    /// custody and canonical snapshot without replacing input or cameras. Missing gameplay
    /// domains remain admission failures, not policy-overridable coverage declarations.
    /// </summary>
    public sealed partial class PlayerMigrationAdapter : IBoltPlayerMigrationAdapter,
        IBoltMigrationPlanParticipant, IBoltMigrationAdmission,
        IBoltMigrationReplicaLifecycle, IBoltMigrationReplacementAdmission,
        IBoltMigrationReplacementIdentity
    {
        public static Guid PlayerContractId { get; } = new("d9c985b3-fafe-47a7-bf73-91648b94711d");
        public const uint PlayerContractVersion = 9;
        public Guid ContractId => PlayerContractId;
        public uint ContractVersion => PlayerContractVersion;
        public BoltMigrationRole Role { get; }
        public BoltMigrationPlan Plan => _plan;
        public bool IsUnconfirmed => _unconfirmed;
        public bool IsCancelled => _cancelled;
        public string Detail { get; private set; } = "Player transaction has not been prepared.";
        public PlayerMigrationSnapshot CapturedSnapshot => _snapshot;
        public string ApprovedInventoryFingerprint => _approvedFingerprint;
        public string InventoryApprovalReason => _inventoryApprovalReason;
        public string ApprovedHeldOnlyFingerprint => _approvedHeldFingerprint;
        public string HeldOnlyApprovalReason => _heldApprovalReason;
        public string ApprovedArmourFingerprint => _approvedArmourFingerprint;
        public string ArmourApprovalReason => _armourApprovalReason;
        public string PhysicsApprovalReason => _physicsApprovalReason;
        public string SurvivalApprovalReason => _survivalApprovalReason;
        public PlayerClientMovement.RetainedRig RetainedMovementRig => _movementRig;

        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerMigrationAdapterFactory _factory;
        private readonly ControlPeer _peer;
        private readonly BoltMigrationPlan _request;
        private BoltMigrationPlan _plan;
        private BoltEntity _original, _replacement;
        private Bolt.Entity _originalAttachment;
        private int _originalInstance;
        private ulong _replacementId;
        private bool _busy, _reentered, _quiesceAttempted, _held, _rebindAttempted;
        private bool _rebound, _cancelled, _unconfirmed, _sourceInventoryCancelled;
        private bool _sourceProtectionAttempted;
        private PlayerLifecycle _lifecycle;
        private PlayerRigBindings _rig;
        private PlayerClientMovement.RetainedRig _movementRig;
        private PlayerCarrierProfile _profile;
        private PlayerInventoryEnforcement _sourceInventory;
        private PlayerInventoryEnforcement.HeldOnlySource _sourceHeldOnly;
        private PlayerHeldOnlyAuthority _serverHeldOnly;
        private PlayerArmourAuthority.Snapshot _sourceArmour;
        private PlayerArmourAuthority _serverArmour;
        private PlayerEquipment _sourceEquipment;
        private PlayerMigrationSnapshot _snapshot;
        private IBoltMigrationReferenceRepair _references;
        private string _approvedFingerprint, _inventoryApprovalReason, _physicsApprovalReason, _survivalApprovalReason;
        private string _approvedHeldFingerprint, _heldApprovalReason;
        private string _approvedArmourFingerprint, _armourApprovalReason;

        internal PlayerMigrationAdapter(PlayerMigrationAdapterFactory factory,
            BoltMigrationRole role, ControlPeer peer,
            BoltMigrationPlan request)
        { _factory = factory; Role = role; _peer = peer; _request = request; }

        internal BoltMigrationAdmission BeginSourceAdmissionProtection(BoltEntity original,
            ControlPeer peer, BoltMigrationPlan request, out string detail)
        {
            if (!Enter(out detail)) return BoltMigrationAdmission.Rejected;
            try
            {
                Require(Role == BoltMigrationRole.Source && ReferenceEquals(peer, _peer)
                    && request == _request && !_quiesceAttempted, "Protection belongs to the exact authenticated source request.");
                if (!CaptureOriginal(original, out detail) || !PlayerEventRouting.IsProtectionSelected)
                    return BoltMigrationAdmission.Rejected;
                _sourceProtectionAttempted = true;
                if (!_lifecycle.TryBeginActionFence(out detail)) return BoltMigrationAdmission.Pending;
                RequireContext();
                return BoltMigrationAdmission.Ready;
            }
            catch (Exception error)
            { detail = Failure("Source protection acquisition rejected", error); return BoltMigrationAdmission.Rejected; }
            finally { Exit(detail); }
        }

        internal bool TryCancelAdmissionProtection(out string detail)
        {
            detail = "No source protection was acquired.";
            return !_sourceProtectionAttempted || CancelSource(_original, out detail);
        }

        internal bool ProtectionReleasePending => Role == BoltMigrationRole.Source
            && _sourceProtectionAttempted && _lifecycle != null && !_lifecycle.CanReleaseProtection;

        public BoltMigrationAdmission ReadAdmission(BoltEntity original,
            BoltMigrationRole role, BoltMigrationSnapshot snapshot, out string detail)
        {
            detail = "Player admission requires its exact role and game thread.";
            if (!Enter(out detail)) return BoltMigrationAdmission.Rejected;
            try
            {
                Require(role == Role && !_quiesceAttempted, "Player admission phase or role differs.");
                if (!CaptureOriginal(original, out detail)) return BoltMigrationAdmission.Rejected;
                if (snapshot != null && !ReadSnapshot(snapshot, out detail)) return BoltMigrationAdmission.Rejected;
                if (Role == BoltMigrationRole.Destination && _snapshot != null)
                {
                    var approval = ReadApprovals(out detail);
                    if (approval != BoltMigrationAdmission.Ready) return approval;
                }
                return ReadParticipantBoundary(out detail);
            }
            catch (Exception error) { detail = Failure("Player admission rejected", error); return BoltMigrationAdmission.Rejected; }
            finally { Exit(detail); }
        }

        public bool CanMigrate(BoltEntity entity, out string detail)
        {
            detail = "Player preflight requires the exact original.";
            if (!Enter(out detail)) return false;
            try
            {
                if (Role == BoltMigrationRole.Destination && _held && _carrier != null
                    && !_restoreAttempted && ReplacementCurrent(entity))
                {
                    detail = "The exact destination replacement lost its replica writer hold.";
                    return _replacementReplica?.Held() == true
                        && _replacementReplica.ReadBoundary(out detail) == BoltMigrationAdmission.Ready
                        && _carrier.ReadReadiness(out detail) == BoltMigrationAdmission.Ready;
                }
                if (!CaptureOriginal(entity, out detail)) return false;
                return ReadParticipantBoundary(out detail) == BoltMigrationAdmission.Ready;
            }
            catch (Exception error) { detail = Failure("Player preflight rejected", error); return false; }
            finally { Exit(detail); }
        }

        public bool TryValidatePlayerLifecycle(BoltEntity entity, BoltMigrationRole role, out string detail)
        {
            if (role != Role) { detail = "The adapter belongs to a different participant role."; return false; }
            return CanMigrate(entity, out detail);
        }

        private bool CaptureOriginal(BoltEntity original, out string detail)
        {
            detail = "The exact current PlayerNetwork attachment is required.";
            if (!Alive(original) || !original.isAttached || original.networkId.PackedValue != _request.OldNetworkId
                || original.prefabId.Value != _request.PrefabId || original.prefabId != BoltPrefabs.PlayerNetwork
                || !Same(BoltNetwork.FindEntity(new NetworkId(_request.OldNetworkId)), original)
                || original._entity?.Serializer?.TryCast<NetworkState>()?.Meta?.TypeId.Value != _request.StateTypeId)
                return false;
            if (_original != null)
                return Same(_original, original) && OriginalCurrent();
            if (Role == BoltMigrationRole.Source)
            {
                if (!original.isOwner || !Same(original, LocalPlayer.Entity) || _peer.ServerSession != null
                    || !Same(original.gameObject, LocalPlayer.GameObject)) return false;
                if (!PlayerLifecycle.TryCapture(original, out _lifecycle, out detail)
                    || !PlayerRigBindings.TryCapture(original, out _rig, out detail)
                    || !PlayerClientMovement.TryCaptureRig(_rig, out _movementRig, out detail)
                    || !TryPrepareSourceEnvironment(out detail)) return false;
            }
            else if (original.isOwner || Same(original, LocalPlayer.Entity)
                || (Role == BoltMigrationRole.Destination) != (_peer.ServerSession != null)) return false;
            _original = original;
            _originalAttachment = original._entity;
            _originalInstance = original.GetInstanceID();
            detail = "Exact player attachment and retained source identities captured without native writes.";
            return OriginalCurrent();
        }

        public bool TryBeginPreparation(BoltMigrationPlan plan, BoltMigrationRole role,
            ControlPeer peer, out string detail)
        {
            detail = "Preparation requires the captured player and an unused exact plan.";
            if (!Enter(out detail)) return false;
            try
            {
                Require(!_plan.IsValid && !_quiesceAttempted && role == Role && ReferenceEquals(peer, _peer)
                    && MatchesRequest(plan) && OriginalCurrent() && _references != null, detail);
                if (_snapshot != null) Require(_snapshot.Plan == plan, "The prepared plan differs from its admitted snapshot.");
                _plan = plan;
                RequireContext();
                detail = "Exact player plan recorded; native state remains unchanged.";
                return true;
            }
            catch (Exception error) { detail = Failure("Player preparation rejected", error); return false; }
            finally { Exit(detail); }
        }

        public bool TryQuiesce(BoltEntity entity, out string detail)
        {
            detail = "Source quiescence requires the exact prepared player.";
            if (!Enter(out detail)) return false;
            try
            {
                Require(Role == BoltMigrationRole.Source && _plan.IsValid && !_quiesceAttempted
                    && Same(entity, _original) && OriginalCurrent(), detail);
                if (ReadParticipantBoundary(out detail) != BoltMigrationAdmission.Ready) return false;
                // Capture resume physics before the lifecycle introduces temporary freeze values.
                if (!PlayerCarrierProfile.CaptureProfile(entity, out _profile, out detail)) return false;
                _quiesceAttempted = true;
                if (!_lifecycle.TryPause(out detail)
                    || !PlayerWaterContactAuthority.TryHoldSource(entity, _lifecycle,
                        global::Modstoke.Loader.HarmonySupport.CreateState(), out _sourceWater, out detail)
                    || !_lifecycle.TryCaptureHeldOnlySource(_plan, LocalPlayer.Inventory, out _sourceHeldOnly, out detail)
                    || !PlayerInventoryEnforcement.TryCaptureSource(_plan, out _sourceInventory, out detail)
                    || !PlayerEquipment.TryCaptureSource(_plan, out _sourceEquipment, out detail)) return false;
                RequireContext();
                Require(_lifecycle.IsPaused && OriginalCurrent() && !_sourceInventory.IsFaulted
                    && !_sourceEquipment.IsFaulted && _sourceEquipment.PendingCount == 0,
                    "The retained domains changed during pause acquisition.");
                _held = true;
                detail = "Original rig and inventory held; input objects and camera identities retained.";
                return true;
            }
            catch (Exception error) { detail = Failure("Player pause rejected or partial", error); return false; }
            finally { Exit(detail); }
        }

        public bool TryCapture(BoltEntity entity, out byte[] payload, out string detail)
        {
            payload = null;
            detail = "Player capture requires all retained source domains to remain held.";
            if (!Enter(out detail)) return false;
            try
            {
                Require(Role == BoltMigrationRole.Source && Same(entity, _original) && SourceHeld(), detail);
                if (!_sourceInventory.Baseline.TryValidateLocal(out detail)
                    || _sourceHeldOnly == null
                    || !_sourceHeldOnly.Baseline.TryValidateSource(_lifecycle, LocalPlayer.Inventory, out detail)
                    || !TryCaptureSourceArmour(out detail)
                    || !PlayerSurvivalSnapshot.TryCapture(entity, _lifecycle, out var survival, out detail)
                    || !PlayerLifeAdmission.TryCapture(entity, _lifecycle, survival, out var life, out detail)
                    || !survival.TryBindLifeAdmission(life)
                    || !TryCaptureSourceEnvironment(survival, out detail)
                    || !PlayerPreservation.TryCaptureSupportedState(_peer, entity, out var state, out detail)
                    || !PlayerPreservation.TryCaptureSettledAnimationState(_peer, entity, _lifecycle,
                        out var animation, out detail)
                    || !PlayerMigrationSnapshot.TryCreate(_plan, state, animation, _sourceInventory.Baseline,
                        _profile, survival, _sourceHeldOnly.Baseline, _sourceArmour, out var captured, out detail)) return false;
                var bytes = captured.Export();
                RequireContext();
                Require(SourceHeld() && SourceEnvironmentHeld(out detail), "Retained source changed during snapshot capture: " + detail);
                Require(_sourceArmour.TryValidateSource(_lifecycle, _sourceInventory.Baseline, out detail), detail);
                if (_snapshot != null)
                    Require(bytes.AsSpan().SequenceEqual(_snapshot.Encoded), "The held canonical player payload changed.");
                _snapshot = captured;
                payload = bytes;
                detail = "Canonical player, animation, inventory and pre-pause physics captured under one hold.";
                return true;
            }
            catch (Exception error) { detail = Failure("Player snapshot capture rejected", error); return false; }
            finally { Exit(detail); }
        }

        public bool TryValidateSnapshot(BoltMigrationSnapshot snapshot, out string detail)
        {
            if (!Enter(out detail)) return false;
            try
            {
                if (!ReadSnapshot(snapshot, out detail)) return false;
                if (Role == BoltMigrationRole.Source)
                    return SourceHeld() && _sourceInventory.Baseline.TryValidateLocal(out detail)
                        && _sourceArmour != null
                        && _sourceArmour.TryValidateSource(_lifecycle, _sourceInventory.Baseline, out detail)
                        && SourceEnvironmentHeld(out detail);
                return Role != BoltMigrationRole.Destination
                    || ReadApprovals(out detail) == BoltMigrationAdmission.Ready;
            }
            catch (Exception error) { detail = Failure("Player snapshot validation rejected", error); return false; }
            finally { Exit(detail); }
        }

        private bool ReadSnapshot(BoltMigrationSnapshot snapshot, out string detail)
        {
            detail = "Snapshot does not match this player's contract and transaction.";
            if (snapshot == null || !MatchesRequest(snapshot.Plan) || (_plan.IsValid && snapshot.Plan != _plan))
                return false;
            if (_snapshot != null)
            {
                bool unchanged = snapshot.Plan == _snapshot.Plan && snapshot.PayloadSpan.SequenceEqual(_snapshot.Encoded);
                detail = unchanged ? "Exact canonical player snapshot retained."
                    : "The exact canonical player snapshot changed after admission.";
                return unchanged;
            }
            if (!PlayerMigrationSnapshot.TryRead(snapshot, out var decoded, out detail)) return false;
            _snapshot = decoded;
            detail = "Correlated canonical player snapshot validated; approval and native coverage are separate.";
            return true;
        }

        private BoltMigrationAdmission ReadApprovals(out string detail)
        {
            detail = "Only the exact destination may approve this received player snapshot.";
            if (Role != BoltMigrationRole.Destination || _snapshot == null)
                return BoltMigrationAdmission.Rejected;
            if (_approvedFingerprint == null)
            {
                var decision = _factory.ApproveInventory(_peer, _snapshot.Inventory,
                    out var fingerprint, out var reason, out detail);
                RequireContext();
                if (decision != BoltMigrationAdmission.Ready) return decision;
                Require(fingerprint == _snapshot.Inventory.Fingerprint && ValidReason(reason),
                    "Inventory policy did not approve this exact baseline with an explicit reason.");
                _approvedFingerprint = fingerprint;
                _inventoryApprovalReason = reason;
            }
            if (_physicsApprovalReason == null)
            {
                var decision = _factory.ApprovePhysics(_peer, _snapshot.Plan, _snapshot.CarrierProfile,
                    out var reason, out detail);
                RequireContext();
                if (decision != BoltMigrationAdmission.Ready) return decision;
                Require(ValidReason(reason), "Physics policy did not independently approve the exact carrier profile.");
                _physicsApprovalReason = reason;
            }
            if (_approvedHeldFingerprint == null)
            {
                var decision = _factory.ApproveHeldOnly(_peer, _snapshot.HeldOnly,
                    out var fingerprint, out var reason, out detail);
                RequireContext();
                if (decision != BoltMigrationAdmission.Ready) return decision;
                Require(fingerprint == _snapshot.HeldOnly.Fingerprint && ValidReason(reason),
                    "Held-only policy did not approve this exact carried inventory with an explicit reason.");
                _approvedHeldFingerprint = fingerprint;
                _heldApprovalReason = reason;
            }
            if (_survivalApprovalReason == null)
            {
                var decision = _factory.ApproveSurvival(_peer, _snapshot.Plan, _snapshot.Survival,
                    out var reason, out detail);
                RequireContext();
                if (decision != BoltMigrationAdmission.Ready) return decision;
                Require(ValidReason(reason), "Survival policy did not approve the exact canonical initial stats.");
                _survivalApprovalReason = reason;
            }
            if (_approvedArmourFingerprint == null)
            {
                var decision = _factory.ApproveArmour(_peer, _snapshot.Armour,
                    out var fingerprint, out var reason, out detail);
                RequireContext();
                if (decision != BoltMigrationAdmission.Ready) return decision;
                Require(fingerprint == _snapshot.Armour.Fingerprint && ValidReason(reason),
                    "Armour policy did not independently approve this exact worn-state observation.");
                _approvedArmourFingerprint = fingerprint;
                _armourApprovalReason = reason;
            }
            detail = "Inventory, carried items, armour, physics and survival observations independently approved for this exact immutable snapshot.";
            return BoltMigrationAdmission.Ready;
        }

        private bool TryCaptureSourceArmour(out string detail)
            => _sourceArmour != null
                ? _sourceArmour.TryValidateSource(_lifecycle, _sourceInventory.Baseline, out detail)
                : PlayerArmourAuthority.Snapshot.TryCaptureSource(_plan, _lifecycle,
                    LocalPlayer.Stats, _sourceInventory.Baseline, out _sourceArmour, out detail);

        private bool CancelSource(BoltEntity original, out string detail)
        {
            detail = "Cancellation requires the exact intact original, before any retirement or repair.";
            if (!Enter(out detail, allowUnconfirmed: true)) return false;
            try
            {
                Require(Role == BoltMigrationRole.Source && !_rebindAttempted && _bridge == null
                    && Same(original, _original) && OriginalCurrent(), detail);
                if (!_lifecycle.CanReleaseProtection)
                { detail = "Wait for retained native callbacks before cancelling source protection."; return false; }
                // Detach our observers before native ingress can resume and publish new changes.
                _sourceEquipment?.Dispose();
                Require(_sourceEquipment == null || _sourceEquipment.SubscriptionsDetached,
                    "Source equipment observers did not detach; leave the lifecycle held.");
                if (_sourceInventory != null && !_sourceInventoryCancelled)
                {
                    if (!_sourceInventory.TryCancelBeforeRetirement(out detail)) return false;
                    _sourceInventoryCancelled = true;
                }
                if (_sourceHeldOnly != null && !_sourceHeldOnly.IsDisposed
                    && !_sourceHeldOnly.TryCancelBeforeRetirement(_lifecycle, out detail)) return false;
                if (!TryReleaseCancelledEnvironment(out detail)) return false;
                RequireContext();
                var restored = _quiesceAttempted || _sourceProtectionAttempted ? _lifecycle.TryResumeOriginal(out detail)
                    : _lifecycle.TryCancelCapture(out detail);
                if (!Successful(restored))
                {
                    if (restored == BoltMigrationMutationResult.Unconfirmed) _unconfirmed = true;
                    return false;
                }
                _held = false;
                RequireContext();
                _cancelled = true;
                detail = "Exact original inventory, callbacks and native lifecycle restored without retiring its rig.";
                return true;
            }
            catch (Exception error) { _unconfirmed = true; detail = Failure("Source cancellation unconfirmed", error); return false; }
            finally { Exit(detail); }
        }

        private BoltMigrationAdmission ReadParticipantBoundary(out string detail)
        {
            detail = "Read participant readiness against the exact captured player.";
            if (!OriginalCurrent()) return BoltMigrationAdmission.Rejected;
            if (!_replicaHooksInstalled || Environment.CurrentManagedThreadId != _replicaThread)
            { detail = "Exact replica writer hooks must be installed before any participant can retire."; return BoltMigrationAdmission.Rejected; }
            if (Role != BoltMigrationRole.Source)
                return ReadOriginalReplicaBoundary(out detail);
            if (_sourceEnvironmentSetupFrame < 0 || Time.frameCount <= _sourceEnvironmentSetupFrame)
            { detail = "Waiting for a complete native frame after source environment hooks were installed."; return BoltMigrationAdmission.Pending; }
            var admission = _lifecycle.ReadAdmission(out detail);
            if (admission != BoltMigrationAdmission.Ready) return admission;
            if (_movementRig == null || !_movementRig.GroundMode())
            { detail = "The retained sampler cannot start in this player's current native movement mode."; return BoltMigrationAdmission.Rejected; }
            if (!PlayerCarrierProfile.CaptureProfile(_original, out _, out detail)
                || !_lifecycle.CanFreezeHeldOnlyWriters(LocalPlayer.Inventory, out detail))
                return BoltMigrationAdmission.Rejected;
            RequireContext();
            // Complete inventory capture follows the reversible pause, after acquiring
            // the separate held-item guard. No source retirement is possible before it.
            detail = "Exact source action boundary, sampler mode and physics profile admitted for reversible inventory capture.";
            return BoltMigrationAdmission.Ready;
        }

        private bool SourceHeld() => _held && OriginalCurrent() && _lifecycle?.IsPaused == true
            && _sourceWater != null && !_sourceWaterReleased
            && _sourceHeldOnly != null && !_sourceHeldOnly.IsDisposed && _sourceHeldOnly.FailureReason == null
            && _sourceHeldOnly.IsNativeGuardHeld && !_sourceHeldOnly.HasPendingCustody
            && _sourceInventory != null && !_sourceInventory.IsDisposed && !_sourceInventory.IsFaulted
            && _sourceEquipment != null && !_sourceEquipment.IsDisposed && !_sourceEquipment.IsFaulted
            && _sourceEquipment.PendingCount == 0;

        private bool OriginalCurrent() => Alive(_original) && _original.GetInstanceID() == _originalInstance
            && _original.isAttached && _original.networkId.PackedValue == _request.OldNetworkId
            && Live(_originalAttachment) && _originalAttachment.IsAttached
            && Same(_original._entity, _originalAttachment)
            && Same(BoltNetwork.FindEntity(new NetworkId(_request.OldNetworkId)), _original);

        private bool MatchesRequest(BoltMigrationPlan plan) => plan.IsValid
            && plan.ContractId == ContractId && plan.ContractVersion == ContractVersion
            && plan.OldNetworkId == _request.OldNetworkId && plan.Generation == _request.Generation
            && plan.PrefabId == _request.PrefabId && plan.StateTypeId == _request.StateTypeId
            && (Role != BoltMigrationRole.Observer || plan.Id == _request.Id);

        private bool Enter(out string detail, bool allowUnconfirmed = false)
        {
            detail = "Player adapter requires its exact live peer on the selected game thread.";
            if (Environment.CurrentManagedThreadId != _thread || !PlayerRoles.IsAvailable
                || _factory.IsDisposed || !_peer.IsCurrent || _cancelled || (!allowUnconfirmed && _unconfirmed)) return false;
            if (_busy) { _reentered = true; detail = "Player adapter cannot re-enter a transaction callback."; return false; }
            _busy = true;
            _reentered = false;
            return true;
        }

        private void RequireContext() => Require(!_reentered && !_factory.IsDisposed && _peer.IsCurrent
            && Environment.CurrentManagedThreadId == _thread && PlayerRoles.IsAvailable,
            "Player peer, thread or transaction callback changed.");

        private void Exit(string detail) { Detail = detail; _busy = false; }
        private static bool ValidReason(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512;
        private static bool Successful(BoltMigrationMutationResult result)
            => result is BoltMigrationMutationResult.Applied or BoltMigrationMutationResult.Unchanged;
        private static bool Live(Il2CppObjectBase value) => value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero;
        private static bool Alive(UnityEngine.Object value) => Live(value) && value;
        private static bool Same(Il2CppObjectBase left, Il2CppObjectBase right) => Live(left) && Live(right) && left.Pointer == right.Pointer;
        private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
        private static string Failure(string prefix, Exception error) => prefix + ": " + error.Message;
    }
}
