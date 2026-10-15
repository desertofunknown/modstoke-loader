using Bolt;
using RedLoader;
using RedLoader.Utils;
using Sons.Characters;
using Sons.Gameplay;
using Sons.Items.Core;
using Sons.Multiplayer;
using Sons.Prefabs;
using SonsSdk;
using SUI;
using UnityEngine;
using Color = System.Drawing.Color;

namespace SonsSdk.Networking;

internal interface ISdkServerCommandDriver
{
    void RegisterCommand(string command, PlayerRoles permission, Func<ServerCommands.CommandArgs, ServerCommands.EExecutionResult> action);
    ServerCommands.EExecutionResult TryExecuteCommand(string command, bool fromUser, ulong steamId);
    bool HandleChatEvent(ChatEvent chatEvent);
    string ExecutionResultToText(ServerCommands.EExecutionResult result);
    void SendChatMessage(NetworkId sender, string message, string color, BoltConnection receiver);
    bool HasUserPermission(ulong steamId, PlayerRoles minRequiredRole);
}

public static class ServerCommands
{
    public static Dictionary<string, Command> Commands = new();

    static ServerCommands()
    {
        Packets.OnChatReceived += HandleChatEvent;
    }
    
    public static void RegisterCommand(string command, PlayerRoles permission, Func<CommandArgs, EExecutionResult> action)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.ServerCommands.RegisterCommand(command, permission, action);
            return;
        }

        Commands[command.ToLower()] = new(){Action = action, Name = command, Permission = permission};
    }
    
    internal static EExecutionResult TryExecuteCommand(string command, bool fromUser, ulong steamId)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.ServerCommands.TryExecuteCommand(command, fromUser, steamId);

        var split = command.Split(' ');
        var cmd = split[0].Substring(1).ToLower();
        if (Commands.TryGetValue(cmd, out var commandInstance))
        {
            if(fromUser && !commandInstance.CanExecute(steamId))
                return EExecutionResult.NoPermission;

            RLog.Msg(Color.GreenYellow, $"Executing server command {commandInstance.Name}");
            
            return commandInstance.Action(new(command, steamId));
        }

        return EExecutionResult.InvalidCommand;
    }

    internal static bool HandleChatEvent(ChatEvent ev)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.ServerCommands.HandleChatEvent(ev);

        var message = ev.Message;
        if (message.StartsWith("/"))
        {
            var steamId = MultiplayerUtilities.GetSteamId(ev.RaisedBy);
            var userName = "Unknown";
            if (MultiplayerPlayerRoles._instance.TryGetData(steamId, out var playerData))
            {
                userName = playerData.GetPlayerName();
            }
        
            RLog.Msg(Color.Gray, $"Command received from {userName}: {message}");

            var result = TryExecuteCommand(message, true, steamId);
            NetUtils.SendChatMessage(ev.Sender, ExecutionResultToText(result), "blue", ev.RaisedBy);

            return true;
        }

        return false;
    }
    
    public static string ExecutionResultToText(EExecutionResult result)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.ServerCommands.ExecutionResultToText(result);

        return result switch
        {
            EExecutionResult.Success => "Command executed successfully",
            EExecutionResult.NoPermission => "You don't have permission to execute this command",
            EExecutionResult.InvalidCommand => "Invalid command",
            EExecutionResult.CommandFailed => "Command failed",
            EExecutionResult.WrongArguments => "Wrong arguments",
            _ => "Unknown result"
        };
    }

    public enum EExecutionResult
    {
        Success,
        NoPermission,
        InvalidCommand,
        CommandFailed,
        WrongArguments
    }
    
    public struct Command
    {
        public string Name;
        public PlayerRoles Permission;
        public Func<CommandArgs, EExecutionResult> Action;
        
        public bool CanExecute(ulong steamId)
        {
            return NetUtils.HasUserPermission(steamId, Permission);
        }
    }
    
    public struct CommandArgs
    {
        public string Command;
        public string[] Args;
        public ulong SteamId;
        
        public CommandArgs(string str, ulong steamId)
        {
            var split = str.Split(' ');
            Command = split[0];
            Args = split.Skip(1).ToArray();
            SteamId = steamId;
        }
        
        public string ArgsAsString() => string.Join(' ', Args);
        
        public bool HasArgs => Args.Length > 0;
        
        public bool HasNumArgs(int count) => Args.Length == count;
        
        public bool HasMinArgs(int count) => Args.Length >= count;

        public bool TryGet(int index, out string arg)
        {
            if (index < 0 || index >= Args.Length)
            {
                arg = null;
                return false;
            }

            arg = Args[index];
            return true;
        }
        
        public bool TryGetInt(int index, out int arg)
        {
            if (index < 0 || index >= Args.Length)
            {
                arg = 0;
                return false;
            }

            return int.TryParse(Args[index], out arg);
        }
        
        public bool TryGetFloat(int index, out float arg)
        {
            if (index < 0 || index >= Args.Length)
            {
                arg = 0;
                return false;
            }

            return float.TryParse(Args[index], out arg);
        }
        
        public bool TryGetBool(int index, out bool arg)
        {
            if (index < 0 || index >= Args.Length)
            {
                arg = false;
                return false;
            }

            return bool.TryParse(Args[index], out arg);
        }
    }
}
