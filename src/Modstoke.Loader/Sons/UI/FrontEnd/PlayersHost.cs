// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

extern alias SonsGame;

using System;
using System.Collections.Generic;
using Sons.Gameplay.GameSetup;
using Sons.Multiplayer;
using Sons.MultiplayerLegacy;
using Steamworks;
using UnityEngine;

namespace Modstoke.Loader
{
    /// <summary>One player in the game, or one on its banned list.</summary>
    internal sealed record PlayerEntry(ulong Id, string Name, Texture2D Avatar, int Role, bool Host, bool Self);

    /// <summary>
    /// Who is in this multiplayer game and what can be done about them, for Modstoke's Players page. The roster
    /// is the game's own replicated server info, so it reads the same for the host and for a player who joined.
    /// Kicks, bans, roles and the multiplayer rules go through the game's own admin command, which the server
    /// checks the sender's rights for, with its usual messages; muting is the game's own, on this PC only.
    /// </summary>
    internal static class PlayersHost
    {
        // The game's PlayerRoles values.
        internal const int Guest = 1, Banned = 2, Trusted = 4, Admin = 16, Owner = 32;

        internal static ulong SelfId => Safe(() => SteamUser.GetSteamID().m_SteamID, 0UL);

        private static bool Server => Safe(() => BoltNetwork.isServer, false);

        private static bool Dedicated => Safe(() => GameServerManager.IsDedicated, false);

        internal static List<PlayerEntry> Players()
        {
            var players = new List<PlayerEntry>();
            var self = SelfId;
            var ids = Safe(() => SonsGame::CoopServerInfo.GetSteamIds(), null);
            var roles = Safe(() => SonsGame::CoopServerInfo.GetPlayerRoles(), null);
            if (ids != null)
            {
                for (var i = 0; i < ids.Length; i++)
                {
                    var token = ids.GetValue(i)?.TryCast<SteamIdToken>();
                    if (token == null || token.SteamId == 0) continue;
                    var role = roles != null && i < roles.Length ? roles.GetValue(i) : 0;
                    players.Add(Entry(token.SteamId, role, false, token.SteamId == self));
                }
            }
            // A hosted game's own host plays too, but is not one of the server's clients.
            var owner = Dedicated ? 0UL : Safe(() => CoopLobbyManager.ActiveOwnerSteamId().m_SteamID, 0UL);
            if (owner != 0)
            {
                var index = players.FindIndex(player => player.Id == owner);
                if (index >= 0) players[index] = players[index] with { Host = true, Role = Owner };
                else players.Insert(0, Entry(owner, Owner, true, owner == self));
            }
            players.Sort((a, b) => a.Host != b.Host ? (a.Host ? -1 : 1) : b.Role.CompareTo(a.Role) != 0 ? b.Role.CompareTo(a.Role)
                : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return players;
        }

        /// <summary>The banned players, as the server knows them; only the host has the list at hand.</summary>
        internal static List<PlayerEntry> BannedPlayers()
        {
            var banned = new List<PlayerEntry>();
            if (!Server) return banned;
            try
            {
                var roles = MultiplayerPlayerRoles._instance;
                if (roles == null || !roles) return banned;
                roles.GetBannedPlayers(out var list);
                for (var i = 0; list != null && i < list.Count; i++)
                {
                    var player = list[i];
                    if (player == null) continue;
                    var entry = Entry(player.GetPlayerSteamId(), Banned, false, false);
                    var name = player.GetPlayerName();
                    banned.Add(string.IsNullOrWhiteSpace(name) ? entry : entry with { Name = name });
                }
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogDebug("The banned list could not be read: " + error.Message);
            }
            return banned;
        }

        /// <summary>Whether this player may kick, ban and set roles: the host, the game's owner or an admin.</summary>
        internal static bool Privileged(IReadOnlyList<PlayerEntry> players) => Server && !Dedicated || SelfRole(players) >= Admin;

        /// <summary>Whether this player may act on <paramref name="target"/>, by the same rules the server checks.</summary>
        internal static bool CanManage(PlayerEntry target, IReadOnlyList<PlayerEntry> players)
        {
            if (target == null || target.Self || target.Host || target.Role >= Owner || !Privileged(players)) return false;
            // Only the host or the owner acts on an admin.
            return target.Role < Admin || Server && !Dedicated || SelfRole(players) >= Owner;
        }

        private static int SelfRole(IReadOnlyList<PlayerEntry> players)
        {
            foreach (var player in players)
                if (player.Self) return player.Role;
            return 0;
        }

        internal static bool Muted(ulong id) => Safe(() => CoopSteamVoicePlayer.IsPlayerSteamIdMuted(id), false);

        internal static void SetMuted(ulong id, bool muted) => CoopSteamVoicePlayer.SetPlayerSteamIdMuted(id, muted);

        internal static void Kick(ulong id) => CoopAdminCommand.Send(CoopAdminCommand.Command_playerkick, id);

        internal static void Ban(ulong id) => CoopAdminCommand.Send(CoopAdminCommand.Command_playerban, id);

        internal static void SetRole(ulong id, int role)
        {
            var command = role switch
            {
                Admin => CoopAdminCommand.Command_setplayeradmin,
                Trusted => CoopAdminCommand.Command_setplayertrusted,
                _ => CoopAdminCommand.Command_setplayerguest
            };
            CoopAdminCommand.Send(command, id);
        }

        internal static void ShowProfile(ulong id) => SteamFriends.ActivateGameOverlayToUser("steamid", new CSteamID(id));

        internal static bool Cheats => Safe(GameSetupManager.GetMultiplayerCheatsSetting, false);

        internal static void SetCheats(bool on) => CoopAdminCommand.Send(CoopAdminCommand.Command_setcheatsenabled, on);

        internal static string PvpDamage
            => Safe(() => GameSetupManager.GetSetting(GameSetupManager.PvpDamageKey, GameSetupManager.DefaultPvpDamageSettingValue), "");

        internal static void SetPvpDamage(string value) => CoopAdminCommand.Send(CoopAdminCommand.Command_setpvpdamage, value);

        private static PlayerEntry Entry(ulong id, int role, bool host, bool self)
        {
            var name = Safe(() => SteamFriends.GetFriendPersonaName(new CSteamID(id)), "");
            Texture2D avatar = null;
            try
            {
                var details = CoopLobbyManager.GetMemberDetails(new CSteamID(id), false);
                var texture = details != null ? details.Texture : null;
                avatar = texture != null && texture ? texture : null;
            }
            catch (Exception)
            {
                // The row keeps its plain picture box.
            }
            return new PlayerEntry(id, string.IsNullOrWhiteSpace(name) ? id.ToString() : name, avatar, role, host, self);
        }

        private static T Safe<T>(Func<T> read, T fallback)
        {
            try { return read(); }
            catch (Exception) { return fallback; }
        }
    }
}
