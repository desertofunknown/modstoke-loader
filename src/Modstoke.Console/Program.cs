// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using Modstoke.Diagnostics.Desktop;

namespace Modstoke.Diagnostics;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2 || (args[0] != "--pipe" && args[0] != "--log"))
                throw new ArgumentException("Open a running loader with --pipe <name>, or view an existing log with --log <path>.");

            string? pipe = null;
            string? log = null;
            if (args[0] == "--pipe")
            {
                if (string.IsNullOrWhiteSpace(args[1]) || args[1].Length > 160
                    || !args[1].StartsWith("modstoke-console-", StringComparison.Ordinal))
                    throw new ArgumentException("The console pipe name is invalid.");
                foreach (var character in args[1])
                {
                    if (!char.IsAsciiLetterOrDigit(character) && character != '.' && character != '-' && character != '_')
                        throw new ArgumentException("The console pipe name contains unsupported characters.");
                }
                pipe = args[1];
            }
            else
            {
                log = Path.GetFullPath(args[1]);
                if (!File.Exists(log))
                    throw new FileNotFoundException("The requested log file does not exist.", log);
            }

            using var window = new ConsoleWindow(pipe, log);
            return window.Run();
        }
        catch (Exception error)
        {
            Native.MessageBox(0, error.Message, "Modstoke Console", 0x10);
            return 1;
        }
    }
}
