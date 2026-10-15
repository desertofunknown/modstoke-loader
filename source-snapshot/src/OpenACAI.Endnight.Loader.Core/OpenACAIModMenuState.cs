// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Shared controller state for Modstoke Mods menu renderers.

using System;
using System.Collections.Generic;
using System.Linq;
using OpenACAI.Loader;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenAcaiModMenuState
    {
        private static readonly MenuTab[] Tabs =
        {
            new MenuTab(OpenACAIMenuProviderIds.AllMods, "All", "ALL - Show detected Modstoke-native and compatibility mods."),
            new MenuTab(OpenACAIMenuProviderIds.OpenAcaiMods, "Modstoke", "MODSTOKE - Show native Modstoke mod packages."),
            new MenuTab(OpenACAIMenuProviderIds.RedLoaderMods, "RedLoader", "REDLOADER - Show legacy RedLoader-style mods."),
            new MenuTab(OpenACAIMenuProviderIds.Libraries, "Libraries", "LIBRARIES - Show SDK/runtime helper libraries and dependency assemblies."),
            new MenuTab(OpenACAIMenuProviderIds.BepInExPlugins, "BepInEx", "BEPINEX - Show legacy BepInEx plugin assemblies."),
            new MenuTab(OpenACAIMenuProviderIds.Issues, "Issues", "ISSUES - Show mods with warnings, missing files, or duplicate IDs."),
            new MenuTab(OpenACAIMenuProviderIds.Extensions, "Ext", "EXTENSIONS - Show menu, settings, status, and action providers registered by mods and tools."),
            new MenuTab(OpenACAIMenuProviderIds.Credits, "Credits", "CREDITS - Show Modstoke Loader, BepInEx, RedLoader, SonsSdk, and mod author credits.")
        };

        private static ModInventoryState _inventory;
        private static int _activeTab;
        private static ModInventoryState _cachedInventory;
        private static int _cachedTab = -1;
        private static ModInventoryEntry[] _cachedEntries = Array.Empty<ModInventoryEntry>();
        private static ModInventoryEntry _configEntry;
        private static string _configText;
        private static string _configOriginalText;
        private static string _configStatus;
        private static bool _friendlyMode = true;
        private static OpenACAIConfigSettingSnapshot[] _friendlySettings = Array.Empty<OpenACAIConfigSettingSnapshot>();
        private static OpenACAIMenuProviderSnapshot _providerEntry;
        private static OpenACAIMenuContentSnapshot _providerContent;
        private static OpenACAICommandProviderSnapshot _providerCommands;
        private static string _providerStatus;

        internal static ModInventoryState Inventory => _inventory;

        internal static int ActiveTab => _activeTab;

        internal static int TabCount => Tabs.Length;

        internal static string ActiveTabLabel => TabLabel(_activeTab);

        internal static bool IsCreditsTab => string.Equals(ActiveTabProviderId, OpenACAIMenuProviderIds.Credits, StringComparison.OrdinalIgnoreCase);

        internal static bool IsExtensionsTab => string.Equals(ActiveTabProviderId, OpenACAIMenuProviderIds.Extensions, StringComparison.OrdinalIgnoreCase);

        internal static bool IsLibrariesTab => string.Equals(ActiveTabProviderId, OpenACAIMenuProviderIds.Libraries, StringComparison.OrdinalIgnoreCase);

        internal static bool IsConfigOpen => _configEntry != null;

        internal static bool IsProviderOpen => _providerEntry != null;

        internal static ModInventoryEntry ConfigEntry => _configEntry;

        internal static string ConfigText => _configText;

        internal static string ConfigOriginalText => _configOriginalText;

        internal static string ConfigStatus => _configStatus;

        internal static bool FriendlyMode => _friendlyMode;

        internal static OpenACAIConfigSettingSnapshot[] FriendlySettings => _friendlySettings;

        internal static OpenACAIMenuProviderSnapshot ProviderEntry => _providerEntry;

        internal static OpenACAIMenuContentSnapshot ProviderContent => _providerContent;

        internal static OpenACAICommandProviderSnapshot ProviderCommands => _providerCommands;

        internal static string ProviderStatus => _providerStatus;

        private static string ActiveTabProviderId => Tabs[NormalizeTab(_activeTab)].ProviderId;

        internal static void SetInventory(ModInventoryState inventory)
        {
            _inventory = inventory;
            InvalidateEntries();
        }

        internal static void ResetView()
        {
            _activeTab = 0;
            CloseProvider();
            InvalidateEntries();
        }

        internal static bool SelectTab(int tab)
        {
            var normalized = NormalizeTab(tab);
            if (_activeTab == normalized)
            {
                return false;
            }

            _activeTab = normalized;
            CloseProvider();
            InvalidateEntries();
            return true;
        }

        internal static ModInventoryEntry[] GetFilteredEntries()
        {
            if (_cachedInventory == _inventory && _cachedTab == _activeTab)
            {
                return _cachedEntries;
            }

            var entries = _inventory?.Entries ?? Array.Empty<ModInventoryEntry>();
            IEnumerable<ModInventoryEntry> filtered;
            var activeProviderId = ActiveTabProviderId;
            if (string.Equals(activeProviderId, OpenACAIMenuProviderIds.OpenAcaiMods, StringComparison.OrdinalIgnoreCase))
            {
                filtered = entries.Where(entry => string.Equals(entry.SourceFamily, OpenACAIModSourceFamilies.OpenAcai, StringComparison.OrdinalIgnoreCase))
                    .Where(entry => !OpenACAIModLoaderTypes.IsLibrary(entry.LoaderType));
            }
            else if (string.Equals(activeProviderId, OpenACAIMenuProviderIds.RedLoaderMods, StringComparison.OrdinalIgnoreCase))
            {
                filtered = entries.Where(entry => string.Equals(entry.SourceFamily, OpenACAIModSourceFamilies.LegacyRedLoader, StringComparison.OrdinalIgnoreCase)
                    || OpenACAIModLoaderTypes.IsLegacyRedLoader(entry.LoaderType))
                    .Where(entry => !OpenACAIModLoaderTypes.IsLibrary(entry.LoaderType));
            }
            else if (string.Equals(activeProviderId, OpenACAIMenuProviderIds.Libraries, StringComparison.OrdinalIgnoreCase))
            {
                filtered = entries.Where(entry => OpenACAIModLoaderTypes.IsLibrary(entry.LoaderType));
            }
            else if (string.Equals(activeProviderId, OpenACAIMenuProviderIds.BepInExPlugins, StringComparison.OrdinalIgnoreCase))
            {
                filtered = entries.Where(entry => string.Equals(entry.SourceFamily, OpenACAIModSourceFamilies.LegacyBepInEx, StringComparison.OrdinalIgnoreCase)
                    || OpenACAIModLoaderTypes.IsLegacyBepInEx(entry.LoaderType))
                    .Where(entry => !OpenACAIModLoaderTypes.IsLibrary(entry.LoaderType));
            }
            else if (string.Equals(activeProviderId, OpenACAIMenuProviderIds.Issues, StringComparison.OrdinalIgnoreCase))
            {
                filtered = entries.Where(entry => !string.IsNullOrWhiteSpace(entry.IssueCode));
            }
            else if (IsCreditsTab || IsExtensionsTab)
            {
                filtered = Array.Empty<ModInventoryEntry>();
            }
            else
            {
                filtered = entries.Where(entry => !OpenACAIModLoaderTypes.IsLibrary(entry.LoaderType));
            }

            _cachedInventory = _inventory;
            _cachedTab = _activeTab;
            _cachedEntries = filtered.ToArray();
            return _cachedEntries;
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

        internal static OpenACAIMenuProviderSnapshot[] GetExtensionProviders()
        {
            return OpenACAIMenu.Snapshot()
                .Where(IsExtensionProvider)
                .OrderBy(provider => provider.Priority)
                .ThenBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        internal static void OpenProvider(OpenACAIMenuProviderSnapshot provider)
        {
            _providerEntry = provider;
            _providerContent = null;
            _providerCommands = null;
            _providerStatus = null;
            if (provider == null)
            {
                return;
            }

            OpenACAIMenuContentSnapshot content;
            if (OpenACAIMenu.TryGetProviderContent(provider.Id, out content))
            {
                _providerContent = content;
            }

            OpenACAICommandProviderSnapshot commands;
            if (OpenACAIMenu.TryGetProviderCommands(provider.Id, out commands))
            {
                _providerCommands = commands;
            }

            if (_providerContent != null || _providerCommands != null)
            {
                return;
            }

            _providerContent = CreateDefaultProviderContent(provider);
            _providerStatus = provider.HasContent
                ? "Provider content was not available."
                : provider.HasCommands
                ? "Provider commands were not available."
                : "Provider exposes metadata only.";
        }

        internal static void CloseProvider()
        {
            _providerEntry = null;
            _providerContent = null;
            _providerCommands = null;
            _providerStatus = null;
        }

        internal static bool TryGetProviderConfigSnapshot(out OpenACAIConfigProviderSnapshot snapshot)
        {
            snapshot = null;
            return _providerEntry != null
                && OpenACAIConfig.TryGetProviderSnapshotFromMenuProvider(_providerEntry.Id, out snapshot);
        }

        internal static bool TryApplyProviderConfigValue(string key, string value, out string tooltip)
        {
            tooltip = null;
            if (_providerEntry == null)
            {
                _providerStatus = "No provider is open.";
                tooltip = "APPLY - No provider is open.";
                return false;
            }

            string status;
            var applied = OpenACAIConfig.TrySetRegisteredValueFromMenuProvider(_providerEntry.Id, key, value, out status);
            _providerStatus = string.IsNullOrWhiteSpace(status)
                ? (applied ? "Setting applied." : "Setting was not applied.")
                : status;

            var providerId = _providerEntry.Id;
            var refreshed = OpenACAIMenu.Snapshot().FirstOrDefault(provider => string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase));
            if (refreshed != null)
            {
                _providerEntry = refreshed;
            }

            OpenACAIMenuContentSnapshot content;
            _providerContent = OpenACAIMenu.TryGetProviderContent(providerId, out content)
                ? content
                : CreateDefaultProviderContent(_providerEntry);
            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("config-provider-value-applied");
            tooltip = "APPLY - " + _providerStatus;
            return applied;
        }

        internal static bool TryInvokeProviderCommand(string commandId, out string tooltip)
        {
            tooltip = null;
            if (_providerEntry == null)
            {
                _providerStatus = "No provider is open.";
                tooltip = "RUN - No provider is open.";
                return false;
            }

            OpenACAICommandResultSnapshot result;
            var invoked = OpenACAIMenu.TryInvokeProviderCommand(_providerEntry.Id, commandId, out result);
            _providerStatus = result == null
                ? (invoked ? "Command invoked." : "Command failed.")
                : result.Detail;

            RefreshProviderSnapshots();
            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("menu-provider-command-invoked");
            tooltip = "RUN - " + _providerStatus;
            return invoked;
        }

        internal static string BuildMetaLine(string loaderVersion)
        {
            if (_inventory == null)
            {
                return "Inventory is not ready";
            }

            return "Modstoke Loader v" + loaderVersion + " by OpenACAI"
                + "  |  " + (_inventory.VortexDeploymentDetected ? "Vortex detected" : "No Vortex deployment")
                + "  |  " + (_inventory.Summary?.OpenAcai ?? 0) + " Modstoke mods"
                + "  |  " + (_inventory.Summary?.Libraries ?? 0) + " libraries"
                + "  |  " + (_inventory.Summary?.Hosted ?? 0) + " hosted";
        }

        internal static void SetConfigStatus(string status)
        {
            _configStatus = status;
        }

        internal static void OpenConfig(ModInventoryEntry entry, int maxEditorBytes)
        {
            _configEntry = entry;
            _friendlyMode = true;
            var document = OpenACAIConfig.OpenDocument(entry, maxEditorBytes);
            _configText = document.Text ?? "";
            _configOriginalText = document.OriginalText ?? _configText;
            _configStatus = document.Status ?? (document.CanSave ? "Editing detected config. Save writes directly to disk." : null);
            RefreshFriendlySettings();
        }

        internal static void CloseConfig()
        {
            _configEntry = null;
            _configText = null;
            _configOriginalText = null;
            _configStatus = null;
            _friendlyMode = true;
            _friendlySettings = Array.Empty<OpenACAIConfigSettingSnapshot>();
        }

        internal static void SetFriendlyMode(bool friendly)
        {
            _friendlyMode = friendly;
            if (friendly)
            {
                RefreshFriendlySettings();
            }
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
            SetConfigText(_configOriginalText ?? "");
            _configStatus = "Reverted unsaved edits.";
        }

        internal static void ApplyFriendlyEdits(IEnumerable<OpenACAIConfigSettingEdit> edits)
        {
            _configText = OpenACAIConfig.ApplySettings(_configText ?? "", edits);
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

            try
            {
                OpenACAIConfig.SaveText(_configEntry.ConfigPath, _configText ?? "");
                _configOriginalText = _configText ?? "";
                _configStatus = "Saved. Restart the game if the mod does not apply settings immediately.";
                refreshInventory?.Invoke();
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

        private static void InvalidateEntries()
        {
            _cachedInventory = null;
            _cachedTab = -1;
            _cachedEntries = Array.Empty<ModInventoryEntry>();
        }

        private static void RefreshFriendlySettings()
        {
            _friendlySettings = OpenACAIConfig.ParseSettings(_configText ?? "");
        }

        private static int NormalizeTab(int tab)
        {
            return Math.Max(0, Math.Min(tab, Tabs.Length - 1));
        }

        private static OpenACAIMenuProviderSnapshot FindProvider(string id)
        {
            return OpenACAIMenu.Snapshot().FirstOrDefault(provider => string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsExtensionProvider(OpenACAIMenuProviderSnapshot provider)
        {
            if (provider == null)
            {
                return false;
            }

            if (!provider.BuiltIn)
            {
                return true;
            }

            return string.Equals(provider.Kind, OpenACAIMenuProviderKinds.Settings, StringComparison.OrdinalIgnoreCase)
                || string.Equals(provider.Kind, OpenACAIMenuProviderKinds.CommandGroup, StringComparison.OrdinalIgnoreCase)
                || string.Equals(provider.Kind, OpenACAIMenuProviderKinds.Status, StringComparison.OrdinalIgnoreCase);
        }

        private static void RefreshProviderSnapshots()
        {
            if (_providerEntry == null)
            {
                _providerContent = null;
                _providerCommands = null;
                return;
            }

            var providerId = _providerEntry.Id;
            var refreshed = OpenACAIMenu.Snapshot().FirstOrDefault(provider => string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase));
            if (refreshed != null)
            {
                _providerEntry = refreshed;
            }

            OpenACAIMenuContentSnapshot content;
            _providerContent = OpenACAIMenu.TryGetProviderContent(providerId, out content)
                ? content
                : null;

            OpenACAICommandProviderSnapshot commands;
            _providerCommands = OpenACAIMenu.TryGetProviderCommands(providerId, out commands)
                ? commands
                : null;

            if (_providerContent == null && _providerCommands == null)
            {
                _providerContent = CreateDefaultProviderContent(_providerEntry);
            }
        }

        private static OpenACAIMenuContentSnapshot CreateDefaultProviderContent(OpenACAIMenuProviderSnapshot provider)
        {
            return new OpenACAIMenuContentSnapshot(
                provider.DisplayName,
                provider.Description,
                new[]
                {
                    new OpenACAIMenuSectionSnapshot(
                        "Provider",
                        new[]
                        {
                            new OpenACAIMenuRowSnapshot("ID", provider.Id, "", "Provider identifier."),
                            new OpenACAIMenuRowSnapshot("Kind", provider.Kind, "", "Provider category."),
                            new OpenACAIMenuRowSnapshot("Source", provider.Source, "", "Provider owner or mod source."),
                            new OpenACAIMenuRowSnapshot("Authorization", provider.RequiresAuthorization ? "Required" : "Not required", provider.RequiresAuthorization ? "CHECK" : "READY", "Whether this provider is gated by authorization."),
                            new OpenACAIMenuRowSnapshot("Status", provider.Enabled ? "Enabled" : "Disabled", provider.Enabled ? "READY" : "OFF", provider.Description)
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

