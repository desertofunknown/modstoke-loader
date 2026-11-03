// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Modstoke.Sons
{
    public enum ItemDropCallOutcome
    {
        Unavailable,
        Returned,
        Skipped,
        Faulted
    }

    public enum ItemDropResultReadStatus
    {
        Available,
        Unavailable,
        Failed
    }

    /// <summary>Event-entry copies, before policy handlers or native processing. No native references are retained.</summary>
    public sealed class ItemDropSource
    {
        internal ItemDropSource(ItemDropRequest request)
        {
            SourceConnectionId = request.SourceConnectionId;
            SourceSession = request.SourceSession;
            RegisteredSteamId = request.RegisteredSteamId;
            RequestedPrefabId = request.RequestedPrefabId;
            RequestedItemCount = request.RequestedItemCount;
            RequestedRawPreSpawnedNetworkId = request.RequestedRawPreSpawnedNetworkId;
            ResolvedPreSpawnedNetworkId = request.RequestedPreSpawnedNetworkId;
            RequestedItemInstanceTypeName = request.RequestedItemInstanceTypeName;
            RequestedItemInstanceTypeNameReadSucceeded = request.RequestedItemInstanceTypeNameReadSucceeded;
            RequestedItemInstanceText = request.RequestedItemInstanceText;
            RequestedItemInstanceTextReadSucceeded = request.RequestedItemInstanceTextReadSucceeded;
            HasItemInstanceToken = request.HasItemInstanceToken;
            RequestedInstanceItemId = request.RequestedInstanceItemId;
            RequestedInstanceModuleCount = request.RequestedInstanceModuleCount;
            RequestedPreSpawnedTypeName = request.RequestedPreSpawnedTypeName;
            RequestedPreSpawnedTypeNameReadSucceeded = request.RequestedPreSpawnedTypeNameReadSucceeded;
            RequestedPreSpawnedText = request.RequestedPreSpawnedText;
            RequestedPreSpawnedTextReadSucceeded = request.RequestedPreSpawnedTextReadSucceeded;
            RequestedPosition = request.RequestedPosition;
            RequestedRotation = request.RequestedRotation;
            ReceivedAt = request.ReceivedAt;
        }

        /// <summary>Immediate transport source, not necessarily the original author of a relayed event.</summary>
        public uint? SourceConnectionId { get; }
        /// <summary>Connection-lifetime handle, when tracking can resolve it. Its live metadata can change.</summary>
        public ServerSession SourceSession { get; }
        /// <summary>Event-time game registration, not a separate authentication or permission verdict.</summary>
        public ulong? RegisteredSteamId { get; }
        public int RequestedPrefabId { get; }
        public int RequestedItemCount { get; }
        /// <summary>Raw untrusted payload, including unresolved and zero IDs.</summary>
        public ulong RequestedRawPreSpawnedNetworkId { get; }
        /// <summary>Entity resolved at entry, not proof that native processing later used it.</summary>
        public ulong? ResolvedPreSpawnedNetworkId { get; }
        /// <inheritdoc cref="ItemDropRequest.RequestedItemInstanceTypeName"/>
        public string RequestedItemInstanceTypeName { get; }
        /// <inheritdoc cref="ItemDropRequest.RequestedItemInstanceTypeNameReadSucceeded"/>
        public bool RequestedItemInstanceTypeNameReadSucceeded { get; }
        /// <inheritdoc cref="ItemDropRequest.RequestedItemInstanceText"/>
        public string RequestedItemInstanceText { get; }
        /// <inheritdoc cref="ItemDropRequest.RequestedItemInstanceTextReadSucceeded"/>
        public bool RequestedItemInstanceTextReadSucceeded { get; }
        /// <inheritdoc cref="ItemDropRequest.HasItemInstanceToken"/>
        public bool HasItemInstanceToken { get; }
        /// <inheritdoc cref="ItemDropRequest.RequestedInstanceItemId"/>
        public int? RequestedInstanceItemId { get; }
        /// <inheritdoc cref="ItemDropRequest.RequestedInstanceModuleCount"/>
        public int? RequestedInstanceModuleCount { get; }
        /// <inheritdoc cref="ItemDropRequest.RequestedPreSpawnedTypeName"/>
        public string RequestedPreSpawnedTypeName { get; }
        /// <inheritdoc cref="ItemDropRequest.RequestedPreSpawnedTypeNameReadSucceeded"/>
        public bool RequestedPreSpawnedTypeNameReadSucceeded { get; }
        /// <inheritdoc cref="ItemDropRequest.RequestedPreSpawnedText"/>
        public string RequestedPreSpawnedText { get; }
        /// <inheritdoc cref="ItemDropRequest.RequestedPreSpawnedTextReadSucceeded"/>
        public bool RequestedPreSpawnedTextReadSucceeded { get; }
        public Vector3 RequestedPosition { get; }
        public Quaternion RequestedRotation { get; }
        public DateTimeOffset ReceivedAt { get; }
    }

    public sealed class ItemDropObservation
    {
        internal ItemDropObservation(ItemDropSource source, ItemDropCallOutcome outcome,
            bool policyAllowed, ItemDropEntityResult[] results, bool hasReadFailures)
        {
            Source = source;
            Outcome = outcome;
            PolicyAllowed = policyAllowed;
            FactoryResults = Array.AsReadOnly(results);
            HasReadFailures = hasReadFailures;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Null if entry context could not be copied. Never infer a local source from null.</summary>
        public ItemDropSource Source { get; }
        /// <summary>Managed hook outcome, not successful spawning or persistence.</summary>
        public ItemDropCallOutcome Outcome { get; }
        /// <summary>Whether this loader's policy allowed the body. Other patches can still skip or change it.</summary>
        public bool PolicyAllowed { get; }
        /// <summary>
        /// Returns from the prefab/position/rotation overload matching entry arguments within this callback.
        /// An empty list does not prove that no object was created. Other patches can alter inputs or results.
        /// </summary>
        public IReadOnlyList<ItemDropEntityResult> FactoryResults { get; }
        /// <summary>Required context or result read failures. Optional diagnostics have their own success flags.</summary>
        public bool HasReadFailures { get; }
        public DateTimeOffset ObservedAt { get; }
    }

    /// <summary>Factory-return fields, with the same entity binding checked again before delivery.</summary>
    public sealed class ItemDropEntityResult
    {
        internal ItemDropEntityResult(ItemDropResultReadStatus status,
            int? prefabId, bool wasAttached, ulong? networkId)
        {
            Status = status;
            PrefabId = prefabId;
            WasAttached = wasAttached;
            NetworkId = networkId;
        }

        public ItemDropResultReadStatus Status { get; }
        public int? PrefabId { get; }
        public bool WasAttached { get; }
        /// <summary>Current-world ID at factory return, not a persistent identity or permission to remove an object.</summary>
        public ulong? NetworkId { get; }
    }
}
