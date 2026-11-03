// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.CompilerServices;
using Bolt;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.Runtime;
using Modstoke.Loader;
using UdpKit;

namespace Modstoke.Sons
{
    internal sealed class BoltCorrectionTarget
    {
        [ThreadStatic] private static bool _busy;
        [ThreadStatic] private static bool _reentered;

        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly ControlPeer _peer;
        private readonly BoltCorrectionBinding _binding;
        private readonly bool _localOwner;
        private readonly BoltEntity _entity;
        private readonly Entity _backing;
        private readonly NetworkState _state;
        private readonly NetworkObj_Meta _meta;
        private readonly Il2CppReferenceArray<NetworkPropertyInfo> _properties;
        private readonly Il2CppSystem.Collections.Generic.List<NetworkObj> _objects;
        private readonly NetworkProperty _property;
        private readonly NetworkProperty_Mecanim _mecanim;
        private readonly NetworkObj _object;
        private readonly NetworkObj_Meta _objectMeta;
        private readonly NetworkObj_Meta _propertyMeta;
        private readonly IntPtr _stateClass;
        private readonly IntPtr _metaClass;
        private readonly IntPtr _objectClass;
        private readonly int _objectIndex;
        private readonly int _objectStorage;
        private readonly int _objectProperties;
        private readonly int _objectOffsetObjects;
        private readonly int _propertyStorage;
        private readonly int _propertyProperties;
        private readonly int _slot;
        private readonly ConnectionLife _connection;
        private readonly ConnectionLife _source;

        private BoltCorrectionTarget(ControlPeer peer,
            BoltCorrectionBinding binding, bool localOwner, BoltEntity entity,
            Entity backing, NetworkState state, NetworkObj_Meta meta,
            Il2CppReferenceArray<NetworkPropertyInfo> properties,
            Il2CppSystem.Collections.Generic.List<NetworkObj> objects,
            NetworkProperty property, NetworkObj obj, int objectIndex, BoltConnection connection)
        {
            _peer = peer; _binding = binding; _localOwner = localOwner;
            _entity = entity; _backing = backing; _state = state; _meta = meta;
            _properties = properties; _objects = objects; _property = property; _object = obj;
            _mecanim = property.TryCast<NetworkProperty_Mecanim>();
            _objectMeta = obj.Meta; _propertyMeta = property.PropertyMeta;
            _stateClass = IL2CPP.il2cpp_object_get_class(state.Pointer);
            _metaClass = IL2CPP.il2cpp_object_get_class(meta.Pointer);
            _objectClass = IL2CPP.il2cpp_object_get_class(obj.Pointer);
            _objectIndex = objectIndex;
            _objectStorage = obj.OffsetStorage; _objectProperties = obj.OffsetProperties;
            _objectOffsetObjects = obj.OffsetObjects;
            _propertyStorage = property.OffsetStorage; _propertyProperties = property.OffsetProperties;
            _slot = checked(_objectStorage + _propertyStorage);
            _connection = new ConnectionLife(connection);
            _source = new ConnectionLife(backing.Source);
        }

        internal static unsafe bool TryCapture(ControlPeer peer,
            BoltCorrectionBinding binding, bool localOwner,
            out BoltCorrectionTarget target)
        {
            target = null;
            if (_busy) { _reentered = true; return false; }
            _busy = true;
            _reentered = false;
            try
            {
                if (peer == null || !binding.IsValid) return false;
                // GetPeers is managed-only and returns no peers off the initialized game thread.
                // This one-time snapshot precedes every native read during capture.
                bool onThread = false;
                var peers = ControlTransport.GetPeers();
                for (int i = 0; i < peers.Count; i++)
                    if (ReferenceEquals(peers[i], peer)) { onThread = true; break; }
                if (!onThread || !Supported() || !MatchesRole(peer, localOwner)) return false;
                BoltProperties.RequireLayout();
                RuntimeHelpers.RunClassConstructor(typeof(NetworkProperty_Bool).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(NetworkProperty_Integer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(NetworkProperty_Float).TypeHandle);

                var connection = ControlTransport.Resolve(peer);
                if (connection == null || connection.WasCollected) return false;
                var entity = BoltNetwork.FindEntity(new NetworkId(binding.EntityNetworkId));
                if (entity is null || entity.WasCollected || !entity || !entity.isAttached) return false;
                var backing = entity._entity;
                if (backing is null || backing.WasCollected || !backing.IsAttached
                    || backing.IsOwner != localOwner || entity.isOwner != localOwner) return false;
                var state = backing.Serializer?.TryCast<NetworkState>();
                var meta = state?.Meta;
                if (state == null || state.WasCollected || meta == null || meta.WasCollected
                    || meta.TypeId.Value != binding.StateTypeId) return false;
                var properties = meta.Properties;
                var objects = state.RootObjects;
                if (properties == null || properties.WasCollected || objects == null || objects.WasCollected
                    || (uint)binding.PropertyIndex >= (uint)properties.Length) return false;

                // NetworkPropertyInfo contains references; read the qualified inline descriptor,
                // never its boxing indexer or a copied value containing GC references.
                byte* info = (byte*)properties.Pointer + 32 + 32L * binding.PropertyIndex;
                int objectIndex = *(int*)(info + 16);
                IntPtr propertyPointer = *(IntPtr*)(info + 24);
                if (propertyPointer == IntPtr.Zero || (uint)objectIndex >= (uint)objects.Count) return false;
                var property = Il2CppObjectPool.Get<NetworkProperty>(propertyPointer);
                var obj = objects[objectIndex];
                if (obj == null || obj.WasCollected
                    || BoltProperties.KindOf(property) != binding.Kind) return false;
                var candidate = new BoltCorrectionTarget(peer, binding, localOwner,
                    entity, backing, state, meta, properties, objects, property, obj, objectIndex, connection);
                if (!candidate.MatchesCurrent() || !candidate.TryGetStorage(out var frames, out var storage, out var values)
                    || !candidate.MatchesStorage(frames, storage, values, storage.Frame)
                    || _reentered) return false;
                target = candidate;
                return true;
            }
            catch (Exception) { return false; }
            finally { _busy = false; }
        }

        internal bool IsCurrent()
        {
            if (Environment.CurrentManagedThreadId != _thread) return false;
            if (_busy) { _reentered = true; return false; }
            _busy = true;
            _reentered = false;
            try { return MatchesCurrent() && !_reentered; }
            catch (Exception) { return false; }
            finally { _busy = false; GC.KeepAlive(this); }
        }

        internal unsafe BoltCorrectionApplyResult Apply(BoltScalar value)
        {
            if (Environment.CurrentManagedThreadId != _thread) return BoltCorrectionApplyResult.Rejected;
            if (_busy) { _reentered = true; return BoltCorrectionApplyResult.Rejected; }
            _busy = true;
            _reentered = false;
            bool attempted = false;
            NetworkStorage storage = null;
            Il2CppReferenceArray<NetworkValue> values = null;
            BoltDoubleList<NetworkStorage> frames = null;
            try
            {
                if (!_localOwner || value.Kind != _binding.Kind || !value.IsValidServerValue
                    || (value.Kind == BoltScalarKind.Boolean && value.Bits > 1)
                    || !MatchesCurrent() || !TryGetStorage(out frames, out storage, out values))
                    return BoltCorrectionApplyResult.Rejected;
                int frame = storage.Frame;
                if (!MatchesStorage(frames, storage, values, frame) || _reentered)
                    return BoltCorrectionApplyResult.Rejected;

                byte* destination = (byte*)values.Pointer + 32 + 24L * _slot;
                if (EqualsValue(destination, value))
                    return MatchesStorage(frames, storage, values, frame) && !_reentered
                        ? BoltCorrectionApplyResult.Unchanged
                        : BoltCorrectionApplyResult.Rejected;

                // Native SetDynamic writes only the base primitive (Float0 even when interpolated).
                // Mark uncertainty before the first store; never roll back a potentially stale slot.
                attempted = true;
                if (value.Kind == BoltScalarKind.Boolean) *destination = (byte)value.Bits;
                else *(uint*)destination = value.Bits;
                if (!MatchesStorage(frames, storage, values, frame) || _reentered
                    || !EqualsValue(destination, value)) return BoltCorrectionApplyResult.Unconfirmed;
                storage.PropertyChanged(_binding.PropertyIndex);
                // Revalidate native lifetimes after notification before reading the raw slot again.
                return MatchesStorage(frames, storage, values, frame) && !_reentered
                    && EqualsValue(destination, value) && storage.IsSet(_binding.PropertyIndex)
                    ? BoltCorrectionApplyResult.Applied
                    : BoltCorrectionApplyResult.Unconfirmed;
            }
            catch (Exception)
            {
                return attempted ? BoltCorrectionApplyResult.Unconfirmed
                    : BoltCorrectionApplyResult.Rejected;
            }
            finally
            {
                _busy = false;
                GC.KeepAlive(values); GC.KeepAlive(storage); GC.KeepAlive(frames); GC.KeepAlive(this);
            }
        }

        private static bool Supported() => HarmonySupport.CreateState()?.SafeForHarmonyPatches == true
            && GameBuild.SupportsServerCore;

        private static bool MatchesRole(ControlPeer peer, bool localOwner) => BoltNetwork.isRunning
            && (localOwner ? peer.ServerSession == null && BoltNetwork.isClient && !BoltNetwork.isServer
                : peer.ServerSession != null && BoltNetwork.isServer);

        private unsafe bool MatchesCurrent()
        {
            if (Environment.CurrentManagedThreadId != _thread || !Supported() || !MatchesRole(_peer, _localOwner)
                || !_peer.IsCurrent || !_connection.Matches() || !_source.Matches()
                || _connection.Connection.Pointer != _peer.ConnectionPointer
                || _connection.Transport.Pointer != _peer.TransportPointer
                || _connection.Socket.Pointer != _peer.SocketPointer
                || _connection.Id != _peer.ConnectionId) return false;
            if (_localOwner)
            {
                if (BoltNetwork.server?.Pointer != _connection.Connection.Pointer) return false;
            }
            else if (!ReferenceEquals(_peer.ServerSession,
                ServerSessions.ResolveObserved(_connection.Connection))) return false;

            var registered = BoltNetwork.FindEntity(new NetworkId(_binding.EntityNetworkId));
            if (registered is null || registered.WasCollected || !registered
                || registered.Pointer != _entity.Pointer || !registered.isAttached) return false;
            if (_entity.WasCollected || !_entity || !_entity.isAttached || _backing.WasCollected
                || !_backing.IsAttached || _entity._entity?.Pointer != _backing.Pointer
                || _backing.UnityObject?.Pointer != _entity.Pointer
                || _entity.networkId.PackedValue != _binding.EntityNetworkId
                || _backing.NetworkId.PackedValue != _binding.EntityNetworkId
                || _entity.isOwner != _localOwner || _backing.IsOwner != _localOwner
                || (_backing.Source?.Pointer ?? IntPtr.Zero) != _source.Pointer
                || (_localOwner && _source.Pointer != IntPtr.Zero)
                || (!_localOwner && _source.Pointer != _connection.Connection.Pointer)
                || _state.WasCollected || _state.Entity?.Pointer != _backing.Pointer
                || _backing.Serializer?.Pointer != _state.Pointer
                || IL2CPP.il2cpp_object_get_class(_state.Pointer) != _stateClass
                || _meta.WasCollected || _state.Meta?.Pointer != _meta.Pointer
                || IL2CPP.il2cpp_object_get_class(_meta.Pointer) != _metaClass
                || _meta.TypeId.Value != _binding.StateTypeId
                || _properties.WasCollected || _meta.Properties?.Pointer != _properties.Pointer
                || (uint)_binding.PropertyIndex >= (uint)_properties.Length
                || _objects.WasCollected || _state.RootObjects?.Pointer != _objects.Pointer
                || (uint)_objectIndex >= (uint)_objects.Count
                || _object.WasCollected || _objects[_objectIndex]?.Pointer != _object.Pointer
                || IL2CPP.il2cpp_object_get_class(_object.Pointer) != _objectClass
                || _object.RootState?.Pointer != _state.Pointer
                || !MatchesMeta(_object.Meta, _objectMeta) || _property.WasCollected
                || !MatchesMeta(_property.PropertyMeta, _propertyMeta)
                || _object.OffsetStorage != _objectStorage || _object.OffsetProperties != _objectProperties
                || _object.OffsetObjects != _objectOffsetObjects || _property.OffsetStorage != _propertyStorage
                || _property.OffsetProperties != _propertyProperties
                || BoltProperties.KindOf(_property) != _binding.Kind
                || _objectStorage < 0 || _propertyStorage < 0 || _objectProperties < 0 || _propertyProperties < 0
                || (long)_objectProperties + _propertyProperties != _binding.PropertyIndex) return false;

            byte* info = (byte*)_properties.Pointer + 32 + 32L * _binding.PropertyIndex;
            // The native scalar setters refuse animator-directed writes, including bool and integer.
            return *(int*)(info + 16) == _objectIndex && *(IntPtr*)(info + 24) == _property.Pointer
                && _mecanim != null && !_mecanim.WasCollected && _mecanim.Pointer == _property.Pointer
                && _mecanim.MecanimDirection == MecanimDirection.UsingBoltProperties;
        }

        private static bool MatchesMeta(NetworkObj_Meta current, NetworkObj_Meta captured) => captured == null
            ? current == null : !captured.WasCollected && current != null && !current.WasCollected
                && current.Pointer == captured.Pointer;

        private bool TryGetStorage(out BoltDoubleList<NetworkStorage> frames, out NetworkStorage storage,
            out Il2CppReferenceArray<NetworkValue> values)
        {
            frames = _state.Frames;
            storage = null;
            values = null;
            if (frames == null || frames.WasCollected) return false;
            storage = _state.Storage;
            if (storage == null || storage.WasCollected || frames.first?.Pointer != storage.Pointer
                || storage._IBoltListNode_list_k__BackingField?.Pointer != frames.Pointer) return false;
            values = storage.Values;
            return values != null && !values.WasCollected && (uint)_slot < (uint)values.Length;
        }

        private bool MatchesStorage(BoltDoubleList<NetworkStorage> frames, NetworkStorage storage,
            Il2CppReferenceArray<NetworkValue> values, int frame) => MatchesCurrent()
            && !frames.WasCollected && _state.Frames?.Pointer == frames.Pointer
            && !storage.WasCollected && _state.Storage?.Pointer == storage.Pointer
            && _object.Storage?.Pointer == storage.Pointer
            && frames.first?.Pointer == storage.Pointer
            // Root is intentionally unset by native state initialization; list membership is authoritative.
            && storage._IBoltListNode_list_k__BackingField?.Pointer == frames.Pointer
            && storage.Frame == frame && !values.WasCollected && storage.Values?.Pointer == values.Pointer
            && (uint)_slot < (uint)values.Length;

        private static unsafe bool EqualsValue(byte* address, BoltScalar value) => value.Kind switch
        {
            BoltScalarKind.Boolean => *address == value.Bits,
            BoltScalarKind.Int32 => *(uint*)address == value.Bits,
            BoltScalarKind.Single => *(float*)address == BitConverter.UInt32BitsToSingle(value.Bits),
            _ => false
        };

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
