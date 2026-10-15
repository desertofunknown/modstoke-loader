// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Built-in safe OpenACAI command provider used by the public menu/SDK command
// model. Commands here refresh OpenACAI runtime state only; renderers invoke
// them through OpenACAIMenu instead of owning action delegates.

using System;
using System.Collections.Generic;
using OpenACAI.Loader;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenACAIRuntimeCommands
    {
        private const string ProviderId = "openacai.loader.runtimeCommands";
        private const string RefreshInventoryCommandId = "refreshInventory";
        private const string RefreshStateCommandId = "refreshState";

        private static readonly object SyncRoot = new object();
        private static string _lastCommand = "None";
        private static string _lastStatus = "READY";
        private static string _lastDetail = "No runtime command has been invoked this session.";

        internal static void Initialize()
        {
            OpenACAIMenu.RegisterOrUpdateProvider(new RuntimeCommandsProvider());
        }

        private static IReadOnlyList<OpenACAICommandSnapshot> SnapshotCommands()
        {
            lock (SyncRoot)
            {
                return new[]
                {
                    new OpenACAICommandSnapshot(
                        RefreshInventoryCommandId,
                        "Refresh Inventory",
                        "Rescan OpenACAI, RedLoader, BepInEx, Vortex, library, and config inventory.",
                        "READY",
                        true,
                        false),
                    new OpenACAICommandSnapshot(
                        RefreshStateCommandId,
                        "Refresh Loader State",
                        "Rewrite OpenACAI runtime state JSON and publish current SDK services.",
                        "READY",
                        true,
                        false),
                    new OpenACAICommandSnapshot(
                        "lastCommand",
                        "Last Command",
                        _lastDetail,
                        _lastStatus,
                        false,
                        false)
                };
            }
        }

        private static bool TryInvokeCommand(string commandId, out OpenACAICommandResultSnapshot result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(commandId))
            {
                result = OpenACAICommandResultSnapshot.Failure("Command id is required.");
                return false;
            }

            if (string.Equals(commandId, RefreshInventoryCommandId, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var inventory = OpenACAIEndnightRuntime.RefreshModInventory();
                    var total = inventory?.Summary?.Total ?? 0;
                    var libraries = inventory?.Summary?.Libraries ?? 0;
                    var hosted = inventory?.Summary?.Hosted ?? 0;
                    var detail = "Inventory refreshed: " + total + " entries, " + libraries + " libs, " + hosted + " hosted.";
                    SetLast("Refresh Inventory", "READY", detail);
                    OpenACAIEndnightRuntime.RequestLoaderStateRefresh("runtime-command-refresh-inventory");
                    result = OpenACAICommandResultSnapshot.Success(detail);
                    return true;
                }
                catch (Exception ex)
                {
                    var detail = "Inventory refresh failed: " + ex.Message;
                    SetLast("Refresh Inventory", "FAILED", detail);
                    result = OpenACAICommandResultSnapshot.Failure(detail);
                    return false;
                }
            }

            if (string.Equals(commandId, RefreshStateCommandId, StringComparison.OrdinalIgnoreCase))
            {
                var detail = "Loader state refresh requested.";
                SetLast("Refresh Loader State", "READY", detail);
                OpenACAIEndnightRuntime.RequestLoaderStateRefresh("runtime-command-refresh-state");
                result = OpenACAICommandResultSnapshot.Success(detail);
                return true;
            }

            result = OpenACAICommandResultSnapshot.Failure("Unknown runtime command: " + commandId + ".");
            return false;
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

        private sealed class RuntimeCommandsProvider : IOpenACAICommandProvider
        {
            public string Id => ProviderId;
            public string DisplayName => "Runtime Commands";
            public string Kind => OpenACAIMenuProviderKinds.CommandGroup;
            public int Priority => 330;
            public string Source => "Modstoke Loader";
            public string Description => "Run safe OpenACAI runtime refresh actions.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;
            public IReadOnlyList<OpenACAICommandSnapshot> GetCommands() => SnapshotCommands();
            public bool TryInvokeCommand(string commandId, out OpenACAICommandResultSnapshot result) => OpenACAIRuntimeCommands.TryInvokeCommand(commandId, out result);
        }
    }
}
