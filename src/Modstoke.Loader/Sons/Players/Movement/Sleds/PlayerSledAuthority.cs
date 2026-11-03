// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Loader;
using global::Sons.Animation;
using UnityEngine;
using static Modstoke.Sons.PlayerSled;

namespace Modstoke.Sons
{
    /// <summary>
    /// Actor-local riding physics and capsule transitions on one existing server carrier.
    /// The aggregate must exclusively route movement to this writer and supply trusted per-actor environment
    /// observations. Native presentation, collision callbacks, action admission, retained
    /// client controls and reliable cross-peer transition publication remain external.
    /// No LocalPlayer method or coroutine is invoked and no client camera is changed.
    /// </summary>
    public sealed class PlayerSledAuthority
    {
        private static readonly Dictionary<IntPtr, PlayerSledAuthority> Writers = new();
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerBridge _bridge;
        private readonly PlayerCarrier _carrier;
        private readonly BoltEntity _entity;
        private readonly Rigidbody _body;
        private readonly CapsuleCollider _capsule;
        private readonly Transform _root, _sled;
        private readonly PhysicMaterial _material;
        private readonly PlayerSledProfile _profile;
        private readonly int _entityId, _sledId;
        private double _lastPhysicsTime = double.NaN;
        private double _lastSteeringTime = double.NaN;
        private float _stopDeadline;
        private float _expectedRadius, _capsuleProgress;
        private float _expectedHeight;
        private Vector3 _expectedCenter;
        private bool _tracksGeometry;
        private float _standingHeight, _standingCenterY;
        private double _lastTransitionTime = double.NaN;
        private readonly float _staticFriction, _dynamicFriction, _bounciness;
        private readonly PhysicMaterialCombine _frictionCombine, _bounceCombine;
        private bool _retired;

        private PlayerSledAuthority(PlayerBridge bridge,
            PlayerCarrier carrier, Transform sledTransform,
            PlayerSledProfile profile, PlayerSledState state)
        {
            _bridge = bridge;
            _carrier = carrier;
            _entity = carrier.Entity;
            _entityId = _entity.GetInstanceID();
            _body = carrier.Body;
            _capsule = carrier.PrimaryCollider;
            _material = _capsule.sharedMaterial;
            _expectedRadius = _capsule.radius;
            _expectedHeight = _capsule.height;
            _expectedCenter = _capsule.center;
            _capsuleProgress = 1;
            _staticFriction = _material.staticFriction;
            _dynamicFriction = _material.dynamicFriction;
            _bounciness = _material.bounciness;
            _frictionCombine = _material.frictionCombine;
            _bounceCombine = _material.bounceCombine;
            _root = _body.transform;
            _sled = sledTransform;
            _sledId = sledTransform.GetInstanceID();
            _profile = profile;
            State = state;
            _stopDeadline = Time.time + state.StopTimeRemaining;
        }

        public PlayerSledState State { get; private set; }
        public PlayerSledPhase Phase { get; private set; } = PlayerSledPhase.Riding;
        public bool StopRequested { get; private set; }
        public bool IsRetired => _retired;
        public string FailureReason { get; private set; }
        /// <summary>Use instead of the ground capsule check while this motor owns its changing geometry.</summary>
        public bool IsCurrentForMotor
        {
            get
            {
                if (_retired || Environment.CurrentManagedThreadId != _thread) return false;
                try
                {
                    return Current() && (_tracksGeometry || (_capsule.height == _profile.PrimaryHeight
                        && _capsule.center.Equals(new Vector3(0, _profile.PrimaryCenterY, 0))));
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// Bind only after the aggregate has admitted a settled riding state, authenticated
        /// its profile, and suspended its ground/swim writer. Physics must already be active.
        /// sledTransform is the replacement of native PlayerAnimatorControl._transform,
        /// a separately preserved visual transform under the body, never the camera/root.
        /// Capsule radius/entry changes must already be restored. No component is created.
        /// </summary>
        public static bool TryBind(PlayerBridge bridge, PlayerCarrier carrier,
            Transform sledTransform, PlayerSledProfile profile,
            PlayerSledState state, out PlayerSledAuthority authority,
            out string detail)
            => TryBindCore(bridge, carrier, sledTransform, profile, state, false, out authority, out detail);

        private static bool TryBindCore(PlayerBridge bridge, PlayerCarrier carrier,
            Transform sledTransform, PlayerSledProfile profile,
            PlayerSledState state, bool entering, out PlayerSledAuthority authority,
            out string detail)
        {
            authority = null;
            detail = "An admitted rider, active carrier and exclusive movement routing are required.";
            if (!GameBuild.IsRecognized || bridge?.IsServer != true || !bridge.IsGameplayActive
                || carrier == null || !carrier.IsCurrentForMotor || !carrier.IsPhysicsActive || !Valid(profile) || !Valid(state)
                || Time.inFixedTimeStep) return false;
            try
            {
                if (!Alive(carrier.Body) || !Alive(carrier.PrimaryCollider) || !Alive(sledTransform)
                    || !Alive(carrier.PrimaryCollider.sharedMaterial)
                    || Same(sledTransform, carrier.Body.transform)
                    || !sledTransform.IsChildOf(carrier.Body.transform)
                    || (!entering && carrier.PrimaryCollider.radius != profile.PlayerColliderRadius)
                    || Writers.ContainsKey(carrier.Body.Pointer)) return false;
                var candidate = new PlayerSledAuthority(bridge, carrier, sledTransform, profile, state);
                if (!candidate.Current() || !float.IsFinite(candidate._stopDeadline)
                    || !float.IsFinite(candidate._staticFriction) || !float.IsFinite(candidate._dynamicFriction)
                    || !float.IsFinite(candidate._bounciness)) return false;
                bridge.Retired += candidate.Retire;
                Writers.Add(carrier.Body.Pointer, candidate);
                authority = candidate;
                detail = "Riding writer bound; no camera or native controller was invoked.";
                return true;
            }
            catch (Exception error)
            { detail = "Sled binding failed (" + error.GetType().Name + ")."; return false; }
        }

        /// <summary>
        /// Execute the server-body portion of an admitted entry. The caller must have
        /// drained the previous motor step and excluded every other body/capsule writer.
        /// Geometry and environment are server-approved actor observations, never packet
        /// positions. The original client action retains camera, layers, audio and input.
        /// A non-null authority on failure owns a partially changed body: do not retry it.
        /// </summary>
        public static bool TryEnter(PlayerBridge bridge, PlayerCarrier carrier,
            Transform sledTransform, PlayerSledProfile profile,
            PlayerNativeCapsuleProfile geometry, PlayerSledState previousState,
            PlayerSledEnvironment environment, out PlayerSledAuthority authority,
            out string detail)
        {
            authority = null;
            detail = "A server-admitted shell-ride entry with an exclusive single-capsule motor is required.";
            if (!ValidGeometry(geometry) || !Valid(environment) || !Valid(previousState)
                || profile.PlayerColliderRadius != RidingRadius
                || profile.PrimaryHeight != geometry.CrouchingHeight
                || profile.PrimaryCenterY != geometry.CrouchingCenter.y
                || environment.BaseStateTagHash != AnimationHashes.shellRideHash
                || environment.BaseStateShortNameHash == AnimationHashes.shellRideToIdleHash) return false;
            try
            {
                if (carrier == null || !Alive(carrier.Body) || !Alive(carrier.PrimaryCollider)
                    || carrier.PrimaryCollider.radius != geometry.Radius || !Finite(carrier.Body.position)) return false;
                var state = previousState with
                {
                    ForceStop = false, StopTimeRemaining = 0.5f,
                    StartedBelowTerrain = environment.TerrainSurfaceHeight > carrier.Body.position.y
                };
                if (!TryBindCore(bridge, carrier, sledTransform, profile, state, true, out authority, out detail)) return false;
                authority._standingHeight = geometry.StandingHeight;
                authority._standingCenterY = geometry.StandingCenter.y;
                authority.Phase = PlayerSledPhase.Entering;
                authority._tracksGeometry = true;
                authority._capsuleProgress = 0;
                authority._expectedRadius = RidingRadius;
                authority._capsule.radius = RidingRadius;
                if (!authority.Current()) return authority.Fail("Sled entry lost its carrier after changing radius.", out detail);
                detail = "Server sled entry started; advance capsule transition during Update and riding physics during FixedUpdate.";
                return true;
            }
            catch (Exception error)
            {
                detail = "Sled entry failed (" + error.GetType().Name + ").";
                if (authority != null) return authority.Fail(detail, out detail);
                return false;
            }
        }

        /// <summary>
        /// Begin only after the authoritative animator leaves the ride tag or enters
        /// shellRideToIdle. The caller supplies the clearance-approved exit capsule;
        /// a blocked standing exit must use crouched geometry, not expand into a ceiling.
        /// This is the body portion of EndSledding, not permission to resume ground yet.
        /// </summary>
        public bool TryBeginExit(PlayerNativeCapsuleProfile geometry, bool remainCrouched,
            PlayerSledEnvironment environment, out string detail)
        {
            detail = "An admitted exit animation and clearance-approved return capsule are required.";
            if (_retired || Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep
                || Phase is PlayerSledPhase.Exiting or PlayerSledPhase.Exited
                || !ValidGeometry(geometry) || !Valid(environment)
                || geometry.CrouchingHeight != _profile.PrimaryHeight
                || geometry.CrouchingCenter.y != _profile.PrimaryCenterY
                || (environment.BaseStateTagHash == AnimationHashes.shellRideHash
                    && environment.BaseStateShortNameHash != AnimationHashes.shellRideToIdleHash)) return false;
            try
            {
                if (!Current()) return false;
                _standingHeight = remainCrouched ? geometry.CrouchingHeight : geometry.StandingHeight;
                _standingCenterY = remainCrouched ? geometry.CrouchingCenter.y : geometry.StandingCenter.y;
                _capsuleProgress = 0;
                _expectedHeight = _capsule.height;
                _expectedCenter = _capsule.center;
                _tracksGeometry = true;
                Phase = PlayerSledPhase.Exiting;
                StopRequested = true;
                _expectedRadius = geometry.Radius;
                _capsule.radius = geometry.Radius;
                _body.useGravity = true;
                if (!Current()) return Fail("Sled exit lost its carrier after restoring radius/gravity.", out detail);
                detail = "Server sled exit started; body forces stopped, original client finalization remains external.";
                return true;
            }
            catch (Exception error) { return Fail("Sled exit failed (" + error.GetType().Name + ").", out detail); }
        }

        /// <summary>
        /// Reproduce the native 0.18-second capsule coroutine without scheduling a native
        /// continuation. Call once per Update. FixedUpdate still writes crouched geometry
        /// while entering, as the native rider does. Exit never applies riding forces.
        /// </summary>
        public bool TryUpdateTransition(PlayerSledEnvironment environment, out string detail)
        {
            detail = "A current entry/exit capsule transition and fresh Update are required.";
            if (_retired || Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep
                || Phase is not (PlayerSledPhase.Entering or PlayerSledPhase.Exiting)
                || !Valid(environment)) return false;
            try
            {
                double now = Time.timeAsDouble;
                float dt = Time.deltaTime;
                if (!Current() || !double.IsFinite(now) || now <= _lastTransitionTime
                    || !float.IsFinite(dt) || dt <= 0) return false;
                float progress = Mathf.Min(1, _capsuleProgress + dt / CapsuleTransitionSeconds);
                bool entering = Phase == PlayerSledPhase.Entering;
                float alpha = Mathf.SmoothStep(entering ? 0 : 1, entering ? 1 : 0, progress);
                float height = Mathf.Lerp(_standingHeight, _profile.PrimaryHeight, alpha);
                float center = Mathf.Lerp(_standingCenterY, _profile.PrimaryCenterY, alpha);
                var position = _root.position;
                if (!Finite(position) || !float.IsFinite(height) || !float.IsFinite(center)) return false;
                _lastTransitionTime = now;
                _expectedHeight = height;
                _expectedCenter = new Vector3(0, center, 0);
                _capsule.height = height;
                _capsule.center = _expectedCenter;
                if (!State.StartedBelowTerrain && environment.TerrainSurfaceHeight > position.y)
                    _root.position = new Vector3(position.x, environment.TerrainSurfaceHeight, position.z);
                _capsuleProgress = progress;
                if (!Current() || !Finite(_root.position)) return Fail("Sled capsule transition lost its binding or pose.", out detail);
                if (progress == 1 && entering) Phase = PlayerSledPhase.Riding;
                detail = "Server sled capsule transition advanced.";
                return true;
            }
            catch (Exception error) { return Fail("Sled capsule transition failed (" + error.GetType().Name + ").", out detail); }
        }

        /// <summary>
        /// Complete at a drained non-physics boundary after native client finalization
        /// and the reliable exit have settled. Restores only this writer's material;
        /// the aggregate may then resume ground/swim and retire this writer.
        /// </summary>
        public bool TryCompleteExit(out string detail)
        {
            detail = "The capsule exit must finish before restoring ground material.";
            if (_retired || Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep
                || Phase != PlayerSledPhase.Exiting || _capsuleProgress != 1) return false;
            try
            {
                if (!Current()) return false;
                _material.staticFriction = _staticFriction;
                _material.dynamicFriction = _dynamicFriction;
                _material.bounciness = _bounciness;
                _material.frictionCombine = _frictionCombine;
                _material.bounceCombine = _bounceCombine;
                _body.useGravity = true;
                if (!Current()) return Fail("Sled completion lost its binding.", out detail);
                Phase = PlayerSledPhase.Exited;
                detail = "Server sled body exit completed; no ground writer or client controller was enabled.";
                return true;
            }
            catch (Exception error) { return Fail("Sled completion failed (" + error.GetType().Name + ").", out detail); }
        }

        public bool TryReadMotorState(out PlayerSledMotorState state)
        {
            state = default;
            if (_retired || Environment.CurrentManagedThreadId != _thread) return false;
            try
            {
                if (!Current()) return false;
                var riding = State with { StopTimeRemaining = _stopDeadline - Time.time };
                if (!Valid(riding)) return false;
                state = new(riding, Phase, _capsuleProgress);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Single Update endpoint for the mode router. Supplies server observations to
        /// the capsule transition, then consumes horizontal intent only while riding.
        /// Does not turn a completed capsule exit into a reliable lifecycle commit.
        /// </summary>
        public bool TryUpdate(float horizontal, PlayerSledEnvironment environment, out string detail)
        {
            detail = "A current sled motor and finite horizontal intent are required.";
            if (!IsCurrentForMotor || !float.IsFinite(horizontal) || MathF.Abs(horizontal) > 1
                || !Valid(environment) || Time.inFixedTimeStep || Phase == PlayerSledPhase.Exited) return false;
            if (Phase is PlayerSledPhase.Entering or PlayerSledPhase.Exiting)
            {
                if (!TryUpdateTransition(environment, out detail)) return false;
                if (Phase == PlayerSledPhase.Exiting) return true;
            }
            return TryUpdateSteering(horizontal, out detail);
        }

        private static bool ValidGeometry(PlayerNativeCapsuleProfile p)
            => float.IsFinite(p.Radius) && p.Radius > 0
                && float.IsFinite(p.StandingHeight) && float.IsFinite(p.CrouchingHeight)
                && p.StandingHeight >= p.CrouchingHeight && p.CrouchingHeight >= 2 * MathF.Max(p.Radius, RidingRadius)
                && Finite(p.StandingCenter) && Finite(p.CrouchingCenter)
                && p.StandingCenter.x == 0 && p.StandingCenter.z == 0
                && p.CrouchingCenter.x == 0 && p.CrouchingCenter.z == 0;

        /// <summary>Read at the aggregate's pause boundary; rebases only the native stop deadline.</summary>
        public bool TryCaptureState(out PlayerSledState state)
        {
            state = default;
            if (_retired || Environment.CurrentManagedThreadId != _thread
                || Phase != PlayerSledPhase.Riding) return false;
            try
            {
                if (!Current()) return false;
                var captured = State with { StopTimeRemaining = _stopDeadline - Time.time };
                if (!Valid(captured)) return false;
                state = captured;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Route an admitted actor stop action here. Native StopSledding only latches
        /// _forceStopSledding; it does not exit, zero velocity, or change the camera.
        /// </summary>
        public bool TryRequestStop()
        {
            if (_retired || Environment.CurrentManagedThreadId != _thread
                || Phase is PlayerSledPhase.Exiting or PlayerSledPhase.Exited) return false;
            try
            {
                if (!Current()) return false;
                State = State with { ForceStop = true };
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Run at authoritative Update cadence with authenticated horizontal intent.
        /// The result's AnimationBlend belongs to shellBlendHash. Publishing it is the
        /// aggregate's responsibility; this method rotates only the bound server root.
        /// </summary>
        public bool TryUpdateSteering(float horizontal, out string detail)
        {
            detail = "Sled steering requires a current server actor and a fresh Update boundary.";
            if (_retired || Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep
                || Phase is PlayerSledPhase.Exiting or PlayerSledPhase.Exited) return false;
            try
            {
                double now = Time.timeAsDouble;
                if (!Current() || !double.IsFinite(now) || now <= _lastSteeringTime
                    || !TryAdvanceSteering(State, horizontal, Time.deltaTime, out var next, out float yaw)) return false;
                var position = _sled.position;
                if (!Finite(position)) return false;
                // Reserve before the first mutation: a failed write must not be replayed.
                _lastSteeringTime = now;
                if (yaw != 0) _root.RotateAround(position, Vector3.up, yaw);
                State = next;
                if (!Current() || !Finite(_root.position) || !Finite(_root.rotation))
                    return Fail("Sled steering changed an invalid actor binding or pose.", out detail);
                detail = "Actor-local sled steering applied.";
                return true;
            }
            catch (Exception error)
            { return Fail("Sled steering failed (" + error.GetType().Name + ").", out detail); }
        }

        /// <summary>
        /// Call once before physics at FixedUpdate, instead of ground/swim stepping.
        /// Water flattens tilt, disables gravity, and clears vertical speed only inside
        /// the native terrain-height/force-stop branch. StopRequested requests shellRideBool=false;
        /// the aggregate must drive the original exit path, not call EndSledding
        /// on the host or silently switch to walking. Failure after mutation is terminal.
        /// </summary>
        public bool TryFixedStep(PlayerSledEnvironment environment, out string detail)
        {
            detail = "A current riding actor and qualified per-actor environment are required.";
            if (_retired || Environment.CurrentManagedThreadId != _thread || !Time.inFixedTimeStep
                || Phase is PlayerSledPhase.Exiting or PlayerSledPhase.Exited
                || !Valid(environment)
                || environment.BaseStateTagHash != AnimationHashes.shellRideHash
                || environment.BaseStateShortNameHash == AnimationHashes.shellRideToIdleHash) return false;
            try
            {
                double now = Time.fixedTimeAsDouble;
                float dt = Time.deltaTime;
                if (!Current() || !double.IsFinite(now) || now <= _lastPhysicsTime
                    || !float.IsFinite(dt) || dt <= 0 || !float.IsFinite(Time.time)) return false;
                var position = _sled.position;
                var rootPosition = _root.position;
                var velocity = _body.velocity;
                var rotation = _sled.rotation;
                if (!Finite(position) || !Finite(rootPosition) || !Finite(velocity) || !Finite(rotation)) return false;

                var normal = Vector3.up;
                float slope = 0;
                if (Physics.Raycast(position + Vector3.up * 0.5f, Vector3.down, out var hit,
                    2, _profile.GroundLayerMask, QueryTriggerInteraction.Ignore))
                {
                    normal = hit.normal;
                    if (!Finite(normal) || normal.sqrMagnitude < 0.0001f) return false;
                    slope = Mathf.Clamp((Vector3.Angle(normal, _root.forward) - 90) / 30, -1, 1);
                }
                if (environment.InWater) normal = Vector3.up;
                var forward = Vector3.Cross(_sled.right, normal);
                if (!Finite(forward) || forward.sqrMagnitude < 0.0001f) return false;
                var nextRotation = Quaternion.Lerp(rotation, Quaternion.LookRotation(forward, normal), dt * 5);
                Vector3 force = Vector3.zero;
                bool applyMotion = environment.HeightFromTerrain < 0.6f
                    && environment.HeightFromTerrain > -1 && !State.ForceStop;
                bool applyForce = applyMotion && !environment.InWater;
                if (applyForce)
                    force = DryForce(velocity, nextRotation * Vector3.forward, nextRotation * Vector3.right, normal, slope);
                float deadline = environment.SpeedSquaredFromDeltaPosition >= 0.64000005f
                    ? Time.time + 0.6f : _stopDeadline;
                float friction = environment.InSnow ? 0.15f : 0.4f;
                float staticFriction = Mathf.Lerp(_material.staticFriction, friction, dt * 0.5f);
                float dynamicFriction = Mathf.Lerp(_material.dynamicFriction, friction, dt * 0.5f);
                bool jumping = environment.JumpingTimer > 3;
                if (!Finite(force) || !Finite(nextRotation) || !float.IsFinite(deadline)
                    || !float.IsFinite(staticFriction) || !float.IsFinite(dynamicFriction)) return false;

                _lastPhysicsTime = now;
                _sled.rotation = nextRotation;
                _expectedHeight = _profile.PrimaryHeight;
                _expectedCenter = new Vector3(0, _profile.PrimaryCenterY, 0);
                _capsule.height = _profile.PrimaryHeight;
                _capsule.center = _expectedCenter;
                _material.staticFriction = staticFriction;
                _material.dynamicFriction = dynamicFriction;
                _material.frictionCombine = PhysicMaterialCombine.Multiply;
                _material.bounceCombine = jumping ? PhysicMaterialCombine.Multiply : PhysicMaterialCombine.Average;
                _material.bounciness = jumping ? 0 : 0.9f;
                if (applyForce) _body.AddForce(force, ForceMode.Force);
                else if (applyMotion && environment.InWater) _body.velocity = new Vector3(velocity.x, 0, velocity.z);
                _body.useGravity = !environment.InWater;
                if (!State.StartedBelowTerrain && environment.TerrainSurfaceHeight > rootPosition.y)
                    _root.position = new Vector3(rootPosition.x, environment.TerrainSurfaceHeight, rootPosition.z);
                _stopDeadline = deadline;
                StopRequested |= Time.time > deadline;
                State = State with { StopTimeRemaining = deadline - Time.time };
                if (!Current() || !Finite(_root.position) || !Finite(_sled.rotation))
                    return Fail("Sled physics changed an invalid actor binding or pose.", out detail);
                detail = environment.InWater ? "Wet actor-local sled motion applied." : "Dry actor-local sled motion applied.";
                return true;
            }
            catch (Exception error)
            { return Fail("Sled physics failed (" + error.GetType().Name + ").", out detail); }
        }

        private bool Current()
            => !_retired && _bridge.IsServer && _bridge.IsGameplayActive && _carrier.IsCurrentForMotor && _carrier.IsPhysicsActive
                && _bridge.TryReadCurrentTarget(out var target) && Same(target, _entity)
                && _entity.GetInstanceID() == _entityId && Same(_carrier.Body, _body)
                && Same(_carrier.PrimaryCollider, _capsule) && Same(_body.transform, _root)
                && Same(_capsule.attachedRigidbody, _body) && Same(_capsule.sharedMaterial, _material)
                && _capsule.radius == _expectedRadius
                && (!_tracksGeometry || (_capsule.height == _expectedHeight && _capsule.center.Equals(_expectedCenter)))
                && Alive(_sled) && _sled.GetInstanceID() == _sledId && _sled.IsChildOf(_root)
                && _entity.gameObject.activeInHierarchy && _capsule.enabled && !_capsule.isTrigger
                && !_body.isKinematic && _body.detectCollisions && Physics.autoSimulation
                && PhysicsSceneExtensions.GetPhysicsScene(_entity.gameObject.scene) == Physics.defaultPhysicsScene;

        private bool Fail(string reason, out string detail)
        {
            detail = FailureReason = reason;
            Retire();
            return false;
        }

        /// <summary>
        /// Stop this writer only. Does not restore walking geometry, release native holds,
        /// enable competing controllers, or clear unknown pending physics effects.
        /// </summary>
        public void Retire()
        {
            if (_retired || Environment.CurrentManagedThreadId != _thread) return;
            _retired = true;
            _bridge.Retired -= Retire;
            if (Writers.TryGetValue(_body.Pointer, out var writer) && ReferenceEquals(writer, this))
                Writers.Remove(_body.Pointer);
        }
    }
}
