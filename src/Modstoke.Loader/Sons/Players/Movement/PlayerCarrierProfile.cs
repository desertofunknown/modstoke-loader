// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Bolt;
using global::Sons.Gameplay.ZipLines;
using Modstoke.Loader;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    public readonly record struct PlayerCarrierMotionProfile(
        float WalkSpeed, float RunSpeed, float CrouchSpeed, float StrafeSpeed,
        float BackwardSpeedMultiplier, float Gravity, float MaximumVelocityChange,
        float MaximumVelocity, float JumpHeight, float JumpMultiplier,
        float FixedDeltaTime, float DefaultMass);

    public readonly record struct PlayerCarrierGeometryProfile(
        float Radius, float Height, Vector3 Center, float StandingHeight, Vector3 StandingCenter,
        float CrouchingHeight, float CrouchingCenterY, Vector3 LocalPosition,
        Quaternion LocalRotation, float ContactOffset, bool Enabled, bool IsTrigger);

    public readonly record struct PlayerCarrierBodyProfile(
        float Mass, float Drag, float AngularDrag, float MaximumDepenetrationVelocity,
        float MaximumLinearVelocity, float MaximumAngularVelocity, float SleepThreshold,
        RigidbodyConstraints Constraints, CollisionDetectionMode CollisionDetection,
        RigidbodyInterpolation Interpolation, int SolverIterations, int SolverVelocityIterations,
        bool UseGravity, bool IsKinematic, bool DetectCollisions);

    public readonly record struct PlayerCarrierMaterialProfile(
        bool Present, float StaticFriction, float DynamicFriction, float Bounciness,
        PhysicMaterialCombine FrictionCombine, PhysicMaterialCombine BounceCombine);

    public readonly record struct PlayerCarrierContactProfile(
        float HeightOffset, float GroundAngleMaximumConstruction, int SmoothedLayerMask);

    /// <summary>
    /// Raw pre-pause observations only. These are not a live CanRun/CanJump authorization or
    /// a selected server clamp/slope policy. ForceStopRun does not encode the hidden exhaustion
    /// coroutine's remaining wait; an active wait needs lifecycle admission, not a fabricated zero.
    /// </summary>
    public readonly record struct PlayerCarrierObservations(
        float CurrentSpeed, float ClampInput, float OverrideClampInput, float GroundStableForce,
        float ExtremeAngleStart, float ExtremeAngleMaximum, float TerrainExtremeAngleStart,
        float TerrainExtremeAngleMaximum, float OverallSpeed, float WalkToRunThreshold,
        float RunCostPerSecond, float TimeToRecoverFromRun, float RecoveryRemaining, float StaminaMinimumToRun,
        bool CanJump, bool AllowJump, bool CanRun, bool Locked, bool MovementLocked, bool JumpInputBlocked,
        bool OnSlipperySlope, bool DisableGravity, bool ForceStopRun, bool DoingClampInput,
        bool IsJumping, bool TerrainColliderContact);

    /// <summary>
    /// Immutable native source-rig values, with no Unity object references or invented controller
    /// defaults. Decoding validates representation, not server permission to trust a client.
    /// The adapter must approve and bind this profile to its frozen migration snapshot.
    /// </summary>
    public sealed partial class PlayerCarrierProfile
    {
        public ulong SourceNetworkId { get; }
        public int PrefabId { get; }
        public int StateTypeId { get; }
        public int Layer { get; }
        public int CapsuleLayer { get; }
        public PlayerCarrierMotionProfile Motion { get; }
        public PlayerCarrierGeometryProfile Geometry { get; }
        public PlayerCarrierBodyProfile Body { get; }
        public PlayerCarrierMaterialProfile Material { get; }
        public PlayerCarrierContactProfile Contacts { get; }
        public PlayerCarrierObservations Observations { get; }
        public PlayerInteractionProfile Interaction { get; }
        public Vector3 Velocity { get; }
        public Vector3 AngularVelocity { get; }
        public bool Crouched { get; }
        public bool Grounded { get; }
        public float ZiplineConnectedHeightOffset { get; }

        private PlayerCarrierProfile(ulong sourceNetworkId, int prefabId, int stateTypeId,
            int layer, int capsuleLayer, PlayerCarrierMotionProfile motion,
            PlayerCarrierGeometryProfile geometry, PlayerCarrierBodyProfile body,
            PlayerCarrierMaterialProfile material, PlayerCarrierContactProfile contacts,
            Vector3 velocity, Vector3 angularVelocity, bool crouched, bool grounded,
            PlayerCarrierObservations observations, float ziplineConnectedHeightOffset,
            PlayerInteractionProfile interaction)
        {
            SourceNetworkId = sourceNetworkId; PrefabId = prefabId; StateTypeId = stateTypeId;
            Layer = layer; CapsuleLayer = capsuleLayer; Motion = motion; Geometry = geometry; Body = body; Material = material;
            Contacts = contacts; Velocity = velocity; AngularVelocity = angularVelocity;
            Crouched = crouched; Grounded = grounded; Observations = observations;
            ZiplineConnectedHeightOffset = ziplineConnectedHeightOffset;
            Interaction = interaction;
        }

        /// <summary>
        /// Capture immediately BEFORE lifecycle pause, while these are the real resume values.
        /// Keep this immutable object through the pause and bind it to the snapshot digest. A
        /// recapture of the paused body would incorrectly record its temporary hold settings.
        /// </summary>
        public static bool CaptureProfile(BoltEntity source, out PlayerCarrierProfile profile,
            out string detail)
        {
            profile = null;
            detail = "An attached local PlayerNetwork rig on the player-role game thread is required.";
            if (!PlayerRoles.IsAvailable || !GameBuild.IsRecognized) return false;
            try
            {
                if (!Alive(source) || !source.isAttached || !source.isOwner
                    || source.prefabId != BoltPrefabs.PlayerNetwork || !Same(source, LocalPlayer.Entity)
                    || !Same(source.gameObject, LocalPlayer.GameObject)) return false;
                var root = source.transform;
                var character = LocalPlayer.FpCharacter;
                var body = LocalPlayer.Rigidbody;
                if (!Alive(character) || !Alive(body) || !Same(body.gameObject, source.gameObject)
                    || !character.transform.IsChildOf(root) || !Same(character._rigidbody, body)
                    || !Unit(root.lossyScale)) return false;
                var capsule = character._primaryCollider;
                var contacts = character.collFlags;
                if (!Alive(capsule) || !Alive(contacts) || capsule.direction != 1
                    || !Same(capsule.attachedRigidbody, body) || !Same(contacts._targetCollider, capsule)
                    || !Same(contacts._capsuleCollider, capsule) || !Same(contacts.transform, root)
                    || !Unit(capsule.transform.lossyScale) || !capsule.transform.IsChildOf(root))
                {
                    detail = "The local capsule, root contact tracker and native body are not one qualified rig.";
                    return false;
                }
                var attachment = source._entity;
                var serializer = attachment?.Serializer?.TryCast<NetworkState>();
                var animator = LocalPlayer.Animator;
                if (serializer?.Meta == null || !Alive(animator) || !animator.transform.IsChildOf(root)) return false;
                var material = capsule.sharedMaterial;
                var playerBase = LocalPlayer.PlayerBase;
                if (!Alive(playerBase) || !playerBase.transform.IsChildOf(root)) return false;
                var ziplines = playerBase.GetComponentsInChildren<PlayerZipLineAction>(true);
                if (ziplines.Length != 1 || !Alive(ziplines[0]) || !float.IsFinite(ziplines[0]._connectedHeightOffset)) return false;
                var zipline = ziplines[0];
                if (!TryCaptureInteraction(root, out var interaction, out var interactionIdentity, out detail)) return false;
                var candidate = new PlayerCarrierProfile(source.networkId.PackedValue,
                    source.prefabId.Value, serializer.Meta.TypeId.Value, source.gameObject.layer, capsule.gameObject.layer,
                    new(character.WalkSpeed, character.RunSpeed, character._crouchSpeed, character.StrafeSpeed,
                        character.backwardSpeedMult, character.gravity, character.maxVelocityChange,
                        character.maximumVelocity, character._jumpHeight, character._jumpMultiplier,
                        Time.fixedDeltaTime, character.defaultMass),
                    new(capsule.radius, capsule.height, capsule.center, character._defaultPrimaryColliderHeight,
                        character._defaultPrimaryColliderCenter, character.crouchHeight, character._crouchColliderCenter,
                        root.InverseTransformPoint(capsule.transform.position),
                        Quaternion.Inverse(root.rotation) * capsule.transform.rotation, capsule.contactOffset,
                        capsule.enabled, capsule.isTrigger),
                    new(body.mass, body.drag, body.angularDrag, body.maxDepenetrationVelocity,
                        body.maxLinearVelocity, body.maxAngularVelocity, body.sleepThreshold, body.constraints,
                        body.collisionDetectionMode, body.interpolation, body.solverIterations,
                        body.solverVelocityIterations, body.useGravity, body.isKinematic, body.detectCollisions),
                    material == null ? default : new(true, material.staticFriction, material.dynamicFriction,
                        material.bounciness, material.frictionCombine, material.bounceCombine),
                    new(contacts._heightOffset, contacts._groundAngleMaxConstruction, contacts._smoothedLayerMask.value),
                    body.velocity, body.angularVelocity, character._IsCrouchActive_k__BackingField, character._isGrounded,
                    new(character._speed, character._clampInputVal, character._overrideClampInput,
                        character.groundStableForce, character._extremeAngleGroundedLimitStart,
                        character._extremeAngleGroundedLimitMax, character._terrainExtremeAngleGroundedLimitStart,
                        character._terrainExtremeAngleGroundedLimitMax, animator.GetFloat("overallSpeed"),
                        character._walkToRunThreshold, character._runStaminaCostPerSec, character._timeToRecoverFromRun,
                        character._recoveringFromRun, character._staminaMinNeededToRun, character._canJump,
                        character._allowJump, character._canRun, character.Locked, character._movementLocked,
                        character.jumpInputBlock, character.onSlipperySlope, character._disableGravity,
                        character._forceStopRun, character.doingClampInput, character._isJumping, character._terrainColliderContact),
                    zipline._connectedHeightOffset, interaction);
                if (!ValidateProfile(candidate, out detail)) return false;
                if (!TryCaptureInteraction(root, out var currentInteraction, out var currentInteractionIdentity, out detail)) return false;
                if (interactionIdentity != currentInteractionIdentity || interaction != currentInteraction)
                {
                    detail = "The native interaction geometry or camera/head-follow linkage changed during capture.";
                    return false;
                }
                detail = "The source rig changed during native movement profile capture.";
                if (!Same(source, LocalPlayer.Entity) || !Same(character, LocalPlayer.FpCharacter)
                    || !Same(body, LocalPlayer.Rigidbody) || !source.isAttached
                    || source.networkId.PackedValue != candidate.SourceNetworkId
                    || source.prefabId.Value != candidate.PrefabId || source._entity?.Pointer != attachment.Pointer
                    || attachment.Serializer?.Pointer != serializer.Pointer
                    || serializer.Meta.TypeId.Value != candidate.StateTypeId) return false;
                if (!Alive(zipline) || !Same(playerBase, LocalPlayer.PlayerBase)
                    || zipline._connectedHeightOffset != candidate.ZiplineConnectedHeightOffset) return false;
                profile = candidate;
                detail = "Native source motion, capsule, material, body, contacts and compound interaction geometry captured without mutation.";
                return true;
            }
            catch (Exception error)
            { detail = "Native movement profile capture failed (" + error.GetType().Name + ")."; return false; }
        }

        public static bool ValidateProfile(PlayerCarrierProfile profile, out string detail)
        {
            detail = "The movement profile has an invalid identity, numeric value, geometry or enum.";
            if (profile == null || profile.SourceNetworkId == 0 || profile.PrefabId < 0
                || profile.StateTypeId < 0 || profile.Layer < 0 || profile.Layer > 31
                || profile.CapsuleLayer < 0 || profile.CapsuleLayer > 31
                || !float.IsFinite(profile.ZiplineConnectedHeightOffset)) return false;
            var m = profile.Motion;
            var g = profile.Geometry;
            var b = profile.Body;
            var c = profile.Contacts;
            var p = profile.Material;
            var o = profile.Observations;
            if (!Positive(m.WalkSpeed) || !Positive(m.RunSpeed) || !Positive(m.CrouchSpeed)
                || !Positive(m.StrafeSpeed) || !Nonnegative(m.BackwardSpeedMultiplier)
                || !Positive(m.Gravity) || !Positive(m.MaximumVelocityChange) || !Positive(m.MaximumVelocity)
                || !Nonnegative(m.JumpHeight) || !Nonnegative(m.JumpMultiplier)
                || !Positive(m.FixedDeltaTime) || m.FixedDeltaTime > 0.1f || !Positive(m.DefaultMass)
                || !Positive(g.Radius) || !Positive(g.Height) || g.Height < g.Radius * 2
                || !Positive(g.StandingHeight) || g.StandingHeight < g.Radius * 2
                || !Positive(g.CrouchingHeight) || g.CrouchingHeight < g.Radius * 2
                || g.CrouchingHeight > g.StandingHeight || !Finite(g.Center) || !Finite(g.StandingCenter)
                || !float.IsFinite(g.CrouchingCenterY) || !Finite(g.LocalPosition) || !Rotation(g.LocalRotation)
                || !Positive(g.ContactOffset) || !Positive(b.Mass) || !Nonnegative(b.Drag)
                || !Nonnegative(b.AngularDrag) || !Positive(b.MaximumDepenetrationVelocity)
                || !Positive(b.MaximumLinearVelocity) || !Nonnegative(b.MaximumAngularVelocity)
                || !Nonnegative(b.SleepThreshold) || ((int)b.Constraints & ~(int)RigidbodyConstraints.FreezeAll) != 0
                || !Enum.IsDefined(b.CollisionDetection) || !Enum.IsDefined(b.Interpolation)
                || b.SolverIterations < 1 || b.SolverIterations > 255
                || b.SolverVelocityIterations < 1 || b.SolverVelocityIterations > 255
                || !float.IsFinite(c.HeightOffset) || !Nonnegative(c.GroundAngleMaximumConstruction)
                || c.GroundAngleMaximumConstruction > 180 || !Finite(profile.Velocity) || !Finite(profile.AngularVelocity)
                || !Nonnegative(p.StaticFriction) || !Nonnegative(p.DynamicFriction)
                || !Nonnegative(p.Bounciness) || p.Bounciness > 1
                || !Enum.IsDefined(p.FrictionCombine) || !Enum.IsDefined(p.BounceCombine)
                || (!p.Present && p != default)
                || !Nonnegative(o.CurrentSpeed) || !float.IsFinite(o.ClampInput) || !float.IsFinite(o.OverrideClampInput)
                || !Nonnegative(o.GroundStableForce) || !Angle(o.ExtremeAngleStart) || !Angle(o.ExtremeAngleMaximum)
                || o.ExtremeAngleStart > o.ExtremeAngleMaximum || !Angle(o.TerrainExtremeAngleStart)
                || !Angle(o.TerrainExtremeAngleMaximum) || o.TerrainExtremeAngleStart > o.TerrainExtremeAngleMaximum
                || !float.IsFinite(o.OverallSpeed) || !Nonnegative(o.WalkToRunThreshold)
                || !Nonnegative(o.RunCostPerSecond) || !Nonnegative(o.TimeToRecoverFromRun)
                || !float.IsFinite(o.RecoveryRemaining) || !Nonnegative(o.StaminaMinimumToRun)
                || !ValidInteraction(profile.Interaction)) return false;
            detail = "Movement profile representation is valid; source approval and native motor qualification are separate.";
            return true;
        }

        private static bool Alive(UnityEngine.Object value) => value != null && !value.WasCollected && value;
        private static bool Same(UnityEngine.Object left, UnityEngine.Object right)
            => Alive(left) && Alive(right) && left.Pointer == right.Pointer;
        private static bool Positive(float value) => float.IsFinite(value) && value > 0;
        private static bool Nonnegative(float value) => float.IsFinite(value) && value >= 0;
        private static bool Angle(float value) => Nonnegative(value) && value <= 180;
        private static bool Finite(Vector3 value)
            => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        private static bool Unit(Vector3 value) => MathF.Abs(value.x - 1) < 0.0001f
            && MathF.Abs(value.y - 1) < 0.0001f && MathF.Abs(value.z - 1) < 0.0001f;
        private static bool Rotation(Quaternion value) => float.IsFinite(value.x) && float.IsFinite(value.y)
            && float.IsFinite(value.z) && float.IsFinite(value.w)
            && MathF.Abs(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w - 1) < 0.0001f;
    }
}
