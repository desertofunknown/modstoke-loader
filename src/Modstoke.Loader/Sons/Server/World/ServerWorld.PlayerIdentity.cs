// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        private static readonly Dictionary<ServerSession, PlayerIdentityMigration> PlayerIdentityReservations = new();
        private static readonly Dictionary<ServerSession, PlayerIdentityMigration> MigratedPlayerIdentities = new();
        private static bool _playerIdentityHooksInstalled;
        private static bool _changingPlayerIdentity;
        private static int _playerIdentityInstallAttempted;

        /// <summary>
        /// Reserve the original client's character identity before retiring its attachment. This is
        /// only the server-side identity participant, not a player preservation/input adapter. It does
        /// not transfer ownership, select enrollment, stop simulation, or alter the local rig.
        /// </summary>
        public static bool TryPreparePlayerIdentityMigration(ServerSession playerSession,
            BoltMigrationSession migration, out PlayerIdentityMigration identity,
            out string detail)
            => PreparePlayerIdentityMigration(playerSession, migration, false, out identity, out detail);

        private static bool PreparePlayerIdentityMigration(ServerSession playerSession,
            BoltMigrationSession migration, bool preparing,
            out PlayerIdentityMigration identity, out string detail)
        {
            identity = null;
            detail = "A prepared client-to-server migration and the original player attachment are required.";
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref _ownerThreadId) || _changingPlayerIdentity)
                return false;
            _changingPlayerIdentity = true;
            try
            {
                if (!_playerIdentityHooksInstalled || CheckContext() != WorldReadStatus.Success
                    || playerSession == null || migration == null
                    || migration.Role != BoltMigrationRole.Destination
                    || (migration.Status != BoltMigrationStatus.Prepared
                        && !(preparing && migration.Status == BoltMigrationStatus.Preparing))
                    || migration.Snapshot == null || !ReferenceEquals(migration.Peer.ServerSession, playerSession)
                    || !migration.Peer.IsCurrent)
                    return false;
                if (MigratedPlayerIdentities.TryGetValue(playerSession, out var previous)
                    && !TryReleaseIdentityAfterNativeRespawn(previous,
                        ServerSessions.ResolveCurrent(playerSession))) return false;
                if (PlayerIdentityReservations.TryGetValue(playerSession, out var existing))
                {
                    if (!ReferenceEquals(existing.Migration, migration) || existing.Expired || existing.Attempted
                        || !TryValidatePlayerBinding(existing.Original, out _)) return false;
                    identity = existing;
                    detail = "The exact player identity is already reserved for this migration.";
                    return true;
                }
                if (!TryCapturePlayerBinding(playerSession, out var original, out _)
                    || original.NetworkId != migration.Snapshot.Plan.OldNetworkId
                    || !ReferenceEquals(original.SourceSession, playerSession)
                    || original.Player.prefabId != BoltPrefabs.PlayerNetwork)
                    return false;
                var state = original.Player.GetState<IPlayerState>();
                var connection = ControlTransport.Resolve(migration.Peer);
                if (state == null || state.WasCollected || connection == null || connection.WasCollected
                    || !MatchesPlayerAttachment(original, original.Player)
                    || !MatchesIdentityUserData(connection, original.Player, false))
                    return false;

                identity = new PlayerIdentityMigration(playerSession, migration, original);
                PlayerIdentityReservations.Add(playerSession, identity);
                detail = "Player identity reserved; full preservation, input handoff and all-peer repair are still required.";
                return true;
            }
            catch (Exception error)
            {
                detail = "Player identity preparation failed (" + error.GetType().Name + ").";
                return false;
            }
            finally { _changingPlayerIdentity = false; }
        }

        /// <summary>
        /// Publish identity only after the destination completed all-peer reference repair and the
        /// original client was assigned control of the server-owned replacement. Repairs the
        /// connection's native UserData reference used by game validators. This does not establish
        /// that the player's input, camera, inventory or local component lifecycle was preserved.
        /// A partial publication is Unconfirmed and is never replayed or silently rolled back.
        /// </summary>
        public static BoltMigrationMutationResult CommitPlayerIdentityMigration(
            PlayerIdentityMigration identity, BoltMigrationBarrier barrier, out string detail)
        {
            detail = "Use the original server thread and a completed, controlled player migration.";
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref _ownerThreadId) || _changingPlayerIdentity)
                return identity?.Attempted == true ? BoltMigrationMutationResult.Unconfirmed
                    : BoltMigrationMutationResult.Rejected;
            _changingPlayerIdentity = true;
            try
            {
                if (identity == null || identity.Expired || !_playerIdentityHooksInstalled
                    || CheckContext() != WorldReadStatus.Success)
                    return BoltMigrationMutationResult.Rejected;
                if (identity.Attempted)
                {
                    detail = "An attempted identity publication is not replayed.";
                    return ResolveMigratedPlayer(identity) != null
                        ? BoltMigrationMutationResult.Unchanged
                        : BoltMigrationMutationResult.Unconfirmed;
                }
                if (!PlayerIdentityReservations.TryGetValue(identity.PlayerSession, out var reserved)
                    || !ReferenceEquals(reserved, identity)
                    || !identity.Migration.TryReadDestinationForIdentity(identity.PlayerSession, barrier, out var replacement)
                    || !IsControlledServerPlayer(identity, replacement, out var connection)
                    || !MatchesIdentityUserData(connection, identity.Original.Player, true))
                    return BoltMigrationMutationResult.Rejected;
                var original = identity.Original;
                if (original.Player is null || original.Player.WasCollected || original.Attachment.WasCollected
                    || original.Attachment.IsAttached || original.Player.isAttached
                    || BoltNetwork.FindEntity(new Bolt.NetworkId(original.NetworkId)) != null)
                    return BoltMigrationMutationResult.Rejected;

                var attachment = replacement._entity;
                if (attachment == null || attachment.WasCollected || !attachment.IsAttached)
                    return BoltMigrationMutationResult.Rejected;
                identity.Replacement = new PlayerHandle(replacement, attachment, null, false,
                    replacement.networkId.PackedValue, identity.PlayerSession);

                // Reserve the lookup before writing native UserData. An interrupted publication
                // must resolve to unavailable, never to another player or a recycled attachment.
                identity.Attempted = true;
                MigratedPlayerIdentities.Add(identity.PlayerSession, identity);
                connection.UserData = replacement;
                if (!MatchesIdentityUserData(connection, replacement, false)
                    || !identity.Migration.TryReadDestinationForIdentity(identity.PlayerSession, barrier, out var current)
                    || current.Pointer != replacement.Pointer || !IsControlledServerPlayer(identity, current, out _)
                    || !MatchesPlayerAttachment(identity.Replacement, current))
                {
                    detail = "Native player identity publication has unconfirmed effects; automatic replay is disabled.";
                    return BoltMigrationMutationResult.Unconfirmed;
                }
                identity.Committed = true;
                original.Retire();
                PlayerIdentityReservations.Remove(identity.PlayerSession);
                identity.Migration = null;
                detail = "Character lookup and native validator identity now refer to the exact server-owned replacement.";
                return BoltMigrationMutationResult.Applied;
            }
            catch (Exception error)
            {
                detail = "Player identity publication failed (" + error.GetType().Name + ").";
                return identity?.Attempted == true ? BoltMigrationMutationResult.Unconfirmed
                    : BoltMigrationMutationResult.Rejected;
            }
            finally { _changingPlayerIdentity = false; }
        }

        /// <summary>Release identity preparation only while the original attachment remains intact.</summary>
        public static bool TryCancelPlayerIdentityMigration(PlayerIdentityMigration identity)
        {
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref _ownerThreadId) || _changingPlayerIdentity)
                return false;
            _changingPlayerIdentity = true;
            try
            {
                if (identity == null || identity.Attempted || identity.Expired
                    || CheckContext() != WorldReadStatus.Success
                    || !PlayerIdentityReservations.TryGetValue(identity.PlayerSession, out var current)
                    || !ReferenceEquals(identity, current)
                    || !TryValidatePlayerBinding(identity.Original, out _)) return false;
                PlayerIdentityReservations.Remove(identity.PlayerSession);
                identity.Expire();
                return true;
            }
            catch (Exception) { return false; }
            finally { _changingPlayerIdentity = false; }
        }

        internal static void InstallPlayerIdentityHooks(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.Exchange(ref _playerIdentityInstallAttempted, 1) != 0) return;
            if (support?.SafeForHarmonyPatches != true || !GameBuild.SupportsServerCore
                || !ServerSessions.HooksInstalled) return;
            try
            {
                var resolve = AccessTools.Method(typeof(MultiplayerUtilities),
                    nameof(MultiplayerUtilities.GetEntityFromBoltConnectionForServer), new[] { typeof(BoltConnection) });
                if (resolve == null || !resolve.IsStatic || resolve.ReturnType != typeof(BoltEntity))
                    throw new MissingMethodException();
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-player-identity");
                harmony.Patch(resolve, prefix: new HarmonyMethod(typeof(ServerWorld),
                    nameof(BeforeResolvePlayerIdentity)) { priority = Priority.First });
                _playerIdentityHooksInstalled = true;
            }
            catch (Exception error)
            {
                warning?.Invoke("Optional player identity migration hook unavailable (" + error.GetType().Name + ").");
            }
        }

        private static bool BeforeResolvePlayerIdentity(BoltConnection __0, ref BoltEntity __result, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref _ownerThreadId)
                || !_playerIdentityHooksInstalled || MigratedPlayerIdentities.Count == 0) return true;
            bool handled = false;
            try
            {
                var session = ServerSessions.ResolveObserved(__0);
                if (session == null || !MigratedPlayerIdentities.TryGetValue(session, out var identity)) return true;
                handled = true;
                if (TryReleaseIdentityAfterNativeRespawn(identity, __0)) return true;
                __result = ResolveMigratedPlayer(identity);
                return false;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                if (handled) __result = null;
                return !handled;
            }
        }

        private static BoltEntity ResolveMigratedPlayer(PlayerIdentityMigration identity)
        {
            if (!identity.Committed || identity.Expired || CheckContext() != WorldReadStatus.Success
                || !MigratedPlayerIdentities.TryGetValue(identity.PlayerSession, out var registered)
                || !ReferenceEquals(registered, identity) || identity.Replacement == null
                || !MatchesPlayerAttachment(identity.Replacement, identity.Replacement.Player)
                || !IsControlledServerPlayer(identity, identity.Replacement.Player, out var connection)
                || !MatchesIdentityUserData(connection, identity.Replacement.Player, false)) return null;
            return identity.Replacement.Player;
        }

        private static bool TryReleaseIdentityAfterNativeRespawn(PlayerIdentityMigration identity,
            BoltConnection connection)
        {
            // A later native spawn belongs to a new attachment, not to the migration. Accept it
            // only when native UserData, source and both registries agree and the old ID is gone.
            // Never clear a mapping just because a packet, callback or transient read is missing.
            if (!identity.Committed || identity.Expired || identity.Replacement == null
                || !identity.PlayerSession.IsCurrent || CheckContext() != WorldReadStatus.Success
                || connection == null || connection.WasCollected
                || ServerSessions.ResolveCurrent(identity.PlayerSession)?.Pointer != connection.Pointer
                || !MigratedPlayerIdentities.TryGetValue(identity.PlayerSession, out var current)
                || !ReferenceEquals(current, identity)) return false;
            var previous = identity.Replacement;
            if (previous.Attachment == null || previous.Attachment.WasCollected || previous.Attachment.IsAttached
                || BoltNetwork.FindEntity(new Bolt.NetworkId(previous.NetworkId)) != null
                || BoltCore.FindEntity(new Bolt.NetworkId(previous.NetworkId)) != null) return false;
            var nativeData = connection.UserData;
            var player = nativeData?.TryCast<BoltEntity>();
            if (player == null || player.WasCollected || !player || !MatchesPlayerSource(player, connection)
                || player.prefabId != BoltPrefabs.PlayerNetwork || player.isOwner
                || player.networkId.PackedValue == 0 || player.networkId.PackedValue == previous.NetworkId
                || player.networkId.PackedValue == identity.OldNetworkId
                || BoltNetwork.FindEntity(player.networkId)?.Pointer != player.Pointer) return false;
            var attachment = player._entity;
            if (attachment == null || attachment.WasCollected || !attachment.IsAttached || attachment.IsOwner
                || attachment.UnityObject?.Pointer != player.Pointer
                || attachment.NetworkId.PackedValue != player.networkId.PackedValue
                || attachment.Source?.Pointer != connection.Pointer
                || BoltCore.FindEntity(player.networkId)?.Pointer != attachment.Pointer) return false;
            var state = player.GetState<IPlayerState>();
            if (state == null || state.WasCollected || attachment.Serializer?.Pointer != state.Pointer) return false;
            MigratedPlayerIdentities.Remove(identity.PlayerSession);
            identity.Expire();
            PlayerBridge.RetirePeer(identity.Peer);
            return true;
        }

        internal static bool IsCurrentMigratedPlayerBinding(PlayerHandle binding,
            ControlPeer peer)
        {
            if (binding == null || binding.IsRetired || peer?.ServerSession == null
                || !ReferenceEquals(binding.PlayerSession, peer.ServerSession)
                || !MigratedPlayerIdentities.TryGetValue(peer.ServerSession, out var identity)
                || !ReferenceEquals(identity.Peer, peer)) return false;
            var current = ResolveMigratedPlayer(identity);
            return current != null && current.Pointer == binding.Player.Pointer
                && MatchesPlayerAttachment(binding, current);
        }

        private static bool IsControlledServerPlayer(PlayerIdentityMigration identity,
            BoltEntity player, out BoltConnection connection)
        {
            connection = ControlTransport.Resolve(identity.Peer);
            if (connection == null || connection.WasCollected || !identity.PlayerSession.IsCurrent
                || player == null || player.WasCollected || !player || !player.isAttached || !player.isOwner
                || player.source != null || player.prefabId != BoltPrefabs.PlayerNetwork
                || player.networkId.PackedValue != identity.ExpectedNewNetworkId) return false;
            var controller = player.controller;
            var state = player.GetState<IPlayerState>();
            return controller != null && !controller.WasCollected && controller.Pointer == connection.Pointer
                && controller.ConnectionId == connection.ConnectionId && state != null && !state.WasCollected
                && BoltNetwork.FindEntity(player.networkId)?.Pointer == player.Pointer;
        }

        private static bool MatchesIdentityUserData(BoltConnection connection, BoltEntity expected, bool allowEmpty)
        {
            if (connection == null || connection.WasCollected || expected is null || expected.WasCollected) return false;
            var data = connection.UserData;
            return data == null ? allowEmpty : !data.WasCollected && data.Pointer == expected.Pointer;
        }

        private static bool MatchesPlayerSession(BoltEntity player, BoltConnection connection,
            ServerSession session)
        {
            if (session == null || !session.IsCurrent || connection == null || connection.WasCollected
                || !ReferenceEquals(ServerSessions.ResolveObserved(connection), session)) return false;
            if (!MigratedPlayerIdentities.TryGetValue(session, out var identity))
                return MatchesPlayerSource(player, connection);
            var mapped = ResolveMigratedPlayer(identity);
            return mapped != null && player != null && !player.WasCollected && player.Pointer == mapped.Pointer;
        }

        internal static bool PreparePlayerIdentityForSession(BoltMigrationSession migration,
            out string detail)
        {
            detail = "This migration does not replace a remote player's identity.";
            if (!RequiresPlayerIdentity(migration)) return true;
            return PreparePlayerIdentityMigration(migration.Peer.ServerSession, migration, true, out _, out detail);
        }

        internal static bool HasPreparedPlayerIdentity(BoltMigrationSession migration)
        {
            if (!RequiresPlayerIdentity(migration)) return true;
            return PlayerIdentityReservations.TryGetValue(migration.Peer.ServerSession, out var identity)
                && !identity.Expired && !identity.Attempted && ReferenceEquals(identity.Migration, migration)
                && ReferenceEquals(identity.Peer, migration.Peer);
        }

        internal static bool CancelPreparedPlayerIdentity(BoltMigrationSession migration)
        {
            if (!RequiresPlayerIdentity(migration)) return true;
            var session = migration.Peer.ServerSession;
            if (!PlayerIdentityReservations.TryGetValue(session, out var identity))
                return !MigratedPlayerIdentities.ContainsKey(session);
            return ReferenceEquals(identity.Migration, migration) && TryCancelPlayerIdentityMigration(identity);
        }

        private static bool RequiresPlayerIdentity(BoltMigrationSession migration)
            => migration?.Role == BoltMigrationRole.Destination
                && migration.Peer.ServerSession != null
                && migration.Snapshot?.Plan.PrefabId == BoltPrefabs.PlayerNetwork.Value;

        internal static bool CommitPreparedPlayerIdentity(BoltMigrationSession migration,
            BoltMigrationBarrier barrier, out string detail)
        {
            detail = "No player identity participant was reserved for this migration.";
            if (!RequiresPlayerIdentity(migration)) return true;
            var session = migration.Peer.ServerSession;
            if (!PlayerIdentityReservations.TryGetValue(session, out var identity)
                || !ReferenceEquals(identity.Migration, migration)) return false;
            var effect = CommitPlayerIdentityMigration(identity, barrier, out detail);
            return effect is BoltMigrationMutationResult.Applied or BoltMigrationMutationResult.Unchanged;
        }

        internal static void ForgetPlayerIdentity(ServerSession session)
        {
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref _ownerThreadId)) return;
            if (PlayerIdentityReservations.Remove(session, out var pending)) pending.Expire();
            if (MigratedPlayerIdentities.Remove(session, out var migrated)) migrated.Expire();
        }

        internal static void ResetPlayerIdentities()
        {
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref _ownerThreadId)) return;
            foreach (var identity in PlayerIdentityReservations.Values) identity.Expire();
            foreach (var identity in MigratedPlayerIdentities.Values) identity.Expire();
            PlayerIdentityReservations.Clear();
            MigratedPlayerIdentities.Clear();
        }
    }

    /// <summary>
    /// One explicitly reserved remote-player identity transition. This is not an authenticated
    /// input channel or a complete player migration adapter. No native objects are exposed.
    /// </summary>
    public sealed class PlayerIdentityMigration
    {
        internal PlayerIdentityMigration(ServerSession session,
            BoltMigrationSession migration, PlayerHandle original)
        { PlayerSession = session; Migration = migration; Peer = migration.Peer; Original = original; }

        public ServerSession PlayerSession { get; }
        public ulong OldNetworkId => Original.NetworkId;
        public ulong NewNetworkId => Committed && !Expired ? Replacement.NetworkId : 0;
        internal BoltMigrationSession Migration;
        internal ControlPeer Peer { get; }
        internal ulong ExpectedNewNetworkId => Replacement?.NetworkId ?? Migration?.NewNetworkId ?? 0;
        internal PlayerHandle Original { get; }
        internal PlayerHandle Replacement;
        internal bool Attempted;
        internal bool Committed;
        internal bool Expired;
        internal void Expire() { Expired = true; Original.Retire(); Replacement?.Retire(); Migration = null; }
    }
}
