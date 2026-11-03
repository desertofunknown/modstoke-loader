// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Modstoke.Diagnostics.Desktop;

internal sealed partial class ConsoleWindow
{
    private const uint BackgroundColor = 0x18140d, PanelColor = 0x262117, TextColor = 0xece9e2,
        MutedColor = 0xb7ae9b, AccentColor = 0xc8db64, BorderColor = 0x494130,
        AccentPanelColor = 0x45441d, ErrorColor = 0x838cfc, WarningColor = 0x84c8ed;
    private nint _backgroundBrush, _panelBrush, _borderBrush, _accentBrush, _selectionBrush;
    private nint _font, _titleFont, _smallFont, _monoFont;
    private int _width, _height, _mainWidth, _sidebarX, _detailY, _logHeight;

    private void CreateBrushes()
    {
        _backgroundBrush = Brush(BackgroundColor);
        _panelBrush = Brush(PanelColor);
        _borderBrush = Brush(BorderColor);
        _accentBrush = Brush(AccentColor);
        _selectionBrush = Brush(AccentPanelColor);
    }

    private static nint Brush(uint color)
    {
        var brush = Native.CreateSolidBrush(color);
        if (brush == 0)
            throw new Win32Exception("Could not allocate a console drawing brush.");
        return brush;
    }

    private void CreateFonts()
    {
        var previous = new[] { _font, _titleFont, _smallFont, _monoFont };
        var fonts = new nint[4];
        try
        {
            fonts[0] = Font(14, 400, "Segoe UI");
            fonts[1] = Font(27, 600, "Segoe UI");
            fonts[2] = Font(12, 400, "Segoe UI");
            fonts[3] = Font(13, 400, "Consolas");
        }
        catch
        {
            foreach (var font in fonts)
                if (font != 0) Native.DeleteObject(font);
            throw;
        }
        _font = fonts[0]; _titleFont = fonts[1]; _smallFont = fonts[2]; _monoFont = fonts[3];
        foreach (var control in _controls)
            Native.SendMessage(control, Native.SetFont, (nuint)(control == _details || control == _input ? _monoFont : _font), 1);
        if (_logs != 0)
            Native.SendMessage(_logs, Native.LbSetItemHeight, 0, Scale(28));
        foreach (var font in previous)
            if (font != 0) Native.DeleteObject(font);
    }

    private nint Font(int pixels, int weight, string face)
    {
        var font = Native.CreateFont(-Scale(pixels), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, face);
        if (font == 0)
            throw new Win32Exception("Could not allocate a console font.");
        return font;
    }

    private void CreateControls()
    {
        _search = Control("EDIT", "", SearchId, Native.TabStop | 0x80, border: true);
        Native.SendText(_search, 0x1501, 0, "Search messages or sources   ·   Ctrl+F");
        Native.SendMessage(_search, 0xc5, 2048, 0);
        _level = Combo(LevelId);
        foreach (var value in new[] { "All levels", "Errors / fatal", "Warnings", "Info / message", "Debug / trace" })
            Native.SendText(_level, Native.CbAddString, 0, value);
        Native.SendMessage(_level, Native.CbSetCurSel, 0, 0);
        _source = Combo(SourceId);
        Native.SendText(_source, Native.CbAddString, 0, "All sources");
        Native.SendMessage(_source, Native.CbSetCurSel, 0, 0);
        _pause = Button("Pause", PauseId);
        _followButton = Button("Following", FollowId);
        _clear = Button("Clear view", ClearId);
        _copy = Button("Copy entry", CopyId);
        _exportButton = Button("Export view", ExportId);
        // Strings remain available to accessibility clients; only their drawing is customized.
        _logs = Control("LISTBOX", "Log entries", LogsId, Native.TabStop | 0x200000 | 0x1 | 0x10 | 0x40 | 0x100);
        Native.SendMessage(_logs, Native.LbSetItemHeight, 0, Scale(28));
        _empty = Control("STATIC", _logPath == null ? "Waiting for log entries…" : "Opening log file…", 0, 1);
        _details = Control("EDIT", "Select an entry to inspect its full message.", DetailsId,
            Native.TabStop | 0x200000 | 0x4 | 0x40 | 0x800, border: true);
        Native.SendMessage(_details, 0xc5, 131072, 0);
        _mods = Control("EDIT", _logPath == null ? "Waiting for the loader to publish mod status." : "Mod status is unavailable in an offline log file.",
            ModsId, Native.TabStop | 0x200000 | 0x4 | 0x40 | 0x800, border: true);
        Native.SendMessage(_mods, 0xc5, 1048576, 0);
        _catalog = Combo(CatalogId);
        _input = Control("EDIT", "", InputId, Native.TabStop | 0x80, border: true);
        Native.SendText(_input, 0x1501, 0, "Command name [arguments]   ·   Enter to run   ·   ↑ / ↓ history");
        Native.SendMessage(_input, 0xc5, 256, 0);
        _submit = Button("Run command", SubmitId);
        Native.SendMessage(_details, Native.SetFont, (nuint)_monoFont, 1);
        Native.SendMessage(_input, Native.SetFont, (nuint)_monoFont, 1);
    }

    private nint Control(string type, string text, int id, uint style, bool border = false)
    {
        var control = Native.CreateWindowEx(border ? 0x200u : 0, type, text,
            Native.Child | Native.Visible | style, 0, 0, 20, 20, _window, id, _instance, 0);
        if (control == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the " + type + " console control.");
        _controls.Add(control);
        Native.SendMessage(control, Native.SetFont, (nuint)_font, 0);
        Native.SetWindowTheme(control, type == "COMBOBOX" ? "DarkMode_CFD" : "DarkMode_Explorer", null);
        if (type == "EDIT")
            Native.SendMessage(control, 0xd3, 3, Scale(9) | (Scale(9) << 16));
        return control;
    }

    private nint Button(string text, int id) => Control("BUTTON", text, id, Native.TabStop | 0xb);
    private nint Combo(int id) => Control("COMBOBOX", "", id, Native.TabStop | 0x200000 | 0x3);

    private void Layout()
    {
        if (_window == 0)
            return;
        Native.GetClientRect(_window, out var area);
        _width = (int)(area.Right * 96d / _dpi);
        _height = (int)(area.Bottom * 96d / _dpi);
        _sidebarX = _width - 292;
        _mainWidth = _sidebarX - 42;
        _detailY = Math.Max(360, _height - 260);
        _logHeight = Math.Max(100, _detailY - 254);
        var searchWidth = Math.Max(210, _mainWidth - 342);
        Place(_search, 24, 172, searchWidth, 32);
        Place(_level, 24 + searchWidth + 10, 172, 145, 240);
        Place(_source, 24 + searchWidth + 165, 172, _mainWidth - searchWidth - 165, 320);
        Place(_pause, _sidebarX, 172, 80, 32);
        Place(_followButton, _sidebarX + 90, 172, 90, 32);
        Place(_clear, _sidebarX + 190, 172, 78, 32);
        Place(_logs, 24, 244, _mainWidth, _logHeight);
        Place(_empty, 40, 244 + _logHeight / 2 - 12, _mainWidth - 32, 24);
        Place(_details, 24, _detailY + 27, _mainWidth, Math.Max(90, _height - _detailY - 138));
        Place(_copy, _sidebarX, _height - 145, 126, 32);
        Place(_exportButton, _sidebarX + 136, _height - 145, 132, 32);
        var modsHeight = Math.Clamp((_height - 390) / 2, 90, 260);
        Place(_mods, _sidebarX, 244, 268, modsHeight);
        Place(_catalog, _sidebarX, 290 + modsHeight, 268, 300);
        Place(_input, 24, _height - 69, _width - 196, 34);
        Place(_submit, _width - 160, _height - 69, 136, 34);
        Invalidate();
    }

    private void Place(nint control, int x, int y, int width, int height)
    {
        if (control != 0)
            Native.MoveWindow(control, Scale(x), Scale(y), Scale(Math.Max(1, width)), Scale(Math.Max(1, height)), true);
    }

    private int VisibleRowCount()
    {
        Native.GetClientRect(_logs, out var area);
        return Math.Max(1, (area.Bottom - area.Top) / Scale(28));
    }

    private void Paint(nint window)
    {
        var device = Native.BeginPaint(window, out var paint);
        try
        {
            Native.GetClientRect(window, out var area);
            Native.FillRect(device, ref area, _backgroundBrush);
            if (_font == 0)
                return;
            var old = Native.SelectObject(device, _font);
            Native.SetBkMode(device, 1);
            Fill(device, 24, 26, 42, 42, _selectionBrush);
            Label(device, "M", 24, 26, 42, 42, AccentColor, _titleFont, center: true);
            Label(device, "Modstoke Console", 80, 20, 420, 38, TextColor, _titleFont);
            Label(device, "LOADER DIAGNOSTICS", 82, 59, 420, 18, MutedColor, _smallFont);
            var state = _logPath != null ? "OFFLINE FILE" : _connected ? "LIVE SESSION" : "CONNECTING";
            if (_pipe != null && !_connected && _failures > 0)
                state = "DISCONNECTED";
            Fill(device, _width - 170, 32, 146, 28, _connected ? _selectionBrush : _panelBrush);
            Label(device, state, _width - 170, 32, 146, 28, _connected ? AccentColor : MutedColor, _smallFont, center: true);
            Fill(device, 24, 94, _width - 48, 62, _panelBrush);
            Fill(device, 24, 94, 3, 62, _connected ? _accentBrush : _borderBrush);
            Label(device, _phase, 40, 101, _width - 370, 23, TextColor, _font);
            Label(device, _summary, 40, 126, _width - 370, 20, MutedColor, _smallFont);
            Label(device, _visible.Count.ToString("N0") + " visible / " + _history.Count.ToString("N0") + " retained",
                _width - 310, 102, 268, 22, TextColor, _font);
            var retention = _paused ? (_logPath == null ? "Display paused · capture continues" : "Display paused · offline snapshot") : _missed > 0
                ? _missed.ToString("N0") + " entries missed before delivery" : _dropped > 0 || _trimmed > 0
                    ? "Recent history · older entries omitted" : _logPath == null ? "Shared logs · newest 4,096 retained" : "Existing log · newest 4,096 retained";
            Label(device, retention, _width - 310, 126, 268, 20, MutedColor, _smallFont);
            Label(device, "TIME", 34, 216, 92, 22, MutedColor, _smallFont);
            Label(device, "LEVEL", 132, 216, 72, 22, MutedColor, _smallFont);
            Label(device, "SOURCE", 210, 216, 144, 22, MutedColor, _smallFont);
            Label(device, "MESSAGE", 360, 216, Math.Max(80, _mainWidth - 336), 22, MutedColor, _smallFont);
            Label(device, "MOD STATUS", _sidebarX, 216, 268, 22, MutedColor, _smallFont);
            Label(device, "ENTRY DETAILS", 24, _detailY, _mainWidth, 22, MutedColor, _smallFont);
            var modsHeight = Math.Clamp((_height - 390) / 2, 90, 260);
            Label(device, "COMMAND CATALOG", _sidebarX, 260 + modsHeight, 268, 22, MutedColor, _smallFont);
            var command = SelectedCommand();
            var description = command == null
                ? _logPath != null ? "Connect to a running loader to discover and run its registered commands."
                    : "Registered commands will appear when the loader connects."
                : command.Description + (command.RequiresConfirmation ? "\n\nConfirmation required." : "");
            Paragraph(device, description, _sidebarX + 2, 328 + modsHeight, 264,
                Math.Max(16, _height - modsHeight - 490), MutedColor, _smallFont);
            Label(device, "COMMAND", 24, _height - 96, 200, 22, MutedColor, _smallFont);
            Label(device, _connected && _ready ? "Runs on the loader thread" : "Read-only until the loader is ready",
                _width - 308, _height - 96, 284, 22, MutedColor, _smallFont);
            Label(device, _feedback.Length == 0 ? "Select a row for details. Closing this window leaves the game running." : _feedback,
                24, _height - 27, _width - 48, 21, MutedColor, _smallFont);
            Native.SelectObject(device, old);
        }
        finally
        {
            Native.EndPaint(window, ref paint);
        }
    }

    private void Draw(Native.DrawItemData item)
    {
        var old = Native.SelectObject(item.Device, _font);
        Native.SetBkMode(item.Device, 1);
        if (item.ControlId == LogsId)
        {
            Native.FillRect(item.Device, ref item.Bounds, (item.State & 1) != 0 ? _selectionBrush : _panelBrush);
            if (item.ItemId < _visible.Count)
            {
                var entry = _visible[(int)item.ItemId];
                var y = item.Bounds.Top;
                var h = item.Bounds.Bottom - y;
                Cell(item.Device, Time(entry), item.Bounds.Left + Scale(10), y, Scale(92), h, MutedColor, _monoFont);
                Cell(item.Device, entry.Level, item.Bounds.Left + Scale(108), y, Scale(72), h, LevelColor(entry.Level), _smallFont);
                Cell(item.Device, entry.Source, item.Bounds.Left + Scale(186), y, Scale(144), h, AccentColor, _font);
                var message = entry.Message.AsSpan(0, Math.Min(1024, entry.Message.Length)).ToString().Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
                Cell(item.Device, message, item.Bounds.Left + Scale(336), y,
                    Math.Max(0, item.Bounds.Right - item.Bounds.Left - Scale(346)), h, TextColor, _font);
            }
        }
        else
        {
            var enabled = Native.IsWindowEnabled(item.Window);
            var active = item.ControlId == SubmitId || item.ControlId == FollowId && _follow || item.ControlId == PauseId && _paused;
            Native.FillRect(item.Device, ref item.Bounds, active && enabled ? _selectionBrush : _panelBrush);
            Native.FrameRect(item.Device, ref item.Bounds, active && enabled ? _accentBrush : _borderBrush);
            var textRect = item.Bounds;
            Native.SetTextColor(item.Device, !enabled ? MutedColor : active ? AccentColor : TextColor);
            Native.DrawText(item.Device, Text(item.Window), -1, ref textRect,
                Native.TextCenter | Native.TextVCenter | Native.TextSingleLine | Native.TextNoPrefix);
        }
        if ((item.State & 0x10) != 0)
        {
            var focus = item.Bounds;
            focus.Left += 3; focus.Right -= 3; focus.Top += 3; focus.Bottom -= 3;
            Native.DrawFocusRect(item.Device, ref focus);
        }
        Native.SelectObject(item.Device, old);
    }

    private static uint LevelColor(string level) => level.Equals("Error", StringComparison.OrdinalIgnoreCase)
        || level.Equals("Fatal", StringComparison.OrdinalIgnoreCase) ? ErrorColor
        : level.Equals("Warning", StringComparison.OrdinalIgnoreCase) || level.Equals("Warn", StringComparison.OrdinalIgnoreCase)
            ? WarningColor : MutedColor;

    private void Fill(nint device, int x, int y, int width, int height, nint brush)
    {
        var rect = new Native.Rect(Scale(x), Scale(y), Scale(x + width), Scale(y + height));
        Native.FillRect(device, ref rect, brush);
    }

    private void Label(nint device, string text, int x, int y, int width, int height, uint color, nint font, bool center = false)
        => Cell(device, text, Scale(x), Scale(y), Scale(width), Scale(height), color, font, center);

    private static void Cell(nint device, string text, int x, int y, int width, int height, uint color, nint font, bool center = false)
    {
        var old = Native.SelectObject(device, font);
        Native.SetTextColor(device, color);
        var rect = new Native.Rect(x, y, x + width, y + height);
        Native.DrawText(device, text, text.Length, ref rect,
            Native.TextSingleLine | Native.TextVCenter | Native.TextNoPrefix | Native.TextEllipsis | (center ? Native.TextCenter : 0));
        Native.SelectObject(device, old);
    }

    private void Paragraph(nint device, string text, int x, int y, int width, int height, uint color, nint font)
    {
        var old = Native.SelectObject(device, font);
        Native.SetTextColor(device, color);
        var rect = new Native.Rect(Scale(x), Scale(y), Scale(x + width), Scale(y + height));
        Native.DrawText(device, text, text.Length, ref rect, 0x10 | Native.TextNoPrefix | Native.TextEllipsis);
        Native.SelectObject(device, old);
    }

    private void DeleteFonts()
    {
        foreach (var font in new[] { _font, _titleFont, _smallFont, _monoFont })
            if (font != 0) Native.DeleteObject(font);
    }

    private void DeleteBrushes()
    {
        foreach (var brush in new[] { _backgroundBrush, _panelBrush, _borderBrush, _accentBrush, _selectionBrush })
            if (brush != 0) Native.DeleteObject(brush);
    }
}
