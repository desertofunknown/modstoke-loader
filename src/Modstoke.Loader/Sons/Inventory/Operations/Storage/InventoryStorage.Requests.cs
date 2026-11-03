// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Sons
{
    internal sealed partial class InventoryStorage
    {
        internal enum RequestDisposition { Unrelated, Rejected, Reserved }

        internal static bool IsLegacyWorldRequest(Bolt.Event request)
            => request != null && !request.WasCollected && (request.TryCast<DestroyPickUp>() != null
                || request.TryCast<DisablePickup>() != null || request.TryCast<DropItem>() != null
                || request.TryCast<ItemStorageAddItemEvent>() != null || request.TryCast<ItemStorageRemoveItemEvent>() != null);

        /// <summary>
        /// Bind the approved compound interaction profile and an actor-local authoritative camera
        /// reader. Drop clearance is a separate server policy. Native ingress must already be fenced.
        /// No hooks, capability advertisement or baseline approval are installed here.
        /// </summary>
        internal static bool TryBindRequests(PlayerBridge bridge,
            PlayerInventoryAuthority authority, CapsuleCollider body,
            PlayerCarrierProfile profile, ReadInteractionPose readInteractionPose,
            float maximumReach, float clearanceRadius, int obstructionLayers,
            out RequestRouter router, out string detail)
        {
            router = null;
            detail = "World requests require active exact server custody and an approved actor-local physics body.";
            if (bridge == null || authority == null || !bridge.IsServer || bridge.IsDisposed
                || !bridge.IsGameplayActive || !authority.TryReadGuardedInventoryReadiness(out detail)
                || !bridge.TryReadCurrentTarget(out var actor) || !Live(actor)
                || !Live(body) || !Same(body.GetComponentInParent<BoltEntity>(true), actor)
                || !PlayerCarrierProfile.ValidateProfile(profile, out detail)
                || profile.SourceNetworkId != bridge.Plan.OldNetworkId
                || profile.PrefabId != bridge.Plan.PrefabId || profile.StateTypeId != bridge.Plan.StateTypeId
                || readInteractionPose == null
                || !float.IsFinite(maximumReach) || maximumReach <= 0
                || !float.IsFinite(clearanceRadius) || clearanceRadius <= 0 || clearanceRadius > maximumReach
                || obstructionLayers == 0) return false;
            var cursor = Cursors.GetValue(authority, _ => new Cursor());
            if (cursor.Router != null || cursor.Active != null) return false;
            var candidate = new RequestRouter(bridge, authority, actor, body,
                profile, readInteractionPose, maximumReach, clearanceRadius, obstructionLayers);
            if (!candidate.Current() || !candidate.PrepareInteractionQuery(out detail))
            { candidate.Dispose(); return false; }
            cursor.Router = candidate;
            router = candidate;
            detail = "Exact world-request route bound; native ingress must call Route once before any vanilla effect.";
            return true;
        }

        /// <summary>
        /// Call for ALL actors, including vanilla/local actors, before their native world receiver.
        /// A held target is denied even when its native entity wrapper has already retired.
        /// This does not undo a vanilla client's earlier local pickup/addition.
        /// </summary>
        internal static bool RejectCompetingWorldRequest(Bolt.Event request, out string detail)
        {
            detail = null;
            if (request == null || request.WasCollected) return false;
            try
            {
                ulong id;
                int slot = -1;
                var pickup = request.TryCast<DestroyPickUp>();
                var disable = request.TryCast<DisablePickup>();
                var add = request.TryCast<ItemStorageAddItemEvent>();
                var remove = request.TryCast<ItemStorageRemoveItemEvent>();
                var drop = request.TryCast<DropItem>();
                if (pickup != null) id = pickup.GetPickUpEntityNetworkIdValue().PackedValue;
                else if (disable != null) id = disable.GetEntityNetworkIdValue().PackedValue;
                else if (add != null) { id = add.GetTargetNetworkIdValue().PackedValue; slot = add.StorageSlotIndex; }
                else if (remove != null) { id = remove.GetTargetNetworkIdValue().PackedValue; slot = remove.StorageSlotIndex; }
                else if (drop != null) id = drop.GetPreSpawnedNetworkIdValue().PackedValue;
                else return false;
                if (!IsWorldHeld(id, slot)) return false;
                detail = "Exact world object or storage slot is held by an unresolved inventory transfer.";
                return true;
            }
            catch (Exception error)
            {
                detail = "World custody could not be checked: " + error.Message;
                return true;
            }
        }

        internal sealed partial class RequestRouter : IDisposable
        {
            private readonly int _thread = Environment.CurrentManagedThreadId;
            private readonly int _load = SaveLifecycle.State.BeforeLoadCount;
            private readonly PlayerBridge _bridge;
            private readonly PlayerInventoryAuthority _authority;
            private readonly BoltEntity _actor;
            private readonly int _actorInstanceId;
            private readonly CapsuleCollider _body;
            private readonly int _bodyInstanceId;
            private readonly float _maximumReach, _clearanceRadius;
            private readonly int _obstructionLayers;
            private bool _disposed, _routing;

            internal RequestRouter(PlayerBridge bridge, PlayerInventoryAuthority authority,
                BoltEntity actor, CapsuleCollider body, PlayerCarrierProfile profile,
                ReadInteractionPose readInteractionPose, float maximumReach, float clearanceRadius, int obstructionLayers)
            {
                _bridge = bridge;
                _authority = authority;
                _actor = actor;
                _actorInstanceId = actor.GetInstanceID();
                _body = body;
                _bodyInstanceId = body.GetInstanceID();
                _interactionProfile = profile;
                _readInteractionPose = readInteractionPose;
                _maximumReach = maximumReach;
                _clearanceRadius = clearanceRadius;
                _obstructionLayers = obstructionLayers;
            }

            /// <summary>
            /// Call once at raw native dispatch, after exact peer selection and any prior veto.
            /// Reserved AND Rejected suppress the original event. No native event is retained.
            /// Legacy native messages do not establish pre-mutation custody. This route now
            /// only vetoes them; use RouteIntent from the authenticated datagram endpoint.
            /// </summary>
            internal RequestDisposition Route(Bolt.Event request, ulong serverItemKey, out string detail)
            {
                detail = "Unrelated native event.";
                if (request == null || request.WasCollected) return RequestDisposition.Unrelated;
                try
                {
                    var pickup = request.TryCast<DestroyPickUp>();
                    var drop = request.TryCast<DropItem>();
                    var add = request.TryCast<ItemStorageAddItemEvent>();
                    var remove = request.TryCast<ItemStorageRemoveItemEvent>();
                    var disable = request.TryCast<DisablePickup>();
                    if (pickup == null && drop == null && add == null && remove == null && disable == null)
                        return RequestDisposition.Unrelated;
                    detail = "Native world request is not authorized for this exact current peer and actor.";
                    if (!Current() || _routing || !Same(request.RaisedBy, ControlTransport.Resolve(_bridge.Peer)))
                        return RequestDisposition.Rejected;
                    if (RejectCompetingWorldRequest(request, out var heldDetail))
                    { detail = heldDetail; return RequestDisposition.Rejected; }
                    detail = "Native world cleanup cannot authorize another debit or grant; an earlier typed intent is required.";
                    return RequestDisposition.Rejected;
                }
                catch (Exception error)
                {
                    detail = "Native world intent failed without permission to resume vanilla: " + error.Message;
                    return RequestDisposition.Rejected;
                }
            }

            internal bool Current()
                => Environment.CurrentManagedThreadId == _thread && !_disposed
                    && _load == SaveLifecycle.State.BeforeLoadCount
                    && BoltNetwork.isRunning && BoltNetwork.isServer && !_bridge.IsDisposed
                    && _bridge.IsServer && _bridge.IsGameplayActive && _bridge.Peer.IsCurrent
                    && !_authority.IsDisposed && !_authority.IsFaulted && _authority.HasAcceptedBaseline
                    && _authority.MutationEnforcement != null && _authority.MutationEnforcement.IsArmed
                    && !_authority.MutationEnforcement.IsDisposed && !_authority.MutationEnforcement.IsFaulted
                    && _authority.MutationEnforcement.Baseline.Plan == _bridge.Plan
                    && _bridge.TryReadCurrentTarget(out var actor) && Same(actor, _actor) && Live(actor)
                    && actor.isAttached && actor.isOwner && actor.GetInstanceID() == _actorInstanceId
                    && Live(_body) && _body.GetInstanceID() == _bodyInstanceId
                    && Same(_body.GetComponentInParent<BoltEntity>(true), actor)
                    && ControlTransport.Resolve(_bridge.Peer) != null;

            public void Dispose()
            {
                if (Environment.CurrentManagedThreadId != _thread)
                    throw new InvalidOperationException("Release native world routing on its game thread.");
                if (_disposed) return;
                _disposed = true;
                ReleaseInteractionQuery();
                if (Cursors.TryGetValue(_authority, out var cursor) && ReferenceEquals(cursor.Router, this))
                {
                    cursor.Active?.HoldUnconfirmed("Native world routing retired with unresolved custody.");
                    cursor.Router = null;
                }
            }
        }
    }
}
