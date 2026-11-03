// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Crest;
using Il2CppInterop.Runtime;
using Modstoke.Loader;
using UnityEngine;
using Point = System.Numerics.Vector3;

namespace Modstoke.Sons
{
    public sealed partial class PlayerMovementModes
    {
        private sealed class WaterObservation
        {
            internal Vector3 Position, SurfaceVelocity;
            internal float? Height;
            internal bool InWater;
            internal PlayerWaterContactSample? NativeSample;
            internal WaterCoverage Coverage;
            internal TerrainHeight Terrain;
            internal OceanHeight Ocean;
            internal WaterColumn Volume;
        }

        /// <summary>
        /// Binds the exact restored native sensor without querying its held cache. The
        /// aggregate must activate carrier physics and water, then call
        /// TryActivateWaterObservation before the first movement step. No water enum is converted.
        /// </summary>
        public static bool TryPrepare(PlayerBridge bridge,
            PlayerWaterContactAuthority.ServerContinuation waterContacts,
            out PlayerMovementModes modes, out string detail)
        {
            modes = null;
            detail = "A held exact-actor native water continuation and restored movement configuration are required.";
            if (waterContacts == null || waterContacts.IsActive || waterContacts.Carrier.IsPhysicsActive
                || Time.inFixedTimeStep) return false;
            return TryBindNativeWater(bridge, waterContacts, out modes, out detail);
        }

        /// <summary>Observes an already active native sensor at a settled ground re-entry.</summary>
        public static bool TryCreate(PlayerBridge bridge,
            PlayerWaterContactAuthority.ServerContinuation waterContacts,
            out PlayerMovementModes modes, out string detail)
        {
            modes = null;
            if (!TryBindNativeWater(bridge, waterContacts, out var candidate, out detail)) return false;
            if (!candidate.TryActivateWaterObservation(out detail)) { candidate.Dispose(); return false; }
            modes = candidate;
            return true;
        }

        private static bool TryBindNativeWater(PlayerBridge bridge,
            PlayerWaterContactAuthority.ServerContinuation waterContacts,
            out PlayerMovementModes modes, out string detail)
        {
            modes = null;
            detail = "The native water continuation does not belong to this exact restored server carrier.";
            if (!PlayerRoles.IsAvailable || !GameBuild.IsRecognized
                || bridge?.IsServer != true || waterContacts == null || Time.inFixedTimeStep) return false;
            try
            {
                waterContacts.RequireCurrent();
                var carrier = waterContacts.Carrier;
                if (!bridge.TryReadCurrentTarget(out var entity) || !Same(entity, carrier.Entity)
                    || carrier.RestoredSurvival.Water == null
                    || !carrier.RestoredSurvival.Water.TryValidateNativeMovement(bridge, carrier, waterContacts, out detail))
                    return false;
                var candidate = new PlayerMovementModes(bridge, entity, default, waterContacts);
                if (!candidate.NativeWaterCurrent()) return false;
                bridge.Retired += candidate.Dispose;
                modes = candidate;
                detail = "Exact native water owner bound; no held cache was sampled or dry state inferred.";
                return true;
            }
            catch (Exception error)
            { detail = "Native movement water binding failed (" + error.GetType().Name + ")."; return false; }
        }

        /// <summary>
        /// Read the actual active sensor once at the no-physics-step activation boundary.
        /// This qualifies settled enrollment, not actor-region loading or source presentation.
        /// The aggregate retains ownership of the sensor and must retire a failed activation.
        /// </summary>
        public bool TryActivateWaterObservation(out string detail)
        {
            detail = "Activate the exact carrier physics and native water owner before observing movement water.";
            if (_waterContacts == null || _nativeWaterReady || Time.inFixedTimeStep
                || !_waterContacts.IsActive || !_waterContacts.Carrier.IsPhysicsActive) return false;
            try
            {
                if (!TryObserveWater(out var water, out detail)) return false;
                if (water.InWater && water.Position.y + SwimExitOffset <= water.Height.Value)
                {
                    detail = "Existing swim/deadband history cannot be reseeded as a settled ground enrollment.";
                    return false;
                }
                _nativeWaterReady = true;
                detail = "Active native contact/cache observation qualified the settled movement boundary.";
                return true;
            }
            catch (Exception error)
            { detail = "Native movement water activation failed (" + error.GetType().Name + ")."; return false; }
        }

        private bool NativeWaterCurrent()
        {
            if (!Current() || _waterContacts == null || _waterContacts.FailureReason != null
                || _nativeWaterConfiguration == null) return false;
            return Same(_waterContacts.Carrier.Entity, _entity)
                && ReferenceEquals(_nativeWaterConfiguration, _waterContacts.Carrier.RestoredSurvival.Water);
        }

        internal bool TryReadNativeWaterState(
            PlayerWaterContactAuthority.ServerContinuation contacts,
            out bool underwater, out bool swimming, out string detail)
        {
            underwater = swimming = false;
            detail = "The exact activated movement water continuation is required for native state reads.";
            if (!_nativeWaterReady || !ReferenceEquals(contacts, _waterContacts)
                || !NativeWaterCurrent() || !contacts.IsActive) return false;
            contacts.RequireCurrent();
            // This is the motor's retained PAC state, not a new geometric verdict at
            // the post-physics pose. The caller must first establish motor settlement.
            underwater = _underwater;
            swimming = _mode == PlayerMovementMode.Swim;
            detail = "Retained actor-local PAC underwater/swimming state observed.";
            return true;
        }

        private bool TryObserveNativeWater(Vector3 position, out WaterObservation observation, out string detail)
        {
            observation = null;
            detail = "The exact actor's native water observation is unavailable.";
            if (!NativeWaterCurrent() || !_waterContacts.TryRead(out var sample, out detail)
                || sample.ActorNetworkId != _entity.networkId.PackedValue || sample.Frame != Time.frameCount)
                return false;
            // PAC reads the cached height after its InWater membership gate, not the
            // GetWaterLevel boolean. Preserve both in the retained native sample.
            var result = new WaterObservation
            {
                Position = position, InWater = sample.InWater, Height = sample.Height,
                SurfaceVelocity = new Vector3(sample.SurfaceVelocity.X, sample.SurfaceVelocity.Y, sample.SurfaceVelocity.Z),
                NativeSample = sample
            };
            if (!WaterCurrent(result, out detail)) return false;
            observation = result;
            return true;
        }

        private bool TryObserveWater(out WaterObservation observation, out string detail)
        {
            observation = null;
            detail = "The server carrier's water geometry is unavailable.";
            if (!Current()) return false;
            Vector3 rootPosition = _root.position;
            if (!Finite(rootPosition)) return false;
            if (_waterContacts != null) return TryObserveNativeWater(rootPosition, out observation, out detail);
            var point = new Point(rootPosition.x, rootPosition.y + _settings.WaterlineOffset, rootPosition.z);
            if (!float.IsFinite(point.Y)) return false;

            // Reuse PlayerWaterEnvironment's bounded actor-point sampling, but take the
            // point from the migrated server root, not a remote presentation binding.
            if (!ServerWorld.TryReadWaterCoverage(point, _settings.WaterLayerMask,
                _settings.RequiredWaterTag, out var coverage, out var status))
            { detail += " " + status; return false; }
            var result = new WaterObservation { Position = rootPosition, Coverage = coverage };
            bool ocean = coverage.OceanVolumeCount > 0 && _settings.OceanMode != OceanWaterMode.Ignore;
            if (ocean && _settings.OceanMode == OceanWaterMode.IgnoreBelowTerrain)
            {
                if (!ServerWorld.TryReadTerrainHeight(point, out result.Terrain, out status))
                { detail += " " + status; return false; }
                ocean = result.Terrain.Height <= point.Y + 1f;
            }
            if (ocean)
            {
                if (!ServerWorld.TryReadOceanHeight(point, _settings.MinimumOceanSpatialLength,
                    out result.Ocean, out status)
                    || !ServerWorld.TryReadMovementOceanVelocity(result.Ocean,
                        out result.SurfaceVelocity, out status))
                { detail += " " + status; return false; }
                result.Height = result.Ocean.Height;
            }
            else if (coverage.NonOceanVolumeCount > 0)
            {
                if (!ServerWorld.TryReadWaterVolume(point, _settings.WaterLayerMask,
                    _settings.BlockerLayerMask, _settings.RequiredWaterTag, out result.Volume, out status))
                { detail += " " + status; return false; }
                result.Height = result.Volume.Height;
                // WaterLevelSensor.UpdateWaterDataCache explicitly zeros surfaceVel
                // before the non-ocean RayCastWaterLevel branch.
                result.SurfaceVelocity = Vector3.zero;
            }
            result.InWater = result.Height.HasValue;
            if (!WaterCurrent(result, out detail)) return false;
            observation = result;
            return true;
        }

        private bool WaterCurrent(WaterObservation water, out string detail)
        {
            detail = "The water observation or authoritative carrier pose changed.";
            if (water == null || !Current() || !_root.position.Equals(water.Position)) return false;
            if (_waterContacts != null)
                return water.NativeSample is { } sample && NativeWaterCurrent()
                    && sample.Frame == Time.frameCount && sample.ActorNetworkId == _entity.networkId.PackedValue
                    && _waterContacts.IsCurrentSample(sample) && _root.position.Equals(water.Position);
            if (water.Coverage == null || water.Coverage.Frame != Time.frameCount) return false;
            if (!ServerWorld.IsCurrentWaterCoverage(water.Coverage, out var status)
                || (water.Terrain != null && (water.Terrain.Frame != water.Coverage.Frame
                    || !ServerWorld.IsCurrentTerrainHeight(water.Terrain, out status)))
                || (water.Ocean != null && (water.Ocean.Frame != water.Coverage.Frame
                    || !ServerWorld.IsCurrentOceanHeight(water.Ocean, out status)))
                || (water.Volume != null && (water.Volume.Frame != water.Coverage.Frame
                    || !ServerWorld.IsCurrentWaterColumn(water.Volume))))
            { detail += " " + status; return false; }
            return Current() && _root.position.Equals(water.Position);
        }
    }

    public static partial class ServerWorld
    {
        // The existing height observation qualifies this exact CPU provider, world,
        // time source and bounded wave arrays. Native QueryFlow is asynchronous and
        // is deliberately not substituted with zero or another actor's cached result.
        internal static bool TryReadMovementOceanVelocity(OceanHeight height,
            out Vector3 velocity, out CollisionReadStatus status)
        {
            velocity = default;
            bool entered = false;
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (height == null || !ReferenceEquals(height.Observation.World, world) || height.Frame != frame)
                { status = CollisionReadStatus.StaleHit; return false; }
                if (!ReserveCollisionRead(world, frame, out status)) return false;
                _collisionReading = entered = true;
                if (!height.Observation.IsCurrent()) return CollisionChanged(out status);
                var ocean = OceanRenderer._Instance_k__BackingField;
                var flow = ocean._FlowProvider_k__BackingField;
                if (flow is null || flow.WasCollected || flow.Pointer == IntPtr.Zero
                    || IL2CPP.il2cpp_object_get_class(flow.Pointer) != Il2CppClassPointerStore<FlowProviderNull>.NativeClassPtr)
                { status = CollisionReadStatus.UnsupportedProfile; return false; }
                var point = new Vector3(height.Position.X, height.Position.Y, height.Position.Z);
                var original = point;
                bool sampled = height.Observation.Provider.GetSurfaceVelocity(ref point, height.MinimumSpatialLength, out velocity);
                if (!height.Observation.IsCurrent() || point != original
                    || ocean._FlowProvider_k__BackingField?.Pointer != flow.Pointer)
                    return CollisionChanged(out status);
                if (!sampled || !float.IsFinite(velocity.x) || !float.IsFinite(velocity.y) || !float.IsFinite(velocity.z))
                { velocity = default; status = CollisionReadStatus.SurfaceUnavailable; return false; }
                status = CollisionReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                velocity = default;
                status = CollisionReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
            finally { if (entered) _collisionReading = false; }
        }
    }
}
