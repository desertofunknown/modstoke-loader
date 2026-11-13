// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Localization;
using Sons.Save;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Loader
{
    /// <summary>
    /// The title menu: the game's own composition (a row of choices along the bottom strip, beside its logo)
    /// drawn by Modstoke, with one focus highlight for mouse, keyboard and controller.
    /// </summary>
    internal sealed class TitleScreen : FrontScreen
    {
        private sealed class Entry
        {
            internal string Key;
            internal Action Run;
            internal RectTransform Rect;
            internal TextMeshProUGUI Label;
            internal float Width;
        }

        private readonly List<Entry> _entries = new();
        private Glide _underline;
        private Vector2 _origin;
        private float _nextMeasure;

        protected override void Build()
        {
            _entries.Clear();
            _marks.Clear();
            AddEntry("title.singlePlayer", () => FrontEnd.Push(new SavedGamesScreen(SaveGameType.SinglePlayer)));
            AddEntry("title.multiplayer", () => Leave(GameMenus.Join));
            AddEntry("title.options", () => FrontEnd.Push(new OptionsScreen()));
            AddEntry("title.mods", MenuController.Show);
            AddEntry("title.exit", () => FrontEnd.Push(new ConfirmScreen("confirm.quitTitle", "confirm.quitBody", "confirm.quit",
                "common.cancel", Application.Quit, cautious: true)));
            // Over the words' glow, so the dark spill never dims it.
            _underline = new Glide(Root, true);
        }

        internal override void Retext()
        {
            foreach (var entry in _entries)
            {
                var text = Text[entry.Key].ToUpper(Languages.CultureOf(Languages.Current));
                entry.Label.text = text;
                entry.Width = entry.Label.GetPreferredValues(text).x + U(36);
            }
            Layout(Origin());
            _underline.Follow();
        }

        internal override void Opened() => Focus(First());

        // The game lays its own menu out over the first frames, so its position is read again for a while.
        internal override void Tick()
        {
            if (Time.unscaledTime < _nextMeasure) return;
            _nextMeasure = Time.unscaledTime + 0.5f;
            var origin = Origin();
            if ((origin - _origin).sqrMagnitude <= 1f) return;
            Layout(origin);
            _underline.Follow();
        }

        // The title menu is the first screen; Back has nowhere to go.
        internal override bool OnBack() => true;

        // The title scene is the backdrop of the title menu itself.
        internal override bool Blurs => false;

        // The game loads another scene for these; the menu steps aside rather than sit over the fade.
        private static void Leave(Action open)
        {
            open();
            FrontEnd.StepAside();
        }

        // Where the row starts: where the game's own row of choices was, kept clear of its logo.
        private Vector2 Origin()
        {
            var gap = U(10);
            var total = -gap;
            foreach (var entry in _entries) total += entry.Width + gap;
            float x = U(72), y = U(79);
            if (GameMenus.TryBar(out var barLeft, out var barMiddle) && barMiddle > U(30) && barMiddle < U(150))
            {
                x = barLeft;
                y = barMiddle;
            }
            var limit = Screen.width - U(40);
            if (GameMenus.TryLogo(out var logoLeft, out var logoRight))
            {
                if ((logoLeft + logoRight) / 2f > Screen.width / 2f) limit = logoLeft - U(30);
                else x = Mathf.Max(x, logoRight + U(30));
            }
            if (x + total > limit) x = Mathf.Max(U(24), limit - total);
            return new Vector2(x, y);
        }

        private void Layout(Vector2 origin)
        {
            _origin = origin;
            var x = origin.x;
            foreach (var entry in _entries)
            {
                entry.Rect.anchorMin = entry.Rect.anchorMax = new Vector2(0, 0);
                entry.Rect.pivot = new Vector2(0, 0.5f);
                entry.Rect.anchoredPosition = new Vector2(x, origin.y);
                entry.Rect.sizeDelta = new Vector2(entry.Width, U(54));
                x += entry.Width + U(10);
            }
        }

        private void AddEntry(string key, Action run)
        {
            var entry = new Entry { Key = key, Run = run, Rect = MenuKit.Box(Root, key) };
            MenuKit.GlowBehind(entry.Rect, U(14), 0.5f);
            entry.Label = MenuKit.Text(entry.Rect, "Label", "", U(27), true, MenuKit.Soft, TextAlignmentOptions.Center);
            entry.Label.characterSpacing = 12;
            entry.Label.rectTransform.Place(0, 0, 1, 1);
            // The underline runs under the word, not the whole box around it.
            var under = MenuKit.Box(entry.Rect, "Under").Place(0, 0, 1, 0, U(18), U(6), U(18), -U(9));
            _entries.Add(entry);
            var item = Add(entry.Rect, () => Run(entry), focused => entry.Label.color = focused ? MenuKit.Ink : MenuKit.Soft);
            item.Flash = Flashing(entry.Label);
            _marks[item] = under;
        }

        private readonly Dictionary<Focusable, RectTransform> _marks = new();

        protected override void FocusChanged(Focusable previous, Focusable current)
            => _underline.Target(current != null && _marks.TryGetValue(current, out var under) ? under : null, previous == null);

        private static void Run(Entry entry)
        {
            try
            {
                entry.Run();
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The title menu could not open " + entry.Key + ": " + error.Message);
            }
        }
    }
}
