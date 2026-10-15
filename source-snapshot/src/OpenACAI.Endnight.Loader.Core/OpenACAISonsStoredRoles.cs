// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using OpenACAI.Endnight.Loader;
using Sons.Multiplayer;
using Sons.Save;

namespace OpenACAI.Sons
{
    /// <summary>Stored roles in the current server world, including players who are not connected.</summary>
    public static class OpenACAISonsServerRoles
    {
        private static int _ownerThreadId;
        private static int _reportedFailure;

        internal static void Initialize()
        {
            Interlocked.CompareExchange(ref _ownerThreadId, Environment.CurrentManagedThreadId, 0);
        }

        /// <summary>
        /// Copies the raw role record for a nonzero game Steam ID on the game thread.
        /// A successful read may report a missing record. It does not authenticate the ID.
        /// </summary>
        public static bool TryRead(ulong gameSteamId, out OpenACAISonsStoredRoleSnapshot snapshot,
            out OpenACAISonsStoredRoleReadStatus status)
        {
            snapshot = null;
            try
            {
                status = ReadCurrentRecord(gameSteamId, out _, out _, out snapshot);
                return status == OpenACAISonsStoredRoleReadStatus.Success;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                status = OpenACAISonsStoredRoleReadStatus.Failed;
                return false;
            }
        }

        /// <summary>
        /// Changes a role only when its current raw value matches expectedRole; null expects no record.
        /// The caller owns authorization and selection of the current world and ID. No live session
        /// is required. This neither disconnects a player nor forces a save or client update.
        /// </summary>
        public static OpenACAISonsStoredRoleChangeResult SetStoredRole(ulong gameSteamId,
            OpenACAISonsGameRole? expectedRole, OpenACAISonsGameRole role)
        {
            OpenACAISonsStoredRoleSnapshot before = null;
            OpenACAISonsStoredRoleSnapshot after = null;
            var attempted = false;
            OpenACAISonsStoredRoleChangeResult Result(OpenACAISonsStoredRoleChangeStatus status) =>
                new(status, role, before, after);

            try
            {
                var readStatus = ReadCurrentRecord(gameSteamId, out var manager, out var data, out before);
                if (readStatus != OpenACAISonsStoredRoleReadStatus.Success)
                    return Result(ChangeReadFailure(readStatus));
                // Unknown is not removal: the native setter converts it to Trusted.
                if (role is not (OpenACAISonsGameRole.Guest or OpenACAISonsGameRole.Banned
                    or OpenACAISonsGameRole.Trusted or OpenACAISonsGameRole.Admin or OpenACAISonsGameRole.Owner))
                    return Result(OpenACAISonsStoredRoleChangeStatus.InvalidRole);
                if (before.StoredRole != expectedRole)
                    return Result(OpenACAISonsStoredRoleChangeStatus.PreconditionChanged);
                if (before.StoredRole == role)
                    return Result(OpenACAISonsStoredRoleChangeStatus.Unchanged);

                // Insertion can succeed before the setter fails. Do not retry or roll it back.
                attempted = true;
                manager.SetRoleValue(gameSteamId, (PlayerRoles)(int)role);
                readStatus = ReadCurrentRecord(gameSteamId, out var currentManager, out var currentData, out after);
                if (readStatus != OpenACAISonsStoredRoleReadStatus.Success
                    || currentManager.Pointer != manager.Pointer || currentData.Pointer != data.Pointer
                    || after.StoredRole != role)
                    return Result(OpenACAISonsStoredRoleChangeStatus.UpdateUncertain);

                return Result(OpenACAISonsStoredRoleChangeStatus.Updated);
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return Result(attempted ? OpenACAISonsStoredRoleChangeStatus.UpdateUncertain
                    : OpenACAISonsStoredRoleChangeStatus.Failed);
            }
        }

        /// <summary>
        /// Removes the stored player record only when its raw role matches expectedRole.
        /// Null expects an already-missing record and performs no write. Removal also drops the
        /// cached player name. It does not disconnect, save, edit the owners whitelist or prevent recreation.
        /// The caller owns authorization and selection of the current world and ID.
        /// </summary>
        public static OpenACAISonsStoredRoleRemovalResult RemoveStoredRole(ulong gameSteamId,
            OpenACAISonsGameRole? expectedRole)
        {
            OpenACAISonsStoredRoleSnapshot before = null;
            OpenACAISonsStoredRoleSnapshot after = null;
            var attempted = false;
            OpenACAISonsStoredRoleRemovalResult Result(OpenACAISonsStoredRoleRemovalStatus status) =>
                new(status, before, after);

            try
            {
                var readStatus = ReadCurrentRecord(gameSteamId, out var manager, out var data, out before);
                if (readStatus != OpenACAISonsStoredRoleReadStatus.Success)
                    return Result(RemovalReadFailure(readStatus));
                if (before.StoredRole != expectedRole)
                    return Result(OpenACAISonsStoredRoleRemovalStatus.PreconditionChanged);
                if (!before.HasRecord)
                    return Result(OpenACAISonsStoredRoleRemovalStatus.AlreadyMissing);

                attempted = true;
                manager.ClearPlayer(gameSteamId);
                readStatus = ReadCurrentRecord(gameSteamId, out var currentManager, out var currentData, out after);
                if (readStatus != OpenACAISonsStoredRoleReadStatus.Success
                    || currentManager.Pointer != manager.Pointer || currentData.Pointer != data.Pointer
                    || after.HasRecord)
                    return Result(OpenACAISonsStoredRoleRemovalStatus.RemovalUncertain);

                return Result(OpenACAISonsStoredRoleRemovalStatus.Removed);
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return Result(attempted ? OpenACAISonsStoredRoleRemovalStatus.RemovalUncertain
                    : OpenACAISonsStoredRoleRemovalStatus.Failed);
            }
        }

        private static OpenACAISonsStoredRoleReadStatus ReadCurrentRecord(ulong gameSteamId,
            out MultiplayerPlayerRoles manager, out MultiplayerPlayerRoleData data,
            out OpenACAISonsStoredRoleSnapshot snapshot)
        {
            snapshot = null;
            var status = GetStore(gameSteamId, out manager, out data);
            if (status != OpenACAISonsStoredRoleReadStatus.Success)
                return status;

            OpenACAISonsGameRole? role = null;
            if (data.TryGetData(gameSteamId, out var record))
            {
                if (record is null || record.WasCollected || record._playerSteamId != gameSteamId)
                    return OpenACAISonsStoredRoleReadStatus.RoleDataUnavailable;
                // GetRoleValue can mutate Unknown to Trusted; copy the stored field instead.
                role = (OpenACAISonsGameRole)(int)record._playerRole;
            }

            status = GetStore(gameSteamId, out var currentManager, out var currentData);
            if (status != OpenACAISonsStoredRoleReadStatus.Success)
                return status;
            if (currentManager.Pointer != manager.Pointer || currentData.Pointer != data.Pointer)
                return OpenACAISonsStoredRoleReadStatus.RoleDataUnavailable;

            snapshot = new OpenACAISonsStoredRoleSnapshot(gameSteamId, role);
            return OpenACAISonsStoredRoleReadStatus.Success;
        }

        private static OpenACAISonsStoredRoleReadStatus GetStore(ulong gameSteamId,
            out MultiplayerPlayerRoles manager, out MultiplayerPlayerRoleData data)
        {
            manager = null;
            data = null;
            var ownerThread = Volatile.Read(ref _ownerThreadId);
            if (ownerThread == 0)
                return OpenACAISonsStoredRoleReadStatus.Unavailable;
            if (Environment.CurrentManagedThreadId != ownerThread)
                return OpenACAISonsStoredRoleReadStatus.WrongThread;
            if (gameSteamId == 0)
                return OpenACAISonsStoredRoleReadStatus.InvalidSteamId;
            if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                return OpenACAISonsStoredRoleReadStatus.NotServer;
            if (!SaveGameManager.TryGetInstance(out var saveManager)
                || saveManager is null || saveManager.WasCollected || !saveManager
                || saveManager._loadingInProgress || !SaveGameManager._setupSavePaths
                || SaveGameManager._activeSaveGameType != SaveGameType.Multiplayer)
                return OpenACAISonsStoredRoleReadStatus.NotReady;

            manager = MultiplayerPlayerRoles._instance;
            if (manager is null || manager.WasCollected || !manager || !manager.isActiveAndEnabled)
                return OpenACAISonsStoredRoleReadStatus.RoleDataUnavailable;
            data = manager._playerRoleData;
            return data is null || data.WasCollected
                ? OpenACAISonsStoredRoleReadStatus.RoleDataUnavailable : OpenACAISonsStoredRoleReadStatus.Success;
        }

        private static OpenACAISonsStoredRoleChangeStatus ChangeReadFailure(OpenACAISonsStoredRoleReadStatus status) => status switch
        {
            OpenACAISonsStoredRoleReadStatus.Unavailable => OpenACAISonsStoredRoleChangeStatus.Unavailable,
            OpenACAISonsStoredRoleReadStatus.WrongThread => OpenACAISonsStoredRoleChangeStatus.WrongThread,
            OpenACAISonsStoredRoleReadStatus.InvalidSteamId => OpenACAISonsStoredRoleChangeStatus.InvalidSteamId,
            OpenACAISonsStoredRoleReadStatus.NotServer => OpenACAISonsStoredRoleChangeStatus.NotServer,
            OpenACAISonsStoredRoleReadStatus.NotReady => OpenACAISonsStoredRoleChangeStatus.NotReady,
            OpenACAISonsStoredRoleReadStatus.RoleDataUnavailable => OpenACAISonsStoredRoleChangeStatus.RoleDataUnavailable,
            _ => OpenACAISonsStoredRoleChangeStatus.Failed
        };

        private static OpenACAISonsStoredRoleRemovalStatus RemovalReadFailure(OpenACAISonsStoredRoleReadStatus status) => status switch
        {
            OpenACAISonsStoredRoleReadStatus.Unavailable => OpenACAISonsStoredRoleRemovalStatus.Unavailable,
            OpenACAISonsStoredRoleReadStatus.WrongThread => OpenACAISonsStoredRoleRemovalStatus.WrongThread,
            OpenACAISonsStoredRoleReadStatus.InvalidSteamId => OpenACAISonsStoredRoleRemovalStatus.InvalidSteamId,
            OpenACAISonsStoredRoleReadStatus.NotServer => OpenACAISonsStoredRoleRemovalStatus.NotServer,
            OpenACAISonsStoredRoleReadStatus.NotReady => OpenACAISonsStoredRoleRemovalStatus.NotReady,
            OpenACAISonsStoredRoleReadStatus.RoleDataUnavailable => OpenACAISonsStoredRoleRemovalStatus.RoleDataUnavailable,
            _ => OpenACAISonsStoredRoleRemovalStatus.Failed
        };

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Stored server role operation failed ("
                    + error.GetType().FullName + "). Further stored-role failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    /// <summary>A raw role observation, not a connection, authenticated identity or permission verdict.</summary>
    public sealed class OpenACAISonsStoredRoleSnapshot
    {
        internal OpenACAISonsStoredRoleSnapshot(ulong gameSteamId, OpenACAISonsGameRole? role)
        {
            GameSteamId = gameSteamId;
            StoredRole = role;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public ulong GameSteamId { get; }
        /// <summary>Null means no record. Present values include Unknown and unrecognized numeric roles.</summary>
        public OpenACAISonsGameRole? StoredRole { get; }
        public bool HasRecord => StoredRole.HasValue;
        /// <summary>Read completion time, not the time the record changed.</summary>
        public DateTimeOffset ObservedAt { get; }
    }

    public enum OpenACAISonsStoredRoleReadStatus
    {
        Success,
        Unavailable,
        WrongThread,
        InvalidSteamId,
        NotServer,
        NotReady,
        RoleDataUnavailable,
        Failed
    }

    /// <summary>Sequential observations around a conditional native write, not a save or replication receipt.</summary>
    public sealed class OpenACAISonsStoredRoleChangeResult
    {
        internal OpenACAISonsStoredRoleChangeResult(OpenACAISonsStoredRoleChangeStatus status,
            OpenACAISonsGameRole requestedRole, OpenACAISonsStoredRoleSnapshot before, OpenACAISonsStoredRoleSnapshot after)
        {
            Status = status;
            RequestedRole = requestedRole;
            Before = before;
            After = after;
        }

        public OpenACAISonsStoredRoleChangeStatus Status { get; }
        public OpenACAISonsGameRole RequestedRole { get; }
        /// <summary>Fresh pre-call observation, or null when no read succeeded.</summary>
        public OpenACAISonsStoredRoleSnapshot Before { get; }
        /// <summary>Fresh post-call observation when available; inspect Status before relying on it.</summary>
        public OpenACAISonsStoredRoleSnapshot After { get; }
        public bool MutationAttempted => Status is OpenACAISonsStoredRoleChangeStatus.Updated
            or OpenACAISonsStoredRoleChangeStatus.UpdateUncertain;
    }

    /// <summary>Observed removal of a native role record, not a durable erasure or permission verdict.</summary>
    public sealed class OpenACAISonsStoredRoleRemovalResult
    {
        internal OpenACAISonsStoredRoleRemovalResult(OpenACAISonsStoredRoleRemovalStatus status,
            OpenACAISonsStoredRoleSnapshot before, OpenACAISonsStoredRoleSnapshot after)
        {
            Status = status;
            Before = before;
            After = after;
        }

        public OpenACAISonsStoredRoleRemovalStatus Status { get; }
        /// <summary>Fresh pre-call observation, or null when no read succeeded.</summary>
        public OpenACAISonsStoredRoleSnapshot Before { get; }
        /// <summary>Fresh post-call observation when available; inspect Status before relying on it.</summary>
        public OpenACAISonsStoredRoleSnapshot After { get; }
        public bool MutationAttempted => Status is OpenACAISonsStoredRoleRemovalStatus.Removed
            or OpenACAISonsStoredRoleRemovalStatus.RemovalUncertain;
    }

    public enum OpenACAISonsStoredRoleRemovalStatus
    {
        /// <summary>The native call returned and the same store no longer contained the requested ID.</summary>
        Removed,
        /// <summary>The record was absent and expectedRole was null; no removal was attempted.</summary>
        AlreadyMissing,
        Unavailable,
        WrongThread,
        InvalidSteamId,
        NotServer,
        NotReady,
        RoleDataUnavailable,
        /// <summary>The raw role or record presence differed from expectedRole; no removal was attempted.</summary>
        PreconditionChanged,
        /// <summary>Preparation failed before the native removal was attempted.</summary>
        Failed,
        /// <summary>Removal was attempted but its final effect could not be confirmed; no retry or rollback occurred.</summary>
        RemovalUncertain
    }

    public enum OpenACAISonsStoredRoleChangeStatus
    {
        /// <summary>The native setter returned and the same role store contained the requested value.</summary>
        Updated,
        /// <summary>The precondition matched and the record already contained the requested value.</summary>
        Unchanged,
        Unavailable,
        WrongThread,
        InvalidSteamId,
        InvalidRole,
        NotServer,
        NotReady,
        RoleDataUnavailable,
        /// <summary>The current raw role or record presence did not match expectedRole.</summary>
        PreconditionChanged,
        /// <summary>Preparation failed before the native setter was attempted.</summary>
        Failed,
        /// <summary>The setter was attempted but its final effect could not be confirmed; no retry or rollback occurred.</summary>
        UpdateUncertain
    }
}
