// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using Il2CppInterop.Runtime.InteropTypes;
using Modstoke.Loader;
using TheForest.Utils;
using UdpKit;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace Modstoke.Sons
{
    /// <summary>
    /// Exact, surviving player identity caches plus a composed Bolt reference repair.
    /// This is not full reference-graph coverage, rig repair, lifecycle repair or a migration adapter.
    /// </summary>
    public static partial class PlayerMigrationReferences
    {
        [ThreadStatic] private static bool _busy;
        [ThreadStatic] private static bool _reentered;

        /// <summary>
        /// Capture before retirement on any participant, not just LocalPlayer. Loaded native cache
        /// owners, including inactive objects, are captured and rediscovered once before writing.
        /// Keep surviving owners and their writers quiescent until aggregate repair completes.
        /// Invoke the single-use repair only after replica-owned exclusions are destroyed and
        /// replacement replica LOD properties have received their authoritative values.
        /// An empty explicit slot list means no Bolt properties, not discovery of all such properties.
        /// </summary>
        public static bool TryCapture(BoltEntity original,
            IReadOnlyList<BoltMigrationReferenceSlot> boltSlots,
            out IBoltMigrationReferenceRepair repair, out string detail)
            => TryCaptureCore(original, boltSlots, null, out repair, out detail);

        /// <summary>
        /// Compose an already captured, unconsumed repair for this exact original identity/run,
        /// including loaded-slot discovery. The caller transfers exclusive use of that repair;
        /// its identity and completeness are obligations of the supplied participant's contract.
        /// Does not recapture Bolt slots. Keep all writers quiescent until aggregate completion.
        /// </summary>
        public static bool TryCapture(BoltEntity original, IBoltMigrationReferenceRepair boltRepair,
            out IBoltMigrationReferenceRepair repair, out string detail)
            => TryCaptureCore(original, null, boltRepair, out repair, out detail);

        private static bool TryCaptureCore(BoltEntity original,
            IReadOnlyList<BoltMigrationReferenceSlot> boltSlots,
            IBoltMigrationReferenceRepair boltRepair,
            out IBoltMigrationReferenceRepair repair, out string detail)
        {
            repair = null;
            detail = "Player cache capture requires the initialized enrollment game thread.";
            if (!OnSelectedThread()) return false;
            if (_busy) { _reentered = true; detail = "Player cache capture/repair cannot re-enter."; return false; }
            _busy = true;
            _reentered = false;
            try
            {
                if (!Supported() || (boltSlots == null && boltRepair == null))
                { detail = "A recognized build and explicit Bolt slots or a captured Bolt repair are required."; return false; }
                var run = new RunBinding();
                var source = new PlayerBinding(original);
                if (!run.Matches() || !source.Matches() || !source.HasOriginalRole())
                { detail = "The original must be the exact attached player and source, server or observer role."; return false; }
                var caches = new CacheSet(source);
                if (!caches.Matches(null, 0, out detail)) return false;
                if (boltRepair == null
                    && !BoltMigrationReferences.TryCapture(source.Id, boltSlots, out boltRepair, out detail))
                    return false;
                if (!run.Matches() || !source.Matches() || !caches.Matches(null, 0, out detail) || _reentered)
                { detail = "Player identity/cache expectations changed during capture: " + detail; return false; }
                repair = new Repair(run, source, caches, boltRepair);
                detail = "Captured " + caches.Count + " surviving player identity caches and a composed Bolt repair. "
                    + "Replica-owned exclusions require confirmed destruction; full graph coverage remains the adapter's responsibility.";
                return true;
            }
            catch (Exception error)
            { detail = "Player cache capture rejected: " + error.GetType().Name + ": " + error.Message; return false; }
            finally { _busy = false; }
        }

        private static bool OnSelectedThread() => ClientEnrollment.TryRead(
            (ControlPeer)null, out _) == EnrollmentReadStatus.PeerUnavailable;

        private static bool Supported() => HarmonySupport.CreateState()?.SafeForHarmonyPatches == true
            && GameBuild.IsRecognized;

        private static bool Live(Il2CppObjectBase value) => value is not null && !value.WasCollected
            && value.Pointer != IntPtr.Zero;
        private static bool Alive(UnityObject value) => Live(value) && value;
        private static bool Same(Il2CppObjectBase left, Il2CppObjectBase right) =>
            left is null ? right is null : Live(left) && Live(right) && left.Pointer == right.Pointer;

        private static void Require(bool condition, string detail)
        { if (!condition) throw new InvalidOperationException(detail); }

        private sealed class Repair : IBoltMigrationReferenceRepair
        {
            private readonly int _thread = Environment.CurrentManagedThreadId;
            private readonly RunBinding _run;
            private readonly PlayerBinding _source;
            private readonly CacheSet _caches;
            private readonly IBoltMigrationReferenceRepair _bolt;
            private bool _used;
            private PlayerBinding _target;
            private BoltMigrationMutationResult _result;
            private string _detail;

            internal Repair(RunBinding run, PlayerBinding source, CacheSet caches,
                IBoltMigrationReferenceRepair bolt)
            { _run = run; _source = source; _caches = caches; _bolt = bolt; }

            public BoltMigrationMutationResult TryRepair(BoltEntity replacement, out string detail)
            {
                detail = "Player cache repair requires its captured enrollment game thread.";
                if (Environment.CurrentManagedThreadId != _thread || !OnSelectedThread())
                    return BoltMigrationMutationResult.Rejected;
                if (_busy)
                {
                    _reentered = true;
                    detail = "Player cache capture/repair cannot re-enter.";
                    return BoltMigrationMutationResult.Rejected;
                }
                _busy = true;
                _reentered = false;
                bool repeat = _used;
                bool attempted = false;
                try
                {
                    if (repeat)
                    {
                        if (_target != null && Same(replacement, _target.UnityEntity)
                            && ContextMatches() && !_reentered)
                        { detail = _detail; return _result; }
                        detail = "This capture is consumed; the exact original replacement binding is required for a cached result.";
                        return BoltMigrationMutationResult.Rejected;
                    }
                    _used = true;
                    if (!_run.Matches())
                        return Finish(BoltMigrationMutationResult.Rejected, "The captured network run changed.", out detail);
                    _target = new PlayerBinding(replacement);
                    if (_target.Id == _source.Id || Same(_target.UnityEntity, _source.UnityEntity)
                        || Same(_target.State, _source.State) || _target.Serializer != _source.Serializer
                        || !_target.HasReplacementRole(_source) || !ContextMatches())
                        return Finish(BoltMigrationMutationResult.Rejected,
                            "The original is not retired or the distinct replacement does not match its exact player role.", out detail);

                    // Discover once, before even the composed helper can attempt its first write.
                    if (!_caches.DiscoverBeforeWrite(out detail))
                        return Finish(BoltMigrationMutationResult.Rejected, detail, out detail);
                    _caches.PrepareTarget(_target);
                    if (!_caches.Matches(_target, 0, out detail))
                        return Finish(BoltMigrationMutationResult.Rejected, detail, out detail);
                    if (!ContextMatches() || _reentered)
                        return Finish(BoltMigrationMutationResult.Rejected,
                            "Player/run binding changed or repair re-entered before the first write.", out detail);
                    attempted = true;
                    var boltResult = _bolt.TryRepair(replacement, out var boltDetail);
                    if (boltResult == BoltMigrationMutationResult.Rejected)
                        return Finish(boltResult, "Composed Bolt references rejected: " + boltDetail, out detail);
                    if (boltResult == BoltMigrationMutationResult.Unconfirmed)
                        return Finish(boltResult, "Composed Bolt references unconfirmed: " + boltDetail, out detail);
                    Require(boltResult == BoltMigrationMutationResult.Applied
                        || boltResult == BoltMigrationMutationResult.Unchanged, "Unexpected Bolt repair result.");
                    attempted = boltResult == BoltMigrationMutationResult.Applied;
                    for (int i = 0; i < _caches.Count; i++)
                    {
                        Require(ContextMatches() && !_reentered, "Player/run binding changed before cache write.");
                        Require(_caches.Matches(_target, i, out detail) && !_reentered, detail);
                        _caches.Write(i, _target, ref attempted);
                        Require(ContextMatches() && !_reentered, "Player/run binding changed after cache write.");
                        Require(_caches.Matches(_target, i + 1, out detail) && !_reentered, detail);
                    }
                    Require(_caches.Matches(_target, _caches.Count, out detail) && !_reentered, detail);
                    Require(ContextMatches() && !_reentered, "Player/run binding changed before completion.");
                    return Finish(attempted ? BoltMigrationMutationResult.Applied
                        : BoltMigrationMutationResult.Unchanged,
                        "Captured player caches and composed Bolt references confirmed; surviving owners were not replaced. "
                            + "This does not certify rig/lifecycle or full reference-graph repair.", out detail);
                }
                catch (Exception error)
                {
                    detail = "Player cache repair stopped: " + error.GetType().Name + ": " + error.Message;
                    if (repeat) return BoltMigrationMutationResult.Rejected;
                    return Finish(attempted ? BoltMigrationMutationResult.Unconfirmed
                        : BoltMigrationMutationResult.Rejected, detail, out detail);
                }
                finally { _busy = false; GC.KeepAlive(this); }
            }

            private bool ContextMatches() => _run.Matches() && _source.MatchesRetired() && _target.Matches()
                && _target.HasReplacementRole(_source);

            private BoltMigrationMutationResult Finish(BoltMigrationMutationResult result,
                string message, out string detail)
            { _result = result; _detail = detail = message; return result; }
        }

        private sealed class RunBinding
        {
            private readonly UdpSocket _socket = BoltCore._udpSocket;
            private readonly bool _server = BoltNetwork.isServer;
            private readonly bool _client = BoltNetwork.isClient;
            internal bool Matches() => OnSelectedThread() && Supported() && BoltNetwork.isRunning
                && (_server || _client) && BoltNetwork.isServer == _server && BoltNetwork.isClient == _client
                && Live(_socket) && _socket.State != UdpSocketState.Shutdown && Same(BoltCore._udpSocket, _socket);
        }

        private sealed class PlayerBinding
        {
            internal readonly BoltEntity UnityEntity;
            internal readonly Entity Attachment;
            internal readonly IPlayerState State;
            private readonly NetworkState _networkState;
            internal readonly ulong Id;
            internal readonly UniqueId Serializer;
            private readonly GameObject _root;
            private readonly Transform _rootTransform;
            internal readonly bool RetainsRoot;
            private readonly bool _owner;
            private readonly bool _control;
            private readonly ConnectionBinding _source;
            private readonly ConnectionBinding _controller;

            internal PlayerBinding(BoltEntity entity)
            {
                Require(Alive(entity) && entity.isAttached && entity.prefabId == BoltPrefabs.PlayerNetwork,
                    "An attached PlayerNetwork entity is required.");
                UnityEntity = entity; Attachment = entity._entity; State = entity.GetState<IPlayerState>();
                _networkState = State?.TryCast<NetworkState>();
                Id = entity.networkId.PackedValue; Serializer = entity.serializerGuid; _root = entity.gameObject;
                _rootTransform = entity.transform;
                Require(Live(Attachment) && Live(State) && Live(_networkState) && Id != 0,
                    "The player attachment/state is unavailable.");
                _owner = Attachment.IsOwner; _control = Attachment.HasControl;
                RetainsRoot = _owner && Same(LocalPlayer.GameObject, _root);
                _source = new ConnectionBinding(Attachment.Source);
                _controller = new ConnectionBinding(Attachment.Controller);
            }

            internal bool Matches() => Alive(UnityEntity) && Alive(_root) && Same(UnityEntity.gameObject, _root)
                && UnityEntity.isAttached && Live(Attachment) && Attachment.IsAttached && Live(State)
                && Same(UnityEntity._entity, Attachment) && Same(Attachment.UnityObject, UnityEntity)
                && UnityEntity.networkId.PackedValue == Id && Attachment.NetworkId.PackedValue == Id
                && UnityEntity.prefabId == BoltPrefabs.PlayerNetwork && Attachment.PrefabId == BoltPrefabs.PlayerNetwork
                && UnityEntity.serializerGuid == Serializer && Same(Attachment.Serializer, State)
                && Same(_networkState, State) && Same(_networkState.Entity, Attachment)
                && Same(UnityEntity.GetState<IPlayerState>(), State)
                && Same(BoltNetwork.FindEntity(new NetworkId(Id)), UnityEntity)
                && Same(BoltCore.FindEntity(new NetworkId(Id)), Attachment)
                && UnityEntity.isOwner == _owner && Attachment.IsOwner == _owner
                && UnityEntity.hasControl == _control && Attachment.HasControl == _control
                && _source.Matches(Attachment.Source) && _controller.Matches(Attachment.Controller)
                && Same(UnityEntity.source, _source.Connection) && Same(UnityEntity.controller, _controller.Connection)
                && RetainsRoot == (_owner && Same(LocalPlayer.GameObject, _root));

            internal bool HasOriginalRole()
            {
                if (RetainsRoot)
                    return BoltNetwork.isClient && !BoltNetwork.isServer && _source.Connection is null
                        && Same(LocalPlayer.Entity, UnityEntity) && Same(LocalPlayer.State, State);
                if (_owner || _control || Same(LocalPlayer.Entity, UnityEntity)
                    || Same(LocalPlayer.GameObject, _root)) return false;
                return Live(_source.Connection) && (BoltNetwork.isServer
                    || (BoltNetwork.isClient && _source.Matches(BoltNetwork.server)));
            }

            internal bool HasReplacementRole(PlayerBinding original)
            {
                if (RetainsRoot || Same(_root, original._root) || Same(LocalPlayer.GameObject, _root)) return false;
                if (original.RetainsRoot)
                    return BoltNetwork.isClient && !BoltNetwork.isServer && !_owner && _control
                        && _source.Matches(BoltNetwork.server);
                if (BoltNetwork.isServer)
                    return _owner && _source.Connection is null
                        && original._source.Matches(original._source.Connection)
                        && PlayerRoles.IsCurrentCarrierFor(UnityEntity, original.Id, original._source.Connection);
                return BoltNetwork.isClient && !_owner && !_control
                    && original._source.Matches(_source.Connection) && _source.Matches(BoltNetwork.server);
            }

            internal bool OwnsDoomedHierarchy(Component component) => !RetainsRoot && Alive(component)
                && Alive(_rootTransform) && (Same(component.transform, _rootTransform)
                    || component.transform.IsChildOf(_rootTransform));

            internal bool ReplicaHierarchyDestroyed() => !RetainsRoot && Live(UnityEntity) && !UnityEntity
                && Live(_root) && !_root && Live(_rootTransform) && !_rootTransform;

            internal bool MatchesRetired() => Live(UnityEntity) && Live(Attachment) && !Attachment.IsAttached
                && Attachment.NetworkId.PackedValue == Id && BoltCore.FindEntity(new NetworkId(Id)) is null
                && BoltNetwork.FindEntity(new NetworkId(Id)) is null
                && (!UnityEntity || (UnityEntity._entity is null && !UnityEntity.isAttached
                    && Alive(_root) && Same(UnityEntity.gameObject, _root)))
                && (!RetainsRoot || (Alive(UnityEntity) && Same(LocalPlayer.GameObject, _root)));
        }

        private sealed class ConnectionBinding
        {
            internal readonly BoltConnection Connection;
            private readonly UdpConnection _transport;
            private readonly UdpSocket _socket;
            private readonly uint _id;
            internal ConnectionBinding(BoltConnection connection)
            {
                Connection = connection; _transport = connection?._udp;
                _socket = _transport?.Socket; _id = connection?.ConnectionId ?? 0;
            }
            internal bool Matches(BoltConnection current) => Same(current, Connection)
                && (Connection is null || (Connection.ConnectionId == _id && Live(_transport)
                    && Same(Connection._udp, _transport) && _transport.ConnectionId == _id
                    && _transport.State == UdpConnectionState.Connected && Same(_transport.UserToken, Connection)
                    && Live(_socket) && _socket.State != UdpSocketState.Shutdown
                    && Same(_transport.Socket, _socket) && Same(BoltCore._udpSocket, _socket)));
        }
    }
}
