// SPDX-License-Identifier: GPL-3.0-or-later
// Linked UI behavior adapted from SonsSdk's SonsUiTools.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Linq;
using RedLoader;
using global::Sons.Gui;
using global::Sons.Gui.Input;
using SonsSdk;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Modstoke.Loader;

internal static class LinkUi
{
    internal static void Register(SonsUiTools.CustomUiElement element)
    {
        if (SonsUiTools.CustomLinkElements.Any(existing => existing.Id == element.Id))
            return;

        element.Container.Root.DontDestroyOnLoad();
        element.Container.Active(false);
        SonsUiTools.CustomLinkElements.Add(element);
    }

    internal static void AddLinkUi(GameObject gameObject, string id, float distance, bool refresh)
    {
        var wasActive = gameObject.activeSelf;
        LinkUiElement linkUi = null;
        try
        {
            if (refresh)
                gameObject.SetActive(false);
            linkUi = gameObject.AddComponent<LinkUiElement>();
            linkUi._maxDistance = distance;
            linkUi.SetId(id, refresh);
            if (refresh)
                gameObject.SetActive(wasActive);
        }
        catch
        {
            // Remove the partial component before restoring the caller's object.
            try { if (linkUi) Object.DestroyImmediate(linkUi); }
            catch (Exception exception) { ReportCleanupFailure(exception); }

            if (refresh)
            {
                try { if (gameObject) gameObject.SetActive(wasActive); }
                catch (Exception exception) { ReportCleanupFailure(exception); }
            }
            throw;
        }
    }

    internal static Transform CreateLinkUi(Transform parent, string id, float distance, bool refresh)
    {
        var gameObject = new GameObject("InteractionUi");
        try
        {
            gameObject.transform.SetParent(parent, false);
            AddLinkUi(gameObject, id, distance, refresh);
            return gameObject.transform;
        }
        catch
        {
            DiscardCreatedObject(gameObject);
            throw;
        }
    }

    internal static void Initialize() => SdkEvents.OnGameActivated.Subscribe(SonsUiTools.BeforeLoadSave);

    internal static void BeforeLoadSave()
    {
        if (SonsUiTools.CustomLinkElements.Count == 0)
            return;

        var manager = UiManager._instance;
        // Activation can repeat on the same manager after a partial failure.
        var registeredIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var registered in manager._elements)
        {
            if (registered != null)
                registeredIds.Add(registered._id);
        }

        UiElement prefab3d = null;
        UiElement prefab2d = null;
        List<Exception> failures = null;
        foreach (var element in SonsUiTools.CustomLinkElements.ToArray())
        {
            if (registeredIds.Contains(element.Id))
                continue;

            GameObject target = null;
            try
            {
                if (element.Container == null || !element.Container.Root)
                    throw new InvalidOperationException("The linked UI template has been destroyed.");

                var prefab = element.Is2d
                    ? (prefab2d ??= manager.FindElement("construction.Place", null))
                    : (prefab3d ??= manager.FindElement("screen.use", null));
                if (prefab == null || !prefab._target)
                    throw new InvalidOperationException("The native linked UI template is unavailable.");

                var uiElement = new UiElement(prefab);
                uiElement._id = element.Id;
                uiElement._idHash = element.IdHash;
                uiElement._transformType = element.TransformType;
                uiElement._maxPooled = element.MaxPooled;
                target = Object.Instantiate(element.Container.Root, prefab._target.transform.parent, false);
                uiElement._target = target;
                uiElement._rootParent = prefab._rootParent;
                if (element.Is2d)
                    uiElement._tempParent = prefab._tempParent;
                manager._elements.Add(uiElement);
                registeredIds.Add(element.Id);
            }
            catch (Exception exception)
            {
                DiscardCreatedObject(target);
                (failures ??= new List<Exception>()).Add(
                    new InvalidOperationException("Linked UI '" + element.Id + "' failed to initialize.", exception));
            }
        }

        if (failures != null)
            throw new AggregateException("One or more linked UI elements failed to initialize.", failures);
    }

    private static void DiscardCreatedObject(GameObject gameObject)
    {
        if (!gameObject)
            return;

        try { gameObject.SetActive(false); }
        catch (Exception exception) { ReportCleanupFailure(exception); }
        try { Object.Destroy(gameObject); }
        catch (Exception exception) { ReportCleanupFailure(exception); }
    }

    private static void ReportCleanupFailure(Exception exception)
    {
        try { RLog.Warning("Failed to clean up an incomplete linked UI: " + exception); }
        catch { /* Preserve the original setup failure. */ }
    }
}
