// SPDX-License-Identifier: GPL-3.0-or-later
// Input icon fallback adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using global::Sons.Gui.Input;
using SonsSdk;
using SUI;
using UnityEngine;

namespace Modstoke.Loader;

internal sealed class InputIconFallback : ISdkInputIconFallbackDriver
{
    private static readonly InputIconFallback Driver = new();

    internal static ISdkInputIconFallbackDriver Create() => Driver;

    public IconAssetDatabase IconDatabase => InputIcons.Instance.IconDatabase;

    public GameObject Get(string path, out string displayName, out bool isDefaultPrefab)
    {
        return InputIcons.Instance.Get(path, out displayName, out isDefaultPrefab);
    }

    public GameObject Add(string path, Transform transform)
    {
        return InputIcons.Instance.Add(path, transform);
    }

    public void RetireIcon(GameObject icon, GameObject template)
    {
        InputIcons.RetireIcon(icon, template);
    }

    public GameObject CreateKeybindIcon(SKeybindOptions options)
    {
        var replacementIcon = InputIcons.Instance.PrepareKeybindIcon(
            options._keybindConfig.GetAction(), options.DynamicInputIcon, out var defaultInstance, out var activeIcon);
        if (!InputIcons.IsLive(replacementIcon))
            return null;

        var previous = options._instance;
        options._instance = replacementIcon;
        InputIcons.RetireIcon(activeIcon, defaultInstance);
        if (InputIcons.IsLive(previous) &&
            (!InputIcons.IsLive(activeIcon) || previous != activeIcon))
            InputIcons.RetireIcon(previous, defaultInstance);
        return replacementIcon;
    }
}
