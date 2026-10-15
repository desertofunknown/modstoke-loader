// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned runtime preferences exposed through the structured config
// provider model. This gives the SDK/menu path a real editable provider without
// depending on BepInEx or RedLoader preferences.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenACAI.Loader;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenACAIRuntimePreferences
    {
        private const string ProviderId = "openacai.loader.runtimePreferences";
        private const string SettingsFileName = "openacai-loader.settings.cfg";
        private const string ModsMenuHotkeyEnabledKey = "modsMenuHotkeyEnabled";
        private const string BackHotkeyEnabledKey = "backHotkeyEnabled";

        private static readonly object SyncRoot = new object();
        private static bool _modsMenuHotkeyEnabled = true;
        private static bool _backHotkeyEnabled = true;

        internal static string SettingsPath => Path.Combine(OpenACAIPaths.UserDataDirectory, SettingsFileName);

        internal static void Initialize()
        {
            lock (SyncRoot)
            {
                LoadNoLock();
                ApplyNoLock();
            }

            OpenACAIConfig.RegisterOrUpdateProvider(new RuntimePreferencesProvider());
        }

        private static IReadOnlyList<OpenACAIConfigValueSnapshot> SnapshotSettings()
        {
            lock (SyncRoot)
            {
                return new[]
                {
                    new OpenACAIConfigValueSnapshot(
                        "Input",
                        ModsMenuHotkeyEnabledKey,
                        "Mods Menu Hotkey",
                        OpenACAIConfigValueKinds.Boolean,
                        BoolString(_modsMenuHotkeyEnabled),
                        "true",
                        "Allow the configured shortcut to open or close the Modstoke Mods menu.",
                        true,
                        false),
                    new OpenACAIConfigValueSnapshot(
                        "Input",
                        BackHotkeyEnabledKey,
                        "Back Hotkey",
                        OpenACAIConfigValueKinds.Boolean,
                        BoolString(_backHotkeyEnabled),
                        "true",
                        "Allow the configured shortcut to return from OpenACAI menu detail views.",
                        true,
                        false),
                    new OpenACAIConfigValueSnapshot(
                        "Runtime",
                        "loaderVersion",
                        "Loader Version",
                        OpenACAIConfigValueKinds.Text,
                        OpenACAIEndnightRuntime.Version,
                        OpenACAIEndnightRuntime.Version,
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
            if (!TryParseBoolean(value, out var parsed))
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
                    ReadValues(text, out var modsMenuEnabled, out var backEnabled);
                    var settingKey = isModsMenu ? ModsMenuHotkeyEnabledKey : BackHotkeyEnabledKey;
                    var edits = OpenACAIConfig.ParseSettings(text)
                        .Where(setting => string.Equals(setting.Key, settingKey, StringComparison.OrdinalIgnoreCase))
                        .Select(setting => new OpenACAIConfigSettingEdit(setting.LineIndex, BoolString(parsed)))
                        .ToArray();
                    var updated = edits.Length > 0
                        ? OpenACAIConfig.ApplySettings(text, edits)
                        : text + (text.Length == 0 || text.EndsWith("\n", StringComparison.Ordinal)
                            || text.EndsWith("\r", StringComparison.Ordinal) ? "" : Environment.NewLine)
                            + settingKey + " = " + BoolString(parsed) + Environment.NewLine;

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

        private static void LoadNoLock()
        {
            try
            {
                ReadValues(ReadSettingsText() ?? "", out _modsMenuHotkeyEnabled, out _backHotkeyEnabled);
            }
            catch (Exception ex)
            {
                Warn("OpenACAI runtime preferences load failed; the file was left unchanged: " + ex.Message);
            }
        }

        private static string ReadSettingsText()
        {
            try { return File.ReadAllText(SettingsPath); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private static void ReadValues(string text, out bool modsMenuEnabled, out bool backEnabled)
        {
            modsMenuEnabled = true;
            backEnabled = true;
            foreach (var setting in OpenACAIConfig.ParseSettings(text))
            {
                if (!TryParseBoolean(setting.Value, out var parsed))
                    continue;
                if (string.Equals(setting.Key, ModsMenuHotkeyEnabledKey, StringComparison.OrdinalIgnoreCase))
                    modsMenuEnabled = parsed;
                else if (string.Equals(setting.Key, BackHotkeyEnabledKey, StringComparison.OrdinalIgnoreCase))
                    backEnabled = parsed;
            }
        }

        private static void SaveNoLock(string original, string updated)
        {
            Directory.CreateDirectory(OpenACAIPaths.UserDataDirectory);
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
            try { OpenACAIEndnightRuntime.LogSource?.LogWarning(message); }
            catch { /* A logging listener cannot turn a preference failure into a startup failure. */ }
        }

        private static void ApplyNoLock()
        {
            OpenACAIInput.SetEnabled(OpenACAIKeybindIds.ToggleModsMenu, _modsMenuHotkeyEnabled);
            OpenACAIInput.SetEnabled(OpenACAIKeybindIds.Back, _backHotkeyEnabled);
        }

        private static bool TryParseBoolean(string value, out bool result)
        {
            result = false;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var normalized = value.Trim();
            if (string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "enabled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "on", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase))
            {
                result = true;
                return true;
            }

            if (string.Equals(normalized, "false", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "disabled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "off", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "no", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "0", StringComparison.OrdinalIgnoreCase))
            {
                result = false;
                return true;
            }

            return false;
        }

        private static string BoolString(bool value)
        {
            return value ? "true" : "false";
        }

        private sealed class RuntimePreferencesProvider : IOpenACAIConfigProvider
        {
            public string Id => ProviderId;
            public string DisplayName => "Runtime Preferences";
            public string Source => "Modstoke Loader";
            public int Priority => 310;
            public bool Enabled => true;
            public IReadOnlyList<OpenACAIConfigValueSnapshot> GetSettings() => SnapshotSettings();
            public bool TrySetValue(string key, string value, out string status) => OpenACAIRuntimePreferences.TrySetValue(key, value, out status);
        }
    }
}
