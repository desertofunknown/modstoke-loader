// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Built-in safe OpenACAI command provider used by the public menu/SDK command
// model. Commands here refresh OpenACAI runtime state only; renderers invoke
// them through OpenACAIMenu instead of owning action delegates.

using System;
using System.Collections.Generic;
using Modstoke.Loader;

namespace Modstoke.Loader
{
    internal static class RuntimeCommands
    {
        private const string ProviderId = "openacai.loader.runtimeCommands";
        private const string RefreshInventoryCommandId = "refreshInventory";
        private const string RefreshStateCommandId = "refreshState";

        private static readonly object SyncRoot = new object();
        private static string _lastCommand = "None";
        private static string _lastStatus = "READY";
        private static string _lastDetail = "No runtime command has been invoked this session.";
        private static bool _commandInProgress;

        internal static void Initialize()
        {
            MenuRegistry.RegisterOrUpdateBuiltInProvider(new RuntimeCommandsProvider());
        }

        private static IReadOnlyList<CommandSnapshot> SnapshotCommands()
        {
            lock (SyncRoot)
            {
                return new[]
                {
                    new CommandSnapshot(
                        RefreshInventoryCommandId,
                        "Refresh Inventory",
                        "Rescan Modstoke, RedLoader, BepInEx, Vortex, library, and config inventory.",
                        _commandInProgress ? "BUSY" : "READY",
                        !_commandInProgress,
                        false),
                    new CommandSnapshot(
                        RefreshStateCommandId,
                        "Refresh Loader State",
                        "Rewrite Modstoke runtime state JSON and publish current SDK services.",
                        _commandInProgress ? "BUSY" : "READY",
                        !_commandInProgress,
                        false),
                    new CommandSnapshot(
                        "lastCommand",
                        "Last Command",
                        _lastDetail,
                        _lastStatus,
                        false,
                        false)
                };
            }
        }

        private static bool TryInvokeCommand(string commandId, out CommandResultSnapshot result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(commandId))
            {
                result = CommandResultSnapshot.Failure("Command id is required.");
                return false;
            }

            var refreshInventory = string.Equals(commandId, RefreshInventoryCommandId, StringComparison.OrdinalIgnoreCase);
            if (!refreshInventory && !string.Equals(commandId, RefreshStateCommandId, StringComparison.OrdinalIgnoreCase))
            {
                result = CommandResultSnapshot.Failure("Unknown runtime command: " + commandId + ".");
                return false;
            }

            var command = refreshInventory ? "Refresh Inventory" : "Refresh Loader State";
            lock (SyncRoot)
            {
                if (_commandInProgress)
                {
                    result = CommandResultSnapshot.Failure("A runtime command is already running: " + _lastCommand + ".");
                    return false;
                }

                _commandInProgress = true;
                SetLast(command, "RUNNING", command + " is running.");
            }

            try
            {
                string detail;
                if (refreshInventory)
                {
                    var inventory = RuntimeEntryPoint.RefreshModInventory();
                    var total = inventory?.Summary?.Total ?? 0;
                    var libraries = inventory?.Summary?.Libraries ?? 0;
                    var hosted = inventory?.Summary?.Hosted ?? 0;
                    detail = "Inventory refreshed: " + total + " entries, " + libraries + " libs, " + hosted + " hosted.";
                    RuntimeEntryPoint.RequestLoaderStateRefresh("runtime-command-refresh-inventory", refreshFileIntegrity: true);
                }
                else
                {
                    RuntimeEntryPoint.RequestLoaderStateRefresh("runtime-command-refresh-state", refreshFileIntegrity: true);
                    detail = "Loader state refresh requested.";
                }

                SetLast(command, "READY", detail);
                result = CommandResultSnapshot.Success(detail);
                return true;
            }
            catch (Exception ex)
            {
                var detail = (refreshInventory ? "Inventory refresh failed: " : "Loader state refresh failed: ") + ex.Message;
                SetLast(command, "FAILED", detail);
                result = CommandResultSnapshot.Failure(detail);
                return false;
            }
            finally
            {
                lock (SyncRoot)
                {
                    _commandInProgress = false;
                }
            }
        }

        private static void SetLast(string command, string status, string detail)
        {
            lock (SyncRoot)
            {
                _lastCommand = string.IsNullOrWhiteSpace(command) ? "Unknown" : command;
                _lastStatus = string.IsNullOrWhiteSpace(status) ? "READY" : status;
                _lastDetail = (string.IsNullOrWhiteSpace(detail) ? "Command invoked." : detail)
                    + " Last command: " + _lastCommand + ".";
            }
        }

        private sealed class RuntimeCommandsProvider : ICommandProvider
        {
            public string Id => ProviderId;
            public string DisplayName => "Runtime Commands";
            public string Kind => MenuProviderKinds.CommandGroup;
            public int Priority => 330;
            public string Source => "Modstoke Loader";
            public string Description => "Run safe Modstoke runtime refresh actions.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;
            public IReadOnlyList<CommandSnapshot> GetCommands() => SnapshotCommands();
            public bool TryInvokeCommand(string commandId, out CommandResultSnapshot result) => RuntimeCommands.TryInvokeCommand(commandId, out result);
        }
    }
}
