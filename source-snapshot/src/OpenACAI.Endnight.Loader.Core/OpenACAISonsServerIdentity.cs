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
        private static int _reportedIdentityReadFailure;

        /// <summary>
        /// Reads the game's identity and stored role for an exact live remote session on the game thread.
        /// Success is an observation, not an authentication or permission verdict.
        /// </summary>
        public static bool TryReadIdentity(OpenACAISonsServerSession session,
            out OpenACAISonsServerIdentitySnapshot snapshot, out OpenACAISonsIdentityReadStatus status)
        {
            snapshot = null;
            if (!HooksInstalled)
            {
                status = OpenACAISonsIdentityReadStatus.Unavailable;
                return false;
            }
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                status = OpenACAISonsIdentityReadStatus.WrongThread;
                return false;
            }

            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                {
                    status = OpenACAISonsIdentityReadStatus.NotServer;
                    return false;
                }
                var connection = ResolveCurrent(session);
                if (connection == null)
                {
                    status = HooksInstalled ? OpenACAISonsIdentityReadStatus.SessionUnavailable : OpenACAISonsIdentityReadStatus.Unavailable;
                    return false;
                }

                var dedicated = GameServerManager.IsDedicatedServer;
                ulong? registeredSteamId = null;
                if (GameServerManager.GetRegisteredClient(connection, out var registered) && registered.m_SteamID != 0)
                    registeredSteamId = registered.m_SteamID;
                var gameSteamId = dedicated ? registeredSteamId.GetValueOrDefault() : MultiplayerUtilities.GetSteamId(connection);
                if (gameSteamId == 0)
                {
                    status = OpenACAISonsIdentityReadStatus.IdentityUnavailable;
                    return false;
                }

                var roleStatus = ReadStoredRole(gameSteamId, out var role);
                if (!HooksInstalled || !session.IsCurrent)
                {
                    status = HooksInstalled ? OpenACAISonsIdentityReadStatus.SessionUnavailable : OpenACAISonsIdentityReadStatus.Unavailable;
                    return false;
                }

                snapshot = new OpenACAISonsServerIdentitySnapshot(session, gameSteamId,
                    dedicated ? OpenACAISonsIdentitySource.DedicatedRegistration : OpenACAISonsIdentitySource.TransportEndpoint,
                    registeredSteamId, roleStatus, role);
                status = OpenACAISonsIdentityReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = OpenACAISonsIdentityReadStatus.Failed;
                ReportIdentityReadFailure(error);
                return false;
            }
        }

        private static OpenACAISonsRoleDataStatus ReadStoredRole(ulong steamId, out OpenACAISonsGameRole? role)
        {
            role = null;
            var manager = MultiplayerPlayerRoles._instance;
            if (manager is null || manager.WasCollected || !manager)
                return OpenACAISonsRoleDataStatus.Unavailable;
            var roleData = manager._playerRoleData;
            if (roleData is null || roleData.WasCollected)
                return OpenACAISonsRoleDataStatus.Unavailable;
            if (!roleData.TryGetData(steamId, out var data))
                return OpenACAISonsRoleDataStatus.Missing;
            if (data is null || data.WasCollected || data._playerSteamId != steamId)
                throw new InvalidOperationException("The role record no longer matches the requested identity.");

            // Both native GetRoleValue methods can turn Unknown into Trusted; copy the field instead.
            role = (OpenACAISonsGameRole)(int)data._playerRole;
            return OpenACAISonsRoleDataStatus.Present;
        }

        private static void ReportIdentityReadFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedIdentityReadFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Server identity read failed ("
                    + error.GetType().FullName + "). Further identity read failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum OpenACAISonsIdentityReadStatus
    {
        Success,
        Unavailable,
        WrongThread,
        NotServer,
        SessionUnavailable,
        IdentityUnavailable,
        Failed
    }

    public enum OpenACAISonsIdentitySource
    {
        DedicatedRegistration,
        TransportEndpoint
    }

    public enum OpenACAISonsRoleDataStatus
    {
        Unavailable,
        Missing,
        Present
    }

    /// <summary>Known game role values, not flags or an ordered permission scale.</summary>
    public enum OpenACAISonsGameRole
    {
        Unknown = 0,
        Guest = 1,
        Banned = 2,
        Trusted = 4,
        Admin = 16,
        Owner = 32
    }

    /// <summary>Managed identity observations. No native connection, role manager or player record is retained.</summary>
    public sealed class OpenACAISonsServerIdentitySnapshot
    {
        internal OpenACAISonsServerIdentitySnapshot(OpenACAISonsServerSession session, ulong gameSteamId,
            OpenACAISonsIdentitySource identitySource, ulong? registeredSteamId,
            OpenACAISonsRoleDataStatus roleStatus, OpenACAISonsGameRole? storedRole)
        {
            Session = session;
            GameSteamId = gameSteamId;
            IdentitySource = identitySource;
            RegisteredSteamId = registeredSteamId;
            RoleStatus = roleStatus;
            StoredRole = storedRole;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>The exact requested lifetime handle. Its current state can change after this read.</summary>
        public OpenACAISonsServerSession Session { get; }
        /// <summary>Nonzero identity used by the game. This does not independently verify a Steam account.</summary>
        public ulong GameSteamId { get; }
        public OpenACAISonsIdentitySource IdentitySource { get; }
        /// <summary>Fresh registration-table observation, not the session handle's cached metadata.</summary>
        public ulong? RegisteredSteamId { get; }
        public OpenACAISonsRoleDataStatus RoleStatus { get; }
        /// <summary>Raw stored value when present, including unknown numeric values. No default role is inferred.</summary>
        public OpenACAISonsGameRole? StoredRole { get; }
        /// <summary>Read completion time, not authentication or role-change time.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
