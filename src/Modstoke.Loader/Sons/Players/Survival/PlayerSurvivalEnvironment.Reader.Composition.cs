// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerSurvivalEnvironmentReader
    {
        private PlayerSurvivalEnvironmentTemperature _temperature;
        private PlayerSurvivalEnvironmentExposureServer _exposure;
        private PlayerHeldOnlyAuthority _heldOnly;
        private bool _composing;

        /// <summary>
        /// Bind after all concrete owners have prepared against this restored carrier,
        /// before gameplay/physics release. The adapter retains and retires every producer.
        /// This operation neither calls GetWaterLevel nor registers its native runner.
        /// </summary>
        internal static bool TryPrepare(PlayerBridge bridge, PlayerCarrier carrier,
            PlayerAuthorityDynamics dynamics,
            PlayerSurvivalEnvironmentClimate climate,
            PlayerSurvivalEnvironmentTemperature temperature,
            PlayerSurvivalEnvironmentExposureServer exposure,
            PlayerWaterContactAuthority.ServerContinuation waterContacts,
            PlayerHeldOnlyAuthority heldOnly,
            out PlayerSurvivalEnvironmentReader reader, out string detail)
        {
            reader = null;
            detail = "Environment binding requires the exact held server carrier and all concrete actor owners.";
            if (bridge == null || carrier == null || dynamics == null || climate == null
                || temperature == null || exposure == null || waterContacts == null || heldOnly == null
                || bridge.IsDisposed || !bridge.IsServer || bridge.IsGameplayActive
                || carrier.IsDisposed || carrier.IsPhysicsActive || !carrier.IsSurvivalRestored
                || carrier.RestoredSurvival?.Water == null || Time.inFixedTimeStep) return false;
            var water = carrier.RestoredSurvival.Water;
            if (!water.TryValidateNativeMovement(bridge, carrier, waterContacts, out detail)) return false;
            PlayerSurvivalEnvironmentReader candidate = null;
            try
            {
                if (!carrier.TryReadRestoredServerStats(out var stats))
                { detail = "The restored server actor's exact stat receiver is unavailable."; return false; }
                candidate = new(bridge, carrier, dynamics, default, climate, stats);
                candidate.RequireCurrent();
                int frame = Time.frameCount;
                Vector3 position = candidate._root.position;
                Quaternion rotation = candidate._root.rotation;
                if (!Finite(position) || !Finite(rotation))
                    throw new InvalidOperationException("The held actor pose is nonfinite.");
                climate.Capture();
                candidate.RequireInteriorManager();
                if (!temperature.MatchesReaderBinding(bridge, carrier)
                    || !exposure.MatchesReaderBinding(bridge, carrier, climate)
                    || !ReferenceEquals(waterContacts.Carrier, carrier) || waterContacts.IsActive)
                    throw new InvalidOperationException("The environment producers do not share this held actor/climate binding.");
                // These reads are observations of existing held histories. Water instead
                // checks its restored lifetime, since its lazy read requires activation.
                if (!temperature.TryRead(out _, out detail)
                    || !exposure.TryReadLocation(out _, out detail)
                    || !exposure.TryReadExposure(out _, out detail)
                    || !heldOnly.TryRead(bridge, out _, out detail))
                { candidate.Dispose(); return false; }
                waterContacts.RequireCurrent();
                if (!candidate.TryBindWaterContacts(waterContacts, out detail))
                { candidate.Dispose(); return false; }
                candidate._temperature = temperature;
                candidate._exposure = exposure;
                candidate._heldOnly = heldOnly;
                candidate.RequireCurrent();
                if (bridge.IsGameplayActive || carrier.IsPhysicsActive || waterContacts.IsActive
                    || Time.frameCount != frame || !candidate._root.position.Equals(position)
                    || !candidate._root.rotation.Equals(rotation))
                    throw new InvalidOperationException("The actor left its exact held pose/frame during environment binding.");
                bridge.Retired += candidate.Dispose;
                reader = candidate;
                detail = "Concrete actor environment owners bound while held; no water getter, physics, runner or survival clock activated.";
                return true;
            }
            catch (Exception error)
            { candidate?.Dispose(); detail = "Environment owner binding refused: " + error.Message; return false; }
        }

        /// <summary>
        /// Read after active native water, climate, temperature-volume, cave/area and
        /// construction producers and settled actor physics. No stale sample, empty-hand
        /// default or point-water fallback is returned on failure. The bound motor owns
        /// underwater/swimming continuation; this reader never infers either from height.
        /// </summary>
        internal bool TryRead(out PlayerSurvivalEnvironment environment, out string detail)
        {
            environment = default;
            detail = "Environment reading requires the concrete bound owners and an active, settled game-thread render phase.";
            if (IsDisposed || _reading || _composing || Environment.CurrentManagedThreadId != _thread
                || Time.inFixedTimeStep || _temperature == null || _exposure == null || _heldOnly == null
                || _waterContacts == null || !_bridge.IsGameplayActive || !_carrier.IsPhysicsActive) return false;
            _composing = true;
            try
            {
                RequireCurrent();
                int frame = Time.frameCount;
                Vector3 position = _root.position;
                Quaternion rotation = _root.rotation;
                if (!Finite(position) || !Finite(rotation))
                    throw new InvalidOperationException("Actor environment pose is nonfinite.");
                var climate = _climate.Capture();
                if (!TryReadInterior(out var interior, out detail)) return false;
                // Interior uses the existing reader guard while querying its reusable buffers.
                // Retain the same guard for the remainder of this synchronous composition.
                _reading = true;
                if (!_temperature.TryRead(out var temperature, out detail)
                    || !_exposure.TryReadLocation(out var location, out detail)
                    || !_exposure.TryReadExposure(out var exposure, out detail)
                    || !_heldOnly.TryReadSurvivalState(_bridge, out var held, out var heldTransaction, out detail)) return false;
                if (!TryReadWaterContacts(out var water, out detail)
                    || !_dynamics.TryReadSurvivalEnvironmentWater(_bridge, _carrier, _waterContacts,
                        out bool underwater, out bool swimming, out detail)) return false;
                if (interior.Frame != frame || interior.Scene != _scene || !interior.Position.Equals(position)
                    || location.Frame != frame || !location.Position.Equals(position)
                    || exposure.Frame != frame || !exposure.Position.Equals(position)
                    || exposure.LocationRevision != location.Revision || exposure.AreaMask != location.AreaMask
                    || exposure.InCaves != location.InCaves || exposure.InDeepCaves != location.InDeepCaves
                    || water.Frame != frame || water.ActorNetworkId != _entity.networkId.PackedValue)
                    throw new InvalidOperationException("Actor environment observations do not share one frame, pose and location history.");
                float statAmbient = _climate.TemperatureAt(position.y, interior.InteriorSpaceWarmth, climate);
                // PlayerStats.UpdateTemperature additionally selects indoor clamping from
                // the actor area mask; TemperatureStat only uses interior-space warmth.
                float bodyAmbient = _climate.TemperatureAt(position.y,
                    interior.InteriorSpaceWarmth || (location.AreaMask & 0x7E0020) != 0, climate);
                if (!float.IsFinite(statAmbient) || !float.IsFinite(bodyAmbient)
                    || !float.IsFinite(temperature.Temperature) || !float.IsFinite(exposure.Wetness)
                    || exposure.Wetness < 0 || exposure.Wetness > 1 || held.Amount < 0)
                    throw new InvalidOperationException("A concrete environment producer returned invalid numeric inputs.");
                RequireCurrent();
                RequireInteriorManager();
                if (!WaterContactsCurrent(water, frame, out detail)
                    || !_temperature.TryRead(out var currentTemperature, out detail)
                    || !_exposure.TryReadLocation(out var currentLocation, out detail)
                    || !_exposure.TryReadExposure(out var currentExposure, out detail)
                    || !_heldOnly.TryReadSurvivalState(_bridge, out var currentHeld, out var currentHeldTransaction, out detail)
                    || !_dynamics.TryReadSurvivalEnvironmentWater(_bridge, _carrier, _waterContacts,
                        out bool currentUnderwater, out bool currentSwimming, out detail)) return false;
                if (currentTemperature != temperature || currentLocation != location || currentExposure != exposure
                    || currentHeld != held || currentHeldTransaction != heldTransaction
                    || currentUnderwater != underwater || currentSwimming != swimming
                    || _climate.Capture() != climate || Time.frameCount != frame
                    || !_root.position.Equals(position) || !_root.rotation.Equals(rotation))
                    throw new InvalidOperationException("Actor environment histories, custody, climate or pose changed during composition.");
                RequireCurrent();
                if (!WaterContactsCurrent(water, frame, out detail)) return false;
                // PlayerStats.UpdateWetness (18311DE50) selects 1 before storing +140h
                // when the actor is underwater or swimming. Indoor zeroing is later,
                // in UpdateWetnessInternal, and remains owned by the survival runtime.
                float externalWetness = underwater || swimming ? 1f : exposure.Wetness;
                environment = new(frame, statAmbient, bodyAmbient, externalWetness,
                    interior.InteriorSpaceWarmth, exposure.Snowing, underwater, swimming,
                    temperature.Inside, temperature.Temperature, held.Amount, location.InDeepCaves);
                detail = "Exact actor environment composed from native histories and settled movement; world-region production remains the adapter's responsibility.";
                return true;
            }
            catch (Exception error) { detail = "Environment composition refused: " + error.Message; return false; }
            finally { _reading = false; _composing = false; }
        }

        internal PlayerSurvivalEnvironment Read()
        {
            if (!TryRead(out var environment, out string detail))
                throw new InvalidOperationException(detail);
            return environment;
        }
    }
}
