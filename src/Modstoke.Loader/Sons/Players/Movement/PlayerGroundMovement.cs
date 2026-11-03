// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>Server-owned settings, never populated from a client's equipment or input packet.</summary>
    public readonly record struct PlayerGroundSettings(float WalkSpeed, float SprintSpeed,
        float CrouchSpeed, float Acceleration, float Gravity, float JumpSpeed, float TerminalSpeed,
        float Radius, float StandingHeight, float CrouchingHeight, float FootOffset, float StepHeight,
        float SlopeLimit, float SkinWidth);

    /// <summary>
    /// Server ground/swim simulation for an already migrated carrier; no LocalPlayer state is read.
    /// TryStart uses an explicit CharacterController profile. TryPrepare binds the held native
    /// carrier; TryActivate releases it only on an active gameplay bridge. Neither path
    /// replaces the retained client's controls/camera or implements climbing/vehicle actions.
    /// Admitted sled entry, dry/wet riding and exit share the same post-physics boundary.
    /// </summary>
    public sealed partial class PlayerGroundMovement : IDisposable
    {
        private static readonly List<PlayerGroundMovement> Active = new();
        private static bool _ticking;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerBridge _bridge;
        private readonly BoltEntity _entity;
        private readonly Transform _transform;
        private readonly PlayerGroundSettings _settings;
        private readonly Func<bool> _groundModeAllowed;
        private readonly Il2CppReferenceArray<Collider> _overlaps = new(32);
        private readonly int _layer;
        private PlayerAnimationGround _animation;
        private CharacterController _controller;
        private Vector3 _velocity;
        private PlayerMotionState _pendingState;
        private uint _stateSequence;
        private uint _lastInput;
        private uint _lastJumpSequence;
        private PlayerMoveButtons _lastButtons;
        private double _lastFixedTime = double.NaN;
        private bool _crouched;
        private bool _disposed;
        private bool _activated;
        public bool IsDisposed => _disposed;
        public bool IsPrepared => !_disposed && !_activated && _nativeCarrier != null;
        public bool IsActive => !_disposed && _activated && _bridge.IsGameplayActive;
        public string StopReason { get; private set; }

        private PlayerGroundMovement(PlayerBridge bridge, BoltEntity entity,
            PlayerGroundSettings settings, Func<bool> groundModeAllowed)
        {
            _bridge = bridge;
            _entity = entity;
            _transform = entity.transform;
            _layer = entity.gameObject.layer;
            _settings = settings;
            _groundModeAllowed = groundModeAllowed;
        }

        /// <summary>
        /// The adapter must exclusively own this carrier's root motion and provide an authoritative
        /// supported-modes predicate. After TryEnableSwimming this policy must allow both
        /// Ground and Swim; water geometry, not the policy or client, selects between them.
        /// False stops the motor. Does not create a migration or advertise full-mode support.
        /// The retained client's original controller and cameras are never replaced or disabled.
        /// </summary>
        public static bool TryStart(PlayerBridge bridge, PlayerGroundSettings settings,
            Vector3 preservedVelocity, bool initiallyCrouched, Func<bool> groundModeAllowed,
            out PlayerGroundMovement movement, out string detail)
        {
            movement = null;
            detail = "A completed server player bridge, ground-mode policy and valid server settings are required.";
            if (!PlayerRoles.IsAvailable || bridge?.IsServer != true || !bridge.IsGroundMovementMode
                || groundModeAllowed == null
                || !Valid(settings) || !Finite(preservedVelocity)
                || MathF.Abs(preservedVelocity.y) > settings.TerminalSpeed
                || preservedVelocity.x * preservedVelocity.x + preservedVelocity.z * preservedVelocity.z
                    > settings.SprintSpeed * settings.SprintSpeed
                || !bridge.TryReadCurrentTarget(out var entity)) return false;
            PlayerGroundMovement candidate = null;
            try
            {
                if (!PlayerRoles.IsCurrentCarrier(entity) || !groundModeAllowed()) return false;
                foreach (var existing in Active)
                    if (!existing._disposed && existing._entity.Pointer == entity.Pointer) return false;
                var root = entity.transform;
                if (root.parent != null || !UnitScale(root.lossyScale) || !Upright(root.up)
                    || PhysicsSceneExtensions.GetPhysicsScene(entity.gameObject.scene) != Physics.defaultPhysicsScene
                    || entity.GetComponentsInChildren<Rigidbody>(true).Length != 0
                    || entity.GetComponentsInChildren<FirstPersonCharacter>(true).Length != 0
                    || entity.GetComponentsInChildren<CharacterController>(true).Length != 0)
                {
                    detail = "The carrier must have an unparented upright root, unit scale and no competing body/controller.";
                    return false;
                }
                foreach (var collider in entity.GetComponentsInChildren<Collider>(true))
                    if (collider != null && !collider.isTrigger)
                    {
                        detail = "A carrier with existing solid colliders needs a game-specific collision adapter.";
                        return false;
                    }
                candidate = new PlayerGroundMovement(bridge, entity, settings, groundModeAllowed);
                candidate._velocity = preservedVelocity;
                candidate._crouched = initiallyCrouched;
                float initialHeight = initiallyCrouched ? settings.CrouchingHeight : settings.StandingHeight;
                // Check before adding the native component, including disabled-controller overlaps.
                if (!candidate.CapsuleClear(initialHeight))
                {
                    detail = "The standing capsule is obstructed or the bounded overlap query was incomplete.";
                    return false;
                }
                candidate._controller = entity.gameObject.AddComponent<CharacterController>();
                var controller = candidate._controller;
                controller.enabled = false;
                controller.radius = settings.Radius;
                controller.height = initialHeight;
                controller.center = new Vector3(0, settings.FootOffset + initialHeight * 0.5f, 0);
                controller.slopeLimit = settings.SlopeLimit;
                controller.stepOffset = settings.StepHeight;
                controller.skinWidth = settings.SkinWidth;
                controller.minMoveDistance = 0;
                controller.detectCollisions = true;
                controller.enableOverlapRecovery = true;
                controller.enabled = true;
                candidate._activated = true;
                if (!candidate.Current() || !groundModeAllowed())
                    throw new InvalidOperationException("Carrier changed during motor creation.");
                if (!candidate.Current())
                    throw new InvalidOperationException("Carrier changed in the ground-mode predicate.");
                bridge.Retired += candidate.Dispose;
                Active.Add(candidate);
                movement = candidate;
                detail = "Server ground movement started. Other movement modes require separate lifecycle adapters.";
                return true;
            }
            catch (Exception error)
            {
                candidate?.Stop("Motor creation failed (" + error.GetType().Name + ").");
                detail = candidate?.StopReason ?? "The carrier is no longer available.";
                return false;
            }
        }

        internal static void FixedTick()
        {
            if (Active.Count == 0 || _ticking || !PlayerRoles.IsAvailable) return;
            _ticking = true;
            try
            {
                // New motors added by a native callback wait until the next fixed step.
                int count = Active.Count;
                for (int i = 0; i < count; i++)
                {
                    var movement = Active[i];
                    if (!movement._disposed) movement.Step();
                }
            }
            finally
            {
                _ticking = false;
                for (int i = Active.Count - 1; i >= 0; i--)
                    if (Active[i]._disposed) Active.RemoveAt(i);
            }
        }

        /// <summary>
        /// Opt in after the adapter has quiesced the NEW carrier's PlayerAnimatorControl,
        /// bound its server animator, and left its native Mecanim layer publisher active.
        /// Publishes hSpeed/vSpeed/overallSpeed from this motor. Does not own crouch/jump
        /// transitions, stamina, other actions, or the retained client's animator/cameras.
        /// </summary>
        public bool TryEnableLocomotionAnimation(out string detail)
        {
            detail = "A live exclusive server ground motor is required.";
            if (Environment.CurrentManagedThreadId != _thread || _disposed || _animation != null) return false;
            try
            {
                if (!Current() || !_groundModeAllowed() || !Current()) return false;
                var animation = new PlayerAnimationGround(_bridge);
                if (!Current()) return false;
                _animation = animation;
                detail = "Server ground locomotion scalars bound; native layer publication remains active.";
                return true;
            }
            catch (Exception error)
            {
                detail = error is InvalidOperationException ? error.Message
                    : "Locomotion animation binding failed (" + error.GetType().Name + ").";
                return false;
            }
        }

        private void Step()
        {
            if (!_activated || CartMigrationHeld) return;
            _insideMovementStep = true;
            try
            {
                if (!_bridge.IsGameplayActive) { Stop("The active gameplay bridge ended."); return; }
                if (_nativeModeSuspended) { StepNativeMode(); return; }
                if (!_bridge.IsGroundMovementMode)
                {
                    Stop("Movement mode changed without yielding the ground motor at a settled boundary.");
                    return;
                }
                if (!Current()) { Stop("Carrier or controller lifetime ended."); return; }
                if (!SupportedModeAllowed()) { Stop("The authoritative supported-modes policy ended this motor."); return; }
                // A predicate is application code and may retire the player or motor.
                if (!Current()) { Stop("Carrier changed while checking its movement mode."); return; }
                if (_sledAuthority == null) _animation?.Validate();
                double now = Time.fixedTimeAsDouble;
                if (now == _lastFixedTime) return;
                if (_swimAwaitingPhysics) { Stop("A swim step missed its post-physics boundary."); return; }
                float dt = Time.fixedDeltaTime;
                if (!double.IsFinite(now) || (!double.IsNaN(_lastFixedTime) && now < _lastFixedTime)
                    || !float.IsFinite(dt) || dt <= 0 || dt > 0.1f)
                { Stop("Unsupported fixed-step timing."); return; }
                _lastFixedTime = now;

                // WindowFull is pre-send: keep simulating current intent and replace an
                // unsent boundary only after a newer input has completed simulation.
                var input = default(PlayerMotionInput);
                bool canSelect = !_nativeGroundEnabled || _nativeContactsReady;
                if (_sledAuthority != null)
                    canSelect &= (_sledAuthority.Phase is PlayerSledPhase.Entering or PlayerSledPhase.Riding)
                        && _bridge.TryPeekCurrentInput(out var steering)
                        && steering.Sequence == _sledSteeringInput.Sequence;
                bool hasInput = canSelect && _bridge.TryReadCurrentInput(out input);
                if (!Current()) { Stop("Player bridge expired while reading input."); return; }
                if (hasInput && input.Sequence > _lastInput && input.JumpSequence < _lastJumpSequence)
                { Stop("Accepted-jump sequence moved backwards."); return; }
                if (_nativeGroundEnabled)
                {
                    if (_nativeGroundStep == ulong.MaxValue)
                    { Stop("Native movement step counter exhausted."); return; }
                    ++_nativeGroundStep;
                }
                if (RouteSled(hasInput, input, dt) || RouteSwimming(hasInput, input, dt)) return;
                if (_nativeGroundEnabled) { StepNativeGround(hasInput, input, dt); return; }
                bool fresh = hasInput && input.Sequence > _lastInput;
                var buttons = hasInput ? input.MoveButtons : PlayerMoveButtons.None;
                bool crouch = (buttons & PlayerMoveButtons.Crouch) != 0;
                if (crouch != _crouched && (crouch || CapsuleClear(_settings.StandingHeight)))
                {
                    _crouched = crouch;
                    float height = crouch ? _settings.CrouchingHeight : _settings.StandingHeight;
                    _controller.height = height;
                    _controller.center = new Vector3(0, _settings.FootOffset + height * 0.5f, 0);
                }
                bool running = (buttons & PlayerMoveButtons.Sprint) != 0;
                if (_staminaVitals != null) running = ApplyRunningStamina(running, dt);
                float speed = _crouched ? _settings.CrouchSpeed
                    : running ? _settings.SprintSpeed : _settings.WalkSpeed;
                if (_movementModes != null) _swimCurrentSpeed = speed;
                float x = hasInput ? input.Movement.x : 0;
                float z = hasInput ? input.Movement.y : 0;
                float magnitude = MathF.Sqrt(x * x + z * z);
                if (magnitude > 1) { x /= magnitude; z /= magnitude; }
                float yaw = hasInput ? input.ViewYaw * (MathF.PI / 180f) : 0;
                var target = new Vector3((x * MathF.Cos(yaw) + z * MathF.Sin(yaw)) * speed, 0,
                    (z * MathF.Cos(yaw) - x * MathF.Sin(yaw)) * speed);
                var horizontal = new Vector3(_velocity.x, 0, _velocity.z);
                horizontal = Vector3.MoveTowards(horizontal, target, _settings.Acceleration * dt);
                bool jump = fresh && NewJumpIntent(input);
                // A re-enabled CC still has its pre-swim contact history until Move.
                bool grounded = _groundAfterSwim ? _groundAfterSwimGrounded : _controller.isGrounded;
                _groundAfterSwim = false;
                if (grounded && _velocity.y < 0) _velocity.y = -_settings.Gravity * dt;
                if (jump && !_crouched && grounded)
                {
                    ChargeOrdinaryJump();
                    _velocity.y = _settings.JumpSpeed;
                }
                _velocity = new Vector3(horizontal.x, MathF.Max(_velocity.y - _settings.Gravity * dt,
                    -_settings.TerminalSpeed), horizontal.z);
                Vector3 before = _transform.position;
                if (!Finite(before)) { Stop("The carrier pose is not finite."); return; }
                if (hasInput) _transform.rotation = Quaternion.Euler(0, input.ViewYaw, 0);
                var collisions = _controller.Move(_velocity * dt);
                if (!Current()) { Stop("Carrier changed during collision resolution."); return; }
                if (_movementModes != null && _controller.isGrounded) _swimJumping = false;
                Vector3 position = _transform.position;
                Vector3 actualVelocity = (position - before) / dt;
                if (!Finite(position) || !Finite(actualVelocity)) { Stop("The resolved pose is not finite."); return; }
                if ((collisions & CollisionFlags.Above) != 0 && _velocity.y > 0) _velocity.y = 0;
                if ((collisions & CollisionFlags.Below) != 0 && _velocity.y < 0) _velocity.y = 0;
                // Native Move may slide along a wall; do not accumulate hidden speed through it.
                _velocity.x = actualVelocity.x;
                _velocity.z = actualVelocity.z;
                // Only server-resolved motion reaches publication. No client pose or
                // retained-local Animator is consulted, and no Animator.Update is forced.
                _animation?.Publish(new Vector2(x, z), actualVelocity, dt);
                if (!Current()) { Stop("Carrier changed during locomotion publication."); return; }
                if (fresh)
                {
                    _stateSequence = System.Math.Max(_stateSequence, _bridge.LastSentStateSequence);
                    if (_stateSequence == uint.MaxValue || !_bridge.TryCommitSimulatedInput(input.Sequence))
                    { Stop("The simulated input could not be committed."); return; }
                    _lastInput = input.Sequence;
                    _lastJumpSequence = input.JumpSequence;
                    _lastButtons = buttons;
                    var rotation = CanonicalRotation(_transform.rotation);
                    _pendingState = new PlayerMotionState(_bridge.Plan.Id, _bridge.Plan.Generation,
                        _bridge.NetworkId, ++_stateSequence, input.Sequence, position, rotation, actualVelocity,
                        (_controller.isGrounded ? PlayerMotionFlags.Grounded : 0)
                        | (_crouched ? PlayerMotionFlags.Crouched : 0))
                    { MovementTransitionSequence = input.MovementTransitionSequence };
                }
                // Fresh input coalesces any unsent state above. Latched steps retain the
                // exact prior boundary, never relabel their newer pose with an older ACK.
                FlushMovementState();
            }
            catch (Exception error) { Stop(error is InvalidOperationException ? error.Message
                : "Ground simulation failed (" + error.GetType().Name + ")."); }
            finally { _insideMovementStep = false; }
        }

        private bool NewJumpIntent(PlayerMotionInput input)
            => (input.MoveButtons & PlayerMoveButtons.Jump) != 0
                && (input.JumpSequence != 0 ? input.JumpSequence > _lastJumpSequence
                    : (_lastButtons & PlayerMoveButtons.Jump) == 0);

        private bool Current() => CarrierCurrent()
            && (_nativeGroundEnabled && _controller == null
                || _controller != null && !_controller.WasCollected && _controller
                    && _controller.enabled == !DynamicBodyActive && _controller.detectCollisions == !DynamicBodyActive
                    && _controller.gameObject.Pointer == _entity.gameObject.Pointer)
            && (_movementModes == null ? !_nativeGroundEnabled : SwimComponentsCurrent())
            && (_staminaVitals is null || StaminaCurrent());

        private bool MotorIdentityCurrent() => !_disposed && Environment.CurrentManagedThreadId == _thread
            && _bridge.TryReadCurrentTarget(out var entity) && entity.Pointer == _entity.Pointer
            && _entity.gameObject.activeInHierarchy && _entity.gameObject.layer == _layer
            && PhysicsSceneExtensions.GetPhysicsScene(_entity.gameObject.scene) == Physics.defaultPhysicsScene
            && UnitScale(_transform.lossyScale)
            && _entity.GetState<IPlayerState>().Transform.Simulate?.Pointer == _transform.Pointer;

        private bool CarrierCurrent() => !_nativeModeSuspended && MotorIdentityCurrent()
            && _transform.parent == null && Upright(_transform.up);

        private bool CapsuleClear(float height)
        {
            // Shrink by the contact skin so an existing floor contact is not an obstruction.
            float radius = _settings.Radius - _settings.SkinWidth;
            Vector3 foot = _transform.position + new Vector3(0, _settings.FootOffset, 0);
            Vector3 bottom = foot + new Vector3(0, _settings.Radius, 0);
            Vector3 top = foot + new Vector3(0, height - _settings.Radius, 0);
            int mask = 0;
            for (int layer = 0; layer < 32; layer++)
                if (!Physics.GetIgnoreLayerCollision(_layer, layer)) mask |= 1 << layer;
            if (!Physics.autoSyncTransforms) Physics.SyncTransforms();
            try
            {
                int count = Physics.defaultPhysicsScene.OverlapCapsule(bottom, top, radius,
                    _overlaps, mask, QueryTriggerInteraction.Ignore);
                if (count < 0 || count >= _overlaps.Length) return false;
                for (int i = 0; i < count; i++)
                {
                    var collider = _overlaps[i];
                    if (collider == null || collider.WasCollected || !collider) return false;
                    if ((_controller == null || collider.Pointer != _controller.Pointer)
                        && (_swimCapsule == null || collider.Pointer != _swimCapsule.Pointer)) return false;
                }
                return true;
            }
            finally { for (int i = 0; i < _overlaps.Length; i++) _overlaps[i] = null; }
        }

        private void Stop(string reason)
        {
            StopReason ??= reason;
            Dispose();
        }

        /// <summary>Stops this motor and releases only the collision components it created.</summary>
        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("Dispose ground movement on its game thread.");
            if (_disposed) return;
            _disposed = true;
            _bridge.Retired -= Dispose;
            DisposeNativeModeRouting();
            _pendingState = default;
            _animation = null;
            DisposeStamina();
            DisposeSwimming();
            try
            {
                if (_controller != null && !_controller.WasCollected && _controller)
                { _controller.enabled = false; UnityEngine.Object.Destroy(_controller); }
            }
            catch (Exception error)
            {
                StopReason = "Controller cleanup could not be confirmed (" + error.GetType().Name + ").";
            }
            finally { if (!_ticking) Active.Remove(this); }
        }

        private static Quaternion CanonicalRotation(Quaternion rotation)
        {
            // q and -q share one wire representation, with ties resolved in w, x, y, z order.
            if (rotation.w < 0 || (rotation.w == 0 && (rotation.x < 0
                || (rotation.x == 0 && (rotation.y < 0 || (rotation.y == 0 && rotation.z < 0))))))
                return new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
            return rotation;
        }

        private static bool Valid(PlayerGroundSettings s) => Range(s.WalkSpeed, 0.01f, 30)
            && Range(s.SprintSpeed, s.WalkSpeed, 40) && Range(s.CrouchSpeed, 0.01f, s.WalkSpeed)
            && Range(s.Acceleration, 0.1f, 100) && Range(s.Gravity, 0.1f, 100)
            && Range(s.JumpSpeed, 0, 30) && Range(s.TerminalSpeed, 1, 100)
            && Range(s.Radius, 0.05f, 2) && Range(s.StandingHeight, 2 * s.Radius, 5)
            && Range(s.CrouchingHeight, 2 * s.Radius, s.StandingHeight) && Range(s.FootOffset, -5, 5)
            && Range(s.StepHeight, 0, s.CrouchingHeight) && Range(s.SlopeLimit, 0, 89)
            && Range(s.SkinWidth, 0.001f, s.Radius * 0.5f);
        private static bool Range(float f, float min, float max) => float.IsFinite(f) && f >= min && f <= max;
        private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        private static bool UnitScale(Vector3 v) => MathF.Abs(v.x - 1) < 0.0001f
            && MathF.Abs(v.y - 1) < 0.0001f && MathF.Abs(v.z - 1) < 0.0001f;
        private static bool Upright(Vector3 v) => MathF.Abs(v.x) < 0.0001f
            && MathF.Abs(v.y - 1) < 0.0001f && MathF.Abs(v.z) < 0.0001f;
    }
}
