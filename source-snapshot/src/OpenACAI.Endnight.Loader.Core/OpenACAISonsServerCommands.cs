// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Bolt;
using RedLoader;
using Sons.Multiplayer;
using SonsSdk.Networking;
using Color = System.Drawing.Color;
using Commands = SonsSdk.Networking.ServerCommands;
using ExecutionResult = SonsSdk.Networking.ServerCommands.EExecutionResult;

namespace OpenACAI.Endnight.Loader
{
    internal sealed class OpenACAISonsServerCommands : ISdkServerCommandDriver
    {
        internal static readonly OpenACAISonsServerCommands Instance = new();
        private int _reportedFailure;

        private OpenACAISonsServerCommands() { }

        public void RegisterCommand(string command, PlayerRoles permission, Func<Commands.CommandArgs, ExecutionResult> action)
        {
            Commands.Commands[command.ToLower()] = new()
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
                var split = command.Split(' ');
                var name = split[0].Substring(1).ToLower();
                var commands = Commands.Commands;
                if (name.Length == 0 || commands == null || !commands.TryGetValue(name, out var registration))
                    return ExecutionResult.InvalidCommand;
                if (fromUser && !registration.CanExecute(steamId))
                    return ExecutionResult.NoPermission;
                if (registration.Action == null)
                    return ExecutionResult.CommandFailed;

                RLog.Msg(Color.GreenYellow, $"Executing server command {registration.Name}");
                return registration.Action(new(command, steamId));
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

            var chat = receiver != null ? ChatEvent.Create(receiver) : ChatEvent.Create(GlobalTargets.AllClients);
            if (!string.IsNullOrEmpty(color))
                message = $"<color={color}>{message}</color>";

            chat.Message = message;
            chat.Sender = sender;
            chat.Send();
        }

        public bool HasUserPermission(ulong steamId, PlayerRoles minRequiredRole)
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
                return role != PlayerRoles.Banned && (int)role >= (int)minRequiredRole;
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
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Server command " + operation + " failed ("
                    + error.GetType().FullName + "). Further command failures will not be logged.");
            }
            catch (Exception) { }
        }
    }
}
