// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// Concrete readable subset, NOT a complete SurvivalEnvironment. Candidate temperatures
    /// have no assumed interior/area flag. RawWeatherWetness excludes actor overrides. Snow
    /// candidates are classifications, not the retained IsSnowing state. Water is qualified
    /// point geometry, not proof of loaded-world completeness or native sensor contact history.
    /// No conversion to SurvivalEnvironment is supplied while those producers are missing.
    /// </summary>
    internal readonly record struct PlayerSurvivalEnvironmentSpatial(
        int Frame, int Scene, Vector3 Position, Quaternion Rotation,
        float OutdoorAmbientTemperature, float IndoorAmbientTemperature,
        float RawWeatherWetness, bool Raining, bool WouldSnowInCaves, bool WouldSnowOutsideCaves,
        bool Swimming, WaterCoverage WaterCoverage, OceanHeight Ocean,
        WaterColumn NonOceanSurface, PlayerWaterRelation SampledWaterRelation);

    /// <summary>
    /// Reads the exact server carrier and concrete actor-owned environment producers,
    /// without LocalPlayer fallback, client flags, stat writes or a second clock.
    /// The spatial-only overload remains diagnostic; the concrete overload binds the
    /// imported histories while held, without advancing the native water continuation.
    /// </summary>
    internal sealed partial class PlayerSurvivalEnvironmentReader : IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerBridge _bridge;
        private readonly PlayerCarrier _carrier;
        private readonly PlayerAuthorityDynamics _dynamics;
        private readonly PlayerSurvivalSnapshot _snapshot;
        private readonly PlayerWaterConfiguration _water;
        private readonly PlayerMovementModeSettings _waterSettings;
        private readonly PlayerSurvivalEnvironmentClimate _climate;
        private readonly BoltEntity _entity;
        private readonly Transform _root;
        private readonly PlayerStats _stats;
        private readonly int _entityId, _rootId, _statsId, _scene;
        private bool _reading;

        internal bool IsDisposed { get; private set; }

        private PlayerSurvivalEnvironmentReader(PlayerBridge bridge,
            PlayerCarrier carrier, PlayerAuthorityDynamics dynamics,
            PlayerMovementModeSettings waterSettings,
            PlayerSurvivalEnvironmentClimate climate, PlayerStats stats)
        {
            _bridge = bridge; _carrier = carrier; _dynamics = dynamics;
            _climate = climate; _stats = stats; _waterSettings = waterSettings;
            _snapshot = carrier.RestoredSurvival; _water = _snapshot.Water;
            _entity = carrier.Entity; _root = _entity.transform;
            _entityId = _entity.GetInstanceID(); _rootId = _root.GetInstanceID(); _statsId = stats.GetInstanceID();
            _scene = _entity.gameObject.scene.handle;
        }

        /// <summary>
        /// approvedWaterSettings must be the adapter's already approved water mapping,
        /// not another client-provided profile. This prepares only the spatial diagnostic;
        /// runtime integration uses the overload taking the concrete history owners.
        /// </summary>
        internal static bool TryPrepare(PlayerBridge bridge, PlayerCarrier carrier,
            PlayerAuthorityDynamics dynamics,
            PlayerMovementModeSettings approvedWaterSettings,
            PlayerSurvivalEnvironmentClimate climate,
            out PlayerSurvivalEnvironmentReader reader, out string detail)
        {
            reader = null;
            detail = "Exact server bridge, restored carrier, prepared dynamics, approved water mapping and climate are required.";
            if (bridge == null || carrier == null || dynamics == null || climate == null
                || carrier.IsDisposed || !carrier.IsSurvivalRestored || carrier.RestoredSurvival?.Water == null)
                return false;
            var water = carrier.RestoredSurvival.Water;
            if (!water.TryCreateSettings(out _, out detail)) return false;
            if (!water.Matches(approvedWaterSettings))
            { detail = "The approved water mapping differs from this exact carrier's restored native settings."; return false; }
            PlayerSurvivalEnvironmentReader candidate = null;
            try
            {
                if (!carrier.TryReadRestoredServerStats(out var stats))
                { detail = "The restored server actor's exact stat receiver is unavailable."; return false; }
                candidate = new(bridge, carrier, dynamics, approvedWaterSettings, climate, stats);
                if (!candidate.TryReadSpatial(out _, out detail)) { candidate.Dispose(); return false; }
                bridge.Retired += candidate.Dispose;
                reader = candidate;
                detail = "Concrete actor spatial sampler prepared; missing actor histories prevent a complete SurvivalEnvironment.";
                return true;
            }
            catch (Exception error)
            { candidate?.Dispose(); detail = "Spatial sampler preparation refused: " + error.Message; return false; }
        }

        internal bool TryReadSpatial(out PlayerSurvivalEnvironmentSpatial spatial, out string detail)
        {
            spatial = default;
            detail = "Spatial sampling requires a live, non-reentrant game-thread render phase.";
            if (IsDisposed || _reading || _composing || Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep)
                return false;
            _reading = true;
            try
            {
                RequireCurrent();
                int frame = Time.frameCount;
                Vector3 position = _root.position;
                Quaternion rotation = _root.rotation;
                if (!Finite(position) || !Finite(rotation))
                    throw new InvalidOperationException("Actor spatial pose is nonfinite.");
                var climate = _climate.Capture();
                float outdoors = _climate.TemperatureAt(position.y, false, climate);
                float indoors = _climate.TemperatureAt(position.y, true, climate);
                if (!float.IsFinite(outdoors) || !float.IsFinite(indoors))
                    throw new InvalidOperationException("Actor ambient temperature is nonfinite.");
                bool swimming = ReadSwimming();
                if (!TryReadWater(position, out var water, out detail)) return false;
                RequireCurrent();
                if (!WaterCurrent(water, frame, out detail)) return false;
                if (_climate.Capture() != climate || ReadSwimming() != swimming
                    || Time.frameCount != frame || !_root.position.Equals(position) || !_root.rotation.Equals(rotation))
                    throw new InvalidOperationException("Actor pose, native climate or swimming changed during the read.");
                spatial = new(frame, _scene, position, rotation, outdoors, indoors, climate.Wetness,
                    climate.CloudState == 3, _climate.WouldSnowAt(position.y, true, climate),
                    _climate.WouldSnowAt(position.y, false, climate), swimming,
                    water.Coverage, water.Ocean, water.Volume, water.Relation);
                detail = "Actor climate candidates, motor swimming and loaded point-water geometry read; not a complete survival environment.";
                return true;
            }
            catch (Exception error) { detail = "Spatial environment read refused: " + error.Message; return false; }
            finally { _reading = false; }
        }

        private void RequireCurrent()
        {
            if (IsDisposed || Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep
                || _bridge.IsDisposed || !_bridge.IsServer || !_bridge.TryReadCurrentTarget(out var target)
                || !Same(target, _entity) || _entity.GetInstanceID() != _entityId
                || !Same(_entity.transform, _root) || _root.GetInstanceID() != _rootId
                || _entity.gameObject.scene.handle != _scene || !_entity.gameObject.activeInHierarchy
                || _carrier.IsDisposed || !_carrier.IsSurvivalRestored || !Same(_carrier.Entity, _entity)
                || !ReferenceEquals(_carrier.RestoredSurvival, _snapshot)
                || !_carrier.TryReadRestoredServerStats(out var stats) || !Same(stats, _stats)
                || _stats.GetInstanceID() != _statsId || !Same(_stats.transform, _root)
                || !_dynamics.OwnsCarrier(_carrier, _bridge))
                throw new InvalidOperationException("The spatial reader's exact server actor/dynamics binding changed.");
            _climate.RequireCurrent();
        }

        private bool ReadSwimming()
        {
            if (!_dynamics.TryReadSurvivalEnvironmentSwimming(_bridge, _carrier, out bool swimming))
                throw new InvalidOperationException("The exact prepared/active motor has no settled authoritative swimming state.");
            return swimming;
        }

        private static bool Alive(UnityEngine.Object value) => value is not null && !value.WasCollected && value;
        private static bool Same(UnityEngine.Object left, UnityEngine.Object right)
            => Alive(left) && Alive(right) && left.Pointer == right.Pointer;
        private static bool Finite(Vector3 value)
            => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        private static bool Finite(Quaternion value)
            => float.IsFinite(value.x) && float.IsFinite(value.y)
                && float.IsFinite(value.z) && float.IsFinite(value.w);

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("Dispose the actor spatial reader on its game thread.");
            if (IsDisposed) return;
            IsDisposed = true;
            _bridge.Retired -= Dispose;
        }
    }
}
