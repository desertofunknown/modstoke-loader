// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned runtime preferences exposed through the structured config
// provider model. This gives the SDK/menu path a real editable provider without
// depending on BepInEx or RedLoader preferences.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Modstoke.Loader;

namespace Modstoke.Loader
{
    internal static class RuntimePreferences
    {
        private const string ProviderId = "openacai.loader.runtimePreferences";
        private const string SettingsFileName = "openacai-loader.settings.cfg";
        private const string ModsMenuHotkeyEnabledKey = "modsMenuHotkeyEnabled";
        private const string BackHotkeyEnabledKey = "backHotkeyEnabled";

        private static readonly object SyncRoot = new object();
        private static bool _modsMenuHotkeyEnabled = true;
        private static bool _backHotkeyEnabled = true;

        internal static string SettingsPath => Path.Combine(LoaderPaths.UserDataDirectory, SettingsFileName);

        internal static void Initialize()
        {
            lock (SyncRoot)
            {
                LoadNoLock(out _);
                ApplyNoLock();
            }

            LoaderConfig.RegisterOrUpdateProvider(new RuntimePreferencesProvider());
        }

        internal static bool Reload(out string detail)
        {
            lock (SyncRoot)
            {
                if (!LoadNoLock(out var error))
                {
                    detail = "Runtime preferences were not reloaded: " + error;
                    return false;
                }

                ApplyNoLock();
            }

            detail = "Runtime preferences reloaded from " + SettingsPath + ".";
            return true;
        }

        private static IReadOnlyList<ConfigValueSnapshot> SnapshotSettings()
        {
            lock (SyncRoot)
            {
                return new[]
                {
                    new ConfigValueSnapshot(
                        "Input",
                        ModsMenuHotkeyEnabledKey,
                        "Mods Menu Hotkey",
                        ConfigValueKinds.Boolean,
                        BoolString(_modsMenuHotkeyEnabled),
                        "true",
                        "Allow the configured shortcut to open or close the Modstoke Mods menu.",
                        true,
                        false),
                    new ConfigValueSnapshot(
                        "Input",
                        BackHotkeyEnabledKey,
                        "Back Hotkey",
                        ConfigValueKinds.Boolean,
                        BoolString(_backHotkeyEnabled),
                        "true",
                        "Allow the configured shortcut to return from Modstoke menu detail views.",
                        true,
                        false),
                    new ConfigValueSnapshot(
                        "Runtime",
                        "loaderVersion",
                        "Loader Version",
                        ConfigValueKinds.Text,
                        RuntimeEntryPoint.Version,
                        RuntimeEntryPoint.Version,
                        "Current Modstoke Loader runtime version.",
                        false,
                        false)
                };
            }
        }

        private static bool TrySetValue(string key, string value, out string status)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                status = "Setting key is required.";
                return false;
            }

            var isModsMenu = string.Equals(key, ModsMenuHotkeyEnabledKey, StringComparison.OrdinalIgnoreCase);
            var isBack = string.Equals(key, BackHotkeyEnabledKey, StringComparison.OrdinalIgnoreCase);
            if (!isModsMenu && !isBack)
            {
                status = "Unknown runtime preference: " + key + ".";
                return false;
            }

            var displayName = isModsMenu ? "Mods Menu Hotkey" : "Back Hotkey";
            if (!LoaderConfig.TryParseBoolean(value, out var parsed))
            {
                status = displayName + " expects true or false.";
                return false;
            }

            lock (SyncRoot)
            {
                try
                {
                    var original = ReadSettingsText();
                    var text = original ?? "# Modstoke Loader runtime preferences" + Environment.NewLine
                        + ModsMenuHotkeyEnabledKey + " = true" + Environment.NewLine
                        + BackHotkeyEnabledKey + " = true" + Environment.NewLine;
                    var settings = LoaderConfig.ParseSettings(text);
                    ReadValues(settings, out var modsMenuEnabled, out var backEnabled);
                    var settingKey = isModsMenu ? ModsMenuHotkeyEnabledKey : BackHotkeyEnabledKey;
                    var hasSetting = false;
                    var edits = new List<ConfigSettingEdit>();
                    foreach (var setting in settings)
                    {
                        if (!string.Equals(setting.Key, settingKey, StringComparison.OrdinalIgnoreCase))
                            continue;
                        hasSetting = true;
                        if (!LoaderConfig.TryParseBoolean(setting.Value, out var current) || current != parsed)
                            edits.Add(new ConfigSettingEdit(setting.LineIndex, BoolString(parsed)));
                    }

                    var updated = text;
                    if (edits.Count > 0)
                        updated = LoaderConfig.ApplySettings(text, edits);
                    else if (!hasSetting)
                    {
                        var separator = text.Length == 0 || text.EndsWith("\n", StringComparison.Ordinal)
                            || text.EndsWith("\r", StringComparison.Ordinal) ? "" : Environment.NewLine;
                        updated = text + separator + settingKey + " = " + BoolString(parsed) + Environment.NewLine;
                    }

                    if (!string.Equals(original, updated, StringComparison.Ordinal))
                        SaveNoLock(original, updated);
                    // Commit in-memory preferences only after persistence succeeds.
                    _modsMenuHotkeyEnabled = isModsMenu ? parsed : modsMenuEnabled;
                    _backHotkeyEnabled = isBack ? parsed : backEnabled;
                }
                catch (Exception ex)
                {
                    status = displayName + " was not saved: " + ex.Message;
                    Warn(status);
                    return false;
                }

                ApplyNoLock();
            }

            status = displayName + " saved as " + BoolString(parsed) + ".";
            return true;
        }

        private static bool LoadNoLock(out string error)
        {
            try
            {
                ReadValues(LoaderConfig.ParseSettings(ReadSettingsText() ?? ""),
                    out var modsMenuEnabled, out var backEnabled);
                _modsMenuHotkeyEnabled = modsMenuEnabled;
                _backHotkeyEnabled = backEnabled;
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Warn("Modstoke runtime preferences load failed; the file was left unchanged: " + ex.Message);
                return false;
            }
        }

        private static string ReadSettingsText()
        {
            try { return File.ReadAllText(SettingsPath); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private static void ReadValues(IEnumerable<ConfigSettingSnapshot> settings,
            out bool modsMenuEnabled, out bool backEnabled)
        {
            modsMenuEnabled = true;
            backEnabled = true;
            foreach (var setting in settings)
            {
                if (!LoaderConfig.TryParseBoolean(setting.Value, out var parsed))
                    continue;
                if (string.Equals(setting.Key, ModsMenuHotkeyEnabledKey, StringComparison.OrdinalIgnoreCase))
                    modsMenuEnabled = parsed;
                else if (string.Equals(setting.Key, BackHotkeyEnabledKey, StringComparison.OrdinalIgnoreCase))
                    backEnabled = parsed;
            }
        }

        private static void SaveNoLock(string original, string updated)
        {
            Directory.CreateDirectory(LoaderPaths.UserDataDirectory);
            var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true))
                    {
                        writer.Write(updated);
                    }
                    stream.Flush(flushToDisk: true);
                }

                if (!string.Equals(ReadSettingsText(), original, StringComparison.Ordinal))
                    throw new IOException("The settings file changed on disk. Retry the edit.");
                File.Move(temporary, SettingsPath, overwrite: original != null);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception ex) { Warn("Could not remove the temporary preferences file: " + ex.Message); }
            }
        }

        private static void Warn(string message)
        {
            try { RuntimeEntryPoint.LogSource?.LogWarning(message); }
            catch { /* A logging listener cannot turn a preference failure into a startup failure. */ }
        }

        private static void ApplyNoLock()
        {
            InputBindings.SetEnabled(KeybindIds.ToggleModsMenu, _modsMenuHotkeyEnabled);
            InputBindings.SetEnabled(KeybindIds.Back, _backHotkeyEnabled);
        }

        private static string BoolString(bool value)
        {
            return value ? "true" : "false";
        }

        private sealed class RuntimePreferencesProvider : IConfigProvider
        {
            public string Id => ProviderId;
            public string DisplayName => "Runtime Preferences";
            public string Source => "Modstoke Loader";
            public int Priority => 310;
            public bool Enabled => true;
            public IReadOnlyList<ConfigValueSnapshot> GetSettings() => SnapshotSettings();
            public bool TrySetValue(string key, string value, out string status) => RuntimePreferences.TrySetValue(key, value, out status);
        }
    }
}
