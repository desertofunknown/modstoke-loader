// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Blueprint discovery and planning remain side-effect free with respect to
// construction. Explicit native registration and actions live in the partials.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    public static partial class Building
    {
        public const string ServiceKey = "openacai.sons.building";
        public const int MaxBlueprintDefinitionCount = 512;
        public const int MaxBlueprintIdLength = 96;
        public const int MaxListedBlueprintFiles = 256;

        internal const string OpenACAIBlueprintsDirectoryName = "Blueprints";
        internal const string OpenACAIModBlueprintDirectoryName = "Mods";

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, BlueprintDefinitionSnapshot> RegisteredBlueprints =
            new Dictionary<string, BlueprintDefinitionSnapshot>(StringComparer.OrdinalIgnoreCase);
        private static readonly IReadOnlyList<string> SupportedPlanKindNames = Array.AsReadOnly(new[]
        {
            "blueprint-item",
            "custom-blueprint",
            "blueprint-file"
        });

        private static readonly IReadOnlyList<string> BlueprintFileExtensions = Array.AsReadOnly(new[]
        {
            ".gltf",
            ".glb",
            ".json",
            ".blueprint"
        });

        private static BuildingSnapshot _state = BuildingSnapshot.CreateUnknown();
        private static bool _initialized;
        private static long _refreshedAt;
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

        public static BuildingSnapshot State
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
        public static bool RuntimePlacementEnabled => CanUseNativePlacement();
        public static bool RuntimeBookIntegrationEnabled => CanUseNativeBook();
        public static IReadOnlyList<string> SupportedPlanKinds => SupportedPlanKindNames;
        public static IReadOnlyList<string> SupportedBlueprintFileExtensions => BlueprintFileExtensions;

        public static ModBlueprintPath GetModBlueprintPath(string modId)
        {
            var state = State;
            var normalizedId = SaveData.NormalizeModId(modId);
            var root = state.ModstokeModBlueprintDirectory;
            if (string.IsNullOrWhiteSpace(root))
                root = ModBlueprintRoot;
            var directory = CombineUnderRoot(root, normalizedId, "blueprint");
            return new ModBlueprintPath(
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

        public static IReadOnlyList<BlueprintFileSnapshot> ListModBlueprintFiles(string modId)
        {
            return ListModBlueprintFiles(modId, "*", MaxListedBlueprintFiles);
        }

        public static IReadOnlyList<BlueprintFileSnapshot> ListModBlueprintFiles(string modId, string searchPattern, int maxFiles)
        {
            return ListFiles(GetModBlueprintDirectory(modId), searchPattern, Math.Min(Math.Max(0, maxFiles), MaxListedBlueprintFiles), "openacai", SaveData.NormalizeModId(modId));
        }

        public static IReadOnlyList<BlueprintDefinitionSnapshot> GetRegisteredBlueprints()
        {
            lock (SyncRoot)
            {
                return Array.AsReadOnly(RegisteredBlueprints.Values
                    .OrderBy(blueprint => blueprint.ModId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(blueprint => blueprint.BlueprintId, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
            }
        }

        public static bool TryRegisterBlueprint(
            string modId,
            string blueprintId,
            int recipeId,
            string displayName,
            out BlueprintDefinitionSnapshot blueprint)
        {
            return TryRegisterBlueprint(modId, blueprintId, recipeId, displayName, "", out blueprint);
        }

        public static bool TryRegisterBlueprint(
            string modId,
            string blueprintId,
            int recipeId,
            string displayName,
            string sourceRelativePath,
            out BlueprintDefinitionSnapshot blueprint)
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
            if (!string.IsNullOrWhiteSpace(normalizedPath))
            {
                try
                {
                    if (!HasSupportedBlueprintExtension(normalizedPath))
                        return false;

                    // Registration may precede the first building snapshot; validate
                    // the configured path without requiring the source file to exist.
                    ResolveModFilePath(CombineUnderRoot(ModBlueprintRoot, normalizedModId, "blueprint"), normalizedPath);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException || ex is InvalidOperationException)
                {
                    return false;
                }
            }

            var key = BlueprintKey(normalizedModId, normalizedBlueprintId);
            lock (SyncRoot)
            {
                if (RegisteredBlueprints.Count >= MaxBlueprintDefinitionCount
                    && !RegisteredBlueprints.ContainsKey(key))
                {
                    return false;
                }

                blueprint = new BlueprintDefinitionSnapshot(
                    normalizedModId,
                    normalizedBlueprintId,
                    recipeId,
                    string.IsNullOrWhiteSpace(displayName) ? normalizedBlueprintId : displayName.Trim(),
                    normalizedPath,
                    DateTimeOffset.UtcNow.ToString("O"));
                RegisteredBlueprints[key] = blueprint;
            }

            Refresh(!string.IsNullOrWhiteSpace(normalizedPath));
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
                Refresh(false);
            }

            return removed;
        }

        public static BuildingPlan PlanBlueprintItem(int blueprintItemId)
        {
            return Plan(
                BuildingPlanKind.BlueprintItem,
                "",
                "",
                blueprintItemId,
                0,
                "");
        }

        public static BuildingPlan PlanCustomBlueprint(string modId, string blueprintId)
        {
            return Plan(
                BuildingPlanKind.CustomBlueprint,
                modId,
                blueprintId,
                0,
                0,
                "");
        }

        public static BuildingPlan PlanBlueprintFile(string modId, string relativePath)
        {
            return Plan(
                BuildingPlanKind.BlueprintFile,
                modId,
                "",
                0,
                0,
                relativePath);
        }

        public static BuildingPlan Plan(
            BuildingPlanKind kind,
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
            var normalizedModId = SaveData.NormalizeModId(modId);
            var normalizedBlueprintId = NormalizeId(blueprintId);
            var normalizedPath = NormalizeRelativePath(relativePath);
            var resolvedRecipeId = Math.Max(0, recipeId);
            var resolvedBlueprintItemId = Math.Max(0, blueprintItemId);

            if (!IsSupportedPlanKind(kind))
            {
                reason = "unsupported-plan-kind";
                detail = "Unsupported Modstoke building plan kind.";
            }
            else if (kind == BuildingPlanKind.BlueprintItem)
            {
                accepted = TryValidateBlueprintItem(resolvedBlueprintItemId, out reason, out detail);
            }
            else if (kind == BuildingPlanKind.CustomBlueprint)
            {
                BlueprintDefinitionSnapshot definition;
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
                    normalizedPath = definition.SourceRelativePath;
                    detail = "Modstoke validated the mod-owned blueprint definition.";
                    reason = "validated";
                }
            }
            else if (kind == BuildingPlanKind.BlueprintFile)
            {
                accepted = TryValidateBlueprintFile(modId, normalizedPath, out reason, out detail);
            }

            if (accepted)
            {
                if (!snapshot.IsInWorld)
                {
                    status = "DEFERRED";
                    reason = "not-in-world";
                    detail = "Modstoke accepted the building plan, but placement is deferred until the player is in-world.";
                }
                else
                {
                    status = "VALIDATED";
                    reason = "explicit-action-required";
                    detail = "Plan validated only. Use an explicit native building action; metadata and files alone are not runtime recipes.";
                }
            }

            var plan = new BuildingPlan(
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
            Refresh(false);
            return plan;
        }

        public static BuildingSnapshot Refresh()
        {
            return Refresh(true);
        }

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            InitializeNativeBuilding();
            GameRuntime.SceneChanged += _ => Refresh();
            GameRuntime.WorldChanged += _ => Refresh();
            GameRuntime.Lifecycle += _ => Refresh();
            Publish(BuildingSnapshot.CreateUnknown());
            if (!MenuRegistry.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Modstoke SDK building provider could not be registered.");
            }
        }

        private static void Publish(BuildingSnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _state = snapshot ?? BuildingSnapshot.CreateUnknown();
                Modding.SetService(ServiceKey, _state);
            }
        }

        private static void RecordPlan(BuildingPlan plan)
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

        private static BuildingSnapshot Refresh(bool refreshFiles)
        {
            var sonsState = GameRuntime.State;
            var environment = GameRuntime.Environment;
            var itemCatalog = ItemCatalog.Catalog;
            var itemCatalogReady = itemCatalog != null && itemCatalog.Ready;
            var modstokeBlueprintDirectory = Combine(LoaderPaths.UserDataDirectory, OpenACAIBlueprintsDirectoryName);
            var modstokeModBlueprintDirectory = ModBlueprintRoot;
            var legacyBlueprintDirectory = environment == null || string.IsNullOrWhiteSpace(environment.LegacyBlueprintsDirectory)
                ? Combine(SafePath(PathsGameRoot), "Mods", "Blueprints")
                : environment.LegacyBlueprintsDirectory;

            // GeneratedUtc timestamps state publication, not file discovery. Activity-only
            // updates retain the inventory until a full refresh or source-file registration.
            var previous = State;
            refreshFiles = refreshFiles || !previous.Ready
                || !string.Equals(previous.ModstokeModBlueprintDirectory, modstokeModBlueprintDirectory, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(previous.LegacyBlueprintDirectory, legacyBlueprintDirectory, StringComparison.OrdinalIgnoreCase);
            IReadOnlyList<BlueprintFileSnapshot> files = null;
            var modstokeBlueprintDirectoryExists = false;
            var modstokeModBlueprintDirectoryExists = false;
            var legacyBlueprintDirectoryExists = false;
            if (refreshFiles)
            {
                EnsureDirectory(modstokeBlueprintDirectory);
                EnsureDirectory(modstokeModBlueprintDirectory);
                files = ListKnownBlueprintFiles(modstokeModBlueprintDirectory, legacyBlueprintDirectory);
                modstokeBlueprintDirectoryExists = Directory.Exists(modstokeBlueprintDirectory);
                modstokeModBlueprintDirectoryExists = Directory.Exists(modstokeModBlueprintDirectory);
                legacyBlueprintDirectoryExists = Directory.Exists(legacyBlueprintDirectory);
                _refreshedAt = Environment.TickCount64;
            }

            lock (SyncRoot)
            {
                if (!refreshFiles && _state.Ready
                    && string.Equals(_state.ModstokeModBlueprintDirectory, modstokeModBlueprintDirectory, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(_state.LegacyBlueprintDirectory, legacyBlueprintDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    previous = _state;
                }

                var registeredBlueprints = RegisteredBlueprints.Values
                    .OrderBy(blueprint => blueprint.ModId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(blueprint => blueprint.BlueprintId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var snapshot = new BuildingSnapshot(
                    ServiceKey,
                    DateTimeOffset.UtcNow.ToString("O"),
                    true,
                    "READY",
                    "Blueprint planning is separate from explicit native registration, book, placement and construction actions.",
                    sonsState == null ? "" : sonsState.SceneName,
                    sonsState == null ? "" : sonsState.SceneKind,
                    sonsState != null && sonsState.IsInWorld,
                    itemCatalogReady,
                    itemCatalog == null ? "PENDING" : itemCatalogReady ? "READY" : itemCatalog.Status,
                    itemCatalog == null ? 0 : itemCatalog.PublishedItemCount,
                    modstokeBlueprintDirectory,
                    modstokeModBlueprintDirectory,
                    legacyBlueprintDirectory,
                    refreshFiles ? modstokeBlueprintDirectoryExists : previous.ModstokeBlueprintDirectoryExists,
                    refreshFiles ? modstokeModBlueprintDirectoryExists : previous.ModstokeModBlueprintDirectoryExists,
                    refreshFiles ? legacyBlueprintDirectoryExists : previous.LegacyBlueprintDirectoryExists,
                    refreshFiles ? files : previous.BlueprintFiles,
                    registeredBlueprints,
                    PlanningEnabled,
                    RuntimePlacementEnabled,
                    RuntimeBookIntegrationEnabled,
                    SupportedPlanKindNames,
                    BlueprintFileExtensions,
                    MaxBlueprintDefinitionCount,
                    MaxBlueprintIdLength,
                    _planRequestCount,
                    _acceptedPlanCount,
                    _rejectedPlanCount,
                    _lastPlanKind,
                    _lastPlanStatus,
                    _lastPlanDetail,
                    _lastPlanReason,
                    _lastPlanModId,
                    _lastPlanBlueprintId,
                    _lastPlanBlueprintItemId,
                    _lastPlanRecipeId,
                    _lastPlanRelativePath);
                Publish(snapshot);
                return snapshot;
            }
        }

        private static IReadOnlyList<BlueprintFileSnapshot> ListKnownBlueprintFiles(string modstokeModBlueprintDirectory, string legacyBlueprintDirectory)
        {
            var files = new List<BlueprintFileSnapshot>();
            files.AddRange(ListFiles(modstokeModBlueprintDirectory, "*", MaxListedBlueprintFiles, "openacai", ""));
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

        private static IReadOnlyList<BlueprintFileSnapshot> ListFiles(
            string directory,
            string searchPattern,
            int maxFiles,
            string source,
            string modId)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) || maxFiles <= 0)
            {
                return Array.Empty<BlueprintFileSnapshot>();
            }

            try
            {
                var root = EnsureTrailingSeparator(Path.GetFullPath(directory));
                var pattern = string.IsNullOrWhiteSpace(searchPattern) ? "*" : searchPattern;
                if (Path.IsPathRooted(pattern) || pattern.IndexOf('\0') >= 0)
                    return Array.Empty<BlueprintFileSnapshot>();

                // A directory prefix scopes the whole recursive search, not each branch.
                var searchDirectory = Path.GetDirectoryName(pattern);
                if (!string.IsNullOrEmpty(searchDirectory))
                {
                    directory = EnsureTrailingSeparator(Path.GetFullPath(Path.Combine(root, searchDirectory)));
                    if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        return Array.Empty<BlueprintFileSnapshot>();

                    pattern = Path.GetFileName(pattern);
                    if (string.IsNullOrEmpty(pattern))
                        pattern = "*";
                }

                return DiscoveryFiles.EnumerateFiles(directory, pattern, SearchOption.AllDirectories, null)
                    .Where(HasSupportedBlueprintExtension)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(path => CreateFileSnapshot(root, path, source, modId))
                    .Where(file => file != null)
                    .Take(maxFiles)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<BlueprintFileSnapshot>();
            }
        }

        private static BlueprintFileSnapshot CreateFileSnapshot(string root, string path, string source, string modId)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return null;

                var relativePath = fullPath.Substring(root.Length);
                var effectiveModId = string.IsNullOrWhiteSpace(modId) ? ExtractModId(relativePath) : modId;
                var info = new FileInfo(fullPath);
                if (!info.Exists)
                    return null;

                return new BlueprintFileSnapshot(
                    source,
                    effectiveModId,
                    relativePath.Replace(Path.DirectorySeparatorChar, '/'),
                    fullPath,
                    Path.GetFileName(fullPath),
                    Path.GetExtension(fullPath).ToLowerInvariant(),
                    info.Length,
                    info.LastWriteTimeUtc.ToString("O"));
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

            ItemSnapshot item;
            if (!ItemCatalog.TryFindItem(itemId, out item))
            {
                reason = "unknown-blueprint-item";
                detail = "Sons blueprint item id " + itemId + " was not found in the Modstoke item catalog.";
                return false;
            }

            if (!LooksLikeBlueprintItem(item))
            {
                reason = "not-a-blueprint-item";
                detail = "Item id " + itemId + " is known, but it does not look like a blueprint item.";
                return false;
            }

            reason = "validated";
            detail = "Modstoke validated the Sons blueprint item against the item catalog.";
            return true;
        }

        private static bool TryValidateBlueprintFile(string modId, string relativePath, out string reason, out string detail)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                reason = "missing-mod-id";
                detail = "A mod id is required for Modstoke blueprint file ownership.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                reason = "missing-blueprint-file";
                detail = "A blueprint file relative path is required.";
                return false;
            }

            string fullPath;
            try
            {
                if (!HasSupportedBlueprintExtension(relativePath))
                {
                    reason = "unsupported-blueprint-extension";
                    detail = "Blueprint files must use one of: " + string.Join(", ", BlueprintFileExtensions) + ".";
                    return false;
                }

                fullPath = GetModBlueprintFilePath(modId, relativePath);
            }
            catch (InvalidOperationException)
            {
                reason = "blueprint-directory-unavailable";
                detail = "The mod blueprint directory is not available yet.";
                return false;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException)
            {
                reason = "invalid-blueprint-file";
                detail = "Blueprint file paths must be valid relative paths under the mod blueprint directory.";
                return false;
            }

            if (!File.Exists(fullPath))
            {
                reason = "blueprint-file-missing";
                detail = "Blueprint file does not exist under the mod blueprint directory.";
                return false;
            }

            reason = "validated";
            detail = "Modstoke validated the blueprint file path under the mod-owned blueprint directory.";
            return true;
        }

        private static bool LooksLikeBlueprintItem(ItemSnapshot item)
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

        private static bool TryFindRegisteredBlueprint(string modId, string blueprintId, out BlueprintDefinitionSnapshot blueprint)
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
            normalizedModId = SaveData.NormalizeModId(modId);
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
                    return "A mod id is required for Modstoke blueprint ownership.";
                case "missing-blueprint-id":
                    return "A blueprint id is required.";
                case "blueprint-id-too-long":
                    return "Blueprint ids are bounded to " + MaxBlueprintIdLength + " characters.";
                default:
                    return "Blueprint identity is invalid.";
            }
        }

        // Blueprint ids are their own namespace: lowercase, with unsafe runs collapsed to one dash.
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
            return SaveData.NormalizeModId(modId).ToLowerInvariant() + ":" + NormalizeId(blueprintId);
        }

        private static bool IsSupportedPlanKind(BuildingPlanKind kind)
        {
            return kind == BuildingPlanKind.BlueprintItem
                || kind == BuildingPlanKind.CustomBlueprint
                || kind == BuildingPlanKind.BlueprintFile;
        }

        private static string PlanKindName(BuildingPlanKind kind)
        {
            switch (kind)
            {
                case BuildingPlanKind.BlueprintItem:
                    return "blueprint-item";
                case BuildingPlanKind.CustomBlueprint:
                    return "custom-blueprint";
                case BuildingPlanKind.BlueprintFile:
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

            return relativePath.Trim().Replace('\\', '/');
        }

        private static bool HasSupportedBlueprintExtension(string path)
        {
            var extension = string.IsNullOrWhiteSpace(path) ? "" : Path.GetExtension(path);
            return BlueprintFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        private static string ResolveModFilePath(string directory, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("A relative blueprint file path is required.", nameof(relativePath));
            }

            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("Modstoke mod blueprint directory is not available.");
            }

            if (Path.IsPathRooted(relativePath))
            {
                throw new ArgumentException("Mod blueprint file paths must be relative.", nameof(relativePath));
            }

            var invalidFileNameChars = Path.GetInvalidFileNameChars();
            foreach (var segment in relativePath.Replace('\\', '/').Split('/'))
            {
                if (segment.IndexOfAny(invalidFileNameChars) >= 0)
                    throw new ArgumentException("Mod blueprint file paths contain invalid characters.", nameof(relativePath));
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
                throw new InvalidOperationException("Resolved " + kind + " path escaped the Modstoke blueprint root.");
            }

            return fullPath;
        }

        private static string ModBlueprintRoot
            => Combine(LoaderPaths.UserDataDirectory, OpenACAIBlueprintsDirectoryName, OpenACAIModBlueprintDirectoryName);

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

            var separator = Path.DirectorySeparatorChar;
            return path.EndsWith(separator)
                ? path
                : path + separator;
        }

        private static MenuContentSnapshot CreateContent()
        {
            // Open menu pages poll this, so a file scan from the last two seconds is reused.
            var snapshot = Environment.TickCount64 - _refreshedAt < 2000 ? State : Refresh();
            return new MenuContentSnapshot(
                "Sons Building",
                "Modstoke building and blueprint planning gate.",
                new[]
                {
                    CreateGateSection(snapshot),
                    CreateBlueprintSection(snapshot),
                    CreateActivitySection(snapshot)
                });
        }

        private static MenuSectionSnapshot CreateGateSection(BuildingSnapshot snapshot)
        {
            return new MenuSectionSnapshot(
                "Building Gate",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Status, "Modstoke building and blueprint planning snapshot."),
                    Row("Planning", snapshot.PlanningEnabled ? "Enabled" : "Disabled", snapshot.PlanningEnabled ? "READY" : "CHECK", "Mods can validate blueprint and placement intent."),
                    Row("Runtime Placement", snapshot.RuntimePlacementEnabled ? "Available" : "Unavailable", snapshot.RuntimePlacementEnabled ? "READY" : "INFO", "Explicit actions revalidate the local player and native construction state on the Unity thread."),
                    Row("Book Integration", snapshot.RuntimeBookIntegrationEnabled ? "Available" : "Unavailable", snapshot.RuntimeBookIntegrationEnabled ? "READY" : "INFO", "Explicit book pages require native recipes, caller artwork and a loaded book database."),
                    Row("Scene", string.IsNullOrWhiteSpace(snapshot.SceneName) ? snapshot.SceneKind : snapshot.SceneName, snapshot.IsInWorld ? "READY" : "INFO", "In-world " + snapshot.IsInWorld + "."),
                    Row("Item Catalog", snapshot.ItemCatalogPublishedCount + " items", snapshot.ItemCatalogReady ? "READY" : snapshot.ItemCatalogStatus, "Blueprint item ids are validated against Modstoke SDK Items."),
                    Row("Supported", string.Join(", ", snapshot.SupportedPlanKinds), "INFO", "Current Modstoke building plan kinds.")
                });
        }

        private static MenuSectionSnapshot CreateBlueprintSection(BuildingSnapshot snapshot)
        {
            var rows = new List<MenuRowSnapshot>
            {
                PathRow("Modstoke Blueprints", snapshot.ModstokeBlueprintDirectory, snapshot.ModstokeBlueprintDirectoryExists),
                PathRow("Mod Blueprints", snapshot.ModstokeModBlueprintDirectory, snapshot.ModstokeModBlueprintDirectoryExists),
                PathRow("Legacy Blueprints", snapshot.LegacyBlueprintDirectory, snapshot.LegacyBlueprintDirectoryExists),
                Row("Files", snapshot.BlueprintFileCount + " discovered", "INFO", "Supported extensions: " + string.Join(", ", snapshot.SupportedBlueprintFileExtensions) + "."),
                Row("Registered", snapshot.RegisteredBlueprintCount + "/" + snapshot.MaxBlueprintDefinitionCount, "INFO", "Mod-owned Modstoke blueprint definitions.")
            };

            foreach (var blueprint in snapshot.RegisteredBlueprints.Take(8))
            {
                rows.Add(Row(
                    blueprint.ModId + ":" + blueprint.BlueprintId,
                    blueprint.RecipeId.ToString(),
                    "READY",
                    string.IsNullOrWhiteSpace(blueprint.DisplayName) ? "Registered Modstoke blueprint." : blueprint.DisplayName));
            }

            foreach (var file in snapshot.BlueprintFiles.Take(6))
            {
                rows.Add(Row(
                    file.Source + " " + (string.IsNullOrWhiteSpace(file.ModId) ? file.FileName : file.ModId),
                    file.RelativePath,
                    "INFO",
                    file.SizeBytes + " bytes."));
            }

            return new MenuSectionSnapshot("Blueprints", rows);
        }

        private static MenuSectionSnapshot CreateActivitySection(BuildingSnapshot snapshot)
        {
            return new MenuSectionSnapshot(
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

        private static string LastBlueprint(BuildingSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.LastPlanModId) || string.IsNullOrWhiteSpace(snapshot.LastPlanBlueprintId))
            {
                return "none";
            }

            return snapshot.LastPlanModId + ":" + snapshot.LastPlanBlueprintId;
        }

        private static MenuRowSnapshot PathRow(string label, string path, bool exists)
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

        private static MenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new MenuRowSnapshot(label, value, status, detail);
        }

        private sealed class Provider : IMenuContentProvider
        {
            public string Id => MenuProviderIds.SonsBuilding;
            public string DisplayName => "Sons Building";
            public string Kind => MenuProviderKinds.Status;
            public int Priority => 725;
            public string Source => "Modstoke Loader";
            public string Description => "Show Modstoke Sons building and blueprint planning gate.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public MenuContentSnapshot CreateContent()
            {
                return Building.CreateContent();
            }
        }
    }

    public enum BuildingPlanKind
    {
        BlueprintItem = 0,
        CustomBlueprint = 1,
        BlueprintFile = 2
    }

    public sealed class ModBlueprintPath
    {
        internal ModBlueprintPath(string modId, string normalizedModId, string directory, bool exists)
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

    public sealed class BlueprintDefinitionSnapshot
    {
        internal BlueprintDefinitionSnapshot(
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

    public sealed class BlueprintFileSnapshot
    {
        internal BlueprintFileSnapshot(
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

    public sealed class BuildingPlan
    {
        internal BuildingPlan(
            BuildingPlanKind planKind,
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

        public BuildingPlanKind PlanKind { get; }
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

    public sealed class BuildingSnapshot
    {
        internal BuildingSnapshot(
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
            string modstokeBlueprintDirectory,
            string modstokeModBlueprintDirectory,
            string legacyBlueprintDirectory,
            bool modstokeBlueprintDirectoryExists,
            bool modstokeModBlueprintDirectoryExists,
            bool legacyBlueprintDirectoryExists,
            IReadOnlyList<BlueprintFileSnapshot> blueprintFiles,
            IReadOnlyList<BlueprintDefinitionSnapshot> registeredBlueprints,
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
            ServiceKey = serviceKey ?? Building.ServiceKey;
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
            ModstokeBlueprintDirectory = modstokeBlueprintDirectory ?? "";
            ModstokeModBlueprintDirectory = modstokeModBlueprintDirectory ?? "";
            LegacyBlueprintDirectory = legacyBlueprintDirectory ?? "";
            ModstokeBlueprintDirectoryExists = modstokeBlueprintDirectoryExists;
            ModstokeModBlueprintDirectoryExists = modstokeModBlueprintDirectoryExists;
            LegacyBlueprintDirectoryExists = legacyBlueprintDirectoryExists;
            BlueprintFiles = Array.AsReadOnly((blueprintFiles ?? Array.Empty<BlueprintFileSnapshot>()).ToArray());
            BlueprintFileCount = BlueprintFiles.Count;
            RegisteredBlueprints = Array.AsReadOnly((registeredBlueprints ?? Array.Empty<BlueprintDefinitionSnapshot>()).ToArray());
            RegisteredBlueprintCount = RegisteredBlueprints.Count;
            PlanningEnabled = planningEnabled;
            RuntimePlacementEnabled = runtimePlacementEnabled;
            RuntimeBookIntegrationEnabled = runtimeBookIntegrationEnabled;
            SupportedPlanKinds = Array.AsReadOnly((supportedPlanKinds ?? Array.Empty<string>()).ToArray());
            SupportedBlueprintFileExtensions = Array.AsReadOnly((supportedBlueprintFileExtensions ?? Array.Empty<string>()).ToArray());
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
        public string ModstokeBlueprintDirectory { get; }
        public string ModstokeModBlueprintDirectory { get; }
        public string LegacyBlueprintDirectory { get; }
        public bool ModstokeBlueprintDirectoryExists { get; }
        public bool ModstokeModBlueprintDirectoryExists { get; }
        public bool LegacyBlueprintDirectoryExists { get; }
        public IReadOnlyList<BlueprintFileSnapshot> BlueprintFiles { get; }
        public int BlueprintFileCount { get; }
        public IReadOnlyList<BlueprintDefinitionSnapshot> RegisteredBlueprints { get; }
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

        [Obsolete("Use ModstokeBlueprintDirectory.")]
        public string OpenAcaiBlueprintDirectory => ModstokeBlueprintDirectory;
        [Obsolete("Use ModstokeModBlueprintDirectory.")]
        public string OpenAcaiModBlueprintDirectory => ModstokeModBlueprintDirectory;
        [Obsolete("Use ModstokeBlueprintDirectoryExists.")]
        public bool OpenAcaiBlueprintDirectoryExists => ModstokeBlueprintDirectoryExists;
        [Obsolete("Use ModstokeModBlueprintDirectoryExists.")]
        public bool OpenAcaiModBlueprintDirectoryExists => ModstokeModBlueprintDirectoryExists;

        internal static BuildingSnapshot CreateUnknown()
        {
            return new BuildingSnapshot(
                Building.ServiceKey,
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
                Array.Empty<BlueprintFileSnapshot>(),
                Array.Empty<BlueprintDefinitionSnapshot>(),
                true,
                Building.RuntimePlacementEnabled,
                Building.RuntimeBookIntegrationEnabled,
                Building.SupportedPlanKinds,
                Building.SupportedBlueprintFileExtensions,
                Building.MaxBlueprintDefinitionCount,
                Building.MaxBlueprintIdLength,
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
