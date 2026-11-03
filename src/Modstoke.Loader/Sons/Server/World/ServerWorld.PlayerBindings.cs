// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Multiplayer;
using TheForest.Utils;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>
        /// Captures the current native player attachment for an exact remote session.
        /// Retain the handle and read through TryReadPlayerBinding; a network ID alone is not its lifetime.
        /// </summary>
        public static bool TryCapturePlayerBinding(ServerSession session,
            out PlayerHandle binding, out WorldReadStatus status)
        {
            binding = null;
            try
            {
                status = CheckContext();
                if (status != WorldReadStatus.Success)
                    return false;
                if (!ServerSessions.HooksInstalled)
                {
                    status = WorldReadStatus.Unavailable;
                    return false;
                }
                var connection = ServerSessions.ResolveCurrent(session);
                if (connection == null)
                {
                    status = WorldReadStatus.SessionUnavailable;
                    return false;
                }
                var player = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(connection);
                return CapturePlayerBinding(player, connection, session, false, out binding, out status);
            }
            catch (Exception error)
            {
                status = WorldReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        /// <summary>
        /// Captures the actual listen host's LocalPlayer.Entity. Dedicated servers have no local player.
        /// Does not infer a host actor from ownership, a Steam ID, or a missing remote connection.
        /// </summary>
        public static bool TryCaptureLocalPlayerBinding(out PlayerHandle binding,
            out WorldReadStatus status)
        {
            binding = null;
            try
            {
                status = CheckContext();
                if (status != WorldReadStatus.Success)
                    return false;
                if (GameServerManager.IsDedicatedServer)
                {
                    status = WorldReadStatus.EntityUnavailable;
                    return false;
                }
                return CapturePlayerBinding(LocalPlayer.Entity, null, null, true, out binding, out status);
            }
            catch (Exception error)
            {
                status = WorldReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        /// <summary>
        /// Reads only the captured player attachment. Proven loss or replacement retires the handle and returns
        /// BindingChanged; it cannot bind a later attachment, even if the component or network ID is reused.
        /// </summary>
        /// <remarks>Other failures leave the handle for a later read. Health and dying/respawn transitions
        /// within one attachment remain separate observations.</remarks>
        public static bool TryReadPlayerBinding(PlayerHandle binding,
            out EntitySnapshot snapshot, out WorldReadStatus status)
            => TryReadPlayerBindingCore(binding, true, out snapshot, out status);

        /// <summary>Validates an exact retained player attachment on the server game thread without allocating an entity snapshot.</summary>
        /// <remarks>Performs the same native reads and retirement checks as TryReadPlayerBinding. Success is a current observation,
        /// not permission, pose or health authority; the attachment can change after the call.</remarks>
        public static bool TryValidatePlayerBinding(PlayerHandle binding, out WorldReadStatus status)
            => TryReadPlayerBindingCore(binding, false, out _, out status);

        private static bool TryReadPlayerBindingCore(PlayerHandle binding, bool includeSnapshot,
            out EntitySnapshot snapshot, out WorldReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckContext();
                if (status != WorldReadStatus.Success)
                {
                    RetireAfterBoltStop(binding, status);
                    return false;
                }
                if (binding == null || binding.IsRetired)
                {
                    status = WorldReadStatus.BindingChanged;
                    return false;
                }

                BoltConnection connection = null;
                BoltEntity player;
                if (binding.IsLocalPlayer)
                {
                    if (GameServerManager.IsDedicatedServer)
                        return RetirePlayerBinding(binding, out status);
                    player = LocalPlayer.Entity;
                }
                else
                {
                    if (!ServerSessions.HooksInstalled)
                        return RejectPlayerBinding(binding, WorldReadStatus.Unavailable, out status);
                    connection = ServerSessions.ResolveCurrent(binding.PlayerSession);
                    if (connection == null)
                        return RejectPlayerBinding(binding, WorldReadStatus.SessionUnavailable, out status);
                    player = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(connection);
                }

                if (!MatchesPlayerAttachment(binding, player))
                    return RejectPlayerAttachment(binding, player, out status);
                if (!binding.IsLocalPlayer && !MatchesPlayerSession(player, connection, binding.PlayerSession))
                    return RejectPlayerSession(binding, out status);
                if (!TryCapture(player, binding.NetworkId, null, out var captured)
                    || (binding.IsLocalPlayer && (!captured.IsServerOwned || captured.SourceConnectionId.HasValue)))
                    return RetirePlayerBinding(binding, out status);
                var entitySnapshot = includeSnapshot ? captured.ToSnapshot() : null;
                if (!ReferenceEquals(captured.SourceSession, binding.SourceSession))
                {
                    // Another source session is an ownership change; an unresolved one is lost tracking.
                    return captured.SourceSession != null ? RetirePlayerBinding(binding, out status)
                        : RejectPlayerBinding(binding, WorldReadStatus.SessionUnavailable, out status);
                }
                // Resolving the source can retire sessions, so check the attachment and session again.
                if (!MatchesPlayerAttachment(binding, player))
                    return RejectPlayerAttachment(binding, player, out status);
                if (!binding.IsLocalPlayer && !MatchesPlayerSession(player, connection, binding.PlayerSession))
                    return RejectPlayerSession(binding, out status);

                snapshot = entitySnapshot;
                status = WorldReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                RetireEndedPlayerBinding(binding);
                status = WorldReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        internal static bool IsLocalPlayerBindingCurrent(PlayerHandle binding)
        {
            try
            {
                var status = CheckContext();
                if (status != WorldReadStatus.Success)
                {
                    RetireAfterBoltStop(binding, status);
                    return false;
                }
                if (binding == null || binding.IsRetired || !binding.IsLocalPlayer || binding.SourceSession != null)
                    return false;
                if (GameServerManager.IsDedicatedServer)
                    return RetirePlayerBinding(binding, out _);

                // Host identity needs the attachment and ownership, not a pose snapshot
                // or the remote controller's connection history on every availability poll.
                var player = LocalPlayer.Entity;
                if (!MatchesPlayerAttachment(binding, player))
                    return RejectPlayerAttachment(binding, player, out _);
                if (!player.isOwner || player.source != null)
                    return RetirePlayerBinding(binding, out _);
                var transform = player.transform;
                if (transform is null || transform.WasCollected || !transform)
                    return RetirePlayerBinding(binding, out _);
                if (!MatchesPlayerAttachment(binding, player))
                    return RejectPlayerAttachment(binding, player, out _);
                return true;
            }
            catch (Exception error)
            {
                RetireEndedPlayerBinding(binding);
                ReportFailure(error);
                return false;
            }
        }

        private static bool CapturePlayerBinding(BoltEntity player, BoltConnection connection,
            ServerSession session, bool local, out PlayerHandle binding,
            out WorldReadStatus status)
        {
            binding = null;
            status = WorldReadStatus.EntityUnavailable;
            if (player is null || player.WasCollected || !player || !player.isAttached)
                return false;
            var attachment = player._entity;
            if (attachment is null || attachment.WasCollected)
                return false;
            if (!TryCapture(player, null, null, out var captured)
                || (!local && !MatchesPlayerSession(player, connection, session))
                || (local && (!captured.IsServerOwned || captured.SourceConnectionId.HasValue)))
                return false;

            var candidate = new PlayerHandle(player, attachment, captured.SourceSession, local,
                captured.NetworkId, session);
            if (!MatchesPlayerAttachment(candidate, player) || (!local && !MatchesPlayerSession(player, connection, session)))
            {
                candidate.Retire();
                return false;
            }
            binding = candidate;
            status = WorldReadStatus.Success;
            return true;
        }

        private static bool MatchesPlayerAttachment(PlayerHandle binding, BoltEntity current)
        {
            var retained = binding.Player;
            var attachment = binding.Attachment;
            if (binding.IsRetired || retained is null || retained.WasCollected || !retained
                || attachment is null || attachment.WasCollected
                || current is null || current.WasCollected || !current || !current.isAttached
                || current.Pointer != retained.Pointer)
                return false;
            var active = current._entity;
            return active != null && !active.WasCollected && active.Pointer == attachment.Pointer
                && current.networkId.PackedValue == binding.NetworkId;
        }

        // Reads only the retained wrappers and Bolt.Entity flags, which stay valid after detach.
        private static bool HasPlayerAttachmentEnded(PlayerHandle binding)
        {
            var retained = binding.Player;
            var attachment = binding.Attachment;
            if (retained is null || retained.WasCollected || !retained
                || attachment is null || attachment.WasCollected || !attachment.IsAttached)
                return true;
            var active = retained._entity;
            return active == null || active.WasCollected || active.Pointer != attachment.Pointer;
        }

        private static bool RetirePlayerBinding(PlayerHandle binding,
            out WorldReadStatus status)
        {
            binding.Retire();
            status = WorldReadStatus.BindingChanged;
            return false;
        }

        // Lookups and native reads can fail while the captured attachment is intact.
        // Only its own state or a closed session proves that it ended.
        private static bool RejectPlayerBinding(PlayerHandle binding, WorldReadStatus unavailable,
            out WorldReadStatus status)
        {
            if (HasPlayerAttachmentEnded(binding) || binding.PlayerSession?.ClosedAt != null)
                return RetirePlayerBinding(binding, out status);
            status = unavailable;
            return false;
        }

        // A lookup naming another attached player is a replacement; an empty or stale entry is not.
        private static bool RejectPlayerAttachment(PlayerHandle binding, BoltEntity current,
            out WorldReadStatus status)
        {
            var retained = binding.Player;
            if (retained is not null && current is not null && !current.WasCollected && current
                && current.isAttached && current.Pointer != retained.Pointer)
                return RetirePlayerBinding(binding, out status);
            return RejectPlayerBinding(binding, WorldReadStatus.EntityUnavailable, out status);
        }

        // With tracking intact, a current unmigrated session that rejects the same attachment no longer owns it.
        private static bool RejectPlayerSession(PlayerHandle binding, out WorldReadStatus status)
        {
            if (binding.PlayerSession.IsCurrent && !MigratedPlayerIdentities.ContainsKey(binding.PlayerSession))
                return RetirePlayerBinding(binding, out status);
            return RejectPlayerBinding(binding, WorldReadStatus.SessionUnavailable, out status);
        }

        // An exception is not evidence of replacement; keep the handle unless its attachment ended.
        private static void RetireEndedPlayerBinding(PlayerHandle binding)
        {
            if (binding != null && HasPlayerAttachmentEnded(binding))
                binding.Retire();
        }

        // The game clears HasDedicatedState before Bolt stops; only a stopped or client-mode Bolt ends every attachment.
        private static void RetireAfterBoltStop(PlayerHandle binding, WorldReadStatus status)
        {
            if (status == WorldReadStatus.NotServer && (!BoltNetwork.isRunning || !BoltNetwork.isServer))
                binding?.Retire();
        }
    }

    /// <summary>
    /// Opaque handle retaining one native player attachment. Use on the SDK's game thread.
    /// It conveys no permission, trusted position, health or life-state authority.
    /// </summary>
    public sealed class PlayerHandle
    {
        internal PlayerHandle(BoltEntity player, Bolt.Entity attachment,
            ServerSession session, bool local, ulong networkId,
            ServerSession playerSession)
        {
            Player = player;
            Attachment = attachment;
            SourceSession = session;
            PlayerSession = playerSession;
            IsLocalPlayer = local;
            NetworkId = networkId;
        }

        // Strong wrappers keep a retired native allocation from being mistaken for a new one
        // at the same address while this handle is still live.
        internal BoltEntity Player { get; private set; }
        internal Bolt.Entity Attachment { get; private set; }
        /// <summary>True once a read proved the attachment ended. A retired handle never reads Success again.</summary>
        public bool IsRetired { get; private set; }
        /// <summary>Actual native Bolt source at capture; null for a server-owned character.</summary>
        public ServerSession SourceSession { get; }
        /// <summary>The client's character identity, independent of network ownership. Null for the listen host.</summary>
        public ServerSession PlayerSession { get; }
        public bool IsLocalPlayer { get; }
        public ulong NetworkId { get; }

        internal void Retire()
        {
            IsRetired = true;
            Player = null;
            Attachment = null;
        }
    }
}
