// SPDX-License-Identifier: GPL-3.0-or-later
// Compatibility shared UI handling adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using System.Collections.Generic;
using RedLoader;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Sui = SUI.SUI;

namespace Modstoke.Loader;

internal sealed partial class UiFallback
{
    public void CreateViewport() => UiViewport.Instance.Create();

    public void SetEventSystemActive(bool active)
    {
        UiViewport.Instance.SetEventSystemActive(active);
    }

    public IEnumerable<GameObject> GetHoveredUiObjects(GraphicRaycaster raycaster)
    {
        var eventSystem = EventSystem.current;
        if (!raycaster || !eventSystem)
            yield break;

        var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
        raycaster.Raycast(new PointerEventData(eventSystem)
        {
            position = Input.mousePosition
        }, results);
        foreach (RaycastResult raycastResult in results)
        {
            var gameObject = raycastResult.gameObject;
            if (gameObject)
                yield return gameObject;
        }
    }

    public void OnEventSystemEnabled(ref int activeEnablers)
    {
        activeEnablers++;
        RLog.Msg("Enabling event system");
        Sui.SetEventSystemActive(true);
    }

    public void OnEventSystemDisabled(ref int activeEnablers)
    {
        activeEnablers--;
        if(activeEnablers == 0)
        {
            RLog.Msg("No more active enablers, disabling event system");
            Sui.SetEventSystemActive(false);
        }
    }
}
