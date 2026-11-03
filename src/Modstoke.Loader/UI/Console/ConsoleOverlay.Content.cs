// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Modstoke.Diagnostics;
using UnityEngine;

namespace Modstoke.Loader
{
    internal static partial class ConsoleOverlay
    {
        private const int RetainedEntries = 4096;
        private static readonly List<ConsoleEntry> Entries = new(RetainedEntries);
        private static readonly List<ConsoleEntry> Filtered = new(RetainedEntries);
        private static readonly List<string> History = new();
        private static readonly string[] Levels = { "All", "Debug", "Info", "Warning", "Error", "Fatal" };
        private static ConsoleCommandInfo[] _commands = Array.Empty<ConsoleCommandInfo>();
        private static string _sessionId;
        private static string _catalogSignature;
        private static ConsoleCommandInfo _pendingCommand;
        private static string _pendingArguments = "";
        private static string _historyDraft = "";
        private static ConsoleEntry _selectedEntry;
        private static long _cursor;
        private static long _dropped;
        private static long _firstId;
        private static int _level;
        private static int _offset;
        private static int _historyIndex;
        private static int _submitFrame = -1;
        private static bool _paused;
        private static bool _follow = true;

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
                _offset = 0;
                _selectedEntry = null;
                _detail.text = "";
                CancelConfirmation();
                if (!_paused) snapshot = ConsoleHub.Read(0, 256);
            }
            var dropped = _dropped != snapshot.Dropped;
            _dropped = snapshot.Dropped;
            _firstId = snapshot.FirstId;
            _status.text = (snapshot.Ready ? "READY" : "STARTING") + "  /  " + snapshot.Phase
                + "  /  " + snapshot.Mods.Length.ToString(CultureInfo.InvariantCulture) + " mods  /  " + snapshot.Summary;
            RefreshCatalog(snapshot.Commands);
            if (_paused || snapshot.Entries.Length == 0)
            {
                // Filters, paging and selection render on their own; the rows only change here on a reset or eviction.
                if (reset || dropped) RenderRows();
                return;
            }
            if (_cursor > 0 && _firstId > _cursor + 1)
                _notice.text = "Log history advanced while this view was idle. Shared history now starts at #" + _firstId + ".";
            var anchor = !_follow && _offset < Filtered.Count ? Filtered[_offset].Id : 0;
            foreach (var entry in snapshot.Entries)
            {
                if (entry.Id <= _cursor) continue;
                Entries.Add(entry);
                _cursor = entry.Id;
            }
            // LastId can be beyond this batch; only consumed entries advance the cursor.
            if (Entries.Count > RetainedEntries) Entries.RemoveRange(0, Entries.Count - RetainedEntries);
            FilterEntries();
            if (anchor != 0)
            {
                var index = Filtered.FindIndex(entry => entry.Id >= anchor);
                _offset = Math.Max(0, index);
            }
            RenderRows();
        }

        private static void CycleLevel()
        {
            _level = (_level + 1) % Levels.Length;
            _levelLabel.text = "Level: " + Levels[_level];
            ApplyFilters();
        }

        private static void ApplyFilters()
        {
            _offset = 0;
            FilterEntries();
            RenderRows();
        }

        private static void FilterEntries()
        {
            Filtered.Clear();
            var source = _source.text.Trim();
            var search = _search.text.Trim();
            foreach (var entry in Entries)
            {
                if (_level != 0 && !MatchesLevel(entry.Level, Levels[_level])) continue;
                if (source.Length != 0 && entry.Source.IndexOf(source, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (search.Length != 0 && entry.Message.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                Filtered.Add(entry);
            }
        }

        private static bool MatchesLevel(string actual, string filter)
        {
            if (string.Equals(actual, filter, StringComparison.OrdinalIgnoreCase)) return true;
            return (filter == "Warning" && actual.Equals("Warn", StringComparison.OrdinalIgnoreCase))
                || (filter == "Info" && actual.Equals("Message", StringComparison.OrdinalIgnoreCase))
                || (filter == "Debug" && actual.Equals("Trace", StringComparison.OrdinalIgnoreCase));
        }

        private static void RenderRows()
        {
            if (_root == null) return;
            var lastOffset = Math.Max(0, Filtered.Count - _pageSize);
            _offset = _follow ? lastOffset : Math.Min(_offset, lastOffset);
            for (var i = 0; i < Rows.Count; i++)
            {
                var row = Rows[i];
                var index = _offset + i;
                var active = i < _pageSize && index < Filtered.Count;
                row.Image.gameObject.SetActive(active);
                if (!active) continue;
                var entry = Filtered[index];
                row.EntryId = entry.Id;
                row.Image.color = _selectedEntry?.Id == entry.Id ? new Color(0.11f, 0.26f, 0.32f, 1f)
                    : i % 2 == 0 ? Surface : Background;
                row.Text.color = MatchesLevel(entry.Level, "Error") || MatchesLevel(entry.Level, "Fatal")
                    ? new Color(1f, 0.55f, 0.53f, 1f)
                    : MatchesLevel(entry.Level, "Warning") ? new Color(1f, 0.78f, 0.40f, 1f) : Foreground;
                var message = entry.Message;
                if (message.Length > 300) message = message.Substring(0, 300) + "…";
                row.Text.text = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                    + "  " + entry.Level + "  [" + entry.Source + "]  " + message.Replace('\r', ' ').Replace('\n', ' ');
            }
            var start = Filtered.Count == 0 ? 0 : _offset + 1;
            var end = Math.Min(Filtered.Count, _offset + _pageSize);
            _counter.text = start + "–" + end + " of " + Filtered.Count
                + (_paused ? "  / paused" : "") + (_dropped > 0 ? "  / evicted: " + _dropped : "");
        }

        private static void SelectEntry(long id)
        {
            _selectedEntry = Entries.Find(entry => entry.Id == id);
            if (_selectedEntry == null) return;
            var entry = _selectedEntry;
            _detail.text = entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)
                + "\n" + entry.Level + "  /  " + entry.Source + "  /  #" + entry.Id + "\n\n" + entry.Message;
            _detail.caretPosition = 0;
            _notice.text = "Selected message can be scrolled, selected and copied.";
            RenderRows();
        }

        private static void TogglePause()
        {
            _paused = !_paused;
            _pauseLabel.text = _paused ? "Resume" : "Pause";
            _notice.text = _paused ? "Log display paused. Collection continues; older entries may be evicted."
                : "Log display resumed.";
            if (!_paused) RefreshSnapshot();
            RenderRows();
        }

        private static void ToggleFollow()
        {
            _follow = !_follow;
            _followLabel.text = _follow ? "Follow: On" : "Follow: Off";
            RenderRows();
        }

        private static void ChangePage(int direction)
        {
            _follow = false;
            _followLabel.text = "Follow: Off";
            _offset = Math.Max(0, _offset + direction * _pageSize);
            RenderRows();
        }

        private static void ClearView()
        {
            var snapshot = ConsoleHub.Read(long.MaxValue, 1);
            _cursor = snapshot.LastId;
            Entries.Clear();
            Filtered.Clear();
            _selectedEntry = null;
            _detail.text = "";
            _offset = 0;
            _notice.text = "View cleared. Shared log history is unchanged.";
            RenderRows();
        }

        private static void RefreshCatalog(ConsoleCommandInfo[] commands)
        {
            _commands = commands;
            var signature = new StringBuilder();
            foreach (var command in commands)
                signature.Append(command.Name).Append('\0').Append(command.Description).Append('\0')
                    .Append(command.Enabled).Append(command.RequiresConfirmation).Append('\n');
            var current = signature.ToString();
            if (current == _catalogSignature) return;
            _catalogSignature = current;
            var selected = _eventSystem?.currentSelectedGameObject;
            if (selected != null && selected.transform.IsChildOf(_catalogContent)) _command.Select();
            for (var i = _catalogContent.childCount - 1; i >= 0; i--)
            {
                var child = _catalogContent.GetChild(i).gameObject;
                child.SetActive(false);
                UnityEngine.Object.Destroy(child);
            }
            _catalogContent.sizeDelta = new Vector2(0f, commands.Length * 72f);
            for (var i = 0; i < commands.Length; i++)
            {
                var command = commands[i];
                var button = Button(_catalogContent, command.Name, () =>
                {
                    _command.text = command.Name;
                    _command.Select();
                    _command.ActivateInputField();
                    _notice.text = command.Description + (command.RequiresConfirmation ? " Confirmation required." : "");
                }, out var label);
                button.interactable = command.Enabled;
                TopStretch(button.GetComponent<RectTransform>(), 2f, i * 72f, 2f, 68f);
                label.text = command.Name + (command.Enabled ? "" : "  / unavailable")
                    + (command.RequiresConfirmation ? "  / confirm" : "") + "\n" + command.Description;
                label.enableWordWrapping = true;
                label.fontSize = 13f;
                label.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
                Fill(label.rectTransform, 8f, 4f, 8f, 4f);
            }
        }

        private static ConsoleCommandInfo FindCommand(string value)
            => Array.Find(_commands, command => string.Equals(command.Name, value, StringComparison.Ordinal));

        private static void PrepareCommand()
        {
            if (!_visible || _pendingCommand != null || _submitFrame == Time.frameCount) return;
            _submitFrame = Time.frameCount;
            var value = _command.text.Trim();
            if (value.Length == 0) return;
            var split = value.IndexOfAny(new[] { ' ', '\t' });
            var name = split < 0 ? value : value[..split];
            var arguments = split < 0 ? "" : value[(split + 1)..].Trim();
            var command = FindCommand(name);
            if (command == null || !command.Enabled)
            {
                _notice.text = command == null ? "Unknown command. Choose a command from the catalog." : "This command is unavailable.";
                return;
            }
            if (arguments.Length != 0 && !command.AcceptsArguments)
            {
                _notice.text = "This command does not take arguments.";
                return;
            }
            if (command.RequiresConfirmation)
            {
                _pendingCommand = command;
                _pendingArguments = arguments;
                _confirmationText.text = command.Name + (arguments.Length == 0 ? "" : " " + arguments)
                    + "\n\n" + command.Description + "\n\nRun this command now?";
                _command.DeactivateInputField();
                _panelInteraction.interactable = false;
                _confirmation.SetActive(true);
                _cancelButton.Select();
                return;
            }
            SubmitCommand(command.Name, arguments, false);
        }

        private static void ConfirmCommand()
        {
            var command = _pendingCommand;
            var arguments = _pendingArguments;
            if (command == null) return;
            CancelConfirmation();
            if (!command.Equals(FindCommand(command.Name)))
            {
                _notice.text = "The command changed. Review the catalog and submit again.";
                return;
            }
            SubmitCommand(command.Name, arguments, true);
        }

        private static void CancelConfirmation()
        {
            _pendingCommand = null;
            _pendingArguments = "";
            if (_confirmation != null) _confirmation.SetActive(false);
            if (_panelInteraction != null) _panelInteraction.interactable = true;
            if (_visible && _command != null)
            {
                _command.Select();
                _command.ActivateInputField();
            }
        }

        private static void SubmitCommand(string command, string arguments, bool confirmed)
        {
            var accepted = ConsoleHub.TrySubmit(command, arguments, confirmed, ConsoleOrigin.InGameConsole, out var detail);
            _notice.text = detail;
            if (!accepted) return;
            var line = arguments.Length == 0 ? command : command + " " + arguments;
            if (History.Count == 0 || History[History.Count - 1] != line) History.Add(line);
            if (History.Count > 64) History.RemoveAt(0);
            _historyIndex = History.Count;
            _historyDraft = "";
            _command.text = "";
            _command.Select();
            _command.ActivateInputField();
        }

        private static void UpdateHistory()
        {
            if (_pendingCommand != null || !_command.isFocused || History.Count == 0) return;
            var direction = Input.GetKeyDown(KeyCode.UpArrow) ? -1 : Input.GetKeyDown(KeyCode.DownArrow) ? 1 : 0;
            if (direction == 0) return;
            if (_historyIndex == History.Count) _historyDraft = _command.text;
            _historyIndex = Mathf.Clamp(_historyIndex + direction, 0, History.Count);
            _command.text = _historyIndex == History.Count ? _historyDraft : History[_historyIndex];
            _command.caretPosition = _command.text.Length;
        }
    }
}
