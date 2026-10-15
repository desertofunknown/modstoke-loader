// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using OpenACAI.Endnight.Loader;
using Sons.Multiplayer;

namespace OpenACAI.Sons
{
    public static partial class OpenACAISonsServerSessions
    {
        private static int _reportedRoleChangeFailure;

        /// <summary>
        /// Changes a live session's stored game role only when its identity and previous role still match.
        /// The caller owns authorization. This does not disconnect, force a save or confirm replication.
        /// </summary>
        public static OpenACAISonsRoleChangeResult SetStoredRole(OpenACAISonsServerIdentitySnapshot expected,
            OpenACAISonsGameRole role)
        {
            OpenACAISonsServerIdentitySnapshot before = null;
            OpenACAISonsServerIdentitySnapshot after = null;
            var attempted = false;
            OpenACAISonsRoleChangeResult Result(OpenACAISonsRoleChangeStatus status) => new(status, role, before, after);

            if (!HooksInstalled)
                return Result(OpenACAISonsRoleChangeStatus.Unavailable);
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return Result(OpenACAISonsRoleChangeStatus.WrongThread);
            if (expected == null || expected.RoleStatus == OpenACAISonsRoleDataStatus.Unavailable)
                return Result(OpenACAISonsRoleChangeStatus.InvalidSnapshot);
            // Unknown is not a clear operation: the native setter silently changes it to Trusted.
            if (role is not (OpenACAISonsGameRole.Guest or OpenACAISonsGameRole.Banned
                or OpenACAISonsGameRole.Trusted or OpenACAISonsGameRole.Admin or OpenACAISonsGameRole.Owner))
                return Result(OpenACAISonsRoleChangeStatus.InvalidRole);

            try
            {
                if (!TryReadIdentity(expected.Session, out before, out var readStatus))
                    return Result(RoleChangeReadFailure(readStatus));
                if (before.RoleStatus == OpenACAISonsRoleDataStatus.Unavailable)
                    return Result(OpenACAISonsRoleChangeStatus.RoleDataUnavailable);
                if (!SameRoleIdentity(expected, before) || expected.RoleStatus != before.RoleStatus
                    || expected.StoredRole != before.StoredRole)
                    return Result(OpenACAISonsRoleChangeStatus.PreconditionChanged);
                if (before.RoleStatus == OpenACAISonsRoleDataStatus.Present && before.StoredRole == role)
                    return Result(OpenACAISonsRoleChangeStatus.Unchanged);

                var manager = MultiplayerPlayerRoles._instance;
                if (manager is null || manager.WasCollected || !manager)
                    return Result(OpenACAISonsRoleChangeStatus.RoleDataUnavailable);
                var data = manager._playerRoleData;
                if (data is null || data.WasCollected)
                    return Result(OpenACAISonsRoleChangeStatus.RoleDataUnavailable);

                // The native call can add a missing record before failing. Never retry or roll it back here.
                attempted = true;
                manager.SetRoleValue(before.GameSteamId, (PlayerRoles)(int)role);
                if (!TryReadIdentity(expected.Session, out after, out _)
                    || !SameRoleIdentity(before, after)
                    || after.RoleStatus != OpenACAISonsRoleDataStatus.Present || after.StoredRole != role)
                    return Result(OpenACAISonsRoleChangeStatus.UpdateUncertain);

                return Result(OpenACAISonsRoleChangeStatus.Updated);
            }
            catch (Exception error)
            {
                ReportRoleChangeFailure(error);
                return Result(attempted ? OpenACAISonsRoleChangeStatus.UpdateUncertain : OpenACAISonsRoleChangeStatus.Failed);
            }
        }

        private static bool SameRoleIdentity(OpenACAISonsServerIdentitySnapshot first,
            OpenACAISonsServerIdentitySnapshot second) =>
            ReferenceEquals(first.Session, second.Session) && first.GameSteamId == second.GameSteamId
            && first.IdentitySource == second.IdentitySource && first.RegisteredSteamId == second.RegisteredSteamId;

        private static OpenACAISonsRoleChangeStatus RoleChangeReadFailure(OpenACAISonsIdentityReadStatus status) => status switch
        {
            OpenACAISonsIdentityReadStatus.Unavailable => OpenACAISonsRoleChangeStatus.Unavailable,
            OpenACAISonsIdentityReadStatus.WrongThread => OpenACAISonsRoleChangeStatus.WrongThread,
            OpenACAISonsIdentityReadStatus.NotServer => OpenACAISonsRoleChangeStatus.NotServer,
            OpenACAISonsIdentityReadStatus.SessionUnavailable => OpenACAISonsRoleChangeStatus.SessionUnavailable,
            OpenACAISonsIdentityReadStatus.IdentityUnavailable => OpenACAISonsRoleChangeStatus.IdentityUnavailable,
            _ => OpenACAISonsRoleChangeStatus.Failed
        };

        private static void ReportRoleChangeFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedRoleChangeFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Server role change failed ("
                    + error.GetType().FullName + "). Further role change failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    /// <summary>Managed observations around a role change, not a save or client acknowledgement.</summary>
    public sealed class OpenACAISonsRoleChangeResult
    {
        internal OpenACAISonsRoleChangeResult(OpenACAISonsRoleChangeStatus status, OpenACAISonsGameRole requestedRole,
            OpenACAISonsServerIdentitySnapshot before, OpenACAISonsServerIdentitySnapshot after)
        {
            Status = status;
            RequestedRole = requestedRole;
            Before = before;
            After = after;
        }

        public OpenACAISonsRoleChangeStatus Status { get; }
        /// <summary>The original requested value, including invalid input.</summary>
        public OpenACAISonsGameRole RequestedRole { get; }
        /// <summary>Fresh pre-call observation, or null when that read did not succeed.</summary>
        public OpenACAISonsServerIdentitySnapshot Before { get; }
        /// <summary>Post-call observation, or null when no call was made or the subsequent read failed.</summary>
        public OpenACAISonsServerIdentitySnapshot After { get; }
        public bool MutationAttempted => Status is OpenACAISonsRoleChangeStatus.Updated or OpenACAISonsRoleChangeStatus.UpdateUncertain;
    }

    public enum OpenACAISonsRoleChangeStatus
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
