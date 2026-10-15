// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Numerics;

namespace OpenACAI.Sons
{
    public enum OpenACAISonsItemDropCallOutcome
    {
        Unavailable,
        Returned,
        Skipped,
        Faulted
    }

    public enum OpenACAISonsItemDropResultReadStatus
    {
        Available,
        Unavailable,
        Failed
    }

    /// <summary>Event-entry copies, before policy handlers or native processing. No native references are retained.</summary>
    public sealed class OpenACAISonsItemDropSource
    {
        internal OpenACAISonsItemDropSource(OpenACAISonsItemDropRequest request)
        {
            SourceConnectionId = request.SourceConnectionId;
            SourceSession = request.SourceSession;
            RegisteredSteamId = request.RegisteredSteamId;
            RequestedPrefabId = request.RequestedPrefabId;
            RequestedItemCount = request.RequestedItemCount;
            RequestedRawPreSpawnedNetworkId = request.RequestedRawPreSpawnedNetworkId;
            ResolvedPreSpawnedNetworkId = request.RequestedPreSpawnedNetworkId;
            RequestedPosition = request.RequestedPosition;
            RequestedRotation = request.RequestedRotation;
            ReceivedAt = request.ReceivedAt;
        }

        /// <summary>Immediate transport source, not necessarily the original author of a relayed event.</summary>
        public uint? SourceConnectionId { get; }
        /// <summary>Connection-lifetime handle, when tracking can resolve it. Its live metadata can change.</summary>
        public OpenACAISonsServerSession SourceSession { get; }
        /// <summary>Event-time game registration, not a separate authentication or permission verdict.</summary>
        public ulong? RegisteredSteamId { get; }
        public int RequestedPrefabId { get; }
        public int RequestedItemCount { get; }
        /// <summary>Raw untrusted payload, including unresolved and zero IDs.</summary>
        public ulong RequestedRawPreSpawnedNetworkId { get; }
        /// <summary>Entity resolved at entry, not proof that native processing later used it.</summary>
        public ulong? ResolvedPreSpawnedNetworkId { get; }
        public Vector3 RequestedPosition { get; }
        public Quaternion RequestedRotation { get; }
        public DateTimeOffset ReceivedAt { get; }
    }

    public sealed class OpenACAISonsItemDropObservation
    {
        internal OpenACAISonsItemDropObservation(OpenACAISonsItemDropSource source, OpenACAISonsItemDropCallOutcome outcome,
            bool policyAllowed, OpenACAISonsItemDropEntityResult[] results, bool hasReadFailures)
        {
            Source = source;
            Outcome = outcome;
            PolicyAllowed = policyAllowed;
            FactoryResults = Array.AsReadOnly(results);
            HasReadFailures = hasReadFailures;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Null if entry context could not be copied. Never infer a local source from null.</summary>
        public OpenACAISonsItemDropSource Source { get; }
        /// <summary>Managed hook outcome, not successful spawning or persistence.</summary>
        public OpenACAISonsItemDropCallOutcome Outcome { get; }
        /// <summary>Whether this loader's policy allowed the body. Other patches can still skip or change it.</summary>
        public bool PolicyAllowed { get; }
        /// <summary>
        /// Returns from the prefab/position/rotation overload matching entry arguments within this callback.
        /// An empty list does not prove that no object was created. Other patches can alter inputs or results.
        /// </summary>
        public IReadOnlyList<OpenACAISonsItemDropEntityResult> FactoryResults { get; }
        public bool HasReadFailures { get; }
        public DateTimeOffset ObservedAt { get; }
    }

    /// <summary>Factory-return fields, with the same entity binding checked again before delivery.</summary>
    public sealed class OpenACAISonsItemDropEntityResult
    {
        internal OpenACAISonsItemDropEntityResult(OpenACAISonsItemDropResultReadStatus status,
            int? prefabId, bool wasAttached, ulong? networkId)
        {
            Status = status;
            PrefabId = prefabId;
            WasAttached = wasAttached;
            NetworkId = networkId;
        }

        public OpenACAISonsItemDropResultReadStatus Status { get; }
        public int? PrefabId { get; }
        public bool WasAttached { get; }
        /// <summary>Current-world ID at factory return, not a persistent identity or permission to remove an object.</summary>
        public ulong? NetworkId { get; }
    }
}
