// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Loader;
using global::Sons.Atmosphere;
using TheForest.World;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// Prepared native scene configuration shared by actor readers. Does not run native
    /// updates, receiver callbacks or LocalPlayer-dependent weather methods. The aggregate
    /// must retire/reprepare this binding after scene/configuration replacement.
    /// </summary>
    internal sealed class PlayerSurvivalEnvironmentClimate : IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly int _temperatureId, _weatherId, _snowId, _seasonsId;
        private readonly float _startElevation, _peakElevation, _peakTemperature;
        private readonly float _indoorMinimum, _indoorMaximum, _snowTriggerDepth;

        internal TemperatureManager Temperature { get; }
        internal WeatherSystem Weather { get; }
        internal SnowHeightManager Snow { get; }
        internal SeasonsManager Seasons { get; }
        internal bool IsDisposed { get; private set; }

        private PlayerSurvivalEnvironmentClimate(TemperatureManager temperature,
            WeatherSystem weather, SnowHeightManager snow, SeasonsManager seasons)
        {
            Temperature = temperature; Weather = weather; Snow = snow; Seasons = seasons;
            _temperatureId = temperature.GetInstanceID(); _weatherId = weather.GetInstanceID();
            _snowId = snow.GetInstanceID(); _seasonsId = seasons.GetInstanceID();
            _startElevation = temperature._startingElevationForTemperatureDrop;
            _peakElevation = temperature._mountainPeakElevation;
            _peakTemperature = temperature._mountainPeakTemperature;
            _indoorMinimum = temperature._minIndoorTemperature;
            _indoorMaximum = temperature._maxIndoorTemperature;
            _snowTriggerDepth = weather._triggerSnowAtDepth;
            if (!float.IsFinite(_startElevation) || !float.IsFinite(_peakElevation)
                || !float.IsFinite(_peakTemperature) || !float.IsFinite(_indoorMinimum)
                || !float.IsFinite(_indoorMaximum) || !float.IsFinite(_snowTriggerDepth)
                || !float.IsFinite(_peakElevation - _startElevation)
                || _peakElevation <= _startElevation || _indoorMaximum < _indoorMinimum)
                throw new InvalidOperationException("Native climate configuration is invalid.");
        }

        internal static bool TryPrepare(out PlayerSurvivalEnvironmentClimate climate,
            out string detail)
        {
            climate = null;
            detail = "Initialized server temperature, weather, snow and season managers are required.";
            if (!PlayerRoles.IsAvailable || !GameBuild.IsRecognized
                || !BoltNetwork.isServer || Time.inFixedTimeStep) return false;
            try
            {
                var temperature = TemperatureManager._Instance_k__BackingField;
                var weather = WeatherSystem.Instance;
                var snow = SnowHeightManager._Instance_k__BackingField;
                var seasons = SeasonsManager._Instance_k__BackingField;
                if (!Active(temperature) || !Active(weather) || !Active(snow) || !Active(seasons)
                    || !temperature._minMaxValuesInitialized) return false;
                var candidate = new PlayerSurvivalEnvironmentClimate(temperature, weather, snow, seasons);
                candidate.RequireCurrent();
                climate = candidate;
                detail = "Native climate identities/configuration prepared; this does not establish producer scheduling or actor receiver history.";
                return true;
            }
            catch (Exception error) { detail = "Climate preparation refused: " + error.Message; return false; }
        }

        internal void RequireCurrent()
        {
            if (IsDisposed || Environment.CurrentManagedThreadId != _thread
                || !PlayerRoles.IsAvailable || !GameBuild.IsRecognized || !BoltNetwork.isServer
                || !Current(Temperature, TemperatureManager._Instance_k__BackingField, _temperatureId)
                || !Current(Weather, WeatherSystem.Instance, _weatherId)
                || !Current(Snow, SnowHeightManager._Instance_k__BackingField, _snowId)
                || !Current(Seasons, SeasonsManager._Instance_k__BackingField, _seasonsId)
                || !Temperature._minMaxValuesInitialized
                || Temperature._startingElevationForTemperatureDrop != _startElevation
                || Temperature._mountainPeakElevation != _peakElevation
                || Temperature._mountainPeakTemperature != _peakTemperature
                || Temperature._minIndoorTemperature != _indoorMinimum
                || Temperature._maxIndoorTemperature != _indoorMaximum
                || Weather._triggerSnowAtDepth != _snowTriggerDepth)
                throw new InvalidOperationException("The prepared server climate identity/configuration changed.");
        }

        internal ClimateFrame Capture()
        {
            RequireCurrent();
            int season = (int)Seasons._activeSeason;
            SeasonProperties properties = season switch
            {
                0 => Seasons._springProperties, 1 => Seasons._summerProperties,
                2 => Seasons._fallProperties, 3 => Seasons._winterProperties, _ => null
            };
            var snow = Snow._currentProfile;
            if (properties == null || properties.WasCollected || !properties
                || snow == null || snow.WasCollected)
                throw new InvalidOperationException("The native active season or transitioning snow profile is unavailable.");
            var value = new ClimateFrame(Temperature._currentAmbientTemperature, Weather._wetnessCurrent,
                (int)Weather.CloudStateValue, (int)properties._season,
                snow._snowHeight, snow._fadeRange, snow._snowAmount);
            if (!float.IsFinite(value.Ambient) || !Unit(value.Wetness)
                || value.CloudState is < 0 or > 3 || value.Season is < 0 or > 3
                || !float.IsFinite(value.SnowHeight) || !float.IsFinite(value.SnowFadeRange)
                || value.SnowFadeRange <= 0 || !float.IsFinite(value.SnowAmount))
                throw new InvalidOperationException("Native climate data is nonfinite, uninitialized or unsupported.");
            return value;
        }

        internal float TemperatureAt(float height, bool indoors, in ClimateFrame frame)
        {
            // TemperatureManager.GetAmbientTemperature(Vector3,bool), VA 182A21500.
            float temperature = frame.Ambient;
            float rise = height - _startElevation;
            if (rise > 0)
            {
                float t = Mathf.Clamp01(rise / (_peakElevation - _startElevation));
                temperature += (_peakTemperature - temperature) * t;
            }
            return indoors ? Mathf.Clamp(temperature, _indoorMinimum, _indoorMaximum) : temperature;
        }

        internal bool WouldSnowAt(float height, bool inCaves, in ClimateFrame frame)
        {
            // ShouldSnow (1830675B0) and GetSnowDepthAtHeight (182A1E300), with
            // supplied cave membership. This is a classification, not IsSnowing:
            // CheckForRainToSnow only runs in Raining and StopRaining retains the flag.
            float depth = Mathf.Clamp01(Mathf.Clamp01((height - frame.SnowHeight) / frame.SnowFadeRange)
                * frame.SnowAmount);
            return depth >= _snowTriggerDepth || frame.Season == 3 && !inCaves;
        }

        internal readonly record struct ClimateFrame(float Ambient, float Wetness, int CloudState,
            int Season, float SnowHeight, float SnowFadeRange, float SnowAmount);

        private static bool Active(Behaviour value)
            => value is not null && !value.WasCollected && value && value.isActiveAndEnabled;
        private static bool Current(Behaviour expected, Behaviour actual, int id)
            => Active(expected) && Active(actual) && expected.Pointer == actual.Pointer && expected.GetInstanceID() == id;
        private static bool Unit(float value) => float.IsFinite(value) && value >= 0 && value <= 1;

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("Dispose the climate binding on its game thread.");
            IsDisposed = true;
        }
    }
}
