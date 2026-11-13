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
            internal Image Brush;
            internal TextMeshProUGUI Label;
            internal float Width;
        }

        private readonly List<Entry> _entries = new();
        private Vector2 _origin;
        private float _nextMeasure;

        protected override void Build()
        {
            _entries.Clear();
            AddEntry("title.singlePlayer", () => FrontEnd.Push(new SavedGamesScreen(SaveGameType.SinglePlayer)));
            AddEntry("title.multiplayer", () => Leave(GameMenus.Join));
            AddEntry("title.options", () => Leave(GameMenus.Options));
            AddEntry("title.mods", MenuController.Show);
            AddEntry("title.exit", GameMenus.Exit);
        }

        internal override void Retext()
        {
            foreach (var entry in _entries)
            {
                var text = Text[entry.Key].ToUpper(Languages.CultureOf(Languages.Current));
                entry.Label.text = text;
                entry.Width = entry.Label.GetPreferredValues(text).x + U(44);
            }
            Layout(Origin());
        }

        internal override void Opened() => Focus(First());

        // The game lays its own menu out over the first frames, so its position is read again for a while.
        internal override void Tick()
        {
            if (Time.unscaledTime < _nextMeasure) return;
            _nextMeasure = Time.unscaledTime + 0.5f;
            var origin = Origin();
            if ((origin - _origin).sqrMagnitude > 1f) Layout(origin);
        }

        // The title menu is the first screen; Back has nowhere to go.
        internal override bool OnBack() => true;

        // The game loads another scene for these; the menu steps aside rather than sit over the fade.
        private static void Leave(Action open)
        {
            open();
            FrontEnd.StepAside();
        }

        // Where the row starts: where the game's own row of choices was, kept clear of its logo.
        private Vector2 Origin()
        {
            var gap = U(14);
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
                x += entry.Width + U(14);
            }
        }

        private void AddEntry(string key, Action run)
        {
            var entry = new Entry { Key = key, Run = run, Rect = MenuKit.Box(Root, key) };
            entry.Brush = MenuKit.Fill(MenuKit.Box(entry.Rect, "Brush").Place(0, 0, 1, 1), MenuKit.Brush, MenuKit.Ink);
            entry.Label = MenuKit.Text(entry.Rect, "Label", "", U(29), true, MenuKit.Soft, TextAlignmentOptions.Center);
            entry.Label.characterSpacing = 13;
            entry.Label.rectTransform.Place(0, 0, 1, 1);
            _entries.Add(entry);
            Add(entry.Rect, () => Run(entry), focused =>
            {
                entry.Brush.enabled = focused;
                entry.Label.color = focused ? MenuKit.Dark : MenuKit.Soft;
            });
        }

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
