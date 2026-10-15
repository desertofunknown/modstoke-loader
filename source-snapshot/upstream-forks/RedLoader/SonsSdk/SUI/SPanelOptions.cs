using RedLoader;
using SonsSdk;
using UnityEngine;

namespace SUI;

public class SPanelOptions : SContainerOptions
{
    public string Id { get; internal set; }

    private bool _closeOnKeyRelease;
    private KeyCode? _toggleKey;
    private Action _toggleAction;
    private ModKeybind _boundKeybind;
    
    public SPanelOptions(GameObject root) : base(root)
    {
    }
    
    public SPanelOptions BindKeyConfig(KeybindConfigEntry keybind, bool closeOnKeyRelease = false)
    {
        var nextKeybind = keybind.GetKeybind();
        _boundKeybind?.RemoveNotify(KeyPressed, KeyReleased);
        _closeOnKeyRelease = closeOnKeyRelease;
        _boundKeybind = nextKeybind;
        _boundKeybind.Notify(KeyPressed, KeyReleased);
        return this;
    }

    internal void RegisterToggleKey(KeyCode key)
    {
        Action toggle = () =>
        {
            if (Root && ReferenceEquals(SUI.GetPanel(Id), this))
                Toggle();
        };

        if (!GlobalInput.RegisterKey(key, toggle))
        {
            RLog.Error("Key already registered: " + key);
            return;
        }

        _toggleKey = key;
        _toggleAction = toggle;
    }

    internal void ReleaseInputBindings()
    {
        var key = _toggleKey;
        var action = _toggleAction;
        var keybind = _boundKeybind;
        _toggleKey = null;
        _toggleAction = null;
        _boundKeybind = null;

        if (key.HasValue)
            GlobalInput.UnregisterKey(key.Value, action);
        keybind?.RemoveNotify(KeyPressed, KeyReleased);
    }

    private void KeyPressed()
    {
        if (_boundKeybind == null || !Root)
            return;

        if (_closeOnKeyRelease)
        {
            Active(true);
            return;
        }

        Toggle();
    }

    private void KeyReleased()
    {
        if (_boundKeybind == null || !Root)
            return;

        if(_closeOnKeyRelease)
            Active(false);
    }
}
