// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Modstoke.Loader;
using TheForest.World;
using UnityEngine;

namespace Modstoke.Sons
{
    internal readonly record struct PlayerExposureReading(
        ulong SourceSequence, ulong WeatherSequence, float Wetness, float RainIntensity, bool Raining, bool Snowing,
        int Frame, Vector3 Position, int AreaMask, bool InCaves, bool InDeepCaves, ulong LocationRevision);
    internal readonly record struct PlayerExposureLocation(
        int Frame, Vector3 Position, int AreaMask, bool InCaves, bool InDeepCaves, ulong Revision);

    /// <summary>
    /// Owns one imported actor's override order, with server-resolved definitions. Host
    /// Add/Remove/OnDisable callbacks never change this order. Native server precipitation
    /// callbacks classify immediately against this actor's imported and continued native graph.
    /// </summary>
    internal sealed partial class PlayerSurvivalEnvironmentExposureServer : IDisposable
    {
        internal static readonly ConcurrentDictionary<nint, PlayerSurvivalEnvironmentExposureServer> Owners = new();
        private static readonly ConditionalWeakTable<PlayerCarrier, PlayerSurvivalSnapshot> Prepared = new();
        private readonly record struct ResolvedOverride(WeatherSystemWetnessOverride Value, int InstanceId, int Scene);
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerBridge _bridge;
        private readonly PlayerCarrier _carrier;
        private readonly PlayerSurvivalEnvironmentClimate _climate;
        private readonly PlayerSurvivalSnapshot _survival;
        private readonly PlayerStats _stats;
        private readonly int _statsId, _entityId;
        private readonly nint _statsPointer;
        private readonly Dictionary<ulong, PlayerExposureOverride> _known = new();
        private PlayerExposureSnapshot _history;
        private ResolvedOverride[] _overrides;
        private PlayerExposureGraph _graph;
        private ulong _weatherSequence;
        private bool _snowing, _registered;
        private int _snowCandidateFrame = -1;
        private bool _snowCandidate;
        internal bool IsDisposed { get; private set; }
        internal string FailureReason { get; private set; }

        private PlayerSurvivalEnvironmentExposureServer(PlayerBridge bridge,
            PlayerCarrier carrier, PlayerSurvivalEnvironmentClimate climate, PlayerStats stats)
        {
            _bridge = bridge; _carrier = carrier; _climate = climate; _stats = stats;
            _statsPointer = stats.Pointer; _statsId = stats.GetInstanceID(); _entityId = carrier.Entity.GetInstanceID();
            _survival = carrier.RestoredSurvival; _history = _survival.ExposureBaseline;
            _snowing = _history.Snowing;
        }

        internal static bool TryPrepare(PlayerBridge bridge, PlayerCarrier carrier,
            PlayerSurvivalEnvironmentClimate climate, HarmonySupportSnapshot support,
            out PlayerSurvivalEnvironmentExposureServer owner, out string detail)
        {
            owner = null;
            detail = "Exposure requires the exact held server carrier and its authenticated source exposure baseline.";
            if (bridge == null || carrier == null || climate == null || bridge.IsDisposed || !bridge.IsServer
                || bridge.IsGameplayActive || carrier.IsDisposed || carrier.IsPhysicsActive
                || !carrier.IsSurvivalRestored || carrier.RestoredSurvival?.ExposureBaseline == null || Time.inFixedTimeStep)
                return false;
            PlayerSurvivalEnvironmentExposureServer candidate = null;
            try
            {
                if (Prepared.TryGetValue(carrier, out _))
                { detail = "A retired exposure owner cannot restart this carrier from its old baseline."; return false; }
                if (!carrier.TryReadRestoredServerStats(out var stats)
                    || !PlayerSurvivalEnvironmentExposure.EnsureExposureHooks(support, out detail)) return false;
                candidate = new(bridge, carrier, climate, stats);
                candidate.RequireCurrent();
                candidate._overrides = candidate.Resolve(candidate._history);
                candidate.Remember(candidate._history);
                var order = new List<WeatherSystemWetnessOverride>(candidate._overrides.Length);
                foreach (var binding in candidate._overrides) order.Add(binding.Value);
                candidate._graph = PlayerExposureGraph.Create(carrier.PrimaryCollider,
                    order, candidate._history.CopyGraph(), out _);
                if (!Owners.TryAdd(candidate._statsPointer, candidate))
                    throw new InvalidOperationException("The exact stat receiver already owns exposure history.");
                candidate._registered = true;
                candidate.RegisterSpatial();
                Prepared.Add(carrier, candidate._survival);
                bridge.Retired += candidate.Dispose;
                owner = candidate;
                detail = "Exact source graph mapped to loaded server definitions; actor collider history, cave state and retained snow imported.";
                return true;
            }
            catch (Exception error)
            { candidate?.Dispose(); detail = "Server exposure preparation refused: " + error.Message; return false; }
        }

        // The envelope must authenticate capture + old/new actor identity before calling.
        // Source observations cannot change an authoritative server actor's activation graph.
        internal bool TryFoldSourceObservation(PlayerExposureSnapshot next, out string detail)
        {
            detail = "Exposure observations require the same capture and the next exact source sequence.";
            if (next == null || IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread) return false;
            try
            {
                RequireCurrent();
                if (next.Sequence == _history.Sequence)
                {
                    bool same = _history.SameObservation(next);
                    if (same) detail = "Exact exposure observation already folded; duplicate acknowledgement may be resent.";
                    return same;
                }
                if (next.CaptureId != _history.CaptureId || next.SourceNetworkId != _history.SourceNetworkId
                    || _history.Sequence == ulong.MaxValue || next.Sequence != _history.Sequence + 1
                    || next.SourceFrame < _history.SourceFrame) return false;
                if (next.OverrideCount != _history.OverrideCount
                    || !next.CopyGraph().AsSpan().SequenceEqual(_history.CopyGraph())) return false;
                for (int i = 0; i < next.OverrideCount; i++)
                    if (next.OverrideAt(i) != _history.OverrideAt(i))
                    { detail = "Client activation observations cannot replace server collider-qualified exposure history."; return false; }
                _history = next;
                // Source weather fields are observations only after restore. Never overwrite
                // server raw wetness or retained snow with a later client weather sample.
                detail = "Source observation acknowledged without replacing server actor activation or climate.";
                return true;
            }
            catch (Exception error) { detail = "Exposure observation refused: " + error.Message; return false; }
        }

        internal bool TryRead(out PlayerExposureReading reading, out string detail)
            => TryReadExposure(out reading, out detail);

        internal bool TryReadLocation(out PlayerExposureLocation location, out string detail)
        {
            location = default;
            detail = FailureReason ?? "Actor location history is unavailable.";
            if (IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep) return false;
            try
            {
                RequireCurrent(); _graph.RequireCurrent();
                Vector3 position = _carrier.Entity.transform.position;
                if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z))
                    throw new InvalidOperationException("Nonfinite actor location.");
                location = new(Time.frameCount, position, _graph.AreaMask, _graph.InCaves, _graph.InDeepCaves, _graph.Revision);
                detail = "Exact actor location and native cave/area history read without host substitution.";
                return true;
            }
            catch (Exception error) { Fail(error.Message); detail = FailureReason; return false; }
        }

        internal bool TryReadExposure(out PlayerExposureReading reading, out string detail)
        {
            reading = default;
            detail = FailureReason ?? "Actor exposure is unavailable.";
            if (IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep)
                return false;
            try
            {
                RequireCurrent();
                var climate = _climate.Capture();
                float wetness = climate.Wetness;
                float rain = PlayerSurvivalEnvironmentExposure.ReadRainIntensity(_climate.Weather);
                var selected = _graph.Selected();
                if (PlayerSurvivalEnvironmentExposure.Alive(selected))
                {
                    wetness = selected._wetnessOverride; rain = selected._rainOverride;
                }
                Vector3 position = _carrier.Entity.transform.position;
                if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)
                    || !float.IsFinite(wetness) || !float.IsFinite(rain)) throw new InvalidOperationException("Nonfinite actor exposure reading.");
                reading = new(_history.Sequence, _weatherSequence, wetness, rain, climate.CloudState == 3, _snowing,
                    Time.frameCount, position, _graph.AreaMask, _graph.InCaves, _graph.InDeepCaves, _graph.Revision);
                detail = "Actor native graph exposure and cave history read against server climate; no host location or stat writes.";
                return true;
            }
            catch (Exception error) { Fail(error.Message); detail = FailureReason; return false; }
        }

        internal void ObserveSnowClassification(nint weather, bool applyToRetainedSnow)
        {
            if (IsDisposed || FailureReason != null || weather != _climate.Weather.Pointer) return;
            try
            {
                RequireCurrent();
                if (_snowCandidateFrame == Time.frameCount)
                {
                    if (applyToRetainedSnow) _snowing = _snowCandidate;
                    return;
                }
                var frame = _climate.Capture();
                Vector3 position = _carrier.Entity.transform.position;
                if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z))
                    throw new InvalidOperationException("Actor precipitation position is nonfinite.");
                _graph.RequireCurrent();
                _snowCandidate = _climate.WouldSnowAt(position.y, _graph.InCaves, frame);
                _snowCandidateFrame = Time.frameCount;
                _weatherSequence = checked(_weatherSequence + 1);
                if (applyToRetainedSnow) _snowing = _snowCandidate;
            }
            catch (Exception error) { Fail(error.Message); }
        }

        private ResolvedOverride[] Resolve(PlayerExposureSnapshot snapshot)
        {
            var result = new ResolvedOverride[snapshot.OverrideCount];
            if (result.Length == 0) return result; // Explicitly observed empty source list only.
            var loaded = Resources.FindObjectsOfTypeAll<WeatherSystemWetnessOverride>();
            for (int i = 0; i < result.Length; i++)
            {
                var expected = snapshot.OverrideAt(i);
                if (_known.TryGetValue(expected.Identity, out var old)
                    && (old.ScenePath != expected.ScenePath || old.Hierarchy != expected.Hierarchy || old.ComponentIndex != expected.ComponentIndex))
                    throw new InvalidOperationException("A source override identity was reused for a different object.");
                foreach (var candidate in loaded)
                {
                    if (!PlayerSurvivalEnvironmentExposure.Alive(candidate)) continue;
                    var scene = candidate.gameObject.scene;
                    if (!scene.IsValid() || !scene.isLoaded || scene.path != expected.ScenePath) continue;
                    var actual = PlayerSurvivalEnvironmentExposure.Describe(candidate, expected.Identity);
                    if (actual.Hierarchy != expected.Hierarchy || actual.ComponentIndex != expected.ComponentIndex) continue;
                    if (result[i].Value != null || actual != expected)
                        throw new InvalidOperationException("Override scene identity is ambiguous or its server definition differs.");
                    result[i] = new(candidate, candidate.GetInstanceID(), scene.handle);
                }
                if (result[i].Value == null) throw new InvalidOperationException("The captured override has no matching loaded server object.");
                for (int j = 0; j < i; j++)
                    if (result[j].Value.Pointer == result[i].Value.Pointer) throw new InvalidOperationException("Multiple source identities resolved to one override.");
            }
            return result;
        }

        private void Remember(PlayerExposureSnapshot snapshot)
        {
            for (int i = 0; i < snapshot.OverrideCount; i++)
            { var value = snapshot.OverrideAt(i); _known[value.Identity] = value; }
        }

        private void RequireCurrent()
        {
            if (IsDisposed || Environment.CurrentManagedThreadId != _thread || _bridge.IsDisposed || !_bridge.IsServer
                || _carrier.IsDisposed || !_carrier.IsSurvivalRestored || !ReferenceEquals(_carrier.RestoredSurvival, _survival)
                || !_bridge.TryReadCurrentTarget(out var target)
                || !PlayerSurvivalEnvironmentExposure.Same(target, _carrier.Entity)
                || target.GetInstanceID() != _entityId || !_carrier.TryReadRestoredServerStats(out var stats)
                || !PlayerSurvivalEnvironmentExposure.Same(stats, _stats) || stats.GetInstanceID() != _statsId)
                throw new InvalidOperationException("Exact server exposure actor binding changed.");
            _climate.RequireCurrent();
        }

        internal void Fail(string detail) => FailureReason ??= detail;
        internal void ObserveHeldWetness()
        {
            try { RequireCurrent(); }
            catch (Exception error) { Fail(error.Message); }
        }
        internal void ObserveDirectSnowSet(nint weather)
        {
            if (!IsDisposed && weather == _climate.Weather.Pointer)
                Fail("A direct shared snow setter requires an actor-qualified transition; host value was not adopted.");
        }
        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("Retire exposure on its game thread.");
            if (IsDisposed) return;
            IsDisposed = true;
            _bridge.Retired -= Dispose;
            RetireSpatial();
            if (_registered) Owners.TryRemove(_statsPointer, out _);
            _registered = false;
        }
    }
}
