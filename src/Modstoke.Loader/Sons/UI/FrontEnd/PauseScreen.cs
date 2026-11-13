// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Localization;
using Sons.Gameplay.GameSetup;
using TMPro;
using UnityEngine;

namespace Modstoke.Loader
{
    /// <summary>
    /// The pause menu, in place of the game's: a column of choices on the veil over the game, with what this game
    /// is beside it. Resume, options, the players of a multiplayer game, mods, and leaving for the title or the
    /// desktop, each of which asks first.
    /// </summary>
    internal sealed class PauseScreen : FrontScreen
    {
        private const float StripHeight = 143, ColumnWidth = 420, EntryHeight = 52, EntryGap = 6;

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
            _title.rectTransform.At(pad, U(46), U(1100), U(66));
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
            new Cascade(_rects);
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
            var item = Add(rect, () => Run(entry), focused => entry.Label.color = focused ? MenuKit.Ink : MenuKit.Soft);
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
