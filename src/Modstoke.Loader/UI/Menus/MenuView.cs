// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Unity-free view model for the Modstoke Mods menu, shared by both renderers.

using System;
using System.Collections.Generic;
using Modstoke.Loader;

namespace Modstoke.Loader
{
    internal enum MenuTone
    {
        Neutral,
        Good,
        Info,
        Warn,
        Bad
    }

    internal enum MenuNavKind
    {
        Group,
        Mod,
        Provider,
        Home,
        About
    }

    internal enum MenuPageKind
    {
        Home,
        Mod,
        Provider,
        About
    }

    internal enum MenuRowKind
    {
        Info,
        ModEntry,
        ProviderLink,
        Content,
        Setting,
        ConfigValue,
        Input,
        Command,
        Credit,
        FriendlyConfig
    }

    internal enum MenuControlKind
    {
        None,
        Toggle,
        Choice,
        Slider,
        TextApply,
        InputLive,
        Run,
        Open,
        // Colour choices for screen host panels; the options carry the colours and the value is the selected index.
        Swatches
    }

    internal enum MenuIntentKind
    {
        None,
        Open,
        Back,
        Refresh,
        ApplySetting,
        ApplyConfigValue,
        SetInput,
        RunCommand,
        SetFilter,
        Expand,
        Collapse,
        // These leave the menu for another surface, so the renderer carries them out.
        OpenConfigFile,
        OpenLiveSettings,
        OpenModActions
    }

    internal sealed class MenuNavItem
    {
        internal string Key { get; init; }
        internal MenuNavKind Kind { get; init; }
        internal string Label { get; init; }
        internal string Detail { get; init; }
        internal MenuTone Tone { get; init; }
        internal int Depth { get; init; }
        internal bool Collapsed { get; init; }
        internal IReadOnlyList<MenuNavItem> Children { get; init; } = Array.Empty<MenuNavItem>();
        /// <summary>The page key this item opens, or null for a group.</summary>
        internal string Target { get; init; }
    }

    internal sealed class MenuPage
    {
        internal string Key { get; init; }
        internal MenuPageKind Kind { get; init; }
        internal string Title { get; init; }
        internal string Meta { get; init; }
        internal string Description { get; init; }
        internal MenuTone Tone { get; init; }
        internal IReadOnlyList<MenuSection> Sections { get; init; } = Array.Empty<MenuSection>();
        internal string FooterStatus { get; init; }
        internal bool Dirty { get; init; }
    }

    internal sealed class MenuSection
    {
        internal string Key { get; init; }
        internal string Title { get; init; }
        internal bool Collapsible { get; init; }
        internal bool Collapsed { get; init; }
        /// <summary>Background detail that gets no jump bar button and does not count toward showing the bar.</summary>
        internal bool NoJump { get; init; }
        internal IReadOnlyList<MenuRow> Rows { get; init; } = Array.Empty<MenuRow>();
    }

    internal sealed class MenuRow
    {
        internal string Key { get; init; }
        internal MenuRowKind Kind { get; init; }
        internal string Label { get; init; }
        internal string Value { get; init; }
        internal string Status { get; init; }
        internal MenuTone Tone { get; init; }
        internal string Detail { get; init; }
        /// <summary>Published with the UI state, so it never carries a value.</summary>
        internal string Tooltip { get; init; }
        internal MenuControl Control { get; init; } = MenuControl.None;
        internal MenuIntent Intent { get; init; } = MenuIntent.None;
        /// <summary>Figures for a stat strip; a row with any is drawn as one, with <see cref="Value"/> as its plain-text form.</summary>
        internal IReadOnlyList<MenuStat> Stats { get; init; } = Array.Empty<MenuStat>();
    }

    internal sealed class MenuStat
    {
        internal string Label { get; init; }
        internal string Value { get; init; }
        internal MenuTone Tone { get; init; }
    }

    internal sealed class MenuControl
    {
        internal static readonly MenuControl None = new MenuControl();

        internal MenuControlKind Kind { get; init; }
        internal IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();
        internal double? Min { get; init; }
        internal double? Max { get; init; }
        internal double? Step { get; init; }
        internal int MaxLength { get; init; }
        internal bool Editable { get; init; }
        /// <summary>The declared value kind, one of <see cref="ConfigValueKinds"/>.</summary>
        internal string ValueKind { get; init; }
    }

    internal sealed class MenuIntent
    {
        internal static readonly MenuIntent None = new MenuIntent();

        internal MenuIntentKind Kind { get; init; }
        internal string ProviderId { get; init; }
        internal Guid Scope { get; init; }
        /// <summary>The setting or config key, or the page, filter or section key for navigation intents.</summary>
        internal string Key { get; init; }
        internal string CommandId { get; init; }
        internal string FieldId { get; init; }
        internal string EntryId { get; init; }
    }

    internal static class MenuKeys
    {
        internal const string Home = "home";
        internal const string About = "about";
        internal const string ModPrefix = "mod:";
        internal const string ProviderPrefix = "provider:";

        internal static string Mod(ModInventoryEntry entry) => ModPrefix + EntryId(entry);

        internal static string Provider(string providerId) => ProviderPrefix + providerId;

        internal static string Group(string name) => "group:" + name;

        internal static string Setting(Guid scope, string key) => "setting:" + scope + "/" + key;

        internal static string Config(string providerId, string key) => "config:" + providerId + "/" + key;

        internal static string Input(Guid scope, string fieldId) => "input:" + scope + "/" + fieldId;

        internal static string Command(string commandId) => "command:" + commandId;

        internal static string Content(int sectionIndex, string title, string label, int occurrence) =>
            "content:" + sectionIndex + ":" + title + "/" + label + "#" + occurrence;

        /// <summary>
        /// Inventory entries may lack an ID, so their display name stands in. Entries that share an ID add their
        /// manifest or assembly path so each one keeps its own page.
        /// </summary>
        internal static string EntryId(ModInventoryEntry entry)
        {
            var id = entry?.Id ?? entry?.DisplayName ?? "";
            var location = entry?.ManifestPath ?? entry?.AssemblyPath ?? entry?.PackagePath;
            return entry?.DuplicateId == true && !string.IsNullOrWhiteSpace(location) ? id + "@" + location : id;
        }
    }

    /// <summary>Menu navigation state for this session. It survives hide and show, but not a restart.</summary>
    internal sealed class MenuViewState
    {
        private const int MaxHistory = 32;

        // Pixel offsets per view, so returning to a view lands where the user left it.
        private readonly Dictionary<string, float> _scrollOffsets = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _collapsed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _history = new List<string>();

        internal string SelectedNavKey { get; set; }
        internal string SearchText { get; set; } = "";
        internal string Filter { get; set; } = MenuProviderIds.AllMods;
        internal string LastPage { get; set; } = MenuKeys.Home;
        /// <summary>The most recent page other than Home, for the Home page's shortcut back to it.</summary>
        internal string LastVisited { get; set; }

        internal float GetScrollOffset(string view)
        {
            return view != null && _scrollOffsets.TryGetValue(view, out var offset) ? offset : 0f;
        }

        internal void SetScrollOffset(string view, float offset)
        {
            if (view != null)
            {
                _scrollOffsets[view] = offset;
            }
        }

        internal bool IsCollapsed(string key, bool collapsedByDefault)
        {
            return key != null && _collapsed.TryGetValue(key, out var collapsed) ? collapsed : collapsedByDefault;
        }

        internal void SetCollapsed(string key, bool collapsed)
        {
            if (key != null)
            {
                _collapsed[key] = collapsed;
            }
        }

        internal void PushHistory(string pageKey)
        {
            if (string.IsNullOrWhiteSpace(pageKey))
            {
                return;
            }

            _history.Add(pageKey);
            if (_history.Count > MaxHistory)
            {
                _history.RemoveAt(0);
            }
        }

        internal void ClearHistory() => _history.Clear();

        internal string PopHistory()
        {
            if (_history.Count == 0)
            {
                return null;
            }

            var pageKey = _history[_history.Count - 1];
            _history.RemoveAt(_history.Count - 1);
            return pageKey;
        }
    }
}
