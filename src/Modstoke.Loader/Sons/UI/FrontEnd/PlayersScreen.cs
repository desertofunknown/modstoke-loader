// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Modstoke.Localization;
using Modstoke.Sons;
using Sons.Gameplay.GameSetup;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Loader
{
    /// <summary>A player on the Players page: their Steam picture and name, their role as the value.</summary>
    internal sealed class PlayerRow : OptionRow
    {
        internal PlayerRow(PlayerEntry player, Action<PlayerEntry> open)
            : base(player.Id != 0 ? "player:" + player.Id : "entity:" + player.Entity)
        {
            Player = player;
            // Without a Steam ID there is no profile to show and nothing the game's commands can act on.
            Open = player.Id != 0 ? () => open(player) : null;
            Ready = true;
        }

        internal PlayerEntry Player { get; }
        internal override Action Open { get; }
        // A row with nothing to open is drawn as a line to read, not as a choice with arrows.
        internal override bool ReadOnly => Player.Id == 0;
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
    /// and admins their role, a kick or a ban, each through the game's own commands. Whoever is talking has the
    /// voice bars after their name, and a player muted here a Muted tag instead. Each player's ping ends their name.
    /// </summary>
    internal sealed class PlayersScreen : SettingsScreen
    {
        private const float RefreshSeconds = 1f;
        private readonly string[] _tabs;
        private readonly bool _privileged;
        private readonly List<VoiceMark> _voices = new();
        private readonly List<PingMark> _pings = new();
        private List<PlayerEntry> _players = new();
        private List<PlayerEntry> _banned = new();
        private OptionRow _focused;
        private string _shown = "";
        private float _nextRoster, _voiceTime;

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
            if (entry.Id == 0) return lines[0];
            if (!entry.Self && PlayersHost.Muted(entry.Id)) lines.Add(Text["players.mutedNote"]);
            lines.Add(Text["players.openHint"]);
            return string.Join("\n\n", lines);
        }

        protected override OptionTab BuildTab(string id)
        {
            // Each build of the list starts here, before its rows are made, so the old rows' marks go with them.
            _voices.Clear();
            _pings.Clear();
            return id switch
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
        }

        protected override void BuildNameMark(OptionRow row, TextMeshProUGUI label)
        {
            if (row is not PlayerRow player || player.Player.Role == PlayersHost.Banned) return;
            // The ping first: it takes the end of the name's box, which the voice mark then measures.
            _pings.Add(new PingMark(player, label));
            _voices.Add(new VoiceMark(player, label));
        }

        protected override void FocusChanged(Focusable previous, Focusable current)
        {
            base.FocusChanged(previous, current);
            _focused = FocusedRow;
        }

        internal override void Tick()
        {
            base.Tick();
            var now = PlayerVoice.Now;
            var dt = Math.Min(now - _voiceTime, 0.1f);
            _voiceTime = now;
            foreach (var mark in _voices) mark.Paint(now, dt, ReferenceEquals(mark.Row, _focused));
            foreach (var mark in _pings) mark.Paint(ReferenceEquals(mark.Row, _focused));
            if (Time.unscaledTime < _nextRoster) return;
            _nextRoster = Time.unscaledTime + RefreshSeconds;
            foreach (var mark in _pings) mark.Read();
            // Someone joined, left, changed role or their picture arrived: the lists are built again.
            ReadRoster();
            var key = RosterKey();
            if (key == _shown) return;
            _shown = key;
            Rebuild();
        }

        internal static string RoleKey(PlayerEntry player)
        {
            if (player.Id == 0) return "players.role.player";
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
            => string.Join("|", _players.Concat(_banned).Select(player => player.Id + ":" + player.Entity + ":" + player.Name + ":" + player.Role
                + (player.Avatar != null ? "+" : "-")));

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

        // A player's ping at the end of their name's room, as the server list draws one: three bars and the round trip,
        // amber while that player's link is poor. Nothing shows where this PC can't know it, as for the host.
        private sealed class PingMark
        {
            private const float Width = 84;
            private readonly RectTransform _rect;
            private readonly Image[] _bars = new Image[3];
            private readonly TextMeshProUGUI _value;
            private int _ms = -1, _lit;
            private bool _plate, _poor, _shown = true;

            internal PingMark(PlayerRow row, TextMeshProUGUI label)
            {
                Row = row;
                // The name's box gives up its end, so a long name ends in its ellipsis before the mark.
                var name = label.rectTransform;
                var right = -name.offsetMax.x;
                name.offsetMax = new Vector2(-(right + FrontEnd.U(Width)), name.offsetMax.y);
                // A child of the name, as the voice mark is, so it draws over the focus plate, in the room the name gave up.
                _rect = MenuKit.Box(name, "Ping").Place(1, 0, 1, 1, 0, 0, -FrontEnd.U(Width), 0);
                for (var b = 0; b < 3; b++)
                {
                    var bar = MenuKit.Box(_rect, "Bar " + b);
                    bar.anchorMin = bar.anchorMax = new Vector2(0, 0.5f);
                    bar.pivot = Vector2.zero;
                    bar.anchoredPosition = new Vector2(FrontEnd.U(b * 6), -FrontEnd.U(7));
                    bar.sizeDelta = new Vector2(FrontEnd.U(4), FrontEnd.U(6 + b * 4));
                    _bars[b] = MenuKit.Fill(bar, null, MenuKit.BarOff);
                }
                _value = MenuKit.Text(_rect, "Value", "", FrontEnd.U(14), false, MenuKit.Muted);
                _value.rectTransform.Place(0, 0, 1, 1, FrontEnd.U(24), 0, 0, 0);
                Read();
            }

            internal PlayerRow Row { get; }

            internal void Read()
            {
                var known = PlayersHost.TryLink(Row.Player, out var link);
                if (known != _shown)
                {
                    _shown = known;
                    _rect.gameObject.SetActive(known);
                }
                if (!known || link.RoundTripMs == _ms && link.Poor == _poor) return;
                _ms = link.RoundTripMs;
                _poor = link.Poor;
                // The server list's own steps for its bars.
                _lit = _ms <= 0 ? 0 : _ms < 60 ? 3 : _ms < 110 ? 2 : 1;
                _value.text = _ms > 0 ? FrontEnd.Text.Get("multiplayer.details.pingValue", ("ping", _ms)) : "—";
                Paint(_plate, true);
            }

            internal void Paint(bool plate, bool force = false)
            {
                if (plate == _plate && !force) return;
                _plate = plate;
                var lit = plate ? MenuKit.OnPlate : _poor ? MenuKit.Amber : MenuKit.Ink;
                for (var b = 0; b < 3; b++) _bars[b].color = b < _lit ? lit : plate ? MenuKit.OnPlateTrack : MenuKit.BarOff;
                // On the plate the muted grey all but vanishes, so the figure takes the row's dark like its name.
                _value.color = plate ? MenuKit.OnPlate : _poor ? MenuKit.Amber : MenuKit.Muted;
            }
        }

        // A player's voice just after their name: the voice bars while they talk, or a tag while they are muted here.
        // On the focus plate both turn dark with the rest of the row. Your own row talks while your voice goes out.
        private sealed class VoiceMark
        {
            private const float MuteSeconds = 0.5f;
            private readonly VoiceBars _bars;
            private readonly CanvasGroup _group;
            private readonly RectTransform _tag;
            private readonly Image _tagFill;
            private readonly TextMeshProUGUI _tagText;
            private float _level, _nextMute;
            private int _muteVersion = -1;
            private bool _muted, _plate, _talking;

            internal VoiceMark(PlayerRow row, TextMeshProUGUI label)
            {
                Row = row;
                var rect = label.rectTransform;
                // After the name as drawn, kept clear of the ellipsis a very long name ends in.
                var name = label.GetPreferredValues(label.text).x;
                var room = rect.rect.width - FrontEnd.U(70);
                var x = (room > 0 ? Math.Min(name, room) : name) + FrontEnd.U(12);
                _bars = new VoiceBars(rect, "Voice", FrontEnd.U(13));
                _bars.Rect.anchorMin = _bars.Rect.anchorMax = _bars.Rect.pivot = new Vector2(0, 0.5f);
                _bars.Rect.anchoredPosition = new Vector2(x, 0);
                _bars.Tint(MenuKit.Green);
                _group = _bars.Rect.gameObject.AddComponent<CanvasGroup>();
                _bars.Rect.gameObject.SetActive(false);
                var text = FrontEnd.Text["players.muted"].ToUpper(Languages.CultureOf(Languages.Current));
                _tag = MenuKit.Box(rect, "Muted");
                _tagFill = MenuKit.Fill(_tag, null, MenuKit.ChipOff);
                _tagText = MenuKit.Text(_tag, "Label", text, FrontEnd.U(11), true, MenuKit.Muted, TextAlignmentOptions.Center);
                _tagText.characterSpacing = 12;
                _tagText.rectTransform.Place(0, 0, 1, 1);
                _tag.anchorMin = _tag.anchorMax = _tag.pivot = new Vector2(0, 0.5f);
                _tag.sizeDelta = new Vector2(_tagText.GetPreferredValues(text).x + FrontEnd.U(14), FrontEnd.U(20));
                _tag.anchoredPosition = new Vector2(x, 0);
                _tag.gameObject.SetActive(false);
            }

            internal PlayerRow Row { get; }

            internal void Paint(float now, float dt, bool plate)
            {
                var player = Row.Player;
                if (!player.Self && player.Id != 0 && (now >= _nextMute || _muteVersion != PlayerVoice.MuteVersion))
                {
                    _nextMute = now + MuteSeconds;
                    _muteVersion = PlayerVoice.MuteVersion;
                    var muted = PlayersHost.Muted(player.Id);
                    if (muted != _muted)
                    {
                        _muted = muted;
                        _tag.gameObject.SetActive(muted);
                    }
                }
                if (plate != _plate)
                {
                    _plate = plate;
                    _bars.Tint(plate ? MenuKit.OnPlate : MenuKit.Green);
                    _tagFill.color = plate ? MenuKit.OnPlateTrack : MenuKit.ChipOff;
                    _tagText.color = plate ? MenuKit.OnPlate : MenuKit.Muted;
                }
                var speaking = !_muted && (player.Self ? PlayerVoice.Transmitting : PlayerVoice.Speaking(player.Id, player.Entity));
                _level = VoiceBars.Step(_level, speaking ? 1f : 0f, dt / (speaking ? 0.12f : 0.35f));
                var talking = _level > 0f;
                if (talking != _talking)
                {
                    _talking = talking;
                    _bars.Rect.gameObject.SetActive(talking);
                }
                if (!talking) return;
                _group.alpha = VoiceBars.Smooth(_level);
                _bars.Paint(_level, now);
            }
        }
    }
}
