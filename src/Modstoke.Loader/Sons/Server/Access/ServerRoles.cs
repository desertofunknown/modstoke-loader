// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Modstoke.Loader;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    public static partial class ServerSessions
    {
        private static int _reportedRoleChangeFailure;

        /// <summary>
        /// Changes a live session's stored game role only when its identity and previous role still match.
        /// The caller owns authorization. This does not disconnect, force a save or confirm replication.
        /// </summary>
        public static RoleChangeResult SetStoredRole(ServerIdentitySnapshot expected,
            GameRole role)
        {
            ServerIdentitySnapshot before = null;
            ServerIdentitySnapshot after = null;
            var attempted = false;
            RoleChangeResult Result(RoleChangeStatus status) => new(status, role, before, after);

            if (!HooksInstalled)
                return Result(RoleChangeStatus.Unavailable);
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return Result(RoleChangeStatus.WrongThread);
            if (expected == null || expected.RoleStatus == RoleDataStatus.Unavailable)
                return Result(RoleChangeStatus.InvalidSnapshot);
            if (!ServerRoles.IsWritableRole(role))
                return Result(RoleChangeStatus.InvalidRole);

            try
            {
                if (!TryReadIdentity(expected.Session, out before, out var readStatus))
                    return Result(RoleChangeReadFailure(readStatus));
                if (before.RoleStatus == RoleDataStatus.Unavailable)
                    return Result(RoleChangeStatus.RoleDataUnavailable);
                if (!SameRoleIdentity(expected, before) || expected.RoleStatus != before.RoleStatus
                    || expected.StoredRole != before.StoredRole)
                    return Result(RoleChangeStatus.PreconditionChanged);
                if (before.RoleStatus == RoleDataStatus.Present && before.StoredRole == role)
                    return Result(RoleChangeStatus.Unchanged);

                // A write during save loading or setup can land in a store the load then replaces.
                var storeStatus = ServerRoles.GetWritableStore(out var manager, out _);
                if (storeStatus != StoredRoleReadStatus.Success)
                    return Result(storeStatus == StoredRoleReadStatus.NotServer
                        ? RoleChangeStatus.NotServer : RoleChangeStatus.RoleDataUnavailable);

                // The native call can add a missing record before failing. Never retry or roll it back here.
                attempted = true;
                manager.SetRoleValue(before.GameSteamId, (global::Sons.Multiplayer.PlayerRoles)(int)role);
                if (!TryReadIdentity(expected.Session, out after, out _)
                    || !SameRoleIdentity(before, after)
                    || after.RoleStatus != RoleDataStatus.Present || after.StoredRole != role)
                    return Result(RoleChangeStatus.UpdateUncertain);

                return Result(RoleChangeStatus.Updated);
            }
            catch (Exception error)
            {
                ReportRoleChangeFailure(error);
                return Result(attempted ? RoleChangeStatus.UpdateUncertain : RoleChangeStatus.Failed);
            }
        }

        private static bool SameRoleIdentity(ServerIdentitySnapshot first,
            ServerIdentitySnapshot second) =>
            ReferenceEquals(first.Session, second.Session) && first.GameSteamId == second.GameSteamId
            && first.IdentitySource == second.IdentitySource && first.RegisteredSteamId == second.RegisteredSteamId;

        private static RoleChangeStatus RoleChangeReadFailure(IdentityReadStatus status) => status switch
        {
            IdentityReadStatus.Unavailable => RoleChangeStatus.Unavailable,
            IdentityReadStatus.WrongThread => RoleChangeStatus.WrongThread,
            IdentityReadStatus.NotServer => RoleChangeStatus.NotServer,
            IdentityReadStatus.SessionUnavailable => RoleChangeStatus.SessionUnavailable,
            IdentityReadStatus.IdentityUnavailable => RoleChangeStatus.IdentityUnavailable,
            _ => RoleChangeStatus.Failed
        };

        private static void ReportRoleChangeFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedRoleChangeFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server role change failed ("
                    + error.GetType().FullName + "). Further role change failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    /// <summary>Managed observations around a role change, not a save or client acknowledgement.</summary>
    public sealed class RoleChangeResult
    {
        internal RoleChangeResult(RoleChangeStatus status, GameRole requestedRole,
            ServerIdentitySnapshot before, ServerIdentitySnapshot after)
        {
            Status = status;
            RequestedRole = requestedRole;
            Before = before;
            After = after;
        }

        public RoleChangeStatus Status { get; }
        /// <summary>The original requested value, including invalid input.</summary>
        public GameRole RequestedRole { get; }
        /// <summary>Fresh pre-call observation, or null when that read did not succeed.</summary>
        public ServerIdentitySnapshot Before { get; }
        /// <summary>Post-call observation, or null when no call was made or the subsequent read failed.</summary>
        public ServerIdentitySnapshot After { get; }
        public bool MutationAttempted => Status is RoleChangeStatus.Updated or RoleChangeStatus.UpdateUncertain;
    }

    public enum RoleChangeStatus
    {
        /// <summary>The setter returned and the same live identity's stored role matched on readback.</summary>
        Updated,
        /// <summary>The expected snapshot matched and already had the requested role. No setter was called.</summary>
        Unchanged,
        Unavailable,
        WrongThread,
        NotServer,
        InvalidSnapshot,
        InvalidRole,
        SessionUnavailable,
        IdentityUnavailable,
        RoleDataUnavailable,
        /// <summary>Identity, registration or stored role changed since the supplied snapshot. No mutation was attempted.</summary>
        PreconditionChanged,
        /// <summary>Preparation failed before the setter was called.</summary>
        Failed,
        /// <summary>The setter was attempted, but its final effect could not be confirmed. No retry or rollback was made.</summary>
        UpdateUncertain
    }
}
