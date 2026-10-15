// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned mod config helpers. The UI can render native controls, but
// file validation and simple friendly parsing live in this service.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenACAI.Endnight.Loader;

namespace OpenACAI.Loader
{
    public static class OpenACAIConfig
    {
        public const string ServiceKey = "openacai.loader.config";
        internal const string MenuProviderIdPrefix = "openacai.config.";

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, IOpenACAIConfigProvider> Providers =
            new Dictionary<string, IOpenACAIConfigProvider>(StringComparer.OrdinalIgnoreCase);

        public static event Action<OpenACAIConfigProviderSnapshot> ProviderChanged;

        internal static void Initialize()
        {
            lock (SyncRoot)
            {
                PublishServiceNoLock();
            }
        }

        public static bool RegisterOrUpdateProvider(IOpenACAIConfigProvider provider)
        {
            var snapshot = OpenACAIConfigProviderSnapshot.FromProvider(provider);
            if (snapshot == null)
            {
                return false;
            }

            Action<OpenACAIConfigProviderSnapshot> handlers;
            lock (SyncRoot)
            {
                Providers[snapshot.Id] = provider;
                PublishServiceNoLock();
                handlers = ProviderChanged;
            }

            OpenACAIMenu.RegisterOrUpdateProvider(new OpenACAIConfigMenuProvider(snapshot.Id));
            DispatchProviderChanged(snapshot, handlers);
            return true;
        }

        public static bool TryUnregisterProvider(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            lock (SyncRoot)
            {
                if (!Providers.Remove(id.Trim()))
                {
                    return false;
                }

                PublishServiceNoLock();
            }

            OpenACAIMenu.TryUnregisterProvider(OpenACAIConfigMenuProvider.MenuProviderId(id));
            return true;
        }

        public static IReadOnlyList<OpenACAIConfigProviderSnapshot> SnapshotProviders()
        {
            lock (SyncRoot)
            {
                return SnapshotProvidersNoLock();
            }
        }

        public static bool TryGetProviderSnapshot(string id, out OpenACAIConfigProviderSnapshot snapshot)
        {
            snapshot = null;
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            lock (SyncRoot)
            {
                IOpenACAIConfigProvider provider;
                if (!Providers.TryGetValue(id.Trim(), out provider))
                {
                    return false;
                }

                snapshot = OpenACAIConfigProviderSnapshot.FromProvider(provider);
                return snapshot != null;
            }
        }

        public static bool TrySetRegisteredValue(string providerId, string key, string value, out string status)
        {
            status = null;
            if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(key))
            {
                status = "A provider id and setting key are required.";
                return false;
            }

            IOpenACAIConfigProvider provider;
            lock (SyncRoot)
            {
                Providers.TryGetValue(providerId.Trim(), out provider);
            }

            if (provider == null)
            {
                status = "Config provider was not registered.";
                return false;
            }

            var applied = provider.TrySetValue(key.Trim(), value, out status);
            lock (SyncRoot)
            {
                PublishServiceNoLock();
            }

            OpenACAIMenu.RegisterOrUpdateProvider(new OpenACAIConfigMenuProvider(providerId));
            return applied;
        }

        internal static bool TryGetProviderSnapshotFromMenuProvider(string menuProviderId, out OpenACAIConfigProviderSnapshot snapshot)
        {
            snapshot = null;
            string providerId;
            return TryGetProviderIdFromMenuProviderId(menuProviderId, out providerId)
                && TryGetProviderSnapshot(providerId, out snapshot);
        }

        internal static bool TrySetRegisteredValueFromMenuProvider(string menuProviderId, string key, string value, out string status)
        {
            string providerId;
            if (!TryGetProviderIdFromMenuProviderId(menuProviderId, out providerId))
            {
                status = "Menu provider is not backed by OpenACAIConfig.";
                return false;
            }

            return TrySetRegisteredValue(providerId, key, value, out status);
        }

        internal static bool TryGetProviderIdFromMenuProviderId(string menuProviderId, out string providerId)
        {
            providerId = null;
            if (string.IsNullOrWhiteSpace(menuProviderId))
            {
                return false;
            }

            var trimmed = menuProviderId.Trim();
            if (!trimmed.StartsWith(MenuProviderIdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            providerId = trimmed.Substring(MenuProviderIdPrefix.Length).Trim();
            return !string.IsNullOrWhiteSpace(providerId);
        }

        internal static OpenACAIConfigDocument OpenDocument(ModInventoryEntry entry, int maxEditorBytes)
        {
            if (entry == null)
            {
                return OpenACAIConfigDocument.CreateStatus("No mod selected.", null, null, null, false, false, false, null, null, Array.Empty<OpenACAIConfigSettingSnapshot>());
            }

            var status = entry.LiveSettingsAvailable
                ? "Live RedLoader settings are registered. File-backed settings are shown when available."
                : null;

            if (!entry.HasConfig || string.IsNullOrWhiteSpace(entry.ConfigPath))
            {
                return OpenACAIConfigDocument.CreateStatus(
                    string.IsNullOrWhiteSpace(status) ? "No config file detected for " + entry.DisplayName + "." : status,
                    entry.DisplayName,
                    entry.ConfigPath,
                    entry.ConfigFormat,
                    entry.HasConfig,
                    entry.LiveSettingsAvailable,
                    false,
                    null,
                    null,
                    Array.Empty<OpenACAIConfigSettingSnapshot>());
            }

            try
            {
                var info = new FileInfo(entry.ConfigPath);
                if (!info.Exists)
                {
                    return OpenACAIConfigDocument.CreateStatus("Config file no longer exists.", entry.DisplayName, entry.ConfigPath, entry.ConfigFormat, true, entry.LiveSettingsAvailable, false, null, null, Array.Empty<OpenACAIConfigSettingSnapshot>());
                }

                if (info.Length > maxEditorBytes)
                {
                    return OpenACAIConfigDocument.CreateStatus("Config file is too large for the in-game editor: " + info.Length + " bytes.", entry.DisplayName, entry.ConfigPath, entry.ConfigFormat, true, entry.LiveSettingsAvailable, false, null, null, Array.Empty<OpenACAIConfigSettingSnapshot>());
                }

                var text = File.ReadAllText(entry.ConfigPath);
                return OpenACAIConfigDocument.CreateLoaded(
                    entry.DisplayName,
                    entry.ConfigPath,
                    entry.ConfigFormat,
                    entry.ConfigSource,
                    entry.LiveSettingsAvailable,
                    text,
                    status,
                    ParseSettings(text));
            }
            catch (Exception ex)
            {
                return OpenACAIConfigDocument.CreateStatus("Failed to read config: " + ex.Message, entry.DisplayName, entry.ConfigPath, entry.ConfigFormat, true, entry.LiveSettingsAvailable, false, null, null, Array.Empty<OpenACAIConfigSettingSnapshot>());
            }
        }

        public static OpenACAIConfigSettingSnapshot[] ParseSettings(string text)
        {
            var lines = SplitLines(text);
            var settings = new List<OpenACAIConfigSettingSnapshot>();
            var comments = new List<string>();
            var section = "GENERAL";

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i] ?? "";
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    comments.Clear();
                    continue;
                }

                if (trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith(";", StringComparison.Ordinal))
                {
                    comments.Add(trimmed.TrimStart('#', ';', ' '));
                    continue;
                }

                if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.Contains("]"))
                {
                    section = trimmed.Trim('[', ']', ' ');
                    comments.Clear();
                    continue;
                }

                var equals = line.IndexOf('=');
                if (equals <= 0)
                {
                    comments.Clear();
                    continue;
                }

                var key = line.Substring(0, equals).Trim();
                var value = line.Substring(equals + 1).Trim();
                if (key.Length == 0)
                {
                    continue;
                }

                settings.Add(OpenACAIConfigSettingSnapshot.Create(
                    i,
                    section,
                    key,
                    value,
                    comments.Count == 0 ? null : string.Join(" ", comments.Where(comment => !string.IsNullOrWhiteSpace(comment)))));
                comments.Clear();
            }

            return settings.ToArray();
        }

        public static string ApplySettings(string text, IEnumerable<OpenACAIConfigSettingEdit> edits)
        {
            var lines = SplitLines(text);
            if (lines.Length == 0)
            {
                return "";
            }

            foreach (var edit in edits ?? Array.Empty<OpenACAIConfigSettingEdit>())
            {
                if (edit == null || edit.LineIndex < 0 || edit.LineIndex >= lines.Length)
                {
                    continue;
                }

                var line = lines[edit.LineIndex] ?? "";
                var equals = line.IndexOf('=');
                if (equals < 0)
                {
                    continue;
                }

                lines[edit.LineIndex] = line.Substring(0, equals + 1) + " " + (edit.Value ?? "");
            }

            return string.Join(Environment.NewLine, lines);
        }

        public static void SaveText(string configPath, string text)
        {
            if (string.IsNullOrWhiteSpace(configPath))
            {
                throw new InvalidOperationException("No config file is selected.");
            }

            File.WriteAllText(configPath, text ?? "");
        }

        internal static object CreateState()
        {
            lock (SyncRoot)
            {
                return CreateStateNoLock();
            }
        }

        private static object CreateStateNoLock()
        {
            var providerSnapshots = SnapshotProvidersNoLock();
            return new
            {
                schema = 1,
                supportsFriendlyKeyValueParsing = true,
                supportsRawTextEditing = true,
                supportsStructuredProviders = true,
                supportsRegisteredValueApply = true,
                serviceKey = ServiceKey,
                providerCount = providerSnapshots.Count,
                providers = providerSnapshots
            };
        }

        private static IReadOnlyList<OpenACAIConfigProviderSnapshot> SnapshotProvidersNoLock()
        {
            return Providers.Values
                .Select(OpenACAIConfigProviderSnapshot.FromProvider)
                .Where(snapshot => snapshot != null)
                .OrderBy(snapshot => snapshot.Priority)
                .ThenBy(snapshot => snapshot.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static void PublishServiceNoLock()
        {
            OpenACAIModding.SetService(ServiceKey, CreateStateNoLock());
        }

        private static void DispatchProviderChanged(OpenACAIConfigProviderSnapshot snapshot, Action<OpenACAIConfigProviderSnapshot> handlers)
        {
            try
            {
                handlers?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                OpenACAI.Endnight.Loader.OpenACAIEndnightRuntime.LogSource?.LogWarning("OpenACAI config provider event subscriber failed: " + ex.Message);
            }
        }

        private static string[] SplitLines(string text)
        {
            var normalized = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            return normalized.Split('\n');
        }

        private sealed class OpenACAIConfigMenuProvider : IOpenACAIMenuContentProvider
        {
            private readonly string _providerId;

            internal OpenACAIConfigMenuProvider(string providerId)
            {
                _providerId = providerId;
            }

            public string Id => MenuProviderId(_providerId);
            public string DisplayName
            {
                get
                {
                    OpenACAIConfigProviderSnapshot snapshot;
                    return TryGetProviderSnapshot(_providerId, out snapshot) ? snapshot.DisplayName : _providerId;
                }
            }

            public string Kind => OpenACAIMenuProviderKinds.Settings;
            public int Priority
            {
                get
                {
                    OpenACAIConfigProviderSnapshot snapshot;
                    return TryGetProviderSnapshot(_providerId, out snapshot) ? snapshot.Priority : 500;
                }
            }

            public string Source
            {
                get
                {
                    OpenACAIConfigProviderSnapshot snapshot;
                    return TryGetProviderSnapshot(_providerId, out snapshot) ? snapshot.Source : "OpenACAI Config";
                }
            }

            public string Description
            {
                get
                {
                    OpenACAIConfigProviderSnapshot snapshot;
                    return TryGetProviderSnapshot(_providerId, out snapshot)
                        ? "Structured OpenACAI settings: " + snapshot.SettingCount + " setting(s)."
                        : "Structured OpenACAI settings.";
                }
            }

            public bool RequiresAuthorization => false;
            public bool Enabled
            {
                get
                {
                    OpenACAIConfigProviderSnapshot snapshot;
                    return !TryGetProviderSnapshot(_providerId, out snapshot) || snapshot.Enabled;
                }
            }

            internal static string MenuProviderId(string providerId)
            {
                return MenuProviderIdPrefix + (providerId ?? "").Trim();
            }

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                OpenACAIConfigProviderSnapshot snapshot;
                if (!TryGetProviderSnapshot(_providerId, out snapshot))
                {
                    return new OpenACAIMenuContentSnapshot(
                        _providerId,
                        "Config provider is no longer registered.",
                        new[]
                        {
                            new OpenACAIMenuSectionSnapshot(
                                "Status",
                                new[] { new OpenACAIMenuRowSnapshot("Provider", _providerId, "MISSING", "The provider was not found.") })
                        });
                }

                var sections = snapshot.Settings
                    .GroupBy(setting => string.IsNullOrWhiteSpace(setting.Section) ? "Settings" : setting.Section)
                    .Select(group => new OpenACAIMenuSectionSnapshot(
                        group.Key,
                        group.Select(setting => new OpenACAIMenuRowSnapshot(
                            setting.DisplayName,
                            setting.Value,
                            setting.CanEdit ? setting.Kind : "READONLY",
                            setting.Tooltip))))
                    .ToArray();

                return new OpenACAIMenuContentSnapshot(
                    snapshot.DisplayName,
                    snapshot.Source + " | " + snapshot.SettingCount + " structured setting(s)",
                    sections);
            }
        }
    }

    public interface IOpenACAIConfigProvider
    {
        string Id { get; }
        string DisplayName { get; }
        string Source { get; }
        int Priority { get; }
        bool Enabled { get; }
        IReadOnlyList<OpenACAIConfigValueSnapshot> GetSettings();
        bool TrySetValue(string key, string value, out string status);
    }

    public sealed class OpenACAIConfigProviderSnapshot
    {
        private OpenACAIConfigProviderSnapshot(
            string id,
            string displayName,
            string source,
            int priority,
            bool enabled,
            IReadOnlyList<OpenACAIConfigValueSnapshot> settings,
            string updatedUtc)
        {
            Id = id;
            DisplayName = displayName;
            Source = source;
            Priority = priority;
            Enabled = enabled;
            Settings = settings;
            SettingCount = settings == null ? 0 : settings.Count;
            UpdatedUtc = updatedUtc;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Source { get; }
        public int Priority { get; }
        public bool Enabled { get; }
        public int SettingCount { get; }
        public IReadOnlyList<OpenACAIConfigValueSnapshot> Settings { get; }
        public string UpdatedUtc { get; }

        internal static OpenACAIConfigProviderSnapshot FromProvider(IOpenACAIConfigProvider provider)
        {
            if (provider == null || string.IsNullOrWhiteSpace(provider.Id))
            {
                return null;
            }

            var id = provider.Id.Trim();
            IReadOnlyList<OpenACAIConfigValueSnapshot> settings;
            try
            {
                settings = (provider.GetSettings() ?? Array.Empty<OpenACAIConfigValueSnapshot>())
                    .Where(setting => setting != null)
                    .ToArray();
            }
            catch (Exception ex)
            {
                OpenACAI.Endnight.Loader.OpenACAIEndnightRuntime.LogSource?.LogWarning("OpenACAI config provider snapshot failed for '" + id + "': " + ex.Message);
                settings = Array.Empty<OpenACAIConfigValueSnapshot>();
            }

            return new OpenACAIConfigProviderSnapshot(
                id,
                string.IsNullOrWhiteSpace(provider.DisplayName) ? id : provider.DisplayName.Trim(),
                string.IsNullOrWhiteSpace(provider.Source) ? "Unknown" : provider.Source.Trim(),
                provider.Priority,
                provider.Enabled,
                settings,
                DateTimeOffset.UtcNow.ToString("O"));
        }
    }

    public sealed class OpenACAIConfigValueSnapshot
    {
        public OpenACAIConfigValueSnapshot(
            string section,
            string key,
            string displayName,
            string kind,
            string value,
            string defaultValue,
            string tooltip,
            bool canEdit,
            bool requiresRestart,
            double? minimum = null,
            double? maximum = null,
            double? step = null,
            IEnumerable<string> options = null)
        {
            Section = string.IsNullOrWhiteSpace(section) ? "Settings" : section;
            Key = key ?? "";
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Key : displayName;
            Kind = string.IsNullOrWhiteSpace(kind) ? OpenACAIConfigValueKinds.Text : kind;
            Value = value ?? "";
            DefaultValue = defaultValue ?? "";
            Tooltip = tooltip ?? "";
            CanEdit = canEdit;
            RequiresRestart = requiresRestart;
            Minimum = minimum;
            Maximum = maximum;
            Step = step;
            Options = (options ?? Array.Empty<string>()).Where(option => option != null).ToArray();
        }

        public string Section { get; }
        public string Key { get; }
        public string DisplayName { get; }
        public string Kind { get; }
        public string Value { get; }
        public string DefaultValue { get; }
        public string Tooltip { get; }
        public bool CanEdit { get; }
        public bool RequiresRestart { get; }
        public double? Minimum { get; }
        public double? Maximum { get; }
        public double? Step { get; }
        public IReadOnlyList<string> Options { get; }
    }

    public static class OpenACAIConfigValueKinds
    {
        public const string Text = "text";
        public const string Boolean = "boolean";
        public const string Integer = "integer";
        public const string Number = "number";
        public const string Choice = "choice";
    }

    public sealed class OpenACAIConfigDocument
    {
        private OpenACAIConfigDocument(
            string displayName,
            string configPath,
            string configFormat,
            string configSource,
            bool hasConfig,
            bool liveSettingsAvailable,
            bool canSave,
            string text,
            string originalText,
            string status,
            IEnumerable<OpenACAIConfigSettingSnapshot> settings)
        {
            DisplayName = displayName;
            ConfigPath = configPath;
            ConfigFormat = configFormat;
            ConfigSource = configSource;
            HasConfig = hasConfig;
            LiveSettingsAvailable = liveSettingsAvailable;
            CanSave = canSave;
            Text = text;
            OriginalText = originalText;
            Status = status;
            Settings = (settings ?? Array.Empty<OpenACAIConfigSettingSnapshot>()).ToArray();
        }

        public string DisplayName { get; }
        public string ConfigPath { get; }
        public string ConfigFormat { get; }
        public string ConfigSource { get; }
        public bool HasConfig { get; }
        public bool LiveSettingsAvailable { get; }
        public bool CanSave { get; }
        public string Text { get; }
        public string OriginalText { get; }
        public string Status { get; }
        public IReadOnlyList<OpenACAIConfigSettingSnapshot> Settings { get; }

        internal static OpenACAIConfigDocument CreateLoaded(
            string displayName,
            string configPath,
            string configFormat,
            string configSource,
            bool liveSettingsAvailable,
            string text,
            string status,
            IEnumerable<OpenACAIConfigSettingSnapshot> settings)
        {
            return new OpenACAIConfigDocument(displayName, configPath, configFormat, configSource, true, liveSettingsAvailable, true, text, text, status, settings);
        }

        internal static OpenACAIConfigDocument CreateStatus(
            string status,
            string displayName,
            string configPath,
            string configFormat,
            bool hasConfig,
            bool liveSettingsAvailable,
            bool canSave,
            string text,
            string originalText,
            IEnumerable<OpenACAIConfigSettingSnapshot> settings)
        {
            return new OpenACAIConfigDocument(displayName, configPath, configFormat, null, hasConfig, liveSettingsAvailable, canSave, text, originalText, status, settings);
        }
    }

    public sealed class OpenACAIConfigSettingSnapshot
    {
        private OpenACAIConfigSettingSnapshot(int lineIndex, string section, string key, string value, string tooltip)
        {
            LineIndex = lineIndex;
            Section = section;
            Key = key;
            Value = value;
            Tooltip = tooltip;
        }

        public int LineIndex { get; }
        public string Section { get; }
        public string Key { get; }
        public string Value { get; }
        public string Tooltip { get; }

        internal static OpenACAIConfigSettingSnapshot Create(int lineIndex, string section, string key, string value, string tooltip)
        {
            return new OpenACAIConfigSettingSnapshot(lineIndex, section, key, value, tooltip);
        }
    }

    public sealed class OpenACAIConfigSettingEdit
    {
        public OpenACAIConfigSettingEdit(int lineIndex, string value)
        {
            LineIndex = lineIndex;
            Value = value;
        }

        public int LineIndex { get; }
        public string Value { get; }
    }
}

