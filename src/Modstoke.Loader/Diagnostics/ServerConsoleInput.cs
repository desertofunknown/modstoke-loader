// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
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
        private const string ConfirmPrefix = "confirm ";
        private const int MaxLineLength = 2048;
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
                var input = Console.In;
                RuntimeEntryPoint.LogSource?.LogMessage("Server console ready. Type help for Modstoke commands.");
                while (!_stopped)
                {
                    var line = input.ReadLine();
                    if (line == null || _stopped)
                        return;
                    line = line.Trim();
                    if (line.Length == 0)
                        continue;
                    if (line.Length > MaxLineLength)
                    {
                        RuntimeEntryPoint.LogSource?.LogWarning("Console input longer than " + MaxLineLength
                            + " characters was ignored.");
                        continue;
                    }
                    var confirmed = line.StartsWith(ConfirmPrefix, StringComparison.OrdinalIgnoreCase);
                    if (confirmed)
                        line = line[ConfirmPrefix.Length..];
                    if (!ConsoleHub.TrySubmitLine(line, confirmed, ConsoleOrigin.ServerConsole, out var detail))
                        RuntimeEntryPoint.LogSource?.LogWarning(detail);
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException
                or InvalidOperationException or UnauthorizedAccessException)
            {
                // Input closed or never existed, for example when the server runs as a service.
            }
        }
    }
}
