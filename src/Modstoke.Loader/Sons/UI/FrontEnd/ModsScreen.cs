// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Modstoke.Localization;
using UnityEngine;

namespace Modstoke.Loader
{
    /// <summary>One installed mod on the Mods screen: its status as the value, and its own page to open.</summary>
    internal sealed class ModRow : OptionRow
    {
        internal ModRow(ModInventoryEntry entry, Action<ModInventoryEntry> open) : base(MenuKeys.EntryId(entry))
        {
            Entry = entry;
            Open = () => open(entry);
            Ready = true;
        }

        internal ModInventoryEntry Entry { get; }
        internal override Action Open { get; }
        internal override string Value => Text[ModsScreen.StatusKey(Entry)];
        internal override string DefaultValue => "";
        internal override bool IsDefault => true;
        internal override bool Step(int direction, bool wrap) => false;
        internal override bool Reset() => false;

        internal override Color? Tint => MenuText.EntryTone(Entry) switch
        {
            MenuTone.Bad => MenuKit.Red,
            MenuTone.Warn => MenuKit.Amber,
            _ => null
        };
    }

    /// <summary>
    /// The installed mods, in place of the Mods menu's home: a tab for each kind with its count, mods that need
    /// attention first, a search, and a column that says what each mod is and what is wrong with it. A mod's own
    /// settings and pages open in the Mods menu, and Back from there comes back here.
    /// </summary>
    internal sealed class ModsScreen : SettingsScreen
    {
        private static int _lastTab;
        private static string _search = "";
        private readonly string[] _tabs = MenuViewBuilder.FilterChips;

        internal ModsScreen() : base(_lastTab)
        {
            try
            {
                MenuState.Refresh(out _);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The mod list could not be refreshed: " + error.Message);
            }
        }

        protected override IReadOnlyList<string> TabIds => _tabs;
        protected override string Heading => Counts;
        protected override string Title => Text["title.mods"];
        protected override bool CanReset => false;

        protected override string TabName(string id) => Text["mods.tab." + TabKey(id)] + "  " + Entries(id).Length;

        protected override string SectionName(string id) => Text["mods.section." + id];

        protected override string ItemName(OptionRow row) => row is ModRow mod ? mod.Entry.DisplayName : Text["mods.search"];

        protected override string ItemAbout(OptionRow row)
        {
            if (row is not ModRow mod) return Text["mods.searchAbout"];
            var entry = mod.Entry;
            var lines = new List<string>();
            var by = string.IsNullOrWhiteSpace(entry.Author) ? null : Text.Get("mods.by", ("author", entry.Author.Trim()));
            var version = MenuText.VersionText(entry);
            lines.Add(string.Join(" · ", new[] { by, version != "-" ? "v" + version.TrimStart('v') : null, Text[KindKey(entry)] }
                .Where(part => !string.IsNullOrEmpty(part))));
            // What is wrong comes first and stands out; otherwise how the mod is running.
            if (!string.IsNullOrWhiteSpace(entry.Issue))
                lines.Add("<color=" + MenuKit.Html(MenuText.EntryTone(entry) == MenuTone.Bad ? MenuKit.Red : MenuKit.Amber) + ">"
                    + entry.Issue.Trim() + "</color>");
            else lines.Add(Text[StatusAboutKey(entry)]);
            var path = entry.PackagePath ?? entry.AssemblyPath;
            if (!string.IsNullOrWhiteSpace(path)) lines.Add("<size=80%><color=" + MenuKit.Html(MenuKit.Muted) + ">" + path + "</color></size>");
            lines.Add(Text["mods.openHint"]);
            return string.Join("\n\n", lines);
        }

        protected override void TabChanged(int index) => _lastTab = index;

        protected override OptionTab BuildTab(string id)
        {
            var search = new TextRow("search", () => _search, text =>
            {
                _search = text ?? "";
                return true;
            }, "", 40);
            var shown = Entries(id).Where(Matches).ToArray();
            var attention = shown.Where(NeedsAttention).Select(Row).ToArray();
            var rest = shown.Where(entry => !NeedsAttention(entry)).Select(Row).ToArray();
            return new OptionTab(id, new[]
            {
                OptionsCatalog.Section("find", search),
                OptionsCatalog.Section("attention", attention),
                OptionsCatalog.Section(shown.Length == 0 ? "none" : "installed", rest)
            });
        }

        // A new search lists the mods again, with focus staying in the search box.
        protected override void RowChanged(OptionRow row)
        {
            if (row.Id == "search") Rebuild("search");
        }

        private static OptionRow Row(ModInventoryEntry entry) => new ModRow(entry, OpenPage);

        private static ModInventoryEntry[] Entries(string tab)
        {
            try
            {
                return MenuState.FilterEntries(MenuState.Inventory?.Entries, tab)
                    .OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
            }
            catch (Exception)
            {
                return Array.Empty<ModInventoryEntry>();
            }
        }

        private static bool Matches(ModInventoryEntry entry)
        {
            if (string.IsNullOrWhiteSpace(_search)) return true;
            var term = _search.Trim();
            return Contains(entry.DisplayName, term) || Contains(entry.Id, term) || Contains(entry.Author, term);
        }

        private static bool Contains(string text, string term) => text != null && text.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0;

        private static bool NeedsAttention(ModInventoryEntry entry)
            => MenuText.EntryTone(entry) is MenuTone.Bad or MenuTone.Warn;

        // The mod's own page in the Mods menu: its settings, config file, live controls and details.
        private static void OpenPage(ModInventoryEntry entry)
        {
            try
            {
                MenuController.ShowHandedOver(MenuKeys.Mod(entry));
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The page of " + entry.DisplayName + " could not open: " + error.Message);
            }
        }

        // The heading line: the Loader's version, how many mods there are, and whether any need attention.
        private static string Counts
        {
            get
            {
                var all = Entries(MenuProviderIds.AllMods).Length;
                var issues = Entries(MenuProviderIds.Issues).Length;
                // The release number alone, without the build label and commit after it.
                var version = RuntimeEntryPoint.Version ?? "";
                var cut = version.IndexOfAny(new[] { '-', '+' });
                if (cut > 0) version = version.Substring(0, cut);
                var parts = new List<string> { "Modstoke " + version, Text.Count("mods.count", all) };
                parts.Add(issues == 0 ? Text["mods.noIssues"] : Text.Count("mods.issues", issues));
                return string.Join(" · ", parts);
            }
        }

        // The filter ids the Mods menu uses, as this screen's text keys.
        private static string TabKey(string id) => id switch
        {
            MenuProviderIds.AllMods => "all",
            MenuProviderIds.OpenAcaiMods => "modstoke",
            MenuProviderIds.RedLoaderMods => "redloader",
            MenuProviderIds.BepInExPlugins => "bepinex",
            MenuProviderIds.Libraries => "libraries",
            MenuProviderIds.Issues => "issues",
            _ => "all"
        };

        internal static string StatusKey(ModInventoryEntry entry) => "mods.status." + StatusName(entry);

        private static string StatusAboutKey(ModInventoryEntry entry) => "mods.statusAbout." + StatusName(entry);

        private static string StatusName(ModInventoryEntry entry) => (entry?.Status ?? "").ToLowerInvariant() switch
        {
            "hosted" => "running",
            "ready" => "ready",
            "missing" => "missing",
            "warning" => "check",
            "disabled" => "off",
            _ => "found"
        };

        private static string KindKey(ModInventoryEntry entry)
        {
            var library = ModLoaderTypes.IsLibrary(entry.LoaderType);
            if (string.Equals(entry.SourceFamily, ModSourceFamilies.OpenAcai, StringComparison.OrdinalIgnoreCase))
                return library ? "mods.kind.modstokeLibrary" : "mods.kind.modstoke";
            if (ModLoaderTypes.IsLegacyBepInEx(entry.LoaderType)) return "mods.kind.bepinex";
            if (ModLoaderTypes.IsLegacyRedLoader(entry.LoaderType)) return library ? "mods.kind.redloaderLibrary" : "mods.kind.redloader";
            return "mods.kind.other";
        }
    }
}
