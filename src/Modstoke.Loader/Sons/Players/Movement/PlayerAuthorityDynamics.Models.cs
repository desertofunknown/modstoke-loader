// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

namespace Modstoke.Sons
{
    /// <summary>
    /// Native configuration which requires explicit server policy approval; constructing
    /// it from captured observations grants no authority. Ground and Swim supply configuration;
    /// their permission and transient history fields are replaced by this owner.
    /// Ground.OverallSpeed seeds native smoothing once; subsequent
    /// values come from the carrier body. InitialCanRun is approved admission state.
    /// An active source exhaustion wait cannot be reconstructed from ForceStopRun alone.
    /// </summary>
    public sealed record PlayerAuthorityDynamicsProfile(
        PlayerNativeGroundDynamics Ground,
        PlayerSwimDynamics Swim,
        PlayerStaminaProfile Stamina,
        float TerrainSlideStartAngle,
        float TerrainSlideEndAngle,
        float MaximumAnimationInputSpeed,
        float OverallSpeedBlend,
        bool AllowJump,
        bool InitialCanRun,
        PlayerAuthorityRuntimeState Runtime);

    /// <summary>Native stat storage; delegates and effects are not portable state.</summary>
    public readonly record struct PlayerAuthorityStatState(float Current, float Base,
        float Minimum, float Maximum, float Default, float FadeRate, float RegenRate);

    /// <summary>
    /// Approved enrollment values, not a live permission provider. StaminaActionAge is an
    /// elapsed duration at capture, never another peer's Time.time. This restores numerical
    /// native stat/configuration storage only; active cooking/effect continuations and full
    /// survival/environment progression still require their own aggregate authority.
    /// </summary>
    public sealed record PlayerAuthorityRuntimeState(
        PlayerAuthorityStatState Health, PlayerAuthorityStatState Sickness,
        PlayerAuthorityStatState Stamina, PlayerAuthorityStatState Vitality,
        PlayerAuthorityStatState Hydration, PlayerAuthorityStatState Fullness,
        PlayerAuthorityStatState Rested, PlayerAuthorityStatState Strength,
        PlayerAuthorityStatState Temperature,
        float JumpStaminaCost, float StaminaRecoveryRate, float StaminaActionAge,
        float ColdRecoveryNormal, float ColdRecoveryHard, float StatRecoveryNormal, float StatRecoveryHard,
        float TargetHealth, float MaximumVitalityLostWhenFreezing, float ColdFactor,
        bool Cold, bool Freezing, bool HasDied, bool Downed, int DyingState, bool Respawning, bool FinalDeath,
        float HydrationBuff, float FullnessBuff, float RestedBuff,
        float LastHydrationFactor, float LastFullnessFactor, float LastRestedFactor);

    /// <summary>
    /// Live carrier-local life/action gates. These do not authorize an inventory operation,
    /// transfer item custody, simulate survival, or implement a special movement mode.
    /// </summary>
    public readonly record struct PlayerAuthorityPermissions(
        bool Alive, bool CanMove, bool CanJump, bool CanRun, bool CanUseInventory,
        bool CanAttack, bool Cold, bool Freezing, float Health, float Stamina,
        float Vitality, float HydrationFactor, float FullnessFactor, float RestedFactor);

    /// <summary>
    /// Live, server-owned action gates for this exact actor. The provider must derive these
    /// from committed action/mode owners, never client permission flags or the host's rig.
    /// A default value denies all actions. Special-mode owners may permit their own motion.
    /// </summary>
    public readonly record struct PlayerAuthorityActionState(
        bool CanMove, bool CanJump, bool CanRun, bool CanUseInventory, bool CanAttack);
}
