// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Crest;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        public const int MaximumOceanWaveComponents = 1024;

        /// <summary>
        /// Samples the current synchronous CPU Gerstner provider. Rejects asynchronous,
        /// flat fallback and unknown providers. A height does not establish ocean
        /// presence, cave eligibility, wet/dry state or permission to change oxygen.
        /// The native sampler may initialize its sea-level cache.
        /// </summary>
        public static bool TryReadOceanHeight(Vector3 position, float minimumSpatialLength,
            out OceanHeight snapshot, out CollisionReadStatus status)
        {
            snapshot = null;
            bool entered = false;
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (!Finite(position) || !float.IsFinite(minimumSpatialLength) || minimumSpatialLength < 0)
                { status = CollisionReadStatus.InvalidQuery; return false; }
                // Four inverse-position passes and one final displacement pass, each
                // bounded by the supported wave count before entering native code.
                for (int i = 0; i < 5; i++)
                    if (!ReserveCollisionRead(world, frame, out status)) return false;
                _collisionReading = entered = true;
                if (!OceanHeightObservation.TryCapture(world, frame, out var observation))
                { status = CollisionReadStatus.UnsupportedProfile; return false; }
                if (!observation.IsCurrent()) return CollisionChanged(out status);

                var point = new UnityEngine.Vector3(position.X, position.Y, position.Z);
                bool sampled = observation.Provider.SampleHeight(ref point, minimumSpatialLength, out float height);
                if (!observation.IsCurrent() || point.x != position.X
                    || point.y != position.Y || point.z != position.Z)
                    return CollisionChanged(out status);
                if (!sampled || !float.IsFinite(height))
                { status = CollisionReadStatus.SurfaceUnavailable; return false; }

                snapshot = new OceanHeight(observation, position, minimumSpatialLength, height);
                status = CollisionReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = CollisionReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
            finally { if (entered) _collisionReading = false; }
        }

        /// <summary>
        /// Rechecks world, frame, provider, root, wave-array identities and sampling time.
        /// Does not freeze wave contents, prove their update age or resample the height.
        /// Uses one shared collision-read budget unit.
        /// </summary>
        public static bool IsCurrentOceanHeight(OceanHeight snapshot,
            out CollisionReadStatus status)
        {
            bool entered = false;
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (snapshot == null)
                { status = CollisionReadStatus.InvalidQuery; return false; }
                if (!ReferenceEquals(world, snapshot.Observation.World) || frame != snapshot.Frame)
                { status = CollisionReadStatus.StaleHit; return false; }
                if (!ReserveCollisionRead(world, frame, out status)) return false;
                _collisionReading = entered = true;
                if (!snapshot.Observation.IsCurrent()) return CollisionChanged(out status);
                status = CollisionReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = CollisionReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
            finally { if (entered) _collisionReading = false; }
        }

        internal sealed class OceanHeightObservation
        {
            private readonly OceanRenderer _ocean;
            private readonly Transform _root;
            private readonly OceanWaveSpectrum _spectrum;
            private readonly Il2CppStructArray<float> _wavelengths, _amplitudes, _angles, _phases;
            private readonly int _oceanId, _providerId, _rootId, _gravityBits, _timeBits, _seaLevelBits;
            private readonly nint _timeProvider;

            private OceanHeightObservation(WorldRecordContext world, int frame,
                OceanRenderer ocean, ShapeGerstnerBatched provider, Transform root,
                OceanWaveSpectrum spectrum, Il2CppStructArray<float> wavelengths,
                Il2CppStructArray<float> amplitudes, Il2CppStructArray<float> angles,
                Il2CppStructArray<float> phases, nint timeProvider, float time, float seaLevel)
            {
                World = world;
                Frame = frame;
                _ocean = ocean;
                Provider = provider;
                _root = root;
                _spectrum = spectrum;
                _wavelengths = wavelengths;
                _amplitudes = amplitudes;
                _angles = angles;
                _phases = phases;
                _oceanId = ocean.GetInstanceID();
                _providerId = provider.GetInstanceID();
                _rootId = root.GetInstanceID();
                _gravityBits = BitConverter.SingleToInt32Bits(spectrum._gravityScale);
                _timeProvider = timeProvider;
                SampleTime = time;
                SeaLevel = seaLevel;
                _timeBits = BitConverter.SingleToInt32Bits(time);
                _seaLevelBits = BitConverter.SingleToInt32Bits(seaLevel);
            }

            internal WorldRecordContext World { get; }
            internal int Frame { get; }
            internal ShapeGerstnerBatched Provider { get; }
            internal float SampleTime { get; }
            internal float SeaLevel { get; }

            internal static bool TryCapture(WorldRecordContext world, int frame,
                out OceanHeightObservation observation)
            {
                observation = null;
                var ocean = OceanRenderer._Instance_k__BackingField;
                if (!LiveBallisticObject(ocean) || !ocean.isActiveAndEnabled) return false;
                var selected = ocean._CollisionProvider_k__BackingField;
                if (!ExactGerstner(selected)) return false;
                var provider = selected.TryCast<ShapeGerstnerBatched>();
                var root = ocean.Root;
                if (!LiveBallisticObject(provider) || !provider.isActiveAndEnabled
                    || !LiveBallisticObject(root)) return false;
                var spectrum = provider._spectrum;
                var wavelengths = provider._wavelengths;
                var amplitudes = provider._amplitudes;
                var angles = provider._angleDegs;
                var phases = provider._phases;
                if (!ValidWaves(spectrum, wavelengths, amplitudes, angles, phases)
                    || !TryGetTimeProvider(ocean, out nint timeProvider)) return false;
                float time = ocean.CurrentTime;
                float seaLevel = root.position.y;
                if (!float.IsFinite(time) || !float.IsFinite(seaLevel)) return false;
                observation = new OceanHeightObservation(world, frame, ocean, provider, root,
                    spectrum, wavelengths, amplitudes, angles, phases, timeProvider, time, seaLevel);
                return observation.IsCurrent();
            }

            internal bool IsCurrent()
            {
                if (!CollisionContextCurrent(World, Frame)
                    || !SameBallisticObject(_ocean, OceanRenderer._Instance_k__BackingField)
                    || !_ocean.isActiveAndEnabled || _ocean.GetInstanceID() != _oceanId
                    || !LiveBallisticObject(Provider) || !Provider.isActiveAndEnabled
                    || Provider.GetInstanceID() != _providerId
                    || !SameBallisticObject(_root, _ocean.Root) || _root.GetInstanceID() != _rootId
                    || !SameBallisticObject(_spectrum, Provider._spectrum)) return false;
                var selected = _ocean._CollisionProvider_k__BackingField;
                if (!ExactGerstner(selected) || selected.Pointer != Provider.Pointer
                    || !SameArray(_wavelengths, Provider._wavelengths)
                    || !SameArray(_amplitudes, Provider._amplitudes)
                    || !SameArray(_angles, Provider._angleDegs) || !SameArray(_phases, Provider._phases)
                    || !ValidWaves(_spectrum, _wavelengths, _amplitudes, _angles, _phases)
                    || BitConverter.SingleToInt32Bits(_spectrum._gravityScale) != _gravityBits
                    || !TryGetTimeProvider(_ocean, out nint timeProvider) || timeProvider != _timeProvider
                    || BitConverter.SingleToInt32Bits(_ocean.CurrentTime) != _timeBits
                    || BitConverter.SingleToInt32Bits(_root.position.y) != _seaLevelBits) return false;
                return CollisionContextCurrent(World, Frame);
            }

            private static bool ExactGerstner(ICollProvider provider)
                => provider is not null && !provider.WasCollected
                    && IL2CPP.il2cpp_object_get_class(provider.Pointer)
                        == Il2CppClassPointerStore<ShapeGerstnerBatched>.NativeClassPtr;

            private static bool TryGetTimeProvider(OceanRenderer ocean, out nint pointer)
            {
                pointer = 0;
                // Avoid the TimeProvider getter's lazy default-provider allocation.
                Il2CppObjectBase provider = LiveBallisticObject(ocean._timeProvider)
                    ? ocean._timeProvider : ocean._timeProviderDefault;
                if (provider is null || provider.WasCollected) return false;
                pointer = provider.Pointer;
                return pointer != 0;
            }

            private static bool SameArray(Il2CppStructArray<float> first, Il2CppStructArray<float> second)
                => first is not null && second is not null && !first.WasCollected && !second.WasCollected
                    && first.Pointer == second.Pointer && first.Length == second.Length;

            private static bool ValidWaves(OceanWaveSpectrum spectrum, Il2CppStructArray<float> wavelengths,
                Il2CppStructArray<float> amplitudes, Il2CppStructArray<float> angles, Il2CppStructArray<float> phases)
            {
                if (!LiveBallisticObject(spectrum) || !float.IsFinite(spectrum._gravityScale)
                    || spectrum._gravityScale < 0 || wavelengths is null || wavelengths.WasCollected
                    || amplitudes is null || amplitudes.WasCollected || angles is null || angles.WasCollected
                    || phases is null || phases.WasCollected) return false;
                int count = amplitudes.Length;
                if (count <= 0 || count > MaximumOceanWaveComponents || wavelengths.Length != count
                    || angles.Length != count || phases.Length != count) return false;
                for (int i = 0; i < count; i++)
                    if (!float.IsFinite(wavelengths[i]) || wavelengths[i] <= 0
                        || !float.IsFinite(amplitudes[i]) || !float.IsFinite(angles[i])
                        || !float.IsFinite(phases[i])) return false;
                return true;
            }
        }
    }

    /// <summary>
    /// Copied absolute CPU ocean height at a supplied point. Native provider and time
    /// identities support same-frame freshness checks, not water-coverage guarantees.
    /// </summary>
    public sealed class OceanHeight
    {
        internal OceanHeight(ServerWorld.OceanHeightObservation observation,
            Vector3 position, float minimumSpatialLength, float height)
        { Observation = observation; Position = position; MinimumSpatialLength = minimumSpatialLength; Height = height; }

        internal ServerWorld.OceanHeightObservation Observation { get; }
        public int Frame => Observation.Frame;
        public Vector3 Position { get; }
        public float MinimumSpatialLength { get; }
        public float Height { get; }
        public float SeaLevel => Observation.SeaLevel;
        /// <summary>Native time-source value observed while sampling; not a wave-data update timestamp.</summary>
        public float SampleTime => Observation.SampleTime;
    }
}
