// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;

namespace Modstoke.Diagnostics;

/// <summary>Bounded process-local history and submission queue shared by both console views.</summary>
public static class ConsoleHub
{
    public const int HistoryLimit = 4096;
    public const int MessageLimit = 16384;
    public const int ArgumentLimit = 1024;
    private const int HistoryCharacterLimit = 4 * 1024 * 1024;
    private const int SnapshotCharacterLimit = 128 * 1024;
    private static readonly object Sync = new();
    private static readonly Queue<ConsoleEntry> History = new();
    private static readonly Queue<ConsoleSubmission> Pending = new();
    private static readonly string Session = Guid.NewGuid().ToString("N");
    private static long _sequence, _dropped;
    private static int _characters;
    private static string _phase = "Starting", _summary = "Waiting for the Loader.";
    private static bool _ready;
    private static ConsoleCommandInfo[] _commands = Array.Empty<ConsoleCommandInfo>();
    private static string[] _mods = Array.Empty<string>();

    public static void Append(string level, string source, string message)
    {
        var text = Bound(message, MessageLimit);
        var origin = Bound(source, 128);
        var severity = Bound(level, 16);
        lock (Sync)
        {
            History.Enqueue(new ConsoleEntry(++_sequence, DateTimeOffset.Now, severity, origin, text));
            _characters += text.Length + origin.Length;
            while (History.Count > HistoryLimit || _characters > HistoryCharacterLimit)
            {
                var removed = History.Dequeue();
                _characters -= removed.Message.Length + removed.Source.Length;
                _dropped++;
            }
        }
    }

    public static ConsoleSnapshot Read(long after = 0, int limit = 256)
    {
        limit = Math.Clamp(limit, 1, 256);
        lock (Sync)
        {
            var entries = new List<ConsoleEntry>(limit);
            int characters = 0;
            foreach (var entry in History)
            {
                if (entry.Id <= after) continue;
                if (entries.Count == limit || characters + entry.Message.Length > SnapshotCharacterLimit) break;
                entries.Add(entry);
                characters += entry.Message.Length;
            }
            return new ConsoleSnapshot(Session, _phase, _summary, _ready,
                History.TryPeek(out var first) ? first.Id : _sequence + 1, _sequence, _dropped,
                entries.ToArray(), (ConsoleCommandInfo[])_commands.Clone(), (string[])_mods.Clone());
        }
    }

    public static void SetPhase(string phase, string summary)
    {
        lock (Sync)
        {
            _phase = Bound(phase, 80);
            _summary = Bound(summary, 512);
        }
    }

    /// <summary>Publish copied runtime metadata on the owning thread, never native objects or callbacks.</summary>
    public static void SetRuntime(string summary, IEnumerable<string> mods, IEnumerable<ConsoleCommandInfo> commands)
    {
        var modRows = mods.Take(512).Select(value => Bound(value, 256)).ToArray();
        var commandRows = commands.Take(512).Select(value => new ConsoleCommandInfo(
            Bound(value.Name, 256), Bound(value.Description, 512), value.Enabled, value.RequiresConfirmation,
            value.AcceptsArguments)).ToArray();
        lock (Sync)
        {
            _summary = Bound(summary, 512);
            _phase = "Running";
            _mods = modRows;
            _commands = commandRows;
            _ready = true;
        }
    }

    /// <summary>Queues one exact registered command name. Acceptance is not execution or authorization.</summary>
    public static bool TrySubmit(string command, bool confirmed, out string detail) =>
        TrySubmit(command, "", confirmed, ConsoleOrigin.Other, out detail);

    /// <summary>
    /// Queues a command name with raw argument text from the named view. Acceptance is not execution or
    /// authorization; providers decide what an origin may do.
    /// </summary>
    internal static bool TrySubmit(string command, string arguments, bool confirmed, ConsoleOrigin origin,
        out string detail)
    {
        arguments ??= "";
        if (string.IsNullOrWhiteSpace(command) || command.Length > 256 || HasLineBreak(command)
            || command.Trim().IndexOfAny(new[] { ' ', '\t' }) >= 0)
        {
            detail = "Enter one command name of at most 256 characters.";
            return false;
        }
        if (arguments.Length > ArgumentLimit || HasLineBreak(arguments))
        {
            detail = "Command arguments are limited to one line of " + ArgumentLimit + " characters.";
            return false;
        }
        lock (Sync)
        {
            if (!_ready)
            {
                detail = "Commands are unavailable until the Loader is ready.";
                return false;
            }
            if (Pending.Count >= 32)
            {
                detail = "The command queue is full. Wait for the existing results.";
                return false;
            }
            Pending.Enqueue(new ConsoleSubmission(command.Trim(), confirmed, arguments.Trim(), origin));
            detail = "Queued for the game thread; execution has not yet been confirmed.";
            return true;
        }
    }

    /// <summary>Splits one typed line at its first space or tab into a command name and arguments.</summary>
    internal static bool TrySubmitLine(string line, bool confirmed, ConsoleOrigin origin, out string detail)
    {
        var text = line?.Trim() ?? "";
        var split = text.IndexOfAny(new[] { ' ', '\t' });
        return split < 0 ? TrySubmit(text, "", confirmed, origin, out detail)
            : TrySubmit(text[..split], text[(split + 1)..], confirmed, origin, out detail);
    }

    private static bool HasLineBreak(string value) => value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0;

    /// <summary>Drained only by the Loader's owner-thread dispatcher; no command executes inside the hub.</summary>
    public static bool TryTake(out ConsoleSubmission? submission)
    {
        lock (Sync) return Pending.TryDequeue(out submission);
    }

    public static void StopCommands(string phase, string detail)
    {
        int abandoned;
        lock (Sync)
        {
            _ready = false;
            _phase = Bound(phase, 80);
            _summary = Bound(detail, 512);
            abandoned = Pending.Count;
            Pending.Clear();
        }
        if (abandoned != 0)
            Append("Warning", "Commands", abandoned + " queued commands were not executed before shutdown.");
    }

    internal static string Bound(string? value, int limit)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= limit ? value : value[..(limit - 1)] + "…";
    }
}
