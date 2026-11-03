// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Shared controller state for Modstoke Mods menu renderers.

using System;
using System.Collections.Generic;
using System.Linq;
using Modstoke.Loader;

namespace Modstoke.Loader
{
    internal static class MenuState
    {
        private static readonly MenuTab[] Tabs =
        {
            new MenuTab(MenuProviderIds.AllMods, "All", "ALL - Show detected Modstoke-native and compatibility mods."),
            new MenuTab(MenuProviderIds.OpenAcaiMods, "Modstoke", "MODSTOKE - Show native Modstoke mod packages."),
            new MenuTab(MenuProviderIds.RedLoaderMods, "RedLoader", "REDLOADER - Show legacy RedLoader-style mods."),
            new MenuTab(MenuProviderIds.Libraries, "Libraries", "LIBRARIES - Show SDK/runtime helper libraries and dependency assemblies."),
            new MenuTab(MenuProviderIds.BepInExPlugins, "BepInEx", "BEPINEX - Show legacy BepInEx plugin assemblies."),
            new MenuTab(MenuProviderIds.Issues, "Issues", "ISSUES - Show mods with warnings, missing files, or duplicate IDs."),
            new MenuTab(MenuProviderIds.Extensions, "Ext", "EXTENSIONS - Show menu, settings, status, and action providers registered by mods and tools."),
            new MenuTab(MenuProviderIds.Credits, "Credits", "CREDITS - Show Modstoke Loader, BepInEx, RedLoader, SonsSdk, and mod author credits.")
        };

        private static ModInventoryState _inventory;
        private static ModInventoryEntry _configEntry;
        private static string _configText;
        private static string _configOriginalText;
        private static string _configStatus;
        private static bool _configCanSave;
        private static bool _friendlyMode = true;
        private static ConfigSettingSnapshot[] _friendlySettings = Array.Empty<ConfigSettingSnapshot>();
        private static readonly Dictionary<int, string> _friendlyDrafts = new Dictionary<int, string>();
        private static MenuProviderSnapshot _providerEntry;
        private static MenuContentSnapshot _providerContent;
        private static CommandProviderSnapshot _providerCommands;
        private static MenuInputSnapshot _providerInputs;
        private static MenuSettingsSnapshot _providerSettings;
        private static string _providerStatus;
        private static long _providerContentRevision;

        internal static ModInventoryState Inventory => _inventory;

        internal static MenuViewState ViewState { get; } = new MenuViewState();

        internal static bool IsConfigOpen => _configEntry != null;

        internal static bool IsProviderOpen => _providerEntry != null;

        internal static ModInventoryEntry ConfigEntry => _configEntry;

        internal static string ConfigText => _configText;

        internal static string ConfigOriginalText => _configOriginalText;

        internal static string ConfigStatus => _configStatus;

        /// <summary>The open config file was read into the editor; a file that could not be read is never written.</summary>
        internal static bool ConfigCanSave => _configEntry != null && _configCanSave;

        internal static bool FriendlyMode => _friendlyMode;

        internal static ConfigSettingSnapshot[] FriendlySettings => _friendlySettings;

        /// <summary>The open config text differs from what was last read or saved, or a friendly value is still a draft.</summary>
        internal static bool IsConfigDirty => ConfigCanSave
            && (_friendlyDrafts.Count > 0 || !string.Equals(_configText ?? "", _configOriginalText ?? "", StringComparison.Ordinal));

        internal static MenuProviderSnapshot ProviderEntry => _providerEntry;

        internal static MenuContentSnapshot ProviderContent => _providerContent;

        internal static CommandProviderSnapshot ProviderCommands => _providerCommands;

        internal static MenuInputSnapshot ProviderInputs => _providerInputs;

        internal static MenuSettingsSnapshot ProviderSettings => _providerSettings;

        internal static string ProviderStatus => _providerStatus;

        /// <summary>The open provider called <see cref="MenuRegistry.NotifyContentChanged"/> since its last refresh.</summary>
        internal static bool ProviderContentNotified => _providerEntry != null
            && MenuRegistry.GetContentRevision(_providerEntry.Id) != _providerContentRevision;

        internal static void SetInventory(ModInventoryState inventory)
        {
            _inventory = inventory;
        }

        /// <summary>Entries shown by a mods tab or filter chip, given its built-in provider ID.</summary>
        internal static ModInventoryEntry[] FilterEntries(IEnumerable<ModInventoryEntry> source, string filterProviderId)
        {
            var entries = source ?? Array.Empty<ModInventoryEntry>();
            IEnumerable<ModInventoryEntry> filtered;
            if (string.Equals(filterProviderId, MenuProviderIds.OpenAcaiMods, StringComparison.OrdinalIgnoreCase))
            {
                filtered = entries.Where(entry => string.Equals(entry.SourceFamily, ModSourceFamilies.OpenAcai, StringComparison.OrdinalIgnoreCase))
                    .Where(entry => !ModLoaderTypes.IsLibrary(entry.LoaderType));
            }
            else if (string.Equals(filterProviderId, MenuProviderIds.RedLoaderMods, StringComparison.OrdinalIgnoreCase))
            {
                filtered = entries.Where(entry => string.Equals(entry.SourceFamily, ModSourceFamilies.LegacyRedLoader, StringComparison.OrdinalIgnoreCase)
                    || ModLoaderTypes.IsLegacyRedLoader(entry.LoaderType))
                    .Where(entry => !ModLoaderTypes.IsLibrary(entry.LoaderType));
            }
            else if (string.Equals(filterProviderId, MenuProviderIds.Libraries, StringComparison.OrdinalIgnoreCase))
            {
                filtered = entries.Where(entry => ModLoaderTypes.IsLibrary(entry.LoaderType));
            }
            else if (string.Equals(filterProviderId, MenuProviderIds.BepInExPlugins, StringComparison.OrdinalIgnoreCase))
            {
                filtered = entries.Where(entry => string.Equals(entry.SourceFamily, ModSourceFamilies.LegacyBepInEx, StringComparison.OrdinalIgnoreCase)
                    || ModLoaderTypes.IsLegacyBepInEx(entry.LoaderType))
                    .Where(entry => !ModLoaderTypes.IsLibrary(entry.LoaderType));
            }
            else if (string.Equals(filterProviderId, MenuProviderIds.Issues, StringComparison.OrdinalIgnoreCase))
            {
                filtered = entries.Where(entry => !string.IsNullOrWhiteSpace(entry.IssueCode));
            }
            else if (string.Equals(filterProviderId, MenuProviderIds.Credits, StringComparison.OrdinalIgnoreCase)
                || string.Equals(filterProviderId, MenuProviderIds.Extensions, StringComparison.OrdinalIgnoreCase))
            {
                filtered = Array.Empty<ModInventoryEntry>();
            }
            else
            {
                filtered = entries.Where(entry => !ModLoaderTypes.IsLibrary(entry.LoaderType));
            }

            return filtered.ToArray();
        }

        /// <summary>The tab for a filter chip's built-in provider ID, or the All tab when none matches.</summary>
        internal static int TabIndex(string filterProviderId)
        {
            return Math.Max(0, Array.FindIndex(Tabs, tab => string.Equals(tab.ProviderId, filterProviderId, StringComparison.OrdinalIgnoreCase)));
        }

        internal static string TabLabel(int tab)
        {
            var definition = Tabs[NormalizeTab(tab)];
            var provider = FindProvider(definition.ProviderId);
            return provider == null ? definition.DisplayName : provider.DisplayName;
        }

        internal static string TabTooltip(int tab)
        {
            var definition = Tabs[NormalizeTab(tab)];
            var provider = FindProvider(definition.ProviderId);
            return provider == null || string.IsNullOrWhiteSpace(provider.Description)
                ? definition.Tooltip
                : TabLabel(tab).ToUpperInvariant() + " - " + provider.Description;
        }

        internal static void OpenProvider(MenuProviderSnapshot provider)
        {
            _providerEntry = provider;
            _providerContent = null;
            _providerCommands = null;
            _providerInputs = null;
            _providerSettings = null;
            _providerStatus = null;
            if (provider == null)
            {
                return;
            }

            // Read before the snapshots, so a notification that lands while they are captured is not lost.
            _providerContentRevision = MenuRegistry.GetContentRevision(provider.Id);
            MenuContentSnapshot content;
            if (MenuRegistry.TryGetProviderContent(provider.Id, out content))
            {
                _providerContent = content;
            }

            CommandProviderSnapshot commands;
            if (MenuRegistry.TryGetProviderCommands(provider.Id, out commands))
            {
                _providerCommands = commands;
            }

            if (MenuRegistry.TryGetProviderInputs(provider.Id, out var inputs))
            {
                _providerInputs = inputs;
            }

            if (MenuRegistry.TryGetProviderSettings(provider.Id, out var settings))
            {
                _providerSettings = settings;
            }

            if (_providerContent != null || _providerCommands != null || _providerInputs != null || _providerSettings != null)
            {
                return;
            }

            _providerContent = CreateDefaultProviderContent(provider);
            _providerStatus = provider.HasContent
                ? "Provider content was not available."
                : provider.HasCommands
                ? "Provider commands were not available."
                : provider.HasSettings
                ? "Provider settings were not available."
                : "Provider exposes metadata only.";
        }

        internal static void CloseProvider()
        {
            _providerEntry = null;
            _providerContent = null;
            _providerCommands = null;
            _providerInputs = null;
            _providerSettings = null;
            _providerStatus = null;
        }

        internal static bool TryGetProviderConfigSnapshot(out ConfigProviderSnapshot snapshot)
        {
            snapshot = null;
            return _providerEntry != null
                && LoaderConfig.TryGetProviderSnapshotFromMenuProvider(_providerEntry.Id, out snapshot);
        }

        /// <summary>Applies a value through the config mirror <paramref name="providerId"/>, which need not be the open page.</summary>
        internal static bool ApplyConfigValue(string providerId, string key, string value, out string tooltip)
        {
            tooltip = null;
            if (string.IsNullOrWhiteSpace(providerId))
            {
                _providerStatus = "No provider is open.";
                tooltip = "APPLY - No provider is open.";
                return false;
            }

            string status;
            var applied = LoaderConfig.TrySetRegisteredValueFromMenuProvider(providerId, key, value, out status);
            _providerStatus = string.IsNullOrWhiteSpace(status)
                ? (applied ? "Setting applied." : "Setting was not applied.")
                : status;

            if (_providerEntry != null && string.Equals(_providerEntry.Id, providerId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                var openId = _providerEntry.Id;
                var refreshed = FindProvider(openId);
                if (refreshed != null)
                {
                    _providerEntry = refreshed;
                }

                MenuContentSnapshot content;
                _providerContent = MenuRegistry.TryGetProviderContent(openId, out content)
                    ? content
                    : CreateDefaultProviderContent(_providerEntry);
            }

            RuntimeEntryPoint.RequestLoaderStateRefresh("config-provider-value-applied");
            tooltip = "APPLY - " + _providerStatus;
            return applied;
        }

        internal static bool ApplySetting(Guid scope, string key, string value, out string tooltip)
        {
            tooltip = null;
            if (_providerEntry == null)
            {
                _providerStatus = "No provider is open.";
                tooltip = "APPLY - No provider is open.";
                return false;
            }

            var applied = MenuRegistry.TrySetProviderSetting(_providerEntry.Id, scope, key, value,
                CommandOrigin.ModsMenu, out var result);
            _providerStatus = string.IsNullOrWhiteSpace(result.Detail)
                ? (applied ? "Setting applied." : "Setting was not applied.")
                : result.Detail;
            tooltip = "APPLY - " + _providerStatus;
            // Settings stay out of loader state, so unlike config values there is no state refresh to request.
            RefreshProviderSnapshotsIfChanged();
            return applied;
        }

        /// <summary>Pushes one form field as the user types; the provider keeps the draft.</summary>
        internal static bool SetInput(string providerId, Guid scope, string fieldId, string value)
        {
            return MenuRegistry.TrySetProviderInput(providerId, scope, fieldId, value, out _);
        }

        internal static bool RunCommand(string commandId, out string tooltip)
        {
            tooltip = null;
            if (_providerEntry == null)
            {
                _providerStatus = "No provider is open.";
                tooltip = "RUN - No provider is open.";
                return false;
            }

            CommandResultSnapshot result;
            var invoked = MenuRegistry.TryInvokeProviderCommand(_providerEntry.Id, commandId,
                new CommandInvocation(CommandOrigin.ModsMenu, "", false), out result);
            _providerStatus = result == null
                ? (invoked ? "Command invoked." : "Command failed.")
                : result.Detail;

            RefreshProviderSnapshotsIfChanged();
            RuntimeEntryPoint.RequestLoaderStateRefresh("menu-provider-command-invoked");
            tooltip = "RUN - " + _providerStatus;
            return invoked;
        }

        /// <summary>Opens a page by its <see cref="MenuKeys"/> key. A key that no longer resolves opens Home instead.</summary>
        internal static bool Open(string pageKey)
        {
            return OpenPage(pageKey, true);
        }

        /// <summary>Returns to the previous page; false when Home is already showing with nothing behind it.</summary>
        internal static bool Back()
        {
            var previous = ViewState.PopHistory();
            if (previous == null)
            {
                if (string.Equals(ViewState.LastPage, MenuKeys.Home, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                previous = MenuKeys.Home;
            }

            OpenPage(previous, false);
            return true;
        }

        internal static bool Refresh(out string tooltip)
        {
            try
            {
                SetInventory(RuntimeEntryPoint.RefreshModInventory());
            }
            catch (Exception ex)
            {
                tooltip = "REFRESH - Failed: " + ex.Message;
                return false;
            }

            RefreshProviderSnapshotsIfChanged();
            tooltip = "REFRESH - Mod inventory refreshed.";
            return true;
        }

        internal static void SetFilter(string filterProviderId)
        {
            ViewState.Filter = string.IsNullOrWhiteSpace(filterProviderId) ? MenuProviderIds.AllMods : filterProviderId.Trim();
        }

        internal static void SetSearch(string text)
        {
            ViewState.SearchText = text ?? "";
        }

        internal static void SetCollapsed(string key, bool collapsed)
        {
            ViewState.SetCollapsed(key, collapsed);
        }

        /// <summary>
        /// Folds a nav item's children for a click on it: a second click on the open item folds them away, and a click on
        /// a folded item opens it unfolded. True when the item is already the open page, so there is nothing to open.
        /// </summary>
        internal static bool FoldNavItem(MenuNavItem item)
        {
            var current = !IsConfigOpen
                && string.Equals(item.Target, ViewState.SelectedNavKey, StringComparison.OrdinalIgnoreCase);
            if (item.Children.Count > 0 && (current || item.Collapsed))
            {
                ViewState.SetCollapsed(item.Key, !item.Collapsed);
            }

            return current;
        }

        internal static ModInventoryEntry FindEntry(string entryId)
        {
            return FindEntry(_inventory?.Entries, entryId);
        }

        /// <summary>Finds an entry by the ID that <see cref="MenuKeys.EntryId"/> gives it, or else by its plain mod ID.</summary>
        internal static ModInventoryEntry FindEntry(IEnumerable<ModInventoryEntry> source, string entryId)
        {
            if (source == null || string.IsNullOrWhiteSpace(entryId))
            {
                return null;
            }

            var entries = source as IReadOnlyList<ModInventoryEntry> ?? source.ToArray();
            return entries.FirstOrDefault(entry => string.Equals(MenuKeys.EntryId(entry), entryId, StringComparison.OrdinalIgnoreCase))
                ?? entries.FirstOrDefault(entry => string.Equals(entry.Id, entryId, StringComparison.OrdinalIgnoreCase));
        }

        private static bool OpenPage(string pageKey, bool remember)
        {
            var key = string.IsNullOrWhiteSpace(pageKey) ? MenuKeys.Home : pageKey.Trim();
            var found = true;
            if (key.StartsWith(MenuKeys.ProviderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var provider = FindProvider(key.Substring(MenuKeys.ProviderPrefix.Length));
                found = provider != null;
                if (found)
                {
                    OpenProvider(provider);
                    key = MenuKeys.Provider(provider.Id);
                }
            }
            else if (key.StartsWith(MenuKeys.ModPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var entry = FindEntry(key.Substring(MenuKeys.ModPrefix.Length));
                found = entry != null;
                if (found)
                {
                    CloseProvider();
                    key = MenuKeys.Mod(entry);
                }
            }
            else if (string.Equals(key, MenuKeys.About, StringComparison.OrdinalIgnoreCase))
            {
                CloseProvider();
                key = MenuKeys.About;
            }
            else
            {
                found = string.Equals(key, MenuKeys.Home, StringComparison.OrdinalIgnoreCase);
                key = MenuKeys.Home;
            }

            if (!found || key == MenuKeys.Home)
            {
                CloseProvider();
                key = MenuKeys.Home;
            }

            var view = ViewState;
            if (remember && !string.Equals(view.LastPage, key, StringComparison.OrdinalIgnoreCase))
            {
                view.PushHistory(view.LastPage);
            }

            view.LastPage = key;
            view.SelectedNavKey = key;
            if (key != MenuKeys.Home)
            {
                view.LastVisited = key;
            }

            return found;
        }

        internal static void SetConfigStatus(string status)
        {
            _configStatus = status;
        }

        internal static void OpenConfig(ModInventoryEntry entry, int maxEditorBytes)
        {
            _configEntry = entry;
            _friendlyMode = true;
            var document = LoaderConfig.OpenDocument(entry, maxEditorBytes);
            _configText = document.Text ?? "";
            _configOriginalText = document.OriginalText ?? _configText;
            _configStatus = document.Status ?? (document.CanSave ? "Editing detected config. Save writes directly to disk." : null);
            _configCanSave = document.CanSave;
            _friendlyDrafts.Clear();
            RefreshFriendlySettings();
        }

        internal static void CloseConfig()
        {
            _configEntry = null;
            _configText = null;
            _configOriginalText = null;
            _configStatus = null;
            _configCanSave = false;
            _friendlyMode = true;
            _friendlySettings = Array.Empty<ConfigSettingSnapshot>();
            _friendlyDrafts.Clear();
        }

        /// <summary>Drafts go into the text before the raw view shows it.</summary>
        internal static void SetFriendlyMode(bool friendly)
        {
            if (!friendly)
            {
                ApplyFriendlyDrafts();
            }

            _friendlyMode = friendly;
            if (friendly)
            {
                RefreshFriendlySettings();
            }
        }

        internal static bool TryGetFriendlyDraft(int lineIndex, out string value)
        {
            return _friendlyDrafts.TryGetValue(lineIndex, out value);
        }

        /// <summary>Keeps an edited friendly value until it is applied; a value set back to the file's own drops the draft.</summary>
        internal static void SetFriendlyDraft(ConfigSettingSnapshot setting, string value)
        {
            value ??= "";
            if (string.Equals(value, setting.Value ?? "", StringComparison.Ordinal))
            {
                _friendlyDrafts.Remove(setting.LineIndex);
            }
            else
            {
                _friendlyDrafts[setting.LineIndex] = value;
            }
        }

        /// <summary>Writes the friendly drafts into the config text; only the edited lines change.</summary>
        internal static void ApplyFriendlyDrafts()
        {
            if (_friendlyDrafts.Count == 0)
            {
                return;
            }

            var edits = _friendlyDrafts.Select(draft => new ConfigSettingEdit(draft.Key, draft.Value)).ToArray();
            _friendlyDrafts.Clear();
            ApplyFriendlyEdits(edits);
        }

        internal static void SetConfigText(string text)
        {
            _configText = text ?? "";
            if (_friendlyMode)
            {
                RefreshFriendlySettings();
            }
        }

        internal static void RevertConfig()
        {
            _friendlyDrafts.Clear();
            SetConfigText(_configOriginalText ?? "");
            _configStatus = "Reverted unsaved edits.";
        }

        internal static void ApplyFriendlyEdits(IEnumerable<ConfigSettingEdit> edits)
        {
            _configText = LoaderConfig.ApplySettings(_configText ?? "", edits);
            RefreshFriendlySettings();
        }

        internal static bool SaveConfig(Action refreshInventory, out string tooltip)
        {
            if (_configEntry == null || _configEntry.HasConfig != true || string.IsNullOrWhiteSpace(_configEntry.ConfigPath))
            {
                _configStatus = "No config file selected.";
                tooltip = "SAVE - No config file is selected.";
                return false;
            }

            if (!_configCanSave)
            {
                // The editor holds no copy of the file, so a save would replace it with whatever was typed.
                tooltip = "SAVE - The config file could not be read, so it was not overwritten.";
                return false;
            }

            try
            {
                ApplyFriendlyDrafts();
                LoaderConfig.SaveText(_configEntry.ConfigPath, _configText ?? "");
                _configOriginalText = _configText ?? "";
                _configStatus = "Saved. Restart the game if the mod does not apply settings immediately.";
                refreshInventory?.Invoke();
                // The refresh replaces the inventory, so keep the editor on the rescanned entry.
                _configEntry = FindEntry(MenuKeys.EntryId(_configEntry)) ?? _configEntry;
                tooltip = "SAVE - Saved config. Some mods apply changes after restart or reload.";
                return true;
            }
            catch (Exception ex)
            {
                _configStatus = "Save failed: " + ex.Message;
                tooltip = "SAVE - Failed: " + ex.Message;
                return false;
            }
        }

        private static void RefreshFriendlySettings()
        {
            _friendlySettings = LoaderConfig.ParseSettings(_configText ?? "");
        }

        private static int NormalizeTab(int tab)
        {
            return Math.Max(0, Math.Min(tab, Tabs.Length - 1));
        }

        private static MenuProviderSnapshot FindProvider(string id)
        {
            return MenuRegistry.Snapshot().FirstOrDefault(provider => string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool RefreshProviderSnapshotsIfChanged(bool includeInputValues = true)
        {
            if (_providerEntry == null)
            {
                _providerContent = null;
                _providerCommands = null;
                _providerInputs = null;
                _providerSettings = null;
                return false;
            }

            var previousProvider = _providerEntry;
            var previousContent = _providerContent;
            var previousCommands = _providerCommands;
            var previousInputs = _providerInputs;
            var previousSettings = _providerSettings;
            var providerId = _providerEntry.Id;
            _providerContentRevision = MenuRegistry.GetContentRevision(providerId);
            var refreshed = FindProvider(providerId);
            if (refreshed == null)
            {
                CloseProvider();
                return true;
            }
            _providerEntry = refreshed;

            MenuContentSnapshot content;
            _providerContent = MenuRegistry.TryGetProviderContent(providerId, out content)
                ? content
                : null;

            CommandProviderSnapshot commands;
            _providerCommands = MenuRegistry.TryGetProviderCommands(providerId, out commands)
                ? commands
                : null;

            _providerInputs = MenuRegistry.TryGetProviderInputs(providerId, out var inputs) ? inputs : null;
            _providerSettings = MenuRegistry.TryGetProviderSettings(providerId, out var settings) ? settings : null;

            if (_providerContent == null && _providerCommands == null && _providerInputs == null && _providerSettings == null)
            {
                _providerContent = CreateDefaultProviderContent(_providerEntry);
            }

            return !SameProvider(previousProvider, _providerEntry)
                || !SameContent(previousContent, _providerContent)
                || !SameCommands(previousCommands, _providerCommands)
                || !SameInputs(previousInputs, _providerInputs, includeInputValues)
                || !SameSettings(previousSettings, _providerSettings);
        }

        private static bool SameProvider(MenuProviderSnapshot a, MenuProviderSnapshot b)
        {
            return a.Id == b.Id && a.DisplayName == b.DisplayName && a.Kind == b.Kind
                && a.Priority == b.Priority && a.Source == b.Source && a.Description == b.Description
                && a.RequiresAuthorization == b.RequiresAuthorization && a.Enabled == b.Enabled
                && a.HasContent == b.HasContent && a.HasCommands == b.HasCommands && a.HasSettings == b.HasSettings
                && a.HasInputs == b.HasInputs && a.BuiltIn == b.BuiltIn
                && a.OwnerModId == b.OwnerModId && a.Group == b.Group;
        }

        private static bool SameContent(MenuContentSnapshot a, MenuContentSnapshot b)
        {
            if (a == null || b == null) return a == b;
            if (a.Title != b.Title || a.Subtitle != b.Subtitle || a.Sections.Count != b.Sections.Count) return false;
            for (var i = 0; i < a.Sections.Count; i++)
            {
                var left = a.Sections[i];
                var right = b.Sections[i];
                if (left.Title != right.Title || left.Rows.Count != right.Rows.Count) return false;
                for (var j = 0; j < left.Rows.Count; j++)
                {
                    var x = left.Rows[j];
                    var y = right.Rows[j];
                    if (x.Label != y.Label || x.Value != y.Value || x.Status != y.Status || x.Detail != y.Detail) return false;
                }
            }
            return true;
        }

        private static bool SameCommands(CommandProviderSnapshot a, CommandProviderSnapshot b)
        {
            if (a == null || b == null) return a == b;
            if (a.ProviderId != b.ProviderId || a.Title != b.Title || a.Subtitle != b.Subtitle || a.Commands.Count != b.Commands.Count) return false;
            for (var i = 0; i < a.Commands.Count; i++)
            {
                var x = a.Commands[i];
                var y = b.Commands[i];
                if (x.Id != y.Id || x.DisplayName != y.DisplayName || x.Description != y.Description
                    || x.Status != y.Status || x.Enabled != y.Enabled || x.RequiresConfirmation != y.RequiresConfirmation) return false;
            }
            return true;
        }

        private static bool SameInputs(MenuInputSnapshot a, MenuInputSnapshot b, bool includeValues)
        {
            if (a == null || b == null) return a == b;
            if (a.Scope != b.Scope || a.Fields.Count != b.Fields.Count) return false;
            for (var i = 0; i < a.Fields.Count; i++)
            {
                var x = a.Fields[i];
                var y = b.Fields[i];
                if (x.Id != y.Id || x.DisplayName != y.DisplayName || x.Description != y.Description
                    || x.MaximumLength != y.MaximumLength || x.Enabled != y.Enabled
                    || (includeValues && x.Value != y.Value)) return false;
            }
            return true;
        }

        // Unlike input drafts, setting values always count: renderers keep unapplied edits across a rebuild.
        private static bool SameSettings(MenuSettingsSnapshot a, MenuSettingsSnapshot b)
        {
            if (a == null || b == null) return a == b;
            if (a.Scope != b.Scope || a.Settings.Count != b.Settings.Count) return false;
            for (var i = 0; i < a.Settings.Count; i++)
            {
                var x = a.Settings[i];
                var y = b.Settings[i];
                if (x.Section != y.Section || x.Key != y.Key || x.Kind != y.Kind || x.Value != y.Value
                    || x.DisplayName != y.DisplayName || x.Description != y.Description || x.CanEdit != y.CanEdit
                    || !x.Options.SequenceEqual(y.Options)
                    || x.Minimum != y.Minimum || x.Maximum != y.Maximum || x.Step != y.Step) return false;
            }
            return true;
        }

        private static MenuContentSnapshot CreateDefaultProviderContent(MenuProviderSnapshot provider)
        {
            return new MenuContentSnapshot(
                provider.DisplayName,
                provider.Description,
                new[]
                {
                    new MenuSectionSnapshot(
                        "Provider",
                        new[]
                        {
                            new MenuRowSnapshot("ID", provider.Id, "", "Provider identifier."),
                            new MenuRowSnapshot("Kind", provider.Kind, "", "Provider category."),
                            new MenuRowSnapshot("Source", provider.Source, "", "Provider owner or mod source."),
                            new MenuRowSnapshot("Authorization", provider.RequiresAuthorization ? "Required" : "Not required", provider.RequiresAuthorization ? "CHECK" : "READY", "Whether this provider is gated by authorization."),
                            new MenuRowSnapshot("Status", provider.Enabled ? "Enabled" : "Disabled", provider.Enabled ? "READY" : "OFF", provider.Description)
                        })
                });
        }

        private sealed class MenuTab
        {
            internal MenuTab(string providerId, string displayName, string tooltip)
            {
                ProviderId = providerId;
                DisplayName = displayName;
                Tooltip = tooltip;
            }

            internal string ProviderId { get; }
            internal string DisplayName { get; }
            internal string Tooltip { get; }
        }
    }
}

