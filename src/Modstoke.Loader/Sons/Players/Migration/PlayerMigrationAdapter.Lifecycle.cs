// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Bolt;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerMigrationAdapter
    {
        private PlayerCarrier _carrier;
        private bool _referencesRepaired, _retirementObserved, _restoreAttempted, _restored;
        private bool _controlAssigned;
        private Animator _restoredAnimator;
        private float _restoredAnimatorSpeed;
        private bool _animatorHeld;
        public PlayerCarrier Carrier => _carrier;
        public float? HeldAnimatorResumeSpeed => _animatorHeld ? _restoredAnimatorSpeed : null;

        public bool TryPrepareReferences(ulong oldNetworkId,
            out IBoltMigrationReferenceRepair references, out string detail)
        {
            references = null;
            if (!Enter(out detail)) return false;
            try
            {
                Require(oldNetworkId == _request.OldNetworkId && OriginalCurrent()
                    && !_quiesceAttempted && _references == null, "Player references must be captured once before quiescence.");
                // A raw NetworkId match is not proof that the field is a player reference.
                // Unknown Entity references are also refused until their owner is integrated.
                if (!BoltMigrationReferences.TryCaptureLoadedReferences(oldNetworkId,
                        (_, _, _) => false, out var bolt, out detail)
                    || !PlayerMigrationReferences.TryCapture(_original, bolt, out var caches, out detail))
                    return false;
                Require(OriginalCurrent(), "Player identity changed during reference capture.");
                _references = references = new ReferenceRepair(this, caches);
                detail = "Loaded Bolt references and surviving player caches captured; unknown reference owners reject.";
                return true;
            }
            catch (Exception error) { detail = Failure("Player reference capture rejected", error); return false; }
            finally { Exit(detail); }
        }

        private sealed class ReferenceRepair : IBoltMigrationReferenceRepair
        {
            private readonly PlayerMigrationAdapter _owner;
            private readonly IBoltMigrationReferenceRepair _caches;
            private bool _attempted;
            private BoltMigrationMutationResult _result;
            private string _detail;

            internal ReferenceRepair(PlayerMigrationAdapter owner, IBoltMigrationReferenceRepair caches)
            { _owner = owner; _caches = caches; }

            public BoltMigrationMutationResult TryRepair(BoltEntity replacement, out string detail)
            {
                if (!_owner.Enter(out detail)) return BoltMigrationMutationResult.Rejected;
                try
                {
                    Require(_owner.ReplacementCurrent(replacement), "Reference repair requires the authenticated exact replacement.");
                    if (_attempted) { detail = _detail; return _result; }
                    _attempted = true;
                    _result = BoltMigrationMutationResult.Rejected;
                    if (_owner.Role == BoltMigrationRole.Source)
                    {
                        var session = _owner.ReadExactSession();
                        Require(_owner.SessionMatches(session), "The exact kernel source session is unavailable for retirement proof.");
                        if (!_owner._lifecycle.TryObserveRetirement(session, out detail)) return Finish(_result, detail);
                        _owner._retirementObserved = true;
                        Require(_owner._sourceEnvironmentSealed && _owner._sourceWater != null,
                            "Source environment handoff was not sealed before retirement.");
                        if (!_owner._sourceWater.TryRetireSource(session, out detail))
                        { _owner._unconfirmed = true; return Finish(BoltMigrationMutationResult.Unconfirmed, detail); }
                    }
                    var result = _caches.TryRepair(replacement, out detail);
                    if (Successful(result)) _owner._referencesRepaired = true;
                    else _owner._unconfirmed = true;
                    return Finish(result, detail);
                }
                catch (Exception error)
                {
                    _owner._unconfirmed = true;
                    detail = Failure("Player reference repair unconfirmed", error);
                    return Finish(BoltMigrationMutationResult.Unconfirmed, detail);
                }
                finally { _owner.Exit(detail); }
            }

            private BoltMigrationMutationResult Finish(BoltMigrationMutationResult result, string detail)
            { _result = result; _detail = detail; return result; }
        }

        public bool TryAuthorizeReplacementIdentity(ulong networkId, out string detail)
        {
            if (!Enter(out detail)) return false;
            try
            {
                Require(_held && _plan.IsValid && networkId != 0 && networkId != _plan.OldNetworkId
                    && (_replacementId == 0 || _replacementId == networkId), "Replacement identity conflicts with the held player transaction.");
                _replacementId = networkId;
                ExpectReplacementReplica();
                detail = "Exact replacement ID registered for native writer custody; early receipt does not prove retirement or control.";
                return true;
            }
            catch (Exception error)
            { _unconfirmed |= _replacementId != 0; detail = Failure("Player replacement identity rejected or unconfirmed", error); return false; }
            finally { Exit(detail); }
        }

        public bool TryQuiesceReplacement(BoltEntity replacement, BoltMigrationSnapshot snapshot,
            out string detail)
        {
            if (!Enter(out detail)) return false;
            try
            {
                Require(Role == BoltMigrationRole.Destination && _held && _carrier == null
                    && !_restoreAttempted && _approvedFingerprint != null && _physicsApprovalReason != null
                    && _survivalApprovalReason != null,
                    "Destination replica hold and independent inventory, physics and survival approval must precede carrier preparation.");
                if (!ReadSnapshot(snapshot, out detail)) return false;
                Require(Alive(replacement) && replacement.isOwner && replacement.isAttached,
                    "The verified native Instantiate return must be the new attached owner.");
                ulong id = replacement.networkId.PackedValue;
                Require(id != _plan.OldNetworkId && (_replacementId == 0 || _replacementId == id),
                    "The native replacement conflicts with the authorized identity.");
                _replacementId = id;
                _replacement = replacement;
                ExpectReplacementReplica();
                AcquireReplacementReplica(replacement);
                if (!PlayerCarrier.TryPrepare(replacement, _snapshot.CarrierProfile, out _carrier, out detail))
                {
                    if (_carrier != null) _unconfirmed = true;
                    return false;
                }
                Require(ReplacementCurrent(replacement), "Player replacement changed during carrier preparation.");
                detail = "Exact server carrier held; ordinary native startup must complete before restoration.";
                return true;
            }
            catch (Exception error) { _unconfirmed = true; detail = Failure("Player replacement hold unconfirmed", error); return false; }
            finally { Exit(detail); }
        }

        public BoltMigrationAdmission ReadReplacementAdmission(BoltEntity replacement,
            BoltMigrationRole role, BoltMigrationSnapshot snapshot, out string detail)
        {
            if (!Enter(out detail)) return BoltMigrationAdmission.Rejected;
            try
            {
                Require(role == Role && _held && !_rebindAttempted && !_restoreAttempted,
                    "Replacement admission has the wrong role or phase.");
                if (!ReadSnapshot(snapshot, out detail)) return BoltMigrationAdmission.Rejected;
                if (!ReplacementCurrent(replacement))
                { detail = "Waiting for the exact authenticated replacement and expected local role."; return BoltMigrationAdmission.Pending; }
                _replacement = replacement;
                if (_replacementReplica == null || !_replacementReplica.Held())
                { detail = "Waiting for authenticated replacement writer custody."; return BoltMigrationAdmission.Pending; }
                var replicaAdmission = _replacementReplica.ReadBoundary(out detail);
                if (replicaAdmission != BoltMigrationAdmission.Ready) return replicaAdmission;
                if (Role == BoltMigrationRole.Destination)
                    return _carrier == null ? BoltMigrationAdmission.Rejected : _carrier.ReadReadiness(out detail);
                if (Role == BoltMigrationRole.Source)
                {
                    Require(_lifecycle.IsPaused, "The retained source lost its native hold.");
                    if (BoltNetwork.FindEntity(new NetworkId(_plan.OldNetworkId)) != null)
                    { detail = "Waiting for retirement of the original player attachment."; return BoltMigrationAdmission.Pending; }
                    return _replacementReplica.ReadBoundary(out detail);
                }
                return _replacementReplica.ReadBoundary(out detail);
            }
            catch (Exception error) { detail = Failure("Player replacement admission rejected", error); return BoltMigrationAdmission.Rejected; }
            finally { Exit(detail); }
        }

        public bool TryRestore(BoltEntity replacement, BoltMigrationSnapshot snapshot, out string detail)
        {
            if (!Enter(out detail)) return false;
            try
            {
                Require(Role == BoltMigrationRole.Destination && _held && !_restoreAttempted
                    && ReplacementCurrent(replacement) && _carrier != null, "Restore requires the one held destination carrier.");
                if (!ReadSnapshot(snapshot, out detail)
                    || _carrier.ReadReadiness(out detail) != BoltMigrationAdmission.Ready) return false;
                _restoreAttempted = true;
                var animation = PlayerPreservation.TryRestoreSettledAnimationState(_peer, replacement,
                    _plan.OldNetworkId, _snapshot.AnimationState, out detail);
                if (!Successful(animation)) { _unconfirmed = true; return false; }
                _restoredAnimator = replacement._entity.Serializer.TryCast<global::PlayerState>()?.Animator;
                Require(Alive(_restoredAnimator), "The restored carrier Animator is unavailable.");
                _restoredAnimatorSpeed = _restoredAnimator.speed;
                Require(float.IsFinite(_restoredAnimatorSpeed) && _restoredAnimatorSpeed >= 0,
                    "The restored carrier playback speed is invalid.");
                _animatorHeld = true;
                _restoredAnimator.speed = 0;
                Require(_restoredAnimator.speed == 0, "Restored carrier animation could not be held through aggregate repair.");
                // Scalar Animator bindings depend on the restored controller state and layers.
                var state = PlayerPreservation.TryRestoreWritableState(_peer, replacement,
                    _plan.OldNetworkId, _snapshot.PlayerState, null, out detail);
                if (!Successful(state)) { _unconfirmed = true; return false; }
                if (!_carrier.TryPrepareServerStats(out _, out detail)
                    || !_carrier.TryRestoreSurvival(_snapshot.Survival, ApproveRestoredSurvival, out detail))
                { _unconfirmed = true; return false; }
                if (!PlayerWaterContactAuthority.TryRestoreServer(_carrier, _snapshot.Survival.WaterContacts,
                    global::Modstoke.Loader.HarmonySupport.CreateState(), out _serverWater, out detail))
                { _unconfirmed = true; return false; }
                RequireContext();
                Require(ReplacementCurrent(replacement) && Alive(_restoredAnimator) && _restoredAnimator.speed == 0,
                    "Destination or its held animation changed during player restoration.");
                if (!ServerWorld.TryAssignEntityControl(_replacementId, null, _peer.ServerSession,
                        out var control))
                { _unconfirmed = true; detail = "Native player control assignment was not confirmed: " + control.Detail; return false; }
                RequireContext();
                Require(ReplacementCurrent(replacement) && replacement.controller != null
                    && !replacement.controller.WasCollected
                    && replacement.controller.Pointer == _peer.ConnectionPointer
                    && replacement.controller.ConnectionId == _peer.ConnectionId,
                    "The original peer did not retain exact native control after assignment.");
                _controlAssigned = true;
                _restored = true;
                detail = "Canonical state restored and original peer control assigned once; carrier physics remains held.";
                return true;
            }
            catch (Exception error) { _unconfirmed |= _restoreAttempted; detail = Failure("Player restoration rejected or unconfirmed", error); return false; }
            finally { Exit(detail); }
        }

        private bool ApproveRestoredSurvival(PlayerCarrier carrier,
            PlayerSurvivalSnapshot survival)
        {
            RequireContext();
            return Role == BoltMigrationRole.Destination && _restoreAttempted && !_restored
                && ReferenceEquals(carrier, _carrier) && ReferenceEquals(survival, _snapshot?.Survival)
                && ValidReason(_survivalApprovalReason) && ReplacementCurrent(carrier.Entity);
        }

        public bool TryRebindRetainedPlayer(BoltEntity originalRigEntity, BoltEntity replacement,
            BoltMigrationSnapshot snapshot, out string detail)
        {
            if (!Enter(out detail)) return false;
            try
            {
                Require(Role == BoltMigrationRole.Source && Same(originalRigEntity, _original)
                    && _referencesRepaired && _retirementObserved && !_rebindAttempted && _lifecycle.IsPaused
                    && ReplacementCurrent(replacement), "Retained rebind requires proven retirement and completed reference repair.");
                if (!ReadSnapshot(snapshot, out detail)) return false;
                _rebindAttempted = true;
                if (!Successful(_rig.TryRebind(replacement, _replacementId, out detail))
                    || !Successful(_lifecycle.TryRestoreAfterRebind(replacement, _replacementId, out detail))
                    || !_sourceEquipment.TryRebindSourceSubscriptions(out detail))
                { _unconfirmed = true; return false; }
                RequireContext();
                Require(_lifecycle.IsPaused && _lifecycle.CallbacksRestored && ReplacementCurrent(replacement)
                    && !_sourceInventory.IsFaulted && !_sourceEquipment.IsFaulted,
                    "Retained lifetime changed during callback repair.");
                _rebound = true;
                detail = "Original rig, camera, inventory and callback sources rebound; native writers remain held.";
                return true;
            }
            catch (Exception error) { _unconfirmed |= _rebindAttempted; detail = Failure("Retained player rebind rejected or unconfirmed", error); return false; }
            finally { Exit(detail); }
        }

        public bool TryRebindReplica(BoltEntity replacement, BoltMigrationSnapshot snapshot, out string detail)
        {
            if (!Enter(out detail)) return false;
            try
            {
                if (!ReadSnapshot(snapshot, out detail)) return false;
                Require(_referencesRepaired && ReplacementCurrent(replacement) && _replacementReplica?.Held() == true
                    && (Role == BoltMigrationRole.Source ? _rebound
                        : Role == BoltMigrationRole.Observer || (_restored && _controlAssigned)),
                    "Peer-local player rebind has not completed for this exact role.");
                detail = "Exact peer-local player rebind verified; no writers resumed.";
                return true;
            }
            catch (Exception error) { detail = Failure("Player replica rebind rejected", error); return false; }
            finally { Exit(detail); }
        }

        public bool TryResume(BoltEntity entity, out string detail)
        {
            if (Same(entity, _original) && Role == BoltMigrationRole.Source)
                return CancelSource(entity, out detail);
            detail = "Player resume requires canonical vitals, complete gameplay authority and prepared runtime endpoints before releasing carrier physics.";
            return false;
        }

        public bool TryResumeRetainedPlayer(BoltEntity originalRigEntity, BoltEntity replacement,
            BoltMigrationSnapshot snapshot, out string detail)
        {
            detail = "Retained resume requires complete ingress and gameplay authority with prepared runtime endpoints; no writer hold was released.";
            return false;
        }

        public bool TryResumeReplica(BoltEntity replacement, BoltMigrationSnapshot snapshot, out string detail)
        {
            detail = "Aggregate gameplay activation is not complete; replica writers remain held and completion is rejected.";
            return false;
        }

        internal bool SessionMatches(BoltMigrationSession session) => session != null
            && ReferenceEquals(session.Peer, _peer) && session.Role == Role && _plan.IsValid
            && session.Snapshot != null && session.Snapshot.Plan == _plan && _snapshot != null
            && session.Snapshot.PayloadSpan.SequenceEqual(_snapshot.Encoded);

        private BoltMigrationSession _kernelSession;

        private BoltMigrationSession ReadExactSession()
        {
            if (_kernelSession != null)
                return SessionMatches(_kernelSession) ? _kernelSession : null;
            var session = _factory.ReadSession();
            if (!SessionMatches(session)) return null;
            _kernelSession = session;
            return session;
        }

        private bool RetainExactSession(BoltMigrationSession session)
        {
            if (!SessionMatches(session) || (_kernelSession != null && !ReferenceEquals(_kernelSession, session)))
                return false;
            _kernelSession = session;
            return true;
        }

        internal void RetainUnconfirmed(string reason)
        {
            if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("Use the player adapter's game thread.");
            _unconfirmed = true;
            Detail = reason;
            RetireRuntimeAnimation();
            // Disposal is not rollback: retired native publishers and inventory custody must
            // remain held. The lifecycle and inventory registries retain those exact objects.
            _bridge?.Dispose();
        }

        private bool ReplacementCurrent(BoltEntity entity) => _replacementId != 0 && Alive(entity)
            && entity.isAttached && entity.networkId.PackedValue == _replacementId
            && entity.prefabId.Value == _plan.PrefabId && !Same(entity, _original)
            && (_replacement == null || Same(entity, _replacement))
            && entity.isOwner == (Role == BoltMigrationRole.Destination)
            && (Role != BoltMigrationRole.Source || entity.hasControl)
            && entity._entity?.Serializer?.TryCast<NetworkState>()?.Meta?.TypeId.Value == _plan.StateTypeId
            && Same(BoltNetwork.FindEntity(new NetworkId(_replacementId)), entity);
    }
}
