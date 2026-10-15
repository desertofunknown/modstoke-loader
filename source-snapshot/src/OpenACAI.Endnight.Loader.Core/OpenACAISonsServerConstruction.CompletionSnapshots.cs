// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;

namespace OpenACAI.Sons
{
    /// <summary>Unvalidated completion command. This event can complete a node without an ingredient-complete check.</summary>
    public sealed class OpenACAISonsBlueprintCompletionRequest : OpenACAISonsConstructionRequest
    {
        internal OpenACAISonsBlueprintCompletionRequest(StructureCraftingNodeComplete source) : base(source)
        {
            RequestedTargetNetworkId = source.GetTargetNetworkIdValue().PackedValue;
            RoutingTargets = source.Targets;
            var entity = source.TargetEntity;
            RoutedEntityNetworkId = entity == null ? null : entity.NetworkId.PackedValue;
            var connection = source.TargetConnection;
            RoutedConnectionId = connection == null ? null : connection.ConnectionId;
        }

        public ulong RequestedTargetNetworkId { get; }
        public int RoutingTargets { get; }
        public ulong? RoutedEntityNetworkId { get; }
        public uint? RoutedConnectionId { get; }
    }

    /// <summary>Outcome of a patched invocation, including other patches; not a successful construction receipt.</summary>
    public enum OpenACAISonsConstructionCallOutcome { Unavailable, Returned, Skipped, Faulted }

    public enum OpenACAISonsConstructionResultReadStatus { Available, Unavailable, Failed }

    public enum OpenACAISonsCompletionSourceKind { IngredientAddition, CompletionCommand }

    /// <summary>Enclosing synchronous request for the same node, not its sole contributor or original author.</summary>
    public sealed class OpenACAISonsBlueprintCompletionSource
    {
        internal OpenACAISonsBlueprintCompletionSource(OpenACAISonsConstructionRequest request,
            OpenACAISonsCompletionSourceKind kind, ulong targetNetworkId, int? itemId = null, int? amount = null)
        {
            Kind = kind;
            RequestedTargetNetworkId = targetNetworkId;
            RequestedItemId = itemId;
            RequestedAmount = amount;
            SourceSession = request.SourceSession;
            SourceConnectionId = request.SourceConnectionId;
            RegisteredSteamId = request.RegisteredSteamId;
        }

        public OpenACAISonsCompletionSourceKind Kind { get; }
        public ulong RequestedTargetNetworkId { get; }
        public int? RequestedItemId { get; }
        public int? RequestedAmount { get; }
        public OpenACAISonsServerSession SourceSession { get; }
        public uint? SourceConnectionId { get; }
        /// <summary>Event-time game registration, not independent authentication or permission.</summary>
        public ulong? RegisteredSteamId { get; }
    }

    /// <summary>Managed observations collected during one StructureCompleted invocation on the server.</summary>
    public sealed class OpenACAISonsBlueprintCompletionObservation
    {
        internal OpenACAISonsBlueprintCompletionObservation(ulong? blueprintNetworkId, int? recipeId,
            bool wasRelocated, OpenACAISonsConstructionCallOutcome outcome, OpenACAISonsBlueprintCompletionSource source,
            OpenACAISonsBlueprintEntityResult[] entities, OpenACAISonsBlueprintFreeFormResult[] structures, bool hasReadFailures)
        {
            BlueprintNetworkId = blueprintNetworkId;
            RecipeId = recipeId;
            WasRelocated = wasRelocated;
            Outcome = outcome;
            Source = source;
            CreatedEntities = Array.AsReadOnly(entities);
            RegisteredStructures = Array.AsReadOnly(structures);
            HasReadFailures = hasReadFailures;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public ulong? BlueprintNetworkId { get; }
        public int? RecipeId { get; }
        public bool WasRelocated { get; }
        public OpenACAISonsConstructionCallOutcome Outcome { get; }
        /// <summary>Null outside a matching inspected request callback; no owner or host fallback.</summary>
        public OpenACAISonsBlueprintCompletionSource Source { get; }
        /// <summary>Matching delayed-attach Bolt factory returns, not necessarily attached or replicated entities.</summary>
        public IReadOnlyList<OpenACAISonsBlueprintEntityResult> CreatedEntities { get; }
        /// <summary>New database memberships during the matching free-form deployment, not fully converted structures.</summary>
        public IReadOnlyList<OpenACAISonsBlueprintFreeFormResult> RegisteredStructures { get; }
        /// <summary>A context or result read failed. False does not establish exhaustive coverage of other mods.</summary>
        public bool HasReadFailures { get; }
        public DateTimeOffset ObservedAt { get; }
    }

    public sealed class OpenACAISonsBlueprintEntityResult
    {
        internal OpenACAISonsBlueprintEntityResult(OpenACAISonsConstructionResultReadStatus status,
            int? prefabId = null, bool? isAttached = null, ulong? networkId = null)
        {
            ReadStatus = status;
            PrefabId = prefabId;
            IsAttached = isAttached;
            NetworkId = networkId;
        }

        /// <summary>Read at completion-observation time; creation was observed even if the result is no longer readable.</summary>
        public OpenACAISonsConstructionResultReadStatus ReadStatus { get; }
        public int? PrefabId { get; }
        public bool? IsAttached { get; }
        /// <summary>Present only when the returned entity is still attached and resolves to the same native object.</summary>
        public ulong? NetworkId { get; }
    }

    public sealed class OpenACAISonsBlueprintFreeFormResult
    {
        internal OpenACAISonsBlueprintFreeFormResult(int typeId, uint instanceId,
            OpenACAISonsConstructionCallOutcome registrationOutcome, OpenACAISonsConstructionCallOutcome deploymentOutcome,
            OpenACAISonsConstructionResultReadStatus status)
        {
            TypeId = typeId;
            InstanceId = instanceId;
            RegistrationOutcome = registrationOutcome;
            DeploymentOutcome = deploymentOutcome;
            ReadStatus = status;
        }

        public int TypeId { get; }
        public uint InstanceId { get; }
        public OpenACAISonsConstructionCallOutcome RegistrationOutcome { get; }
        public OpenACAISonsConstructionCallOutcome DeploymentOutcome { get; }
        /// <summary>Whether this exact structure still has the observed database membership when completion ends.</summary>
        public OpenACAISonsConstructionResultReadStatus ReadStatus { get; }
    }
}
