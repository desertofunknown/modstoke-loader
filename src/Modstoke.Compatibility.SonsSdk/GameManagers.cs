using System.Reflection;
using Sons.Characters;
using TheForest;
using UnityEngine;

namespace SonsSdk;

internal interface ISdkGameManagerDriver
{
    void Initialize();
    void Create();
    T GetManager<T>(string managerName) where T : Component;
}

public class GameManagers
{
    internal static GameManagers _instance;
    internal readonly Transform _gameManagersTr;

    internal GameManagers() : this(DebugConsole.Instance.transform.parent)
    {
    }

    internal GameManagers(Transform gameManagersTransform)
    {
        _gameManagersTr = gameManagersTransform;
    }

    private static readonly Lazy<ISdkGameManagerDriver> FallbackDriver = new(() =>
    {
        var type = Assembly.Load("Modstoke.Loader")
            .GetType("Modstoke.Loader.SdkGameManagerFallback", true);
        var method = type.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(type.FullName, "Create");
        return ((Func<ISdkGameManagerDriver>)method.CreateDelegate(typeof(Func<ISdkGameManagerDriver>)))();
    });
    internal static ISdkGameManagerDriver Fallback => FallbackDriver.Value;

    internal static void Create()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.GameManagers.Create();
            return;
        }

        Fallback.Create();
    }

    internal static void Init()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.GameManagers.Initialize();
            return;
        }

        Fallback.Initialize();
    }
    
    public static T GetManager<T>(string managerName) where T : Component
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.GameManagers.GetManager<T>(managerName);

        return Fallback.GetManager<T>(managerName);
    }

    public static CharacterManager GetCharacterManager() => GetManager<CharacterManager>("CharacterManager");
}
