// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Builds the Modstoke Mods menu view model from MenuState and MenuRegistry snapshots.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Modstoke.Loader;

namespace Modstoke.Loader
{
    internal static class MenuViewBuilder
    {
        internal const string PinnedGroup = "pinned";
        internal const string ModsGroup = "mods";
        internal const string ModPagesGroup = "mod-pages";
        internal const string LoaderGroup = "loader";
        internal const string DiagnosticsGroup = "diagnostics";
        internal const string AboutGroup = "about";

        private const string LoaderConfigPrefix = "openacai.loader.";
        private const string SonsPrefix = "openacai.menu.sons.";
        private const string BreadcrumbSeparator = "  >  ";
        private const int CollapseRowThreshold = 12;
        private const int CollapsedRowCount = 8;
        private const int HomeIssueCount = 8;
        private const long SettingTermsLifetimeMs = 2000;
        private static readonly Dictionary<string, (long At, string[] Terms)> SettingTerms =
            new Dictionary<string, (long At, string[] Terms)>(StringComparer.OrdinalIgnoreCase);

        internal static readonly string[] FilterChips =
        {
            MenuProviderIds.AllMods, MenuProviderIds.OpenAcaiMods, MenuProviderIds.RedLoaderMods,
            MenuProviderIds.BepInExPlugins, MenuProviderIds.Libraries, MenuProviderIds.Issues
        };

        private enum Placement
        {
            Hidden,
            Pinned,
            Owned,
            ModPages,
            Loader,
            Diagnostics
        }

        internal static IReadOnlyList<MenuNavItem> BuildNav()
        {
            return BuildNav(MenuState.ViewState, MenuState.Inventory, MenuRegistry.Snapshot(), null);
        }

        /// <summary>
        /// Builds the nav groups. <paramref name="providerTone"/> rates a provider's dot; by default none stands out, since
        /// a provider that is switched off has not failed.
        /// </summary>
        internal static IReadOnlyList<MenuNavItem> BuildNav(MenuViewState state, ModInventoryState inventory,
            IReadOnlyList<MenuProviderSnapshot> providers, Func<MenuProviderSnapshot, MenuTone> providerTone)
        {
            providers = providers ?? Array.Empty<MenuProviderSnapshot>();
            var entries = inventory?.Entries ?? Array.Empty<ModInventoryEntry>();
            var search = (state.SearchText ?? "").Trim();
            var searching = search.Length > 0;
            var tone = providerTone ?? (_ => MenuTone.Neutral);
            var owners = ResolveOwners(providers, entries);
            var visible = MenuState.FilterEntries(entries, ChipFilter(state.Filter))
                .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var visibleKeys = new HashSet<string>(visible.Select(MenuKeys.EntryId), StringComparer.OrdinalIgnoreCase);

            var pinned = new List<MenuProviderSnapshot>();
            var modPages = new List<MenuProviderSnapshot>();
            var loader = new List<MenuProviderSnapshot>();
            var diagnostics = new List<MenuProviderSnapshot>();
            var owned = new Dictionary<string, List<MenuProviderSnapshot>>(StringComparer.OrdinalIgnoreCase);
            foreach (var provider in providers)
            {
                switch (Place(provider, owners))
                {
                    case Placement.Pinned:
                        pinned.Add(provider);
                        break;
                    case Placement.Owned:
                        var ownerKey = MenuKeys.EntryId(owners[provider.Id]);
                        // A page whose mod is filtered out stays reachable under MOD PAGES.
                        if (!visibleKeys.Contains(ownerKey))
                        {
                            modPages.Add(provider);
                            break;
                        }

                        if (!owned.TryGetValue(ownerKey, out var list))
                        {
                            owned[ownerKey] = list = new List<MenuProviderSnapshot>();
                        }

                        list.Add(provider);
                        break;
                    case Placement.ModPages:
                        modPages.Add(provider);
                        break;
                    case Placement.Loader:
                        loader.Add(provider);
                        break;
                    case Placement.Diagnostics:
                        diagnostics.Add(provider);
                        break;
                }
            }

            var items = new List<MenuNavItem>
            {
                new MenuNavItem { Key = MenuKeys.Home, Kind = MenuNavKind.Home, Label = "Home", Target = MenuKeys.Home }
            };
            AddGroup(items, state, PinnedGroup, "PINNED", ProviderItems(pinned, 1, tone, search), searching, false);

            var mods = new List<MenuNavItem>();
            foreach (var entry in visible)
            {
                var key = MenuKeys.Mod(entry);
                var entryMatches = !searching || Matches(search, entry.DisplayName, entry.Id, entry.Author);
                owned.TryGetValue(MenuKeys.EntryId(entry), out var pages);
                var children = ProviderItems(GroupedByLabel(pages), 2, tone, entryMatches ? null : search);
                if (!entryMatches && children.Count == 0)
                {
                    continue;
                }

                mods.Add(new MenuNavItem
                {
                    Key = key,
                    Kind = MenuNavKind.Mod,
                    Label = entry.DisplayName,
                    Detail = MenuText.VersionText(entry),
                    Tone = MenuText.EntryTone(entry),
                    Depth = 1,
                    Collapsed = !searching && children.Count > 0 && state.IsCollapsed(key, false),
                    Children = children,
                    Target = key
                });
            }

            AddGroup(items, state, ModsGroup, "MODS", mods, searching, false);
            AddGroup(items, state, ModPagesGroup, "MOD PAGES", ProviderItems(modPages, 1, tone, search), searching, false);
            AddGroup(items, state, LoaderGroup, "LOADER", ProviderItems(loader, 1, tone, search), searching, false);

            var subgroups = new List<MenuNavItem>();
            AddGroup(subgroups, state, DiagnosticsGroup + "/core", "Loader core",
                ProviderItems(diagnostics.Where(provider => DiagnosticsSubgroup(provider) == 0), 2, tone, search), searching, false, 1);
            AddGroup(subgroups, state, DiagnosticsGroup + "/sons", "Sons runtime",
                ProviderItems(diagnostics.Where(provider => DiagnosticsSubgroup(provider) == 1), 2, tone, search), searching, false, 1);
            AddGroup(items, state, DiagnosticsGroup, "DIAGNOSTICS", subgroups, searching, true);

            var credits = providers.FirstOrDefault(provider => IsId(provider, MenuProviderIds.Credits));
            var creditsLabel = credits?.DisplayName ?? "Credits";
            var about = !searching || Matches(search, creditsLabel, "About")
                ? new[] { new MenuNavItem { Key = MenuKeys.About, Kind = MenuNavKind.About, Label = creditsLabel, Depth = 1, Target = MenuKeys.About } }
                : Array.Empty<MenuNavItem>();
            AddGroup(items, state, AboutGroup, "ABOUT", about, searching, false);
            return items;
        }

        internal static MenuPage BuildPage()
        {
            var state = MenuState.ViewState;
            var providers = MenuRegistry.Snapshot();
            var provider = MenuState.ProviderEntry;
            if (provider != null)
            {
                MenuState.TryGetProviderConfigSnapshot(out var config);
                return BuildProviderPage(state, provider, MenuState.ProviderContent, MenuState.ProviderCommands,
                    MenuState.ProviderInputs, MenuState.ProviderSettings, config, MenuState.ProviderStatus);
            }

            var key = state.LastPage ?? MenuKeys.Home;
            if (string.Equals(key, MenuKeys.About, StringComparison.OrdinalIgnoreCase))
            {
                return BuildAboutPage();
            }

            if (key.StartsWith(MenuKeys.ModPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var entry = MenuState.FindEntry(key.Substring(MenuKeys.ModPrefix.Length));
                if (entry != null)
                {
                    return BuildModPage(state, entry, MenuState.Inventory?.Entries, providers, MenuState.ProviderStatus);
                }
            }

            return BuildHome(state, MenuState.Inventory, providers);
        }

        /// <summary>The trail above the page, such as Mods > mod > page; an open config file ends it, and Home has none.</summary>
        internal static string Breadcrumb(MenuPage page, ModInventoryEntry configEntry)
        {
            const string trail = "Mods";
            if (configEntry != null)
            {
                return trail + BreadcrumbSeparator + configEntry.DisplayName + BreadcrumbSeparator + "Config file";
            }

            if (page == null || page.Kind == MenuPageKind.Home)
            {
                return "";
            }

            if (page.Kind == MenuPageKind.About)
            {
                return trail + BreadcrumbSeparator + "About";
            }

            var provider = MenuState.ProviderEntry;
            if (page.Kind == MenuPageKind.Provider && provider != null)
            {
                var owner = ResolveOwner(provider, MenuRegistry.Snapshot(), MenuState.Inventory?.Entries);
                return trail + BreadcrumbSeparator + (owner != null ? owner.DisplayName + BreadcrumbSeparator : "") + provider.DisplayName;
            }

            return trail + BreadcrumbSeparator + page.Title;
        }

        /// <summary>The filter chip for the session's filter, or All when the filter is not a chip.</summary>
        internal static string ActiveChip()
        {
            return ChipFilter(MenuState.ViewState.Filter);
        }

        /// <summary>The chip's label from its built-in tab provider, upper-cased, as the list view string names it.</summary>
        internal static string ChipViewName(string chip)
        {
            return MenuState.TabLabel(MenuState.TabIndex(chip)).ToUpperInvariant();
        }

        internal static MenuPage BuildHome(MenuViewState state, ModInventoryState inventory, IReadOnlyList<MenuProviderSnapshot> providers)
        {
            providers = providers ?? Array.Empty<MenuProviderSnapshot>();
            var entries = inventory?.Entries ?? Array.Empty<ModInventoryEntry>();
            var summary = inventory?.Summary;
            var issues = entries.Where(entry => !string.IsNullOrWhiteSpace(entry.IssueCode))
                .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            // Counted as the filter chips count, so the strip, the chips and the status line agree.
            var mods = MenuState.FilterEntries(entries, MenuProviderIds.AllMods).Length;
            var modstoke = MenuState.FilterEntries(entries, MenuProviderIds.OpenAcaiMods).Length;
            var libraries = MenuState.FilterEntries(entries, MenuProviderIds.Libraries).Length;
            var hosted = summary?.Hosted ?? 0;
            var vortex = inventory?.VortexDeploymentDetected == true;
            var sections = new List<MenuSection>
            {
                new MenuSection
                {
                    Key = "home/summary",
                    Rows = new[]
                    {
                        new MenuRow
                        {
                            Key = "home/summary/stats",
                            Kind = MenuRowKind.Info,
                            Value = MenuText.Counted(mods, "mod", "mods") + "  |  " + modstoke + " Modstoke  |  "
                                + MenuText.Counted(libraries, "library", "libraries") + "  |  " + hosted + " hosted  |  "
                                + (issues.Length > 0 ? MenuText.Counted(issues.Length, "issue", "issues") : "No issues") + "  |  "
                                + (vortex ? "Vortex detected" : "No Vortex deployment"),
                            Stats = new[]
                            {
                                new MenuStat { Label = "Mods", Value = Count(mods) },
                                new MenuStat { Label = "Modstoke", Value = Count(modstoke) },
                                new MenuStat { Label = "Libraries", Value = Count(libraries) },
                                new MenuStat { Label = "Hosted", Value = Count(hosted) },
                                new MenuStat { Label = "Issues", Value = Count(issues.Length), Tone = issues.Length > 0 ? MenuTone.Warn : MenuTone.Neutral },
                                new MenuStat { Label = "Vortex", Value = vortex ? "Detected" : "None" }
                            }
                        }
                    }
                }
            };

            var issueRows = issues.Take(HomeIssueCount).Select(entry => ModRow(entry, true)).ToList();
            if (issues.Length > HomeIssueCount)
            {
                issueRows.Add(new MenuRow
                {
                    Key = "home/issues/all",
                    Kind = MenuRowKind.Info,
                    Label = "Show all " + issues.Length + " issues",
                    Tooltip = "ISSUES - Show every mod with warnings, missing files, or duplicate IDs.",
                    Control = new MenuControl { Kind = MenuControlKind.Open, Editable = true },
                    Intent = new MenuIntent { Kind = MenuIntentKind.SetFilter, Key = MenuProviderIds.Issues }
                });
            }
            else if (issueRows.Count == 0)
            {
                issueRows.Add(InfoRow("home/issues/none", "No issues were found.", null));
            }

            sections.Add(new MenuSection { Key = "home/issues", Title = "Issues", Rows = issueRows });

            var owners = ResolveOwners(providers, entries);
            var pinned = providers.Where(provider => Place(provider, owners) == Placement.Pinned).Select(ProviderLinkRow).ToArray();
            if (pinned.Length > 0)
            {
                sections.Add(new MenuSection { Key = "home/pinned", Title = "Pinned", Rows = pinned });
            }

            var last = LastVisitedRow(state.LastVisited, entries, providers);
            if (last != null)
            {
                sections.Add(new MenuSection { Key = "home/last", Title = "Last visited", Rows = new[] { last } });
            }

            return new MenuPage
            {
                Key = MenuKeys.Home,
                Kind = MenuPageKind.Home,
                Title = "MODS",
                Sections = Fold(state, sections)
            };
        }

        internal static MenuPage BuildModPage(MenuViewState state, ModInventoryEntry entry, IReadOnlyList<ModInventoryEntry> entries,
            IReadOnlyList<MenuProviderSnapshot> providers, string footerStatus)
        {
            providers = providers ?? Array.Empty<MenuProviderSnapshot>();
            entries = entries ?? Array.Empty<ModInventoryEntry>();
            var pageKey = MenuKeys.Mod(entry);
            var entryId = MenuKeys.EntryId(entry);
            var owners = ResolveOwners(providers, entries);
            var ownedPages = providers.Where(provider => owners.TryGetValue(provider.Id, out var owner)
                    && string.Equals(MenuKeys.EntryId(owner), entryId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var sections = new List<MenuSection>();

            if (!string.IsNullOrWhiteSpace(entry.IssueCode))
            {
                sections.Add(new MenuSection
                {
                    Key = pageKey + "/issue",
                    Title = "Issue",
                    Rows = new[]
                    {
                        new MenuRow
                        {
                            Key = pageKey + "/issue/" + entry.IssueCode,
                            Kind = MenuRowKind.Info,
                            Label = entry.Issue ?? entry.IssueCode,
                            Status = entry.IssueCode,
                            Tone = MenuText.EntryTone(entry) == MenuTone.Bad ? MenuTone.Bad : MenuTone.Warn
                        }
                    }
                });
            }

            var mirrors = ownedPages.Where(IsConfigMirror).ToArray();
            foreach (var mirror in mirrors)
            {
                if (!LoaderConfig.TryGetProviderSnapshotFromMenuProvider(mirror.Id, out var config) || (config.Settings?.Count ?? 0) == 0)
                {
                    continue;
                }

                sections.Add(new MenuSection
                {
                    Key = pageKey + "/" + MenuKeys.Provider(mirror.Id),
                    Title = mirrors.Length == 1 ? "Settings" : mirror.DisplayName,
                    Rows = config.Settings.Where(setting => setting != null).Select(setting => ConfigRow(mirror.Id, setting)).ToArray()
                });
            }

            if (entry.HasConfig)
            {
                sections.Add(new MenuSection
                {
                    Key = pageKey + "/config-file",
                    Title = "Config file",
                    Rows = new[]
                    {
                        new MenuRow
                        {
                            Key = pageKey + "/config-file/edit",
                            Kind = MenuRowKind.FriendlyConfig,
                            Label = "Edit config file",
                            Detail = "Friendly or text editor for the detected file.",
                            Tooltip = "CONFIG - Edit this mod's config file in FRIENDLY or TEXT mode.",
                            Control = new MenuControl { Kind = MenuControlKind.Open, Editable = true },
                            Intent = new MenuIntent { Kind = MenuIntentKind.OpenConfigFile, EntryId = entryId }
                        },
                        InfoRow(pageKey + "/config-file/path", "Path", entry.ConfigPath),
                        InfoRow(pageKey + "/config-file/format", "Format", entry.ConfigFormat),
                        InfoRow(pageKey + "/config-file/source", "Source", entry.ConfigSource)
                    }
                });
            }

            if (entry.LiveSettingsAvailable || entry.ModActionsAvailable)
            {
                var live = new List<MenuRow>();
                if (entry.LiveSettingsAvailable)
                {
                    live.Add(LiveRow(pageKey + "/live/settings", "Mod settings", "Registered mod settings", MenuIntentKind.OpenLiveSettings, entryId));
                }

                if (entry.ModActionsAvailable)
                {
                    live.Add(LiveRow(pageKey + "/live/actions", "Mod actions", "Registered mod actions", MenuIntentKind.OpenModActions, entryId));
                }

                sections.Add(new MenuSection { Key = pageKey + "/live", Title = "Live controls", Rows = live });
            }

            var links = ownedPages.Where(provider => !IsConfigMirror(provider)).Select(ProviderLinkRow).ToArray();
            if (links.Length > 0)
            {
                sections.Add(new MenuSection { Key = pageKey + "/pages", Title = "Pages", Rows = links });
            }

            sections.Add(new MenuSection
            {
                Key = pageKey + "/details",
                Title = "Details",
                Collapsed = sections.Count > 0,
                NoJump = true,
                Rows = DetailRows(pageKey, entry)
            });

            return new MenuPage
            {
                Key = pageKey,
                Kind = MenuPageKind.Mod,
                Title = entry.DisplayName,
                Meta = MenuText.ModMeta(entry),
                Description = MenuText.DetailText(entry),
                Tone = MenuText.EntryTone(entry),
                Sections = Fold(state, sections),
                FooterStatus = footerStatus
            };
        }

        internal static MenuPage BuildProviderPage(MenuViewState state, MenuProviderSnapshot provider, MenuContentSnapshot content,
            CommandProviderSnapshot commands, MenuInputSnapshot inputs, MenuSettingsSnapshot settings, ConfigProviderSnapshot config,
            string footerStatus)
        {
            var pageKey = MenuKeys.Provider(provider.Id);
            var sections = new List<MenuSection>();
            if (config != null)
            {
                // Config-backed pages show their typed editors only.
                foreach (var group in (config.Settings ?? Array.Empty<ConfigValueSnapshot>()).Where(setting => setting != null)
                    .GroupBy(setting => string.IsNullOrWhiteSpace(setting.Section) ? "Settings" : setting.Section))
                {
                    sections.Add(new MenuSection
                    {
                        Key = pageKey + "/settings:" + group.Key,
                        Title = group.Key,
                        Rows = group.Select(setting => ConfigRow(provider.Id, setting)).ToArray()
                    });
                }

                if (sections.Count == 0)
                {
                    sections.Add(NoticeSection(pageKey, "No structured settings are available."));
                }
            }
            else
            {
                if (settings != null)
                {
                    foreach (var group in settings.Settings.GroupBy(setting => setting.Section))
                    {
                        sections.Add(new MenuSection
                        {
                            Key = pageKey + "/settings:" + group.Key,
                            Title = group.Key,
                            Rows = group.Select(setting => SettingRow(provider, settings.Scope, setting)).ToArray()
                        });
                    }
                }

                if (inputs != null && inputs.Fields.Count > 0)
                {
                    sections.Add(new MenuSection
                    {
                        Key = pageKey + "/inputs",
                        Title = "Forms",
                        Rows = inputs.Fields.Select(field => InputRow(provider, inputs.Scope, field)).ToArray()
                    });
                }

                if (content != null)
                {
                    for (var i = 0; i < content.Sections.Count; i++)
                    {
                        sections.Add(ContentSection(state, pageKey, i, content.Sections[i]));
                    }
                }

                if (commands != null && commands.Commands.Count > 0)
                {
                    sections.Add(new MenuSection
                    {
                        Key = pageKey + "/commands",
                        Title = "Actions",
                        Rows = commands.Commands.Where(command => command != null).Select(command => CommandRow(provider, command)).ToArray()
                    });
                }

                if (sections.Count == 0)
                {
                    sections.Add(NoticeSection(pageKey, "No provider detail rows are available yet."));
                }
            }

            sections.Add(new MenuSection
            {
                Key = pageKey + "/details",
                Title = "Details",
                Collapsed = true,
                NoJump = true,
                Rows = ProviderDetailRows(pageKey, provider, config)
            });

            // A config page's content is the loader's own summary of the file, so its subtitle is not a description.
            return new MenuPage
            {
                Key = pageKey,
                Kind = MenuPageKind.Provider,
                Title = content?.Title ?? commands?.Title ?? provider.DisplayName,
                Description = config != null ? provider.Description : content?.Subtitle ?? commands?.Subtitle ?? provider.Description,
                Sections = Fold(state, sections),
                FooterStatus = footerStatus
            };
        }

        internal static MenuPage BuildAboutPage()
        {
            return new MenuPage
            {
                Key = MenuKeys.About,
                Kind = MenuPageKind.About,
                Title = "CREDITS",
                Sections = Fold(MenuState.ViewState, new[]
                {
                    new MenuSection
                    {
                        Key = "about/credits",
                        Title = "Credits",
                        Rows = MenuText.Credits.Select(credit => new MenuRow
                        {
                            Key = "about/credits/" + credit.Project,
                            Kind = MenuRowKind.Credit,
                            Label = credit.Project,
                            Value = credit.Credit,
                            Status = credit.Role,
                            Detail = credit.Notes,
                            Tooltip = credit.Project + " - " + credit.Notes
                        }).ToArray()
                    },
                    new MenuSection
                    {
                        Key = "about/notes",
                        Title = "Licensing",
                        Rows = MenuText.CreditNotes.Select((note, index) => new MenuRow
                        {
                            Key = "about/notes/" + index,
                            Kind = MenuRowKind.Info,
                            Label = note.Text,
                            Tooltip = note.Tooltip
                        }).ToArray()
                    }
                })
            };
        }

        /// <summary>
        /// Makes every titled section foldable from its header, keeping the session's fold state. A section built
        /// collapsed starts folded. Folded sections keep their rows so the header can still show their worst tone.
        /// </summary>
        internal static MenuSection[] Fold(MenuViewState state, IEnumerable<MenuSection> sections)
        {
            return sections.Where(section => section != null)
                .Select(section => string.IsNullOrWhiteSpace(section.Title) || section.Key == null
                    ? section
                    : new MenuSection
                    {
                        Key = section.Key,
                        Title = section.Title,
                        Collapsible = true,
                        Collapsed = state.IsCollapsed(section.Key, section.Collapsed),
                        NoJump = section.NoJump,
                        Rows = section.Rows
                    })
                .ToArray();
        }

        /// <summary>The control for a provider setting, chosen from its declared kind only.</summary>
        internal static MenuControl SettingControl(MenuSettingSnapshot setting, bool editable)
        {
            if (setting == null || !editable)
            {
                return MenuControl.None;
            }

            var kind = setting.Kind == ConfigValueKinds.Boolean ? MenuControlKind.Toggle
                : setting.Kind == ConfigValueKinds.Choice ? MenuControlKind.Choice
                : IsBoundedNumber(setting.Kind, setting.Minimum, setting.Maximum) ? MenuControlKind.Slider
                : MenuControlKind.TextApply;
            return new MenuControl
            {
                Kind = kind,
                Options = setting.Options,
                Min = setting.Minimum,
                Max = setting.Maximum,
                Step = setting.Step,
                MaxLength = MenuSettingSnapshot.MaxValueLength,
                Editable = true,
                ValueKind = setting.Kind
            };
        }

        /// <summary>The control for a config value, chosen from its declared kind only; a choice without options takes text.</summary>
        internal static MenuControl ConfigControl(ConfigValueSnapshot setting)
        {
            if (setting == null || !setting.CanEdit)
            {
                return MenuControl.None;
            }

            var valueKind = (setting.Kind ?? ConfigValueKinds.Text).Trim().ToLowerInvariant();
            var options = setting.Options.Where(option => !string.IsNullOrWhiteSpace(option)).ToArray();
            var kind = valueKind == ConfigValueKinds.Boolean ? MenuControlKind.Toggle
                : valueKind == ConfigValueKinds.Choice && options.Length > 0 ? MenuControlKind.Choice
                : IsBoundedNumber(valueKind, setting.Minimum, setting.Maximum) ? MenuControlKind.Slider
                : MenuControlKind.TextApply;
            return new MenuControl
            {
                Kind = kind,
                Options = options,
                Min = setting.Minimum,
                Max = setting.Maximum,
                Step = setting.Step,
                Editable = true,
                ValueKind = valueKind
            };
        }

        /// <summary>
        /// The inventory entry that owns a non-built-in provider: its declared owner, then for a config mirror the
        /// owner of the longest provider or mod ID that dot-prefixes its config ID, then the only entry whose name or author
        /// its Source names.
        /// </summary>
        internal static ModInventoryEntry ResolveOwner(MenuProviderSnapshot provider, IReadOnlyList<MenuProviderSnapshot> providers,
            IReadOnlyList<ModInventoryEntry> entries)
        {
            return ResolveOwner(provider, providers ?? Array.Empty<MenuProviderSnapshot>(), entries ?? Array.Empty<ModInventoryEntry>(), true);
        }

        private static ModInventoryEntry ResolveOwner(MenuProviderSnapshot provider, IReadOnlyList<MenuProviderSnapshot> providers,
            IReadOnlyList<ModInventoryEntry> entries, bool followConfig)
        {
            if (provider == null || provider.BuiltIn)
            {
                return null;
            }

            var declared = FindEntry(entries, provider.OwnerModId);
            if (declared != null)
            {
                return declared;
            }

            if (followConfig && LoaderConfig.TryGetProviderIdFromMenuProviderId(provider.Id, out var configId))
            {
                var parent = providers
                    .Where(candidate => candidate != provider && IsDotPrefix(candidate.Id, configId))
                    .OrderByDescending(candidate => candidate.Id.Length)
                    .FirstOrDefault();
                var owner = parent == null ? null : ResolveOwner(parent, providers, entries, false);
                if (owner != null)
                {
                    return owner;
                }

                owner = entries
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.Id) && IsDotPrefix(entry.Id, configId))
                    .OrderByDescending(entry => entry.Id.Length)
                    .FirstOrDefault();
                if (owner != null)
                {
                    return owner;
                }
            }

            var source = provider.Source?.Trim();
            if (string.IsNullOrEmpty(source))
            {
                return null;
            }

            var matches = entries.Where(entry => IsSourceName(entry.Name, source)
                    || string.Equals(entry.Author?.Trim(), source, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        // Sources often credit the publisher, as in "Modstoke Client by OpenACAI" for the mod named "Modstoke Client".
        private static bool IsSourceName(string name, string source)
        {
            name = name?.Trim();
            return !string.IsNullOrEmpty(name)
                && (string.Equals(name, source, StringComparison.OrdinalIgnoreCase)
                    || source.StartsWith(name + " by ", StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<string, ModInventoryEntry> ResolveOwners(IReadOnlyList<MenuProviderSnapshot> providers,
            IReadOnlyList<ModInventoryEntry> entries)
        {
            var owners = new Dictionary<string, ModInventoryEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var provider in providers)
            {
                var owner = ResolveOwner(provider, providers, entries, true);
                if (owner != null)
                {
                    owners[provider.Id] = owner;
                }
            }

            return owners;
        }

        private static Placement Place(MenuProviderSnapshot provider, Dictionary<string, ModInventoryEntry> owners)
        {
            if (provider == null || IsId(provider, MenuProviderIds.ModConfig))
            {
                return Placement.Hidden;
            }

            if (provider.BuiltIn)
            {
                if (IsKind(provider, MenuProviderKinds.Status))
                {
                    return Placement.Diagnostics;
                }

                return IsKind(provider, MenuProviderKinds.Settings) || IsKind(provider, MenuProviderKinds.CommandGroup)
                    ? Placement.Loader
                    : Placement.Hidden;
            }

            if (LoaderConfig.TryGetProviderIdFromMenuProviderId(provider.Id, out var configId)
                && configId.StartsWith(LoaderConfigPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return Placement.Loader;
            }

            if (IsKind(provider, MenuProviderKinds.Tab))
            {
                return Placement.Pinned;
            }

            return owners.ContainsKey(provider.Id) ? Placement.Owned : Placement.ModPages;
        }

        private static int DiagnosticsSubgroup(MenuProviderSnapshot provider)
        {
            if (provider.Id.StartsWith(SonsPrefix, StringComparison.OrdinalIgnoreCase)) return 1;
            return 0;
        }

        private static void AddGroup(List<MenuNavItem> items, MenuViewState state, string name, string label,
            IReadOnlyList<MenuNavItem> children, bool searching, bool collapsedByDefault, int depth = 0)
        {
            if (children.Count == 0)
            {
                return;
            }

            var key = MenuKeys.Group(name);
            var tone = MenuTone.Neutral;
            foreach (var child in children)
            {
                tone = MenuText.WorseTone(tone, child.Tone);
            }

            items.Add(new MenuNavItem
            {
                Key = key,
                Kind = MenuNavKind.Group,
                Label = label,
                Detail = Count(LeafCount(children)),
                Tone = tone,
                Depth = depth,
                // A search expands every group that has a match.
                Collapsed = !searching && state.IsCollapsed(key, collapsedByDefault),
                Children = children
            });
        }

        private static IReadOnlyList<MenuNavItem> ProviderItems(IEnumerable<MenuProviderSnapshot> providers, int depth,
            Func<MenuProviderSnapshot, MenuTone> tone, string search)
        {
            if (providers == null)
            {
                return Array.Empty<MenuNavItem>();
            }

            var searching = !string.IsNullOrWhiteSpace(search);
            return providers
                .Where(provider => !searching || Matches(search, provider.DisplayName, provider.Source) || Matches(search, SettingSearchTerms(provider)))
                .Select(provider => new MenuNavItem
                {
                    Key = MenuKeys.Provider(provider.Id),
                    Kind = MenuNavKind.Provider,
                    Label = provider.DisplayName,
                    Detail = provider.Group,
                    Tone = tone(provider),
                    Depth = depth,
                    Target = MenuKeys.Provider(provider.Id)
                })
                .ToArray();
        }

        // Keeps pages that share a Group label together, in registry order otherwise.
        private static IEnumerable<MenuProviderSnapshot> GroupedByLabel(IEnumerable<MenuProviderSnapshot> providers)
        {
            return providers?.GroupBy(provider => provider.Group ?? "", StringComparer.OrdinalIgnoreCase).SelectMany(group => group);
        }

        // Search runs on every keystroke and providers build their settings on demand, so the labels and keys are kept briefly.
        private static string[] SettingSearchTerms(MenuProviderSnapshot provider)
        {
            var now = Environment.TickCount64;
            if (SettingTerms.TryGetValue(provider.Id, out var cached) && now - cached.At < SettingTermsLifetimeMs)
            {
                return cached.Terms;
            }

            var terms = new List<string>();
            if (LoaderConfig.TryGetProviderSnapshotFromMenuProvider(provider.Id, out var config))
            {
                foreach (var setting in config.Settings ?? Array.Empty<ConfigValueSnapshot>())
                {
                    if (setting != null)
                    {
                        terms.Add(setting.DisplayName);
                        terms.Add(setting.Key);
                    }
                }
            }
            else if (provider.HasSettings && MenuRegistry.TryGetProviderSettings(provider.Id, out var settings))
            {
                foreach (var setting in settings.Settings)
                {
                    terms.Add(setting.DisplayName);
                    terms.Add(setting.Key);
                }
            }

            var result = terms.ToArray();
            SettingTerms[provider.Id] = (now, result);
            return result;
        }

        private static int LeafCount(IEnumerable<MenuNavItem> items)
        {
            return items.Sum(item => item.Kind == MenuNavKind.Group ? LeafCount(item.Children) : 1);
        }

        private static MenuSection ContentSection(MenuViewState state, string pageKey, int index, MenuSectionSnapshot section)
        {
            var key = pageKey + "/content:" + index;
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            var rows = new List<MenuRow>();
            foreach (var item in section.Rows)
            {
                occurrences.TryGetValue(item.Label, out var occurrence);
                occurrences[item.Label] = occurrence + 1;
                rows.Add(new MenuRow
                {
                    Key = MenuKeys.Content(index, section.Title, item.Label, occurrence),
                    Kind = MenuRowKind.Content,
                    Label = item.Label,
                    Value = item.Value,
                    Status = MenuText.NotableStatus(item.Status),
                    Tone = MenuText.StatusTone(item.Status),
                    Detail = item.Detail,
                    Tooltip = section.Title + " - " + item.Label
                });
            }

            if (rows.Count == 0)
            {
                rows.Add(InfoRow(key + "/empty", "No rows in this section.", null));
            }

            if (rows.Count > CollapseRowThreshold)
            {
                // One row key for both states, so the selection stays on it when it is used.
                var rowsKey = key + "/rows";
                var hidden = rows.Count - CollapsedRowCount;
                var shortened = state.IsCollapsed(rowsKey, true);
                if (shortened)
                {
                    rows.RemoveRange(CollapsedRowCount, hidden);
                }

                rows.Add(new MenuRow
                {
                    Key = rowsKey,
                    Kind = MenuRowKind.Info,
                    Label = shortened ? "Show " + hidden + " more" : "Show fewer",
                    Control = new MenuControl { Kind = MenuControlKind.Open, Editable = true },
                    Intent = new MenuIntent { Kind = shortened ? MenuIntentKind.Expand : MenuIntentKind.Collapse, Key = rowsKey }
                });
            }

            return new MenuSection { Key = key, Title = section.Title, Rows = rows };
        }

        private static MenuRow SettingRow(MenuProviderSnapshot provider, Guid scope, MenuSettingSnapshot setting)
        {
            var editable = setting.CanEdit && provider.Enabled;
            var status = editable ? "READY" : "READONLY";
            return new MenuRow
            {
                Key = MenuKeys.Setting(scope, setting.Key),
                Kind = MenuRowKind.Setting,
                Label = setting.DisplayName,
                Value = setting.Value,
                Status = MenuText.NotableStatus(status),
                Tone = MenuText.StatusTone(status),
                Detail = MenuText.SettingDetail(setting),
                Tooltip = MenuText.SettingTooltip(setting),
                Control = SettingControl(setting, editable),
                Intent = new MenuIntent { Kind = MenuIntentKind.ApplySetting, ProviderId = provider.Id, Scope = scope, Key = setting.Key }
            };
        }

        private static MenuRow ConfigRow(string providerId, ConfigValueSnapshot setting)
        {
            var status = MenuText.ProviderSettingStatus(setting);
            return new MenuRow
            {
                Key = MenuKeys.Config(providerId, setting.Key),
                Kind = MenuRowKind.ConfigValue,
                Label = setting.DisplayName,
                Value = setting.Value,
                Status = MenuText.NotableStatus(status),
                Tone = MenuText.StatusTone(status),
                Detail = MenuText.ProviderSettingDetail(setting),
                Tooltip = MenuText.ProviderSettingTooltip(setting),
                Control = ConfigControl(setting),
                Intent = new MenuIntent { Kind = MenuIntentKind.ApplyConfigValue, ProviderId = providerId, Key = setting.Key }
            };
        }

        private static MenuRow InputRow(MenuProviderSnapshot provider, Guid scope, MenuInputFieldSnapshot field)
        {
            return new MenuRow
            {
                Key = MenuKeys.Input(scope, field.Id),
                Kind = MenuRowKind.Input,
                Label = field.DisplayName,
                Value = field.Value,
                Detail = field.Description,
                Tooltip = field.DisplayName + " - " + field.Description,
                Control = new MenuControl
                {
                    Kind = MenuControlKind.InputLive,
                    MaxLength = field.MaximumLength,
                    Editable = field.Enabled && provider.Enabled,
                    ValueKind = ConfigValueKinds.Text
                },
                Intent = new MenuIntent { Kind = MenuIntentKind.SetInput, ProviderId = provider.Id, Scope = scope, FieldId = field.Id }
            };
        }

        // A disabled command reads as information, such as a last-result line, and keeps its status only when it is a problem.
        private static MenuRow CommandRow(MenuProviderSnapshot provider, CommandSnapshot command)
        {
            var status = MenuText.ProviderCommandStatus(command);
            if (!command.Enabled)
            {
                var reported = command.Status?.Trim().ToUpperInvariant() ?? "";
                var tone = MenuText.StatusTone(reported);
                status = tone == MenuTone.Warn || tone == MenuTone.Bad ? reported : "";
            }

            var runnable = command.Enabled && !command.RequiresConfirmation;
            return new MenuRow
            {
                Key = MenuKeys.Command(command.Id),
                Kind = MenuRowKind.Command,
                Label = command.DisplayName,
                Value = command.Enabled && !runnable ? "Run in console (F5): " + provider.Id + "/" + command.Id : "",
                Status = MenuText.NotableStatus(status),
                Tone = MenuText.StatusTone(status),
                Detail = command.Description,
                Tooltip = MenuText.ProviderCommandTooltip(command),
                Control = runnable ? new MenuControl { Kind = MenuControlKind.Run, Editable = true } : MenuControl.None,
                Intent = new MenuIntent { Kind = MenuIntentKind.RunCommand, ProviderId = provider.Id, CommandId = command.Id }
            };
        }

        // A shortcut to a mod drops HOSTED, which the nav and the mod page already show.
        private static MenuRow ModRow(ModInventoryEntry entry, bool showHosted)
        {
            var tone = MenuText.EntryTone(entry);
            var status = !showHosted && tone == MenuTone.Info ? "" : MenuText.NotableStatus(MenuText.StatusText(entry));
            return new MenuRow
            {
                Key = MenuKeys.Mod(entry),
                Kind = MenuRowKind.ModEntry,
                Label = entry.DisplayName,
                Value = MenuText.VersionText(entry),
                Status = status,
                Tone = tone,
                Detail = MenuText.DetailText(entry),
                Tooltip = entry.DisplayName + " - " + MenuText.TypeText(entry),
                Control = new MenuControl { Kind = MenuControlKind.Open, Editable = true },
                Intent = new MenuIntent { Kind = MenuIntentKind.Open, Key = MenuKeys.Mod(entry), EntryId = MenuKeys.EntryId(entry) }
            };
        }

        private static MenuRow ProviderLinkRow(MenuProviderSnapshot provider)
        {
            var key = MenuKeys.Provider(provider.Id);
            var status = provider.Enabled ? "READY" : "OFF";
            return new MenuRow
            {
                Key = key,
                Kind = MenuRowKind.ProviderLink,
                Label = provider.DisplayName,
                Status = MenuText.NotableStatus(status),
                Tone = MenuText.StatusTone(status),
                Detail = provider.Description,
                Tooltip = provider.DisplayName + " - " + provider.Description,
                Control = new MenuControl { Kind = MenuControlKind.Open, Editable = true },
                Intent = new MenuIntent { Kind = MenuIntentKind.Open, Key = key, ProviderId = provider.Id }
            };
        }

        private static MenuRow LiveRow(string key, string label, string tooltip, MenuIntentKind intent, string entryId)
        {
            return new MenuRow
            {
                Key = key,
                Kind = MenuRowKind.Info,
                Label = label,
                Tooltip = tooltip,
                Control = new MenuControl { Kind = MenuControlKind.Open, Editable = true },
                Intent = new MenuIntent { Kind = intent, EntryId = entryId }
            };
        }

        private static MenuRow LastVisitedRow(string pageKey, IReadOnlyList<ModInventoryEntry> entries, IReadOnlyList<MenuProviderSnapshot> providers)
        {
            if (string.IsNullOrWhiteSpace(pageKey))
            {
                return null;
            }

            if (pageKey.StartsWith(MenuKeys.ProviderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var id = pageKey.Substring(MenuKeys.ProviderPrefix.Length);
                var provider = providers.FirstOrDefault(candidate => IsId(candidate, id));
                return provider == null ? null : ProviderLinkRow(provider);
            }

            if (pageKey.StartsWith(MenuKeys.ModPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var entry = MenuState.FindEntry(entries, pageKey.Substring(MenuKeys.ModPrefix.Length));
                return entry == null ? null : ModRow(entry, false);
            }

            return string.Equals(pageKey, MenuKeys.About, StringComparison.OrdinalIgnoreCase)
                ? new MenuRow
                {
                    Key = MenuKeys.About,
                    Kind = MenuRowKind.Info,
                    Label = "Credits",
                    Control = new MenuControl { Kind = MenuControlKind.Open, Editable = true },
                    Intent = new MenuIntent { Kind = MenuIntentKind.Open, Key = MenuKeys.About }
                }
                : null;
        }

        private static MenuRow[] DetailRows(string pageKey, ModInventoryEntry entry)
        {
            var rows = new List<MenuRow>();
            void Add(string label, string value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    rows.Add(InfoRow(pageKey + "/details/" + label, label, value));
                }
            }

            Add("Id", entry.Id);
            Add("Loader type", entry.LoaderType);
            Add("Manifest type", entry.ManifestType);
            Add("Source family", entry.SourceFamily);
            Add("Install source", entry.InstallSource);
            Add("Store", entry.Store);
            Add("Vortex package", entry.VortexPackage);
            Add("Enabled", YesNo(entry.Enabled));
            Add("Hosted by Modstoke", YesNo(entry.HostedByOpenAcai));
            Add("Duplicate ID", YesNo(entry.DuplicateId));
            Add("Issue code", entry.IssueCode);
            Add("Issue", entry.Issue);
            Add("Config path", entry.ConfigPath);
            Add("Config format", entry.ConfigFormat);
            Add("Assembly path", entry.AssemblyPath);
            Add("Assembly version", entry.AssemblyVersion);
            Add("Assembly size", entry.AssemblySize?.ToString(CultureInfo.InvariantCulture));
            Add("Manifest path", entry.ManifestPath);
            Add("Package path", entry.PackagePath);
            return rows.ToArray();
        }

        private static MenuRow[] ProviderDetailRows(string pageKey, MenuProviderSnapshot provider, ConfigProviderSnapshot config)
        {
            var rows = new List<MenuRow>
            {
                InfoRow(pageKey + "/details/id", "Provider ID", provider.Id),
                InfoRow(pageKey + "/details/source", "Source", provider.Source),
                InfoRow(pageKey + "/details/kind", "Kind", provider.Kind),
                InfoRow(pageKey + "/details/surface", "Surface", MenuText.ProviderSurfaceDetail(provider)),
                InfoRow(pageKey + "/details/authorization", "Authorization", provider.RequiresAuthorization ? "Required" : "Not required"),
                InfoRow(pageKey + "/details/enabled", "Enabled", YesNo(provider.Enabled))
            };
            if (config != null)
            {
                rows.Add(InfoRow(pageKey + "/details/settings", "Settings", Count(config.SettingCount)));
            }

            return rows.ToArray();
        }

        private static MenuSection NoticeSection(string pageKey, string text)
        {
            return new MenuSection { Key = pageKey + "/notice", Title = "", Rows = new[] { InfoRow(pageKey + "/notice/text", text, null) } };
        }

        private static MenuRow InfoRow(string key, string label, string value)
        {
            return new MenuRow { Key = key, Kind = MenuRowKind.Info, Label = label, Value = value ?? "" };
        }

        private static string ChipFilter(string filter)
        {
            return Array.Find(FilterChips, chip => string.Equals(chip, filter, StringComparison.OrdinalIgnoreCase)) ?? MenuProviderIds.AllMods;
        }

        private static bool IsConfigMirror(MenuProviderSnapshot provider)
        {
            return LoaderConfig.TryGetProviderIdFromMenuProviderId(provider.Id, out _);
        }

        private static bool IsBoundedNumber(string kind, double? minimum, double? maximum)
        {
            return (kind == ConfigValueKinds.Integer || kind == ConfigValueKinds.Number) && minimum != null && maximum != null;
        }

        private static bool IsDotPrefix(string prefix, string value)
        {
            return !string.IsNullOrWhiteSpace(prefix)
                && (string.Equals(prefix, value, StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));
        }

        private static ModInventoryEntry FindEntry(IReadOnlyList<ModInventoryEntry> entries, string id)
        {
            return string.IsNullOrWhiteSpace(id)
                ? null
                : entries.FirstOrDefault(entry => string.Equals(entry.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsId(MenuProviderSnapshot provider, string id)
        {
            return string.Equals(provider?.Id, id, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsKind(MenuProviderSnapshot provider, string kind)
        {
            return string.Equals(provider.Kind, kind, StringComparison.OrdinalIgnoreCase);
        }

        private static bool Matches(string search, params string[] values)
        {
            return values.Any(value => value != null && value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string Count(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string YesNo(bool value)
        {
            return value ? "Yes" : "No";
        }
    }
}
