// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons
{
    public sealed partial class PlayerMigrationAdapter
    {
        private PlayerAuthorityDynamics _serverDynamics;
        private PlayerGroundMovement _serverMovement;
        private PlayerSurvivalEnvironmentClimate _serverClimate;
        private PlayerSurvivalEnvironmentReader _serverEnvironment;
        private PlayerSurvivalEnvironmentTemperature _serverTemperature;
        private PlayerSurvivalRuntime _serverSurvival;
        private PlayerLifeAuthority _serverLife;
        private bool _serverMovementActivationAttempted;

        private bool TryActivateServerMovement(out string detail)
        {
            detail = "The exact prepared server movement, survival and native water owners are required.";
            if (_serverMovementActivationAttempted || !ServerMovementLifetimeCurrent()
                || !_bridge.IsGameplayActive || _serverMovement?.IsPrepared != true
                || _serverWater == null || _serverWater.IsActive || _serverLife == null
                || _serverSurvival == null || _serverSurvival.IsActive
                || _serverConsumption == null || UnityEngine.Time.inFixedTimeStep) return false;
            _serverMovementActivationAttempted = true;
            // No game-loop boundary may intervene between releasing the motor and
            // activating its exact sensor. The survival clock owns subsequent stat ticks.
            if (!_serverSurvival.TryActivate(_serverConsumption, out detail)
                || !_serverMovement.TryActivate(out detail)
                || !_serverWater.TryActivate(out detail)
                || !_serverMovement.TryActivateWaterObservation(out detail))
            {
                RetireServerMovement();
                return false;
            }
            return true;
        }

        private bool TryPrepareServerMovement(out string detail)
        {
            detail = "Server movement requires this adapter's restored actor and approved snapshot.";
            if (Role != BoltMigrationRole.Destination || _serverDynamics != null
                || _serverMovement != null || !ServerMovementLifetimeCurrent()) return false;
            if (!PlayerAuthorityDynamics.TryPrepareFromRestoredSnapshot(_bridge, _carrier,
                    _serverWater, ReadServerMovementActions, ApproveRestoredDynamics,
                    out _serverDynamics, out _serverMovement, out detail)) return false;
            var support = global::Modstoke.Loader.HarmonySupport.CreateState();
            if (!PlayerSurvivalEnvironmentClimate.TryPrepare(out _serverClimate, out detail)
                || !PlayerSurvivalEnvironmentTemperature.TryPrepare(_bridge, _carrier,
                    support, out _serverTemperature, out detail)
                || !PlayerSurvivalEnvironmentExposureServer.TryPrepare(_bridge, _carrier,
                    _serverClimate, support, out _serverExposure, out detail)
                || !PlayerSurvivalEnvironmentReader.TryPrepare(_bridge, _carrier, _serverDynamics,
                    _serverClimate, _serverTemperature, _serverExposure, _serverWater,
                    _serverInventory.MutationEnforcement.HeldOnlyAuthority,
                    out _serverEnvironment, out detail)
                || !PlayerSurvivalRuntime.TryPrepare(_bridge, _carrier, _serverDynamics,
                    _serverInventory, ApproveRestoredSurvivalSettings, _serverEnvironment.Read,
                    ServerExternalMutationPending, out _serverSurvival, out detail)
                || !PlayerLifeAuthority.TryPrepare(_bridge, _carrier, _serverSurvival,
                    _snapshot.Survival.LifeAdmission, out _serverLife, out detail))
            {
                RetireServerMovement();
                return false;
            }
            if (ServerMovementLifetimeCurrent()) return true;
            RetireServerMovement();
            detail = "The player lifetime changed during server movement preparation.";
            return false;
        }

        private bool ApproveRestoredDynamics(PlayerCarrierProfile carrier,
            PlayerAuthorityDynamicsProfile dynamics)
            => ServerMovementLifetimeCurrent() && ReferenceEquals(carrier, _snapshot.CarrierProfile)
                && _snapshot.Survival.TryCreateDynamicsProfile(carrier, out var approved, out _)
                && dynamics == approved;

        private bool ApproveRestoredSurvivalSettings(PlayerSurvivalSnapshot survival,
            PlayerSurvivalSettings settings)
            => ServerMovementLifetimeCurrent() && ReferenceEquals(survival, _snapshot.Survival)
                && ReferenceEquals(settings, survival.Settings) && ValidReason(_survivalApprovalReason);

        private bool ServerExternalMutationPending()
            => !ServerMovementLifetimeCurrent() || _serverHeldOnly == null
                || _serverHeldOnly.IsDisposed || _serverHeldOnly.FailureReason != null
                || _serverHeldOnly.HasPendingCustody
                || _serverInventory.HasPendingArmourCustody
                || _serverDynamics?.HasPendingSwimmingEntry == true || _waterPresentation?.HasPendingSwimmingTransition == true
                || _serverInventory.MutationEnforcement.HasPendingHeldOnlyCustody;

        private bool ServerMovementLifetimeCurrent()
            => AnimationLifetimeCurrent() && Role == BoltMigrationRole.Destination
                && _carrier != null && !_carrier.IsDisposed && _carrier.IsSurvivalRestored
                && ReferenceEquals(_carrier.Profile, _snapshot?.CarrierProfile)
                && ReferenceEquals(_carrier.RestoredSurvival, _snapshot?.Survival)
                && ValidReason(_physicsApprovalReason) && ValidReason(_survivalApprovalReason)
                && _bridge != null && !_bridge.IsDisposed && _serverInventory != null
                && !_serverInventory.IsDisposed && !_serverInventory.IsFaulted;

        private PlayerAuthorityActionState ReadServerMovementActions()
        {
            if (!ServerMovementLifetimeCurrent()
                || !PlayerAuthorityDynamics.TryReadCarrierActions(_carrier, out var actions, out _))
                throw new InvalidOperationException("The migrated player's committed action state is unavailable.");
            // Custody owns inventory/equipment effects, not unrelated locomotion. Stamina
            // serialization remains an aggregate activation requirement, not a movement lock.
            bool custody = _serverInventory.MutationEnforcement?.HasPendingCustody != false
                || _serverInventory.MutationEnforcement.HasPendingHeldOnlyCustody
                || _serverInventory.HasPendingArmourCustody || _serverLife?.BlocksActorActions == true
                || _serverDynamics?.HasPendingSwimmingEntry == true || _waterPresentation?.HasPendingSwimmingTransition == true;
            return actions with
            {
                CanUseInventory = actions.CanUseInventory && !custody,
                CanAttack = actions.CanAttack && !custody
            };
        }

        private void RetireServerMovement()
        {
            // The motor borrows the carrier and dynamics. Release in dependency order.
            _serverLife?.Dispose();
            _serverSurvival?.Dispose();
            _serverEnvironment?.Dispose();
            _serverExposure?.Dispose();
            _serverTemperature?.Dispose();
            _serverClimate?.Dispose();
            _serverMovement?.Dispose();
            _serverDynamics?.Dispose();
            _serverWater?.Dispose();
        }
    }
}
