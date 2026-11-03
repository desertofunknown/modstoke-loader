using System.Reflection;
using System.Drawing;
using Bolt;
using RedLoader;
using RedLoader.Utils;
using Sons.Multiplayer;

namespace SonsSdk.Networking;

public static class NetUtils
{
    private static readonly Lazy<ISdkServerCommandDriver> FallbackDriver = new(() =>
    {
        var type = Assembly.Load("Modstoke.Loader")
            .GetType("Modstoke.Loader.ServerCommandFallback", true);
        var method = type.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(type.FullName, "Create");
        return ((Func<ISdkServerCommandDriver>)method.CreateDelegate(typeof(Func<ISdkServerCommandDriver>)))();
    });
    internal static ISdkServerCommandDriver Fallback => FallbackDriver.Value;

    public static bool IsMultiplayer => BoltNetwork.isRunning;
    public static bool IsServer => BoltNetwork.isServer;
    public static bool IsClient => BoltNetwork.isClient;
    public static bool IsDedicatedServer => LoaderEnvironment.IsDedicatedServer;
    
    /// <summary>
    /// Send a chat message to everyone or a specific connection. THIS IS ONLY POSSIBLE IF YOU ARE A SERVER (either dedicated server or host in coop).
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="message"></param>
    /// <param name="color"></param>
    /// <param name="receiver"></param>
    public static void SendChatMessage(NetworkId sender, string message, string color = null, BoltConnection receiver = null)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.ServerCommands.SendChatMessage(sender, message, color, receiver);
            return;
        }

        Fallback.SendChatMessage(sender, message, color, receiver);
    }

    public static bool HasUserPermission(ulong steamId, PlayerRoles minRequiredRole)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.ServerCommands.HasUserPermission(steamId, minRequiredRole);

        return Fallback.HasUserPermission(steamId, minRequiredRole);
    }
}
