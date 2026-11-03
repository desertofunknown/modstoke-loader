// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using HarmonyLib;
using TheForest.Player.Actions;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerClientMovement
    {
        /// <summary>Check the authenticated action-to-anchor mapping, never proximity or a client pose.</summary>
        public delegate bool ValidateClimbAnchor(Guid migrationId, ulong generation, ulong networkId,
            NativeModeKey key, Transform anchor);

        private bool _climbFixedStep;
        private int _climbAnimationFrame = -1;

        /// <summary>
        /// Observe an already completed, committed climb on the original rig. This does
        /// not issue entry, settle a coroutine, approve an anchor or enable climb actions.
        /// Bounds and anchor mapping must come from the authenticated native action.
        /// </summary>
        public bool TryBeginClimb(NativeModeKey key, PlayerClimb climb,
            Transform authorizedAnchor, Bounds approvedLocalBounds, ValidateClimbAnchor validateAnchor,
            out string detail)
        {
            detail = "Climb entry choreography is unsupported here; bind only an authorized, settled native climb after commit.";
            try
            {
                if (!CanBindNative(key) || !IsClimbMode(key.Mode) || key.SeatIndex != -1
                    || _transition != null || climb == null || climb.IsDisposed || validateAnchor == null
                    || !Alive(authorizedAnchor) || !Finite(approvedLocalBounds.center)
                    || !Finite(approvedLocalBounds.size) || approvedLocalBounds.size.x <= 0
                    || approvedLocalBounds.size.y <= 0 || approvedLocalBounds.size.z <= 0
                    || !climb.TryRead(out var state, out _)) return false;
                var binding = new ClimbBinding(this, climb, authorizedAnchor, approvedLocalBounds,
                    validateAnchor, state);
                if (!binding.TryRead(_rig, key, out _)) return false;
                _climbAnimationFrame = -1;
                return BindNative(new NativeBinding(key, _rig, binding), out detail);
            }
            catch (Exception error)
            { detail = "Settled climb binding failed (" + error.GetType().Name + ")."; return false; }
        }

        private static void InstallNativeClimb(HarmonyLib.Harmony harmony)
        {
            var animatorMove = AccessTools.DeclaredMethod(typeof(PlayerAnimatorControl),
                nameof(PlayerAnimatorControl.OnAnimatorMove), Type.EmptyTypes);
            if (animatorMove == null || animatorMove.IsStatic || animatorMove.ReturnType != typeof(void))
                throw new MissingMethodException("Recognized native climb animation boundary is unavailable.");
            harmony.Patch(animatorMove,
                prefix: new HarmonyMethod(typeof(PlayerClientMovement), nameof(BeforeClimbAnimation)) { priority = Priority.Last },
                postfix: new HarmonyMethod(typeof(PlayerClientMovement), nameof(AfterClimbAnimation)) { priority = Priority.Last });

            RefuseEntry(typeof(PlayerClimbRopeAction), nameof(PlayerClimbRopeAction.EnterClimbRopeFromBottom), typeof(activateClimb));
            RefuseEntry(typeof(PlayerClimbRopeAction), nameof(PlayerClimbRopeAction.EnterClimbRopeFromTop), typeof(activateClimbTop));
            RefuseEntry(typeof(PlayerClimbWallAction), nameof(PlayerClimbWallAction.enterClimbWall), typeof(Transform));
            RefuseEntry(typeof(PlayerClimbWallAction), nameof(PlayerClimbWallAction.enterClimbWallTop), typeof(Transform));
            RefuseEntry(typeof(playerCraneClimbAction), nameof(playerCraneClimbAction.EnterCraneClimb), typeof(Transform));

            void RefuseEntry(Type type, string name, Type argument)
            {
                var method = AccessTools.DeclaredMethod(type, name, new[] { argument });
                if (method == null || method.IsStatic || method.ReturnType != typeof(void))
                    throw new MissingMethodException("Recognized climb entry boundary is unavailable: " + name);
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(PlayerClientMovement),
                    nameof(BeforeUnsupportedClimbEntry)) { priority = Priority.Last });
            }
        }

        private static bool BeforeUnsupportedClimbEntry(Behaviour __instance, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            var active = _active;
            if (active == null || active._disposed || Environment.CurrentManagedThreadId != _hookThread) return true;
            try
            {
                if (!active.OwnsAction(__instance)) return true;
                active.NativeTransitionDetail = "Climb entry refused: ordered native coroutine/inventory choreography is not implemented.";
            }
            catch (Exception error) { active.Stop("Climb entry identity is unconfirmed (" + error.GetType().Name + ")."); }
            return false;
        }

        private static void BeforeClimbAnimation(PlayerAnimatorControl __instance, bool __runOriginal)
        {
            var active = ForNative(__instance);
            if (active?._nativeMode.Climb == null) return;
            try
            {
                bool fixedStep = Time.inFixedTimeStep;
                double time = Time.fixedTimeAsDouble;
                float dt = Time.fixedDeltaTime;
                if (!__runOriginal || !active.Current(true) || active._insideFixed || active._awaitingPhysics
                    || fixedStep != (__instance._animator.updateMode == AnimatorUpdateMode.AnimatePhysics)
                    || (fixedStep ? !double.IsFinite(time) || !Range(dt, 0.001f, 0.1f)
                        || (!double.IsNaN(active._fixedTime) && time <= active._fixedTime)
                        : active._climbAnimationFrame == Time.frameCount))
                { active.Stop("Climb animation no longer has one exact native prediction boundary."); return; }
                // Matched UpdateRootMotionAndClimbing reads _lastInputMovement.y (0x55C),
                // not the next _currentInputMovement. Never query or consume input again.
                active._nativeSample = active.CaptureNativeInput(__instance._lastInputMovement, false, false, false, 0);
                active._climbFixedStep = fixedStep;
                active._climbAnimationFrame = Time.frameCount;
                if (fixedStep) { active._fixedTime = time; active._fixedDeltaTime = dt; }
                active._insideFixed = true;
            }
            catch (Exception error) { active.Stop("Native climb capture failed (" + error.GetType().Name + ")."); }
        }

        private static void AfterClimbAnimation(PlayerAnimatorControl __instance, bool __runOriginal)
        {
            var active = ForNative(__instance);
            if (active?._nativeMode.Climb == null) return;
            try
            {
                if (!__runOriginal || !active._insideFixed || !active.Current(true)
                    || active._climbFixedStep != Time.inFixedTimeStep
                    || active._climbAnimationFrame != Time.frameCount
                    || (active._climbFixedStep && (active._fixedTime != Time.fixedTimeAsDouble
                        || active._fixedDeltaTime != Time.fixedDeltaTime)))
                { active.Stop("Native climb action changed during its animation evaluation."); return; }
                active._insideFixed = false;
                if (active._climbFixedStep) active._awaitingPhysics = true;
                else active.CompleteClimbSample();
            }
            catch (Exception error) { active.Stop("Native climb completion failed (" + error.GetType().Name + ")."); }
        }

        private void CompleteClimbPhysics()
        {
            if (!_awaitingPhysics) return;
            if (!_climbFixedStep || _insideFixed || Time.fixedTimeAsDouble != _fixedTime || Time.fixedDeltaTime != _fixedDeltaTime)
            { Stop("Climb physics no longer matches its native animation sample."); return; }
            _awaitingPhysics = false;
            CompleteClimbSample();
        }

        private void CompleteClimbSample()
        {
            if (!_nativeMode.Climb.TryRead(_rig, _nativeMode.Key, out var state))
            { Stop("Climb readback lost its exact action, anchor or settled animation."); return; }
            RecordNativePrediction(_nativeSample, state.Position, state.Rotation, state.Velocity);
            if (!_disposed) ReconcileNativeState();
        }

        private sealed class ClimbBinding
        {
            private readonly PlayerClientMovement _owner;
            private readonly PlayerClimb _climb;
            private readonly ValidateClimbAnchor _validate;
            private readonly uint _issuedSequence;
            private readonly bool _onRope, _gravityLocked;
            private readonly AnimatorUpdateMode _updateMode;
            internal readonly Transform Anchor;
            internal readonly Bounds LocalBounds;

            internal ClimbBinding(PlayerClientMovement owner, PlayerClimb climb,
                Transform anchor, Bounds bounds, ValidateClimbAnchor validate, PlayerClimbReadback state)
            {
                _owner = owner; _climb = climb; Anchor = anchor; LocalBounds = bounds;
                _validate = validate; _issuedSequence = state.LastIssuedSequence;
                _onRope = state.OnRope; _gravityLocked = state.GravityLocked;
                _updateMode = owner._rig.Animation._animator.updateMode;
            }

            internal bool TryRead(RetainedRig rig, NativeModeKey key, out PlayerClimbReadback state)
            {
                state = default;
                var bridge = _owner._bridge;
                if (_climb.IsDisposed || !Alive(Anchor) || !Anchor.gameObject.activeInHierarchy
                    || Anchor.gameObject.scene.handle != rig.Root.scene.handle || !UnitScale(Anchor.lossyScale)
                    || Anchor.IsChildOf(rig.Root.transform) || !Finite(Anchor.position) || !UnitRotation(Anchor.rotation)
                    || !_validate(bridge.Plan.Id, bridge.Plan.Generation, bridge.NetworkId, key, Anchor)
                    || !_climb.TryRead(out state, out _) || state.NativeCallUncertain
                    || state.LastIssuedSequence != _issuedSequence || state.AnimatorInTransition || !state.RootMotion
                    || state.OnRope != _onRope || state.GravityLocked != _gravityLocked
                    || rig.Animation._isNetworkPlayer || rig.Animation.cliffClimb
                    || !Alive(rig.Animation._animator) || !rig.Animation._animator.enabled
                    || !rig.Animation._animator.applyRootMotion || rig.Animation._animator.updateMode != _updateMode
                    || (_updateMode != AnimatorUpdateMode.Normal && _updateMode != AnimatorUpdateMode.AnimatePhysics)
                    || !Finite(state.Position) || !Finite(state.Velocity)
                    || !UnitRotation(state.Rotation) || !LocalBounds.Contains(Anchor.InverseTransformPoint(state.Position))) return false;
                // The old cliff-climb path has different input/collider writers. It is
                // not the wall action represented by this retained climb helper.
                return key.Mode switch
                {
                    NativeMode.RopeClimb => state.RopeActive && !state.WallActive && !state.CraneActive
                        && Same(state.RopeRoot, Anchor),
                    NativeMode.WallClimb => state.WallActive && !state.RopeActive && !state.CraneActive,
                    NativeMode.CraneClimb => state.CraneActive && !state.RopeActive && !state.WallActive
                        && Same(state.CraneTrigger, Anchor),
                    _ => false
                };
            }
        }

        private void ReconcileClimbState(NativeBinding binding, PlayerMotionState state)
        {
            var status = _history.TryPrepareCorrection(state, _limits.Position, _limits.Velocity, out var correction);
            if (status == PlayerCorrectionStatus.StaleState) return;
            if (status != PlayerCorrectionStatus.Prepared)
            { Stop("Climb acknowledgement requires resynchronization (" + status + ")."); return; }
            bool attempted = false;
            try
            {
                var climb = binding.Climb;
                if (!climb.TryRead(_rig, binding.Key, out var before))
                { Stop("Native climb changed before correction."); return; }
                var anchorPosition = climb.Anchor.position;
                var anchorRotation = climb.Anchor.rotation;
                var localOffset = Quaternion.Inverse(anchorRotation) * correction.PositionOffset;
                var destination = before.Position + correction.PositionOffset;
                var velocity = before.Velocity + correction.VelocityOffset;
                // No lateral attachment rewrite, rotation rewind or kinematic velocity
                // assignment. Those require a canonical native action-state handoff.
                if (!Finite(destination) || !Finite(velocity) || Mathf.Abs(localOffset.x) > 0.000001f
                    || Mathf.Abs(localOffset.z) > 0.000001f
                    || (before.Kinematic && !Exact(correction.VelocityOffset, Vector3.zero))
                    || !climb.LocalBounds.Contains(climb.Anchor.InverseTransformPoint(destination))
                    || !ClimbCorrectionClear(before.Position, correction.PositionOffset))
                { Stop("Climb correction exceeds the authorized attachment or has no proven clear path."); return; }
                if (!Current(true) || !climb.TryRead(_rig, binding.Key, out var current)
                    || !Exact(current.Position, before.Position) || !Exact(current.Velocity, before.Velocity)
                    || !Exact(current.Rotation, before.Rotation) || !Exact(climb.Anchor.position, anchorPosition)
                    || !Exact(climb.Anchor.rotation, anchorRotation))
                { Stop("Climb action or anchor moved during correction preflight."); return; }
                if (!Exact(correction.PositionOffset, Vector3.zero))
                {
                    attempted = true;
                    _rig.Root.transform.position = destination;
                    if (!Current()) { Stop("Climb retired during position correction."); return; }
                }
                if (!Exact(correction.VelocityOffset, Vector3.zero))
                {
                    attempted = true;
                    _rig.Body.velocity = velocity;
                }
                if (!Current(true) || !climb.TryRead(_rig, binding.Key, out var after)
                    || !Exact(after.Position, destination) || !Exact(after.Velocity, velocity)
                    || !Exact(after.Rotation, before.Rotation) || !Exact(climb.Anchor.position, anchorPosition)
                    || !Exact(climb.Anchor.rotation, anchorRotation) || !_history.TryCommitCorrection(correction))
                { Stop("Native climb correction readback is unconfirmed; history was retired."); return; }
                RetireNativePredictions(state);
            }
            catch (Exception error)
            {
                Stop((attempted ? "Native climb correction is unconfirmed" : "Climb correction preflight failed")
                    + " (" + error.GetType().Name + "); no retry or rollback was attempted.");
            }
        }

        private bool ClimbCorrectionClear(Vector3 position, Vector3 offset)
        {
            if (Exact(offset, Vector3.zero)) return true;
            float radius = 0;
            // Include disabled/trigger climb colliders using their actual primitive
            // geometry. Disabled Collider.bounds is empty and cannot prove clearance.
            foreach (var collider in _rig.Colliders)
            {
                var transform = collider.transform;
                var scale = transform.lossyScale;
                if (!Finite(scale) || scale.x <= 0 || scale.y <= 0 || scale.z <= 0) return false;
                Vector3 center;
                float extent;
                var capsule = collider.TryCast<CapsuleCollider>();
                var sphere = collider.TryCast<SphereCollider>();
                var box = collider.TryCast<BoxCollider>();
                if (capsule != null) { center = capsule.center; extent = MathF.Max(capsule.height * 0.5f, capsule.radius); }
                else if (sphere != null) { center = sphere.center; extent = sphere.radius; }
                else if (box != null) { center = box.center; extent = box.size.magnitude * 0.5f; }
                else return false;
                extent = extent * MathF.Max(scale.x, MathF.Max(scale.y, scale.z))
                    + (transform.TransformPoint(center) - position).magnitude;
                if (!Range(extent, 0.001f, 10)) return false;
                radius = MathF.Max(radius, extent);
            }
            if (radius == 0) return false;
            try
            {
                int count = Physics.OverlapCapsuleNonAlloc(position, position + offset, radius + _limits.Clearance,
                    _overlaps, ~0, QueryTriggerInteraction.Collide);
                if (count < 0 || count >= _overlaps.Length) return false;
                for (int i = 0; i < count; i++)
                {
                    var other = _overlaps[i];
                    if (!Alive(other) || !Same(other.attachedRigidbody, _rig.Body)) return false;
                }
                return true;
            }
            finally { for (int i = 0; i < _overlaps.Length; i++) _overlaps[i] = null; }
        }
    }
}
