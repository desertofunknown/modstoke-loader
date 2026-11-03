using System.Globalization;
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

        NetUtils.Fallback.RegisterCommand(command, permission, action);
    }
    
    internal static EExecutionResult TryExecuteCommand(string command, bool fromUser, ulong steamId)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.ServerCommands.TryExecuteCommand(command, fromUser, steamId);

        return NetUtils.Fallback.TryExecuteCommand(command, fromUser, steamId);
    }

    internal static bool HandleChatEvent(ChatEvent ev)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.ServerCommands.HandleChatEvent(ev);

        return NetUtils.Fallback.HandleChatEvent(ev);
    }
    
    public static string ExecutionResultToText(EExecutionResult result)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.ServerCommands.ExecutionResultToText(result);

        return NetUtils.Fallback.ExecutionResultToText(result);
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

            return int.TryParse(Args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out arg);
        }
        
        public bool TryGetFloat(int index, out float arg)
        {
            if (index < 0 || index >= Args.Length)
            {
                arg = 0;
                return false;
            }

            return float.TryParse(Args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out arg);
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
