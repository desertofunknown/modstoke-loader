// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    public sealed partial class StrictCombatClient
    {
        private StrictCombatRebreatherRefillResponse _lastRebreatherResponse;

        /// <summary>Queues one refill start against the accepted server reserve. Does not refill native equipment.</summary>
        public bool TryRequestRebreatherRefill(StrictCombatRebreatherRefillMode mode,
            out StrictCombatRebreatherRefillRequest request)
        {
            request = default;
            if (!_requiresRebreatherReceipts || _projectingHealth || _projectingLife || _placingRespawn
                || _presentingWeapon || _projectingArmour || _projectingAmmunition || _updatingEquipment
                || _respawnStatus is StrictCombatRespawnClientStatus.Requested
                    or StrictCombatRespawnClientStatus.Offered or StrictCombatRespawnClientStatus.Placed
                || !IsCurrent(out long now) || !RebreatherRefillNegotiated()
                || !TryReadOwnedRebreather(out _, out _, out var resources)
                || !resources.IsProvisioned || !_state.TryBeginRebreather(mode, resources.Revision, now, _requiresRebreatherProgress)) return false;
            _lastRebreatherResponse = default;
            request = _state.PendingRebreatherRequest;
            return true;
        }

        /// <summary>
        /// Reads the last correlated refill decision. Its resources describe that decision;
        /// ordinary combat-state delivery supplies current values for native projection.
        /// </summary>
        public bool TryReadRebreatherRefillResponse(out StrictCombatRebreatherRefillResponse response)
        {
            response = default;
            if (!IsCurrent(out _) || !_lastRebreatherResponse.IsValid) return false;
            response = _lastRebreatherResponse;
            return true;
        }

        private bool AcceptRebreatherRefillResponse(in StrictCombatRebreatherRefillResponse response, long now)
        {
            if (response.Request.AllowsTimedProgress != _requiresRebreatherProgress || !RebreatherRefillNegotiated()) return false;
            if (!_state.TryAcceptRebreatherResponse(in response, now)) return false;
            if (_state.IsRetired) { ReleaseLocalPlayer(); return true; }
            _lastRebreatherResponse = response;
            // Fetch one coherent health/armour/resource receipt before another operation.
            // The refill response never overwrites a newer accepted resource snapshot.
            ResetArmourFragments();
            ResetRebreatherFragments();
            ResetBreathingFragments();
            if (!_state.TryBegin(StrictCombatAction.ReadState, now)) { Retire(); return false; }
            _lastObservation = now;
            return true;
        }

        private bool RebreatherRefillNegotiated()
        {
            return _requiresRebreatherReceipts && _peer.IsCurrent
                && ClientEnrollment.TryRead(_peer, out var enrollment) == EnrollmentReadStatus.Success
                && ReferenceEquals(enrollment.Peer, _peer) && enrollment.State == EnrollmentState.Enrolled
                && HasCapabilities(enrollment, StrictCombatRebreatherRefillProtocol.RequiredCapabilities);
        }

        // For an enrollment the caller has already matched to this peer.
        private bool RebreatherRefillNegotiated(EnrollmentSnapshot enrollment)
        {
            return _requiresRebreatherReceipts && _peer.IsCurrent
                && HasCapabilities(enrollment, StrictCombatRebreatherRefillProtocol.RequiredCapabilities);
        }
    }
}
