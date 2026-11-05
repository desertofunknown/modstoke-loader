// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Text;
using System.Threading;
using Modstoke.Diagnostics;

namespace Modstoke.Loader
{
    /// <summary>
    /// Reads console commands from a dedicated server's standard input, which the game itself leaves unused.
    /// The window keeps its own output; this only queues typed or panel-written lines for the game thread.
    /// </summary>
    internal static class ServerConsoleInput
    {
        private static Thread _reader;
        private static volatile bool _stopped;

        internal static void Start()
        {
            if (_reader != null)
                return;
            _reader = new Thread(Read) { IsBackground = true, Name = "Modstoke server console input" };
            _reader.Start();
        }

        internal static void Stop() => _stopped = true;

        private static void Read()
        {
            try
            {
                // Hosting panels write UTF-8 regardless of the Windows console code page.
                var input = Console.IsInputRedirected
                    ? new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false))
                    : Console.In;
                RuntimeEntryPoint.LogSource?.LogMessage("Server console ready. Type help for Modstoke commands.");
                var buffer = new StringBuilder();
                while (!_stopped)
                {
                    var line = ReadLine(input, buffer, out var tooLong);
                    if (line == null || _stopped)
                        return;
                    if (tooLong)
                    {
                        Refuse("too-large", null);
                        continue;
                    }
                    line = line.Trim();
                    if (line.Length == 0)
                        continue;
                    if (!HostLine.TryParse(line, out var text, out var confirmed, out var requestId, out var error))
                    {
                        Refuse(error, requestId);
                        continue;
                    }
                    if (!ConsoleHub.TrySubmitLine(text, confirmed, ConsoleOrigin.ServerConsole, requestId, false, 0,
                            out var detail, out var reason))
                    {
                        RuntimeEntryPoint.LogSource?.LogWarning(detail);
                        if (requestId != null)
                            HostOutput.Reply(requestId, 0, FirstWord(text), false, new { error = reason, detail });
                    }
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException
                or InvalidOperationException or UnauthorizedAccessException)
            {
                // Input closed or never existed, for example when the server runs as a service.
            }
        }

        private static void Refuse(string error, string requestId)
        {
            var problem = error == "too-large"
                ? "Console input longer than " + HostLine.MaxLength + " characters was ignored."
                : error == "invalid-id" ? "A request id is 1 to 64 letters, digits, '.', '_', ':' or '-'."
                : "A request id must be followed by a command.";
            RuntimeEntryPoint.LogSource?.LogWarning(problem);
            if (requestId != null || error == "invalid-id")
                HostOutput.Reply(requestId, 0, "", false, new { error, detail = problem });
        }

        // ReadLine would buffer a line of any length; this keeps at most one character past the limit
        // and skips the rest of the line.
        private static string ReadLine(TextReader input, StringBuilder buffer, out bool tooLong)
        {
            buffer.Clear();
            tooLong = false;
            while (true)
            {
                var next = input.Read();
                if (next < 0)
                    return buffer.Length > 0 || tooLong ? buffer.ToString() : null;
                if (next is '\n' or '\r')
                    return buffer.ToString();
                if (buffer.Length <= HostLine.MaxLength)
                    buffer.Append((char)next);
                else
                    tooLong = true;
            }
        }

        private static string FirstWord(string text)
        {
            var split = text.IndexOfAny(new[] { ' ', '\t' });
            return split < 0 ? text : text[..split];
        }
    }
}
