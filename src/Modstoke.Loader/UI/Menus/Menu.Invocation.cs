// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;

namespace Modstoke.Loader
{
    /// <summary>
    /// Where a provider command was run from. The Loader sets it; it labels the user surface for a provider's
    /// own authorization and is not a boundary against other code in the process.
    /// </summary>
    public enum CommandOrigin
    {
        /// <summary>Another mod invoked the command, for example from its own interface.</summary>
        OtherMod,
        /// <summary>A RUN button in the Loader's mods menu, used by this process's local player.</summary>
        ModsMenu,
        /// <summary>The in-game console overlay, used by this process's local player.</summary>
        InGameConsole,
        /// <summary>The desktop console or Manager pipe, open only to the Windows user running this process.</summary>
        DesktopConsole,
        /// <summary>A dedicated server's own console input, typed in its window or written by a hosting panel.</summary>
        ServerConsole
    }

    /// <summary>One provider command invocation. Created only by the Loader.</summary>
    public sealed class CommandInvocation
    {
        internal CommandInvocation(CommandOrigin origin, string arguments, bool confirmed)
        {
            Origin = origin;
            Arguments = arguments ?? "";
            Confirmed = confirmed;
        }

        public CommandOrigin Origin { get; }
        /// <summary>Untrusted raw text after the command name, trimmed; empty when none was given.</summary>
        public string Arguments { get; }
        /// <summary>The user explicitly confirmed a command that requires confirmation.</summary>
        public bool Confirmed { get; }
    }

    /// <summary>
    /// A command provider that receives the invoking origin and argument text. The Loader's own views call it on
    /// the game thread. Providers without this interface keep their argument-free call, and any arguments sent
    /// to them are refused.
    /// </summary>
    public interface IConsoleCommandProvider : ICommandProvider
    {
        /// <summary>
        /// Optional short console name, such as "admin" for "admin/status": lowercase ASCII letters, digits,
        /// '-' or '_', at most 32 characters. It is used only while no other provider claims it or has it as
        /// its id. The console views list the short name; the server console and the pipe also accept the full
        /// "providerId/commandId" name.
        /// </summary>
        string ConsolePrefix => null;

        bool TryInvokeCommand(string commandId, CommandInvocation invocation, out CommandResultSnapshot result);
    }

    public static partial class MenuRegistry
    {
        internal static string GetConsolePrefix(string providerId)
        {
            if (!TryGetRegistration(providerId, out var provider, out _) || provider is not IConsoleCommandProvider console)
                return null;
            try
            {
                var prefix = console.ConsolePrefix;
                return prefix is { Length: > 0 and <= 32 }
                    && prefix.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')
                    ? prefix : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
