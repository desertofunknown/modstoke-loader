// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Modstoke.Diagnostics;

internal sealed class DiagnosticTail(string path, string source, string level, DateTimeOffset started, bool timestamped)
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly byte[] _bytes = new byte[16384];
    private readonly char[] _chars = new char[Encoding.UTF8.GetMaxCharCount(16384)];
    private readonly StringBuilder _line = new();
    private long _position;
    private bool _opened, _skipLine, _reportedFailure;

    internal void Poll()
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LastWriteTimeUtc < started.UtcDateTime) return;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (!_opened || stream.Length < _position)
            {
                _opened = true;
                _position = Math.Max(0, stream.Length - 256 * 1024);
                _skipLine = _position != 0;
                _line.Clear();
                _decoder.Reset();
            }
            stream.Position = _position;
            for (int chunk = 0; chunk < 16; chunk++)
            {
                int read = stream.Read(_bytes, 0, _bytes.Length);
                if (read == 0) break;
                _position += read;
                int count = _decoder.GetChars(_bytes, 0, read, _chars, 0, false);
                for (int i = 0; i < count; i++)
                {
                    char value = _chars[i];
                    if (value == '\n')
                    {
                        if (!_skipLine) PublishLine();
                        _skipLine = false;
                        _line.Clear();
                    }
                    else if (value != '\r' && !_skipLine && _line.Length < ConsoleHub.MessageLimit)
                        _line.Append(value);
                }
            }
            if (!timestamped && _line.Length != 0 && stream.Position == stream.Length)
            {
                PublishLine();
                _line.Clear();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!_reportedFailure)
            {
                _reportedFailure = true;
                ConsoleHub.Append("Warning", "Console", source + " capture could not read its log: " + error.Message);
            }
        }
    }

    private void PublishLine()
    {
        if (_line.Length == 0) return;
        string text = _line.ToString();
        if (timestamped)
        {
            int separator = text.IndexOf(' ');
            if (separator < 0 || !DateTimeOffset.TryParse(text.AsSpan(0, separator), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var timestamp) || timestamp < started)
                return;
        }
        ConsoleHub.Append(level, source, text);
    }
}
