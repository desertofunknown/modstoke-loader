// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Runtime.InteropServices;
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
                // Hosting panels write UTF-8 regardless of the Windows console code page. Under Wine a panel's TTY
                // reaches us as a console in line mode, which only ends a line on CR; panels send LF, so the
                // console is switched to raw input and read as a byte stream.
                var raw = !Console.IsInputRedirected && RunningUnderWine() && TryUseRawConsoleInput();
                var input = Console.IsInputRedirected || raw
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

        private static bool RunningUnderWine()
        {
            try
            {
                return NativeLibrary.TryLoad("ntdll.dll", out var ntdll) && NativeLibrary.TryGetExport(ntdll, "wine_get_version", out _);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryUseRawConsoleInput()
        {
            const uint EnableLineInput = 0x0002, EnableEchoInput = 0x0004;
            var handle = GetStdHandle(StdInputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1) || !GetConsoleMode(handle, out var mode))
                return false;
            if (!SetConsoleMode(handle, mode & ~(EnableLineInput | EnableEchoInput)))
                return false;
            RuntimeEntryPoint.LogSource?.LogInfo("Running under Wine: server console input reads LF-terminated lines.");
            return true;
        }

        private const int StdInputHandle = -10;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr handle, uint mode);

        private static string FirstWord(string text)
        {
            var split = text.IndexOfAny(new[] { ' ', '\t' });
            return split < 0 ? text : text[..split];
        }
    }
}
