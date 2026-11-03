// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Modstoke.Loader;
using global::Sons.Crafting.Structures;
using UnityEngine;
using UnityGLTF;

namespace Modstoke.Sons
{
    public static partial class Building
    {
        private const int MaxPendingBlueprintImports = 16;
        private static readonly HashSet<BlueprintFileImport> BlueprintImports = new();
        private static GameObject _importedBlueprintSources;

        /// <summary>Starts an SDK glTF import for a registered mod definition's SourceRelativePath.</summary>
        /// <remarks>
        /// Only trusted mod-authored .gltf/.glb assets with the SDK's mesh ItemId extras are supported.
        /// This is not a game structure-save, .json or .blueprint importer. Dependencies are resolved
        /// by the existing importer. Supply explicit root-local boxes; geometry bounds are not guessed.
        /// Call CompleteBlueprintFileImport or DiscardBlueprintFileImport on the Unity thread after
        /// IsLoadCompleted. No continuation registers objects from a worker thread, and no world
        /// placement, inventory grant, discovery override or save restoration is performed.
        /// </remarks>
        public static BuildingActionResult BeginBlueprintFileImport(
            string modId, string blueprintId, IReadOnlyList<BlueprintFootprintBox> footprint,
            out BlueprintFileImport import)
        {
            import = null;
            if (!TryEnterNative(out var rejected)) return rejected;
            GLTFSceneImporter loader = null;
            int recipeId = 0;
            bool attempted = false;
            try
            {
                if (!TryNormalizeBlueprint(modId, blueprintId, out var mod, out var id, out var reason))
                    return BuildingResult(BuildingActionStatus.Rejected, 0, false, reason);
                if (!TryFindRegisteredBlueprint(mod, id, out var definition) || string.IsNullOrWhiteSpace(definition.SourceRelativePath))
                    return BuildingResult(BuildingActionStatus.Rejected, 0, false, "Register a mod blueprint definition with its source asset path first.");
                recipeId = definition.RecipeId;
                var extension = Path.GetExtension(definition.SourceRelativePath);
                if (!string.Equals(extension, ".gltf", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(extension, ".glb", StringComparison.OrdinalIgnoreCase))
                    return BuildingResult(BuildingActionStatus.Rejected, recipeId, false, "Only SDK glTF/GLB assets can be imported; game structure saves and other file formats are not supported.");
                if (recipeId <= 1 || NativeBlueprints.ContainsKey(BlueprintKey(mod, id))
                    || SonsSdk.Building.CustomBlueprintManager.LoadedObjects.ContainsKey(recipeId)
                    || BlueprintImports.Count >= MaxPendingBlueprintImports)
                    return BuildingResult(BuildingActionStatus.Rejected, recipeId, false, "The runtime identity/recipe is reserved, invalid, or the pending import limit was reached.");
                foreach (var pending in BlueprintImports)
                    if (pending.Definition.RecipeId == recipeId)
                        return BuildingResult(BuildingActionStatus.Rejected, recipeId, false, "An import is already pending for this recipe.");
                if (footprint == null || footprint.Count == 0)
                    return BuildingResult(BuildingActionStatus.Rejected, recipeId, false, "An explicit authored box footprint is required.");
                var boxes = new BlueprintFootprintBox[footprint.Count];
                for (int index = 0; index < boxes.Length; index++)
                {
                    boxes[index] = footprint[index];
                    if (!ValidFootprintBox(boxes[index].Center, boxes[index].Size))
                        return BuildingResult(BuildingActionStatus.Rejected, recipeId, false, "Footprint boxes must be finite with positive size.");
                }
                var path = GetModBlueprintFilePath(mod, definition.SourceRelativePath);
                if (!File.Exists(path))
                    return BuildingResult(BuildingActionStatus.Rejected, recipeId, false, "The registered source asset does not exist.");

                attempted = true;
                SdkResources.RegisterAssetImporters();
                if (!_importedBlueprintSources)
                {
                    _importedBlueprintSources = new GameObject("Modstoke Blueprint Sources");
                    _importedBlueprintSources.SetActive(false);
                    UnityEngine.Object.DontDestroyOnLoad(_importedBlueprintSources);
                    _importedBlueprintSources.hideFlags = HideFlags.HideAndDontSave;
                }
                // Imported templates stay inactive in the hierarchy while retaining activeSelf,
                // so native registration can clone active prefabs without exposing the source in-world.
                loader = new GLTFSceneImporter(path, new())
                {
                    ProcessMaterials = !GameRuntime.Environment.IsDedicatedServer,
                    SceneParent = _importedBlueprintSources.transform
                };
                var candidate = new BlueprintFileImport(definition, boxes, loader);
                candidate.LoadTask = loader.LoadSceneAsync();
                BlueprintImports.Add(candidate);
                import = candidate;
                return BuildingResult(BuildingActionStatus.ImportStarted, recipeId, true,
                    "Asset loading started. Complete the import on the Unity thread after IsLoadCompleted; no recipe or structure has been created by this result.");
            }
            catch (Exception exception)
            {
                if (loader != null && import == null) ReleaseBlueprintImporter(loader, true);
                return BuildingResult(BuildingActionStatus.Failed, recipeId, attempted, exception.Message);
            }
            finally { _nativeBusy = false; }
        }

        /// <summary>Completes a finished import through RegisterRuntimeBlueprint's validation/publication path.</summary>
        /// <remarks>
        /// The loader retains published source assets for its lifetime. A returned handle can remain
        /// published on partial failure; use PrepareRuntimeBlueprint, never destroy or reimport it.
        /// This does not block for a running task. Failed/changed definitions require a new import.
        /// </remarks>
        public static BuildingActionResult CompleteBlueprintFileImport(
            BlueprintFileImport import, out RuntimeBlueprint blueprint)
        {
            blueprint = null;
            if (!TryEnterNative(out var rejected)) return rejected;
            bool finalize = false;
            bool attempted = false;
            try
            {
                if (import == null || !BlueprintImports.Contains(import))
                    return BuildingResult(BuildingActionStatus.Rejected, 0, false, "This is not a pending import handle.");
                if (!import.IsLoadCompleted)
                    return BuildingResult(BuildingActionStatus.Unavailable, import.RecipeId, false, "The importer is still running; no synchronous wait was performed.");
                finalize = true;
                import.LoadTask.GetAwaiter().GetResult();
                var definition = import.Definition;
                if (!TryFindRegisteredBlueprint(definition.ModId, definition.BlueprintId, out var current)
                    || !ReferenceEquals(current, definition))
                    return BuildingResult(BuildingActionStatus.Rejected, import.RecipeId, false, "The registered definition changed while its asset was loading.");
                var prefab = import.Loader.CreatedObject;
                if (!prefab || !IsImportedBlueprintSource(prefab) || !prefab.activeSelf
                    || prefab.transform.localScale != Vector3.one || prefab.GetComponents<BoxCollider>().Length != 0)
                    return BuildingResult(BuildingActionStatus.Rejected, import.RecipeId, false, "The importer did not return an active-self, unit-scale source under the inactive source root, or it supplied an unexpected root collider.");
                var ingredients = prefab.GetComponentsInChildren<StructureCraftingNodeIngredient>(true);
                if (ingredients.Length == 0)
                    return BuildingResult(BuildingActionStatus.Rejected, import.RecipeId, false, "The imported asset has no SDK ItemId ingredients; no recipe was registered.");
                attempted = true;
                foreach (var ingredient in ingredients)
                {
                    foreach (var renderer in ingredient.GetComponentsInChildren<Renderer>(true))
                        if (!renderer.GetComponent<StructureGhostSwapper>()) renderer.gameObject.AddComponent<StructureGhostSwapper>();
                    ingredient.OnValidate();
                }
                foreach (var box in import.Boxes)
                {
                    var collider = prefab.AddComponent<BoxCollider>();
                    collider.center = box.Center;
                    collider.size = box.Size;
                    collider.isTrigger = false;
                    collider.enabled = true;
                }
                prefab.hideFlags = HideFlags.HideAndDontSave;
                var result = RegisterRuntimeBlueprintCore(definition.ModId, definition.BlueprintId, definition.RecipeId,
                    definition.DisplayName, prefab, out blueprint);
                return BuildingResult(result.Status, result.RecipeId, attempted || result.NativeCallAttempted, result.Detail);
            }
            catch (Exception exception)
            {
                return BuildingResult(BuildingActionStatus.Failed, import?.RecipeId ?? 0, attempted, exception.Message);
            }
            finally
            {
                if (finalize)
                {
                    // Register publishes before callbacks. Never discard that source after a callback fault.
                    bool published = blueprint != null && ReferenceEquals(blueprint.Registration.prefab, import.Loader.CreatedObject);
                    ReleaseBlueprintFileImport(import, !published);
                }
                _nativeBusy = false;
            }
        }

        /// <summary>Discards an unpublished import only after its task finishes, including faulted tasks. Does not cancel running native work.</summary>
        public static BuildingActionResult DiscardBlueprintFileImport(BlueprintFileImport import)
        {
            if (!TryEnterNative(out var rejected)) return rejected;
            try
            {
                if (import == null || !BlueprintImports.Contains(import))
                    return BuildingResult(BuildingActionStatus.Rejected, 0, false, "This is not a pending import handle.");
                if (!import.IsLoadCompleted)
                    return BuildingResult(BuildingActionStatus.Unavailable, import.RecipeId, false, "The importer is still running; finish loading before discarding it.");
                var error = import.LoadTask.Exception;
                var cleanupFailure = ReleaseBlueprintFileImport(import, true);
                return BuildingResult(cleanupFailure == null ? BuildingActionStatus.Submitted : BuildingActionStatus.Failed,
                    import.RecipeId, true, cleanupFailure != null ? "Import cleanup failed: " + cleanupFailure
                        : error == null ? "Importer released and unpublished source destruction requested."
                        : "Importer released and unpublished source destruction requested after load failure: " + error.GetBaseException().Message);
            }
            finally { _nativeBusy = false; }
        }

        private static bool IsImportedBlueprintSource(GameObject prefab)
            => _importedBlueprintSources && !_importedBlueprintSources.activeSelf && prefab
                && prefab.transform.parent == _importedBlueprintSources.transform;

        private static string ReleaseBlueprintFileImport(BlueprintFileImport import, bool discardSource)
        {
            var cleanupFailure = ReleaseBlueprintImporter(import.Loader, discardSource);
            BlueprintImports.Remove(import);
            import.Release(cleanupFailure);
            return cleanupFailure;
        }

        private static string ReleaseBlueprintImporter(GLTFSceneImporter loader, bool discardSource)
        {
            string failure = null;
            if (discardSource)
            {
                try { if (loader.CreatedObject) UnityEngine.Object.Destroy(loader.CreatedObject); }
                catch (Exception exception)
                {
                    failure = exception.Message;
                    BlueprintRegistration.ReportPrefabCleanupFailure(exception);
                }
            }
            try { loader.Dispose(); }
            catch (Exception exception)
            {
                failure = failure == null ? exception.Message : failure + "; " + exception.Message;
                BlueprintRegistration.ReportPrefabCleanupFailure(exception);
            }
            return failure;
        }
    }

    /// <summary>Retains the real importer until explicit completion/discard. IsLoadCompleted means task completion, not successful import or registration.</summary>
    public sealed class BlueprintFileImport
    {
        internal BlueprintFileImport(BlueprintDefinitionSnapshot definition,
            BlueprintFootprintBox[] boxes, GLTFSceneImporter loader)
        { Definition = definition; Boxes = boxes; Loader = loader; }
        public string ModId => Definition.ModId;
        public string BlueprintId => Definition.BlueprintId;
        public int RecipeId => Definition.RecipeId;
        public string SourceRelativePath => Definition.SourceRelativePath;
        public bool IsLoadCompleted => LoadTask?.IsCompleted == true;
        public bool IsReleased { get; private set; }
        /// <summary>A cleanup error, if any. Destruction is deferred by Unity; release is not proof of immediate destruction.</summary>
        public string CleanupFailure { get; private set; }
        internal BlueprintDefinitionSnapshot Definition { get; }
        internal BlueprintFootprintBox[] Boxes { get; }
        internal GLTFSceneImporter Loader { get; private set; }
        internal Task LoadTask { get; set; }
        internal void Release(string cleanupFailure) { Loader = null; IsReleased = true; CleanupFailure = cleanupFailure; }
    }
}
