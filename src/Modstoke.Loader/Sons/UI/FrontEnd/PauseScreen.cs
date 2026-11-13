// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using Modstoke.Localization;
using Modstoke.Sons;
using Sons.Gameplay.GameSetup;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Loader
{
    /// <summary>
    /// The pause menu, in place of the game's: a column of choices on the veil over the game, and beside it where
    /// the game stands (its day, time of day and season, its mode) and, online, who is playing. Resume, options,
    /// the players of a multiplayer game, mods, and leaving for the title or the desktop, each of which asks first.
    /// </summary>
    internal sealed class PauseScreen : FrontScreen
    {
        private const float StripHeight = 143, ColumnWidth = 420, EntryHeight = 52, EntryGap = 6;
        private const float PanelWidth = 440, PlayerHeight = 40, RefreshSeconds = 1f;
        private const int ShownPlayers = 6;

        private sealed class PlayerView
        {
            internal RectTransform Rect;
            internal RawImage Avatar;
            internal TextMeshProUGUI Name, Tag;
        }

        private readonly List<PlayerView> _players = new();
        private TextMeshProUGUI _day, _clock, _playing, _worldLabel, _playersLabel;
        private RectTransform _playersBox;
        private float _nextRefresh;

        private sealed class Entry
        {
            internal string Key;
            internal Func<string> Name;
            internal Action Run;
            internal TextMeshProUGUI Label;
        }

        private readonly List<Entry> _entries = new();
        private TextMeshProUGUI _heading, _title, _line;

        protected override void Build()
        {
            _entries.Clear();
            _rects.Clear();
            var pad = U(60);
            MenuKit.Backdrop(Root, U(StripHeight - 24), U(110));
            _heading = MenuKit.Text(Root, "Heading", "", U(14), true, MenuKit.Muted);
            _heading.characterSpacing = 32;
            _heading.rectTransform.At(pad, U(30), U(900), U(18));
            _title = MenuKit.Text(Root, "Title", "", U(50), true, MenuKit.Ink, TextAlignmentOptions.MidlineLeft, true);
            _title.characterSpacing = 5;
            _title.rectTransform.At(pad, U(39), U(1100), U(80));
            _line = MenuKit.Text(Root, "Line", "", U(17), false, MenuKit.Soft);
            _line.rectTransform.At(pad, U(112), U(1100), U(26));

            var column = MenuKit.Box(Root, "Choices").Place(0, 0, 0, 1, pad, U(StripHeight), -(pad + U(ColumnWidth)), U(160));
            MenuKit.Hairline(column, "Top", 0);
            var mark = new Glide(column, false);
            AddEntry(column, mark, "pause.resume", PauseHost.Resume);
            AddEntry(column, mark, "title.options", () => FrontEnd.Push(new OptionsScreen()));
            if (PauseHost.Multiplayer) AddEntry(column, mark, "pause.players", () => FrontEnd.Push(new PlayersScreen()));
            AddEntry(column, mark, "title.mods", MenuController.Show);
            // Buttons mods added to the game's pause bar, which is put away while this screen shows.
            foreach (var (label, press) in UiMenuButtons.Registered(SonsSdk.ESonsScene.Game))
                AddEntry(column, mark, "mod:" + label, press, () => label);
            AddEntry(column, mark, "pause.quitTitle", () => FrontEnd.Push(new ConfirmScreen("pause.quitTitleTitle",
                PauseHost.Multiplayer ? "pause.quitTitleBodyOnline" : "pause.quitTitleBody", "pause.quitTitle", "common.cancel",
                PauseHost.QuitToTitle, cautious: true)));
            AddEntry(column, mark, "pause.quitDesktop", () => FrontEnd.Push(new ConfirmScreen("confirm.quitTitle",
                PauseHost.Multiplayer ? "pause.quitTitleBodyOnline" : "pause.quitTitleBody", "confirm.quit", "common.cancel",
                () =>
                {
                    // The game leaves through the title, where its quitting work runs, and closes from there.
                    FrontEnd.QuitWhenTitleLoads();
                    PauseHost.QuitToTitle();
                }, cautious: true)));
            BuildPanel(pad);
            new Cascade(_rects);
        }

        // Where the game stands: a large day count over the time of day and season, and online a short list
        // of who is playing.
        private void BuildPanel(float pad)
        {
            _players.Clear();
            var panel = MenuKit.Box(Root, "Session").Place(1, 0, 1, 1, -(pad + U(PanelWidth)), U(StripHeight), pad, U(160));
            _rects.Add(panel);
            MenuKit.Hairline(panel, "Top", 0);
            _worldLabel = Heading(panel, "World", U(16));
            _day = MenuKit.Text(panel, "Day", "", U(46), true, MenuKit.Ink);
            _day.characterSpacing = 4;
            _day.rectTransform.Strip(U(42), U(58), 0, 0);
            _clock = MenuKit.Text(panel, "Clock", "", U(20), false, MenuKit.Soft);
            _clock.rectTransform.Strip(U(102), U(28), 0, 0);
            if (!PauseHost.Multiplayer) return;
            MenuKit.Hairline(panel, "PlayersRule", U(150));
            _playersLabel = Heading(panel, "Players", U(166));
            _playersBox = MenuKit.Box(panel, "List").Place(0, 0, 1, 1, 0, 0, 0, U(194));
            for (var i = 0; i < ShownPlayers; i++)
            {
                var view = new PlayerView { Rect = MenuKit.Box(_playersBox, "Player " + i).Strip(i * U(PlayerHeight), U(PlayerHeight), 0, 0) };
                var picture = MenuKit.Box(view.Rect, "Picture").At(0, U((PlayerHeight - 28) / 2), U(28), U(28));
                MenuKit.Fill(picture, null, MenuKit.Cell);
                view.Avatar = MenuKit.Box(picture, "Image").Place(0, 0, 1, 1).gameObject.AddComponent<RawImage>();
                view.Avatar.raycastTarget = false;
                // Steam's pictures arrive upside down in Unity's texture space.
                view.Avatar.uvRect = new Rect(0, 1, 1, -1);
                view.Name = MenuKit.Text(view.Rect, "Name", "", U(18), false, MenuKit.Soft, TextAlignmentOptions.MidlineLeft, true);
                view.Name.rectTransform.Place(0, 0, 1, 1, U(42), 0, U(90), 0);
                view.Tag = MenuKit.Text(view.Rect, "Tag", "", U(13), true, MenuKit.Muted, TextAlignmentOptions.MidlineRight);
                view.Tag.characterSpacing = 14;
                view.Tag.rectTransform.Place(1, 0, 1, 1, -U(90), 0, 0, 0);
                _players.Add(view);
            }
            _playing = MenuKit.Text(_playersBox, "Count", "", U(15), false, MenuKit.Muted);
            _playing.rectTransform.Strip(ShownPlayers * U(PlayerHeight) + U(6), U(24), 0, 0);
        }

        private static TextMeshProUGUI Heading(RectTransform panel, string name, float y)
        {
            var heading = MenuKit.Text(panel, name, "", U(13), true, MenuKit.Muted);
            heading.characterSpacing = 28;
            heading.rectTransform.Strip(y, U(20), 0, 0);
            return heading;
        }

        internal override void Tick()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + RefreshSeconds;
            PaintPanel();
        }

        private void PaintPanel()
        {
            if (_day == null) return;
            var culture = Languages.CultureOf(Languages.Current);
            if (EnvironmentReads.TryReadClock(out var clock, out _))
            {
                var time = clock.EffectiveTime;
                _day.text = Text.Get("pause.day", ("day", time.Days)).ToUpper(culture);
                var parts = new List<string> { DateTime.Today.Add(new TimeSpan(time.Hours, time.Minutes, 0)).ToString("t", culture) };
                if (EnvironmentReads.TryReadSeason(out var season, out _) && season.ActiveSeason is { } active)
                    parts.Add(Text["options.value." + active.ToString().ToLowerInvariant()]);
                _clock.text = string.Join("  ·  ", parts);
            }
            else
            {
                _day.text = "";
                _clock.text = "";
            }
            if (_playersBox == null) return;
            var players = PlayersHost.Players();
            for (var i = 0; i < _players.Count; i++)
            {
                var view = _players[i];
                var shown = i < players.Count;
                view.Rect.gameObject.SetActive(shown);
                if (!shown) continue;
                var player = players[i];
                view.Avatar.texture = player.Avatar;
                view.Avatar.enabled = player.Avatar != null;
                view.Name.text = player.Name;
                view.Name.color = player.Self ? MenuKit.Ink : MenuKit.Soft;
                view.Tag.text = (player.Host ? Text["lobby.host"] : player.Self ? Text["lobby.you"] : "").ToUpper(culture);
            }
            _playing.text = Text.Count("pause.playing", players.Count);
        }

        private readonly List<RectTransform> _rects = new();

        private void AddEntry(RectTransform column, Glide mark, string key, Action run, Func<string> name = null)
        {
            var rect = MenuKit.Box(column, key).Strip(U(22 + _entries.Count * (EntryHeight + EntryGap)), U(EntryHeight), 0, 0);
            var entry = new Entry { Key = key, Name = name ?? (() => Text[key]), Run = run };
            _rects.Add(rect);
            entry.Label = MenuKit.Text(rect, "Label", "", U(24), true, MenuKit.Soft);
            entry.Label.characterSpacing = 12;
            entry.Label.rectTransform.Place(0, 0, 1, 1, U(20), 0, U(16), 0);
            _entries.Add(entry);
            var item = Add(rect, () => Run(entry), focused => entry.Label.color = focused ? MenuKit.OnPlate : MenuKit.Soft);
            item.Mark = mark;
            item.Flash = Flashing(entry.Label);
        }

        internal override void Retext()
        {
            var culture = Languages.CultureOf(Languages.Current);
            // A multiplayer game carries on while the menu is open, so it is not called paused there.
            _heading.text = Text[PauseHost.Multiplayer ? "title.multiplayer" : "title.singlePlayer"].ToUpper(culture);
            _title.text = Text[PauseHost.Multiplayer ? "pause.titleOnline" : "pause.title"].ToUpper(culture);
            _line.text = ModeLine();
            foreach (var entry in _entries) entry.Label.text = entry.Name().ToUpper(culture);
            _worldLabel.text = Text["custom.tab.world"].ToUpper(culture);
            if (_playersLabel != null) _playersLabel.text = Text["lobby.players"].ToUpper(culture);
            PaintPanel();
        }

        internal override void Opened() => Focus(First());

        // Escape and the controller's Back close the pause menu through the game's own handling.
        internal override bool OnBack() => true;

        internal override bool Blurs => false;

        internal override IReadOnlyList<PromptSpec> Prompts => new[]
        {
            new PromptSpec(Text["pause.resume"], "B", "ESC", PauseHost.Resume),
            new PromptSpec(Text["common.select"], "A", "ENTER", null)
        };

        private static string ModeLine()
        {
            try
            {
                var mode = GameSetupManager.GetMode(Mode.Normal).ToString();
                var key = "mode." + char.ToLowerInvariant(mode[0]) + mode.Substring(1);
                return Text.Has(key) ? Text[key] : mode;
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static void Run(Entry entry)
        {
            try
            {
                entry.Run();
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The pause menu could not open " + entry.Key + ": " + error.Message);
            }
        }
    }
}
