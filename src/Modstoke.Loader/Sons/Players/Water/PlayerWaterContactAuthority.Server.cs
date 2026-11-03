// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using Crest;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Loader;
using global::Sons.Physics;
using UnityEngine;
using UnityEngine.SceneManagement;
using V3 = System.Numerics.Vector3;

namespace Modstoke.Sons
{
    /// <summary>
    /// InWater is native enabled/last-contact membership; HasWaterSurface is the lazy
    /// GetWaterLevel result. InCave is the water ocean-admission cache, not InDeepCaves.
    /// </summary>
    public readonly record struct PlayerWaterContactSample(int Frame, ulong ActorNetworkId,
        bool InWater, bool HasWaterSurface, bool InOcean, bool InCave, float Height, float Depth, float VolumeLevel,
        V3 Normal, V3 SurfaceVelocity);

    public sealed partial class PlayerWaterContactAuthority
    {
        private static readonly Dictionary<nint, ServerContinuation> ServerSensors = new();
        private static ServerContinuation PreparingServer;

        /// <summary>
        /// Restores an authenticated reservation's history onto the exact carrier. The caller
        /// owns authentication; this operation has no approval callback and grants no migration
        /// readiness. In particular it cannot establish a missing actor-region streaming owner.
        /// </summary>
        public static bool TryRestoreServer(PlayerCarrier carrier,
            PlayerWaterContactSnapshot snapshot, HarmonySupportSnapshot support,
            out ServerContinuation continuation, out string detail)
        {
            continuation = null;
            detail = "Water restoration requires the held, restored, exact source-qualified server carrier.";
            if (!PlayerRoles.IsAvailable || !BoltNetwork.isServer || Time.inFixedTimeStep
                || carrier == null || carrier.IsDisposed || carrier.IsPhysicsActive || !carrier.IsSurvivalRestored
                || snapshot?.Runtime == null || PreparingServer != null
                || carrier.Profile.SourceNetworkId != snapshot.SourceNetworkId
                || carrier.Profile.PrefabId != snapshot.PrefabId || carrier.Profile.StateTypeId != snapshot.StateTypeId
                || !PlayerRoles.IsCurrentCarrier(carrier.Entity)) return false;
            if (!EnsureHooks(support, out detail)) return false;
            ServerContinuation candidate = null;
            try
            {
                foreach (var entry in new List<KeyValuePair<nint, ServerContinuation>>(ServerSensors))
                    if (!Alive(entry.Value._sensor)) ServerSensors.Remove(entry.Key);
                var s = snapshot.State;
                var water = carrier.RestoredSurvival.Water;
                if (water == null || water.WaterLayerMask != s.WaterMask || water.BlockerLayerMask != s.BlockerMask
                    || water.WaterAndBlockerLayerMask != s.CombinedMask || (int)water.NativeOceanMode != s.OceanMode
                    || water.ObjectWidth != s.ObjectWidth || (snapshot.Tags?.Count ?? 0) > 1
                    || (snapshot.Tags?.Count == 1 ? snapshot.Tags[0] : null) != water.RequiredWaterTag)
                    throw new InvalidOperationException("Restored survival configuration differs from water history.");
                if (s.HasCaveOverride)
                    throw new InvalidOperationException("The source cave override has no transferable actor-bound native owner.");
                if (!s.HasBody || !s.RunnerRegistered || !Alive(carrier.Body)
                    || snapshot.Tracked == null || snapshot.TrackedOcean == null || snapshot.Scratch == null
                    || snapshot.Scratch.Count == 0 || HasNull(snapshot.Tracked) || HasNull(snapshot.TrackedOcean)
                    || (s.InOcean && snapshot.TrackedOcean.Count == 0))
                    throw new InvalidOperationException("Source water initialization/accepted cleanup must settle before restoration.");
                if (carrier.Entity.GetComponentsInChildren<WaterLevelSensor>(true).Length != 0)
                    throw new InvalidOperationException("The carrier already has a native water sensor owner.");
                if (!V(carrier.Body.position).Equals(s.BodyPosition) || !Q(carrier.Body.rotation).Equals(s.BodyRotation))
                    throw new InvalidOperationException("Carrier and captured water body poses differ.");
                var resolved = new Collider[snapshot.Identities.Count];
                var seen = new HashSet<nint>();
                for (int i = 0; i < resolved.Length; i++)
                {
                    var status = Resolve(snapshot.Identities[i], out resolved[i]);
                    if (status != WaterContactIdentityStatus.Resolved || !seen.Add(resolved[i].Pointer))
                        throw new InvalidOperationException("Water contact " + i + " cannot be restored: " + status + ".");
                }
                candidate = new(carrier, snapshot, resolved);
                candidate.ResolveTopology();
                candidate.CaptureProviders();
                candidate.CaptureScenes();
                PreparingServer = candidate;
                try
                {
                    var sensor = candidate._target.gameObject.AddComponent<WaterLevelSensor>();
                    // Keep the allocation even if another prefix prevented our setup prefix.
                    if (candidate._sensor == null && Alive(sensor))
                    {
                        candidate._sensor = sensor;
                        candidate._registered = sensor._cachedThreadRunnerInstanceId != 0;
                        if (!ServerSensors.ContainsKey(sensor.Pointer)) ServerSensors.Add(sensor.Pointer, candidate);
                        throw new InvalidOperationException("Native water OnEnable isolation was not observed.");
                    }
                    if (!Same(sensor, candidate._sensor) || !ServerSensors.TryGetValue(sensor.Pointer, out var owner)
                        || !ReferenceEquals(owner, candidate) || sensor._cachedThreadRunnerInstanceId != 0)
                        throw new InvalidOperationException("Native water OnEnable isolation was not observed.");
                }
                finally { PreparingServer = null; }
                candidate.RestoreStorage();
                candidate.RequireCurrent();
                continuation = candidate;
                detail = "Ordered native contacts, lazy cache, helper storage and cave/stay clocks restored on the exact carrier; sensor remains held.";
                return true;
            }
            catch (Exception error)
            {
                detail = "Server water restoration refused: " + error.Message;
                try { candidate?.Dispose(); }
                catch (Exception cleanup)
                {
                    continuation = candidate;
                    candidate.Fail(detail);
                    detail += " Retain the failed cleanup owner: " + cleanup.Message;
                }
                return false;
            }
        }

        /// <summary>
        /// Owns the real native WaterLevelSensor on an existing carrier collision receiver.
        /// Native ManagedUpdate, physics callbacks and lazy getters continue its state. No
        /// additional physics body, overlap loop or substitute water classification is created.
        /// </summary>
        public sealed class ServerContinuation : IDisposable
        {
            internal readonly PlayerCarrier _carrier;
            private readonly PlayerWaterContactSnapshot _snapshot;
            private readonly Collider[] _resolved;
            private readonly BoltEntity _entity;
            private readonly nint _attachment, _serializer;
            private readonly ulong _networkId;
            private readonly int _thread = Environment.CurrentManagedThreadId;
            internal Transform _target;
            internal WaterLevelSensor _sensor;
            internal PlayerWaterPresentation _presentation;
            private Collider _stay;
            private Collider[] _topology;
            private int[] _sceneHandles;
            private OceanRenderer _ocean;
            private nint _heightProvider, _flowProvider;
            internal bool _active, _registering, _disposing, _registered;
            private bool _disposed;
            private int _sampleFrame = -1;
            private PlayerWaterContactSample _sample;
            public string FailureReason { get; private set; }
            public bool IsActive => _active && !_disposed && FailureReason == null;
            public PlayerCarrier Carrier => _carrier;

            internal ServerContinuation(PlayerCarrier carrier,
                PlayerWaterContactSnapshot snapshot, Collider[] resolved)
            {
                _carrier = carrier; _snapshot = snapshot; _resolved = resolved;
                _entity = carrier.Entity; _networkId = _entity.networkId.PackedValue;
                _attachment = _entity._entity.Pointer; _serializer = _entity._entity.Serializer.Pointer;
            }

            internal void ResolveTopology()
            {
                var source = _snapshot.Runtime;
                var all = _carrier.Body.GetComponentsInChildren<Collider>(true);
                var target = new List<Collider>();
                foreach (var collider in all)
                    if (Alive(collider) && Same(collider.attachedRigidbody, _carrier.Body)) target.Add(collider);
                if (target.Count != source.BodyColliders.Count)
                    throw new InvalidOperationException("Carrier collision callback topology differs from the source water body.");
                var used = new HashSet<nint>();
                foreach (var collider in source.BodyColliders)
                {
                    Collider match = null;
                    foreach (var candidate in target)
                        if (SameShape(collider, Describe(candidate)))
                        {
                            if (Alive(match)) throw new InvalidOperationException("Ambiguous carrier water callback collider.");
                            match = candidate;
                        }
                    if (!Alive(match) || !used.Add(match.Pointer))
                        throw new InvalidOperationException("A source water callback collider has no exact carrier geometry.");
                    if (collider == _snapshot.StayCollider) _stay = match;
                }
                if (_snapshot.StayCollider != null && !Alive(_stay))
                    throw new InvalidOperationException("Source stay-check collider is not owned by the restored body topology.");
                _target = source.SensorOnBody ? _carrier.Body.transform
                    : source.SensorOnStay && Alive(_stay) ? _stay.transform : null;
                if (!Alive(_target) || V(_target.position) != _snapshot.State.SensorPosition
                    || Q(_target.rotation) != _snapshot.State.SensorRotation || V(_target.lossyScale) != _snapshot.State.SensorScale)
                    throw new InvalidOperationException("Source water sensor callback receiver/pose is not represented by the carrier.");
                _topology = target.ToArray();
            }

            internal void CaptureProviders()
            {
                if (_snapshot.State.OceanMode == 1 && !_snapshot.State.InOcean) return;
                var state = _snapshot.Runtime;
                _ocean = OceanRenderer._Instance_k__BackingField;
                if (state.HeightProvider is not (1 or 2) || state.FlowProvider != 1
                    || !Alive(_ocean) || HeightProvider(_ocean) != state.HeightProvider || FlowProvider(_ocean) != state.FlowProvider)
                    throw new InvalidOperationException("Ocean query history requires matching synchronous native providers; pending GPU queries cannot be transferred.");
                if (!_snapshot.State.HasHeightHelper || !_snapshot.State.HasFlowHelper
                    || state.HeightPositions?.Count != 1 || state.Heights?.Count != 1 || state.Normals?.Count != 1
                    || state.Velocities?.Count != 1 || state.FlowPositions?.Count != 1 || state.Flows?.Count != 1)
                    throw new InvalidOperationException("Native ocean helper storage is not initialized.");
                _heightProvider = _ocean._CollisionProvider_k__BackingField.Pointer;
                _flowProvider = _ocean._FlowProvider_k__BackingField.Pointer;
            }

            internal void CaptureScenes()
            {
                _sceneHandles = new int[SceneManager.sceneCount];
                for (int i = 0; i < _sceneHandles.Length; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.IsValid() || !scene.isLoaded)
                        throw new InvalidOperationException("Water restoration cannot cross a scene loading transition.");
                    _sceneHandles[i] = scene.handle;
                }
            }

            internal void RestoreStorage()
            {
                var s = _snapshot.State; var r = _snapshot.Runtime; var sensor = _sensor;
                sensor._rigidBody = _carrier.Body; sensor._stayTriggerCollider = _stay;
                sensor._autoUpdate = s.AutoUpdate; sensor._refreshOnTrigger = s.RefreshOnTrigger;
                sensor._waterLayerMask = s.WaterMask; sensor._blockerLayerMask = s.BlockerMask;
                sensor._waterAndBlockerMask = s.CombinedMask; sensor._objectWidth = s.ObjectWidth;
                sensor._oceanInteraction = (WaterLevelSensor.OceanInteraction)s.OceanMode;
                sensor._caveCheckInterval = (WaterLevelSensor.CaveCheckInterval)s.CaveInterval;
                sensor._triggerStayCheckInterval = s.StayInterval;
                sensor._tagFilter = _snapshot.Tags == null ? null : new Il2CppSystem.Collections.Generic.List<string>();
                if (_snapshot.Tags != null) foreach (string tag in _snapshot.Tags) sensor._tagFilter.Add(tag);
                sensor._trackedColliders = RestoreList(_snapshot.Tracked);
                sensor._trackedOceanColliders = RestoreList(_snapshot.TrackedOcean);
                sensor._insideColliderResult = new Il2CppReferenceArray<Collider>(_snapshot.Scratch.Count);
                for (int i = 0; i < _snapshot.Scratch.Count; i++) sensor._insideColliderResult[i] = Contact(_snapshot.Scratch[i]);
                sensor._lastWaterCollider = Contact(_snapshot.LastWaterCollider);
                sensor._isInWater = s.InWater; sensor._isInOcean = s.InOcean; sensor._inCave = s.InCave;
                sensor._cachedWaterHeight = s.WaterHeight; sensor._cachedWaterDepth = s.WaterDepth;
                sensor._waterVolumeLevelCache = s.VolumeLevel; sensor._cachedWaterNormal = U(s.Normal);
                sensor._cachedWaterSurfaceVelocity = U(s.SurfaceVelocity); sensor._lastManualCheckPosition = U(s.LastManualPosition);
                sensor._overrideCheckInCaveAction = null;
                sensor._sampleHeightHelper = s.HasHeightHelper ? new SampleHeightHelper() : null;
                sensor._sampleFlowHelper = s.HasFlowHelper ? new SampleFlowHelper() : null;
                if (s.HasHeightHelper)
                {
                    var helper = sensor._sampleHeightHelper;
                    helper._queryPos = RestoreVectors(r.HeightPositions); helper._queryResult = RestoreVectors(r.Heights);
                    helper._queryResultNormal = RestoreVectors(r.Normals); helper._queryResultVel = RestoreVectors(r.Velocities);
                    helper._minLength = r.HeightMinimum;
                }
                if (s.HasFlowHelper)
                {
                    var helper = sensor._sampleFlowHelper;
                    helper._queryPos = RestoreVectors(r.FlowPositions); helper._queryResult = RestoreVectors(r.Flows);
                    helper._minLength = r.FlowMinimum;
                }
                RebaseClocks();
                VerifyRestoredStorage();
            }

            private Collider Contact(int index) => index < 0 ? null : _resolved[index];
            private Il2CppSystem.Collections.Generic.List<Collider> RestoreList(IReadOnlyList<int> entries)
            {
                var list = new Il2CppSystem.Collections.Generic.List<Collider>();
                foreach (int entry in entries) list.Add(Contact(entry));
                return list;
            }

            private void VerifyRestoredStorage()
            {
                var s = _snapshot.State;
                if (_sensor._trackedColliders.Count != _snapshot.Tracked.Count
                    || _sensor._trackedOceanColliders.Count != _snapshot.TrackedOcean.Count
                    || _sensor._isInWater != s.InWater || _sensor._isInOcean != s.InOcean || _sensor._inCave != s.InCave
                    || _sensor._cachedWaterHeight != s.WaterHeight || _sensor._cachedWaterDepth != s.WaterDepth
                    || _sensor._waterVolumeLevelCache != s.VolumeLevel || V(_sensor._cachedWaterNormal) != s.Normal
                    || V(_sensor._cachedWaterSurfaceVelocity) != s.SurfaceVelocity
                    || (_sensor._lastWaterCollider?.Pointer ?? 0) != (Contact(_snapshot.LastWaterCollider)?.Pointer ?? 0))
                    throw new InvalidOperationException("Native water storage readback differs from the source.");
                for (int i = 0; i < _snapshot.Tracked.Count; i++)
                    if (!Same(_sensor._trackedColliders[i], Contact(_snapshot.Tracked[i])))
                        throw new InvalidOperationException("Native water contact order changed during restoration.");
                for (int i = 0; i < _snapshot.TrackedOcean.Count; i++)
                    if (!Same(_sensor._trackedOceanColliders[i], Contact(_snapshot.TrackedOcean[i])))
                        throw new InvalidOperationException("Native ocean contact order changed during restoration.");
            }

            private void RebaseClocks()
            {
                var s = _snapshot.State; int frame = Time.frameCount;
                int Clock(int value)
                {
                    if (value < 0) return value;
                    int rebased = checked(value + checked(frame - s.SourceFrame));
                    if (rebased < 0) throw new InvalidOperationException("Water clock age cannot be represented in the server frame domain.");
                    return rebased;
                }
                float offset = s.StayOffset;
                if (offset >= 0 && frame != s.SourceFrame)
                {
                    if (s.StayInterval <= 0) throw new InvalidOperationException("Invalid native water stay interval.");
                    float phase = ((float)s.SourceFrame + offset) % s.StayInterval;
                    offset = (phase - (float)frame % s.StayInterval + s.StayInterval) % s.StayInterval;
                    if (!float.IsFinite(offset) || ((float)frame + offset) % s.StayInterval != phase)
                        throw new InvalidOperationException("Native floating water stay phase cannot be preserved.");
                }
                _sensor._cacheFrame = Clock(s.CacheFrame); _sensor._lastInCaveCheckFrame = Clock(s.CaveFrame);
                _sensor._triggerStayCheckIntervalOffset = offset;
            }

            /// <summary>Call at the same no-physics-step boundary as carrier activation, after source commit.</summary>
            public bool TryActivate(out string detail)
            {
                detail = "Native water continuation is no longer a held, unchanged restoration.";
                if (_active || _disposed || Time.inFixedTimeStep) return false;
                try
                {
                    RequireCurrent(); VerifyRestoredStorage();
                    for (int i = 0; i < _resolved.Length; i++)
                        if (Resolve(_snapshot.Identities[i], out var current) != WaterContactIdentityStatus.Resolved
                            || !Same(current, _resolved[i])) throw new InvalidOperationException("Restored contact changed before activation.");
                    RebaseClocks();
                    _registering = true;
                    try { _sensor.OnEnable(); }
                    finally
                    {
                        _registering = false;
                        _registered = _sensor._cachedThreadRunnerInstanceId != 0;
                    }
                    if (_sensor._cachedThreadRunnerInstanceId == 0)
                        throw new InvalidOperationException("Native water update registration was not established.");
                    _active = true;
                    detail = "Native water runner and existing carrier physics callbacks now own this actor's restored history.";
                    return true;
                }
                catch (Exception error) { Fail(error.Message); detail = FailureReason; return false; }
            }

            public bool TryRead(out PlayerWaterContactSample sample, out string detail)
            {
                sample = default; detail = FailureReason ?? "An active exact-actor native water continuation is required.";
                // The original getter is frame-lazy, not render-only. Movement may read
                // this same actor sensor during FixedUpdate; no second query loop runs.
                if (!IsActive) return false;
                try
                {
                    RequireCurrent();
                    int frame = Time.frameCount;
                    _sensor.GetWaterLevel(out bool wet, out float height, out float depth, out float volume,
                        out Vector3 normal, out Vector3 velocity, out Collider hit);
                    RequireCurrent();
                    if (Time.frameCount != frame || !float.IsFinite(height) || !float.IsFinite(depth)
                        || !float.IsFinite(volume) || !Finite(normal) || !Finite(velocity))
                        throw new InvalidOperationException("Native water read crossed a frame or produced nonfinite state.");
                    _sample = sample = new(frame, _networkId, _sensor.InWater, wet, _sensor.InOcean, _sensor._inCave,
                        height, depth, volume, V(normal), V(velocity));
                    _sampleFrame = frame;
                    detail = "Exact actor native contact/cache observation read; region-streaming completeness is a separate unresolved owner.";
                    return true;
                }
                catch (Exception error) { Fail(error.Message); detail = FailureReason; return false; }
            }

            internal bool IsCurrentSample(PlayerWaterContactSample sample)
            {
                if (!IsActive || _sampleFrame != Time.frameCount || sample != _sample) return false;
                try
                {
                    RequireCurrent();
                    return _sensor.InWater == sample.InWater && _sensor._isInWater == sample.HasWaterSurface
                        && _sensor.InOcean == sample.InOcean
                        && _sensor._inCave == sample.InCave && _sensor._cachedWaterHeight == sample.Height
                        && _sensor._cachedWaterDepth == sample.Depth && _sensor._waterVolumeLevelCache == sample.VolumeLevel
                        && V(_sensor._cachedWaterNormal) == sample.Normal && V(_sensor._cachedWaterSurfaceVelocity) == sample.SurfaceVelocity;
                }
                catch (Exception error) { Fail(error.Message); return false; }
            }

            internal void RequireCurrent()
            {
                if (_disposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread
                    || _offThreadWriter != 0
                    || !PlayerRoles.IsAvailable || !BoltNetwork.isServer || _carrier.IsDisposed
                    || (_active && !_carrier.IsPhysicsActive)
                    || !_carrier.IsSurvivalRestored || !Alive(_entity) || !_entity.isAttached
                    || _entity.networkId.PackedValue != _networkId || !Same(BoltNetwork.FindEntity(_entity.networkId), _entity)
                    || _entity._entity?.Pointer != _attachment || _entity._entity.Serializer?.Pointer != _serializer
                    || !PlayerRoles.IsCurrentCarrier(_entity) || !Alive(_sensor) || !_sensor.isActiveAndEnabled
                    || !Same(_sensor.transform, _target) || !Same(_sensor._rigidBody, _carrier.Body)
                    || (_sensor._stayTriggerCollider?.Pointer ?? 0) != (_stay?.Pointer ?? 0)
                    || _sensor._overrideCheckInCaveAction != null)
                    throw new InvalidOperationException("The exact server water actor/lifetime binding changed.");
                if (SceneManager.sceneCount != _sceneHandles.Length)
                    throw new InvalidOperationException("Loaded scenes changed; actor-region water loading must be requalified.");
                for (int i = 0; i < _sceneHandles.Length; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (scene.handle != _sceneHandles[i] || !scene.isLoaded)
                        throw new InvalidOperationException("A retained water scene changed or unloaded.");
                }
                foreach (var collider in _topology)
                    if (!Alive(collider) || !Same(collider.attachedRigidbody, _carrier.Body))
                        throw new InvalidOperationException("Actor water collision callback topology detached.");
                if (_heightProvider != 0 && (!Same(_ocean, OceanRenderer._Instance_k__BackingField)
                    || _ocean._CollisionProvider_k__BackingField?.Pointer != _heightProvider
                    || _ocean._FlowProvider_k__BackingField?.Pointer != _flowProvider))
                    throw new InvalidOperationException("The native ocean query provider lifetime changed.");
            }

            internal void Fail(string detail) { FailureReason ??= detail; _sampleFrame = -1; }
            private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

            public void Dispose()
            {
                if (Environment.CurrentManagedThreadId != _thread)
                    throw new InvalidOperationException("Retire the native water continuation on its game thread.");
                if (_disposed) return;
                if (Alive(_sensor) && NativeDepth.TryGetValue(_sensor.Pointer, out int depth) && depth != 0)
                    throw new InvalidOperationException("Accepted native water callbacks must drain before retirement.");
                _active = false;
                _disposing = true;
                try
                {
                    if (Alive(_sensor))
                    {
                        if (_registered)
                        {
                            _sensor.OnDisable();
                            _registered = false;
                        }
                        _sensor.enabled = false;
                        // Retain the gate until native destruction; Unity destruction is deferred.
                        UnityEngine.Object.Destroy(_sensor);
                    }
                    _disposed = true;
                }
                finally { _disposing = false; }
            }
        }
    }
}
