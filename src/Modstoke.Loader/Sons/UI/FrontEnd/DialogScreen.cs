// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Localization;
using TheForest.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// A system dialog in the front end's design: the dark band Modstoke's own questions use, a status mark when the
    /// dialog reports a failure or asks for something that can't be undone, the owner's text, a text field when it
    /// asks for one, and its answers in a row, or a column when they don't fit. It draws its own prompts on the
    /// prompt bar's line, since it also shows where no front-end screen is up.
    /// </summary>
    internal sealed class DialogScreen : FrontScreen
    {
        private const float ColumnWidth = 760, Top = 38, Bottom = 32, TitleGap = 12, Gap = 22, MaxBody = 330,
            FieldHeight = 46, ChoiceHeight = 46, ChoiceGap = 12, StackGap = 4, ChoicePad = 48, MinChoice = 220,
            PromptHeight = 44, TagHeight = 24, FootFade = 40, CaretBlink = 0.53f, SweepSeconds = 1.1f;

        private sealed class ChoiceView
        {
            internal RectTransform Rect;
            internal TextMeshProUGUI Label;
            internal Focusable Item;
            internal DialogChoice Choice;
        }

        private sealed class PromptView
        {
            internal RectTransform Rect;
            internal TextMeshProUGUI Label;
            internal Action Click;
        }

        private readonly SystemDialog _dialog;
        private readonly List<ChoiceView> _choices = new();
        private readonly List<PromptView> _prompts = new();
        private RectTransform _band, _column, _mark, _field, _caret, _busy, _light, _bar;
        private TextMeshProUGUI _title, _body, _fieldLabel, _fieldText;
        private Image _fieldLine;
        private Focusable _fieldItem;
        private Glide _plate;
        private PromptView _hover;
        private string _painted;
        private float _opened, _typedAt;
        private bool _done, _keyboard;

        internal DialogScreen(SystemDialog dialog) => _dialog = dialog;

        /// <summary>The dialog's parts this screen was built for.</summary>
        internal int Shape { get; private set; }

        internal override bool Overlay => true;

        protected override void Build()
        {
            Shape = _dialog.Shape;
            _choices.Clear();
            _prompts.Clear();
            _fieldItem = null;
            _field = _caret = _busy = _light = _mark = null;
            _fieldLabel = _fieldText = null;
            // A raycast target, so clicks meant for the mods menu or a mod's screen under the dialog stop here; the
            // screen's own group would otherwise let them through.
            Group.blocksRaycasts = true;
            MenuKit.Fill(MenuKit.Box(Root, "Dim").Place(0, 0, 1, 1), null, MenuKit.Hex(0x000000, _dialog.Dim)).raycastTarget = true;
            _band = MenuKit.Box(Root, "Band");
            MenuKit.Fill(_band, null, MenuKit.Band);
            var rule = Mathf.Max(1f, U(1));
            MenuKit.Fill(MenuKit.Box(_band, "Top").Place(0, 1, 1, 1, 0, -rule, 0, 0), null, MenuKit.Rule);
            MenuKit.Fill(MenuKit.Box(_band, "Bottom").Place(0, 0, 1, 0, 0, 0, 0, -rule), null, MenuKit.Rule);
            _column = MenuKit.Box(_band, "Column");
            _column.anchorMin = _column.anchorMax = _column.pivot = new Vector2(0.5f, 0.5f);
            if (_dialog.Tone != DialogTone.Plain)
            {
                // A failure takes the logo's red: the soft red that text uses reads as white on a mark this small.
                _mark = MenuKit.Box(_column, "Status");
                MenuKit.Fill(_mark, null, _dialog.Tone switch
                {
                    DialogTone.Caution => MenuKit.Amber,
                    DialogTone.Problem => MenuKit.Accent,
                    _ => MenuKit.Green
                });
            }
            _title = MenuKit.Text(_column, "Title", "", U(32), true, MenuKit.Ink, TextAlignmentOptions.TopLeft);
            _title.characterSpacing = 4;
            _title.enableWordWrapping = true;
            _body = MenuKit.Text(_column, "Body", "", U(18), false, MenuKit.Soft, TextAlignmentOptions.TopLeft, true);
            _body.enableWordWrapping = true;
            if (_dialog.Typed != null) BuildField();
            if (_dialog.Busy)
            {
                // A light running along a line, as the title's while a game loads: the dialog stands for work going on.
                _busy = MenuKit.Box(_column, "Busy");
                MenuKit.Fill(_busy, null, MenuKit.Faded(0.1f));
                _busy.gameObject.AddComponent<RectMask2D>();
                _light = MenuKit.Box(_busy, "Light");
                _light.anchorMin = Vector2.zero;
                _light.anchorMax = new Vector2(0, 1);
                _light.pivot = new Vector2(0, 0.5f);
                MenuKit.Fill(_light, MenuKit.Bump, MenuKit.Ink);
            }
            _plate = new Glide(_column, false);
            foreach (var choice in _dialog.Choices)
            {
                var view = new ChoiceView { Choice = choice, Rect = MenuKit.Box(_column, "Choice") };
                var label = view.Label = MenuKit.Text(view.Rect, "Label", "", U(18), true, MenuKit.Soft);
                label.characterSpacing = 12;
                label.rectTransform.Place(0, 0, 1, 1, U(18), 0, U(12), 0);
                view.Item = Add(view.Rect, () => Choose(choice), focused => Paint(label, choice, focused), enabled: choice.Enabled);
                view.Item.Mark = _plate;
                view.Item.Flash = Flashing(label);
                _choices.Add(view);
            }
        }

        private void BuildField()
        {
            _fieldLabel = MenuKit.Text(_column, "Field label", "", U(15), true, MenuKit.Muted);
            _fieldLabel.characterSpacing = 10;
            _field = MenuKit.Box(_column, "Field");
            MenuKit.Fill(_field, null, MenuKit.Field);
            _field.gameObject.AddComponent<RectMask2D>();
            _fieldLine = MenuKit.Fill(MenuKit.Box(_field, "Line").Place(0, 0, 1, 0, 0, 0, 0, -Mathf.Max(1f, U(2))), null, MenuKit.Rule);
            _fieldText = MenuKit.Text(_field, "Text", "", U(20), false, MenuKit.Ink);
            // What the player types is shown as typed, never read as markup.
            _fieldText.richText = false;
            _fieldText.rectTransform.Place(0, 0, 1, 1, U(16), 0, U(16), 0);
            _caret = MenuKit.Box(_fieldText.rectTransform, "Caret");
            _caret.anchorMin = _caret.anchorMax = new Vector2(0, 0.5f);
            _caret.pivot = new Vector2(0, 0.5f);
            _caret.sizeDelta = new Vector2(Mathf.Max(2f, U(2)), U(22));
            MenuKit.Fill(_caret, null, MenuKit.Ink);
            _caret.gameObject.SetActive(false);
            _fieldItem = Add(_field, Submit, _ => PaintField());
        }

        internal override void Retext()
        {
            var culture = Languages.CultureOf(Languages.Current);
            _title.text = _dialog.Title;
            _body.text = Paragraphs(_dialog.Body);
            if (_fieldLabel != null) _fieldLabel.text = (_dialog.FieldLabel ?? "").ToUpper(culture);
            foreach (var view in _choices) view.Label.text = view.Choice.Label.ToUpper(culture);
            Layout();
            BuildPrompts();
        }

        internal override void Opened()
        {
            _opened = Time.unscaledTime;
            var back = _choices.Find(view => view.Choice.Back);
            Focus(_fieldItem ?? (_dialog.Cautious && back != null && back.Item.Usable ? back.Item : First()));
        }

        internal override void DeviceChanged()
        {
            PaintField();
            BuildPrompts();
        }

        protected override void FocusChanged(Focusable previous, Focusable current)
        {
            // On a controller the field's prompt reads "type" rather than "select".
            if (_fieldItem != null && (ReferenceEquals(previous, _fieldItem) || ReferenceEquals(current, _fieldItem))) BuildPrompts();
        }

        internal override IReadOnlyList<PromptSpec> Prompts
        {
            get
            {
                var prompts = new List<PromptSpec>(2);
                var back = BackAnswer();
                if (back != null) prompts.Add(new PromptSpec(back.Label, "B", "ESC", () => Choose(back)));
                var typing = FrontInput.PadActive && _fieldItem != null && ReferenceEquals(Focused, _fieldItem);
                if (_choices.Count > 0 || _fieldItem != null)
                    prompts.Add(new PromptSpec(Text[typing ? "dialog.type" : "common.select"], "A", "ENTER", null));
                return prompts;
            }
        }

        // Back gives the dialog's way out, and acknowledges one that only reports something. Either way the press is
        // the dialog's: nothing under it may take it.
        internal override bool OnBack()
        {
            var back = BackAnswer();
            if (back != null) Choose(back);
            return true;
        }

        private DialogChoice BackAnswer()
        {
            foreach (var view in _choices)
                if (view.Choice.Back) return view.Choice;
            return _choices.Count == 1 && !_dialog.Cautious && _fieldItem == null ? _choices[0].Choice : null;
        }

        protected override bool CaptureInput()
        {
            if (_fieldItem == null) return false;
            // Steam's on-screen keyboard has the controller until it closes; its text arrives in the field.
            if (KeyboardOpen()) return true;
            var typed = _dialog.Typed ?? "";
            var text = typed;
            var consumed = false;
            if ((UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl))
                && UnityEngine.Input.GetKeyDown(KeyCode.V))
            {
                text += (GUIUtility.systemCopyBuffer ?? "").Replace('\n', ' ').Replace('\r', ' ');
                consumed = true;
            }
            foreach (var c in UnityEngine.Input.inputString ?? "")
            {
                if (c == '\b')
                {
                    if (text.Length > 0) text = text.Substring(0, text.Length - 1);
                    consumed = true;
                }
                else if (!char.IsControl(c))
                {
                    text += c;
                    consumed = true;
                }
            }
            if (text != typed)
            {
                _dialog.Type(text);
                _typedAt = Time.unscaledTime;
                PaintField();
            }
            // A typed character is not also a menu press: Space would answer and Q or E would change tabs.
            return consumed;
        }

        private static bool KeyboardOpen()
        {
            try { return global::Sons.Input.InputSystem.GetState(InputState.VirtualKeyboard); }
            catch (Exception) { return false; }
        }

        // A click only puts focus in the field. Enter answers with the dialog's first answer, its OK; a controller's
        // confirm opens Steam's keyboard.
        private void Submit()
        {
            if (FromPointer)
            {
                MenuSound.Play(MenuCue.Move);
                return;
            }
            if (FrontInput.PadActive)
            {
                _keyboard = true;
                _dialog.OpenKeyboard();
                return;
            }
            var answer = Primary();
            if (answer != null) Choose(answer.Choice);
        }

        private ChoiceView Primary() => _choices.Find(view => !view.Choice.Back && view.Item.Usable);

        private void Choose(DialogChoice choice)
        {
            if (_done || choice.Enabled?.Invoke() == false) return;
            _done = true;
            MenuSound.Play(choice.Back ? MenuCue.Back : MenuCue.Activate);
            SystemDialogs.Answer(choice);
        }

        internal override void Tick()
        {
            if (_light != null)
            {
                var span = _busy.rect.width;
                var band = span * 0.22f;
                var t = (Time.unscaledTime - _opened) % SweepSeconds / SweepSeconds;
                _light.sizeDelta = new Vector2(band, 0);
                _light.anchoredPosition = new Vector2(Mathf.Lerp(-band, span, t * t * (3f - 2f * t)), 0);
            }
            if (_fieldItem != null)
            {
                // Steam's keyboard writes into the field without passing through here; once it has, focus moves on to
                // the answer, a press away.
                if (_dialog.Typed != _painted)
                {
                    PaintField();
                    if (_keyboard && !KeyboardOpen() && Primary() is { } answer) Focus(answer.Item);
                    _keyboard = false;
                }
                var shown = ReferenceEquals(Focused, _fieldItem) && !FrontInput.PadActive
                    && (Time.unscaledTime - _typedAt) % (CaretBlink * 2) < CaretBlink;
                if (_caret.gameObject.activeSelf != shown) _caret.gameObject.SetActive(shown);
            }
            // The game can switch an answer on or off while its dialog is up, as one waiting for the field.
            foreach (var view in _choices)
                if (!ReferenceEquals(view.Item, Focused)) Paint(view.Label, view.Choice, false);
        }

        private static void Paint(TextMeshProUGUI label, DialogChoice choice, bool focused)
        {
            var color = focused ? MenuKit.OnPlate : choice.Enabled?.Invoke() == false ? MenuKit.Faint : MenuKit.Soft;
            if (label.color != color) label.color = color;
        }

        private void PaintField()
        {
            if (_fieldText == null) return;
            _painted = _dialog.Typed;
            var typed = _painted ?? "";
            var shown = _dialog.Secret ? new string('•', typed.Length) : typed;
            _fieldText.text = shown;
            _fieldLine.color = ReferenceEquals(Focused, _fieldItem) ? MenuKit.Ink : MenuKit.Rule;
            // A long entry keeps its end, where the caret is, in view.
            var width = _fieldText.GetPreferredValues(shown).x;
            var box = _fieldText.rectTransform.rect.width;
            _fieldText.alignment = width + U(4) > box ? TextAlignmentOptions.MidlineRight : TextAlignmentOptions.MidlineLeft;
            _caret.anchoredPosition = new Vector2(Mathf.Min(width, box) + U(2), 0);
        }

        // Top to bottom: title, text, field, the busy line, then the answers; the band grows to fit and stays centred.
        private void Layout()
        {
            var width = U(ColumnWidth);
            float y = U(Top), gap = 0;
            if (_mark != null) _mark.At(0, y - U(18), U(48), Mathf.Max(3f, U(4)));
            if (Shown(_title))
            {
                var height = _title.GetPreferredValues(_title.text, width, 0).y;
                _title.rectTransform.Strip(y, height, 0, 0);
                y += height;
                gap = U(TitleGap);
            }
            if (Shown(_body))
            {
                y += gap;
                var height = Mathf.Min(_body.GetPreferredValues(_body.text, width, 0).y, U(MaxBody));
                _body.rectTransform.Strip(y, height, 0, 0);
                y += height;
                gap = U(Gap);
            }
            if (_field != null)
            {
                y += gap;
                if (Shown(_fieldLabel))
                {
                    _fieldLabel.rectTransform.Strip(y, U(20), 0, 0);
                    y += U(28);
                }
                _field.Strip(y, U(FieldHeight), 0, 0);
                y += U(FieldHeight);
                gap = U(Gap);
                PaintField();
            }
            if (_busy != null)
            {
                y += gap;
                var line = Mathf.Max(2f, U(2));
                _busy.Strip(y, line, 0, 0);
                y += line;
                gap = U(Gap);
            }
            if (_choices.Count > 0) y = PlaceChoices(y + gap + U(6), width);
            y += U(Bottom);
            _band.Place(0, 0.5f, 1, 0.5f, 0, -y / 2, 0, -y / 2);
            _column.sizeDelta = new Vector2(width, y);
            _plate.Follow();
        }

        private static bool Shown(TextMeshProUGUI text)
        {
            var shown = !string.IsNullOrEmpty(text.text);
            if (text.gameObject.activeSelf != shown) text.gameObject.SetActive(shown);
            return shown;
        }

        // In a row like Modstoke's own questions; too many or too long for one, a column like an actions menu.
        private float PlaceChoices(float y, float width)
        {
            var widths = new float[_choices.Count];
            var total = U(ChoiceGap) * (_choices.Count - 1);
            for (var i = 0; i < _choices.Count; i++)
            {
                var label = _choices[i].Label;
                widths[i] = Mathf.Max(U(MinChoice), label.GetPreferredValues(label.text).x + U(ChoicePad));
                total += widths[i];
            }
            if (total <= width)
            {
                var x = 0f;
                for (var i = 0; i < _choices.Count; i++)
                {
                    _choices[i].Rect.At(x, y, widths[i], U(ChoiceHeight));
                    x += widths[i] + U(ChoiceGap);
                }
                return y + U(ChoiceHeight);
            }
            for (var i = 0; i < _choices.Count; i++)
                _choices[i].Rect.Strip(y + i * U(ChoiceHeight + StackGap), U(ChoiceHeight), 0, 0);
            return y + _choices.Count * U(ChoiceHeight + StackGap) - U(StackGap);
        }

        // The first paragraph says what happened; what follows, usually what to do about it, sits back a step.
        private static string Paragraphs(string text)
        {
            var split = text.IndexOf("\n\n", StringComparison.Ordinal);
            return split < 0 ? text : text.Substring(0, split) + "<color=" + MenuKit.Html(MenuKit.Muted) + ">" + text.Substring(split) + "</color>";
        }

        // The prompts on the line the front end's bar uses, way back on the left; at the title that is the line the
        // game's own row of choices sits on. They stand on a dark foot, so the prompts of whatever the dialog opened
        // over (the mods menu, a mod's screen, the loading view) never read through them.
        private void BuildPrompts()
        {
            if (_bar != null && _bar) Object.Destroy(_bar.gameObject);
            _prompts.Clear();
            _hover = null;
            var middle = U(52);
            if (GameMenus.TryBar(out _, out var line) && line > U(30) && line < U(150)) middle = line;
            var bottom = middle - U(PromptHeight) / 2;
            var top = bottom + U(PromptHeight + 12);
            _bar = MenuKit.Box(Root, "Prompts").Place(0, 0, 1, 0, 0, 0, 0, -(top + U(FootFade)));
            MenuKit.Fill(MenuKit.Box(_bar, "Foot").Place(0, 0, 1, 0, 0, 0, 0, -top), null, MenuKit.Band);
            // The fade ramp is solid at its top; turned over, it is solid where it meets the foot.
            var edge = MenuKit.Box(_bar, "Foot edge").Place(0, 0, 1, 0, 0, top, 0, -(top + U(FootFade)));
            edge.localScale = new Vector3(1, -1, 1);
            MenuKit.Fill(edge, MenuKit.Fade, MenuKit.Band);
            var row = MenuKit.Box(_bar, "Row").Place(0, 0, 1, 0, U(60), bottom, U(60), -(bottom + U(PromptHeight)));
            var pad = FrontInput.PadActive;
            var culture = Languages.CultureOf(Languages.Current);
            float left = 0, right = 0;
            foreach (var spec in Prompts)
            {
                var key = pad ? spec.Pad : spec.Key;
                if (key == null) continue;
                var view = new PromptView { Rect = MenuKit.Box(row, spec.Label), Click = spec.Click };
                var tag = new KeyTag(view.Rect, "Key", U(TagHeight)).Set(key, pad);
                view.Label = MenuKit.Text(view.Rect, "Label", spec.Label.ToUpper(culture), U(19), true, MenuKit.Soft);
                view.Label.characterSpacing = 14;
                var size = tag.Size;
                var labelWidth = view.Label.GetPreferredValues(view.Label.text).x + U(4);
                var width = size.x + U(10) + labelWidth;
                tag.Rect.At(0, (U(PromptHeight) - size.y) / 2, size.x, size.y);
                view.Label.rectTransform.At(size.x + U(10), 0, labelWidth, U(PromptHeight));
                if (_prompts.Count == 0)
                {
                    view.Rect.At(left, 0, width, U(PromptHeight));
                    left += width + U(38);
                }
                else
                {
                    view.Rect.AtRight(right, 0, width, U(PromptHeight));
                    right += width + U(38);
                }
                _prompts.Add(view);
            }
        }

        /// <summary>Prompts are clickable; true when this frame's click went to one.</summary>
        internal bool PromptInput()
        {
            PromptView over = null;
            foreach (var view in _prompts)
                if (view.Rect && RectTransformUtility.RectangleContainsScreenPoint(view.Rect, FrontInput.Pointer, null))
                    over = view;
            if (!ReferenceEquals(over, _hover))
            {
                if (_hover != null && _hover.Label) _hover.Label.color = MenuKit.Soft;
                _hover = over;
                if (over != null)
                {
                    over.Label.color = MenuKit.Ink;
                    if (over.Click != null) MenuSound.Play(MenuCue.Move);
                }
            }
            if (!FrontInput.Click || over?.Click == null) return false;
            over.Click();
            return true;
        }
    }
}
