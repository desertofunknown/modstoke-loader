// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Localization;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    internal enum KeyboardResult { Typing, Done, Cancelled }

    /// <summary>
    /// Typing with a controller. Where Steam shows a keyboard of its own (a Steam Deck, Big Picture) that one opens;
    /// everywhere else a compact grid in the front end's design sits under the field. On the grid the stick or D-pad
    /// moves, A types, X deletes, Y types a space, the bumpers move the caret, the triggers switch case and layer,
    /// Start keeps the text and B puts the old text back, and a keyboard and mouse work alongside. The grid only
    /// types: the field it belongs to shows the text and the caret, so a masked field stays masked.
    /// </summary>
    internal sealed partial class PadKeyboard
    {
        private const int Columns = 11, GridRows = 6;
        private const float KeyWidth = 40, KeyHeight = 34, Gap = 4, Inset = 14, LegendHeight = 34, NoteHeight = 40,
            FieldGap = 8, Margin = 16, FirstRepeat = 0.35f, NextRepeat = 0.07f, AppearSeconds = 0.12f;

        private enum Button { A, B, X, Y, LB, RB, LT, RT, Start }

        private enum Shift { Off, Once, Locked }

        private sealed class Key
        {
            internal RectTransform Rect, Symbol;
            internal TextMeshProUGUI Label;
            internal Image[] Strokes;
            internal Image Lock;
            internal KeyTag Tag;
            internal string Button;
            internal Action Press;
            internal int Row, Column;
            internal float Left, Right;
        }

        private sealed class Hint
        {
            internal RectTransform Rect;
            internal KeyTag Tag;
            internal TextMeshProUGUI Label;
            internal string Pad, Key;
            internal Action Click;
        }

        // Delete and the caret repeat while their button is held, as a keyboard's keys do.
        private static readonly bool[] Repeats = { false, false, true, false, true, true, false, false, false };
        private static bool _padFailed;
        private readonly List<Key> _keys = new();
        private readonly List<Key>[] _rows = new List<Key>[GridRows];
        private readonly List<Hint> _hints = new();
        private readonly Il2CppStructArray<Vector3> _corners = new(4);
        private readonly string _original;
        private readonly int _maxLength;
        private List<Layer> _layers;
        private RectTransform _layer, _field, _panel, _grid, _legend;
        private CanvasGroup _group;
        private Glide _plate;
        private Key _focused, _shiftKey, _layerKey;
        private CultureInfo _culture;
        private Shift _shift;
        private KeyboardResult _result;
        private Vector2Int _move;
        private Vector2 _pointer;
        private int _buttons, _layerIndex, _lastStep = -1;
        private float _column, _moveAt, _buttonAt;
        private bool _pad;

        private PadKeyboard(string text, int maxLength, int caret)
        {
            Text = _original = text ?? "";
            Caret = caret >= 0 ? Mathf.Min(caret, Text.Length) : Text.Length;
            _maxLength = maxLength > 0 ? maxLength : int.MaxValue;
        }

        /// <summary>What has been typed so far.</summary>
        internal string Text { get; private set; }

        /// <summary>Where the caret is, as the index of the character it stands before.</summary>
        internal int Caret { get; private set; }

        /// <summary>Whether Steam's keyboard has the text; nothing of the front end's is drawn meanwhile.</summary>
        internal bool OnSteam { get; private set; }

        private static float U(float design) => FrontEnd.U(design);

        /// <summary>
        /// Opens a keyboard for <paramref name="field"/>, which keeps showing the text: Steam's where it shows one, the grid
        /// on <paramref name="layer"/> otherwise. <paramref name="title"/> heads Steam's keyboard; the caret starts at
        /// <paramref name="caret"/>, or at the end.
        /// </summary>
        internal static PadKeyboard Open(RectTransform layer, RectTransform field, string text, int maxLength, string title,
            bool secret = false, int caret = -1)
        {
            var keyboard = new PadKeyboard(text, maxLength, caret);
            keyboard.OnSteam = SteamKeyboard.TryOpen(keyboard.Text, maxLength, secret, title);
            if (!keyboard.OnSteam) keyboard.Build(layer, field);
            return keyboard;
        }

        /// <summary>
        /// Takes this frame's input; the host keeps calling it until the text is done or cancelled. In a frame after
        /// ones the host skipped, as under a dialog, the console or the mods menu, nothing is read: what is pressed or
        /// held then belongs to whatever had the input, as the press that answered a dialog does.
        /// </summary>
        internal KeyboardResult Step()
        {
            var frame = Time.frameCount;
            var resumed = frame != _lastStep + 1;
            _lastStep = frame;
            if (OnSteam) return StepSteam(resumed);
            // The screen the grid stood on was taken down under it.
            if (_panel == null || !_panel) return KeyboardResult.Cancelled;
            if (_result != KeyboardResult.Typing) return _result;
            Place();
            if (_group.alpha < 1f) _group.alpha = Mathf.MoveTowards(_group.alpha, 1f, Time.unscaledDeltaTime / AppearSeconds);
            var pad = FrontInput.PadActive;
            if (pad != _pad)
            {
                _pad = pad;
                Retag();
            }
            if (resumed)
            {
                Prime();
                return KeyboardResult.Typing;
            }
            ReadPad();
            if (_result == KeyboardResult.Typing) ReadKeys();
            if (_result == KeyboardResult.Typing) ReadMouse();
            return _result;
        }

        // Steam's keyboard is drawn by Steam and can't be watched from here. Should it never answer, Escape or a click
        // in the game gives up on it and hands the game its controls back.
        private KeyboardResult StepSteam(bool resumed)
        {
            if (SteamKeyboard.Open)
            {
                if (resumed || !UnityEngine.Input.GetKeyDown(KeyCode.Escape) && !UnityEngine.Input.GetMouseButtonDown(0))
                    return KeyboardResult.Typing;
                SteamKeyboard.Abandon();
                return KeyboardResult.Cancelled;
            }
            Text = Clip(SteamKeyboard.Text);
            Caret = Text.Length;
            return Text != _original ? KeyboardResult.Done : KeyboardResult.Cancelled;
        }

        /// <summary>Takes the text back as the field holds it, as when the field refused some of what was typed.</summary>
        internal void Set(string text)
        {
            text ??= "";
            if (text == Text) return;
            Caret = Mathf.Clamp(Caret - Math.Max(0, Text.Length - text.Length), 0, text.Length);
            Text = text;
        }

        /// <summary>Takes the grid down. Steam's keyboard can't be closed from here; what it returns is then ignored.</summary>
        internal void Close()
        {
            if (_panel != null && _panel) Object.Destroy(_panel.gameObject);
            _panel = null;
        }

        private void Build(RectTransform layer, RectTransform field)
        {
            _layer = layer;
            _field = field;
            _culture = Languages.CultureOf(Languages.Current);
            _layers = LayersFor(Languages.Current);
            _pad = FrontInput.PadActive;
            var gridWidth = Columns * U(KeyWidth + Gap) - U(Gap);
            var gridHeight = GridRows * U(KeyHeight + Gap) - U(Gap);
            var note = Note();
            var noteHeight = note != null ? U(NoteHeight) : 0;
            var width = gridWidth + 2 * U(Inset);
            var height = U(Inset) + gridHeight + noteHeight + U(10) + U(LegendHeight) + U(6);

            _panel = MenuKit.Box(layer, "Keyboard").At(0, 0, width, height);
            _group = _panel.gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.interactable = false;
            _group.blocksRaycasts = false;
            MenuKit.GlowBehind(_panel, U(20), 0.55f);
            MenuKit.Fill(MenuKit.Box(_panel, "Band").Place(0, 0, 1, 1), null, MenuKit.Band);
            var rule = Mathf.Max(1f, U(1));
            MenuKit.Fill(MenuKit.Box(_panel, "Top").Place(0, 1, 1, 1, 0, -rule, 0, 0), null, MenuKit.Rule);
            MenuKit.Fill(MenuKit.Box(_panel, "Bottom").Place(0, 0, 1, 0, 0, 0, 0, -rule), null, MenuKit.Rule);

            _grid = MenuKit.Box(_panel, "Keys").At(U(Inset), U(Inset), gridWidth, gridHeight);
            // Made before the keys, so the plate draws under their letters.
            _plate = new Glide(_grid, false);
            for (var row = 0; row < GridRows; row++) _rows[row] = new List<Key>();
            for (var row = 0; row < GridRows - 1; row++)
                for (var column = 0; column < Columns; column++)
                    AddCharacter(row, column);
            BuildActions(GridRows - 1);

            var y = U(Inset) + gridHeight;
            if (note != null)
            {
                var lines = MenuKit.Text(_panel, "Note", note, U(13), false, MenuKit.Muted, TextAlignmentOptions.TopLeft, true);
                lines.enableWordWrapping = true;
                lines.rectTransform.Strip(y + U(6), U(NoteHeight - 6), U(Inset), U(Inset));
            }
            y += noteHeight + U(10);
            MenuKit.Hairline(_panel, "Legend rule", y - U(4), U(Inset), U(Inset)).color = MenuKit.Line;
            _legend = MenuKit.Box(_panel, "Legend").Strip(y, U(LegendHeight), U(Inset), U(Inset));
            var text = FrontEnd.Text;
            AddHint(text["common.cancel"], "B", "ESC", Cancel);
            AddHint(text["keyboard.done"], "MENU", "ENTER", Done);
            AddHint(text["dialog.type"], "A", null, () => Press(_focused));

            Relabel();
            Retag();
            _column = 0.5f;
            Focus(_rows[1][0], false);
            Place();
        }

        private void AddCharacter(int row, int column)
        {
            var key = NewKey(row, column, column + 1f);
            key.Column = column;
            key.Press = () => Type(key);
            key.Label = MenuKit.Text(key.Rect, "Label", "", U(18), true, MenuKit.Ink, TextAlignmentOptions.Center);
            key.Label.richText = false;
            key.Label.rectTransform.Place(0, 0, 1, 1);
        }

        // Shift, layer, caret left, space, caret right, delete and done, each with the button that does the same.
        private void BuildActions(int row)
        {
            var left = 0f;
            Key Add(float span, string button, Action press, float symbol)
            {
                var key = NewKey(row, left, left + span);
                key.Button = button;
                key.Press = press;
                key.Tag = new KeyTag(key.Rect, "Button", U(14));
                key.Symbol = MenuKit.Box(key.Rect, "Symbol");
                key.Symbol.anchorMin = key.Symbol.anchorMax = key.Symbol.pivot = new Vector2(0, 0.5f);
                key.Symbol.sizeDelta = new Vector2(U(symbol), U(symbol));
                left += span;
                return key;
            }

            // An arrow up, with a bar under it while the shift stays on.
            _shiftKey = Add(1.5f, "LT", NextShift, 14);
            MenuKit.Chevron(_shiftKey.Symbol, "Head", U(14), MenuKit.Ink);
            Stroke(_shiftKey.Symbol, U(7), -U(3), U(9), 90f);
            _shiftKey.Lock = Stroke(_shiftKey.Symbol, U(7), -U(11), U(10), 0);
            _shiftKey.Strokes = Strokes(_shiftKey.Symbol);

            _layerKey = Add(1.5f, "RT", NextLayer, 0);
            _layerKey.Label = MenuKit.Text(_layerKey.Symbol, "Label", "", U(15), true, MenuKit.Ink, TextAlignmentOptions.Center);
            _layerKey.Label.richText = false;
            _layerKey.Label.rectTransform.Place(0, 0, 1, 1);

            var back = Add(1.25f, "LB", () => MoveCaret(-1), 13);
            Turned(MenuKit.Chevron(back.Symbol, "Arrow", U(13), MenuKit.Ink), 90f);
            back.Strokes = Strokes(back.Symbol);

            var space = Add(2.5f, "Y", () => Insert(" ", true), 26);
            Stroke(space.Symbol, U(13), -U(4), U(24), 0);
            Stroke(space.Symbol, U(2), -U(1), U(7), 90f);
            Stroke(space.Symbol, U(24), -U(1), U(7), 90f);
            space.Strokes = Strokes(space.Symbol);

            var forward = Add(1.25f, "RB", () => MoveCaret(1), 13);
            Turned(MenuKit.Chevron(forward.Symbol, "Arrow", U(13), MenuKit.Ink), -90f);
            forward.Strokes = Strokes(forward.Symbol);

            // An arrow pointing back over what it takes away.
            var delete = Add(1.5f, "X", () => Backspace(true), 20);
            var head = Turned(MenuKit.Chevron(delete.Symbol, "Head", U(12), MenuKit.Ink), 90f);
            head.anchorMin = head.anchorMax = new Vector2(0, 0.5f);
            head.anchoredPosition = new Vector2(U(5), 0);
            Stroke(delete.Symbol, U(12), 0, U(14), 0);
            delete.Strokes = Strokes(delete.Symbol);

            var done = Add(1.5f, "MENU", Done, 20);
            Stroke(done.Symbol, U(5), -U(1), U(7), -45f);
            Stroke(done.Symbol, U(12), U(1), U(13), 45f);
            done.Strokes = Strokes(done.Symbol);
        }

        private Key NewKey(int row, float left, float right)
        {
            var step = U(KeyWidth + Gap);
            var key = new Key { Row = row, Left = left, Right = right };
            key.Rect = MenuKit.Box(_grid, "Key").At(left * step, row * U(KeyHeight + Gap), (right - left) * step - U(Gap), U(KeyHeight));
            MenuKit.Fill(key.Rect, null, row == GridRows - 1 ? MenuKit.Line : MenuKit.Cell);
            _keys.Add(key);
            _rows[row].Add(key);
            return key;
        }

        private static RectTransform Turned(RectTransform rect, float degrees)
        {
            rect.localEulerAngles = new Vector3(0, 0, degrees);
            return rect;
        }

        // One straight stroke of a symbol, centered at (x, y) from the symbol's middle left.
        private static Image Stroke(RectTransform parent, float x, float y, float length, float degrees)
        {
            var stroke = MenuKit.Box(parent, "Stroke");
            stroke.anchorMin = stroke.anchorMax = new Vector2(0, 0.5f);
            stroke.pivot = new Vector2(0.5f, 0.5f);
            stroke.anchoredPosition = new Vector2(x, y);
            stroke.sizeDelta = new Vector2(length, Mathf.Max(2f, U(2)));
            stroke.localEulerAngles = new Vector3(0, 0, degrees);
            return MenuKit.Fill(stroke, null, MenuKit.Ink);
        }

        private static Image[] Strokes(RectTransform symbol) => symbol.GetComponentsInChildren<Image>(true);

        private void AddHint(string label, string pad, string key, Action click)
        {
            var hint = new Hint { Rect = MenuKit.Box(_legend, label), Pad = pad, Key = key, Click = click };
            hint.Tag = new KeyTag(hint.Rect, "Key", U(20));
            hint.Label = MenuKit.Text(hint.Rect, "Label", label.ToUpper(_culture), U(15), true, MenuKit.Soft);
            hint.Label.characterSpacing = 12;
            _hints.Add(hint);
        }

        // Chinese, Japanese and Korean need an input method, which Steam's keyboard has and this grid doesn't.
        private static string Note()
        {
            var code = Languages.BaseOf(Languages.Current);
            return code is "zh" or "ja" or "ko" ? FrontEnd.Text["keyboard.cjk"] : null;
        }

        // The characters of the open layer, in the case the shift asks for, and the layer key naming the next layer.
        private void Relabel()
        {
            foreach (var key in _keys)
                if (key.Row < GridRows - 1) key.Label.text = Character(key).ToString();
            _layerKey.Label.text = _layers[(_layerIndex + 1) % _layers.Count].Name;
            Paint();
        }

        private char Character(Key key)
        {
            var c = _layers[_layerIndex].Rows[key.Row][key.Column];
            return _shift == Shift.Off ? c : char.ToUpper(c, _culture);
        }

        // The buttons on the action keys and in the legend follow the device in use: a controller's, or a key's name.
        private void Retag()
        {
            foreach (var key in _keys)
            {
                if (key.Symbol == null) continue;
                key.Tag.Set(_pad ? key.Button : "", _pad);
                var symbol = key.Label != null ? key.Label.GetPreferredValues(key.Label.text).x : key.Symbol.sizeDelta.x;
                if (key.Label != null) key.Symbol.sizeDelta = new Vector2(symbol, U(KeyHeight));
                var tag = _pad ? key.Tag.Size.x + U(5) : 0;
                var x = (key.Rect.sizeDelta.x - tag - symbol) / 2;
                if (_pad) key.Tag.Rect.At(x, (U(KeyHeight) - key.Tag.Size.y) / 2, key.Tag.Size.x, key.Tag.Size.y);
                key.Symbol.anchoredPosition = new Vector2(x + tag, 0);
            }
            float left = 0, right = 0;
            for (var i = 0; i < _hints.Count; i++)
            {
                var hint = _hints[i];
                var button = _pad ? hint.Pad : hint.Key;
                hint.Rect.gameObject.SetActive(button != null);
                if (button == null) continue;
                hint.Tag.Set(button, _pad);
                var tag = hint.Tag.Size;
                var labelWidth = hint.Label.GetPreferredValues(hint.Label.text).x + U(4);
                var width = tag.x + U(8) + labelWidth;
                hint.Tag.Rect.At(0, (U(LegendHeight) - tag.y) / 2, tag.x, tag.y);
                hint.Label.rectTransform.At(tag.x + U(8), 0, labelWidth, U(LegendHeight));
                // Cancel on the left, as Back is in every prompt bar; the rest from the right.
                if (i == 0)
                {
                    hint.Rect.At(left, 0, width, U(LegendHeight));
                    left += width + U(28);
                }
                else
                {
                    hint.Rect.AtRight(right, 0, width, U(LegendHeight));
                    right += width + U(28);
                }
            }
        }

        private void Paint()
        {
            foreach (var key in _keys)
            {
                var plate = ReferenceEquals(key, _focused);
                var ink = plate ? MenuKit.OnPlate : MenuKit.Ink;
                if (key.Label != null) key.Label.color = ink;
                if (key.Strokes == null) continue;
                // The shift is dim while it is off.
                var color = ReferenceEquals(key, _shiftKey) && _shift == Shift.Off ? plate ? MenuKit.OnPlateMuted : MenuKit.Faint : ink;
                foreach (var stroke in key.Strokes) stroke.color = color;
            }
            _shiftKey.Lock.enabled = _shift == Shift.Locked;
        }

        private void Focus(Key key, bool sound)
        {
            if (key == null || ReferenceEquals(key, _focused)) return;
            var first = _focused == null;
            _focused = key;
            if (sound) MenuSound.Play(MenuCue.Move);
            _plate.Target(key.Rect, first);
            Paint();
        }

        // Left and right run round the row; up and down run round the columns, to the key nearest the column the player
        // was last moving in, so a pass through the wide keys of the bottom row comes back to the same letter.
        private void Navigate(int dx, int dy)
        {
            var row = _rows[_focused.Row];
            if (dx != 0)
            {
                var next = row[(row.IndexOf(_focused) + dx + row.Count) % row.Count];
                _column = (next.Left + next.Right) / 2;
                Focus(next, true);
                return;
            }
            var target = _rows[(_focused.Row + dy + GridRows) % GridRows];
            foreach (var key in target)
                if (_column >= key.Left && _column < key.Right)
                {
                    Focus(key, true);
                    return;
                }
            Focus(target[target.Count - 1], true);
        }

        private static void Press(Key key) => key?.Press?.Invoke();

        private void Type(Key key)
        {
            Insert(Character(key).ToString(), true);
            if (_shift != Shift.Once) return;
            _shift = Shift.Off;
            Relabel();
        }

        private void Insert(string typed, bool sound)
        {
            var room = _maxLength - Text.Length;
            if (room <= 0 || typed.Length == 0)
            {
                if (sound) MenuSound.Play(MenuCue.Off);
                return;
            }
            if (typed.Length > room) typed = typed.Substring(0, room);
            Text = Text.Insert(Caret, typed);
            Caret += typed.Length;
            if (sound) MenuSound.Play(MenuCue.Move);
        }

        private void Backspace(bool sound)
        {
            if (Caret == 0) return;
            Text = Text.Remove(Caret - 1, 1);
            Caret--;
            if (sound) MenuSound.Play(MenuCue.Move);
        }

        private void MoveCaret(int step)
        {
            var caret = Mathf.Clamp(Caret + step, 0, Text.Length);
            if (caret == Caret) return;
            Caret = caret;
            MenuSound.Play(MenuCue.Move);
        }

        // Off, on for the next letter, then on until pressed again.
        private void NextShift()
        {
            _shift = _shift == Shift.Locked ? Shift.Off : _shift + 1;
            MenuSound.Play(_shift == Shift.Off ? MenuCue.Off : MenuCue.On);
            Relabel();
        }

        private void NextLayer()
        {
            _layerIndex = (_layerIndex + 1) % _layers.Count;
            MenuSound.Play(MenuCue.Move);
            Relabel();
            Retag();
        }

        private void Done() => _result = KeyboardResult.Done;

        private void Cancel() => _result = KeyboardResult.Cancelled;

        private string Clip(string text) => text.Length > _maxLength ? text.Substring(0, _maxLength) : text;

        // Under the field, or over it where there is no room below, kept on the screen.
        private void Place()
        {
            if (_field == null || !_field) return;
            _field.GetWorldCorners(_corners);
            var area = _layer.rect;
            Vector2 low = _layer.InverseTransformPoint(_corners[0]), high = _layer.InverseTransformPoint(_corners[2]);
            var size = _panel.sizeDelta;
            var x = Mathf.Clamp(low.x - area.xMin, U(Margin), Mathf.Max(U(Margin), area.width - size.x - U(Margin)));
            var y = area.yMax - low.y + U(FieldGap);
            if (y + size.y > area.height - U(Margin)) y = Mathf.Max(U(Margin), area.yMax - high.y - U(FieldGap) - size.y);
            _panel.anchoredPosition = new Vector2(x, -y);
        }

        // What is held as the grid opens or comes back counts as held, not pressed: the button that opened it does nothing
        // until it is pressed again, and a hover waits for the pointer to move.
        private void Prime()
        {
            if (ReadPadState(out var held, out var move))
            {
                _buttons = held;
                _move = move;
                _moveAt = Time.unscaledTime + FirstRepeat;
            }
            var position = UnityEngine.Input.mousePosition;
            _pointer = new Vector2(position.x, position.y);
        }

        private static bool ReadPadState(out int held, out Vector2Int move)
        {
            held = 0;
            move = Vector2Int.zero;
            if (_padFailed) return false;
            try
            {
                var pad = Gamepad.current;
                if (pad == null) return false;
                held = Bit(Button.A, pad.buttonSouth) | Bit(Button.B, pad.buttonEast) | Bit(Button.X, pad.buttonWest)
                    | Bit(Button.Y, pad.buttonNorth) | Bit(Button.LB, pad.leftShoulder) | Bit(Button.RB, pad.rightShoulder)
                    | Bit(Button.LT, pad.leftTrigger) | Bit(Button.RT, pad.rightTrigger) | Bit(Button.Start, pad.startButton);
                move = new Vector2Int(
                    pad.dpad.right.isPressed || pad.leftStick.right.isPressed ? 1 : pad.dpad.left.isPressed || pad.leftStick.left.isPressed ? -1 : 0,
                    pad.dpad.down.isPressed || pad.leftStick.down.isPressed ? 1 : pad.dpad.up.isPressed || pad.leftStick.up.isPressed ? -1 : 0);
            }
            catch (Exception error)
            {
                _padFailed = true;
                RuntimeEntryPoint.LogSource?.LogWarning("The on-screen keyboard stopped reading the controller: " + error.Message);
                return false;
            }
            // One axis at a time, vertical first, as the menus move.
            if (move.y != 0) move.x = 0;
            return true;
        }

        private void ReadPad()
        {
            if (!ReadPadState(out var held, out var move)) return;
            var now = Time.unscaledTime;
            if (move != _move)
            {
                _move = move;
                _moveAt = now + FirstRepeat;
                if (move != Vector2Int.zero) Navigate(move.x, move.y);
            }
            else if (move != Vector2Int.zero && now >= _moveAt)
            {
                _moveAt = now + NextRepeat;
                Navigate(move.x, move.y);
            }
            var pressed = held & ~_buttons;
            _buttons = held;
            for (var b = 0; b <= (int)Button.Start && _result == KeyboardResult.Typing; b++)
            {
                var bit = 1 << b;
                if ((pressed & bit) != 0) _buttonAt = now + FirstRepeat;
                else if ((held & bit) == 0 || !Repeats[b] || now < _buttonAt) continue;
                else _buttonAt = now + NextRepeat;
                Run((Button)b);
            }
        }

        private static int Bit(Button button, ButtonControl control) => control.isPressed ? 1 << (int)button : 0;

        private void Run(Button button)
        {
            switch (button)
            {
                case Button.A: Press(_focused); break;
                case Button.B: Cancel(); break;
                case Button.X: Backspace(true); break;
                case Button.Y: Insert(" ", true); break;
                case Button.LB: MoveCaret(-1); break;
                case Button.RB: MoveCaret(1); break;
                case Button.LT: NextShift(); break;
                case Button.RT: NextLayer(); break;
                case Button.Start: Done(); break;
            }
        }

        // A keyboard types as it does into any field, at the caret, which its arrows, Home and End move.
        private void ReadKeys()
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
            {
                Cancel();
                return;
            }
            if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow)) MoveCaret(-1);
            if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow)) MoveCaret(1);
            if (UnityEngine.Input.GetKeyDown(KeyCode.Home)) Caret = 0;
            if (UnityEngine.Input.GetKeyDown(KeyCode.End)) Caret = Text.Length;
            if (UnityEngine.Input.GetKeyDown(KeyCode.Delete) && Caret < Text.Length) Text = Text.Remove(Caret, 1);
            if ((UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl))
                && UnityEngine.Input.GetKeyDown(KeyCode.V))
                Insert((GUIUtility.systemCopyBuffer ?? "").Replace('\n', ' ').Replace('\r', ' '), false);
            foreach (var c in UnityEngine.Input.inputString ?? "")
            {
                if (c == '\b') Backspace(false);
                else if (c is '\n' or '\r')
                {
                    Done();
                    return;
                }
                else if (!char.IsControl(c)) Insert(c.ToString(), false);
            }
        }

        // The pointer focuses the key it moves over and a click types it. A click away from the grid and its field
        // keeps the text, as it does when typing on a keyboard.
        private void ReadMouse()
        {
            var position = UnityEngine.Input.mousePosition;
            var pointer = new Vector2(position.x, position.y);
            var moved = (pointer - _pointer).sqrMagnitude > 1f;
            var click = UnityEngine.Input.GetMouseButtonDown(0);
            if (!moved && !click) return;
            _pointer = pointer;
            Key over = null;
            foreach (var key in _keys)
                if (Hit(key.Rect, pointer))
                    over = key;
            if (over != null && moved)
            {
                _column = (over.Left + over.Right) / 2;
                Focus(over, true);
            }
            foreach (var hint in _hints)
                if (hint.Rect.gameObject.activeSelf)
                    hint.Label.color = Hit(hint.Rect, pointer) ? MenuKit.Ink : MenuKit.Soft;
            if (!click) return;
            if (over != null)
            {
                Focus(over, false);
                Press(over);
                return;
            }
            foreach (var hint in _hints)
                if (hint.Rect.gameObject.activeSelf && Hit(hint.Rect, pointer))
                {
                    hint.Click();
                    return;
                }
            if (!Hit(_panel, pointer) && !Hit(_field, pointer)) Done();
        }

        private static bool Hit(RectTransform rect, Vector2 pointer)
            => rect != null && rect && RectTransformUtility.RectangleContainsScreenPoint(rect, pointer, null);
    }
}
