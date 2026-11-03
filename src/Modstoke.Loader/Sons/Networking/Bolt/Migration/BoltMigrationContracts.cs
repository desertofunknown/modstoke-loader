// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons
{
    public enum BoltMigrationAdmission { Pending, Ready, Rejected }

    /// <summary>
    /// Read-only admission before native preparation: wait for a settled action or explicit
    /// approval of the received inventory snapshot without replaying a mutating preparation.
    /// Pending is bounded by the coordinator's transaction timeout and confers no readiness.
    /// </summary>
    public interface IBoltMigrationAdmission
    {
        BoltMigrationAdmission ReadAdmission(BoltEntity original,
            BoltMigrationRole role, BoltMigrationSnapshot snapshot, out string detail);
    }

    /// <summary>
    /// Receives the exact transaction identity before capture. Implementations may retain context,
    /// but must not pause or mutate native objects here; TryQuiesce owns those effects.
    /// </summary>
    public interface IBoltMigrationPlanParticipant
    {
        bool TryBeginPreparation(BoltMigrationPlan plan, BoltMigrationRole role,
            ControlPeer peer, out string detail);
    }

    /// <summary>
    /// Records the exact replacement identity from an authenticated, correlated Recreated receipt.
    /// Repeated authorization of the same ID must be idempotent; conflicting IDs must be rejected.
    /// This may establish a one-time hold on that exact replacement after native retirement.
    /// An early receipt must only record the ID; it must not reject ordinary removal latency.
    /// Never select an object by pose or repeat initialization, restoration or reference repair.
    /// </summary>
    public interface IBoltMigrationReplacementIdentity
    {
        bool TryAuthorizeReplacementIdentity(ulong networkId, out string detail);
    }

    /// <summary>
    /// Separates one-time native creation from restoration that requires Unity startup or
    /// replicated state. Pending reads must not mutate objects or replay lifecycle callbacks.
    /// The coordinator retains the original transaction deadline throughout this phase.
    /// </summary>
    public interface IBoltMigrationReplacementAdmission
    {
        /// <summary>Destination only, once after creation. Hold writers without preventing required initialization.</summary>
        bool TryQuiesceReplacement(BoltEntity replacement, BoltMigrationSnapshot snapshot,
            out string detail);

        /// <summary>Destination before restoration; source/observer before their one-shot reference repair.</summary>
        BoltMigrationAdmission ReadReplacementAdmission(BoltEntity replacement,
            BoltMigrationRole role, BoltMigrationSnapshot snapshot, out string detail);
    }

    /// <summary>
    /// Preserves peer-local gameplay objects which outlive the old entity, such as seated actors.
    /// Replica readiness includes quiescence; rebind never resumes writers before aggregate repair.
    /// Implementations retain local handles during preparation, not in the wire snapshot.
    /// </summary>
    public interface IBoltMigrationReplicaLifecycle
    {
        /// <summary>Destination/observer preparation. False may have partially paused writers.</summary>
        bool TryQuiesceReplica(BoltEntity original, BoltMigrationRole role, out string detail);

        /// <summary>Every role, after reference repair. The old native entity may already be destroyed.</summary>
        bool TryRebindReplica(BoltEntity replacement, BoltMigrationSnapshot snapshot, out string detail);

        /// <summary>Every role, only after the exact aggregate repair authorization.</summary>
        bool TryResumeReplica(BoltEntity replacement, BoltMigrationSnapshot snapshot, out string detail);

        /// <summary>Destination/observer cancellation while the original native entity is still attached.</summary>
        bool TryCancelReplica(BoltEntity original, out string detail);
    }

    /// <summary>
    /// An explicit complete entity-type contract, not a generic serializer. Implementations must cover
    /// all Bolt state, gameplay components, save identities, physics/transform bindings and external
    /// references for their supported type. Refuse unknown versions, opaque state and incomplete coverage.
    /// No implementation or vanilla entity type is enabled automatically.
    /// </summary>
    public interface IBoltMigrationAdapter
    {
        Guid ContractId { get; }
        uint ContractVersion { get; }

        /// <summary>Read-only preflight, including whether native post-Attached restoration is safe.</summary>
        bool CanMigrate(BoltEntity entity, out string detail);

        /// <summary>
        /// Stops this exact entity's gameplay writers, simulation and side effects. The mode must
        /// maintain that quiescence until retirement or explicit cancellation. False may be partial.
        /// </summary>
        bool TryQuiesce(BoltEntity entity, out string detail);

        /// <summary>
        /// Capture independent, deterministic data, never native pointers, storage or borrowed mutable
        /// buffers. Unchanged quiescent state must encode identically when rechecked before retirement.
        /// </summary>
        bool TryCapture(BoltEntity entity, out byte[] payload, out string detail);

        /// <summary>Read-only complete schema/content preflight before the old owner is allowed to retire.</summary>
        bool TryValidateSnapshot(BoltMigrationSnapshot snapshot, out string detail);

        /// <summary>
        /// Restore the replacement after native creation/Attached callbacks, including remapping self
        /// references from snapshot.Plan.OldNetworkId to the replacement's new identity. Rebind native
        /// transforms, component caches, parenting, subscriptions and save records explicitly.
        /// The replacement must remain quiescent; no blind replay after false or an exception.
        /// </summary>
        bool TryRestore(BoltEntity replacement, BoltMigrationSnapshot snapshot, out string detail);

        /// <summary>Resume only a verified, fully repaired replacement, or the intact cancelled source.</summary>
        bool TryResume(BoltEntity entity, out string detail);

        /// <summary>
        /// Capture every locally owned inbound reference before retirement, including non-Bolt caches.
        /// Preparation must not write gameplay state. The returned participant owns validation and
        /// single-use repair. It must track reference-set changes until repair and reject incomplete
        /// coverage, including newly added references. A missing participant is not an empty set.
        /// </summary>
        bool TryPrepareReferences(ulong oldNetworkId, out IBoltMigrationReferenceRepair references,
            out string detail);
    }

    /// <summary>
    /// Required for every PlayerNetwork migration, in addition to complete state and reference coverage.
    /// Preserves the original local rig, controls and camera while its network identity is replaced.
    /// </summary>
    public interface IBoltPlayerMigrationAdapter : IBoltMigrationAdapter
    {
        /// <summary>Read-only, role-aware lifecycle preflight before any player is quiesced.</summary>
        bool TryValidatePlayerLifecycle(BoltEntity entity, BoltMigrationRole role, out string detail);

        /// <summary>
        /// Bind the retained original rig to the replacement after reference repair. Keep input and
        /// gameplay writers quiescent until aggregate repair authorization. False may be partial.
        /// </summary>
        bool TryRebindRetainedPlayer(BoltEntity originalRigEntity, BoltEntity replacement,
            BoltMigrationSnapshot snapshot, out string detail);

        /// <summary>
        /// Resume the rebound original rig and its controls only after aggregate repair authorization.
        /// False or an exception may be partial and must never be blindly replayed.
        /// </summary>
        bool TryResumeRetainedPlayer(BoltEntity originalRigEntity, BoltEntity replacement,
            BoltMigrationSnapshot snapshot, out string detail);
    }

    public interface IBoltMigrationReferenceRepair
    {
        /// <summary>
        /// Repair only the captured, still-current references. Preserve unrelated intervening writes.
        /// Cache completed results for the same replacement; never replay uncertain partial writes.
        /// </summary>
        BoltMigrationMutationResult TryRepair(BoltEntity replacement, out string detail);
    }

    public enum BoltMigrationMutationResult { Rejected, Unchanged, Applied, Unconfirmed }

    public enum BoltMigrationStatus
    {
        Preparing, Prepared, Retired, Recreated, ReferencesRepaired, Completed, Aborted, Unconfirmed,
        InitializingReplacement
    }

    /// <summary>Local phase evidence only. A receipt is not proof of remote integrity or durable saves.</summary>
    public sealed record BoltMigrationResult(BoltMigrationStatus Status,
        bool MutationAttempted, string Detail, BoltMigrationReceipt Receipt = null);
}
