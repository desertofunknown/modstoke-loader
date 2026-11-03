// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;

namespace Modstoke.Sons
{
    public enum PlayerMovementMode : byte
    {
        Ground,
        Swim,
        Sled
    }

    /// <summary>
    /// Intent only. Positive pitch looks down; no client position or mode is accepted.
    /// On the qualified binary, FixedUpdate's 182F805C2 call to DetermineVelocityChange
    /// calls GetTargetVelocity once at 182F84F00, before the swim branch. Its postfix
    /// sees local inputVelocity (y=0, dead zone 0.1, magnitude cap 1.1), stored at
    /// 182F84CD6 before TransformDirection. ApplySwimmingForce normalizes it later.
    /// Early-exit fixed steps need not sample; this is not permission to call native
    /// GetTargetVelocity on a carrier, since it also accesses LocalPlayer.
    /// Water jump intent comes from HandleSwimmingJumpInput(bool): Update calls it
    /// at 182F7EE16 and skips the separate OnJumpInput call while Swimming.
    /// </summary>
    public readonly record struct PlayerMovementModeInput(
        Vector2 Move, float LookPitchDegrees, bool Jump, bool Sprint = false);

    /// <summary>
    /// Server-owned native profile. Enter/exit offsets are PlayerAnimatorControl's
    /// _waterMinHeightRange.y/x respectively; WaterlineOffset is _underWaterOffset.
    /// No defaults are inferred from the host's LocalPlayer or a client input packet.
    /// The adapter must ensure the relevant world water geometry is loaded.
    /// </summary>
    public readonly record struct PlayerMovementModeSettings(
        int WaterLayerMask, int BlockerLayerMask, string RequiredWaterTag,
        OceanWaterMode OceanMode, float MinimumOceanSpatialLength,
        float WaterlineOffset, float SwimEnterOffset, float SwimExitOffset,
        float ForceDisableSwimmingAfter);

    /// <summary>
    /// Per-step SERVER state, not input. CurrentSpeed is the previous native _speed;
    /// the native fixed step selects the next swim speed after steering. DefaultMass is
    /// FPC.defaultMass, distinct from the actual Rigidbody.mass read by the motor.
    /// EffectiveInputClamp is the selected _overrideClampInput / _clampInputVal.
    /// MaximumVelocity is the already selected hitByEnemy/storeMaxVelocity limit.
    /// With WaterJump configured, the integrated router owns the jump timer after
    /// dry enrollment and replaces SwimmingJumpTimerRunning before applying forces.
    /// RunningAllowed is the SERVER-evaluated _canRun, stamina and animation-speed
    /// eligibility gate. Sprint remains intent, not permission. An adapter that has
    /// not bound authoritative stamina leaves running disabled; no stamina is invented.
    /// FPC._maxSwimVelocity is unused by the qualified branch; its clamp uses _speed.
    /// </summary>
    public readonly record struct PlayerSwimDynamics(
        float CurrentSpeed, float SwimSpeed, float SwimSpeedMultiplier,
        float MaximumVelocityChange, float EffectiveInputClamp, float DefaultMass,
        float MaximumVelocity, float MaximumDiveVelocity, bool SwimmingJumpTimerRunning,
        bool RunningAllowed = false, PlayerSwimJumpDynamics? WaterJump = null);

    /// <summary>
    /// Native actor-owned values: FPC._jumpHeight, gravity, _edgeWaterJumpMultiplier,
    /// _swimmingJumpTimer._interval, _jumpMultiplier and onSlipperySlope. No input
    /// packet may supply these. IsJumping and BlockWaterJump qualify settled initial
    /// history only; the integrated motor owns both after enrollment. Geometry/contact
    /// permission is never supplied as a client acceptance flag.
    /// </summary>
    public readonly record struct PlayerSwimJumpDynamics(
        float JumpHeight, float Gravity, float EdgeMultiplier, float TimerInterval,
        float JumpMultiplier, bool IsJumping, bool BlockWaterJump, bool OnSlipperySlope = false);

    /// <summary>
    /// Server observation before the next physics simulation. Velocity does not
    /// include queued AddForce effects or prove collision-resolved movement.
    /// WaterSurfaceVelocity is an observation, not an extra steering force.
    /// </summary>
    public readonly record struct PlayerMovementModeState(
        PlayerMovementMode Mode, bool IsUnderwater, bool SwimmingUnderwater,
        bool ModeChanged, bool ServerGrounded, bool ServerJumping,
        Vector3 Position, Vector3 Velocity, float? WaterHeight,
        Vector3 WaterSurfaceVelocity, float CurrentSpeed, int Frame, double FixedTime);
}
