// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using Modstoke.Loader;
using RedLoader;
using RedLoader.Preferences;
using SonsSdk;

namespace Modstoke.Loader
{
    internal sealed class Keybinds : ISdkKeybindDriver
    {
        internal static readonly Keybinds Instance = new();

        private Keybinds() { }

        public ModKeybind GetKeybind(KeybindConfigEntry config)
        {
            ArgumentNullException.ThrowIfNull(config);
            if (ModInputCache.Keybinds.TryGetValue(config, out var keybind))
                return keybind;

            keybind = new ModKeybind(config);
            ModInputCache.Keybinds[config] = keybind;
            ModInputCache._keybindSnapshot = null;
            try
            {
                // Refresh the action before ordinary config listeners read its binding.
                config.OnValueChanged.Subscribe(keybind.OnConfigValueChanged, priority: int.MinValue);
                keybind.Enable();
            }
            catch
            {
                if (ModInputCache.Keybinds.TryGetValue(config, out var current) && ReferenceEquals(current, keybind))
                    ModInputCache.Keybinds.Remove(config);
                ModInputCache._keybindSnapshot = null;
                try { config.OnValueChanged.Unsubscribe(keybind.OnConfigValueChanged); }
                catch (Exception exception) { keybind.ReportFailure("registration unsubscribe", exception); }
                try { keybind.Action.Dispose(); }
                catch (Exception exception) { keybind.ReportFailure("registration dispose", exception); }
                throw;
            }
            try { RLog.Msg("Registered keybind: " + config.Identifier + "::" + config.FullPath); }
            catch { /* Logging must not prevent the registered action from being enabled. */ }
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
            if (InputBindings.HasFocus || !keybind.Action.enabled)
                return;

            var customCheck = keybind.CustomCheck;
            if (customCheck != null)
            {
                using var capture = PerformanceCapture.MeasureCallback(customCheck, "input-check");
                if (!customCheck()) return;
            }

            if (!CanInvoke(keybind))
                return;

            if (keybind.Action.triggered)
                InvokeCallbacks(keybind, keybind.PerformedCallbacks, "performed");

            if (CanInvoke(keybind) && keybind.Action.WasReleasedThisFrame())
                InvokeCallbacks(keybind, keybind.ReleasedCallbacks, "released");
        }

        private static bool CanInvoke(ModKeybind keybind) =>
            !InputBindings.HasFocus && keybind.Action.enabled &&
            (!keybind.NeedsInGame || GameState.IsInGame) &&
            (!keybind.IgnoreInConsole || !GameState.IsInConsole) &&
            (!keybind.NeedsPlayerControllable || GameState.IsPlayerControllable);

        private static void InvokeCallbacks(ModKeybind keybind, Action callbacks, string phase)
        {
            if (callbacks == null)
                return;

            foreach (var callback in Delegate.EnumerateInvocationList(callbacks))
            {
                if (!CanInvoke(keybind))
                    return;

                var current = phase == "performed" ? keybind.PerformedCallbacks : keybind.ReleasedCallbacks;
                if (current == null)
                    return;
                if (!ReferenceEquals(current, callbacks) && !ContainsCallback(current, callback))
                    continue;

                try
                {
                    using var capture = PerformanceCapture.MeasureCallback(callback, "input");
                    callback();
                }
                catch (Exception exception)
                {
                    keybind.ReportFailure(phase, exception);
                }
            }
        }

        private static bool ContainsCallback(Action callbacks, Action expected)
        {
            foreach (var callback in Delegate.EnumerateInvocationList(callbacks))
                if (callback == expected)
                    return true;
            return false;
        }
    }
}
