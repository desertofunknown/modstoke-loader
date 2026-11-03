// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned mod config helpers. The UI can render native controls, but
// file validation and simple friendly parsing live in this service.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Modstoke.Loader;

namespace Modstoke.Loader
{
    public static class LoaderConfig
    {
        public const string ServiceKey = "openacai.loader.config";
        internal const string MenuProviderIdPrefix = "openacai.config.";

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, ProviderRegistration> Providers =
            new Dictionary<string, ProviderRegistration>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> PendingMenuUpdates = new(StringComparer.OrdinalIgnoreCase);
        private static bool _publishingMenus;
        private static IReadOnlyList<ConfigProviderSnapshot> _publishedSnapshots;

        private sealed class ProviderRegistration(IConfigProvider provider, ConfigProviderSnapshot snapshot)
        {
            internal readonly IConfigProvider Provider = provider;
            internal ConfigProviderSnapshot Snapshot = snapshot;
            internal bool Capturing;
        }

        public static event Action<ConfigProviderSnapshot> ProviderChanged;

        internal static void Initialize()
        {
            lock (SyncRoot)
            {
                PublishServiceNoLock();
            }
        }

        public static bool RegisterOrUpdateProvider(IConfigProvider provider)
        {
            var snapshot = ConfigProviderSnapshot.FromProvider(provider);
            if (snapshot == null)
            {
                return false;
            }

            var registration = new ProviderRegistration(provider, snapshot);
            Action<ConfigProviderSnapshot> handlers;
            lock (SyncRoot)
            {
                Providers[snapshot.Id] = registration;
                _publishedSnapshots = null;
                PendingMenuUpdates.Add(snapshot.Id);
                PublishServiceNoLock();
                handlers = ProviderChanged;
            }

            SynchronizeMenus();
            DispatchProviderChanged(registration, snapshot, handlers);
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

                _publishedSnapshots = null;
                PendingMenuUpdates.Add(id.Trim());
                PublishServiceNoLock();
            }

            SynchronizeMenus();
            return true;
        }

        public static IReadOnlyList<ConfigProviderSnapshot> SnapshotProviders()
        {
            ProviderRegistration[] registrations;
            lock (SyncRoot)
            {
                registrations = Providers.Values.ToArray();
            }

            var captured = new List<(ProviderRegistration Registration, ConfigProviderSnapshot Snapshot)>(registrations.Length);
            foreach (var registration in registrations)
                if (TryCaptureRegistration(registration, out var snapshot))
                    captured.Add((registration, snapshot));

            lock (SyncRoot)
                return OrderSnapshots(captured.Where(pair => IsCurrentNoLock(pair.Registration)).Select(pair => pair.Snapshot));
        }

        public static bool TryGetProviderSnapshot(string id, out ConfigProviderSnapshot snapshot)
        {
            snapshot = null;
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            ProviderRegistration registration;
            lock (SyncRoot)
                Providers.TryGetValue(id.Trim(), out registration);
            return registration != null && TryCaptureRegistration(registration, out snapshot);
        }

        public static bool TrySetRegisteredValue(string providerId, string key, string value, out string status)
        {
            status = null;
            if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(key))
            {
                status = "A provider id and setting key are required.";
                return false;
            }

            ProviderRegistration registration;
            lock (SyncRoot)
            {
                Providers.TryGetValue(providerId.Trim(), out registration);
            }

            if (registration == null)
            {
                status = "Config provider was not registered.";
                return false;
            }

            bool applied;
            try
            {
                var enabled = registration.Provider.Enabled;
                lock (SyncRoot)
                {
                    if (!enabled || !IsCurrentNoLock(registration))
                    {
                        status = "Config provider is no longer available.";
                        return false;
                    }
                }
                applied = registration.Provider.TrySetValue(key.Trim(), value, out status);
            }
            catch (Exception ex)
            {
                status = "Config setting failed: " + ex.Message;
                ReportProviderFailure("setting", providerId, ex);
                return false;
            }

            // Refresh failure must not turn a completed setting change into an apparent failure.
            if (!TryCaptureRegistration(registration, out var snapshot))
                return applied;
            Action<ConfigProviderSnapshot> handlers;
            lock (SyncRoot)
            {
                if (!IsCurrentNoLock(registration))
                    return applied;
                PendingMenuUpdates.Add(snapshot.Id);
                PublishServiceNoLock();
                handlers = ProviderChanged;
            }

            SynchronizeMenus();
            if (applied)
                DispatchProviderChanged(registration, snapshot, handlers);
            return applied;
        }

        private static bool IsCurrentNoLock(ProviderRegistration registration) =>
            Providers.TryGetValue(registration.Snapshot.Id, out var current) && ReferenceEquals(current, registration);

        private static bool TryCaptureRegistration(ProviderRegistration registration, out ConfigProviderSnapshot snapshot)
        {
            snapshot = null;
            lock (SyncRoot)
            {
                if (!IsCurrentNoLock(registration))
                    return false;
                if (registration.Capturing)
                {
                    snapshot = registration.Snapshot;
                    return true;
                }
                registration.Capturing = true;
            }

            try
            {
                var captured = ConfigProviderSnapshot.FromProvider(registration.Provider);
                lock (SyncRoot)
                {
                    if (captured == null || !IsCurrentNoLock(registration)
                        || !string.Equals(captured.Id, registration.Snapshot.Id, StringComparison.OrdinalIgnoreCase))
                        return false;
                    registration.Snapshot = captured;
                    _publishedSnapshots = null;
                    snapshot = captured;
                    return true;
                }
            }
            finally
            {
                lock (SyncRoot)
                    registration.Capturing = false;
            }
        }

        private static void SynchronizeMenus()
        {
            lock (SyncRoot)
            {
                if (_publishingMenus)
                    return;
                _publishingMenus = true;
            }

            // Menu observers may register or remove providers. Drain their updates after each callback.
            while (true)
            {
                string id;
                ConfigProviderSnapshot snapshot;
                lock (SyncRoot)
                {
                    if (PendingMenuUpdates.Count == 0)
                    {
                        _publishingMenus = false;
                        return;
                    }
                    id = PendingMenuUpdates.First();
                    PendingMenuUpdates.Remove(id);
                    snapshot = Providers.TryGetValue(id, out var registration) ? registration.Snapshot : null;
                }
                try
                {
                    if (snapshot == null)
                        MenuRegistry.TryUnregisterProvider(ConfigMenuProvider.MenuProviderId(id));
                    else
                        MenuRegistry.RegisterOrUpdateProvider(new ConfigMenuProvider(snapshot));
                }
                catch (Exception ex)
                {
                    ReportProviderFailure("menu update", id, ex);
                }
            }
        }

        internal static bool TryGetProviderSnapshotFromMenuProvider(string menuProviderId, out ConfigProviderSnapshot snapshot)
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
                status = "Menu provider is not backed by a Modstoke config provider.";
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

        internal static bool TryParseBoolean(string value, out bool result)
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

        internal static ConfigDocument OpenDocument(ModInventoryEntry entry, int maxEditorBytes)
        {
            if (entry == null)
            {
                return ConfigDocument.CreateStatus("No mod selected.", null, null, null, false, false, false, null, null, Array.Empty<ConfigSettingSnapshot>());
            }

            var status = entry.LiveSettingsAvailable
                ? "Live RedLoader settings are registered. File-backed settings are shown when available."
                : null;

            if (!entry.HasConfig || string.IsNullOrWhiteSpace(entry.ConfigPath))
            {
                return ConfigDocument.CreateStatus(
                    string.IsNullOrWhiteSpace(status) ? "No config file detected for " + entry.DisplayName + "." : status,
                    entry.DisplayName,
                    entry.ConfigPath,
                    entry.ConfigFormat,
                    entry.HasConfig,
                    entry.LiveSettingsAvailable,
                    false,
                    null,
                    null,
                    Array.Empty<ConfigSettingSnapshot>());
            }

            ConfigDocument CreateFailure(string detail) =>
                ConfigDocument.CreateStatus(detail, entry.DisplayName, entry.ConfigPath, entry.ConfigFormat, true, entry.LiveSettingsAvailable, false, null, null, Array.Empty<ConfigSettingSnapshot>());

            try
            {
                var info = new FileInfo(entry.ConfigPath);
                if (!info.Exists)
                {
                    return CreateFailure("Config file no longer exists.");
                }

                if (info.Length > maxEditorBytes)
                {
                    return CreateFailure("Config file is too large for the in-game editor: " + info.Length + " bytes.");
                }

                var text = File.ReadAllText(entry.ConfigPath);
                return ConfigDocument.CreateLoaded(
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
                return CreateFailure("Failed to read config: " + ex.Message);
            }
        }

        public static ConfigSettingSnapshot[] ParseSettings(string text)
        {
            var lines = SplitLines(text);
            var settings = new List<ConfigSettingSnapshot>();
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

                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    var close = trimmed.IndexOf(']');
                    if (close > 0)
                    {
                        var tail = trimmed.AsSpan(close + 1).TrimStart();
                        if (tail.IsEmpty || tail[0] == '#' || tail[0] == ';')
                        {
                            section = trimmed.Substring(1, close - 1).Trim();
                            comments.Clear();
                            continue;
                        }
                    }
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

                settings.Add(ConfigSettingSnapshot.Create(
                    i,
                    section,
                    key,
                    value,
                    comments.Count == 0 ? null : string.Join(" ", comments.Where(comment => !string.IsNullOrWhiteSpace(comment)))));
                comments.Clear();
            }

            return settings.ToArray();
        }

        public static string ApplySettings(string text, IEnumerable<ConfigSettingEdit> edits)
        {
            text ??= "";
            var replacements = new Dictionary<int, string>();
            foreach (var edit in edits ?? Array.Empty<ConfigSettingEdit>())
            {
                if (edit == null || edit.LineIndex < 0)
                    continue;
                var value = edit.Value ?? "";
                if (value.AsSpan().IndexOfAny('\r', '\n') >= 0)
                    throw new ArgumentException("A friendly setting must fit on one line.", nameof(edits));
                replacements[edit.LineIndex] = value;
            }

            if (replacements.Count == 0)
                return text;

            StringBuilder output = null;
            // Rewrite value spans only; untouched bytes retain their whitespace and line endings.
            var copiedThrough = 0;
            var lineIndex = 0;
            for (var start = 0; start < text.Length; lineIndex++)
            {
                var end = start;
                while (end < text.Length && text[end] != '\r' && text[end] != '\n')
                    end++;
                var line = text.AsSpan(start, end - start);
                if (replacements.TryGetValue(lineIndex, out var value))
                {
                    var trimmed = line.TrimStart();
                    var equals = line.IndexOf('=');
                    if (!trimmed.IsEmpty && trimmed[0] != '#' && trimmed[0] != ';' && trimmed[0] != '['
                        && equals > 0 && !line[..equals].Trim().IsEmpty
                        && !line[(equals + 1)..].Trim().SequenceEqual(value.AsSpan()))
                    {
                        var valueStart = equals + 1;
                        while (valueStart < line.Length && char.IsWhiteSpace(line[valueStart]))
                            valueStart++;
                        var valueEnd = line.Length;
                        while (valueEnd > valueStart && char.IsWhiteSpace(line[valueEnd - 1]))
                            valueEnd--;
                        output ??= new StringBuilder(text.Length);
                        output.Append(text.AsSpan(copiedThrough, start + valueStart - copiedThrough));
                        output.Append(value);
                        copiedThrough = start + valueEnd;
                    }
                }
                start = end;
                if (start < text.Length && text[start++] == '\r' && start < text.Length && text[start] == '\n')
                    start++;
            }

            return output == null ? text : output.Append(text.AsSpan(copiedThrough)).ToString();
        }

        public static void SaveText(string configPath, string text)
        {
            if (string.IsNullOrWhiteSpace(configPath))
            {
                throw new InvalidOperationException("No config file is selected.");
            }

            var destination = Path.GetFullPath(configPath);
            var file = new FileInfo(destination);
            if (file.LinkTarget != null)
                destination = file.ResolveLinkTarget(true)?.FullName
                    ?? throw new IOException("The config link target could not be resolved.");
            var content = text ?? "";
            if (File.Exists(destination) && string.Equals(File.ReadAllText(destination), content, StringComparison.Ordinal))
                return;

            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(true);
                }
                if (File.Exists(destination))
                    File.Replace(temporary, destination, null);
                else
                    File.Move(temporary, destination);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { }
            }
        }

        internal static object CreateState()
        {
            return CreateState(SnapshotProviders());
        }

        private static object CreateState(IReadOnlyList<ConfigProviderSnapshot> providerSnapshots)
        {
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

        private static IReadOnlyList<ConfigProviderSnapshot> OrderSnapshots(IEnumerable<ConfigProviderSnapshot> snapshots)
        {
            return Array.AsReadOnly(snapshots
                .OrderBy(snapshot => snapshot.Priority)
                .ThenBy(snapshot => snapshot.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(snapshot => snapshot.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray());
        }

        private static void PublishServiceNoLock()
        {
            _publishedSnapshots ??= OrderSnapshots(Providers.Values.Select(registration => registration.Snapshot));
            Modding.SetService(ServiceKey, CreateState(_publishedSnapshots));
        }

        private static void DispatchProviderChanged(ProviderRegistration registration, ConfigProviderSnapshot snapshot, Action<ConfigProviderSnapshot> handlers)
        {
            foreach (var handler in Delegate.EnumerateInvocationList(handlers))
            {
                lock (SyncRoot)
                    if (!IsCurrentNoLock(registration))
                        return;
                try { handler(snapshot); }
                catch (Exception ex) { ReportProviderFailure("event subscriber", snapshot.Id, ex); }
            }
        }

        internal static void ReportProviderFailure(string operation, string id, Exception exception)
        {
            try { RuntimeEntryPoint.LogSource?.LogWarning("Modstoke config provider " + operation + " failed for '" + id + "': " + exception.Message); }
            catch { }
        }

        private static string[] SplitLines(string text)
        {
            var normalized = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            return normalized.Split('\n');
        }

        private sealed class ConfigMenuProvider : IMenuContentProvider, IMenuProviderPresentation
        {
            private readonly string _providerId;
            private readonly ConfigProviderSnapshot _metadata;

            internal ConfigMenuProvider(ConfigProviderSnapshot metadata)
            {
                _metadata = metadata;
                _providerId = metadata.Id;
            }

            public string Id => MenuProviderId(_providerId);
            public string DisplayName => _metadata.DisplayName;
            public string Kind => MenuProviderKinds.Settings;
            public int Priority => _metadata.Priority;
            public string Source => _metadata.Source;
            public string Description => "Settings from " + (string.IsNullOrWhiteSpace(_metadata.Source) ? "this mod" : _metadata.Source) + ".";
            public string OwnerModId => _metadata.OwnerModId;

            public bool RequiresAuthorization => false;
            public bool Enabled
            {
                get
                {
                    lock (SyncRoot)
                        return Providers.TryGetValue(_providerId, out var registration) && registration.Snapshot.Enabled;
                }
            }

            internal static string MenuProviderId(string providerId)
            {
                return MenuProviderIdPrefix + (providerId ?? "").Trim();
            }

            public MenuContentSnapshot CreateContent()
            {
                ConfigProviderSnapshot snapshot;
                if (!TryGetProviderSnapshot(_providerId, out snapshot))
                {
                    return new MenuContentSnapshot(
                        _providerId,
                        "Config provider is no longer registered.",
                        new[]
                        {
                            new MenuSectionSnapshot(
                                "Status",
                                new[] { new MenuRowSnapshot("Provider", _providerId, "MISSING", "The provider was not found.") })
                        });
                }

                var sections = snapshot.Settings
                    .GroupBy(setting => string.IsNullOrWhiteSpace(setting.Section) ? "Settings" : setting.Section)
                    .Select(group => new MenuSectionSnapshot(
                        group.Key,
                        group.Select(setting => new MenuRowSnapshot(
                            setting.DisplayName,
                            setting.Value,
                            setting.CanEdit ? setting.Kind : "READONLY",
                            setting.Tooltip))))
                    .ToArray();

                return new MenuContentSnapshot(
                    snapshot.DisplayName,
                    snapshot.Source + " | " + snapshot.SettingCount + " structured setting(s)",
                    sections);
            }
        }
    }

    public interface IConfigProvider
    {
        string Id { get; }
        string DisplayName { get; }
        string Source { get; }
        int Priority { get; }
        bool Enabled { get; }
        IReadOnlyList<ConfigValueSnapshot> GetSettings();
        bool TrySetValue(string key, string value, out string status);

        /// <summary>The ID of the mod that owns these settings, as the mod inventory lists it.</summary>
        string OwnerModId => null;
    }

    public sealed class ConfigProviderSnapshot
    {
        private ConfigProviderSnapshot(
            string id,
            string displayName,
            string source,
            int priority,
            bool enabled,
            string ownerModId,
            IReadOnlyList<ConfigValueSnapshot> settings,
            string updatedUtc)
        {
            Id = id;
            DisplayName = displayName;
            Source = source;
            Priority = priority;
            Enabled = enabled;
            OwnerModId = ownerModId;
            Settings = settings;
            SettingCount = settings == null ? 0 : settings.Count;
            UpdatedUtc = updatedUtc;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Source { get; }
        public int Priority { get; }
        public bool Enabled { get; }
        /// <summary>The provider's owning mod ID, or null when it does not declare one.</summary>
        public string OwnerModId { get; }
        public int SettingCount { get; }
        public IReadOnlyList<ConfigValueSnapshot> Settings { get; }
        public string UpdatedUtc { get; }

        internal static ConfigProviderSnapshot FromProvider(IConfigProvider provider)
        {
            if (provider == null)
                return null;
            string id = null;
            try
            {
                id = provider.Id?.Trim();
                if (string.IsNullOrEmpty(id))
                    return null;
                var displayName = provider.DisplayName;
                var source = provider.Source;
                var priority = provider.Priority;
                var enabled = provider.Enabled;
                var ownerModId = provider.OwnerModId;
                var settings = (provider.GetSettings() ?? Array.Empty<ConfigValueSnapshot>())
                    .Where(setting => setting != null)
                    .ToArray();
                return new ConfigProviderSnapshot(
                    id,
                    string.IsNullOrWhiteSpace(displayName) ? id : displayName.Trim(),
                    string.IsNullOrWhiteSpace(source) ? "Unknown" : source.Trim(),
                    priority,
                    enabled,
                    string.IsNullOrWhiteSpace(ownerModId) ? null : ownerModId.Trim(),
                    Array.AsReadOnly(settings),
                    DateTimeOffset.UtcNow.ToString("O"));
            }
            catch (Exception ex)
            {
                LoaderConfig.ReportProviderFailure("snapshot", id, ex);
                return null;
            }
        }
    }

    public sealed class ConfigValueSnapshot
    {
        public ConfigValueSnapshot(
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
            Kind = string.IsNullOrWhiteSpace(kind) ? ConfigValueKinds.Text : kind;
            Value = value ?? "";
            DefaultValue = defaultValue ?? "";
            Tooltip = tooltip ?? "";
            CanEdit = canEdit;
            RequiresRestart = requiresRestart;
            Minimum = minimum;
            Maximum = maximum;
            Step = step;
            Options = Array.AsReadOnly((options ?? Array.Empty<string>()).Where(option => option != null).ToArray());
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

    public static class ConfigValueKinds
    {
        public const string Text = "text";
        public const string Boolean = "boolean";
        public const string Integer = "integer";
        public const string Number = "number";
        public const string Choice = "choice";
    }

    public sealed class ConfigDocument
    {
        private ConfigDocument(
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
            IEnumerable<ConfigSettingSnapshot> settings)
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
            Settings = Array.AsReadOnly((settings ?? Array.Empty<ConfigSettingSnapshot>()).ToArray());
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
        public IReadOnlyList<ConfigSettingSnapshot> Settings { get; }

        internal static ConfigDocument CreateLoaded(
            string displayName,
            string configPath,
            string configFormat,
            string configSource,
            bool liveSettingsAvailable,
            string text,
            string status,
            IEnumerable<ConfigSettingSnapshot> settings)
        {
            return new ConfigDocument(displayName, configPath, configFormat, configSource, true, liveSettingsAvailable, true, text, text, status, settings);
        }

        internal static ConfigDocument CreateStatus(
            string status,
            string displayName,
            string configPath,
            string configFormat,
            bool hasConfig,
            bool liveSettingsAvailable,
            bool canSave,
            string text,
            string originalText,
            IEnumerable<ConfigSettingSnapshot> settings)
        {
            return new ConfigDocument(displayName, configPath, configFormat, null, hasConfig, liveSettingsAvailable, canSave, text, originalText, status, settings);
        }
    }

    public sealed class ConfigSettingSnapshot
    {
        private ConfigSettingSnapshot(int lineIndex, string section, string key, string value, string tooltip)
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

        internal static ConfigSettingSnapshot Create(int lineIndex, string section, string key, string value, string tooltip)
        {
            return new ConfigSettingSnapshot(lineIndex, section, key, value, tooltip);
        }
    }

    public sealed class ConfigSettingEdit
    {
        public ConfigSettingEdit(int lineIndex, string value)
        {
            LineIndex = lineIndex;
            Value = value;
        }

        public int LineIndex { get; }
        public string Value { get; }
    }
}

