// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using Il2CppInterop.Runtime.InteropTypes;
using TheForest.Utils;
using UdpKit;
using UnityEngine;
using UnityObject = UnityEngine.Object;
using VoiceTable = Il2CppSystem.Collections.Generic.Dictionary<ulong, FmodVoiceBuffer>;

namespace Modstoke.Sons
{
    /// <summary>
    /// One-shot, game-thread-only repair of captured LOCAL player caches, not a migration adapter.
    /// Capture before Detach and keep the rig and cache owners quiescent until TryRebind returns.
    /// No subscriptions, coroutine captures, lifecycle callbacks, simulation or ownership are repaired.
    /// Applied means only that the captured cache writes and retained identities were read back.
    /// </summary>
    public sealed class PlayerRigBindings
    {
        [ThreadStatic] private static bool _busy;
        [ThreadStatic] private static bool _reentered;

        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly BoltEntity _original;
        private readonly Entity _attachment;
        private readonly IPlayerState _state;
        private readonly UniqueId _serializer;
        private readonly BoltConnection _server;
        private readonly UdpConnection _transport;
        private readonly UdpSocket _socket;
        private readonly uint _connectionId;
        private readonly List<ObjectSlot> _rig = new();
        private readonly List<BehaviourCache> _behaviours = new();
        private readonly List<StatsCache> _stats = new();
        private readonly List<VoiceCaches> _voices = new();
        private bool _used;

        public GameObject Root { get; }
        public BoltEntity OriginalEntity => _original;
        public ulong OriginalNetworkId { get; }
        public int CapturedEntityCacheCount => _behaviours.Count;
        public int CapturedStatsCount => _stats.Count;
        public int CapturedVoiceBufferCount { get; private set; }
        /// <summary>These are not cleared by Applied; the enclosing adapter must resolve them.</summary>
        public IReadOnlyList<string> MigrationBlockers { get; private set; }

        private PlayerRigBindings(BoltEntity original)
        {
            _original = original;
            _attachment = original._entity;
            _state = original.GetState<IPlayerState>();
            _serializer = original.serializerGuid;
            OriginalNetworkId = original.networkId.PackedValue;
            Root = LocalPlayer.GameObject;
            _server = BoltNetwork.server;
            _transport = _server?._udp;
            _socket = _transport?.Socket;
            _connectionId = _server?.ConnectionId ?? 0;
        }

        /// <summary>
        /// Read-only native capture. Scans loaded EntityBehaviour and PlayerStats instances once,
        /// selecting exact old references, plus null behaviour caches whose nearest Bolt parent is
        /// the original entity. Captures voice-table membership and exact old-state buffers.
        /// Does not call the lazy EntityBehaviour.entity getter or initialize missing caches.
        /// Rediscovery before the first write rejects new matching cache owners. The caller must
        /// keep owners and buffers quiescent throughout the write phase.
        /// </summary>
        public static bool TryCapture(BoltEntity original, out PlayerRigBindings bindings,
            out string detail)
        {
            bindings = null;
            detail = "Retained-rig capture requires the initialized player-role game thread.";
            if (!PlayerRoles.IsAvailable) return false;
            if (_busy) { _reentered = true; detail = "Rig capture/rebind cannot re-enter."; return false; }
            _busy = true;
            _reentered = false;
            try
            {
                if (!Alive(original) || !original.isAttached || !original.isOwner
                    || original.prefabId != BoltPrefabs.PlayerNetwork
                    || !Same(LocalPlayer.Entity, original) || !Same(original.gameObject, LocalPlayer.GameObject))
                { detail = "The exact attached, locally owned LocalPlayer PlayerNetwork root is required."; return false; }
                var capture = new PlayerRigBindings(original);
                if (!capture.MatchesOriginal())
                { detail = "Original player attachment/state or server connection is inconsistent."; return false; }

                capture.CaptureRig();
                bool delayedMecanim = false;
                foreach (var component in Resources.FindObjectsOfTypeAll<EntityBehaviour>())
                {
                    if (!Alive(component)) continue;
                    var cached = component._entity;
                    if (!Same(cached, original)
                        && !(cached == null && Same(component.GetComponentInParent<BoltEntity>(true), original)))
                        continue;
                    capture._behaviours.Add(new BehaviourCache(component, cached));
                    if (component.TryCast<CoopMecanimReplicator>() != null) delayedMecanim = true;
                }
                bool foundLocalStats = false;
                foreach (var stats in Resources.FindObjectsOfTypeAll<PlayerStats>())
                {
                    if (!Alive(stats)) continue;
                    if (!Same(stats, LocalPlayer.Stats) && !Same(stats._playerEntityCache, original)
                        && !Same(stats._playerStateCache, capture._state)) continue;
                    var entity = stats._playerEntityCache;
                    var state = stats._playerStateCache;
                    if ((entity != null && !Same(entity, original)) || (state != null && !Same(state, capture._state)))
                    { detail = "A selected PlayerStats cache already refers to a different player."; return false; }
                    capture._stats.Add(new StatsCache(stats, entity, state));
                    if (Same(stats, LocalPlayer.Stats)) foundLocalStats = true;
                }
                var voiceBufferOwners = new Dictionary<IntPtr, VoiceClaim>();
                foreach (var voice in Resources.FindObjectsOfTypeAll<CoopSteamVoicePlayer>())
                {
                    if (!Alive(voice)) continue;
                    var caches = new VoiceCaches(voice, capture._state, capture.OriginalNetworkId);
                    if (!caches.TryClaimBuffers(voiceBufferOwners))
                    { detail = "A captured voice buffer has conflicting ownership or retirement aliases."; return false; }
                    capture._voices.Add(caches);
                }
                capture.CapturedVoiceBufferCount = voiceBufferOwners.Count;
                if (capture._behaviours.Count == 0 || !foundLocalStats)
                { detail = "The original rig has no captured behaviour or stats caches."; return false; }
                var blockers = new List<string>
                {
                    "Detached subscriptions and callback registrations are not repaired; LocalAttached is never replayed.",
                    "Cache repair does not provide authoritative locomotion, input/state publication, or migration enablement."
                };
                if (delayedMecanim)
                    blockers.Add("CoopMecanimReplicator.Detached cancels delayed sends through native StopAllCoroutines. "
                        + "The migration kernel verifies those callbacks complete; the adapter must still preserve "
                        + "animation state and restart publication after rebinding. This cache helper does neither.");
                capture.MigrationBlockers = blockers.AsReadOnly();
                if (!capture.MatchesOriginal() || !capture.MatchesCaches(false, null, null, out detail) || _reentered)
                { detail = "Capture changed while reading: " + detail; return false; }
                bindings = capture;
                detail = "Captured exact retained-rig identities and cache expectations; migration blockers remain unresolved.";
                return true;
            }
            catch (Exception error)
            { detail = "Rig capture rejected (" + error.GetType().Name + ")."; return false; }
            finally { _busy = false; }
        }

        /// <summary>
        /// Consumed on the first game-thread attempt, including rejection. Call only AFTER the
        /// original native attachment has detached and disappeared from both registries. Requires
        /// the caller's exact new ID and exact server-sourced, locally controlled PlayerNetwork.
        /// Rejected means no setter was attempted; Unconfirmed may mean partial writes. Unexpected
        /// references are preserved, with no rollback, retry, destruction or ownership spoofing.
        /// </summary>
        public BoltMigrationMutationResult TryRebind(BoltEntity replacement,
            ulong expectedNewNetworkId, out string detail)
        {
            detail = "Rig rebind requires its captured player-role game thread.";
            if (Environment.CurrentManagedThreadId != _thread || !PlayerRoles.IsAvailable)
                return BoltMigrationMutationResult.Rejected;
            if (_busy)
            { _reentered = true; detail = "Rig capture/rebind cannot re-enter."; return BoltMigrationMutationResult.Rejected; }
            if (_used)
            { detail = "This cache capture has already been consumed; do not replay it."; return BoltMigrationMutationResult.Rejected; }
            _used = true;
            _busy = true;
            _reentered = false;
            bool attempted = false;
            try
            {
                if (!MatchesRetired() || !MatchesReplacement(replacement, expectedNewNetworkId, out var state))
                { detail = "Original is not detached, or replacement is not the exact controlled server PlayerNetwork."; return BoltMigrationMutationResult.Rejected; }
                if (!MatchesCachePopulation(out detail)) return BoltMigrationMutationResult.Rejected;
                // Account for EVERY captured expectation before the first setter, including voice
                // table membership and caches which are deliberately not being changed.
                if (!MatchesCaches(false, replacement, state, out detail, retired: true) || _reentered)
                    return BoltMigrationMutationResult.Rejected;
                if (!MatchesRetired() || !MatchesReplacement(replacement, expectedNewNetworkId, out var preparedState)
                    || !Same(preparedState, state) || _reentered)
                { detail = "Player lifetime/control changed during cache rediscovery."; return BoltMigrationMutationResult.Rejected; }
                foreach (var cache in _behaviours)
                {
                    if (!cache.Matches(cache.Expected)) throw new InvalidOperationException("Behaviour cache changed.");
                    attempted = true;
                    cache.Component._entity = replacement;
                    if (_reentered) throw new InvalidOperationException("Reentrant rebind.");
                }
                foreach (var cache in _stats)
                {
                    if (!cache.Matches(cache.Entity, cache.State)) throw new InvalidOperationException("Stats cache changed.");
                    attempted = true;
                    cache.Component._playerEntityCache = replacement;
                    if (!Same(cache.Component._playerStateCache, cache.State))
                        throw new InvalidOperationException("Stats state changed.");
                    cache.Component._playerStateCache = state;
                }
                foreach (var voice in _voices)
                    voice.Apply(state, ref attempted);
                if (!Same(LocalPlayer.Entity, _original) || !Same(LocalPlayer.State, _state))
                    throw new InvalidOperationException("LocalPlayer globals changed.");
                attempted = true;
                LocalPlayer.Entity = replacement;
                if (!Same(LocalPlayer.State, _state)) throw new InvalidOperationException("LocalPlayer state changed.");
                LocalPlayer.State = state;
                if (_reentered || !MatchesRetired() || !MatchesReplacement(replacement, expectedNewNetworkId, out var currentState)
                    || !Same(currentState, state) || !MatchesCaches(true, replacement, state, out detail, retired: true))
                { detail = "Cache writes were attempted but retained identities/readback could not be confirmed."; return BoltMigrationMutationResult.Unconfirmed; }
                detail = "Captured caches rebound and exact rig identities retained. Subscriptions/coroutines and full migration remain unresolved.";
                return BoltMigrationMutationResult.Applied;
            }
            catch (Exception error)
            {
                detail = "Rig rebind " + (attempted ? "unconfirmed after attempted writes" : "rejected before writes")
                    + " (" + error.GetType().Name + "). Unexpected values were not rolled back.";
                return attempted ? BoltMigrationMutationResult.Unconfirmed : BoltMigrationMutationResult.Rejected;
            }
            finally { _busy = false; }
        }

        private void CaptureRig()
        {
            _rig.Add(new ObjectSlot("LocalPlayer instance", () => LocalPlayer._instance));
            _rig.Add(new ObjectSlot("root", () => LocalPlayer.GameObject));
            _rig.Add(new ObjectSlot("transform", () => LocalPlayer.Transform));
            _rig.Add(new ObjectSlot("player base", () => LocalPlayer.PlayerBase));
            _rig.Add(new ObjectSlot("camera", () => LocalPlayer.MainCam));
            _rig.Add(new ObjectSlot("camera transform", () => LocalPlayer.MainCamTr));
            _rig.Add(new ObjectSlot("inventory camera", () => LocalPlayer.InventoryCam));
            _rig.Add(new ObjectSlot("first person controller", () => LocalPlayer.FpCharacter));
            _rig.Add(new ObjectSlot("inventory", () => LocalPlayer.Inventory));
            _rig.Add(new ObjectSlot("stats", () => LocalPlayer.Stats));
            _rig.Add(new ObjectSlot("rigidbody", () => LocalPlayer.Rigidbody));
            var character = LocalPlayer.FpCharacter;
            _rig.Add(new ObjectSlot("controller rigidbody", () => character._rigidbody));
            _rig.Add(new ObjectSlot("animator", () => LocalPlayer.Animator));
            _rig.Add(new ObjectSlot("animation control", () => LocalPlayer.AnimControl));
            _rig.Add(new ObjectSlot("camera input rotator", () => LocalPlayer.CamRotator));
            _rig.Add(new ObjectSlot("main input rotator", () => LocalPlayer.MainRotator));
            _rig.Add(new ObjectSlot("script setup", () => LocalPlayer.ScriptSetup));
            _rig.Add(new ObjectSlot("original Bolt component", () => _original));
            if (!Same(character._rigidbody, LocalPlayer.Rigidbody))
                throw new InvalidOperationException("Local body bindings disagree.");
        }

        private bool MatchesRun() => BoltNetwork.isRunning && BoltNetwork.isClient && !BoltNetwork.isServer
            && Live(_server) && Live(_transport) && Live(_socket)
            && Same(BoltNetwork.server, _server) && _server.ConnectionId == _connectionId
            && Same(_server._udp, _transport) && _transport.ConnectionId == _connectionId
            && _transport.State == UdpConnectionState.Connected && Same(_transport.UserToken, _server)
            && Same(_transport.Socket, _socket) && Same(BoltCore._udpSocket, _socket)
            && _socket.State != UdpSocketState.Shutdown;

        private bool MatchesOriginal() => MatchesRun() && OriginalNetworkId != 0
            && Alive(_original) && _original.isAttached && _original.isOwner && _original.source == null
            && Live(_attachment) && _attachment.IsAttached && _attachment.IsOwner && _attachment.Source == null
            && Same(_original._entity, _attachment) && Same(_attachment.UnityObject, _original)
            && _original.networkId.PackedValue == OriginalNetworkId && _attachment.NetworkId.PackedValue == OriginalNetworkId
            && _original.prefabId == BoltPrefabs.PlayerNetwork && _original.serializerGuid == _serializer
            && Same(BoltNetwork.FindEntity(new NetworkId(OriginalNetworkId)), _original)
            && Same(BoltCore.FindEntity(new NetworkId(OriginalNetworkId)), _attachment)
            && Live(_state) && Same(_attachment.Serializer, _state) && Same(_original.GetState<IPlayerState>(), _state)
            && Same(LocalPlayer.Entity, _original) && Same(LocalPlayer.State, _state)
            && Same(LocalPlayer.GameObject, Root) && Same(_original.gameObject, Root);

        private bool MatchesRetired() => MatchesRun() && Alive(_original) && Same(_original.gameObject, Root)
            && _original._entity == null && !_original.isAttached && Live(_attachment) && !_attachment.IsAttached
            && _attachment.NetworkId.PackedValue == OriginalNetworkId
            && BoltCore.FindEntity(new NetworkId(OriginalNetworkId)) == null
            && BoltNetwork.FindEntity(new NetworkId(OriginalNetworkId)) == null;

        private bool MatchesReplacement(BoltEntity replacement, ulong id, out IPlayerState state)
        {
            state = null;
            if (!MatchesRun() || id == 0 || id == OriginalNetworkId || !Alive(replacement)
                || Same(replacement, _original) || Same(replacement.gameObject, Root)
                || !replacement.isAttached || replacement.isOwner || !replacement.hasControl
                || replacement.prefabId != BoltPrefabs.PlayerNetwork || replacement.serializerGuid != _serializer
                || replacement.networkId.PackedValue != id || !Same(replacement.source, _server)
                || !Same(BoltNetwork.FindEntity(new NetworkId(id)), replacement)) return false;
            var attachment = replacement._entity;
            if (!Live(attachment) || !attachment.IsAttached || attachment.IsOwner || !attachment.HasControl
                || attachment.NetworkId.PackedValue != id || attachment.PrefabId != BoltPrefabs.PlayerNetwork
                || !Same(attachment.UnityObject, replacement) || !Same(attachment.Source, _server)
                || !Same(replacement.controller, attachment.Controller)
                || !Same(BoltCore.FindEntity(new NetworkId(id)), attachment)) return false;
            state = replacement.GetState<IPlayerState>();
            var networkState = attachment.Serializer?.TryCast<NetworkState>();
            return Live(state) && !Same(state, _state) && Same(networkState, state)
                && Same(networkState.Entity, attachment);
        }

        private bool MatchesCachePopulation(out string detail)
        {
            var behaviours = Resources.FindObjectsOfTypeAll<EntityBehaviour>();
            var stats = Resources.FindObjectsOfTypeAll<PlayerStats>();
            var voices = Resources.FindObjectsOfTypeAll<CoopSteamVoicePlayer>();
            if (!Live(behaviours) || !Live(stats) || !Live(voices))
            { detail = "Retained player cache rediscovery is unavailable."; return false; }
            var captured = new HashSet<IntPtr>();
            foreach (var cache in _behaviours) captured.Add(cache.Component.Pointer);
            foreach (var owner in behaviours)
                if (Alive(owner) && !captured.Contains(owner.Pointer)
                    && (Same(owner._entity, _original) || (owner._entity is null
                        && Same(owner.GetComponentInParent<BoltEntity>(true), _original))))
                { detail = "An uncaptured behaviour acquired the retained player's old entity."; return false; }
            captured.Clear();
            foreach (var cache in _stats) captured.Add(cache.Component.Pointer);
            foreach (var owner in stats)
                if (Alive(owner) && !captured.Contains(owner.Pointer)
                    && (Same(owner, LocalPlayer.Stats) || Same(owner._playerEntityCache, _original)
                        || Same(owner._playerStateCache, _state)))
                { detail = "An uncaptured stats owner acquired the retained player's old entity/state."; return false; }
            captured.Clear();
            foreach (var cache in _voices) captured.Add(cache.OwnerPointer);
            foreach (var owner in voices)
                if (Alive(owner) && !captured.Contains(owner.Pointer)
                    && (HasOriginalVoiceReference(owner._fmodSoundDictionary2D)
                        || HasOriginalVoiceReference(owner._fmodSoundDictionary3D)))
                { detail = "An uncaptured voice owner acquired the retained player's old identity."; return false; }
            detail = "No uncaptured retained-player cache owner was found.";
            return true;
        }

        private bool HasOriginalVoiceReference(VoiceTable table)
        {
            if (table is null) return false;
            if (!Live(table)) throw new InvalidOperationException("A voice table is unavailable.");
            foreach (var pair in table)
                if (pair.Key == OriginalNetworkId || Same(pair.Value?._PlayerState_k__BackingField, _state)) return true;
            return false;
        }

        private bool MatchesCaches(bool applied, BoltEntity replacement, IPlayerState state, out string detail,
            bool retired = false)
        {
            foreach (var slot in _rig)
                if (!slot.Matches()) { detail = "Retained " + slot.Name + " identity/parent changed."; return false; }
            if (!Same(LocalPlayer.Entity, applied ? replacement : _original) || !Same(LocalPlayer.State, applied ? state : _state))
            { detail = "LocalPlayer entity/state changed unexpectedly."; return false; }
            foreach (var cache in _behaviours)
                if (!cache.Matches(applied ? replacement : cache.Expected))
                { detail = "A captured behaviour identity/entity cache changed."; return false; }
            foreach (var cache in _stats)
                if (!cache.Matches(applied ? replacement : cache.Entity, applied ? state : cache.State))
                { detail = "A captured stats identity/entity/state cache changed."; return false; }
            foreach (var voice in _voices)
                if (!voice.Matches(applied, retired, state))
                { detail = "A captured voice owner, table membership or state cache changed."; return false; }
            detail = "All captured cache expectations and retained rig identities match.";
            return true;
        }

        private static bool Live(Il2CppObjectBase value) => value != null && !value.WasCollected && value.Pointer != IntPtr.Zero;
        private static bool Alive(UnityObject value) => Live(value) && value;
        private static bool Same(Il2CppObjectBase left, Il2CppObjectBase right)
            => left == null ? right == null : Live(left) && Live(right) && left.Pointer == right.Pointer;

        private sealed class ObjectSlot
        {
            internal readonly string Name;
            private readonly Func<UnityObject> _read;
            private readonly UnityObject _value;
            private readonly int _instanceId;
            private readonly GameObject _owner;
            private readonly Transform _transform;
            private readonly Transform _parent;

            internal ObjectSlot(string name, Func<UnityObject> read)
            {
                Name = name;
                _read = read;
                _value = read();
                if (!Alive(_value)) throw new InvalidOperationException("Missing retained " + name + ".");
                _instanceId = _value.GetInstanceID();
                _owner = _value.TryCast<GameObject>() ?? _value.TryCast<Component>()?.gameObject;
                if (!Alive(_owner)) throw new InvalidOperationException("Missing owner of " + name + ".");
                _transform = _owner.transform;
                _parent = _transform.parent;
            }

            internal bool Matches() => Alive(_value) && _value.GetInstanceID() == _instanceId && Same(_read(), _value)
                && Alive(_owner) && Alive(_transform) && Same(_owner.transform, _transform)
                && Same(_transform.parent, _parent)
                && Same(_value.TryCast<GameObject>() ?? _value.TryCast<Component>()?.gameObject, _owner);
        }

        private sealed class BehaviourCache
        {
            internal readonly EntityBehaviour Component;
            internal readonly BoltEntity Expected;
            private readonly ObjectSlot _life;
            internal BehaviourCache(EntityBehaviour component, BoltEntity expected)
            { Component = component; Expected = expected; _life = new ObjectSlot("behaviour", () => component); }
            internal bool Matches(BoltEntity expected) => _life.Matches() && Same(Component._entity, expected);
        }

        private sealed class StatsCache
        {
            internal readonly PlayerStats Component;
            internal readonly BoltEntity Entity;
            internal readonly IPlayerState State;
            private readonly ObjectSlot _life;
            internal StatsCache(PlayerStats component, BoltEntity entity, IPlayerState state)
            { Component = component; Entity = entity; State = state; _life = new ObjectSlot("stats", () => component); }
            internal bool Matches(BoltEntity entity, IPlayerState state) => _life.Matches()
                && Same(Component._playerEntityCache, entity) && Same(Component._playerStateCache, state);
        }

        private sealed class VoiceCaches
        {
            private readonly CoopSteamVoicePlayer _owner;
            private readonly ObjectSlot _life;
            private readonly VoiceTableCache _twoD;
            private readonly VoiceTableCache _threeD;
            private readonly bool _singleton;
            internal IntPtr OwnerPointer => _owner.Pointer;
            internal VoiceCaches(CoopSteamVoicePlayer owner, IPlayerState oldState, ulong oldId)
            {
                _owner = owner;
                _life = new ObjectSlot("voice player", () => owner);
                _singleton = Same(owner, CoopSteamVoicePlayer._instance);
                _twoD = new VoiceTableCache(owner._fmodSoundDictionary2D, oldState, oldId, _singleton);
                _threeD = new VoiceTableCache(owner._fmodSoundDictionary3D, oldState, oldId, _singleton);
            }
            internal bool Matches(bool applied, bool retired, IPlayerState state) => _life.Matches()
                && _singleton == Same(_owner, CoopSteamVoicePlayer._instance)
                && _twoD.Matches(_owner._fmodSoundDictionary2D, applied, retired, state)
                && _threeD.Matches(_owner._fmodSoundDictionary3D, applied, retired, state);
            internal bool TryClaimBuffers(Dictionary<IntPtr, VoiceClaim> owners)
                => _twoD.TryClaimBuffers(owners, _owner.Pointer) && _threeD.TryClaimBuffers(owners, _owner.Pointer);
            internal void Apply(IPlayerState state, ref bool attempted)
            {
                if (!Matches(false, true, state)) throw new InvalidOperationException("Voice caches changed.");
                _twoD.Apply(state, ref attempted);
                _threeD.Apply(state, ref attempted);
            }
        }

        private sealed class VoiceTableCache
        {
            private readonly VoiceTable _table;
            private readonly List<VoiceEntry> _entries = new();
            internal VoiceTableCache(VoiceTable table, IPlayerState oldState, ulong oldId, bool singleton)
            {
                _table = table;
                if (table == null) return;
                foreach (var pair in table)
                {
                    var buffer = pair.Value;
                    var state = buffer?._PlayerState_k__BackingField;
                    bool replace = Same(state, oldState);
                    _entries.Add(new VoiceEntry(pair.Key, buffer, state, replace, singleton && pair.Key == oldId));
                }
            }
            internal bool Matches(VoiceTable current, bool applied, bool retired, IPlayerState state)
            {
                if (!Same(current, _table)) return false;
                if (_table == null) return true;
                int expectedCount = _entries.Count;
                if (retired)
                    foreach (var entry in _entries) if (entry.RemovedOnDetach) expectedCount--;
                if (_table.Count != expectedCount) return false;
                foreach (var entry in _entries)
                {
                    // Native ClearPlayerSource disposes both singleton buffers and removes this
                    // key. Never resurrect their released FMOD handles under the replacement ID.
                    if (retired && entry.RemovedOnDetach)
                    {
                        if (_table.ContainsKey(entry.Key) || (Live(entry.Buffer) && !entry.Buffer._disposed)) return false;
                        continue;
                    }
                    if (entry.RemovedOnDetach && !Live(entry.Buffer)) return false;
                    if (!_table.TryGetValue(entry.Key, out var buffer) || !Same(buffer, entry.Buffer)
                        || (buffer != null && buffer._disposed)
                        || (buffer != null && !Same(buffer._PlayerState_k__BackingField,
                            applied && entry.Replace ? state : entry.State))) return false;
                }
                return true;
            }
            internal bool TryClaimBuffers(Dictionary<IntPtr, VoiceClaim> owners, IntPtr owner)
            {
                foreach (var entry in _entries)
                {
                    if (!Live(entry.Buffer)) continue;
                    var claim = new VoiceClaim(owner, entry.RemovedOnDetach);
                    if (owners.TryGetValue(entry.Buffer.Pointer, out var existing) && existing != claim) return false;
                    owners[entry.Buffer.Pointer] = claim;
                }
                return true;
            }
            internal void Apply(IPlayerState state, ref bool attempted)
            {
                foreach (var entry in _entries)
                {
                    if (!entry.Replace || entry.RemovedOnDetach) continue;
                    if (!_table.TryGetValue(entry.Key, out var buffer) || !Same(buffer, entry.Buffer))
                        throw new InvalidOperationException("Voice table changed.");
                    // The two native dictionaries may share a buffer. A previous write in this
                    // same operation is allowed; an unexpected third state is never overwritten.
                    if (Same(buffer._PlayerState_k__BackingField, state)) continue;
                    if (!Same(buffer._PlayerState_k__BackingField, entry.State))
                        throw new InvalidOperationException("Voice buffer state changed.");
                    attempted = true;
                    buffer.PlayerState = state;
                }
            }
        }

        private readonly record struct VoiceEntry(ulong Key, FmodVoiceBuffer Buffer, IPlayerState State,
            bool Replace, bool RemovedOnDetach);
        private readonly record struct VoiceClaim(IntPtr Owner, bool RemovedOnDetach);
    }
}
