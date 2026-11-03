// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;
using NativeModeKey = Modstoke.Sons.PlayerClientMovement.NativeModeKey;
using TransitionProposal = Modstoke.Sons.PlayerModeTransitionProposal;
using TransitionApproval = Modstoke.Sons.PlayerModeTransitionApproval;
using TransitionDecision = Modstoke.Sons.PlayerModeTransitionDecision;
using TransitionKind = Modstoke.Sons.PlayerBridgeProtocol.ModeTransitionPacketKind;

namespace Modstoke.Sons
{
    public sealed partial class PlayerBridge
    {
        private static readonly long ModeTransitionRetryInterval = Stopwatch.Frequency / 4;
        private TransitionProposal _modeProposal;
        private TransitionApproval _modeApproval, _modeCompletedApproval;
        private uint _modeCompletedRequest;
        private long _modeNextSend;
        private bool _modeClientApplied, _modeReceiptPending;

        /// <summary>Cached unfinished transaction; local mode selection alone does not settle it.</summary>
        public bool HasPendingMovementTransition => !_disposed && _modeProposal.RequestSequence != 0;

        public bool TryGetCommittedMovementMode(out NativeModeKey key)
        {
            key = default;
            if (!CanAccessModeTransitions() || !ValidateCurrent()) return false;
            key = _movementModeKey;
            return true;
        }

        /// <summary>
        /// Retain one client request until its decision and application acknowledgement settle.
        /// False is pre-admission backpressure. This never starts a native action or grants authority.
        /// </summary>
        public bool TryProposeMovementTransition(NativeModeKey expected, NativeModeKey requested,
            out TransitionProposal proposal, out string detail)
        {
            proposal = default;
            detail = "An active client bridge with no unsettled movement transition is required.";
            if (!CanAccessModeTransitions() || _server) return false;
            _busy = true;
            try
            {
                if (!ValidateCurrent() || HasPendingMovementTransition || _modeCompletedRequest == uint.MaxValue)
                    return false;
                var candidate = new TransitionProposal(Plan.Id, Plan.Generation, NetworkId,
                    _modeCompletedRequest + 1, expected, requested);
                if (expected != _movementModeKey || !PlayerBridgeProtocol.IsValidMovementTransition(candidate))
                {
                    detail = "Use the exact current key and next transition ordinal, with an explicit ground enter/exit boundary.";
                    return false;
                }
                _modeProposal = candidate;
                _modeApproval = default;
                _modeClientApplied = false;
                _modeNextSend = 0;
                proposal = candidate;
                detail = "Proposal retained; native action and authority remain unchanged until server approval.";
                return true;
            }
            finally { _busy = false; }
        }

        /// <summary>Observe, without consuming, the next authenticated request awaiting server policy.</summary>
        public bool TryPeekMovementTransitionProposal(out TransitionProposal proposal)
        {
            proposal = default;
            if (!CanAccessModeTransitions() || !_server || !ValidateCurrent()
                || !HasPendingMovementTransition || _modeApproval.Decision != TransitionDecision.None) return false;
            proposal = _modeProposal;
            return true;
        }

        /// <summary>
        /// The server lifecycle owner calls this only after validating the exact actor, live anchor,
        /// seat occupancy/reach and native ownership handoff. Finish the selected physics step first.
        /// A successful decision is immutable and retained until the client acknowledges application.
        /// </summary>
        public bool TryApproveMovementTransition(TransitionProposal proposal,
            out TransitionApproval approval, out string detail)
        {
            approval = default;
            detail = "The exact pending server proposal and completed native authority handoff are required.";
            if (!CanAccessModeTransitions() || !_server || UnityEngine.Time.inFixedTimeStep) return false;
            _busy = true;
            try
            {
                if (!ValidateCurrent() || !HasPendingMovementTransition || proposal != _modeProposal
                    || _modeApproval.Decision != TransitionDecision.None) return false;
                if (!TrySelectCommittedMovementMode(proposal.Expected, proposal.Requested, out detail)) return false;
                _modeApproval = approval = new(proposal, TransitionDecision.Approved);
                _modeNextSend = 0;
                detail = "Server mode committed; exact approval retained until client application acknowledgement.";
                return true;
            }
            finally { _busy = false; }
        }

        /// <summary>Reject without changing mode. The rejection is ordered and acknowledged like approval.</summary>
        public bool TryRejectMovementTransition(TransitionProposal proposal, out string detail)
        {
            detail = "Only the exact undecided server proposal can be rejected.";
            if (!CanAccessModeTransitions() || !_server) return false;
            _busy = true;
            try
            {
                if (!ValidateCurrent() || !HasPendingMovementTransition || proposal != _modeProposal
                    || _modeApproval.Decision != TransitionDecision.None || proposal.Expected != _movementModeKey) return false;
                _modeApproval = new(proposal, TransitionDecision.Rejected);
                _modeNextSend = 0;
                detail = "Rejection retained; committed movement mode is unchanged.";
                return true;
            }
            finally { _busy = false; }
        }

        /// <summary>Observe the exact server decision until the client explicitly acknowledges application.</summary>
        public bool TryPeekMovementTransitionApproval(out TransitionApproval approval)
        {
            approval = default;
            if (!CanAccessModeTransitions() || _server || !ValidateCurrent() || _modeClientApplied
                || !HasPendingMovementTransition || _modeApproval.Decision == TransitionDecision.None) return false;
            approval = _modeApproval;
            return true;
        }

        /// <summary>
        /// Call after the approved native action completed on the retained rig, or to acknowledge a
        /// rejection without running it. Success selects the local key and retains the ack for retry.
        /// On failure retain native completion state; do not run an already completed action twice.
        /// Bind the native movement sampler after success. This method never invokes a native action.
        /// </summary>
        public bool TryCommitMovementTransition(TransitionApproval approval, out string detail)
        {
            detail = "The exact received server decision and completed native action are required.";
            if (!CanAccessModeTransitions() || _server || UnityEngine.Time.inFixedTimeStep) return false;
            _busy = true;
            try
            {
                if (!ValidateCurrent() || !HasPendingMovementTransition || _modeClientApplied
                    || approval != _modeApproval || approval.Proposal != _modeProposal
                    || approval.Decision == TransitionDecision.None) return false;
                if (approval.Decision == TransitionDecision.Approved)
                {
                    if (!TrySelectCommittedMovementMode(approval.Proposal.Expected, approval.Proposal.Requested, out detail))
                        return false;
                }
                else if (approval.Decision != TransitionDecision.Rejected || _movementModeKey != approval.Proposal.Expected)
                    return false;
                _modeClientApplied = true;
                _modeNextSend = 0;
                detail = "Decision applied once; acknowledgement retained until the server confirms receipt.";
                return true;
            }
            finally { _busy = false; }
        }

        private bool CanAccessModeTransitions()
            => Environment.CurrentManagedThreadId == _thread && !_disposed && !_busy && IsGameplayActive
                && _cartRebindSession == null;

        // Called only by Bridge.Receive after its exact peer reference lookup. Continuous-input
        // credits, sequence counters and coalescing are intentionally not shared with this stream.
        private bool TryReceiveModeTransition(ReadOnlySpan<byte> bytes)
        {
            if (!PlayerBridgeProtocol.MatchesModeTransitionEnvelope(bytes)) return false;
            if (!CanAccessModeTransitions()) return true;
            _busy = true;
            try
            {
                // Reject malformed or misaddressed frames before the costly live binding check.
                if (!PlayerBridgeProtocol.TryReadModeTransition(bytes, out var kind,
                        out var proposal, out var decision)
                    || !Matches(proposal.MigrationId, proposal.Generation, proposal.NetworkId)
                    || !ValidateCurrent()) return true;
                if (_server) ReceiveServerModeTransition(kind, proposal, decision);
                else ReceiveClientModeTransition(kind, proposal, decision);
            }
            catch (Exception error) { Retire(error); }
            finally { _busy = false; }
            return true;
        }

        private void ReceiveServerModeTransition(TransitionKind kind, TransitionProposal proposal, TransitionDecision decision)
        {
            if (kind == TransitionKind.Proposal)
            {
                // A duplicate cannot replace a retained request, reset its retry clock or rerun policy.
                if (HasPendingMovementTransition || _modeCompletedRequest == uint.MaxValue
                    || proposal.RequestSequence != _modeCompletedRequest + 1 || proposal.Expected != _movementModeKey) return;
                _modeProposal = proposal;
                _modeApproval = default;
                _modeReceiptPending = false;
                _modeNextSend = 0;
            }
            else if (kind == TransitionKind.Ack)
            {
                var acknowledged = new TransitionApproval(proposal, decision);
                if (HasPendingMovementTransition)
                {
                    if (_modeApproval.Decision == TransitionDecision.None || acknowledged != _modeApproval) return;
                    _modeCompletedRequest = proposal.RequestSequence;
                    _modeCompletedApproval = _modeApproval;
                    _modeProposal = default;
                    _modeApproval = default;
                    _modeReceiptPending = true;
                    _modeNextSend = 0;
                }
                else if (acknowledged == _modeCompletedApproval)
                {
                    // Lost receipts are recovered by the client's retained ack, without replaying
                    // either native action or advancing request/movement ordinals a second time.
                    _modeReceiptPending = true;
                }
            }
        }

        private void ReceiveClientModeTransition(TransitionKind kind, TransitionProposal proposal, TransitionDecision decision)
        {
            if (!HasPendingMovementTransition || proposal != _modeProposal) return;
            var approval = new TransitionApproval(proposal, decision);
            if (kind == TransitionKind.Approval)
            {
                if (_modeApproval.Decision != TransitionDecision.None) return;
                _modeApproval = approval;
            }
            else if (kind == TransitionKind.Receipt && _modeClientApplied && approval == _modeApproval)
            {
                _modeCompletedRequest = proposal.RequestSequence;
                _modeCompletedApproval = approval;
                _modeProposal = default;
                _modeApproval = default;
                _modeClientApplied = false;
                _modeNextSend = 0;
            }
        }

        // Called once per ordinary bridge tick, after ValidateCurrent. No timeout abandons an accepted
        // proposal or decision. Connection retirement is the only local teardown boundary.
        private void TickModeTransitions()
        {
            if (!CanAccessModeTransitions()) return;
            long now = Stopwatch.GetTimestamp();
            if (now < _modeNextSend) return;
            _busy = true;
            try
            {
                TransitionKind kind;
                TransitionProposal proposal;
                TransitionDecision decision;
                if (_server)
                {
                    if (_modeReceiptPending)
                    {
                        kind = TransitionKind.Receipt;
                        proposal = _modeCompletedApproval.Proposal;
                        decision = _modeCompletedApproval.Decision;
                    }
                    else if (HasPendingMovementTransition && _modeApproval.Decision != TransitionDecision.None)
                    {
                        kind = TransitionKind.Approval;
                        proposal = _modeProposal;
                        decision = _modeApproval.Decision;
                    }
                    else return;
                }
                else
                {
                    if (!HasPendingMovementTransition) return;
                    if (_modeClientApplied) kind = TransitionKind.Ack;
                    else if (_modeApproval.Decision == TransitionDecision.None) kind = TransitionKind.Proposal;
                    else return;
                    proposal = _modeProposal;
                    decision = kind == TransitionKind.Proposal ? TransitionDecision.None : _modeApproval.Decision;
                }

                Span<byte> bytes = stackalloc byte[PlayerBridgeProtocol.ModeTransitionLength];
                if (!PlayerBridgeProtocol.TryWriteModeTransition(kind, proposal, decision, bytes, out var written))
                {
                    Dispose();
                    return;
                }
                _modeNextSend = now + ModeTransitionRetryInterval;
                var sent = Send(bytes.Slice(0, written));
                if (!_disposed && kind == TransitionKind.Receipt && sent == DatagramSendResult.Queued)
                    _modeReceiptPending = false;
            }
            catch (Exception error) { Retire(error); }
            finally { _busy = false; }
        }

        private void DisposeModeTransitions()
        {
            _modeProposal = default;
            _modeApproval = _modeCompletedApproval = default;
            _modeCompletedRequest = 0;
            _modeNextSend = 0;
            _modeClientApplied = _modeReceiptPending = false;
        }
    }
}
