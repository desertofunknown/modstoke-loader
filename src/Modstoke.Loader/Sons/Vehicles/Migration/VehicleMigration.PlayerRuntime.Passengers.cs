// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using UnityEngine;
using NativeInput = Modstoke.Sons.PlayerClientMovement.NativeInput;
using NativeState = Modstoke.Sons.PlayerClientMovement.NativeState;
using NativeModeKey = Modstoke.Sons.PlayerClientMovement.NativeModeKey;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        private sealed partial class CartPlayerPassengerAuthority
        {
            private static CartPlayerPassengerAuthority[] _active = Array.Empty<CartPlayerPassengerAuthority>();
            private readonly Binding _binding;
            internal readonly CartMigrationPlayer _player;
            private readonly PlayerBridge _bridge;
            private readonly NativeModeKey _key;
            private NativeInput _selected;
            private NativeState _pending;
            private uint _simulated;
            private int _frame = -1;
            private bool _started, _inside, _quarantined;
            private CartPlayerMigrationContext _migrationYield;
            private bool _retired;
            internal bool IsQuarantined => _quarantined;

            internal CartPlayerPassengerAuthority(Binding binding, CartMigrationPlayer player,
                PlayerBridge bridge, NativeModeKey key)
            {
                _binding = binding; _player = player; _bridge = bridge; _key = key;
                RequireIdentity(false);
            }

            private void RequireIdentity(bool committed)
            {
                Require(OnSelectedThread() && !_quarantined && _binding.Matches()
                    && _binding.Entity.isOwner && _player.Slot is >= 1 and <= 3
                    && _player.DriverGhost == null && _player.LocalAction == null
                    && _bridge.IsServer && _bridge.IsGameplayActive && _bridge.Peer.IsCurrent
                    && _bridge.NetworkId == _player.PlayerNetworkId
                    && _bridge.TryReadCurrentTarget(out var entity) && SameEntity(entity, _player.Entity)
                    && _binding.CartState.ConnectedSlots.GetValueNetworkId(_player.Slot).PackedValue == _player.PlayerNetworkId
                    && Live(_player.Projection)
                    && _binding.Cart._externalEntityTransforms[_player.Slot]?.Pointer == _player.Projection.Pointer
                    && (!committed || _bridge.IsCommittedMovementMode(_key)),
                    "The exact server passenger, physical seat, native projection or bridge changed.");
            }

            internal void RequirePrepared()
            {
                CartPassengerExitHooks.Arm();
                Require(!_started && _bridge.CartMigrationBoundaryDrained, "Passenger activation was attempted or has pending native input.");
                RequireIdentity(false);
            }

            internal void Activate()
            {
                RequirePrepared(); RequireIdentity(true);
                Require(!CartWriterHooks.IsHeld(_binding.Cart) && OccupantsFor(_binding.Cart) == null,
                    "Stock cart holds must release before passenger acknowledgement starts.");
                Register(); _started = true;
            }

            private void Register()
            {
                var active = Volatile.Read(ref _active);
                Require(active.Length < 192, "The bounded passenger authority registry is full.");
                foreach (var item in active)
                    Require(!ReferenceEquals(item._bridge, _bridge), "The passenger bridge already has a seat authority.");
                var next = new CartPlayerPassengerAuthority[active.Length + 1];
                Array.Copy(active, next, active.Length); next[active.Length] = this;
                Volatile.Write(ref _active, next);
            }

            private void Unregister()
            {
                var active = Volatile.Read(ref _active);
                int index = Array.IndexOf(active, this);
                Require(index >= 0, "The exact passenger authority registration was lost.");
                var next = new CartPlayerPassengerAuthority[active.Length - 1];
                Array.Copy(active, 0, next, 0, index);
                Array.Copy(active, index + 1, next, index, next.Length - index);
                Volatile.Write(ref _active, next);
            }

            internal static CartPlayerPassengerAuthority[] ForCart(Component cart)
                => Array.FindAll(Volatile.Read(ref _active), item => item._binding.Cart.Pointer == cart?.Pointer);

            internal void RequireCanYield(CartPlayerRuntime runtime)
            {
                RequireIdentity(true);
                Require(_started && !_retired && _migrationYield == null && !_inside && !_exitRequested
                    && _selected.Input.Sequence == 0 && _pending.Motion.Sequence == 0
                    && runtime != null && ReferenceEquals(runtime.Bridge, _bridge)
                    && runtime.OriginalSeat == _key && runtime.ServerMovement != null
                    && runtime.ServerMovement.YieldedNativeMode == _key && runtime.ServerMovement.IsActive
                    && _bridge.CartMigrationBoundaryDrained && ReferenceEquals(Find(_bridge), this),
                    "The exact passenger projection, input publication and native exit must drain before migration.");
                CartPlayerCallbackSettlement.RequireSettled(_binding.Cart);
            }

            internal void Yield(CartPlayerMigrationContext context, CartPlayerRuntime runtime)
            {
                RequireCanYield(runtime);
                Require(context.Plan.OldNetworkId == _binding.Id, "Passenger yield changed its cart plan.");
                _migrationYield = context;
                Unregister();
            }

            internal void RequireYielded(CartPlayerMigrationContext context, bool retiring)
            {
                Require(OnSelectedThread() && !_quarantined && !_retired && ReferenceEquals(_migrationYield, context)
                    && Find(_bridge) == null && !_inside && !_exitRequested
                    && _selected.Input.Sequence == 0 && _pending.Motion.Sequence == 0
                    && (!retiring || !Live(_binding.Entity) || !_binding.Entity.isAttached),
                    "The original passenger policy must remain yielded until its exact cart retires.");
            }

            internal void Retire(CartPlayerMigrationContext context)
            {
                RequireYielded(context, true);
                _retired = true; _migrationYield = null;
            }

            internal void CancelYield(CartPlayerMigrationContext context)
            {
                RequireYielded(context, false);
                RequireIdentity(true);
                Require(_bridge.CartMigrationBoundaryDrained && !CartWriterHooks.IsHeld(_binding.Cart)
                    && OccupantsFor(_binding.Cart) == null, "Restore the original stock occupant writers before its passenger route.");
                Register(); _frame = -1; _migrationYield = null;
            }

            internal void Quarantine()
            {
                if (_retired) return;
                _quarantined = true;
                if (OnSelectedThread() && Live(_binding.Body))
                {
                    if (_migrationYield != null && !CartWriterHooks.IsHeld(_binding.Cart) && Find(_bridge) == null) Register();
                    _binding.Body.isKinematic = true;
                }
            }

            internal static bool IsAcquired(Component component)
            {
                foreach (var item in Volatile.Read(ref _active))
                    if (component?.Pointer == item._binding.Cart.Pointer) return true;
                return false;
            }

            private void Begin()
            {
                if (_quarantined) return;
                if (_exitRequested) { RequireExiting(); return; }
                RequireIdentity(true);
                Require(!_inside, "Reentrant native passenger projection.");
                if (_frame == Time.frameCount) return;
                _frame = Time.frameCount; _inside = true;
                Publish();
                if (_bridge.TryPeekCurrentNativeInput(out var current) && current.Input.Sequence > _simulated
                    && _bridge.TryReadCurrentNativeInput(out var input) && input.Input.Sequence > _simulated)
                {
                    Require(input.Key == _key, "Passenger input selected a different physical seat.");
                    _selected = input;
                }
            }

            private void Complete(bool ran, Exception error)
            {
                if (!_inside) return;
                _inside = false;
                if (_quarantined || !ran || error != null) { Quarantine(); return; }
                RequireIdentity(true);
                if (_selected.Input.Sequence == 0) return;
                var input = _selected;
                Require(_bridge.TryCommitSimulatedNativeInput(_key, input.Input.Sequence),
                    "Completed native passenger projection lost its selected input ticket.");
                _simulated = input.Input.Sequence; _selected = default; _pending = default;
                if (!input.SeatSettled) return;
                uint sequence = _bridge.LastSentStateSequence;
                Require(sequence != uint.MaxValue, "Passenger state sequence exhausted.");
                var root = _binding.Entity.transform;
                var state = new PlayerMotionState(_bridge.Plan.Id, _bridge.Plan.Generation,
                    _player.PlayerNetworkId, sequence + 1, input.Input.Sequence,
                    root.InverseTransformPoint(_player.Projection.position),
                    Quaternion.Inverse(root.rotation) * _player.Projection.rotation, Vector3.zero,
                    PlayerMotionFlags.None) { MovementTransitionSequence = _key.TransitionSequence };
                _pending = new(_key, state);
                Publish();
            }

            private void Publish()
            {
                if (_pending.Motion.Sequence == 0) return;
                var sent = _bridge.SendNativeState(_pending);
                Require(sent is DatagramSendResult.Queued or DatagramSendResult.WindowFull,
                    "Passenger projection publication failed: " + sent);
                if (sent == DatagramSendResult.Queued) _pending = default;
            }

            internal static bool Before(Component component, string method)
            {
                if (method != "LastUpdate") return true;
                foreach (var item in Volatile.Read(ref _active))
                {
                    if (component?.Pointer != item._binding.Cart.Pointer) continue;
                    try { item.Begin(); }
                    catch { item.Quarantine(); return false; }
                    if (item._quarantined) return false;
                }
                return true;
            }

            internal static void After(Component component, string method, bool ran, Exception error)
            {
                if (ran && error != null)
                    foreach (var item in Volatile.Read(ref _active))
                        if (component?.Pointer == item._binding.Cart.Pointer) item.Quarantine();
                if (method == "OnDestroy" && ran && error == null && OnSelectedThread())
                {
                    var current = Volatile.Read(ref _active);
                    int count = 0;
                    foreach (var item in current) if (component?.Pointer != item._binding.Cart.Pointer) count++;
                    if (count == current.Length) return;
                    var retained = new CartPlayerPassengerAuthority[count];
                    int index = 0;
                    foreach (var item in current)
                        if (component?.Pointer != item._binding.Cart.Pointer) retained[index++] = item;
                    Volatile.Write(ref _active, retained);
                    return;
                }
                if (method != "LastUpdate") return;
                foreach (var item in Volatile.Read(ref _active))
                {
                    if (component?.Pointer != item._binding.Cart.Pointer) continue;
                    try { item.Complete(ran, error); }
                    catch { item.Quarantine(); }
                }
            }
        }
    }
}
