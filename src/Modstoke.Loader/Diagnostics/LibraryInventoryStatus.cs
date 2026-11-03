// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Built-in public status provider for the OpenACAI-owned mod/library boundary.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Modstoke.Loader;

namespace Modstoke.Loader
{
    internal static class LibraryInventoryStatus
    {
        internal static void Initialize()
        {
            if (!MenuRegistry.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                RuntimeEntryPoint.LogSource?.LogWarning("OpenACAI runtime library inventory provider could not be registered.");
            }
        }

        private static MenuContentSnapshot CreateContent()
        {
            var inventory = Modding.ModInventory ?? ModInventorySnapshot.Empty();
            var catalog = Modding.ModInventoryCatalog ?? ModInventoryCatalogSnapshot.Empty();
            return new MenuContentSnapshot(
                "Runtime Libraries",
                "Modstoke runtime view of SDK/helper libraries separate from loadable mods.",
                new[]
                {
                    CreateBoundarySection(inventory, catalog),
                    CreateCatalogSection(catalog),
                    CreateLibraryEntriesSection(inventory)
                });
        }

        private static MenuSectionSnapshot CreateBoundarySection(ModInventorySnapshot inventory, ModInventoryCatalogSnapshot catalog)
        {
            ModInventoryCatalogSection libraries;
            var librarySectionReady = catalog.TryGetSection("libraries", out libraries)
                && libraries.Count == catalog.LibraryCount
                && catalog.LibraryCount == inventory.Libraries.Count;
            var modsExcludeLibraries = inventory.Mods.All(entry => !entry.IsLibrary);
            var librariesAreLibraries = inventory.Libraries.All(entry => entry.IsLibrary);

            return new MenuSectionSnapshot(
                "Boundary",
                new[]
                {
                    Row(
                        "Service",
                        MenuProviderIds.RuntimeLibraries,
                        "READY",
                        "Built-in Modstoke provider for library inventory status."),
                    Row(
                        "Mods",
                        inventory.Mods.Count + " entries",
                        modsExcludeLibraries ? "READY" : "CHECK",
                        "Loadable/user-facing mod views exclude SDK/runtime libraries."),
                    Row(
                        "Libraries",
                        inventory.Libraries.Count + " entries",
                        librarySectionReady && librariesAreLibraries ? "READY" : "CHECK",
                        "SDK/runtime helper libraries are cataloged through the Libraries section."),
                    Row(
                        "Catalog",
                        catalog.LibraryCount + " libraries",
                        librarySectionReady ? "READY" : "CHECK",
                        "Catalog library count matches the public library inventory."),
                    Row(
                        "Hosted Mods",
                        inventory.HostedEntries.Count.ToString(),
                        inventory.HostedEntries.Count > 0 ? "READY" : "INFO",
                        "Hosted entries remain mods; libraries are not hosted as user-facing mods."),
                    Row(
                        "Vortex",
                        inventory.VortexDeploymentDetected ? "Detected" : "Not detected",
                        "INFO",
                        string.IsNullOrWhiteSpace(inventory.VortexStagingPath)
                            ? "No Vortex staging path was published."
                            : inventory.VortexStagingPath)
                });
        }

        private static MenuSectionSnapshot CreateCatalogSection(ModInventoryCatalogSnapshot catalog)
        {
            var rows = new List<MenuRowSnapshot>();
            foreach (var section in catalog.Sections)
            {
                rows.Add(Row(
                    section.DisplayName,
                    section.Count + " entries",
                    SectionStatus(section),
                    section.Description));
            }

            if (rows.Count == 0)
            {
                rows.Add(Row("Catalog", "Unavailable", "CHECK", "Modstoke mod inventory catalog has not been published yet."));
            }

            return new MenuSectionSnapshot("Catalog Sections", rows);
        }

        private static MenuSectionSnapshot CreateLibraryEntriesSection(ModInventorySnapshot inventory)
        {
            var libraries = inventory.Libraries
                .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.AssemblyPath ?? entry.ManifestPath ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            var rows = new List<MenuRowSnapshot>();

            foreach (var library in libraries.Take(12))
            {
                rows.Add(Row(
                    library.DisplayName,
                    EntryValue(library),
                    EntryStatus(library),
                    EntryDetail(library)));
            }

            if (inventory.Libraries.Count > 12)
            {
                rows.Add(Row(
                    "Additional Libraries",
                    (inventory.Libraries.Count - 12).ToString(),
                    "INFO",
                    "Open the Libraries tab for the complete inventory."));
            }

            if (rows.Count == 0)
            {
                rows.Add(Row(
                    "Libraries",
                    "None",
                    "READY",
                    "No SDK/runtime helper libraries are currently installed."));
            }

            return new MenuSectionSnapshot("Library Entries", rows);
        }

        private static string SectionStatus(ModInventoryCatalogSection section)
        {
            if (section == null)
            {
                return "CHECK";
            }

            if (string.Equals(section.Id, "issues", StringComparison.OrdinalIgnoreCase))
            {
                return section.Count == 0 ? "READY" : "CHECK";
            }

            return "INFO";
        }

        private static string EntryValue(ModInfo entry)
        {
            var type = TypeLabel(entry);
            return string.IsNullOrWhiteSpace(entry.AssemblyVersion)
                ? type
                : type + " " + entry.AssemblyVersion;
        }

        private static string EntryStatus(ModInfo entry)
        {
            if (entry == null)
            {
                return "CHECK";
            }

            if (!entry.Enabled)
            {
                return "OFF";
            }

            if (entry.HasIssue)
            {
                return string.Equals(entry.IssueCode, "missing-assembly", StringComparison.OrdinalIgnoreCase) ? "FAILED" : "CHECK";
            }

            if (entry.DataOnly)
            {
                return "INFO";
            }

            return entry.HasAssembly ? "READY" : "CHECK";
        }

        private static string EntryDetail(ModInfo entry)
        {
            if (entry == null)
            {
                return "No library entry was published.";
            }

            var parts = new[]
            {
                entry.SourceFamily,
                entry.ExpectedLocation,
                entry.Store,
                ShortPath(entry.AssemblyPath ?? entry.ManifestPath ?? entry.PackagePath),
                entry.Issue
            };
            return string.Join(" | ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        }

        private static string TypeLabel(ModInfo entry)
        {
            if (entry == null)
            {
                return "library";
            }

            if (string.Equals(entry.LoaderType, ModLoaderTypes.OpenAcaiLibrary, StringComparison.OrdinalIgnoreCase))
            {
                return "Modstoke library";
            }

            if (string.Equals(entry.LoaderType, ModLoaderTypes.RedLoaderLibrary, StringComparison.OrdinalIgnoreCase))
            {
                return "RedLoader library shim";
            }

            return string.IsNullOrWhiteSpace(entry.LoaderType) ? "library" : entry.LoaderType;
        }

        private static string ShortPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return Path.GetFileName(trimmed);
            }
            catch
            {
                return path;
            }
        }

        private static MenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new MenuRowSnapshot(label, value, status, detail);
        }

        private sealed class Provider : IMenuContentProvider
        {
            public string Id => MenuProviderIds.RuntimeLibraries;
            public string DisplayName => "Runtime Libraries";
            public string Kind => MenuProviderKinds.Status;
            public int Priority => 705;
            public string Source => "Modstoke Loader";
            public string Description => "Show SDK/runtime helper libraries separately from loadable mods.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public MenuContentSnapshot CreateContent()
            {
                return LibraryInventoryStatus.CreateContent();
            }
        }
    }
}
