using RedLoader;
using RedLoader.Preferences;
using UnityEngine.InputSystem;

namespace SonsSdk;

internal interface ISdkKeybindDriver
{
    ModKeybind GetKeybind(KeybindConfigEntry config);
    void CheckAll();
    void Check(ModKeybind keybind);
}

public static class ModInputCache
{
    internal static readonly Dictionary<KeybindConfigEntry, ModKeybind> Keybinds = new();
    internal static ModKeybind[] _keybindSnapshot;

    public static ModKeybind GetKeybind(this KeybindConfigEntry config)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.Keybinds.GetKeybind(config);

        if (Keybinds.TryGetValue(config, out var action))
        {
            return action;
        }

        Keybinds[config] = action = new ModKeybind(config);
        // Refresh the action before ordinary config listeners read its binding.
        config.OnValueChanged.Subscribe(action.OnConfigValueChanged, priority: int.MinValue);
        _keybindSnapshot = null;
        try { RLog.Msg("Registered keybind: " + config.Identifier + "::" + config.FullPath); }
        catch { /* Logging must not prevent the registered action from being enabled. */ }
        action.Enable();
        return action;
    }
    
    public static InputAction GetAction(this KeybindConfigEntry config)
    {
        return GetKeybind(config).Action;
    }
    
    /// <summary>
    /// Set when the action can trigger. If <paramref name="needsPlayerControllable"/> is true, set the other two options to false as they are automatically checked.
    /// </summary>
    /// <param name="needsInGame">Does the player need to be in a world</param>
    /// <param name="ignoreInConsole">Don't trigger the action in the console</param>
    /// <param name="needsPlayerControllable">Don't trigger the action in the console, book, cutscene etc. (uses LocalPlayer.IsInWorld)</param>
    public static void SetScope(this KeybindConfigEntry config, bool needsInGame = false, bool ignoreInConsole = false, bool needsPlayerControllable = false)
    {
        var keybind = GetKeybind(config);
        if (keybind == null)
        {
            return;
        }
        
        keybind.SetScope(needsInGame, ignoreInConsole, needsPlayerControllable);
    }
    
    /// <summary>
    /// Registers callbacks for the performed and released events of the input action.
    /// </summary>
    public static void Notify(this KeybindConfigEntry config, Action performAction = null, Action releasedAction = null)
    {
        var keybind = GetKeybind(config);
        if (keybind == null)
        {
            return;
        }
        
        keybind.Notify(performAction, releasedAction);
    }

    /// <summary>
    /// Removes previously registered callbacks for the performed and released events of the input action.
    /// </summary>
    public static void RemoveNotify(this KeybindConfigEntry config, Action performAction = null, Action releasedAction = null)
    {
        var keybind = GetKeybind(config);
        if (keybind == null)
        {
            return;
        }
        
        keybind.RemoveNotify(performAction, releasedAction);
    }

    internal static void CheckAll()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Keybinds.CheckAll();
            return;
        }

        // Registrations made by callbacks join the next update, not this dispatch.
        var keybinds = _keybindSnapshot ??= Keybinds.Values.ToArray();
        foreach (var keybind in keybinds)
        {
            try
            {
                keybind.Check();
            }
            catch (Exception exception)
            {
                keybind.ReportFailure("poll", exception);
            }
        }
    }
}

public class ModKeybind
{
    public readonly InputAction Action;
    public readonly KeybindConfigEntry ConfigEntry;
    public Func<bool> CustomCheck;
    public bool IgnoreInConsole;
    public bool NeedsPlayerControllable;
    public bool NeedsInGame;
    
    private event Action OnPerformed;
    private event Action OnReleased;

    internal Action PerformedCallbacks => OnPerformed;
    internal Action ReleasedCallbacks => OnReleased;

    public ModKeybind(InputAction action, KeybindConfigEntry configEntry)
    {
        Action = action;
        ConfigEntry = configEntry;
    }
    
    public ModKeybind(KeybindConfigEntry configEntry)
    {
        ConfigEntry = configEntry;

        Action = new InputAction(configEntry.Identifier, binding: configEntry.FullPath);
    }

    /// <summary>
    /// Registers callbacks for the performed and released events of the input action.
    /// </summary>
    /// <param name="performedAction"></param>
    /// <param name="releasedAction"></param>
    public void Notify(Action performedAction = null, Action releasedAction = null)
    {
        if (performedAction != null)
        {
            OnPerformed += performedAction;
        }
        
        if (releasedAction != null)
        {
            OnReleased += releasedAction;
        }
    }

    /// <summary>
    /// Removes previously registered callbacks for the performed and released events of the input action.
    /// </summary>
    /// <param name="performedAction"></param>
    /// <param name="releasedAction"></param>
    public void RemoveNotify(Action performedAction = null, Action releasedAction = null)
    {
        if (performedAction != null)
        {
            OnPerformed -= performedAction;
        }
        
        if (releasedAction != null)
        {
            OnReleased -= releasedAction;
        }
    }

    public void RemoveAllCallbacks()
    {
        OnPerformed = null;
        OnReleased = null;
    }

    public void Enable()
    {
        Action.Enable();
    }
    
    public void Disable()
    {
        Action.Disable();
    }

    public void SaveToConfig()
    {
        var bindings = Action.bindings;
        if (bindings.Count == 0)
            return;

        var bindingIndex = 0;
        if (bindings.Count != 1)
        {
            var controls = Action.controls;
            if (controls.Count == 0)
                return;

            bindingIndex = Action.GetBindingIndexForControl(controls[0]);
            if (bindingIndex < 0)
                return;
        }

        if (!bindings[bindingIndex].isComposite)
            SaveBindingPath(bindings[bindingIndex].effectivePath);
    }

    internal void SaveBindingPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        const string keyboardPrefix = "<Keyboard>/";
        if (path.StartsWith(keyboardPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var keyName = path.Substring(keyboardPrefix.Length);
            if (!keyName.Contains('/'))
                path = keyName;
        }

        ConfigEntry.Value = path;
    }
    
    public void RevertToDefault()
    {
        ConfigEntry.ResetToDefault();
        RefreshBinding();
    }

    internal void OnConfigValueChanged(string oldValue, string newValue)
    {
        RefreshBinding();
    }

    private void RefreshBinding()
    {
        var path = ConfigEntry.FullPath;
        var bindings = Action.bindings;
        for (var i = 0; i < bindings.Count; i++)
        {
            if (string.Equals(bindings[i].effectivePath, path, StringComparison.Ordinal))
                continue;

            Action.ApplyBindingOverride(path);
            return;
        }
    }
    
    /// <summary>
    /// Set when the action can trigger. If <paramref name="needsPlayerControllable"/> is true, set the other two options to false as they are automatically checked.
    /// </summary>
    /// <param name="needsInGame">Does the player need to be in a world</param>
    /// <param name="ignoreInConsole">Don't trigger the action in the console</param>
    /// <param name="needsPlayerControllable">Don't trigger the action in the console, book, cutscene etc. (uses LocalPlayer.IsInWorld)</param>
    public void SetScope(bool needsInGame = false, bool ignoreInConsole = false, bool needsPlayerControllable = false)
    {
        NeedsInGame = needsInGame;
        IgnoreInConsole = ignoreInConsole;
        NeedsPlayerControllable = needsPlayerControllable;
    }

    internal void Check()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Keybinds.Check(this);
            return;
        }

        if (CustomCheck != null && !CustomCheck())
        {
            return;
        }
        
        if(NeedsInGame && !GameState.IsInGame)
            return;

        if (IgnoreInConsole && GameState.IsInConsole)
            return;
        
        if(NeedsPlayerControllable && !GameState.IsPlayerControllable)
            return;
        
        if (Action.triggered)
        {
            InvokeCallbacks(OnPerformed, "performed");
        }

        if (Action.WasReleasedThisFrame())
        {
            InvokeCallbacks(OnReleased, "released");
        }
    }

    private void InvokeCallbacks(Action callbacks, string phase)
    {
        if (callbacks == null)
        {
            return;
        }

        foreach (Action callback in callbacks.GetInvocationList())
        {
            try
            {
                callback();
            }
            catch (Exception exception)
            {
                ReportFailure(phase, exception);
            }
        }
    }

    internal void ReportFailure(string phase, Exception exception)
    {
        try
        {
            RLog.Error($"Keybind '{ConfigEntry?.Identifier}' ({ConfigEntry?.FullPath}) {phase} failed: {exception}");
        }
        catch
        {
            // A diagnostic listener must not interrupt the remaining input callbacks.
        }
    }
}
