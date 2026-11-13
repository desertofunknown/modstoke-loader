// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Text;
using Modstoke.Diagnostics;
using TMPro;
using UnityEngine;

namespace Modstoke.Loader
{
    internal static partial class ConsoleOverlay
    {
        private const int SuggestionLimit = 6, HistoryLimit = 64;
        private const float CommandHeight = 68f, SuggestionHeight = 40f;
        // Stands for "no list worked out yet", which no typed text can equal.
        private const string Unknown = "\0";
        private static readonly char[] Blank = { ' ', '\t' };
        private static readonly List<string> History = new();
        private static readonly List<CommandView> CommandViews = new();
        private static readonly List<CommandView> SuggestionViews = new();
        private static readonly List<ConsoleCommandInfo> Matches = new();
        private static readonly List<ConsoleCommandInfo> Listed = new();
        private static ConsoleCommandInfo[] _commands = Array.Empty<ConsoleCommandInfo>();
        private static ConsoleCommandInfo _pendingCommand;
        private static RectTransform _catalogContent, _suggestions;
        private static string _catalogSignature, _suggestFor = Unknown, _pendingArguments = "", _historyDraft = "";
        private static int _historyIndex, _submitFrame = -1, _suggestIndex, _tab, _pinCaret, _refocus;

        /// <summary>A command in the list or the suggestions under the command line.</summary>
        private sealed class CommandView
        {
            internal Control Control;
            internal TextMeshProUGUI About, Badge;
            internal ConsoleCommandInfo Command;
            internal int Index;
        }

        private static void BuildSuggestions()
        {
            _suggestions = Image("Suggestions", _panel, Raised).rectTransform;
            _suggestions.anchorMin = _suggestions.anchorMax = _suggestions.pivot = Vector2.zero;
            _suggestions.anchoredPosition = new Vector2(Pad + 30f, 54f);
            Rule(_suggestions, new Vector2(0f, 1f), Vector2.one, new Vector2(0f, -1f), Vector2.zero);
            Rule(_suggestions, Vector2.zero, new Vector2(0f, 1f), Vector2.zero, new Vector2(1f, 0f));
            Rule(_suggestions, new Vector2(1f, 0f), Vector2.one, new Vector2(-1f, 0f), Vector2.zero);
            _suggestions.gameObject.SetActive(false);
        }

        private static void ShowTab(int index)
        {
            _tab = index;
            _messagePane.gameObject.SetActive(index == 0);
            _commandsPane.gameObject.SetActive(index == 1);
            _messageTab.Paint(_messageTab);
            _commandsTab.Paint(_commandsTab);
            if (index == 1) RenderCommands();
        }

        private static void RefreshCatalog(ConsoleCommandInfo[] commands)
        {
            _commands = commands;
            var signature = new StringBuilder();
            foreach (var command in commands)
                signature.Append(command.Name).Append('\0').Append(command.Description).Append('\0')
                    .Append(command.Enabled).Append(command.RequiresConfirmation).Append(command.AcceptsArguments).Append('\n');
            var current = signature.ToString();
            if (current == _catalogSignature) return;
            _catalogSignature = current;
            var selected = _eventSystem?.currentSelectedGameObject;
            if (selected != null && selected.transform.IsChildOf(_catalogContent)) FocusCommand();
            _suggestFor = Unknown;
            RenderCommands();
        }

        // The commands whose names hold what was typed, those starting with it first; everything when nothing was typed.
        private static void Collect(string typed, List<ConsoleCommandInfo> into, int limit = int.MaxValue)
        {
            into.Clear();
            foreach (var command in _commands)
                if (into.Count < limit && (typed.Length == 0 || command.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)))
                    into.Add(command);
            if (typed.Length == 0) return;
            foreach (var command in _commands)
                if (into.Count < limit && !command.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)
                    && command.Name.IndexOf(typed, StringComparison.OrdinalIgnoreCase) >= 0)
                    into.Add(command);
        }

        // The command's name as typed so far, and whether arguments have started after it.
        private static string Typed(out bool arguments)
        {
            var text = (_command.text ?? "").TrimStart();
            var split = text.IndexOfAny(Blank);
            arguments = split >= 0;
            return split < 0 ? text : text.Substring(0, split);
        }

        private static void CommandTyped()
        {
            if (_tab == 1) RenderCommands();
        }

        private static void RenderCommands()
        {
            if (_catalogContent == null || _tab != 1) return;
            Collect(Typed(out _), Listed);
            while (CommandViews.Count < Listed.Count) CommandViews.Add(CommandItem());
            for (var i = 0; i < CommandViews.Count; i++)
            {
                var view = CommandViews[i];
                var shown = i < Listed.Count;
                view.Control.Rect.gameObject.SetActive(shown);
                if (!shown) continue;
                Describe(view, Listed[i]);
                TopStretch(view.Control.Rect, 0f, i * CommandHeight, 0f, CommandHeight - 6f);
                view.Control.Paint(view.Control);
            }
            _catalogContent.sizeDelta = new Vector2(0f, Listed.Count * CommandHeight);
            _commandCount.text = Listed.Count == 0 ? Text["console.noCommands"] : Text.Count("console.commands", Listed.Count);
        }

        private static CommandView CommandItem()
        {
            var view = new CommandView();
            view.Control = Clickable(_catalogContent, "Command", () => Prepare(view.Command), _ => PaintCommand(view));
            var rect = view.Control.Rect;
            view.Control.Label = Label(rect, "Name", 15f, true, MenuKit.Ink);
            TopStretch(view.Control.Label.rectTransform, 12f, 8f, 120f, 22f);
            view.Badge = Label(rect, "Badge", 10f, true, MenuKit.Amber, TextAlignmentOptions.MidlineRight);
            view.Badge.characterSpacing = 10f;
            RightTop(view.Badge.rectTransform, 12f, 10f, 110f, 18f);
            view.About = Label(rect, "About", 13f, false, MenuKit.Muted, TextAlignmentOptions.TopLeft);
            view.About.enableWordWrapping = true;
            TopStretch(view.About.rectTransform, 12f, 31f, 12f, 30f);
            return view;
        }

        private static void Describe(CommandView view, ConsoleCommandInfo command)
        {
            view.Command = command;
            view.Control.Button.interactable = command.Enabled;
            view.Control.Label.text = command.Name;
            view.Control.Label.color = command.Enabled ? MenuKit.Ink : MenuKit.Faint;
            view.About.text = command.Description;
            view.Badge.text = !command.Enabled ? Upper(Text["console.unavailableBadge"])
                : command.RequiresConfirmation ? Upper(Text["console.confirmBadge"]) : "";
            view.Badge.color = command.Enabled ? MenuKit.Amber : MenuKit.Faint;
        }

        private static void PaintCommand(CommandView view)
        {
            var control = view.Control;
            var suggested = SuggestionViews.Contains(view) && view.Index == _suggestIndex;
            control.Back.color = suggested ? MenuKit.Faded(0.12f) : control.Hover && view.Command is { Enabled: true } ? MenuKit.Faded(0.08f)
                : SuggestionViews.Contains(view) ? Clear : MenuKit.Faded(0.03f);
        }

        // The list under the command line: commands that match the name being typed, until arguments start.
        private static void UpdateSuggestions()
        {
            if (_suggestions == null) return;
            var typed = Typed(out var arguments);
            // A line brought back from history is left as it is until it is edited.
            var recalled = _historyIndex < History.Count && _command.text == History[_historyIndex];
            var key = _command.isFocused && _pendingCommand == null && !arguments && !recalled ? typed : null;
            if (key == _suggestFor) return;
            _suggestFor = key;
            Matches.Clear();
            if (!string.IsNullOrEmpty(key)) Collect(key, Matches, SuggestionLimit);
            // A name typed out in full needs no list.
            if (Matches.Count == 1 && string.Equals(Matches[0].Name, key, StringComparison.Ordinal)) Matches.Clear();
            _suggestIndex = 0;
            RenderSuggestions();
        }

        private static void RenderSuggestions()
        {
            var open = Matches.Count > 0;
            if (_suggestions.gameObject.activeSelf != open) _suggestions.gameObject.SetActive(open);
            if (!open) return;
            _suggestions.sizeDelta = new Vector2(_suggestionWidth, Matches.Count * SuggestionHeight + 8f);
            var nameWidth = Mathf.Clamp(_suggestionWidth * 0.36f, 140f, 280f);
            while (SuggestionViews.Count < Matches.Count) SuggestionViews.Add(SuggestionItem());
            for (var i = 0; i < SuggestionViews.Count; i++)
            {
                var view = SuggestionViews[i];
                var shown = i < Matches.Count;
                view.Control.Rect.gameObject.SetActive(shown);
                if (!shown) continue;
                view.Index = i;
                Describe(view, Matches[i]);
                TopStretch(view.Control.Rect, 4f, 4f + i * SuggestionHeight, 4f, SuggestionHeight);
                Region(view.Control.Label.rectTransform, Vector2.zero, new Vector2(0f, 1f), new Vector2(14f, 0f), new Vector2(14f + nameWidth, 0f));
                Region(view.About.rectTransform, Vector2.zero, Vector2.one, new Vector2(28f + nameWidth, 0f), new Vector2(-110f, 0f));
                view.Control.Paint(view.Control);
            }
        }

        private static CommandView SuggestionItem()
        {
            var view = new CommandView();
            // Taken on the press itself in HandleSuggestionClick, before the command line loses focus to it.
            view.Control = Clickable(_suggestions, "Suggestion", () => { }, _ => PaintCommand(view));
            var rect = view.Control.Rect;
            view.Control.Label = Label(rect, "Name", 15f, true, MenuKit.Ink);
            view.About = Label(rect, "About", 13f, false, MenuKit.Muted);
            view.Badge = Label(rect, "Badge", 10f, true, MenuKit.Amber, TextAlignmentOptions.MidlineRight);
            view.Badge.characterSpacing = 10f;
            Region(view.Badge.rectTransform, new Vector2(1f, 0f), Vector2.one, new Vector2(-100f, 0f), new Vector2(-12f, 0f));
            return view;
        }

        private static void PaintSuggestions()
        {
            foreach (var view in SuggestionViews) view.Control.Paint(view.Control);
        }

        private static void HandleSuggestionClick()
        {
            if (!Input.GetMouseButtonDown(0) || !_suggestions.gameObject.activeSelf) return;
            foreach (var view in SuggestionViews)
            {
                if (!view.Control.Hover || !view.Control.Rect.gameObject.activeInHierarchy) continue;
                Complete(view.Command);
                return;
            }
        }

        // Tab completes a suggestion; Up and Down move through the suggestions while they show, otherwise through history.
        private static void HandleCommandKeys()
        {
            if (Input.GetKeyDown(KeyCode.PageUp)) ScrollBy(-_viewportHeight * 0.9f);
            else if (Input.GetKeyDown(KeyCode.PageDown)) ScrollBy(_viewportHeight * 0.9f);
            HandleSuggestionClick();
            // A press on the panel can take selection after the command line was given it back, so it is given back again.
            if (_refocus > 0 && _pendingCommand == null)
            {
                _refocus--;
                if (_eventSystem != null && _eventSystem.currentSelectedGameObject != _command.gameObject) FocusCommand();
            }
            if (_pinCaret > 0 && _command.isFocused)
            {
                // The field may move its caret on the same key after this runs, so the end is kept for a frame more.
                _pinCaret--;
                _command.MoveTextEnd(false);
            }
            if (_pendingCommand != null || !_command.isFocused) return;
            var tab = Input.GetKeyDown(KeyCode.Tab);
            var text = _command.text;
            if (text.IndexOf('\t') >= 0)
            {
                _command.text = text.Replace("\t", "");
                tab = true;
            }
            if (tab)
            {
                if (Matches.Count > 0) Complete(Matches[Mathf.Clamp(_suggestIndex, 0, Matches.Count - 1)]);
                return;
            }
            var direction = Input.GetKeyDown(KeyCode.UpArrow) ? -1 : Input.GetKeyDown(KeyCode.DownArrow) ? 1 : 0;
            if (direction == 0) return;
            if (Matches.Count > 0)
            {
                _suggestIndex = (_suggestIndex + direction + Matches.Count) % Matches.Count;
                PaintSuggestions();
            }
            else if (History.Count > 0)
            {
                if (_historyIndex == History.Count) _historyDraft = _command.text;
                _historyIndex = Mathf.Clamp(_historyIndex + direction, 0, History.Count);
                _command.text = _historyIndex == History.Count ? _historyDraft : History[_historyIndex];
            }
            _pinCaret = 2;
        }

        private static void Complete(ConsoleCommandInfo command)
        {
            if (command == null) return;
            _command.text = command.Name + (command.AcceptsArguments ? " " : "");
            Explain(command);
            FocusCommand();
            _refocus = 2;
        }

        // Clicking a command in the list puts it on the command line, ready for its arguments.
        private static void Prepare(ConsoleCommandInfo command)
        {
            if (command == null || !command.Enabled) return;
            Complete(command);
        }

        private static void Explain(ConsoleCommandInfo command)
            => _notice.text = command.Description + (command.RequiresConfirmation ? " " + Text["console.confirmFirst"] : "");

        private static void FocusCommand()
        {
            if (_command == null) return;
            _command.Select();
            _command.ActivateInputField();
            _pinCaret = 2;
        }

        private static ConsoleCommandInfo FindCommand(string value)
            => Array.Find(_commands, command => string.Equals(command.Name, value, StringComparison.Ordinal));

        private static void PrepareCommand()
        {
            if (!_visible || _pendingCommand != null || _submitFrame == Time.frameCount) return;
            _submitFrame = Time.frameCount;
            var value = _command.text.Trim();
            if (value.Length == 0) return;
            var split = value.IndexOfAny(Blank);
            var name = split < 0 ? value : value[..split];
            var arguments = split < 0 ? "" : value[(split + 1)..].Trim();
            var command = FindCommand(name);
            if (command == null || !command.Enabled)
            {
                _notice.text = Text[command == null ? "console.unknown" : "console.unavailable"];
                FocusCommand();
                return;
            }
            if (arguments.Length != 0 && !command.AcceptsArguments)
            {
                _notice.text = Text["console.noArguments"];
                FocusCommand();
                return;
            }
            if (command.RequiresConfirmation)
            {
                _pendingCommand = command;
                _pendingArguments = arguments;
                _confirmationLine.text = command.Name + (arguments.Length == 0 ? "" : " " + arguments);
                _confirmationAbout.text = command.Description;
                _command.DeactivateInputField();
                _suggestions.gameObject.SetActive(false);
                _suggestFor = Unknown;
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
                _notice.text = Text["console.changed"];
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
            if (_visible) FocusCommand();
        }

        private static void SubmitCommand(string command, string arguments, bool confirmed)
        {
            var accepted = ConsoleHub.TrySubmit(command, arguments, confirmed, ConsoleOrigin.InGameConsole, out var detail);
            _notice.text = detail;
            if (!accepted)
            {
                FocusCommand();
                return;
            }
            var line = arguments.Length == 0 ? command : command + " " + arguments;
            if (History.Count == 0 || History[History.Count - 1] != line) History.Add(line);
            if (History.Count > HistoryLimit) History.RemoveAt(0);
            _historyIndex = History.Count;
            _historyDraft = "";
            _command.text = "";
            FocusCommand();
        }
    }
}
