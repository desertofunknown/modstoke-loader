// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Construction;
using Il2CppInterop.Runtime;
using global::Sons.Crafting.Structures;
using global::Sons.Electricity;
using SonsSdk.Networking;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// Explicit ordered direct children of the node's last-child FreeFormGhostPrefab. Supply real
    /// prefab assets, not live/persistent scene objects. Native deployment detaches these structures
    /// and replaces each authored element with its registered profile's native Prefab. No graph,
    /// profile registration, generator, ingredient, link or lifecycle is synthesized by this API.
    /// This profile accepts exact native Beam, Pilar, Floor, Wall, Ramp, Stairs and HalfStairs structures with
    /// contained links and complete non-disabled ingredient lists. Ramps and stairs require authored
    /// beam/wall supports with reciprocal native links. HalfStairs require wall-supported native
    /// template geometry in both the authored and converted graph. Auto-foundations and other types are not supported.
    /// Retain every asset and keep it immutable. All peers must provide the same dependencies.
    /// </summary>
    public sealed class BuildingFreeFormGraph
    {
        public BuildingFreeFormGraph(FreeFormGhostPrefab deployment, params Structure[] structures)
        {
            ArgumentNullException.ThrowIfNull(structures);
            if (structures.Length == 0 || structures.Length > 4096)
                throw new ArgumentOutOfRangeException(nameof(structures));
            Deployment = deployment;
            Structures = Array.AsReadOnly((Structure[])structures.Clone());
        }

        public FreeFormGhostPrefab Deployment { get; }
        public IReadOnlyList<Structure> Structures { get; }
    }

    public static partial class Building
    {
        private static bool NativeProfileBuiltMatches(BuildingNativeProfile profile, int id)
        {
            var recipe = profile.Recipe;
            if (recipe._useFreeFormStructures && !recipe._hybridFreeFormAndScrew)
                return ReferenceEquals(profile.BuiltPrefab, null) && ReferenceEquals(recipe._builtPrefab, null);
            var prefab = profile.BuiltPrefab;
            if (!prefab || prefab.GetComponentsInChildren<Collider>(true).Length == 0) return false;
            var screw = prefab.GetComponent<ScrewStructure>();
            var electric = prefab.GetComponent<ElectricDeviceScrewStructure>();
            if (screw && !electric)
                return NativeProfilePrefab(prefab, id, BoltFactories.ScrewStructureState)
                    && screw._recipe == recipe && NativeProfileBehaviour(screw);
            if (!electric || screw || !NativeProfilePrefab(prefab, id, BoltFactories.ElectricDeviceScrewStructureState)
                || electric._recipe != recipe || !NativeProfileBehaviour(electric)) return false;
            // SetupElectricitySystem gets Wire on this object, assigns the receiver, then hooks
            // the wire into the native grid. Never replay it on a retained source prefab.
            var device = electric._device;
            var wire = prefab.GetComponent<Wire>();
            return device && IsWithinNativePrefab(device.transform, prefab.transform) && NativeProfileBehaviour(device)
                && device._PowerGrid_k__BackingField == null && !device._Wire_k__BackingField
                && wire && NativeProfileBehaviour(wire) && wire._PowerGrid_k__BackingField == null
                && wire._connectedWires != null && wire._connectedWires.Count == 0
                && wire._connectedPowerSources != null && wire._connectedPowerSources.Count == 0
                && wire._connectedDevices != null && wire._connectedDevices.Count == 0;
        }

        private static bool NativeFreeFormGraphMatches(BuildingNativeProfile profile)
        {
            var graph = profile.FreeFormGraph;
            if (!profile.Recipe._useFreeFormStructures) return graph == null;
            // Persistent scene copies may already have executed Awake/registration on their source
            // structures. Only untouched authored asset graphs have a supported lifecycle here.
            if (graph == null || !graph.Deployment || profile.NodePrefab.scene.IsValid()
                || (profile.BuiltPrefab && profile.BuiltPrefab.scene.IsValid()) || profile.Recipe._autoFoundation)
                return false;
            var root = profile.NodePrefab.transform;
            var deployment = graph.Deployment.transform;
            if (root.childCount == 0 || root.GetChild(root.childCount - 1) != deployment
                || !graph.Deployment.enabled || !deployment.gameObject.activeSelf
                || deployment.childCount != graph.Structures.Count
                || profile.NodePrefab.GetComponentsInChildren<FreeFormGhostPrefab>(true).Length != 1) return false;
            var members = new HashSet<IntPtr>();
            int elementCount = 0;
            for (int i = 0; i < graph.Structures.Count; i++)
            {
                var structure = graph.Structures[i];
                if (!structure || !NativeFreeFormStructureSupported(structure)
                    || structure.transform != deployment.GetChild(i) || !structure.enabled
                    || !structure.gameObject.activeSelf || structure.InstanceID != 0 || structure.SuperStructure != null
                    || !members.Add(structure.Pointer)) return false;
                var elements = structure.ElementRoot;
                if (!elements || !IsWithinNativePrefab(elements, structure.transform) || elements.childCount == 0
                    || elements.childCount > 4096 - elementCount) return false;
                elementCount += elements.childCount;
                for (int j = 0; j < elements.childCount; j++)
                {
                    var element = elements.GetChild(j).GetComponent<StructureElement>();
                    if (!element || !element._profile || !element.gameObject.activeSelf) return false;
                    var scale = element.transform.localScale;
                    if (!FiniteVector(scale) || scale.z <= 0) return false;
                    var replacement = element._profile.Prefab;
                    if (!replacement || !replacement._profile || replacement.gameObject.scene.IsValid()
                        || !replacement.gameObject.activeSelf || !replacement.enabled
                        || !NativeFreeFormLengthScaleMatches(replacement, scale.z)
                        || (graph.Deployment._preserveBakedRandomRotation
                            && (element.transform.childCount == 0 || replacement.transform.childCount == 0))) return false;
                }
            }
            foreach (var structure in graph.Structures)
            {
                var links = structure._structures;
                if (links == null || links.Length != structure.LinkedStructureCount) return false;
                foreach (var linked in links)
                    if (linked && !members.Contains(linked.Pointer)) return false;
                if (!NativeSlopedFreeFormLinksMatch(structure)) return false;
            }
            foreach (var structure in graph.Structures)
                if (!NativeHalfStairsFreeFormMatches(structure)) return false;
            // Verify the ingredient graph is real and entirely contained, not just matching totals.
            var node = profile.NodePrefab.GetComponent<StructureCraftingNode>();
            if (!node || node._craftingIngredientLinks == null) return false;
            var ingredients = new HashSet<IntPtr>();
            foreach (var link in node._craftingIngredientLinks)
            {
                if (link == null || link.Ingredient == null || link.Ingredient.DisabledCount != 0
                    || link.Ingredients == null || link.Ingredients.Count != link.Ingredient.Count) return false;
                for (int i = 0; i < link.Ingredients.Count; i++)
                {
                    var ingredient = link.Ingredients[i];
                    if (!ingredient || ingredient._itemId != link.Ingredient.ItemId
                        || !IsWithinNativePrefab(ingredient.transform, root) || !ingredients.Add(ingredient.Pointer)) return false;
                }
            }
            return ingredients.Count == profile.NodePrefab.GetComponentsInChildren<StructureCraftingNodeIngredient>(true).Length;
        }

        private static bool NativeFreeFormLengthScaleMatches(StructureElement replacement, float lengthScale)
        {
            // DeployFreeForm skips ApplyLengthScale within this tolerance. Outside it, the
            // helper reads the replacement profile's prefab size, not the authored ghost size.
            if (MathF.Abs(lengthScale - 1f) <= 0.001f) return true;
            var profile = replacement._profile;
            var sizeSource = profile.Prefab;
            if (!sizeSource || sizeSource.gameObject.scene.IsValid()) return false;
            var size = sizeSource._size;
            float offset = profile._extremityToSnapPositionOffset;
            if (!FiniteVector(size) || !float.IsFinite(offset)) return false;
            float ends = 2f * offset;
            float length = (size.z - ends) * lengthScale + ends;
            return float.IsFinite(length) && length > 0;
        }

        private static bool NativeFreeFormDependenciesReady(BuildingNativeProfile profile)
        {
            if (profile.FreeFormGraph == null) return true;
            if (!RuntimeStructureDatabase._instance || !ElementProfileDatabase._instance
                || FreeFormGhostPrefab._delayedCommandBatchProcessor == null || !FreeFormGhostPrefab._environmentCleanupManager)
                return false;
            var validatedProfiles = new HashSet<IntPtr>();
            foreach (var structure in profile.FreeFormGraph.Structures)
            {
                var generators = RuntimeStructureDatabase.StructureGenerators;
                if (generators == null || structure.TypeID >= generators.Length || generators[structure.TypeID] == null) return false;
                var root = structure.ElementRoot;
                for (int i = 0; i < root.childCount; i++)
                {
                    var element = root.GetChild(i).GetComponent<StructureElement>();
                    var source = element._profile;
                    var replacement = source.Prefab;
                    if (validatedProfiles.Add(source.Pointer) && !NativeElementProfileRegistered(source)) return false;
                    var converted = replacement._profile;
                    if (validatedProfiles.Add(converted.Pointer) && !NativeElementProfileRegistered(converted)) return false;
                }
            }
            return true;
        }

        private static bool NativeElementProfileRegistered(ElementProfile profile)
        {
            // Match native GetProfile's first-ID lookup in the already loaded registry without
            // invoking its TryGetOrLocateInstance asset-search path or registering anything.
            var profiles = ElementProfileDatabase._instance._profiles;
            if (profiles == null) return false;
            foreach (var current in profiles)
                if (current && current._id == profile._id) return current == profile;
            return false;
        }

        internal static bool NativeFreeFormStructureSupported(Structure structure)
        {
            if (!structure) return false;
            var type = structure.GetIl2CppType().Pointer;
            return type == Il2CppType.Of<BeamStructure>().Pointer || type == Il2CppType.Of<PilarStructure>().Pointer
                || type == Il2CppType.Of<FloorStructure>().Pointer || type == Il2CppType.Of<WallStructure>().Pointer
                || type == Il2CppType.Of<RampStructure>().Pointer || type == Il2CppType.Of<StairsStructure>().Pointer
                || type == Il2CppType.Of<HalfStairsStructure>().Pointer;
        }

        private static bool IsWithinNativePrefab(Transform candidate, Transform root)
            => candidate && root && (candidate == root || candidate.IsChildOf(root));
    }
}
