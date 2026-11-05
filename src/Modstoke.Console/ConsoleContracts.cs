// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Diagnostics;

public sealed record ConsoleEntry(long Id, DateTimeOffset Timestamp, string Level, string Source, string Message);

public sealed record ConsoleCommandInfo(string Name, string Description, bool Enabled, bool RequiresConfirmation,
    bool AcceptsArguments = false);

public sealed record ConsoleRequest(string Operation, long After = 0, int Limit = 256,
    string Command = "", bool Confirmed = false, string Arguments = "");

public sealed record ConsoleSnapshot(string SessionId, string Phase, string Summary, bool Ready,
    long FirstId, long LastId, long Dropped, ConsoleEntry[] Entries, ConsoleCommandInfo[] Commands,
    string[] Mods, bool Accepted = false, string Detail = "");

/// <summary>Written to Modstoke/UserData/console-endpoint.json while a game or server process serves its console.</summary>
public sealed record ConsoleEndpoint(int Schema, string Pipe, int ProcessId, DateTimeOffset StartedUtc);

public sealed record ConsoleSubmission(string Command, bool Confirmed, string Arguments = "",
    ConsoleOrigin Origin = ConsoleOrigin.Other)
{
    /// <summary>The hosting agent's id from <c>#id</c> or <c>--id</c>, echoed in the machine reply.</summary>
    public string? RequestId { get; init; }
    /// <summary>True when the sender asked for a machine reply with <c>--json</c> or over TCP.</summary>
    public bool Json { get; init; }
    /// <summary>Where the machine reply goes: 0 for the server's stdout, otherwise a host TCP client id.</summary>
    public int ReplyChannel { get; init; }
}

/// <summary>Which view queued a submission. A label for authorization policy, not a security boundary.</summary>
public enum ConsoleOrigin
{
    /// <summary>Queued by in-process code through the origin-less overload.</summary>
    Other,
    /// <summary>The in-game console overlay, used by this process's local player.</summary>
    InGameConsole,
    /// <summary>The local desktop pipe, open only to the Windows user running the game or server.</summary>
    DesktopConsole,
    /// <summary>The dedicated server's own console input: typed in its window or written by a hosting panel.</summary>
    ServerConsole
}
