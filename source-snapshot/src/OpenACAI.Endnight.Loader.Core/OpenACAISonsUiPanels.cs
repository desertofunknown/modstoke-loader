// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Sons.Input;
using SUI;
using TheForest.Utils;
using UnityEngine;
using Sui = SUI.SUI;

namespace OpenACAI.Endnight.Loader
{
    internal sealed class OpenACAISonsUiPanels : ISdkUiPanelDriver
    {
        internal static readonly OpenACAISonsUiPanels Instance = new();

        private OpenACAISonsUiPanels() { }

        public SPanelOptions Register(string id, Transform parent, bool enableInput, KeyCode? toggleKey)
        {
            if (Sui._panels.ContainsKey(id))
                throw new ArgumentException("A panel with this ID is already registered.", nameof(id));

            var panel = Sui.CreatePanel(parent);
            panel.Id = id;
            panel.Root.name = id;
            Sui._panels[id] = panel;

            if (enableInput)
            {
                var cursor = panel.Root.AddComponent<InputCursorState>();
                cursor._enabled = true;
                cursor._hardwareCursor = true;
                cursor._priority = 100;

                var actionMap = panel.Root.AddComponent<InputActionMapState>();
                actionMap._applyState = InputState.Console;
                panel.Root.AddComponent<Sui.EventSystemEnabler>();
            }

            if (toggleKey.HasValue)
                panel.RegisterToggleKey(toggleKey.Value);

            return panel;
        }

        public bool Remove(string id)
        {
            var panel = Sui.GetPanel(id);
            if (panel == null)
                return false;

            Sui._panels.Remove(id);
            panel.Remove();
            return true;
        }
    }
}
