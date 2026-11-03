// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Atmosphere;

namespace Modstoke.Sons
{
    public static partial class EnvironmentReads
    {
        /// <summary>
        /// Copies stored temperature simulation fields from the active local temperature manager.
        /// This does not calculate temperature at a position or apply indoor, elevation or player modifiers.
        /// Success means the fields were observed, not that the simulation has initialized, the world is
        /// ready or the values agree with the server. No temperature update or receiver callback is invoked.
        /// </summary>
        public static bool TryReadTemperature(out TemperatureSnapshot snapshot,
            out EnvironmentReadStatus status)
        {
            snapshot = null;
            status = CheckThread();
            if (status != EnvironmentReadStatus.Success)
                return false;

            try
            {
                var temperature = TemperatureManager._Instance_k__BackingField;
                if (temperature is null || temperature.WasCollected || !temperature || !temperature.isActiveAndEnabled)
                {
                    status = EnvironmentReadStatus.TemperatureUnavailable;
                    return false;
                }

                var dayRange = temperature._dayMinMax;
                var nightRange = temperature._nightMinMax;
                snapshot = new TemperatureSnapshot(temperature._currentAmbientTemperature,
                    temperature._minMaxValuesInitialized, dayRange.x, dayRange.y, nightRange.x, nightRange.y,
                    temperature._appliedDay, temperature._appliedNight);
                return true;
            }
            catch (Exception error)
            {
                status = EnvironmentReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }
    }

    /// <summary>
    /// Managed temperature simulation fields copied locally, without retained native objects.
    /// Values use the native temperature scale without conversion or normalization. They are not a
    /// measured temperature, a forecast, player body temperature or a location-adjusted ambient value.
    /// The fields are sequential observations, not an atomic simulation snapshot.
    /// </summary>
    public sealed class TemperatureSnapshot
    {
        internal TemperatureSnapshot(float rawAmbientTemperature, bool areSimulationRangesInitialized,
            float dayMinimumTemperature, float dayMaximumTemperature, float nightMinimumTemperature,
            float nightMaximumTemperature, bool isDayApplied, bool isNightApplied)
        {
            RawAmbientTemperature = rawAmbientTemperature;
            AreSimulationRangesInitialized = areSimulationRangesInitialized;
            DayMinimumTemperature = areSimulationRangesInitialized ? dayMinimumTemperature : null;
            DayMaximumTemperature = areSimulationRangesInitialized ? dayMaximumTemperature : null;
            NightMinimumTemperature = areSimulationRangesInitialized ? nightMinimumTemperature : null;
            NightMaximumTemperature = areSimulationRangesInitialized ? nightMaximumTemperature : null;
            IsDayApplied = isDayApplied;
            IsNightApplied = isNightApplied;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>
        /// The manager's stored ambient simulation value. Its source and last simulation-update time
        /// are not established by this read, and the range-initialization flag does not validate it.
        /// Non-finite values are preserved rather than replaced with a default temperature.
        /// </summary>
        public float RawAmbientTemperature { get; }
        /// <summary>The native min/max-initialization flag, not general temperature or world readiness.</summary>
        public bool AreSimulationRangesInitialized { get; }
        /// <summary>The stored day range's x value, or null while the native ranges are uninitialized.</summary>
        public float? DayMinimumTemperature { get; }
        /// <summary>The stored day range's y value, or null while the native ranges are uninitialized.</summary>
        public float? DayMaximumTemperature { get; }
        /// <summary>The stored night range's x value, or null while the native ranges are uninitialized.</summary>
        public float? NightMinimumTemperature { get; }
        /// <summary>The stored night range's y value, or null while the native ranges are uninitialized.</summary>
        public float? NightMaximumTemperature { get; }
        /// <summary>The native applied-day flag, not a current time-of-day or daylight observation.</summary>
        public bool IsDayApplied { get; }
        /// <summary>The native applied-night flag, preserved independently of the applied-day flag.</summary>
        public bool IsNightApplied { get; }
        /// <summary>Read completion time, not a simulation or replication timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
