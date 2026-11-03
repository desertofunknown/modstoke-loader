using RedLoader;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SonsSdk;

internal interface ISdkGlobalInputDriver
{
    bool RegisterKey(KeyCode key, Action action);
    void UnregisterKey(KeyCode key);
    void UnregisterKey(KeyCode key, Action action);
    void Initialize();
    void OnGameActivated();
    void OnUpdate();
}

public class GlobalInput
{
    public static MelonEvent OnUsePerformed = new();

    
    internal static readonly List<KeyRegistration> _keyRegistrations = new();
    internal static KeyRegistration[] _keySnapshot;

    internal static InputAction _useAction;

    private static readonly Lazy<ISdkGlobalInputDriver> FallbackDriver = new(() =>
    {
        var type = Assembly.Load("Modstoke.Loader")
            .GetType("Modstoke.Loader.GlobalInput", true);
        var method = type.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(type.FullName, "Create");
        return ((Func<ISdkGlobalInputDriver>)method.CreateDelegate(typeof(Func<ISdkGlobalInputDriver>)))();
    });
    private static ISdkGlobalInputDriver Fallback => FallbackDriver.Value;

    /// <summary>
    /// Register a key that will be polled every frame. The action will be invoke if the key is pressed during that frame.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="action"></param>
    /// <returns>False if the key is already registered</returns>
    public static bool RegisterKey(KeyCode key, Action action)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.GlobalInput.RegisterKey(key, action);

        return Fallback.RegisterKey(key, action);
    }
    
    public static void UnregisterKey(KeyCode key)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.GlobalInput.UnregisterKey(key);
            return;
        }

        Fallback.UnregisterKey(key);
    }

    internal static void UnregisterKey(KeyCode key, Action action)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.GlobalInput.UnregisterKey(key, action);
            return;
        }

        Fallback.UnregisterKey(key, action);
    }

    internal static void Init()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.GlobalInput.Initialize();
            return;
        }

        Fallback.Initialize();
    }

    internal static void OnGameActivated()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.GlobalInput.OnGameActivated();
            return;
        }

        Fallback.OnGameActivated();
    }

    internal static void OnUpdate()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.GlobalInput.OnUpdate();
            return;
        }

        Fallback.OnUpdate();
    }

    internal static void ReportFailure(string operation, Exception exception)
    {
        try
        {
            RLog.Error("Global input " + operation + " failed.", exception);
        }
        catch
        {
            // A log listener must not interrupt the remaining input dispatch.
        }
    }

    internal struct KeyRegistration
    {
        public KeyCode Key;
        public Action Action;
    }
}
