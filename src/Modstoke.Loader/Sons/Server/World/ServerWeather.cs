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
    /// <summary>Server weather readiness, transition observations and native replicated precipitation requests.</summary>
    public static class ServerWeather
    {
        private static int _ownerThreadId;
        private static int _reportedFailure;
        private static bool _requestInProgress;

        internal static void Initialize()
        {
            Interlocked.CompareExchange(ref _ownerThreadId, Environment.CurrentManagedThreadId, 0);
        }

        /// <summary>
        /// Requests a native precipitation transition. Light, Medium and Heavy can produce rain or snow,
        /// according to game conditions; None requests the native stop path, not permanently clear skies.
        /// Requested means ForceRain returned, not that precipitation started or clients received it.
        /// The caller owns authorization and timing. This does not disable automatic weather or native blocking.
        /// </summary>
        public static ServerWeatherStatus RequestPrecipitation(Precipitation precipitation)
        {
            var status = CheckPrecipitationRequest(precipitation, out var weather);
            if (status != ServerWeatherStatus.Ready)
                return status;

            try
            {
                // ForceRain drives the cloud transition and native stop path. Calling StartRaining directly
                // would skip that transition and its blocked-rain handling.
                _requestInProgress = true;
                weather.ForceRain((int)precipitation);
                return ServerWeatherStatus.Requested;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return ServerWeatherStatus.RequestUncertain;
            }
            finally
            {
                _requestInProgress = false;
            }
        }

        /// <summary>
        /// Runs the same preflight as RequestPrecipitation without requesting weather or changing authority.
        /// Ready is a point-in-time observation, not a reservation, authorization or delivery guarantee.
        /// Starting precipitation may resolve the game's rain-effects cache with its native scene lookup;
        /// stopping does not require that lookup or a seasons manager. No polling is installed.
        /// </summary>
        public static ServerWeatherStatus CheckPrecipitationReadiness(Precipitation precipitation)
        {
            return CheckPrecipitationRequest(precipitation, out _);
        }

        /// <summary>
        /// Copies server precipitation, cloud phase and native transition timing without retaining native objects.
        /// Observed does not confirm request readiness, a completed transition or client delivery. The dice and
        /// timers also belong to automatic weather and other callers; they do not identify an SDK request.
        /// </summary>
        public static bool TryReadTransition(out PrecipitationTransitionSnapshot snapshot,
            out ServerWeatherStatus status)
        {
            snapshot = null;
            status = CheckThread();
            if (status != ServerWeatherStatus.Ready)
                return false;
            if (_requestInProgress)
            {
                status = ServerWeatherStatus.Busy;
                return false;
            }

            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                {
                    status = ServerWeatherStatus.NotServer;
                    return false;
                }
                var weather = WeatherSystem.Instance;
                if (weather is null || weather.WasCollected || !weather || !weather.isActiveAndEnabled)
                {
                    status = ServerWeatherStatus.WeatherUnavailable;
                    return false;
                }

                var current = new WeatherSnapshot((int)weather.CurrentRainType,
                    (int)weather.CloudStateValue, weather.IsSnowing, weather._rainBlocked);
                snapshot = new PrecipitationTransitionSnapshot(current, weather.RainDiceRoll,
                    new TimeSpan(weather._rainStartTime._timeSpan.Ticks), weather._activeRainDurationSeconds,
                    weather._baseTimeToWaitBeforeCheckingForRain);
                status = ServerWeatherStatus.Observed;
                return true;
            }
            catch (Exception error)
            {
                status = ServerWeatherStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        private static ServerWeatherStatus CheckThread()
        {
            var ownerThread = Volatile.Read(ref _ownerThreadId);
            if (ownerThread == 0)
                return ServerWeatherStatus.Unavailable;
            return Environment.CurrentManagedThreadId == ownerThread
                ? ServerWeatherStatus.Ready : ServerWeatherStatus.WrongThread;
        }

        private static ServerWeatherStatus CheckPrecipitationRequest(Precipitation precipitation,
            out WeatherSystem weather)
        {
            weather = null;
            var status = CheckThread();
            if (status != ServerWeatherStatus.Ready)
                return status;
            if ((uint)precipitation > (uint)Precipitation.Heavy)
                return ServerWeatherStatus.InvalidPrecipitation;
            if (_requestInProgress)
                return ServerWeatherStatus.Busy;

            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return ServerWeatherStatus.NotServer;
                weather = WeatherSystem.Instance;
                if (weather is null || weather.WasCollected || !weather || !weather.isActiveAndEnabled)
                    return ServerWeatherStatus.WeatherUnavailable;
                if (precipitation != Precipitation.None && weather._rainBlocked)
                    return ServerWeatherStatus.Blocked;
                var clouds = weather._cloudManager;
                if (clouds is null || clouds.WasCollected || !clouds || !clouds.isActiveAndEnabled)
                    return ServerWeatherStatus.WeatherUnavailable;
                if (precipitation != Precipitation.None)
                {
                    var seasons = SeasonsManager.Instance;
                    if (seasons is null || seasons.WasCollected || !seasons || !seasons.isActiveAndEnabled)
                        return ServerWeatherStatus.WeatherUnavailable;
                }
                if (!HasOwnedReplicationPublisher())
                    return ServerWeatherStatus.ReplicationUnavailable;
                if (precipitation != Precipitation.None)
                {
                    // The native stop path tolerates absent rain effects. GetRainTypes can scan the scene,
                    // so resolve it only for a start request whose other dependencies are ready.
                    var rainTypes = WeatherSystem.GetRainTypes();
                    if (rainTypes is null || rainTypes.WasCollected || !rainTypes)
                        return ServerWeatherStatus.WeatherUnavailable;
                }

                return ServerWeatherStatus.Ready;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return ServerWeatherStatus.Failed;
            }
        }

        private static bool HasOwnedReplicationPublisher()
        {
            var receivers = WindManager.Receivers;
            if (receivers is null || receivers.WasCollected)
                return false;

            // Both native publishers register on owner attachment and unregister on detachment.
            // Check live ownership too: their Update methods otherwise rely on a cached server flag.
            var found = IntPtr.Zero;
            foreach (var receiver in receivers)
            {
                if (receiver is null || receiver.WasCollected)
                    continue;
                var network = receiver.TryCast<NetworkWeather>();
                var proxy = network is null ? receiver.TryCast<CoopWeatherProxy>() : null;
                Bolt.EntityBehaviour<IWeatherState> publisher = network is null ? proxy : network;
                if (publisher is null || publisher.WasCollected || !publisher || !publisher.isActiveAndEnabled)
                    continue;
                if (!(network is null ? proxy._isServer : network._isServer))
                    continue;
                var entity = publisher.entity;
                if (entity is null || entity.WasCollected || !entity || !entity.isAttached || !entity.isOwner)
                    continue;
                var state = publisher.state;
                if (state is null || state.WasCollected)
                    continue;
                var pointer = entity.Pointer;
                if (found != IntPtr.Zero && found != pointer)
                    return false;
                found = pointer;
            }
            return found != IntPtr.Zero;
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server weather access failed ("
                    + error.GetType().FullName + "). Further weather failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    /// <summary>Copied server weather transition fields, not an operation handle or completion forecast.</summary>
    public sealed class PrecipitationTransitionSnapshot
    {
        internal PrecipitationTransitionSnapshot(WeatherSnapshot weather, int rawRainDiceRoll,
            TimeSpan rainStartGameTime, float rawActiveRainDurationSeconds, float rawAutomaticCheckCountdownSeconds)
        {
            Weather = weather;
            RawRainDiceRoll = rawRainDiceRoll;
            RainStartGameTime = rainStartGameTime;
            RawActiveRainDurationSeconds = rawActiveRainDurationSeconds;
            RawAutomaticCheckCountdownSeconds = rawAutomaticCheckCountdownSeconds;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public WeatherSnapshot Weather { get; }
        /// <summary>
        /// Native transition choice, not current precipitation: 0 stops, 1..3 select strength, and 4 selects
        /// a random cloud profile. Other values are preserved without interpretation.
        /// </summary>
        public int RawRainDiceRoll { get; }
        /// <summary>
        /// Strength selected for a growing-cloud transition, or null when no recognized strength is pending.
        /// This is not a promise that clouds will finish or precipitation will start.
        /// </summary>
        public Precipitation? PendingPrecipitation =>
            Weather.CloudState == global::Modstoke.Sons.CloudState.GrowingClouds && RawRainDiceRoll >= 1 && RawRainDiceRoll <= 3
                ? (Precipitation)RawRainDiceRoll : null;
        /// <summary>Native stored start time, including game days; it may be stale when precipitation is inactive.</summary>
        public TimeSpan RainStartGameTime { get; }
        /// <summary>Native duration in game-time seconds, not remaining time or a wall-clock countdown.</summary>
        public float RawActiveRainDurationSeconds { get; }
        /// <summary>
        /// Native automatic-check countdown in game-time seconds. It can be nonpositive and is not decremented
        /// during growing clouds or precipitation; its value does not schedule a guaranteed transition.
        /// </summary>
        public float RawAutomaticCheckCountdownSeconds { get; }
        /// <summary>Read completion time, not a simulation or replication timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
    }

    /// <summary>Native precipitation strength, not a choice between rain and snow.</summary>
    public enum Precipitation
    {
        None = 0,
        Light = 1,
        Medium = 2,
        Heavy = 3
    }

    public enum ServerWeatherStatus
    {
        /// <summary>The native request returned; the transition and client delivery are not confirmed.</summary>
        Requested,
        Unavailable,
        WrongThread,
        NotServer,
        InvalidPrecipitation,
        WeatherUnavailable,
        /// <summary>No unambiguous active, attached, locally owned weather publisher was registered.</summary>
        ReplicationUnavailable,
        /// <summary>The native weather system currently blocks rain. Stopping precipitation remains allowed.</summary>
        Blocked,
        /// <summary>A nested SDK precipitation request is already running.</summary>
        Busy,
        /// <summary>A read or request preflight failed before invoking ForceRain.</summary>
        Failed,
        /// <summary>ForceRain threw; it may already have changed weather state or invoked receivers.</summary>
        RequestUncertain,
        /// <summary>The request preflight passed at observation time; no weather change was requested.</summary>
        Ready,
        /// <summary>Native transition fields were copied; readiness, completion and delivery are not established.</summary>
        Observed
    }
}
