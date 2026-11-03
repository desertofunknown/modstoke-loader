// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Modstoke.Diagnostics.Desktop;

internal sealed partial class ConsoleWindow
{
    private void CopyEntry()
    {
        var entry = SelectedEntry();
        if (entry == null)
            return;
        if (!Native.OpenClipboard(_window))
        {
            _feedback = "The clipboard is busy. Try copying again.";
            Invalidate();
            return;
        }
        nint memory = 0;
        try
        {
            var bytes = Encoding.Unicode.GetBytes(FormatEntry(entry) + '\0');
            memory = Native.GlobalAlloc(2, (nuint)bytes.Length);
            if (memory == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not allocate clipboard data.");
            var destination = Native.GlobalLock(memory);
            if (destination == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not access clipboard data.");
            try { Marshal.Copy(bytes, 0, destination, bytes.Length); }
            finally { Native.GlobalUnlock(memory); }
            if (!Native.EmptyClipboard() || Native.SetClipboardData(13, memory) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not copy the selected log entry.");
            memory = 0;
            _feedback = "Selected entry copied.";
        }
        catch (Win32Exception error)
        {
            _feedback = error.Message;
        }
        finally
        {
            if (memory != 0)
                Native.GlobalFree(memory);
            Native.CloseClipboard();
        }
        Invalidate();
    }

    private void Export()
    {
        if (_visible.Count == 0 || _export != null)
            return;
        var file = new Native.OpenFileName
        {
            Size = (uint)Marshal.SizeOf<Native.OpenFileName>(), Owner = _window,
            Filter = "Text log (*.txt)\0*.txt\0All files (*.*)\0*.*\0\0", FilterIndex = 1,
            File = new StringBuilder("Modstoke-console-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt", 32768),
            MaxFile = 32768, Title = "Export visible log entries", DefaultExtension = "txt",
            Flags = 0x80000 | 0x800 | 0x8 | 0x2
        };
        if (!Native.GetSaveFileName(ref file))
        {
            var error = Native.CommDlgExtendedError();
            if (error != 0)
                _feedback = "The export dialog could not open (" + error.ToString("X") + ").";
            Invalidate();
            return;
        }
        var entries = _visible.ToArray();
        var path = file.File.ToString();
        _export = Task.Run(async () =>
        {
            await using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
            foreach (var entry in entries)
                await writer.WriteLineAsync(FormatEntry(entry)).ConfigureAwait(false);
        });
        _feedback = "Exporting " + entries.Length.ToString("N0") + " visible entries…";
        UpdateActions();
        Invalidate();
    }
}
