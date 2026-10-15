// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using OpenACAI.Endnight.Loader;
using Sons.Atmosphere;
using Sons.Environment;
using TheForest.World;

namespace OpenACAI.Sons
{
    /// <summary>Server precipitation requests through the game's native weather transition and replication.</summary>
    public static class OpenACAISonsServerWeather
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
        public static OpenACAISonsServerWeatherStatus RequestPrecipitation(OpenACAISonsPrecipitation precipitation)
        {
            var ownerThread = Volatile.Read(ref _ownerThreadId);
            if (ownerThread == 0)
                return OpenACAISonsServerWeatherStatus.Unavailable;
            if (Environment.CurrentManagedThreadId != ownerThread)
                return OpenACAISonsServerWeatherStatus.WrongThread;
            if ((uint)precipitation > (uint)OpenACAISonsPrecipitation.Heavy)
                return OpenACAISonsServerWeatherStatus.InvalidPrecipitation;
            if (_requestInProgress)
                return OpenACAISonsServerWeatherStatus.Busy;

            var requestStarted = false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return OpenACAISonsServerWeatherStatus.NotServer;
                var weather = WeatherSystem.Instance;
                if (weather is null || weather.WasCollected || !weather || !weather.isActiveAndEnabled)
                    return OpenACAISonsServerWeatherStatus.WeatherUnavailable;
                if (precipitation != OpenACAISonsPrecipitation.None && weather._rainBlocked)
                    return OpenACAISonsServerWeatherStatus.Blocked;
                var clouds = weather._cloudManager;
                if (clouds is null || clouds.WasCollected || !clouds || !clouds.isActiveAndEnabled)
                    return OpenACAISonsServerWeatherStatus.WeatherUnavailable;
                var rainTypes = WeatherSystem.GetRainTypes();
                if (rainTypes is null || rainTypes.WasCollected || !rainTypes)
                    return OpenACAISonsServerWeatherStatus.WeatherUnavailable;
                if (precipitation != OpenACAISonsPrecipitation.None)
                {
                    var seasons = SeasonsManager.Instance;
                    if (seasons is null || seasons.WasCollected || !seasons || !seasons.isActiveAndEnabled)
                        return OpenACAISonsServerWeatherStatus.WeatherUnavailable;
                }
                if (!HasOwnedReplicationPublisher())
                    return OpenACAISonsServerWeatherStatus.ReplicationUnavailable;

                // ForceRain drives the cloud transition and native stop path. Calling StartRaining directly
                // would skip that transition and its blocked-rain handling.
                _requestInProgress = true;
                requestStarted = true;
                weather.ForceRain((int)precipitation);
                return OpenACAISonsServerWeatherStatus.Requested;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return requestStarted ? OpenACAISonsServerWeatherStatus.RequestUncertain : OpenACAISonsServerWeatherStatus.Failed;
            }
            finally
            {
                if (requestStarted)
                    _requestInProgress = false;
            }
        }

        private static bool HasOwnedReplicationPublisher()
        {
            var receivers = WindManager.Receivers;
            if (receivers is null || receivers.WasCollected)
                return false;

            // Both native publishers register on owner attachment and unregister on detachment.
            // Check live ownership too: their Update methods otherwise rely on a cached server flag.
            BoltEntity found = null;
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
                if (found != null && found.Pointer != entity.Pointer)
                    return false;
                found = entity;
            }
            return found != null;
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Server precipitation request failed ("
                    + error.GetType().FullName + "). Further weather failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    /// <summary>Native precipitation strength, not a choice between rain and snow.</summary>
    public enum OpenACAISonsPrecipitation
    {
        None = 0,
        Light = 1,
        Medium = 2,
        Heavy = 3
    }

    public enum OpenACAISonsServerWeatherStatus
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
        /// <summary>Preflight failed before invoking ForceRain.</summary>
        Failed,
        /// <summary>ForceRain threw; it may already have changed weather state or invoked receivers.</summary>
        RequestUncertain
    }
}
