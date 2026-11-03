// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons
{
    /// <summary>Fixed-order V1 native scalar storage. Values are observations, not policy.</summary>
    public enum PlayerSurvivalScalar
    {
        StartingHealth = 0,
        MaximumHealth = 1,
        HealthRecoveryColdNormal = 2,
        HealthRecoveryColdHard = 3,
        HealthRecoveryStatNormal = 4,
        HealthRecoveryStatHard = 5,
        SicknessNotificationThreshold = 6,
        HungerNotificationThreshold = 7,
        RequiredBaseStrength = 8,
        RequiredCurrentStrength = 9,
        CurrentStrength = 10,
        MeleeDamagePerStrengthLevel = 11,
        TreeDamagePerStrengthLevel = 12,
        StrengthFromLogs = 13,
        StrengthFromStones = 14,
        HydrationDrainBuffModifier = 15,
        FullnessDrainBuffModifier = 16,
        RestedDrainBuffModifier = 17,
        StrengthBoostModifier = 18,
        CookingSecondsRemaining = 19,
        ComfortRating = 20,
        HardConsumablePositiveMultiplier = 21,
        HardConsumableNegativeMultiplier = 22,
        HardStatsDamageMultiplier = 23,
        StarvationDamagePerTick = 24,
        DehydrationDamagePerTick = 25,
        GameSecondsBetweenDamageTicks = 26,
        GameSecondsUntilDamageTick = 27,
        EnergyLostFromLogs = 28,
        EnergyLostFromCold = 29,
        EnergyLostFromFreezing = 30,
        EnergyLostFromHunger = 31,
        EnergyLostFromStarvation = 32,
        EnergyLostFromThirst = 33,
        EnergyLostFromDehydration = 34,
        StealthRating = 35,
        TreeCutDamageMultiplier = 36,
        SoundRangeDampFactor = 37,
        Flammable = 38,
        DaySurvived = 39,
        CurrentEnergyDrain = 40,
        Tired = 41,
        BurnSecondsRemaining = 42,
        SkunkSprayAge = 43,
        ExternalWetness = 44,
        RequiredSnowDepth = 45,
        ThermalRating = 46,
        MaximumThermalBoost = 47,
        BodyTemperature = 48,
        BodyTemperatureChangeRate = 49,
        ColdTemperature = 50,
        FreezingTemperature = 51,
        ShiverBlendSpeed = 52,
        TemperatureVolumeBlendSpeed = 53,
        WaterResistance = 54,
        WaterSoakRate = 55,
        SnowSoakRate = 56,
        DryOffRate = 57,
        MaximumWetnessTemperatureDrop = 58,
        MaximumDryOffTemperature = 59,
        WetnessThermalMultiplier = 60,
        AmbientAirTemperature = 61,
        HeatSourceTemperature = 62,
        CurrentThermalBoost = 63,
        CurrentWetnessTemperatureDrop = 64,
        TemperatureVolumeTemperature = 65,
        TemperatureBlendDelta = 66,
        CurrentWetness = 67,
        ShiverBlendDelta = 68,
        WetThermalRating = 69,
        SwimSpeed = 70,
        SwimSpeedMultiplier = 71,
        MaximumDiveVelocity = 72,
        EdgeWaterJumpMultiplier = 73,
        WaterJumpInterval = 74,
        MaximumAnimationInputSpeed = 75,
        OverallSpeedBlend = 76,
        TemperatureFreezingThreshold = 77,
        Count = 78
    }

    public sealed partial class PlayerSurvivalSnapshot
    {
        private static float[] CaptureScalars(PlayerStats s, FirstPersonCharacter c, PlayerAnimatorControl a, float captureTime)
        {
            var v = s._vitals;
            return new float[]
            {
                v._startingHealth, // StartingHealth
                v._maxHealth, // MaximumHealth
                v._healthRegenColdModifierSurvivalNormal, // HealthRecoveryColdNormal
                v._healthRegenColdModifierSurvivalHard, // HealthRecoveryColdHard
                v._healthRegenStatModifierSurvivalNormal, // HealthRecoveryStatNormal
                v._healthRegenStatModifierSurvivalHard, // HealthRecoveryStatHard
                v._sicknessNotificationThreshold, // SicknessNotificationThreshold
                v._hungryNotificationThreshold, // HungerNotificationThreshold
                v._requiredBaseStrengthPerLevel, // RequiredBaseStrength
                v._requiredCurrentStrengthPerLevel, // RequiredCurrentStrength
                v._currentStrength, // CurrentStrength
                v._increasedMeleeDamageModifierPerStrengthLevel, // MeleeDamagePerStrengthLevel
                v._increasedTreeDamageModifierPerStrengthLevel, // TreeDamagePerStrengthLevel
                v._strengthGainedForLiftingLogs, // StrengthFromLogs
                v._strengthGainedForLiftingStones, // StrengthFromStones
                v._hydrationDrainBuffModifier, // HydrationDrainBuffModifier
                v._fullnessDrainBuffModifier, // FullnessDrainBuffModifier
                v._restedDrainBuffModifier, // RestedDrainBuffModifier
                v._strengthBoostModifier, // StrengthBoostModifier
                v._remainingCookingBuffTime, // CookingSecondsRemaining
                s._comfortRating, // ComfortRating
                s._hardSurvivalConsumablePositiveMultiplier, // HardConsumablePositiveMultiplier
                s._hardSurvivalConsumableNegativeMultiplier, // HardConsumableNegativeMultiplier
                s._hardSettingStatsDamageMultiplier, // HardStatsDamageMultiplier
                s._starvationDamagePerTick, // StarvationDamagePerTick
                s._dehydrationDamagePerTick, // DehydrationDamagePerTick
                s._gameSecondsBetweenStatDamageTicks, // GameSecondsBetweenDamageTicks
                s._timeUntilNextStatDamageTick, // GameSecondsUntilDamageTick
                s._energyLostFromLogs, // EnergyLostFromLogs
                s._energyLostFromCold, // EnergyLostFromCold
                s._energyLostFromFreezing, // EnergyLostFromFreezing
                s._energyLostFromHunger, // EnergyLostFromHunger
                s._energyLostFromStarvation, // EnergyLostFromStarvation
                s._energyLostFromThirst, // EnergyLostFromThirst
                s._energyLostFromDehydration, // EnergyLostFromDehydration
                s._stealthRating, // StealthRating
                s._treeCutDamageMultiplier, // TreeCutDamageMultiplier
                s.SoundRangeDampFactor, // SoundRangeDampFactor
                s.Flammable, // Flammable
                s.DaySurvived, // DaySurvived
                s._currentEnergyDrain, // CurrentEnergyDrain
                s._tired, // Tired
                s._burnTime, // BurnSecondsRemaining
                captureTime - s._skunkSprayTime, // SkunkSprayAge
                s._externalWetnessLevel, // ExternalWetness
                s._requiredDepthToBeConsideredInSnow, // RequiredSnowDepth
                s._thermalRating, // ThermalRating
                s._maxTempBoostFromThermalRating, // MaximumThermalBoost
                s._bodyTemperature, // BodyTemperature
                s._bodyTemperatureChangeRate, // BodyTemperatureChangeRate
                s._coldAtTemperature, // ColdTemperature
                s._freezeAtTemperature, // FreezingTemperature
                s._shiverAnimBlendSpeed, // ShiverBlendSpeed
                s._blendSpeedOfTemperatureVolumeModifiers, // TemperatureVolumeBlendSpeed
                s._waterResistRating, // WaterResistance
                s._waterSoakInRate, // WaterSoakRate
                s._snowSoakInRate, // SnowSoakRate
                s._waterDryOffRate, // DryOffRate
                s._maxTempDropFromWetness, // MaximumWetnessTemperatureDrop
                s._maxDryOffTemperature, // MaximumDryOffTemperature
                s._wetnessAffectsThermalRatingMultiplier, // WetnessThermalMultiplier
                s._ambientAirTemperature, // AmbientAirTemperature
                s._airTemperatureFromHeatSource, // HeatSourceTemperature
                s._currentTempBoostFromThermalRating, // CurrentThermalBoost
                s._currentTempDropFromWetness, // CurrentWetnessTemperatureDrop
                s._temperatureModifierVolumeTemperature, // TemperatureVolumeTemperature
                s._currentModifiedTemperatureBlendDelta, // TemperatureBlendDelta
                s._currentWetness, // CurrentWetness
                s._shiverAnimBlendDelta, // ShiverBlendDelta
                s._thermalRatingAfterWetnessPenalty, // WetThermalRating
                c._swimSpeed, // SwimSpeed
                c._swimSpeedMultiplier, // SwimSpeedMultiplier
                c.maxDiveVelocity, // MaximumDiveVelocity
                c._edgeWaterJumpMultiplier, // EdgeWaterJumpMultiplier
                c._swimmingJumpTimer._interval, // WaterJumpInterval
                a._maxInputSpeed, // MaximumAnimationInputSpeed
                a._overallBlendSpeed, // OverallSpeedBlend
                v._temperature._freezingTemperature,
            };
        }

        private void RestoreScalars(PlayerStats s)
        {
            var v = s._vitals;
            v._startingHealth = ReadScalar(PlayerSurvivalScalar.StartingHealth);
            v._maxHealth = ReadScalar(PlayerSurvivalScalar.MaximumHealth);
            v._healthRegenColdModifierSurvivalNormal = ReadScalar(PlayerSurvivalScalar.HealthRecoveryColdNormal);
            v._healthRegenColdModifierSurvivalHard = ReadScalar(PlayerSurvivalScalar.HealthRecoveryColdHard);
            v._healthRegenStatModifierSurvivalNormal = ReadScalar(PlayerSurvivalScalar.HealthRecoveryStatNormal);
            v._healthRegenStatModifierSurvivalHard = ReadScalar(PlayerSurvivalScalar.HealthRecoveryStatHard);
            v._sicknessNotificationThreshold = ReadScalar(PlayerSurvivalScalar.SicknessNotificationThreshold);
            v._hungryNotificationThreshold = ReadScalar(PlayerSurvivalScalar.HungerNotificationThreshold);
            v._requiredBaseStrengthPerLevel = ReadScalar(PlayerSurvivalScalar.RequiredBaseStrength);
            v._requiredCurrentStrengthPerLevel = ReadScalar(PlayerSurvivalScalar.RequiredCurrentStrength);
            v._currentStrength = ReadScalar(PlayerSurvivalScalar.CurrentStrength);
            v._increasedMeleeDamageModifierPerStrengthLevel = ReadScalar(PlayerSurvivalScalar.MeleeDamagePerStrengthLevel);
            v._increasedTreeDamageModifierPerStrengthLevel = ReadScalar(PlayerSurvivalScalar.TreeDamagePerStrengthLevel);
            v._strengthGainedForLiftingLogs = ReadScalar(PlayerSurvivalScalar.StrengthFromLogs);
            v._strengthGainedForLiftingStones = ReadScalar(PlayerSurvivalScalar.StrengthFromStones);
            v._hydrationDrainBuffModifier = ReadScalar(PlayerSurvivalScalar.HydrationDrainBuffModifier);
            v._fullnessDrainBuffModifier = ReadScalar(PlayerSurvivalScalar.FullnessDrainBuffModifier);
            v._restedDrainBuffModifier = ReadScalar(PlayerSurvivalScalar.RestedDrainBuffModifier);
            v._strengthBoostModifier = ReadScalar(PlayerSurvivalScalar.StrengthBoostModifier);
            v._remainingCookingBuffTime = ReadScalar(PlayerSurvivalScalar.CookingSecondsRemaining);
            s._comfortRating = ReadScalar(PlayerSurvivalScalar.ComfortRating);
            s._hardSurvivalConsumablePositiveMultiplier = ReadScalar(PlayerSurvivalScalar.HardConsumablePositiveMultiplier);
            s._hardSurvivalConsumableNegativeMultiplier = ReadScalar(PlayerSurvivalScalar.HardConsumableNegativeMultiplier);
            s._hardSettingStatsDamageMultiplier = ReadScalar(PlayerSurvivalScalar.HardStatsDamageMultiplier);
            s._starvationDamagePerTick = ReadScalar(PlayerSurvivalScalar.StarvationDamagePerTick);
            s._dehydrationDamagePerTick = ReadScalar(PlayerSurvivalScalar.DehydrationDamagePerTick);
            s._gameSecondsBetweenStatDamageTicks = ReadScalar(PlayerSurvivalScalar.GameSecondsBetweenDamageTicks);
            s._timeUntilNextStatDamageTick = ReadScalar(PlayerSurvivalScalar.GameSecondsUntilDamageTick);
            s._energyLostFromLogs = ReadScalar(PlayerSurvivalScalar.EnergyLostFromLogs);
            s._energyLostFromCold = ReadScalar(PlayerSurvivalScalar.EnergyLostFromCold);
            s._energyLostFromFreezing = ReadScalar(PlayerSurvivalScalar.EnergyLostFromFreezing);
            s._energyLostFromHunger = ReadScalar(PlayerSurvivalScalar.EnergyLostFromHunger);
            s._energyLostFromStarvation = ReadScalar(PlayerSurvivalScalar.EnergyLostFromStarvation);
            s._energyLostFromThirst = ReadScalar(PlayerSurvivalScalar.EnergyLostFromThirst);
            s._energyLostFromDehydration = ReadScalar(PlayerSurvivalScalar.EnergyLostFromDehydration);
            s._stealthRating = ReadScalar(PlayerSurvivalScalar.StealthRating);
            s._treeCutDamageMultiplier = ReadScalar(PlayerSurvivalScalar.TreeCutDamageMultiplier);
            s.SoundRangeDampFactor = ReadScalar(PlayerSurvivalScalar.SoundRangeDampFactor);
            s.Flammable = ReadScalar(PlayerSurvivalScalar.Flammable);
            s.DaySurvived = ReadScalar(PlayerSurvivalScalar.DaySurvived);
            s._currentEnergyDrain = ReadScalar(PlayerSurvivalScalar.CurrentEnergyDrain);
            s._tired = ReadScalar(PlayerSurvivalScalar.Tired);
            s._burnTime = ReadScalar(PlayerSurvivalScalar.BurnSecondsRemaining);
            s._skunkSprayTime = UnityEngine.Time.time - ReadScalar(PlayerSurvivalScalar.SkunkSprayAge);
            s._externalWetnessLevel = ReadScalar(PlayerSurvivalScalar.ExternalWetness);
            s._requiredDepthToBeConsideredInSnow = ReadScalar(PlayerSurvivalScalar.RequiredSnowDepth);
            s._thermalRating = ReadScalar(PlayerSurvivalScalar.ThermalRating);
            s._maxTempBoostFromThermalRating = ReadScalar(PlayerSurvivalScalar.MaximumThermalBoost);
            s._bodyTemperature = ReadScalar(PlayerSurvivalScalar.BodyTemperature);
            s._bodyTemperatureChangeRate = ReadScalar(PlayerSurvivalScalar.BodyTemperatureChangeRate);
            s._coldAtTemperature = ReadScalar(PlayerSurvivalScalar.ColdTemperature);
            s._freezeAtTemperature = ReadScalar(PlayerSurvivalScalar.FreezingTemperature);
            s._shiverAnimBlendSpeed = ReadScalar(PlayerSurvivalScalar.ShiverBlendSpeed);
            s._blendSpeedOfTemperatureVolumeModifiers = ReadScalar(PlayerSurvivalScalar.TemperatureVolumeBlendSpeed);
            s._waterResistRating = ReadScalar(PlayerSurvivalScalar.WaterResistance);
            s._waterSoakInRate = ReadScalar(PlayerSurvivalScalar.WaterSoakRate);
            s._snowSoakInRate = ReadScalar(PlayerSurvivalScalar.SnowSoakRate);
            s._waterDryOffRate = ReadScalar(PlayerSurvivalScalar.DryOffRate);
            s._maxTempDropFromWetness = ReadScalar(PlayerSurvivalScalar.MaximumWetnessTemperatureDrop);
            s._maxDryOffTemperature = ReadScalar(PlayerSurvivalScalar.MaximumDryOffTemperature);
            s._wetnessAffectsThermalRatingMultiplier = ReadScalar(PlayerSurvivalScalar.WetnessThermalMultiplier);
            s._ambientAirTemperature = ReadScalar(PlayerSurvivalScalar.AmbientAirTemperature);
            s._airTemperatureFromHeatSource = ReadScalar(PlayerSurvivalScalar.HeatSourceTemperature);
            s._currentTempBoostFromThermalRating = ReadScalar(PlayerSurvivalScalar.CurrentThermalBoost);
            s._currentTempDropFromWetness = ReadScalar(PlayerSurvivalScalar.CurrentWetnessTemperatureDrop);
            s._temperatureModifierVolumeTemperature = ReadScalar(PlayerSurvivalScalar.TemperatureVolumeTemperature);
            s._currentModifiedTemperatureBlendDelta = ReadScalar(PlayerSurvivalScalar.TemperatureBlendDelta);
            s._currentWetness = ReadScalar(PlayerSurvivalScalar.CurrentWetness);
            s._shiverAnimBlendDelta = ReadScalar(PlayerSurvivalScalar.ShiverBlendDelta);
            s._thermalRatingAfterWetnessPenalty = ReadScalar(PlayerSurvivalScalar.WetThermalRating);
            v._temperature._freezingTemperature = ReadScalar(PlayerSurvivalScalar.TemperatureFreezingThreshold);
        }

        internal bool MatchesRestored(PlayerStats s)
        {
            var v = s._vitals;
            if (!PlayerAuthorityDynamics.TryCaptureRuntimeState(s, out var current, out _)
                || (current with { StaminaActionAge = Runtime.StaminaActionAge }) != Runtime
                || CaptureFlags(s) != (_flags & 1279u)
                || v._currentStrengthLevel != StrengthLevel || v._maxStrengthLevel != MaximumStrengthLevel
                || v._lowHealthThreshold != LowHealthThreshold || s._playerDeathCount != DeathCount
                || s._maxDeathCount != MaximumDeathCount
                || v._startingHealth != ReadScalar(PlayerSurvivalScalar.StartingHealth)
                || v._maxHealth != ReadScalar(PlayerSurvivalScalar.MaximumHealth)
                || v._healthRegenColdModifierSurvivalNormal != ReadScalar(PlayerSurvivalScalar.HealthRecoveryColdNormal)
                || v._healthRegenColdModifierSurvivalHard != ReadScalar(PlayerSurvivalScalar.HealthRecoveryColdHard)
                || v._healthRegenStatModifierSurvivalNormal != ReadScalar(PlayerSurvivalScalar.HealthRecoveryStatNormal)
                || v._healthRegenStatModifierSurvivalHard != ReadScalar(PlayerSurvivalScalar.HealthRecoveryStatHard)
                || v._sicknessNotificationThreshold != ReadScalar(PlayerSurvivalScalar.SicknessNotificationThreshold)
                || v._hungryNotificationThreshold != ReadScalar(PlayerSurvivalScalar.HungerNotificationThreshold)
                || v._requiredBaseStrengthPerLevel != ReadScalar(PlayerSurvivalScalar.RequiredBaseStrength)
                || v._requiredCurrentStrengthPerLevel != ReadScalar(PlayerSurvivalScalar.RequiredCurrentStrength)
                || v._currentStrength != ReadScalar(PlayerSurvivalScalar.CurrentStrength)
                || v._increasedMeleeDamageModifierPerStrengthLevel != ReadScalar(PlayerSurvivalScalar.MeleeDamagePerStrengthLevel)
                || v._increasedTreeDamageModifierPerStrengthLevel != ReadScalar(PlayerSurvivalScalar.TreeDamagePerStrengthLevel)
                || v._strengthGainedForLiftingLogs != ReadScalar(PlayerSurvivalScalar.StrengthFromLogs)
                || v._strengthGainedForLiftingStones != ReadScalar(PlayerSurvivalScalar.StrengthFromStones)
                || v._hydrationDrainBuffModifier != ReadScalar(PlayerSurvivalScalar.HydrationDrainBuffModifier)
                || v._fullnessDrainBuffModifier != ReadScalar(PlayerSurvivalScalar.FullnessDrainBuffModifier)
                || v._restedDrainBuffModifier != ReadScalar(PlayerSurvivalScalar.RestedDrainBuffModifier)
                || v._strengthBoostModifier != ReadScalar(PlayerSurvivalScalar.StrengthBoostModifier)
                || v._remainingCookingBuffTime != ReadScalar(PlayerSurvivalScalar.CookingSecondsRemaining)
                || s._comfortRating != ReadScalar(PlayerSurvivalScalar.ComfortRating)
                || s._hardSurvivalConsumablePositiveMultiplier != ReadScalar(PlayerSurvivalScalar.HardConsumablePositiveMultiplier)
                || s._hardSurvivalConsumableNegativeMultiplier != ReadScalar(PlayerSurvivalScalar.HardConsumableNegativeMultiplier)
                || s._hardSettingStatsDamageMultiplier != ReadScalar(PlayerSurvivalScalar.HardStatsDamageMultiplier)
                || s._starvationDamagePerTick != ReadScalar(PlayerSurvivalScalar.StarvationDamagePerTick)
                || s._dehydrationDamagePerTick != ReadScalar(PlayerSurvivalScalar.DehydrationDamagePerTick)
                || s._gameSecondsBetweenStatDamageTicks != ReadScalar(PlayerSurvivalScalar.GameSecondsBetweenDamageTicks)
                || s._timeUntilNextStatDamageTick != ReadScalar(PlayerSurvivalScalar.GameSecondsUntilDamageTick)
                || s._energyLostFromLogs != ReadScalar(PlayerSurvivalScalar.EnergyLostFromLogs)
                || s._energyLostFromCold != ReadScalar(PlayerSurvivalScalar.EnergyLostFromCold)
                || s._energyLostFromFreezing != ReadScalar(PlayerSurvivalScalar.EnergyLostFromFreezing)
                || s._energyLostFromHunger != ReadScalar(PlayerSurvivalScalar.EnergyLostFromHunger)
                || s._energyLostFromStarvation != ReadScalar(PlayerSurvivalScalar.EnergyLostFromStarvation)
                || s._energyLostFromThirst != ReadScalar(PlayerSurvivalScalar.EnergyLostFromThirst)
                || s._energyLostFromDehydration != ReadScalar(PlayerSurvivalScalar.EnergyLostFromDehydration)
                || s._stealthRating != ReadScalar(PlayerSurvivalScalar.StealthRating)
                || s._treeCutDamageMultiplier != ReadScalar(PlayerSurvivalScalar.TreeCutDamageMultiplier)
                || s.SoundRangeDampFactor != ReadScalar(PlayerSurvivalScalar.SoundRangeDampFactor)
                || s.Flammable != ReadScalar(PlayerSurvivalScalar.Flammable)
                || s.DaySurvived != ReadScalar(PlayerSurvivalScalar.DaySurvived)
                || s._currentEnergyDrain != ReadScalar(PlayerSurvivalScalar.CurrentEnergyDrain)
                || s._tired != ReadScalar(PlayerSurvivalScalar.Tired)
                || s._burnTime != ReadScalar(PlayerSurvivalScalar.BurnSecondsRemaining)
                || s._externalWetnessLevel != ReadScalar(PlayerSurvivalScalar.ExternalWetness)
                || s._requiredDepthToBeConsideredInSnow != ReadScalar(PlayerSurvivalScalar.RequiredSnowDepth)
                || s._thermalRating != ReadScalar(PlayerSurvivalScalar.ThermalRating)
                || s._maxTempBoostFromThermalRating != ReadScalar(PlayerSurvivalScalar.MaximumThermalBoost)
                || s._bodyTemperature != ReadScalar(PlayerSurvivalScalar.BodyTemperature)
                || s._bodyTemperatureChangeRate != ReadScalar(PlayerSurvivalScalar.BodyTemperatureChangeRate)
                || s._coldAtTemperature != ReadScalar(PlayerSurvivalScalar.ColdTemperature)
                || s._freezeAtTemperature != ReadScalar(PlayerSurvivalScalar.FreezingTemperature)
                || s._shiverAnimBlendSpeed != ReadScalar(PlayerSurvivalScalar.ShiverBlendSpeed)
                || s._blendSpeedOfTemperatureVolumeModifiers != ReadScalar(PlayerSurvivalScalar.TemperatureVolumeBlendSpeed)
                || s._waterResistRating != ReadScalar(PlayerSurvivalScalar.WaterResistance)
                || s._waterSoakInRate != ReadScalar(PlayerSurvivalScalar.WaterSoakRate)
                || s._snowSoakInRate != ReadScalar(PlayerSurvivalScalar.SnowSoakRate)
                || s._waterDryOffRate != ReadScalar(PlayerSurvivalScalar.DryOffRate)
                || s._maxTempDropFromWetness != ReadScalar(PlayerSurvivalScalar.MaximumWetnessTemperatureDrop)
                || s._maxDryOffTemperature != ReadScalar(PlayerSurvivalScalar.MaximumDryOffTemperature)
                || s._wetnessAffectsThermalRatingMultiplier != ReadScalar(PlayerSurvivalScalar.WetnessThermalMultiplier)
                || s._ambientAirTemperature != ReadScalar(PlayerSurvivalScalar.AmbientAirTemperature)
                || s._airTemperatureFromHeatSource != ReadScalar(PlayerSurvivalScalar.HeatSourceTemperature)
                || s._currentTempBoostFromThermalRating != ReadScalar(PlayerSurvivalScalar.CurrentThermalBoost)
                || s._currentTempDropFromWetness != ReadScalar(PlayerSurvivalScalar.CurrentWetnessTemperatureDrop)
                || s._temperatureModifierVolumeTemperature != ReadScalar(PlayerSurvivalScalar.TemperatureVolumeTemperature)
                || s._currentModifiedTemperatureBlendDelta != ReadScalar(PlayerSurvivalScalar.TemperatureBlendDelta)
                || s._currentWetness != ReadScalar(PlayerSurvivalScalar.CurrentWetness)
                || s._shiverAnimBlendDelta != ReadScalar(PlayerSurvivalScalar.ShiverBlendDelta)
                || s._thermalRatingAfterWetnessPenalty != ReadScalar(PlayerSurvivalScalar.WetThermalRating)
                || v._temperature._freezingTemperature != ReadScalar(PlayerSurvivalScalar.TemperatureFreezingThreshold)
                ) return false;
            var recipe = v._activeCookingBuffRecipe;
            return CookingRecipeId == -1 ? recipe == null
                : Alive(recipe) && recipe.Id == CookingRecipeId && RecipeFingerprint(recipe) == CookingRecipeFingerprint;
        }

        public float ReadScalar(PlayerSurvivalScalar field)
        {
            if ((uint)field >= (uint)PlayerSurvivalScalar.Count)
                throw new ArgumentOutOfRangeException(nameof(field));
            return _scalars[(int)field];
        }
    }
}

