// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Modstoke.Localization;
using UnityEngine;

namespace Modstoke.Loader
{
    /// <summary>
    /// One installed mod on the Mods screen: a lettered tile in the color of its kind, its name over who made it
    /// and its version, its status as the value, and its own page to open.
    /// </summary>
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
        internal override string Detail => ModsScreen.Byline(Entry);
        internal override string Monogram => ModsScreen.Letters(Entry.DisplayName);
        internal override Color? MonogramTint => ModsScreen.KindColor(Entry);

        // Working mods read green, ones with a problem in its color, and found libraries stay plain.
        internal override Color? Tint => MenuText.EntryTone(Entry) switch
        {
            MenuTone.Bad => MenuKit.Red,
            MenuTone.Warn => MenuKit.Amber,
            _ => ModsScreen.StatusName(Entry) switch
            {
                "running" or "ready" => MenuKit.Green,
                "off" => MenuKit.Faint,
                _ => null
            }
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
            // What is wrong comes first and stands out; otherwise how the mod is running.
            if (!string.IsNullOrWhiteSpace(entry.Issue))
                lines.Add("<color=" + MenuKit.Html(MenuText.EntryTone(entry) == MenuTone.Bad ? MenuKit.Red : MenuKit.Amber) + ">"
                    + entry.Issue.Trim() + "</color>");
            else lines.Add(Text[StatusAboutKey(entry)]);
            lines.Add(Text["mods.openHint"]);
            var path = entry.PackagePath ?? entry.AssemblyPath;
            if (!string.IsNullOrWhiteSpace(path)) lines.Add("<size=80%><color=" + MenuKit.Html(MenuKit.Faint) + ">" + path + "</color></size>");
            return string.Join("\n\n", lines);
        }

        protected override IReadOnlyList<(string Label, string Value)> ItemFacts(OptionRow row)
        {
            if (row is not ModRow mod) return null;
            var entry = mod.Entry;
            var version = MenuText.VersionText(entry);
            var tint = mod.Tint;
            var status = Text[StatusKey(entry)];
            var settings = entry.LiveSettingsAvailable ? "mods.settings.live" : entry.HasConfig ? "mods.settings.file" : "mods.settings.none";
            return new[]
            {
                (Text["mods.fact.author"], string.IsNullOrWhiteSpace(entry.Author) ? "—" : entry.Author.Trim()),
                (Text["mods.fact.version"], version != "-" ? "v" + version.TrimStart('v') : "—"),
                (Text["mods.fact.kind"], Text[KindKey(entry)]),
                (Text["mods.fact.status"], tint.HasValue ? "<color=" + MenuKit.Html(tint.Value) + ">" + status + "</color>" : status),
                (Text["mods.fact.settings"], Text[settings])
            };
        }

        protected override void TabChanged(int index) => _lastTab = index;

        protected override OptionTab BuildTab(string id)
        {
            var search = new TextRow("search", () => _search, text =>
            {
                _search = text ?? "";
                return true;
            }, "", 40) { Placeholder = Text["mods.searchHint"] };
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

        // The mod's own page: its settings, pages, live controls and details. Should Modstoke's screen not manage it, the
        // Mods menu shows the same page.
        private static void OpenPage(ModInventoryEntry entry)
        {
            var key = MenuKeys.Mod(entry);
            try
            {
                FrontEnd.Push(new ModPageScreen(key, Text["title.mods"]));
                return;
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The page of " + entry.DisplayName + " opens in the Mods menu instead: " + error.Message);
            }
            try
            {
                MenuController.ShowHandedOver(key);
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
                var parts = new List<string> { ModNames.Label("Modstoke", RuntimeEntryPoint.Version), Text.Count("mods.count", all) };
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

        /// <summary>Who made the mod and its version, for the line under its name; its kind when neither is known.</summary>
        internal static string Byline(ModInventoryEntry entry)
        {
            var by = string.IsNullOrWhiteSpace(entry.Author) ? null : Text.Get("mods.by", ("author", entry.Author.Trim()));
            var version = MenuText.VersionText(entry);
            var parts = new[] { by, version != "-" ? "v" + version.TrimStart('v') : null }.Where(part => part != null).ToArray();
            return parts.Length == 0 ? Text[KindKey(entry)] : string.Join("  ·  ", parts);
        }

        /// <summary>
        /// Two letters for a mod's tile, from the part of its name that sets it apart: "Modstoke Optics" is "Op",
        /// "Item 3000 test" is "I3".
        /// </summary>
        internal static string Letters(string name)
        {
            var words = (name ?? "").Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(word => char.IsLetterOrDigit(word[0])).ToList();
            if (words.Count > 1 && string.Equals(words[0], "Modstoke", StringComparison.OrdinalIgnoreCase)) words.RemoveAt(0);
            if (words.Count == 0) return "?";
            if (words.Count > 1) return (char.ToUpperInvariant(words[0][0]).ToString() + char.ToUpperInvariant(words[1][0])).Trim();
            var word = words[0];
            return word.Length > 1 ? char.ToUpperInvariant(word[0]) + word.Substring(1, 1).ToLowerInvariant() : word.ToUpperInvariant();
        }

        /// <summary>A deep tone for a mod's tile by its kind, warmer for one that has a problem; the letters stay ink.</summary>
        internal static Color KindColor(ModInventoryEntry entry)
        {
            switch (MenuText.EntryTone(entry))
            {
                case MenuTone.Bad: return MenuKit.Hex(0x7A2A1E, 0.9f);
                case MenuTone.Warn: return MenuKit.Hex(0x6E5418, 0.9f);
            }
            return KindKey(entry) switch
            {
                "mods.kind.modstoke" => MenuKit.Hex(0x6E1F24, 0.9f),
                "mods.kind.bepinex" => MenuKit.Hex(0x24465E, 0.9f),
                "mods.kind.redloader" => MenuKit.Hex(0x5E4420, 0.9f),
                _ => MenuKit.Faded(0.1f)
            };
        }

        internal static string StatusName(ModInventoryEntry entry) => (entry?.Status ?? "").ToLowerInvariant() switch
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
