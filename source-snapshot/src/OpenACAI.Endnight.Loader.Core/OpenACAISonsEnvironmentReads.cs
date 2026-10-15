// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using OpenACAI.Endnight.Loader;
using Sons.Environment;
using TheForest.World;

namespace OpenACAI.Sons
{
    /// <summary>Read-only local clock and weather observations, including on clients and in single-player.</summary>
    public static class OpenACAISonsEnvironmentReads
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
        public static bool TryReadClock(out OpenACAISonsClockSnapshot snapshot,
            out OpenACAISonsEnvironmentReadStatus status)
        {
            snapshot = null;
            status = CheckThread();
            if (status != OpenACAISonsEnvironmentReadStatus.Success)
                return false;

            try
            {
                if (!TimeOfDayHolder.TryGetInstance(out var holder)
                    || holder is null || holder.WasCollected || !holder || !holder.isActiveAndEnabled)
                {
                    status = OpenACAISonsEnvironmentReadStatus.ClockUnavailable;
                    return false;
                }

                // Native component getters use the stored TimeSpan, not the serialized editor fields.
                var gameTime = new TimeSpan(holder._timeOfDay.Ticks);
                var usingOverride = holder._usingOverrideTime;
                var effectiveTime = usingOverride ? new TimeSpan(holder._overrideTimeOfDay.Ticks) : gameTime;
                snapshot = new OpenACAISonsClockSnapshot(gameTime, effectiveTime, usingOverride,
                    TimeOfDayHolder._networkManaged);
                return true;
            }
            catch (Exception error)
            {
                status = OpenACAISonsEnvironmentReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        /// <summary>
        /// Copies the active native weather system's current fields, not the last requested weather.
        /// Success does not confirm visible effects or agreement with other clients.
        /// </summary>
        public static bool TryReadWeather(out OpenACAISonsWeatherSnapshot snapshot,
            out OpenACAISonsEnvironmentReadStatus status)
        {
            snapshot = null;
            status = CheckThread();
            if (status != OpenACAISonsEnvironmentReadStatus.Success)
                return false;

            try
            {
                var weather = WeatherSystem.Instance;
                if (weather is null || weather.WasCollected || !weather || !weather.isActiveAndEnabled)
                {
                    status = OpenACAISonsEnvironmentReadStatus.WeatherUnavailable;
                    return false;
                }

                snapshot = new OpenACAISonsWeatherSnapshot((int)weather.CurrentRainType,
                    (int)weather.CloudStateValue, weather.IsSnowing, weather._rainBlocked);
                return true;
            }
            catch (Exception error)
            {
                status = OpenACAISonsEnvironmentReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        private static OpenACAISonsEnvironmentReadStatus CheckThread()
        {
            var ownerThread = Volatile.Read(ref _ownerThreadId);
            if (ownerThread == 0)
                return OpenACAISonsEnvironmentReadStatus.Unavailable;
            return Environment.CurrentManagedThreadId == ownerThread
                ? OpenACAISonsEnvironmentReadStatus.Success : OpenACAISonsEnvironmentReadStatus.WrongThread;
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Environment read failed ("
                    + error.GetType().FullName + "). Further environment read failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum OpenACAISonsEnvironmentReadStatus
    {
        Success,
        Unavailable,
        WrongThread,
        ClockUnavailable,
        WeatherUnavailable,
        Failed
    }

    /// <summary>Managed clock values copied from the local time holder, without retained native objects.</summary>
    public sealed class OpenACAISonsClockSnapshot
    {
        internal OpenACAISonsClockSnapshot(TimeSpan gameTime, TimeSpan effectiveTime, bool isOverrideActive,
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
    public enum OpenACAISonsCloudState
    {
        Idle = 0,
        GrowingClouds = 1,
        ReducingClouds = 2,
        Raining = 3
    }

    /// <summary>Managed weather fields copied locally, without retained native objects or delivery claims.</summary>
    public sealed class OpenACAISonsWeatherSnapshot
    {
        internal OpenACAISonsWeatherSnapshot(int rawPrecipitation, int rawCloudState, bool isSnowing,
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
        public OpenACAISonsPrecipitation? Precipitation =>
            (uint)RawPrecipitation <= (uint)OpenACAISonsPrecipitation.Heavy
                ? (OpenACAISonsPrecipitation)RawPrecipitation : null;
        public int RawCloudState { get; }
        /// <summary>Known native cloud phase, or null for an unrecognized value.</summary>
        public OpenACAISonsCloudState? CloudState =>
            (uint)RawCloudState <= (uint)OpenACAISonsCloudState.Raining
                ? (OpenACAISonsCloudState)RawCloudState : null;
        /// <summary>The native snow flag, not an observation of rendered snow particles.</summary>
        public bool IsSnowing { get; }
        /// <summary>The native rain-block flag, not a comprehensive effects or wetness override check.</summary>
        public bool IsRainBlocked { get; }
        /// <summary>Read completion time, not a simulation or replication timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
