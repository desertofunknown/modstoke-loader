// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using RedLoader;
using global::Sons.Input;
using SUI;
using TheForest.Utils;
using UnityEngine;
using Sui = SUI.SUI;

namespace Modstoke.Loader
{
    internal sealed class UiPanels : ISdkUiPanelDriver
    {
        internal static readonly UiPanels Instance = new();

        private UiPanels() { }

        public SPanelOptions Register(string id, Transform parent, bool enableInput, KeyCode? toggleKey)
        {
            if (Find(id) != null)
                throw new ArgumentException("A panel with this ID is already registered.", nameof(id));

            if (!parent)
            {
                if (!Sui.SUIViewport)
                    throw new InvalidOperationException("The SUI viewport is not available.");
                parent = Sui.SUIViewport.transform;
            }

            GameObject root = null;
            SPanelOptions panel = null;
            try
            {
                root = new GameObject(id);
                root.SetActive(false);
                root.AddComponent<RectTransform>();
                panel = new SPanelOptions(root);
                panel.Id = id;
                panel.Anchor(AnchorType.Fill);
                panel.SetParent(parent);

                if (enableInput)
                {
                    var cursor = root.AddComponent<InputCursorState>();
                    cursor._enabled = true;
                    cursor._hardwareCursor = true;
                    cursor._priority = 100;

                    var actionMap = root.AddComponent<InputActionMapState>();
                    actionMap._applyState = InputState.Console;
                    root.AddComponent<Sui.EventSystemEnabler>();
                }

                if (toggleKey.HasValue)
                    panel.RegisterToggleKey(toggleKey.Value);

                Sui._panels.Add(id, panel);
                root.SetActive(true);
                return panel;
            }
            catch
            {
                try { if (panel != null) Release(panel); }
                catch (Exception exception) { ReportCleanupFailure(exception); }
                try { if (root) UnityEngine.Object.Destroy(root); }
                catch (Exception exception) { ReportCleanupFailure(exception); }
                throw;
            }
        }

        public bool Remove(string id)
        {
            if (!Sui._panels.TryGetValue(id, out var panel))
                return false;

            try { Release(panel); }
            finally { UnityEngine.Object.Destroy(panel.Root); }
            return true;
        }

        internal static SPanelOptions Find(string id)
        {
            if (!Sui._panels.TryGetValue(id, out var panel))
                return null;
            if (panel.Root)
                return panel;

            Release(panel);
            return null;
        }

        internal static void Release(SPanelOptions panel)
        {
            if (panel.Id != null && Sui._panels.TryGetValue(panel.Id, out var registered) && ReferenceEquals(registered, panel))
                Sui._panels.Remove(panel.Id);

            try
            {
                panel.UnbindVisibility();
                panel.ReleaseInputBindings();
            }
            finally
            {
                if (panel.Root)
                    panel.Root.SetActive(false);
            }
        }

        private static void ReportCleanupFailure(Exception exception)
        {
            try { RLog.Warning("Failed to clean up an incomplete SUI panel: " + exception); }
            catch { /* Preserve the construction failure. */ }
        }
    }
}
