// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

namespace Modstoke.Sons
{
    public readonly record struct PlayerSurvivalSleepState(ulong ProfileRevision,
        ulong RateRevision, bool IsAsleep, bool HasRestedOverride, float RestGainMultiplier);

    /// <summary>
    /// A server-owned spatial observation for this actor and rendered frame, never client
    /// permission/balance data. Stat ambient uses TemperatureStat's interior flag; body ambient
    /// additionally uses the actor area mask. Neither may be the host player's location.
    /// </summary>
    public readonly record struct PlayerSurvivalEnvironment(int Frame,
        float StatAmbientTemperature, float BodyAmbientTemperature, float ExternalWetness,
        bool InteriorSpaceWarmth, bool Snowing, bool Underwater, bool Swimming,
        bool InsideTemperatureVolume, float TemperatureVolumeTemperature,
        int HeldOnlyItemCount, bool InDeepCaves);

    /// <summary>Numeric progress only. Source HUD, audio, network FX and life require their owners.</summary>
    public readonly record struct PlayerSurvivalProgress(ulong Revision, int SourceFrame,
        float Health, float Hydration, float Fullness, float Rested, float Vitality,
        float BodyTemperature, float Wetness, bool Cold, bool Freezing, bool Burning,
        bool Smells, int ExpiredCookingRecipeId, float StatDamageDue, bool RequiresLifeTransition)
    {
        /// <summary>Profile and rates used by the last committed numeric frame, not the latest native event.</summary>
        public PlayerSurvivalSleepState Sleep { get; init; }
    }
}
