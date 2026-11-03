// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>Exact, server-approved native capsule dimensions; not CharacterController step/skin settings.</summary>
    public readonly record struct PlayerNativeCapsuleProfile(float Radius,
        float StandingHeight, Vector3 StandingCenter, float CrouchingHeight, Vector3 CrouchingCenter);

    /// <summary>
    /// Actor-owned FPC values. CurrentSpeed seeds _speed once; the motor owns its subsequent
    /// history. InputClamp and velocity/slide limits are the already selected server values.
    /// CanRun, CanJump and OverallSpeed must be evaluated by the actor's authoritative action
    /// and animation owner, never copied from client flags. This is the ordinary dry branch,
    /// not sled, zipline, climbing, flight, knockback or the native step-helper collider.
    /// </summary>
    public readonly record struct PlayerNativeGroundDynamics(float CurrentSpeed,
        float WalkSpeed, float RunSpeed, float CrouchSpeed, float BackwardSpeedMultiplier,
        float MaximumVelocityChange, float InputClamp, float MaximumVelocity,
        float Gravity, float JumpHeight, float JumpMultiplier, float GroundStickiness,
        float SlideStartAngle, float SlideEndAngle, bool CanJump, bool CanRun,
        float OverallSpeed, float WalkToRunThreshold, bool InputBlocked = false,
        bool OnSlipperySlope = false, bool DisableGravity = false);

    /// <summary>
    /// Server-owned run cost and exhaustion recovery interval from the actor's FPC.
    /// Jump cost, action timestamp and recovery rate come from the exact bound PlayerStats.
    /// </summary>
    public readonly record struct PlayerStaminaProfile(float RunCostPerSecond, float TimeToRecover,
        float RecoveryRemaining = 0, float ExhaustionWaitRemaining = 0);

    /// <summary>
    /// Server-selected cold and difficulty multiplier and native run permission. After
    /// exhaustion the action owner must reproduce native CanRun re-arming; this motor
    /// does not invent a client input edge or re-enable the native permission itself.
    /// </summary>
    public readonly record struct PlayerStaminaConditions(bool CanRun,
        float OverallSpeed, float WalkToRunThreshold, float RecoveryMultiplier);

    /// <summary>Server account observation, not a wire input or acknowledgement.</summary>
    public readonly record struct PlayerStaminaState(float Stamina, float Vitality,
        bool Running, bool ForceStopRun, float RecoveryRemaining, float LastActionTime);
}
