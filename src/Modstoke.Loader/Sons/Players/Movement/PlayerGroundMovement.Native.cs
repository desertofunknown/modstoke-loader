// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Loader;
using global::Sons.Physics;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerGroundMovement
    {
        private bool _nativeGroundEnabled, _nativeJumping, _nativeContactsReady;
        private float _nativeAirControl = 1;
        private ulong _nativeGroundStep, _nativeJumpStep;
        private Func<PlayerNativeGroundDynamics> _readNativeGroundDynamics;
        private PlayerNativeCapsuleProfile _nativeCapsule;
        private PlayerCarrier _nativeCarrier;
        private Transform _nativeCapsuleTransform;
        private GameObject _nativeCapsuleObject;
        private Vector2 _bodyStepMove;
        private StepHelper _nativeStepHelper;
        private CapsuleCollider _nativeStepCollider;
        private Transform _nativeStepTransform;
        private Func<bool> _stepHelperAllowed;
        private int _nativeStepId, _nativeStepColliderId, _nativeStepLayer, _nativeStepDirection;
        private Vector3 _nativeStepCenter, _nativeStepOffset, _nativeStepRotationOffset, _nativeStepPosition;
        private Quaternion _nativeStepRotation;
        private Vector2 _nativeStepInput;
        private float _nativeStepRadius, _nativeStepHeight, _nativeStepContactOffset, _nativeStepMagnitude;
        private PhysicMaterial _nativeStepMaterial;
        private bool _nativeStepEnabled;
        private PlayerSledAuthority _sledAuthority;
        private Func<PlayerSledEnvironment> _readSledEnvironment;
        private Func<bool> _sledAllowed;
        private PlayerMotionInput _sledSteeringInput;
        private int _sledUpdateFrame = -1;
        private bool _crouchedBeforeSled;
        private bool _sledExitCrouched;

        public bool NativeGroundEnabled => !_disposed && _nativeGroundEnabled;
        public bool SledExitRequired => _sledAuthority?.StopRequested == true;

        /// <summary>
        /// Route an already authenticated, settled sled action. This is not native
        /// entry permission or inventory custody. The aggregate must keep all competing
        /// writers quiescent and call TryUpdateSledSteering from ordinary Update.
        /// This single-capsule carrier requires the approved rider radius to match its
        /// primary capsule; distinct player/primary collider profiles remain unsupported.
        /// </summary>
        public bool TryAdoptSled(Transform sledTransform, PlayerSledProfile profile,
            PlayerSledState state, Func<PlayerSledEnvironment> readEnvironment,
            Func<bool> actionAllowed, out string detail)
            => TryRouteSled(sledTransform, profile, state, readEnvironment, actionAllowed, false, out detail);

        /// <summary>
        /// Start the server-body part of an authenticated shell-ride entry. The actor's
        /// original client action retains camera, controls, layers and finalization.
        /// The provider must qualify terrain, water and animation for this actor and
        /// exclude log-sled attachment. It remains authoritative throughout entry/exit.
        /// </summary>
        public bool TryEnterSled(Transform sledTransform, PlayerSledProfile profile,
            PlayerSledState previousState, Func<PlayerSledEnvironment> readEnvironment,
            Func<bool> actionAllowed, out string detail)
            => TryRouteSled(sledTransform, profile, previousState, readEnvironment, actionAllowed, true, out detail);

        private bool TryRouteSled(Transform sledTransform, PlayerSledProfile profile,
            PlayerSledState state, Func<PlayerSledEnvironment> readEnvironment,
            Func<bool> actionAllowed, bool entering, out string detail)
        {
            detail = "A settled native motor and an authenticated actor-local sled action are required.";
            if (!IsActive || Environment.CurrentManagedThreadId != _thread || _insideMovementStep
                || Time.inFixedTimeStep || !_nativeGroundEnabled || _nativeCarrier == null
                || _sledAuthority != null || _swimActive || _swimAwaitingPhysics || !_nativeContactsReady
                || _pendingState.Sequence != 0 || readEnvironment == null || actionAllowed == null
                || !_bridge.IsGroundMovementMode || _nativeModeSuspended
                || (!entering && profile.PlayerColliderRadius != _nativeCapsule.Radius)
                || profile.PrimaryHeight != _nativeCapsule.CrouchingHeight
                || !new Vector3(0, profile.PrimaryCenterY, 0).Equals(_nativeCapsule.CrouchingCenter)) return false;
            bool mutated = false;
            try
            {
                if (!Current() || !actionAllowed() || !Current()
                    || !_swimGrounded || _nativeJumping || !SwimAlive(_swimCapsule.sharedMaterial)) return false;
                var environment = readEnvironment();
                if (!Current() || !PlayerSled.Valid(environment)) return false;
                if (entering && (profile.PlayerColliderRadius != PlayerSled.RidingRadius
                    || _nativeCapsule.CrouchingHeight < 2 * MathF.Max(_nativeCapsule.Radius, PlayerSled.RidingRadius)
                    || _swimProfile.ContactOffset >= PlayerSled.RidingRadius
                    || !NativeCapsuleClear(_swimCapsule.height, _swimCapsule.center, PlayerSled.RidingRadius)))
                { detail = "The native riding radius has no verified clearance."; return false; }
                _crouchedBeforeSled = _crouched;
                bool bound = entering
                    ? PlayerSledAuthority.TryEnter(_bridge, _nativeCarrier, sledTransform,
                        profile, _nativeCapsule, state, environment, out _sledAuthority, out detail)
                    : PlayerSledAuthority.TryBind(_bridge, _nativeCarrier, sledTransform,
                        profile, state, out _sledAuthority, out detail);
                mutated = _sledAuthority != null;
                if (!bound)
                {
                    if (mutated) Stop(detail);
                    return false;
                }
                _readSledEnvironment = readEnvironment;
                _sledAllowed = actionAllowed;
                _crouched = true;
                if (!entering) CopyNativeCapsule();
                UpdateNativeStepHelper(Vector2.zero, true);
                _staminaRunning = false;
                _sledSteeringInput = default;
                _sledUpdateFrame = -1;
                if (!Current()) throw new InvalidOperationException("Sled adoption lost the native carrier.");
                detail = entering ? "Sled entry routed on the native body; dispatch ordinary Update and retain original client actions."
                    : "Settled sled routed on the native body with actor-local dry/wet dynamics.";
                return true;
            }
            catch (Exception error)
            {
                detail = "Sled routing failed (" + error.GetType().Name + ").";
                if (mutated) Stop(detail);
                else DisposeSledRouting();
                return false;
            }
        }

        /// <summary>
        /// Call once per ordinary Update while Sled is selected. Absolute view angles
        /// are not steering deltas. Fixed stepping acknowledges only the same input
        /// identity actually integrated here, after its physics boundary completes.
        /// </summary>
        public bool TryUpdateSledSteering(out string detail)
        {
            detail = "Sled steering needs the current actor's ordinary Update boundary.";
            if (_disposed || Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep
                || _insideMovementStep || _swimAwaitingPhysics || _sledAuthority == null
                || _sledUpdateFrame == Time.frameCount || Time.deltaTime == 0) return false;
            try
            {
                if (!Current() || !_sledAllowed() || !Current())
                    throw new InvalidOperationException("Sled action authority ended.");
                if (_sledAuthority.Phase == PlayerSledPhase.Exited)
                { detail = "Complete the authenticated sled exit before resuming ground/swim."; return false; }
                var environment = _readSledEnvironment();
                if (!Current() || !PlayerSled.Valid(environment))
                    throw new InvalidOperationException("The actor's sled environment is unavailable.");
                bool riding = _sledAuthority.Phase is PlayerSledPhase.Entering or PlayerSledPhase.Riding;
                if (!riding && !SledExitCapsuleClear())
                    throw new InvalidOperationException("The approved sled exit capsule became obstructed.");
                var input = default(PlayerMotionInput);
                bool hasInput = riding && _bridge.TryPeekCurrentInput(out input);
                if (!Current() || !_sledAuthority.TryUpdate(hasInput ? input.Movement.x : 0, environment, out detail)
                    || !Current())
                    throw new InvalidOperationException(detail);
                _sledUpdateFrame = Time.frameCount;
                _sledSteeringInput = hasInput ? input : default;
                return true;
            }
            catch (Exception error)
            {
                detail = error is InvalidOperationException ? error.Message
                    : "Sled steering failed (" + error.GetType().Name + ").";
                Stop(detail);
                return false;
            }
        }

        /// <summary>
        /// Begin the server-body exit only after the authenticated action and authoritative
        /// animation permit it. StopRequested alone is not that boundary. Standing clearance
        /// falls back to a checked crouched capsule; no ground/swim forces resume here.
        /// </summary>
        public bool TryBeginSledExit(bool remainCrouched, out string detail)
        {
            detail = "Sled exit requires a drained step/state and the authoritative exit animation.";
            if (_disposed || Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep
                || _insideMovementStep || _swimAwaitingPhysics || _sledAuthority == null
                || _pendingState.Sequence != 0
                || _sledAuthority.Phase is PlayerSledPhase.Exiting or PlayerSledPhase.Exited) return false;
            try
            {
                if (!Current() || !_sledAllowed() || !Current()) return false;
                var environment = _readSledEnvironment();
                if (!Current() || !PlayerSled.Valid(environment)) return false;
                _sledExitCrouched = remainCrouched || !NativeCapsuleClear(_nativeCapsule.StandingHeight,
                    _nativeCapsule.StandingCenter);
                if (!SledExitCapsuleClear())
                { detail = "Neither the requested nor the crouched return capsule has clearance."; return false; }
                if (!_sledAuthority.TryBeginExit(_nativeCapsule, _sledExitCrouched, environment, out detail))
                {
                    if (_sledAuthority.IsRetired) Stop(detail);
                    return false;
                }
                _sledSteeringInput = default;
                _staminaRunning = false;
                if (!Current()) throw new InvalidOperationException("Sled exit lost its exact motor.");
                return true;
            }
            catch (Exception error)
            {
                detail = error is InvalidOperationException ? error.Message
                    : "Sled exit failed (" + error.GetType().Name + ").";
                Stop(detail);
                return false;
            }
        }

        public bool TryBeginSledExit(out string detail) => TryBeginSledExit(_crouchedBeforeSled, out detail);

        private bool SledExitCapsuleClear() => NativeCapsuleClear(
            _sledExitCrouched ? _nativeCapsule.CrouchingHeight : _nativeCapsule.StandingHeight,
            _sledExitCrouched ? _nativeCapsule.CrouchingCenter : _nativeCapsule.StandingCenter);

        /// <summary>
        /// Call only after the aggregate has completed the authenticated sled exit and
        /// drained its native callbacks. Restores this motor's geometry/material without
        /// replaying a local-player exit, altering the camera or discarding pending ACKs.
        /// </summary>
        public bool TryFinishSled(out string detail)
        {
            detail = "Complete the sled exit at a non-physics boundary with no pending movement state.";
            if (_disposed || Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep
                || _insideMovementStep || _swimAwaitingPhysics || _sledAuthority == null
                || _pendingState.Sequence != 0 || _sledAuthority.Phase != PlayerSledPhase.Exiting) return false;
            try
            {
                if (!Current() || !_groundModeAllowed() || !Current() || !SledExitCapsuleClear()) return false;
                if (!_sledAuthority.TryCompleteExit(out detail))
                {
                    if (_sledAuthority.IsRetired) Stop(detail);
                    return false;
                }
                DisposeSledRouting();
                _crouched = _sledExitCrouched;
                _nativeJumping = _swimJumping = !_swimGrounded;
                _nativeAirControl = 1;
                _nativeContactsReady = false;
                if (!Current()) throw new InvalidOperationException("Sled exit lost the native carrier.");
                if (_animation != null) _animation = new PlayerAnimationGround(_bridge);
                detail = "Native ground/swim routing resumed after the aggregate's completed sled exit.";
                return true;
            }
            catch (Exception error)
            {
                detail = "Sled routing exit failed (" + error.GetType().Name + ").";
                Stop(detail);
                return false;
            }
        }

        private bool RouteSled(bool hasInput, PlayerMotionInput input, float dt)
        {
            if (_sledAuthority == null) return false;
            if (_sledAuthority.IsRetired || !_sledAllowed() || !Current())
                throw new InvalidOperationException("Sled routing lost its authenticated action authority.");
            var environment = _readSledEnvironment();
            if (!Current() || !PlayerSled.Valid(environment))
                throw new InvalidOperationException("The actor's sled environment is unavailable.");
            UpdateNativeStepHelper(Vector2.zero, true);
            _swimStepPosition = _swimBody.position;
            bool riding = _sledAuthority.Phase is PlayerSledPhase.Entering or PlayerSledPhase.Riding;
            if (_sledAuthority.Phase == PlayerSledPhase.Exited)
                throw new InvalidOperationException("An exited sled must be finalized before the next physics boundary.");
            if (riding && (!_sledAuthority.TryFixedStep(environment, out var detail) || !Current()))
                throw new InvalidOperationException(detail);
            _staminaRunning = false;
            _swimStepState = new(PlayerMovementMode.Sled, false, false, false,
                _swimGrounded, false, _swimStepPosition, _swimBody.velocity, null, Vector3.zero,
                _swimCurrentSpeed, Time.frameCount, Time.fixedTimeAsDouble);
            _swimStepInput = riding && hasInput && input.Sequence > _lastInput
                && input.Sequence == _sledSteeringInput.Sequence ? input : default;
            _swimStepDelta = dt;
            _swimAwaitingPhysics = true;
            return true;
        }

        /// <summary>Actor-local mode observation for presentation or a coordinated pause, not a wire ACK.</summary>
        public bool TryReadSledState(out PlayerSledState state)
        {
            state = default;
            if (_disposed || Environment.CurrentManagedThreadId != _thread) return false;
            try { return Current() && _sledAuthority != null && _sledAuthority.TryCaptureState(out state); }
            catch { return false; }
        }

        /// <summary>Local phase/capsule progress, not a lifecycle receipt or migration snapshot.</summary>
        public bool TryReadSledMotorState(out PlayerSledMotorState state)
        {
            state = default;
            if (_disposed || Environment.CurrentManagedThreadId != _thread) return false;
            try { return Current() && _sledAuthority != null && _sledAuthority.TryReadMotorState(out state); }
            catch { return false; }
        }

        /// <summary>Route only an authenticated stop action; this latches native force-stop without exiting.</summary>
        public bool TryRequestSledStop()
        {
            if (_disposed || Environment.CurrentManagedThreadId != _thread) return false;
            try { return Current() && _sledAuthority != null && _sledAllowed() && Current() && _sledAuthority.TryRequestStop(); }
            catch { return false; }
        }

        private bool SupportedModeAllowed() => _sledAuthority != null ? _sledAllowed() : _groundModeAllowed();

        private void DisposeSledRouting()
        {
            _sledAuthority?.Retire();
            _sledAuthority = null;
            _readSledEnvironment = null;
            _sledAllowed = null;
            _sledSteeringInput = default;
            _sledUpdateFrame = -1;
        }

        public static bool TryStartNative(PlayerBridge bridge,
            PlayerCarrier carrier, PlayerMovementModeSettings waterSettings,
            Func<bool> supportedModesAllowed,
            Func<PlayerNativeGroundDynamics> readGroundDynamics,
            Func<PlayerSwimDynamics> readSwimDynamics, PlayerStats serverStats,
            PlayerStaminaProfile staminaProfile,
            Func<PlayerStaminaConditions> readStaminaConditions,
            out PlayerGroundMovement movement, out string detail)
            => TryStartNative(bridge, carrier, waterSettings, supportedModesAllowed,
                readGroundDynamics, readSwimDynamics, serverStats, staminaProfile,
                readStaminaConditions, out movement, out detail, null, null);

        /// <summary>
        /// Adopt the prepared carrier's held body, capsule and contacts at a settled dry boundary.
        /// Creates no Rigidbody or CharacterController. The server adapter must approve the
        /// captured profile against the frozen snapshot and own live dynamics/permissions.
        /// This method activates carrier physics; do not activate it separately. Movement owns
        /// the contact tick and one collision receiver, not the borrowed components' lifetime.
        /// The first fixed step resolves server contacts without consuming/acknowledging input.
        /// Dispose movement before disposing the carrier. A stopped motor holds borrowed
        /// physics closed; the aggregate must retire that carrier, not resume it independently.
        /// Competing root/body, step-helper, action and survival writers must be quiescent.
        /// An optional native step helper must already be server-approved, disabled and
        /// attached directly below this carrier root. Its collider is borrowed, not created
        /// from the primary capsule profile. The caller retains its lifetime ownership.
        /// </summary>
        public static bool TryStartNative(PlayerBridge bridge,
            PlayerCarrier carrier, PlayerMovementModeSettings waterSettings,
            Func<bool> supportedModesAllowed,
            Func<PlayerNativeGroundDynamics> readGroundDynamics,
            Func<PlayerSwimDynamics> readSwimDynamics, PlayerStats serverStats,
            PlayerStaminaProfile staminaProfile,
            Func<PlayerStaminaConditions> readStaminaConditions,
            out PlayerGroundMovement movement, out string detail,
            StepHelper stepHelper, Func<bool> stepHelperAllowed)
        {
            movement = null;
            detail = "Native movement startup requires an active ground gameplay bridge.";
            if (bridge?.IsGroundMovementMode != true) return false;
            if (!TryPrepare(bridge, carrier, waterSettings, supportedModesAllowed, readGroundDynamics,
                readSwimDynamics, serverStats, staminaProfile, readStaminaConditions,
                out var prepared, out detail, stepHelper, stepHelperAllowed)) return false;
            if (!prepared.TryActivate(out detail)) { prepared.Dispose(); return false; }
            movement = prepared;
            return true;
        }

        /// <summary>
        /// Constructs the native motor on an exact prepared or completed server bridge.
        /// Keeps physics held and stamina timers frozen. The aggregate must complete every
        /// domain, activate the bridge, then call TryActivate on this same motor before
        /// another game-loop boundary. Dispose before the carrier on failure or retirement.
        /// </summary>
        public static bool TryPrepare(PlayerBridge bridge,
            PlayerCarrier carrier, PlayerMovementModeSettings waterSettings,
            Func<bool> supportedModesAllowed,
            Func<PlayerNativeGroundDynamics> readGroundDynamics,
            Func<PlayerSwimDynamics> readSwimDynamics, PlayerStats serverStats,
            PlayerStaminaProfile staminaProfile,
            Func<PlayerStaminaConditions> readStaminaConditions,
            out PlayerGroundMovement movement, out string detail,
            StepHelper stepHelper = null, Func<bool> stepHelperAllowed = null,
            PlayerWaterContactAuthority.ServerContinuation waterContacts = null)
        {
            movement = null;
            detail = "An exact server bridge, held carrier and approved settled-dry native profile are required.";
            if (!PlayerRoles.IsAvailable || !GameBuild.IsRecognized || bridge?.IsServer != true
                || (bridge.IsGameplayActive && !bridge.IsGroundMovementMode)
                || carrier == null || carrier.IsDisposed || carrier.IsPhysicsActive
                || supportedModesAllowed == null || readGroundDynamics == null || readSwimDynamics == null
                || (stepHelper == null) != (stepHelperAllowed == null)
                || readStaminaConditions == null || Time.inFixedTimeStep || !Physics.autoSimulation
                || !bridge.TryReadCurrentTarget(out var entity)) return false;
            PlayerGroundMovement candidate = null;
            PlayerMovementModes modes = null;
            try
            {
                if (!SwimAlive(carrier.Entity) || carrier.Entity.Pointer != entity.Pointer
                    || carrier.ReadReadiness(out detail) != BoltMigrationAdmission.Ready
                    || !PlayerRoles.IsCurrentCarrier(entity) || !supportedModesAllowed()
                    || entity.GetComponentsInChildren<Rigidbody>(true).Length != 1
                    || entity.GetComponentsInChildren<CharacterController>(true).Length != 0
                    || entity.GetComponentsInChildren<FirstPersonCharacter>(true).Length != 0
                    || entity.GetComponentsInChildren<RigidBodyCollisionFlags>(true).Length != 1
                    || !SwimAlive(carrier.Body) || !SwimAlive(carrier.PrimaryCollider) || !SwimAlive(carrier.Contacts)) return false;
                foreach (var collider in entity.GetComponentsInChildren<Collider>(true))
                    if (collider != null && !collider.isTrigger && collider.Pointer != carrier.PrimaryCollider.Pointer)
                    {
                        if (!SwimAlive(stepHelper) || !SwimAlive(stepHelper._targetCollider)
                            || collider.Pointer != stepHelper._targetCollider.Pointer)
                        { detail = "The native carrier has an additional solid collision writer."; return false; }
                    }
                var profile = carrier.Profile;
                var geometry = profile.Geometry;
                var body = profile.Body;
                var contacts = profile.Contacts;
                var capsule = new PlayerNativeCapsuleProfile(geometry.Radius,
                    geometry.StandingHeight, geometry.StandingCenter, geometry.CrouchingHeight,
                    new Vector3(0, geometry.CrouchingCenterY, 0));
                var bodyProfile = new PlayerSwimBodyProfile(body.Mass, body.Drag, body.AngularDrag,
                    body.UseGravity, body.CollisionDetection, body.SolverIterations, body.SolverVelocityIterations,
                    body.MaximumDepenetrationVelocity, body.MaximumLinearVelocity, geometry.ContactOffset,
                    carrier.PrimaryCollider.sharedMaterial, contacts.HeightOffset,
                    contacts.GroundAngleMaximumConstruction, contacts.SmoothedLayerMask);
                if (!PlayerCarrierProfile.ValidateProfile(profile, out detail)
                    || !ValidNativeCapsule(capsule) || !ValidSwimProfile(bodyProfile)
                    || bodyProfile.ContactOffset >= capsule.Radius || body.IsKinematic || !body.DetectCollisions
                    || !geometry.Enabled || geometry.IsTrigger || !profile.Grounded || profile.Observations.IsJumping
                    || (body.Constraints & RigidbodyConstraints.FreezePosition) != 0
                    || profile.Motion.FixedDeltaTime != Time.fixedDeltaTime) return false;
                foreach (var active in Active)
                    if (!active._disposed && active._entity.Pointer == entity.Pointer) return false;
                candidate = new(bridge, entity, default, supportedModesAllowed)
                {
                    _nativeGroundEnabled = true, _nativeCapsule = capsule, _swimProfile = bodyProfile,
                    _nativeCarrier = carrier, _swimBody = carrier.Body, _swimCapsule = carrier.PrimaryCollider,
                    _swimContacts = carrier.Contacts, _velocity = profile.Velocity, _crouched = profile.Crouched,
                    _nativeCapsuleTransform = carrier.PrimaryCollider.transform,
                    _nativeCapsuleObject = carrier.PrimaryCollider.gameObject,
                    _readNativeGroundDynamics = readGroundDynamics, _readSwimDynamics = readSwimDynamics,
                    _waterSettings = waterSettings, _waterContacts = waterContacts
                };
                Active.Add(candidate);
                bridge.Retired += candidate.Dispose;
                if (!candidate.TryBindNativeStepHelper(stepHelper, stepHelperAllowed, out detail)) return false;
                if (!candidate.CarrierCurrent() || !candidate.NativeCapsuleCurrent()
                    || !candidate.BorrowedCarrierCurrent(false) || !candidate.SwimNativeProxiesQuiescent()) return false;
                var ground = readGroundDynamics();
                var swim = readSwimDynamics();
                if (!candidate.CarrierCurrent() || !ValidNativeGround(ground)
                    || !PlayerMovementModes.ValidDynamics(swim)
                    || swim.SwimmingJumpTimerRunning || swim.WaterJump is { IsJumping: true } or { BlockWaterJump: true }) return false;
                if (!candidate.NativeCapsuleClear(profile.Crouched ? capsule.CrouchingHeight : capsule.StandingHeight,
                    profile.Crouched ? capsule.CrouchingCenter : capsule.StandingCenter))
                { detail = "The exact native carrier capsule is obstructed or the overlap query is incomplete."; return false; }
                if (!candidate.TryCreateWaterModes(true, out modes, out detail)) return false;
                candidate._movementModes = modes;
                candidate._swimCurrentSpeed = ground.CurrentSpeed;
                candidate._swimWaterJumpsEnabled = swim.WaterJump.HasValue;
                candidate.CreateSwimReceiver();
                if (!modes.TryBindSwimBody(candidate._swimBody, out detail, candidate._nativeStepCollider))
                    throw new InvalidOperationException(detail);
                candidate._swimGrounded = profile.Grounded;
                if (!candidate.Current() || !candidate.TryEnableStaminaAuthority(serverStats, staminaProfile,
                    readStaminaConditions, out detail)) throw new InvalidOperationException(detail);
                if (!candidate.Current() || !supportedModesAllowed() || !candidate.Current())
                    throw new InvalidOperationException("Carrier changed during native motor preparation.");
                movement = candidate;
                detail = "Native ground/swim motor and actor-local stamina prepared; physics and timers remain held.";
                return true;
            }
            catch (Exception error)
            {
                modes?.Dispose();
                candidate?.Stop(error is InvalidOperationException ? error.Message
                    : "Native movement preparation failed (" + error.GetType().Name + ").");
                detail = candidate?.StopReason ?? "The native carrier is no longer available.";
                return false;
            }
            finally
            {
                if (movement == null) { modes?.Dispose(); candidate?.Dispose(); }
            }
        }

        private bool BorrowedCarrierCurrent(bool active)
        {
            if (_nativeCarrier == null) return true;
            var carrier = _nativeCarrier;
            if (!carrier.IsCurrentForMotor || carrier.IsPhysicsActive != active
                || !SwimAlive(carrier.Entity) || carrier.Entity.Pointer != _entity.Pointer
                || carrier.Body?.Pointer != _swimBody.Pointer || carrier.PrimaryCollider?.Pointer != _swimCapsule.Pointer
                || carrier.Contacts?.Pointer != _swimContacts.Pointer
                || !SwimAlive(_nativeCapsuleTransform) || !SwimAlive(_nativeCapsuleObject)) return false;
            var profile = carrier.Profile;
            var body = profile.Body;
            var capsuleTransform = _nativeCapsuleTransform;
            return _swimBody.constraints == body.Constraints && _swimBody.interpolation == body.Interpolation
                && _swimBody.sleepThreshold == body.SleepThreshold && _swimBody.maxAngularVelocity == body.MaximumAngularVelocity
                && capsuleTransform.IsChildOf(_transform) && UnitScale(capsuleTransform.lossyScale)
                && Upright(capsuleTransform.up) && _nativeCapsuleObject.layer == profile.CapsuleLayer
                && (_transform.InverseTransformPoint(capsuleTransform.position) - profile.Geometry.LocalPosition).sqrMagnitude < 0.00000001f
                && Quaternion.Angle(Quaternion.Inverse(_transform.rotation) * capsuleTransform.rotation,
                    profile.Geometry.LocalRotation) < 0.01f
                && Time.fixedDeltaTime == profile.Motion.FixedDeltaTime;
        }

        /// <summary>
        /// At a dry, settled non-physics boundary, change the already enrolled Ground/Swim
        /// motor to one continuous Rigidbody. The adapter must supply exact native geometry,
        /// own all action/animation decisions and exclude unported step-helper/special modes.
        /// The retained client's FPC, collision body, controls and cameras are unchanged.
        /// Requires TryEnableSwimming and TryEnableStaminaAuthority first. No new wire input.
        /// </summary>
        public bool TryEnableNativeGround(PlayerNativeCapsuleProfile capsule,
            Func<PlayerNativeGroundDynamics> readServerDynamics, out string detail)
        {
            detail = "A settled dry Ground/Swim motor, authoritative stamina and exact native profile are required.";
            if (_disposed || Environment.CurrentManagedThreadId != _thread || _insideMovementStep
                || Time.inFixedTimeStep || _swimActive || _swimAwaitingPhysics || _nativeGroundEnabled
                || _movementModes == null || _staminaVitals == null || _pendingState.Sequence != 0
                || readServerDynamics == null || !GameBuild.IsRecognized
                || !ValidNativeCapsule(capsule) || _swimProfile.ContactOffset >= capsule.Radius) return false;
            bool mutated = false;
            try
            {
                if (!Current() || !_groundModeAllowed() || !Current() || !StaminaCurrent()) return false;
                var dynamics = readServerDynamics();
                if (!Current() || !ValidNativeGround(dynamics)) return false;
                _nativeCapsule = capsule;
                if (!NativeCapsuleClear(_crouched ? capsule.CrouchingHeight : capsule.StandingHeight,
                    _crouched ? capsule.CrouchingCenter : capsule.StandingCenter)) return false;
                bool grounded = _controller.isGrounded;
                mutated = true;
                EnterSwimBody(grounded);
                _nativeGroundEnabled = true;
                _swimActive = false;
                _readNativeGroundDynamics = readServerDynamics;
                _swimCurrentSpeed = dynamics.CurrentSpeed;
                CopyNativeCapsule();
                if (!Current()) throw new InvalidOperationException("Native ground body ownership changed.");
                detail = "Native dry-ground/air forces enabled on the shared body; special actions and step-helper remain adapter-owned.";
                return true;
            }
            catch (Exception error)
            {
                detail = "Native ground enrollment failed (" + error.GetType().Name + ").";
                if (mutated) Stop(detail);
                return false;
            }
        }

        private void StepNativeGround(bool hasInput, PlayerMotionInput input, float dt)
        {
            var d = _readNativeGroundDynamics();
            if (!Current() || !ValidNativeGround(d) || !StaminaCurrent())
                throw new InvalidOperationException("The actor's native ground dynamics or stamina binding changed.");
            var buttons = hasInput ? input.MoveButtons : PlayerMoveButtons.None;
            bool crouch = hasInput ? (buttons & PlayerMoveButtons.Crouch) != 0 : _crouched;
            if (crouch != _crouched && (crouch || NativeCapsuleClear(_nativeCapsule.StandingHeight, _nativeCapsule.StandingCenter)))
            {
                _crouched = crouch;
                CopyNativeCapsule();
            }
            if (hasInput) _swimBody.rotation = Quaternion.Euler(0, input.ViewYaw, 0);
            _transform.rotation = _swimBody.rotation;
            Vector3 local = hasInput && !d.InputBlocked ? new(input.Movement.x, 0, input.Movement.y) : Vector3.zero;
            if (local.magnitude < 0.1f) local = Vector3.zero;
            local = Vector3.ClampMagnitude(local, 1.1f);
            UpdateNativeStepHelper(hasInput && !d.InputBlocked ? input.Movement : Vector2.zero);
            Vector3 velocity = _swimBody.velocity;
            Vector3 target = _transform.TransformDirection(local) * _swimCurrentSpeed;
            bool grounded = _swimGrounded;
            if (grounded && _nativeGroundStep - _nativeJumpStep > 2) _nativeJumping = false;
            bool jump = hasInput && input.Sequence > _lastInput && NewJumpIntent(input)
                && grounded && !_nativeJumping && d.CanJump && !_crouched && !d.InputBlocked;
            if (!Finite(velocity) || !float.IsFinite(velocity.sqrMagnitude) || !Finite(target))
                throw new InvalidOperationException("Native ground velocity is invalid.");
            if (jump)
            {
                // OnJumpInput precedes FixedUpdate; its slope blend also reduces lateral jump velocity.
                float slide = Mathf.Clamp01((_swimContacts.GroundAngleVal - d.SlideStartAngle)
                    / (d.SlideEndAngle - d.SlideStartAngle));
                float factor = d.OnSlipperySlope ? 0.1f : 1f - slide * 0.7f;
                if (d.JumpMultiplier > 1) factor = d.JumpMultiplier;
                float vertical = MathF.Sqrt(2f * d.JumpHeight * factor * d.Gravity);
                velocity = Vector3.Slerp(new Vector3(velocity.x, vertical, velocity.z), new Vector3(0, vertical, 0), slide);
                if (!Finite(velocity)) throw new InvalidOperationException("Native jump speed is invalid.");
                ChargeOrdinaryJump();
                _swimBody.velocity = velocity;
                _nativeJumping = true;
                _nativeJumpStep = _nativeGroundStep;
            }
            // DetermineVelocityChange 182F84E90: independent world-axis clamps, not MoveTowards.
            Vector3 change = target - velocity;
            change.x = Mathf.Clamp(change.x, -d.MaximumVelocityChange, d.MaximumVelocityChange) * d.InputClamp;
            change.y = 0;
            change.z = Mathf.Clamp(change.z, -d.MaximumVelocityChange, d.MaximumVelocityChange) * d.InputClamp;
            if (!Finite(change))
                throw new InvalidOperationException("Native ground velocity is invalid.");

            _swimStepPosition = _swimBody.position;
            _swimBody.useGravity = _swimProfile.UseGravity;
            // Ordinary FPC FixedUpdate resets these before steering; captured special drag is not a dry default.
            _swimProfile = _swimProfile with { Drag = 0, AngularDrag = 0.05f };
            _swimBody.drag = _swimProfile.Drag;
            _swimBody.angularDrag = _swimProfile.AngularDrag;
            if (grounded)
            {
                if (!PlayerFlightZipline.TryApplyGroundExitForces(_bridge, _swimBody, change))
                    _swimBody.AddForce(change, ForceMode.Acceleration);
                _swimBody.velocity = Vector3.ClampMagnitude(velocity, d.MaximumVelocity);
                if (local.magnitude > 0.05f && d.CanJump && !_nativeJumping)
                    _swimBody.AddForce(new Vector3(0, -_swimBody.velocity.magnitude * _swimBody.mass * d.GroundStickiness, 0), ForceMode.Force);
            }
            else _nativeJumping = true;
            if (_nativeJumping)
            {
                // HandleJumpSpeed 182F85A40: two Slerps, with the decaying factor between them.
                _nativeAirControl = Mathf.Lerp(_nativeAirControl, 0, dt);
                Vector3 air = Vector3.Slerp(Vector3.zero, change * 0.9f, dt * 10f);
                air.x *= _nativeAirControl;
                air.z *= _nativeAirControl;
                air = Vector3.Slerp(air, change * 0.9f, dt * 10f);
                air.y = change.y;
                _swimBody.AddForce(air, ForceMode.Acceleration);
                _swimBody.velocity = Vector3.ClampMagnitude(_swimBody.velocity, d.MaximumVelocity);
            }
            else _nativeAirControl = 1;
            _swimJumping = _nativeJumping;
            bool running = ApplyRunningStamina(hasInput && (buttons & PlayerMoveButtons.Sprint) != 0,
                d.CanRun, d.OverallSpeed, d.WalkToRunThreshold, dt);
            // Speed selection follows steering, so the next fixed step uses this _speed.
            if (running)
            {
                float reverse = Mathf.Clamp01(Vector3.Dot(target.normalized, -_transform.forward) + 0.6f);
                _swimCurrentSpeed = _crouched ? d.CrouchSpeed * 1.75f
                    : Mathf.Lerp(d.RunSpeed, Mathf.Max(d.BackwardSpeedMultiplier * d.RunSpeed, d.WalkSpeed), reverse);
            }
            else _swimCurrentSpeed = _crouched ? d.CrouchSpeed : d.WalkSpeed;
            if (!d.DisableGravity)
                _swimBody.AddForce(new Vector3(0, -d.Gravity * _swimBody.mass, 0), ForceMode.Force);
            if (!Current()) throw new InvalidOperationException("Native ground force ownership ended.");
            _bodyStepMove = new Vector2(local.x, local.z);
            _swimStepState = new(PlayerMovementMode.Ground, false, false, false,
                grounded, _nativeJumping, _swimStepPosition, velocity, null, Vector3.zero,
                _swimCurrentSpeed, Time.frameCount, Time.fixedTimeAsDouble);
            _swimStepInput = hasInput && input.Sequence > _lastInput ? input : default;
            _swimStepDelta = dt;
            _swimAwaitingPhysics = true;
        }

        private void CopyNativeCapsule()
        {
            _swimCapsule.radius = _nativeCapsule.Radius;
            _swimCapsule.height = _crouched ? _nativeCapsule.CrouchingHeight : _nativeCapsule.StandingHeight;
            _swimCapsule.center = _crouched ? _nativeCapsule.CrouchingCenter : _nativeCapsule.StandingCenter;
        }

        private bool NativeCapsuleCurrent() => _sledAuthority != null ? _sledAuthority.IsCurrentForMotor
            : _swimCapsule.radius == _nativeCapsule.Radius
            && _swimCapsule.height == (_crouched ? _nativeCapsule.CrouchingHeight : _nativeCapsule.StandingHeight)
            && _swimCapsule.center.Equals(_crouched ? _nativeCapsule.CrouchingCenter : _nativeCapsule.StandingCenter);

        private bool NativeCapsuleClear(float height, Vector3 center)
            => NativeCapsuleClear(height, center, _nativeCapsule.Radius);

        private bool NativeCapsuleClear(float height, Vector3 center, float radius)
        {
            var capsuleTransform = _nativeCapsuleTransform ?? _transform;
            Vector3 worldCenter = capsuleTransform.TransformPoint(center);
            Vector3 extent = Vector3.up * (height * 0.5f - radius);
            int mask = 0;
            int capsuleLayer = _nativeCapsuleObject != null ? _nativeCapsuleObject.layer : _layer;
            for (int layer = 0; layer < 32; layer++)
                if (!Physics.GetIgnoreLayerCollision(capsuleLayer, layer)) mask |= 1 << layer;
            if (!Physics.autoSyncTransforms) Physics.SyncTransforms();
            try
            {
                // Preserve the native contact offset instead of substituting CC skin width.
                int count = Physics.defaultPhysicsScene.OverlapCapsule(worldCenter - extent, worldCenter + extent,
                    radius - _swimProfile.ContactOffset,
                    _overlaps, mask, QueryTriggerInteraction.Ignore);
                if (count < 0 || count >= _overlaps.Length) return false;
                for (int i = 0; i < count; i++)
                {
                    var collider = _overlaps[i];
                    if (!SwimAlive(collider) || (collider.Pointer != _controller?.Pointer
                        && collider.Pointer != _swimCapsule?.Pointer
                        && collider.Pointer != _nativeStepCollider?.Pointer)) return false;
                }
                return true;
            }
            finally { for (int i = 0; i < _overlaps.Length; i++) _overlaps[i] = null; }
        }

        private bool TryBindNativeStepHelper(StepHelper helper, Func<bool> allowed, out string detail)
        {
            detail = "A step helper must have one disabled, actor-approved capsule on the same carrier body.";
            if (helper is null) return allowed == null;
            if (!SwimAlive(helper) || helper.enabled || allowed == null) return false;
            var collider = helper._targetCollider;
            var transform = helper.transform;
            if (!SwimAlive(collider) || !SwimAlive(transform) || collider.enabled || collider.isTrigger
                || collider.Pointer == _swimCapsule.Pointer || collider.attachedRigidbody?.Pointer != _swimBody.Pointer
                || collider.transform.Pointer != transform.Pointer || transform.parent?.Pointer != _transform.Pointer
                || !helper.gameObject.activeInHierarchy || !UnitScale(transform.localScale)
                || !Range(collider.radius, 0.01f, 5) || !Range(collider.height, collider.radius * 2, 10)
                || collider.direction is < 0 or > 2 || !Finite(collider.center)
                || !Range(collider.contactOffset, 0.0001f, collider.radius)
                || !Finite(helper._movementOffset) || !Finite(helper._rotationOffset)
                || !Range(helper._maxMagnitude, -1, 100) || helper._maxMagnitude == 0
                || !Finite(transform.localPosition)
                || !float.IsFinite(helper._inputMovement.x) || !float.IsFinite(helper._inputMovement.y)) return false;
            _nativeStepHelper = helper;
            _nativeStepCollider = collider;
            _nativeStepTransform = transform;
            _stepHelperAllowed = allowed;
            _nativeStepId = helper.GetInstanceID();
            _nativeStepColliderId = collider.GetInstanceID();
            _nativeStepLayer = collider.gameObject.layer;
            _nativeStepDirection = collider.direction;
            _nativeStepRadius = collider.radius;
            _nativeStepHeight = collider.height;
            _nativeStepCenter = collider.center;
            _nativeStepContactOffset = collider.contactOffset;
            _nativeStepMaterial = collider.sharedMaterial;
            _nativeStepOffset = helper._movementOffset;
            _nativeStepRotationOffset = helper._rotationOffset;
            _nativeStepPosition = transform.localPosition;
            _nativeStepRotation = transform.localRotation;
            _nativeStepMagnitude = helper._maxMagnitude;
            _nativeStepInput = helper._inputMovement;
            return NativeStepHelperCurrent();
        }

        private bool NativeStepHelperCurrent()
        {
            if (_nativeStepHelper is null) return _nativeStepCollider is null;
            return SwimAlive(_nativeStepHelper) && SwimAlive(_nativeStepCollider) && SwimAlive(_nativeStepTransform)
                && _nativeStepHelper.GetInstanceID() == _nativeStepId && !_nativeStepHelper.enabled
                && _nativeStepCollider.GetInstanceID() == _nativeStepColliderId
                && _nativeStepHelper._targetCollider?.Pointer == _nativeStepCollider.Pointer
                && _nativeStepHelper.transform.Pointer == _nativeStepTransform.Pointer
                && _nativeStepCollider.transform.Pointer == _nativeStepTransform.Pointer
                && _nativeStepTransform.parent?.Pointer == _transform.Pointer
                && _nativeStepHelper.gameObject.activeInHierarchy && UnitScale(_nativeStepTransform.localScale)
                && _nativeStepCollider.attachedRigidbody?.Pointer == _swimBody.Pointer
                && _nativeStepCollider.enabled == _nativeStepEnabled && !_nativeStepCollider.isTrigger
                && _nativeStepCollider.direction == _nativeStepDirection
                && _nativeStepCollider.gameObject.layer == _nativeStepLayer
                && _nativeStepCollider.radius == _nativeStepRadius && _nativeStepCollider.height == _nativeStepHeight
                && _nativeStepCollider.center.Equals(_nativeStepCenter)
                && _nativeStepCollider.contactOffset == _nativeStepContactOffset
                && _nativeStepCollider.sharedMaterial == _nativeStepMaterial
                && _nativeStepHelper._movementOffset.Equals(_nativeStepOffset)
                && _nativeStepHelper._rotationOffset.Equals(_nativeStepRotationOffset)
                && _nativeStepHelper._maxMagnitude == _nativeStepMagnitude
                && _nativeStepHelper._inputMovement.Equals(_nativeStepInput)
                && _nativeStepTransform.localPosition.Equals(_nativeStepPosition)
                && Quaternion.Angle(_nativeStepTransform.localRotation, _nativeStepRotation) < 0.01f;
        }

        private void UpdateNativeStepHelper(Vector2 input, bool forceDisabled = false)
        {
            if (_nativeStepHelper is null) return;
            bool enabled = !forceDisabled && _stepHelperAllowed();
            if (!Current() || !NativeStepHelperCurrent())
                throw new InvalidOperationException("Native step-helper ownership changed.");
            // FPC 182F81220 forwards actor input; StepHelper 182E77B80 touches only
            // its own fields/transform. 182F834D0 gates the collider on the primary.
            _nativeStepHelper.UpdateInputMovement(input);
            _nativeStepInput = input;
            _nativeStepMagnitude = _nativeStepHelper._maxMagnitude;
            _nativeStepPosition = _nativeStepTransform.localPosition;
            _nativeStepRotation = _nativeStepTransform.localRotation;
            _nativeStepEnabled = enabled && _swimCapsule.enabled;
            _nativeStepHelper.SetColliderEnabled(_nativeStepEnabled);
            if (!Finite(_nativeStepPosition) || !Range(_nativeStepMagnitude, 0.0001f, 100)
                || !Current() || !NativeStepHelperCurrent())
                throw new InvalidOperationException("Native step-helper input application lost its actor.");
        }

        private void DisposeNativeStepHelper()
        {
            try { if (SwimAlive(_nativeStepCollider)) _nativeStepCollider.enabled = false; }
            catch (Exception error) { StopReason ??= "Step-helper hold unconfirmed (" + error.GetType().Name + ")."; }
            _nativeStepHelper = null;
            _nativeStepCollider = null;
            _nativeStepTransform = null;
            _stepHelperAllowed = null;
            _nativeStepMaterial = null;
            _nativeStepEnabled = false;
        }

        private static bool ValidNativeCapsule(PlayerNativeCapsuleProfile p)
            => Range(p.Radius, 0.05f, 2) && Range(p.StandingHeight, p.Radius * 2, 5)
                && Range(p.CrouchingHeight, p.Radius * 2, p.StandingHeight)
                && Finite(p.StandingCenter) && Finite(p.CrouchingCenter)
                && p.StandingCenter.sqrMagnitude <= 100 && p.CrouchingCenter.sqrMagnitude <= 100;

        private static bool ValidNativeGround(PlayerNativeGroundDynamics d)
            => Range(d.CurrentSpeed, 0, 100) && Range(d.WalkSpeed, 0, 100) && Range(d.RunSpeed, 0, 100)
                && Range(d.CrouchSpeed, 0, 100) && Range(d.BackwardSpeedMultiplier, 0, 10)
                && Range(d.MaximumVelocityChange, 0, 1000) && Range(d.InputClamp, 0, 100)
                && Range(d.MaximumVelocity, 0.01f, 1000) && Range(d.Gravity, 0, 1000)
                && Range(d.JumpHeight, 0, 100) && Range(d.JumpMultiplier, 0, 100)
                && Range(d.GroundStickiness, 0, 1000) && Range(d.SlideStartAngle, 0, 180)
                && Range(d.SlideEndAngle, 0, 180) && d.SlideEndAngle > d.SlideStartAngle
                && Range(d.OverallSpeed, 0, 1000) && Range(d.WalkToRunThreshold, 0, 1000);
    }
}
