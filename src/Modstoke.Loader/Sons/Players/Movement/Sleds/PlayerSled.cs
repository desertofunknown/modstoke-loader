// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Loader;
using global::Sons.Animation;
using UnityEngine;

namespace Modstoke.Sons
{
    public enum PlayerSledPhase : byte { Entering, Riding, Exiting, Exited }

    /// <summary>Local motor progress, not a serialized or authenticated migration snapshot.</summary>
    public readonly record struct PlayerSledMotorState(
        PlayerSledState Riding, PlayerSledPhase Phase, float CapsuleProgress);

    /// <summary>Values for an already riding sled, not an entry/exit coroutine snapshot.</summary>
    public readonly record struct PlayerSledState(
        float SteeringVelocity, float HorizontalSteering, float AnimationBlend,
        float IdleBlend, float StopTimeRemaining, Vector2 OriginalCameraRotationRange,
        bool ForceStop, bool StartedBelowTerrain);

    /// <summary>
    /// Native rider geometry for the qualified single-capsule layout. StartSledding
    /// changes playerCollider.radius while the fixed step changes FPC._primaryCollider;
    /// capture refuses layouts where these are not the same collider.
    /// </summary>
    public readonly record struct PlayerSledProfile(
        float PrimaryHeight, float PrimaryCenterY, float PlayerColliderRadius, int GroundLayerMask);

    /// <summary>
    /// Authoritative observations for one fixed step. HeightFromTerrain is the actor's
    /// treeHitTrigger.heightFromTerrain, not raycast distance. TerrainSurfaceHeight must
    /// come from a qualified terrain observation, not a failed-query fallback. InSnow
    /// must reproduce that actor's PlayerStats.IsInSnow(), not the host's LocalPlayer.
    /// </summary>
    public readonly record struct PlayerSledEnvironment(
        bool InWater, bool InSnow, float HeightFromTerrain, float TerrainSurfaceHeight,
        float JumpingTimer, float SpeedSquaredFromDeltaPosition,
        int BaseStateTagHash, int BaseStateShortNameHash);

    /// <summary>
    /// Riding-sled values and actor-independent calculations from the matched native
    /// SleddingUpdate/SleddingFixedUpdate and UpdateSleddingFriction bodies. Does not
    /// call native actions, read LocalPlayer or prove transfer/entry/exit readiness.
    /// </summary>
    public static class PlayerSled
    {
        // Matched StartSledding / ScaleCapsuleForSledding constants.
        public const float RidingRadius = 0.328f;
        internal const float CapsuleTransitionSeconds = 0.18f;

        /// <summary>
        /// Observe the exact retained actor before pausing it. Caller must separately
        /// establish that capsule/finalization coroutines are settled and own snapshot
        /// authentication. Capturing these values is not an authority grant.
        /// </summary>
        public static bool TryCapture(PlayerAnimatorControl animation, FirstPersonCharacter character,
            out PlayerSledState state, out PlayerSledProfile profile,
            out string detail)
        {
            state = default;
            profile = default;
            detail = "An exact, already riding actor with settled native transitions is required.";
            if (!GameBuild.IsRecognized) return false;
            try
            {
                if (!Alive(animation) || !Alive(character) || !animation._IsSledding_k__BackingField
                    || animation._playerBaseState.tagHash != AnimationHashes.shellRideHash
                    || animation._playerBaseState.shortNameHash == AnimationHashes.shellRideToIdleHash
                    || animation._isConnectedToLogSled || animation.fullyAttachedToSled
                    || !Same(animation.rb, character._rigidbody)
                    || !Same(animation._playerScriptSetup, character.setup)
                    || !Same(animation.rootTr, character._rigidbody.transform)
                    || !Same(animation._transform, animation.transform)
                    || Same(animation._transform, animation.rootTr)
                    || !Alive(animation.playerCollider) || !Alive(character._primaryCollider)
                    || !Same(animation.playerCollider, character._primaryCollider)
                    || !Same(animation.playerCollider.attachedRigidbody, character._rigidbody)
                    || !Same(character._primaryCollider.attachedRigidbody, character._rigidbody)) return false;

                var captured = new PlayerSledState(animation._currentSledVelocity,
                    animation._sledHorizontalVelocity, animation._sleddingBlendVal,
                    animation._smoothSleddingBlend, animation._sleddingStopTimer - Time.time,
                    animation._originalCameraRotationRange, animation._forceStopSledding,
                    animation._IsSleddingBelowTerrain_k__BackingField);
                var geometry = new PlayerSledProfile(character.crouchHeight,
                    character._crouchColliderCenter, animation.playerCollider.radius, animation._layerMask.value);
                if (!Valid(captured) || !Valid(geometry)) return false;
                state = captured;
                profile = geometry;
                detail = "Riding values captured; controls, cameras, coroutines and physics were not changed.";
                return true;
            }
            catch (Exception error)
            { detail = "Sled capture failed (" + error.GetType().Name + ")."; return false; }
        }

        /// <summary>
        /// The original Update cadence is significant: call once per authoritative
        /// steering update, independently of the physics step. Returns root yaw in degrees.
        /// Input is authenticated horizontal intent in [-1,1], not a client-provided angle.
        /// </summary>
        public static bool TryAdvanceSteering(PlayerSledState state, float horizontal,
            float deltaTime, out PlayerSledState next, out float yawDegrees)
        {
            next = state;
            yawDegrees = 0;
            if (!Valid(state) || !float.IsFinite(horizontal) || MathF.Abs(horizontal) > 1
                || !float.IsFinite(deltaTime) || deltaTime <= 0) return false;
            float velocity = state.SteeringVelocity;
            float steering = Mathf.SmoothDamp(state.HorizontalSteering, horizontal * 75,
                ref velocity, 0.1f, float.PositiveInfinity, deltaTime);
            // Native order is new steering -> previous blend, not the reverse.
            float blend = Mathf.SmoothStep(steering, state.AnimationBlend, deltaTime / 9);
            if (MathF.Abs(steering) <= 0.0001f) velocity = 0;
            else yawDegrees = steering * deltaTime;
            next = state with { SteeringVelocity = velocity, HorizontalSteering = steering, AnimationBlend = blend };
            return Valid(next) && float.IsFinite(yawDegrees);
        }

        internal static Vector3 DryForce(Vector3 velocity, Vector3 sledForward, Vector3 sledRight,
            Vector3 surfaceNormal, float slope)
        {
            // SleddingFixedUpdate 0x35B6DB0: replace normalized velocity Y before
            // measuring slip; the braking term uses that unnormalized replacement.
            var direction = velocity.normalized;
            direction.y = sledForward.y;
            float angle = (float)Math.Acos(Mathf.Clamp(Vector3.Dot(direction.normalized, sledForward.normalized), -1, 1));
            var forward = Vector3.Cross(sledRight, surfaceNormal);
            return forward + forward * slope * -20 + forward * ((angle * 16 + 1) * 15)
                - direction * (angle * 90);
        }

        internal static bool Valid(PlayerSledState s)
            => float.IsFinite(s.SteeringVelocity) && float.IsFinite(s.HorizontalSteering)
                && float.IsFinite(s.AnimationBlend) && float.IsFinite(s.IdleBlend)
                && float.IsFinite(s.StopTimeRemaining) && float.IsFinite(s.OriginalCameraRotationRange.x)
                && float.IsFinite(s.OriginalCameraRotationRange.y);

        internal static bool Valid(PlayerSledProfile p)
            => float.IsFinite(p.PrimaryHeight) && p.PrimaryHeight > 0
                && float.IsFinite(p.PrimaryCenterY) && float.IsFinite(p.PlayerColliderRadius)
                && p.PlayerColliderRadius > 0;

        internal static bool Valid(PlayerSledEnvironment e)
            => float.IsFinite(e.HeightFromTerrain) && float.IsFinite(e.TerrainSurfaceHeight)
                && float.IsFinite(e.JumpingTimer) && float.IsFinite(e.SpeedSquaredFromDeltaPosition)
                && e.SpeedSquaredFromDeltaPosition >= 0;

        internal static bool Alive(UnityEngine.Object value)
            => value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero && value != null;
        internal static bool Same(UnityEngine.Object a, UnityEngine.Object b)
            => Alive(a) && Alive(b) && a.Pointer == b.Pointer && a.GetInstanceID() == b.GetInstanceID();
        internal static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        internal static bool Finite(Quaternion q)
            => float.IsFinite(q.x) && float.IsFinite(q.y) && float.IsFinite(q.z) && float.IsFinite(q.w);
    }
}
