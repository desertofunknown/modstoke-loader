// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;
using NativeMode = Modstoke.Sons.PlayerClientMovement.NativeMode;
using NativeModeKey = Modstoke.Sons.PlayerClientMovement.NativeModeKey;
using Proposal = Modstoke.Sons.PlayerModeTransitionProposal;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        /// <summary>
        /// Complete an authenticated mode request for an already settled native remote driver.
        /// Resolves the physical cart from the received request, never from a supplied pose.
        /// This does not create a ghost, reserve an empty seat or replay native player entry.
        /// A non-null failed lease is quarantined; retain it for lifecycle recovery.
        /// </summary>
        public static bool TryAcquireReceivedServerCartDriverAuthority(PlayerBridge bridge,
            out CartDriverAuthority authority, out string detail)
        {
            authority = null;
            detail = "The selected vehicle thread and an exact received driver request are required.";
            if (!Enter()) return false;
            try
            {
                RequireContext();
                var proposal = ReadReceivedDriverEntry(bridge);
                var entity = RequireCartEndpoint(proposal.Requested.AnchorNetworkId);
                var binding = new Binding(entity);
                Require(binding.Cart != null && entity.isOwner && !entity.isFrozen && binding.Cart.enabled
                    && !Time.inFixedTimeStep && !CartDriverAuthorityHooks.IsAcquired(binding.Cart)
                    && !CartWriterHooks.IsHeld(binding.Cart) && OccupantsFor(binding.Cart) == null,
                    "Received entry requires the exact active server cart after its migration gates release.");
                CartWriterHooks.RequireInitialized(binding.Cart);
                RequireSettledServerCart(binding);
                Require(bridge.TryReadCurrentTarget(out var driver), "The authenticated driver attachment is unavailable.");
                var candidate = new CartDriverAuthority(binding, driver, bridge.Peer);
                candidate.PrepareReceivedInput(bridge);
                CartDriverAuthorityHooks.Add(candidate);
                authority = candidate;
                candidate.Start();
                candidate.CommitPreparedInput();
                detail = "Received driver proposal approved against its exact settled world seat; native input and acknowledgements bound.";
                return true;
            }
            catch (Exception error)
            {
                authority?.Quarantine(error.Message);
                detail = Failure("Received driver acquisition rejected or unconfirmed", error);
                return false;
            }
            finally { _busy = false; }
        }

        // The lifecycle provider calls this while its vehicle Enter() is already active.
        // Candidate capture, input preparation and activation never change the outer gate.
        internal static bool TryPrepareServerCartDriverInput(CartDriverAuthority authority,
            PlayerBridge bridge, out string detail)
        {
            try
            {
                Require(_busy && OnSelectedThread() && !_reentered && !Time.inFixedTimeStep && authority != null,
                    "Driver input preparation requires the enclosing vehicle operation.");
                authority.RequirePrepared();
                authority.PrepareReceivedInput(bridge);
                detail = "Exact received driver request retained for synchronous prepared motor activation.";
                return true;
            }
            catch (Exception error) { detail = Failure("Prepared driver input rejected", error); return false; }
        }

        internal static bool TryValidatePreparedServerCartDriverAuthority(CartDriverAuthority authority, out string detail)
        {
            try
            {
                Require(_busy && OnSelectedThread() && !_reentered && authority != null && !Time.inFixedTimeStep,
                    "Prepared driver validation requires the enclosing vehicle operation.");
                authority.RequirePrepared();
                authority.RequirePreparedInput();
                detail = "Prepared driver motor and received input route still name the exact retained seat.";
                return true;
            }
            catch (Exception error) { detail = Failure("Prepared driver validation rejected", error); return false; }
        }

        internal static bool TryPrepareMigratedServerCartDriverInput(CartDriverAuthority authority,
            PlayerBridge bridge, BoltMigrationPlan cartPlan,
            NativeModeKey expected, NativeModeKey replacement, out string detail)
        {
            try
            {
                Require(_busy && OnSelectedThread() && !_reentered && !Time.inFixedTimeStep && authority != null,
                    "Migrated driver preparation requires the enclosing vehicle operation.");
                authority.RequirePrepared();
                Require(cartPlan.IsValid && cartPlan.OldNetworkId == expected.AnchorNetworkId
                    && expected.Mode == NativeMode.GolfCartDriver && expected.SeatIndex == 0
                    && expected.TransitionSequence < uint.MaxValue && replacement.Mode == expected.Mode
                    && replacement.SeatIndex == expected.SeatIndex
                    && replacement.TransitionSequence == expected.TransitionSequence + 1
                    && replacement.AnchorNetworkId != expected.AnchorNetworkId
                    && bridge.IsCommittedMovementMode(expected) && bridge.CartMigrationBoundaryDrained,
                    "A drained existing driver key and exact cart replacement plan are required.");
                authority.RequireInputBridge(bridge, replacement);
                Require(authority._preparedBridge == null, "Prepared driver input cannot change its transaction.");
                authority._preparedBridge = bridge;
                authority._migrationExpected = expected; authority._migrationReplacement = replacement;
                authority._migrationInput = true;
                detail = "Existing migrated driver route prepared; cart session must authorize the same-mode key change.";
                return true;
            }
            catch (Exception error) { detail = Failure("Migrated driver preparation rejected", error); return false; }
        }

        private static Proposal ReadReceivedDriverEntry(PlayerBridge bridge)
        {
            Require(BoltNetwork.isServer && bridge != null && bridge.IsServer && bridge.IsGameplayActive
                && bridge.Peer?.ServerSession != null && bridge.Peer.IsCurrent,
                "Only the current authenticated server player bridge may bind driver input.");
            Require(bridge.TryPeekMovementTransitionProposal(out var proposal)
                && proposal.Expected.Mode == NativeMode.Ground && proposal.Requested.Mode == NativeMode.GolfCartDriver
                && proposal.Requested.SeatIndex == 0 && proposal.Requested.AnchorNetworkId != 0
                && proposal.MigrationId == bridge.Plan.Id && proposal.Generation == bridge.Plan.Generation
                && proposal.NetworkId == bridge.NetworkId && bridge.IsCommittedMovementMode(proposal.Expected),
                "Wait for the exact ordered ground-to-driver request on this bridge.");
            return proposal;
        }

        public sealed partial class CartDriverAuthority
        {
            internal PlayerBridge _preparedBridge;
            private Proposal _entryProposal, _exitProposal;
            internal bool _migrationInput;
            internal NativeModeKey _migrationExpected, _migrationReplacement;

            internal bool IsBoundTo(PlayerBridge bridge, NativeModeKey key)
                => _started && !_released && _cartMigrationYield == null && FailureDetail == null && ReferenceEquals(_bridge, bridge)
                    && _mode == key && bridge.IsCommittedMovementMode(key);

            internal void RequireInputBridge(PlayerBridge bridge, NativeModeKey key)
            {
                Require(bridge != null && bridge.IsServer && bridge.IsGameplayActive
                    && ReferenceEquals(bridge.Peer, _peer) && bridge.NetworkId == _driverId
                    && key.Mode == NativeMode.GolfCartDriver && key.SeatIndex == 0
                    && key.AnchorNetworkId == CartNetworkId && key.TransitionSequence != 0
                    && bridge.TryReadCurrentTarget(out var driver) && driver.Pointer == _driver.Pointer,
                    "Driver input must name the exact authenticated player and physical cart seat, not its ghost.");
                Require(ServerWorld.TryValidatePlayerBinding(_playerBinding, out _),
                    "The authenticated player binding changed before input activation.");
            }

            internal void PrepareReceivedInput(PlayerBridge bridge)
            {
                Require(!_started && !_released && _bridge == null && FailureDetail == null,
                    "Input preparation must precede motor activation.");
                var proposal = ReadReceivedDriverEntry(bridge);
                RequireInputBridge(bridge, proposal.Requested);
                RequireIdentity();
                Require(_preparedBridge == null || (ReferenceEquals(_preparedBridge, bridge) && _entryProposal == proposal),
                    "A prepared driver lease cannot change its received proposal.");
                // Actual native seat caches, physical slot -> ghost -> player and exact rig
                // identity were checked above. A nearby free seat is not an occupied seat.
                _preparedBridge = bridge; _entryProposal = proposal;
            }

            internal void RequirePreparedInput()
            {
                if (_migrationInput)
                {
                    Require(_preparedBridge != null && _preparedBridge.CartMigrationBoundaryDrained
                        && (_preparedBridge.IsCommittedMovementMode(_migrationExpected)
                            || _preparedBridge.IsCommittedMovementMode(_migrationReplacement)),
                        "The prepared migrated driver no longer has its drained old or authorized replacement key.");
                    RequireInputBridge(_preparedBridge, _migrationReplacement);
                    return;
                }
                Require(_preparedBridge != null && _entryProposal == ReadReceivedDriverEntry(_preparedBridge),
                    "The prepared driver proposal is no longer pending.");
                RequireInputBridge(_preparedBridge, _entryProposal.Requested);
            }

            internal void CommitPreparedInput()
            {
                Require(_preparedBridge != null && _started && !_released && FailureDetail == null && _bridge == null,
                    "Prepared driver motor must activate before approving its input route.");
                RequirePreparedInput();
                RequireIdentity();
                if (_migrationInput)
                    Require(_preparedBridge.IsCommittedMovementMode(_migrationReplacement),
                        "The exact cart session must commit its replacement key before motor activation.");
                else Require(_preparedBridge.TryApproveMovementTransition(_entryProposal, out _, out var detail), detail);
                _bridge = _preparedBridge; _mode = _migrationInput ? _migrationReplacement : _entryProposal.Requested;
                _preparedBridge = null;
                Require(_bridge.IsCommittedMovementMode(_mode), "Approved driver key did not commit.");
                _hasInput = false;
                ApplyInput();
            }

            private void RequireReceivedExit()
            {
                Require(_bridge.TryPeekMovementTransitionProposal(out var proposal)
                    && proposal.Expected == _mode && _mode.TransitionSequence < uint.MaxValue
                    && proposal.Requested == NativeModeKey.Ground(_mode.TransitionSequence + 1)
                    && proposal.MigrationId == _bridge.Plan.Id && proposal.Generation == _bridge.Plan.Generation
                    && proposal.NetworkId == _driverId,
                    "Native driver exit requires the exact received ordered driver-to-ground proposal.");
                _exitProposal = proposal;
            }

            private void RequireExitProposalCurrent()
            {
                if (_bridge == null) return;
                Require(_exitRequested && _exitProposal.RequestSequence != 0
                    && _bridge.TryPeekMovementTransitionProposal(out var proposal) && proposal == _exitProposal
                    && _bridge.IsCommittedMovementMode(_mode),
                    "The exact received exit must remain pending through native continuation completion.");
            }

            private void CommitReceivedExit()
            {
                if (_bridge == null) return;
                RequireExitProposalCurrent();
                Require(_bridge.TryApproveMovementTransition(_exitProposal, out _, out var detail), detail);
            }
        }
    }
}
