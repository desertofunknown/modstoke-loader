// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerLifecycle
    {
        internal delegate BoltMigrationAdmission SourceHoldAdmission(out string detail);
        private static SourceInputHold _sourceInputHold;

        // Unlike IsInventoryIngressFenced this is ONLY a new local input fence. It
        // must not reject an authoritative grant or suspend an accepted transaction.
        internal static bool IsSourceInputFenced(PlayerInventory inventory)
            => _sourceInputHold?.FencesInventory(inventory) == true;

        internal bool TryArmSourceInputHold(PlayerBridge bridge,
            SourceHoldAdmission acceptedWork, out SourceInputHold hold, out string detail)
        {
            hold = null;
            detail = "Arm the live hold on the exact repaired source while its initial lifecycle pause is owned.";
            if (!OnThread || _busy || _sourceInputHold != null || acceptedWork == null
                || bridge == null || bridge.IsDisposed || bridge.IsServer || bridge.IsGameplayActive
                || !_retired || !_restored || !IsPaused || _resumeComplete || _restorationFailed
                || Time.inFixedTimeStep || HasActiveNativeCall() || !ActionObservationAvailable(out detail)) return false;
            try
            {
                var candidate = new SourceInputHold(this, bridge, acceptedWork);
                if (!candidate.IdentityCurrent()) return false;
                if (!TryInstallSourceHoldHooks(out detail)) return false;
                _sourceInputHold = hold = candidate;
                bridge.Retired += candidate.OnBridgeRetired;
                detail = "Live input/body owner armed; the initial migration pause still owns all native writes.";
                return true;
            }
            catch (Exception error) { detail = "Source hold preparation refused: " + error.Message; return false; }
        }

        /// <summary>Call before an accepted external life/inventory writer touches this rig.</summary>
        internal static bool TryYieldSourceInputHoldForAcceptedWork(PlayerBridge bridge, out string detail)
        {
            detail = "No source freshness hold owns this bridge.";
            var hold = _sourceInputHold;
            return hold == null || !ReferenceEquals(hold.Bridge, bridge) || hold.TryYield(out detail);
        }

        internal sealed class SourceInputHold
        {
            private readonly PlayerLifecycle _owner;
            internal readonly PlayerBridge Bridge;
            private readonly SourceHoldAdmission _acceptedWork;
            private bool _acknowledged, _fenced, _writing, _retired;
            private bool _animatorOwned, _bodyOwned, _collisionOwned;
            private SourceHoldSnapshot _snapshot;
            internal string FailureReason { get; private set; }
            internal bool IsAcknowledged => _acknowledged && !_retired && FailureReason == null;
            internal bool OwnsPhysics => _bodyOwned || _animatorOwned || _collisionOwned;
            internal bool IsPhysicsHeld
            {
                get { try { return OwnsPhysics && IdentityCurrent() && _snapshot?.MatchesHeld(_owner) == true; } catch (Exception) { return false; } }
            }
            internal bool FencesInput => _acknowledged && (_fenced || OwnsPhysics || FailureReason != null || _retired);

            internal SourceInputHold(PlayerLifecycle owner, PlayerBridge bridge,
                SourceHoldAdmission acceptedWork)
            { _owner = owner; Bridge = bridge; _acceptedWork = acceptedWork; }

            internal bool IdentityCurrent() => _owner.OnThread && !_retired && !Bridge.IsDisposed
                && _owner._retired && _owner._restored && !_owner._cancelled && !_owner._restorationFailed
                && _owner.SourceSessionProvesRetirement(_owner._sourceSession)
                && _owner._sourceSession.Snapshot.Plan.Id == Bridge.Plan.Id
                && _owner._sourceSession.Snapshot.Plan.Generation == Bridge.Plan.Generation
                && _owner._sourceSession.NewNetworkId == Bridge.NetworkId
                && ReferenceEquals(_owner._sourceSession.Peer, Bridge.Peer)
                && Bridge.TryReadCurrentTarget(out var target) && _owner.ReboundIdentity(target, Bridge.NetworkId);

            internal bool TryAcknowledgeRelease(PlayerLifecycle lifecycle,
                BoltMigrationMutationResult result, out string detail)
            {
                detail = FailureReason ?? "Acknowledge the exact confirmed lifecycle release before allowing a native consumer.";
                if (!_owner.OnThread || !ReferenceEquals(lifecycle, _owner) || _writing || _retired
                    || FailureReason != null || Time.inFixedTimeStep || HasActiveNativeCall()
                    || result is not (BoltMigrationMutationResult.Applied or BoltMigrationMutationResult.Unchanged)) return false;
                try
                {
                    if (!IdentityCurrent() || !Bridge.IsGameplayActive || !_owner._resumeComplete
                        || !_owner._writersReleased || _owner._freezeStarted || _owner._paused
                        || _owner._protectionEvents != null || _actionFence != null || _owner._body.isKinematic
                        || _owner._animator.speed != _owner._savedAnimatorSpeed) return false;
                    _acknowledged = true;
                    detail = "Initial migration writes/protection released; repeatable live hold owns subsequent freshness intervals.";
                    return true;
                }
                catch (Exception error) { Fail("Source release acknowledgement failed: " + error.Message); detail = FailureReason; return false; }
            }

            internal BoltMigrationAdmission Pump(bool fresh, out string detail)
            {
                detail = FailureReason ?? "Source input hold awaits the initial lifecycle release acknowledgement.";
                if (!_owner.OnThread || _writing || FailureReason != null || _retired)
                    return BoltMigrationAdmission.Rejected;
                if (!_acknowledged) return BoltMigrationAdmission.Pending;
                try
                {
                    RequireLive();
                    if (!fresh) _fenced = true;
                    var external = _acceptedWork(out detail);
                    if (external == BoltMigrationAdmission.Rejected)
                    { Fail(detail); return external; }
                    if (fresh)
                    {
                        if (HasActiveNativeCall() || Time.inFixedTimeStep)
                        { detail = "Fresh water retained; release waits for the outer render boundary."; return BoltMigrationAdmission.Pending; }
                        if (OwnsPhysics && !Restore(out detail)) return BoltMigrationAdmission.Rejected;
                        _fenced = false;
                        detail = "Fresh water applied; original native writers/input and exact owned physics state restored.";
                        return BoltMigrationAdmission.Ready;
                    }
                    if (OwnsPhysics)
                    {
                        if (!_snapshot.MatchesHeld(_owner)) throw new InvalidOperationException("A competing writer changed the held source rig.");
                        if (external == BoltMigrationAdmission.Pending)
                            return TryYield(out detail) ? BoltMigrationAdmission.Pending : BoltMigrationAdmission.Rejected;
                        detail = "Stale water: exact body and settled motor writers held; accepted work remains separately owned.";
                        return BoltMigrationAdmission.Pending;
                    }
                    if (external != BoltMigrationAdmission.Ready) return external;
                    var native = ReadDrain(out detail);
                    if (native == BoltMigrationAdmission.Rejected) Fail(detail);
                    if (native != BoltMigrationAdmission.Ready) return native;
                    Acquire();
                    detail = "Stale water: accepted native/physics work drained; exact body, Animator and motor/input writers held.";
                    return BoltMigrationAdmission.Pending;
                }
                catch (Exception error) { Fail("Source freshness quarantine failed: " + error.Message); detail = FailureReason; return BoltMigrationAdmission.Rejected; }
            }

            private void RequireLive()
            {
                if (!IdentityCurrent() || !Bridge.IsGameplayActive || !_owner._resumeComplete
                    || _actionFence != null || !_owner.ActionObservationAvailable(out _))
                    throw new InvalidOperationException("Source hold lost its exact completed session or action observation.");
            }

            internal void ValidateWaterProjection()
            {
                if (!_owner.OnThread || _writing || _retired || FailureReason != null || !IdentityCurrent())
                    throw new InvalidOperationException(FailureReason ?? "Water projection lost its retained source hold lifetime.");
                if (!_acknowledged) return; // First sample is still under the initial migration pause.
                RequireLive();
                if (OwnsPhysics && (_snapshot == null || !_snapshot.MatchesHeld(_owner)))
                {
                    Fail("A writer changed held motor/body values across sensor-only water projection.");
                    throw new InvalidOperationException(FailureReason);
                }
            }

            private BoltMigrationAdmission ReadDrain(out string detail)
            {
                detail = "Finish the accepted native call and physics step before acquiring a live body hold.";
                if (HasActiveNativeCall() || _routineStart != null || Time.inFixedTimeStep)
                    return BoltMigrationAdmission.Pending;
                var c = _owner._character;
                var a = _owner._animationControl;
                bool swimming = c._isAnimControllerSwimming && a.IsSwimming;
                if (c._isAnimControllerSwimming != a.IsSwimming)
                { detail = "Native water entry/exit has not settled."; return BoltMigrationAdmission.Pending; }
                if (!c.enabled || !a.enabled || !_owner._animator.enabled || _owner._body.isKinematic
                    || !_owner._body.detectCollisions || !_owner.Root.activeInHierarchy || _owner.Root.transform.parent != null
                    || c.standingOnRaft || c.StandingOnDynamicObject || c.onSlipperySlope || a.onRope || a.cliffClimb
                    || a.onRaft || a.IsSledding || a.fullyAttachedToSled || a.IsConnectedToLogSled())
                { detail = "Live freshness hold does not own this disabled, parented, vehicle, climb or dynamic-support motor."; return BoltMigrationAdmission.Rejected; }
                foreach (var routine in Routines.Values)
                    if (_owner.OwnsActor(routine.Owner) && routine.Suspended)
                    { detail = "An accepted source continuation has an unconfirmed suspended lifetime."; return BoltMigrationAdmission.Rejected; }
                var continuation = _owner.ReadContinuationAdmission(out detail, swimming);
                if (continuation != BoltMigrationAdmission.Ready) return continuation;
                if (!float.IsFinite(a._disableSwimmingTimer))
                { detail = "The native swimming-exit timer is not finite."; return BoltMigrationAdmission.Rejected; }
                if (a._disableSwimmingTimer > 0)
                { detail = "The accepted native swimming-exit timer is still draining."; return BoltMigrationAdmission.Pending; }
                float dt = Time.fixedDeltaTime;
                if (!float.IsFinite(dt) || dt <= 0)
                { detail = "Source physics step duration is invalid."; return BoltMigrationAdmission.Rejected; }
                var force = _owner._body.GetAccumulatedForce(dt);
                var torque = _owner._body.GetAccumulatedTorque(dt);
                if (!Finite(force) || !Finite(torque))
                { detail = "Source force accumulators are not finite."; return BoltMigrationAdmission.Rejected; }
                if (!force.Equals(Vector3.zero) || !torque.Equals(Vector3.zero) || c._isJumping)
                { detail = "Accepted force, torque or jump is still draining."; return BoltMigrationAdmission.Pending; }
                // Refresh this read-only inventory of interaction proxies for each acquisition.
                // It scans every loaded object, so it runs last, once everything else is settled.
                _owner.CaptureInteractionBoundaries();
                var interactions = _owner.ReadInteractionAdmission(out detail);
                if (interactions != BoltMigrationAdmission.Ready) return interactions;
                detail = "Exact rebound native actions and body forces are settled.";
                return BoltMigrationAdmission.Ready;
            }

            private void Acquire()
            {
                var snapshot = new SourceHoldSnapshot(_owner);
                _snapshot = snapshot;
                _writing = true;
                try
                {
                    // Own before setters: native writes can precede an interop exception.
                    _animatorOwned = true;
                    _owner._animator.speed = 0;
                    _collisionOwned = true;
                    _owner._body.collisionDetectionMode = CollisionDetectionMode.Discrete;
                    _bodyOwned = true;
                    _owner._body.isKinematic = true;
                    snapshot.HeldVelocity = _owner._body.velocity;
                    snapshot.HeldAngularVelocity = _owner._body.angularVelocity;
                    if (!snapshot.MatchesHeld(_owner)) throw new InvalidOperationException("Source body hold readback changed.");
                }
                catch (Exception error)
                {
                    Fail("Source hold acquisition unconfirmed: " + error.Message);
                    ReholdOwnedWrites();
                    throw;
                }
                finally { _writing = false; }
            }

            // Incoming accepted work must run once, on the original stack. This is an
            // ownership yield, not a fresh-water resume or a queued/replayed callback.
            internal bool TryYield(out string detail)
            {
                detail = FailureReason ?? "Accepted work can continue on the retained source.";
                if (!_owner.OnThread || _writing || FailureReason != null || _retired) return false;
                if (!OwnsPhysics) return true;
                try { RequireLive(); return Restore(out detail); }
                catch (Exception error) { Fail("Accepted-work source yield failed: " + error.Message); detail = FailureReason; return false; }
            }

            private bool Restore(out string detail)
            {
                detail = "Source hold restoration is unconfirmed; retain ownership without retry.";
                RequireLive();
                if (_snapshot == null || !_snapshot.MatchesHeld(_owner))
                    throw new InvalidOperationException("Held source values changed; no foreign state was overwritten.");
                _writing = true;
                try
                {
                    var b = _owner._body;
                    b.isKinematic = false;
                    b.velocity = _snapshot.Velocity;
                    b.angularVelocity = _snapshot.AngularVelocity;
                    b.collisionDetectionMode = _snapshot.CollisionMode;
                    if (_snapshot.Sleeping) b.Sleep(); else b.WakeUp();
                    _owner._animator.speed = _snapshot.AnimatorSpeed;
                    if (!_snapshot.MatchesRestored(_owner) || !IdentityCurrent())
                        throw new InvalidOperationException("Source hold restoration readback changed.");
                    _bodyOwned = _animatorOwned = _collisionOwned = false;
                    _snapshot = null;
                    detail = "Exact body/Animator state restored; new input remains fenced until water is fresh.";
                    return true;
                }
                catch (Exception error)
                {
                    Fail("Source hold restoration unconfirmed: " + error.Message);
                    ReholdOwnedWrites();
                    detail = FailureReason;
                    return false;
                }
                finally { _writing = false; }
            }

            private void ReholdOwnedWrites()
            {
                // Only compensate our attempted writes when exact identity and all
                // untouched values still match. Never retry a failed live restoration.
                try
                {
                    if (!IdentityCurrent() || _snapshot == null || !_snapshot.MatchesOwnedTransition(_owner)) return;
                    if (_animatorOwned) _owner._animator.speed = 0;
                    if (_collisionOwned) _owner._body.collisionDetectionMode = CollisionDetectionMode.Discrete;
                    if (_bodyOwned)
                    {
                        _owner._body.isKinematic = true;
                        _snapshot.HeldVelocity = _owner._body.velocity;
                        _snapshot.HeldAngularVelocity = _owner._body.angularVelocity;
                    }
                }
                catch (Exception) { /* Ownership remains failed; no automatic thaw. */ }
            }

            internal bool Owns(Component actor) => _owner.OwnsActionIdentity(actor);
            internal bool HoldsWriter(Component actor) => OwnsPhysics && Owns(actor) && !Same(actor, _owner._stats);
            internal bool MasksInput(Component actor) => FencesInput && Same(actor, _owner._character) && Owns(actor);
            internal bool FencesInventory(PlayerInventory inventory) => FencesInput && inventory != null
                && _owner._inventoryIdentity?.Matches(inventory) == true;
            internal bool IsInputPlayer => FencesInput && _owner.IsCapturedInputPlayer();
            internal void Fail(string reason) { FailureReason ??= reason ?? "Source freshness hold became unconfirmed."; }
            internal void OnBridgeRetired() => Retire("The exact source hold bridge retired.");
            internal void Retire(string reason)
            {
                if (!_owner.OnThread) throw new InvalidOperationException("Retire source hold on its game thread.");
                _retired = true;
                Fail(reason);
                Bridge.Retired -= OnBridgeRetired;
                // Prepared cancellation owns no writes and never fenced native input.
                if (!_acknowledged && !OwnsPhysics && ReferenceEquals(_sourceInputHold, this)) _sourceInputHold = null;
            }

            internal bool TargetsGone()
            {
                if (Alive(_owner.Root) || Alive(_owner._body) || Alive(_owner._animator)) return false;
                foreach (var actor in _owner._actionActors) if (Alive(actor)) return false;
                foreach (var writer in _owner._coreWriters) if (Alive(writer)) return false;
                return _owner._inventoryIdentity?.IsAlive != true;
            }
        }

        private sealed class SourceHoldSnapshot
        {
            internal readonly Vector3 Velocity, AngularVelocity, Position, Input;
            internal Vector3 HeldVelocity, HeldAngularVelocity;
            internal readonly Quaternion Rotation;
            internal readonly bool Sleeping, Gravity, Run, CanRun, Grounded, Swimming, DisableGravity;
            internal readonly float AnimatorSpeed, Drag, AngularDrag, Mass;
            internal readonly int FixedFrame;
            internal readonly RigidbodyConstraints Constraints;
            internal readonly RigidbodyInterpolation Interpolation;
            internal readonly CollisionDetectionMode CollisionMode;
            internal SourceHoldSnapshot(PlayerLifecycle owner)
            {
                var b = owner._body;
                var c = owner._character;
                Velocity = b.velocity; AngularVelocity = b.angularVelocity; Position = b.position; Rotation = b.rotation;
                Sleeping = b.IsSleeping(); Gravity = b.useGravity; Drag = b.drag; AngularDrag = b.angularDrag; Mass = b.mass;
                Constraints = b.constraints; Interpolation = b.interpolation; CollisionMode = b.collisionDetectionMode;
                AnimatorSpeed = owner._animator.speed; Input = c.inputVelocity; FixedFrame = c._currentFixedFrame;
                Run = c._runInputActive; CanRun = c._canRun; Grounded = c._isGrounded;
                Swimming = c._isAnimControllerSwimming; DisableGravity = c._disableGravity;
                if (!Finite(Velocity) || !Finite(AngularVelocity) || !Finite(Position) || !Finite(Rotation) || !Finite(Input)
                    || !float.IsFinite(AnimatorSpeed) || AnimatorSpeed < 0 || !float.IsFinite(Drag)
                    || !float.IsFinite(AngularDrag) || !float.IsFinite(Mass)
                    || (Sleeping && (!Velocity.Equals(Vector3.zero) || !AngularVelocity.Equals(Vector3.zero))))
                    throw new InvalidOperationException("Source hold cannot retain this non-finite or sleeping-with-velocity state.");
            }
            private bool Unchanged(PlayerLifecycle o) => o._body.position.Equals(Position) && o._body.rotation.Equals(Rotation)
                && o._body.constraints == Constraints && o._body.interpolation == Interpolation && o._body.useGravity == Gravity
                && o._body.detectCollisions && o._body.drag == Drag && o._body.angularDrag == AngularDrag && o._body.mass == Mass
                && o._character.enabled && o._animationControl.enabled && o._animator.enabled
                && o._character.inputVelocity.Equals(Input) && o._character._currentFixedFrame == FixedFrame
                && o._character._runInputActive == Run && o._character._canRun == CanRun && o._character._isGrounded == Grounded
                && o._character._isAnimControllerSwimming == Swimming && o._animationControl.IsSwimming == Swimming
                && o._character._disableGravity == DisableGravity;
            internal bool MatchesHeld(PlayerLifecycle o) => Unchanged(o) && o._body.isKinematic
                && o._body.collisionDetectionMode == CollisionDetectionMode.Discrete && o._animator.speed == 0
                && o._body.velocity.Equals(HeldVelocity) && o._body.angularVelocity.Equals(HeldAngularVelocity);
            internal bool MatchesRestored(PlayerLifecycle o) => Unchanged(o) && !o._body.isKinematic
                && o._body.collisionDetectionMode == CollisionMode && o._animator.speed == AnimatorSpeed
                && o._body.IsSleeping() == Sleeping && o._body.velocity.Equals(Velocity) && o._body.angularVelocity.Equals(AngularVelocity);
            internal bool MatchesOwnedTransition(PlayerLifecycle o) => Unchanged(o)
                && (o._body.collisionDetectionMode == CollisionMode || o._body.collisionDetectionMode == CollisionDetectionMode.Discrete)
                && (o._animator.speed == 0 || o._animator.speed == AnimatorSpeed)
                && (o._body.velocity.Equals(Velocity) || o._body.velocity.Equals(HeldVelocity))
                && (o._body.angularVelocity.Equals(AngularVelocity) || o._body.angularVelocity.Equals(HeldAngularVelocity));
        }
    }
}
