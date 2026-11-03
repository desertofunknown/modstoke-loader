// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Gameplay;
using UnityEngine;
using NativeModeKey = Modstoke.Sons.PlayerClientMovement.NativeModeKey;

namespace Modstoke.Sons
{
    public sealed partial class PlayerBridge
    {
        // A migration boundary may not discard a selected but uncommitted simulation step.
        // The parent-owned authenticated rebind preserves transport/ACK high-water marks.
        internal bool CartMigrationBoundaryDrained => Environment.CurrentManagedThreadId == _thread
            && !_disposed && !_busy && IsGameplayActive && ValidateCurrent() && !HasPendingMovementTransition
            && (!_server || (_takenInputSequence <= _simulatedInputSequence
                && _selectedNativeInput.Input.Sequence <= _simulatedInputSequence));
    }

    public sealed partial class PlayerClientMovement
    {
        private CartPlayerMigrationContext _cartMigration;
        private NativeBinding _cartMigrationOriginal, _cartMigrationReplacement;
        private bool _cartMigrationQuarantined;
        private float _cartMigrationPausedAt, _cartMigrationInitializedAt, _cartMigrationDriverStartedAt;
        private int _cartMigrationInitializedFrame;
        private RigPhysics _cartMigrationPhysics;
        private bool[] _cartMigrationColliderEnabled, _cartMigrationColliderTrigger;

        // Parent gates Tick, CompletePhysics, ForNative and NativeActionOwner before
        // their ordinary binding checks. Keep the observation coroutine alive at its yield.
        internal bool CartMigrationHeld => _cartMigration != null || _cartMigrationQuarantined;

        internal bool TryHoldCartMigration(CartPlayerMigrationContext context,
            CartMigrationPlayer player, PlayerBridge bridge, NativeModeKey key, out string detail)
        {
            detail = "Wait for the exact source seat sampler's completed native callback boundary.";
            if (context == null || CartMigrationHeld || !ReferenceEquals(_bridge, bridge)
                || _transition != null || _deferredNativeExit != null || _insideFixed || _insideCoroutine
                || _awaitingPhysics || _sendingNative || Time.inFixedTimeStep || !Current(true)
                || _nativeMode == null || _nativeMode.Key != key || !Same(_nativeMode.Seat, player.LocalAction)
                || _nativeMode.SeatLerp != 1 || !_bridge.CartMigrationBoundaryDrained) return false;
            _cartMigrationOriginal = _nativeMode;
            _cartMigrationPausedAt = Time.time;
            var state = _nativeMode.Seat._state;
            _cartMigrationInitializedAt = state.InitializedTime;
            _cartMigrationDriverStartedAt = state.DriverStartTime;
            _cartMigrationInitializedFrame = state.InitializedFrame;
            _cartMigrationPhysics = RigPhysics.Read(_rig);
            _cartMigrationColliderEnabled = new bool[_rig.Colliders.Length];
            _cartMigrationColliderTrigger = new bool[_rig.Colliders.Length];
            for (int i = 0; i < _rig.Colliders.Length; i++)
            {
                _cartMigrationColliderEnabled[i] = _rig.Colliders[i].enabled;
                _cartMigrationColliderTrigger[i] = _rig.Colliders[i].isTrigger;
            }
            _cartMigration = context;
            detail = "Original source sampler retained with its input, camera and native action unchanged.";
            return true;
        }

        internal bool IsCartMigrationHeld(CartPlayerMigrationContext context)
            => ReferenceEquals(_cartMigration, context) && !_cartMigrationQuarantined && !_disposed
                && _activated && Environment.CurrentManagedThreadId == _thread && RetainedTargetCurrent(true)
                && ReferenceEquals(_nativeMode, _cartMigrationOriginal) && _transition == null
                && _deferredNativeExit == null && !_insideFixed && !_awaitingPhysics && !_sendingNative;

        internal bool TryPrepareCartMigrationRebind(CartPlayerMigrationContext context,
            NativeModeKey key, GolfCartController cart, out string detail)
        {
            detail = "The exact held source sampler and repaired original native action are required.";
            if (!IsCartMigrationHeld(context) || _cartMigrationReplacement != null
                || key.Mode != _cartMigrationOriginal.Key.Mode || key.SeatIndex != _cartMigrationOriginal.Key.SeatIndex
                || key.TransitionSequence != _cartMigrationOriginal.Key.TransitionSequence + 1
                || key.AnchorNetworkId == _cartMigrationOriginal.Key.AnchorNetworkId) return false;
            var replacement = new NativeBinding(key, _rig, _cartMigrationOriginal.Seat, cart);
            if (!replacement.Matches(_rig) || replacement.SeatLerp != 1) return false;
            _cartMigrationReplacement = replacement;
            detail = "Replacement locator bound to the same original action, ghost, rig and camera.";
            return true;
        }

        internal bool IsCartMigrationPrepared(CartPlayerMigrationContext context)
            => IsCartMigrationHeld(context) && _cartMigrationReplacement != null
                && _cartMigrationReplacement.Matches(_rig) && _cartMigrationReplacement.SeatLerp == 1;

        internal bool TryResumeCartMigration(CartPlayerMigrationContext context, out string detail)
        {
            detail = "The repaired source seat must have its exact session-authorized replacement key.";
            if (!IsCartMigrationHeld(context) || _cartMigrationReplacement == null
                || !_bridge.IsCommittedMovementMode(_cartMigrationReplacement.Key) || !CartMigrationClocksReleased()) return false;
            // The stock occupant hold shifts native action clocks during its release.
            // Re-capture only after validating that exact shift, without replaying Initialize.
            var released = new NativeBinding(_cartMigrationReplacement.Key, _rig,
                _cartMigrationOriginal.Seat, _cartMigrationReplacement.Cart);
            if (!released.Matches(_rig) || released.SeatLerp != 1) return false;
            ResetModeHistory(_cartMigrationReplacement.Key.TransitionSequence);
            if (_disposed) return false;
            _nativeMode = released;
            _modeSequence = _nativeMode.Key.TransitionSequence;
            _cartMigration = null; _cartMigrationOriginal = _cartMigrationReplacement = null;
            if (!Current(true)) { _cartMigrationQuarantined = true; return false; }
            detail = "Same source sampler resumed on the replacement cart; transport high-water marks retained.";
            return true;
        }

        internal bool TryCancelCartMigration(CartPlayerMigrationContext context, out string detail)
        {
            detail = "Original source seat cannot resume after a replacement or changed movement key.";
            if (_cartMigration == null && !_cartMigrationQuarantined) return !_disposed && Current(true);
            if (!IsCartMigrationHeld(context) || _cartMigrationReplacement != null
                || !CartMigrationClocksReleased() || !_bridge.IsCommittedMovementMode(_cartMigrationOriginal.Key)) return false;
            var released = new NativeBinding(_cartMigrationOriginal.Key, _rig,
                _cartMigrationOriginal.Seat, _cartMigrationOriginal.Cart);
            if (!released.Matches(_rig) || released.SeatLerp != 1) return false;
            _nativeMode = released;
            _cartMigration = null; _cartMigrationOriginal = null;
            detail = "Original source sampler resumed without changing its mode or replaying inputs.";
            return true;
        }

        private bool CartMigrationClocksReleased()
        {
            float elapsed = Time.time - _cartMigrationPausedAt;
            var state = _cartMigrationOriginal.Seat._state;
            if (!float.IsFinite(elapsed) || elapsed < 0 || state == null
                || state.InitializedFrame != _cartMigrationInitializedFrame
                || state.InitializedTime != _cartMigrationInitializedAt + elapsed
                || state.DriverStartTime != (_cartMigrationOriginal.Key.SeatIndex == 0
                    ? _cartMigrationDriverStartedAt + elapsed : _cartMigrationDriverStartedAt)
                || RigPhysics.Read(_rig) != _cartMigrationPhysics) return false;
            for (int i = 0; i < _rig.Colliders.Length; i++)
                if (!Alive(_rig.Colliders[i]) || _rig.Colliders[i].enabled != _cartMigrationColliderEnabled[i]
                    || _rig.Colliders[i].isTrigger != _cartMigrationColliderTrigger[i]) return false;
            return true;
        }

        internal void QuarantineCartMigration(CartPlayerMigrationContext context)
        {
            // Remain paused even when a later save failure follows successful local release.
            if (_cartMigration != null && !ReferenceEquals(_cartMigration, context)) return;
            _cartMigration = context; _cartMigrationQuarantined = true;
        }
    }

    public sealed partial class PlayerGroundMovement
    {
        private CartPlayerMigrationContext _cartMigration;
        private VehiclePreservation.CartDriverAuthority _cartMigrationDriver;
        private bool _cartMigrationQuarantined;
        internal bool CartMigrationHeld => _cartMigration != null || _cartMigrationQuarantined;

        internal bool CanYieldCartMigrationDriver(VehiclePreservation.CartDriverAuthority driver,
            PlayerBridge bridge, NativeModeKey key, out string detail)
        {
            detail = "The current server motor must retain this exact driver authority.";
            return !CartMigrationHeld && ReferenceEquals(_bridge, bridge) && IsActive
                && !_insideMovementStep && NativeModeIdentityCurrent() && NativeModeBodyHeld()
                && key == _yieldedNativeKey && ReferenceEquals(_cartDriver, driver) && driver != null
                && driver.CanYieldCartMigration(bridge, key, out detail);
        }

        internal bool RetainsCartDriver(VehiclePreservation.CartDriverAuthority driver,
            PlayerBridge bridge, NativeModeKey key)
            => Environment.CurrentManagedThreadId == _thread && IsActive && !CartMigrationHeld
                && ReferenceEquals(_bridge, bridge) && ReferenceEquals(_cartDriver, driver)
                && driver != null && _nativeModeSuspended && _yieldedNativeKey == key;

        internal bool TryHoldCartMigration(CartPlayerMigrationContext context,
            PlayerBridge bridge, NativeModeKey key, out string detail)
        {
            detail = "A drained, already-yielded server player motor is required; this is not native entry.";
            if (context == null || CartMigrationHeld || !ReferenceEquals(_bridge, bridge)
                || Environment.CurrentManagedThreadId != _thread || !IsActive || _insideMovementStep
                || Time.inFixedTimeStep || _swimAwaitingPhysics || _pendingState.Sequence != 0
                || !NativeModeIdentityCurrent() || !NativeModeBodyHeld()
                || key != _yieldedNativeKey || !_bridge.IsCommittedMovementMode(key) || !_bridge.CartMigrationBoundaryDrained)
                return false;
            if (_cartDriver != null && !CanYieldCartMigrationDriver(_cartDriver, bridge, key, out detail)) return false;
            _cartMigration = context;
            if (_cartDriver != null)
            {
                _cartMigrationDriver = _cartDriver;
                if (!_cartMigrationDriver.TryYieldCartMigration(context, bridge, key, out detail)) return false;
                _cartDriver = null;
            }
            detail = "Existing server motor and held carrier retained; prior driver authority explicitly yielded.";
            return true;
        }

        internal bool IsCartMigrationHeld(CartPlayerMigrationContext context)
            => ReferenceEquals(_cartMigration, context) && !_cartMigrationQuarantined
                && Environment.CurrentManagedThreadId == _thread && IsActive && !_insideMovementStep
                && NativeModeIdentityCurrent() && NativeModeBodyHeld() && _cartDriver == null
                && (_cartMigrationDriver == null || _cartMigrationDriver.IsCartMigrationYielded(context));

        internal bool IsCartMigrationPrepared(CartPlayerMigrationContext context)
            => IsCartMigrationHeld(context)
                && (_cartMigrationDriver == null || _cartMigrationDriver.CanRetireCartMigration(context));

        internal bool TryResumeCartMigration(CartPlayerMigrationContext context, NativeModeKey key,
            VehiclePreservation.CartDriverAuthority driver, out string detail)
        {
            detail = "The held server motor requires the exact committed replacement seat and prepared driver.";
            if (!IsCartMigrationPrepared(context) || !_bridge.IsCommittedMovementMode(key)
                || key.Mode != _yieldedNativeKey.Mode || key.SeatIndex != _yieldedNativeKey.SeatIndex
                || key.TransitionSequence != _yieldedNativeKey.TransitionSequence + 1
                || key.AnchorNetworkId == _yieldedNativeKey.AnchorNetworkId
                || (key.SeatIndex == 0 ? driver == null || !driver.IsBoundTo(_bridge, key) : driver != null)) return false;
            if (_cartMigrationDriver != null && !_cartMigrationDriver.TryRetireCartMigration(context, out detail)) return false;
            _cartMigrationDriver = null;
            _yieldedNativeKey = key; _cartDriver = driver; _cartMigration = null;
            detail = "Yielded server motor now follows the same player on the replacement cart."; return true;
        }

        internal bool TryCancelCartMigration(CartPlayerMigrationContext context, out string detail)
        {
            detail = "The original yielded motor must remain on its unchanged committed native key.";
            if (_cartMigration == null && !_cartMigrationQuarantined)
                return IsActive && NativeModeIdentityCurrent() && NativeModeBodyHeld();
            if (!IsCartMigrationHeld(context) || !_bridge.IsCommittedMovementMode(_yieldedNativeKey)) return false;
            if (_cartMigrationDriver != null)
            {
                if (!_cartMigrationDriver.TryCancelCartMigration(context, out detail)) return false;
                _cartDriver = _cartMigrationDriver; _cartMigrationDriver = null;
            }
            _cartMigration = null; detail = "Original yielded server motor retained."; return true;
        }

        internal void QuarantineCartMigration(CartPlayerMigrationContext context)
        {
            if (_cartMigration != null && !ReferenceEquals(_cartMigration, context)) return;
            _cartMigration = context; _cartMigrationQuarantined = true;
            try { _cartMigrationDriver?.Quarantine("Occupied cart runtime failed while the prior driver was yielded."); }
            finally
            {
                try { _cartDriver?.Quarantine("Occupied cart runtime failed after movement handoff."); }
                finally
                {
                    if (SwimAlive(_swimBody)) { _swimBody.isKinematic = true; _swimBody.detectCollisions = false; }
                }
            }
        }
    }
}
