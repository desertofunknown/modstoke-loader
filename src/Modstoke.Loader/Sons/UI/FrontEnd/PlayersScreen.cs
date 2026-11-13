// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Sons.Gameplay.GameSetup;
using UnityEngine;

namespace Modstoke.Loader
{
    /// <summary>A player on the Players page: their Steam picture and name, their role as the value.</summary>
    internal sealed class PlayerRow : OptionRow
    {
        internal PlayerRow(PlayerEntry player, Action<PlayerEntry> open) : base("player:" + player.Id)
        {
            Player = player;
            Open = () => open(player);
            Ready = true;
        }

        internal PlayerEntry Player { get; }
        internal override Action Open { get; }
        internal override string Value => Text[PlayersScreen.RoleKey(Player)];
        internal override string DefaultValue => "";
        internal override bool IsDefault => true;
        internal override bool Step(int direction, bool wrap) => false;
        internal override bool Reset() => false;
        internal override bool HasPicture => true;
        internal override Texture Picture => Player.Avatar;
        internal override Color? Tint => Player.Role == PlayersHost.Banned ? MenuKit.Red : null;
    }

    /// <summary>
    /// The players of a multiplayer game, in place of the game's own page: everyone in it with their Steam
    /// picture and role, the banned list for the host, and the multiplayer rules for those allowed to change them.
    /// Choosing a player offers what can be done: their Steam profile, muting them on this PC, and for the host
    /// and admins their role, a kick or a ban, each through the game's own commands.
    /// </summary>
    internal sealed class PlayersScreen : SettingsScreen
    {
        private const float RefreshSeconds = 1f;
        private readonly string[] _tabs;
        private readonly bool _privileged;
        private List<PlayerEntry> _players = new();
        private List<PlayerEntry> _banned = new();
        private string _shown = "";
        private float _nextRoster;

        internal PlayersScreen()
        {
            _privileged = PlayersHost.Privileged(PlayersHost.Players());
            _tabs = _privileged ? new[] { "players", "banned", "rules" } : new[] { "players" };
            ReadRoster();
            _shown = RosterKey();
        }

        protected override string EmptyText => Text[TabIds[TabIndex] == "banned" ? "players.noBans" : "players.nobody"];

        protected override IReadOnlyList<string> TabIds => _tabs;
        protected override string Heading => Text["title.multiplayer"];
        protected override string Title => Text["pause.players"];
        protected override bool CanReset => false;
        protected override string TabName(string id) => Text["players.tab." + id];
        protected override string SectionName(string id) => Text["players.section." + id];

        protected override string ItemName(OptionRow row) => row switch
        {
            PlayerRow player => player.Player.Name,
            _ => Text["custom.item." + row.Id + ".name"]
        };

        protected override string ItemAbout(OptionRow row)
        {
            if (row is not PlayerRow player) return Text["custom.item." + row.Id + ".about"];
            var entry = player.Player;
            var lines = new List<string> { Text[RoleKey(entry) + "About"] };
            if (!entry.Self && PlayersHost.Muted(entry.Id)) lines.Add(Text["players.mutedNote"]);
            lines.Add(Text["players.openHint"]);
            return string.Join("\n\n", lines);
        }

        protected override OptionTab BuildTab(string id) => id switch
        {
            "banned" => new OptionTab(id, new[] { OptionsCatalog.Section("banned", _banned.Select(Row).ToArray()) }),
            "rules" => new OptionTab(id, new[]
            {
                OptionsCatalog.Section("rules",
                    OptionsCatalog.Make("cheats", () => new SwitchRow("cheats", () => PlayersHost.Cheats, PlayersHost.SetCheats,
                        GameSetupManager.DefaultMultiplayerCheatsSettingValue)),
                    OptionsCatalog.Make("pvpDamage", PvpRow))
            }),
            _ => new OptionTab(id, new[] { OptionsCatalog.Section("online", _players.Select(Row).ToArray()) })
        };

        internal override void Tick()
        {
            base.Tick();
            if (Time.unscaledTime < _nextRoster) return;
            _nextRoster = Time.unscaledTime + RefreshSeconds;
            // Someone joined, left, changed role or their picture arrived: the lists are built again.
            ReadRoster();
            var key = RosterKey();
            if (key == _shown) return;
            _shown = key;
            Rebuild();
        }

        internal static string RoleKey(PlayerEntry player)
        {
            if (player.Host) return "players.role.host";
            if (player.Role >= PlayersHost.Owner) return "players.role.owner";
            if (player.Role >= PlayersHost.Admin) return "players.role.admin";
            if (player.Role >= PlayersHost.Trusted) return "players.role.trusted";
            if (player.Role == PlayersHost.Banned) return "players.role.banned";
            return "players.role.guest";
        }

        private void ReadRoster()
        {
            _players = PlayersHost.Players();
            _banned = _privileged ? PlayersHost.BannedPlayers() : new List<PlayerEntry>();
        }

        private string RosterKey()
            => string.Join("|", _players.Concat(_banned).Select(player => player.Id + ":" + player.Role + (player.Avatar != null ? "+" : "-")));

        private OptionRow Row(PlayerEntry player) => new PlayerRow(player, OpenActions);

        private static OptionRow PvpRow()
        {
            var list = GameSetupManager.PvpDamageSettings;
            var choices = new List<(string Value, Func<string> Name)>();
            for (var i = 0; list != null && i < list.Count; i++)
            {
                var value = list[i];
                if (!string.IsNullOrEmpty(value)) choices.Add((value, () => OptionText.Describe(value)));
            }
            return new ChoiceRow("pvpDamage", () => PlayersHost.PvpDamage, value =>
            {
                PlayersHost.SetPvpDamage(value);
                return true;
            }, GameSetupManager.DefaultPvpDamageSettingValue, choices.ToArray());
        }

        // What can be done with one player, by what this player is allowed to do.
        private void OpenActions(PlayerEntry player)
        {
            var actions = new List<MenuAction> { new(Text["lobby.profile"], () => PlayersHost.ShowProfile(player.Id)) };
            if (player.Role == PlayersHost.Banned)
            {
                actions.Add(new MenuAction(Text["players.unban"], () => PlayersHost.SetRole(player.Id, PlayersHost.Guest)));
            }
            else if (!player.Self)
            {
                var muted = PlayersHost.Muted(player.Id);
                actions.Add(new MenuAction(Text[muted ? "players.unmute" : "players.mute"], () => PlayersHost.SetMuted(player.Id, !muted)));
                if (PlayersHost.CanManage(player, _players))
                {
                    foreach (var (role, key) in new[] { (PlayersHost.Guest, "players.makeGuest"), (PlayersHost.Trusted, "players.makeTrusted"),
                                 (PlayersHost.Admin, "players.makeAdmin") })
                        if (player.Role != role) actions.Add(new MenuAction(Text[key], () => PlayersHost.SetRole(player.Id, role)));
                    actions.Add(new MenuAction(Text["players.kick"], () => FrontEnd.Push(new ConfirmScreen("", "players.kickBody",
                        "players.kick", "common.cancel", () => PlayersHost.Kick(player.Id), cautious: true,
                        titleText: Text.Get("players.kickTitle", ("name", player.Name))))));
                    actions.Add(new MenuAction(Text["players.ban"], () => FrontEnd.Push(new ConfirmScreen("", "players.banBody",
                        "players.ban", "common.cancel", () => PlayersHost.Ban(player.Id), cautious: true,
                        titleText: Text.Get("players.banTitle", ("name", player.Name))))));
                }
            }
            FrontEnd.Push(new ActionsScreen(player.Name, Text[RoleKey(player)], actions));
        }
    }
}
