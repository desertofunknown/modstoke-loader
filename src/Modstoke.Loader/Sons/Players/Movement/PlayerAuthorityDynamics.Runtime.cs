// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Loader;
using global::Sons.StatSystem;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerAuthorityDynamics
    {
        /// <summary>
        /// Capture disabled server receivers, or the exact source under its verified lifecycle
        /// hold. This is observation, not server approval. The aggregate must correlate and
        /// carry it with the snapshot and validate it at destination enrollment.
        /// </summary>
        public static bool TryCaptureRuntimeState(PlayerStats source,
            out PlayerAuthorityRuntimeState state, out string detail)
            => TryCaptureRuntimeState(source, null, out state, out detail);

        public static bool TryCaptureRuntimeState(PlayerStats source, PlayerLifecycle lifecycle,
            out PlayerAuthorityRuntimeState state, out string detail)
        {
            state = null;
            detail = "A current native actor's complete stats and scaled action timestamp are required.";
            if (!PlayerRoles.IsAvailable || !GameBuild.IsRecognized) return false;
            try
            {
                if (!RuntimeCaptureHeld(source, lifecycle)) return false;
                int frame = Time.frameCount;
                var v = source._vitals;
                var stats = new Stat[] { v._health, v._sickness, v._stamina, v._vitality,
                    v._hydration, v._fullness, v._rested, v._strength, v._temperature };
                float age = (lifecycle?.PausedGameTime ?? Time.time) - source._staminaActionTime;
                if (!Range(age, 0, 10000000)) return false;
                var candidate = new PlayerAuthorityRuntimeState(
                    CaptureStat(stats[0]), CaptureStat(stats[1]), CaptureStat(stats[2]), CaptureStat(stats[3]),
                    CaptureStat(stats[4]), CaptureStat(stats[5]), CaptureStat(stats[6]), CaptureStat(stats[7]), CaptureStat(stats[8]),
                    source._jumpStaminaCost, source._staminaRecoverRate, age,
                    source._staminaRegenColdModifierSurvivalNormal, source._staminaRegenColdModifierSurvivalHard,
                    source._staminaRegenStatModifierSurvivalNormal, source._staminaRegenStatModifierSurvivalHard,
                    v._targetHealth, v._maxVitalityLostWhenFreezing, v._coldFactor, v._isCold,
                    source._IsFreezing_k__BackingField, v._hasDied, source._playerIsDowned,
                    (int)source._dyingState, source._respawning, source._doFinalDeath,
                    v._currentHydrationBuffFactor, v._currentFullnessBuffFactor, v._currentRestedBuffFactor,
                    v._lastCachedHydrationFactor, v._lastCachedFullnessFactor, v._lastCachedRestedFactor);
                if (!RuntimeCaptureHeld(source, lifecycle) || !Same(source._vitals, v)
                    || frame != Time.frameCount || !ValidRuntimeStorage(candidate)
                    || !SameStat(v._health, stats[0]) || !SameStat(v._sickness, stats[1])
                    || !SameStat(v._stamina, stats[2]) || !SameStat(v._vitality, stats[3])
                    || !SameStat(v._hydration, stats[4]) || !SameStat(v._fullness, stats[5])
                    || !SameStat(v._rested, stats[6]) || !SameStat(v._strength, stats[7])
                    || !SameStat(v._temperature, stats[8])) return false;
                state = candidate;
                detail = "Native numerical runtime state captured; explicit server approval and separate survival/effect authority are still required.";
                return true;
            }
            catch (Exception error)
            { detail = "Native runtime capture failed (" + error.GetType().Name + ")."; return false; }
        }

        private static bool RuntimeCaptureHeld(PlayerStats source, PlayerLifecycle lifecycle)
        {
            if (!Alive(source) || !Alive(source._vitals)) return false;
            if (lifecycle == null) return !source.enabled && !source._vitals.enabled;
            var entity = lifecycle.OriginalEntity;
            return lifecycle.IsPaused && Alive(entity) && entity.isAttached && entity.isOwner
                && source.transform.IsChildOf(entity.transform) && source._vitals.transform.IsChildOf(entity.transform);
        }

        private void ValidateApprovedRuntime()
        {
            var s = Profile.Runtime;
            RequireCurrent();
            if (!TryCaptureRuntimeState(_stats, out var current, out _)
                || (current with { StaminaActionAge = s.StaminaActionAge }) != s)
                throw new InvalidOperationException("The carrier must already contain its approved survival baseline.");
        }

        internal static void RestoreRuntime(PlayerStats stats, PlayerAuthorityRuntimeState s, float now)
        {
            if (!ValidRuntimeStorage(s) || !Alive(stats) || !Alive(stats._vitals)
                || stats.enabled || stats._vitals.enabled)
                throw new InvalidOperationException("Complete approved storage and held actor stats are required.");
            var v = stats._vitals;
            float actionTime = now - s.StaminaActionAge;
            if (!float.IsFinite(actionTime)) throw new InvalidOperationException("The rebased stamina action time is invalid.");
            // Native stat storage is restored directly, as in StrictCombat's health-isolated
            // restoration. Native LoadVitalsFromPlayerData applies cooking effects via
            // LocalPlayer, so it must not be invoked for this remote carrier.
            RestoreStat(v._health, s.Health); RestoreStat(v._sickness, s.Sickness);
            RestoreStat(v._stamina, s.Stamina); RestoreStat(v._vitality, s.Vitality);
            RestoreStat(v._hydration, s.Hydration); RestoreStat(v._fullness, s.Fullness);
            RestoreStat(v._rested, s.Rested); RestoreStat(v._strength, s.Strength);
            RestoreStat(v._temperature, s.Temperature);
            stats._jumpStaminaCost = s.JumpStaminaCost;
            stats._staminaRecoverRate = s.StaminaRecoveryRate;
            stats._staminaActionTime = actionTime;
            stats._staminaRegenColdModifierSurvivalNormal = s.ColdRecoveryNormal;
            stats._staminaRegenColdModifierSurvivalHard = s.ColdRecoveryHard;
            stats._staminaRegenStatModifierSurvivalNormal = s.StatRecoveryNormal;
            stats._staminaRegenStatModifierSurvivalHard = s.StatRecoveryHard;
            v._targetHealth = s.TargetHealth;
            v._maxVitalityLostWhenFreezing = s.MaximumVitalityLostWhenFreezing;
            v._coldFactor = s.ColdFactor; v._isCold = s.Cold;
            stats._IsFreezing_k__BackingField = s.Freezing;
            v._hasDied = s.HasDied; stats._playerIsDowned = s.Downed;
            stats._dyingState = (PlayerStats.DyingState)s.DyingState;
            stats._respawning = s.Respawning; stats._doFinalDeath = s.FinalDeath;
            v._currentHydrationBuffFactor = s.HydrationBuff;
            v._currentFullnessBuffFactor = s.FullnessBuff;
            v._currentRestedBuffFactor = s.RestedBuff;
            v._lastCachedHydrationFactor = s.LastHydrationFactor;
            v._lastCachedFullnessFactor = s.LastFullnessFactor;
            v._lastCachedRestedFactor = s.LastRestedFactor;
            if (!TryCaptureRuntimeState(stats, out var restored, out _)
                || (restored with { StaminaActionAge = s.StaminaActionAge }) != s
                || stats._staminaActionTime != actionTime)
                throw new InvalidOperationException("Approved actor stat restoration did not retain its values.");
        }

        private static PlayerAuthorityStatState CaptureStat(Stat stat)
        {
            if (stat == null || stat.WasCollected) throw new InvalidOperationException("A native stat is missing.");
            return new(stat._currentValue, stat._baseValue, stat._min, stat._max,
                stat._defaultValue, stat._fadeRate, stat._regenRate);
        }

        private static void RestoreStat(Stat stat, PlayerAuthorityStatState s)
        {
            if (stat == null || stat.WasCollected) throw new InvalidOperationException("A native stat was replaced during restoration.");
            stat._min = s.Minimum; stat._max = s.Maximum; stat._defaultValue = s.Default;
            stat._baseValue = s.Base; stat._fadeRate = s.FadeRate; stat._regenRate = s.RegenRate;
            stat._currentValue = s.Current;
        }

        private static bool ValidStat(PlayerAuthorityStatState s)
            => Range(s.Minimum, -1000000, 1000000) && Range(s.Maximum, s.Minimum, 1000000)
                && s.Maximum > s.Minimum && Range(s.Current, s.Minimum, s.Maximum)
                && Range(s.Default, s.Minimum, s.Maximum) && Range(s.Base, -1000000, 1000000)
                && Range(s.FadeRate, -1000000, 1000000) && Range(s.RegenRate, -1000000, 1000000);

        private static bool ValidRuntime(PlayerAuthorityRuntimeState s)
            => ValidRuntimeStorage(s) && s.Health.Current > 0 && !s.HasDied && !s.Downed
                && s.DyingState == 0 && !s.Respawning && !s.FinalDeath;

        internal static bool ValidRuntimeStorage(PlayerAuthorityRuntimeState s)
            => s != null && ValidStat(s.Health) && ValidStat(s.Sickness) && ValidStat(s.Stamina)
                && ValidStat(s.Vitality) && ValidStat(s.Hydration) && ValidStat(s.Fullness)
                && ValidStat(s.Rested) && ValidStat(s.Strength) && ValidStat(s.Temperature)
                && Enum.IsDefined(typeof(PlayerStats.DyingState), s.DyingState)
                && Range(s.StaminaActionAge, 0, 10000000)
                && Range(s.JumpStaminaCost, 0, 10000) && Range(s.StaminaRecoveryRate, 0, 10000)
                && Range(s.ColdRecoveryNormal, 0, 1000) && Range(s.ColdRecoveryHard, 0, 1000)
                && Range(s.StatRecoveryNormal, 0, 1000) && Range(s.StatRecoveryHard, 0, 1000)
                && Range(s.TargetHealth, s.Health.Minimum, s.Health.Maximum)
                && Range(s.MaximumVitalityLostWhenFreezing, 0, s.Vitality.Maximum) && Range(s.ColdFactor, 0, 1)
                && Range(s.HydrationBuff, -1000, 1000) && Range(s.FullnessBuff, -1000, 1000)
                && Range(s.RestedBuff, -1000, 1000) && Range(s.LastHydrationFactor, 0, 1)
                && Range(s.LastFullnessFactor, 0, 1) && Range(s.LastRestedFactor, 0, 1);
    }
}
