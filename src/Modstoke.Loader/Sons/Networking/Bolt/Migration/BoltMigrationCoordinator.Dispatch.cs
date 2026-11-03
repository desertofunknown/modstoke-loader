// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Bolt;

namespace Modstoke.Sons
{
    public sealed partial class BoltMigrationCoordinator
    {
        private void OnPrepareSource(ControlPeer server, BoltMigrationPrepareSource request)
        {
            if (!Enter()) return;
            try
            {
                if (!request.IsValid || server?.ServerSession != null
                    || request.RequiredCapabilities != Capabilities(request.RequestedPlan)
                    || !Authorized(server, request.RequiredCapabilities) || !BeginRun(server)
                    || _active != null || !Reserve(server, request.RequestedPlan.OldNetworkId, request.RequestedPlan.Id)) return;
                var transaction = new Transaction(server, request, BoltMigrationRole.Source);
                _active = transaction;
                if (!CaptureServer(transaction)
                    || !_factory.TryCreateAdapter(transaction.Role, server, request.RequestedPlan,
                        out transaction.Adapter, out var detail))
                {
                    Fail(transaction, "The source factory or server lifetime refused preparation.");
                    return;
                }
                if (!AdapterMatches(transaction)
                    || !BoltMigrationNative.TryCapture(server, request.RequestedPlan.OldNetworkId, true,
                        out transaction.Native, out detail)
                    || transaction.Native.PrefabId != request.RequestedPlan.PrefabId
                    || transaction.Native.StateTypeId != request.RequestedPlan.StateTypeId || !Check(transaction))
                {
                    Fail(transaction, "The requested source identity or adapter contract is unavailable.");
                    return;
                }
                PrepareSource(transaction);
            }
            catch (Exception error)
            {
                if (_active != null) Fail(_active, "Source dispatch failed (" + error.GetType().Name + ").");
            }
            finally { Exit(); }
        }

        private void PrepareSource(Transaction transaction)
        {
            if (transaction.Adapter is PlayerMigrationAdapter player)
            {
                var protection = player.BeginSourceAdmissionProtection(transaction.Native.Entity,
                    transaction.Peer, transaction.Request.RequestedPlan, out var protectionDetail);
                if (protection != BoltMigrationAdmission.Ready)
                {
                    if (protection != BoltMigrationAdmission.Pending) Fail(transaction, protectionDetail);
                    return;
                }
            }
            if (!AdmissionReady(transaction)) return;
            var plan = transaction.Request.RequestedPlan;
            if (!BoltMigrationSession.TryPrepareSource(transaction.Peer, plan.OldNetworkId,
                    plan.Generation, transaction.Adapter, out transaction.Session, out var result))
            { Fail(transaction, result.Detail); return; }
            transaction.Snapshot = transaction.Session.Snapshot;
            if (transaction.Snapshot == null || !transaction.Request.Matches(transaction.Snapshot.Plan)
                || transaction.Session.ReadyReceipt == null || !Check(transaction))
            { Fail(transaction, "The prepared source did not produce the exact requested snapshot contract."); return; }
            var sent = BoltMigrationTransport.SendSnapshot(transaction.Peer, transaction.Snapshot);
            if (sent != BoltMigrationSendResult.Queued)
            { Fail(transaction, "Source snapshot delivery was " + sent + "."); return; }
            transaction.ReadySent = SendReceipt(transaction, transaction.Peer, transaction.Session.ReadyReceipt);
        }

        private bool AdmissionReady(Transaction transaction)
        {
            if (!Check(transaction) || !AdapterMatches(transaction) || !transaction.Native.IsCurrent())
            {
                if (!transaction.Terminal) Fail(transaction, "The preparation identity or contract changed during admission.");
                return false;
            }
            if (transaction.Adapter is not IBoltMigrationAdmission admission) return true;
            var decision = admission.ReadAdmission(transaction.Native.Entity, transaction.Role,
                transaction.Snapshot, out var detail);
            if (!Check(transaction) || !transaction.Native.IsCurrent() || !AdapterMatches(transaction))
            {
                if (!transaction.Terminal) Fail(transaction, "The admission callback changed its native identity or contract.");
                return false;
            }
            if (decision == BoltMigrationAdmission.Ready) return true;
            if (decision != BoltMigrationAdmission.Pending)
                Fail(transaction, string.IsNullOrWhiteSpace(detail) ? "The adapter rejected migration admission." : detail);
            return false;
        }

        private void OnSnapshot(ControlPeer peer, BoltMigrationSnapshot snapshot)
        {
            if (!Enter()) return;
            try
            {
                if (snapshot == null || !Authorized(peer, Capabilities(snapshot.Plan)) || !BeginRun(peer)) return;
                var transaction = _active;
                if (transaction != null)
                {
                    if (transaction.Terminal || !ReferenceEquals(transaction.Peer, peer)
                        || !transaction.Request.Matches(snapshot.Plan)) return;
                    if (!Check(transaction)) return;
                    if (transaction.Snapshot != null)
                    {
                        if (transaction.Snapshot.Plan != snapshot.Plan
                            || !transaction.Snapshot.MatchesDigest(snapshot.DigestSpan))
                            Fail(transaction, "A conflicting snapshot was received for the active request.");
                        return;
                    }
                    if (transaction.Role != BoltMigrationRole.Destination) return;
                    PrepareDestination(transaction, snapshot);
                }
                else if (peer.ServerSession == null && Reserve(peer, snapshot.Plan.OldNetworkId, snapshot.Plan.Id))
                {
                    transaction = new Transaction(peer, new BoltMigrationPrepareSource(snapshot.Plan,
                        Capabilities(snapshot.Plan)), BoltMigrationRole.Observer) { Snapshot = snapshot };
                    _active = transaction;
                    PrepareObserver(transaction);
                }
                if (transaction != null && ReferenceEquals(_active, transaction)
                    && !transaction.Terminal && Check(transaction)) Advance(transaction);
            }
            catch (Exception error)
            {
                if (_active != null) Fail(_active, "Snapshot dispatch failed (" + error.GetType().Name + ").");
            }
            finally { Exit(); }
        }

        private void PrepareDestination(Transaction transaction, BoltMigrationSnapshot snapshot)
        {
            transaction.Snapshot = snapshot;
            FindParticipant(transaction, transaction.Peer).SnapshotSent = true;
            if (!AdapterMatches(transaction) || !transaction.Native.IsCurrent())
            {
                Fail(transaction, "The destination adapter or old native identity changed before preparation.");
                return;
            }
            if (!AdmissionReady(transaction)) return;
            if (!BoltMigrationSession.TryPrepareDestination(transaction.Peer, snapshot,
                transaction.Adapter, out transaction.Session, out var result))
            {
                Fail(transaction, result.Detail);
                return;
            }
            if (!Check(transaction)
                || !BoltMigrationBarrier.TryCreate(snapshot, out transaction.Barrier, out _)
                || !transaction.Barrier.RecordLocal(transaction.Session.ReadyReceipt) || !Check(transaction))
            {
                Fail(transaction, "The destination could not establish the exact complete readiness barrier.");
                return;
            }
            if (transaction.EarlyReady != null)
            {
                if (!transaction.EarlyReady.Matches(snapshot)
                    || !transaction.Barrier.TryRecord(transaction.Peer, transaction.EarlyReady))
                {
                    Fail(transaction, "The source readiness did not match its snapshot.");
                    return;
                }
                FindParticipant(transaction, transaction.Peer).Ready = true;
                transaction.EarlyReady = null;
            }
            foreach (var participant in transaction.Participants)
            {
                if (ReferenceEquals(participant.Peer, transaction.Peer)) continue;
                if (!Check(transaction)) return;
                participant.SnapshotSent = true;
                var sent = BoltMigrationTransport.SendSnapshot(participant.Peer, snapshot);
                if (sent != BoltMigrationSendResult.Queued)
                {
                    Fail(transaction, "Observer snapshot delivery was " + sent + ".");
                    return;
                }
            }
        }

        private void PrepareObserver(Transaction transaction)
        {
            if (transaction.Adapter == null && (!CaptureServer(transaction)
                || !_factory.TryCreateAdapter(transaction.Role, transaction.Peer, transaction.Snapshot.Plan,
                    out transaction.Adapter, out _) || !AdapterMatches(transaction)
                || !BoltMigrationNative.TryCapture(transaction.Peer, transaction.Snapshot.Plan.OldNetworkId,
                    false, out transaction.Native, out _) || !Check(transaction)))
            {
                Fail(transaction, "Observer preparation lacks an authorized complete adapter or current old identity.");
                return;
            }
            if (!AdmissionReady(transaction)) return;
            if (!BoltMigrationSession.TryPrepareObserver(transaction.Peer, transaction.Snapshot,
                transaction.Adapter, out transaction.Session, out var result))
            {
                Fail(transaction, result.Detail);
                return;
            }
            transaction.ReadySent = SendReceipt(transaction, transaction.Peer, transaction.Session.ReadyReceipt);
        }

        private static bool AdapterMatches(Transaction transaction) => transaction.Adapter != null
            && transaction.Adapter.ContractId == transaction.Request.RequestedPlan.ContractId
            && transaction.Adapter.ContractVersion == transaction.Request.RequestedPlan.ContractVersion;

        private void OnReceipt(ControlPeer peer, BoltMigrationReceipt receipt)
        {
            if (!Enter()) return;
            try
            {
                if (receipt == null || !Authorized(peer, Capabilities(receipt.Plan)) || !BeginRun(peer)) return;
                var transaction = _active;
                if (transaction == null)
                {
                    // A reliable abort may beat a larger forwarded snapshot. Remember it so that
                    // the late snapshot cannot begin a new observer preparation.
                    if (peer.ServerSession == null && receipt.Kind == BoltMigrationReceiptKind.Aborted)
                        Reserve(peer, receipt.Plan.OldNetworkId, receipt.Plan.Id);
                    return;
                }
                if (transaction.Terminal || !transaction.Request.Matches(receipt.Plan)
                    || FindParticipant(transaction, peer) == null || !Check(transaction)) return;
                if (transaction.Snapshot == null || (transaction.Role == BoltMigrationRole.Destination
                    && transaction.Barrier == null))
                {
                    if (transaction.Role != BoltMigrationRole.Destination
                        || !ReferenceEquals(peer, transaction.Peer)) return;
                    if (transaction.Snapshot != null && !receipt.Matches(transaction.Snapshot))
                    {
                        Fail(transaction, "The pending source receipt conflicts with its snapshot.");
                        return;
                    }
                    if (receipt.Kind == BoltMigrationReceiptKind.Aborted)
                        Fail(transaction, "The source cancelled before snapshot admission.");
                    else if (receipt.Kind == BoltMigrationReceiptKind.Ready)
                    {
                        if (transaction.EarlyReady != null && (transaction.EarlyReady.Plan != receipt.Plan
                            || !transaction.EarlyReady.DigestSpan.SequenceEqual(receipt.DigestSpan)))
                            Fail(transaction, "Conflicting early source readiness.");
                        else transaction.EarlyReady = receipt;
                    }
                    else
                    {
                        transaction.RetirementPossible = true;
                        Fail(transaction, "The source reported a destructive phase before snapshot admission.");
                    }
                    return;
                }
                if (!receipt.Matches(transaction.Snapshot))
                {
                    Fail(transaction, "The receipt conflicts with the exact admitted snapshot.");
                    return;
                }
                if (receipt.Kind == BoltMigrationReceiptKind.Aborted)
                {
                    Fail(transaction, "A migration participant revoked its participation.");
                    return;
                }
                if (transaction.Role == BoltMigrationRole.Destination) ReceiveAtServer(transaction, peer, receipt);
                else ReceiveAtClient(transaction, receipt);
                if (!transaction.Terminal && ReferenceEquals(_active, transaction) && Check(transaction)) Advance(transaction);
            }
            catch (Exception error)
            {
                if (_active != null) Fail(_active, "Receipt dispatch failed (" + error.GetType().Name + ").");
            }
            finally { Exit(); }
        }

        private void ReceiveAtServer(Transaction transaction, ControlPeer peer,
            BoltMigrationReceipt receipt)
        {
            var participant = FindParticipant(transaction, peer);
            switch (receipt.Kind)
            {
                case BoltMigrationReceiptKind.Ready:
                    if (!participant.SnapshotSent || !transaction.Barrier.TryRecord(peer, receipt))
                    { Fail(transaction, "Readiness was outside the frozen snapshot cohort."); return; }
                    participant.Ready = true;
                    break;
                case BoltMigrationReceiptKind.Retired:
                    if (ReferenceEquals(peer, transaction.Peer)) transaction.RetirementPossible = true;
                    if (!ReferenceEquals(peer, transaction.Peer) || !transaction.ReadySent)
                    { Fail(transaction, "Retirement was not authorized for this participant and phase."); return; }
                    transaction.Retired = receipt;
                    break;
                case BoltMigrationReceiptKind.Repaired:
                    if (transaction.Recreated == null || !participant.Ready
                        || receipt.NewNetworkId != transaction.Session.NewNetworkId
                        || !transaction.Barrier.TryRecord(peer, receipt))
                    { Fail(transaction, "Reference repair was outside the authorized replacement phase."); return; }
                    break;
                default:
                    Fail(transaction, "A participant sent a server-only migration authorization.");
                    break;
            }
        }

        private void ReceiveAtClient(Transaction transaction, BoltMigrationReceipt receipt)
        {
            switch (receipt.Kind)
            {
                case BoltMigrationReceiptKind.Ready:
                    if (!transaction.ReadySent)
                    { Fail(transaction, "Aggregate readiness preceded local readiness."); return; }
                    if (transaction.RetirementPossible) return;
                    transaction.RetirementPossible = true;
                    if (transaction.Role == BoltMigrationRole.Observer) return;
                    if (!transaction.Session.TryRetire(transaction.Peer, receipt, null, out var retired))
                    {
                        // A refusal while the exact prepared source is still intact can be cancelled.
                        if (transaction.Session.Status == BoltMigrationStatus.Prepared
                            && transaction.Native.IsCurrent()) transaction.RetirementPossible = false;
                        Fail(transaction, retired.Detail);
                        return;
                    }
                    SendReceipt(transaction, transaction.Peer, retired.Receipt);
                    break;
                case BoltMigrationReceiptKind.Recreated:
                    transaction.RetirementPossible = true;
                    if (!transaction.ReadySent || (transaction.Role == BoltMigrationRole.Source
                        && transaction.Session.Status != BoltMigrationStatus.Retired
                        && !transaction.RepairAttempted))
                    { Fail(transaction, "Replacement authorization preceded source retirement."); return; }
                    if (transaction.Recreated != null && transaction.Recreated.NewNetworkId != receipt.NewNetworkId)
                    { Fail(transaction, "Conflicting replacement identity."); return; }
                    if (transaction.Recreated == null
                        && transaction.Adapter is IBoltMigrationReplacementIdentity identity
                        && (!identity.TryAuthorizeReplacementIdentity(receipt.NewNetworkId, out var identityDetail)
                            || !Check(transaction)))
                    { Fail(transaction, "Replacement identity authorization failed: " + identityDetail); return; }
                    transaction.Recreated = receipt;
                    break;
                case BoltMigrationReceiptKind.Repaired:
                    transaction.RetirementPossible = true;
                    if (!transaction.RepairSent || receipt.NewNetworkId != transaction.Session.NewNetworkId)
                    { Fail(transaction, "Aggregate repair preceded exact local reference repair."); return; }
                    transaction.AuthorizedRepair = receipt;
                    AdvanceParticipantCompletion(transaction);
                    break;
                default:
                    Fail(transaction, "The server sent an invalid client migration phase.");
                    break;
            }
        }

        private void Advance(Transaction transaction)
        {
            if (!Check(transaction)) return;
            if (transaction.Session == null)
            {
                if (transaction.Role == BoltMigrationRole.Source) PrepareSource(transaction);
                else if (transaction.Snapshot != null)
                {
                    if (transaction.Role == BoltMigrationRole.Destination)
                        PrepareDestination(transaction, transaction.Snapshot);
                    else PrepareObserver(transaction);
                }
                if (transaction.Terminal || transaction.Session == null) return;
            }
            if (transaction.Snapshot == null || !Check(transaction)) return;
            if (transaction.Role == BoltMigrationRole.Destination)
            {
                AdvanceServer(transaction);
                return;
            }
            if (transaction.RepairSent)
            {
                AdvanceParticipantCompletion(transaction);
                return;
            }
            if (transaction.Recreated == null || transaction.RepairAttempted || !transaction.Native.IsPlayerHierarchyRetired()) return;
            var replacement = BoltNetwork.FindEntity(new NetworkId(transaction.Recreated.NewNetworkId));
            if (!BoltMigrationNative.MatchesReplacement(transaction.Peer, replacement,
                transaction.Snapshot, false)) return;
            // Control assignment may arrive after entity creation through Bolt's ordinary
            // channel. Retained-rig repair is single-use; wait before consuming that attempt.
            if (transaction.Role == BoltMigrationRole.Source
                && transaction.Snapshot.Plan.PrefabId == BoltPrefabs.PlayerNetwork.Value
                && !replacement.hasControl) return;
            if (transaction.Adapter is IBoltMigrationReplacementIdentity identity
                && (!identity.TryAuthorizeReplacementIdentity(transaction.Recreated.NewNetworkId, out var identityDetail)
                    || !Check(transaction)))
            { Fail(transaction, "Replacement attachment authorization failed: " + identityDetail); return; }
            if (transaction.Adapter is IBoltMigrationReplacementAdmission admission)
            {
                var readiness = admission.ReadReplacementAdmission(replacement, transaction.Role,
                    transaction.Snapshot, out var detail);
                if (!Check(transaction)) return;
                if (readiness == BoltMigrationAdmission.Pending) return;
                if (readiness != BoltMigrationAdmission.Ready)
                { Fail(transaction, "Replica initialization rejected: " + detail); return; }
            }
            if (!Check(transaction)) return;
            transaction.RepairAttempted = true;
            if (!transaction.Session.TryRepairReferences(transaction.Peer, transaction.Recreated, out var repaired))
            { Fail(transaction, repaired.Detail); return; }
            transaction.RepairSent = SendReceipt(transaction, transaction.Peer, transaction.Session.LocalRepairReceipt);
            if (transaction.RepairSent) AdvanceParticipantCompletion(transaction);
        }

        private bool RuntimeEndpointsReady(Transaction transaction)
        {
            if (transaction.Adapter is not PlayerMigrationAdapter player) return true;
            var readiness = player.ReadRuntimePreparation(transaction.Session, transaction.Barrier, out var detail);
            if (!Check(transaction)) return false;
            if (readiness == BoltMigrationAdmission.Pending) return false;
            if (readiness == BoltMigrationAdmission.Ready) return true;
            Fail(transaction, "Held player endpoint preparation rejected: " + detail);
            return false;
        }

        private void AdvanceParticipantCompletion(Transaction transaction)
        {
            if (transaction.Adapter is PlayerMigrationAdapter player && player.ProtectionReleasePending)
                return;
            // Bind the source's baseline endpoint immediately after local repair, before the
            // server waits for that handshake. The aggregate resume receipt remains separate.
            if (!Check(transaction) || !RuntimeEndpointsReady(transaction)
                || transaction.AuthorizedRepair == null) return;
            if (transaction.Session.Status == BoltMigrationStatus.ReferencesRepaired
                && !transaction.Session.TryResumeParticipant(transaction.Peer, transaction.AuthorizedRepair,
                    null, out var resumed))
            { Fail(transaction, resumed.Detail); return; }
            if (transaction.Session.Status != BoltMigrationStatus.Completed || !Check(transaction))
            { Fail(transaction, "The authorized local migration did not complete."); return; }
            if (!ActivatePlayerRuntime(transaction)) return;
            Finish(transaction, transaction.Session.LastResult);
        }

        private bool ActivatePlayerRuntime(Transaction transaction)
        {
            if (transaction.Adapter is not PlayerMigrationAdapter player) return true;
            var readiness = player.ReadRuntimeActivation(transaction.Session, out var detail);
            if (!Check(transaction)) return false;
            if (readiness == BoltMigrationAdmission.Pending) return false;
            if (readiness == BoltMigrationAdmission.Ready) return true;
            Fail(transaction, "Completed player runtime activation failed: " + detail);
            return false;
        }

        private void AdvanceServer(Transaction transaction)
        {
            if (!transaction.ReadySent)
            {
                if (!transaction.Barrier.TryGetReadyReceipt(out var ready)) return;
                // Observers learn that retirement can now occur before the source is authorized.
                foreach (var participant in transaction.Participants)
                    if (!ReferenceEquals(participant.Peer, transaction.Peer)
                        && !SendReceipt(transaction, participant.Peer, ready)) return;
                if (!Check(transaction)) return;
                transaction.RetirementPossible = true;
                var sent = BoltMigrationTransport.SendReceipt(transaction.Peer, ready);
                if (sent != BoltMigrationSendResult.Queued)
                {
                    if (sent == BoltMigrationSendResult.Rejected) transaction.RetirementPossible = false;
                    Fail(transaction, "Source retirement authorization delivery was " + sent + ".");
                    return;
                }
                transaction.ReadySent = true;
            }
            if (transaction.Retired == null) return;
            if (!transaction.RecreateAttempted)
            {
                // Native entity/proxy removal is asynchronous relative to the reliable receipt.
                // Poll read-only evidence, then enter the destructive session method exactly once.
                if (!transaction.Native.IsPlayerHierarchyRetired() || !Check(transaction)) return;
                transaction.RecreateAttempted = true;
                if (!transaction.Session.TryRecreate(transaction.Peer, transaction.Retired,
                    transaction.Barrier, out var created))
                { Fail(transaction, created.Detail); return; }
            }
            if (transaction.Recreated == null)
            {
                if (transaction.Session.Status == BoltMigrationStatus.InitializingReplacement
                    && !transaction.Session.TryFinishRecreation(transaction.Barrier, out var initialized))
                { Fail(transaction, initialized.Detail); return; }
                if (transaction.Session.Status == BoltMigrationStatus.InitializingReplacement) return;
                var recreated = transaction.Session.LastResult;
                if (transaction.Session.Status != BoltMigrationStatus.Recreated || recreated.Receipt == null)
                { Fail(transaction, recreated.Detail); return; }
                if (!transaction.Barrier.RecordLocal(transaction.Session.LocalRepairReceipt) || !Check(transaction))
                { Fail(transaction, "The replacement's local repair could not enter the frozen barrier."); return; }
                transaction.Recreated = recreated.Receipt;
                foreach (var participant in transaction.Participants)
                    if (!SendReceipt(transaction, participant.Peer, transaction.Recreated)) return;
            }
            if (!transaction.Barrier.TryGetRepairReceipt(transaction.Session.NewNetworkId, out var repaired)) return;
            // Server aggregate calls use a null sender and this exact barrier. Client aggregate
            // calls use the exact server peer and no barrier; never interchange these authorities.
            if (!Check(transaction) || !RuntimeEndpointsReady(transaction)) return;
            if (!transaction.Session.TryComplete(null, repaired, transaction.Barrier, out var completed))
            { Fail(transaction, completed.Detail); return; }
            if (!ActivatePlayerRuntime(transaction)) return;
            foreach (var participant in transaction.Participants)
                if (!SendReceipt(transaction, participant.Peer, repaired)) return;
            Finish(transaction, completed);
        }
    }
}
