// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Modstoke.Sons
{
    /// <summary>Explicit mode policy; none of these decisions are inferred from enrollment claims.</summary>
    public interface IBoltMigrationCoordinatorFactory
    {
        /// <summary>
        /// Read-only authorization for this exact peer lifetime and dispatcher wire version, based
        /// on the consuming mode's established compatibility contract. Capability 15/16 is not enough.
        /// </summary>
        bool SupportsDispatcher(ControlPeer peer, byte prepareSourceVersion);

        /// <summary>
        /// Supply a complete, role-specific adapter. For Source, plan.Id is a server request ID,
        /// not the later source-generated snapshot ID. Refusal must not mutate gameplay state.
        /// </summary>
        bool TryCreateAdapter(BoltMigrationRole role, ControlPeer peer,
            BoltMigrationPlan plan, out IBoltMigrationAdapter adapter, out string detail);
    }

    /// <summary>
    /// Owner-thread dispatcher for explicit client-to-server migrations. One transaction is active
    /// at a time. Each old identity is attempted at most once per transport run, with a bounded
    /// lifetime ledger; uncertain mutations halt this coordinator until a new transport run.
    /// Selection does not offer capabilities, enable a vanilla adapter, or configure player bridges.
    /// </summary>
    public sealed partial class BoltMigrationCoordinator : IDisposable
    {
        public const int MaxTransactionsPerRun = 128;
        public const int MaxParticipants = 128;
        public const int TransactionTimeoutSeconds = 60;
        private static BoltMigrationCoordinator _selected;
        private static Guid _historyRun;
        private static readonly HashSet<ulong> AttemptedEntities = new();
        private static readonly HashSet<Guid> AttemptedIds = new();
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly IBoltMigrationCoordinatorFactory _factory;
        private Transaction _active;
        private bool _busy;
        private bool _disposed;
        private bool _disposeRequested;

        public bool IsDisposed => _disposed;
        public bool IsSelected => !_disposed && ReferenceEquals(_selected, this);
        public bool HasActiveMigration => _active != null && !_active.Terminal;
        public Guid ActiveRequestId => _active?.Request.RequestedPlan.Id ?? Guid.Empty;
        public ulong ActiveRequestGeneration => _active?.Request.RequestedPlan.Generation ?? 0;
        /// <summary>Failure or cancellation is accepted, but local cleanup has not finished.</summary>
        public bool HasPendingFailure => HasActiveMigration && _active.PendingFailure != null;
        public BoltMigrationSession ActiveSession => _active?.Session;
        public BoltMigrationSession LastSession { get; private set; }
        public BoltMigrationResult LastResult { get; private set; }
        public Guid LastRequestId { get; private set; }
        public ulong LastRequestGeneration { get; private set; }

        /// <summary>
        /// Owner-thread terminal notification. Session may be null, or lack a snapshot on failed
        /// preparation. Completed means the local session completed, not remote durable-save proof.
        /// Only a Completed result permits passing its player session to the separately configured
        /// bridge factory; an Unconfirmed result can accompany an already locally completed session.
        /// </summary>
        public event Action<BoltMigrationSession, BoltMigrationResult> Finished;

        private BoltMigrationCoordinator(IBoltMigrationCoordinatorFactory factory)
            => _factory = factory;

        public static bool SelectForNextStart(IBoltMigrationCoordinatorFactory factory,
            out BoltMigrationCoordinator coordinator, out string detail)
        {
            coordinator = null;
            detail = "A complete mode factory and no existing selected coordinator are required.";
            if (factory == null || _selected != null) return false;
            if (!BoltMigrationTransport.SelectForNextStart(true, out detail)) return false;
            coordinator = new BoltMigrationCoordinator(factory);
            _selected = coordinator;
            BoltMigrationTransport.PrepareSourceReceived += coordinator.OnPrepareSource;
            BoltMigrationTransport.SnapshotReceived += coordinator.OnSnapshot;
            BoltMigrationTransport.ReceiptReceived += coordinator.OnReceipt;
            detail = "Dispatcher selected for the next Bolt start; enrollment, version authorization and complete adapters remain explicit.";
            return true;
        }

        /// <summary>
        /// Server-only request. The destination adapter is supplied explicitly; remote source and
        /// observer adapters come from their factories. False can mean an uncertain queued request;
        /// requestId and LastResult remain available. No send or destructive phase is retried.
        /// </summary>
        public bool TryRequestMigration(ControlPeer source, ulong oldNetworkId,
            ulong generation, IBoltMigrationAdapter adapter, out Guid requestId, out string detail)
        {
            requestId = Guid.Empty;
            detail = "Use the selected, idle coordinator on its owner thread with a current enrolled client.";
            if (!Enter()) return false;
            try
            {
                if (!BeginRun(source) || _active != null || source.ServerSession == null
                    || oldNetworkId == 0 || generation == 0 || adapter == null
                    || !Authorized(source, BoltMigrationProtocol.Capability)
                    || !BoltMigrationNative.TryCapture(source, oldNetworkId, false, out var native, out detail))
                    return false;
                var plan = new BoltMigrationPlan(Guid.NewGuid(), generation, oldNetworkId,
                    native.PrefabId, native.StateTypeId, adapter.ContractId, adapter.ContractVersion);
                var request = new BoltMigrationPrepareSource(plan, Capabilities(plan));
                if (!request.IsValid)
                { detail = "The adapter contract or requested migration identity is invalid."; return false; }
                if (!adapter.CanMigrate(native.Entity, out detail)) return false;
                if (!TryCaptureCohort(source, request.RequiredCapabilities, out var cohort, out var sessions))
                { detail = "The complete client cohort lacks current enrollment or explicit dispatcher-version support."; return false; }
                if (!native.IsCurrent())
                { detail = "The requested native source lifetime changed during preflight."; return false; }
                if (!Reserve(source, plan.OldNetworkId, plan.Id))
                { detail = "This old identity was already attempted, or the bounded run ledger is full."; return false; }

                requestId = plan.Id;
                var transaction = new Transaction(source, request, BoltMigrationRole.Destination)
                { Adapter = adapter, Native = native, Participants = cohort, ServerSessions = sessions };
                _active = transaction;
                if (!Current(transaction))
                {
                    Fail(transaction, "The request cohort changed before transmission.");
                    detail = LastResult.Detail;
                    return false;
                }
                var sent = BoltMigrationTransport.SendPrepareSource(source, request);
                if (sent != BoltMigrationSendResult.Queued)
                {
                    // Prepare alone cannot authorize retirement. A late prepared source will time
                    // out and resume itself; this old identity remains in the no-replay ledger.
                    Fail(transaction, "PrepareSource delivery was " + sent + "; the request will not be replayed.");
                    detail = LastResult.Detail;
                    return false;
                }
                detail = "PrepareSource queued to the exact client; waiting for its snapshot and readiness.";
                return true;
            }
            catch (Exception error)
            {
                detail = "Migration request failed (" + error.GetType().Name + ").";
                if (_active != null) Fail(_active, detail);
                return false;
            }
            finally { Exit(); }
        }

        /// <summary>
        /// Cancel the exact active request on the selected coordinator's non-reentrant owner thread.
        /// Use ActiveRequestId (not the session snapshot ID) and ActiveRequestGeneration. The caller
        /// supplies trusted operator authorization; possession of these identities is not authorization.
        /// True means accepted, not necessarily cleaned up: HasPendingFailure and HasActiveMigration
        /// remain true while native callbacks unwind. Finished and the matching LastRequestId,
        /// LastRequestGeneration and LastResult report completion. Retirement may yield Unconfirmed,
        /// never a rollback. Selection and the run's no-replay ledger are preserved.
        /// </summary>
        public bool TryCancelMigration(Guid requestId, ulong generation, out string detail)
        {
            detail = "Use the selected coordinator on its non-reentrant owner thread.";
            if (!Enter()) return false;
            try
            {
                var transaction = _active;
                if (requestId == Guid.Empty || generation == 0 || transaction == null || transaction.Terminal
                    || transaction.Request.RequestedPlan.Id != requestId
                    || transaction.Request.RequestedPlan.Generation != generation)
                {
                    detail = "Cancellation requires the exact active request ID and generation; no request was changed.";
                    return false;
                }
                Fail(transaction, "Migration cancellation requested by the coordinator owner.");
                detail = transaction.Terminal ? LastResult.Detail
                    : transaction.PendingFailure + " Local cleanup is pending until native callbacks unwind; continue Update.";
                return true;
            }
            finally { Exit(); }
        }

        /// <summary>Call from the ordinary owner-thread update. Only availability checks are polled.</summary>
        internal static void Tick() => _selected?.Update();

        /// <summary>Advance the selected transaction on its owner thread.</summary>
        public void Update()
        {
            if (!Enter()) return;
            try
            {
                PlayerLifecycle.PruneActionTracking();
                var transaction = _active;
                if (transaction == null || transaction.Terminal) return;
                if (transaction.PendingFailure != null) Fail(transaction, transaction.PendingFailure);
                else if (!Current(transaction)) Fail(transaction, "The frozen peer, enrollment or server cohort changed.");
                else if (Expired(transaction)) Fail(transaction, "The migration lifetime expired; no phase will be retried.");
                else Advance(transaction);
            }
            catch (Exception error)
            {
                if (_active != null) Fail(_active, "Migration update failed (" + error.GetType().Name + ").");
            }
            finally { Exit(); }
        }

        private bool Enter()
        {
            if (Environment.CurrentManagedThreadId != _thread || _disposed || _busy
                || !ReferenceEquals(_selected, this)) return false;
            _busy = true;
            return true;
        }

        private void Exit()
        {
            _busy = false;
            if (_disposeRequested) Dispose();
        }

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("Dispose the migration coordinator on its owner thread.");
            if (_disposed) return;
            if (_busy) { _disposeRequested = true; return; }
            _busy = true;
            try
            {
                if (_active != null && !_active.Terminal) Fail(_active, "Migration dispatcher disposed.");
                if (_active != null && !_active.Terminal)
                { _disposeRequested = true; return; }
                _disposed = true;
                _active?.Barrier?.Dispose();
                BoltMigrationTransport.PrepareSourceReceived -= OnPrepareSource;
                BoltMigrationTransport.SnapshotReceived -= OnSnapshot;
                BoltMigrationTransport.ReceiptReceived -= OnReceipt;
                if (ReferenceEquals(_selected, this)) _selected = null;
                // Transport may already be running. Do not reset its channel or other consumers.
            }
            finally { _busy = false; }
        }

        private sealed class Participant
        {
            internal readonly ControlPeer Peer;
            internal readonly ulong Required;
            internal readonly ulong Offered;
            internal bool SnapshotSent;
            internal bool Ready;
            internal Participant(ControlPeer peer, EnrollmentSnapshot enrollment)
            { Peer = peer; Required = enrollment.RequiredCapabilities; Offered = enrollment.OfferedCapabilities; }
        }

        private sealed class Transaction
        {
            internal readonly ControlPeer Peer;
            internal readonly BoltMigrationPrepareSource Request;
            internal readonly BoltMigrationRole Role;
            internal readonly long Started = Stopwatch.GetTimestamp();
            internal IBoltMigrationAdapter Adapter;
            internal BoltMigrationNative Native;
            internal BoltMigrationSession Session;
            internal BoltMigrationSnapshot Snapshot;
            internal BoltMigrationBarrier Barrier;
            internal Participant[] Participants;
            internal IReadOnlyList<ServerSession> ServerSessions;
            internal BoltMigrationReceipt EarlyReady;
            internal BoltMigrationReceipt Retired;
            internal BoltMigrationReceipt Recreated;
            internal BoltMigrationReceipt AuthorizedRepair;
            internal bool RetirementPossible;
            internal bool ReadySent;
            internal bool RecreateAttempted;
            internal bool RepairAttempted;
            internal bool RepairSent;
            internal bool Terminal;
            internal string PendingFailure;

            internal Transaction(ControlPeer peer, BoltMigrationPrepareSource request,
                BoltMigrationRole role)
            { Peer = peer; Request = request; Role = role; }
        }
    }
}
