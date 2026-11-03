// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using global::Sons.Animation;
using global::Sons.Gameplay.GrabBag;
using global::Sons.Multiplayer;
using global::Sons.Multiplayer.Gui;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using KnownPlayers = Il2CppSystem.Collections.Generic.Dictionary<BoltConnection, global::Sons.Multiplayer.Gui.PlayerListBase.KnownPlayerData>;

namespace Modstoke.Sons
{
    public static partial class PlayerMigrationReferences
    {
        private sealed class CacheSet
        {
            private readonly PlayerBinding _source;
            private readonly List<Cache> _slots = new();
            private readonly HashSet<IntPtr> _receivers = new();
            private readonly HashSet<IntPtr> _stimuli = new();
            private readonly HashSet<IntPtr> _names = new();
            private readonly HashSet<IntPtr> _lod = new();
            private readonly HashSet<IntPtr> _behaviours = new();
            private readonly HashSet<IntPtr> _stats = new();
            private readonly HashSet<(IntPtr, IntPtr)> _known = new();
            private readonly List<ComponentOwner> _doomed = new();
            private readonly List<LodCache> _lodCaches = new();
            private Il2CppArrayBase<ReceiveItemsInteractionTrigger> _allReceivers;
            private Il2CppArrayBase<PlayerActionStimuli> _allStimuli;
            private Il2CppArrayBase<PlayerName> _allNames;
            private Il2CppArrayBase<NetworkTransformLodManager> _lodManagers;
            private Il2CppArrayBase<EntityBehaviour> _allBehaviours;
            private Il2CppArrayBase<PlayerStats> _allStats;
            private readonly NetworkTransform[] _oldTransforms;
            private bool _rediscovered;
            internal int Count => _slots.Count;

            internal CacheSet(PlayerBinding source)
            {
                _source = source;
                _allReceivers = Resources.FindObjectsOfTypeAll<ReceiveItemsInteractionTrigger>();
                _allStimuli = Resources.FindObjectsOfTypeAll<PlayerActionStimuli>();
                _allNames = Resources.FindObjectsOfTypeAll<PlayerName>();
                _lodManagers = Resources.FindObjectsOfTypeAll<NetworkTransformLodManager>();
                Require(Live(_allReceivers) && Live(_allStimuli) && Live(_allNames) && Live(_lodManagers),
                    "Loaded native cache owners could not be enumerated.");
                _oldTransforms = new[] { source.State.Transform, source.State.RaftTransform, source.State.HeadVR,
                    source.State.RightHandVR, source.State.LeftHandVR, source.State.ConnectedTransform };
                // The source's RigBindings owns these slots and writes them AFTER this repair.
                // Other peers must repair surviving caches, not resurrect the retired replica.
                if (!source.RetainsRoot)
                {
                    _allBehaviours = Resources.FindObjectsOfTypeAll<EntityBehaviour>();
                    _allStats = Resources.FindObjectsOfTypeAll<PlayerStats>();
                    Require(Live(_allBehaviours) && Live(_allStats), "Loaded player entity/state caches could not be enumerated.");
                    foreach (var owner in _allBehaviours)
                        if (RefersToOldPlayer(owner) && _behaviours.Add(owner.Pointer))
                            if (!ExcludeDoomed(owner)) _slots.Add(new BehaviourCache(owner, owner._entity));
                    foreach (var owner in _allStats)
                        if (RefersToOldPlayer(owner) && _stats.Add(owner.Pointer))
                        {
                            Require((owner._playerEntityCache is null || Same(owner._playerEntityCache, source.UnityEntity))
                                && (owner._playerStateCache is null || Same(owner._playerStateCache, source.State)),
                                "A matching PlayerStats cache contains mixed player identities.");
                            Require(!Same(owner, TheForest.Utils.LocalPlayer.Stats),
                                "A remote player's stats cache aliases the retained local player's stats.");
                            if (!ExcludeDoomed(owner)) _slots.Add(new StatsCache(owner));
                        }
                }
                foreach (var owner in _allReceivers)
                    if (Alive(owner) && Same(owner._ownerEntiy, source.UnityEntity) && _receivers.Add(owner.Pointer))
                    {
                        Require(!owner._isInteractionActive, "A matching receive-items interaction is active.");
                        if (!ExcludeDoomed(owner)) _slots.Add(new ReceiveCache(owner, source.UnityEntity));
                    }
                foreach (var owner in _allStimuli)
                    if (Alive(owner) && owner._playerNetworkId.PackedValue == source.Id && _stimuli.Add(owner.Pointer))
                        if (!ExcludeDoomed(owner)) _slots.Add(new StimuliCache(owner, source.Id));
                foreach (var owner in _allNames)
                    if (Alive(owner) && Same(owner._playerState, source.State) && _names.Add(owner.Pointer))
                        if (!ExcludeDoomed(owner)) _slots.Add(new NameCache(owner, source.State));
                var properties = new HashSet<int>();
                foreach (var manager in _lodManagers)
                    if (Alive(manager) && RefersToOldPlayer(manager._transformsBinder) && _lod.Add(manager.Pointer))
                    {
                        if (ExcludeDoomed(manager)) continue;
                        var cache = new LodCache(manager, source);
                        Require(properties.Add(cache.PropertyIndex), "Multiple surviving LOD managers claim one player transform property.");
                        _lodCaches.Add(cache);
                        _slots.Add(cache);
                    }
                var table = PlayerListBase._knownPlayers;
                if (table is not null)
                {
                    Require(Live(table), "The known-player table is unavailable.");
                    var records = new HashSet<IntPtr>();
                    foreach (var pair in table)
                    {
                        var record = pair.Value;
                        if (!Live(record) || !Same(record._BoltEntity_k__BackingField, source.UnityEntity)) continue;
                        Require(Live(pair.Key) && records.Add(record.Pointer), "An old-player list record has an ambiguous connection key.");
                        _known.Add((pair.Key.Pointer, record.Pointer));
                        _slots.Add(new KnownCache(table, pair.Key, record, source.UnityEntity));
                    }
                }
            }

            private bool ExcludeDoomed(Component owner)
            {
                if (!_source.OwnsDoomedHierarchy(owner)) return false;
                _doomed.Add(new ComponentOwner(owner));
                return true;
            }

            private bool RefersToOldPlayer(TransformsBinder binder)
            {
                if (binder is null) return false;
                Require(Live(binder), "A loaded LOD transform binder is unavailable.");
                if (Same(binder._entity, _source.UnityEntity) || Same(binder._state, _source.State)) return true;
                foreach (var transform in _oldTransforms)
                    if (Live(transform) && Same(binder._networkTransform, transform)) return true;
                return false;
            }

            private bool RefersToOldPlayer(EntityBehaviour owner) => Alive(owner)
                && (Same(owner._entity, _source.UnityEntity) || (owner._entity is null
                    && Same(owner.GetComponentInParent<BoltEntity>(true), _source.UnityEntity)));

            private bool RefersToOldPlayer(PlayerStats owner) => Alive(owner)
                && (Same(owner._playerEntityCache, _source.UnityEntity) || Same(owner._playerStateCache, _source.State));

            internal bool DiscoverBeforeWrite(out string detail)
            {
                detail = "Prewrite player cache discovery was already consumed.";
                if (_rediscovered) return false;
                _rediscovered = true;
                var receivers = Resources.FindObjectsOfTypeAll<ReceiveItemsInteractionTrigger>();
                var stimuli = Resources.FindObjectsOfTypeAll<PlayerActionStimuli>();
                var names = Resources.FindObjectsOfTypeAll<PlayerName>();
                var managers = Resources.FindObjectsOfTypeAll<NetworkTransformLodManager>();
                if (!Live(receivers) || !Live(stimuli) || !Live(names) || !Live(managers))
                { detail = "Prewrite player cache discovery is unavailable."; return false; }
                if (!_source.RetainsRoot)
                {
                    _allBehaviours = Resources.FindObjectsOfTypeAll<EntityBehaviour>();
                    _allStats = Resources.FindObjectsOfTypeAll<PlayerStats>();
                    if (!Live(_allBehaviours) || !Live(_allStats))
                    { detail = "Prewrite player entity/state cache discovery is unavailable."; return false; }
                }
                if (!PopulationMatches(receivers, stimuli, names, managers, out detail)) return false;
                _allReceivers = receivers; _allStimuli = stimuli; _allNames = names; _lodManagers = managers;
                return true;
            }

            internal void PrepareTarget(PlayerBinding target)
            { foreach (var cache in _lodCaches) cache.PrepareTarget(target); }

            internal bool Matches(PlayerBinding target, int completed, out string detail)
            {
                detail = "A captured player cache or retained owner changed.";
                foreach (var owner in _doomed)
                    if (target == null ? !owner.MatchesDoomedCapture(_source)
                        : !_source.ReplicaHierarchyDestroyed() || !owner.IsDestroyed())
                    { detail = "A replica-owned cache escaped its old hierarchy or its destruction is not yet confirmed."; return false; }
                for (int i = 0; i < _slots.Count; i++)
                    if (!_slots[i].Matches(target, i < completed))
                    { detail = _slots[i].Name + " changed; unrelated values were preserved."; return false; }
                return PopulationMatches(_allReceivers, _allStimuli, _allNames, _lodManagers, out detail);
            }

            // The single prewrite discovery replaces this cached population for subsequent write
            // boundaries. No per-slot scene discovery; writers remain the caller's responsibility.
            private bool PopulationMatches(Il2CppArrayBase<ReceiveItemsInteractionTrigger> receivers,
                Il2CppArrayBase<PlayerActionStimuli> stimuli, Il2CppArrayBase<PlayerName> names,
                Il2CppArrayBase<NetworkTransformLodManager> managers, out string detail)
            {
                if (!_source.RetainsRoot)
                {
                    foreach (var owner in _allBehaviours)
                        if (RefersToOldPlayer(owner) && !_behaviours.Contains(owner.Pointer))
                        { detail = "An uncaptured behaviour acquired the old player entity."; return false; }
                    foreach (var owner in _allStats)
                        if (RefersToOldPlayer(owner) && !_stats.Contains(owner.Pointer))
                        { detail = "An uncaptured stats owner acquired the old player entity/state."; return false; }
                }
                foreach (var owner in receivers)
                    if (Alive(owner) && Same(owner._ownerEntiy, _source.UnityEntity) && !_receivers.Contains(owner.Pointer))
                    { detail = "An uncaptured receive-items owner acquired the old entity."; return false; }
                foreach (var owner in stimuli)
                    if (Alive(owner) && owner._playerNetworkId.PackedValue == _source.Id && !_stimuli.Contains(owner.Pointer))
                    { detail = "An uncaptured action-stimuli owner acquired the old ID."; return false; }
                foreach (var owner in names)
                    if (Alive(owner) && Same(owner._playerState, _source.State) && !_names.Contains(owner.Pointer))
                    { detail = "An uncaptured player-name owner acquired the old state."; return false; }
                var table = PlayerListBase._knownPlayers;
                if (table is not null)
                {
                    if (!Live(table)) { detail = "The known-player table is unavailable."; return false; }
                    foreach (var pair in table)
                        if (Live(pair.Value) && Same(pair.Value._BoltEntity_k__BackingField, _source.UnityEntity)
                            && (!Live(pair.Key) || !_known.Contains((pair.Key.Pointer, pair.Value.Pointer))))
                        { detail = "An uncaptured player-list record acquired the old entity."; return false; }
                }
                foreach (var manager in managers)
                {
                    if (!Alive(manager)) continue;
                    if (RefersToOldPlayer(manager._transformsBinder) && !_lod.Contains(manager.Pointer))
                    { detail = "An uncaptured LOD manager acquired an old-player entity, state or network transform."; return false; }
                    foreach (var cache in _lodCaches)
                        if (cache.HasCompetingManager(manager))
                        { detail = "Another live LOD manager claims the replacement player transform property."; return false; }
                }
                detail = "All captured player cache expectations match.";
                return true;
            }

            internal void Write(int index, PlayerBinding target, ref bool attempted)
                => _slots[index].Write(target, ref attempted);
        }

        private abstract class Cache
        {
            internal abstract string Name { get; }
            internal abstract bool Matches(PlayerBinding target, bool applied);
            internal abstract void Write(PlayerBinding target, ref bool attempted);
        }

        private sealed class ComponentOwner
        {
            private readonly Component _component;
            private readonly GameObject _object;
            private readonly Transform _transform;
            private readonly Transform _parent;
            private readonly int _instanceId;
            internal ComponentOwner(Component component)
            {
                _component = component; _object = component.gameObject; _transform = component.transform;
                _parent = _transform.parent; _instanceId = component.GetInstanceID();
            }
            internal bool Matches() => Alive(_component) && Alive(_object) && Alive(_transform)
                && _component.GetInstanceID() == _instanceId && Same(_component.gameObject, _object)
                && Same(_component.transform, _transform) && Same(_transform.parent, _parent);
            internal bool MatchesDoomedCapture(PlayerBinding source) => Matches() && source.OwnsDoomedHierarchy(_component);
            internal bool IsDestroyed() => Live(_component) && !_component && Live(_object) && !_object
                && Live(_transform) && !_transform;
        }

        private sealed class BehaviourCache : Cache
        {
            private readonly EntityBehaviour _owner;
            private readonly ComponentOwner _life;
            private readonly BoltEntity _old;
            internal BehaviourCache(EntityBehaviour owner, BoltEntity old)
            { _owner = owner; _old = old; _life = new ComponentOwner(owner); }
            internal override string Name => "EntityBehaviour._entity";
            internal override bool Matches(PlayerBinding target, bool applied) => _life.Matches()
                && Same(_owner._entity, applied ? target.UnityEntity : _old);
            internal override void Write(PlayerBinding target, ref bool attempted)
            {
                Require(Matches(target, false) && !_reentered, Name + " changed before write.");
                attempted = true;
                _owner._entity = target.UnityEntity;
                Require(Matches(target, true) && !_reentered, Name + " readback failed.");
            }
        }

        private sealed class StatsCache : Cache
        {
            private readonly PlayerStats _owner;
            private readonly ComponentOwner _life;
            private readonly BoltEntity _entity;
            private readonly IPlayerState _state;
            internal StatsCache(PlayerStats owner)
            {
                _owner = owner; _life = new ComponentOwner(owner);
                _entity = owner._playerEntityCache; _state = owner._playerStateCache;
            }
            internal override string Name => "PlayerStats player entity/state caches";
            internal override bool Matches(PlayerBinding target, bool applied) => _life.Matches()
                && Same(_owner._playerEntityCache, applied ? target.UnityEntity : _entity)
                && Same(_owner._playerStateCache, applied ? target.State : _state);
            internal override void Write(PlayerBinding target, ref bool attempted)
            {
                Require(Matches(target, false) && !_reentered, Name + " changed before write.");
                attempted = true;
                _owner._playerEntityCache = target.UnityEntity;
                Require(Same(_owner._playerStateCache, _state) && !_reentered, Name + " state changed during write.");
                _owner._playerStateCache = target.State;
                Require(Matches(target, true) && !_reentered, Name + " readback failed.");
            }
        }

        private sealed class ReceiveCache : Cache
        {
            private readonly ReceiveItemsInteractionTrigger _owner;
            private readonly ComponentOwner _life;
            private readonly BoltEntity _old;
            internal ReceiveCache(ReceiveItemsInteractionTrigger owner, BoltEntity old)
            { _owner = owner; _old = old; _life = new ComponentOwner(owner); }
            internal override string Name => "ReceiveItemsInteractionTrigger._ownerEntiy (interaction must be inactive)";
            internal override bool Matches(PlayerBinding target, bool applied) => _life.Matches()
                && !_owner._isInteractionActive && Same(_owner._ownerEntiy, applied ? target.UnityEntity : _old);
            internal override void Write(PlayerBinding target, ref bool attempted)
            {
                Require(Matches(target, false) && !_reentered, Name + " changed before write.");
                attempted = true;
                _owner._ownerEntiy = target.UnityEntity;
                Require(Matches(target, true) && !_reentered, Name + " readback failed.");
            }
        }

        private sealed class StimuliCache : Cache
        {
            private readonly PlayerActionStimuli _owner;
            private readonly ComponentOwner _life;
            private readonly ulong _oldId;
            private readonly bool _found;
            internal StimuliCache(PlayerActionStimuli owner, ulong oldId)
            { _owner = owner; _oldId = oldId; _found = owner._foundNetworkId; _life = new ComponentOwner(owner); }
            internal override string Name => "PlayerActionStimuli network-ID cache";
            internal override bool Matches(PlayerBinding target, bool applied) => _life.Matches()
                && _owner._playerNetworkId.PackedValue == (applied ? target.Id : _oldId)
                && _owner._foundNetworkId == _found;
            internal override void Write(PlayerBinding target, ref bool attempted)
            {
                Require(Matches(target, false) && !_reentered, Name + " changed before write.");
                attempted = true;
                _owner._playerNetworkId = new NetworkId(target.Id);
                Require(Matches(target, true) && !_reentered, Name + " readback failed.");
            }
        }

        private sealed class NameCache : Cache
        {
            private readonly PlayerName _owner;
            private readonly ComponentOwner _life;
            private readonly IPlayerState _old;
            internal NameCache(PlayerName owner, IPlayerState old)
            { _owner = owner; _old = old; _life = new ComponentOwner(owner); }
            internal override string Name => "PlayerName._playerState";
            internal override bool Matches(PlayerBinding target, bool applied) => _life.Matches()
                && Same(_owner._playerState, applied ? target.State : _old);
            internal override void Write(PlayerBinding target, ref bool attempted)
            {
                Require(Matches(target, false) && !_reentered, Name + " changed before write.");
                attempted = true;
                _owner._playerState = target.State;
                Require(Matches(target, true) && !_reentered, Name + " readback failed.");
            }
        }

        private sealed class LodCache : Cache
        {
            private static readonly string[] Properties =
                { "Transform", "RaftTransform", "HeadVR", "RightHandVR", "LeftHandVR", "ConnectedTransform" };
            private readonly NetworkTransformLodManager _manager;
            private readonly ComponentOwner _life;
            private readonly PlayerBinding _source;
            private readonly TransformsBinder _oldBinder;
            private readonly NetworkTransform _oldNetwork;
            private readonly NetworkStorage _oldStorage;
            private readonly string _property;
            private readonly int _stateType;
            internal readonly int PropertyIndex;
            private readonly Transform _bound;
            private readonly Transform _original;
            private readonly Transform _proxy;
            private readonly Rigidbody _body;
            private readonly bool _initialized;
            private readonly bool _currentlyBound;
            private readonly bool _hasBody;
            private readonly bool _registered;
            private PlayerBinding _target;
            private NetworkState _newState;
            private NetworkStorage _newStorage;
            private NetworkTransform _newNetwork;
            private TransformsBinder _newBinder;
            private Transform _newSimulate;
            private Transform _newRender;
            private bool _newDefault;
            private Vector3 _previousPosition;
            private Vector3 _currentPosition;
            private Quaternion _previousRotation;
            private Quaternion _currentRotation;
            private Vector3 _position;
            private Quaternion _rotation;

            internal LodCache(NetworkTransformLodManager manager, PlayerBinding source)
            {
                _manager = manager; _source = source; _life = new ComponentOwner(manager);
                _oldBinder = manager._transformsBinder;
                Require(Live(_oldBinder) && Same(_oldBinder._entity, source.UnityEntity)
                    && Same(_oldBinder._state, source.State), "The matching LOD binder has mixed entity/state identities.");
                _oldNetwork = _oldBinder._networkTransform;
                PropertyIndex = _oldBinder._propertyIndex;
                var state = source.Attachment.Serializer.TryCast<NetworkState>();
                Require(Live(state) && Same(state, source.State) && Live(state.Meta) && Live(_oldNetwork),
                    "The old LOD state, property metadata or network transform is unavailable.");
                _oldStorage = state.Storage;
                _stateType = state.Meta.TypeId.Value;
                foreach (string property in Properties)
                {
                    var value = state.GetDynamicInternal(property, out int index);
                    if (index == PropertyIndex && Same(value, _oldNetwork))
                    { Require(_property == null, "The old LOD transform property is ambiguous."); _property = property; }
                }
                Require(_property != null && Live(_oldStorage), "The old LOD binder is not an exact player transform property.");
                _bound = _oldBinder._transform;
                _original = manager._originalBoundTransform; _proxy = manager._proxyBoundTransform;
                _initialized = manager._isInitialized; _currentlyBound = manager._currentlyBoundTransform;
                _body = manager._cachedRigidbody; _hasBody = manager._hasRigidbody; _registered = manager._registeredLastUpdate;
                Require(Alive(_bound) && _oldBinder._isBoundOrErrored
                    && Same(_oldNetwork.Simulate, _bound) && Same(_oldNetwork.Render, _bound),
                    "The old LOD binder has not completed its native transform binding.");
                Require(!_initialized || (Alive(_original) && Same(_bound, _currentlyBound ? _original : _proxy)),
                    "The old LOD original/proxy relation is inconsistent.");
                Require(!source.OwnsDoomedHierarchy(_bound) && !source.OwnsDoomedHierarchy(_original)
                    && !source.OwnsDoomedHierarchy(_proxy), "A surviving LOD manager depends on a doomed replica transform.");
            }

            internal override string Name => "NetworkTransformLodManager binder for " + _property;

            internal void PrepareTarget(PlayerBinding target)
            {
                Require(_target == null, Name + " target preparation was already consumed.");
                _target = target;
                _newState = target.Attachment.Serializer.TryCast<NetworkState>();
                Require(Live(_newState) && Same(_newState, target.State) && !Same(_newState, _source.State)
                    && Live(_newState.Meta) && _newState.Meta.TypeId.Value == _stateType, Name + " replacement state/schema differs.");
                _newNetwork = _newState.GetDynamicInternal(_property, out int index)?.TryCast<NetworkTransform>();
                _newStorage = _newState.Storage;
                Require(index == PropertyIndex && Live(_newNetwork) && !Same(_newNetwork, _oldNetwork)
                    && Live(_newStorage) && !Same(_newStorage, _oldStorage), Name + " replacement property aliases retired storage or differs.");
                _newDefault = _newState.IsNetworkValueDefault(PropertyIndex);
                // Native Bind calls Tick for replicas. Tick deliberately waits while this bit is set.
                Require(target.UnityEntity.isOwner || !_newDefault,
                    Name + " replacement replica still awaits its authoritative transform value.");
                _newSimulate = _newNetwork.Simulate; _newRender = _newNetwork.Render;
                var positions = _newNetwork.RenderDoubleBufferPosition;
                var rotations = _newNetwork.RenderDoubleBufferRotation;
                Require(Live(positions) && Live(rotations) && Alive(_bound), Name + " replacement render samples are unavailable.");
                _previousPosition = positions.Previous; _currentPosition = positions.Current;
                _previousRotation = rotations.Previous; _currentRotation = rotations.Current;
                _position = _bound.position; _rotation = _bound.rotation;
                Require(float.IsFinite(_position.x) && float.IsFinite(_position.y) && float.IsFinite(_position.z)
                    && float.IsFinite(_rotation.x) && float.IsFinite(_rotation.y)
                    && float.IsFinite(_rotation.z) && float.IsFinite(_rotation.w), Name + " retained pose is not finite.");
            }

            private bool ManagerMatches() => _life.Matches() && _manager._isInitialized == _initialized
                && _manager._currentlyBoundTransform == _currentlyBound && Same(_manager._originalBoundTransform, _original)
                && Same(_manager._proxyBoundTransform, _proxy) && Same(_manager._cachedRigidbody, _body)
                && _manager._hasRigidbody == _hasBody && _manager._registeredLastUpdate == _registered
                && Alive(_bound) && (_original is null || Alive(_original)) && (_proxy is null || Alive(_proxy))
                && (!_hasBody || Alive(_body));

            private bool OldBinderMatches() => Live(_oldBinder) && Same(_oldBinder._entity, _source.UnityEntity)
                && Same(_oldBinder._state, _source.State) && Same(_oldBinder._networkTransform, _oldNetwork)
                && _oldBinder._propertyIndex == PropertyIndex && Same(_oldBinder._transform, _bound)
                && _oldBinder._isBoundOrErrored && Live(_oldNetwork)
                && Same(_oldNetwork.Simulate, _bound) && Same(_oldNetwork.Render, _bound);

            private bool TargetMatches(bool applied)
            {
                if (_target == null || !Live(_newState) || !Live(_newNetwork) || !Live(_newStorage)
                    || !Same(_newState, _target.State) || !Same(_newState.Entity, _target.Attachment)
                    || !Same(_newState.Storage, _newStorage) || _newState.Meta?.TypeId.Value != _stateType
                    || !Same(_newState.GetDynamicInternal(_property, out int index), _newNetwork) || index != PropertyIndex
                    || _newState.IsNetworkValueDefault(PropertyIndex) != _newDefault
                    || !_bound.position.Equals(_position) || !_bound.rotation.Equals(_rotation)) return false;
                var positions = _newNetwork.RenderDoubleBufferPosition;
                var rotations = _newNetwork.RenderDoubleBufferRotation;
                return Live(positions) && Live(rotations)
                    && Same(_newNetwork.Simulate, applied ? _bound : _newSimulate)
                    && Same(_newNetwork.Render, applied ? _bound : _newRender)
                    && positions.Previous.Equals(applied ? _position : _previousPosition)
                    && positions.Current.Equals(applied ? _position : _currentPosition)
                    && rotations.Previous.Equals(applied ? _rotation : _previousRotation)
                    && rotations.Current.Equals(applied ? _rotation : _currentRotation);
            }

            private bool NewBinderMatches(bool bound) => Live(_newBinder) && !Same(_newBinder, _oldBinder)
                && Same(_newBinder._entity, _target.UnityEntity) && Same(_newBinder._state, _target.State)
                && Same(_newBinder._networkTransform, _newNetwork) && _newBinder._propertyIndex == PropertyIndex
                && Same(_newBinder._transform, bound ? _bound : null) && _newBinder._isBoundOrErrored;

            internal bool HasCompetingManager(NetworkTransformLodManager manager) => _target != null
                && !Same(manager, _manager) && Live(manager._transformsBinder)
                && Same(manager._transformsBinder._networkTransform, _newNetwork);

            internal override bool Matches(PlayerBinding target, bool applied) => ManagerMatches() && OldBinderMatches()
                && Same(_manager._transformsBinder, applied ? _newBinder : _oldBinder)
                && (target == null || (ReferenceEquals(target, _target) && TargetMatches(applied)))
                && (!applied || NewBinderMatches(true));

            internal override void Write(PlayerBinding target, ref bool attempted)
            {
                Require(Matches(target, false) && !_reentered && target.Matches(), Name + " changed before creation.");
                attempted = true;
                // Matched GameAssembly 9BEED267: Create 0x182E63630 resolves the NEW state's
                // dynamic property/index; no subscriptions or retirement callback are installed.
                _newBinder = TransformsBinder.Create(target.UnityEntity, _property);
                Require(Matches(target, false) && NewBinderMatches(false) && !_reentered && target.Matches(),
                    Name + " native creation could not be confirmed.");
                // Bind 0x182E639C0 -> SetTransforms 0x18095C570 seeds both render samples from
                // this exact retained original/proxy transform. Keep the new authoritative storage;
                // never copy retired state, old interpolation frames, or replay Initialize/Unbind.
                _newBinder.Bind(_bound);
                Require(ManagerMatches() && OldBinderMatches() && Same(_manager._transformsBinder, _oldBinder)
                    && NewBinderMatches(true) && TargetMatches(true) && !_reentered && target.Matches(),
                    Name + " native bind/readback could not be confirmed.");
                // SetNetworkTransformBinder 0x180604930 only assigns the binder with a GC barrier.
                _manager.SetNetworkTransformBinder(_newBinder);
                Require(Matches(target, true) && !_reentered && target.Matches(), Name + " manager installation readback failed.");
            }
        }

        private sealed class KnownCache : Cache
        {
            private readonly KnownPlayers _table;
            private readonly BoltConnection _key;
            private readonly PlayerListBase.KnownPlayerData _record;
            private readonly ConnectionBinding _connection;
            private readonly BoltEntity _old;
            private readonly ulong _steamId;
            private readonly ulong _cSteamId;
            internal KnownCache(KnownPlayers table, BoltConnection key, PlayerListBase.KnownPlayerData record, BoltEntity old)
            {
                _table = table; _key = key; _record = record; _old = old;
                _connection = new ConnectionBinding(record._Connection_k__BackingField);
                _steamId = record._SteamId_k__BackingField;
                _cSteamId = record._CSteamId_k__BackingField.m_SteamID;
            }
            internal override string Name => "PlayerListBase.KnownPlayerData.BoltEntity/connection/Steam identity";
            internal override bool Matches(PlayerBinding target, bool applied) => Live(_table) && Live(_key)
                && Live(_record) && Same(PlayerListBase._knownPlayers, _table)
                && _table.TryGetValue(_key, out var record) && Same(record, _record)
                && Same(_connection.Connection, _key) && _connection.Matches(_record._Connection_k__BackingField)
                && _record._SteamId_k__BackingField == _steamId && _record._CSteamId_k__BackingField.m_SteamID == _cSteamId
                && Same(_record._BoltEntity_k__BackingField, applied ? target.UnityEntity : _old);
            internal override void Write(PlayerBinding target, ref bool attempted)
            {
                Require(Matches(target, false) && !_reentered, Name + " changed before write.");
                attempted = true;
                // The generated field setter performs the native GC write barrier without invoking
                // player-list callbacks or changing connection, Steam identity, name or table keys.
                _record._BoltEntity_k__BackingField = target.UnityEntity;
                Require(Matches(target, true) && !_reentered, Name + " readback failed.");
            }
        }
    }
}
