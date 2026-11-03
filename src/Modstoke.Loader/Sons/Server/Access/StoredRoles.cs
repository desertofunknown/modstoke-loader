// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Modstoke.Loader;
using global::Sons.Multiplayer;
using global::Sons.Save;

namespace Modstoke.Sons
{
    /// <summary>Stored roles in the current server world, including players who are not connected.</summary>
    public static class ServerRoles
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
        public static bool TryRead(ulong gameSteamId, out StoredRoleSnapshot snapshot,
            out StoredRoleReadStatus status)
        {
            snapshot = null;
            try
            {
                status = ReadCurrentRecord(gameSteamId, out _, out _, out snapshot);
                return status == StoredRoleReadStatus.Success;
            }
            catch (Exception error)
            {
                ReportFailure("read", error);
                status = StoredRoleReadStatus.Failed;
                return false;
            }
        }

        /// <summary>
        /// Changes a role only when its current raw value matches expectedRole; null expects no record.
        /// The caller owns authorization and selection of the current world and ID. No live session
        /// is required. This neither disconnects a player nor forces a save or client update.
        /// </summary>
        public static StoredRoleChangeResult SetStoredRole(ulong gameSteamId,
            GameRole? expectedRole, GameRole role)
        {
            StoredRoleSnapshot before = null;
            StoredRoleSnapshot after = null;
            var attempted = false;
            StoredRoleChangeResult Result(StoredRoleChangeStatus status) =>
                new(status, role, before, after);

            try
            {
                var readStatus = ReadCurrentRecord(gameSteamId, out var manager, out var data, out before);
                if (readStatus != StoredRoleReadStatus.Success)
                    return Result(ChangeReadFailure(readStatus));
                if (!IsWritableRole(role))
                    return Result(StoredRoleChangeStatus.InvalidRole);
                if (before.StoredRole != expectedRole)
                    return Result(StoredRoleChangeStatus.PreconditionChanged);
                if (before.StoredRole == role)
                    return Result(StoredRoleChangeStatus.Unchanged);

                // Insertion can succeed before the setter fails. Do not retry or roll it back.
                attempted = true;
                manager.SetRoleValue(gameSteamId, (global::Sons.Multiplayer.PlayerRoles)(int)role);
                readStatus = ReadCurrentRecord(gameSteamId, out var currentManager, out var currentData, out after);
                if (readStatus != StoredRoleReadStatus.Success
                    || currentManager.Pointer != manager.Pointer || currentData.Pointer != data.Pointer
                    || after.StoredRole != role)
                    return Result(StoredRoleChangeStatus.UpdateUncertain);

                return Result(StoredRoleChangeStatus.Updated);
            }
            catch (Exception error)
            {
                ReportFailure("change", error);
                return Result(attempted ? StoredRoleChangeStatus.UpdateUncertain
                    : StoredRoleChangeStatus.Failed);
            }
        }

        /// <summary>
        /// Removes the stored player record only when its raw role matches expectedRole.
        /// Null expects an already-missing record and performs no write. Removal also drops the
        /// cached player name. It does not disconnect, save, edit the owners whitelist or prevent recreation.
        /// The caller owns authorization and selection of the current world and ID.
        /// </summary>
        public static StoredRoleRemovalResult RemoveStoredRole(ulong gameSteamId,
            GameRole? expectedRole)
        {
            StoredRoleSnapshot before = null;
            StoredRoleSnapshot after = null;
            var attempted = false;
            StoredRoleRemovalResult Result(StoredRoleRemovalStatus status) =>
                new(status, before, after);

            try
            {
                var readStatus = ReadCurrentRecord(gameSteamId, out var manager, out var data, out before);
                if (readStatus != StoredRoleReadStatus.Success)
                    return Result(RemovalReadFailure(readStatus));
                if (before.StoredRole != expectedRole)
                    return Result(StoredRoleRemovalStatus.PreconditionChanged);
                if (!before.HasRecord)
                    return Result(StoredRoleRemovalStatus.AlreadyMissing);

                attempted = true;
                manager.ClearPlayer(gameSteamId);
                readStatus = ReadCurrentRecord(gameSteamId, out var currentManager, out var currentData, out after);
                if (readStatus != StoredRoleReadStatus.Success
                    || currentManager.Pointer != manager.Pointer || currentData.Pointer != data.Pointer
                    || after.HasRecord)
                    return Result(StoredRoleRemovalStatus.RemovalUncertain);

                return Result(StoredRoleRemovalStatus.Removed);
            }
            catch (Exception error)
            {
                ReportFailure("removal", error);
                return Result(attempted ? StoredRoleRemovalStatus.RemovalUncertain
                    : StoredRoleRemovalStatus.Failed);
            }
        }

        private static StoredRoleReadStatus ReadCurrentRecord(ulong gameSteamId,
            out MultiplayerPlayerRoles manager, out MultiplayerPlayerRoleData data,
            out StoredRoleSnapshot snapshot)
        {
            snapshot = null;
            var status = GetStore(gameSteamId, out manager, out data);
            if (status != StoredRoleReadStatus.Success)
                return status;

            GameRole? role = null;
            if (data.TryGetData(gameSteamId, out var record))
            {
                if (record is null || record.WasCollected || record._playerSteamId != gameSteamId)
                    return StoredRoleReadStatus.RoleDataUnavailable;
                // GetRoleValue can mutate Unknown to Trusted; copy the stored field instead.
                role = (GameRole)(int)record._playerRole;
            }

            status = GetStore(gameSteamId, out var currentManager, out var currentData);
            if (status != StoredRoleReadStatus.Success)
                return status;
            if (currentManager.Pointer != manager.Pointer || currentData.Pointer != data.Pointer)
                return StoredRoleReadStatus.RoleDataUnavailable;

            snapshot = new StoredRoleSnapshot(gameSteamId, role);
            return StoredRoleReadStatus.Success;
        }

        private static StoredRoleReadStatus GetStore(ulong gameSteamId,
            out MultiplayerPlayerRoles manager, out MultiplayerPlayerRoleData data)
        {
            manager = null;
            data = null;
            var ownerThread = Volatile.Read(ref _ownerThreadId);
            if (ownerThread == 0)
                return StoredRoleReadStatus.Unavailable;
            if (Environment.CurrentManagedThreadId != ownerThread)
                return StoredRoleReadStatus.WrongThread;
            if (gameSteamId == 0)
                return StoredRoleReadStatus.InvalidSteamId;
            return GetWritableStore(out manager, out data);
        }

        // Unknown is not removal or a clear: the native setter converts it to Trusted.
        internal static bool IsWritableRole(GameRole role) => role is GameRole.Guest or GameRole.Banned
            or GameRole.Trusted or GameRole.Admin or GameRole.Owner;

        /// <summary>The live role store, only while a loaded multiplayer save can safely accept writes.</summary>
        internal static StoredRoleReadStatus GetWritableStore(out MultiplayerPlayerRoles manager,
            out MultiplayerPlayerRoleData data)
        {
            manager = null;
            data = null;
            if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                return StoredRoleReadStatus.NotServer;
            if (!SaveGameManager.TryGetInstance(out var saveManager)
                || saveManager is null || saveManager.WasCollected || !saveManager
                || saveManager._loadingInProgress || !SaveGameManager._setupSavePaths
                || SaveGameManager._activeSaveGameType != SaveGameType.Multiplayer)
                return StoredRoleReadStatus.NotReady;

            manager = MultiplayerPlayerRoles._instance;
            if (manager is null || manager.WasCollected || !manager || !manager.isActiveAndEnabled)
                return StoredRoleReadStatus.RoleDataUnavailable;
            data = manager._playerRoleData;
            return data is null || data.WasCollected
                ? StoredRoleReadStatus.RoleDataUnavailable : StoredRoleReadStatus.Success;
        }

        private static StoredRoleChangeStatus ChangeReadFailure(StoredRoleReadStatus status) => status switch
        {
            StoredRoleReadStatus.Unavailable => StoredRoleChangeStatus.Unavailable,
            StoredRoleReadStatus.WrongThread => StoredRoleChangeStatus.WrongThread,
            StoredRoleReadStatus.InvalidSteamId => StoredRoleChangeStatus.InvalidSteamId,
            StoredRoleReadStatus.NotServer => StoredRoleChangeStatus.NotServer,
            StoredRoleReadStatus.NotReady => StoredRoleChangeStatus.NotReady,
            StoredRoleReadStatus.RoleDataUnavailable => StoredRoleChangeStatus.RoleDataUnavailable,
            _ => StoredRoleChangeStatus.Failed
        };

        private static StoredRoleRemovalStatus RemovalReadFailure(StoredRoleReadStatus status) => status switch
        {
            StoredRoleReadStatus.Unavailable => StoredRoleRemovalStatus.Unavailable,
            StoredRoleReadStatus.WrongThread => StoredRoleRemovalStatus.WrongThread,
            StoredRoleReadStatus.InvalidSteamId => StoredRoleRemovalStatus.InvalidSteamId,
            StoredRoleReadStatus.NotServer => StoredRoleRemovalStatus.NotServer,
            StoredRoleReadStatus.NotReady => StoredRoleRemovalStatus.NotReady,
            StoredRoleReadStatus.RoleDataUnavailable => StoredRoleRemovalStatus.RoleDataUnavailable,
            _ => StoredRoleRemovalStatus.Failed
        };

        private static void ReportFailure(string operation, Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Stored server role " + operation + " failed ("
                    + error.GetType().FullName + "). Further stored-role failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    /// <summary>A raw role observation, not a connection, authenticated identity or permission verdict.</summary>
    public sealed class StoredRoleSnapshot
    {
        internal StoredRoleSnapshot(ulong gameSteamId, GameRole? role)
        {
            GameSteamId = gameSteamId;
            StoredRole = role;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public ulong GameSteamId { get; }
        /// <summary>Null means no record. Present values include Unknown and unrecognized numeric roles.</summary>
        public GameRole? StoredRole { get; }
        public bool HasRecord => StoredRole.HasValue;
        /// <summary>Read completion time, not the time the record changed.</summary>
        public DateTimeOffset ObservedAt { get; }
    }

    public enum StoredRoleReadStatus
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
    public sealed class StoredRoleChangeResult
    {
        internal StoredRoleChangeResult(StoredRoleChangeStatus status,
            GameRole requestedRole, StoredRoleSnapshot before, StoredRoleSnapshot after)
        {
            Status = status;
            RequestedRole = requestedRole;
            Before = before;
            After = after;
        }

        public StoredRoleChangeStatus Status { get; }
        public GameRole RequestedRole { get; }
        /// <summary>Fresh pre-call observation, or null when no read succeeded.</summary>
        public StoredRoleSnapshot Before { get; }
        /// <summary>Fresh post-call observation when available; inspect Status before relying on it.</summary>
        public StoredRoleSnapshot After { get; }
        public bool MutationAttempted => Status is StoredRoleChangeStatus.Updated
            or StoredRoleChangeStatus.UpdateUncertain;
    }

    /// <summary>Observed removal of a native role record, not a durable erasure or permission verdict.</summary>
    public sealed class StoredRoleRemovalResult
    {
        internal StoredRoleRemovalResult(StoredRoleRemovalStatus status,
            StoredRoleSnapshot before, StoredRoleSnapshot after)
        {
            Status = status;
            Before = before;
            After = after;
        }

        public StoredRoleRemovalStatus Status { get; }
        /// <summary>Fresh pre-call observation, or null when no read succeeded.</summary>
        public StoredRoleSnapshot Before { get; }
        /// <summary>Fresh post-call observation when available; inspect Status before relying on it.</summary>
        public StoredRoleSnapshot After { get; }
        public bool MutationAttempted => Status is StoredRoleRemovalStatus.Removed
            or StoredRoleRemovalStatus.RemovalUncertain;
    }

    public enum StoredRoleRemovalStatus
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

    public enum StoredRoleChangeStatus
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
