// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerLifecycle
    {
        private enum OwnedWrite { None, Acquiring, Held, Releasing }
        private bool _pauseAttempted, _freezeStarted, _paused;
        private bool _writersReleased, _resumeComplete, _restorationFailed;
        private OwnedWrite _bodyWrite, _animatorWrite, _collisionWrite;
        private string _fenceViolation;
        private bool _wasSleeping;
        private float _savedAnimatorSpeed;
        private float _pausedGameTime;
        private Vector3 _savedVelocity, _savedAngularVelocity, _frozenPosition;
        private Quaternion _frozenRotation;
        private RigidbodyConstraints _savedConstraints;
        private CollisionDetectionMode _savedCollisionMode;
        private RigidbodyInterpolation _savedInterpolation;

        /// <summary>
        /// True only while the exact native writers and owned freeze values still match. This is
        /// not aggregate migration readiness: inventory and interaction/event domains outside
        /// the qualified ingress hooks remain caller-owned through aggregate repair.
        /// </summary>
        public bool IsPaused
        {
            get { try { return OnThread && _paused && FrozenValuesMatch(); } catch (Exception) { return false; } }
        }

        internal bool CanReleaseProtection => OnThread && !HasActiveNativeCall()
            && (_protectionEvents?.CanRelease ?? true);

        // Relative ages in a held snapshot use one clock origin on every recapture.
        internal float PausedGameTime => IsPaused ? _pausedGameTime : float.NaN;

        /// <summary>
        /// The parent must encode this speed, not the temporary zero, in its settled Animator
        /// payload. Layers/times/parameters are read normally from the frozen Animator.
        /// </summary>
        public bool TryReadResumeAnimatorSpeed(out float speed)
        {
            speed = 0;
            if (!IsPaused) return false;
            speed = _savedAnimatorSpeed;
            return true;
        }

        /// <summary>
        /// Read-only admission for the supported retained local ground rig. Pending means a known
        /// action, invoke, coroutine, trigger or transition is still live. Rejected means the
        /// identities, startup observation, callbacks, locomotion mode or ingress routing are unsupported.
        /// Never treats elapsed time, an idle tag, or an empty coroutine handle as completion.
        /// </summary>
        public BoltMigrationAdmission ReadAdmission(out string detail)
        {
            detail = "Retained native action admission requires the intact captured source on its game thread.";
            if (!OnThread || _busy || _retired || _cancelled || _freezeStarted)
                return BoltMigrationAdmission.Rejected;
            if (!CanFenceProtectedIngress(out detail)) return BoltMigrationAdmission.Rejected;
            try { return ReadActionAdmission(out detail); }
            catch (Exception error)
            {
                detail = "Native action admission is unreadable (" + error.GetType().Name + ").";
                return BoltMigrationAdmission.Rejected;
            }
        }

        /// <summary>
        /// Optional one-time pre-admission fence for new FPC and GenericInteraction input and
        /// interactions. Native damage and accepted iterators/invokes continue during drain;
        /// damage protection begins only once TryPause freezes the settled player.
        /// Input maps, device state and camera look are not cleared or replaced; inventory and
        /// unqualified action entry points still require their own owners.
        /// Call TryResumeOriginal on cancellation, including any later failed TryPause.
        /// </summary>
        public bool TryBeginActionFence(out string detail)
        {
            detail = "The retained movement fence requires an intact source and no competing fence.";
            if (!OnThread || _busy || _retired || _cancelled || _pauseAttempted
                || (_actionFence != null && !ReferenceEquals(_actionFence, this))) return false;
            if (!CanFenceProtectedIngress(out detail)) return false;
            try
            {
                if (HasActiveNativeCall())
                { detail = "Wait for the current native action call to finish before acquiring its ingress fence."; return false; }
                if (!OriginalIntact() || !ActionObservationAvailable(out detail)) return false;
                if (ReferenceEquals(_actionFence, this) && _protectionEvents != null)
                { detail = "This exact preparation already owns input protection and its event cutoff."; return true; }
                if (!PlayerEventRouting.TryBeginProtection(_original, out _protectionEvents, out detail)) return false;
                _actionFence = this;
                detail = "Retained input fenced; the native event cohort and accepted actions remain runnable before protected freeze.";
                return true;
            }
            catch (Exception error)
            { detail = "Movement fence rejected (" + error.GetType().Name + ")."; return false; }
        }

        /// <summary>
        /// Called once after all participant admission is Ready and external action ingress is
        /// closed. Rechecks the native boundary, then gates this rig's frame writers, pauses its
        /// Animator clock and makes its same Rigidbody kinematic. No enabled/GameObject/input
        /// state is changed and no gameplay coroutine is stopped. Retains ownership on failure.
        /// </summary>
        public bool TryPause(out string detail)
        {
            detail = "Native pause requires a settled original and an uncontended action fence.";
            if (!OnThread || _busy || _pauseAttempted || _retired || _cancelled
                || (_actionFence != null && !ReferenceEquals(_actionFence, this))) return false;
            if (!CanFenceProtectedIngress(out detail)) return false;
            _busy = true;
            try
            {
                CaptureInteractionBoundaries();
                if (ReadActionAdmission(out detail) != BoltMigrationAdmission.Ready) return false;
                _pauseAttempted = true;
                _actionFence = this;
                _savedVelocity = _body.velocity;
                _savedAngularVelocity = _body.angularVelocity;
                _wasSleeping = _body.IsSleeping();
                _frozenPosition = _body.position;
                _frozenRotation = _body.rotation;
                _savedAnimatorSpeed = _animator.speed;
                _savedConstraints = _body.constraints;
                _savedCollisionMode = _body.collisionDetectionMode;
                _savedInterpolation = _body.interpolation;
                if (!Finite(_savedVelocity) || !Finite(_savedAngularVelocity) || !Finite(_frozenPosition)
                    || !Finite(_frozenRotation)
                    || !float.IsFinite(_savedAnimatorSpeed) || _savedAnimatorSpeed < 0)
                { detail = "The retained rig has unsupported non-finite motion/animation state."; return false; }
                if (_wasSleeping && (!_savedVelocity.Equals(Vector3.zero) || !_savedAngularVelocity.Equals(Vector3.zero)))
                { detail = "A sleeping body with residual velocity has no exact sleep/velocity restoration boundary."; return false; }
                // Mark ownership BEFORE each setter: an interop exception may follow its write.
                _freezeStarted = true;
                _animatorWrite = OwnedWrite.Acquiring;
                _animator.speed = 0;
                if (_animator.speed != 0) throw new InvalidOperationException("Animator pause readback changed.");
                _animatorWrite = OwnedWrite.Held;
                // Collision-mode changes also touch the body's native physics profile. Own its
                // restoration before that first setter, even if kinematic acquisition fails.
                _bodyWrite = OwnedWrite.Acquiring;
                if (_savedCollisionMode != CollisionDetectionMode.Discrete)
                {
                    _collisionWrite = OwnedWrite.Acquiring;
                    _body.collisionDetectionMode = CollisionDetectionMode.Discrete;
                    if (_body.collisionDetectionMode != CollisionDetectionMode.Discrete)
                        throw new InvalidOperationException("Collision-mode pause readback changed.");
                    _collisionWrite = OwnedWrite.Held;
                }
                _body.isKinematic = true;
                if (!_body.isKinematic) throw new InvalidOperationException("Rigidbody pause readback changed.");
                _bodyWrite = OwnedWrite.Held;
                if (!FrozenValuesMatch())
                { detail = "Native pause readback failed; restore this handle before abandoning preparation."; return false; }
                _pausedGameTime = Time.time;
                _paused = true;
                detail = "Retained core writers and physics frozen at the verified action boundary; original objects retained.";
                return true;
            }
            catch (Exception error)
            { detail = "Native pause incomplete (" + error.GetType().Name + "); this handle still owns any attempted writes."; return false; }
            finally { _busy = false; }
        }

        /// <summary>
        /// Pre-retirement rollback, including partial acquisition and no Snapshot. The parent must
        /// establish that readiness has not escaped and native retirement has not started.
        /// Restores only this handle's writes, then releases its movement fence.
        /// </summary>
        public BoltMigrationMutationResult TryResumeOriginal(out string detail)
            => ResumeWriters(null, 0, false, out detail);

        /// <summary>
        /// Parent calls only after aggregate repair. Releases the temporary movement/physics pause
        /// but NOT the original setup/Mecanim publisher gates installed at retirement.
        /// </summary>
        public BoltMigrationMutationResult TryResumeRebound(BoltEntity replacement, ulong newId, out string detail)
            => ResumeWriters(replacement, newId, true, out detail);

        private BoltMigrationMutationResult ResumeWriters(BoltEntity replacement, ulong id,
            bool rebound, out string detail)
        {
            detail = "Writer restoration requires this handle's unchanged original rig and exact lifecycle phase.";
            if (!OnThread || _busy || (_actionFence != null && !ReferenceEquals(_actionFence, this)))
                return BoltMigrationMutationResult.Rejected;
            _busy = true;
            bool wrote = false;
            try
            {
                if (_resumeComplete && (rebound ? ReboundIdentity(replacement, id) : !_retired && OriginalIntact()))
                { detail = "This handle already restored its native writers and released protection."; return BoltMigrationMutationResult.Unchanged; }
                if (_writersReleased || _restorationFailed)
                { detail = "Writer restoration is unconfirmed and cannot be retried or rolled back."; return BoltMigrationMutationResult.Unconfirmed; }
                if (rebound ? (!_retired || !_restored || !_paused || !ReboundIdentity(replacement, id))
                    : (_retired || !OriginalIntact())) return BoltMigrationMutationResult.Rejected;
                if (HasActiveNativeCall())
                { detail = "Wait for the retained native action call stack before restoring writers."; return BoltMigrationMutationResult.Rejected; }
                if (!CanReleaseProtection)
                { detail = "Wait for the native event callback stack before restoring writers or releasing protection."; return BoltMigrationMutationResult.Rejected; }
                if ((_bodyWrite != OwnedWrite.None || _animatorWrite != OwnedWrite.None || _collisionWrite != OwnedWrite.None)
                    && !FreezeOwnershipMatches())
                { detail = "A foreign writer changed the paused rig; no foreign value was overwritten."; return BoltMigrationMutationResult.Rejected; }
                if (rebound && !FrozenValuesMatch())
                { detail = _fenceViolation ?? "The repaired rig is no longer at its frozen action boundary."; return BoltMigrationMutationResult.Rejected; }
                if (rebound && (!PlayerEventRouting.IsProtectionSelected || _protectionEvents == null
                    || _protectionEvents.Failure != null))
                { detail = "Native event protection became unconfirmed during the ownership swap."; return BoltMigrationMutationResult.Rejected; }
                // Do not relinquish any write until every readback succeeds. A later failure
                // must not leave an already-restored domain running behind held frame gates.
                if (_animatorWrite != OwnedWrite.None)
                {
                    wrote = true;
                    _animatorWrite = OwnedWrite.Releasing;
                    _animator.speed = _savedAnimatorSpeed;
                }
                if (_bodyWrite != OwnedWrite.None)
                {
                    wrote = true;
                    _bodyWrite = OwnedWrite.Releasing;
                    _body.isKinematic = false;
                    _body.velocity = _savedVelocity;
                    _body.angularVelocity = _savedAngularVelocity;
                }
                if (_collisionWrite != OwnedWrite.None)
                {
                    wrote = true;
                    if (_body.isKinematic) throw new InvalidOperationException("Cannot restore a dynamic collision mode while kinematic.");
                    _collisionWrite = OwnedWrite.Releasing;
                    _body.collisionDetectionMode = _savedCollisionMode;
                }
                if (_bodyWrite != OwnedWrite.None)
                { if (_wasSleeping) _body.Sleep(); else _body.WakeUp(); }
                if ((_animatorWrite != OwnedWrite.None && _animator.speed != _savedAnimatorSpeed)
                    || (_collisionWrite != OwnedWrite.None && _body.collisionDetectionMode != _savedCollisionMode)
                    || (_bodyWrite != OwnedWrite.None && (_body.isKinematic || _body.IsSleeping() != _wasSleeping
                        || !_body.velocity.Equals(_savedVelocity) || !_body.angularVelocity.Equals(_savedAngularVelocity)))
                    || (_freezeStarted && !FreezeOwnershipMatches()))
                    throw new InvalidOperationException("Retained writer restoration readback changed.");
                _bodyWrite = _animatorWrite = _collisionWrite = OwnedWrite.None;
                _writersReleased = true;
                _protectionEvents?.Dispose();
                _protectionEvents = null;
                _freezeStarted = false;
                _paused = false;
                _actionFence = null;
                _resumeComplete = true;
                if (!rebound) _cancelled = true;
                detail = rebound
                    ? "Retained writers resumed and migration protection ended; old setup/Mecanim publishers remain gated."
                    : "Original rig writers restored and preparation protection ended without a migration snapshot.";
                return wrote ? BoltMigrationMutationResult.Applied : BoltMigrationMutationResult.Unchanged;
            }
            catch (Exception error)
            {
                if (_writersReleased)
                {
                    _restorationFailed = true;
                    detail = "Writer restoration is unconfirmed (" + error.GetType().Name + "); do not repeat attempted writes.";
                    return BoltMigrationMutationResult.Unconfirmed;
                }
                bool held = !wrote || TryReholdOwnedWrites();
                detail = "Writer restoration unconfirmed (" + error.GetType().Name + "); "
                    + (held ? "owned writes are held again" : "owned freeze could not be confirmed")
                    + "; keep this handle and external ingress held.";
                return wrote ? BoltMigrationMutationResult.Unconfirmed : BoltMigrationMutationResult.Rejected;
            }
            finally { _busy = false; }
        }

        private bool TryReholdOwnedWrites()
        {
            // Try both independently. One failed interop call cannot skip the other domain's
            // compensation, and no new write is acquired during rollback of a partial pause.
            bool held = true;
            bool collisionHeld = false;
            try
            {
                if (_collisionWrite != OwnedWrite.None)
                {
                    if (!CollisionOwnershipMatches()) held = false;
                    else
                    {
                        _collisionWrite = OwnedWrite.Acquiring;
                        _body.collisionDetectionMode = CollisionDetectionMode.Discrete;
                        if (_body.collisionDetectionMode == CollisionDetectionMode.Discrete)
                        { _collisionWrite = OwnedWrite.Held; collisionHeld = true; }
                        else held = false;
                    }
                }
                else collisionHeld = _body.collisionDetectionMode == CollisionDetectionMode.Discrete;
            }
            catch (Exception) { held = false; }
            if (_bodyWrite != OwnedWrite.None)
            {
                try
                {
                    if (!collisionHeld) held = false;
                    else
                    {
                        _bodyWrite = OwnedWrite.Acquiring;
                        _body.isKinematic = true;
                        if (_body.isKinematic) _bodyWrite = OwnedWrite.Held;
                        else held = false;
                    }
                }
                catch (Exception) { held = false; }
            }
            if (_animatorWrite != OwnedWrite.None)
            {
                try
                {
                    if (!AnimatorOwnershipMatches()) held = false;
                    else
                    {
                        _animatorWrite = OwnedWrite.Acquiring;
                        _animator.speed = 0;
                        if (_animator.speed == 0) _animatorWrite = OwnedWrite.Held;
                        else held = false;
                    }
                }
                catch (Exception) { held = false; }
            }
            return held;
        }

        private bool ActionObservationAvailable(out string detail)
        {
            detail = "Native action observation was not installed before this player's initialization.";
            if (!ActionHooksAvailable) { detail = "Native action tracking was not explicitly selected before player initialization."; return false; }
            if (_routineTrackingFailed) { detail = "A native coroutine could not be attributed; action admission is unqualified."; return false; }
            foreach (var actor in _actionActors)
            {
                if (!Alive(actor) || !ObservedActors.TryGetValue(actor.Pointer, out var observed) || !observed.Matches(actor)) return false;
                if (UnqualifiedActors.TryGetValue(actor.Pointer, out var fault) && fault.Matches(actor))
                { detail = "A retained actor has an unqualified coroutine start or cancellation history."; return false; }
            }
            return true;
        }

        private BoltMigrationAdmission ReadActionAdmission(out string detail)
        {
            detail = "Captured local rig or original attachment changed.";
            if (!OriginalIntact() || !ActionObservationAvailable(out detail)) return BoltMigrationAdmission.Rejected;
            if (_protectionEvents == null || !ReferenceEquals(_actionFence, this))
            { detail = "The authenticated coordinator must acquire this player's protection cutoff before admission."; return BoltMigrationAdmission.Pending; }
            var events = _protectionEvents.ReadAdmission(out detail);
            if (events != BoltMigrationAdmission.Ready) return events;
            if (HasActiveNativeCall())
            { detail = "A retained native action is still on the call stack."; return BoltMigrationAdmission.Pending; }
            if (_fenceViolation != null) { detail = _fenceViolation; return BoltMigrationAdmission.Rejected; }
            if (_state.OnknockBackTrigger != null)
            {
                detail = "The original knockback callback is non-null and its reconstruction is not qualified; this is not a draining action.";
                return BoltMigrationAdmission.Rejected;
            }
            if (_character._isAnimControllerSwimming || _animationControl.IsSwimming)
            {
                detail = "Water handoff is not qualified: FirstPersonCharacter._isAnimControllerSwimming or PlayerAnimatorControl.IsSwimming is set.";
                return BoltMigrationAdmission.Rejected;
            }
            if (!GroundProfileMatches(false))
            {
                detail = "This lifecycle fence supports the active retained ground rig, not a special motor, disabled rig or parented body.";
                return BoltMigrationAdmission.Rejected;
            }
            // Unity's own accumulators distinguish pending force integration from a merely
            // quiet-looking body. Never discard a queued impulse by toggling kinematic.
            if (Time.inFixedTimeStep)
            { detail = "Wait for the current native physics step to finish."; return BoltMigrationAdmission.Pending; }
            float step = Time.fixedDeltaTime;
            if (!float.IsFinite(step) || step <= 0)
            { detail = "The native physics step is unavailable."; return BoltMigrationAdmission.Rejected; }
            var force = _body.GetAccumulatedForce(step);
            var torque = _body.GetAccumulatedTorque(step);
            if (!Finite(force) || !Finite(torque))
            { detail = "The native force accumulators are unreadable."; return BoltMigrationAdmission.Rejected; }
            if (!force.Equals(Vector3.zero) || !torque.Equals(Vector3.zero))
            { detail = "A retained body force or torque is awaiting native physics integration."; return BoltMigrationAdmission.Pending; }
            var actions = ReadContinuationAdmission(out detail);
            if (actions != BoltMigrationAdmission.Ready) return actions;
            var interactions = ReadInteractionAdmission(out detail);
            if (interactions != BoltMigrationAdmission.Ready) return interactions;
            var trigger = ReadKnockbackAdmission(out detail);
            if (trigger != BoltMigrationAdmission.Ready) return trigger;
            detail = "Retained core actions/invokes/iterators/triggers are drained; external action owners must also be fenced.";
            return BoltMigrationAdmission.Ready;
        }

        private BoltMigrationAdmission ReadContinuationAdmission(out string detail, bool stableSwimming = false)
        {
            foreach (var routine in Routines.Values)
            {
                if (!OwnsActor(routine.Owner) || routine.Instance != routine.Owner.GetInstanceID()) continue;
                detail = "Native continuation has not terminated: " + routine.Spec.Factory.DeclaringType.Name + "." + routine.Spec.Factory.Name + ".";
                return routine.Failed ? BoltMigrationAdmission.Rejected : BoltMigrationAdmission.Pending;
            }
            foreach (var actor in _actionActors)
                if (actor.TryCast<MonoBehaviour>()?.IsInvoking() == true)
                { detail = "A retained native Invoke is still scheduled on " + actor.GetIl2CppType().Name + "."; return BoltMigrationAdmission.Pending; }
            if ((!stableSwimming && !_character._isGrounded) || _character.Locked || _character._movementLocked || _character._sitting
                || (!stableSwimming && _character._disableGravity) || _character._hasOverrideMovement || _character._isInMidAction
                || _character._blockFullBodyInteraction || _character._doingExitVelocity
                || _animationControl.IsMidKnockdown() || _animationControl.IsMidHitReact() || _animationControl.IsMidSlideDown()
                || _animationControl.doingFullBodyAction || _animationControl._settingFullBodyWeightFromTransition
                || _animationControl.enteringACave || _animationControl.introCutScene || _animationControl.endGameCutScene
                || _animationControl.useRootMotion || _animationControl.useRootRotation || _animationControl.sitting
                || _animationControl.injured || _animationControl._doingGroundChop || _animationControl._doingJumpCrouch
                || _animationControl._playerUpperBodyInTransition || _animationControl._playerInventoryUpperBodyInTransition
                || _hitReactions.doingExplodeShake)
            { detail = "A native ground/action/knockback transition is still active."; return BoltMigrationAdmission.Pending; }
            if (!_animator.isInitialized || !Alive(_animator.runtimeAnimatorController) || _animator.hasBoundPlayables
                || _animator.recorderMode != AnimatorRecorderMode.Offline || _animator.isMatchingTarget
                || _animator.layerCount < 1 || _animator.layerCount > 32)
            { detail = "The retained Animator has an unsupported controller/playable/recorder state."; return BoltMigrationAdmission.Rejected; }
            for (int i = 0; i < _animator.layerCount; i++)
                if (_animator.IsInTransition(i))
                { detail = "An Animator layer transition is still active."; return BoltMigrationAdmission.Pending; }
            var parameters = _animator.parameters;
            if (!Live(parameters) || parameters.Length != _animator.parameterCount || parameters.Length > 4096)
            { detail = "The retained Animator parameter layout is unreadable or changed."; return BoltMigrationAdmission.Rejected; }
            foreach (var parameter in parameters)
            {
                if (!Live(parameter))
                { detail = "A retained Animator parameter is unavailable."; return BoltMigrationAdmission.Rejected; }
                if (parameter.type == AnimatorControllerParameterType.Trigger && _animator.GetBool(parameter.name))
                { detail = "An Animator trigger has not been consumed: " + parameter.name + "."; return BoltMigrationAdmission.Pending; }
            }
            detail = "Retained native continuations and Animator triggers are settled.";
            return BoltMigrationAdmission.Ready;
        }

        private bool FreezeOwnershipMatches() => IdentitiesMatch() && Alive(_body) && Alive(_animator)
            && _body.position.Equals(_frozenPosition) && _body.rotation.Equals(_frozenRotation)
            && _body.constraints == _savedConstraints && CollisionOwnershipMatches()
            && _body.interpolation == _savedInterpolation
            && (_bodyWrite != OwnedWrite.Held || _body.isKinematic)
            && AnimatorOwnershipMatches();

        private bool CollisionOwnershipMatches() => _collisionWrite == OwnedWrite.None
            ? _body.collisionDetectionMode == _savedCollisionMode
            : _body.collisionDetectionMode == CollisionDetectionMode.Discrete
                || (_collisionWrite != OwnedWrite.Held && _body.collisionDetectionMode == _savedCollisionMode);

        private bool AnimatorOwnershipMatches() => _animatorWrite == OwnedWrite.None || _animator.speed == 0
                || (_animatorWrite != OwnedWrite.Held && _animator.speed == _savedAnimatorSpeed);

        private bool GroundProfileMatches(bool kinematic) => _character.enabled && _animationControl.enabled
            && _animator.enabled && _body.isKinematic == kinematic && _body.detectCollisions
            && Root.activeInHierarchy && Root.transform.parent == null
            && !_character._isAnimControllerSwimming && !_animationControl.IsSwimming && !_animationControl.IsSledding
            && !_character.standingOnRaft && !_character.StandingOnDynamicObject && !_character.onSlipperySlope
            && !_animationControl.onRope && !_animationControl.cliffClimb && !_animationControl.onRaft
            && !_animationControl.fullyAttachedToSled && !_animationControl.IsConnectedToLogSled();

        private bool FrozenValuesMatch() => ReferenceEquals(_actionFence, this) && _freezeStarted
            && _fenceViolation == null && FreezeOwnershipMatches() && _body.isKinematic && _animator.speed == 0
            && _body.collisionDetectionMode == CollisionDetectionMode.Discrete && GroundProfileMatches(true)
            && ActionObservationAvailable(out _) && ReadContinuationAdmission(out _) == BoltMigrationAdmission.Ready
            && ReadInteractionAdmission(out _) == BoltMigrationAdmission.Ready
            && (_retired || !_original.isAttached || ReadKnockbackAdmission(out _) == BoltMigrationAdmission.Ready);

        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        private static bool Finite(Quaternion value) => float.IsFinite(value.x) && float.IsFinite(value.y)
            && float.IsFinite(value.z) && float.IsFinite(value.w);
    }
}
