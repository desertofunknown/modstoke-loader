// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using RedLoader;
using SonsSdk;
using SonsSdk.Private;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OpenACAI.Endnight.Loader
{
    internal sealed class OpenACAISonsSdkOverlay : ISdkOverlayDriver
    {
        internal static readonly OpenACAISonsSdkOverlay Instance = new();

        private OpenACAISonsSdkOverlay() { }

        public GameObject CreateBlackScreen()
        {
            var createdObjects = new List<GameObject>(5);
            try
            {
                var root = new GameObject("BlackScreenCanvas");
                createdObjects.Add(root);
                root.DontDestroyOnLoad().HideAndDontSave();
                root.layer = 5;

                var canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 999;

                var background = CreateChild("BlackScreen", canvas.transform, createdObjects);
                var rect = background.AddComponent<RectTransform>();
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = rect.offsetMax = Vector2.zero;

                var text = background.Instantiate();
                createdObjects.Add(text);
                text.SetParent(background.transform);
                var backgroundImage = background.AddComponent<Image>();
                backgroundImage.color = Color.black;
                var overlayText = text.AddComponent<TextMeshProUGUI>();
                overlayText.fontSize = 30;
                overlayText.color = System.Drawing.Color.PaleVioletRed.ToUnityColor();
                overlayText.alignment = TextAlignmentOptions.Center;
                var progressBar = CreateProgressbar(root.GetComponent<RectTransform>(), createdObjects);

                // Publish only complete overlays; failed attempts keep the previous references.
                GlobalOverlays.BlackScreenImage = backgroundImage;
                GlobalOverlays.OverlayText = overlayText;
                GlobalOverlays.ProgressBar = progressBar;
                GlobalOverlays.OverlayContainer = root;
                return root;
            }
            catch
            {
                DiscardCreatedObjects(createdObjects);
                throw;
            }
        }

        public void ToggleOverlay(bool enable)
        {
            if (!GlobalOverlays.OverlayContainer)
                return;

            GlobalOverlays.OverlayContainer.SetActive(enable);
        }

        public void SetProgressbarOffset(float y)
        {
            if (GlobalOverlays.ProgressBar == null)
                return;

            GlobalOverlays.ProgressBar.ProgressbarBackground.anchoredPosition = new Vector2(0, y);
        }

        public void ToggleBackground(bool enable)
        {
            if (!GlobalOverlays.BlackScreenImage)
                return;

            GlobalOverlays.BlackScreenImage.enabled = enable;
        }

        public void SetProgress(RectTransform foreground, float progress)
        {
            foreground.anchorMax = new Vector2(progress, 1);
        }

        private static GlobalOverlays.ProgressBarContainer CreateProgressbar(RectTransform container, List<GameObject> createdObjects)
        {
            var background = CreateChild("ProgressBarBackground", container, createdObjects);
            var backgroundRect = background.AddComponent<RectTransform>();
            backgroundRect.anchorMin = new Vector2(0.1f, 0f);
            backgroundRect.anchorMax = new Vector2(0.9f, 0f);
            backgroundRect.sizeDelta = new Vector2(0, 30);
            backgroundRect.pivot = new Vector2(0.5f, 0);
            backgroundRect.anchoredPosition = new Vector2(0, 200);
            var backgroundImage = background.AddComponent<Image>();
            backgroundImage.color = Color.grey;

            var foreground = CreateChild("ProgressBarForeground", background.transform, createdObjects);
            var foregroundRect = foreground.AddComponent<RectTransform>();
            foregroundRect.anchorMin = Vector2.zero;
            foregroundRect.anchorMax = new Vector2(0, 1);
            foregroundRect.sizeDelta = Vector2.zero;
            foregroundRect.pivot = new Vector2(0, 0.5f);
            var foregroundImage = foreground.AddComponent<Image>();
            foregroundImage.color = new Color(0.735849f, 0.1087575f, 0.3132702f);

            return new GlobalOverlays.ProgressBarContainer
            {
                ProgressbarBackground = backgroundRect,
                ProgressbarBackgroundImage = backgroundImage,
                ProgressbarForeground = foregroundRect,
                ProgressbarForegroundImage = foregroundImage
            };
        }

        private static GameObject CreateChild(string name, Transform parent, List<GameObject> createdObjects)
        {
            var child = new GameObject(name);
            createdObjects.Add(child);
            child.transform.SetParent(parent, false);
            return child;
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
            try { RLog.Warning("Failed to clean up an incomplete startup overlay: " + exception); }
            catch { /* Keep the original construction failure. */ }
        }
    }
}
