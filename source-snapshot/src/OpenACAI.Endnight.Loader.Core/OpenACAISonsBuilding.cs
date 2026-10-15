// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned Sons building/blueprint planning surface. This publishes
// deterministic blueprint roots and validates placement intent without
// depending on RedLoader/SonsSdk building globals or mutating in-world
// construction state before the path is verified.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;

namespace OpenACAI.Sons
{
    public static class OpenACAISonsBuilding
    {
        public const string ServiceKey = "openacai.sons.building";
        public const int MaxBlueprintDefinitionCount = 512;
        public const int MaxBlueprintIdLength = 96;
        public const int MaxListedBlueprintFiles = 256;

        internal const string OpenACAIBlueprintsDirectoryName = "Blueprints";
        internal const string OpenACAIModBlueprintDirectoryName = "Mods";

        private static readonly bool RuntimePlacementEnabledValue = false;
        private static readonly bool RuntimeBookIntegrationEnabledValue = false;
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, OpenACAISonsBlueprintDefinitionSnapshot> RegisteredBlueprints =
            new Dictionary<string, OpenACAISonsBlueprintDefinitionSnapshot>(StringComparer.OrdinalIgnoreCase);
        private static readonly string[] SupportedPlanKindNames =
        {
            "blueprint-item",
            "custom-blueprint",
            "blueprint-file"
        };

        private static readonly string[] BlueprintFileExtensions =
        {
            ".gltf",
            ".glb",
            ".json",
            ".blueprint"
        };

        private static OpenACAISonsBuildingSnapshot _state = OpenACAISonsBuildingSnapshot.CreateUnknown();
        private static bool _initialized;
        private static int _planRequestCount;
        private static int _acceptedPlanCount;
        private static int _rejectedPlanCount;
        private static string _lastPlanKind = "none";
        private static string _lastPlanStatus = "INFO";
        private static string _lastPlanDetail = "No building plan has been validated.";
        private static string _lastPlanReason = "none";
        private static string _lastPlanModId = "";
        private static string _lastPlanBlueprintId = "";
        private static int _lastPlanBlueprintItemId;
        private static int _lastPlanRecipeId;
        private static string _lastPlanRelativePath = "";

        public static OpenACAISonsBuildingSnapshot State
        {
            get
            {
                lock (SyncRoot)
                {
                    return _state;
                }
            }
        }

        public static bool Ready => State.Ready;
        public static bool PlanningEnabled => true;
        public static bool RuntimePlacementEnabled => RuntimePlacementEnabledValue;
        public static bool RuntimeBookIntegrationEnabled => RuntimeBookIntegrationEnabledValue;
        public static IReadOnlyList<string> SupportedPlanKinds => SupportedPlanKindNames;
        public static IReadOnlyList<string> SupportedBlueprintFileExtensions => BlueprintFileExtensions;

        public static OpenACAISonsModBlueprintPath GetModBlueprintPath(string modId)
        {
            var state = State;
            var normalizedId = OpenACAISonsSaveData.NormalizeModId(modId);
            var directory = CombineUnderRoot(state.OpenAcaiModBlueprintDirectory, normalizedId, "blueprint");
            return new OpenACAISonsModBlueprintPath(
                modId,
                normalizedId,
                directory,
                Directory.Exists(directory));
        }

        public static string GetModBlueprintDirectory(string modId)
        {
            return GetModBlueprintPath(modId).Directory;
        }

        public static string EnsureModBlueprintDirectory(string modId)
        {
            var path = GetModBlueprintPath(modId);
            EnsureDirectory(path.Directory);
            Refresh();
            return path.Directory;
        }

        public static string GetModBlueprintFilePath(string modId, string relativePath)
        {
            return ResolveModFilePath(GetModBlueprintDirectory(modId), relativePath);
        }

        public static IReadOnlyList<OpenACAISonsBlueprintFileSnapshot> ListModBlueprintFiles(string modId)
        {
            return ListModBlueprintFiles(modId, "*", MaxListedBlueprintFiles);
        }

        public static IReadOnlyList<OpenACAISonsBlueprintFileSnapshot> ListModBlueprintFiles(string modId, string searchPattern, int maxFiles)
        {
            return ListFiles(GetModBlueprintDirectory(modId), searchPattern, Math.Min(Math.Max(0, maxFiles), MaxListedBlueprintFiles), "openacai", OpenACAISonsSaveData.NormalizeModId(modId));
        }

        public static IReadOnlyList<OpenACAISonsBlueprintDefinitionSnapshot> GetRegisteredBlueprints()
        {
            lock (SyncRoot)
            {
                return RegisteredBlueprints.Values
                    .OrderBy(blueprint => blueprint.ModId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(blueprint => blueprint.BlueprintId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }

        public static bool TryRegisterBlueprint(
            string modId,
            string blueprintId,
            int recipeId,
            string displayName,
            out OpenACAISonsBlueprintDefinitionSnapshot blueprint)
        {
            return TryRegisterBlueprint(modId, blueprintId, recipeId, displayName, "", out blueprint);
        }

        public static bool TryRegisterBlueprint(
            string modId,
            string blueprintId,
            int recipeId,
            string displayName,
            string sourceRelativePath,
            out OpenACAISonsBlueprintDefinitionSnapshot blueprint)
        {
            blueprint = null;
            string normalizedModId;
            string normalizedBlueprintId;
            string reason;
            if (!TryNormalizeBlueprint(modId, blueprintId, out normalizedModId, out normalizedBlueprintId, out reason))
            {
                return false;
            }

            if (recipeId <= 0)
            {
                return false;
            }

            var normalizedPath = NormalizeRelativePath(sourceRelativePath);
            if (!string.IsNullOrWhiteSpace(normalizedPath) && !HasSupportedBlueprintExtension(normalizedPath))
            {
                return false;
            }

            lock (SyncRoot)
            {
                if (RegisteredBlueprints.Count >= MaxBlueprintDefinitionCount
                    && !RegisteredBlueprints.ContainsKey(BlueprintKey(normalizedModId, normalizedBlueprintId)))
                {
                    return false;
                }

                blueprint = new OpenACAISonsBlueprintDefinitionSnapshot(
                    normalizedModId,
                    normalizedBlueprintId,
                    recipeId,
                    string.IsNullOrWhiteSpace(displayName) ? normalizedBlueprintId : displayName.Trim(),
                    normalizedPath,
                    DateTimeOffset.UtcNow.ToString("O"));
                RegisteredBlueprints[BlueprintKey(normalizedModId, normalizedBlueprintId)] = blueprint;
            }

            Refresh();
            return true;
        }

        public static bool TryUnregisterBlueprint(string modId, string blueprintId)
        {
            string normalizedModId;
            string normalizedBlueprintId;
            string reason;
            if (!TryNormalizeBlueprint(modId, blueprintId, out normalizedModId, out normalizedBlueprintId, out reason))
            {
                return false;
            }

            bool removed;
            lock (SyncRoot)
            {
                removed = RegisteredBlueprints.Remove(BlueprintKey(normalizedModId, normalizedBlueprintId));
            }

            if (removed)
            {
                Refresh();
            }

            return removed;
        }

        public static OpenACAISonsBuildingPlan PlanBlueprintItem(int blueprintItemId)
        {
            return Plan(
                OpenACAISonsBuildingPlanKind.BlueprintItem,
                "",
                "",
                blueprintItemId,
                0,
                "");
        }

        public static OpenACAISonsBuildingPlan PlanCustomBlueprint(string modId, string blueprintId)
        {
            return Plan(
                OpenACAISonsBuildingPlanKind.CustomBlueprint,
                modId,
                blueprintId,
                0,
                0,
                "");
        }

        public static OpenACAISonsBuildingPlan PlanBlueprintFile(string modId, string relativePath)
        {
            return Plan(
                OpenACAISonsBuildingPlanKind.BlueprintFile,
                modId,
                "",
                0,
                0,
                relativePath);
        }

        public static OpenACAISonsBuildingPlan Plan(
            OpenACAISonsBuildingPlanKind kind,
            string modId,
            string blueprintId,
            int blueprintItemId,
            int recipeId,
            string relativePath)
        {
            var snapshot = State;
            var planKindName = PlanKindName(kind);
            var accepted = false;
            var canPlaceNow = false;
            var status = "CHECK";
            var reason = "invalid";
            var detail = "Building plan is invalid.";
            var normalizedModId = OpenACAISonsSaveData.NormalizeModId(modId);
            var normalizedBlueprintId = NormalizeId(blueprintId);
            var normalizedPath = NormalizeRelativePath(relativePath);
            var resolvedRecipeId = Math.Max(0, recipeId);
            var resolvedBlueprintItemId = Math.Max(0, blueprintItemId);

            if (!IsSupportedPlanKind(kind))
            {
                reason = "unsupported-plan-kind";
                detail = "Unsupported OpenACAI building plan kind.";
            }
            else if (kind == OpenACAISonsBuildingPlanKind.BlueprintItem)
            {
                accepted = TryValidateBlueprintItem(resolvedBlueprintItemId, out reason, out detail);
            }
            else if (kind == OpenACAISonsBuildingPlanKind.CustomBlueprint)
            {
                OpenACAISonsBlueprintDefinitionSnapshot definition;
                if (!TryNormalizeBlueprint(modId, blueprintId, out normalizedModId, out normalizedBlueprintId, out reason))
                {
                    detail = BlueprintReasonDetail(reason);
                }
                else if (!TryFindRegisteredBlueprint(normalizedModId, normalizedBlueprintId, out definition))
                {
                    reason = "blueprint-not-registered";
                    detail = "Register the mod-owned blueprint definition before planning placement.";
                }
                else
                {
                    accepted = true;
                    resolvedRecipeId = definition.RecipeId;
                    detail = "OpenACAI validated the mod-owned blueprint definition.";
                    reason = "validated";
                }
            }
            else if (kind == OpenACAISonsBuildingPlanKind.BlueprintFile)
            {
                accepted = TryValidateBlueprintFile(modId, normalizedPath, out reason, out detail);
            }

            if (accepted)
            {
                if (!snapshot.IsInWorld)
                {
                    status = "DEFERRED";
                    reason = "not-in-world";
                    detail = "OpenACAI accepted the building plan, but placement is deferred until the player is in-world.";
                }
                else if (!RuntimePlacementEnabled)
                {
                    status = "DEFERRED";
                    reason = "placement-disabled";
                    detail = "OpenACAI accepted the building plan; runtime placement remains disabled until the construction path is verified.";
                }
                else
                {
                    canPlaceNow = true;
                    status = "READY";
                    reason = "ready";
                    detail = "OpenACAI building gate accepted the plan for placement.";
                }
            }

            var plan = new OpenACAISonsBuildingPlan(
                kind,
                planKindName,
                normalizedModId,
                normalizedBlueprintId,
                resolvedBlueprintItemId,
                resolvedRecipeId,
                normalizedPath,
                accepted,
                canPlaceNow,
                RuntimePlacementEnabled,
                RuntimeBookIntegrationEnabled,
                status,
                reason,
                detail,
                DateTimeOffset.UtcNow.ToString("O"));
            RecordPlan(plan);
            Refresh();
            return plan;
        }

        public static OpenACAISonsBuildingSnapshot Refresh()
        {
            var snapshot = CreateSnapshot();
            Publish(snapshot);
            return snapshot;
        }

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            OpenACAISons.SceneChanged += _ => Refresh();
            OpenACAISons.WorldChanged += _ => Refresh();
            OpenACAISons.Lifecycle += _ => Refresh();
            Publish(OpenACAISonsBuildingSnapshot.CreateUnknown());
            if (!OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke SDK building provider could not be registered.");
            }
        }

        private static void Publish(OpenACAISonsBuildingSnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _state = snapshot ?? OpenACAISonsBuildingSnapshot.CreateUnknown();
                OpenACAIModding.SetService(ServiceKey, _state);
            }
        }

        private static void RecordPlan(OpenACAISonsBuildingPlan plan)
        {
            lock (SyncRoot)
            {
                _planRequestCount++;
                if (plan != null && plan.Accepted)
                {
                    _acceptedPlanCount++;
                }
                else
                {
                    _rejectedPlanCount++;
                }

                _lastPlanKind = plan == null ? "unknown" : plan.PlanKindName;
                _lastPlanStatus = plan == null ? "CHECK" : plan.Status;
                _lastPlanDetail = plan == null ? "Building plan was null." : plan.Detail;
                _lastPlanReason = plan == null ? "null-plan" : plan.ReasonCode;
                _lastPlanModId = plan == null ? "" : plan.ModId;
                _lastPlanBlueprintId = plan == null ? "" : plan.BlueprintId;
                _lastPlanBlueprintItemId = plan == null ? 0 : plan.BlueprintItemId;
                _lastPlanRecipeId = plan == null ? 0 : plan.RecipeId;
                _lastPlanRelativePath = plan == null ? "" : plan.RelativePath;
            }
        }

        private static OpenACAISonsBuildingSnapshot CreateSnapshot()
        {
            var sonsState = OpenACAISons.State;
            var environment = OpenACAISons.Environment;
            var itemCatalog = OpenACAISonsItems.Catalog;
            var itemCatalogReady = itemCatalog != null && itemCatalog.Ready;
            var openAcaiBlueprintDirectory = Combine(OpenACAIPaths.UserDataDirectory, OpenACAIBlueprintsDirectoryName);
            var openAcaiModBlueprintDirectory = Combine(openAcaiBlueprintDirectory, OpenACAIModBlueprintDirectoryName);
            EnsureDirectory(openAcaiBlueprintDirectory);
            EnsureDirectory(openAcaiModBlueprintDirectory);

            var legacyBlueprintDirectory = environment == null || string.IsNullOrWhiteSpace(environment.LegacyBlueprintsDirectory)
                ? Combine(SafePath(PathsGameRoot), "Mods", "Blueprints")
                : environment.LegacyBlueprintsDirectory;
            var files = ListKnownBlueprintFiles(openAcaiModBlueprintDirectory, legacyBlueprintDirectory);

            IReadOnlyList<OpenACAISonsBlueprintDefinitionSnapshot> registeredBlueprints;
            int planRequestCount;
            int acceptedPlanCount;
            int rejectedPlanCount;
            string lastPlanKind;
            string lastPlanStatus;
            string lastPlanDetail;
            string lastPlanReason;
            string lastPlanModId;
            string lastPlanBlueprintId;
            int lastPlanBlueprintItemId;
            int lastPlanRecipeId;
            string lastPlanRelativePath;
            lock (SyncRoot)
            {
                registeredBlueprints = RegisteredBlueprints.Values
                    .OrderBy(blueprint => blueprint.ModId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(blueprint => blueprint.BlueprintId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                planRequestCount = _planRequestCount;
                acceptedPlanCount = _acceptedPlanCount;
                rejectedPlanCount = _rejectedPlanCount;
                lastPlanKind = _lastPlanKind ?? "";
                lastPlanStatus = _lastPlanStatus ?? "";
                lastPlanDetail = _lastPlanDetail ?? "";
                lastPlanReason = _lastPlanReason ?? "";
                lastPlanModId = _lastPlanModId ?? "";
                lastPlanBlueprintId = _lastPlanBlueprintId ?? "";
                lastPlanBlueprintItemId = _lastPlanBlueprintItemId;
                lastPlanRecipeId = _lastPlanRecipeId;
                lastPlanRelativePath = _lastPlanRelativePath ?? "";
            }

            return new OpenACAISonsBuildingSnapshot(
                ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                true,
                "READY",
                "OpenACAI building and blueprint planning is available; runtime placement is gated until verified.",
                sonsState == null ? "" : sonsState.SceneName,
                sonsState == null ? "" : sonsState.SceneKind,
                sonsState != null && sonsState.IsInWorld,
                itemCatalogReady,
                itemCatalog == null ? "PENDING" : itemCatalogReady ? "READY" : itemCatalog.Status,
                itemCatalog == null ? 0 : itemCatalog.PublishedItemCount,
                openAcaiBlueprintDirectory,
                openAcaiModBlueprintDirectory,
                legacyBlueprintDirectory,
                Directory.Exists(openAcaiBlueprintDirectory),
                Directory.Exists(openAcaiModBlueprintDirectory),
                Directory.Exists(legacyBlueprintDirectory),
                files,
                registeredBlueprints,
                PlanningEnabled,
                RuntimePlacementEnabled,
                RuntimeBookIntegrationEnabled,
                SupportedPlanKindNames,
                BlueprintFileExtensions,
                MaxBlueprintDefinitionCount,
                MaxBlueprintIdLength,
                planRequestCount,
                acceptedPlanCount,
                rejectedPlanCount,
                lastPlanKind,
                lastPlanStatus,
                lastPlanDetail,
                lastPlanReason,
                lastPlanModId,
                lastPlanBlueprintId,
                lastPlanBlueprintItemId,
                lastPlanRecipeId,
                lastPlanRelativePath);
        }

        private static IReadOnlyList<OpenACAISonsBlueprintFileSnapshot> ListKnownBlueprintFiles(string openAcaiModBlueprintDirectory, string legacyBlueprintDirectory)
        {
            var files = new List<OpenACAISonsBlueprintFileSnapshot>();
            files.AddRange(ListFiles(openAcaiModBlueprintDirectory, "*", MaxListedBlueprintFiles, "openacai", ""));
            if (files.Count < MaxListedBlueprintFiles)
            {
                files.AddRange(ListFiles(legacyBlueprintDirectory, "*", MaxListedBlueprintFiles - files.Count, "legacy", ""));
            }

            return files
                .OrderBy(file => file.Source, StringComparer.OrdinalIgnoreCase)
                .ThenBy(file => file.ModId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Take(MaxListedBlueprintFiles)
                .ToArray();
        }

        private static IReadOnlyList<OpenACAISonsBlueprintFileSnapshot> ListFiles(
            string directory,
            string searchPattern,
            int maxFiles,
            string source,
            string modId)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) || maxFiles <= 0)
            {
                return Array.Empty<OpenACAISonsBlueprintFileSnapshot>();
            }

            try
            {
                var root = EnsureTrailingSeparator(Path.GetFullPath(directory));
                return Directory.GetFiles(directory, string.IsNullOrWhiteSpace(searchPattern) ? "*" : searchPattern, SearchOption.AllDirectories)
                    .Where(HasSupportedBlueprintExtension)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Take(maxFiles)
                    .Select(path => CreateFileSnapshot(root, path, source, modId))
                    .Where(file => file != null)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<OpenACAISonsBlueprintFileSnapshot>();
            }
        }

        private static OpenACAISonsBlueprintFileSnapshot CreateFileSnapshot(string root, string path, string source, string modId)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                var relativePath = fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    ? fullPath.Substring(root.Length)
                    : Path.GetFileName(fullPath);
                var effectiveModId = string.IsNullOrWhiteSpace(modId) ? ExtractModId(relativePath) : modId;
                var info = new FileInfo(fullPath);
                return new OpenACAISonsBlueprintFileSnapshot(
                    source,
                    effectiveModId,
                    relativePath.Replace(Path.DirectorySeparatorChar, '/'),
                    fullPath,
                    Path.GetFileName(fullPath),
                    Path.GetExtension(fullPath).ToLowerInvariant(),
                    info.Exists ? info.Length : 0,
                    info.Exists ? info.LastWriteTimeUtc.ToString("O") : "");
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractModId(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return "";
            }

            var normalized = relativePath.Replace('\\', '/');
            var index = normalized.IndexOf('/');
            if (index <= 0)
            {
                return "";
            }

            return normalized.Substring(0, index);
        }

        private static bool TryValidateBlueprintItem(int itemId, out string reason, out string detail)
        {
            if (itemId <= 0)
            {
                reason = "invalid-blueprint-item-id";
                detail = "A positive Sons blueprint item id is required.";
                return false;
            }

            OpenACAISonsItemSnapshot item;
            if (!OpenACAISonsItems.TryFindItem(itemId, out item))
            {
                reason = "unknown-blueprint-item";
                detail = "Sons blueprint item id " + itemId + " was not found in the OpenACAI item catalog.";
                return false;
            }

            if (!LooksLikeBlueprintItem(item))
            {
                reason = "not-a-blueprint-item";
                detail = "Item id " + itemId + " is known, but it does not look like a blueprint item.";
                return false;
            }

            reason = "validated";
            detail = "OpenACAI validated the Sons blueprint item against the item catalog.";
            return true;
        }

        private static bool TryValidateBlueprintFile(string modId, string relativePath, out string reason, out string detail)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                reason = "missing-mod-id";
                detail = "A mod id is required for OpenACAI blueprint file ownership.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                reason = "missing-blueprint-file";
                detail = "A blueprint file relative path is required.";
                return false;
            }

            if (!HasSupportedBlueprintExtension(relativePath))
            {
                reason = "unsupported-blueprint-extension";
                detail = "Blueprint files must use one of: " + string.Join(", ", BlueprintFileExtensions) + ".";
                return false;
            }

            var fullPath = GetModBlueprintFilePath(modId, relativePath);
            if (!File.Exists(fullPath))
            {
                reason = "blueprint-file-missing";
                detail = "Blueprint file does not exist under the mod blueprint directory.";
                return false;
            }

            reason = "validated";
            detail = "OpenACAI validated the blueprint file path under the mod-owned blueprint directory.";
            return true;
        }

        private static bool LooksLikeBlueprintItem(OpenACAISonsItemSnapshot item)
        {
            if (item == null)
            {
                return false;
            }

            return ContainsBlueprint(item.Name)
                || ContainsBlueprint(item.EditorName)
                || ContainsBlueprint(item.DisplayName)
                || ContainsBlueprint(item.Type)
                || item.Tags.Any(ContainsBlueprint);
        }

        private static bool ContainsBlueprint(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.IndexOf("blueprint", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryFindRegisteredBlueprint(string modId, string blueprintId, out OpenACAISonsBlueprintDefinitionSnapshot blueprint)
        {
            lock (SyncRoot)
            {
                return RegisteredBlueprints.TryGetValue(BlueprintKey(modId, blueprintId), out blueprint);
            }
        }

        private static bool TryNormalizeBlueprint(
            string modId,
            string blueprintId,
            out string normalizedModId,
            out string normalizedBlueprintId,
            out string reason)
        {
            normalizedModId = OpenACAISonsSaveData.NormalizeModId(modId);
            normalizedBlueprintId = NormalizeId(blueprintId);
            if (string.IsNullOrWhiteSpace(modId))
            {
                reason = "missing-mod-id";
                return false;
            }

            if (string.IsNullOrWhiteSpace(normalizedBlueprintId))
            {
                reason = "missing-blueprint-id";
                return false;
            }

            if (normalizedBlueprintId.Length > MaxBlueprintIdLength)
            {
                reason = "blueprint-id-too-long";
                return false;
            }

            reason = "validated";
            return true;
        }

        private static string BlueprintReasonDetail(string reason)
        {
            switch (reason)
            {
                case "missing-mod-id":
                    return "A mod id is required for OpenACAI blueprint ownership.";
                case "missing-blueprint-id":
                    return "A blueprint id is required.";
                case "blueprint-id-too-long":
                    return "Blueprint ids are bounded to " + MaxBlueprintIdLength + " characters.";
                default:
                    return "Blueprint identity is invalid.";
            }
        }

        private static string NormalizeId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var trimmed = value.Trim();
            var builder = new StringBuilder(trimmed.Length);
            var lastWasSeparator = false;
            for (var index = 0; index < trimmed.Length; index++)
            {
                var current = trimmed[index];
                if (IsSafeIdCharacter(current))
                {
                    builder.Append(char.ToLowerInvariant(current));
                    lastWasSeparator = false;
                    continue;
                }

                if (!lastWasSeparator)
                {
                    builder.Append('-');
                    lastWasSeparator = true;
                }
            }

            return builder.ToString().Trim('.', '_', '-');
        }

        private static bool IsSafeIdCharacter(char value)
        {
            return (value >= 'a' && value <= 'z')
                || (value >= 'A' && value <= 'Z')
                || (value >= '0' && value <= '9')
                || value == '.'
                || value == '_'
                || value == '-';
        }

        private static string BlueprintKey(string modId, string blueprintId)
        {
            return OpenACAISonsSaveData.NormalizeModId(modId).ToLowerInvariant() + ":" + NormalizeId(blueprintId);
        }

        private static bool IsSupportedPlanKind(OpenACAISonsBuildingPlanKind kind)
        {
            return kind == OpenACAISonsBuildingPlanKind.BlueprintItem
                || kind == OpenACAISonsBuildingPlanKind.CustomBlueprint
                || kind == OpenACAISonsBuildingPlanKind.BlueprintFile;
        }

        private static string PlanKindName(OpenACAISonsBuildingPlanKind kind)
        {
            switch (kind)
            {
                case OpenACAISonsBuildingPlanKind.BlueprintItem:
                    return "blueprint-item";
                case OpenACAISonsBuildingPlanKind.CustomBlueprint:
                    return "custom-blueprint";
                case OpenACAISonsBuildingPlanKind.BlueprintFile:
                    return "blueprint-file";
                default:
                    return "unknown";
            }
        }

        private static string NormalizeRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return "";
            }

            return relativePath.Trim().Replace('\\', '/').TrimStart('/');
        }

        private static bool HasSupportedBlueprintExtension(string path)
        {
            var extension = string.IsNullOrWhiteSpace(path) ? "" : Path.GetExtension(path);
            return BlueprintFileExtensions.Any(candidate => string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase));
        }

        private static string ResolveModFilePath(string directory, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("A relative blueprint file path is required.", nameof(relativePath));
            }

            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("OpenACAI mod blueprint directory is not available.");
            }

            if (Path.IsPathRooted(relativePath))
            {
                throw new ArgumentException("Mod blueprint file paths must be relative.", nameof(relativePath));
            }

            var fullRoot = EnsureTrailingSeparator(Path.GetFullPath(directory));
            var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
            if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Mod blueprint file paths must stay under the mod blueprint directory.", nameof(relativePath));
            }

            return fullPath;
        }

        private static string CombineUnderRoot(string root, string child, string kind)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            var fullRoot = EnsureTrailingSeparator(Path.GetFullPath(root));
            var fullPath = Path.GetFullPath(Path.Combine(fullRoot, child ?? "unknown-mod"));
            if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Resolved " + kind + " path escaped the OpenACAI blueprint root.");
            }

            return fullPath;
        }

        private static string Combine(params string[] parts)
        {
            var usable = (parts ?? Array.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            return usable.Length == 0 ? null : Path.Combine(usable);
        }

        private static string SafePath(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? "" : path;
        }

        private static string PathsGameRoot
        {
            get
            {
                try
                {
                    return Paths.GameRootPath;
                }
                catch
                {
                    return "";
                }
            }
        }

        private static void EnsureDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(path);
            }
            catch
            {
            }
        }

        private static string EnsureTrailingSeparator(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            var separator = Path.DirectorySeparatorChar.ToString();
            return path.EndsWith(separator, StringComparison.Ordinal)
                ? path
                : path + separator;
        }

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            var snapshot = Refresh();
            return new OpenACAIMenuContentSnapshot(
                "Sons Building",
                "OpenACAI-owned building and blueprint planning gate.",
                new[]
                {
                    CreateGateSection(snapshot),
                    CreateBlueprintSection(snapshot),
                    CreateActivitySection(snapshot)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateGateSection(OpenACAISonsBuildingSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Building Gate",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Status, "OpenACAI-owned building and blueprint planning snapshot."),
                    Row("Planning", snapshot.PlanningEnabled ? "Enabled" : "Disabled", snapshot.PlanningEnabled ? "READY" : "CHECK", "Mods can validate blueprint and placement intent."),
                    Row("Runtime Placement", snapshot.RuntimePlacementEnabled ? "Enabled" : "Disabled", snapshot.RuntimePlacementEnabled ? "READY" : "INFO", "Placement remains gated until the Sons construction path is verified."),
                    Row("Book Integration", snapshot.RuntimeBookIntegrationEnabled ? "Enabled" : "Disabled", snapshot.RuntimeBookIntegrationEnabled ? "READY" : "INFO", "Blueprint book mutation remains gated until the path is verified."),
                    Row("Scene", string.IsNullOrWhiteSpace(snapshot.SceneName) ? snapshot.SceneKind : snapshot.SceneName, snapshot.IsInWorld ? "READY" : "INFO", "In-world " + snapshot.IsInWorld + "."),
                    Row("Item Catalog", snapshot.ItemCatalogPublishedCount + " items", snapshot.ItemCatalogReady ? "READY" : snapshot.ItemCatalogStatus, "Blueprint item ids are validated against Modstoke SDK Items."),
                    Row("Supported", string.Join(", ", snapshot.SupportedPlanKinds), "INFO", "Current OpenACAI-owned building plan kinds.")
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateBlueprintSection(OpenACAISonsBuildingSnapshot snapshot)
        {
            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                PathRow("OpenACAI Blueprints", snapshot.OpenAcaiBlueprintDirectory, snapshot.OpenAcaiBlueprintDirectoryExists),
                PathRow("Mod Blueprints", snapshot.OpenAcaiModBlueprintDirectory, snapshot.OpenAcaiModBlueprintDirectoryExists),
                PathRow("Legacy Blueprints", snapshot.LegacyBlueprintDirectory, snapshot.LegacyBlueprintDirectoryExists),
                Row("Files", snapshot.BlueprintFileCount + " discovered", "INFO", "Supported extensions: " + string.Join(", ", snapshot.SupportedBlueprintFileExtensions) + "."),
                Row("Registered", snapshot.RegisteredBlueprintCount + "/" + snapshot.MaxBlueprintDefinitionCount, "INFO", "Mod-owned OpenACAI blueprint definitions.")
            };

            foreach (var blueprint in snapshot.RegisteredBlueprints.Take(8))
            {
                rows.Add(Row(
                    blueprint.ModId + ":" + blueprint.BlueprintId,
                    blueprint.RecipeId.ToString(),
                    "READY",
                    string.IsNullOrWhiteSpace(blueprint.DisplayName) ? "Registered OpenACAI blueprint." : blueprint.DisplayName));
            }

            foreach (var file in snapshot.BlueprintFiles.Take(6))
            {
                rows.Add(Row(
                    file.Source + " " + (string.IsNullOrWhiteSpace(file.ModId) ? file.FileName : file.ModId),
                    file.RelativePath,
                    "INFO",
                    file.SizeBytes + " bytes."));
            }

            return new OpenACAIMenuSectionSnapshot("Blueprints", rows);
        }

        private static OpenACAIMenuSectionSnapshot CreateActivitySection(OpenACAISonsBuildingSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Plan Activity",
                new[]
                {
                    Row("Plans", snapshot.PlanRequestCount + " requested", "INFO", snapshot.AcceptedPlanCount + " accepted, " + snapshot.RejectedPlanCount + " rejected."),
                    Row("Last Plan", snapshot.LastPlanKind + " / " + snapshot.LastPlanStatus, snapshot.LastPlanStatus, snapshot.LastPlanDetail),
                    Row("Last Blueprint", LastBlueprint(snapshot), "INFO", "Last planned blueprint identity."),
                    Row("Last Item", snapshot.LastPlanBlueprintItemId <= 0 ? "none" : snapshot.LastPlanBlueprintItemId.ToString(), "INFO", "Last planned blueprint item id."),
                    Row("Last Recipe", snapshot.LastPlanRecipeId <= 0 ? "none" : snapshot.LastPlanRecipeId.ToString(), "INFO", "Last planned recipe id."),
                    Row("Last File", string.IsNullOrWhiteSpace(snapshot.LastPlanRelativePath) ? "none" : snapshot.LastPlanRelativePath, "INFO", "Last planned blueprint file."),
                    Row("Last Reason", snapshot.LastPlanReason, snapshot.LastPlanStatus, "Last building plan reason code.")
                });
        }

        private static string LastBlueprint(OpenACAISonsBuildingSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.LastPlanModId) || string.IsNullOrWhiteSpace(snapshot.LastPlanBlueprintId))
            {
                return "none";
            }

            return snapshot.LastPlanModId + ":" + snapshot.LastPlanBlueprintId;
        }

        private static OpenACAIMenuRowSnapshot PathRow(string label, string path, bool exists)
        {
            return Row(label, ShortPath(path), string.IsNullOrWhiteSpace(path) ? "PENDING" : exists ? "READY" : "CHECK", path);
        }

        private static string ShortPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "Unavailable";
            }

            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fileName = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(fileName) ? path : fileName;
        }

        private static OpenACAIMenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new OpenACAIMenuRowSnapshot(label, value, status, detail);
        }

        private sealed class Provider : IOpenACAIMenuContentProvider
        {
            public string Id => OpenACAIMenuProviderIds.SonsBuilding;
            public string DisplayName => "Sons Building";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 725;
            public string Source => "Modstoke Loader";
            public string Description => "Show OpenACAI-owned Sons building and blueprint planning gate.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAISonsBuilding.CreateContent();
            }
        }
    }

    public enum OpenACAISonsBuildingPlanKind
    {
        BlueprintItem = 0,
        CustomBlueprint = 1,
        BlueprintFile = 2
    }

    public sealed class OpenACAISonsModBlueprintPath
    {
        internal OpenACAISonsModBlueprintPath(string modId, string normalizedModId, string directory, bool exists)
        {
            ModId = modId ?? "";
            NormalizedModId = normalizedModId ?? "";
            Directory = directory ?? "";
            Exists = exists;
        }

        public string ModId { get; }
        public string NormalizedModId { get; }
        public string Directory { get; }
        public bool Exists { get; }
    }

    public sealed class OpenACAISonsBlueprintDefinitionSnapshot
    {
        internal OpenACAISonsBlueprintDefinitionSnapshot(
            string modId,
            string blueprintId,
            int recipeId,
            string displayName,
            string sourceRelativePath,
            string registeredUtc)
        {
            ModId = modId ?? "";
            BlueprintId = blueprintId ?? "";
            RecipeId = Math.Max(0, recipeId);
            DisplayName = displayName ?? "";
            SourceRelativePath = sourceRelativePath ?? "";
            RegisteredUtc = registeredUtc ?? "";
        }

        public string ModId { get; }
        public string BlueprintId { get; }
        public int RecipeId { get; }
        public string DisplayName { get; }
        public string SourceRelativePath { get; }
        public string RegisteredUtc { get; }
    }

    public sealed class OpenACAISonsBlueprintFileSnapshot
    {
        internal OpenACAISonsBlueprintFileSnapshot(
            string source,
            string modId,
            string relativePath,
            string fullPath,
            string fileName,
            string extension,
            long sizeBytes,
            string lastWriteUtc)
        {
            Source = source ?? "";
            ModId = modId ?? "";
            RelativePath = relativePath ?? "";
            FullPath = fullPath ?? "";
            FileName = fileName ?? "";
            Extension = extension ?? "";
            SizeBytes = Math.Max(0, sizeBytes);
            LastWriteUtc = lastWriteUtc ?? "";
        }

        public string Source { get; }
        public string ModId { get; }
        public string RelativePath { get; }
        public string FullPath { get; }
        public string FileName { get; }
        public string Extension { get; }
        public long SizeBytes { get; }
        public string LastWriteUtc { get; }
    }

    public sealed class OpenACAISonsBuildingPlan
    {
        internal OpenACAISonsBuildingPlan(
            OpenACAISonsBuildingPlanKind planKind,
            string planKindName,
            string modId,
            string blueprintId,
            int blueprintItemId,
            int recipeId,
            string relativePath,
            bool accepted,
            bool canPlaceNow,
            bool runtimePlacementEnabled,
            bool runtimeBookIntegrationEnabled,
            string status,
            string reasonCode,
            string detail,
            string generatedUtc)
        {
            PlanKind = planKind;
            PlanKindName = planKindName ?? "";
            ModId = modId ?? "";
            BlueprintId = blueprintId ?? "";
            BlueprintItemId = Math.Max(0, blueprintItemId);
            RecipeId = Math.Max(0, recipeId);
            RelativePath = relativePath ?? "";
            Accepted = accepted;
            CanPlaceNow = canPlaceNow;
            RuntimePlacementEnabled = runtimePlacementEnabled;
            RuntimeBookIntegrationEnabled = runtimeBookIntegrationEnabled;
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
            GeneratedUtc = generatedUtc ?? "";
        }

        public OpenACAISonsBuildingPlanKind PlanKind { get; }
        public string PlanKindName { get; }
        public string ModId { get; }
        public string BlueprintId { get; }
        public int BlueprintItemId { get; }
        public int RecipeId { get; }
        public string RelativePath { get; }
        public bool Accepted { get; }
        public bool CanPlaceNow { get; }
        public bool RuntimePlacementEnabled { get; }
        public bool RuntimeBookIntegrationEnabled { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public string GeneratedUtc { get; }
    }

    public sealed class OpenACAISonsBuildingSnapshot
    {
        internal OpenACAISonsBuildingSnapshot(
            string serviceKey,
            string generatedUtc,
            bool ready,
            string status,
            string detail,
            string sceneName,
            string sceneKind,
            bool isInWorld,
            bool itemCatalogReady,
            string itemCatalogStatus,
            int itemCatalogPublishedCount,
            string openAcaiBlueprintDirectory,
            string openAcaiModBlueprintDirectory,
            string legacyBlueprintDirectory,
            bool openAcaiBlueprintDirectoryExists,
            bool openAcaiModBlueprintDirectoryExists,
            bool legacyBlueprintDirectoryExists,
            IReadOnlyList<OpenACAISonsBlueprintFileSnapshot> blueprintFiles,
            IReadOnlyList<OpenACAISonsBlueprintDefinitionSnapshot> registeredBlueprints,
            bool planningEnabled,
            bool runtimePlacementEnabled,
            bool runtimeBookIntegrationEnabled,
            IReadOnlyList<string> supportedPlanKinds,
            IReadOnlyList<string> supportedBlueprintFileExtensions,
            int maxBlueprintDefinitionCount,
            int maxBlueprintIdLength,
            int planRequestCount,
            int acceptedPlanCount,
            int rejectedPlanCount,
            string lastPlanKind,
            string lastPlanStatus,
            string lastPlanDetail,
            string lastPlanReason,
            string lastPlanModId,
            string lastPlanBlueprintId,
            int lastPlanBlueprintItemId,
            int lastPlanRecipeId,
            string lastPlanRelativePath)
        {
            ServiceKey = serviceKey ?? OpenACAISonsBuilding.ServiceKey;
            GeneratedUtc = generatedUtc ?? "";
            Ready = ready;
            Status = status ?? "";
            Detail = detail ?? "";
            SceneName = sceneName ?? "";
            SceneKind = sceneKind ?? "";
            IsInWorld = isInWorld;
            ItemCatalogReady = itemCatalogReady;
            ItemCatalogStatus = itemCatalogStatus ?? "";
            ItemCatalogPublishedCount = Math.Max(0, itemCatalogPublishedCount);
            OpenAcaiBlueprintDirectory = openAcaiBlueprintDirectory ?? "";
            OpenAcaiModBlueprintDirectory = openAcaiModBlueprintDirectory ?? "";
            LegacyBlueprintDirectory = legacyBlueprintDirectory ?? "";
            OpenAcaiBlueprintDirectoryExists = openAcaiBlueprintDirectoryExists;
            OpenAcaiModBlueprintDirectoryExists = openAcaiModBlueprintDirectoryExists;
            LegacyBlueprintDirectoryExists = legacyBlueprintDirectoryExists;
            BlueprintFiles = blueprintFiles ?? Array.Empty<OpenACAISonsBlueprintFileSnapshot>();
            BlueprintFileCount = BlueprintFiles.Count;
            RegisteredBlueprints = registeredBlueprints ?? Array.Empty<OpenACAISonsBlueprintDefinitionSnapshot>();
            RegisteredBlueprintCount = RegisteredBlueprints.Count;
            PlanningEnabled = planningEnabled;
            RuntimePlacementEnabled = runtimePlacementEnabled;
            RuntimeBookIntegrationEnabled = runtimeBookIntegrationEnabled;
            SupportedPlanKinds = supportedPlanKinds ?? Array.Empty<string>();
            SupportedBlueprintFileExtensions = supportedBlueprintFileExtensions ?? Array.Empty<string>();
            MaxBlueprintDefinitionCount = Math.Max(0, maxBlueprintDefinitionCount);
            MaxBlueprintIdLength = Math.Max(0, maxBlueprintIdLength);
            PlanRequestCount = Math.Max(0, planRequestCount);
            AcceptedPlanCount = Math.Max(0, acceptedPlanCount);
            RejectedPlanCount = Math.Max(0, rejectedPlanCount);
            LastPlanKind = lastPlanKind ?? "";
            LastPlanStatus = lastPlanStatus ?? "";
            LastPlanDetail = lastPlanDetail ?? "";
            LastPlanReason = lastPlanReason ?? "";
            LastPlanModId = lastPlanModId ?? "";
            LastPlanBlueprintId = lastPlanBlueprintId ?? "";
            LastPlanBlueprintItemId = Math.Max(0, lastPlanBlueprintItemId);
            LastPlanRecipeId = Math.Max(0, lastPlanRecipeId);
            LastPlanRelativePath = lastPlanRelativePath ?? "";
        }

        public int Schema => 1;
        public string ServiceKey { get; }
        public string GeneratedUtc { get; }
        public bool Ready { get; }
        public string Status { get; }
        public string Detail { get; }
        public string SceneName { get; }
        public string SceneKind { get; }
        public bool IsInWorld { get; }
        public bool ItemCatalogReady { get; }
        public string ItemCatalogStatus { get; }
        public int ItemCatalogPublishedCount { get; }
        public string OpenAcaiBlueprintDirectory { get; }
        public string OpenAcaiModBlueprintDirectory { get; }
        public string LegacyBlueprintDirectory { get; }
        public bool OpenAcaiBlueprintDirectoryExists { get; }
        public bool OpenAcaiModBlueprintDirectoryExists { get; }
        public bool LegacyBlueprintDirectoryExists { get; }
        public IReadOnlyList<OpenACAISonsBlueprintFileSnapshot> BlueprintFiles { get; }
        public int BlueprintFileCount { get; }
        public IReadOnlyList<OpenACAISonsBlueprintDefinitionSnapshot> RegisteredBlueprints { get; }
        public int RegisteredBlueprintCount { get; }
        public bool PlanningEnabled { get; }
        public bool RuntimePlacementEnabled { get; }
        public bool RuntimeBookIntegrationEnabled { get; }
        public IReadOnlyList<string> SupportedPlanKinds { get; }
        public IReadOnlyList<string> SupportedBlueprintFileExtensions { get; }
        public int MaxBlueprintDefinitionCount { get; }
        public int MaxBlueprintIdLength { get; }
        public int PlanRequestCount { get; }
        public int AcceptedPlanCount { get; }
        public int RejectedPlanCount { get; }
        public string LastPlanKind { get; }
        public string LastPlanStatus { get; }
        public string LastPlanDetail { get; }
        public string LastPlanReason { get; }
        public string LastPlanModId { get; }
        public string LastPlanBlueprintId { get; }
        public int LastPlanBlueprintItemId { get; }
        public int LastPlanRecipeId { get; }
        public string LastPlanRelativePath { get; }

        internal static OpenACAISonsBuildingSnapshot CreateUnknown()
        {
            return new OpenACAISonsBuildingSnapshot(
                OpenACAISonsBuilding.ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                "PENDING",
                "Sons building planning has not refreshed yet.",
                "",
                "",
                false,
                false,
                "PENDING",
                0,
                "",
                "",
                "",
                false,
                false,
                false,
                Array.Empty<OpenACAISonsBlueprintFileSnapshot>(),
                Array.Empty<OpenACAISonsBlueprintDefinitionSnapshot>(),
                true,
                OpenACAISonsBuilding.RuntimePlacementEnabled,
                OpenACAISonsBuilding.RuntimeBookIntegrationEnabled,
                OpenACAISonsBuilding.SupportedPlanKinds,
                OpenACAISonsBuilding.SupportedBlueprintFileExtensions,
                OpenACAISonsBuilding.MaxBlueprintDefinitionCount,
                OpenACAISonsBuilding.MaxBlueprintIdLength,
                0,
                0,
                0,
                "none",
                "INFO",
                "No building plan has been validated.",
                "none",
                "",
                "",
                0,
                0,
                "");
        }
    }
}
