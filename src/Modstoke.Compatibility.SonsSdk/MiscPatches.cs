using System.Reflection;
using Harmony;
using Sons.Multiplayer.Dedicated;

namespace SonsSdk;

internal interface ISdkBootstrapFallbackDriver
{
    void Initialize();
    void DoServerPatches();
    void SonsLaunchStart(SonsLaunch instance);
    void BeforeServerCreation(DedicatedServerBoostrap instance);
    void AlterServerName(ref string serverName);
    void PatchMaxPlayerLimit();
}

internal class MiscPatches
{
    internal static ConfiguredPatcher<MiscPatches> _patcher;

    private static readonly Lazy<ISdkBootstrapFallbackDriver> FallbackDriver = new(() =>
    {
        var type = Assembly.Load("Modstoke.Loader")
            .GetType("Modstoke.Loader.SdkBootstrapFallback", true);
        var method = type.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(type.FullName, "Create");
        return ((Func<ISdkBootstrapFallbackDriver>)method.CreateDelegate(typeof(Func<ISdkBootstrapFallbackDriver>)))();
    });
    internal static ISdkBootstrapFallbackDriver Fallback => FallbackDriver.Value;
    
    internal static void Init()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.InitializeBootstrapPatches();
            return;
        }

        Fallback.Initialize();
    }

    internal static void DoServerPatches()
    {
        Fallback.DoServerPatches();
    }

    internal static void SonsLaunchStart(SonsLaunch __instance)
    {
        Fallback.SonsLaunchStart(__instance);
    }

    internal static void BeforeServerCreation(DedicatedServerBoostrap __instance)
    {
        Fallback.BeforeServerCreation(__instance);
    }
    
    internal static void AlterServerName(ref string pszServerName)
    {
        Fallback.AlterServerName(ref pszServerName);
    }
    
    public static unsafe void PatchMaxPlayerLimit()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.PatchMaxPlayerLimit();
            return;
        }

        Fallback.PatchMaxPlayerLimit();
    }
}
