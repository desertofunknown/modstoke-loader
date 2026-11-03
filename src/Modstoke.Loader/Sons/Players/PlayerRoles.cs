// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;
using TheForest.Utils;

namespace Modstoke.Sons
{
    // Native ownership and local input are different roles. Only migration-created server
    // carriers enter this registry; the host's local player and ordinary replicas are unchanged.
    internal static class PlayerRoles
    {
        private static readonly Dictionary<IntPtr, Carrier> Carriers = new();
        private static int _thread;
        private static int _attempted;
        private static bool _installed;
        [ThreadStatic] private static Creation _creation;
        [ThreadStatic] private static Retirement _retirement;

        internal static bool IsAvailable => _installed && Environment.CurrentManagedThreadId == _thread;

        internal static bool IsCurrentCarrier(BoltEntity entity)
        {
            if (!IsAvailable || entity == null) return false;
            foreach (var carrier in Carriers.Values)
                if (carrier.Entity.Pointer == entity.Pointer)
                    return carrier.Initialized && carrier.IsCurrent();
            return false;
        }

        internal static bool IsCurrentCarrierFor(BoltEntity entity, ulong oldNetworkId, BoltConnection source)
        {
            if (!IsAvailable || entity == null || source == null || source.WasCollected) return false;
            foreach (var carrier in Carriers.Values)
                if (carrier.Entity.Pointer == entity.Pointer)
                    return carrier.OldNetworkId == oldNetworkId && carrier.Peer.ConnectionPointer == source.Pointer
                        && carrier.Initialized && carrier.IsCurrent();
            return false;
        }

        internal static void Install(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.Exchange(ref _attempted, 1) != 0) return;
            if (support?.SafeForHarmonyPatches != true || !GameBuild.SupportsServerCore) return;
            HarmonyLib.Harmony harmony = null;
            try
            {
                _thread = Environment.CurrentManagedThreadId;
                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-player-roles");
                Patch(nameof(BoltPlayerSetup.Attached), nameof(BeforeAttached), null);
                Patch(nameof(BoltPlayerSetup.LocalAttached), nameof(BeforeLocalAttached), null);
                Patch(nameof(BoltPlayerSetup.Update), nameof(BeforeUpdate), null);
                Patch(nameof(BoltPlayerSetup.UpdateState), nameof(BeforeUpdate), null);
                Patch(nameof(BoltPlayerSetup.Detached), null, nameof(AfterDetached));
                var animationDetach = AccessTools.DeclaredMethod(typeof(CoopMecanimReplicator),
                    nameof(CoopMecanimReplicator.Detached), Type.EmptyTypes);
                if (animationDetach == null || animationDetach.IsStatic || animationDetach.ReturnType != typeof(void))
                    throw new MissingMethodException(typeof(CoopMecanimReplicator).FullName, "Detached");
                harmony.Patch(animationDetach, postfix: new HarmonyMethod(typeof(PlayerRoles),
                    nameof(AfterAnimationDetached)));
                _installed = true;

                void Patch(string method, string prefix, string postfix)
                {
                    var target = AccessTools.DeclaredMethod(typeof(BoltPlayerSetup), method, Type.EmptyTypes);
                    if (target == null || target.IsStatic || target.ReturnType != typeof(void))
                        throw new MissingMethodException(typeof(BoltPlayerSetup).FullName, method);
                    harmony.Patch(target,
                        prefix: prefix == null ? null : new HarmonyMethod(typeof(PlayerRoles), prefix)
                            { priority = Priority.First },
                        postfix: postfix == null ? null : new HarmonyMethod(typeof(PlayerRoles), postfix));
                }
            }
            catch (Exception error)
            {
                _installed = false;
                try { harmony?.UnpatchSelf(); } catch (Exception) { }
                warning?.Invoke("Player role migration hooks unavailable (" + error.GetType().Name + ").");
            }
        }

        internal static bool TryBeginRecreation(ControlPeer peer,
            BoltMigrationSnapshot snapshot, out Creation creation)
        {
            creation = null;
            if (!IsAvailable || _creation != null || peer?.ServerSession == null || !peer.IsCurrent
                || !peer.ServerSession.IsCurrent || !BoltNetwork.isServer || snapshot == null
                || snapshot.Plan.PrefabId != BoltPrefabs.PlayerNetwork.Value) return false;
            creation = _creation = new Creation(peer, snapshot.Plan.OldNetworkId);
            return true;
        }

        private static void BeforeAttached(BoltPlayerSetup __instance, bool __runOriginal)
        {
            var pending = _creation;
            if (!__runOriginal || pending == null || !IsAvailable) return;
            try
            {
                var entity = __instance?.entity;
                if (entity == null || entity.WasCollected || entity.prefabId != BoltPrefabs.PlayerNetwork) return;
                if (pending.Carrier != null || !pending.Peer.IsCurrent || !BoltNetwork.isServer
                    || !entity.isOwner || entity.source != null || !entity.isAttached
                    || entity.networkId.PackedValue == pending.OldNetworkId
                    || entity.gameObject?.Pointer == LocalPlayer.GameObject?.Pointer)
                {
                    pending.Failed = true;
                    return;
                }
                var attachment = entity._entity;
                var state = entity.GetState<IPlayerState>();
                if (attachment == null || attachment.WasCollected || state == null || state.WasCollected)
                { pending.Failed = true; return; }
                var carrier = new Carrier(__instance, entity, attachment, state, pending.Peer, pending.OldNetworkId);
                pending.Carrier = carrier;
                Carriers.Add(__instance.Pointer, carrier);
            }
            catch (Exception) { pending.Failed = true; }
        }

        private static bool BeforeLocalAttached(BoltPlayerSetup __instance, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            var pending = _creation;
            if (!IsAvailable || __instance == null) return true;
            if (!Carriers.TryGetValue(__instance.Pointer, out var carrier))
            {
                if (pending == null) return true;
                try
                {
                    var entity = __instance.entity;
                    if (entity == null || entity.prefabId != BoltPrefabs.PlayerNetwork
                        || entity.gameObject?.Pointer == LocalPlayer.GameObject?.Pointer) return true;
                }
                catch (Exception) { }
                pending.Failed = true;
                return false;
            }
            // Never replay either side of attachment after an interrupted native creation.
            if (pending == null || pending.Failed || !ReferenceEquals(pending.Carrier, carrier) || carrier.RedirectAttempted)
                return false;
            carrier.RedirectAttempted = true;
            try
            {
                if (!carrier.IsCurrent() || !pending.Peer.IsCurrent) { pending.Failed = true; return false; }
                // Keep the native Attached transform/registration prelude and its common tail.
                // LocalAttached reads the host's inventory and randomizes race: a remote character
                // must instead install the existing remote presentation callbacks.
                __instance.RemoteAttached();
                carrier.Initialized = carrier.IsCurrent() && pending.Peer.IsCurrent;
                pending.Failed |= !carrier.Initialized;
            }
            catch (Exception) { pending.Failed = true; }
            return false;
        }

        private static bool BeforeUpdate(BoltPlayerSetup __instance, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            // UpdateState reads LocalPlayer globals, not this character's controller. The server
            // simulation/bridge owns carrier publication, including while restoration is pending.
            return !IsAvailable || Carriers.Count == 0 || __instance == null
                || !Carriers.ContainsKey(__instance.Pointer);
        }

        private static void AfterDetached(BoltPlayerSetup __instance, bool __runOriginal)
        {
            if (!IsAvailable || __instance == null) return;
            if (__runOriginal) Carriers.Remove(__instance.Pointer);
            _retirement?.ObserveSetup(__instance, __runOriginal);
        }

        private static void AfterAnimationDetached(CoopMecanimReplicator __instance, bool __runOriginal)
        {
            if (IsAvailable && __instance != null) _retirement?.ObserveAnimation(__instance, __runOriginal);
        }

        internal static bool TryBeginRetirement(BoltEntity entity, out Retirement retirement)
        {
            retirement = null;
            if (!IsAvailable || _retirement != null || entity == null || entity.WasCollected || !entity
                || !entity.isAttached || !entity.isOwner || entity.prefabId != BoltPrefabs.PlayerNetwork
                || entity.Pointer != LocalPlayer.Entity?.Pointer
                || entity.gameObject?.Pointer != LocalPlayer.GameObject?.Pointer) return false;
            var setup = entity.GetComponent<BoltPlayerSetup>();
            if (setup == null || setup.WasCollected || !setup || setup.entity?.Pointer != entity.Pointer) return false;
            var captured = new Retirement(entity, setup);
            foreach (var animation in entity.GetComponentsInChildren<CoopMecanimReplicator>(true))
            {
                if (animation == null || animation.WasCollected || !animation) return false;
                var owner = animation.GetComponentInParent<BoltEntity>(true);
                if (owner == null || owner.Pointer != entity.Pointer) continue;
                if (animation._entity != null && animation._entity.Pointer != entity.Pointer) return false;
                captured.Animations.Add(animation.Pointer, false);
            }
            retirement = _retirement = captured;
            return true;
        }

        internal static void ResetForStart()
        {
            if (!IsAvailable) return;
            if (_creation != null) _creation.Failed = true;
            if (_retirement != null) _retirement.Failed = true;
            // Leave old carriers blocked until their native Detached callback runs. Clearing these
            // while shutdown callbacks are still pending could publish host data to an old carrier.
            foreach (var entry in new List<KeyValuePair<IntPtr, Carrier>>(Carriers))
            {
                try
                {
                    var entity = entry.Value.Entity;
                    if (entity.WasCollected || !entity || !entity.isAttached) Carriers.Remove(entry.Key);
                }
                catch (Exception) { }
            }
        }

        internal sealed class Creation : IDisposable
        {
            internal Creation(ControlPeer peer, ulong oldNetworkId)
            { Peer = peer; OldNetworkId = oldNetworkId; }
            internal readonly ControlPeer Peer;
            internal readonly ulong OldNetworkId;
            internal Carrier Carrier;
            internal bool Failed;

            internal bool Matches(BoltEntity entity) => !Failed && ReferenceEquals(_creation, this)
                && Peer.IsCurrent && Carrier?.Initialized == true && Carrier.IsCurrent()
                && entity != null && entity.Pointer == Carrier.Entity.Pointer;

            public void Dispose()
            {
                if (Environment.CurrentManagedThreadId == _thread && ReferenceEquals(_creation, this))
                    _creation = null;
            }
        }

        // Native CoopMecanimReplicator.Detached calls StopAllCoroutines. Observe completion
        // only inside our exact synchronous retirement; ordinary players allocate nothing here.
        internal sealed class Retirement : IDisposable
        {
            private readonly BoltEntity _entity;
            private readonly BoltPlayerSetup _setup;
            private bool _setupDetached;
            internal readonly Dictionary<IntPtr, bool> Animations = new();
            internal bool Failed;

            internal Retirement(BoltEntity entity, BoltPlayerSetup setup) { _entity = entity; _setup = setup; }

            internal void ObserveSetup(BoltPlayerSetup setup, bool ran)
            {
                if (setup.Pointer != _setup.Pointer) return;
                Failed |= !ran || _setupDetached;
                _setupDetached = ran;
            }

            internal void ObserveAnimation(CoopMecanimReplicator animation, bool ran)
            {
                if (!Animations.TryGetValue(animation.Pointer, out bool completed)) return;
                Failed |= !ran || completed;
                Animations[animation.Pointer] = ran;
            }

            internal bool IsComplete
            {
                get
                {
                    if (!IsAvailable || !ReferenceEquals(_retirement, this) || Failed || !_setupDetached
                        || _entity.WasCollected || !_entity || _entity.isAttached
                        || _entity.gameObject?.Pointer != LocalPlayer.GameObject?.Pointer) return false;
                    foreach (bool completed in Animations.Values) if (!completed) return false;
                    return true;
                }
            }

            public void Dispose()
            {
                if (Environment.CurrentManagedThreadId == _thread && ReferenceEquals(_retirement, this))
                    _retirement = null;
            }
        }

        internal sealed class Carrier
        {
            internal Carrier(BoltPlayerSetup setup, BoltEntity entity, Bolt.Entity attachment, IPlayerState state,
                ControlPeer peer, ulong oldNetworkId)
            {
                Setup = setup; Entity = entity; Attachment = attachment; State = state;
                Peer = peer; OldNetworkId = oldNetworkId; _networkState = state.TryCast<Bolt.NetworkState>();
                _networkId = entity.networkId.PackedValue;
            }
            internal readonly BoltPlayerSetup Setup;
            internal readonly BoltEntity Entity;
            internal readonly Bolt.Entity Attachment;
            internal readonly IPlayerState State;
            internal readonly ControlPeer Peer;
            internal readonly ulong OldNetworkId;
            private readonly Bolt.NetworkState _networkState;
            private readonly ulong _networkId;
            internal bool RedirectAttempted;
            internal bool Initialized;

            internal bool IsCurrent() => Peer.IsCurrent && Peer.ServerSession?.IsCurrent == true
                && BoltNetwork.isServer && !Setup.WasCollected && Setup && !Entity.WasCollected && Entity
                && !Attachment.WasCollected && !State.WasCollected && Entity.isAttached && Entity.isOwner
                && Attachment.IsAttached && Attachment.IsOwner && Entity.source == null && Attachment.Source == null
                && Entity._entity?.Pointer == Attachment.Pointer && Attachment.UnityObject?.Pointer == Entity.Pointer
                && Entity.gameObject?.Pointer != LocalPlayer.GameObject?.Pointer
                && Entity.networkId.PackedValue == _networkId && Attachment.NetworkId.PackedValue == _networkId
                && Entity.prefabId == BoltPrefabs.PlayerNetwork && Attachment.PrefabId == BoltPrefabs.PlayerNetwork
                && Attachment.Serializer?.Pointer == State.Pointer
                && _networkState != null && !_networkState.WasCollected && _networkState.Entity?.Pointer == Attachment.Pointer
                && (Attachment.Controller == null || Attachment.Controller.Pointer == Peer.ConnectionPointer)
                && Entity.controller?.Pointer == Attachment.Controller?.Pointer
                && Setup.entity?.Pointer == Entity.Pointer && Entity.GetState<IPlayerState>()?.Pointer == State.Pointer;
        }
    }
}
