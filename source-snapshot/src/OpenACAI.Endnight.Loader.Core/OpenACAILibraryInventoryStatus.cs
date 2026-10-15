// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Built-in public status provider for the OpenACAI-owned mod/library boundary.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenACAI.Loader;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenACAILibraryInventoryStatus
    {
        internal static void Initialize()
        {
            if (!OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("OpenACAI runtime library inventory provider could not be registered.");
            }
        }

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            var inventory = OpenACAIModding.ModInventory ?? OpenACAIModInventorySnapshot.Empty();
            var catalog = OpenACAIModding.ModInventoryCatalog ?? OpenACAIModInventoryCatalogSnapshot.Empty();
            return new OpenACAIMenuContentSnapshot(
                "Runtime Libraries",
                "OpenACAI-owned runtime view of SDK/helper libraries separate from loadable mods.",
                new[]
                {
                    CreateBoundarySection(inventory, catalog),
                    CreateCatalogSection(catalog),
                    CreateLibraryEntriesSection(inventory)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateBoundarySection(OpenACAIModInventorySnapshot inventory, OpenACAIModInventoryCatalogSnapshot catalog)
        {
            OpenACAIModInventoryCatalogSection libraries;
            var librarySectionReady = catalog.TryGetSection("libraries", out libraries)
                && libraries.Count == catalog.LibraryCount
                && catalog.LibraryCount == inventory.Libraries.Count;
            var modsExcludeLibraries = inventory.Mods.All(entry => !entry.IsLibrary);
            var librariesAreLibraries = inventory.Libraries.All(entry => entry.IsLibrary);

            return new OpenACAIMenuSectionSnapshot(
                "Boundary",
                new[]
                {
                    Row(
                        "Service",
                        OpenACAIMenuProviderIds.RuntimeLibraries,
                        "READY",
                        "Built-in OpenACAI provider for library inventory status."),
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

        private static OpenACAIMenuSectionSnapshot CreateCatalogSection(OpenACAIModInventoryCatalogSnapshot catalog)
        {
            var rows = new List<OpenACAIMenuRowSnapshot>();
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
                rows.Add(Row("Catalog", "Unavailable", "CHECK", "OpenACAI mod inventory catalog has not been published yet."));
            }

            return new OpenACAIMenuSectionSnapshot("Catalog Sections", rows);
        }

        private static OpenACAIMenuSectionSnapshot CreateLibraryEntriesSection(OpenACAIModInventorySnapshot inventory)
        {
            var libraries = inventory.Libraries
                .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.AssemblyPath ?? entry.ManifestPath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var rows = new List<OpenACAIMenuRowSnapshot>();

            foreach (var library in libraries.Take(12))
            {
                rows.Add(Row(
                    library.DisplayName,
                    EntryValue(library),
                    EntryStatus(library),
                    EntryDetail(library)));
            }

            if (libraries.Length > 12)
            {
                rows.Add(Row(
                    "Additional Libraries",
                    (libraries.Length - 12).ToString(),
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

            return new OpenACAIMenuSectionSnapshot("Library Entries", rows);
        }

        private static string SectionStatus(OpenACAIModInventoryCatalogSection section)
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

        private static string EntryValue(OpenACAIModInfo entry)
        {
            var type = TypeLabel(entry);
            return string.IsNullOrWhiteSpace(entry.AssemblyVersion)
                ? type
                : type + " " + entry.AssemblyVersion;
        }

        private static string EntryStatus(OpenACAIModInfo entry)
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

            return entry.HasAssembly ? "READY" : "CHECK";
        }

        private static string EntryDetail(OpenACAIModInfo entry)
        {
            if (entry == null)
            {
                return "No library entry was published.";
            }

            var parts = new[]
            {
                string.IsNullOrWhiteSpace(entry.SourceFamily) ? null : entry.SourceFamily,
                string.IsNullOrWhiteSpace(entry.ExpectedLocation) ? null : entry.ExpectedLocation,
                string.IsNullOrWhiteSpace(entry.Store) ? null : entry.Store,
                ShortPath(entry.AssemblyPath ?? entry.ManifestPath ?? entry.PackagePath),
                string.IsNullOrWhiteSpace(entry.Issue) ? null : entry.Issue
            };
            return string.Join(" | ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        }

        private static string TypeLabel(OpenACAIModInfo entry)
        {
            if (entry == null)
            {
                return "library";
            }

            if (string.Equals(entry.LoaderType, OpenACAIModLoaderTypes.OpenAcaiLibrary, StringComparison.OrdinalIgnoreCase))
            {
                return "OpenACAI library";
            }

            if (string.Equals(entry.LoaderType, OpenACAIModLoaderTypes.RedLoaderLibrary, StringComparison.OrdinalIgnoreCase))
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

        private static OpenACAIMenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new OpenACAIMenuRowSnapshot(label, value, status, detail);
        }

        private sealed class Provider : IOpenACAIMenuContentProvider
        {
            public string Id => OpenACAIMenuProviderIds.RuntimeLibraries;
            public string DisplayName => "Runtime Libraries";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 705;
            public string Source => "Modstoke Loader";
            public string Description => "Show SDK/runtime helper libraries separately from loadable mods.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAILibraryInventoryStatus.CreateContent();
            }
        }
    }
}
