// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;
using NativeMode = Modstoke.Sons.PlayerClientMovement.NativeMode;
using NativeModeKey = Modstoke.Sons.PlayerClientMovement.NativeModeKey;

namespace Modstoke.Sons
{
    public sealed partial class PlayerGroundMovement
    {
        private bool _nativeModeSuspended;
        private NativeModeKey _yieldedGroundKey, _yieldedNativeKey;
        private VehiclePreservation.CartDriverAuthority _cartDriver;
        private float _heldExhaustionRemaining;

        public bool IsYieldedToNativeMode => !_disposed && _nativeModeSuspended;
        public NativeModeKey? YieldedNativeMode => IsYieldedToNativeMode ? _yieldedNativeKey : null;

        /// <summary>
        /// Yield the existing motor before the aggregate commits an authenticated native
        /// enter. No native action or bridge commit is inferred here. Outstanding physics
        /// and unsent ground state must drain first. Holds the borrowed body, retains this
        /// motor's ownership and freezes its stamina timers until explicit ground resume.
        /// The aggregate owns special-mode physics, survival and reliable action settlement.
        /// </summary>
        public bool TryYieldToNativeMode(NativeModeKey expectedGround, NativeModeKey committed,
            out string detail)
        {
            detail = "Native entry needs an active native motor and a settled, published ground boundary.";
            if (Environment.CurrentManagedThreadId != _thread || !IsActive || CartMigrationHeld || _nativeModeSuspended
                || HasPendingSwimmingEntry
                || _activating || _insideMovementStep || _insideSwimCoroutine || _insideStaminaCoroutine
                || Time.inFixedTimeStep || _nativeCarrier == null || _swimAwaitingPhysics
                || _swimActive || _sledAuthority != null || !_nativeContactsReady
                || expectedGround.Mode != NativeMode.Ground || committed.Mode == NativeMode.Ground
                || committed.TransitionSequence <= expectedGround.TransitionSequence
                || !PlayerBridgeProtocol.IsValidMovementModeKey(committed)) return false;
            bool holding = false;
            try
            {
                if (!Current() || !_bridge.IsCommittedMovementMode(expectedGround)) return false;
                if (!FlushMovementState() || _pendingState.Sequence != 0)
                { detail = "The final ground state is still awaiting transport credit."; return false; }
                UpdateNativeStepHelper(Vector2.zero, true);
                if (!Current() || !_bridge.IsCommittedMovementMode(expectedGround)) return false;
                holding = true;
                _heldExhaustionRemaining = _staminaForceStop
                    ? Mathf.Max(0, _staminaExhaustedUntil - Time.time) : 0;
                _swimCapsule.enabled = false;
                _swimBody.detectCollisions = false;
                _swimBody.collisionDetectionMode = CollisionDetectionMode.Discrete;
                _swimBody.velocity = Vector3.zero;
                _swimBody.angularVelocity = Vector3.zero;
                _swimBody.isKinematic = true;
                _yieldedGroundKey = expectedGround;
                _yieldedNativeKey = committed;
                _nativeModeSuspended = true;
                _staminaRunning = false;
                if (!NativeModeIdentityCurrent() || !NativeModeBodyHeld())
                    throw new InvalidOperationException("The native-mode handoff did not retain the exact held carrier.");
                detail = "Ground motor yielded with body held; commit the exact native key and install its authoritative writer.";
                return true;
            }
            catch (Exception error)
            {
                detail = error is InvalidOperationException ? error.Message
                    : "Native-mode handoff failed (" + error.GetType().Name + ").";
                if (holding) Stop(detail);
                return false;
            }
        }

        /// <summary>
        /// Connect a separately acquired, settled server cart driver to authenticated native
        /// controls through the cart's native fixed/seat completion callbacks. This does
        /// not acquire a seat or substitute ground stepping for vehicle/seat simulation.
        /// The aggregate retains the driver lease and must settle/release it on exit.
        /// </summary>
        public bool TryBindNativeCartDriver(NativeModeKey key,
            VehiclePreservation.CartDriverAuthority authority, out string detail)
        {
            detail = "A committed cart-driver key and the exact server driver authority are required.";
            if (Environment.CurrentManagedThreadId != _thread || _disposed || CartMigrationHeld || !_nativeModeSuspended
                || _insideMovementStep || Time.inFixedTimeStep || _cartDriver != null
                || authority == null || key != _yieldedNativeKey || key.Mode != NativeMode.GolfCartDriver
                || authority.IsQuarantined || authority.CartNetworkId != key.AnchorNetworkId
                || authority.DriverNetworkId != _bridge.NetworkId) return false;
            try
            {
                if (!NativeModeIdentityCurrent() || !NativeModeBodyHeld()
                    || !_bridge.IsCommittedMovementMode(key)) return false;
                if (!authority.TryBindInput(_bridge, key, out detail)) return false;
                _cartDriver = authority;
                detail = "Native cart input and seat completion connected to the existing driver authority.";
                return true;
            }
            catch (Exception error)
            { detail = "Cart control binding failed (" + error.GetType().Name + ")."; return false; }
        }

        private void StepNativeMode()
        {
            if (!NativeModeIdentityCurrent())
            { Stop("The yielded motor lost its exact carrier or bridge."); return; }
            // Entry and exit commits can precede their final participant callback. Remain
            // held until explicit resume; never turn a key observation into a physics release.
            if (_bridge.IsGroundMovementMode)
            {
                if (!NativeModeBodyHeld()) Stop("Ground physics was released before explicit motor resume.");
                return;
            }
            if (!_bridge.IsCommittedMovementMode(_yieldedNativeKey))
            { Stop("The committed native mode does not match this motor's handoff."); return; }
            if (_cartDriver == null) return;
            if (_cartDriver.IsQuarantined || !NativeModeBodyHeld())
            { Stop("The native cart driver or held player body changed."); return; }
            // The bound cart callbacks own input selection, simulation and seat-pose ACKs.
        }

        /// <summary>
        /// After native exit/callback settlement and the committed ground key, restore this
        /// same motor. The aggregate first restores the carrier's ground profile and upright
        /// root pose, then supplies server-resolved velocity/contact/crouch state, never a
        /// client pose claim. The original key is allowed only when entry never committed.
        /// No native exit, entry impulse, animation or camera operation is replayed.
        /// </summary>
        public bool TryResumeGroundMode(NativeModeKey ground, Vector3 velocity, Vector3 angularVelocity,
            bool grounded, bool crouched, out string detail)
        {
            detail = "Settle native exit, restore ground bindings and commit the exact ground key before resume.";
            if (Environment.CurrentManagedThreadId != _thread || !IsActive || CartMigrationHeld || !_nativeModeSuspended
                || _insideMovementStep || _insideSwimCoroutine || _insideStaminaCoroutine || Time.inFixedTimeStep
                || ground.Mode != NativeMode.Ground || !Finite(velocity) || !Finite(angularVelocity)
                || (ground != _yieldedGroundKey && ground.TransitionSequence <= _yieldedNativeKey.TransitionSequence)) return false;
            bool releasing = false;
            PlayerMovementModes modes = null;
            try
            {
                if (!NativeModeIdentityCurrent() || !NativeModeBodyHeld() || !_bridge.IsCommittedMovementMode(ground)
                    || _transform.parent != null || !Upright(_transform.up) || !StaminaCurrent()) return false;
                if (!NativeCapsuleClear(crouched ? _nativeCapsule.CrouchingHeight : _nativeCapsule.StandingHeight,
                    crouched ? _nativeCapsule.CrouchingCenter : _nativeCapsule.StandingCenter))
                { detail = "The resumed ground capsule is obstructed or its overlap observation is incomplete."; return false; }
                if (!TryCreateWaterModes(false, out modes, out detail)
                    || !modes.TryBindSwimBody(_swimBody, out detail, _nativeStepCollider)) return false;
                releasing = true;
                _crouched = crouched;
                CopyNativeCapsule();
                _swimBody.position = _transform.position;
                _swimBody.rotation = _transform.rotation;
                _swimBody.useGravity = _swimProfile.UseGravity;
                _swimBody.isKinematic = false;
                _swimBody.collisionDetectionMode = _swimProfile.CollisionDetection;
                _swimBody.velocity = velocity;
                _swimBody.angularVelocity = angularVelocity;
                _swimCapsule.enabled = true;
                _swimBody.detectCollisions = true;
                _nativeModeSuspended = false;
                _movementModes.Dispose();
                _movementModes = modes;
                modes = null;
                _velocity = velocity;
                ResetSwimContacts();
                _swimGrounded = grounded;
                _swimJumping = _nativeJumping = !grounded;
                _nativeAirControl = 1;
                _nativeContactsReady = false;
                _lastFixedTime = double.NaN;
                _staminaExhaustedUntil = _staminaForceStop ? Time.time + _heldExhaustionRemaining : float.NaN;
                _staminaFrame = Time.frameCount;
                if (!Current() || !SupportedModeAllowed() || !Current() || !_bridge.IsCommittedMovementMode(ground))
                    throw new InvalidOperationException("The restored carrier no longer has exclusive ground authority.");
                if (_animation != null) _animation = new PlayerAnimationGround(_bridge);
                _swimBody.WakeUp();
                DisposeNativeModeRouting();
                detail = "The retained ground/swim motor resumed; contacts must complete before accepting new motion input.";
                return true;
            }
            catch (Exception error)
            {
                detail = error is InvalidOperationException ? error.Message
                    : "Ground resume failed (" + error.GetType().Name + ").";
                if (releasing) Stop(detail);
                return false;
            }
            finally { modes?.Dispose(); }
        }

        private bool NativeModeIdentityCurrent() => _nativeModeSuspended && MotorIdentityCurrent()
            && _bridge.IsGameplayActive && _nativeCarrier != null && !_nativeCarrier.IsDisposed
            && _nativeCarrier.FailureReason == null && SwimAlive(_nativeCarrier.Entity)
            && _nativeCarrier.Entity.Pointer == _entity.Pointer
            && SwimAlive(_swimBody) && _nativeCarrier.Body?.Pointer == _swimBody.Pointer
            && SwimAlive(_swimCapsule) && _nativeCarrier.PrimaryCollider?.Pointer == _swimCapsule.Pointer
            && SwimAlive(_swimContacts) && _nativeCarrier.Contacts?.Pointer == _swimContacts.Pointer
            && !_swimContacts.enabled;

        private bool NativeModeBodyHeld() => _swimBody.isKinematic && !_swimBody.detectCollisions
            && !_swimCapsule.enabled && (_nativeStepCollider == null || !_nativeStepCollider.enabled);

        private void DisposeNativeModeRouting()
        {
            _cartDriver = null;
            _nativeModeSuspended = false;
            _yieldedGroundKey = _yieldedNativeKey = default;
        }
    }
}
