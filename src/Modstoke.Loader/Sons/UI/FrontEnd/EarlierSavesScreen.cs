// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Modstoke.Localization;
using Modstoke.Sons;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// One world's earlier saves, newest first, each with its picture, the day it reached and its size. Restoring one
    /// asks first, and what the world is now is kept as an earlier save of its own, so a restore can be undone here
    /// the same way.
    /// </summary>
    internal sealed class EarlierSavesScreen : FrontScreen
    {
        private const float PanelTop = 120, StripHeight = 143, RowsTop = 22, RowHeight = 78, RowGap = 4, DetailsWidth = 440;

        private sealed class RowView
        {
            internal RectTransform Rect;
            internal Focusable Focus;
            internal Image Highlight;
            internal RawImage Thumbnail;
            internal TextMeshProUGUI Title, Detail;
        }

        private readonly bool _host;
        private readonly string _world, _slotDirectory;
        private readonly Action<SaveHistoryEntry> _restored;
        private readonly Dictionary<string, Texture2D> _thumbnails = new();
        private IReadOnlyList<SaveHistoryEntry> _entries = Array.Empty<SaveHistoryEntry>();
        private Task<IReadOnlyList<SaveHistoryEntry>> _listing;
        private Task<PngTextures.Image> _decoding;
        private string _decodingId;
        private Task _restoring;
        private SaveHistoryEntry _restoringEntry;
        private RowView[] _rows = Array.Empty<RowView>();
        private Glide _rowMark;
        private TextMeshProUGUI _section, _title, _count, _note, _when, _line, _tag, _restoreLabel;
        private RectTransform _details, _scrollThumb;
        private RawImage _bigThumbnail;
        private int _top, _selected;
        private float _trackHeight;
        private string _problem;
        private bool _built, _listed;

        /// <param name="world">The world's name as the saved games list shows it.</param>
        /// <param name="restored">Runs on the game thread once an earlier save is back in the slot.</param>
        internal EarlierSavesScreen(bool host, string world, string slotDirectory, Action<SaveHistoryEntry> restored)
        {
            _host = host;
            _world = world;
            _slotDirectory = slotDirectory;
            _restored = restored;
        }

        protected override void Build()
        {
            _built = false;
            var pad = U(60);
            MenuKit.Backdrop(Root, U(StripHeight - 24), U(110));
            _section = MenuKit.Text(Root, "Section", "", U(14), true, MenuKit.Muted);
            _section.characterSpacing = 32;
            _section.rectTransform.At(pad, U(30), U(1000), U(18));
            _title = MenuKit.Text(Root, "Title", "", U(50), true, MenuKit.Ink);
            _title.characterSpacing = 5;
            _title.rectTransform.At(pad, U(39), U(1000), U(80));

            var list = MenuKit.Box(Root, "Saves").Place(0, 0, 1, 1, pad, U(StripHeight), pad + U(DetailsWidth + 40), U(PanelTop));
            MenuKit.Hairline(list, "Top", 0);
            var panelHeight = Root.rect.height - U(StripHeight) - U(PanelTop);
            var visible = Mathf.Clamp(Mathf.FloorToInt((panelHeight - U(RowsTop) - U(46)) / U(RowHeight + RowGap)), 3, 9);
            _rowMark = new Glide(list, false);
            _rows = new RowView[visible];
            for (var i = 0; i < visible; i++) _rows[i] = Row(list, i);
            new Cascade(_rows.Select(row => row.Rect));
            _trackHeight = visible * U(RowHeight + RowGap) - U(RowGap);
            var track = MenuKit.Box(list, "Scroll").AtRight(U(8), U(RowsTop), U(3), _trackHeight);
            _scrollThumb = MenuKit.Box(track, "Thumb");
            MenuKit.Fill(_scrollThumb, null, MenuKit.Rule);
            _note = MenuKit.Text(list, "Note", "", U(15), false, MenuKit.Muted);
            _note.rectTransform.Place(0, 0, 1, 0, U(8), U(12), U(240), -U(34));
            _count = MenuKit.Text(list, "Count", "", U(14), false, MenuKit.Muted, TextAlignmentOptions.MidlineRight);
            _count.rectTransform.Place(1, 0, 1, 0, -U(260), U(12), U(8), -U(34));

            _details = MenuKit.Box(Root, "Details").Place(1, 0, 1, 1, -(pad + U(DetailsWidth)), U(StripHeight), pad, U(PanelTop));
            MenuKit.Hairline(_details, "Top", 0);
            var width = U(DetailsWidth);
            var height = width * 9f / 16f;
            var thumb = MenuKit.Box(_details, "Thumbnail").At(0, U(22), width, height);
            _bigThumbnail = thumb.gameObject.AddComponent<RawImage>();
            _bigThumbnail.raycastTarget = false;
            var y = U(22) + height + U(16);
            _when = MenuKit.Text(_details, "When", "", U(28), true, MenuKit.Ink, TextAlignmentOptions.MidlineLeft, true);
            _when.rectTransform.Strip(y, U(40), 0, 0);
            _line = MenuKit.Text(_details, "Line", "", U(16), false, MenuKit.Soft);
            _line.rectTransform.Strip(y + U(42), U(24), 0, 0);
            _tag = MenuKit.Text(_details, "Tag", "", U(15), false, MenuKit.Muted);
            _tag.rectTransform.Strip(y + U(66), U(22), 0, 0);
            var restore = MenuKit.Box(_details, "Restore").Place(0, 0, 1, 0, 0, U(20), 0, -U(78));
            var fill = MenuKit.Fill(restore, MenuKit.Brush, MenuKit.Soft);
            _restoreLabel = MenuKit.Text(restore, "Label", "", U(21), true, MenuKit.Dark, TextAlignmentOptions.Center);
            _restoreLabel.characterSpacing = 16;
            _restoreLabel.rectTransform.Place(0, 0, 1, 1);
            Add(restore, () => Confirm(Selected()), focused => fill.color = focused ? MenuKit.Ink : MenuKit.Soft,
                enabled: () => _restoring == null);
            _built = true;
        }

        private RowView Row(RectTransform list, int slot)
        {
            var rect = MenuKit.Box(list, "Row " + slot).Strip(U(RowsTop + slot * (RowHeight + RowGap)), U(RowHeight), 0, U(16));
            var view = new RowView { Rect = rect };
            view.Highlight = MenuKit.Fill(MenuKit.Box(rect, "Selected").Place(0, 0, 1, 1), null, MenuKit.RowSelected);
            var thumb = MenuKit.Box(rect, "Thumbnail").At(U(14), U((RowHeight - 63) / 2), U(112), U(63));
            view.Thumbnail = thumb.gameObject.AddComponent<RawImage>();
            view.Thumbnail.raycastTarget = false;
            view.Title = MenuKit.Text(rect, "Title", "", U(19), true, MenuKit.Ink, TextAlignmentOptions.MidlineLeft, true);
            view.Title.rectTransform.Place(0, 0.5f, 1, 1, U(140), -U(4), U(16), U(8));
            view.Detail = MenuKit.Text(rect, "Detail", "", U(14), false, MenuKit.Muted, TextAlignmentOptions.MidlineLeft, true);
            view.Detail.rectTransform.Place(0, 0, 1, 0.5f, U(140), U(8), U(16), -U(4));
            view.Focus = Add(rect, () => ActivateRow(slot), _ => PaintHighlights());
            view.Focus.Mark = _rowMark;
            view.Focus.Flash = Flashing(view.Title);
            return view;
        }

        internal override void Retext()
        {
            var culture = Languages.CultureOf(Languages.Current);
            _section.text = (_world ?? Text[_host ? "games.hostTitle" : "games.singleTitle"]).ToUpper(culture);
            _title.text = Text["saves.earlier"].ToUpper(culture);
            _restoreLabel.text = Text["saves.restore"].ToUpper(culture);
            Paint();
        }

        internal override void Opened()
        {
            if (!_listed)
            {
                _listed = true;
                _listing = SaveHistory.ListAsync(_slotDirectory);
            }
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
                var prompts = new List<PromptSpec> { new(Text["common.back"], "B", "ESC", () => { if (!OnBack()) FrontEnd.Pop(); }) };
                if (Selected() != null && _restoring == null) prompts.Add(new PromptSpec(Text["saves.restore"], "A", "ENTER", null));
                return prompts;
            }
        }

        // A restore under way finishes here before the list it reports to is shown again.
        internal override bool OnBack() => _restoring != null;

        internal override void Tick()
        {
            if (_listing is { IsCompleted: true } listing)
            {
                _listing = null;
                if (listing.IsCompletedSuccessfully) _entries = listing.Result;
                else
                {
                    _problem = Text["saves.listFailed"];
                    RuntimeEntryPoint.LogSource?.LogWarning("Earlier saves could not be listed: " + listing.Exception?.GetBaseException().Message);
                }
                _selected = 0;
                FocusSelectedRow();
                FrontEnd.RefreshPrompts();
            }
            if (_restoring is { IsCompleted: true } restoring) Finish(restoring);
            LoadThumbnails();
            if (FrontInput.Scroll != 0 && _entries.Count > _rows.Length)
            {
                _top = Mathf.Clamp(_top - Math.Sign(FrontInput.Scroll), 0, _entries.Count - _rows.Length);
                Paint();
            }
        }

        protected override void Navigate(int dx, int dy)
        {
            var slot = Focused != null ? SlotOf(Focused) : -1;
            if (slot < 0 || dy == 0 || _entries.Count == 0)
            {
                base.Navigate(dx, dy);
                return;
            }
            var index = Mathf.Clamp(_top + slot + dy, 0, _entries.Count - 1);
            if (index == _selected && index == _top + slot) return;
            Select(index);
            FocusSelectedRow();
        }

        protected override void FocusChanged(Focusable previous, Focusable current)
        {
            if (FromPointer || current == null) return;
            var slot = SlotOf(current);
            if (slot >= 0 && _top + slot < _entries.Count && _selected != _top + slot) Select(_top + slot);
        }

        /// <summary>A save's time as the player's language writes it.</summary>
        internal static string Time(DateTimeOffset saved) => saved.LocalDateTime.ToString("g", Languages.CultureOf(Languages.Current));

        private static string DayAndSize(SaveHistoryEntry entry)
        {
            var size = Text.Get("saves.size", ("size", entry.SizeBytes / 1048576.0));
            return entry.Day is int day ? Text.Get("saves.day", ("days", day)) + " · " + size : size;
        }

        // Why a copy was kept, when it was not an ordinary save.
        // A restore can reuse an identical copy already kept; the flag, not the reason, marks the one to undo to.
        private static string Reason(SaveHistoryEntry entry) => entry.Undo ? Text["saves.reason.restore"] : entry.Reason switch
        {
            "restore" => Text["saves.reason.restore"],
            "load" => Text["saves.reason.load"],
            "game-backup" => Text["saves.reason.gameBackup"],
            _ => null
        };

        private SaveHistoryEntry Selected() => _selected >= 0 && _selected < _entries.Count ? _entries[_selected] : null;

        private int SlotOf(Focusable item) => Array.FindIndex(_rows, row => row != null && ReferenceEquals(row.Focus, item));

        private void Select(int index)
        {
            _selected = index;
            _problem = null;
            PaintHighlights();
            PaintDetails();
            PaintNote();
            FrontEnd.RefreshPrompts();
        }

        private void FocusSelectedRow()
        {
            if (_rows.Length == 0) return;
            if (_entries.Count == 0)
            {
                Paint();
                Focus(null);
                return;
            }
            _selected = Mathf.Clamp(_selected, 0, _entries.Count - 1);
            if (_selected < _top) _top = _selected;
            else if (_selected >= _top + _rows.Length) _top = _selected - _rows.Length + 1;
            Paint();
            Focus(_rows[_selected - _top].Focus);
        }

        // Enter or the controller asks at once; the mouse selects first and asks on a second click.
        private void ActivateRow(int slot)
        {
            var index = _top + slot;
            if (index >= _entries.Count) return;
            if (index != _selected)
            {
                Select(index);
                if (FromPointer) return;
            }
            Confirm(Selected());
        }

        private void Confirm(SaveHistoryEntry entry)
        {
            if (entry == null || _restoring != null) return;
            FrontEnd.Push(new ConfirmScreen("", "saves.restoreBody", "saves.restore", "common.cancel", () => Restore(entry),
                cautious: true, titleText: Text.Get("saves.restoreTitle", ("when", Time(entry.Saved)))));
        }

        private void Restore(SaveHistoryEntry entry)
        {
            _restoringEntry = entry;
            _restoring = SaveHistory.RestoreAsync(_slotDirectory, entry);
            _problem = null;
            PaintNote();
            FrontEnd.RefreshPrompts();
        }

        private void Finish(Task restoring)
        {
            _restoring = null;
            if (restoring.IsCompletedSuccessfully)
            {
                MenuSound.Play(MenuCue.Activate);
                _restored?.Invoke(_restoringEntry);
                FrontEnd.Pop(true);
                return;
            }
            _problem = Text["saves.restoreFailed"];
            RuntimeEntryPoint.LogSource?.LogWarning("Save " + Path.GetFileName(_slotDirectory) + " could not be restored: "
                + restoring.Exception?.GetBaseException().Message);
            PaintNote();
            FrontEnd.RefreshPrompts();
        }

        // One picture at a time, rows on screen first: decoded on a worker, made a texture here.
        private void LoadThumbnails()
        {
            if (_decoding != null)
            {
                if (!_decoding.IsCompleted) return;
                // A copy without a picture keeps the plain box.
                Texture2D texture = null;
                if (_decoding.IsCompletedSuccessfully)
                {
                    using var image = _decoding.Result;
                    texture = PngTextures.Create(image, "Earlier save " + _decodingId);
                }
                else _ = _decoding.Exception;
                _thumbnails[_decodingId] = texture;
                _decoding = null;
                Paint();
                return;
            }
            for (var i = 0; i < _rows.Length && _top + i < _entries.Count; i++)
            {
                var entry = _entries[_top + i];
                if (_thumbnails.ContainsKey(entry.Id)) continue;
                var path = Path.Combine(entry.Directory, "slot", "SaveDataThumbnail.png");
                _decodingId = entry.Id;
                _decoding = Task.Run(() => PngTextures.Decode(File.ReadAllBytes(path)));
                return;
            }
        }

        private void Paint()
        {
            if (!_built || Root == null || !Root) return;
            for (var i = 0; i < _rows.Length; i++) PaintRow(_rows[i], _top + i);
            PaintHighlights();
            PaintDetails();
            PaintNote();
            _count.text = _listing == null ? Text.Count("saves.count", _entries.Count) : "";
            var scrolls = _entries.Count > _rows.Length;
            _scrollThumb.gameObject.SetActive(scrolls);
            if (!scrolls) return;
            var size = _trackHeight * _rows.Length / _entries.Count;
            _scrollThumb.At(0, (_trackHeight - size) * _top / (_entries.Count - _rows.Length), U(3), size);
        }

        private void PaintNote()
        {
            if (!_built) return;
            _note.color = _problem != null ? MenuKit.Red : MenuKit.Muted;
            _note.text = _problem ?? (_restoring != null ? Text["saves.restoring"]
                : _listing != null ? Text["saves.reading"]
                : _entries.Count == 0 ? Text["saves.none"] : "");
        }

        private void PaintRow(RowView view, int index)
        {
            var shown = index < _entries.Count;
            if (view.Rect.gameObject.activeSelf != shown) view.Rect.gameObject.SetActive(shown);
            if (!shown) return;
            var entry = _entries[index];
            var texture = _thumbnails.TryGetValue(entry.Id, out var loaded) ? loaded : null;
            view.Thumbnail.texture = texture;
            view.Thumbnail.color = texture != null ? Color.white : MenuKit.Cell;
            view.Title.text = SavedGamesScreen.WhenOf(entry.Saved.LocalDateTime);
            view.Detail.text = Reason(entry) is { } reason ? DayAndSize(entry) + " · " + reason : DayAndSize(entry);
        }

        private void PaintHighlights()
        {
            if (!_built) return;
            for (var i = 0; i < _rows.Length; i++)
            {
                var view = _rows[i];
                var focused = ReferenceEquals(view.Focus, Focused);
                view.Highlight.enabled = _top + i == _selected && !focused;
                view.Title.color = focused ? MenuKit.OnPlate : MenuKit.Ink;
                view.Detail.color = focused ? MenuKit.OnPlateMuted : MenuKit.Muted;
            }
            _rowMark.Follow();
        }

        private void PaintDetails()
        {
            if (!_built) return;
            var entry = Selected();
            _details.gameObject.SetActive(entry != null);
            if (entry == null) return;
            var texture = _thumbnails.TryGetValue(entry.Id, out var loaded) ? loaded : null;
            _bigThumbnail.texture = texture;
            _bigThumbnail.color = texture != null ? Color.white : MenuKit.Cell;
            _when.text = SavedGamesScreen.WhenOf(entry.Saved.LocalDateTime);
            _line.text = DayAndSize(entry);
            _tag.text = Reason(entry) ?? "";
        }
    }
}
