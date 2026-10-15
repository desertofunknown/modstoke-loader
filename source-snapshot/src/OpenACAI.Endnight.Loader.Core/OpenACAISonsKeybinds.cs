// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using RedLoader;
using RedLoader.Preferences;
using SonsSdk;

namespace OpenACAI.Endnight.Loader
{
    internal sealed class OpenACAISonsKeybinds : ISdkKeybindDriver
    {
        internal static readonly OpenACAISonsKeybinds Instance = new();

        private OpenACAISonsKeybinds() { }

        public ModKeybind GetKeybind(KeybindConfigEntry config)
        {
            if (ModInputCache.Keybinds.TryGetValue(config, out var keybind))
                return keybind;

            keybind = new ModKeybind(config);
            ModInputCache.Keybinds[config] = keybind;
            // Refresh the action before ordinary config listeners read its binding.
            config.OnValueChanged.Subscribe(keybind.OnConfigValueChanged, priority: int.MinValue);
            ModInputCache._keybindSnapshot = null;
            try { RLog.Msg("Registered keybind: " + config.Identifier + "::" + config.FullPath); }
            catch { /* Logging must not prevent the registered action from being enabled. */ }
            keybind.Enable();
            return keybind;
        }

        public void CheckAll()
        {
            // Registrations made by callbacks join the next update, not this dispatch.
            var keybinds = ModInputCache._keybindSnapshot ??= ModInputCache.Keybinds.Values.ToArray();
            foreach (var keybind in keybinds)
            {
                try
                {
                    Check(keybind);
                }
                catch (Exception exception)
                {
                    keybind.ReportFailure("poll", exception);
                }
            }
        }

        public void Check(ModKeybind keybind)
        {
            if (keybind.CustomCheck != null && !keybind.CustomCheck())
                return;

            if (keybind.NeedsInGame && !GameState.IsInGame)
                return;

            if (keybind.IgnoreInConsole && GameState.IsInConsole)
                return;

            if (keybind.NeedsPlayerControllable && !GameState.IsPlayerControllable)
                return;

            if (keybind.Action.triggered)
                InvokeCallbacks(keybind, keybind.PerformedCallbacks, "performed");

            if (keybind.Action.WasReleasedThisFrame())
                InvokeCallbacks(keybind, keybind.ReleasedCallbacks, "released");
        }

        private static void InvokeCallbacks(ModKeybind keybind, Action callbacks, string phase)
        {
            if (callbacks == null)
                return;

            foreach (Action callback in callbacks.GetInvocationList())
            {
                try
                {
                    callback();
                }
                catch (Exception exception)
                {
                    keybind.ReportFailure(phase, exception);
                }
            }
        }
    }
}
