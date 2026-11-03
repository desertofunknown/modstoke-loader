// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerClientMovement
    {
        private readonly Il2CppReferenceArray<Collider> _overlaps = new(64);

        private void ReconcileNativeState()
        {
            if (CartMigrationHeld || _transition != null) return;
            ReceiveNativeState();
            if (_disposed) return;
            var state = _nativeState;
            if (state.Motion.Sequence == 0) return;
            _nativeState = default;
            var binding = _nativeMode;
            int index = FindNativePrediction(state.Motion.InputSequence);
            if (binding == null || state.Key != binding.Key || !Current(true) || index < 0
                || !_nativePredictions[index].Sent)
            { Stop("Native acknowledgement outlived its exact action/prediction boundary."); return; }
            var prediction = _nativePredictions[index];
            // Check historical orientation, never today's view. A changed flight heading
            // needs native action-state resynchronization, not a camera or body rewind.
            if (!SameOrientation(state.Motion.Rotation, prediction.Rotation))
            { Stop("Native authoritative orientation requires action-state resynchronization."); return; }

            if (IsCartMode(binding.Key.Mode))
            {
                // Seat writers already followed the CURRENT cart pose in their normal
                // LateUpdate/LastUpdate. A delayed world-space error must not pull the
                // passenger off that cart. Only acknowledge the same settled local seat.
                if (!prediction.SeatSettled || binding.SeatLerp != 1 || !Exact(state.Motion.Velocity, Vector3.zero)
                    || (state.Motion.Position - prediction.Position).sqrMagnitude > 0.000001f)
                { Stop("Authoritative seat geometry differs from the bound native seat."); return; }
                RetireNativePredictions(state.Motion);
                return;
            }

            if (binding.Climb != null)
            {
                ReconcileClimbState(binding, state.Motion);
                return;
            }
            if (binding.Key.Mode != NativeMode.HangGlider)
            { Stop("Native acknowledgement has no supported correction owner."); return; }

            var status = _history.TryPrepareCorrection(state.Motion, _limits.Position, _limits.Velocity, out var correction);
            if (status == PlayerCorrectionStatus.StaleState) return;
            if (status != PlayerCorrectionStatus.Prepared)
            { Stop("Native glider state requires resynchronization (" + status + ")."); return; }
            bool attempted = false;
            try
            {
                var body = binding.MotionBody;
                var position = body.position;
                var velocity = body.velocity;
                var rotation = body.rotation;
                var destination = position + correction.PositionOffset;
                var correctedVelocity = velocity + correction.VelocityOffset;
                if (!Finite(destination) || !Finite(correctedVelocity)
                    || !NativeGuideShapeMatches(binding)
                    || !GliderCorrectionClear(binding, position, correction.PositionOffset))
                { Stop("Glider correction has no qualified native collision shape or clear path."); return; }
                if (!Current() || !Exact(body.position, position) || !Exact(body.velocity, velocity) || !Exact(body.rotation, rotation))
                { Stop("Native guide changed during correction preflight."); return; }
                if (!Exact(correction.PositionOffset, Vector3.zero))
                {
                    attempted = true;
                    body.position = destination;
                    if (!Current()) { Stop("Native guide retired during position correction."); return; }
                }
                if (!Exact(correction.VelocityOffset, Vector3.zero))
                {
                    attempted = true;
                    body.velocity = correctedVelocity;
                    if (!Current()) { Stop("Native guide retired during velocity correction."); return; }
                }
                // The original glider still owns heading/pitch/tilt, damping, FOV, body
                // activation and exit. Do not replay FixedUpdate or its input callbacks.
                if (!Current(true) || !Exact(body.position, destination) || !Exact(body.velocity, correctedVelocity)
                    || !Exact(body.rotation, rotation) || !_history.TryCommitCorrection(correction))
                { Stop("Native guide correction readback is unconfirmed; history was retired."); return; }
                RetireNativePredictions(state.Motion);
            }
            catch (Exception error)
            {
                Stop((attempted ? "Native guide correction is unconfirmed" : "Native guide correction preflight failed")
                    + " (" + error.GetType().Name + "); no retry or rollback was attempted.");
            }
        }

        private void RetireNativePredictions(PlayerMotionState state)
        {
            while (_nativeCount > 0 && _nativePredictions[_nativeHead].Sequence <= state.InputSequence)
            {
                _nativePredictions[_nativeHead] = default;
                _nativeHead = (_nativeHead + 1) % _nativePredictions.Length;
                _nativeCount--;
            }
            _lastNativeState = state.Sequence;
            _lastNativeAck = state.InputSequence;
        }

        private static bool NativeGuideShapeMatches(NativeBinding binding)
        {
            var current = binding.MotionBody.GetComponentsInChildren<Collider>(true);
            if (current.Length != binding.Colliders.Length) return false;
            for (int i = 0; i < current.Length; i++)
            {
                var collider = current[i];
                if (!Same(collider, binding.Colliders[i]) || !Same(collider.attachedRigidbody, binding.MotionBody)) return false;
            }
            return true;
        }

        private bool GliderCorrectionClear(NativeBinding binding, Vector3 position, Vector3 offset)
        {
            if (Exact(offset, Vector3.zero)) return true;
            // BeginGliding creates a colliderless guide and a ParentConstraint on the
            // original player. Enclose its real obstacle collider for the whole move,
            // including the source offset. This intentionally rejects nearby geometry
            // rather than inventing a guide collider or skipping native trigger crossings.
            if (binding.Colliders.Length != 0) return false;
            var obstacle = binding.Obstacle;
            var bounds = obstacle.bounds;
            float radius = bounds.extents.magnitude + (bounds.center - _rig.Root.transform.position).magnitude
                + binding.GuideTranslationOffset.magnitude + _limits.Clearance;
            if (!Range(radius, 0.001f, 10)) return false;
            try
            {
                int count = Physics.OverlapCapsuleNonAlloc(position, position + offset, radius,
                    _overlaps, ~0, QueryTriggerInteraction.Collide);
                if (count < 0 || count >= _overlaps.Length) return false;
                for (int i = 0; i < count; i++)
                {
                    var other = _overlaps[i];
                    if (!Alive(other)) return false;
                    if (Same(other, obstacle) || Same(other.attachedRigidbody, _rig.Body)
                        || Physics.GetIgnoreLayerCollision(obstacle.gameObject.layer, other.gameObject.layer)
                        || Physics.GetIgnoreCollision(obstacle, other)) continue;
                    return false;
                }
                return true;
            }
            finally { for (int i = 0; i < _overlaps.Length; i++) _overlaps[i] = null; }
        }

        private static bool SameOrientation(Quaternion left, Quaternion right)
        {
            if (!UnitRotation(left) || !UnitRotation(right)) return false;
            float dot = left.x * right.x + left.y * right.y + left.z * right.z + left.w * right.w;
            float a = left.x * left.x + left.y * left.y + left.z * left.z + left.w * left.w;
            float b = right.x * right.x + right.y * right.y + right.z * right.z + right.w * right.w;
            return MathF.Abs(dot) / MathF.Sqrt(a * b) >= 0.999999f;
        }

        private void Reconcile(PlayerMotionState state)
        {
            // A ground ACK sent before a native action (or its exit) is not a correction
            // for the new prediction epoch, even if it arrives after ground control resumes.
            if (_transition != null || !_bridge.IsGameplayActive || state.MovementTransitionSequence != _modeSequence
                || state.InputSequence <= _groundInputFloor) return;
            // Yaw belongs to the historical input, not the present view. Do not rotate the
            // retained body/camera backwards, but reject a server mode with a tilted body.
            if (!Upright(state.Rotation * Vector3.up))
            { Stop("Authoritative body orientation is outside the supported ground mode."); return; }
            var status = _history.TryPrepareCorrection(state, _limits.Position, _limits.Velocity, out var correction);
            if (status == PlayerCorrectionStatus.StaleState) return;
            if (status != PlayerCorrectionStatus.Prepared)
            { Stop("Authoritative state requires resynchronization (" + status + ")."); return; }
            bool attempted = false;
            try
            {
                if (!CheckMode()) { Stop("Retained rig changed before correction."); return; }
                var body = _rig.Body;
                var before = body.position;
                var velocity = body.velocity;
                var rotation = body.rotation;
                var destination = before + correction.PositionOffset;
                var correctedVelocity = velocity + correction.VelocityOffset;
                if (!Finite(destination) || !Finite(correctedVelocity)
                    || !CorrectionClear(before, rotation, correction.PositionOffset))
                { Stop("Bounded correction would cross a collision/trigger or an unsupported body shape."); return; }
                // No clamping, partial slide, teleport fallback or retry. History is shifted only
                // after the entire exact offset was accepted by this same native body lifetime.
                if (!Current() || !Exact(body.position, before) || !Exact(body.velocity, velocity)
                    || !Exact(body.rotation, rotation))
                { Stop("Retained body changed during correction preflight."); return; }
                if (!Exact(correction.PositionOffset, Vector3.zero))
                {
                    attempted = true;
                    body.position = destination;
                    if (!Current()) { Stop("Retained body retired during position correction."); return; }
                }
                if (!Exact(correction.VelocityOffset, Vector3.zero))
                {
                    attempted = true;
                    body.velocity = correctedVelocity;
                    if (!Current()) { Stop("Retained body retired during velocity correction."); return; }
                }
                if (!Current(true) || !Exact(body.position, destination) || !Exact(body.velocity, correctedVelocity)
                    || !Exact(body.rotation, rotation) || !_history.TryCommitCorrection(correction))
                    Stop("Native correction readback is unconfirmed; this history will not be reused.");
            }
            catch (Exception error)
            {
                Stop((attempted ? "Native correction is unconfirmed" : "Correction preflight failed")
                    + " (" + error.GetType().Name + "); no retry or rollback was attempted.");
            }
        }

        private bool CorrectionClear(Vector3 position, Quaternion rotation, Vector3 offset)
        {
            if (Exact(offset, Vector3.zero)) return true;
            // Only root-attached primitive shapes are supported. Moving child colliders would
            // require their own animation/physics pose history; do not silently omit them.
            float radius = 0;
            bool any = false;
            foreach (var collider in _rig.Colliders)
            {
                if (!collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                if (!Same(collider.transform, _rig.Body.transform) || collider.isTrigger) return false;
                float extent;
                var capsule = collider.TryCast<CapsuleCollider>();
                var sphere = collider.TryCast<SphereCollider>();
                var box = collider.TryCast<BoxCollider>();
                if (capsule != null)
                    extent = capsule.center.magnitude + MathF.Max(capsule.height * 0.5f, capsule.radius);
                else if (sphere != null) extent = sphere.center.magnitude + sphere.radius;
                else if (box != null) extent = box.center.magnitude + box.size.magnitude * 0.5f;
                else return false;
                if (!Range(extent, 0.001f, 10)) return false;
                radius = MathF.Max(radius, extent);
                any = true;
            }
            if (!any) return false;
            if (!ClearAt(position, rotation, radius + _limits.Clearance)) return false;
            float distance = offset.magnitude;
            if (!float.IsFinite(distance) || distance <= 0) return false;
            // Native SweepTest uses the body's actual physics shapes, not render interpolation.
            // Include trigger crossings: their enter/exit history cannot be reconstructed here.
            if (_rig.Body.SweepTest(offset / distance, out _, distance + _limits.Clearance, QueryTriggerInteraction.Collide))
                return false;
            return ClearAt(position + offset, rotation, radius + _limits.Clearance);
        }

        private bool ClearAt(Vector3 position, Quaternion rotation, float radius)
        {
            try
            {
                int count = Physics.OverlapSphereNonAlloc(position, radius, _overlaps, ~0, QueryTriggerInteraction.Collide);
                if (count < 0 || count >= _overlaps.Length) return false;
                for (int i = 0; i < count; i++)
                {
                    var other = _overlaps[i];
                    if (!Alive(other)) return false;
                    if (Same(other.attachedRigidbody, _rig.Body)) continue;
                    var otherTransform = other.transform;
                    var otherPosition = otherTransform.position;
                    var otherRotation = otherTransform.rotation;
                    var otherBody = other.attachedRigidbody;
                    if (otherBody != null)
                    {
                        if (!Alive(otherBody)) return false;
                        var bodyTransform = otherBody.transform;
                        var inverse = Quaternion.Inverse(bodyTransform.rotation);
                        otherPosition = otherBody.position + otherBody.rotation * (inverse * (otherPosition - bodyTransform.position));
                        otherRotation = otherBody.rotation * inverse * otherRotation;
                    }
                    foreach (var collider in _rig.Colliders)
                    {
                        if (!collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                        if (Physics.GetIgnoreLayerCollision(collider.gameObject.layer, other.gameObject.layer)
                            || Physics.GetIgnoreCollision(collider, other)) continue;
                        if (Physics.ComputePenetration(collider, position, rotation, other, otherPosition, otherRotation,
                            out _, out var distance) && distance > 0) return false;
                    }
                }
                return true;
            }
            finally { for (int i = 0; i < _overlaps.Length; i++) _overlaps[i] = null; }
        }

        private static bool Exact(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
        private static bool Exact(Quaternion a, Quaternion b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
    }
}
