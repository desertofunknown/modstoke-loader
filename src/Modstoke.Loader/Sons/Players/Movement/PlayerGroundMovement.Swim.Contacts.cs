// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using global::Sons.Gameplay;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerGroundMovement
    {
        private bool _swimWaterJumpsEnabled, _swimJumping;
        private float _swimJumpStarted = float.NaN, _swimJumpBlockedUntil = float.NaN;
        private Il2CppReferenceArray<Collider> _waterJumpOverlaps;
        private Il2CppSystem.Collections.Generic.List<PlayerJumpOutOfWaterDetection> _waterJumpDetectors;
        private Il2CppSystem.Collections.Generic.List<PlayerBlockJumpOutOfWaterMultiplier> _waterJumpBlockers;

        public bool SurfaceWaterJumpsEnabled => SwimmingEnabled && _swimWaterJumpsEnabled;

        private void AdvanceWaterJumpCooldown()
        {
            float now = Time.time;
            if (!float.IsFinite(now)) throw new InvalidOperationException("Invalid water-jump time.");
            // ResetBlockWaterJump (182F891A0), scheduled after exactly one scaled second.
            // An intervening edge jump does not cancel this existing reset.
            if (!float.IsNaN(_swimJumpBlockedUntil) && now >= _swimJumpBlockedUntil)
            {
                _swimJumpBlockedUntil = float.NaN;
                _swimJumping = false;
            }
        }

        private bool WaterJumpTimerRunning(PlayerSwimJumpDynamics profile)
            => !float.IsNaN(_swimJumpStarted) && Time.time - _swimJumpStarted < profile.TimerInterval;

        private bool TryApplySurfaceWaterJump(PlayerSwimJumpDynamics profile,
            CollisionFlags contacts, out string detail)
        {
            detail = "The server water-jump geometry could not be qualified.";
            if (!TryObserveWaterJumpGeometry(out bool allowEdge, out bool blockMultiplier, out detail)) return false;
            // HandleSwimmingJumpInput 182F7FBA2..182F7FD30: equality, not a bit test.
            bool edge = allowEdge || (!blockMultiplier && contacts == CollisionFlags.Sides);
            if (!edge && !float.IsNaN(_swimJumpBlockedUntil))
            { detail = "Surface water-jump input ignored during its native cooldown."; return true; }

            // GroundAngleSlideFactor returns zero while swimming (182F7F3DF).
            // CalculateJumpVerticalSpeed's slippery override precedes jumpMultiplier.
            float factor = profile.OnSlipperySlope ? 0.1f : 1f;
            if (profile.JumpMultiplier > 1f) factor = profile.JumpMultiplier;
            float vertical = MathF.Sqrt((profile.JumpHeight * 2f * factor) * profile.Gravity) * 0.5f;
            if (edge) vertical *= profile.EdgeMultiplier;
            float now = Time.time;
            // Native uses the actor's cached _velocity.x/z, never clears horizontal motion.
            Vector3 velocity = new(_velocity.x, vertical, _velocity.z);
            if (!Finite(velocity) || !float.IsFinite(now) || !Current()) return false;
            _swimBody.velocity = velocity;
            _velocity = velocity;
            _swimJumpStarted = now;
            _swimJumping = true;
            if (!edge) _swimJumpBlockedUntil = now + 1f;
            detail = edge ? "Server edge water jump applied." : "Server surface water jump applied.";
            return true;
        }

        private bool TryObserveWaterJumpGeometry(out bool allowEdge, out bool blockMultiplier, out string detail)
        {
            allowEdge = blockMultiplier = false;
            detail = "Water-jump overlap or native trigger dispatch is incomplete or ambiguous.";
            _waterJumpOverlaps ??= new(64);
            _waterJumpDetectors ??= new(4);
            _waterJumpBlockers ??= new(4);
            Vector3 position = _transform.position;
            Quaternion rotation = _transform.rotation;
            Vector3 center = position + rotation * _swimCapsule.center;
            Vector3 extent = rotation * Vector3.up * (_swimCapsule.height * 0.5f - _swimCapsule.radius);
            int mask = 0;
            for (int layer = 0; layer < 32; layer++)
                if (!Physics.GetIgnoreLayerCollision(_layer, layer)) mask |= 1 << layer;
            if (!Physics.autoSyncTransforms) Physics.SyncTransforms();
            try
            {
                // Query this actor's actual capsule. A complete fresh overlap replaces
                // the native LocalPlayer trigger-membership cache, including exits and
                // disabled/destroyed blockers; never read their shared _setBlockTime.
                int count = Physics.defaultPhysicsScene.OverlapCapsule(center - extent, center + extent,
                    _swimCapsule.radius, _waterJumpOverlaps, mask, QueryTriggerInteraction.Collide);
                if (count < 0 || count >= _waterJumpOverlaps.Length) return false;
                bool? edgeResult = null;
                for (int i = 0; i < count; i++)
                {
                    var other = _waterJumpOverlaps[i];
                    if (!SwimAlive(other)) return false;
                    if (!other.isTrigger || other.transform.IsChildOf(_transform)
                        || Physics.GetIgnoreCollision(_swimCapsule, other)) continue;
                    // Compound actor triggers must not grant permission outside the
                    // owned solid capsule. ComputePenetration accepts this capsule pair.
                    if (!Physics.ComputePenetration(_swimCapsule, position, rotation,
                        other, other.transform.position, other.transform.rotation, out _, out _)) continue;
                    if (!ObserveWaterJumpComponents(other.gameObject, position, rotation,
                        ref edgeResult, ref blockMultiplier)) return false;
                    var body = other.attachedRigidbody;
                    if (SwimAlive(body) && body.gameObject.Pointer != other.gameObject.Pointer
                        && !ObserveWaterJumpComponents(body.gameObject, position, rotation,
                            ref edgeResult, ref blockMultiplier)) return false;
                }
                allowEdge = edgeResult == true;
                return Current() && _transform.position.Equals(position) && _transform.rotation.Equals(rotation);
            }
            finally { for (int i = 0; i < _waterJumpOverlaps.Length; i++) _waterJumpOverlaps[i] = null; }
        }

        private bool ObserveWaterJumpComponents(GameObject owner, Vector3 position, Quaternion rotation,
            ref bool? edgeResult, ref bool blockMultiplier)
        {
            owner.GetComponents<PlayerJumpOutOfWaterDetection>(_waterJumpDetectors);
            owner.GetComponents<PlayerBlockJumpOutOfWaterMultiplier>(_waterJumpBlockers);
            if (_waterJumpDetectors.Count > 4 || _waterJumpBlockers.Count > 4) return false;
            for (int i = 0; i < _waterJumpDetectors.Count; i++)
            {
                var detector = _waterJumpDetectors[i];
                if (!SwimAlive(detector) || !detector.isActiveAndEnabled
                    || IL2CPP.il2cpp_object_get_class(detector.Pointer)
                        != Il2CppClassPointerStore<PlayerJumpOutOfWaterDetection>.NativeClassPtr
                    || !SwimAlive(detector._collider) || !detector._collider.enabled
                    || !Range(detector._angleThreshold, 0, 180)) return false;
                // PlayerJumpOutOfWaterDetection.OnTriggerStay 18337285E..18337295F.
                Vector3 direction = detector._collider.bounds.center - position;
                direction.y = 0;
                Vector3 forward = rotation * Vector3.forward;
                forward.y = 0;
                if (!Finite(direction) || !Finite(forward)) return false;
                bool allowed = Vector3.Angle(direction.normalized, forward.normalized) <= detector._angleThreshold;
                // Native detectors write a shared bool in callback order. Do not invent
                // a winner when overlapping detectors disagree for this actor.
                if (edgeResult.HasValue && edgeResult.Value != allowed) return false;
                edgeResult = allowed;
            }
            for (int i = 0; i < _waterJumpBlockers.Count; i++)
            {
                var blocker = _waterJumpBlockers[i];
                if (!SwimAlive(blocker)
                    || IL2CPP.il2cpp_object_get_class(blocker.Pointer)
                        != Il2CppClassPointerStore<PlayerBlockJumpOutOfWaterMultiplier>.NativeClassPtr) return false;
                // Native OnTriggerStay explicitly ignores disabled components. A live
                // overlap refreshes membership; ClearBlock/OnDisable remove it on exit.
                if (blocker.isActiveAndEnabled) blockMultiplier = true;
            }
            return true;
        }
    }

    // Native RigidBodyCollisionFlags receives proxied messages, not Unity collision
    // messages directly. Forward only this owned body's actual solver contacts.
    // Its Awake/FixedUpdate/capsule contact path is actor-local in the qualified build
    // (1835C7DA0, 1835C7FE0, 1835C8190, 1835C8890, 1835C8BA0).
    public sealed class PlayerSwimCollisionReceiver : MonoBehaviour
    {
        internal PlayerGroundMovement Owner;

        public PlayerSwimCollisionReceiver(IntPtr pointer) : base(pointer) { }

        public void OnCollisionEnter(Collision collision) => Owner?.ReceiveSwimCollision(collision, 0);
        public void OnCollisionStay(Collision collision) => Owner?.ReceiveSwimCollision(collision, 1);
        public void OnCollisionExit(Collision collision) => Owner?.ReceiveSwimCollision(collision, 2);
    }
}
