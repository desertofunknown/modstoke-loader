// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Construction;
using UnityEngine;

namespace OpenACAI.Sons
{
    public static partial class OpenACAISonsServerConstruction
    {
        [ThreadStatic] private static CompletionDeployment _completionDeployment;

        private static void AfterCompletionEntityCreated(GameObject __0, bool __1, BoltEntity __result, bool __runOriginal)
        {
            var scope = _completion;
            if (scope?.Handlers == null || _publishingCompletion || !__runOriginal || !__1)
                return;
            try
            {
                if (!IsLiveConstructionObject(__0) || __0.Pointer != scope.BuiltPrefabPointer
                    || !IsLiveConstructionObject(__result))
                    return;
                foreach (var entity in scope.Entities)
                    if (entity.Pointer == __result.Pointer)
                        return;
                scope.Entities.Add(__result);
            }
            catch (Exception error)
            {
                scope.HasReadFailures = true;
                ReportObservationFailure(error);
            }
        }

        private static void BeforeCompletionDeployment(FreeFormGhostPrefab __instance, out CompletionDeployment __state)
        {
            __state = null;
            var owner = _completion;
            if (_completionDeployment == null && (owner?.Handlers == null || _publishingCompletion))
                return;
            var scope = new CompletionDeployment { Parent = _completionDeployment };
            __state = scope;
            _completionDeployment = scope;
            try
            {
                if (owner?.Handlers == null || _publishingCompletion || !IsLiveConstructionObject(__instance))
                    return;
                var transform = __instance.transform;
                if (IsLiveConstructionObject(transform) && transform.Pointer == owner.DeploymentTransformPointer)
                    scope.Owner = owner;
            }
            catch (Exception error)
            {
                if (owner != null)
                    owner.HasReadFailures = true;
                ReportObservationFailure(error);
            }
        }

        private static void AfterCompletionDeployment(bool __runOriginal, CompletionDeployment __state)
        {
            if (__state != null)
                __state.Outcome = __runOriginal ? OpenACAISonsConstructionCallOutcome.Returned : OpenACAISonsConstructionCallOutcome.Skipped;
        }

        private static Exception FinishCompletionDeployment(Exception __exception, CompletionDeployment __state)
        {
            if (__state != null)
            {
                _completionDeployment = __state.Parent;
                if (__exception != null)
                    __state.Outcome = OpenACAISonsConstructionCallOutcome.Faulted;
            }
            return __exception;
        }

        private static void BeforeCompletionRegistration(RuntimeStructureDatabase.RegisterStructureAction __instance,
            out CompletionRegistration __state)
        {
            __state = null;
            var owner = _completion;
            var deployment = _completionDeployment;
            if (owner?.Handlers == null || _publishingCompletion || deployment?.Owner != owner)
                return;
            try
            {
                var structure = __instance.Structure;
                if (!TryReadStructureRegistration(structure, out _, out _, out var registered))
                {
                    owner.HasReadFailures = true;
                    return;
                }
                if (!registered)
                    __state = new CompletionRegistration { Owner = owner, Deployment = deployment, Structure = structure };
            }
            catch (Exception error)
            {
                owner.HasReadFailures = true;
                ReportObservationFailure(error);
            }
        }

        private static void AfterCompletionRegistration(bool __runOriginal, CompletionRegistration __state)
        {
            if (__state != null)
                __state.Outcome = __runOriginal ? OpenACAISonsConstructionCallOutcome.Returned : OpenACAISonsConstructionCallOutcome.Skipped;
        }

        private static Exception FinishCompletionRegistration(RuntimeStructureDatabase.RegisterStructureAction __instance,
            Exception __exception, CompletionRegistration __state)
        {
            if (__state == null)
                return __exception;
            try
            {
                var current = __instance.Structure;
                if (!IsLiveConstructionObject(current) || current.Pointer != __state.Structure.Pointer
                    || !TryReadStructureRegistration(current, out var typeId, out var instanceId, out var registered))
                {
                    __state.Owner.HasReadFailures = true;
                    return __exception;
                }
                if (!registered)
                    return __exception;
                foreach (var entry in __state.Owner.Structures)
                    if (entry.Structure.Pointer == current.Pointer && entry.TypeId == typeId && entry.InstanceId == instanceId)
                        return __exception;
                // Registration can precede a later exception in a native created callback or deployment conversion.
                var outcome = __exception == null ? __state.Outcome : OpenACAISonsConstructionCallOutcome.Faulted;
                __state.Owner.Structures.Add(new RegisteredStructure(current, typeId, instanceId, outcome, __state.Deployment));
            }
            catch (Exception error)
            {
                __state.Owner.HasReadFailures = true;
                ReportObservationFailure(error);
            }
            finally { __state.Structure = null; }
            return __exception;
        }

        private static bool TryReadStructureRegistration(Structure structure, out int typeId, out uint instanceId, out bool registered)
        {
            typeId = 0;
            instanceId = 0;
            registered = false;
            if (!IsLiveConstructionObject(structure))
                return false;
            typeId = structure.TypeID;
            instanceId = structure.InstanceID;
            if (instanceId == 0)
                return true;
            if (instanceId > int.MaxValue)
                return false;
            registered = RuntimeStructureDatabase.TryGetStructure(typeId, (int)instanceId, out var current)
                && IsLiveConstructionObject(current) && current.Pointer == structure.Pointer
                && current.TypeID == typeId && current.InstanceID == instanceId;
            return true;
        }

        private static OpenACAISonsBlueprintEntityResult[] ReadCompletionEntities(CompletionScope scope)
        {
            var results = new OpenACAISonsBlueprintEntityResult[scope.Entities.Count];
            for (var i = 0; i < results.Length; i++)
            {
                try
                {
                    var entity = scope.Entities[i];
                    if (!IsLiveConstructionObject(entity))
                    {
                        results[i] = new(OpenACAISonsConstructionResultReadStatus.Unavailable);
                        continue;
                    }
                    var attached = entity.isAttached;
                    var prefabId = entity.prefabId.Value;
                    ulong? networkId = null;
                    if (attached)
                    {
                        var id = entity.networkId.PackedValue;
                        var current = id == 0 ? null : BoltNetwork.FindEntity(new Bolt.NetworkId(id));
                        if (!IsLiveConstructionObject(current) || current.Pointer != entity.Pointer)
                        {
                            results[i] = new(OpenACAISonsConstructionResultReadStatus.Unavailable, prefabId, attached);
                            continue;
                        }
                        networkId = id;
                    }
                    results[i] = new(OpenACAISonsConstructionResultReadStatus.Available, prefabId, attached, networkId);
                }
                catch (Exception error)
                {
                    scope.HasReadFailures = true;
                    results[i] = new(OpenACAISonsConstructionResultReadStatus.Failed);
                    ReportObservationFailure(error);
                }
            }
            return results;
        }

        private static OpenACAISonsBlueprintFreeFormResult[] ReadCompletionStructures(CompletionScope scope)
        {
            var results = new OpenACAISonsBlueprintFreeFormResult[scope.Structures.Count];
            for (var i = 0; i < results.Length; i++)
            {
                var entry = scope.Structures[i];
                OpenACAISonsConstructionResultReadStatus status;
                try
                {
                    status = TryReadStructureRegistration(entry.Structure, out var typeId, out var instanceId, out var registered)
                        && registered && typeId == entry.TypeId && instanceId == entry.InstanceId
                        ? OpenACAISonsConstructionResultReadStatus.Available : OpenACAISonsConstructionResultReadStatus.Unavailable;
                }
                catch (Exception error)
                {
                    scope.HasReadFailures = true;
                    status = OpenACAISonsConstructionResultReadStatus.Failed;
                    ReportObservationFailure(error);
                }
                results[i] = new(entry.TypeId, entry.InstanceId, entry.Outcome, entry.Deployment.Outcome, status);
            }
            return results;
        }

        private sealed class CompletionDeployment
        {
            internal CompletionDeployment Parent;
            internal CompletionScope Owner;
            internal OpenACAISonsConstructionCallOutcome Outcome;
        }

        private sealed class CompletionRegistration
        {
            internal CompletionScope Owner;
            internal CompletionDeployment Deployment;
            internal Structure Structure;
            internal OpenACAISonsConstructionCallOutcome Outcome;
        }

        private sealed record RegisteredStructure(Structure Structure, int TypeId, uint InstanceId,
            OpenACAISonsConstructionCallOutcome Outcome, CompletionDeployment Deployment);
    }
}
