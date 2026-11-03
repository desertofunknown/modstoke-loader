// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using System.Text;
using System.Threading;
using Modstoke.Loader;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    public static partial class ServerSessions
    {
        private static int _reportedIdentityReadFailure;

        /// <summary>
        /// Reads the game's identity and stored role for an exact live remote session on the game thread.
        /// Success is an observation, not an authentication or permission verdict.
        /// </summary>
        public static bool TryReadIdentity(ServerSession session,
            out ServerIdentitySnapshot snapshot, out IdentityReadStatus status)
            => TryReadIdentityCore(session, true, out snapshot, out _, out status);

        /// <summary>
        /// Reads the game's nonzero Steam ID for an exact live remote session on the game thread
        /// without constructing an identity snapshot. Retains TryReadIdentity's session and role-data
        /// consistency checks. Failure returns zero; success is not authentication or permission.
        /// </summary>
        public static bool TryReadGameSteamId(ServerSession session,
            out ulong steamId, out IdentityReadStatus status)
            => TryReadIdentityCore(session, false, out _, out steamId, out status);

        private static bool TryReadIdentityCore(ServerSession session, bool includeSnapshot,
            out ServerIdentitySnapshot snapshot, out ulong steamId, out IdentityReadStatus status)
        {
            snapshot = null;
            steamId = 0;
            if (!HooksInstalled)
            {
                status = IdentityReadStatus.Unavailable;
                return false;
            }
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                status = IdentityReadStatus.WrongThread;
                return false;
            }

            try
            {
                if (!ServerWorld.IsServerActive)
                {
                    status = IdentityReadStatus.NotServer;
                    return false;
                }
                var connection = ResolveCurrent(session);
                if (connection == null)
                {
                    status = HooksInstalled ? IdentityReadStatus.SessionUnavailable : IdentityReadStatus.Unavailable;
                    return false;
                }

                var dedicated = GameServerManager.IsDedicatedServer;
                ulong? registeredSteamId = null;
                if (GameServerManager.GetRegisteredClient(connection, out var registered) && registered.m_SteamID != 0)
                    registeredSteamId = registered.m_SteamID;
                var gameSteamId = dedicated ? registeredSteamId.GetValueOrDefault() : MultiplayerUtilities.GetSteamId(connection);
                if (gameSteamId == 0)
                {
                    status = IdentityReadStatus.IdentityUnavailable;
                    return false;
                }

                var roleStatus = ReadStoredRole(gameSteamId, out var role);
                var displayName = includeSnapshot ? ReadJoinName(connection) : null;
                if (!HooksInstalled || !session.IsCurrent)
                {
                    status = HooksInstalled ? IdentityReadStatus.SessionUnavailable : IdentityReadStatus.Unavailable;
                    return false;
                }

                if (includeSnapshot)
                    snapshot = new ServerIdentitySnapshot(session, gameSteamId,
                        dedicated ? IdentitySource.DedicatedRegistration : IdentitySource.TransportEndpoint,
                        registeredSteamId, roleStatus, role, displayName);
                steamId = gameSteamId;
                status = IdentityReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = IdentityReadStatus.Failed;
                ReportIdentityReadFailure(error);
                return false;
            }
        }

        private static RoleDataStatus ReadStoredRole(ulong steamId, out GameRole? role)
        {
            role = null;
            var manager = MultiplayerPlayerRoles._instance;
            if (manager is null || manager.WasCollected || !manager)
                return RoleDataStatus.Unavailable;
            var roleData = manager._playerRoleData;
            if (roleData is null || roleData.WasCollected)
                return RoleDataStatus.Unavailable;
            if (!roleData.TryGetData(steamId, out var data))
                return RoleDataStatus.Missing;
            if (data is null || data.WasCollected || data._playerSteamId != steamId)
                throw new InvalidOperationException("The role record no longer matches the requested identity.");

            // Both native GetRoleValue methods can turn Unknown into Trusted; copy the field instead.
            role = (GameRole)(int)data._playerRole;
            return RoleDataStatus.Present;
        }

        // The joining client chose this name and sent it in its Bolt connect token.
        private static string ReadJoinName(BoltConnection connection)
        {
            var token = connection.ConnectToken;
            if (token is null || token.WasCollected)
                return null;
            var name = (token.TryCast<CoopJoinDedicatedServerToken>()?.PlayerName
                ?? token.TryCast<CoopConnectToken>()?.PlayerName)?.Trim();
            // Steam limits persona names to 128 UTF-8 bytes; anything else came from a modified client.
            if (string.IsNullOrEmpty(name) || Encoding.UTF8.GetByteCount(name) > 128)
                return null;
            return IsPlainDisplayText(name) ? name : null;
        }

        /// <summary>
        /// Rejects control, bidi/zero-width format and line-breaking characters and unpaired surrogates,
        /// so client-chosen text cannot reorder or hide parts of admin-facing names. Paired surrogates
        /// (emoji, tag sequences) are allowed, as are ZWJ and ZWNJ between two visible characters.
        /// This is not rich-text escaping.
        /// </summary>
        internal static bool IsPlainDisplayText(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                    continue;
                }
                if (char.IsControl(c) || char.IsSurrogate(c))
                    return false;
                if (c is '‌' or '‍')
                {
                    // Emoji sequences and Persian or Indic names join visible characters.
                    if (i == 0 || i + 1 == text.Length || !IsVisible(text[i - 1]) || !IsVisible(text[i + 1]))
                        return false;
                    continue;
                }
                var category = char.GetUnicodeCategory(c);
                if (category is UnicodeCategory.Format or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator)
                    return false;
            }
            return true;
        }

        private static bool IsVisible(char c) =>
            !char.IsWhiteSpace(c) && !char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format;

        private static void ReportIdentityReadFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedIdentityReadFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server identity read failed ("
                    + error.GetType().FullName + "). Further identity read failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum IdentityReadStatus
    {
        Success,
        Unavailable,
        WrongThread,
        NotServer,
        SessionUnavailable,
        IdentityUnavailable,
        Failed
    }

    public enum IdentitySource
    {
        DedicatedRegistration,
        TransportEndpoint
    }

    public enum RoleDataStatus
    {
        Unavailable,
        Missing,
        Present
    }

    /// <summary>Known game role values, not flags or an ordered permission scale.</summary>
    public enum GameRole
    {
        Unknown = 0,
        Guest = 1,
        Banned = 2,
        Trusted = 4,
        Admin = 16,
        Owner = 32
    }

    /// <summary>Managed identity observations. No native connection, role manager or player record is retained.</summary>
    public sealed class ServerIdentitySnapshot
    {
        internal ServerIdentitySnapshot(ServerSession session, ulong gameSteamId,
            global::Modstoke.Sons.IdentitySource identitySource, ulong? registeredSteamId,
            RoleDataStatus roleStatus, GameRole? storedRole, string displayName)
        {
            Session = session;
            GameSteamId = gameSteamId;
            IdentitySource = identitySource;
            RegisteredSteamId = registeredSteamId;
            RoleStatus = roleStatus;
            StoredRole = storedRole;
            DisplayName = displayName;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>The exact requested lifetime handle. Its current state can change after this read.</summary>
        public ServerSession Session { get; }
        /// <summary>Nonzero identity used by the game. This does not independently verify a Steam account.</summary>
        public ulong GameSteamId { get; }
        public global::Modstoke.Sons.IdentitySource IdentitySource { get; }
        /// <summary>Fresh registration-table observation, not the session handle's cached metadata.</summary>
        public ulong? RegisteredSteamId { get; }
        public RoleDataStatus RoleStatus { get; }
        /// <summary>Raw stored value when present, including unknown numeric values. No default role is inferred.</summary>
        public GameRole? StoredRole { get; }
        /// <summary>Name the client sent when it joined, or null. Display text only: the client chose it.</summary>
        /// <remarks>Never match players by it. Escape it before showing it in rich-text UI.</remarks>
        public string DisplayName { get; }
        /// <summary>Read completion time, not authentication or role-change time.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
