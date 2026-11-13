// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Localization;
using TMPro;
using UnityEngine;

namespace Modstoke.Loader
{
    /// <summary>
    /// A question over the screen it came from: a dark band across the middle with a title, a line of
    /// explanation and two choices. With a countdown, the cautious choice is taken when it runs out, as for
    /// display settings that may have left the screen unreadable.
    /// </summary>
    internal sealed class ConfirmScreen : FrontScreen
    {
        private const float BandHeight = 260, ColumnWidth = 760;
        private readonly string _titleKey, _bodyKey, _yesKey, _noKey;
        private readonly Action _yes, _no;
        private readonly float _seconds;
        private readonly bool _cautious;
        private TextMeshProUGUI _title, _body, _countdown, _yesLabel, _noLabel;
        private Focusable _yesItem, _noItem;
        private float _deadline;
        private bool _done;

        /// <param name="cautious">Focus starts on the "no" choice, for questions whose "yes" cannot be undone.</param>
        /// <param name="seconds">With a value above zero, "no" is chosen when this many seconds pass.</param>
        internal ConfirmScreen(string titleKey, string bodyKey, string yesKey, string noKey, Action yes, Action no = null,
            bool cautious = false, float seconds = 0)
        {
            _titleKey = titleKey;
            _bodyKey = bodyKey;
            _yesKey = yesKey;
            _noKey = noKey;
            _yes = yes;
            _no = no;
            _cautious = cautious;
            _seconds = seconds;
            _deadline = seconds > 0 ? Time.unscaledTime + seconds : 0;
        }

        internal override bool Overlay => true;

        protected override void Build()
        {
            MenuKit.Fill(MenuKit.Box(Root, "Dim").Place(0, 0, 1, 1), null, MenuKit.Hex(0x000000, 0.45f));
            var band = MenuKit.Box(Root, "Band").Place(0, 0.5f, 1, 0.5f, 0, -U(BandHeight / 2), 0, -U(BandHeight / 2));
            MenuKit.Fill(band, null, MenuKit.Hex(0x050606, 0.92f));
            MenuKit.Fill(MenuKit.Box(band, "Top").Place(0, 1, 1, 1, 0, -Mathf.Max(1f, U(1)), 0, 0), null, MenuKit.Rule);
            MenuKit.Fill(MenuKit.Box(band, "Bottom").Place(0, 0, 1, 0, 0, 0, 0, -Mathf.Max(1f, U(1))), null, MenuKit.Rule);
            var column = MenuKit.Box(band, "Column");
            column.anchorMin = column.anchorMax = column.pivot = new Vector2(0.5f, 0.5f);
            column.sizeDelta = new Vector2(U(ColumnWidth), U(BandHeight));
            _title = MenuKit.Text(column, "Title", "", U(32), true, MenuKit.Ink);
            _title.characterSpacing = 4;
            _title.rectTransform.Strip(U(34), U(44), 0, 0);
            _body = MenuKit.Text(column, "Body", "", U(18), false, MenuKit.Soft, TextAlignmentOptions.TopLeft);
            _body.enableWordWrapping = true;
            _body.rectTransform.Strip(U(86), U(56), 0, 0);
            _countdown = MenuKit.Text(column, "Countdown", "", U(15), false, MenuKit.Muted);
            _countdown.rectTransform.Strip(U(144), U(22), 0, 0);

            var mark = new Glide(column, false);
            (_yesItem, _yesLabel) = Choice(column, "Yes", 0, () => Choose(true));
            (_noItem, _noLabel) = Choice(column, "No", 1, () => Choose(false));
            _yesItem.Mark = _noItem.Mark = mark;
        }

        private (Focusable Item, TextMeshProUGUI Label) Choice(RectTransform column, string name, int index, Action run)
        {
            var rect = MenuKit.Box(column, name).At(index * U(232), U(BandHeight - 76), U(220), U(46));
            var label = MenuKit.Text(rect, "Label", "", U(18), true, MenuKit.Soft);
            label.characterSpacing = 12;
            label.rectTransform.Place(0, 0, 1, 1, U(18), 0, U(12), 0);
            var item = Add(rect, run, focused => label.color = focused ? MenuKit.Ink : MenuKit.Soft);
            item.Flash = Flashing(label);
            return (item, label);
        }

        internal override void Retext()
        {
            var culture = Languages.CultureOf(Languages.Current);
            _title.text = Text[_titleKey];
            _body.text = Text[_bodyKey];
            _yesLabel.text = Text[_yesKey].ToUpper(culture);
            _noLabel.text = Text[_noKey].ToUpper(culture);
            PaintCountdown();
        }

        internal override void Opened() => Focus(_cautious ? _noItem : _yesItem);

        internal override IReadOnlyList<PromptSpec> Prompts => new[]
        {
            new PromptSpec(Text[_noKey], "B", "ESC", () => Choose(false)),
            new PromptSpec(Text["common.select"], "A", "ENTER", null)
        };

        // Back answers "no".
        internal override bool OnBack()
        {
            Choose(false);
            return true;
        }

        internal override void Tick()
        {
            if (_deadline <= 0 || _done) return;
            if (Time.unscaledTime >= _deadline) Choose(false);
            else PaintCountdown();
        }

        private void PaintCountdown()
        {
            if (_countdown == null || _deadline <= 0) return;
            var left = Mathf.Max(0, Mathf.CeilToInt(_deadline - Time.unscaledTime));
            _countdown.text = Text.Count("confirm.revertIn", left);
        }

        private void Choose(bool yes)
        {
            if (_done) return;
            _done = true;
            MenuSound.Play(yes ? MenuCue.Activate : MenuCue.Back);
            FrontEnd.Pop(true);
            try
            {
                (yes ? _yes : _no)?.Invoke();
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("A menu choice did not complete: " + error.Message);
            }
        }
    }
}
