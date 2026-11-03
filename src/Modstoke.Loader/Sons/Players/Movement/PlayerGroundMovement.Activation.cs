// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Loader;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerGroundMovement
    {
        private PlayerMovementModeSettings _waterSettings;
        private bool _activating;

        /// <summary>
        /// Activate the exact prepared motor only after aggregate completion and bridge
        /// gameplay activation. Starts observers before releasing physics, on the same game
        /// thread and outside a physics step. Never activates the bridge or native writers.
        /// A failure after observation starts retires this motor and holds its borrowed body.
        /// </summary>
        public bool TryActivate(out string detail)
        {
            detail = "Activate the completed gameplay bridge before releasing its prepared native motor.";
            if (Environment.CurrentManagedThreadId != _thread || _disposed || _activating || _activated
                || _insideMovementStep || Time.inFixedTimeStep || _nativeCarrier == null
                || !_bridge.IsGameplayActive || !_bridge.IsGroundMovementMode) return false;
            _activating = true;
            PlayerMovementModes modes = null;
            bool observing = false;
            try
            {
                if (!Current() || _nativeCarrier.ReadReadiness(out detail) != BoltMigrationAdmission.Ready
                    || !SupportedModeAllowed() || !Current() || !StaminaCurrent()) return false;
                var ground = _readNativeGroundDynamics();
                var swim = _readSwimDynamics();
                var stamina = _readStaminaConditions();
                if (!Current() || !ValidNativeGround(ground) || !ValidStaminaConditions(stamina)
                    || !PlayerMovementModes.ValidDynamics(swim)
                    || swim.WaterJump.HasValue != _swimWaterJumpsEnabled || swim.SwimmingJumpTimerRunning
                    || swim.WaterJump is { IsJumping: true } or { BlockWaterJump: true }
                    || !SwimNativeProxiesQuiescent()
                    || !NativeCapsuleClear(_swimCapsule.height, _swimCapsule.center))
                { detail = "Prepared native geometry, dynamics or stamina authority changed before activation."; return false; }
                // Preparation may span frames. Re-observe water without advancing a movement step.
                if (!TryCreateWaterModes(true, out modes, out detail)
                    || !modes.TryBindSwimBody(_swimBody, out detail, _nativeStepCollider)) return false;
                if (!Current() || !_bridge.IsGroundMovementMode) return false;
                _movementModes.Dispose();
                _movementModes = modes;
                modes = null;
                observing = true;
                if (!TryStartStaminaObservation(out detail)
                    || !global::Modstoke.Loader.Coroutines.TryStartObserved(ObserveSwimPhysics(), SwimCoroutineCompleted,
                        out var token, out detail)) throw new InvalidOperationException(detail);
                _swimCoroutine = token;
                if (!Current() || !_bridge.IsGroundMovementMode
                    || !_nativeCarrier.TryActivatePhysics(_bridge, out detail))
                    throw new InvalidOperationException(detail);
                _activated = true;
                ResetSwimContacts();
                _swimGrounded = _nativeCarrier.Profile.Grounded;
                _swimCurrentSpeed = ground.CurrentSpeed;
                _nativeContactsReady = false;
                _swimBody.WakeUp();
                if (!Current() || !_bridge.IsGameplayActive)
                    throw new InvalidOperationException("Carrier changed while activating its native motor.");
                detail = "Prepared native ground/swim motor activated; first physics step resolves contacts before input ACKs.";
                return true;
            }
            catch (Exception error)
            {
                detail = error is InvalidOperationException ? error.Message
                    : "Native motor activation failed (" + error.GetType().Name + ").";
                if (observing) Stop(detail);
                return false;
            }
            finally { modes?.Dispose(); _activating = false; }
        }
    }
}
