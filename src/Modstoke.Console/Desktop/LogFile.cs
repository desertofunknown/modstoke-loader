// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Modstoke.Diagnostics.Desktop;

internal static class LogFile
{
    internal static ConsoleEntry[] Read(string path, CancellationToken token)
    {
        var entries = new Queue<ConsoleEntry>(4096);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var source = Path.GetFileName(path);
        ConsoleEntry? previous = null;
        var previousStructured = false;
        long id = 0;
        while (reader.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            var entry = Parse(line, source, out var structured);
            if (!structured && previousStructured && previous != null)
            {
                var remaining = 32768 - previous.Message.Length - Environment.NewLine.Length;
                if (remaining > 0)
                    previous = previous with { Message = previous.Message + Environment.NewLine + line[..Math.Min(remaining, line.Length)] };
                continue;
            }
            if (previous != null)
            {
                entries.Enqueue(previous);
                if (entries.Count > 4096)
                    entries.Dequeue();
            }
            previous = entry with { Id = ++id };
            previousStructured = structured;
        }
        if (previous != null)
        {
            entries.Enqueue(previous);
            if (entries.Count > 4096)
                entries.Dequeue();
        }
        return entries.ToArray();
    }

    private static ConsoleEntry Parse(string line, string source, out bool structured)
    {
        structured = false;
        var timestamp = DateTimeOffset.MinValue;
        var level = "Info";
        var text = line.AsSpan();
        var bracket = text.Length > 0 && text[0] == '[';
        var end = bracket ? text.IndexOf(']') : text.IndexOf(' ');
        var start = bracket ? 1 : 0;
        if (end > start && DateTimeOffset.TryParse(text[start..end], CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            timestamp = parsed;
            text = text[(end + 1)..].TrimStart();
            structured = true;
        }
        if (text.Length > 0 && text[0] == '[' && (end = text.IndexOf(']')) > 1)
        {
            var header = text[1..end];
            var separator = header.IndexOf(':');
            if (separator > 0)
            {
                level = header[..separator].Trim().ToString();
                source = header[(separator + 1)..].Trim().ToString();
                structured = true;
                text = text[(end + 1)..].TrimStart();
            }
            else if (structured)
            {
                source = header.ToString();
                text = text[(end + 1)..].TrimStart();
            }
        }
        return new ConsoleEntry(0, timestamp, level, source, text[..Math.Min(text.Length, 32768)].ToString());
    }
}
