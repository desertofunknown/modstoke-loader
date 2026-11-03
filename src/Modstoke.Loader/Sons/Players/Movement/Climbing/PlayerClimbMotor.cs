// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.Injection;
using Modstoke.Loader;
using UnityEngine;
using AnimationRuntimeBinding = Modstoke.Sons.PlayerPreservation.AnimationRuntimeBinding;
using NativeInput = Modstoke.Sons.PlayerClientMovement.NativeInput;
using NativeMode = Modstoke.Sons.PlayerClientMovement.NativeMode;
using NativeModeKey = Modstoke.Sons.PlayerClientMovement.NativeModeKey;
using NativeState = Modstoke.Sons.PlayerClientMovement.NativeState;

namespace Modstoke.Sons
{
    public enum PlayerClimbKind : byte { Rope, Wall, Crane }

    /// <summary>
    /// Server-approved native action profile, not values copied from an input packet.
    /// Mode must be the protocol's assigned mode for Kind. Bounds are in the exact
    /// anchor's local frame; they include the approved entry and exit animation paths.
    /// No default speeds, animation states or synthetic scene anchors are supplied.
    /// </summary>
    public readonly record struct PlayerClimbMotorProfile(
        NativeMode Mode, PlayerClimbKind Kind, int NativeClimbType,
        int AnimationLayer, float ClimbSpeed, float InputDeadZone, Bounds LocalBounds,
        float MaximumRootMotionSpeed, bool ApplyRootRotation);

    public readonly record struct PlayerClimbStepReceipt(
        Guid MigrationId, ulong Generation, ulong NetworkId,
        NativeModeKey Key, ulong Step, double FixedTime, uint InputSequence,
        bool InputCommitted, bool CollisionLimited, Vector3 Position, Quaternion Rotation,
        Vector3 Velocity, Vector3 AnchorLocalPosition);

    /// <summary>
    /// A quiescent motor boundary. Anchor and actor object references are deliberately
    /// absent. The aggregate must preserve the complete animator/lifecycle snapshot too.
    /// This record alone is not a transferable animation state machine.
    /// </summary>
    public readonly record struct PlayerClimbHandoff(
        Guid MigrationId, ulong Generation, ulong NetworkId, NativeModeKey Key,
        ulong Step, uint LastCommittedInput, Vector3 AnchorLocalPosition,
        Quaternion AnchorLocalRotation, Vector3 Velocity, int AnimationState,
        float NormalizedTime, float AnimatorSpeed);

    /// <summary>
    /// Owns only the exact server actor's root-motion/physics loop. Native local-player
    /// actions are never called here: their implementation reads global input/camera.
    /// Preparation requires the reliable mode commit and preserves the settled native
    /// animation through its exact binding. Client actions/camera stay client-owned.
    /// </summary>
    public sealed class PlayerClimbMotor : IDisposable
    {
        private static readonly int DirectionId = Animator.StringToHash("climbDirInt");
        private static readonly int TypeId = Animator.StringToHash("climbTypeInt");
        private static readonly int SpeedId = Animator.StringToHash("climbSpeed");
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerBridge _bridge;
        private readonly AnimationRuntimeBinding _animation;
        private readonly PlayerCarrier _carrier;
        private readonly PlayerClimbMotorProfile _profile;
        private readonly BoltEntity _entity;
        private readonly Rigidbody _body;
        private readonly Animator _animator;
        private readonly RuntimeAnimatorController _controller;
        private readonly Transform _anchor, _anchorParent, _root;
        private readonly Vector3 _anchorScale;
        private readonly IntPtr _anchorParentPointer;
        private readonly int _entityId, _bodyId, _animatorId, _anchorId, _controllerId, _scene;
        private readonly bool _gravity;
        private readonly float _drag, _animatorSpeed;
        private PlayerClimbCollision _collision;
        private PlayerClimbDriver _driver;
        private NativeInput _input;
        private NativeState _publication;
        private Vector3 _anchorLocalPosition;
        private Quaternion _anchorLocalRotation, _stepAnchorRotation;
        private Matrix4x4 _stepAnchorWorldToLocal;
        private double _fixedTime = double.NaN;
        private float _deltaTime;
        private ulong _step;
        private uint _committedInput;
        private bool _pending, _rootMotionApplied, _collisionLimited, _paused, _activated, _ownsSettings, _busy, _disposed;
        private bool _released, _retirementRequested, _disposing;
        private PlayerClimbStepReceipt _receipt;
        private PlayerClimbHandoff _handoff;

        public NativeModeKey Key { get; }
        public bool IsDisposed => _disposed;
        public bool IsPaused => _paused;
        public bool IsActivated => _activated && !_disposed && !_released && !_retirementRequested && StopReason == null;
        public bool HasPendingStep => _pending;
        public string StopReason { get; private set; }
        internal bool IsExecuting => _busy || _disposing;

        private PlayerClimbMotor(PlayerBridge bridge,
            AnimationRuntimeBinding animation, PlayerCarrier carrier, Animator animator, Transform anchor,
            NativeModeKey key, PlayerClimbMotorProfile profile)
        {
            _bridge = bridge; _animation = animation; _carrier = carrier; _profile = profile; Key = key;
            _entity = carrier.Entity; _root = _entity.transform; _body = carrier.Body;
            _animator = animator; _controller = animator.runtimeAnimatorController;
            _anchor = anchor; _anchorParent = anchor.parent;
            _anchorScale = anchor.lossyScale;
            _anchorParentPointer = Alive(_anchorParent) ? _anchorParent.Pointer : IntPtr.Zero;
            _entityId = _entity.GetInstanceID(); _bodyId = _body.GetInstanceID();
            _animatorId = animator.GetInstanceID(); _anchorId = anchor.GetInstanceID();
            _controllerId = _controller.GetInstanceID(); _scene = _root.gameObject.scene.handle;
            _gravity = _body.useGravity; _drag = _body.drag;
            _animatorSpeed = animator.speed;
            _anchorLocalPosition = anchor.InverseTransformPoint(_body.position);
            _anchorLocalRotation = Quaternion.Inverse(anchor.rotation) * _body.rotation;
        }

        /// <summary>
        /// Called outside a fixed step after the aggregate has admitted the exact world
        /// anchor, inventory/action boundary, exclusive actor writers, animation snapshot
        /// and reliable enter commit. Resolving AnchorNetworkId to anchor is the caller's
        /// responsibility; this lease pins its actual object, parent, scene and controller.
        /// Returns paused. The exact animation binding captures and restores every settled
        /// layer/parameter around root-motion reinitialization. Drain triggers and native
        /// actions and establish the approved animation BEFORE this call, then activate.
        /// On a mutation failure a non-null stopped motor is returned for explicit cleanup.
        /// </summary>
        internal static bool TryPrepareCommitted(PlayerBridge bridge, AnimationRuntimeBinding animation,
            PlayerCarrier carrier, Animator animator, Transform actionOrigin,
            Transform anchor, NativeModeKey key, PlayerClimbMotorProfile profile,
            int serverCeilingLayers, out PlayerClimbMotor motor, out string detail)
        {
            motor = null;
            detail = "Commit the climb mode and restore its exact server actor/anchor/animation first.";
            if (!GameBuild.IsRecognized || bridge?.IsServer != true || !bridge.IsGameplayActive
                || Time.inFixedTimeStep || carrier == null || !carrier.IsPhysicsActive
                || !carrier.IsCurrentForMotor || !Alive(animator) || !Alive(anchor)
                || !Valid(profile) || key.Mode != profile.Mode || key.AnchorNetworkId == 0
                || key.SeatIndex != -1 || key.TransitionSequence == 0 || !bridge.IsCommittedMovementMode(key)
                || animation == null || !Same(animation.Animator, animator) || !animation.TryValidate(out detail)) return false;
            try
            {
                if (!animator.enabled || !animator.isInitialized || animator.applyRootMotion
                    || !Alive(animator.runtimeAnimatorController) || !float.IsFinite(animator.speed)
                    || animator.speed <= 0 || profile.AnimationLayer >= animator.layerCount
                    || !HasParameter(animator, DirectionId, AnimatorControllerParameterType.Int)
                    || !HasParameter(animator, TypeId, AnimatorControllerParameterType.Int)
                    || !HasParameter(animator, SpeedId, AnimatorControllerParameterType.Float)
                    || animator.GetInteger(TypeId) != profile.NativeClimbType
                    || !float.IsFinite(animator.GetFloat(SpeedId))
                    || animator.IsParameterControlledByCurve(DirectionId)
                    || animator.IsParameterControlledByCurve(TypeId)
                    || animator.IsParameterControlledByCurve(SpeedId)) return false;
                var root = carrier.Entity.transform;
                if ((!Same(animator.transform, root) && !animator.transform.IsChildOf(root))
                    || Same(anchor, root) || anchor.IsChildOf(root) || root.IsChildOf(anchor)
                    || !profile.LocalBounds.Contains(anchor.InverseTransformPoint(carrier.Body.position))
                    || (ClassInjector.IsTypeRegisteredInIl2Cpp(typeof(PlayerClimbDriver))
                        && animator.GetComponent<PlayerClimbDriver>() != null)) return false;
                // A disabled native OnAnimatorMove receiver can still be called by Unity.
                // Do not share the root-motion receiver object with that native writer.
                if (animator.GetComponent<PlayerAnimatorControl>() != null) return false;
                foreach (var writer in root.GetComponentsInChildren<PlayerAnimatorControl>(true))
                    if (Alive(writer) && writer.enabled) return false;
                if (!PlayerClimbCollision.TryCreate(bridge, carrier, actionOrigin,
                    serverCeilingLayers, out var collision, out detail)) return false;
                PlayerClimbMotor candidate;
                try
                {
                    candidate = new PlayerClimbMotor(bridge, animation, carrier, animator, anchor, key, profile);
                    candidate._collision = collision;
                }
                catch { collision.Dispose(); throw; }
                if (!candidate.Current(false)) { collision.Dispose(); return false; }
                motor = candidate;
                candidate._busy = true;
                candidate._paused = true;
                bridge.Retired += candidate.Dispose;
                if (!ClassInjector.IsTypeRegisteredInIl2Cpp(typeof(PlayerClimbDriver)))
                    ClassInjector.RegisterTypeInIl2Cpp<PlayerClimbDriver>();
                candidate._driver = animator.gameObject.AddComponent<PlayerClimbDriver>();
                candidate._driver.Bind(candidate);
                candidate._ownsSettings = true;
                if (!candidate.Current(false)) throw new InvalidOperationException("Climb preparation lost its actor.");
                carrier.Body.useGravity = false;
                carrier.Body.drag = 0;
                carrier.Body.velocity = Vector3.zero;
                carrier.Body.angularVelocity = Vector3.zero;
                if (!animation.TryAcquireClimbRootMotion(candidate, candidate._driver, out detail))
                { candidate.Stop(detail); return false; }
                if (!candidate.Current(false) || !animation.TryValidateClimbMotor(candidate))
                { candidate.Stop("Climb ownership changed during entry."); detail = candidate.StopReason; return false; }
                detail = "Committed actor climb prepared; exact animation lease restored all settled state and remains paused for activation.";
                return true;
            }
            catch (Exception error)
            {
                detail = "Climb motor entry failed (" + error.GetType().Name + ").";
                motor?.Stop(detail);
                return false;
            }
            finally { motor?.Exit(); }
        }

        internal bool TryActivateRestoredAnimation(int expectedStateHash, float expectedNormalizedTime, out string detail)
        {
            detail = "Activate the exact prepared and restored climb animation outside physics.";
            if (!Enter()) return false;
            try
            {
                if (!_paused || _activated || StopReason != null || Time.inFixedTimeStep || !Current(false)
                    || expectedStateHash == 0 || !float.IsFinite(expectedNormalizedTime)
                    || !_ownsSettings || !Alive(_driver) || !_driver.IsBoundTo(this) || !_animation.TryValidateClimbMotor(this)
                    || !_animator.applyRootMotion || _animator.speed != 0
                    || _animator.updateMode != AnimatorUpdateMode.AnimatePhysics
                    || _animator.cullingMode != AnimatorCullingMode.AlwaysAnimate
                    || _animator.GetInteger(TypeId) != _profile.NativeClimbType) return false;
                for (int layer = 0; layer < _animator.layerCount; layer++)
                    if (_animator.IsInTransition(layer)) return false;
                var state = _animator.GetCurrentAnimatorStateInfo(_profile.AnimationLayer);
                if (state.fullPathHash != expectedStateHash || !AnimationTimeMatches(state.normalizedTime, expectedNormalizedTime)
                    || !_profile.LocalBounds.Contains(_anchor.InverseTransformPoint(_body.position))) return false;
                _animator.SetInteger(DirectionId, 0);
                if (!Current(false) || !_animation.TryValidateClimbMotor(this)) throw new InvalidOperationException("Climb activation was retired.");
                _animator.SetFloat(SpeedId, _profile.ClimbSpeed);
                if (!Current(true)) throw new InvalidOperationException("Climb activation lost its settings.");
                _animator.speed = _animatorSpeed;
                if (!Current(true)) { Stop("Climb animation activation lost its exact actor binding."); detail = StopReason; return false; }
                _anchorLocalPosition = _anchor.InverseTransformPoint(_body.position);
                _anchorLocalRotation = Quaternion.Inverse(_anchor.rotation) * _body.rotation;
                _activated = true;
                _paused = false;
                detail = "Restored actor climb activated with typed input and post-physics receipts.";
                return true;
            }
            catch (Exception error) { Stop("Climb activation failed (" + error.GetType().Name + ")."); detail = StopReason; return false; }
            finally { Exit(); }
        }

        internal void FixedTick()
        {
            if (!Enter()) return;
            try
            {
                if (_paused || StopReason != null) return;
                if (!Current(true)) { Stop("Climb actor, anchor, mode or animation lifetime changed."); return; }
                double now = Time.fixedTimeAsDouble;
                if (!double.IsFinite(now) || now == _fixedTime) return;
                if (!double.IsNaN(_fixedTime) && now < _fixedTime)
                { Stop("Climb simulation time moved backwards."); return; }
                // With multiple fixed steps in one frame, the previous physics result is
                // visible here; Update completes the last step before rendering.
                if (_pending && !CompleteStep()) return;
                if (!_bridge.IsCommittedMovementMode(Key)) return;
                float dt = Time.fixedDeltaTime;
                if (!float.IsFinite(dt) || dt <= 0 || dt > 0.1f || _step == ulong.MaxValue)
                { Stop("Unsupported climb fixed-step timing."); return; }
                _input = default;
                if (_bridge.TryPeekCurrentNativeInput(out var observed))
                {
                    if (!ValidInput(observed)) { Stop("Climb input does not match the committed actor mode."); return; }
                    if (_bridge.TryReadCurrentNativeInput(out var selected))
                    {
                        if (!ValidInput(selected)) { Stop("Climb selected input changed mode."); return; }
                        _input = selected;
                    }
                }
                _fixedTime = now; _deltaTime = dt; _step++;
                _rootMotionApplied = _collisionLimited = false;
                _pending = true;
                float vertical = _input.Input.Sequence == 0 ? 0 : _input.Input.Movement.y;
                int direction = Mathf.Abs(vertical) <= _profile.InputDeadZone ? 0 : (vertical > 0 ? 1 : -1);
                _animator.SetInteger(DirectionId, direction);
                if (!Current(true)) { Stop("Climb ownership changed while selecting its animation input."); return; }
                _body.velocity = Vector3.zero;
                _body.angularVelocity = Vector3.zero;
            }
            catch (Exception error) { Stop("Climb fixed step failed (" + error.GetType().Name + ")."); }
            finally { Exit(); }
        }

        internal void AnimatorMove()
        {
            if (_animation.IsClimbRestoreEvaluation(this, _driver)) return;
            if (!Enter()) return;
            try
            {
                if (_paused || StopReason != null) return;
                if (!Current(true) || !_pending || !Time.inFixedTimeStep || _fixedTime != Time.fixedTimeAsDouble
                    || _rootMotionApplied)
                { Stop("Climb animator evaluation did not match its selected physics step."); return; }
                Vector3 delta = _animator.deltaPosition;
                if (!Finite(delta) || delta.magnitude > _profile.MaximumRootMotionSpeed * _deltaTime)
                { Stop("Climb animation produced an invalid root-motion delta."); return; }
                // Expired intent cannot continue climbing via a residual animation blend.
                if (_input.Input.Sequence == 0) delta = Vector3.zero;
                Vector3 anchorPosition = _anchor.TransformPoint(_anchorLocalPosition);
                _stepAnchorWorldToLocal = _anchor.worldToLocalMatrix;
                _stepAnchorRotation = _anchor.rotation;
                Vector3 requested = anchorPosition - _body.position + delta;
                Vector3 localDestination = _anchor.InverseTransformPoint(_body.position + requested);
                if (!Finite(localDestination) || !_profile.LocalBounds.Contains(localDestination))
                { requested = Vector3.zero; _collisionLimited = true; }
                if (!_collision.TryConstrainRootMotion(requested, _profile.Kind == PlayerClimbKind.Rope,
                    out var allowed, out bool blocked, out var detail))
                { Stop(detail); return; }
                Quaternion rotation = _stepAnchorRotation * _anchorLocalRotation;
                if (_profile.ApplyRootRotation && _input.Input.Sequence != 0)
                    rotation *= _animator.deltaRotation;
                if (!Finite(rotation) || !Current(true)) { Stop("Climb anchor pose changed during animation."); return; }
                _collisionLimited |= blocked;
                _body.velocity = allowed / _deltaTime;
                if (!Current(true)) { Stop("Climb ownership changed while applying root motion."); return; }
                _body.MoveRotation(rotation);
                if (!Current(true)) { Stop("Climb ownership changed before its physics receipt."); return; }
                _rootMotionApplied = true;
            }
            catch (Exception error) { Stop("Climb root motion failed (" + error.GetType().Name + ")."); }
            finally { Exit(); }
        }

        internal void UpdateTick()
        {
            if (!Enter()) return;
            try
            {
                if (_paused || StopReason != null) return;
                if (!Current(true)) { Stop("Climb publication lost its exact animation and physics owner."); return; }
                if (_pending && !CompleteStep()) return;
                Publish();
            }
            catch (Exception error) { Stop("Climb post-physics step failed (" + error.GetType().Name + ")."); }
            finally { Exit(); }
        }

        internal void DriverDisabled()
        {
            if (!OnThread() || _disposed) return;
            StopReason ??= "The actor's climb root-motion receiver was disabled.";
            Dispose();
        }

        private bool CompleteStep()
        {
            if (!Current(true) || !_rootMotionApplied)
            { Stop("Climb physics has no matching root-motion evaluation."); return false; }
            Vector3 position = _body.position, velocity = _body.velocity;
            Quaternion rotation = _body.rotation;
            Vector3 local = _stepAnchorWorldToLocal.MultiplyPoint3x4(position);
            if (!Finite(position) || !Finite(velocity) || !Finite(rotation) || !Finite(local)
                || !_profile.LocalBounds.Contains(local))
            { Stop("Climb physics returned a non-finite or out-of-bounds pose."); return false; }
            bool commit = _input.Input.Sequence > _committedInput;
            if (commit && !_bridge.TryCommitSimulatedNativeInput(Key, _input.Input.Sequence))
            { Stop("The exact climb input receipt expired or was replaced before physics completed."); return false; }
            if (!Current(true)) { Stop("Climb ownership changed during input receipt commit."); return false; }
            if (commit) _committedInput = _input.Input.Sequence;
            _anchorLocalPosition = local;
            _anchorLocalRotation = Quaternion.Inverse(_stepAnchorRotation) * rotation;
            _receipt = new(_bridge.Plan.Id, _bridge.Plan.Generation, _bridge.NetworkId,
                Key, _step, _fixedTime, _input.Input.Sequence, commit, _collisionLimited,
                position, rotation, velocity, local);
            _pending = false;
            if (commit)
            {
                if (_bridge.LastSentStateSequence == uint.MaxValue)
                { Stop("Climb publication sequence is exhausted."); return false; }
                // q and -q describe the same pose; the wire requires one canonical sign.
                if (rotation.w < 0 || (rotation.w == 0 && (rotation.x < 0
                    || (rotation.x == 0 && (rotation.y < 0 || (rotation.y == 0 && rotation.z < 0))))))
                    rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
                var motion = new PlayerMotionState(_bridge.Plan.Id, _bridge.Plan.Generation,
                    _bridge.NetworkId, _bridge.LastSentStateSequence + 1, _committedInput,
                    position, rotation, velocity, PlayerMotionFlags.None)
                    { MovementTransitionSequence = Key.TransitionSequence };
                _publication = new(Key, motion);
            }
            return true;
        }

        private void Publish()
        {
            if (_publication.Motion.Sequence == 0) return;
            var result = _bridge.SendNativeState(_publication);
            if (result == DatagramSendResult.WindowFull) return;
            if (result != DatagramSendResult.Queued)
            { Stop("Climb state publication failed (" + result + ")."); return; }
            _publication = default;
        }

        public bool TryReadLastStep(out PlayerClimbStepReceipt receipt)
        {
            receipt = default;
            if (!OnThread() || _disposed || _busy || StopReason != null || _receipt.Step == 0) return false;
            try
            {
                if (!Current(true)) return false;
                receipt = _receipt;
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Pause at a settled post-physics boundary before snapshotting or committing an
        /// exit to ground. Finish the original native exit/action drain separately. Never
        /// reuse this receipt after another actor, scene, controller or anchor is resolved.
        /// </summary>
        internal bool TryPrepareHandoff(out PlayerClimbHandoff handoff, out string detail)
        {
            handoff = default;
            detail = "Climb handoff requires a settled animation and completed input/publication boundary.";
            if (!Enter()) return false;
            try
            {
                if (_paused || !_activated || StopReason != null || Time.inFixedTimeStep || !Current(true)
                    || _pending || _publication.Motion.Sequence != 0) return false;
                for (int layer = 0; layer < _animator.layerCount; layer++)
                    if (_animator.IsInTransition(layer)) return false;
                var state = _animator.GetCurrentAnimatorStateInfo(_profile.AnimationLayer);
                if (!float.IsFinite(state.normalizedTime)) return false;
                _handoff = new(_bridge.Plan.Id, _bridge.Plan.Generation, _bridge.NetworkId, Key,
                    _step, _committedInput, _anchor.InverseTransformPoint(_body.position),
                    Quaternion.Inverse(_anchor.rotation) * _body.rotation, _body.velocity,
                    state.fullPathHash, state.normalizedTime, _animator.speed);
                _paused = true;
                _animator.speed = 0;
                if (!Current(true)) { Stop("Climb handoff lost its exact animation owner."); return false; }
                _body.velocity = Vector3.zero;
                _body.angularVelocity = Vector3.zero;
                if (!_animation.TryCaptureClimbHandoff(this, out detail)) { Stop(detail); return false; }
                handoff = _handoff;
                detail = "Actor climb paused at an exact input/animation boundary; aggregate snapshot and exit commit remain explicit.";
                return true;
            }
            catch (Exception error) { Stop("Climb handoff failed (" + error.GetType().Name + ")."); detail = StopReason; return false; }
            finally { Exit(); }
        }

        internal bool TryResumeCommitted(PlayerClimbHandoff handoff, out string detail)
        {
            detail = "Only this exact paused actor/anchor receipt can resume this motor.";
            if (!Enter()) return false;
            try
            {
                if (!_paused || StopReason != null || Time.inFixedTimeStep || handoff != _handoff
                    || !_activated || !Current(true) || _animator.speed != 0 || !_animation.TryValidateClimbHandoff(this)) return false;
                var state = _animator.GetCurrentAnimatorStateInfo(_profile.AnimationLayer);
                if (state.fullPathHash != handoff.AnimationState || state.normalizedTime != handoff.NormalizedTime
                    || !_anchor.InverseTransformPoint(_body.position).Equals(handoff.AnchorLocalPosition)
                    || !(Quaternion.Inverse(_anchor.rotation) * _body.rotation).Equals(handoff.AnchorLocalRotation)) return false;
                _animator.speed = handoff.AnimatorSpeed;
                if (!Current(true)) { Stop("Climb ownership changed during cancellation resume."); return false; }
                _anchorLocalPosition = handoff.AnchorLocalPosition;
                _anchorLocalRotation = handoff.AnchorLocalRotation;
                _input = default;
                _paused = false;
                detail = "Exact actor climb resumed; the next step selects current intent again.";
                return true;
            }
            catch (Exception error) { Stop("Climb resume failed (" + error.GetType().Name + ")."); detail = StopReason; return false; }
            finally { Exit(); }
        }

        /// <summary>After the reliable exit commits, release only motor-owned settings, never an old pose.</summary>
        internal bool TryReleaseToGround(uint committedExitSequence, out string detail)
        {
            detail = "Pause the climb motor, complete native exit, then commit the exact next ground mode.";
            if (!Enter()) return false;
            try
            {
                if (!_paused || !_activated || StopReason != null || _pending || _publication.Motion.Sequence != 0
                    || Time.inFixedTimeStep || Key.TransitionSequence == uint.MaxValue
                    || committedExitSequence != Key.TransitionSequence + 1 || !ActorCurrent()
                    || !_bridge.IsCommittedMovementMode(NativeModeKey.Ground(committedExitSequence))) return false;
                if (!_animation.TryReleaseClimbRootMotion(this, committedExitSequence, out detail))
                { Stop(detail); return false; }
                ReleaseSettings();
                _released = true;
                _retirementRequested = true;
                detail = "Climb lease and physics released to exact committed ground; settled exit animation preserved and paused for ground activation.";
                return true;
            }
            catch (Exception error) { Stop("Climb release failed (" + error.GetType().Name + ")."); detail = StopReason; return false; }
            finally { Exit(); }
        }

        private bool ValidInput(NativeInput input)
            => input.Key == Key && input.Input.MigrationId == _bridge.Plan.Id
                && input.Input.Generation == _bridge.Plan.Generation && input.Input.NetworkId == _bridge.NetworkId
                && input.Input.Sequence != 0 && input.Input.MovementTransitionSequence == Key.TransitionSequence
                && float.IsFinite(input.Input.Movement.x) && float.IsFinite(input.Input.Movement.y)
                && Mathf.Abs(input.Input.Movement.x) <= 1 && Mathf.Abs(input.Input.Movement.y) <= 1
                && input.Input.MoveButtons == PlayerMoveButtons.None
                && input.Input.DeltaTime == 0 && !input.CartBoost && !input.CartHandBrake && !input.SeatSettled;

        private bool ActorCurrent(bool requireBridge = true)
            => !_disposed && !_released && !_retirementRequested && OnThread() && (!requireBridge || (!_bridge.IsDisposed && _bridge.IsGameplayActive
                && _bridge.TryReadCurrentTarget(out var entity) && Same(entity, _entity)))
                && BodyCurrent()
                && Alive(_animator) && _animator.GetInstanceID() == _animatorId
                && _animator.enabled && _animator.isInitialized && _animator.gameObject.activeInHierarchy
                && (Same(_animator.transform, _root) || _animator.transform.IsChildOf(_root))
                && Alive(_controller) && _controller.GetInstanceID() == _controllerId
                && Same(_animator.runtimeAnimatorController, _controller)
                && _root.gameObject.scene.handle == _scene;

        private bool BodyCurrent()
            => !_disposed && OnThread() && _carrier.IsPhysicsActive && _carrier.IsCurrentForMotor
                && Alive(_entity) && _entity.GetInstanceID() == _entityId && Same(_entity.transform, _root)
                && Same(_carrier.Body, _body) && _body.GetInstanceID() == _bodyId
                && !_body.isKinematic && _body.detectCollisions && _carrier.PrimaryCollider.enabled
                && !_carrier.PrimaryCollider.isTrigger
                && _root.gameObject.scene.handle == _scene;

        private bool Current(bool owned)
            => CurrentCore(owned) && (!owned || _animation.TryValidateClimbMotor(this));

        private bool CurrentCore(bool owned)
        {
            if (!ActorCurrent() || !_bridge.IsCommittedMovementMode(Key) || !Alive(_anchor)
                || _anchor.GetInstanceID() != _anchorId || !_anchor.gameObject.activeInHierarchy
                || _anchor.gameObject.scene.handle != _scene || !_collision.Matches(_bridge, _carrier)) return false;
            var parent = _anchor.parent;
            if (_anchorParentPointer == IntPtr.Zero ? Alive(parent) : !Same(parent, _anchorParent)) return false;
            if (!Finite(_anchor.position) || !Finite(_anchor.rotation) || !_anchorScale.Equals(_anchor.lossyScale)
                || !Finite(_anchor.lossyScale) || _anchor.lossyScale.x <= 0
                || _anchor.lossyScale.y <= 0 || _anchor.lossyScale.z <= 0) return false;
            return !owned || (_ownsSettings && Alive(_driver) && _driver.isActiveAndEnabled
                && _driver.IsBoundTo(this) && _animator.applyRootMotion
                && _animator.updateMode == AnimatorUpdateMode.AnimatePhysics
                && _animator.cullingMode == AnimatorCullingMode.AlwaysAnimate
                && !_body.useGravity && _body.drag == 0
                && _animator.GetInteger(TypeId) == _profile.NativeClimbType
                && _animator.GetFloat(SpeedId) == _profile.ClimbSpeed);
        }

        private static bool Valid(PlayerClimbMotorProfile profile)
            => (profile.Kind switch
                {
                    PlayerClimbKind.Rope => profile.Mode == NativeMode.RopeClimb,
                    PlayerClimbKind.Wall => profile.Mode == NativeMode.WallClimb,
                    PlayerClimbKind.Crane => profile.Mode == NativeMode.CraneClimb,
                    _ => false
                })
                && profile.NativeClimbType >= 0 && profile.AnimationLayer >= 0
                && float.IsFinite(profile.ClimbSpeed) && profile.ClimbSpeed > 0
                && float.IsFinite(profile.InputDeadZone) && profile.InputDeadZone >= 0 && profile.InputDeadZone < 1
                && float.IsFinite(profile.MaximumRootMotionSpeed) && profile.MaximumRootMotionSpeed > 0
                && Finite(profile.LocalBounds.center) && Finite(profile.LocalBounds.size)
                && profile.LocalBounds.size.x > 0 && profile.LocalBounds.size.y > 0 && profile.LocalBounds.size.z > 0;

        private static bool HasParameter(Animator animator, int id, AnimatorControllerParameterType type)
        {
            foreach (var parameter in animator.parameters)
                if (parameter.nameHash == id) return parameter.type == type;
            return false;
        }

        private bool Enter()
        {
            if (!OnThread() || _disposed || _released) return false;
            if (_retirementRequested) { Dispose(); return false; }
            if (_busy || _disposing || _animation.ClimbCallInProgress)
            { _retirementRequested = true; return false; }
            _busy = true;
            return true;
        }

        private void Exit()
        {
            _busy = false;
            if (_retirementRequested) Dispose();
        }

        internal bool MatchesAnimationLease(AnimationRuntimeBinding binding, PlayerBridge bridge,
            Animator animator, RuntimeAnimatorController controller, PlayerClimbDriver driver,
            NativeModeKey key, bool releasing)
            => ReferenceEquals(_animation, binding) && ReferenceEquals(_bridge, bridge) && Key == key
                && StopReason == null && _ownsSettings && Same(animator, _animator) && Same(controller, _controller)
                && ReferenceEquals(driver, _driver) && Alive(driver) && driver.IsBoundTo(this)
                && (releasing ? _busy && _paused && _activated && !_pending && _publication.Motion.Sequence == 0
                    && Key.TransitionSequence != uint.MaxValue && ActorCurrent()
                    && _bridge.IsCommittedMovementMode(NativeModeKey.Ground(Key.TransitionSequence + 1))
                    : CurrentCore(false));

        private void Stop(string reason)
        {
            StopReason ??= reason;
            // Keep an uncertain selected step unresolved. It must not be replayed or
            // converted to a successful ACK just to let the aggregate change mode.
            try
            {
                if (!_released && !_disposed && _ownsSettings && BodyCurrent()
                    && _animation.OwnsClimbSafetyStop(this, _driver)
                    && (_bridge.IsCommittedMovementMode(Key) || (_paused && _activated && !_pending
                        && Key.TransitionSequence != uint.MaxValue
                        && _bridge.IsCommittedMovementMode(NativeModeKey.Ground(Key.TransitionSequence + 1)))))
                {
                    _body.velocity = Vector3.zero;
                    _body.angularVelocity = Vector3.zero;
                    if (Alive(_animator) && _animator.GetInstanceID() == _animatorId)
                        _animator.speed = 0;
                }
            }
            catch (Exception) { }
        }

        private void ReleaseSettings()
        {
            if (!_ownsSettings) return;
            if (!ActorCurrent() || _animator.applyRootMotion || _animator.speed != 0
                || Key.TransitionSequence == uint.MaxValue
                || !_bridge.IsCommittedMovementMode(NativeModeKey.Ground(Key.TransitionSequence + 1)))
                throw new InvalidOperationException("Climb settings cannot release without the exact restored ground animation.");
            _body.useGravity = _gravity;
            _body.drag = _drag;
            _ownsSettings = false;
        }

        /// <summary>Retire authorization. Planned exits must use TryReleaseToGround first.</summary>
        public void Dispose()
        {
            if (!OnThread() || _disposed) return;
            if (IsExecuting || _animation.ClimbCallInProgress)
            { _retirementRequested = true; return; }
            _disposing = true;
            try
            {
                Stop("Climb motor retired; aggregate cleanup owns any unfinished native action.");
                _disposed = true;
                _bridge.Retired -= Dispose;
                _collision?.Dispose();
                // Keep an inert receiver on uncertain retirement. It must not be rebound
                // or removed while Unity might otherwise apply root motion automatically.
                if (!_ownsSettings && Alive(_driver))
                {
                    _driver.Unbind(this);
                    UnityEngine.Object.Destroy(_driver);
                }
            }
            finally { _disposing = false; }
        }

        private bool OnThread() => Environment.CurrentManagedThreadId == _thread;
        private static bool AnimationTimeMatches(float actual, float expected)
            => float.IsFinite(actual) && float.IsFinite(expected)
                && MathF.Abs(actual - expected) <= MathF.Max(0.00001f, 2f * MathF.Abs(MathF.BitIncrement(expected) - expected));
        private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        private static bool Finite(Quaternion q) => float.IsFinite(q.x) && float.IsFinite(q.y)
            && float.IsFinite(q.z) && float.IsFinite(q.w) && Mathf.Abs(Quaternion.Dot(q, q) - 1) < 0.001f;
        private static bool Alive(UnityEngine.Object value)
            => value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero && value != null;
        private static bool Same(UnityEngine.Object left, UnityEngine.Object right)
            => Alive(left) && Alive(right) && left.Pointer == right.Pointer && left.GetInstanceID() == right.GetInstanceID();
    }
}
