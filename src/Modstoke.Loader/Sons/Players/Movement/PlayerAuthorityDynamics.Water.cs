// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Physics;

namespace Modstoke.Sons
{
    public sealed partial class PlayerAuthorityDynamics
    {
        internal bool TryBindSwimmingTransitions(PlayerBridge bridge,
            PlayerLifeAuthority life, PlayerLifeRoute route,
            PlayerWaterPresentation publisher, out string detail)
        {
            detail = "Bind the prepared dynamics' exact swimming motor after life and water preparation.";
            if (!ReferenceEquals(bridge, _bridge) || _disposed || FailureReason != null || _movement == null) return false;
            RequireCurrent();
            return _movement.TryBindSwimmingTransitions(bridge, life, route, publisher, out detail);
        }

        internal bool HasPendingSwimmingEntry => _movement?.HasPendingSwimmingEntry == true;

        internal bool TryReadSurvivalEnvironmentWater(PlayerBridge bridge,
            PlayerCarrier carrier,
            PlayerWaterContactAuthority.ServerContinuation waterContacts,
            out bool underwater, out bool swimming, out string detail)
        {
            underwater = swimming = false;
            detail = "The environment reader must retain this actor's exact dynamics, motor and native water owner.";
            if (!OwnsCarrier(carrier, bridge) || FailureReason != null || _movement == null) return false;
            try
            {
                RequireCurrent();
                return _movement.TryReadSurvivalEnvironmentWater(bridge, carrier, waterContacts,
                    out underwater, out swimming, out detail);
            }
            catch (Exception error)
            { detail = "Movement water state is unavailable (" + error.GetType().Name + ")."; return false; }
        }

        /// <summary>
        /// Uses the exact restored sensor and its native configuration, including
        /// IgnoreInCave. No legacy point-query conversion or active sensor read occurs
        /// during preparation. Aggregate activation and the first water observation remain separate.
        /// </summary>
        public static bool TryPrepareFromRestoredSnapshot(PlayerBridge bridge,
            PlayerCarrier carrier,
            PlayerWaterContactAuthority.ServerContinuation waterContacts,
            Func<PlayerAuthorityActionState> readActions,
            Func<PlayerCarrierProfile, PlayerAuthorityDynamicsProfile, bool> approveProfile,
            out PlayerAuthorityDynamics dynamics, out PlayerGroundMovement movement,
            out string detail, StepHelper stepHelper = null, Func<bool> stepHelperAllowed = null)
        {
            dynamics = null; movement = null;
            detail = "The held carrier's restored survival baseline and exact native water owner are required.";
            if (carrier == null || !carrier.IsSurvivalRestored || waterContacts == null
                || !ReferenceEquals(waterContacts.Carrier, carrier) || waterContacts.IsActive
                || waterContacts.FailureReason != null || !carrier.TryReadServerStats(out var stats)
                || !carrier.RestoredSurvival.TryCreateDynamicsProfile(carrier.Profile, out var profile, out detail))
                return false;
            if (!TryCreate(bridge, carrier, stats, readActions, profile, approveProfile, out var owner, out detail))
                return false;
            if (!owner.TryPrepareMovement(waterContacts, out movement, out detail, stepHelper, stepHelperAllowed))
            { owner.Dispose(); return false; }
            dynamics = owner;
            return true;
        }

        public bool TryPrepareMovement(PlayerWaterContactAuthority.ServerContinuation waterContacts,
            out PlayerGroundMovement movement, out string detail,
            StepHelper stepHelper = null, Func<bool> stepHelperAllowed = null)
        {
            movement = null;
            detail = "An approved held carrier and its exact native water continuation are required.";
            try
            {
                RequireCurrent();
                if (_movement != null || _carrier.IsPhysicsActive || waterContacts == null
                    || !ReferenceEquals(waterContacts.Carrier, _carrier)
                    || (stepHelper == null) != (stepHelperAllowed == null)) return false;
                if (!PlayerGroundMovement.TryPrepare(_bridge, _carrier, waterContacts,
                    SupportedModesAllowed, ReadGroundDynamics, ReadSwimDynamics, _stats,
                    StaminaProfile, ReadStaminaConditions, out movement, out detail, stepHelper, stepHelperAllowed))
                { FailureReason = detail; Dispose(); return false; }
                _movement = movement;
                detail = "Actor-local motor and native water binding prepared with physics and timers held.";
                return true;
            }
            catch (Exception error) { movement?.Dispose(); detail = Fail(error); return false; }
        }
    }
}
