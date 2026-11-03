// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Construction;
using global::Sons.Crafting.Structures;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>
        /// Reads a qualified host-owned relocation proxy and the native player's held recipe on the server game thread.
        /// Resolves only the requested prefab recipe, not an original relocated structure. No state is changed.
        /// Success is a current observation, not placement permission or evidence about a delayed request.
        /// </summary>
        public static bool TryReadBlueprintRelocationState(ServerSession session, ulong expectedSteamId,
            int requestedPrefabId, out BlueprintRelocationSnapshot snapshot,
            out WorldReadStatus status)
        {
            snapshot = null;
            if (expectedSteamId == 0 || requestedPrefabId < 0)
            {
                status = WorldReadStatus.InvalidQuery;
                return false;
            }

            try
            {
                var sourceStatus = CaptureRangeSource(session, expectedSteamId, out var source, out var connection);
                if (sourceStatus.HasValue)
                {
                    status = BlueprintRelocationSourceStatus(sourceStatus.Value);
                    return false;
                }

                status = CaptureBlueprintRelocation(source, connection, requestedPrefabId, out var before);
                if (status != WorldReadStatus.Success)
                    return false;
                status = CaptureBlueprintRelocation(source, connection, requestedPrefabId, out var after);
                if (status != WorldReadStatus.Success)
                    return false;
                if (before != after)
                {
                    status = WorldReadStatus.BindingChanged;
                    return false;
                }

                sourceStatus = CaptureRangeSource(session, expectedSteamId, out var currentSource, out _);
                if (sourceStatus.HasValue)
                {
                    status = BlueprintRelocationSourceStatus(sourceStatus.Value);
                    return false;
                }
                if (source != currentSource)
                {
                    status = WorldReadStatus.BindingChanged;
                    return false;
                }

                var state = before.ProxyRecipeId == -1 && !before.HeldRecipeId.HasValue
                    ? BlueprintRelocationState.AuthoritativeIdle
                    : before.ProxyRecipeId >= 0 && before.HeldRecipeId.HasValue
                        ? BlueprintRelocationState.RegisteredRecipe
                        : BlueprintRelocationState.Inconsistent;
                snapshot = new BlueprintRelocationSnapshot(state, source.PlayerNetworkId,
                    before.ProxyNetworkId, before.RequestedRecipeId, before.ProxyRecipeId, before.HeldRecipeId,
                    before.HeldRecipeId.HasValue
                        ? before.HeldRecipePointer == before.RequestedRecipePointer : (bool?)null);
                status = WorldReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = WorldReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        private static WorldReadStatus CaptureBlueprintRelocation(RangeBinding source,
            BoltConnection connection, int requestedPrefabId, out BlueprintRelocationBinding binding)
        {
            binding = default;
            if (connection is null || connection.WasCollected || connection.Pointer != source.ConnectionPointer)
                return WorldReadStatus.SessionUnavailable;
            var userData = connection.UserData;
            var player = userData is null || userData.WasCollected ? null : userData.TryCast<BoltEntity>();
            if (!IsLiveRangeEntity(player) || player.Pointer != source.PlayerPointer
                || player.networkId.PackedValue != source.PlayerNetworkId)
                return WorldReadStatus.PlayerStateUnavailable;
            var registeredPlayer = BoltNetwork.FindEntity(new Bolt.NetworkId(source.PlayerNetworkId));
            if (!IsLiveRangeEntity(registeredPlayer) || registeredPlayer.Pointer != player.Pointer
                || registeredPlayer.networkId.PackedValue != source.PlayerNetworkId)
                return WorldReadStatus.PlayerStateUnavailable;
            if (!player.TryGetComponent<HeldScrewStructure>(out var held)
                || held is null || held.WasCollected || !held)
                return WorldReadStatus.PlayerStateUnavailable;

            var proxy = held.Proxy;
            if (proxy is null || proxy.WasCollected || !proxy)
                return WorldReadStatus.BlueprintUnavailable;
            var proxyEntity = proxy.entity;
            if (!IsLiveRangeEntity(proxyEntity) || !proxyEntity.isOwner
                || proxyEntity.source is not null || proxyEntity.controller is not null)
                return WorldReadStatus.BlueprintUnavailable;
            var proxyId = proxyEntity.networkId.PackedValue;
            if (proxyId == 0)
                return WorldReadStatus.BlueprintUnavailable;
            var registeredProxy = BoltNetwork.FindEntity(new Bolt.NetworkId(proxyId));
            if (!IsLiveRangeEntity(registeredProxy) || registeredProxy.Pointer != proxyEntity.Pointer
                || registeredProxy.networkId.PackedValue != proxyId)
                return WorldReadStatus.BlueprintUnavailable;

            var proxyState = proxy.state;
            if (proxyState is null || proxyState.WasCollected)
                return WorldReadStatus.BlueprintUnavailable;
            var proxyPlayer = proxyState.Player;
            if (!IsLiveRangeEntity(proxyPlayer) || proxyPlayer.Pointer != source.PlayerPointer
                || proxyPlayer.networkId.PackedValue != source.PlayerNetworkId)
                return WorldReadStatus.BlueprintUnavailable;
            var reciprocalHeld = proxy._heldScrewStructure;
            if (reciprocalHeld is null || reciprocalHeld.WasCollected || !reciprocalHeld
                || reciprocalHeld.Pointer != held.Pointer)
                return WorldReadStatus.BlueprintUnavailable;

            var prefab = Bolt.PrefabDatabase.Find(new Bolt.PrefabId(requestedPrefabId));
            if (prefab is null || prefab.WasCollected || !prefab
                || !prefab.TryGetComponent<StructureCraftingNode>(out var node)
                || node is null || node.WasCollected || !node)
                return WorldReadStatus.BlueprintUnavailable;
            var requestedRecipe = node.Recipe;
            if (requestedRecipe is null || requestedRecipe.WasCollected || !requestedRecipe)
                return WorldReadStatus.BlueprintUnavailable;
            var requestedRecipeId = requestedRecipe.Id;
            if (requestedRecipeId < 0)
                return WorldReadStatus.BlueprintUnavailable;

            // Actual null is idle evidence; Unity fake-null and collected wrappers are unavailable.
            var heldRecipe = held.HeldStructureRecipe;
            if (heldRecipe is not null && (heldRecipe.WasCollected || !heldRecipe))
                return WorldReadStatus.BlueprintUnavailable;
            int? heldRecipeId = heldRecipe is null ? null : heldRecipe.Id;
            var proxyRecipeId = proxyState.StructureRecipeID;
            if (proxyRecipeId < -1 || heldRecipeId < 0
                || (proxyRecipeId >= 0 && heldRecipeId.HasValue && proxyRecipeId != heldRecipeId.Value))
                return WorldReadStatus.BlueprintUnavailable;

            binding = new BlueprintRelocationBinding(held.Pointer, proxy.Pointer, proxyEntity.Pointer,
                proxyId, proxyState.Pointer, prefab.Pointer, node.Pointer, requestedRecipe.Pointer,
                requestedRecipeId, proxyRecipeId, heldRecipe is null ? IntPtr.Zero : heldRecipe.Pointer, heldRecipeId);
            return WorldReadStatus.Success;
        }

        private static WorldReadStatus BlueprintRelocationSourceStatus(NativeRangeStatus status)
            => status switch
            {
                NativeRangeStatus.WrongThread => WorldReadStatus.WrongThread,
                NativeRangeStatus.NotServer => WorldReadStatus.NotServer,
                NativeRangeStatus.SessionUnavailable => WorldReadStatus.SessionUnavailable,
                NativeRangeStatus.TransportUnavailable => WorldReadStatus.SessionUnavailable,
                NativeRangeStatus.IdentityUnavailable => WorldReadStatus.PlayerStateUnavailable,
                NativeRangeStatus.PlayerUnavailable => WorldReadStatus.PlayerStateUnavailable,
                NativeRangeStatus.NativePlayerMismatch => WorldReadStatus.PlayerStateUnavailable,
                NativeRangeStatus.NonFinitePosition => WorldReadStatus.PlayerStateUnavailable,
                NativeRangeStatus.BindingChanged => WorldReadStatus.BindingChanged,
                NativeRangeStatus.Failed => WorldReadStatus.Failed,
                _ => WorldReadStatus.Unavailable
            };

        private readonly record struct BlueprintRelocationBinding(IntPtr HeldPointer, IntPtr ProxyPointer,
            IntPtr ProxyEntityPointer, ulong ProxyNetworkId, IntPtr ProxyStatePointer, IntPtr PrefabPointer,
            IntPtr NodePointer, IntPtr RequestedRecipePointer, int RequestedRecipeId, int ProxyRecipeId,
            IntPtr HeldRecipePointer, int? HeldRecipeId);
    }

    /// <summary>Qualified current relocation observations, not request verdicts or placement permissions.</summary>
    public enum BlueprintRelocationState
    {
        /// <summary>The host proxy reports recipe -1 and the native held recipe is actually null.</summary>
        AuthoritativeIdle,
        /// <summary>The host proxy and live held recipe have the same nonnegative recipe ID.</summary>
        RegisteredRecipe,
        /// <summary>Only one of the host proxy and native held recipe currently reports a recipe.</summary>
        Inconsistent
    }

    /// <summary>
    /// Managed copies of a qualified relocation read, without retained native objects.
    /// Two matching reads do not form an atomic simulation snapshot or establish a request's earlier state.
    /// </summary>
    public sealed class BlueprintRelocationSnapshot
    {
        internal BlueprintRelocationSnapshot(BlueprintRelocationState state,
            ulong playerNetworkId, ulong proxyNetworkId, int requestedRecipeId, int proxyRecipeId,
            int? heldRecipeId, bool? heldRecipeMatchesRequested)
        {
            State = state;
            PlayerNetworkId = playerNetworkId;
            ProxyNetworkId = proxyNetworkId;
            RequestedRecipeId = requestedRecipeId;
            ProxyRecipeId = proxyRecipeId;
            HeldRecipeId = heldRecipeId;
            HeldRecipeMatchesRequested = heldRecipeMatchesRequested;
        }

        public BlueprintRelocationState State { get; }
        public ulong PlayerNetworkId { get; }
        public ulong ProxyNetworkId { get; }
        /// <summary>Recipe ID resolved from the requested Bolt prefab, not the prefab ID itself.</summary>
        public int RequestedRecipeId { get; }
        public int ProxyRecipeId { get; }
        public bool HasHeldRecipe => HeldRecipeId.HasValue;
        /// <summary>Null means actual native null, never a destroyed or collected recipe.</summary>
        public int? HeldRecipeId { get; }
        /// <summary>Native recipe pointer equality, not ID equality; null when no held recipe exists.</summary>
        public bool? HeldRecipeMatchesRequested { get; }
    }
}
