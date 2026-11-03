// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Gameplay.GameSetup;
using global::Sons.StatSystem;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerSurvivalRuntime
    {
        private void AdvanceFrame(Frame frame)
        {
            ValidateNumerics();
            // Vitals.Update: manager/stat tick, health clamp/recovery, nutrition buffers,
            // cooking, then independent infinite-energy and god-mode branches.
            // Neither Vitals.Update nor PlayerStats.Update has a downed/hasDied guard.
            // Life settlement and accepted stat mutations are serialized before entry.
            _vitals._temperature._interiorSpaceWarmth = frame.Environment.InteriorSpaceWarmth;
            _vitals._temperature._baseValue = frame.Environment.StatAmbientTemperature;
            for (int i = 0; i < _values.Length; i++)
            {
                if ((frame.Rates.FadeDelegates & (1u << i)) == 0) _values[i]._fadeRateMethod = null;
                TickStat(_values[i], frame.Rates.Values[i], frame.GameSeconds);
            }
            _appliedSleepState = frame.Rates.State;
            RecoverHealth(frame);
            _vitals._currentHydrationBuffFactor -= MathF.Max(0,
                _vitals._lastCachedHydrationFactor - Factor(_vitals._hydration));
            _vitals._lastCachedHydrationFactor = Factor(_vitals._hydration);
            _vitals._currentFullnessBuffFactor -= MathF.Max(0,
                _vitals._lastCachedFullnessFactor - Factor(_vitals._fullness));
            _vitals._lastCachedFullnessFactor = Factor(_vitals._fullness);
            _vitals._currentRestedBuffFactor -= MathF.Max(0,
                _vitals._lastCachedRestedFactor - Factor(_vitals._rested));
            _vitals._lastCachedRestedFactor = Factor(_vitals._rested);

            int expired = -1;
            _advancingCooking = true;
            try
            {
                // Consumption owns all recipe state and effect removal. Never call native
                // UpdateCookingBuffs/ItemUtils, and never reconstruct its retained cache here.
                if (!_authority.ConsumptionCurrent(_consumption))
                    throw new InvalidOperationException("The exact cooking owner is no longer current.");
                if (!_consumption.TryAdvanceSurvivalCooking(this, frame.GameSeconds, out var cooking, out expired, out string detail))
                    throw new InvalidOperationException(detail ?? "The exact cooking owner refused its survival step.");
                RequireCurrent();
                if (InventoryConsumption.ReadProjectionNumbers(_stats)
                    != ConsumptionNumbers.From(cooking))
                    throw new InvalidOperationException("Cooking progression did not retain its exact actor readback.");
            }
            finally { _advancingCooking = false; }
            if (frame.InfiniteEnergy)
            { SetRest(_vitals._rested._max); SetCurrent(_vitals._stamina, _vitals._stamina._max); }
            if (frame.GodMode)
            {
                SetRest(_vitals._rested._max);
                SetCurrent(_vitals._stamina, _vitals._stamina._max);
                SetVitality(_vitals._vitality._max);
                SetCurrent(_vitals._sickness, 0);
                _vitals._targetHealth = _vitals._health._max;
                SetCurrent(_vitals._health, _vitals._health._max);
                SetCurrent(_vitals._fullness, _vitals._fullness._max);
                SetCurrent(_vitals._hydration, _vitals._hydration._max);
                SetCurrent(_vitals._sickness, 0);
                _vitals._targetHealth = MathF.Min(_vitals._targetHealth,
                    _vitals._health._max - _vitals._sickness._currentValue);
            }
            // PlayerStats.Update numerical order. The separate motor retains run/jump,
            // stamina recovery and exhaustion ownership; this clock never repeats them.
            if (frame.CurrentView != (int)TheForest.Items.Inventory.PlayerInventory.PlayerViews.Loading)
                AdvanceTemperature(frame);
            AdvanceWetness(frame);
            float baseFactor = _vitals._vitality._baseValue / _vitals._vitality._max;
            float part = (1f - baseFactor) / 3f;
            SetVitality((baseFactor + Factor(_vitals._hydration) * part
                + Factor(_vitals._fullness) * part + Factor(_vitals._rested) * part) * _vitals._vitality._max);
            AdvanceRest(frame);
            float due = AdvanceDamageTimer(frame);
            if (!TryAdvanceLifeBurning(frame.ScaledSeconds, frame.ScaledTime, frame.Environment.Frame) && _stats._isOnFire)
            {
                _stats._burnTime -= frame.ScaledSeconds;
                if (_stats._burnTime <= 0) { _stats._burnTime = 0; _stats._isOnFire = false; }
            }
            AdvanceSmell(frame.ScaledSeconds);
            ValidateNumerics();
            RequireCurrent();
            if (_vitals._health._currentValue > Mathf.Epsilon || HasNativeLifeLatch() || due > 0)
                _godModeDeathCheckSuppressed = false;
            Progress = ReadProgress(checked(Progress.Revision + 1), frame.Environment.Frame, expired, due);
        }

        // Stat.Update 182ED3A90: regen is subtracted from the fade step. The native
        // below-base branch uses abs(step), not MoveTowards with a signed maxDelta.
        private static void TickStat(Stat stat, PlayerSurvivalRate rate, float dt)
        {
            stat._fadeRate = rate.Fade;
            stat._regenRate = rate.Regeneration;
            if (rate.Fade < Mathf.Epsilon && rate.Regeneration < Mathf.Epsilon) return;
            float regeneration = rate.Regeneration * dt;
            float step = rate.Fade * dt - regeneration;
            float difference = stat._currentValue - stat._baseValue;
            float distance = MathF.Abs(difference);
            if ((regeneration < Mathf.Epsilon && distance <= 1e-11f) || (step > 0 && step >= distance))
            { stat._currentValue = stat._baseValue; return; }
            float change = difference < 0 ? MathF.Abs(step) : -step;
            if (!float.IsNaN(change)) stat._currentValue = Mathf.Clamp(stat._currentValue + change, stat._min, stat._max);
        }

        private void RecoverHealth(Frame frame)
        {
            var health = _vitals._health;
            _vitals._targetHealth = MathF.Min(_vitals._targetHealth, health._max - _vitals._sickness._currentValue);
            SetCurrent(health, MathF.Min(health._currentValue, _vitals._targetHealth));
            if (_vitals._targetHealth <= health._currentValue) return;
            float step = frame.ScaledSeconds * _settings.HealthRegenerationPerSecond;
            if (_vitals._isCold) step *= Penalty(frame.ColdPenalty,
                _vitals._healthRegenColdModifierSurvivalNormal, _vitals._healthRegenColdModifierSurvivalHard);
            step *= Penalty(frame.RegenerationPenalty,
                _vitals._healthRegenStatModifierSurvivalNormal, _vitals._healthRegenStatModifierSurvivalHard);
            SetCurrent(health, Mathf.MoveTowards(health._currentValue, _vitals._targetHealth, step));
        }

        private void AdvanceTemperature(Frame frame)
        {
            var e = frame.Environment;
            _stats._insideTemperatureModifierVolume = e.InsideTemperatureVolume;
            _stats._temperatureModifierVolumeTemperature = e.TemperatureVolumeTemperature;
            _stats._ambientAirTemperature = e.BodyAmbientTemperature;
            _stats._currentModifiedTemperatureBlendDelta = Mathf.Clamp01(_stats._currentModifiedTemperatureBlendDelta
                + frame.GameSeconds * _stats._blendSpeedOfTemperatureVolumeModifiers * (e.InsideTemperatureVolume ? 1 : -1));
            _stats._airTemperatureFromHeatSource = Mathf.Lerp(_stats._ambientAirTemperature,
                _stats._temperatureModifierVolumeTemperature, _stats._currentModifiedTemperatureBlendDelta);
            _stats._currentTempDropFromWetness = _stats._currentWetness * _stats._maxTempDropFromWetness;
            _stats._thermalRatingAfterWetnessPenalty = _stats._thermalRating
                * (1f - _stats._currentWetness * _stats._wetnessAffectsThermalRatingMultiplier);
            _stats._currentTempBoostFromThermalRating = _stats._thermalRatingAfterWetnessPenalty * _stats._maxTempBoostFromThermalRating;
            float target = _stats._airTemperatureFromHeatSource + _stats._currentTempBoostFromThermalRating
                - _stats._currentTempDropFromWetness + (_stats._isOnFire ? 100f : 0f);
            // 18311E7B3..7FC: authored change per rendered Update, deliberately no dt.
            _stats._bodyTemperature = _stats._isOnFire ? target
                : Mathf.MoveTowards(_stats._bodyTemperature, target, _stats._bodyTemperatureChangeRate);
            _vitals._isCold = !frame.Creative && !e.InDeepCaves && _stats._bodyTemperature <= _stats._coldAtTemperature;
            _vitals._coldFactor = frame.Creative ? 0 : Mathf.Clamp01((_stats._bodyTemperature - _stats._coldAtTemperature)
                / (_stats._freezeAtTemperature - _stats._coldAtTemperature));
            _stats._IsFreezing_k__BackingField = !frame.Creative && !e.InDeepCaves
                && _stats._bodyTemperature <= _stats._freezeAtTemperature;
            _stats._shiverAnimBlendDelta = Mathf.Clamp01(_stats._shiverAnimBlendDelta
                + frame.ScaledSeconds * _stats._shiverAnimBlendSpeed * (_vitals._isCold ? 1 : -1));
        }

        private void AdvanceWetness(Frame frame)
        {
            var e = frame.Environment;
            _stats._externalWetnessLevel = e.ExternalWetness;
            float external = e.InteriorSpaceWarmth ? 0 : e.ExternalWetness;
            float soakRate = e.Snowing && !e.Underwater && !e.Swimming ? _stats._snowSoakInRate : _stats._waterSoakInRate;
            float soak = external > _stats._currentWetness
                ? (1f - Mathf.Clamp01(_stats._waterResistRating)) * soakRate * frame.ScaledSeconds : 0;
            if (soak > Mathf.Epsilon) _stats._currentWetness += soak;
            float drying = (1f - external) * MathF.Max(0, _stats._airTemperatureFromHeatSource)
                / _stats._maxDryOffTemperature * _stats._waterDryOffRate * frame.ScaledSeconds;
            _stats._currentWetness = Mathf.Clamp01(_stats._currentWetness - drying);
        }

        private void AdvanceRest(Frame frame)
        {
            bool hungry = _vitals._fullness._currentValue < Mathf.Epsilon
                || _vitals._fullness._currentValue < _vitals._hungryNotificationThreshold;
            bool thirsty = _vitals._hydration._currentValue < Mathf.Epsilon
                || _vitals._hydration._currentValue < _settings.ThirstyThreshold;
            float rate = (_stats._IsFreezing_k__BackingField || _vitals._isCold ? _stats._energyLostFromCold : 0)
                + (hungry ? _stats._energyLostFromHunger : 0) + (thirsty ? _stats._energyLostFromThirst : 0)
                + frame.Environment.HeldOnlyItemCount * _stats._energyLostFromLogs;
            float amount = _stats._currentEnergyDrain = rate * frame.GameSeconds;
            amount = MathF.Max(0, amount) * (_vitals._currentRestedBuffFactor > 0 ? _vitals._restedDrainBuffModifier : 1f);
            SetCurrent(_vitals._rested, _vitals._rested._currentValue - amount);
            _vitals._currentRestedBuffFactor = Mathf.Clamp01(_vitals._currentRestedBuffFactor - amount / _vitals._rested._max);
            _vitals._lastCachedRestedFactor = Factor(_vitals._rested);
        }

        private float AdvanceDamageTimer(Frame frame)
        {
            if (frame.CurrentView != (int)TheForest.Items.Inventory.PlayerInventory.PlayerViews.World
                || frame.DamageSetting == SurvivalSetting.Off._intValue) return 0;
            bool starving = _vitals._fullness._currentValue < Mathf.Epsilon;
            bool dehydrated = _vitals._hydration._currentValue < Mathf.Epsilon;
            if (!starving && !dehydrated) return 0;
            _stats._timeUntilNextStatDamageTick -= frame.GameSeconds;
            if (_stats._timeUntilNextStatDamageTick > 0) return 0;
            float damage = ((starving ? _stats._starvationDamagePerTick : 0)
                + (dehydrated ? _stats._dehydrationDamagePerTick : 0))
                * (frame.DamageSetting == SurvivalSetting.Normal._intValue ? 1f : _stats._hardSettingStatsDamageMultiplier);
            if (!FiniteNonnegative(damage)) throw new InvalidOperationException("The native stat damage is invalid.");
            if (damage == 0) { _stats._timeUntilNextStatDamageTick = _stats._gameSecondsBetweenStatDamageTicks; return 0; }
            _damageBefore = _vitals._health._currentValue;
            _damageTargetBefore = _vitals._targetHealth;
            return damage;
        }

        private void SetRest(float value)
        {
            SetCurrent(_vitals._rested, MathF.Max(0, value));
            _vitals._currentRestedBuffFactor = MathF.Min(_vitals._currentRestedBuffFactor, Factor(_vitals._rested));
            _vitals._lastCachedRestedFactor = Factor(_vitals._rested);
        }

        private void SetVitality(float value)
            => SetCurrent(_vitals._vitality, Mathf.Clamp(value, _vitals._vitality._min,
                _vitals._vitality._max - _vitals._coldFactor * _vitals._maxVitalityLostWhenFreezing));

        private static float Penalty(int setting, float normal, float hard)
            => setting == SurvivalSetting.Normal._intValue ? normal : setting == SurvivalSetting.Hard._intValue ? hard : 1;
        private static float Factor(Stat stat) => (stat._currentValue - stat._min) / (stat._max - stat._min);
        private static float Clamp(Stat stat, float value)
            => float.IsNaN(value) ? stat._defaultValue : Mathf.Clamp(value, stat._min, stat._max);
        private static void SetCurrent(Stat stat, float value) => stat._currentValue = Clamp(stat, value);
        private bool HasNativeLifeLatch() => _vitals._hasDied
            || _stats._playerIsDowned || _stats._dyingState != PlayerStats.DyingState.Alive
            || _stats._respawning || _stats._doFinalDeath;
        private bool RequiresLife() => _vitals._health._currentValue <= Mathf.Epsilon || HasNativeLifeLatch();

        private PlayerSurvivalProgress ReadProgress(ulong revision, int frame, int expired, float damage)
            => new(revision, frame, _vitals._health._currentValue, _vitals._hydration._currentValue,
                _vitals._fullness._currentValue, _vitals._rested._currentValue, _vitals._vitality._currentValue,
                _stats._bodyTemperature, _stats._currentWetness, _vitals._isCold, _stats._IsFreezing_k__BackingField,
                _stats._isOnFire, _smellAge < 600f, expired, damage, RequiresLife()) { Sleep = _appliedSleepState };

        private void ValidateNumerics()
        {
            foreach (var stat in _values)
                if (stat == null || stat.WasCollected || !float.IsFinite(stat._currentValue)
                    || !float.IsFinite(stat._baseValue) || !float.IsFinite(stat._min) || !float.IsFinite(stat._max)
                    || stat._max <= stat._min || !float.IsFinite(stat._defaultValue))
                    throw new InvalidOperationException("An actor survival stat is invalid.");
            if (_vitals._vitality._max <= 0 || _vitals._rested._max <= 0
                || !FiniteNonnegative(_stats._bodyTemperatureChangeRate) || _stats._maxDryOffTemperature <= 0
                || _stats._freezeAtTemperature >= _stats._coldAtTemperature
                || !FiniteNonnegative(_stats._gameSecondsBetweenStatDamageTicks))
                throw new InvalidOperationException("An authored thermal, vitality or damage denominator is invalid.");
            ReadOnlySpan<float> values = stackalloc float[] { _vitals._targetHealth, _vitals._currentHydrationBuffFactor,
                _vitals._currentFullnessBuffFactor, _vitals._currentRestedBuffFactor, _vitals._coldFactor,
                _stats._currentWetness, _stats._bodyTemperature, _stats._currentModifiedTemperatureBlendDelta,
                _stats._currentTempDropFromWetness, _stats._currentTempBoostFromThermalRating,
                _stats._thermalRatingAfterWetnessPenalty, _stats._shiverAnimBlendDelta,
                _stats._airTemperatureFromHeatSource, _stats._timeUntilNextStatDamageTick,
                _stats._burnTime, _smellAge };
            foreach (float value in values)
                if (!float.IsFinite(value)) throw new InvalidOperationException("A survival scalar is nonfinite.");
        }
    }
}
