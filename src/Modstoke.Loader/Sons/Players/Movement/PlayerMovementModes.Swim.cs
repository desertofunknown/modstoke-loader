// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerMovementModes
    {
        private SwimBodyBinding _swimBinding;
        private Collider _stepHelperCollider;

        internal bool TryBindSwimBody(Rigidbody body, out string detail, Collider stepHelperCollider = null)
        {
            detail = "One exact carrier body and bounded collider/controller topology are required.";
            if (!Current() || !Alive(body) || !Same(body.gameObject, _entity.gameObject)) return false;
            try
            {
                if (_swimBinding == null)
                {
                    if (stepHelperCollider != null && (!Alive(stepHelperCollider)
                        || !Same(stepHelperCollider.attachedRigidbody, body) || stepHelperCollider.enabled
                        || stepHelperCollider.isTrigger)) return false;
                    _swimBinding = new SwimBodyBinding(_entity, body);
                    _stepHelperCollider = stepHelperCollider;
                }
                else if (stepHelperCollider != null && !Same(stepHelperCollider, _stepHelperCollider)) return false;
                return _swimBinding.Matches(body) && _swimBinding.RefreshTopology();
            }
            catch (Exception error)
            { detail = "Swim body enrollment failed (" + error.GetType().Name + ")."; return false; }
        }

        internal bool RefreshSwimBodyTopology(out string detail)
        {
            detail = "The enrolled swim body topology changed.";
            return Current() && _swimBinding != null && _swimBinding.RefreshTopology();
        }

        internal bool BoundSwimBodyCurrent(Rigidbody body, Collider expectedSolid)
        {
            if (_swimBinding == null || !_swimBinding.Matches(body)) return false;
            foreach (var slot in _swimBinding.Colliders)
                if (slot.Value.enabled && !slot.Value.isTrigger && !Same(slot.Value, expectedSolid)
                    && !Same(slot.Value, _stepHelperCollider)) return false;
            return true;
        }

        /// <summary>
        /// Apply only after TryResolveMode selected Swim in this fixed step and the
        /// adapter disabled its ground controller and all competing native root writers.
        /// Uses the carrier body's actual velocity/mass and native FPC force ordering.
        /// No LocalPlayer method is invoked. The integrated GroundMovement router applies
        /// qualified surface jumps first and supplies its owned timer state here. Standalone
        /// callers without that edge/contact/cooldown adapter cannot submit surface Jump.
        /// The caller must stop using this motor on failure and publish only after physics.
        /// </summary>
        public bool TryStepSwim(Rigidbody serverBody, PlayerMovementModeInput input,
            PlayerSwimDynamics dynamics,
            out PlayerMovementModeState state, out string detail)
        {
            state = default;
            detail = "Resolve the current server Swim mode and provide an exclusive dynamic carrier body.";
            if (!Current() || _mode != PlayerMovementMode.Swim) return false;
            try
            {
                double now = Time.fixedTimeAsDouble;
                float dt = Time.fixedDeltaTime;
                if (!Time.inFixedTimeStep || now != _lastResolvedTime || now == _lastAppliedTime
                    || !float.IsFinite(dt) || dt <= 0 || dt > 0.1f
                    || !Valid(input) || !ValidDynamics(dynamics)) return false;
                if ((_swimBinding == null && !TryBindSwimBody(serverBody, out detail))
                    || !RefreshSwimBodyTopology(out detail) || !BodyCurrent(serverBody)
                    || !WaterCurrent(_water, out detail)) return false;
                if (input.Jump && !_state.IsUnderwater)
                { detail = "Surface water-jump contact, edge multiplier and cooldown authority are not bound."; return false; }

                Vector3 velocity = serverBody.velocity;
                float mass = serverBody.mass;
                if (!Finite(velocity) || !float.IsFinite(velocity.sqrMagnitude)
                    || !float.IsFinite(mass) || mass <= 0)
                { detail = "The actual carrier mass or velocity is invalid."; return false; }

                // GameAssembly 9BEED267...980AA9B: GetTargetVelocity 182F84B80,
                // DetermineVelocityChange 182F84E90, HandleSwimmingSpeed 182F850A0.
                Vector3 localInput = new(input.Move.x, 0, input.Move.y);
                if (localInput.magnitude < 0.1f) localInput = Vector3.zero;
                localInput = Vector3.ClampMagnitude(localInput, 1.1f);
                Vector3 target = _root.TransformDirection(localInput) * dynamics.CurrentSpeed;
                Vector3 delta = target - velocity;
                delta.x = Mathf.Clamp(delta.x, -dynamics.MaximumVelocityChange, dynamics.MaximumVelocityChange)
                    * dynamics.EffectiveInputClamp;
                delta.y = 0;
                delta.z = Mathf.Clamp(delta.z, -dynamics.MaximumVelocityChange, dynamics.MaximumVelocityChange)
                    * dynamics.EffectiveInputClamp;
                // FixedUpdate 182F80620..182F80883: grounded swimming uses the
                // ordinary acceleration branch, not the airborne swim speed clamp.
                Vector3 steering = _state.ServerGrounded ? delta
                    : Vector3.Slerp(Vector3.zero, delta * dynamics.SwimSpeedMultiplier, dt * 8f);

                // AddForce is queued, so both native clamps read the PRE-force velocity.
                // ClampSwimVelocity uses _speed, not the unused _maxSwimVelocity field.
                Vector3 swimClamped = _state.ServerGrounded || dynamics.SwimmingJumpTimerRunning ? velocity
                    : Vector3.ClampMagnitude(velocity, dynamics.CurrentSpeed);
                Vector3 generalClamped = Vector3.ClampMagnitude(swimClamped, dynamics.MaximumVelocity);
                Vector3 clamped = generalClamped;

                // ApplySwimmingForce 182F868E0 and StabilizeSwimmingForce 182F86DC0.
                // _absCamX is the signed camera X angle / 45 (1835A6974).
                Vector3 normalizedInput = localInput.normalized;
                float pitch = Mathf.Clamp(input.LookPitchDegrees / 45f, -1f, 1f) * normalizedInput.magnitude;
                float dive = pitch * (pitch > 0 ? 13.5f : 18f);
                float stabilization = 1f - MathF.Abs(pitch);
                float diveForce = 0;
                if (_state.IsUnderwater && clamped.magnitude > 0.05)
                {
                    if (normalizedInput.z < 0) dive = -dive;
                    diveForce = -mass * dive;
                    clamped = Vector3.ClampMagnitude(clamped, dynamics.MaximumDiveVelocity);
                }
                float dampingForce = dynamics.SwimmingJumpTimerRunning ? 0
                    : -clamped.y * 50f * dynamics.DefaultMass
                        * (normalizedInput.magnitude < 0.1f ? 1f : stabilization);
                bool diving = _state.IsUnderwater || (input.LookPitchDegrees / 45f > 0.65f && input.Move.y > 0);
                bool swimmingUnderwater = _swimmingUnderwater;
                bool? gravity = null;
                if (diving) { swimmingUnderwater = true; gravity = false; }
                else if (swimmingUnderwater && _water.Height is float height
                    && _water.Position.y + WaterlineOffset > height)
                { swimmingUnderwater = false; gravity = true; }
                if (!Finite(steering) || !Finite(clamped) || !float.IsFinite(diveForce)
                    || !float.IsFinite(dampingForce) || !Current() || !BodyCurrent(serverBody)) return false;

                // Claim the tick before the first native write: a partial failure must
                // retire the binding, never retry and apply a queued force twice.
                _lastAppliedTime = now;
                if (gravity.HasValue) serverBody.useGravity = gravity.Value;
                serverBody.AddForce(steering, _state.ServerGrounded ? ForceMode.Acceleration : ForceMode.VelocityChange);
                if (!serverBody.velocity.Equals(swimClamped)) serverBody.velocity = swimClamped;
                if (!serverBody.velocity.Equals(generalClamped)) serverBody.velocity = generalClamped;
                if (diveForce != 0) serverBody.AddForce(new Vector3(0, diveForce, 0), ForceMode.Force);
                if (!serverBody.velocity.Equals(clamped)) serverBody.velocity = clamped;
                if (dampingForce != 0) serverBody.AddForce(new Vector3(0, dampingForce, 0), ForceMode.Force);
                if (!Current() || !BodyCurrent(serverBody))
                    return Stop("Carrier changed while applying swim forces.", out detail);
                _swimmingUnderwater = swimmingUnderwater;
                // HandleRunningStaminaAndSpeed 182F86595: grounded *1, airborne *1.2.
                // The non-running branch at 182F80B20/182F80C30 uses *1 / *0.8.
                bool running = input.Sprint && dynamics.RunningAllowed;
                float speedFactor = _state.ServerGrounded ? (running ? 1f : 0.8f) : (running ? 1.2f : 1f);
                _state = _state with { SwimmingUnderwater = swimmingUnderwater,
                    Velocity = serverBody.velocity, CurrentSpeed = dynamics.SwimSpeed * speedFactor };
                state = _state;
                detail = "Native swim steering, dive and damping forces queued on the server body.";
                return true;
            }
            catch (Exception error)
            { return Stop("Swim force application failed (" + error.GetType().Name + ").", out detail); }
        }

        private bool BodyCurrent(Rigidbody body)
        {
            if (!Alive(body) || !Same(body.gameObject, _entity.gameObject)
                || body.isKinematic || !body.detectCollisions || !Same(body.transform, _root)
                || _swimBinding == null || !_swimBinding.Matches(body)) return false;
            foreach (var slot in _swimBinding.Controllers)
                if (slot.Value.enabled) return false;
            bool solid = false;
            foreach (var slot in _swimBinding.Colliders)
            {
                var collider = slot.Value;
                if (!collider.enabled || collider.isTrigger) continue;
                if (!Same(collider.attachedRigidbody, body)) return false;
                solid = true;
            }
            return solid;
        }

        private readonly record struct BoundComponent<T>(T Value, int InstanceId) where T : Component
        {
            internal bool IsCurrent => Alive(Value) && Value.GetInstanceID() == InstanceId;
        }

        private sealed class SwimBodyBinding
        {
            private readonly BoltEntity _entity;
            private readonly Rigidbody _body;
            private readonly BoundComponent<Rigidbody>[] _bodies;
            private readonly BoundComponent<FirstPersonCharacter>[] _characters;
            internal readonly BoundComponent<Collider>[] Colliders;
            internal readonly BoundComponent<CharacterController>[] Controllers;
            private readonly Il2CppSystem.Collections.Generic.List<Rigidbody> _bodyBuffer = new(1);
            private readonly Il2CppSystem.Collections.Generic.List<FirstPersonCharacter> _characterBuffer = new(1);
            private readonly Il2CppSystem.Collections.Generic.List<Collider> _colliderBuffer = new(16);
            private readonly Il2CppSystem.Collections.Generic.List<CharacterController> _controllerBuffer = new(1);

            internal SwimBodyBinding(BoltEntity entity, Rigidbody body)
            {
                _entity = entity;
                _body = body;
                entity.GetComponentsInChildren<Rigidbody>(true, _bodyBuffer);
                entity.GetComponentsInChildren<FirstPersonCharacter>(true, _characterBuffer);
                entity.GetComponentsInChildren<Collider>(true, _colliderBuffer);
                entity.GetComponentsInChildren<CharacterController>(true, _controllerBuffer);
                _bodies = Capture(_bodyBuffer, 1);
                _characters = Capture(_characterBuffer, 16);
                Colliders = Capture(_colliderBuffer, 128);
                Controllers = Capture(_controllerBuffer, 16);
                if (_bodies.Length != 1 || !Same(_bodies[0].Value, body) || Colliders.Length == 0)
                    throw new InvalidOperationException("The carrier does not have one exclusive swim body.");
            }

            // Called at simulation boundaries, not for every identity/state check or
            // collision callback. Reusable native lists still catch added components.
            internal bool RefreshTopology()
            {
                _entity.GetComponentsInChildren<Rigidbody>(true, _bodyBuffer);
                _entity.GetComponentsInChildren<FirstPersonCharacter>(true, _characterBuffer);
                _entity.GetComponentsInChildren<Collider>(true, _colliderBuffer);
                _entity.GetComponentsInChildren<CharacterController>(true, _controllerBuffer);
                return MatchesList(_bodyBuffer, _bodies) && MatchesList(_characterBuffer, _characters)
                    && MatchesList(_colliderBuffer, Colliders) && MatchesList(_controllerBuffer, Controllers);
            }

            internal bool Matches(Rigidbody body)
            {
                if (!Same(body, _body) || !Same(body.gameObject, _entity.gameObject) || !_bodies[0].IsCurrent) return false;
                foreach (var slot in _characters)
                    if (!slot.IsCurrent || slot.Value.enabled || !slot.Value.transform.IsChildOf(_entity.transform)) return false;
                foreach (var slot in Colliders) if (!slot.IsCurrent || !slot.Value.transform.IsChildOf(_entity.transform)) return false;
                foreach (var slot in Controllers) if (!slot.IsCurrent || !slot.Value.transform.IsChildOf(_entity.transform)) return false;
                return true;
            }

            private static BoundComponent<T>[] Capture<T>(Il2CppSystem.Collections.Generic.List<T> values, int maximum)
                where T : Component
            {
                if (values.Count > maximum) throw new InvalidOperationException("Swim topology exceeds its bounded profile.");
                var result = new BoundComponent<T>[values.Count];
                for (int i = 0; i < result.Length; i++)
                {
                    var value = values[i];
                    if (!Alive(value)) throw new InvalidOperationException("An enrolled swim component is not live.");
                    result[i] = new(value, value.GetInstanceID());
                }
                return result;
            }

            private static bool MatchesList<T>(Il2CppSystem.Collections.Generic.List<T> values, BoundComponent<T>[] slots)
                where T : Component
            {
                if (values.Count != slots.Length) return false;
                for (int i = 0; i < slots.Length; i++)
                    if (!slots[i].IsCurrent || !Same(values[i], slots[i].Value)) return false;
                return true;
            }
        }

        private static bool Valid(PlayerMovementModeInput input)
            => float.IsFinite(input.Move.x) && float.IsFinite(input.Move.y)
                && MathF.Abs(input.Move.x) <= 1 && MathF.Abs(input.Move.y) <= 1
                && float.IsFinite(input.LookPitchDegrees) && MathF.Abs(input.LookPitchDegrees) <= 90;

        internal static bool ValidDynamics(PlayerSwimDynamics d)
            => Bounded(d.CurrentSpeed, 0, 1200) && Bounded(d.SwimSpeed, 0, 1000)
                && Bounded(d.SwimSpeedMultiplier, 0, 100) && Bounded(d.MaximumVelocityChange, 0, 1000)
                && Bounded(d.EffectiveInputClamp, 0, 100) && Bounded(d.DefaultMass, 0.001f, 100000)
                && Bounded(d.MaximumVelocity, 0, 1000) && Bounded(d.MaximumDiveVelocity, 0, 1000)
                && (!d.WaterJump.HasValue || ValidJumpDynamics(d.WaterJump.Value));
        private static bool ValidJumpDynamics(PlayerSwimJumpDynamics j)
            => Bounded(j.JumpHeight, 0, 1000) && Bounded(j.Gravity, 0.001f, 1000)
                && Bounded(j.EdgeMultiplier, 0, 100) && Bounded(j.TimerInterval, 0, 60)
                && Bounded(j.JumpMultiplier, 0, 100);
        private static bool Bounded(float value, float minimum, float maximum)
            => float.IsFinite(value) && value >= minimum && value <= maximum;
    }
}
