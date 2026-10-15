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

    internal static void Create()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.GameManagers.Create();
            return;
        }

        _instance = new GameManagers();
    }

    internal static void Init()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.GameManagers.Initialize();
            return;
        }

        SdkEvents.OnGameActivated.Subscribe(Create);
    }
    
    public static T GetManager<T>(string managerName) where T : Component
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.GameManagers.GetManager<T>(managerName);

        if (_instance == null)
        {
            return null;
        }
        
        if (!_instance._gameManagersTr)
            return null;
        
        var manager = _instance._gameManagersTr.Find(managerName);
        return manager ? manager.GetComponent<T>() : null;
    }

    public static CharacterManager GetCharacterManager() => GetManager<CharacterManager>("CharacterManager");
}
