// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using Modstoke.Diagnostics;
using Modstoke.Localization;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Modstoke.Loader
{
    internal static partial class ConsoleOverlay
    {
        private const int RetainedEntries = 4096, CatchUpReads = 16;
        private static readonly List<ConsoleEntry> Entries = new(RetainedEntries);
        private static readonly List<ConsoleEntry> Filtered = new(RetainedEntries);
        private static readonly List<LogRow> Rows = new();
        // All, errors (with fatal), warnings, info, debug: the chips' counts under the source and search filters.
        private static readonly int[] Counts = new int[5];
        private static string _sessionId;
        private static ConsoleEntry _selectedEntry;
        private static long _cursor, _dropped, _firstId;
        private static int _level, _hoverIndex = -1;
        private static float _scroll, _scrollGoal;
        private static bool _paused, _follow = true, _rowsDirty, _paintDirty;

        /// <summary>One pooled line of the log; the pool covers the visible height and is reused as it scrolls.</summary>
        private sealed class LogRow
        {
            internal RectTransform Rect;
            internal Image Back, Edge, BadgeBack;
            internal TextMeshProUGUI Time, Badge, Source, Message;
            internal long EntryId;
            internal int Index = -1;
        }

        private static void RefreshSnapshot()
        {
            var snapshot = ConsoleHub.Read(_paused ? long.MaxValue : _cursor, 256);
            var reset = !string.Equals(_sessionId, snapshot.SessionId, StringComparison.Ordinal);
            if (reset)
            {
                _sessionId = snapshot.SessionId;
                Entries.Clear();
                Filtered.Clear();
                _cursor = 0;
                _scroll = _scrollGoal = 0f;
                _selectedEntry = null;
                ShowEntry();
                CancelConfirmation();
                if (!_paused) snapshot = ConsoleHub.Read(0, 256);
            }
            var dropped = _dropped != snapshot.Dropped;
            _dropped = snapshot.Dropped;
            _firstId = snapshot.FirstId;
            ShowStatus(snapshot);
            RefreshCatalog(snapshot.Commands);
            if (_paused || snapshot.Entries.Length == 0)
            {
                // Filters, scrolling and selection render on their own; the rows only change here on a reset or eviction.
                if (reset || dropped) FilterEntries();
                return;
            }
            if (_cursor > 0 && _firstId > _cursor + 1)
                _notice.text = Text.Get("console.historyMoved", ("id", _firstId));
            // Reading back, the line at the top of the view stays where it is as lines arrive and old ones leave.
            var anchor = !_follow ? TopEntry() : null;
            for (var read = 1; ; read++)
            {
                foreach (var entry in snapshot.Entries)
                {
                    if (entry.Id <= _cursor) continue;
                    Entries.Add(entry);
                    _cursor = entry.Id;
                }
                // A full batch means more is waiting, as when the console opens on a long log.
                if (snapshot.Entries.Length < 256 || read == CatchUpReads) break;
                snapshot = ConsoleHub.Read(_cursor, 256);
            }
            if (Entries.Count > RetainedEntries) Entries.RemoveRange(0, Entries.Count - RetainedEntries);
            var before = anchor != null ? Filtered.IndexOf(anchor) : -1;
            FilterEntries();
            if (anchor == null) return;
            var after = Filtered.FindIndex(entry => entry.Id >= anchor.Id);
            if (before < 0 || after < 0) return;
            var shift = (after - before) * RowHeight;
            _scroll += shift;
            _scrollGoal += shift;
        }

        private static ConsoleEntry TopEntry()
        {
            var index = Mathf.FloorToInt(_scroll / RowHeight);
            return index >= 0 && index < Filtered.Count ? Filtered[index] : null;
        }

        private static void ShowStatus(ConsoleSnapshot snapshot)
        {
            var status = (snapshot.Ready ? Text["console.ready"] : Text["console.starting"]) + "  ·  " + snapshot.Phase
                + "  ·  " + Text.Count("console.mods", snapshot.Mods.Length) + "  ·  " + snapshot.Summary;
            if (_status.text != status) _status.text = status;
            _statusDot.color = snapshot.Ready ? MenuKit.Green : MenuKit.Amber;
        }

        private static void SetLevel(int level)
        {
            if (level == _level) return;
            _level = level;
            foreach (var chip in Chips) chip.Paint(chip);
            ApplyFilters();
        }

        private static void ApplyFilters()
        {
            FilterEntries();
            if (!_follow) _scrollGoal = _scroll = 0f;
        }

        private static void FilterEntries()
        {
            Filtered.Clear();
            Array.Clear(Counts, 0, Counts.Length);
            var source = _source.text.Trim();
            var search = _search.text.Trim();
            foreach (var entry in Entries)
            {
                if (source.Length != 0 && entry.Source.IndexOf(source, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (search.Length != 0 && entry.Message.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var group = Group(entry.Level);
                Counts[0]++;
                Counts[group]++;
                if (_level == 0 || group == _level) Filtered.Add(entry);
            }
            var culture = Languages.CultureOf(Languages.Current);
            for (var i = 0; i < Chips.Length; i++)
            {
                Chips[i].Count.text = Counts[i].ToString("N0", culture);
                Chips[i].Count.color = Counts[i] > 0 && i == 1 ? MenuKit.Red : Counts[i] > 0 && i == 2 ? MenuKit.Amber : MenuKit.Faint;
            }
            LayoutHeader(CanvasSize().x - 2f * Pad);
            _rowsDirty = true;
        }

        private static int Group(string level)
        {
            if (Is(level, "Error") || Is(level, "Fatal")) return 1;
            if (Is(level, "Warning") || Is(level, "Warn")) return 2;
            if (Is(level, "Debug") || Is(level, "Trace")) return 4;
            return 3;
        }

        private static bool Is(string level, string name) => string.Equals(level, name, StringComparison.OrdinalIgnoreCase);

        // The level as a short word in its color; a level the console does not know shows as it was logged.
        private static (string Text, Color Color) Badge(string level)
        {
            if (Is(level, "Fatal")) return (Upper(Text["console.badge.fatal"]), MenuKit.Red);
            if (Is(level, "Error")) return (Upper(Text["console.badge.error"]), MenuKit.Red);
            if (Is(level, "Warning") || Is(level, "Warn")) return (Upper(Text["console.badge.warning"]), MenuKit.Amber);
            if (Is(level, "Debug") || Is(level, "Trace")) return (Upper(Text["console.badge.debug"]), MenuKit.Faint);
            if (Is(level, "Info") || Is(level, "Message")) return (Upper(Text["console.badge.info"]), MenuKit.Soft);
            return (Upper(level), MenuKit.Muted);
        }

        private static float MaxScroll => Mathf.Max(0f, Filtered.Count * RowHeight - _viewportHeight);

        private static void ScrollBy(float delta)
        {
            _scrollGoal = Mathf.Clamp(_scrollGoal + delta, 0f, MaxScroll);
            // Scrolling up stops following; scrolling back to the end picks it up again.
            SetFollow(_scrollGoal >= MaxScroll - 0.5f);
        }

        private static void SetFollow(bool follow)
        {
            if (follow == _follow) return;
            _follow = follow;
            _followToggle.Paint(_followToggle);
            _rowsDirty = true;
        }

        private static void ToggleFollow() => SetFollow(!_follow);

        private static void JumpToLatest() => SetFollow(true);

        private static void ScrollLog(float dt)
        {
            var max = MaxScroll;
            if (_follow) _scrollGoal = max;
            _scrollGoal = Mathf.Clamp(_scrollGoal, 0f, max);
            // A filter that leaves fewer lines brings the view to them at once rather than easing over empty space.
            if (_scroll > max) _scroll = max;
            var scroll = dt <= 0f || Mathf.Abs(_scroll - _scrollGoal) < 0.5f
                ? _scrollGoal
                : Mathf.Lerp(_scroll, _scrollGoal, 1f - Mathf.Exp(-18f * dt));
            if (!Mathf.Approximately(scroll, _scroll))
            {
                _scroll = scroll;
                _paintDirty = true;
            }
            if (_rowsDirty || _paintDirty) RenderRows();
        }

        private static void RenderRows()
        {
            if (_logViewport == null) return;
            var first = Mathf.Max(0, Mathf.FloorToInt(_scroll / RowHeight));
            var offset = _scroll - first * RowHeight;
            for (var i = 0; i < Rows.Count; i++)
            {
                var row = Rows[i];
                var index = first + i;
                var y = i * RowHeight - offset;
                var active = index < Filtered.Count && y < _viewportHeight;
                if (row.Rect.gameObject.activeSelf != active) row.Rect.gameObject.SetActive(active);
                if (!active)
                {
                    row.Index = -1;
                    continue;
                }
                row.Rect.anchoredPosition = new Vector2(0f, -y);
                var entry = Filtered[index];
                if (_rowsDirty || row.Index != index || row.EntryId != entry.Id) Fill(row, entry);
                row.Index = index;
                var selected = _selectedEntry != null && _selectedEntry.Id == entry.Id;
                row.Back.color = selected ? MenuKit.Faded(0.12f) : index == _hoverIndex ? MenuKit.Faded(0.07f)
                    : index % 2 == 0 ? MenuKit.Faded(0.022f) : Clear;
                row.Edge.enabled = selected;
            }
            var latest = !_follow && MaxScroll - _scrollGoal > RowHeight;
            if (_latest.Rect.gameObject.activeSelf != latest)
            {
                _latest.Rect.gameObject.SetActive(latest);
                _latest.Rect.sizeDelta = new Vector2(Preferred(_latest.Label) + 44f, 28f);
            }
            if (_rowsDirty)
            {
                _empty.text = Filtered.Count > 0 ? "" : Text[Entries.Count == 0 ? "console.emptyLog" : "console.empty"];
                var counter = Text.Count("console.lines", Filtered.Count);
                if (_paused) counter += "  ·  " + Text["console.paused"];
                if (_dropped > 0) counter += "  ·  " + Text.Count("console.dropped", _dropped);
                _counter.text = counter;
            }
            _rowsDirty = _paintDirty = false;
        }

        private static void Fill(LogRow row, ConsoleEntry entry)
        {
            row.EntryId = entry.Id;
            row.Time.text = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            var (badge, color) = Badge(entry.Level);
            row.Badge.text = badge;
            row.Badge.color = color;
            row.BadgeBack.color = new Color(color.r, color.g, color.b, 0.13f);
            row.Source.text = entry.Source;
            var message = entry.Message;
            if (message.Length > 300) message = message.Substring(0, 300) + "…";
            row.Message.text = message.Replace('\r', ' ').Replace('\n', ' ');
            row.Message.color = Group(entry.Level) switch
            {
                1 => MenuKit.Red,
                2 => MenuKit.Amber,
                4 => MenuKit.Muted,
                _ => MenuKit.Ink
            };
        }

        // Enough rows to cover the visible height and one more for the part-row a smooth scroll shows.
        private static void EnsureRows(int count)
        {
            while (Rows.Count < count)
            {
                var row = new LogRow { Rect = Box("Row", _logViewport) };
                row.Rect.anchorMin = new Vector2(0f, 1f);
                row.Rect.anchorMax = Vector2.one;
                row.Rect.pivot = new Vector2(0.5f, 1f);
                row.Rect.sizeDelta = new Vector2(0f, RowHeight);
                row.Back = row.Rect.gameObject.AddComponent<Image>();
                row.Back.color = Clear;
                var button = row.Rect.gameObject.AddComponent<Button>();
                button.transition = Selectable.Transition.None;
                button.targetGraphic = row.Back;
                NoNavigation(button);
                button.onClick.AddListener((UnityAction)new Action(() => SelectEntry(row.EntryId)));
                row.Edge = Image("Edge", row.Rect, MenuKit.Ink);
                row.Edge.raycastTarget = false;
                Region(row.Edge.rectTransform, Vector2.zero, new Vector2(0f, 1f), Vector2.zero, new Vector2(2f, 0f));
                row.Edge.enabled = false;
                row.Time = Label(row.Rect, "Time", 13f, false, MenuKit.Muted);
                row.BadgeBack = Image("Level", row.Rect, Clear);
                row.BadgeBack.raycastTarget = false;
                row.Badge = Label(row.BadgeBack.rectTransform, "Text", 10f, true, MenuKit.Soft, TextAlignmentOptions.Center);
                row.Badge.characterSpacing = 6f;
                Fill(row.Badge.rectTransform);
                row.Source = Label(row.Rect, "Source", 13f, false, MenuKit.Soft);
                row.Message = Label(row.Rect, "Message", 14f, false, MenuKit.Ink);
                PlaceColumns(row, _sourceLeft, _sourceWidth, _messageLeft);
                row.Rect.gameObject.SetActive(false);
                Rows.Add(row);
            }
        }

        private static void SelectEntry(long id)
        {
            var entry = Entries.Find(candidate => candidate.Id == id);
            if (entry == null) return;
            _selectedEntry = entry;
            ShowTab(0);
            ShowEntry();
            _paintDirty = true;
        }

        private static void ShowEntry()
        {
            var entry = _selectedEntry;
            _copy.Rect.gameObject.SetActive(entry != null);
            _detailBadge.gameObject.SetActive(entry != null);
            if (entry == null)
            {
                _detailSource.text = _detailMeta.text = "";
                _detail.text = "";
                return;
            }
            var (badge, color) = Badge(entry.Level);
            _detailBadgeText.text = badge;
            _detailBadgeText.color = color;
            _detailBadge.GetComponent<Image>().color = new Color(color.r, color.g, color.b, 0.13f);
            _detailSource.text = entry.Source;
            _detailMeta.text = entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd  HH:mm:ss.fff  zzz", CultureInfo.InvariantCulture)
                + "  ·  #" + entry.Id.ToString(CultureInfo.InvariantCulture);
            _detail.text = entry.Message;
            _detail.caretPosition = 0;
        }

        private static void CopyEntry()
        {
            var entry = _selectedEntry;
            if (entry == null) return;
            GUIUtility.systemCopyBuffer = entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + " " + entry.Level + " [" + entry.Source + "] " + entry.Message;
            _notice.text = Text["console.copied"];
        }

        private static void TogglePause()
        {
            _paused = !_paused;
            _notice.text = Text[_paused ? "console.pausedNotice" : "console.resumed"];
            PaintToggles();
            if (!_paused) RefreshSnapshot();
            _rowsDirty = true;
        }

        private static void ClearView()
        {
            var snapshot = ConsoleHub.Read(long.MaxValue, 1);
            _cursor = snapshot.LastId;
            Entries.Clear();
            _selectedEntry = null;
            ShowEntry();
            _scroll = _scrollGoal = 0f;
            _notice.text = Text["console.cleared"];
            FilterEntries();
        }
    }
}
