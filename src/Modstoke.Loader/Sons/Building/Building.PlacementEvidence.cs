// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Crafting.Structures;
using global::Sons.TerrainDetail;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class Building
    {
        [ThreadStatic] private static PlacementReceipt _placementReceipt;
        [ThreadStatic] private static NativePlacementScope _nativePlacementScope;
        private static bool _placementHooksReady;
        private static string _placementHooksFailure;

        private static bool EnsurePlacementObservationHooks(out string detail)
        {
            detail = _placementHooksFailure;
            if (_placementHooksReady) return true;
            if (_placementHooksFailure != null) return false;
            try
            {
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-building-placement");
                Patch(typeof(BoltNetwork), nameof(BoltNetwork.Instantiate),
                    new[] { typeof(GameObject), typeof(bool), typeof(Vector3), typeof(Quaternion) },
                    null, nameof(AfterPlacementFactory), null);
                Patch(typeof(StructureCraftingSystem), nameof(StructureCraftingSystem.SetActiveStructureNode),
                    new[] { typeof(StructureCraftingNode) }, null, nameof(AfterPlacementActiveNode), null);
                Patch(typeof(StructureCraftingNode), nameof(StructureCraftingNode.ShowGhost),
                    new[] { typeof(bool) }, null, nameof(AfterPlacementGhostShown), null);
                // Install the scope entry last. Until the complete group exists all observers are inert.
                Patch(typeof(StructureCraftingSystem), nameof(StructureCraftingSystem.PlaceStructureNode),
                    new[] { typeof(StructureRecipe), typeof(Vector3), typeof(Quaternion), typeof(bool), typeof(WorldLocatorId),
                        typeof(bool), typeof(bool), typeof(BoltEntity) },
                    nameof(BeforeNativePlacement), nameof(AfterNativePlacement), nameof(FinishNativePlacement));
                _placementHooksReady = true;

                void Patch(Type type, string name, Type[] args, string prefix, string postfix, string finalizer)
                {
                    var target = AccessTools.Method(type, name, args) ?? throw new MissingMethodException(type.FullName, name);
                    HarmonyMethod Method(string method, int priority) => method == null ? null
                        : new HarmonyMethod(typeof(Building), method) { priority = priority };
                    harmony.Patch(target, prefix: Method(prefix, Priority.First), postfix: Method(postfix, Priority.Last),
                        finalizer: Method(finalizer, Priority.Last));
                }
                return true;
            }
            catch (Exception exception)
            {
                _placementHooksFailure = detail = "Placement observation hooks could not be installed; capture is disabled. " + exception.Message;
                return false;
            }
        }

        private static void BeforeNativePlacement(StructureCraftingSystem __instance, StructureRecipe __0,
            out NativePlacementScope __state)
        {
            __state = null;
            if (!_placementHooksReady || !OnNativeThread || _placementReceipt == null) return;
            var scope = new NativePlacementScope { Parent = _nativePlacementScope };
            __state = scope;
            _nativePlacementScope = scope;
            try
            {
                // Nested placement calls get their own non-collecting scope, even with the same recipe.
                if (scope.Parent != null || !__instance || __instance != _placementReceipt.System
                    || !__0 || __0 != _placementReceipt.Recipe) return;
                scope.Receipt = _placementReceipt;
                scope.Receipt.NativePlacementCallCount++;
            }
            catch { _placementReceipt.HasReadFailures = true; }
        }

        private static void AfterPlacementFactory(GameObject __0, bool __1, BoltEntity __result, bool __runOriginal)
        {
            var scope = _nativePlacementScope;
            if (scope?.Receipt == null || !__runOriginal || !__1) return;
            try
            {
                if (!__0 || __0 != scope.Receipt.Recipe._structureNodePrefab || !__result) return;
                CapturePlacementNode(scope, __result.GetComponent<StructureCraftingNode>(), PlacementNodeEvidence.BoltFactoryReturn);
            }
            catch { scope.Receipt.HasReadFailures = true; }
        }

        private static void AfterPlacementActiveNode(StructureCraftingSystem __instance, StructureCraftingNode __0, bool __runOriginal)
        {
            var scope = _nativePlacementScope;
            if (scope?.Receipt == null || !__runOriginal) return;
            try
            {
                if (__instance == scope.Receipt.System && __instance.ActiveStructureNode == __0)
                    CapturePlacementNode(scope, __0, PlacementNodeEvidence.NativeActiveNode);
            }
            catch { scope.Receipt.HasReadFailures = true; }
        }

        private static void AfterPlacementGhostShown(StructureCraftingNode __instance, bool __0, bool __runOriginal)
        {
            var scope = _nativePlacementScope;
            if (scope?.Receipt == null || !__runOriginal || !__0) return;
            try { CapturePlacementNode(scope, __instance, PlacementNodeEvidence.NativeGhostShown); }
            catch { scope.Receipt.HasReadFailures = true; }
        }

        private static void CapturePlacementNode(NativePlacementScope scope, StructureCraftingNode node, PlacementNodeEvidence evidence)
        {
            if (!node || node == scope.Receipt.Recipe._structureNodePrefab.GetComponent<StructureCraftingNode>()
                || node._recipe != scope.Receipt.Recipe) return;
            foreach (var entry in scope.Nodes)
            {
                if (entry.Node != node) continue;
                entry.Evidence |= evidence;
                return;
            }
            if (scope.Receipt.Nodes.Count >= 4096)
            {
                scope.Receipt.WasTruncated = true;
                return;
            }
            var result = new PlacementNode(scope.Receipt, node, evidence);
            scope.Nodes.Add(result);
            scope.Receipt.Add(result);
        }

        private static void AfterNativePlacement(bool __runOriginal, NativePlacementScope __state)
        {
            if (__state != null) __state.Outcome = __runOriginal
                ? ConstructionCallOutcome.Returned : ConstructionCallOutcome.Skipped;
        }

        private static Exception FinishNativePlacement(Exception __exception, NativePlacementScope __state)
        {
            if (__state == null) return __exception;
            _nativePlacementScope = __state.Parent;
            if (__state.Receipt == null) return __exception;
            foreach (var entry in __state.Nodes)
                entry.PlacementCallOutcome = __exception == null ? __state.Outcome : ConstructionCallOutcome.Faulted;
            return __exception;
        }

        private sealed class NativePlacementScope
        {
            internal NativePlacementScope Parent;
            internal PlacementReceipt Receipt;
            internal ConstructionCallOutcome Outcome;
            internal readonly List<PlacementNode> Nodes = new();
        }
    }

    [Flags]
    public enum PlacementNodeEvidence
    {
        BoltFactoryReturn = 1, NativeActiveNode = 2, NativeGhostShown = 4
    }

    /// <summary>
    /// Exact objects observed synchronously inside matching native PlaceStructureNode calls, not an
    /// acknowledgment protocol. Client requests normally have no local results. Custom handlers can
    /// use other paths; empty or truncated evidence is not failure or exhaustive coverage. No owner,
    /// persistence or replication conclusion is inferred, even if the native calls returned.
    /// Other patches/custom callbacks executing inside a scope are included, not attributed solely
    /// to the original native method. The evidence flags distinguish factory returns from node use.
    /// </summary>
    public sealed class PlacementReceipt
    {
        private readonly List<PlacementNode> _nodes = new();
        internal PlacementReceipt(StructureCraftingSystem system, StructureRecipe recipe, int thread)
        {
            System = system; Recipe = recipe; OwnerThread = thread; RecipeId = recipe._id;
            Nodes = _nodes.AsReadOnly(); StartedAt = DateTimeOffset.UtcNow;
        }
        internal StructureCraftingSystem System { get; }
        internal StructureRecipe Recipe { get; }
        internal int OwnerThread { get; }
        internal void Add(PlacementNode node) => _nodes.Add(node);
        public int RecipeId { get; }
        public DateTimeOffset StartedAt { get; }
        public bool SubmitReturned { get; internal set; }
        public int NativePlacementCallCount { get; internal set; }
        public bool HasReadFailures { get; internal set; }
        public bool WasTruncated { get; internal set; }
        public IReadOnlyList<PlacementNode> Nodes { get; }
    }

    public sealed class PlacementNode
    {
        private readonly PlacementReceipt _receipt;
        internal PlacementNode(PlacementReceipt receipt, StructureCraftingNode node, PlacementNodeEvidence evidence)
        { _receipt = receipt; Node = node; Evidence = evidence; }
        internal StructureCraftingNode Node { get; }
        public PlacementNodeEvidence Evidence { get; internal set; }
        public ConstructionCallOutcome PlacementCallOutcome { get; internal set; }

        /// <summary>
        /// Resolves only the retained exact live node in the original world. Use the returned node
        /// with ObserveConstruction. A missing/destroyed/unregistered node is not treated as completed.
        /// </summary>
        public bool TryGetNode(out StructureCraftingNode node)
        {
            node = null;
            if (System.Environment.CurrentManagedThreadId != _receipt.OwnerThread) return false;
            try
            {
                if (!_receipt.System || !StructureCraftingSystem.TryGetInstance(out var current) || current != _receipt.System
                    || !Node || Node._pendingDestroy || Node._recipe != _receipt.Recipe
                    || Node._structureCraftingSystem != current || current._activeStructureNodes == null
                    || !current._activeStructureNodes.Contains(Node)) return false;
                if (BoltNetwork.isRunning)
                {
                    var entity = Node.entity;
                    if (!entity || !entity.isAttached || entity.networkId.PackedValue == 0 || BoltNetwork.FindEntity(entity.networkId) != entity) return false;
                }
                node = Node;
                return true;
            }
            catch { return false; }
        }
    }
}
