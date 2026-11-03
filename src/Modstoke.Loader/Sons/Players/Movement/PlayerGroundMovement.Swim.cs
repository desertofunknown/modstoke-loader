// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections;
using Il2CppInterop.Runtime.Injection;
using Modstoke.Loader;
using UnityEngine;
using TheForest.Utils.Physics;

namespace Modstoke.Sons
{
    public sealed partial class PlayerGroundMovement
    {
        private static bool _swimContactTypeRegistered;
        private PlayerMovementModes _movementModes;
        private PlayerSwimBodyProfile _swimProfile;
        private Func<PlayerSwimDynamics> _readSwimDynamics;
        private Rigidbody _swimBody;
        private CapsuleCollider _swimCapsule;
        private RigidBodyCollisionFlags _swimContacts;
        private PlayerSwimCollisionReceiver _swimReceiver;
        private Coroutine _swimCoroutine;
        private bool _swimActive, _swimGrounded, _swimAwaitingPhysics, _insideSwimCoroutine, _insideMovementStep;
        private bool _groundAfterSwim, _groundAfterSwimGrounded;
        private float _swimCurrentSpeed, _swimStepDelta;
        private Vector3 _swimStepPosition;
        private PlayerMotionInput _swimStepInput;
        private PlayerMovementModeState _swimStepState;
        private Il2CppSystem.Collections.Generic.List<OnCollisionEnterProxy> _swimEnterProxies;
        private Il2CppSystem.Collections.Generic.List<OnCollisionStayProxy> _swimStayProxies;
        private Il2CppSystem.Collections.Generic.List<OnCollisionExitProxy> _swimExitProxies;

        public bool SwimmingEnabled => !_disposed && _movementModes != null;
        private bool DynamicBodyActive => _activated && !_nativeModeSuspended && (_swimActive || _nativeGroundEnabled);
        /// <summary>Retained ground/swim/sled state; while yielded, consult YieldedNativeMode instead.</summary>
        public PlayerMovementMode MovementMode => _sledAuthority != null
            ? PlayerMovementMode.Sled : _swimActive
                ? PlayerMovementMode.Swim : PlayerMovementMode.Ground;

        /// <summary>
        /// Opt in at a dry, non-physics boundary of this completed server bridge.
        /// The adapter must quiesce all other root/body writers and keep the original
        /// client FPC running. The existing policy now means supported Ground/Swim,
        /// not "is currently dry". Server water geometry selects each transition.
        /// readServerDynamics runs on this game thread before each swim step; it supplies
        /// authoritative limits, not client claims. This motor owns CurrentSpeed history
        /// after its initial capture. A non-null WaterJump profile at enrollment enables
        /// native surface jumps with server geometry/contact gates and owned timers.
        /// Enrollment still requires no active jump history. Crouch is not invented as
        /// a dive force. This does not enable any other movement mode.
        /// </summary>
        public bool TryEnableSwimming(PlayerMovementModeSettings settings,
            PlayerSwimBodyProfile bodyProfile,
            Func<PlayerSwimDynamics> readServerDynamics, out string detail)
        {
            detail = "A live dry motor, native server profile and an idle non-physics boundary are required.";
            if (Environment.CurrentManagedThreadId != _thread || _disposed || _insideMovementStep
                || Time.inFixedTimeStep || _movementModes != null || _pendingState.Sequence != 0
                || readServerDynamics == null || !ValidSwimProfile(bodyProfile)
                || !GameBuild.IsRecognized) return false;
            PlayerMovementModes modes = null;
            bool mutated = false;
            try
            {
                if (!Current() || !_groundModeAllowed() || !Current() || !Physics.autoSimulation
                    || _entity.GetComponentsInChildren<Rigidbody>(true).Length != 0
                    || _entity.GetComponentsInChildren<RigidBodyCollisionFlags>(true).Length != 0
                    || !SwimNativeProxiesQuiescent()) return false;
                var dynamics = readServerDynamics();
                if (!Current() || !PlayerMovementModes.ValidDynamics(dynamics)
                    || dynamics.SwimmingJumpTimerRunning
                    || dynamics.WaterJump is { IsJumping: true } or { BlockWaterJump: true }) return false;
                if (!PlayerMovementModes.TryCreate(_bridge, settings, out modes, out detail)) return false;
                if (!Current()) { modes.Dispose(); return false; }
                _swimProfile = bodyProfile;
                _readSwimDynamics = readServerDynamics;
                _swimCurrentSpeed = dynamics.CurrentSpeed;
                _swimWaterJumpsEnabled = dynamics.WaterJump.HasValue;
                mutated = true;
                CreateMovementBody();
                if (!modes.TryBindSwimBody(_swimBody, out detail)) throw new InvalidOperationException(detail);
                _movementModes = modes;
                if (!Current() || !global::Modstoke.Loader.Coroutines.TryStartObserved(ObserveSwimPhysics(), SwimCoroutineCompleted,
                    out var token, out detail))
                    throw new InvalidOperationException(detail);
                _swimCoroutine = token;
                if (!Current()) throw new InvalidOperationException("Carrier changed during swim enrollment.");
                detail = "Ground/Swim router enabled with exclusive motors and post-physics swim ACKs"
                    + (_swimWaterJumpsEnabled ? "; native surface water jumps enabled." : "; no water-jump profile supplied.");
                return true;
            }
            catch (Exception error)
            {
                modes?.Dispose();
                detail = "Swim enrollment failed (" + error.GetType().Name + ").";
                if (mutated) Stop(detail);
                return false;
            }
        }

        private void CreateMovementBody()
        {
            _swimBody = _entity.gameObject.AddComponent<Rigidbody>();
            _swimBody.isKinematic = true;
            _swimBody.detectCollisions = false;
            _swimBody.mass = _swimProfile.Mass;
            _swimBody.drag = _swimProfile.Drag;
            _swimBody.angularDrag = _swimProfile.AngularDrag;
            _swimBody.useGravity = _swimProfile.UseGravity;
            _swimBody.constraints = RigidbodyConstraints.FreezeRotation;
            _swimBody.interpolation = RigidbodyInterpolation.None;
            _swimBody.collisionDetectionMode = CollisionDetectionMode.Discrete;
            _swimBody.solverIterations = _swimProfile.SolverIterations;
            _swimBody.solverVelocityIterations = _swimProfile.SolverVelocityIterations;
            _swimBody.maxDepenetrationVelocity = _swimProfile.MaximumDepenetrationVelocity;
            _swimBody.maxLinearVelocity = _swimProfile.MaximumLinearVelocity;
            _swimBody.sleepThreshold = 0;
            _swimCapsule = _entity.gameObject.AddComponent<CapsuleCollider>();
            _swimCapsule.enabled = false;
            CopySwimCapsule();
            _swimCapsule.contactOffset = _swimProfile.ContactOffset;
            _swimCapsule.sharedMaterial = _swimProfile.Material;
            _swimContacts = _entity.gameObject.AddComponent<RigidBodyCollisionFlags>();
            _swimContacts.enabled = false;
            _swimContacts._targetCollider = _swimCapsule;
            _swimContacts._capsuleCollider = _swimCapsule;
            _swimContacts._transform = _transform;
            _swimContacts._collType = RigidBodyCollisionFlags.ColliderType.Capsule;
            _swimContacts._heightOffset = _swimProfile.ContactHeightOffset;
            _swimContacts._groundAngleMaxConstruction = _swimProfile.GroundAngleMaximumConstruction;
            _swimContacts._smoothedLayerMask = _swimProfile.SmoothedLayerMask;
            CreateSwimReceiver();
        }

        private void CreateSwimReceiver()
        {
            if (!_swimContactTypeRegistered)
            {
                ClassInjector.RegisterTypeInIl2Cpp<PlayerSwimCollisionReceiver>();
                _swimContactTypeRegistered = true;
            }
            _swimReceiver = _entity.gameObject.AddComponent<PlayerSwimCollisionReceiver>();
            _swimReceiver.Owner = this;
        }

        private bool RouteSwimming(bool hasInput, PlayerMotionInput input, float dt)
        {
            if (_movementModes == null) return false;
            AdvanceWaterJumpCooldown();
            bool grounded = DynamicBodyActive ? _swimGrounded : _controller.isGrounded;
            var resolution = _movementModes.ResolveOrderedSwimming(grounded, _swimJumping, out var state, out var detail);
            if (resolution == BoltMigrationAdmission.Rejected)
            { Stop(detail); return true; }
            if (resolution == BoltMigrationAdmission.Pending) return true;
            if (state.Mode == PlayerMovementMode.Ground)
            {
                if (!_movementModes.RefreshSwimBodyTopology(out detail) || !SwimNativeProxiesQuiescent())
                { Stop(detail); return true; }
                if (_swimActive)
                {
                    if (_nativeGroundEnabled) _swimActive = false;
                    else LeaveSwimBody();
                }
                if (!state.IsUnderwater) _swimJumpStarted = float.NaN;
                return _disposed;
            }
            if (state.Mode != PlayerMovementMode.Swim)
            { Stop("The selected movement mode has no qualified server motor."); return true; }
            bool jump = hasInput && input.Sequence > _lastInput && NewJumpIntent(input);
            if (jump && !state.IsUnderwater && !_swimWaterJumpsEnabled)
            { Stop("Surface water-jump contact, edge multiplier and cooldown authority are not bound."); return true; }
            var dynamics = _readSwimDynamics();
            if (!Current() || !PlayerMovementModes.ValidDynamics(dynamics)
                || dynamics.WaterJump.HasValue != _swimWaterJumpsEnabled
                || (!_swimWaterJumpsEnabled && dynamics.SwimmingJumpTimerRunning)
                || !SwimNativeProxiesQuiescent())
            { Stop("The server swim profile or unsupported water-jump history changed."); return true; }
            dynamics = dynamics with { CurrentSpeed = _swimCurrentSpeed };
            var jumpContacts = DynamicBodyActive ? _swimContacts._collFlags : _controller.collisionFlags;
            if (!_movementModes.CommitResolvedSwimmingEntry(out detail))
            { Stop(detail); return true; }
            if (!_swimActive)
            {
                if (_nativeGroundEnabled) _swimActive = true;
                else EnterSwimBody(grounded);
            }
            if (_disposed) return true;
            UpdateNativeStepHelper(hasInput ? input.Movement : Vector2.zero);
            _swimStepPosition = _swimBody.position;
            if (hasInput) _swimBody.rotation = Quaternion.Euler(0, input.ViewYaw, 0);
            // Physics rotation is immediate; TransformDirection uses the same yaw even
            // on configurations where transform synchronization was disabled globally.
            _transform.rotation = _swimBody.rotation;
            if (_swimWaterJumpsEnabled)
            {
                if (jump && !state.IsUnderwater && !TryApplySurfaceWaterJump(dynamics.WaterJump.Value, jumpContacts, out detail))
                { Stop(detail); return true; }
                dynamics = dynamics with { SwimmingJumpTimerRunning = WaterJumpTimerRunning(dynamics.WaterJump.Value) };
            }
            var intent = new PlayerMovementModeInput(hasInput ? input.Movement : Vector2.zero,
                hasInput ? input.ViewPitch : 0, jump && !_swimWaterJumpsEnabled,
                hasInput && (input.MoveButtons & PlayerMoveButtons.Sprint) != 0);
            if (_staminaVitals != null)
            {
                var conditions = ReadStaminaConditions();
                bool running = ApplyRunningStamina(intent.Sprint, dynamics.RunningAllowed,
                    conditions.OverallSpeed, conditions.WalkToRunThreshold, dt);
                dynamics = dynamics with { RunningAllowed = running };
            }
            if (!_movementModes.TryStepSwim(_swimBody, intent, dynamics, out state, out detail))
            { Stop(detail); return true; }
            if (_nativeGroundEnabled) _nativeAirControl = 1;
            _swimCurrentSpeed = state.CurrentSpeed;
            _swimStepState = state with { ServerJumping = _swimJumping };
            _swimStepInput = hasInput && input.Sequence > _lastInput ? input : default;
            _swimStepDelta = dt;
            _swimAwaitingPhysics = true;
            return true;
        }

        private void CopySwimCapsule()
        {
            _swimCapsule.direction = 1;
            _swimCapsule.isTrigger = false;
            if (_nativeGroundEnabled) { CopyNativeCapsule(); return; }
            _swimCapsule.radius = _controller.radius;
            _swimCapsule.height = _controller.height;
            _swimCapsule.center = _controller.center;
        }

        private void EnterSwimBody(bool grounded)
        {
            if (!Current() || !Finite(_velocity)) throw new InvalidOperationException("Invalid ground/swim handoff.");
            CopySwimCapsule();
            Vector3 position = _transform.position;
            Quaternion rotation = _transform.rotation;
            _controller.enabled = false;
            _controller.detectCollisions = false;
            ResetSwimContacts();
            _swimBody.position = position;
            _swimBody.rotation = rotation;
            _swimBody.useGravity = _swimProfile.UseGravity;
            _swimBody.isKinematic = false;
            _swimBody.collisionDetectionMode = _swimProfile.CollisionDetection;
            _swimBody.velocity = _velocity;
            _swimBody.detectCollisions = true;
            _swimCapsule.enabled = true;
            _swimActive = true;
            _swimGrounded = grounded;
            _swimBody.WakeUp();
            if (!Current()) throw new InvalidOperationException("Exclusive swim body could not be established.");
        }

        private void LeaveSwimBody()
        {
            if (_swimAwaitingPhysics || !Current()) throw new InvalidOperationException("Invalid swim/ground handoff.");
            Vector3 velocity = _swimBody.velocity;
            Vector3 position = _swimBody.position;
            Quaternion rotation = _swimBody.rotation;
            if (!Finite(velocity) || !Finite(position) || !CapsuleClear(_controller.height))
                throw new InvalidOperationException("The swim/ground capsule boundary is obstructed or invalid.");
            _swimCapsule.enabled = false;
            _swimBody.detectCollisions = false;
            _swimBody.collisionDetectionMode = CollisionDetectionMode.Discrete;
            _swimBody.isKinematic = true;
            _transform.SetPositionAndRotation(position, rotation);
            _velocity = velocity;
            _groundAfterSwim = true;
            _groundAfterSwimGrounded = _swimGrounded;
            ResetSwimContacts();
            _swimActive = false;
            _controller.detectCollisions = true;
            _controller.enabled = true;
            if (!Current()) throw new InvalidOperationException("Exclusive ground controller could not be restored.");
        }

        private IEnumerator ObserveSwimPhysics()
        {
            var wait = new WaitForFixedUpdate();
            while (!_disposed)
            {
                yield return wait;
                if (_disposed) yield break;
                _insideSwimCoroutine = true;
                try { CompleteSwimPhysics(); }
                finally { _insideSwimCoroutine = false; }
            }
        }

        private void CompleteSwimPhysics()
        {
            if (!_swimAwaitingPhysics) return;
            try
            {
                if (!Current() || !DynamicBodyActive || _insideMovementStep || !SupportedModeAllowed() || !Current()
                    || Time.fixedTimeAsDouble != _lastFixedTime || Time.fixedDeltaTime != _swimStepDelta
                    || !_movementModes.RefreshSwimBodyTopology(out _) || !SwimNativeProxiesQuiescent())
                { Stop("The swim post-physics boundary or supported-modes policy changed."); return; }
                _swimAwaitingPhysics = false;
                // Native flags consume only this body's delivered contacts. Their FixedUpdate
                // transfers _testBelow to AnyPointBelow and clears it for the next step.
                _swimContacts.FixedUpdate();
                _swimGrounded = _swimContacts.AnyPointBelow;
                if (_nativeGroundEnabled) _nativeContactsReady = true;
                if (_swimGrounded && Time.time > _swimJumpStarted) _swimJumping = false;
                Vector3 position = _swimBody.position;
                Vector3 resolvedVelocity = (position - _swimStepPosition) / _swimStepDelta;
                _velocity = _swimBody.velocity;
                if (!Finite(position) || !Finite(_velocity) || !Finite(resolvedVelocity))
                { Stop("The resolved swim pose or velocity is invalid."); return; }
                if (_swimStepState.Mode == PlayerMovementMode.Ground)
                {
                    _animation?.Publish(_bodyStepMove, resolvedVelocity, _swimStepDelta);
                    if (!Current()) { Stop("Carrier changed during native ground publication."); return; }
                }
                var input = _swimStepInput;
                _swimStepInput = default;
                if (input.Sequence != 0)
                {
                    _stateSequence = System.Math.Max(_stateSequence, _bridge.LastSentStateSequence);
                    if (_stateSequence == uint.MaxValue
                        || !_bridge.TryCommitSimulatedInput(input.Sequence))
                    { Stop("The post-physics swim input could not be committed."); return; }
                    _lastInput = input.Sequence;
                    _lastJumpSequence = input.JumpSequence;
                    _lastButtons = input.MoveButtons;
                    var rotation = CanonicalRotation(_swimBody.rotation);
                    // Underwater is the server geometry observation used for this force
                    // step, never the input packet or the host's LocalPlayer cache.
                    var flags = (_swimStepState.Mode == PlayerMovementMode.Swim ? PlayerMotionFlags.Swimming : 0)
                        | (_swimStepState.IsUnderwater ? PlayerMotionFlags.Underwater : 0)
                        | (_swimGrounded ? PlayerMotionFlags.Grounded : 0)
                        | (_crouched ? PlayerMotionFlags.Crouched : 0);
                    // A retained state was rejected before transport by WindowFull.
                    // Only this newly committed post-physics boundary can replace it.
                    _pendingState = new PlayerMotionState(_bridge.Plan.Id, _bridge.Plan.Generation,
                        _bridge.NetworkId, ++_stateSequence, input.Sequence, position, rotation, resolvedVelocity, flags)
                    { MovementTransitionSequence = input.MovementTransitionSequence };
                }
                FlushMovementState();
            }
            catch (Exception error) { Stop("Swim post-physics publication failed (" + error.GetType().Name + ")."); }
        }

        private bool FlushMovementState()
        {
            if (_disposed || !_activated || !_bridge.IsGameplayActive || _nativeModeSuspended) return false;
            if (_pendingState.Sequence == 0) return true;
            var sent = _bridge.SendState(_pendingState);
            if (sent == DatagramSendResult.Queued) _pendingState = default;
            else if (sent != DatagramSendResult.WindowFull)
            { Stop("Authoritative movement publication failed (" + sent + ")."); return false; }
            return true;
        }

        private bool SwimComponentsCurrent()
        {
            if (_movementModes.IsDisposed || !Physics.autoSimulation || !SwimAlive(_swimBody)
                || !SwimAlive(_swimCapsule) || !SwimAlive(_swimContacts) || !SwimAlive(_swimReceiver)
                || !ReferenceEquals(_swimReceiver.Owner, this) || !_swimReceiver.enabled || _swimContacts.enabled
                || _swimBody.gameObject.Pointer != _entity.gameObject.Pointer
                || (_nativeCarrier == null && _swimCapsule.gameObject.Pointer != _entity.gameObject.Pointer)
                || _swimContacts.gameObject.Pointer != _entity.gameObject.Pointer
                || _swimReceiver.gameObject.Pointer != _entity.gameObject.Pointer
                || _swimBody.isKinematic == DynamicBodyActive || _swimBody.detectCollisions != DynamicBodyActive
                || _swimCapsule.enabled != DynamicBodyActive || _swimCapsule.isTrigger || _swimCapsule.direction != 1
                || (_nativeCarrier == null && (_swimBody.constraints != RigidbodyConstraints.FreezeRotation
                    || _swimBody.interpolation != RigidbodyInterpolation.None || _swimBody.sleepThreshold != 0))
                || !BorrowedCarrierCurrent(DynamicBodyActive) || !NativeStepHelperCurrent()
                || _swimBody.mass != _swimProfile.Mass || _swimBody.drag != _swimProfile.Drag
                || _swimBody.angularDrag != _swimProfile.AngularDrag
                || _swimBody.solverIterations != _swimProfile.SolverIterations
                || _swimBody.solverVelocityIterations != _swimProfile.SolverVelocityIterations
                || _swimBody.maxDepenetrationVelocity != _swimProfile.MaximumDepenetrationVelocity
                || _swimBody.maxLinearVelocity != _swimProfile.MaximumLinearVelocity
                || _swimBody.collisionDetectionMode != (DynamicBodyActive ? _swimProfile.CollisionDetection : CollisionDetectionMode.Discrete)
                || _swimCapsule.contactOffset != _swimProfile.ContactOffset
                || _swimCapsule.sharedMaterial != _swimProfile.Material
                || _swimContacts._targetCollider?.Pointer != _swimCapsule.Pointer
                || _swimContacts._capsuleCollider?.Pointer != _swimCapsule.Pointer
                || _swimContacts._transform?.Pointer != _transform.Pointer
                || _swimContacts._collType != RigidBodyCollisionFlags.ColliderType.Capsule
                || _swimContacts._heightOffset != _swimProfile.ContactHeightOffset
                || _swimContacts._groundAngleMaxConstruction != _swimProfile.GroundAngleMaximumConstruction
                || _swimContacts._smoothedLayerMask.value != _swimProfile.SmoothedLayerMask) return false;
            if (_controller != null)
            {
                float height = _crouched ? _settings.CrouchingHeight : _settings.StandingHeight;
                if (!_nativeGroundEnabled && (_controller.radius != _settings.Radius || _controller.height != height
                    || !_controller.center.Equals(new Vector3(0, _settings.FootOffset + height * 0.5f, 0)))) return false;
            }
            if (_nativeGroundEnabled)
            {
                if (!NativeCapsuleCurrent()) return false;
            }
            else if (_swimActive && (_swimCapsule.radius != _controller.radius || _swimCapsule.height != _controller.height
                || !_swimCapsule.center.Equals(_controller.center))) return false;
            return _movementModes.BoundSwimBodyCurrent(_swimBody, _nativeGroundEnabled || DynamicBodyActive ? _swimCapsule : _controller)
                && (!DynamicBodyActive || _swimCapsule.attachedRigidbody?.Pointer == _swimBody.Pointer);
        }

        private bool SwimNativeProxiesQuiescent()
        {
            // Unity can deliver collision messages to disabled MonoBehaviours too.
            // Do not disable or borrow a native proxy with unknown receiver history.
            _swimEnterProxies ??= new(1);
            _swimStayProxies ??= new(1);
            _swimExitProxies ??= new(1);
            _entity.GetComponentsInChildren<OnCollisionEnterProxy>(true, _swimEnterProxies);
            _entity.GetComponentsInChildren<OnCollisionStayProxy>(true, _swimStayProxies);
            _entity.GetComponentsInChildren<OnCollisionExitProxy>(true, _swimExitProxies);
            return _swimEnterProxies.Count == 0 && _swimStayProxies.Count == 0 && _swimExitProxies.Count == 0;
        }

        private void ResetSwimContacts()
        {
            _swimContacts._collCheck.Clear();
            _swimContacts._contacts.Clear();
            _swimContacts._anyPointBelow = false;
            _swimContacts._testBelow = false;
            _swimContacts._collFlags = CollisionFlags.None;
            _swimContacts._inContact = false;
            _swimContacts._collisionCount = 0;
            _swimContacts._lowStuckPointCount = 0;
            _swimContacts._stuckPointCount = 0;
            _swimGrounded = false;
        }

        internal void ReceiveSwimCollision(Collision collision, int kind)
        {
            if (_disposed || !DynamicBodyActive) return;
            try
            {
                if (!Current() || !_swimAwaitingPhysics || collision == null || collision.WasCollected
                    || collision.contactCount > 64 || _swimContacts._collCheck.Count > 64)
                { Stop("Swim contact ownership, timing or bounded contact capacity changed."); return; }
                if (kind == 0) _swimContacts.OnCollisionEnterProxied(collision);
                else if (kind == 1) _swimContacts.OnCollisionStayProxied(collision);
                else _swimContacts.OnCollisionExitProxied(collision);
            }
            catch (Exception error) { Stop("Native swim contact processing failed (" + error.GetType().Name + ")."); }
        }

        private void SwimCoroutineCompleted(bool completed, Exception error)
        {
            _swimCoroutine = null;
            if (!_disposed) Stop("Swim post-physics observation ended"
                + (error == null ? "." : " (" + error.GetType().Name + ")."));
        }

        private void DisposeSwimming()
        {
            DisposeSledRouting();
            DisposeNativeStepHelper();
            _swimAwaitingPhysics = false;
            _swimStepInput = default;
            _readSwimDynamics = null;
            _swimWaterJumpsEnabled = false;
            _swimJumping = false;
            _swimJumpStarted = _swimJumpBlockedUntil = float.NaN;
            _movementModes?.Dispose();
            _movementModes = null;
            var token = _swimCoroutine;
            _swimCoroutine = null;
            if (token != null && !_insideSwimCoroutine)
                try { global::Modstoke.Loader.Coroutines.StopOwned(token); }
                catch (Exception error) { StopReason ??= "Swim observation cleanup unconfirmed (" + error.GetType().Name + ")."; }
            // Try each owned release even if another component was already destroyed.
            try { if (SwimAlive(_swimReceiver)) { _swimReceiver.Owner = null; _swimReceiver.enabled = false; UnityEngine.Object.Destroy(_swimReceiver); } }
            catch (Exception error) { StopReason ??= "Swim receiver cleanup unconfirmed (" + error.GetType().Name + ")."; }
            try
            {
                if (SwimAlive(_swimCapsule))
                {
                    _swimCapsule.enabled = false;
                    if (_nativeCarrier == null) UnityEngine.Object.Destroy(_swimCapsule);
                }
            }
            catch (Exception error) { StopReason ??= "Swim capsule cleanup unconfirmed (" + error.GetType().Name + ")."; }
            try
            {
                if (SwimAlive(_swimContacts))
                {
                    _swimContacts.enabled = false;
                    if (_nativeCarrier == null) UnityEngine.Object.Destroy(_swimContacts);
                }
            }
            catch (Exception error) { StopReason ??= "Swim contacts cleanup unconfirmed (" + error.GetType().Name + ")."; }
            try
            {
                if (SwimAlive(_swimBody))
                {
                    _swimBody.detectCollisions = false;
                    _swimBody.collisionDetectionMode = CollisionDetectionMode.Discrete;
                    if (!_swimBody.isKinematic)
                    {
                        _swimBody.velocity = Vector3.zero;
                        _swimBody.angularVelocity = Vector3.zero;
                    }
                    _swimBody.isKinematic = true;
                    if (_nativeCarrier == null) UnityEngine.Object.Destroy(_swimBody);
                }
            }
            catch (Exception error) { StopReason ??= "Swim body cleanup unconfirmed (" + error.GetType().Name + ")."; }
            _swimActive = false;
            _nativeGroundEnabled = false;
            _nativeContactsReady = false;
            _readNativeGroundDynamics = null;
            _nativeCarrier = null;
            _nativeCapsuleTransform = null;
            _nativeCapsuleObject = null;
        }

        private static bool SwimAlive(UnityEngine.Object value)
            => value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero && value != null;

        private static bool ValidSwimProfile(PlayerSwimBodyProfile p)
            => Range(p.Mass, 0.001f, 100000) && Range(p.Drag, 0, 1000) && Range(p.AngularDrag, 0, 1000)
                && (p.CollisionDetection is CollisionDetectionMode.Discrete or CollisionDetectionMode.Continuous
                    or CollisionDetectionMode.ContinuousDynamic or CollisionDetectionMode.ContinuousSpeculative)
                && p.SolverIterations is >= 1 and <= 255 && p.SolverVelocityIterations is >= 1 and <= 255
                && Range(p.MaximumDepenetrationVelocity, 0.01f, 1000) && Range(p.MaximumLinearVelocity, 0.01f, 1000)
                && Range(p.ContactOffset, 0.0001f, 1)
                && Range(p.ContactHeightOffset, -5, 5) && Range(p.GroundAngleMaximumConstruction, 0, 90)
                && (p.Material is null || SwimAlive(p.Material));
    }
}
