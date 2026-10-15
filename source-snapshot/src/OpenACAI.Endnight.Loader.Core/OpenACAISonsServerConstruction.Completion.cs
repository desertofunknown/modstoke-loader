// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using Construction;
using HarmonyLib;
using OpenACAI.Endnight.Loader;
using Sons.Crafting.Structures;
using UnityEngine;

namespace OpenACAI.Sons
{
    public static partial class OpenACAISonsServerConstruction
    {
        [ThreadStatic] private static CompletionScope _completion;
        [ThreadStatic] private static CompletionRequestScope _completionRequest;
        [ThreadStatic] private static bool _publishingCompletion;
        private static int _reportedObservationFailure;

        /// <summary>Runs before server routing of the explicit completion command, not every local completion call.</summary>
        public static event Action<OpenACAISonsBlueprintCompletionRequest> CompletionRequested;

        /// <summary>
        /// Runs after a scoped server completion invocation, including skipped and faulted calls.
        /// Observations do not grant permission or establish conversion, persistence or replication success.
        /// </summary>
        public static event Action<OpenACAISonsBlueprintCompletionObservation> CompletionObserved;

        private static bool BeforeCompletionDispatch(Bolt.Event __0)
        {
            var handlers = CompletionRequested;
            if (handlers == null)
                return true;
            var source = __0?.TryCast<StructureCraftingNodeComplete>();
            return source == null || DispatchRouted(source, handlers,
                static value => new OpenACAISonsBlueprintCompletionRequest((StructureCraftingNodeComplete)value));
        }

        private static void InstallCompletionObservationHooks(Action<string> addStartupWarning)
        {
            PublishState(Hook.CompletionObservation, OpenACAISonsConstructionHookStatus.Installing,
                "Registering blueprint-completion observation hooks.");
            try
            {
                var harmony = new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + ".sons-blueprint-completion");
                Patch(typeof(CoopClientCallbacks), "OnEvent", new[] { typeof(StructureCraftingNodeAddIngredient) },
                    nameof(BeforeIngredientHandling), null, nameof(FinishCompletionRequest));
                Patch(typeof(CoopClientCallbacks), "OnEvent", new[] { typeof(StructureCraftingNodeComplete) },
                    nameof(BeforeCompletionHandling), null, nameof(FinishCompletionRequest));
                Patch(typeof(BoltNetwork), nameof(BoltNetwork.Instantiate),
                    new[] { typeof(GameObject), typeof(bool), typeof(Vector3), typeof(Quaternion) },
                    null, nameof(AfterCompletionEntityCreated), null);
                Patch(typeof(FreeFormGhostPrefab), nameof(FreeFormGhostPrefab.DeployFreeForm), Type.EmptyTypes,
                    nameof(BeforeCompletionDeployment), nameof(AfterCompletionDeployment), nameof(FinishCompletionDeployment));
                Patch(typeof(RuntimeStructureDatabase.RegisterStructureAction), "Execute", new[] { typeof(RuntimeStructureDatabase) },
                    nameof(BeforeCompletionRegistration), nameof(AfterCompletionRegistration), nameof(FinishCompletionRegistration));
                // Install the collecting entry last; partial registration remains inert until the group is installed.
                Patch(typeof(StructureCraftingNode), nameof(StructureCraftingNode.StructureCompleted), new[] { typeof(bool) },
                    nameof(BeforeCompletion), nameof(AfterCompletion), nameof(FinishCompletion));

                void Patch(Type type, string name, Type[] parameters, string prefix, string postfix, string finalizer)
                {
                    var target = AccessTools.Method(type, name, parameters)
                        ?? throw new MissingMethodException(type.FullName, name);
                    HarmonyMethod Method(string method, int priority) => method == null ? null :
                        new HarmonyMethod(typeof(OpenACAISonsServerConstruction), method) { priority = priority };
                    harmony.Patch(target, prefix: Method(prefix, Priority.First), postfix: Method(postfix, Priority.Last),
                        finalizer: Method(finalizer, Priority.Last));
                }
            }
            catch (Exception error)
            {
                PublishState(Hook.CompletionObservation, OpenACAISonsConstructionHookStatus.Failed,
                    "Completion observation registration failed. Collection is disabled; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server blueprint-completion observation hooks failed: " + error);
                return;
            }
            PublishState(Hook.CompletionObservation, OpenACAISonsConstructionHookStatus.Installed,
                "Completion observation hooks registered. Live capture and correlation remain unverified.");
        }

        private static bool CompletionHooksAvailable =>
            Environment.CurrentManagedThreadId == _ownerThreadId
            && State.CompletionObservationStatus == OpenACAISonsConstructionHookStatus.Installed;

        private static void BeforeIngredientHandling(StructureCraftingNodeAddIngredient __0, out CompletionRequestScope __state) =>
            BeginCompletionRequest(__0, false, out __state);

        private static void BeforeCompletionHandling(StructureCraftingNodeComplete __0, out CompletionRequestScope __state) =>
            BeginCompletionRequest(__0, true, out __state);

        private static void BeginCompletionRequest(Bolt.Event source, bool completionCommand, out CompletionRequestScope scope)
        {
            scope = null;
            if (!CompletionHooksAvailable || (_completionRequest == null && (CompletionObserved == null || _publishingCompletion)))
                return;
            scope = new CompletionRequestScope { Parent = _completionRequest };
            _completionRequest = scope;
            try
            {
                if (_publishingCompletion || CompletionObserved == null || !BoltNetwork.isServer)
                    return;
                OpenACAISonsConstructionRequest request;
                if (completionCommand)
                {
                    var value = new OpenACAISonsBlueprintCompletionRequest((StructureCraftingNodeComplete)source);
                    request = value;
                    scope.Source = new OpenACAISonsBlueprintCompletionSource(value,
                        OpenACAISonsCompletionSourceKind.CompletionCommand, value.RequestedTargetNetworkId);
                }
                else
                {
                    var value = new OpenACAISonsBlueprintIngredientRequest((StructureCraftingNodeAddIngredient)source);
                    request = value;
                    scope.Source = new OpenACAISonsBlueprintCompletionSource(value,
                        OpenACAISonsCompletionSourceKind.IngredientAddition, value.RequestedTargetNetworkId,
                        value.RequestedItemId, value.RequestedAmount);
                }
                request.Close();
                var id = scope.Source.RequestedTargetNetworkId;
                var entity = id == 0 ? null : BoltNetwork.FindEntity(new Bolt.NetworkId(id));
                if (!IsLiveConstructionObject(entity) || !entity.isAttached || entity.networkId.PackedValue != id
                    || !entity.TryGetComponent<StructureCraftingNode>(out var node) || !IsLiveConstructionObject(node))
                    return;
                var nodeEntity = node.entity;
                if (IsLiveConstructionObject(nodeEntity) && nodeEntity.Pointer == entity.Pointer)
                    scope.NodePointer = node.Pointer;
            }
            catch (Exception error)
            {
                scope.ReadFailed = true;
                ReportObservationFailure(error);
            }
        }

        private static Exception FinishCompletionRequest(Exception __exception, CompletionRequestScope __state)
        {
            if (__state != null)
                _completionRequest = __state.Parent;
            return __exception;
        }

        private static void BeforeCompletion(StructureCraftingNode __instance, bool __0, out CompletionScope __state)
        {
            __state = null;
            if (!CompletionHooksAvailable || (_completion == null && (CompletionObserved == null || _publishingCompletion)))
                return;
            var scope = new CompletionScope { Parent = _completion, WasRelocated = __0 };
            __state = scope;
            _completion = scope;
            try
            {
                if (_publishingCompletion || !BoltNetwork.isServer)
                    return;
                scope.Handlers = CompletionObserved;
                if (scope.Handlers == null)
                    return;
                if (!IsLiveConstructionObject(__instance))
                {
                    scope.HasReadFailures = true;
                    return;
                }
                var entity = __instance.entity;
                if (IsLiveConstructionObject(entity) && entity.isAttached)
                {
                    var id = entity.networkId.PackedValue;
                    var registered = id == 0 ? null : BoltNetwork.FindEntity(new Bolt.NetworkId(id));
                    if (IsLiveConstructionObject(registered) && registered.Pointer == entity.Pointer)
                        scope.BlueprintNetworkId = id;
                }
                var source = _completionRequest;
                if (source != null)
                {
                    scope.HasReadFailures |= source.ReadFailed;
                    if (source.NodePointer == __instance.Pointer && source.Source != null
                        && source.Source.RequestedTargetNetworkId == scope.BlueprintNetworkId)
                        scope.Source = source.Source;
                }
                var recipe = __instance.Recipe;
                if (IsLiveConstructionObject(recipe))
                {
                    scope.RecipeId = recipe.Id;
                    var prefab = recipe._builtPrefab;
                    if (IsLiveConstructionObject(prefab))
                        scope.BuiltPrefabPointer = prefab.Pointer;
                }
                if (!IsLiveConstructionObject(recipe) || recipe.UseFreeFormStructure)
                {
                    var transform = __instance.transform;
                    if (IsLiveConstructionObject(transform) && transform.childCount > 0)
                    {
                        var child = transform.GetChild(transform.childCount - 1);
                        if (IsLiveConstructionObject(child))
                            scope.DeploymentTransformPointer = child.Pointer;
                    }
                }
            }
            catch (Exception error)
            {
                scope.HasReadFailures = true;
                ReportObservationFailure(error);
            }
        }

        private static void AfterCompletion(bool __runOriginal, CompletionScope __state)
        {
            if (__state != null)
                __state.Outcome = __runOriginal ? OpenACAISonsConstructionCallOutcome.Returned : OpenACAISonsConstructionCallOutcome.Skipped;
        }

        private static Exception FinishCompletion(Exception __exception, CompletionScope __state)
        {
            if (__state == null)
                return __exception;
            _completion = __state.Parent;
            var wasPublishing = _publishingCompletion;
            _publishingCompletion = true;
            try
            {
                if (__state.Handlers == null)
                    return __exception;
                var entities = ReadCompletionEntities(__state);
                var structures = ReadCompletionStructures(__state);
                var observation = new OpenACAISonsBlueprintCompletionObservation(__state.BlueprintNetworkId,
                    __state.RecipeId, __state.WasRelocated, __exception == null ? __state.Outcome : OpenACAISonsConstructionCallOutcome.Faulted,
                    __state.Source, entities, structures, __state.HasReadFailures);
                __state.Entities.Clear();
                __state.Structures.Clear();
                foreach (Action<OpenACAISonsBlueprintCompletionObservation> handler in __state.Handlers.GetInvocationList())
                {
                    try { handler(observation); }
                    catch (Exception error) { ReportObservationFailure(error); }
                }
            }
            catch (Exception error) { ReportObservationFailure(error); }
            finally
            {
                __state.Entities.Clear();
                __state.Structures.Clear();
                _publishingCompletion = wasPublishing;
            }
            return __exception;
        }

        private static bool IsLiveConstructionObject(UnityEngine.Object value) =>
            value is not null && !value.WasCollected && value;

        private static void ReportObservationFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedObservationFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Blueprint completion observation failed ("
                    + error.GetType().FullName + "). Native handling is unchanged; further observation failures will not be logged.");
            }
            catch (Exception) { }
        }

        private sealed class CompletionRequestScope
        {
            internal CompletionRequestScope Parent;
            internal IntPtr NodePointer;
            internal OpenACAISonsBlueprintCompletionSource Source;
            internal bool ReadFailed;
        }

        private sealed class CompletionScope
        {
            internal CompletionScope Parent;
            internal Action<OpenACAISonsBlueprintCompletionObservation> Handlers;
            internal ulong? BlueprintNetworkId;
            internal int? RecipeId;
            internal bool WasRelocated;
            internal bool HasReadFailures;
            internal IntPtr BuiltPrefabPointer;
            internal IntPtr DeploymentTransformPointer;
            internal OpenACAISonsBlueprintCompletionSource Source;
            internal OpenACAISonsConstructionCallOutcome Outcome;
            internal readonly List<BoltEntity> Entities = new();
            internal readonly List<RegisteredStructure> Structures = new();
        }
    }
}
