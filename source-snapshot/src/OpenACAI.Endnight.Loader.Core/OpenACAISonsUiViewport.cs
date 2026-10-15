// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using RedLoader;
using SonsSdk;
using SUI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Sui = SUI.SUI;

namespace OpenACAI.Endnight.Loader
{
    internal sealed class OpenACAISonsUiViewport : ISdkUiViewportDriver
    {
        internal static readonly OpenACAISonsUiViewport Instance = new();

        private OpenACAISonsUiViewport() { }

        public void Create()
        {
            var createdObjects = new List<GameObject>(2);
            try
            {
                var root = new GameObject("SUICanvas");
                createdObjects.Add(root);
                var canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;

                var canvasScaler = root.AddComponent<CanvasScaler>();
                canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                canvasScaler.referenceResolution = new Vector2(1920, 1080);
                canvasScaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                canvasScaler.matchWidthOrHeight = 1;
                canvasScaler.referencePixelsPerUnit = 100;

                var raycaster = root.AddComponent<GraphicRaycaster>();
                var eventSystem = new GameObject("EventSystem");
                createdObjects.Add(eventSystem);
                eventSystem.transform.SetParent(root.transform);
                eventSystem.SetActive(false);
                eventSystem.AddComponent<EventSystem>();
                eventSystem.AddComponent<StandaloneInputModule>();
                eventSystem.AddComponent<BaseInput>();

                root.DontDestroyOnLoad();
                Sui.PublishViewport(canvas, raycaster, eventSystem);
            }
            catch
            {
                DiscardCreatedObjects(createdObjects);
                throw;
            }
        }

        public void SetEventSystemActive(bool active)
        {
            if (Sui._eventSystemObject)
                Sui._eventSystemObject.SetActive(active);
        }

        private static void DiscardCreatedObjects(List<GameObject> createdObjects)
        {
            for (var i = createdObjects.Count - 1; i >= 0; i--)
            {
                var gameObject = createdObjects[i];
                try { gameObject.SetActive(false); }
                catch (Exception exception) { ReportCleanupFailure(exception); }

                try { UnityEngine.Object.Destroy(gameObject); }
                catch (Exception exception) { ReportCleanupFailure(exception); }
            }
        }

        private static void ReportCleanupFailure(Exception exception)
        {
            try { RLog.Warning("Failed to clean up an incomplete SUI viewport: " + exception); }
            catch { /* Keep the original construction failure. */ }
        }
    }
}
