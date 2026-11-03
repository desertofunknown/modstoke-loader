// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using NativeMode = Modstoke.Sons.PlayerClientMovement.NativeMode;
using NativeModeKey = Modstoke.Sons.PlayerClientMovement.NativeModeKey;
using Proposal = Modstoke.Sons.PlayerModeTransitionProposal;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        // Retains the actual native seat authority even after its registry retirement.
        // Neither the caller nor the client supplies an exit pose or an approval flag.
        internal sealed class CartPlayerGroundExit
        {
            private readonly CartPlayerPassengerAuthority _passenger;
            private readonly CartDriverAuthority _driver;
            private readonly PlayerBridge _bridge;
            private readonly PlayerCarrier _carrier;
            private readonly PlayerGroundMovement _movement;
            private readonly Proposal _proposal;
            private readonly Rigidbody _body;
            private readonly CapsuleCollider _capsule;
            private readonly Transform _root, _locator;
            private readonly bool _crouched;
            private readonly Il2CppReferenceArray<Collider> _overlaps = new(64);
            private readonly Il2CppStructArray<RaycastHit> _support = new(32);
            private bool _pumping, _placed;

            internal bool IsComplete { get; private set; }
            internal bool IsFaulted { get; private set; }
            internal bool HasDispatched => _driver?.GroundExitDispatched ?? _passenger._exitRequested;
            internal string FailureReason { get; private set; }

            private CartPlayerGroundExit(CartPlayerPassengerAuthority passenger, CartDriverAuthority driver,
                PlayerBridge bridge, PlayerCarrier carrier,
                PlayerGroundMovement movement, Proposal proposal, Transform locator, bool crouched)
            {
                _passenger = passenger; _driver = driver; _bridge = bridge; _carrier = carrier; _movement = movement;
                _proposal = proposal; _locator = locator; _crouched = crouched;
                _body = carrier.Body; _capsule = carrier.PrimaryCollider; _root = carrier.Entity.transform;
            }

            internal static bool TryPrepare(PlayerBridge bridge,
                PlayerCarrier carrier, PlayerGroundMovement movement,
                out CartPlayerGroundExit exit, out string detail)
            {
                exit = null;
                detail = "An exact received cart exit and its active yielded ground motor are required.";
                if (!Enter()) return false;
                try
                {
                    RequireContext();
                    if (bridge == null || carrier == null || movement == null
                        || !bridge.TryPeekMovementTransitionProposal(out var proposal)) return false;
                    var passenger = proposal.Expected.Mode == NativeMode.GolfCartPassenger
                        ? CartPlayerPassengerAuthority.Find(bridge) : null;
                    var driver = proposal.Expected.Mode == NativeMode.GolfCartDriver
                        ? CartDriverAuthorityHooks.ForMigration(new Binding(RequireCartEndpoint(proposal.Expected.AnchorNetworkId)).Cart)
                        : null;
                    if (driver == null ? passenger == null || passenger._exitRequested : driver.GroundExitDispatched) return false;
                    var locator = driver != null ? driver.RequireGroundExit(bridge, proposal) : passenger.RequireGroundExit(proposal);
                    var capsule = carrier.PrimaryCollider;
                    Require(Live(capsule), "The retained player capsule is unavailable.");
                    var geometry = carrier.Profile.Geometry;
                    bool crouched = capsule.height == geometry.CrouchingHeight
                        && capsule.center.Equals(new Vector3(0, geometry.CrouchingCenterY, 0));
                    bool standing = capsule.height == geometry.StandingHeight && capsule.center.Equals(geometry.StandingCenter);
                    Require(crouched != standing, "The held capsule has an unknown or indistinguishable ground posture.");
                    var candidate = new CartPlayerGroundExit(passenger, driver, bridge, carrier, movement, proposal, locator, crouched);
                    candidate.RequireCarrier();
                    exit = candidate;
                    detail = "Exact cart receipt, native exit locator and yielded carrier retained without mutation.";
                    return true;
                }
                catch (Exception error) { detail = Failure("Cart ground exit preparation refused", error); return false; }
                finally { _busy = false; }
            }

            // Invoke only from the aggregate's ordinary selected-thread render pump, outside
            // native callbacks. False without IsFaulted is backpressure; never replay a dispatch.
            internal bool TryPump(out string detail)
            {
                detail = FailureReason ?? "Cart ground exit is awaiting its native boundary.";
                if (!OnSelectedThread() || Time.inFixedTimeStep || IsFaulted) return false;
                if (IsComplete) { detail = "Cart ground exit already completed."; return true; }
                if (_pumping) { Fail("Cart ground exit reentered its render pump."); detail = FailureReason; return false; }
                _pumping = true;
                try
                {
                    RequireContext();
                    RequireCarrier();
                    Require((_driver != null ? _driver.RequireGroundExit(_bridge, _proposal)
                        : _passenger.RequireGroundExit(_proposal)).Pointer == _locator.Pointer,
                        "The native exit locator changed during cart exit.");
                    if (!HasDispatched)
                    {
                        if (!TryReadPlacement(out _, out _, out _, out detail)) return false;
                        bool dispatched = _driver != null ? _driver.TryApplyReceivedDriverExit(out detail)
                            : TryApplyReceivedServerCartPassengerExit(_bridge, out detail);
                        if (!dispatched)
                        {
                            if (_driver?.IsQuarantined == true || _passenger?.IsQuarantined == true) Fail(detail);
                            return false;
                        }
                    }
                    Require(_driver?.IsQuarantined != true && _passenger?.IsQuarantined != true,
                        "The native cart exit is quarantined.");
                    if (!(_driver?.GroundExitSettled ?? _passenger.GroundExitSettled)) return false;
                    if (!TryReadPlacement(out var position, out var rotation, out bool grounded, out detail)) return false;
                    RequireCarrier();
                    Vector3 velocity = _body.velocity, angularVelocity = _body.angularVelocity;
                    Require(FiniteVector(velocity) && FiniteVector(angularVelocity), "The authoritative held body has invalid velocity.");

                    // Native DisconnectPlayerHook sets the locator position and upright facing;
                    // it does not assign velocity. Preserve this exact server body's values.
                    _placed = true;
                    _root.SetPositionAndRotation(position, rotation);
                    _body.position = position;
                    _body.rotation = rotation;
                    RequireCarrier();
                    NativeModeKey ground;
                    if (_driver != null)
                    {
                        Require(_driver.TryReleaseAfterDismount(out detail), detail);
                        ground = _proposal.Requested;
                    }
                    else Require(TryCompleteReceivedServerCartPassengerExit(_bridge, out ground, out detail), detail);
                    Require(ground == _proposal.Requested && _bridge.IsCommittedMovementMode(ground),
                        "Cart completion did not retain the exact received ground epoch.");
                    Require(_movement.TryResumeGroundMode(ground, velocity, angularVelocity,
                        grounded, _crouched, out detail), detail);
                    Require(!_movement.IsYieldedToNativeMode && _movement.IsActive
                        && _carrier.IsCurrentForMotor && _carrier.Body?.Pointer == _body.Pointer
                        && _bridge.IsCommittedMovementMode(ground), "The original ground motor did not retain the resumed body.");
                    IsComplete = true;
                    detail = "Native cart exit settled; server locator pose applied and the same ground motor resumed.";
                    return true;
                }
                catch (Exception error)
                {
                    detail = Failure("Cart ground exit unconfirmed", error);
                    Fail(detail);
                    return false;
                }
                finally { _pumping = false; }
            }

            private void RequireCarrier()
            {
                Require(!IsFaulted && _bridge.IsServer && _bridge.IsGameplayActive && !_bridge.IsDisposed
                    && _bridge.Peer.IsCurrent && _bridge.Peer.ServerSession != null
                    && _bridge.Plan.Id == _proposal.MigrationId && _bridge.Plan.Generation == _proposal.Generation
                    && _bridge.NetworkId == _proposal.NetworkId && _bridge.TryReadCurrentTarget(out var entity)
                    && SameEntity(entity, _carrier.Entity) && _carrier.IsCurrentForMotor
                    && _movement.IsActive && _movement.YieldedNativeMode == _proposal.Expected
                    && (_driver == null || _movement.RetainsCartDriver(_driver, _bridge, _proposal.Expected))
                    && Live(_root) && _root.parent == null && UnitScale(_root.lossyScale)
                    && Live(_body) && _carrier.Body?.Pointer == _body.Pointer
                    && _body.transform.Pointer == _root.Pointer && _body.isKinematic && !_body.detectCollisions
                    && _body.collisionDetectionMode == CollisionDetectionMode.Discrete
                    && Live(_capsule) && _carrier.PrimaryCollider?.Pointer == _capsule.Pointer
                    && _capsule.attachedRigidbody?.Pointer == _body.Pointer && !_capsule.enabled && !_capsule.isTrigger,
                    "The exit lost its exact authenticated actor, yielded motor or held authoritative body.");
                var g = _carrier.Profile.Geometry;
                var shape = _capsule.transform;
                Require(_capsule.direction == 1 && _capsule.radius == g.Radius && _capsule.contactOffset == g.ContactOffset
                    && _capsule.gameObject.layer == _carrier.Profile.CapsuleLayer && UnitScale(shape.lossyScale)
                    && shape.IsChildOf(_root)
                    && (_root.InverseTransformPoint(shape.position) - g.LocalPosition).sqrMagnitude < 0.00000001f
                    && Quaternion.Angle(Quaternion.Inverse(_root.rotation) * shape.rotation, g.LocalRotation) < 0.01f
                    && _capsule.height == (_crouched ? g.CrouchingHeight : g.StandingHeight)
                    && _capsule.center.Equals(_crouched ? new Vector3(0, g.CrouchingCenterY, 0) : g.StandingCenter),
                    "The retained ground capsule geometry or posture changed during the cart exit.");
            }

            private bool TryReadPlacement(out Vector3 position, out Quaternion rotation, out bool grounded, out string detail)
            {
                grounded = false;
                detail = "The native exit capsule is obstructed or its server collision observation is incomplete.";
                _locator.GetPositionAndRotation(out position, out var nativeRotation);
                var forward = nativeRotation * Vector3.forward;
                forward.y = 0;
                Require(FiniteVector(position) && FiniteVector(forward) && forward.sqrMagnitude > 0.000001f,
                    "The native seat exit locator has no finite upright pose.");
                rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
                var g = _carrier.Profile.Geometry;
                var center = _crouched ? new Vector3(0, g.CrouchingCenterY, 0) : g.StandingCenter;
                float height = _crouched ? g.CrouchingHeight : g.StandingHeight;
                Vector3 worldCenter = position + rotation * (g.LocalPosition + g.LocalRotation * center);
                Vector3 extent = rotation * g.LocalRotation * Vector3.up * (height * 0.5f - g.Radius);
                float radius = g.Radius - g.ContactOffset;
                Require(radius > 0 && g.ContactOffset > 0 && height >= 2 * g.Radius,
                    "The retained ground capsule has invalid clearance geometry.");
                int mask = 0;
                for (int layer = 0; layer < 32; layer++)
                    if (!Physics.GetIgnoreLayerCollision(_capsule.gameObject.layer, layer)) mask |= 1 << layer;
                Require(mask != 0, "The carrier has no physical collision layers.");
                if (!Physics.autoSyncTransforms) Physics.SyncTransforms();
                var scene = Physics.defaultPhysicsScene;
                Require(scene.IsValid() && PhysicsSceneExtensions.GetPhysicsScene(_root.gameObject.scene) == scene,
                    "The actor's authoritative physics scene is unavailable.");
                try
                {
                    int count = scene.OverlapCapsule(worldCenter - extent, worldCenter + extent, radius,
                        _overlaps, mask, QueryTriggerInteraction.Ignore);
                    if (count < 0 || count >= _overlaps.Length) return false;
                    for (int i = 0; i < count; i++)
                        if (!Live(_overlaps[i]) || _overlaps[i].Pointer != _capsule.Pointer) return false;
                    // Seed only from a fresh contact-offset-sized support observation. Native
                    // collision callbacks still have to complete before the motor accepts input.
                    int hits = scene.SphereCast(worldCenter - extent, radius, Vector3.down, _support,
                        g.ContactOffset * 2, mask, QueryTriggerInteraction.Ignore);
                    if (hits < 0 || hits >= _support.Length) return false;
                    for (int i = 0; i < hits; i++)
                    {
                        var hit = _support[i];
                        if (!Live(hit.collider) || !FiniteVector(hit.point) || !FiniteVector(hit.normal)
                            || !float.IsFinite(hit.distance) || hit.distance < 0) return false;
                        if (hit.collider.Pointer != _capsule.Pointer && hit.normal.y > 0) grounded = true;
                    }
                    detail = "Native seat exit pose has server capsule clearance and a fresh support observation.";
                    return true;
                }
                finally { for (int i = 0; i < _overlaps.Length; i++) _overlaps[i] = null; }
            }

            internal void Fail(string detail)
            {
                if (IsFaulted) return;
                IsComplete = false;
                IsFaulted = true;
                FailureReason = detail;
                if (!OnSelectedThread()) return;
                try
                {
                    if (HasDispatched || _placed)
                    {
                        _passenger?.Quarantine();
                        _driver?.Quarantine(detail);
                    }
                }
                catch (Exception error) { FailureReason += " Cart hold unconfirmed (" + error.GetType().Name + ")."; }
                try { if (_placed) _movement.Dispose(); }
                catch (Exception error) { FailureReason += " Ground motor hold unconfirmed (" + error.GetType().Name + ")."; }
            }

            private static bool FiniteVector(Vector3 value)
                => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
            private static bool UnitScale(Vector3 value)
                => FiniteVector(value) && MathF.Abs(value.x - 1) < 0.0001f
                    && MathF.Abs(value.y - 1) < 0.0001f && MathF.Abs(value.z - 1) < 0.0001f;
        }

        private sealed partial class CartPlayerPassengerAuthority
        {
            internal bool GroundExitSettled => _exitDispatchCompleted && _exitComplete;

            internal Transform RequireGroundExit(Proposal proposal)
            {
                if (_exitRequested) RequireExiting();
                else RequireIdentity(true);
                Require(!_inside && _bridge.TryPeekMovementTransitionProposal(out var current) && current == proposal
                    && proposal.Expected == _key && _key.Mode == NativeMode.GolfCartPassenger
                    && _key.TransitionSequence < uint.MaxValue
                    && proposal.Requested == NativeModeKey.Ground(_key.TransitionSequence + 1)
                    && proposal.MigrationId == _bridge.Plan.Id && proposal.Generation == _bridge.Plan.Generation
                    && proposal.NetworkId == _player.PlayerNetworkId && _bridge.Peer.ServerSession != null
                    && (!_exitRequested || proposal == _exitProposal)
                    && !CartWriterHooks.IsHeld(_binding.Cart) && OccupantsFor(_binding.Cart) == null,
                    "The received passenger exit, cart lifetime or movement receipt changed.");
                var locators = _binding.Cart._disconnectPlayerLocators;
                Require(locators != null && !locators.WasCollected && _player.Slot < locators.Length
                    && Live(locators[_player.Slot]), "The exact native passenger disconnect locator is unavailable.");
                if (GroundExitSettled) RequireEmptySeat();
                return locators[_player.Slot];
            }
        }
    }
}
