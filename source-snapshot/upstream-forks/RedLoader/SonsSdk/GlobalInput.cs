using RedLoader;
using Sons.Input;
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

        if(_keyRegistrations.Any(k => k.Key == key))
            return false;
        
        _keyRegistrations.Add(new KeyRegistration
        {
            Key = key,
            Action = action
        });
        _keySnapshot = null;

        return true;
    }
    
    public static void UnregisterKey(KeyCode key)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.GlobalInput.UnregisterKey(key);
            return;
        }

        if (_keyRegistrations.RemoveAll(k => k.Key == key) > 0)
            _keySnapshot = null;
    }

    internal static void UnregisterKey(KeyCode key, Action action)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.GlobalInput.UnregisterKey(key, action);
            return;
        }

        if (_keyRegistrations.RemoveAll(k => k.Key == key && ReferenceEquals(k.Action, action)) > 0)
            _keySnapshot = null;
    }

    internal static void Init()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.GlobalInput.Initialize();
            return;
        }

        GlobalEvents.OnUpdate.Subscribe(OnUpdate);
        SdkEvents.OnGameActivated.Subscribe(OnGameActivated);
    }

    internal static void OnGameActivated()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.GlobalInput.OnGameActivated();
            return;
        }

        _useAction = Sons.Input.InputSystem.InputMapping.@default.Use;
    }

    internal static void OnUpdate()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.GlobalInput.OnUpdate();
            return;
        }

        // Callback mutations affect the next dispatch, not this snapshot.
        var registrations = _keySnapshot ??= _keyRegistrations.ToArray();
        foreach (var keyRegistration in registrations)
        {
            try
            {
                if (Input.GetKeyDown(keyRegistration.Key))
                    keyRegistration.Action();
            }
            catch (Exception exception)
            {
                ReportFailure("key " + keyRegistration.Key, exception);
            }
        }

        try
        {
            if (_useAction != null && _useAction.WasPerformedThisFrame())
                OnUsePerformed.Invoke();
        }
        catch (Exception exception)
        {
            ReportFailure("Use action", exception);
        }
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
