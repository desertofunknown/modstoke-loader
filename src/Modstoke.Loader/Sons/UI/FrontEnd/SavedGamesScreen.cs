// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using Modstoke.Localization;
using Sons.Save;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// Single player, or hosting, on one screen: the player's saved games of that kind, newest first, under a
    /// new game. A save continues with one press; a new game picks its mode on the right.
    /// </summary>
    internal sealed class SavedGamesScreen : FrontScreen
    {
        private const float PanelTop = 120, StripHeight = 143, RowsTop = 22, RowHeight = 78, RowGap = 4;
        private const float DetailsWidth = 440, DoubleClickSeconds = 0.4f, HoldSeconds = 1.1f;
        private static readonly string[] Modes = { "Normal", "Hard", "HardSurvival", "Peaceful", "Creative", "Custom" };

        private sealed class Save
        {
            internal uint Id;
            internal string Name = "", Mode = "";
            internal int Days;
            internal DateTime Saved;
            internal bool Read, Unreadable;
        }

        private sealed class RowView
        {
            internal RectTransform Rect;
            internal Focusable Focus;
            internal Image Highlight;
            internal RawImage Thumbnail;
            internal TextMeshProUGUI Plus, Title, Detail;
        }

        private sealed class ModeView
        {
            internal string Mode;
            internal Focusable Focus;
            internal TextMeshProUGUI Label;
        }

        private readonly SaveGameType _type;
        private readonly bool _host;
        private readonly List<Save> _saves = new();
        private readonly Queue<Save> _unread = new();
        private readonly Dictionary<uint, Texture2D> _thumbnails = new();
        private readonly List<ModeView> _modes = new();
        private RowView[] _rows = Array.Empty<RowView>();
        private Glide _rowMark;
        private SectionNav _sections;
        private TextMeshProUGUI _section, _title, _count, _note;
        private TextMeshProUGUI _saveTitle, _saveLine, _saveWhen, _newTitle, _newHint, _modeAbout, _continueLabel, _deleteLabel;
        private RectTransform _savePane, _newPane, _scrollThumb, _holdFill;
        private RawImage _bigThumbnail;
        private Focusable _delete;
        // Row 0 is the new game; saves follow.
        private int _top, _selected, _lastClickSlot = -1;
        private float _lastClick, _trackHeight, _hold;
        private string _problem;
        private bool _listed, _built, _holdingButton, _awaitRelease;

        internal SavedGamesScreen(SaveGameType type)
        {
            _type = type;
            _host = type != SaveGameType.SinglePlayer;
        }

        protected override void Build()
        {
            _built = false;
            var pad = U(60);
            MenuKit.Backdrop(Root, U(StripHeight - 24), U(110));
            _section = MenuKit.Text(Root, "Section", "", U(14), true, MenuKit.Muted);
            _section.characterSpacing = 32;
            _section.rectTransform.At(pad, U(30), U(700), U(18));
            _title = MenuKit.Text(Root, "Title", "", U(50), true, MenuKit.Ink);
            _title.characterSpacing = 5;
            _title.rectTransform.At(pad, U(46), U(1000), U(66));
            _sections = null;
            if (_host)
            {
                _sections = new SectionNav(Root, pad, U(56), true);
                Add(_sections.Other, OpenJoin, _sections.Hover);
            }

            // Content sits on the veil: columns are set apart by space and a rule, not by boxes.
            var list = MenuKit.Box(Root, "Games").Place(0, 0, 1, 1, pad, U(StripHeight), pad + U(DetailsWidth + 40), U(PanelTop));
            MenuKit.Hairline(list, "Top", 0);
            var panelHeight = Root.rect.height - U(StripHeight) - U(PanelTop);
            var visible = Mathf.Clamp(Mathf.FloorToInt((panelHeight - U(RowsTop) - U(46)) / U(RowHeight + RowGap)), 3, 9);
            _rowMark = new Glide(list, false);
            _rows = new RowView[visible];
            for (var i = 0; i < visible; i++) _rows[i] = Row(list, i);
            _trackHeight = visible * U(RowHeight + RowGap) - U(RowGap);
            var track = MenuKit.Box(list, "Scroll").AtRight(U(8), U(RowsTop), U(3), _trackHeight);
            _scrollThumb = MenuKit.Box(track, "Thumb");
            MenuKit.Fill(_scrollThumb, null, MenuKit.Rule);
            _note = MenuKit.Text(list, "Note", "", U(15), false, MenuKit.Muted);
            _note.rectTransform.Place(0, 0, 1, 0, U(8), U(12), U(240), -U(34));
            _count = MenuKit.Text(list, "Count", "", U(14), false, MenuKit.Muted, TextAlignmentOptions.MidlineRight);
            _count.rectTransform.Place(1, 0, 1, 0, -U(260), U(12), U(8), -U(34));

            var details = MenuKit.Box(Root, "Details").Place(1, 0, 1, 1, -(pad + U(DetailsWidth)), U(StripHeight), pad, U(PanelTop));
            MenuKit.Hairline(details, "Top", 0);
            BuildSavePane(details);
            BuildNewPane(details);
            _built = true;
        }

        private RowView Row(RectTransform list, int slot)
        {
            var rect = MenuKit.Box(list, "Row " + slot).Strip(U(RowsTop + slot * (RowHeight + RowGap)), U(RowHeight), 0, U(16));
            var view = new RowView { Rect = rect };
            // The selected row keeps a faint fill while focus is elsewhere; the focus mark glides over it.
            view.Highlight = MenuKit.Fill(MenuKit.Box(rect, "Selected").Place(0, 0, 1, 1), null, MenuKit.RowSelected);
            var thumb = MenuKit.Box(rect, "Thumbnail").At(U(14), U((RowHeight - 63) / 2), U(112), U(63));
            view.Thumbnail = thumb.gameObject.AddComponent<RawImage>();
            view.Thumbnail.raycastTarget = false;
            view.Plus = MenuKit.Text(thumb, "Plus", "+", U(34), false, MenuKit.Ink, TextAlignmentOptions.Center);
            view.Plus.rectTransform.Place(0, 0, 1, 1);
            view.Title = MenuKit.Text(rect, "Title", "", U(19), true, MenuKit.Ink, TextAlignmentOptions.MidlineLeft, true);
            view.Title.rectTransform.Place(0, 0.5f, 1, 1, U(140), -U(4), U(16), U(8));
            view.Detail = MenuKit.Text(rect, "Detail", "", U(14), false, MenuKit.Muted, TextAlignmentOptions.MidlineLeft, true);
            view.Detail.rectTransform.Place(0, 0, 1, 0.5f, U(140), U(8), U(16), -U(4));
            view.Focus = Add(rect, () => ActivateRow(slot), _ => PaintHighlights());
            view.Focus.Mark = _rowMark;
            view.Focus.Flash = Flashing(view.Title);
            return view;
        }

        private void BuildSavePane(RectTransform details)
        {
            _savePane = MenuKit.Box(details, "Save").Place(0, 0, 1, 1);
            var width = U(DetailsWidth);
            var height = width * 9f / 16f;
            var thumb = MenuKit.Box(_savePane, "Thumbnail").At(0, U(22), width, height);
            _bigThumbnail = thumb.gameObject.AddComponent<RawImage>();
            _bigThumbnail.raycastTarget = false;
            var y = U(22) + height + U(16);
            _saveTitle = MenuKit.Text(_savePane, "Title", "", U(28), true, MenuKit.Ink, TextAlignmentOptions.MidlineLeft, true);
            _saveTitle.rectTransform.Strip(y, U(40), 0, 0);
            _saveLine = MenuKit.Text(_savePane, "Line", "", U(16), false, MenuKit.Soft);
            _saveLine.rectTransform.Strip(y + U(42), U(24), 0, 0);
            _saveWhen = MenuKit.Text(_savePane, "When", "", U(15), false, MenuKit.Muted);
            _saveWhen.rectTransform.Strip(y + U(66), U(22), 0, 0);

            // Deleting is held, not pressed: a bar fills while the button is down and letting go early cancels.
            var delete = MenuKit.Box(_savePane, "Delete").Place(0, 0, 1, 0, 0, U(86), 0, -U(126));
            _holdFill = MenuKit.Box(delete, "Hold").Place(0, 0, 0, 1);
            MenuKit.Fill(_holdFill, null, MenuKit.Hex(0xF0A08A, 0.28f));
            _deleteLabel = MenuKit.Text(delete, "Label", "", U(14), true, MenuKit.Muted, TextAlignmentOptions.Center);
            _deleteLabel.characterSpacing = 12;
            _deleteLabel.rectTransform.Place(0, 0, 1, 1);
            _delete = Add(delete, () =>
            {
                _holdingButton = true;
                MenuSound.Play(MenuCue.Move);
            }, _ => PaintDelete());

            var go = MenuKit.Box(_savePane, "Continue").Place(0, 0, 1, 0, 0, U(20), 0, -U(78));
            var fill = MenuKit.Fill(go, MenuKit.Brush, MenuKit.Soft);
            _continueLabel = MenuKit.Text(go, "Label", "", U(21), true, MenuKit.Dark, TextAlignmentOptions.Center);
            _continueLabel.characterSpacing = 16;
            _continueLabel.rectTransform.Place(0, 0, 1, 1);
            Add(go, ContinueSelected, focused => fill.color = focused ? MenuKit.Ink : MenuKit.Soft);
        }

        private void BuildNewPane(RectTransform details)
        {
            _newPane = MenuKit.Box(details, "New").Place(0, 0, 1, 1);
            _newTitle = MenuKit.Text(_newPane, "Title", "", U(28), true, MenuKit.Ink);
            _newTitle.rectTransform.Strip(U(22), U(40), 0, 0);
            _newHint = MenuKit.Text(_newPane, "Hint", "", U(16), false, MenuKit.Soft);
            _newHint.rectTransform.Strip(U(62), U(24), 0, 0);
            // Only the modes the game offers this player: it keeps Creative back until the story is finished.
            var offered = GameMenus.OfferedModes(_type, Modes);
            _modeAbout = MenuKit.Text(_newPane, "About", "", U(16), false, MenuKit.Soft, TextAlignmentOptions.TopLeft);
            _modeAbout.enableWordWrapping = true;
            _modeAbout.rectTransform.Strip(U(104 + offered.Length * 46 + 18), U(96), 0, 0);
            var mark = new Glide(_newPane, false);
            _modes.Clear();
            for (var i = 0; i < offered.Length; i++)
            {
                var mode = offered[i];
                var rect = MenuKit.Box(_newPane, mode).Strip(U(104 + i * 46), U(42), 0, 0);
                var view = new ModeView { Mode = mode };
                view.Label = MenuKit.Text(rect, "Label", "", U(18), true, MenuKit.Soft);
                view.Label.characterSpacing = 8;
                view.Label.rectTransform.Place(0, 0, 1, 1, U(18), 0, U(16), 0);
                view.Focus = Add(rect, () => StartNew(mode), focused =>
                {
                    view.Label.color = focused ? MenuKit.Ink : MenuKit.Soft;
                    if (focused) _modeAbout.text = Text["mode.about." + KeyOf(mode)];
                });
                view.Focus.Mark = mark;
                view.Focus.Flash = Flashing(view.Label);
                _modes.Add(view);
            }
        }

        internal override void Retext()
        {
            var culture = Languages.CultureOf(Languages.Current);
            _section.text = Text[_host ? "multiplayer.section" : "games.singleSection"].ToUpper(culture);
            _title.text = Text[_host ? "games.hostTitle" : "games.singleTitle"].ToUpper(culture);
            _newTitle.text = Text["games.newGame"].ToUpper(culture);
            _newHint.text = Text["games.chooseMode"];
            _continueLabel.text = Text["games.continue"].ToUpper(culture);
            foreach (var view in _modes) view.Label.text = Text["mode." + KeyOf(view.Mode)].ToUpper(culture);
            _sections?.Paint(FrontEnd.PadActive);
            Paint();
        }

        internal override void DeviceChanged() => _sections?.Paint(FrontEnd.PadActive);

        internal override void Opened()
        {
            if (!_listed) ListSaves();
            FocusSelectedRow();
        }

        internal override void Closed()
        {
            foreach (var texture in _thumbnails.Values)
                if (texture != null && texture) Object.Destroy(texture);
            _thumbnails.Clear();
        }

        internal override IReadOnlyList<PromptSpec> Prompts
        {
            get
            {
                var prompts = new List<PromptSpec>
                {
                    new(Text["common.back"], "B", "ESC", () => FrontEnd.Pop()),
                    new(Text["common.select"], "A", "ENTER", null)
                };
                if (SelectedSave() != null) prompts.Add(new PromptSpec(Text["games.deleteHold"], "X", "DEL", null));
                return prompts;
            }
        }

        // From the choices on the right, Back returns to the list before it leaves the screen.
        internal override bool OnBack()
        {
            if (Focused == null || SlotOf(Focused) >= 0 || _sections != null && ReferenceEquals(Focused.Rect, _sections.Other))
                return false;
            MenuSound.Play(MenuCue.Back);
            FocusSelectedRow();
            return true;
        }

        internal override void OnSection(int step)
        {
            if (_host) OpenJoin();
        }

        internal override void Tick()
        {
            // Saves are read a couple per frame, so a long list never stalls the menu.
            var read = false;
            for (var i = 0; i < 2 && _unread.Count > 0; i++)
            {
                ReadSave(_unread.Dequeue());
                read = true;
            }
            if (read)
            {
                var selected = SelectedSave();
                _saves.Sort((a, b) => b.Saved.CompareTo(a.Saved));
                if (selected != null) _selected = _saves.IndexOf(selected) + 1;
                Paint();
            }
            else LoadOneThumbnail();
            TickHold();
            if (FrontInput.Scroll != 0 && Count > _rows.Length)
            {
                _top = Mathf.Clamp(_top - Math.Sign(FrontInput.Scroll), 0, Count - _rows.Length);
                Paint();
            }
        }

        // A list longer than the screen scrolls when focus reaches its first or last row.
        protected override void Navigate(int dx, int dy)
        {
            var slot = Focused != null ? SlotOf(Focused) : -1;
            if (slot < 0 || dy == 0)
            {
                base.Navigate(dx, dy);
                return;
            }
            var index = Mathf.Clamp(_top + slot + dy, 0, Count - 1);
            if (index == _selected && index == _top + slot) return;
            Select(index);
            FocusSelectedRow();
        }

        // Moving focus with keys or a controller selects; the mouse only highlights until it clicks.
        protected override void FocusChanged(Focusable previous, Focusable current)
        {
            if (FromPointer || current == null) return;
            var slot = SlotOf(current);
            if (slot >= 0 && _top + slot < Count && _selected != _top + slot) Select(_top + slot);
        }

        private int Count => _saves.Count + 1;

        private Save SelectedSave() => _selected >= 1 && _selected <= _saves.Count ? _saves[_selected - 1] : null;

        private int SlotOf(Focusable item) => Array.FindIndex(_rows, row => row != null && ReferenceEquals(row.Focus, item));

        private void Select(int index)
        {
            _selected = index;
            _hold = 0;
            _problem = null;
            PaintHighlights();
            PaintDetails();
            PaintNote();
            FrontEnd.RefreshPrompts();
        }

        private void FocusSelectedRow()
        {
            if (_rows.Length == 0) return;
            _selected = Mathf.Clamp(_selected, 0, Count - 1);
            if (_selected < _top) _top = _selected;
            else if (_selected >= _top + _rows.Length) _top = _selected - _rows.Length + 1;
            Paint();
            Focus(_rows[_selected - _top].Focus);
        }

        private void ListSaves()
        {
            _listed = true;
            try
            {
                foreach (var id in GameMenus.SaveIds(_type))
                {
                    var save = new Save { Id = id };
                    _saves.Add(save);
                    _unread.Enqueue(save);
                }
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Saved games could not be listed: " + error.Message);
            }
            Paint();
        }

        private void ReadSave(Save save)
        {
            try
            {
                var data = SaveGameManager.GetData<GameStateData>(_type, (int)save.Id);
                if (data == null)
                {
                    save.Unreadable = true;
                    return;
                }
                save.Name = (data.GameName ?? "").Trim();
                save.Mode = data.GameType ?? "";
                save.Days = data.GameDays;
                // The save's own time, written in the round-trip format.
                if (DateTime.TryParse(data.SaveTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var saved))
                    save.Saved = saved.ToLocalTime();
                save.Read = true;
            }
            catch (Exception)
            {
                save.Unreadable = true;
            }
        }

        // One thumbnail per frame, for the rows on screen first.
        private void LoadOneThumbnail()
        {
            for (var i = 0; i < _rows.Length; i++)
            {
                var index = _top + i;
                if (index < 1 || index > _saves.Count) continue;
                var save = _saves[index - 1];
                if (_thumbnails.ContainsKey(save.Id)) continue;
                Texture2D texture = null;
                try
                {
                    texture = SaveGameManager.LoadThumbnail(_type, save.Id);
                }
                catch (Exception)
                {
                    // The row keeps its plain box.
                }
                _thumbnails[save.Id] = texture;
                PaintRow(_rows[i], index);
                if (ReferenceEquals(save, SelectedSave())) PaintDetails();
                return;
            }
        }

        // Enter or the controller acts on the row at once; the mouse selects first and acts on a double click.
        private void ActivateRow(int slot)
        {
            var index = _top + slot;
            if (index >= Count) return;
            var already = index == _selected;
            if (FromPointer)
            {
                var now = Time.unscaledTime;
                var doubled = already && slot == _lastClickSlot && now - _lastClick < DoubleClickSeconds;
                _lastClick = now;
                _lastClickSlot = slot;
                if (!doubled)
                {
                    if (!already) Select(index);
                    return;
                }
            }
            else if (!already) Select(index);
            if (index > 0) ContinueSelected();
            else if (_modes.Count > 0) Focus(_modes[0].Focus);
        }

        private void ContinueSelected()
        {
            var save = SelectedSave();
            if (save != null) Start(() => GameMenus.ContinueGame(_type, save.Id), "continue save " + save.Id);
        }

        private void StartNew(string mode) => Start(() => GameMenus.NewGame(_type, mode), "start a new " + mode + " game");

        private void Start(Action start, string what)
        {
            try
            {
                _problem = null;
                start();
                // Custom settings and host options are the game's own pages, which this layer already steps
                // aside for; anything else is now loading.
                if (!GameMenus.DialogOpen) FrontEnd.StepAside();
                MenuSound.Play(MenuCue.Activate);
                RuntimeEntryPoint.LogSource?.LogInfo("The menu asked the game to " + what + ".");
            }
            catch (Exception error)
            {
                _problem = Text["games.startFailed"];
                RuntimeEntryPoint.LogSource?.LogWarning("The game could not " + what + ": " + error);
            }
            PaintNote();
            FrontInput.Pause(0.5f);
        }

        private void OpenJoin()
        {
            try
            {
                GameMenus.Join();
                FrontEnd.StepAside();
                MenuSound.Play(MenuCue.Forward);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The Join screen could not open: " + error.Message);
            }
        }

        // X or Delete held, or the button held after a press or click on it, fills the bar; full, the save goes.
        private void TickHold()
        {
            if (_holdingButton && !FrontInput.ConfirmHeld && !FrontInput.Held) _holdingButton = false;
            var holding = SelectedSave() != null && (FrontInput.Alt1Held || _holdingButton);
            // After a deletion the button must be let go before the next one can start.
            if (!holding) _awaitRelease = false;
            if (_awaitRelease) holding = false;
            var before = _hold;
            _hold = holding ? _hold + Time.unscaledDeltaTime / HoldSeconds : Mathf.MoveTowards(_hold, 0, Time.unscaledDeltaTime * 4f);
            if (_hold >= 1f)
            {
                _hold = 0;
                _awaitRelease = true;
                _holdingButton = false;
                DeleteNow();
            }
            if (holding && before == 0) MenuSound.Play(MenuCue.Move);
            if (before != _hold) PaintDelete();
        }

        private void DeleteNow()
        {
            var save = SelectedSave();
            if (save == null) return;
            MenuSound.Play(MenuCue.Off);
            try
            {
                SaveGameManager.Delete(_type, (int)save.Id);
                _saves.Remove(save);
                if (_thumbnails.Remove(save.Id, out var texture) && texture != null && texture) Object.Destroy(texture);
                RuntimeEntryPoint.LogSource?.LogInfo("Deleted " + _type + " save " + save.Id + " from the menu.");
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Save " + save.Id + " could not be deleted: " + error.Message);
            }
            _top = Mathf.Clamp(_top, 0, Math.Max(0, Count - _rows.Length));
            FocusSelectedRow();
            Select(_selected);
        }

        private void Paint()
        {
            if (!_built || Root == null || !Root) return;
            for (var i = 0; i < _rows.Length; i++) PaintRow(_rows[i], _top + i);
            PaintHighlights();
            PaintDetails();
            PaintNote();
            _count.text = Text.Count("games.saves", _saves.Count);

            var scrolls = Count > _rows.Length;
            _scrollThumb.gameObject.SetActive(scrolls);
            if (!scrolls) return;
            var size = _trackHeight * _rows.Length / Count;
            _scrollThumb.At(0, (_trackHeight - size) * _top / (Count - _rows.Length), U(3), size);
        }

        private void PaintNote()
        {
            if (!_built) return;
            _note.color = _problem != null ? MenuKit.Red : MenuKit.Muted;
            _note.text = _problem ?? (_unread.Count > 0 ? Text["games.reading"] : _saves.Count == 0 ? Text["games.none"] : "");
        }

        private void PaintRow(RowView view, int index)
        {
            var shown = index < Count;
            if (view.Rect.gameObject.activeSelf != shown) view.Rect.gameObject.SetActive(shown);
            if (!shown) return;
            var save = index >= 1 ? _saves[index - 1] : null;
            view.Plus.enabled = save == null;
            var texture = save != null && _thumbnails.TryGetValue(save.Id, out var loaded) ? loaded : null;
            view.Thumbnail.texture = texture;
            view.Thumbnail.color = texture != null ? Color.white : MenuKit.Cell;
            view.Title.text = save == null ? Text["games.newGame"] : TitleOf(save);
            view.Detail.text = save == null ? Text["games.newGameHint"] : SubtitleOf(save);
        }

        private void PaintHighlights()
        {
            if (!_built) return;
            for (var i = 0; i < _rows.Length; i++)
            {
                var view = _rows[i];
                // The focus mark covers the focused row; the selection shows only while focus is elsewhere.
                view.Highlight.enabled = _top + i == _selected && !ReferenceEquals(view.Focus, Focused);
            }
            _rowMark.Follow();
        }

        private void PaintDetails()
        {
            if (!_built) return;
            var save = SelectedSave();
            _savePane.gameObject.SetActive(save != null);
            _newPane.gameObject.SetActive(save == null);
            if (save == null) return;
            var texture = _thumbnails.TryGetValue(save.Id, out var loaded) ? loaded : null;
            _bigThumbnail.texture = texture;
            _bigThumbnail.color = texture != null ? Color.white : MenuKit.Cell;
            _saveTitle.text = TitleOf(save);
            _saveLine.text = save.Read ? ModeAndDay(save) : "";
            _saveWhen.text = WhenOf(save);
            PaintDelete();
        }

        private void PaintDelete()
        {
            if (!_built) return;
            var holding = _hold > 0;
            _holdFill.anchorMax = new Vector2(Mathf.Clamp01(_hold), 1);
            _deleteLabel.text = Text["games.deleteHold"].ToUpper(Languages.CultureOf(Languages.Current));
            _deleteLabel.color = holding ? MenuKit.Red : ReferenceEquals(Focused, _delete) ? MenuKit.Ink : MenuKit.Muted;
        }

        // A hosted game carries the name it was hosted under; other saves are known by mode and day.
        private static string TitleOf(Save save)
        {
            if (save.Unreadable) return Text["games.unreadable"];
            if (!save.Read) return "…";
            return save.Name.Length > 0 ? save.Name : ModeAndDay(save);
        }

        private static string SubtitleOf(Save save)
            => save.Read && save.Name.Length > 0 ? ModeAndDay(save) + " · " + WhenOf(save) : WhenOf(save);

        private static string ModeAndDay(Save save)
            => Text.Get("games.modeAndDay", ("mode", ModeName(save.Mode)), ("days", save.Days));

        private static string WhenOf(Save save)
            => save.Read && save.Saved != default
                ? Text.Get("games.saved", ("when", save.Saved.ToString("g", Languages.CultureOf(Languages.Current))))
                : "";

        private static string ModeName(string mode)
        {
            var key = "mode." + KeyOf(mode);
            return Text.Has(key) ? Text[key] : mode;
        }

        // "HardSurvival" is the key "hardSurvival".
        private static string KeyOf(string mode)
            => string.IsNullOrEmpty(mode) ? "" : char.ToLowerInvariant(mode[0]) + mode.Substring(1);
    }
}
