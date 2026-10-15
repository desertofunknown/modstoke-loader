// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (C) 2022 BepInEx Team
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Text;
using BepInEx.Logging;

namespace BepInEx.Preloader.RuntimeFixes;

/// <summary>Installs the initial console logging writer without detouring framework methods.</summary>
public static class ConsoleSetOutFix
{
    private static readonly object ApplyLock = new();
    private static LoggedTextWriter loggedTextWriter;
    internal static ManualLogSource ConsoleLogSource = Logger.CreateLogSource("Console");

    /// <summary>Redirects console output to the Console log source and the original writer once.</summary>
    /// <remarks>
    /// Subsequent calls do nothing, even if another component has replaced Console.Out.
    /// Console.SetOut is not patched on .NET 11; later writer replacements remain effective.
    /// </remarks>
    public static void Apply()
    {
        lock (ApplyLock)
        {
            if (loggedTextWriter != null)
                return;

            var writer = new LoggedTextWriter { Parent = Console.Out };
            Console.SetOut(writer);
            // Console.SetOut can wrap the writer, so retain our own installation marker.
            loggedTextWriter = writer;
        }
    }
}

/// <summary>Forwards console output to the compatibility logger and a parent writer.</summary>
internal class LoggedTextWriter : TextWriter
{
    [ThreadStatic]
    private static bool logging;

    /// <inheritdoc />
    public override Encoding Encoding { get; } = Encoding.UTF8;

    /// <summary>Gets or sets the writer that receives the original console output.</summary>
    public TextWriter Parent { get; set; } = TextWriter.Null;

    /// <inheritdoc />
    public override void Flush() => Parent.Flush();

    /// <inheritdoc />
    public override void Write(char value) => Write(value.ToString());

    /// <inheritdoc />
    public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

    /// <inheritdoc />
    public override void Write(string value)
    {
        Log(value);
        Parent.Write(value);
    }

    /// <inheritdoc />
    public override void WriteLine() => WriteLine(string.Empty);

    /// <inheritdoc />
    public override void WriteLine(string value)
    {
        Log(value);
        Parent.WriteLine(value);
    }

    private static void Log(string value)
    {
        // A listener can itself write to Console.Out. Forward that output without logging it again.
        if (logging)
            return;

        try
        {
            logging = true;
            ConsoleSetOutFix.ConsoleLogSource.Log(LogLevel.Info, value);
        }
        finally
        {
            logging = false;
        }
    }
}
