// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using Il2CppInterop.Runtime.InteropTypes;
using TheForest.Utils;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace Modstoke.Sons
{
    /// <summary>
    /// Retained local-player lifecycle repair after kernel-confirmed retirement. Captures identities
    /// and chat, gates surviving native publishers, and repairs chat after RigBindings.TryRebind.
    /// Also owns a reversible retained core writer/physics pause at a tracked native action
    /// boundary, scoped input/damage protection and exact-actor writer restoration. Other action
    /// domains, equipment forwarding, aggregate readiness and state preservation remain caller-owned;
    /// this is not a complete migration adapter.
    /// </summary>
    public sealed partial class PlayerLifecycle
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly BoltEntity _original;
        private readonly Entity _attachment;
        private readonly IPlayerState _state;
        private readonly BoltConnection _server;
        private readonly BoltPlayerSetup _setup;
        private readonly List<Identity> _identities = new();
        private readonly List<CoopMecanimReplicator> _animations = new();
        private bool _retired;
        private bool _restoreAttempted;
        private bool _restored;
        private bool _busy;
        private bool _cancelled;
        private BoltMigrationSession _sourceSession;

        public GameObject Root { get; }
        public BoltEntity OriginalEntity => _original;
        public ulong OriginalNetworkId { get; }
        public bool CallbacksRestored => _restored;

        private PlayerLifecycle(BoltEntity original)
        {
            _original = original;
            _attachment = original._entity;
            _state = original.GetState<IPlayerState>();
            _server = BoltNetwork.server;
            _setup = original.GetComponent<BoltPlayerSetup>();
            Root = LocalPlayer.GameObject;
            OriginalNetworkId = original.networkId.PackedValue;
        }

        /// <summary>Read-only capture; does not subscribe, pause, clear input or modify a player.</summary>
        public static bool TryCapture(BoltEntity original, out PlayerLifecycle participant,
            out string detail)
        {
            participant = null;
            detail = "Retained lifecycle hooks and the player-role game thread are required.";
            if (!HooksAvailable || !PlayerRoles.IsAvailable) return false;
            try
            {
                if (!Alive(original) || !original.isAttached || !original.isOwner
                    || !Same(original, LocalPlayer.Entity) || !Same(original.gameObject, LocalPlayer.GameObject))
                    return false;
                var value = new PlayerLifecycle(original);
                value.CaptureIdentities();
                value.CaptureActionActors();
                if (!value.OriginalIntact() || !value.CaptureChat(out detail)) return false;
                participant = value;
                detail = "Retained identities and chat captured for lifecycle repair; no player writers were paused.";
                return true;
            }
            catch (Exception error)
            { detail = "Retained lifecycle capture rejected (" + error.GetType().Name + ")."; return false; }
        }

        /// <summary>
        /// SDK-facing retirement proof. The exact Source session must have reached Retired,
        /// ReferencesRepaired or Completed for this original identity. Those kernel phases follow
        /// successful native retirement, including the retained player's detach callbacks.
        /// Requires this handle's confirmed pause. Call immediately after retirement, before
        /// allowing another frame or rebinding caches.
        /// Already-rebound identities are also accepted for repair integration; this does not prove
        /// that intervening frames were quiescent. A receipt alone is never retirement authority.
        /// </summary>
        public bool TryObserveRetirement(BoltMigrationSession sourceSession, out string detail)
        {
            detail = "The exact current Source session must prove retirement of this captured player.";
            if (!OnThread || _busy || _retired || _cancelled) return false;
            _busy = true;
            try
            {
                if (!IsPaused || !SourceSessionProvesRetirement(sourceSession) || !RetiredIdentity() || !IdentitiesMatch()) return false;
                bool beforeRebind = sourceSession.Status == BoltMigrationStatus.Retired
                    && Same(LocalPlayer.Entity, _original) && Same(LocalPlayer.State, _state)
                    && Same(_setup._entity, _original);
                if (!beforeRebind && (sourceSession.NewNetworkId == 0
                    || !ReboundIdentity(BoltNetwork.FindEntity(new NetworkId(sourceSession.NewNetworkId)),
                        sourceSession.NewNetworkId))) return false;
                if (!NativeEquipmentPublishersAbsent() || !ChatRetired()) return false;
                RegisterRetainedPublishers(this);
                _sourceSession = sourceSession;
                _retired = true;
                detail = "Source-session native retirement confirmed; retained setup/Mecanim publishers are persistently gated.";
                return true;
            }
            catch (Exception error)
            { detail = "Source-session retirement observation rejected (" + error.GetType().Name + ")."; return false; }
            finally { _busy = false; }
        }

        private bool SourceSessionProvesRetirement(BoltMigrationSession session)
        {
            if (session == null || session.Role != BoltMigrationRole.Source
                || (_sourceSession != null && !ReferenceEquals(session, _sourceSession))) return false;
            var snapshot = session.Snapshot;
            var peer = session.Peer;
            var result = session.LastResult;
            if (snapshot == null || !snapshot.Plan.IsValid || snapshot.Plan.OldNetworkId != OriginalNetworkId
                || snapshot.Plan.PrefabId != BoltPrefabs.PlayerNetwork.Value
                || peer == null || !peer.IsCurrent || peer.ServerSession != null || !Live(_server)
                || peer.ConnectionPointer != _server.Pointer || peer.ConnectionId != _server.ConnectionId
                || !Same(ControlTransport.Resolve(peer), _server)
                || result?.MutationAttempted != true || result.Receipt == null
                || !result.Receipt.Matches(snapshot)) return false;
            // Status is maintained by the sealed kernel session, not supplied by the adapter.
            return result.Status switch
            {
                BoltMigrationStatus.Retired =>
                    result.Receipt.Kind == BoltMigrationReceiptKind.Retired
                    && result.Receipt.NewNetworkId == 0,
                BoltMigrationStatus.ReferencesRepaired or BoltMigrationStatus.Completed =>
                    result.Receipt.Kind == BoltMigrationReceiptKind.Repaired
                    && session.NewNetworkId != 0 && result.Receipt.NewNetworkId == session.NewNetworkId,
                _ => false
            };
        }

        /// <summary>
        /// Observe while the parent's exact native retirement scope is still alive and BEFORE
        /// RigBindings.TryRebind. This does not substitute for successful pre-detach quiescence.
        /// Keeps native setup/presentation publishers suppressed on the surviving source only.
        /// </summary>
        internal bool TryObserveRetirement(PlayerRoles.Retirement retirement, out string detail)
        {
            detail = "Exact completed native retirement and unchanged retained identities are required.";
            if (!OnThread || _busy || _retired || _cancelled || retirement == null) return false;
            _busy = true;
            try
            {
                if (!IsPaused || !retirement.IsComplete || !RetiredIdentity() || !IdentitiesMatch()
                    || !Same(LocalPlayer.Entity, _original) || !Same(LocalPlayer.State, _state)
                    || !NativeEquipmentPublishersAbsent() || !ChatRetired()) return false;
                RegisterRetainedPublishers(this);
                _retired = true;
                detail = "Native retirement observed; retained setup/Mecanim publishers are persistently gated.";
                return true;
            }
            catch (Exception error)
            { detail = "Retirement observation rejected (" + error.GetType().Name + ")."; return false; }
            finally { _busy = false; }
        }

        /// <summary>
        /// Call after RigBindings.TryRebind Applied. Equipment.TryRebindSourceSubscriptions and
        /// TryBindSource replace all ten native source publishers, including race; do not reinstall
        /// native handlers or replay LocalAttached/RemoteAttached here. A partial write is one-shot.
        /// </summary>
        public BoltMigrationMutationResult TryRestoreAfterRebind(BoltEntity replacement,
            ulong expectedNewNetworkId, out string detail)
        {
            detail = "Callback restoration requires observed retirement and an exact rebound rig.";
            if (!OnThread || _busy || !_retired || _restoreAttempted || _cancelled)
                return BoltMigrationMutationResult.Rejected;
            _busy = true;
            bool wrote = false;
            try
            {
                if (_sourceSession != null && (!SourceSessionProvesRetirement(_sourceSession)
                    || _sourceSession.NewNetworkId != expectedNewNetworkId))
                {
                    detail = "The observed source session no longer proves this exact replacement.";
                    return BoltMigrationMutationResult.Rejected;
                }
                if (!IsPaused || !ReboundIdentity(replacement, expectedNewNetworkId) || !NativeEquipmentPublishersAbsent())
                    return BoltMigrationMutationResult.Rejected;
                if (_state.OnknockBackTrigger != null)
                {
                    detail = "Original knockback handler reconstruction remains unqualified; existing carrier callbacks were untouched.";
                    return BoltMigrationMutationResult.Rejected;
                }
                if (!CanRestoreChat(expectedNewNetworkId)) return BoltMigrationMutationResult.Rejected;
                _restoreAttempted = true;
                RestoreChat(expectedNewNetworkId, ref wrote);
                if (!ReboundIdentity(replacement, expectedNewNetworkId) || !ChatRestored(expectedNewNetworkId))
                {
                    detail = "Chat repair attempted but the retained identities/readback changed; publisher gates remain held.";
                    return BoltMigrationMutationResult.Unconfirmed;
                }
                _restored = true;
                detail = "Chat repaired without lifecycle replay; native publishers stay gated. Equipment forwarding remains caller-owned.";
                return BoltMigrationMutationResult.Applied;
            }
            catch (Exception error)
            {
                detail = "Callback repair failed (" + error.GetType().Name + "); unexpected values were not overwritten or rolled back.";
                return wrote ? BoltMigrationMutationResult.Unconfirmed : BoltMigrationMutationResult.Rejected;
            }
            finally { _busy = false; }
        }

        /// <summary>
        /// Cancel an unused capture while its original attachment is intact; no snapshot is needed.
        /// No input/physics/enable-state writes are owned here. This is not a resume operation and
        /// never releases a retired source's persistent publisher gates.
        /// </summary>
        public BoltMigrationMutationResult TryCancelCapture(out string detail)
        {
            detail = "Capture cancellation requires its original intact attachment and game thread.";
            if (!OnThread || _busy) return BoltMigrationMutationResult.Rejected;
            _busy = true;
            try
            {
                if (!_retired && !_restoreAttempted && !_pauseAttempted
                    && !ReferenceEquals(_actionFence, this) && OriginalIntact())
                {
                    _cancelled = true;
                    detail = "Unused lifecycle capture cancelled; no native/input writes were made or undone.";
                    return BoltMigrationMutationResult.Unchanged;
                }
                return BoltMigrationMutationResult.Rejected;
            }
            catch (Exception error)
            { detail = "Capture cancellation rejected (" + error.GetType().Name + ")."; return BoltMigrationMutationResult.Rejected; }
            finally { _busy = false; }
        }

        private bool OnThread => Environment.CurrentManagedThreadId == _thread && HooksAvailable;
        private bool ClientRun() => OnThread && BoltNetwork.isRunning && BoltNetwork.isClient && !BoltNetwork.isServer
            && Live(_server) && Same(BoltNetwork.server, _server);
        private bool OriginalIntact() => ClientRun() && IdentitiesMatch() && Alive(_original) && Alive(_setup)
            && OriginalNetworkId != 0 && Live(_attachment) && Live(_state)
            && _original.isAttached && _original.isOwner && _original.prefabId == BoltPrefabs.PlayerNetwork
            && Same(_original.gameObject, Root) && _attachment.IsAttached && _attachment.IsOwner
            && _original.networkId.PackedValue == OriginalNetworkId && Same(_original._entity, _attachment)
            && Same(_attachment?.Serializer, _state) && Same(_setup._entity, _original)
            && Same(LocalPlayer.Entity, _original) && Same(LocalPlayer.State, _state)
            && Same(BoltNetwork.FindEntity(new NetworkId(OriginalNetworkId)), _original)
            && Same(BoltCore.FindEntity(new NetworkId(OriginalNetworkId)), _attachment);
        private bool RetiredIdentity() => ClientRun() && Alive(_original) && Live(_attachment)
            && !_original.isAttached && _original._entity == null && !_attachment.IsAttached
            && Same(_original.gameObject, Root) && BoltNetwork.FindEntity(new NetworkId(OriginalNetworkId)) == null
            && BoltCore.FindEntity(new NetworkId(OriginalNetworkId)) == null;
        private bool ReboundIdentity(BoltEntity replacement, ulong id)
        {
            if (!RetiredIdentity() || !IdentitiesMatch() || !Alive(replacement) || id == 0 || id == OriginalNetworkId
                || Same(replacement, _original) || Same(replacement.gameObject, Root) || !replacement.isAttached
                || replacement.isOwner || !replacement.hasControl || replacement.prefabId != BoltPrefabs.PlayerNetwork
                || replacement.networkId.PackedValue != id || !Same(replacement.source, _server)
                || !Same(LocalPlayer.Entity, replacement) || !Same(_setup._entity, replacement)
                || !Same(LocalPlayer.State, replacement.GetState<IPlayerState>())
                || !Same(BoltNetwork.FindEntity(new NetworkId(id)), replacement)
                || !Same(BoltCore.FindEntity(new NetworkId(id)), replacement._entity)) return false;
            foreach (var animation in _animations) if (!Same(animation._entity, replacement)) return false;
            return true;
        }

        private void CaptureIdentities()
        {
            _identities.Add(new Identity(() => LocalPlayer.GameObject));
            _identities.Add(new Identity(() => LocalPlayer._instance));
            _identities.Add(new Identity(() => LocalPlayer.MainCam));
            _identities.Add(new Identity(() => LocalPlayer.MainCamTr));
            _identities.Add(new Identity(() => LocalPlayer.FpCharacter));
            _identities.Add(new Identity(() => LocalPlayer.Rigidbody));
            _identities.Add(new Identity(() => LocalPlayer.Inventory));
            _identities.Add(new Identity(() => LocalPlayer.Inventory.InventoryProps));
            _identities.Add(new Identity(() => LocalPlayer.Stats));
            _identities.Add(new Identity(() => LocalPlayer.Stats.ArmourSystem));
            _identities.Add(new Identity(() => LocalPlayer.ClothingSystem));
            _identities.Add(new Identity(() => LocalPlayer.AnimControl));
            _identities.Add(new Identity(() => LocalPlayer.Animator));
            _identities.Add(new Identity(() => LocalPlayer.RaceSystem));
            _identities.Add(new Identity(() => _setup));
            _identities.Add(new Identity(() => _setup._inventory));
            _identities.Add(new Identity(() => _setup._inventoryProps));
            _identities.Add(new Identity(() => _setup._clothingSystem));
            _identities.Add(new Identity(() => _setup._armourSystem));
            _identities.Add(new Identity(() => _setup._raceSystem));
            foreach (var animation in Root.GetComponentsInChildren<CoopMecanimReplicator>(true))
            {
                if (!Alive(animation) || !Same(animation.GetComponentInParent<BoltEntity>(true), _original)) continue;
                _animations.Add(animation);
                _identities.Add(new Identity(() => animation));
            }
        }
        private bool IdentitiesMatch()
        {
            foreach (var identity in _identities) if (!identity.Matches()) return false;
            return Same(LocalPlayer.FpCharacter._rigidbody, LocalPlayer.Rigidbody)
                && Same(_setup._inventory, LocalPlayer.Inventory)
                && Same(_setup._inventoryProps, LocalPlayer.Inventory.InventoryProps)
                && Same(_setup._clothingSystem, LocalPlayer.ClothingSystem)
                && Same(_setup._armourSystem, LocalPlayer.Stats.ArmourSystem)
                && Same(_setup._raceSystem, LocalPlayer.RaceSystem);
        }
        private sealed class Identity
        {
            private readonly Func<UnityObject> _read;
            private readonly UnityObject _value;
            private readonly int _id;
            internal Identity(Func<UnityObject> read)
            {
                _read = read; _value = read();
                if (!Alive(_value)) throw new InvalidOperationException("Missing retained identity.");
                _id = _value.GetInstanceID();
            }
            internal bool Matches() => Alive(_value) && _value.GetInstanceID() == _id && Same(_read(), _value);
        }
        private static bool Live(Il2CppObjectBase value) => value != null && !value.WasCollected && value.Pointer != IntPtr.Zero;
        private static bool Alive(UnityObject value) => Live(value) && value;
        private static bool Same(Il2CppObjectBase left, Il2CppObjectBase right)
            => left == null ? right == null : Live(left) && Live(right) && left.Pointer == right.Pointer;
    }
}
