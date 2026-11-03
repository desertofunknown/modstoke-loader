// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Loader;
using global::Sons.Crafting.Structures;
using TheForest.Player.Actions;
using UnityEngine;
using NativeCollections = Il2CppSystem.Collections.Generic;
using SinglePlaceOperation = TheForest.Player.Actions.PlayerStructurePlaceAction.SinglePlaceOperation;

namespace Modstoke.Loader
{
    internal static class PlacementCollisionPatch
    {
        private static int _installationAttempted;
        private static int _reportedFailure;

        internal static void InstallHarmonyPatch(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                warning?.Invoke("Placement collision fix is unavailable: Harmony support is not ready.");
                return;
            }

            try
            {
                // The geometry below follows this native build, including its foundation adjustments.
                if (!GameBuild.IsRecognized)
                {
                    // The dedicated-server startup warning already covers client-pinned fixes.
                    if (!GameBuild.IsDedicatedServer)
                        warning?.Invoke("Placement collision fix skipped for an unrecognized game build; native placement remains unchanged.");
                    return;
                }

                var target = AccessTools.Method(typeof(SinglePlaceOperation), nameof(SinglePlaceOperation.CheckIfPlacementSpaceIsValid),
                    new[] { typeof(StructureCraftingNode), typeof(float) })
                    ?? throw new MissingMethodException(typeof(SinglePlaceOperation).FullName, nameof(SinglePlaceOperation.CheckIfPlacementSpaceIsValid));
                var prefix = new HarmonyMethod(typeof(PlacementCollisionPatch), nameof(BeforeCheck)) { priority = Priority.Last };
                new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-placement-collisions").Patch(target, prefix: prefix);
                RuntimeEntryPoint.LogSource?.LogInfo("Placement collision buffer fix registered for the recognized game build.");
            }
            catch (Exception error)
            {
                warning?.Invoke("Placement collision fix registration did not complete: " + error);
            }
        }

        private static bool BeforeCheck(SinglePlaceOperation __instance, StructureCraftingNode __0, float __1,
            bool __runOriginal, ref bool __result)
        {
            if (!__runOriginal)
                return false;
            __result = false;
            try
            {
                __result = CheckPlacementSpace(__instance, __0, __1);
            }
            catch (Exception error)
            {
                if (Interlocked.Exchange(ref _reportedFailure, 1) == 0)
                {
                    try { RuntimeEntryPoint.LogSource?.LogWarning("Placement collision check failed; this placement was rejected: " + error); }
                    catch (Exception) { }
                }
            }
            return false;
        }

        private static StructureRecipe Recipe(SinglePlaceOperation operation) =>
            operation._dynamicOverrideRecipe ?? operation._screwOverrideRecipe ?? operation._freeformOverrideRecipe ?? operation._recipe;

        private static bool CheckPlacementSpace(SinglePlaceOperation operation, StructureCraftingNode node, float foundationHeight)
        {
            var colliders = node.GetColliders();
            var provider = node.GetComponent<IAutoFoundationExtendTriggersProvider>();
            var triggers = provider?.GetExtendTriggers()?.Cast<NativeCollections.IEnumerable<BoxCollider>>();
            var recipe = Recipe(operation);
            var shelter = recipe._useFreeFormStructures && recipe._category == StructureRecipe.CategoryType.Shelters;
            var scale = recipe._autoFoundation ? 1f : shelter ? 1.2f : 0.7f;
            var transform = node.transform;
            if (recipe._useOverridePlacementSize)
                scale = recipe._overridePlacementSize;

            foreach (var collider in colliders)
            {
                var size = collider.size;
                size.z *= scale;
                size.x *= scale;
                var offset = 0f;
                if (!shelter)
                {
                    size.y *= scale;
                    offset = 0.1f;
                }

                var extend = Recipe(operation)._autoFoundation && triggers is not null
                    && Il2CppSystem.Linq.Enumerable.Contains(triggers, collider);
                if (extend)
                {
                    var extension = foundationHeight - 0.65f;
                    if (0f > extension)
                        extension = 0f;
                    size.z -= 0.75f;
                    size.y += extension;
                    size.x -= 0.75f;
                    offset = foundationHeight * -0.5f;
                    offset += 0.3f;
                }

                var rotation = transform.rotation;
                var position = transform.position;
                var center = position + rotation * (collider.center + Vector3.up * offset);
                var halfExtents = size * 0.5f;
                halfExtents *= 1f - operation._placementBoundsThreshold;
                var mask = operation.GetOverlapsLayerMask();
                var results = operation._overlapCastResults;
                if (results is null || results.Length == 0)
                    throw new InvalidOperationException("The placement overlap buffer is unavailable.");
                int count;
                while (true)
                {
                    count = Physics.OverlapBoxNonAlloc(center, halfExtents, results, rotation, mask);
                    if (count < results.Length)
                        break;

                    // A full buffer cannot prove that no blocker was omitted. Keep the larger buffer for later frames.
                    results = new Il2CppReferenceArray<Collider>(checked(results.Length * 2));
                    operation._overlapCastResults = results;
                }

                recipe = Recipe(operation);
                var blacklist = recipe._useGameObjectTagBlackListFilter
                    ? recipe._gameObjectTagBlackListIds?.Cast<NativeCollections.IReadOnlyList<int>>() : null;
                for (var i = 0; i < count; i++)
                {
                    if (!operation.Method_Private_Boolean_Collider_Boolean_IReadOnlyList_1_Int32_0(results[i], extend, blacklist))
                        return false;
                }
            }
            return true;
        }
    }
}
