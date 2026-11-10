// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Modstoke.Diagnostics;
using Modstoke.Sons;

namespace Modstoke.Loader
{
    internal static class RuntimeConsole
    {
        private static readonly Dictionary<string, (string Provider, string Command)> Routes = new(StringComparer.Ordinal);
        private static readonly HashSet<string> Unlisted = new(StringComparer.Ordinal);
        private static readonly char[] NameBreaks = { ' ', '\t', '\r', '\n', '\0' };
        private static int _ownerThread;
        private static long _nextCatalog;
        private static long _catalogExpires;
        private static IReadOnlyList<MenuProviderSnapshot> _catalogProviders;
        private static ModInventoryState _catalogInventory;
        private static long _catalogContentRevision;
        private static int _catalogSignature;
        private static bool _stopped;

        internal static void Initialize()
        {
            _ownerThread = Environment.CurrentManagedThreadId;
            RefreshCatalog();
            if (Host.IsServer)
                ServerConsoleInput.Start();
        }

        internal static void Update()
        {
            if (_stopped || _ownerThread == 0 || Environment.CurrentManagedThreadId != _ownerThread) return;
            if (Environment.TickCount64 >= _nextCatalog) RefreshCatalog();
            // Bound work per frame, including providers that enqueue another command from their callback.
            for (int i = 0; i < 4 && ConsoleHub.TryTake(out var submission); i++)
                Execute(submission);
        }

        // The catalog is republished only when the registry, a provider's content revision, the inventory or
        // the listed commands change, with a slow fallback for anything the signature misses.
        private static void RefreshCatalog()
        {
            var now = Environment.TickCount64;
            _nextCatalog = now + 2000;
            var providers = MenuRegistry.Snapshot();
            var inventory = LoaderState.CurrentInventory;
            long contentRevision = 0;
            foreach (var provider in providers)
                contentRevision = Math.Max(contentRevision, MenuRegistry.GetContentRevision(provider.Id));
            var signature = CommandSignature(providers);
            if (now < _catalogExpires && ReferenceEquals(providers, _catalogProviders)
                && ReferenceEquals(inventory, _catalogInventory) && contentRevision == _catalogContentRevision
                && signature == _catalogSignature)
                return;

            _catalogExpires = now + 30000;
            _catalogProviders = providers;
            _catalogInventory = inventory;
            _catalogContentRevision = contentRevision;
            _catalogSignature = signature;
            PublishCatalog(providers, inventory);
        }

        // Providers rarely report content changes, so commands that become available are noticed here.
        private static int CommandSignature(IReadOnlyList<MenuProviderSnapshot> providers)
        {
            var hash = new HashCode();
            foreach (var provider in providers)
            {
                if (!provider.HasCommands || !MenuRegistry.TryGetProviderCommands(provider.Id, out var snapshot)) continue;
                hash.Add(provider.Id);
                hash.Add(provider.Enabled);
                hash.Add(provider.RequiresAuthorization);
                hash.Add(MenuRegistry.AcceptsArguments(provider.Id));
                foreach (var command in snapshot.Commands)
                {
                    hash.Add(command.Id);
                    hash.Add(command.DisplayName);
                    hash.Add(command.Description);
                    hash.Add(command.Enabled);
                    hash.Add(command.RequiresConfirmation);
                }
            }
            return hash.ToHashCode();
        }

        private static void PublishCatalog(IReadOnlyList<MenuProviderSnapshot> providers, ModInventoryState inventory)
        {
            var commands = new List<ConsoleCommandInfo>
            {
                new("help", "Show console usage and the available commands.", true, false),
                new("status", "Show the current Loader status.", true, false),
                new("mods", "List the currently discovered mods and their status.", true, false),
                new("desktop", "Open the desktop console window.", true, false),
                new("boltwidths", "Report the bit width of each networked item-id field (writes UserData/bolt-widths.txt).", true, false)
            };
            commands.AddRange(HostCommands.Catalog());
            Routes.Clear();
            // A short prefix is listed only while exactly one provider claims it and no provider has it as its id.
            var prefixes = providers.Select(provider => (provider.Id, Prefix: MenuRegistry.GetConsolePrefix(provider.Id)))
                .Where(entry => entry.Prefix != null).GroupBy(entry => entry.Prefix, StringComparer.Ordinal)
                .Where(group => group.Count() == 1 && providers.All(provider =>
                    !string.Equals(provider.Id, group.Key, StringComparison.OrdinalIgnoreCase)))
                .ToDictionary(group => group.Single().Id, group => group.Key, StringComparer.Ordinal);
            foreach (var provider in providers)
            {
                if (!provider.HasCommands || !MenuRegistry.TryGetProviderCommands(provider.Id, out var snapshot)) continue;
                var acceptsArguments = MenuRegistry.AcceptsArguments(provider.Id);
                prefixes.TryGetValue(provider.Id, out var prefix);
                foreach (var command in snapshot.Commands)
                {
                    if (commands.Count >= 512) break;
                    string fullName = provider.Id + "/" + command.Id;
                    var route = (provider.Id, command.Id);
                    var routedFullName = IsConsoleName(fullName) && Routes.TryAdd(fullName, route);
                    string shortName = prefix == null ? null : prefix + "/" + command.Id;
                    string name = shortName != null && IsConsoleName(shortName) && Routes.TryAdd(shortName, route)
                        ? shortName : routedFullName ? fullName : null;
                    if (name == null)
                    {
                        if (Unlisted.Add(fullName))
                            RuntimeEntryPoint.LogSource?.LogWarning("Console command \"" + ConsoleHub.Bound(fullName, 96)
                                + "\" is not listed: its name has a space, exceeds 256 characters or is already taken.");
                        continue;
                    }
                    commands.Add(new ConsoleCommandInfo(name, command.DisplayName + " — " + command.Description
                        + (provider.RequiresAuthorization ? " (Provider authorization required.)" : ""),
                        provider.Enabled && command.Enabled, command.RequiresConfirmation, acceptsArguments));
                }
                if (commands.Count >= 512) break;
            }
            var mods = (inventory?.Entries ?? Array.Empty<ModInventoryEntry>()).Select(entry =>
                entry.DisplayName + " " + entry.Version + " · " + entry.Status + " · " + entry.SourceFamily);
            string summary = "Modstoke " + RuntimeEntryPoint.Version + " · "
                + (inventory == null ? "Mod discovery is pending." : inventory.Summary.Total
                    + (inventory.Summary.Total == 1 ? " discovered entry, " : " discovered entries, ")
                    + inventory.Summary.Hosted + " hosted, " + inventory.Summary.Libraries
                    + (inventory.Summary.Libraries == 1 ? " library." : " libraries."));
            ConsoleHub.SetRuntime(summary, mods, commands);
        }

        private static void Execute(ConsoleSubmission submission)
        {
            try
            {
                if (HostCommands.TryExecute(submission)) return;
                var text = submission.Command;
                if (text == "desktop")
                {
                    bool opened = ConsoleHost.TryOpenDesktop(out var detail);
                    Report(submission, opened ? "Info" : "Warning", detail);
                    return;
                }
                if (text == "boltwidths")
                {
                    Report(submission, "Info", BoltWidths.Run());
                    return;
                }
                if (text is "help" or "status" or "mods")
                {
                    var state = ConsoleHub.Read(long.MaxValue, 1);
                    string detail = text == "status" ? state.Summary
                        : text == "mods" ? string.Join(Environment.NewLine, state.Mods)
                        : submission.Origin == ConsoleOrigin.ServerConsole ? ServerHelp(state.Commands)
                        : "Select an exact name from Commands, then submit it in either view. "
                            + state.Commands.Length + " commands are listed. Confirm actions only when prompted. "
                            + "Commands run on the game thread; providers retain their own authorization rules.";
                    Report(submission, "Info", string.IsNullOrEmpty(detail) ? "No mods have been discovered yet." : detail);
                    return;
                }
                if (!Routes.TryGetValue(text, out var route)
                    || !MenuRegistry.TryGetProviderCommands(route.Provider, out var current))
                {
                    Report(submission, "Warning", "Unknown or unavailable command. Select an exact name from Commands.",
                        unknown: true);
                    return;
                }
                var command = current.Commands.FirstOrDefault(value => string.Equals(value.Id, route.Command, StringComparison.Ordinal));
                if (command == null || !command.Enabled)
                {
                    Report(submission, "Warning", "That command is no longer available.");
                    return;
                }
                if (command.RequiresConfirmation && !submission.Confirmed)
                {
                    Report(submission, "Warning", submission.Origin == ConsoleOrigin.ServerConsole
                        ? "That command requires confirmation: run it again as \"confirm " + text
                            + (submission.Arguments.Length == 0 ? "" : " " + submission.Arguments) + "\"."
                        : "That command requires explicit confirmation in the console view.");
                    return;
                }
                var invocation = new CommandInvocation(OriginOf(submission.Origin), submission.Arguments,
                    submission.Confirmed);
                bool invoked = MenuRegistry.TryInvokeProviderCommand(route.Provider, route.Command, invocation,
                    out var result);
                Report(submission, invoked ? "Info" : "Warning",
                    command.DisplayName + ": " + (result?.Detail ?? (invoked ? "Completed." : "Not invoked.")));
                // A command can change what its provider offers without notifying the registry.
                _nextCatalog = 0;
                _catalogExpires = 0;
            }
            catch (Exception error)
            {
                Report(submission, "Error", "Command failed: " + error);
            }
        }

        // Names are typed as the first word of a console line.
        private static bool IsConsoleName(string name) =>
            name.Length <= 256 && name.IndexOfAny(NameBreaks) < 0;

        private static CommandOrigin OriginOf(ConsoleOrigin origin) => origin switch
        {
            ConsoleOrigin.InGameConsole => CommandOrigin.InGameConsole,
            ConsoleOrigin.DesktopConsole => CommandOrigin.DesktopConsole,
            ConsoleOrigin.ServerConsole => CommandOrigin.ServerConsole,
            _ => CommandOrigin.OtherMod
        };

        private static string ServerHelp(ConsoleCommandInfo[] commands) =>
            "Type a command name, optionally followed by arguments. Prefix \"confirm \" to run a command that asks"
            + " for confirmation. Start a line with \"#<id> \" or add --json for a one-line \"@modstoke\" JSON reply."
            + Environment.NewLine + string.Join(Environment.NewLine, commands.Select(command =>
                command.Name + (command.Enabled ? "" : " (unavailable)") + " — " + command.Description));

        internal static void Report(ConsoleSubmission submission, string level, string text, bool unknown = false)
        {
            ReportHuman(submission, level, text);
            if (!HostOutput.WantsReply(submission))
                return;
            if (level == "Info")
                HostOutput.Reply(submission, true, new { text, level });
            else
                HostOutput.Reply(submission, false,
                    new { error = unknown ? "unknown-command" : "failed", detail = text, level });
        }

        // Server console results go through the log so they print in the server's own window; the console
        // listener copies log lines into the hub for the other views.
        internal static void ReportHuman(ConsoleSubmission submission, string level, string text)
        {
            var log = RuntimeEntryPoint.LogSource;
            if (submission.Origin != ConsoleOrigin.ServerConsole || log == null)
            {
                ConsoleHub.Append(level, "Commands", text);
                return;
            }
            if (level == "Info") log.LogMessage(text);
            else if (level == "Warning") log.LogWarning(text);
            else log.LogError(text);
        }

        internal static void Shutdown()
        {
            _stopped = true;
            ServerConsoleInput.Stop();
            ConsoleHub.StopCommands("Closing", "The game is shutting down. Final logs remain available in the desktop window.");
        }
    }
}
