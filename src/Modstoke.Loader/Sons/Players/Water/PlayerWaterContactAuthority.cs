// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Modstoke.Loader;
using global::Sons.Physics;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// Owns the exact local source sensor's numerical/contact-history observation under its
    /// native writer gate. No replacement sensor is enabled, no collider is re-admitted and
    /// no cave/provider callback is replayed. ServerContinuation restores these fields onto
    /// the qualified carrier's native sensor; actor-region loading remains separately owned.
    /// </summary>
    public sealed partial class PlayerWaterContactAuthority
    {
        private readonly BoltEntity _source;
        private readonly PlayerLifecycle _lifecycle;
        private readonly WaterLevelSensor _sensor;
        private readonly nint _sensorPointer;
        private readonly int _frame;
        private Observation _baseline;
        private bool _invalidated, _nativeMutation, _released;
        private bool _retiredSource, _retiringSource, _runnerRemoved;

        private PlayerWaterContactAuthority(BoltEntity source,
            PlayerLifecycle lifecycle, WaterLevelSensor sensor)
        {
            _source = source; _lifecycle = lifecycle; _sensor = sensor;
            _sensorPointer = sensor?.Pointer ?? 0; _frame = Time.frameCount;
        }

        /// <summary>
        /// Install the source-scoped native gates during adapter setup. Success means hook
        /// installation, not acquisition: TryHoldSource requires a strictly later frame.
        /// No sensor is paused or selected by this operation.
        /// </summary>
        public static bool TryPrepareSourceCapture(HarmonySupportSnapshot support, out string detail)
        {
            detail = "Water source capture setup requires the recognized player-role game thread.";
            if (!PlayerRoles.IsAvailable || !GameBuild.IsRecognized || Time.inFixedTimeStep)
                return false;
            if (!EnsureHooks(support, out detail)) return false;
            detail = "Source water gates installed; acquire a source hold on a strictly later frame.";
            return true;
        }

        /// <summary>
        /// Hooks must have observed a whole native call boundary before acquisition; the first
        /// call may install them and return false until a later frame. Call only on the player
        /// role game thread, after the original lifecycle has paused and drained accepted work.
        /// </summary>
        public static bool TryHoldSource(BoltEntity source, PlayerLifecycle lifecycle,
            HarmonySupportSnapshot support, out PlayerWaterContactAuthority owner, out string detail)
        {
            owner = null;
            detail = "The exact original local player must be paused before holding its native water sensor.";
            if (!PlayerRoles.IsAvailable || !GameBuild.IsRecognized || Time.inFixedTimeStep)
                return false;
            if (!EnsureHooks(support, out detail)) return false;
            if (Time.frameCount <= _hooksFrame)
            { detail = "Water hooks installed; acquisition must wait until the next frame's drained native boundary."; return false; }
            PlayerWaterContactAuthority candidate = null;
            try
            {
                var sensor = LocalPlayer._WaterLevelSensor_k__BackingField;
                candidate = new(source, lifecycle, sensor);
                if (!candidate.SourceBound() || Owners.ContainsKey(sensor.Pointer)
                    || (NativeDepth.TryGetValue(sensor.Pointer, out int depth) && depth != 0)) return false;
                candidate.CapturePresentationRig();
                Owners.Add(sensor.Pointer, candidate);
                candidate._baseline = candidate.Observe();
                if (!candidate.IsCurrent(out detail))
                { Owners.Remove(sensor.Pointer); return false; }
                owner = candidate;
                detail = "Exact source sensor held with ordered contacts, cave/cache phase, trigger geometry and pointer-free identities retained.";
                return true;
            }
            catch (Exception error)
            {
                if (candidate != null && Owners.TryGetValue(candidate._sensorPointer, out var held) && ReferenceEquals(held, candidate))
                    Owners.Remove(candidate._sensorPointer);
                detail = "Source water hold refused (" + error.GetType().Name + "): " + error.Message;
                return false;
            }
        }

        public bool TryCapture(out PlayerWaterContactSnapshot snapshot, out string detail)
        {
            snapshot = null;
            if (!IsCurrent(out detail)) return false;
            snapshot = _baseline.Snapshot;
            detail = "Frozen native water history exported; neither scene-water completeness nor server admission is implied.";
            return true;
        }

        public bool IsCurrent(out string detail)
        {
            detail = "The held water sensor changed, received a contact/lifecycle mutation, or lost its exact source binding.";
            if (_released || _invalidated || _offThreadWriter != 0 || !PlayerRoles.IsAvailable
                || Environment.CurrentManagedThreadId != _hookThread || _baseline == null) return false;
            try
            {
                if (!SourceBound() || !Owners.TryGetValue(_sensor.Pointer, out var held) || !ReferenceEquals(held, this)
                    || (NativeDepth.TryGetValue(_sensor.Pointer, out int depth) && depth != 0)) return false;
                int frame = Time.frameCount;
                var current = Observe();
                if (!_baseline.Matches(current) || frame != Time.frameCount) return false;
                detail = "Exact held sensor references, native list order, geometry, cached cave state and phases are unchanged.";
                return true;
            }
            catch (Exception error)
            { detail = "Water continuation recheck refused (" + error.GetType().Name + ")."; return false; }
        }

        /// <summary>
        /// Cancel an unchanged source hold before lifecycle resume. Rebase native frame clocks,
        /// retaining negative sentinels and the exact floating stay-check remainder. Failure
        /// retains this owner; it must not be interpreted as permission to resume gameplay.
        /// </summary>
        public bool TryResumeSource(out string detail)
        {
            if (!IsCurrent(out detail)) return false;
            try
            {
                var s = _baseline.Snapshot.State;
                int frame = Time.frameCount, shift = checked(frame - _frame);
                int cache = s.CacheFrame < 0 ? s.CacheFrame : checked(s.CacheFrame + shift);
                int cave = s.CaveFrame < 0 ? s.CaveFrame : checked(s.CaveFrame + shift);
                float offset = s.StayOffset;
                if (offset >= 0 && shift != 0)
                {
                    if (s.StayInterval <= 0)
                    { detail = "The native stay-check interval cannot be rebased without changing its scheduling."; return false; }
                    float phase = ((float)_frame + offset) % s.StayInterval;
                    offset = (phase - (float)frame % s.StayInterval + s.StayInterval) % s.StayInterval;
                    if (!float.IsFinite(offset) || ((float)frame + offset) % s.StayInterval != phase)
                    { detail = "The native floating stay-check phase is not exactly representable at the resumed frame."; return false; }
                }
                _sensor._cacheFrame = cache; _sensor._lastInCaveCheckFrame = cave;
                _sensor._triggerStayCheckIntervalOffset = offset;
                if (_sensor._cacheFrame != cache || _sensor._lastInCaveCheckFrame != cave
                    || _sensor._triggerStayCheckIntervalOffset != offset || frame != Time.frameCount)
                    throw new InvalidOperationException("Native water clock restoration changed during readback.");
                Owners.Remove(_sensor.Pointer); _released = true;
                detail = "Unchanged source water hold released with native cache/cave/stay phases preserved; lifecycle resume remains separate.";
                return true;
            }
            catch (Exception error)
            { _invalidated = true; detail = "Water resume refused; retain the source gate (" + error.GetType().Name + ")."; return false; }
        }

        /// <summary>
        /// After the sealed source session proves native retirement, unregister the retained
        /// source sensor and keep its lazy/contact writers gated. This does not project server
        /// water observations onto the retained controls or authorize full gameplay resume.
        /// </summary>
        public bool TryRetireSource(BoltMigrationSession session, out string detail)
        {
            detail = "Water retirement requires the exact retired source session and drained native callbacks.";
            if (_released || _baseline == null || _offThreadWriter != 0 || !PlayerRoles.IsAvailable
                || Environment.CurrentManagedThreadId != _hookThread || Time.inFixedTimeStep
                || session?.Role != BoltMigrationRole.Source || session.Snapshot == null
                || session.Peer?.IsCurrent != true || session.Peer.ServerSession != null) return false;
            try
            {
                var plan = session.Snapshot.Plan; var result = session.LastResult;
                var server = BoltNetwork.server;
                if (!plan.IsValid || plan.OldNetworkId != _baseline.Snapshot.SourceNetworkId
                    || plan.PrefabId != _baseline.Snapshot.PrefabId || plan.StateTypeId != _baseline.Snapshot.StateTypeId
                    || server is null || server.WasCollected
                    || ControlTransport.Resolve(session.Peer)?.Pointer != server.Pointer
                    || result?.MutationAttempted != true || result.Receipt?.Matches(session.Snapshot) != true
                    || result.Status is not (BoltMigrationStatus.Retired
                        or BoltMigrationStatus.ReferencesRepaired or BoltMigrationStatus.Completed)
                    || _lifecycle == null || !_lifecycle.IsPaused || !Same(_lifecycle.OriginalEntity, _source)
                    || !Alive(_source) || _source.isAttached
                    || Alive(BoltNetwork.FindEntity(new NetworkId(plan.OldNetworkId)))
                    || !Alive(_sensor) || !Owners.TryGetValue(_sensorPointer, out var held) || !ReferenceEquals(held, this)
                    || (NativeDepth.TryGetValue(_sensorPointer, out int depth) && depth != 0)) return false;
                if (_retiredSource && (_retiredMigrationId != plan.Id || _retiredGeneration != plan.Generation)) return false;
                _retiredMigrationId = plan.Id; _retiredGeneration = plan.Generation;
                _retiredSource = true;
                _retiringSource = true;
                try
                {
                    if (!_runnerRemoved) _sensor.OnDisable();
                    _sensor.enabled = false;
                }
                finally { _retiringSource = false; }
                if (!_runnerRemoved || _sensor.enabled) return false;
                detail = "Retained source water runner retired; contact/lazy writers remain gated until sensor destruction.";
                return true;
            }
            catch (Exception error)
            { detail = "Source water retirement remains held: " + error.Message; return false; }
        }

        /// <summary>Only destruction can discard an invalidated hold without a source continuation decision.</summary>
        public bool TryForgetDestroyedSource()
        {
            if (_released) return true;
            if (Environment.CurrentManagedThreadId != _hookThread || Alive(_sensor)
                || (NativeDepth.TryGetValue(_sensorPointer, out int depth) && depth != 0)) return false;
            Owners.Remove(_sensorPointer); _released = true;
            return true;
        }

        /// <summary>
        /// Abandon an uncommitted export after the adapter has cancelled its reservation.
        /// Preserve the current native state, including any accepted contact/reset/disable
        /// callbacks. Unlike TryResumeSource, this does not claim unchanged phase continuity.
        /// The source must still be the original attached, paused actor; never use after commit.
        /// </summary>
        public bool TryDiscardUncommittedCapture(out string detail)
        {
            detail = "Only the still-attached paused source can discard an uncommitted water export.";
            if (_released || _retiredSource || !PlayerRoles.IsAvailable || Environment.CurrentManagedThreadId != _hookThread)
                return false;
            try
            {
                if (_lifecycle == null || !_lifecycle.IsPaused || !Same(_lifecycle.OriginalEntity, _source)
                    || !Alive(_source) || !_source.isAttached || !_source.isOwner
                    || _source.networkId.PackedValue != _lifecycle.OriginalNetworkId
                    || !Owners.TryGetValue(_sensorPointer, out var held) || !ReferenceEquals(held, this)
                    || (NativeDepth.TryGetValue(_sensorPointer, out int depth) && depth != 0)) return false;
                _invalidated = true; Owners.Remove(_sensorPointer); _released = true;
                detail = "Uncommitted water export discarded; native current state retained without replay. Lifecycle resume remains separate.";
                return true;
            }
            catch (Exception error)
            { detail = "Water export discard refused (" + error.GetType().Name + ")."; return false; }
        }

        private bool SourceBound()
        {
            if (_lifecycle == null || !_lifecycle.IsPaused || !Same(_lifecycle.OriginalEntity, _source)
                || !Alive(_source) || !_source.isAttached || !_source.isOwner || _source.prefabId != BoltPrefabs.PlayerNetwork
                || _source.networkId.PackedValue != _lifecycle.OriginalNetworkId
                || !Same(BoltNetwork.FindEntity(_source.networkId), _source)
                || PlayerRoles.IsCurrentCarrier(_source) || !Alive(_sensor) || !_sensor.isActiveAndEnabled
                || _sensor._cachedThreadRunnerInstanceId == 0
                || IL2CPP.il2cpp_object_get_class(_sensor.Pointer) != Il2CppClassPointerStore<WaterLevelSensor>.NativeClassPtr)
                return false;
            var local = LocalPlayer._instance;
            if ((Alive(_sensor._rigidBody) && !Same(_sensor._rigidBody.GetComponentInParent<BoltEntity>(true), _source))
                || (Alive(_sensor._stayTriggerCollider)
                    && !Same(_sensor._stayTriggerCollider.GetComponentInParent<BoltEntity>(true), _source))) return false;
            return Alive(local) && Same(LocalPlayer._Entity_k__BackingField, _source)
                && Same(LocalPlayer._WaterLevelSensor_k__BackingField, _sensor) && Same(local._waterLevelSensor, _sensor)
                && Same(local.GetComponentInParent<BoltEntity>(true), _source)
                && Same(_sensor.GetComponentInParent<BoltEntity>(true), _source)
                && Alive(local._fpc) && Alive(local._animControl) && Alive(local._inventory)
                && Same(local._fpc.GetComponentInParent<BoltEntity>(true), _source)
                && Same(local._animControl.GetComponentInParent<BoltEntity>(true), _source)
                && Same(local._inventory.GetComponentInParent<BoltEntity>(true), _source)
                && PlayerLifecycle.IsInventoryIngressFenced(local._inventory, out bool frozen) && frozen;
        }

        private Observation Observe()
        {
            var s = _sensor;
            var references = new List<nint>();
            var sceneStamps = new List<(int Handle, bool Loaded)>();
            void Retain(Il2CppObjectBase value)
            {
                if (value is not null && value.WasCollected) throw new InvalidOperationException("A sensor reference was collected.");
                references.Add(value?.Pointer ?? 0);
                if (value is Component component && Alive(component))
                { var scene = component.gameObject.scene; sceneStamps.Add((scene.handle, scene.isLoaded)); }
            }
            var identities = new List<WaterColliderIdentity>();
            var indices = new Dictionary<nint, int>();
            int Contact(Collider collider)
            {
                Retain(collider);
                if (collider is null) return -1;
                if (indices.TryGetValue(collider.Pointer, out int index)) return index;
                if (identities.Count == PlayerWaterContactSnapshot.MaximumContacts)
                    throw new InvalidOperationException("Native contact identity count exceeds its bound.");
                index = identities.Count; indices.Add(collider.Pointer, index); identities.Add(Describe(collider)); return index;
            }
            int[] Contacts(Il2CppSystem.Collections.Generic.List<Collider> values)
            {
                Retain(values); if (values == null) return null;
                int count = values.Count;
                if (count < 0 || count > PlayerWaterContactSnapshot.MaximumContacts)
                    throw new InvalidOperationException("Native contact count exceeds its bound.");
                var result = new int[count]; for (int i = 0; i < count; i++) result[i] = Contact(values[i]);
                if (values.Count != count) throw new InvalidOperationException("Native contact count changed.");
                return result;
            }
            Retain(_source._entity); Retain(_source._entity.Serializer); Retain(s); Retain(s._rigidBody);
            Retain(s._stayTriggerCollider); Retain(s._overrideCheckInCaveAction);
            Retain(s._sampleHeightHelper); Retain(s._sampleFlowHelper); Retain(s._tagFilter);
            if (Alive(s._rigidBody))
                foreach (var collider in s._rigidBody.GetComponentsInChildren<Collider>(true))
                    if (Alive(collider) && Same(collider.attachedRigidbody, s._rigidBody)) Retain(collider);
            string[] tags = null;
            if (s._tagFilter != null)
            {
                int count = s._tagFilter.Count;
                if (count < 0 || count > PlayerWaterContactSnapshot.MaximumContacts)
                    throw new InvalidOperationException("Native tag count exceeds its bound.");
                tags = new string[count]; for (int i = 0; i < count; i++) tags[i] = s._tagFilter[i];
            }
            var tracked = Contacts(s._trackedColliders);
            var ocean = Contacts(s._trackedOceanColliders);
            var scratchSource = s._insideColliderResult; Retain(scratchSource);
            int[] scratch = null;
            if (scratchSource != null)
            {
                if (scratchSource.Length > PlayerWaterContactSnapshot.MaximumScratch)
                    throw new InvalidOperationException("Native water scratch buffer exceeds its bound.");
                scratch = new int[scratchSource.Length];
                for (int i = 0; i < scratch.Length; i++) scratch[i] = Contact(scratchSource[i]);
            }
            int last = Contact(s._lastWaterCollider);
            var transform = s.transform; Retain(transform);
            var body = s._rigidBody;
            var state = new WaterSensorContinuation(_frame, s._cacheFrame, s._lastInCaveCheckFrame,
                (int)s._caveCheckInterval, s._triggerStayCheckInterval, s._triggerStayCheckIntervalOffset,
                s._isInWater, s._isInOcean, s._inCave, s._autoUpdate, s._refreshOnTrigger, (int)s._oceanInteraction,
                s._waterLayerMask.value, s._blockerLayerMask.value, s._waterAndBlockerMask.value, s._objectWidth,
                s._cachedWaterHeight, s._cachedWaterDepth, s._waterVolumeLevelCache, WaterLevelSensor._cachedOceanSeaLevel,
                V(s._cachedWaterNormal), V(s._cachedWaterSurfaceVelocity), V(s._lastManualCheckPosition),
                V(transform.position), Q(transform.rotation), V(transform.lossyScale), Alive(body),
                Alive(body) ? V(body.position) : default, Alive(body) ? Q(body.rotation) : default,
                s._overrideCheckInCaveAction != null, s._sampleHeightHelper != null, s._sampleFlowHelper != null,
                s._cachedThreadRunnerInstanceId != 0);
            var serializer = _source._entity.Serializer.TryCast<NetworkState>();
            var snapshot = new PlayerWaterContactSnapshot(_source.networkId.PackedValue,
                _source.prefabId.Value, serializer.Meta.TypeId.Value, state, Describe(s._stayTriggerCollider),
                identities.ToArray(), tracked, ocean, scratch, tags, last, CaptureRuntime(s));
            byte[] export = snapshot.Export();
            // The same bounded decoder validates exported finite values and all references.
            if (!PlayerWaterContactSnapshot.TryImport(export, out _))
                throw new InvalidOperationException("Native water state is not representable by the contact export.");
            return new(snapshot, export, references.ToArray(), sceneStamps.ToArray(), s._cachedThreadRunnerInstanceId);
        }

        private sealed record Observation(PlayerWaterContactSnapshot Snapshot, byte[] Export,
            nint[] References, (int Handle, bool Loaded)[] Scenes, int Runner)
        {
            internal bool Matches(Observation other)
                => Runner == other.Runner && Export.AsSpan().SequenceEqual(other.Export)
                    && References.AsSpan().SequenceEqual(other.References) && Scenes.AsSpan().SequenceEqual(other.Scenes);
        }

        private static bool Alive(UnityEngine.Object value) => value is not null && !value.WasCollected && value;
        private static bool Same(UnityEngine.Object left, UnityEngine.Object right)
            => Alive(left) && Alive(right) && left.Pointer == right.Pointer;
    }
}
