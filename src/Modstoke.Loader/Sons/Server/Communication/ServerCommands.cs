// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Bolt;
using RedLoader;
using global::Sons.Multiplayer;
using SonsSdk.Networking;
using Color = System.Drawing.Color;
using Commands = SonsSdk.Networking.ServerCommands;
using ExecutionResult = SonsSdk.Networking.ServerCommands.EExecutionResult;

namespace Modstoke.Loader
{
    internal sealed class ServerCommands : ISdkServerCommandDriver
    {
        internal static readonly global::Modstoke.Loader.ServerCommands Instance = new();
        private int _reportedFailure;

        private ServerCommands() { }

        public void RegisterCommand(string command, global::Sons.Multiplayer.PlayerRoles permission, Func<global::SonsSdk.Networking.ServerCommands.CommandArgs, ExecutionResult> action)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(command);
            ArgumentNullException.ThrowIfNull(action);
            if (!Enum.IsDefined(permission))
                throw new ArgumentOutOfRangeException(nameof(permission));
            foreach (var character in command)
                if (char.IsWhiteSpace(character) || char.IsControl(character))
                    throw new ArgumentException("Command names cannot contain whitespace or control characters.", nameof(command));
            var key = command.ToLowerInvariant();
            // SonsSdk replaces an existing registration; keep that, but make the takeover visible.
            if (global::SonsSdk.Networking.ServerCommands.Commands.TryGetValue(key, out var previous))
                RuntimeEntryPoint.LogSource?.LogWarning("Server command /" + key + " was registered again; "
                    + previous.Permission + " permission replaced by " + permission + ".");
            global::SonsSdk.Networking.ServerCommands.Commands[key] = new()
            {
                Action = action,
                Name = command,
                Permission = permission
            };
        }

        public ExecutionResult TryExecuteCommand(string command, bool fromUser, ulong steamId)
        {
            if (string.IsNullOrEmpty(command) || command.Length == 1 || command[0] != '/')
                return ExecutionResult.InvalidCommand;

            try
            {
                // CommandArgs retains the compatibility API's space-only splitting and empty arguments.
                var end = command.IndexOf(' ');
                if (end < 0)
                    end = command.Length;
                if (end == 1)
                    return ExecutionResult.InvalidCommand;
                var name = command.Substring(1, end - 1).ToLowerInvariant();
                var commands = global::SonsSdk.Networking.ServerCommands.Commands;
                if (commands == null || !commands.TryGetValue(name, out var registration))
                    return ExecutionResult.InvalidCommand;
                if (fromUser && !registration.CanExecute(steamId))
                    return ExecutionResult.NoPermission;
                if (registration.Action == null)
                    return ExecutionResult.CommandFailed;

                RLog.Msg(Color.GreenYellow, $"Executing server command {registration.Name}");
                var result = registration.Action(new(command, steamId));
                return Enum.IsDefined(result) ? result : ExecutionResult.CommandFailed;
            }
            catch (Exception error)
            {
                ReportFailure("execution", error);
                return ExecutionResult.CommandFailed;
            }
        }

        public bool HandleChatEvent(ChatEvent chatEvent)
        {
            try
            {
                var message = chatEvent?.Message;
                if (string.IsNullOrEmpty(message) || message[0] != '/')
                    return false;
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return true;

                var connection = chatEvent.RaisedBy;
                if (connection == null)
                    return true;

                var steamId = MultiplayerUtilities.GetSteamId(connection);
                var result = TryExecuteCommand(message, true, steamId);
                SendChatMessage(chatEvent.Sender, ExecutionResultToText(result), "blue", connection);
            }
            catch (Exception error)
            {
                // A failed reply must not let the command fall through to ordinary chat or run again.
                ReportFailure("chat handling", error);
            }
            return true;
        }

        public string ExecutionResultToText(ExecutionResult result)
        {
            return result switch
            {
                ExecutionResult.Success => "Command executed successfully",
                ExecutionResult.NoPermission => "You don't have permission to execute this command",
                ExecutionResult.InvalidCommand => "Invalid command",
                ExecutionResult.CommandFailed => "Command failed",
                ExecutionResult.WrongArguments => "Wrong arguments",
                _ => "Unknown result"
            };
        }

        public void SendChatMessage(NetworkId sender, string message, string color, BoltConnection receiver)
        {
            if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
            {
                RLog.Msg("Chat messages can only be sent by the server in a multiplayer game");
                return;
            }

            ChatEvent chat = null;
            var sendAttempted = false;
            try
            {
                chat = receiver != null ? ChatEvent.Create(receiver) : ChatEvent.Create(GlobalTargets.AllClients);
                if (!string.IsNullOrEmpty(color))
                    message = $"<color={color}>{message}</color>";

                chat.Message = message;
                chat.Sender = sender;
                sendAttempted = true;
                chat.Send();
            }
            finally
            {
                // Once Send starts, native ownership is uncertain even if it throws.
                if (chat != null && !sendAttempted)
                {
                    try { chat.FreeStorage(); }
                    catch (Exception error) { ReportFailure("chat cleanup", error); }
                }
            }
        }

        public bool HasUserPermission(ulong steamId, global::Sons.Multiplayer.PlayerRoles minRequiredRole)
        {
            // The native role lookup defaults absent entries to Trusted, including an unresolved ID.
            if (steamId == 0)
                return false;
            try
            {
                var roles = MultiplayerPlayerRoles._instance;
                if (roles is null || roles.WasCollected || !roles)
                    return false;
                var role = roles.GetRoleValue(steamId);
                return role != global::Sons.Multiplayer.PlayerRoles.Banned && (int)role >= (int)minRequiredRole;
            }
            catch (Exception error)
            {
                ReportFailure("permission lookup", error);
                return false;
            }
        }

        private void ReportFailure(string operation, Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server command " + operation + " failed ("
                    + error.GetType().FullName + "). Further command failures will not be logged.");
            }
            catch (Exception) { }
        }
    }
}
