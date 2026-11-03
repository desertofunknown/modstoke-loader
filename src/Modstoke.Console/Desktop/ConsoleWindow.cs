// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Modstoke.Diagnostics.Desktop;

internal sealed partial class ConsoleWindow : IDisposable
{
    private const string ClassName = "Modstoke.Console.Desktop";
    private const int SearchId = 101, LevelId = 102, SourceId = 103, LogsId = 104,
        PauseId = 105, FollowId = 106, ClearId = 107, CopyId = 108, ExportId = 109,
        DetailsId = 110, ModsId = 111, CatalogId = 112, InputId = 113, SubmitId = 114;
    private readonly string? _pipe;
    private readonly string? _logPath;
    private readonly Native.WindowProcedure _procedure;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<ConsoleEntry> _history = new(4096);
    private readonly List<ConsoleEntry> _visible = new(4096);
    private readonly List<string> _sources = new();
    private readonly List<string> _commandHistory = new();
    private readonly List<nint> _controls = new();
    private readonly nint _instance = Native.GetModuleHandle(null);
    private nint _window, _search, _level, _source, _logs, _pause, _followButton, _clear,
        _copy, _exportButton, _details, _mods, _catalog, _input, _submit, _empty;
    private Task<ConsoleSnapshot>? _request;
    private Task<ConsoleEntry[]>? _fileRead;
    private Task? _export;
    private ConsoleCommandInfo? _pendingCommand;
    private string _pendingSession = "", _pendingArguments = "";
    private ConsoleCommandInfo[] _commands = Array.Empty<ConsoleCommandInfo>();
    private string[] _modLines = Array.Empty<string>();
    private string _session = "", _phase = "Waiting for loader", _summary = "Connecting to the shared console…";
    private string _feedback = "", _commandDraft = "";
    private long _after, _nextRead, _dropped, _trimmed, _missed;
    private int _failures, _historyIndex, _dpi = 96, _exitCode;
    private bool _connected, _ready, _paused, _follow = true, _submission, _disposed, _registered,
        _updating, _faulted, _confirming, _closeAfterExport, _outcomeUnknown;

    internal ConsoleWindow(string? pipe, string? logPath)
    {
        _pipe = pipe;
        _logPath = logPath;
        _procedure = WindowProcedure;
        if (logPath != null)
        {
            _phase = "Opening log file";
            _summary = Path.GetFileName(logPath);
        }
    }

    internal int Run()
    {
        Marshal.ThrowExceptionForHR(Native.SetCurrentProcessExplicitAppUserModelID("OpenACAI.Modstoke.Console"));
        Native.SetProcessDpiAwarenessContext(-4);
        _dpi = Math.Max(96, (int)Native.GetDpiForSystem());
        CreateBrushes();
        var windowClass = new Native.WindowClass
        {
            Size = (uint)Marshal.SizeOf<Native.WindowClass>(), Procedure = _procedure,
            Instance = _instance, ClassName = ClassName, Background = _backgroundBrush,
            Cursor = Native.LoadCursor(0, 32512)
        };
        if (Native.RegisterClassEx(ref windowClass) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the console window.");
        _registered = true;
        var width = Scale(1280);
        var height = Scale(850);
        if (Native.SystemParametersInfo(0x30, 0, out var workArea, 0))
        {
            width = Math.Min(width, workArea.Right - workArea.Left);
            height = Math.Min(height, workArea.Bottom - workArea.Top);
        }
        _window = Native.CreateWindowEx(0, ClassName, "Modstoke Console", 0x02cf0000,
            unchecked((int)0x80000000), unchecked((int)0x80000000), width, height, 0, 0, _instance, 0);
        if (_window == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the console window.");
        if (_faulted)
            return 1;
        _dpi = (int)Native.GetDpiForWindow(_window);
        if (_dpi <= 0)
            _dpi = 96;
        CreateFonts();
        CreateControls();
        var dark = 1;
        Native.DwmSetWindowAttribute(_window, 20, ref dark, sizeof(int));
        Layout();
        UpdateActions();
        if (_logPath != null)
            _fileRead = Task.Run(() => LogFile.Read(_logPath, _lifetime.Token));
        if (Native.SetTimer(_window, 1, 100, 0) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start the console update timer.");
        // Automatic loader startup must not take keyboard focus from the game.
        Native.ShowWindow(_window, 4);
        Native.UpdateWindow(_window);
        int result;
        while ((result = Native.GetMessage(out var message, 0, 0, 0)) > 0)
        {
            if (HandleKey(message) || Native.IsDialogMessage(_window, ref message))
                continue;
            Native.TranslateMessage(ref message);
            Native.DispatchMessage(ref message);
        }
        if (result < 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The console message loop failed.");
        return _exitCode;
    }

    private nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            switch (message)
            {
                case 0x10:
                    if (_export is { IsCompleted: false })
                    {
                        _closeAfterExport = true;
                        _feedback = "Finishing the export before closing…";
                        Invalidate();
                        return 0;
                    }
                    Native.DestroyWindow(window);
                    return 0;
                case 2:
                    Native.KillTimer(window, 1);
                    _lifetime.Cancel();
                    _window = 0;
                    Native.PostQuitMessage(_exitCode);
                    return 0;
                case 5:
                    if (_search != 0)
                        Layout();
                    return 0;
                case 0x24:
                    var size = Marshal.PtrToStructure<Native.MinMaxInfo>(lParam);
                    size.MinTrackSize = new Native.Point { X = Scale(1000), Y = Scale(660) };
                    Marshal.StructureToPtr(size, lParam, false);
                    return 0;
                case 0x2e0:
                    _dpi = (int)(wParam & 0xffff);
                    CreateFonts();
                    var rect = Marshal.PtrToStructure<Native.Rect>(lParam);
                    Native.SetWindowPos(window, 0, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, 0x14);
                    Layout();
                    return 0;
                case 0xf:
                    Paint(window);
                    return 0;
                case 0x14:
                    return 1;
                case 0x2c:
                    var measure = Marshal.PtrToStructure<Native.MeasureItemData>(lParam);
                    measure.ItemHeight = (uint)Scale(28);
                    Marshal.StructureToPtr(measure, lParam, false);
                    return 1;
                case Native.DrawItem:
                    Draw(Marshal.PtrToStructure<Native.DrawItemData>(lParam));
                    return 1;
                case 0x133:
                case 0x134:
                case 0x135:
                case 0x138:
                    Native.SetTextColor((nint)wParam, TextColor);
                    Native.SetBkColor((nint)wParam, PanelColor);
                    return _panelBrush;
                case Native.Timer:
                    Tick();
                    return 0;
                case Native.Command:
                    HandleCommand((int)(wParam & 0xffff), (int)((wParam >> 16) & 0xffff));
                    return 0;
            }
            return Native.DefWindowProc(window, message, wParam, lParam);
        }
        catch (Exception error)
        {
            // Managed exceptions must never unwind through a native window callback.
            if (!_faulted)
            {
                _faulted = true;
                _exitCode = 1;
                Native.MessageBox(window, "The console window could not continue.\n\n" + error.Message,
                    "Modstoke Console", 0x10);
                Native.DestroyWindow(window);
            }
            return 0;
        }
    }

    private void Tick()
    {
        if (_fileRead is { IsCompleted: true })
        {
            try
            {
                var entries = _fileRead.GetAwaiter().GetResult();
                _history.AddRange(entries);
                _after = entries.Length == 0 ? 0 : entries[^1].Id;
                _trimmed = Math.Max(0, _after - entries.Length);
                _phase = "Offline log file";
                _summary = Path.GetFileName(_logPath) + " · read-only snapshot";
                _feedback = "Commands and live mod status are available when connected to the loader.";
                RefreshSources();
                RefreshRows();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                _phase = "Could not open log";
                _summary = error.Message;
            }
            _fileRead = null;
            Invalidate();
        }
        if (_export is { IsCompleted: true })
        {
            try
            {
                _export.GetAwaiter().GetResult();
                _feedback = "Visible entries exported.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _feedback = "Export failed: " + error.Message;
            }
            _export = null;
            if (_closeAfterExport)
            {
                Native.DestroyWindow(_window);
                return;
            }
            UpdateActions();
            Invalidate();
        }
        if (_pipe == null)
            return;
        if (_request is { IsCompleted: true })
        {
            var wasSubmission = _submission;
            try
            {
                var response = _request.GetAwaiter().GetResult();
                ValidateSnapshot(response);
                ApplySnapshot(response);
                _failures = 0;
                _connected = true;
                if (wasSubmission)
                {
                    _feedback = response.Detail.Length != 0 ? response.Detail
                        : response.Accepted ? "Command queued. Watch the log for its result." : "Command rejected by the loader.";
                }
                _nextRead = Environment.TickCount64 + (response.LastId > _after ? 0 : 250);
            }
            catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException
                or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                _connected = false;
                _ready = false;
                _phase = "Disconnected";
                _summary = "Retaining the last view · reconnecting to the same loader";
                _outcomeUnknown |= wasSubmission;
                _feedback = _outcomeUnknown
                    ? "Command outcome unknown: the response was lost. Check the log before submitting again."
                    : "Connection unavailable: " + error.Message;
                if (_pendingCommand != null)
                {
                    _pendingCommand = null;
                    _feedback = "The connection was lost before sending your command. Review the connection before submitting again.";
                }
                _failures = Math.Min(_failures + 1, 5);
                _nextRead = Environment.TickCount64 + Math.Min(5000, 250 * (1 << _failures));
            }
            _request = null;
            _submission = false;
            UpdateActions();
            Invalidate();
        }
        if (_request == null && _pendingCommand != null && !_confirming)
        {
            var command = _pendingCommand;
            _pendingCommand = null;
            if (_connected && _ready && _session == _pendingSession && _commands.Contains(command))
            {
                _submission = true;
                _request = ConsoleWire.ExchangeAsync(_pipe, new ConsoleRequest("submit", _after, 256, command.Name,
                    command.RequiresConfirmation, _pendingArguments), _lifetime.Token);
                _feedback = "Submitting " + command.Name + "…";
            }
            else
                _feedback = "The loader or command state changed before sending. Review the catalog and submit again.";
            UpdateActions();
            Invalidate();
        }
        if (_request == null && !_confirming && Environment.TickCount64 >= _nextRead)
            _request = ConsoleWire.ExchangeAsync(_pipe, new ConsoleRequest("read", _after, 256), _lifetime.Token);
    }

    private static void ValidateSnapshot(ConsoleSnapshot snapshot)
    {
        if (snapshot.SessionId == null || snapshot.Phase == null || snapshot.Summary == null || snapshot.Detail == null
            || snapshot.Entries == null || snapshot.Commands == null || snapshot.Mods == null
            || snapshot.Entries.Length > 256 || snapshot.Commands.Length > 512 || snapshot.Mods.Length > 512
            || snapshot.Entries.Any(entry => entry == null || entry.Message == null || entry.Source == null || entry.Level == null)
            || snapshot.Commands.Any(command => command == null || command.Name == null || command.Description == null)
            || snapshot.Mods.Any(mod => mod == null))
            throw new InvalidDataException("The loader returned an invalid console snapshot.");
    }

    private void ApplySnapshot(ConsoleSnapshot snapshot)
    {
        if (_session.Length != 0 && !string.Equals(_session, snapshot.SessionId, StringComparison.Ordinal))
        {
            _history.Clear();
            _after = 0;
            _trimmed = 0;
            _missed = 0;
            _dropped = 0;
            _ready = false;
            _commands = Array.Empty<ConsoleCommandInfo>();
            _modLines = Array.Empty<string>();
            Native.SendMessage(_catalog, Native.CbResetContent, 0, 0);
            Native.SetWindowText(_mods, "Waiting for the new loader session.");
            _session = snapshot.SessionId;
            _feedback = "A new loader session started.";
            _phase = snapshot.Phase;
            _summary = snapshot.Summary;
            RefreshSources();
            RefreshRows(force: true);
            // A request made with the previous session's cursor cannot cover the new history.
            return;
        }
        _session = snapshot.SessionId;
        _phase = snapshot.Phase;
        _summary = snapshot.Summary;
        _ready = snapshot.Ready;
        _dropped = snapshot.Dropped;
        if (_after > 0 && snapshot.FirstId > _after + 1)
            _missed += snapshot.FirstId - _after - 1;
        var first = _history.Count;
        foreach (var entry in snapshot.Entries)
        {
            if (entry.Id <= _after)
                continue;
            _history.Add(entry);
            _after = entry.Id;
        }
        var changed = _history.Count != first;
        var trimmed = false;
        if (_history.Count > 4096)
        {
            var remove = _history.Count - 4096;
            _history.RemoveRange(0, remove);
            _trimmed += remove;
            trimmed = true;
        }
        if (!_commands.SequenceEqual(snapshot.Commands))
        {
            var selected = SelectedCommand()?.Name;
            _commands = snapshot.Commands;
            Native.SendMessage(_catalog, Native.CbResetContent, 0, 0);
            foreach (var command in _commands)
                Native.SendText(_catalog, Native.CbAddString, 0, command.Name + (command.Enabled ? "" : " · unavailable"));
            var index = Array.FindIndex(_commands, command => command.Name == selected);
            Native.SendMessage(_catalog, Native.CbSetCurSel, (nuint)Math.Max(0, index), 0);
        }
        if (!_modLines.SequenceEqual(snapshot.Mods))
        {
            _modLines = snapshot.Mods;
            Native.SetWindowText(_mods, _modLines.Length == 0 ? "No mod status has been published." : string.Join("\r\n\r\n", _modLines));
        }
        if (!changed)
            return;
        // A trim can remove visible rows and sources, so only an untrimmed update may append.
        if (trimmed)
        {
            RefreshSources();
            RefreshRows();
            return;
        }
        for (var i = first; i < _history.Count; i++)
        {
            if (!_sources.Contains(_history[i].Source))
            {
                RefreshSources();
                break;
            }
        }
        AppendRows(first);
    }

    private void RefreshSources()
    {
        var selected = (int)Native.SendMessage(_source, Native.CbGetCurSel, 0, 0);
        var previous = selected > 0 && selected <= _sources.Count ? _sources[selected - 1] : "";
        var sources = _history.Select(entry => entry.Source).Distinct(StringComparer.Ordinal).OrderBy(source => source, StringComparer.Ordinal).ToArray();
        if (_sources.SequenceEqual(sources))
            return;
        _sources.Clear();
        _sources.AddRange(sources);
        Native.SendMessage(_source, Native.CbResetContent, 0, 0);
        Native.SendText(_source, Native.CbAddString, 0, "All sources");
        foreach (var source in _sources)
            Native.SendText(_source, Native.CbAddString, 0, source);
        Native.SendMessage(_source, Native.CbSetCurSel, (nuint)(_sources.IndexOf(previous) + 1), 0);
    }

    private void RefreshRows(bool force = false)
    {
        if (_paused && !force)
            return;
        var selectedId = SelectedEntry()?.Id;
        var top = (int)Native.SendMessage(_logs, Native.LbGetTopIndex, 0, 0);
        var topId = top >= 0 && top < _visible.Count ? _visible[top].Id : 0;
        var filter = ReadFilter();
        _visible.Clear();
        foreach (var entry in _history)
        {
            if (Matches(entry, filter))
                _visible.Add(entry);
        }
        _updating = true;
        Native.SendMessage(_logs, 0xb, 0, 0);
        Native.SendMessage(_logs, Native.LbResetContent, 0, 0);
        foreach (var entry in _visible)
            Native.SendText(_logs, Native.LbAddString, 0, RowText(entry));
        var selectedIndex = _visible.FindIndex(entry => entry.Id == selectedId);
        Native.SendMessage(_logs, Native.LbSetCurSel, unchecked((nuint)selectedIndex), 0);
        if (_follow && _visible.Count != 0)
            Native.SendMessage(_logs, Native.LbSetTopIndex, (nuint)Math.Max(0, _visible.Count - VisibleRowCount()), 0);
        else
        {
            var restoredTop = _visible.FindIndex(entry => entry.Id == topId);
            Native.SendMessage(_logs, Native.LbSetTopIndex, (nuint)Math.Max(0, restoredTop), 0);
        }
        Native.SendMessage(_logs, 0xb, 1, 0);
        Native.InvalidateRect(_logs, 0, true);
        Native.SetWindowText(_empty, _history.Count == 0 ? "This view is empty." : "No entries match the current filters.");
        Native.ShowWindow(_empty, _visible.Count == 0 ? 4 : 0);
        _updating = false;
        ShowDetails();
        UpdateActions();
        Invalidate();
    }

    // Filter changes rebuild immediately, so new entries only need the current filter.
    private void AppendRows(int first)
    {
        if (_paused)
            return;
        var filter = ReadFilter();
        var start = _visible.Count;
        for (var i = first; i < _history.Count; i++)
        {
            if (Matches(_history[i], filter))
                _visible.Add(_history[i]);
        }
        if (_visible.Count == start)
            return;
        _updating = true;
        Native.SendMessage(_logs, 0xb, 0, 0);
        for (var i = start; i < _visible.Count; i++)
            Native.SendText(_logs, Native.LbAddString, 0, RowText(_visible[i]));
        if (_follow)
            Native.SendMessage(_logs, Native.LbSetTopIndex, (nuint)Math.Max(0, _visible.Count - VisibleRowCount()), 0);
        Native.SendMessage(_logs, 0xb, 1, 0);
        Native.InvalidateRect(_logs, 0, true);
        Native.ShowWindow(_empty, 0);
        _updating = false;
        UpdateActions();
        Invalidate();
    }

    private (string Query, int Level, string Source) ReadFilter()
    {
        var sourceIndex = (int)Native.SendMessage(_source, Native.CbGetCurSel, 0, 0);
        return (Text(_search).Trim(), (int)Native.SendMessage(_level, Native.CbGetCurSel, 0, 0),
            sourceIndex > 0 && sourceIndex <= _sources.Count ? _sources[sourceIndex - 1] : "");
    }

    private static bool Matches(ConsoleEntry entry, (string Query, int Level, string Source) filter) =>
        (filter.Source.Length == 0 || filter.Source == entry.Source)
        && MatchesLevel(entry.Level, filter.Level)
        && (filter.Query.Length == 0 || entry.Message.Contains(filter.Query, StringComparison.OrdinalIgnoreCase)
            || entry.Source.Contains(filter.Query, StringComparison.OrdinalIgnoreCase)
            || entry.Level.Contains(filter.Query, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesLevel(string level, int filter)
    {
        var error = level.Equals("Error", StringComparison.OrdinalIgnoreCase) || level.Equals("Fatal", StringComparison.OrdinalIgnoreCase);
        var warning = level.Equals("Warning", StringComparison.OrdinalIgnoreCase) || level.Equals("Warn", StringComparison.OrdinalIgnoreCase);
        var debug = level.Equals("Debug", StringComparison.OrdinalIgnoreCase) || level.Equals("Trace", StringComparison.OrdinalIgnoreCase);
        return filter switch { 1 => error, 2 => warning, 3 => !error && !warning && !debug, 4 => debug, _ => true };
    }

    private void HandleCommand(int id, int notification)
    {
        if (_updating)
            return;
        switch (id)
        {
            case SearchId when notification == 0x300:
            case LevelId when notification == 1:
            case SourceId when notification == 1:
                RefreshRows(force: true);
                break;
            case LogsId when notification == 1:
                _follow = false;
                Native.SetWindowText(_followButton, "Follow");
                ShowDetails();
                break;
            case PauseId:
                _paused = !_paused;
                Native.SetWindowText(_pause, _paused ? "Resume" : "Pause");
                if (!_paused)
                    RefreshRows();
                Invalidate();
                break;
            case FollowId:
                _follow = !_follow;
                Native.SetWindowText(_followButton, _follow ? "Following" : "Follow");
                if (_follow)
                {
                    _paused = false;
                    Native.SetWindowText(_pause, "Pause");
                    RefreshRows();
                }
                break;
            case ClearId:
                _history.Clear();
                RefreshSources();
                RefreshRows(force: true);
                _feedback = "This window was cleared. Shared history and the disk log are unchanged.";
                break;
            case CopyId:
                CopyEntry();
                break;
            case ExportId:
                Export();
                break;
            case CatalogId when notification == 1:
                var command = SelectedCommand();
                if (command != null)
                    Native.SetWindowText(_input, command.Name);
                Invalidate();
                break;
            case SubmitId:
                Submit();
                break;
        }
        Invalidate();
    }

    private bool HandleKey(Native.Message message)
    {
        if (message.Id != 0x100)
            return false;
        var focus = Native.GetFocus();
        var key = (int)message.WParam;
        if (Native.GetKeyState(0x11) < 0 && key == 'F')
        {
            Native.SetFocus(_search);
            Native.SendMessage(_search, 0xb1, 0, -1);
            return true;
        }
        if (Native.GetKeyState(0x11) < 0 && key == 'C' && focus == _logs)
        {
            CopyEntry();
            return true;
        }
        if (focus == _input)
        {
            if (key == 13)
            {
                Submit();
                return true;
            }
            if ((key == 38 || key == 40) && _commandHistory.Count > 0)
            {
                if (_historyIndex == _commandHistory.Count)
                    _commandDraft = Text(_input);
                _historyIndex = Math.Clamp(_historyIndex + (key == 38 ? -1 : 1), 0, _commandHistory.Count);
                Native.SetWindowText(_input, _historyIndex == _commandHistory.Count ? _commandDraft : _commandHistory[_historyIndex]);
                Native.SendMessage(_input, 0xb1, unchecked((nuint)(-1)), -1);
                return true;
            }
        }
        if (key == 27 && focus == _search)
        {
            Native.SetWindowText(_search, "");
            return true;
        }
        return false;
    }

    private void Submit()
    {
        if (!_connected || !_ready || _pipe == null)
        {
            _feedback = "Commands require a live connection and a ready loader.";
            Invalidate();
            return;
        }
        if (_submission || _pendingCommand != null)
        {
            _feedback = "A command is being submitted. Wait for the loader response.";
            Invalidate();
            return;
        }
        // Split like ConsoleHub.TrySubmitLine: the name ends at the first space or tab.
        var text = Text(_input).Trim();
        var split = text.IndexOfAny(new[] { ' ', '\t' });
        var name = split < 0 ? text : text[..split];
        var arguments = split < 0 ? "" : text[(split + 1)..].Trim();
        var command = _commands.FirstOrDefault(item => item.Name.Equals(name, StringComparison.Ordinal));
        if (command == null || !command.Enabled || (arguments.Length != 0 && !command.AcceptsArguments))
        {
            _feedback = command == null ? "Choose an exact command name from the command catalog."
                : !command.Enabled ? "This command is currently unavailable."
                : "This command does not take arguments.";
            Invalidate();
            return;
        }
        var line = arguments.Length == 0 ? command.Name : command.Name + " " + arguments;
        var session = _session;
        if (command.RequiresConfirmation)
        {
            _confirming = true;
            try
            {
                if (Native.MessageBox(_window, command.Description + "\n\nRun “" + line + "”?",
                        "Confirm console command", 0x124) != 6)
                    return;
            }
            finally { _confirming = false; }
        }
        // Recheck after the modal dialog: its message loop can deliver connection updates.
        if (!_connected || !_ready || _session != session || !_commands.Contains(command))
        {
            _feedback = "The connection or command state changed. Review the catalog and submit again.";
            Invalidate();
            return;
        }
        _pendingCommand = command;
        _pendingSession = _session;
        _pendingArguments = arguments;
        _outcomeUnknown = false;
        if (_commandHistory.Count == 0 || _commandHistory[^1] != line)
            _commandHistory.Add(line);
        if (_commandHistory.Count > 100)
            _commandHistory.RemoveAt(0);
        _historyIndex = _commandHistory.Count;
        _commandDraft = "";
        Native.SetWindowText(_input, "");
        _feedback = "Preparing " + command.Name + "…";
        UpdateActions();
        Invalidate();
    }

    private ConsoleEntry? SelectedEntry()
    {
        var index = (int)Native.SendMessage(_logs, Native.LbGetCurSel, 0, 0);
        return index >= 0 && index < _visible.Count ? _visible[index] : null;
    }

    private ConsoleCommandInfo? SelectedCommand()
    {
        var index = (int)Native.SendMessage(_catalog, Native.CbGetCurSel, 0, 0);
        return index >= 0 && index < _commands.Length ? _commands[index] : null;
    }

    private void ShowDetails()
    {
        var entry = SelectedEntry();
        Native.SetWindowText(_details, entry == null ? "Select an entry to inspect its full message." : FormatEntry(entry));
        Native.EnableWindow(_copy, entry != null);
        Native.InvalidateRect(_copy, 0, true);
    }

    private void UpdateActions()
    {
        Native.EnableWindow(_input, _connected && _ready);
        Native.EnableWindow(_catalog, _connected && _ready && _commands.Length != 0);
        Native.EnableWindow(_submit, _connected && _ready && !_submission && _pendingCommand == null);
        Native.EnableWindow(_exportButton, _visible.Count != 0 && _export == null);
        Native.EnableWindow(_copy, SelectedEntry() != null);
        Native.InvalidateRect(_submit, 0, true);
        Native.InvalidateRect(_exportButton, 0, true);
    }

    private static string Text(nint control)
    {
        var text = new StringBuilder(Native.GetWindowTextLength(control) + 1);
        Native.GetWindowText(control, text, text.Capacity);
        return text.ToString();
    }

    private static string Time(ConsoleEntry entry) => entry.Timestamp == DateTimeOffset.MinValue
        ? "—" : entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static string RowText(ConsoleEntry entry)
    {
        var message = entry.Message.AsSpan(0, Math.Min(1024, entry.Message.Length)).ToString().Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return Time(entry) + "  " + entry.Level + "  " + entry.Source + "  " + message;
    }

    private static string FormatEntry(ConsoleEntry entry)
    {
        var timestamp = entry.Timestamp == DateTimeOffset.MinValue ? "Time unavailable" : entry.Timestamp.ToString("O", CultureInfo.InvariantCulture);
        return "[" + timestamp + "] [" + entry.Level + ": " + entry.Source + "]\r\n"
            + entry.Message.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    private int Scale(int value) => (int)Math.Round(value * _dpi / 96d);
    private void Invalidate() { if (_window != 0) Native.InvalidateRect(_window, 0, false); }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        if (_window != 0)
            Native.DestroyWindow(_window);
        // Completion observes failures without retaining or calling a destroyed HWND.
        var pending = new List<Task>();
        if (_request != null) pending.Add(_request);
        if (_fileRead != null) pending.Add(_fileRead);
        if (_export != null) pending.Add(_export);
        _ = Task.WhenAll(pending).ContinueWith(task =>
        {
            _ = task.Exception;
            _lifetime.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        DeleteFonts();
        if (_registered)
            Native.UnregisterClass(ClassName, _instance);
        DeleteBrushes();
        GC.KeepAlive(_procedure);
    }
}
