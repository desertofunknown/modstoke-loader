// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using global::Sons.Gameplay;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        // The physical cart follows its authoritative transform/wheel channels. The original
        // local action still reads controls and owns seat/camera projection; its ghost remains
        // the physical driver's identity, but no longer publishes a second vehicle motor.
        private sealed class CartPlayerProxyAuthority
        {
            private static CartPlayerProxyAuthority[] _active = Array.Empty<CartPlayerProxyAuthority>();
            private readonly Binding _binding;
            private readonly CartMigrationPlayer _driver;
            private readonly List<Transform> _passengerProjections = new();
            private readonly List<CartMigrationPlayer> _players = new();
            private bool _started, _quarantined, _policyWrite, _localDriverExited;
            private CartPlayerMigrationContext _migrationYield;
            private bool _retired;
            internal bool IsQuarantined => _quarantined;

            internal CartPlayerProxyAuthority(Binding binding, IReadOnlyList<CartMigrationPlayer> players)
            {
                _binding = binding;
                foreach (var player in players)
                {
                    _players.Add(player);
                    if (player.Slot == 0) _driver = player;
                    else if (Live(player.Projection)) _passengerProjections.Add(player.Projection);
                }
                RequirePrepared();
            }

            internal bool OwnsPassengerProjection(Transform target)
            {
                foreach (var projection in _passengerProjections)
                    if (projection.Pointer == target?.Pointer) return true;
                return false;
            }

            internal void RequirePrepared()
            {
                Require(OnSelectedThread() && !_started && !_quarantined && _binding.Matches()
                    && !_binding.Entity.isOwner && Find(_binding.Cart) == null,
                    "Only the exact non-owner replacement may prepare replica authority.");
                RequireDriver();
            }

            private void RequireDriver()
            {
                if (_localDriverExited)
                {
                    // The source's approved native exit can precede Bolt's empty-slot delivery.
                    // Keep replica policy until that exact old seat clears; never adopt a new driver.
                    ulong seat = _binding.CartState.ConnectedSlots.GetValueNetworkId(0).PackedValue;
                    Require((seat == 0 || seat == _driver.SeatNetworkId)
                        && !_binding.Cart._isLocalConnectedSlots[0] && _binding.Cart._localPlayerDriverAction == null,
                        "A new driver arrived before its authenticated replica lifecycle was bound.");
                    return;
                }
                if (_driver == null)
                {
                    Require(_binding.CartState.ConnectedSlots.GetValueNetworkId(0).PackedValue == 0,
                        "A new driver requires its own authenticated native seat lifecycle.");
                    return;
                }
                Require(Live(_driver.Entity) && _driver.Entity.isAttached
                    && _driver.Entity.networkId.PackedValue == _driver.PlayerNetworkId
                    && Live(_driver.DriverGhost) && _driver.DriverGhost.isAttached
                    && _driver.DriverGhost.networkId.PackedValue == _driver.SeatNetworkId
                    && _binding.CartState.ConnectedSlots.GetValueNetworkId(0).PackedValue == _driver.SeatNetworkId
                    && _driver.DriverGhost.TryFindState<IGolfCartState>(out var state)
                    && state.ConnectedSlots.GetValueNetworkId(0).PackedValue == _driver.PlayerNetworkId,
                    "The replica physical-driver -> ghost -> player identity changed.");
                if (_driver.LocalAction != null)
                    Require(SameEntity(_binding.Cart._ownedGolfCartGhostEntity, _driver.DriverGhost)
                        && _binding.Cart._localPlayerDriverAction?.Pointer == _driver.LocalAction.Pointer,
                        "The original local driver action and owned ghost must survive.");
                else
                    Require(SameEntity(_binding.Cart._externalGolfCartGhostEntity, _driver.DriverGhost)
                        && Live(_driver.Projection)
                        && _binding.Cart._externalEntityTransforms[0]?.Pointer == _driver.Projection.Pointer,
                        "The original remote driver projection must survive.");
            }

            internal void RequireLocalDriverExit(PlayerGolfCartAction action, ulong playerId)
            {
                Require(OnSelectedThread() && _started && !_quarantined && !_localDriverExited
                    && _binding.Matches() && !_binding.Entity.isOwner && _driver != null
                    && _driver.PlayerNetworkId == playerId && _driver.LocalAction?.Pointer == action?.Pointer,
                    "Source driver exit must retain the exact migrated replica and original action.");
                // The authenticated server exit may already have cleared the replicated raw slot.
                ulong seat = _binding.CartState.ConnectedSlots.GetValueNetworkId(0).PackedValue;
                Require((seat == 0 || seat == _driver.SeatNetworkId)
                    && SameEntity(_binding.Cart._ownedGolfCartGhostEntity, _driver.DriverGhost)
                    && _binding.Cart._localPlayerDriverAction?.Pointer == action.Pointer,
                    "The approved source driver exit lost its retained ghost or local action.");
            }

            internal void CompleteLocalDriverExit(PlayerGolfCartAction action)
            {
                Require(OnSelectedThread() && !_quarantined && !_localDriverExited
                    && _driver?.LocalAction?.Pointer == action?.Pointer
                    && !_binding.Cart._isLocalConnectedSlots[0] && _binding.Cart._localPlayerDriverAction == null,
                    "The original local driver disconnect did not settle its exact replica.");
                _localDriverExited = true;
            }

            private bool ApplyingLocalDriverExit
                => CartPassengerExitHooks.Client is { Slot: 0 } receipt
                    && receipt.Cart.Pointer == _binding.Cart.Pointer
                    && receipt.Action.Pointer == _driver?.LocalAction?.Pointer;

            internal void Activate()
            {
                RequirePrepared();
                Require(!CartWriterHooks.IsHeld(_binding.Cart) && OccupantsFor(_binding.Cart) == null,
                    "Release the stock cart barrier before installing replica authority.");
                Register();
                _started = true; _policyWrite = true;
                try
                {
                    _binding.Cart.SetToKinematic(true);
                    _binding.Cart.SetRootTransformSynced(true);
                    Require(_binding.Body.isKinematic && _binding.Cart._setToKinematic
                        && _binding.CartState.Transform.Simulate?.Pointer == _binding.Entity.transform.Pointer,
                        "The physical replica did not bind its original authoritative transform channel.");
                }
                catch { Quarantine(); throw; }
                finally { _policyWrite = false; }
            }

            private void Register()
            {
                var active = Volatile.Read(ref _active);
                Require(active.Length < 64 && Find(_binding.Cart) == null, "Cart replica registry is full or already owns this cart.");
                var next = new CartPlayerProxyAuthority[active.Length + 1];
                Array.Copy(active, next, active.Length); next[active.Length] = this;
                Volatile.Write(ref _active, next);
            }

            private void Unregister()
            {
                var active = Volatile.Read(ref _active);
                int index = Array.IndexOf(active, this);
                Require(index >= 0, "The retained cart replica registration was lost.");
                var next = new CartPlayerProxyAuthority[active.Length - 1];
                Array.Copy(active, 0, next, 0, index);
                Array.Copy(active, index + 1, next, index, next.Length - index);
                Volatile.Write(ref _active, next);
            }

            internal void RequireCanYield(IReadOnlyList<CartPlayerRuntime> runtimes)
            {
                Require(OnSelectedThread() && _started && !_quarantined && !_retired && !_localDriverExited
                    && _migrationYield == null && !_policyWrite && !ApplyingLocalDriverExit && _binding.Matches()
                    && !_binding.Entity.isOwner && ReferenceEquals(Find(_binding.Cart), this)
                    && _binding.Body.isKinematic && _binding.Cart._setToKinematic
                    && _binding.CartState.Transform.Simulate?.Pointer == _binding.Entity.transform.Pointer
                    && runtimes.Count == _players.Count, "The exact active occupied cart replica cannot yield at this boundary.");
                RequireDriver();
                CartPlayerCallbackSettlement.RequireSettled(_binding.Cart);
                foreach (var player in _players)
                {
                    int matches = 0;
                    foreach (var runtime in runtimes)
                        if (runtime.PlayerNetworkId == player.PlayerNetworkId && runtime.OriginalSeat.SeatIndex == player.Slot
                            && runtime.OriginalSeat.AnchorNetworkId == _binding.Id) matches++;
                    Require(matches == 1 && _binding.CartState.ConnectedSlots.GetValueNetworkId(player.Slot).PackedValue == player.SeatNetworkId,
                        "Repeat migration must retain every original occupied replica seat and authenticated player.");
                }
            }

            internal void Yield(CartPlayerMigrationContext context, IReadOnlyList<CartPlayerRuntime> runtimes)
            {
                RequireCanYield(runtimes);
                Require(context.Plan.OldNetworkId == _binding.Id, "Replica yield changed its cart plan.");
                _migrationYield = context;
                Unregister();
            }

            internal void RequireYielded(CartPlayerMigrationContext context, bool retiring)
            {
                Require(OnSelectedThread() && !_quarantined && !_retired && ReferenceEquals(_migrationYield, context)
                    && Find(_binding.Cart) == null && !_policyWrite
                    && (!retiring || !Live(_binding.Entity) || !_binding.Entity.isAttached),
                    "The exact replica policy must remain yielded until its original cart retires.");
            }

            internal void Retire(CartPlayerMigrationContext context)
            {
                RequireYielded(context, true);
                _retired = true; _migrationYield = null;
            }

            internal void CancelYield(CartPlayerMigrationContext context)
            {
                RequireYielded(context, false);
                Require(_binding.Matches() && !_binding.Entity.isOwner && !CartWriterHooks.IsHeld(_binding.Cart)
                    && OccupantsFor(_binding.Cart) == null && _binding.Body.isKinematic && _binding.Cart._setToKinematic
                    && _binding.CartState.Transform.Simulate?.Pointer == _binding.Entity.transform.Pointer,
                    "Restore the exact original stock cart and replica transform policy before cancellation.");
                RequireDriver();
                Register(); _migrationYield = null;
            }

            internal bool Before(string method)
            {
                if (method == "OnDestroy") return true;
                if (_quarantined) return method == "OnDisable" || method == "OnSerialize";
                try
                {
                    Require(OnSelectedThread() && _binding.Matches() && !_binding.Entity.isOwner,
                        "The retained cart replica changed its session or attachment.");
                    if (_policyWrite) return true;
                    // The parent's ordered native action/ingress path owns dismount. Do not
                    // manufacture empty-seat settlement or remove this policy on a slot sample.
                    if (!ApplyingLocalDriverExit
                        && (_localDriverExited || _binding.CartState.ConnectedSlots.GetValueNetworkId(0).PackedValue != 0)) RequireDriver();
                    if (method == "UpdateExternalGhostConstraint")
                    {
                        if (!_localDriverExited && _driver?.LocalAction == null && _driver != null
                            && _binding.CartState.ConnectedSlots.GetValueNetworkId(0).PackedValue == _driver.SeatNetworkId)
                        {
                            _binding.Cart.GetPlayerSeatPositionAndRotation(1f, out var position, out var rotation, 0);
                            _driver.Projection.SetPositionAndRotation(position, rotation);
                        }
                        return false;
                    }
                    // SetRootTransformSynced(true) binds all five native visual channels.
                    // Do not overwrite those with local wheel physics or the retained ghost.
                    return method is not ("ManagedFixedUpdate" or "UpdateAccelerationFactor"
                        or "UpdatePower" or "UpdateOwnedGolfCartGhost" or "UpdateLocalDynamicTransforms");
                }
                catch { Quarantine(); return false; }
            }

            internal void Quarantine()
            {
                if (_retired) return;
                _quarantined = true;
                if (OnSelectedThread() && Live(_binding.Body))
                {
                    if (_migrationYield != null && !CartWriterHooks.IsHeld(_binding.Cart) && Find(_binding.Cart) == null) Register();
                    _binding.Body.isKinematic = true;
                }
            }

            internal static CartPlayerProxyAuthority Find(Component component)
            {
                if (component is null) return null;
                foreach (var value in Volatile.Read(ref _active))
                    if (value._binding.Cart.Pointer == component.Pointer) return value;
                return null;
            }

            internal static bool Policy(Component component, string method, bool requested)
            {
                var value = Find(component);
                if (value == null) return true;
                if (!OnSelectedThread()) { value.Quarantine(); return false; }
                return value._policyWrite || (requested && method == "SetToKinematic" && value.ApplyingLocalDriverExit);
            }

            internal static void After(Component component, string method, bool ran, Exception error)
            {
                var value = Find(component);
                if (value == null) return;
                if (ran && error != null) value.Quarantine();
                // Only actual destruction retires the exact receiver. Empty slots alone do
                // not prove the original native disconnect continuation has settled.
                if (method != "OnDestroy" || !ran || error != null || !OnSelectedThread()) return;
                var active = Volatile.Read(ref _active);
                int index = Array.IndexOf(active, value);
                if (index < 0) return;
                var next = new CartPlayerProxyAuthority[active.Length - 1];
                Array.Copy(active, 0, next, 0, index);
                Array.Copy(active, index + 1, next, index, next.Length - index);
                Volatile.Write(ref _active, next);
            }
        }
    }
}
