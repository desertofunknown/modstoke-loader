// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    public sealed partial class StrictCombatClient
    {
        private StrictCombatRebreatherWearResponse _lastRebreatherWearResponse;

        /// <summary>Queues a server-owned wear decision. Does not equip or remove native equipment.</summary>
        public bool TryRequestRebreatherWear(bool isWorn,
            out StrictCombatRebreatherWearRequest request)
        {
            request = default;
            if (!_requiresRebreatherWearReceipts || _projectingHealth || _projectingLife || _placingRespawn
                || _projectingRebreather || _presentingWeapon || _projectingArmour || _projectingAmmunition || _updatingEquipment
                || _respawnStatus is StrictCombatRespawnClientStatus.Requested
                    or StrictCombatRespawnClientStatus.Offered or StrictCombatRespawnClientStatus.Placed
                || !IsCurrent(out long now) || !RebreatherWearNegotiated()
                || !TryReadOwnedRebreather(out _, out _, out var resources)
                || !resources.IsProvisioned || !resources.HasWearState || !_state.TryBeginRebreatherWear(isWorn, resources.Revision, now, _requiresRebreatherProgress)) return false;
            _lastRebreatherWearResponse = default;
            request = _state.PendingRebreatherWearRequest;
            return true;
        }

        /// <summary>
        /// Reads the last correlated wear decision. Its resources describe that decision;
        /// ordinary combat-state delivery supplies current values for native projection.
        /// </summary>
        public bool TryReadRebreatherWearResponse(out StrictCombatRebreatherWearResponse response)
        {
            response = default;
            if (!IsCurrent(out _) || !_lastRebreatherWearResponse.IsValid) return false;
            response = _lastRebreatherWearResponse;
            return true;
        }

        private bool AcceptRebreatherWearResponse(in StrictCombatRebreatherWearResponse response, long now)
        {
            if (response.Request.AllowsTimedProgress != _requiresRebreatherProgress || !RebreatherWearNegotiated()) return false;
            if (!_state.TryAcceptRebreatherWearResponse(in response, now)) return false;
            if (_state.IsRetired) { ReleaseLocalPlayer(); return true; }
            _lastRebreatherWearResponse = response;
            // Fetch one coherent health/armour/resource receipt before another operation.
            // The wear response never overwrites a newer accepted resource snapshot.
            ResetArmourFragments();
            ResetRebreatherFragments();
            ResetBreathingFragments();
            if (!_state.TryBegin(StrictCombatAction.ReadState, now)) { Retire(); return false; }
            _lastObservation = now;
            return true;
        }

        private bool RebreatherWearNegotiated()
        {
            return _requiresRebreatherWearReceipts && _peer.IsCurrent && _peer.ServerSession == null
                && ClientEnrollment.TryRead(_peer, out var enrollment) == EnrollmentReadStatus.Success
                && ReferenceEquals(enrollment.Peer, _peer) && enrollment.State == EnrollmentState.Enrolled
                && HasCapabilities(enrollment, StrictCombatRebreatherWearProtocol.RequiredCapabilities);
        }

        // For an enrollment the caller has already matched to this peer.
        private bool RebreatherWearNegotiated(EnrollmentSnapshot enrollment)
        {
            return _requiresRebreatherWearReceipts && _peer.IsCurrent
                && HasCapabilities(enrollment, StrictCombatRebreatherWearProtocol.RequiredCapabilities);
        }
    }
}
