using System.Reflection;
using Sons.Gui.Input;
using SUI;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SonsSdk;

internal interface ISdkInputIconDriver
{
    IconAssetDatabase IconDatabase { get; }
    GameObject Get(string path, out string displayName, out bool isDefaultPrefab);
    GameObject Add(string path, Transform parent);
    GameObject CreateKeybindIcon(InputAction action, DynamicInputIcon icon, GameObject previous);
}

internal interface ISdkInputIconFallbackDriver
{
    IconAssetDatabase IconDatabase { get; }
    GameObject Get(string path, out string displayName, out bool isDefaultPrefab);
    GameObject Add(string path, Transform transform);
    GameObject CreateKeybindIcon(SKeybindOptions options);
    void RetireIcon(GameObject icon, GameObject template);
}

public class InputIconManager
{
    private static readonly Lazy<ISdkInputIconFallbackDriver> FallbackDriver = new(() =>
    {
        var type = Assembly.Load("Modstoke.Loader")
            .GetType("Modstoke.Loader.InputIconFallback", true);
        var method = type.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(type.FullName, "Create");
        return ((Func<ISdkInputIconFallbackDriver>)method.CreateDelegate(typeof(Func<ISdkInputIconFallbackDriver>)))();
    });
    internal static ISdkInputIconFallbackDriver Fallback => FallbackDriver.Value;

    internal static IconAssetDatabase _iconDatabase;

    public static IconAssetDatabase IconDatabase
    {
        get
        {
            var driver = SdkEvents.BeginLifecycleSetup();
            if (driver != null)
                return driver.InputIcons.IconDatabase;

            return Fallback.IconDatabase;
        }
    }

    public static GameObject Get(InputAction inputAction, out string displayName, out bool isDefaultPrefab)
    {
        if (inputAction.controls.Count == 0)
        {
            displayName = null;
            isDefaultPrefab = false;
            return null;
        }
        
        var path = inputAction.controls[0].path;
        return Get(path, out displayName, out isDefaultPrefab);
    }
    
    public static GameObject Get(string path, out string displayName, out bool isDefaultPrefab)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.InputIcons.Get(path, out displayName, out isDefaultPrefab);

        return Fallback.Get(path, out displayName, out isDefaultPrefab);
    }

    public static GameObject AddInputIcon(string path, SUiElement element)
    {
        return AddInputIcon(path, element.Root.transform);
    }
    
    public static GameObject AddInputIcon(string path, Transform transform)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.InputIcons.Add(path, transform);

        return Fallback.Add(path, transform);
    }

    internal static void RetireIcon(GameObject icon, GameObject template = null)
    {
        Fallback.RetireIcon(icon, template);
    }

}
