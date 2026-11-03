// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Modstoke.Loader;
using global::Sons.Atmosphere;
using global::Sons.Environment;
using TheForest.World;

namespace Modstoke.Sons
{
    /// <summary>Read-only local clock, weather, season and temperature observations, including on clients and in single-player.</summary>
    public static partial class EnvironmentReads
    {
        private static int _ownerThreadId;
        private static int _reportedFailure;

        internal static void Initialize()
        {
            Interlocked.CompareExchange(ref _ownerThreadId, Environment.CurrentManagedThreadId, 0);
        }

        /// <summary>
        /// Copies the underlying and effective clocks from the active native time holder.
        /// Success does not establish world readiness, server synchronization or permission to change time.
        /// </summary>
        public static bool TryReadClock(out ClockSnapshot snapshot,
            out EnvironmentReadStatus status)
        {
            snapshot = null;
            status = CheckThread();
            if (status != EnvironmentReadStatus.Success)
                return false;

            try
            {
                if (!TimeOfDayHolder.TryGetInstance(out var holder)
                    || holder is null || holder.WasCollected || !holder || !holder.isActiveAndEnabled)
                {
                    status = EnvironmentReadStatus.ClockUnavailable;
                    return false;
                }

                // Native component getters use the stored TimeSpan, not the serialized editor fields.
                var gameTime = new TimeSpan(holder._timeOfDay.Ticks);
                var usingOverride = holder._usingOverrideTime;
                var effectiveTime = usingOverride ? new TimeSpan(holder._overrideTimeOfDay.Ticks) : gameTime;
                snapshot = new ClockSnapshot(gameTime, effectiveTime, usingOverride,
                    TimeOfDayHolder._networkManaged);
                return true;
            }
            catch (Exception error)
            {
                status = EnvironmentReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        /// <summary>
        /// Copies the active native weather system's current fields, not the last requested weather.
        /// Success does not confirm visible effects or agreement with other clients.
        /// </summary>
        public static bool TryReadWeather(out WeatherSnapshot snapshot,
            out EnvironmentReadStatus status)
        {
            snapshot = null;
            status = CheckThread();
            if (status != EnvironmentReadStatus.Success)
                return false;

            try
            {
                var weather = WeatherSystem.Instance;
                if (weather is null || weather.WasCollected || !weather || !weather.isActiveAndEnabled)
                {
                    status = EnvironmentReadStatus.WeatherUnavailable;
                    return false;
                }

                snapshot = new WeatherSnapshot((int)weather.CurrentRainType,
                    (int)weather.CloudStateValue, weather.IsSnowing, weather._rainBlocked);
                return true;
            }
            catch (Exception error)
            {
                status = EnvironmentReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        /// <summary>
        /// Copies stored season and transition-policy fields from the active local season manager.
        /// Success does not establish world readiness, transition completion or server synchronization.
        /// This read does not calculate a next season or infer precipitation from season values.
        /// </summary>
        public static bool TryReadSeason(out SeasonSnapshot snapshot,
            out EnvironmentReadStatus status)
        {
            snapshot = null;
            status = CheckThread();
            if (status != EnvironmentReadStatus.Success)
                return false;

            try
            {
                // Copy stored fields only; the convenience getters dispatch native methods.
                var seasons = SeasonsManager._Instance_k__BackingField;
                if (seasons is null || seasons.WasCollected || !seasons || !seasons.isActiveAndEnabled)
                {
                    status = EnvironmentReadStatus.SeasonUnavailable;
                    return false;
                }

                snapshot = new SeasonSnapshot((int)seasons._activeSeason,
                    (int)seasons._previousSeason, seasons._seasonIsLocked, SeasonsManager._seasonSwitchInstant);
                return true;
            }
            catch (Exception error)
            {
                status = EnvironmentReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        private static EnvironmentReadStatus CheckThread()
        {
            var ownerThread = Volatile.Read(ref _ownerThreadId);
            if (ownerThread == 0)
                return EnvironmentReadStatus.Unavailable;
            return Environment.CurrentManagedThreadId == ownerThread
                ? EnvironmentReadStatus.Success : EnvironmentReadStatus.WrongThread;
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Environment read failed ("
                    + error.GetType().FullName + "). Further environment read failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum EnvironmentReadStatus
    {
        Success,
        Unavailable,
        WrongThread,
        ClockUnavailable,
        WeatherUnavailable,
        Failed,
        SeasonUnavailable,
        TemperatureUnavailable
    }

    /// <summary>Managed clock values copied from the local time holder, without retained native objects.</summary>
    public sealed class ClockSnapshot
    {
        internal ClockSnapshot(TimeSpan gameTime, TimeSpan effectiveTime, bool isOverrideActive,
            bool isNetworkManaged)
        {
            GameTime = gameTime;
            EffectiveTime = effectiveTime;
            IsOverrideActive = isOverrideActive;
            IsNetworkManaged = isNetworkManaged;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>The underlying local clock, including its unadjusted native day count.</summary>
        public TimeSpan GameTime { get; }
        /// <summary>The override clock when enabled; otherwise the same copied value as GameTime.</summary>
        public TimeSpan EffectiveTime { get; }
        public bool IsOverrideActive { get; }
        /// <summary>The native holder's network-managed flag, not evidence of current synchronization.</summary>
        public bool IsNetworkManaged { get; }
        /// <summary>Read completion time, not a simulation or replication timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
    }

    /// <summary>Native cloud transition phases. Raining is not proof of visible local rain.</summary>
    public enum CloudState
    {
        Idle = 0,
        GrowingClouds = 1,
        ReducingClouds = 2,
        Raining = 3
    }

    /// <summary>Managed weather fields copied locally, without retained native objects or delivery claims.</summary>
    public sealed class WeatherSnapshot
    {
        internal WeatherSnapshot(int rawPrecipitation, int rawCloudState, bool isSnowing,
            bool isRainBlocked)
        {
            RawPrecipitation = rawPrecipitation;
            RawCloudState = rawCloudState;
            IsSnowing = isSnowing;
            IsRainBlocked = isRainBlocked;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>The native current-rain-type value, preserved even when not recognized.</summary>
        public int RawPrecipitation { get; }
        /// <summary>Known precipitation strength, or null for an unrecognized native value.</summary>
        public global::Modstoke.Sons.Precipitation? Precipitation =>
            (uint)RawPrecipitation <= (uint)global::Modstoke.Sons.Precipitation.Heavy
                ? (global::Modstoke.Sons.Precipitation)RawPrecipitation : null;
        public int RawCloudState { get; }
        /// <summary>Known native cloud phase, or null for an unrecognized value.</summary>
        public global::Modstoke.Sons.CloudState? CloudState =>
            (uint)RawCloudState <= (uint)global::Modstoke.Sons.CloudState.Raining
                ? (global::Modstoke.Sons.CloudState)RawCloudState : null;
        /// <summary>The native snow flag, not an observation of rendered snow particles.</summary>
        public bool IsSnowing { get; }
        /// <summary>The native rain-block flag, not a comprehensive effects or wetness override check.</summary>
        public bool IsRainBlocked { get; }
        /// <summary>Read completion time, not a simulation or replication timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
    }

    /// <summary>Native season labels, not a forecast or evidence of visible seasonal effects.</summary>
    public enum Season
    {
        Spring = 0,
        Summer = 1,
        Fall = 2,
        Winter = 3
    }

    /// <summary>Managed season fields copied locally, without retained native objects or delivery claims.</summary>
    public sealed class SeasonSnapshot
    {
        internal SeasonSnapshot(int rawActiveSeason, int rawPreviousSeason, bool isSeasonLocked,
            bool isSeasonSwitchInstant)
        {
            RawActiveSeason = rawActiveSeason;
            RawPreviousSeason = rawPreviousSeason;
            IsSeasonLocked = isSeasonLocked;
            IsSeasonSwitchInstant = isSeasonSwitchInstant;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>The manager's stored active-season value, preserved even when not recognized.</summary>
        public int RawActiveSeason { get; }
        /// <summary>Known active season, or null for an unrecognized native value.</summary>
        public Season? ActiveSeason =>
            (uint)RawActiveSeason <= (uint)Season.Winter
                ? (Season)RawActiveSeason : null;
        /// <summary>The stored previous-season value, not a history of completed transitions.</summary>
        public int RawPreviousSeason { get; }
        /// <summary>Known stored previous season, or null for an unrecognized native value.</summary>
        public Season? PreviousSeason =>
            (uint)RawPreviousSeason <= (uint)Season.Winter
                ? (Season)RawPreviousSeason : null;
        /// <summary>The manager's native lock flag, not a guarantee that no other caller can change the season.</summary>
        public bool IsSeasonLocked { get; }
        /// <summary>The native instant-switch policy flag, not whether a transition is running or completed.</summary>
        public bool IsSeasonSwitchInstant { get; }
        /// <summary>Read completion time, not a simulation or replication timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
