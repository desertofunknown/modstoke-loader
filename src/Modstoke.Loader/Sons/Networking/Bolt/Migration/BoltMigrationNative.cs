// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Bolt;
using Il2CppInterop.Runtime;
using Modstoke.Loader;
using UdpKit;
using UnityEngine;
using TheForest.Utils;

namespace Modstoke.Sons
{
    /// <summary>
    /// Native lifetime mechanics only; the paired mode must authorize a complete adapter contract
    /// before retirement. Native initialization and Attached callbacks precede state restoration,
    /// so the adapter must explicitly account for their side effects and reference bindings.
    /// </summary>
    internal sealed class BoltMigrationNative
    {
        [ThreadStatic] private static bool _busy;
        [ThreadStatic] private static bool _reentered;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly ControlPeer _peer;
        private readonly bool _localOwner;
        private readonly BoltEntity _entity;
        private readonly GameObject _gameObject;
        private readonly Bolt.Entity _backing;
        private readonly NetworkState _state;
        private readonly NetworkObj_Meta _meta;
        private readonly IntPtr _stateClass;
        private readonly IntPtr _metaClass;
        private readonly UniqueId _serializerId;
        private readonly bool _sceneObject;
        private readonly bool _retainsLocalPlayerRig;
        private readonly IFactory _factory;
        private readonly IPrefabPool _pool;
        private readonly ConnectionLife _connection;
        private readonly ConnectionLife _source;
        private readonly EntityChannel _incomingChannel;
        private readonly Il2CppSystem.Collections.Generic.Dictionary<NetworkId, EntityProxy> _incoming;
        private readonly EntityProxy _incomingProxy;
        private GameObject _prefab;
        private BoltEntity _prefabEntity;
        private bool _retireAttempted;
        private bool _recreateAttempted;
        private BoltMigrationMutationResult _retireResult;
        private BoltMigrationMutationResult _recreateResult;
        private BoltMigrationSnapshot _recreationSnapshot;
        private BoltEntity _replacement;
        private BoltMigrationNative _replacementNative;

        internal BoltEntity Entity => _entity;
        internal bool RetainsLocalPlayerRig => _retainsLocalPlayerRig;
        internal int PrefabId { get; }
        internal int StateTypeId { get; }
        internal Vector3 Position { get; }
        internal Quaternion Rotation { get; }
        internal ulong NetworkId { get; }

        private unsafe BoltMigrationNative(ControlPeer peer, bool localOwner,
            BoltEntity entity, Bolt.Entity backing, NetworkState state, NetworkObj_Meta meta,
            BoltConnection connection)
        {
            _peer = peer; _localOwner = localOwner; _entity = entity;
            _gameObject = entity.gameObject; _backing = backing; _state = state; _meta = meta;
            _stateClass = IL2CPP.il2cpp_object_get_class(state.Pointer);
            _metaClass = IL2CPP.il2cpp_object_get_class(meta.Pointer);
            _serializerId = entity.serializerGuid;
            _sceneObject = backing.IsSceneObject || !entity.sceneGuid.IsNone;
            _factory = Factory.GetFactory(meta.TypeId);
            _pool = BoltCore.PrefabPool;
            _connection = new ConnectionLife(connection);
            _source = new ConnectionLife(backing.Source);
            PrefabId = backing.PrefabId.Value;
            StateTypeId = meta.TypeId.Value;
            _retainsLocalPlayerRig = localOwner && entity.prefabId == BoltPrefabs.PlayerNetwork
                && LocalPlayer.GameObject?.Pointer == _gameObject.Pointer;
            NetworkId = backing.NetworkId.PackedValue;
            var transform = entity.transform;
            Position = transform.position;
            Rotation = transform.rotation;
            if (!localOwner)
            {
                _incomingChannel = connection._entityChannel;
                _incoming = _incomingChannel?._incommingDict;
                EntityProxy proxy = null;
                _incoming?.TryGetValue(new NetworkId(NetworkId), out proxy);
                _incomingProxy = proxy;
            }
        }

        internal static bool TryCapture(ControlPeer peer, ulong entityId, bool localOwner,
            out BoltMigrationNative native, out string detail)
        {
            native = null;
            detail = "Migration capture is unavailable or reentrant.";
            if (!Enter()) return false;
            try { return TryCaptureCore(peer, entityId, localOwner, out native, out detail); }
            catch (Exception error) { detail = "Migration capture failed (" + error.GetType().Name + ")."; return false; }
            finally { _busy = false; GC.KeepAlive(native); }
        }

        private static bool TryCaptureCore(ControlPeer peer, ulong entityId, bool localOwner,
            out BoltMigrationNative native, out string detail)
        {
            native = null;
            detail = "The exact enrolled peer and supported game-thread context are required.";
            if (entityId == 0 || !Enrolled(peer) || !Supported() || !MatchesRole(peer)) return false;
            var connection = ControlTransport.Resolve(peer);
            if (connection == null || connection.WasCollected) return false;
            detail = "The attached entity, native owner/source, serializer and registry must agree.";
            var entity = BoltNetwork.FindEntity(new NetworkId(entityId));
            if (entity is null || entity.WasCollected || !entity || !entity.isAttached) return false;
            var backing = entity._entity;
            if (backing == null || backing.WasCollected || !backing.IsAttached
                || backing.IsOwner != localOwner || entity.isOwner != localOwner) return false;
            var state = backing.Serializer?.TryCast<NetworkState>();
            var meta = state?.Meta;
            if (state == null || state.WasCollected || meta == null || meta.WasCollected) return false;
            var candidate = new BoltMigrationNative(peer, localOwner, entity, backing, state, meta, connection);
            if (candidate.NetworkId != entityId || !candidate.MatchesCurrentCore() || _reentered) return false;
            native = candidate;
            detail = "Captured the exact native entity and connection lifetimes; adapter coverage is separate.";
            return true;
        }

        internal bool IsCurrent()
        {
            if (Environment.CurrentManagedThreadId != _thread || !Enter()) return false;
            try { return MatchesCurrentCore() && !_reentered; }
            catch (Exception) { return false; }
            finally { _busy = false; GC.KeepAlive(this); }
        }

        internal bool IsRetired()
        {
            if (Environment.CurrentManagedThreadId != _thread || !Enter()) return false;
            try { return MatchesRetiredCore() && !_reentered; }
            catch (Exception) { return false; }
            finally { _busy = false; GC.KeepAlive(this); }
        }

        internal bool IsPlayerHierarchyRetired()
        {
            if (Environment.CurrentManagedThreadId != _thread || !Enter()) return false;
            try
            {
                // Bolt detachment and Unity's deferred hierarchy destruction are separate
                // boundaries. Only the original local rig is intentionally retained.
                return MatchesRetiredCore() && (PrefabId != BoltPrefabs.PlayerNetwork.Value
                    || _retainsLocalPlayerRig || (_gameObject is not null && !_gameObject.WasCollected && !_gameObject))
                    && !_reentered;
            }
            catch (Exception) { return false; }
            finally { _busy = false; GC.KeepAlive(this); }
        }

        internal bool TryValidatePrefab(out string detail)
        {
            detail = "Prefab validation requires the original game thread and a non-reentrant context.";
            if (Environment.CurrentManagedThreadId != _thread || !Enter()) return false;
            try { return ValidatePrefabCore(out detail) && !_reentered; }
            catch (Exception error) { detail = "Prefab validation failed (" + error.GetType().Name + ")."; return false; }
            finally { _busy = false; GC.KeepAlive(this); }
        }

        internal BoltMigrationMutationResult Retire(out string detail)
        {
            detail = "Retirement requires the original game thread and a non-reentrant context.";
            if (Environment.CurrentManagedThreadId != _thread || !Enter())
                return _retireAttempted ? BoltMigrationMutationResult.Unconfirmed
                    : BoltMigrationMutationResult.Rejected;
            try
            {
                if (_retireAttempted)
                {
                    detail = "The native retirement attempt is not replayed.";
                    return _retireResult == BoltMigrationMutationResult.Applied
                        && MatchesRetiredCore() && !_reentered ? _retireResult
                        : BoltMigrationMutationResult.Unconfirmed;
                }
                detail = "Only the exact current local owner can retire a recreatable entity.";
                if (!_localOwner || !MatchesCurrentCore()) return BoltMigrationMutationResult.Rejected;
                if (!ValidatePrefabCore(out detail)) return BoltMigrationMutationResult.Rejected;
                detail = "The entity or peer changed during retirement preflight.";
                if (!MatchesCurrentCore() || _reentered) return BoltMigrationMutationResult.Rejected;

                // The local character is an existing rig, not an instance of PlayerNetwork.
                // Native Detach retires its identity and remote replicas without giving this rig
                // to the prefab pool. Its component/state/input rebinding remains adapter work.
                PlayerRoles.Retirement playerRetirement = null;
                if (_retainsLocalPlayerRig && !PlayerRoles.TryBeginRetirement(_entity, out playerRetirement))
                {
                    detail = "The retained player's detach callbacks could not be captured before retirement.";
                    return BoltMigrationMutationResult.Rejected;
                }
                _retireAttempted = true;
                _retireResult = BoltMigrationMutationResult.Unconfirmed;
                bool callbacksCompleted;
                try
                {
                    if (_retainsLocalPlayerRig) BoltCore.Detach(_entity, null);
                    else BoltCore.Destroy(_entity, null);
                    callbacksCompleted = playerRetirement == null || playerRetirement.IsComplete;
                }
                finally { playerRetirement?.Dispose(); }
                if (callbacksCompleted && MatchesRetiredCore() && !_reentered)
                    _retireResult = BoltMigrationMutationResult.Applied;
                detail = _retireResult == BoltMigrationMutationResult.Applied
                    ? (_retainsLocalPlayerRig
                        ? "Native detachment retired the identity and retained the local rig; remote cleanup and control rebinding remain required."
                        : "Native owner destruction removed the local identity; remote cleanup must be observed separately.")
                    : "Native retirement was attempted, but its complete local effect is unconfirmed.";
                return _retireResult;
            }
            catch (Exception error)
            {
                detail = "Native retirement failed (" + error.GetType().Name + ").";
                return _retireAttempted ? BoltMigrationMutationResult.Unconfirmed
                    : BoltMigrationMutationResult.Rejected;
            }
            finally { _busy = false; GC.KeepAlive(this); }
        }

        internal BoltMigrationMutationResult Recreate(BoltMigrationSnapshot snapshot,
            out BoltEntity replacement, out string detail)
        {
            replacement = null;
            detail = "Recreation requires the original game thread and a non-reentrant context.";
            if (Environment.CurrentManagedThreadId != _thread || !Enter())
                return _recreateAttempted ? BoltMigrationMutationResult.Unconfirmed
                    : BoltMigrationMutationResult.Rejected;
            try
            {
                if (_recreateAttempted)
                {
                    replacement = _replacement;
                    detail = "The native instantiate attempt is not replayed.";
                    return _recreateResult == BoltMigrationMutationResult.Applied
                        && snapshot != null && snapshot.Plan == _recreationSnapshot.Plan
                        && snapshot.MatchesDigest(_recreationSnapshot.DigestSpan)
                        && MatchesRetiredCore() && _replacementNative != null
                        && _replacementNative.MatchesCurrentCore() && !_reentered ? _recreateResult
                        : BoltMigrationMutationResult.Unconfirmed;
                }
                detail = "Recreation requires the captured remote identity, its snapshot, and completed native proxy cleanup.";
                if (_localOwner || snapshot == null || !snapshot.Plan.IsValid
                    || snapshot.Plan.OldNetworkId != NetworkId || snapshot.Plan.PrefabId != PrefabId
                    || snapshot.Plan.StateTypeId != StateTypeId
                    || !BoltMigrationProtocol.IsValidPose(snapshot.Position, snapshot.Rotation)
                    || !MatchesRetiredCore())
                    return BoltMigrationMutationResult.Rejected;
                if (!ValidatePrefabCore(out detail)) return BoltMigrationMutationResult.Rejected;
                var prefabId = new Bolt.PrefabId(PrefabId);
                var typeId = _meta.TypeId;
                var flags = InstantiateFlags.ZERO;
                detail = "The old identity or peer changed during recreation preflight.";
                if (!MatchesRetiredCore() || _reentered) return BoltMigrationMutationResult.Rejected;

                PlayerRoles.Creation playerCreation = null;
                if (prefabId == BoltPrefabs.PlayerNetwork
                    && !PlayerRoles.TryBeginRecreation(_peer, snapshot, out playerCreation))
                {
                    detail = "The server player creation role is unavailable; no native creation was attempted.";
                    return BoltMigrationMutationResult.Rejected;
                }

                _recreationSnapshot = snapshot;
                _recreateAttempted = true;
                _recreateResult = BoltMigrationMutationResult.Unconfirmed;
                // The ordinary native path allocates a new identity and invokes initialization/Attached.
                // Restoration and reference repair belong to the complete adapter, after this returns.
                bool playerRoleMatched;
                try
                {
                    _replacement = BoltCore.Instantiate(prefabId, typeId, snapshot.Position, snapshot.Rotation,
                        flags, null, null, false);
                    playerRoleMatched = playerCreation == null || playerCreation.Matches(_replacement);
                }
                finally
                {
                    playerCreation?.Dispose();
                }
                replacement = _replacement;
                if (playerRoleMatched && MatchesRetiredCore() && !_reentered && _replacement is not null
                    && !_replacement.WasCollected && _replacement && _replacement.Pointer != _entity.Pointer
                    && _replacement.gameObject?.Pointer != _gameObject.Pointer
                    && TryCaptureCore(_peer, _replacement.networkId.PackedValue, true, out _replacementNative, out _)
                    && _replacementNative._entity.Pointer == _replacement.Pointer
                    && _replacementNative.NetworkId != NetworkId && _replacementNative.PrefabId == PrefabId
                    && _replacementNative.StateTypeId == StateTypeId
                    && _replacementNative._stateClass == _stateClass && _replacementNative._metaClass == _metaClass
                    && _replacementNative._factory.Pointer == _factory.Pointer && _replacementNative._pool.Pointer == _pool.Pointer
                    && _replacementNative._serializerId == _serializerId && MatchesRetiredCore() && !_reentered)
                    _recreateResult = BoltMigrationMutationResult.Applied;
                detail = _recreateResult == BoltMigrationMutationResult.Applied
                    ? "A fresh locally owned native identity was created; state restoration and reference repair remain required."
                    : "Native instantiation was attempted, but the replacement is unconfirmed; no cleanup or rollback was attempted.";
                return _recreateResult;
            }
            catch (Exception error)
            {
                replacement = _replacement;
                detail = "Native recreation failed (" + error.GetType().Name + ").";
                return _recreateAttempted ? BoltMigrationMutationResult.Unconfirmed
                    : BoltMigrationMutationResult.Rejected;
            }
            finally { _busy = false; GC.KeepAlive(this); }
        }

        internal static bool MatchesReplacement(ControlPeer peer, BoltEntity entity,
            BoltMigrationSnapshot snapshot, bool localOwner)
        {
            if (!Enter()) return false;
            BoltMigrationNative captured = null;
            try
            {
                // Enrollment establishes the game thread before even reading the supplied wrapper.
                if (snapshot == null || !snapshot.Plan.IsValid || !Enrolled(peer) || !Supported()
                    || entity is null || entity.WasCollected || !entity || !entity.isAttached) return false;
                var id = entity.networkId.PackedValue;
                return id != 0 && id != snapshot.Plan.OldNetworkId
                    && TryCaptureCore(peer, id, localOwner, out captured, out _)
                    && captured._entity.Pointer == entity.Pointer && captured.PrefabId == snapshot.Plan.PrefabId
                    && captured.StateTypeId == snapshot.Plan.StateTypeId && !captured._sceneObject && !_reentered;
            }
            catch (Exception) { return false; }
            finally { _busy = false; GC.KeepAlive(captured); GC.KeepAlive(entity); }
        }

        private static bool Enter()
        {
            if (_busy) { _reentered = true; return false; }
            _busy = true;
            _reentered = false;
            return true;
        }

        private static bool Enrolled(ControlPeer peer) => peer != null
            && ClientEnrollment.TryRead(peer, out var enrollment) == EnrollmentReadStatus.Success
            && enrollment != null && ReferenceEquals(enrollment.Peer, peer)
            && enrollment.State == EnrollmentState.Enrolled
            && (enrollment.RequiredCapabilities & BoltMigrationProtocol.Capability) != 0
            && (enrollment.OfferedCapabilities & BoltMigrationProtocol.Capability) != 0;

        private static bool Supported() => HarmonySupport.CreateState()?.SafeForHarmonyPatches == true
            && GameBuild.IsRecognized;

        private static bool MatchesRole(ControlPeer peer) => BoltNetwork.isRunning
            && (peer.ServerSession != null ? BoltNetwork.isServer : BoltNetwork.isClient && !BoltNetwork.isServer);

        private bool MatchesPeerCore()
        {
            if (Environment.CurrentManagedThreadId != _thread || !Enrolled(_peer) || !Supported()
                || !MatchesRole(_peer) || !_peer.IsCurrent || !_connection.Matches() || !_source.Matches()
                || _connection.Connection == null || _connection.Pointer != _peer.ConnectionPointer
                || _connection.Transport.Pointer != _peer.TransportPointer
                || _connection.Socket.Pointer != _peer.SocketPointer || _connection.Id != _peer.ConnectionId
                || ControlTransport.Resolve(_peer)?.Pointer != _connection.Pointer) return false;
            return _peer.ServerSession == null
                ? BoltNetwork.server?.Pointer == _connection.Pointer
                : _peer.ServerSession.IsCurrent && ReferenceEquals(_peer.ServerSession,
                    ServerSessions.ResolveObserved(_connection.Connection));
        }

        private unsafe bool MatchesCurrentCore()
        {
            if (!MatchesPeerCore() || _entity.WasCollected || !_entity || !_entity.isAttached
                || _gameObject == null || _gameObject.WasCollected || !_gameObject
                || _entity.gameObject?.Pointer != _gameObject.Pointer || _backing.WasCollected || !_backing.IsAttached
                || _entity._entity?.Pointer != _backing.Pointer || _backing.UnityObject?.Pointer != _entity.Pointer
                || _entity.networkId.PackedValue != NetworkId || _backing.NetworkId.PackedValue != NetworkId
                || _entity.prefabId.Value != PrefabId || _backing.PrefabId.Value != PrefabId
                || (_backing.IsSceneObject || !_entity.sceneGuid.IsNone) != _sceneObject
                || _entity.isOwner != _localOwner || _backing.IsOwner != _localOwner
                || (_backing.Source?.Pointer ?? IntPtr.Zero) != _source.Pointer
                || (_localOwner ? _source.Pointer != IntPtr.Zero : _source.Pointer != _connection.Pointer)
                || _state.WasCollected || _backing.Serializer?.Pointer != _state.Pointer
                || _state.Entity?.Pointer != _backing.Pointer || _meta.WasCollected
                || _state.Meta?.Pointer != _meta.Pointer || _meta.TypeId.Value != StateTypeId
                || IL2CPP.il2cpp_object_get_class(_state.Pointer) != _stateClass
                || IL2CPP.il2cpp_object_get_class(_meta.Pointer) != _metaClass
                || _entity.serializerGuid != _serializerId || !MatchesFactory()
                || _pool == null || _pool.WasCollected || BoltCore.PrefabPool?.Pointer != _pool.Pointer
                || !BoltMigrationProtocol.IsValidPose(Position, Rotation)) return false;
            if (_retainsLocalPlayerRig && (LocalPlayer.Entity?.Pointer != _entity.Pointer
                || LocalPlayer.GameObject?.Pointer != _gameObject.Pointer)) return false;
            var registered = BoltNetwork.FindEntity(new NetworkId(NetworkId));
            var registeredBacking = BoltCore.FindEntity(new NetworkId(NetworkId));
            if (registered is null || registered.WasCollected || registered.Pointer != _entity.Pointer
                || registeredBacking == null || registeredBacking.WasCollected
                || registeredBacking.Pointer != _backing.Pointer) return false;
            if (_localOwner) return true;
            return MatchesIncomingChannel() && _incomingProxy != null && !_incomingProxy.WasCollected
                && _incoming.TryGetValue(new NetworkId(NetworkId), out var proxy) && proxy != null
                && !proxy.WasCollected && proxy.Pointer == _incomingProxy.Pointer
                && proxy.NetworkId.PackedValue == NetworkId && proxy.Entity?.Pointer == _backing.Pointer
                && proxy.Connection?.Pointer == _source.Pointer;
        }

        private bool MatchesRetiredCore()
        {
            if (!MatchesPeerCore() || _backing.WasCollected || _backing.IsAttached
                || _backing.NetworkId.PackedValue != NetworkId
                || _backing.IsOwner != _localOwner || (_backing.Source?.Pointer ?? IntPtr.Zero) != _source.Pointer
                || BoltCore.FindEntity(new NetworkId(NetworkId)) != null
                || BoltNetwork.FindEntity(new NetworkId(NetworkId)) is not null) return false;
            // Detach clears BoltEntity._entity. A pooled or reattached old object is not retired here.
            if (_entity.WasCollected || (_entity && (_entity._entity != null || _entity.isAttached))) return false;
            if (_retainsLocalPlayerRig && (!_entity || _gameObject == null || _gameObject.WasCollected || !_gameObject
                || _entity.gameObject?.Pointer != _gameObject.Pointer
                || LocalPlayer.GameObject?.Pointer != _gameObject.Pointer)) return false;
            // Registry disappearance alone is insufficient: a late incoming-proxy destroy still owns
            // the old backing object. Never detach it manually or hand its GameObject to Instantiate.
            return _localOwner || (MatchesIncomingChannel() && !_incoming.ContainsKey(new NetworkId(NetworkId)));
        }

        private bool MatchesIncomingChannel() => _incomingChannel != null && !_incomingChannel.WasCollected
            && _source.Connection != null && _source.Connection._entityChannel?.Pointer == _incomingChannel.Pointer
            && _incoming != null && !_incoming.WasCollected && _incomingChannel._incommingDict?.Pointer == _incoming.Pointer;

        private bool MatchesFactory()
        {
            if (_factory == null || _factory.WasCollected || _factory.TypeId.Value != StateTypeId
                || _serializerId.IsNone || _factory.TypeKey != _serializerId) return false;
            var byId = Factory.GetFactory(_meta.TypeId);
            var byKey = Factory.GetFactory(_serializerId);
            return byId != null && !byId.WasCollected && byId.Pointer == _factory.Pointer
                && byKey != null && !byKey.WasCollected && byKey.Pointer == _factory.Pointer;
        }

        private bool ValidatePrefabCore(out string detail)
        {
            detail = "The captured connection, serializer factory and prefab pool must remain current.";
            if (!MatchesPeerCore() || _meta.WasCollected || _meta.TypeId.Value != StateTypeId || !MatchesFactory()
                || _pool == null || _pool.WasCollected || BoltCore.PrefabPool?.Pointer != _pool.Pointer) return false;
            detail = "Scene entities and identities without a recreatable native prefab are unsupported.";
            if (_sceneObject || PrefabId < 0) return false;
            var prefab = _pool.LoadPrefab(new Bolt.PrefabId(PrefabId));
            if (prefab is null || prefab.WasCollected || !prefab || prefab.Pointer == _gameObject.Pointer) return false;
            var component = prefab.GetComponent<BoltEntity>();
            if (component is null || component.WasCollected || !component || component.isAttached
                || component._entity != null || component.prefabId.Value != PrefabId
                || !component.sceneGuid.IsNone || component.serializerGuid != _serializerId) return false;
            detail = "The native prefab does not allow instantiation on this client.";
            if (BoltNetwork.isClient && !_retainsLocalPlayerRig && !component._allowInstantiateOnClient) return false;
            detail = "The native prefab changed after validation.";
            if (_prefab is not null && (_prefab.WasCollected || !_prefab || _prefab.Pointer != prefab.Pointer
                || _prefabEntity.WasCollected || !_prefabEntity || _prefabEntity.Pointer != component.Pointer)) return false;
            _prefab = prefab;
            _prefabEntity = component;
            if (!MatchesPeerCore() || BoltCore.PrefabPool?.Pointer != _pool.Pointer || !MatchesFactory() || _reentered)
                return false;
            detail = "The live native prefab matches the captured serializer; complete adapter coverage is still required.";
            return true;
        }

        private readonly struct ConnectionLife
        {
            internal readonly BoltConnection Connection;
            internal readonly UdpConnection Transport;
            internal readonly UdpSocket Socket;
            internal readonly uint Id;
            internal IntPtr Pointer => Connection?.Pointer ?? IntPtr.Zero;

            internal ConnectionLife(BoltConnection connection)
            {
                Connection = connection;
                Transport = connection?._udp;
                Socket = Transport?.Socket;
                Id = connection?.ConnectionId ?? 0;
            }

            internal bool Matches()
            {
                if (Connection == null) return true;
                if (Connection.WasCollected || Connection.ConnectionId != Id
                    || Transport == null || Transport.WasCollected || Connection._udp?.Pointer != Transport.Pointer
                    || Transport.ConnectionId != Id || Transport.State != UdpConnectionState.Connected
                    || Transport.UserToken?.Pointer != Connection.Pointer
                    || Socket == null || Socket.WasCollected || Transport.Socket?.Pointer != Socket.Pointer
                    || Socket.State == UdpSocketState.Shutdown) return false;
                var socket = BoltCore._udpSocket;
                return socket != null && !socket.WasCollected && socket.Pointer == Socket.Pointer;
            }
        }
    }
}
