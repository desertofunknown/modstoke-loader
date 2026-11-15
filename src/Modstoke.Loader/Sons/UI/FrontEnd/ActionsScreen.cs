// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Localization;
using TMPro;
using UnityEngine;

namespace Modstoke.Loader
{
    /// <summary>One thing that can be done from an actions menu.</summary>
    internal sealed record MenuAction(string Label, Action Run);

    /// <summary>
    /// What can be done with one thing, such as a player: a dark band over the screen it came from, with its name
    /// and a short column of choices. A choice closes the band before it runs, so one that opens a question of its
    /// own puts it over the screen underneath.
    /// </summary>
    internal sealed class ActionsScreen : FrontScreen
    {
        private const float ColumnWidth = 560, ChoiceHeight = 46, ChoiceGap = 4;
        private const int LineRows = 3;
        private readonly string _title, _line;
        private readonly IReadOnlyList<MenuAction> _actions;
        private TextMeshProUGUI _titleText, _lineText;
        private bool _done;

        internal ActionsScreen(string title, string line, IReadOnlyList<MenuAction> actions)
        {
            _title = title;
            _line = line;
            _actions = actions;
        }

        internal override bool Overlay => true;

        protected override void Build()
        {
            MenuKit.Fill(MenuKit.Box(Root, "Dim").Place(0, 0, 1, 1), null, MenuKit.Hex(0x000000, 0.45f));
            var band = MenuKit.Box(Root, "Band");
            MenuKit.Fill(band, null, MenuKit.Band);
            MenuKit.Fill(MenuKit.Box(band, "Top").Place(0, 1, 1, 1, 0, -Mathf.Max(1f, U(1)), 0, 0), null, MenuKit.Rule);
            MenuKit.Fill(MenuKit.Box(band, "Bottom").Place(0, 0, 1, 0, 0, 0, 0, -Mathf.Max(1f, U(1))), null, MenuKit.Rule);
            var column = MenuKit.Box(band, "Column");
            column.anchorMin = column.anchorMax = column.pivot = new Vector2(0.5f, 0.5f);
            _titleText = MenuKit.Text(column, "Title", "", U(30), true, MenuKit.Ink, TextAlignmentOptions.MidlineLeft, true);
            _titleText.characterSpacing = 4;
            _titleText.rectTransform.Strip(U(30), U(42), 0, 0);
            // A long line wraps, up to three rows that push the choices down, and past that ends in "…".
            _lineText = MenuKit.Text(column, "Line", _line ?? "", U(16), false, MenuKit.Muted, TextAlignmentOptions.MidlineLeft, true);
            _lineText.enableWordWrapping = true;
            var lineHeight = _lineText.GetPreferredValues("Ag", U(ColumnWidth), 0).y;
            var lines = string.IsNullOrEmpty(_line) || lineHeight <= 0 ? 1
                : Mathf.Clamp(Mathf.RoundToInt(_lineText.GetPreferredValues(_line, U(ColumnWidth), 0).y / lineHeight), 1, LineRows);
            var extra = (lines - 1) * lineHeight;
            _lineText.rectTransform.Strip(U(72), U(24) + extra, 0, 0);
            var height = U(128 + _actions.Count * (ChoiceHeight + ChoiceGap)) + extra;
            band.Place(0, 0.5f, 1, 0.5f, 0, -height / 2, 0, -height / 2);
            column.sizeDelta = new Vector2(U(ColumnWidth), height);

            var mark = new Glide(column, false);
            var rows = new List<RectTransform>();
            for (var i = 0; i < _actions.Count; i++)
            {
                var action = _actions[i];
                var rect = MenuKit.Box(column, "Choice " + i).Strip(U(112 + i * (ChoiceHeight + ChoiceGap)) + extra, U(ChoiceHeight), 0, 0);
                var label = MenuKit.Text(rect, "Label", "", U(18), true, MenuKit.Soft);
                label.characterSpacing = 12;
                label.rectTransform.Place(0, 0, 1, 1, U(18), 0, U(12), 0);
                label.text = action.Label.ToUpper(Languages.CultureOf(Languages.Current));
                var item = Add(rect, () => Choose(action), focused => label.color = focused ? MenuKit.OnPlate : MenuKit.Soft);
                item.Mark = mark;
                item.Flash = Flashing(label);
                rows.Add(rect);
            }
            new Cascade(rows);
        }

        internal override void Retext()
        {
            _titleText.text = _title;
            _lineText.text = _line ?? "";
        }

        internal override void Opened() => Focus(First());

        internal override IReadOnlyList<PromptSpec> Prompts => new[]
        {
            new PromptSpec(Text["common.back"], "B", "ESC", () => FrontEnd.Pop()),
            new PromptSpec(Text["common.select"], "A", "ENTER", null)
        };

        private void Choose(MenuAction action)
        {
            if (_done) return;
            _done = true;
            MenuSound.Play(MenuCue.Forward);
            FrontEnd.Pop(true);
            try
            {
                action.Run();
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("\"" + action.Label + "\" did not complete: " + error.Message);
            }
        }
    }
}
