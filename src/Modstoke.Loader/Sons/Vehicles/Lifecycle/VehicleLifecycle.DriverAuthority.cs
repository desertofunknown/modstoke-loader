// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Math = System.Math;
using System.Reflection;
using System.Threading;
using Bolt;
using HarmonyLib;
using global::Sons.Gameplay;
using UnityEngine;
using NativeInput = Modstoke.Sons.PlayerClientMovement.NativeInput;
using NativeMode = Modstoke.Sons.PlayerClientMovement.NativeMode;
using NativeModeKey = Modstoke.Sons.PlayerClientMovement.NativeModeKey;
using NativeState = Modstoke.Sons.PlayerClientMovement.NativeState;
using ExitRoutine = global::Sons.Gameplay.GolfCartController._DelayedFixPlayerProxyRotation_d__270;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        /// <summary>
        /// Acquires native physical-cart simulation for one exact remote driver on its server.
        /// The caller must authenticate the peer/driver association and finish the original-rig,
        /// client replica and event-routing barriers first. This does not enable cart migration.
        /// A returned lease on failure is quarantined and must not be retried or discarded.
        /// </summary>
        public static bool TryAcquireServerCartDriverAuthority(BoltEntity cart, BoltEntity driver,
            ControlPeer authenticatedDriver, out CartDriverAuthority authority, out string detail)
        {
            authority = null;
            detail = "Server cart authority requires the selected game thread.";
            if (!Enter()) return false;
            try
            {
                RequireContext();
                Require(BoltNetwork.isServer && authenticatedDriver?.ServerSession != null && authenticatedDriver.IsCurrent,
                    "The current authenticated remote-driver connection is required.");
                var binding = new Binding(cart);
                Require(binding.Cart != null && cart.isOwner && !cart.isFrozen && binding.Cart.enabled,
                    "The server must own an active physical cart.");
                CartWriterHooks.RequireInitialized(binding.Cart);
                Require(OccupantsFor(binding.Cart) == null && !CartWriterHooks.IsHeld(binding.Cart),
                    "The cart and occupant migration barrier must finish before motor acquisition.");
                RequireSettledServerCart(binding);
                var pending = new CartDriverAuthority(binding, driver, authenticatedDriver);
                pending.RequireIdentity();
                CartDriverAuthorityHooks.Add(pending);
                authority = pending;
                pending.Start();
                detail = "Exact server cart motor acquired with neutral input; original player controls and camera are not changed.";
                return true;
            }
            catch (Exception error)
            {
                authority?.Quarantine(error.Message);
                detail = Failure("Server cart authority rejected or held unconfirmed", error);
                return false;
            }
            finally { _busy = false; }
        }

        private static void RequireSettledServerCart(Binding binding)
            => Require(binding.Cart._rigidBodyVelocity == Vector3.zero
                && binding.Cart._rigidbodyPreviousVelocity == Vector3.zero
                && (binding.Body.isKinematic || (binding.Body.IsSleeping()
                    && binding.Body.velocity == Vector3.zero && binding.Body.angularVelocity == Vector3.zero)),
                "Acquire server authority only at the settled cart barrier.");

        // Migration callbacks already own Enter(). Preparation is read-only and retains an
        // exact candidate; activation is legal only after both stock cart gates are released.
        internal static bool TryPrepareServerCartDriverAuthority(BoltEntity cart, BoltEntity driver,
            ControlPeer peer, out CartDriverAuthority authority, out string detail)
        {
            authority = null;
            try
            {
                Require(_busy && OnSelectedThread() && !_reentered && !Time.inFixedTimeStep,
                    "Prepared cart authority requires the enclosing vehicle operation on its selected thread.");
                RequireContext();
                Require(BoltNetwork.isServer && peer?.ServerSession != null && peer.IsCurrent,
                    "Prepared cart authority requires the authenticated remote driver.");
                var binding = new Binding(cart);
                Require(binding.Cart != null && cart.isOwner && CartWriterHooks.IsHeld(binding.Cart)
                    && OccupantsFor(binding.Cart) != null && !CartDriverAuthorityHooks.IsAcquired(binding.Cart),
                    "Prepare only the exact held, occupied server-owned replacement cart.");
                CartWriterHooks.RequireInitialized(binding.Cart);
                RequireSettledServerCart(binding);
                var pending = new CartDriverAuthority(binding, driver, peer);
                pending.RequireIdentity();
                authority = pending;
                detail = "Exact server cart motor prepared without releasing writers or changing native policy.";
                return true;
            }
            catch (Exception error) { detail = Failure("Server cart preparation rejected", error); return false; }
        }

        internal static bool TryActivatePreparedServerCartDriverAuthority(CartDriverAuthority authority, out string detail)
        {
            bool activating = false;
            try
            {
                Require(_busy && OnSelectedThread() && !_reentered && authority != null && !Time.inFixedTimeStep,
                    "Prepared activation requires the enclosing vehicle release operation.");
                authority.RequirePrepared();
                authority.RequirePreparedInput();
                Require(OccupantsFor(authority._binding.Cart) == null && !CartWriterHooks.IsHeld(authority._binding.Cart),
                    "Release and unregister the exact stock cart/occupant holds before motor activation.");
                CartDriverAuthorityHooks.Add(authority);
                activating = true;
                authority.Start();
                authority.CommitPreparedInput();
                detail = "Prepared server cart motor activated synchronously after stock writer release.";
                return true;
            }
            catch (Exception error)
            {
                if (activating) authority.Quarantine(error.Message);
                detail = Failure("Prepared cart activation rejected or unconfirmed", error); return false;
            }
        }

        public sealed partial class CartDriverAuthority
        {
            internal readonly Binding _binding;
            private readonly BoltEntity _driver, _ghost;
            private readonly IPlayerState _driverState;
            private readonly IGolfCartState _ghostState;
            private readonly Transform _driverTransform;
            private readonly ControlPeer _peer;
            private readonly PlayerHandle _playerBinding;
            private readonly ulong _driverId, _ghostId;
            private readonly IntPtr _socket;
            private bool _started, _released, _motorWrite, _late, _power, _visual, _dismounting;
            internal bool _policyWrite, _fixed, _audio;
            private bool _powerLocal;
            private GolfCartGhostController _visualGhost;
            private uint _sequence;
            private bool _hasInput;
            private float _receivedAt, _throttle, _steering, _steeringBefore, _steeringVelocityBefore;
            private bool _boost, _brake = true;
            private int _lateFrame = -1;
            private float _fixedTime = float.NaN;
            private PlayerBridge _bridge;
            private NativeModeKey _mode;
            private NativeInput _selectedInput;
            private NativeState _pendingState;
            private uint _simulatedSequence;
            private bool _stepCompleted, _exitRequested, _exitObserved, _exitComplete, _exitDispatch;
            private GolfCartConnectSlotEvent _exitEvent;
            private bool _exitDispatchEntered;
            internal bool _exitDispatchCompleted;
            private ExitRoutine _exitRoutine;
            public ulong CartNetworkId => _binding.Id;
            public ulong DriverNetworkId => _driverId;
            public ulong DriverGhostNetworkId => _ghostId;
            public string FailureDetail { get; private set; }
            public bool IsQuarantined => FailureDetail != null;

            internal void RequirePrepared()
            {
                Require(!_started && !_released && FailureDetail == null && _binding.Matches(),
                    "Prepared cart motor identity changed or activation was already attempted.");
                RequireIdentity();
                RequireSettledServerCart(_binding);
            }

            internal CartDriverAuthority(Binding binding, BoltEntity driver, ControlPeer peer)
            {
                _binding = binding; _driver = driver; _peer = peer;
                Require(Live(driver) && driver.isAttached && driver.TryFindState<IPlayerState>(out _driverState),
                    "The exact attached driver player state is required.");
                _driverId = driver.networkId.PackedValue;
                Require(ServerWorld.TryCapturePlayerBinding(peer.ServerSession, out _playerBinding, out _)
                    && _playerBinding.NetworkId == _driverId,
                    "The authenticated connection does not own this exact driver player attachment.");
                _ghost = binding.Cart._externalGolfCartGhostEntity;
                Require(Live(_ghost) && _ghost.isAttached && _ghost.Pointer != driver.Pointer
                    && _ghost.TryFindState<IGolfCartState>(out _ghostState),
                    "A settled remote-driver ghost is required; host-local driving uses its native motor.");
                _ghostId = _ghost.networkId.PackedValue;
                _socket = BoltCore._udpSocket.Pointer;
                _driverTransform = binding.Cart._externalEntityTransforms[0];
                Require(Live(_driverTransform) && (_driverTransform.Pointer == driver.transform.Pointer
                    || _driverTransform.IsChildOf(driver.transform)),
                    "The native driver projection must target the exact retained player hierarchy.");
                Require(binding.Cart._ownedGolfCartGhostEntity == null && binding.Cart._localPlayerDriverAction == null
                    && !binding.Cart._isLocalConnectedSlots[0] && binding.Cart._externalDriverPlayerInitialized
                    && _ghostState.Velocity == Vector3.zero
                    && binding.Cart._driverConnectIkCoroutine == null && binding.Cart._pendingDriverIkTargetsController == null
                    && Live(binding.Cart._externalDriverIkTargetsController)
                    && !binding.Cart._externalDriverIkTargetsController.IsSmoothTransitioning()
                    && binding.Cart._externalDriverPlayerState?.Pointer == _driverState.Pointer,
                    "The server driver cache is not the exact settled remote player.");
            }

            private void RequireCartIdentity()
            {
                var cart = _binding.Cart;
                Require(OnSelectedThread() && BoltNetwork.isServer && BoltCore._udpSocket?.Pointer == _socket
                    && Live(_binding.Entity) && _binding.Entity.isAttached && _binding.Entity.isOwner
                    && _binding.Entity.networkId.PackedValue == _binding.Id
                    && _binding.Entity._entity?.Serializer?.Pointer == _binding.CartState.Pointer
                    && Live(cart) && cart.entity?.Pointer == _binding.Entity.Pointer
                    && Live(_binding.Body) && cart._rigidbody?.Pointer == _binding.Body.Pointer,
                    "The server cart's exact attachment, body or session changed.");
            }

            internal void RequireIdentity()
            {
                RequireCartIdentity();
                var cart = _binding.Cart;
                Require(Live(_driver) && _driver.isAttached && _driver.networkId.PackedValue == _driverId
                    && _driver._entity?.Serializer?.Pointer == _driverState.Pointer
                    && Live(_ghost) && _ghost.isAttached && _ghost.networkId.PackedValue == _ghostId
                    && _ghost._entity?.Serializer?.Pointer == _ghostState.Pointer
                    && _binding.CartState.ConnectedSlots.GetValueNetworkId(0).PackedValue == _ghostId
                    && _ghostState.ConnectedSlots.GetValueNetworkId(0).PackedValue == _driverId
                    && cart._externalGolfCartGhostEntity?.Pointer == _ghost.Pointer
                    && cart._externalDriverPlayerState?.Pointer == _driverState.Pointer
                    && Live(_driverTransform) && cart._externalEntityTransforms[0]?.Pointer == _driverTransform.Pointer
                    && _peer.IsCurrent, "Server cart, driver, ghost or authenticated connection lifetime changed.");
            }

            /// <summary>Bind after the reliable lifecycle commits this exact server driver key; peer settlement remains ordered.</summary>
            public bool TryBindInput(PlayerBridge bridge, NativeModeKey committedSeat, out string detail)
            {
                detail = "An active exact-driver bridge and committed driver seat are required.";
                if (!Enter()) return false;
                bool binding = false;
                try
                {
                    Require(_started && !_released && _cartMigrationYield == null && !_dismounting && FailureDetail == null && _bridge == null, detail);
                    RequireInputBridge(bridge, committedSeat);
                    Require(bridge.IsCommittedMovementMode(committedSeat), detail);
                    RequireIdentity();
                    Require(ServerWorld.TryValidatePlayerBinding(_playerBinding, out _),
                        "The authenticated player attachment changed before driver input binding.");
                    binding = true; _bridge = bridge; _mode = committedSeat;
                    _hasInput = false; ApplyInput();
                    detail = "Committed driver input bound; native cart fixed/seat callbacks now consume and acknowledge it.";
                    return true;
                }
                catch (Exception error)
                {
                    if (binding) Quarantine(error.Message);
                    detail = Failure("Cart input binding rejected", error); return false;
                }
                finally { _busy = false; }
            }

            /// <summary>
            /// Apply the reliable lifecycle's authenticated driver-exit event once. Keep the
            /// movement key committed until TryReleaseAfterDismount succeeds. A false result
            /// before dispatch is retryable; a quarantined lease after dispatch is not.
            /// </summary>
            public bool TryApplyDriverExit(ControlPeer peer, NativeModeKey committedSeat,
                GolfCartConnectSlotEvent request, out string detail)
                => TryApplyDriverExitCore(peer, committedSeat, request, false, out detail);

            /// <summary>Dispatch the exact received exit using a scoped native event, without sending or replaying it.</summary>
            public bool TryApplyReceivedDriverExit(out string detail)
                => TryApplyDriverExitCore(_peer, _mode, null, true, out detail);

            private bool TryApplyDriverExitCore(ControlPeer peer, NativeModeKey committedSeat,
                GolfCartConnectSlotEvent request, bool createNativeEvent, out string detail)
            {
                detail = "The exact driver exit must wait for the selected native step to finish.";
                if (!Enter()) return false;
                bool dispatched = false, ownEvent = false;
                try
                {
                    RequireContext();
                    CartPassengerExitHooks.Arm();
                    Require(_started && !_released && _cartMigrationYield == null && !_dismounting && !_exitRequested && FailureDetail == null
                        && !_fixed && !_late && !_power && !_visual && !_audio && !Time.inFixedTimeStep
                        && _selectedInput.Input.Sequence == 0 && _bridge != null
                        && _pendingState.Motion.InputSequence == 0
                        && ReferenceEquals(peer, _peer) && committedSeat == _mode
                        && _bridge.IsCommittedMovementMode(_mode), detail);
                    RequireIdentity();
                    RequireReceivedExit();
                    if (createNativeEvent)
                    {
                        request = GolfCartConnectSlotEvent.Create(_binding.Entity);
                        ownEvent = true;
                        request.SlotIndex = 0; request.OccupiedEntity = _ghost; request.Disconnect = true;
                    }
                    Require(request != null && !request.WasCollected && request.Disconnect && request.SlotIndex == 0
                        && request.GetOccupiedEntityNetworkIdValue().PackedValue == _ghostId
                        && request.OccupiedEntity?.Pointer == _ghost.Pointer,
                        "A physical driver exit names the exact driver ghost, never the player entity.");
                    var sender = request.RaisedBy;
                    Require(sender == null || sender.Pointer == ServerSessions.ResolveCurrent(_peer.ServerSession)?.Pointer,
                        "The native exit event sender is not the exact authenticated driver session.");
                    CartPlayerCallbackSettlement.RequireSettled(_binding.Cart);
                    dispatched = true;
                    _exitRequested = true; _hasInput = false; ApplyInput();
                    _exitDispatch = true; _exitEvent = request;
                    try { _binding.Cart.DisconnectSlot(request); }
                    finally { _exitDispatch = false; _exitEvent = null; }
                    Require(FailureDetail == null && !_reentered && _exitDispatchCompleted
                        && _binding.CartState.ConnectedSlots.GetValueNetworkId(0).PackedValue == 0,
                        "Native driver exit did not clear its exact raw seat.");
                    if (ownEvent) { ownEvent = false; request.FreeStorage(); }
                    detail = "Native driver exit applied once; retain the lease until its exact proxy continuation completes.";
                    return true;
                }
                catch (Exception error)
                {
                    if (dispatched) Quarantine(error.Message);
                    detail = Failure("Driver exit rejected or unconfirmed", error); return false;
                }
                finally
                {
                    try { if (ownEvent) request.FreeStorage(); }
                    catch (Exception error) { Quarantine("Scoped native driver exit cleanup failed: " + error.Message); }
                    _busy = false;
                }
            }

            internal bool AcceptExitDispatch(GolfCartConnectSlotEvent request)
            {
                if (!OnSelectedThread() || FailureDetail != null || !_exitRequested || !_exitDispatch
                    || _exitDispatchEntered || _exitEvent == null || request?.Pointer != _exitEvent.Pointer
                    || request.WasCollected || !request.Disconnect || request.SlotIndex != 0
                    || request.GetOccupiedEntityNetworkIdValue().PackedValue != _ghostId
                    || !SameEntity(request.OccupiedEntity, _ghost)) return false;
                _exitDispatchEntered = true;
                return true;
            }

            internal void Start()
            {
                _policyWrite = true;
                try
                {
                    _binding.Cart.SetRootTransformSynced(true);
                    _binding.Cart.SetToKinematic(false);
                    Require(!_reentered && !_binding.Body.isKinematic && !_binding.Cart._setToKinematic
                        && _binding.CartState.Transform.Simulate?.Pointer == _binding.Entity.transform.Pointer,
                        "Native cart physics/transform authority did not bind to the server body.");
                    _started = true;
                    ApplyInput();
                }
                finally { _policyWrite = false; }
            }

            /// <summary>Only the peer supplied at acquisition may submit validated continuous driver intent.</summary>
            public bool TrySetInput(ControlPeer peer, uint sequence, float throttle, float steering,
                bool boost, bool handBrake, out string detail)
            {
                detail = "Cart input requires its exact live driver lease and increasing sequence.";
                if (!Enter()) return false;
                try
                {
                    Require(_started && !_released && _cartMigrationYield == null && !_dismounting && !_exitRequested && FailureDetail == null && _bridge == null && ReferenceEquals(peer, _peer)
                        && sequence != 0 && (!_hasInput || unchecked((int)(sequence - _sequence)) > 0)
                        && float.IsFinite(throttle) && Math.Abs(throttle) <= 1f
                        && float.IsFinite(steering) && Math.Abs(steering) <= 1f, detail);
                    RequireIdentity();
                    Require(ServerWorld.TryValidatePlayerBinding(_playerBinding, out _),
                        "The authenticated player attachment no longer owns this driver lease.");
                    _sequence = sequence; _hasInput = true; _receivedAt = Time.realtimeSinceStartup;
                    _throttle = throttle; _steering = steering; _boost = boost; _brake = handBrake;
                    detail = "Exact driver input accepted for the native cart motor.";
                    return true;
                }
                catch (Exception error) { detail = Failure("Cart input rejected", error); return false; }
                finally { _busy = false; }
            }

            // Silence is a neutral/braked continuous input, never inferred permission to end a lease.
            private void ApplyInput()
            {
                bool fresh = _hasInput && Time.realtimeSinceStartup - _receivedAt <= 0.5f;
                var cart = _binding.Cart;
                cart._accelerationFactorGoal = fresh ? _throttle : 0f;
                cart._steeringFactorGoal = fresh ? _steering : 0f;
                cart._boostInput = fresh && _boost;
                cart._handBrakeInput = !fresh || _brake;
            }

            private void SelectBridgeInput()
            {
                if (_bridge == null) return;
                if (_exitRequested) { _hasInput = false; return; }
                Require(_bridge.IsCommittedMovementMode(_mode),
                    "The committed cart mode changed before the native driver lifecycle ended.");
                // Never select over a step awaiting its original seat-pose boundary.
                if (_selectedInput.Input.Sequence != 0) return;
                _hasInput = false;
                if (!_bridge.TryReadCurrentNativeInput(out var input)) return;
                Require(input.Key == _mode && input.Input.NetworkId == _driverId,
                    "Native cart input belongs to a different driver, seat or transition.");
                _sequence = input.Input.Sequence; _receivedAt = Time.realtimeSinceStartup;
                _throttle = input.Input.Movement.y; _steering = input.Input.Movement.x;
                _boost = input.CartBoost; _brake = input.CartHandBrake; _hasInput = true;
                if (input.Input.Sequence > _simulatedSequence) _selectedInput = input;
            }

            private void CompleteSeatStep()
            {
                if (_bridge == null) return;
                if (!_stepCompleted || _selectedInput.Input.Sequence == 0) { PublishPendingState(); return; }
                var input = _selectedInput;
                RequireIdentity();
                Require(_bridge.TryCommitSimulatedNativeInput(_mode, input.Input.Sequence),
                    "The completed native cart step could not commit its selected input.");
                _simulatedSequence = input.Input.Sequence; _selectedInput = default; _stepCompleted = false;
                // A newer commit supersedes any coalesced older acknowledgement.
                _pendingState = default;
                if (!input.SeatSettled) return;
                var root = _binding.Entity.transform;
                uint sequence = _bridge.LastSentStateSequence;
                Require(sequence != uint.MaxValue, "Cart state publication sequence is exhausted.");
                var motion = new PlayerMotionState(input.Input.MigrationId, input.Input.Generation,
                    _driverId, sequence + 1, input.Input.Sequence,
                    root.InverseTransformPoint(_driverTransform.position),
                    Quaternion.Inverse(root.rotation) * _driverTransform.rotation, Vector3.zero,
                    PlayerMotionFlags.None) { MovementTransitionSequence = _mode.TransitionSequence };
                _pendingState = new NativeState(_mode, motion);
                PublishPendingState();
            }

            private void PublishPendingState()
            {
                if (_pendingState.Motion.InputSequence == 0) return;
                var result = _bridge.SendNativeState(_pendingState);
                Require(result == DatagramSendResult.Queued || result == DatagramSendResult.WindowFull,
                    "Native cart seat publication failed: " + result);
                if (result == DatagramSendResult.Queued) _pendingState = default;
            }

            internal bool Before(string method)
            {
                if (!OnSelectedThread()) { Quarantine("A server cart callback left the selected game thread."); return false; }
                if (method == "OnDestroy") return true;
                if (FailureDetail != null) return method == "OnDisable" || method == "OnSerialize";
                if (_policyWrite || _motorWrite) return true;
                try
                {
                    Require(_bridge == null || _exitDispatch
                        || (method == "DisconnectSlot" && IsApplyingReceivedPassengerExit(_binding.Cart))
                        || (method != "ConnectSlot" && method != "DisconnectSlot"),
                        "An unrouted native slot mutation crossed the ordered driver authority lease.");
                    if (_started && Live(_binding.Entity) && _binding.Entity.isAttached
                        && _binding.CartState.ConnectedSlots.GetValueNetworkId(0).PackedValue == 0)
                    {
                        RequireCartIdentity();
                        Require(_bridge == null || _exitRequested,
                            "The bound driver seat emptied outside its received, ordered exit proposal.");
                        Require(_selectedInput.Input.Sequence == 0,
                            "Driver exit crossed an unfinished native input step; retain the lease for lifecycle recovery.");
                        _dismounting = true; _hasInput = false; ApplyInput();
                        _binding.Body.isKinematic = true;
                        // The upstream lifecycle owns the dismount. Let its empty-slot callback
                        // finish clearing native caches before the caller releases this lease.
                        return method is "OnConnectedSlotChanged" or "RefreshConnectedSlots"
                            or "UnconstrainToGhost" or "UnconstrainPassenger" or "DisconnectSlot"
                            or "DisconnectDriverIkChains"
                            or "LastUpdate" or "OnDisable" or "OnSerialize";
                    }
                    Require(!_dismounting, "A new driver arrived before the prior driver lease was released.");
                    RequireIdentity();
                    switch (method)
                    {
                        case "UpdateExternalGhostConstraint":
                            // Retain the native seat-pose projection, not the remote ghost's cart
                            // transform, velocity or power. Acquisition admits settled seats only.
                            _binding.Cart.GetPlayerSeatPositionAndRotation(1f, out var position, out var rotation, 0);
                            _driverTransform.SetPositionAndRotation(position, rotation);
                            CompleteSeatStep();
                            return false;
                        case "OnDeactivated": return false;
                        case "UpdateAudio":
                            Require(!_audio, "Reentrant cart audio update.");
                            _audio = true;
                            return true;
                        case "ManagedFixedUpdate":
                            Require(!_fixed, "Reentrant cart fixed update.");
                            if (_fixedTime == Time.fixedTime) return false;
                            _fixedTime = Time.fixedTime; _fixed = true;
                            ApplyInput();
                            return true;
                        case "ManagedLateUpdate":
                            Require(!_late, "Reentrant cart late update.");
                            if (_lateFrame == Time.frameCount) return false;
                            _lateFrame = Time.frameCount; _late = true;
                            SelectBridgeInput();
                            ApplyInput();
                            _steeringBefore = _binding.Cart._steeringFactor;
                            _steeringVelocityBefore = _binding.Cart._steeringFactorVel;
                            return true;
                        case "UpdateAccelerationFactor":
                            if (!_late) return false;
                            RunMotor(true);
                            return false;
                        case "UpdateLocalDynamicTransforms":
                            if (_late)
                            {
                                // Native ManagedLateUpdate inlines a local-only steering update.
                                // The qualified leaf recomputes both factor and target wheel angle
                                // from the pre-call state before native wheel visuals.
                                _binding.Cart._steeringFactor = _steeringBefore;
                                _binding.Cart._steeringFactorVel = _steeringVelocityBefore;
                                ApplyInput();
                                RunMotor(false);
                            }
                            Require(!_visual, "Reentrant native wheel presentation.");
                            _visual = true; _visualGhost = _binding.Cart._externalGolfCartGhostController;
                            _binding.Cart._externalGolfCartGhostController = null;
                            return true;
                        case "UpdatePower":
                            Require(!_power, "Reentrant cart power update.");
                            _power = true; _powerLocal = _binding.Cart._isLocalConnectedSlots[0];
                            _binding.Cart._isLocalConnectedSlots[0] = true;
                            return true;
                        default: return true;
                    }
                }
                catch (Exception error) { Quarantine(error.Message); return false; }
            }

            private void RunMotor(bool acceleration)
            {
                var cart = _binding.Cart;
                bool local = cart._isLocalConnectedSlots[0];
                _motorWrite = true;
                try
                {
                    // These qualified leaf motor bodies read no LocalPlayer/input/camera objects.
                    // The scoped bit selects their native smoothing and braking branch only.
                    cart._isLocalConnectedSlots[0] = true;
                    if (acceleration) cart.UpdateAccelerationFactor(); else cart.UpdateSteeringFactor();
                }
                finally { cart._isLocalConnectedSlots[0] = local; _motorWrite = false; }
            }

            internal void After(string method, bool ran, Exception error)
            {
                if (method == "UpdateAudio") _audio = false;
                if (method == "ManagedFixedUpdate" && _fixed)
                {
                    _fixed = false;
                    if (ran && error == null && FailureDetail == null && !_dismounting)
                        _stepCompleted = _selectedInput.Input.Sequence != 0;
                }
                if (method == "ManagedLateUpdate")
                {
                    _late = false;
                    if (ran && error == null && FailureDetail == null && !_dismounting)
                    {
                        try
                        {
                            RequireIdentity();
                            var velocity = _binding.Body.velocity;
                            Require(float.IsFinite(velocity.x) && float.IsFinite(velocity.y) && float.IsFinite(velocity.z),
                                "The server cart body produced an invalid velocity.");
                            _binding.CartState.Velocity = velocity;
                        }
                        catch (Exception failure) { Quarantine(failure.Message); }
                    }
                }
                if (method == "UpdatePower" && _power)
                { _binding.Cart._isLocalConnectedSlots[0] = _powerLocal; _power = false; }
                if (method == "UpdateLocalDynamicTransforms" && _visual)
                { _binding.Cart._externalGolfCartGhostController = _visualGhost; _visualGhost = null; _visual = false; }
                if (ran && error != null) Quarantine("Native cart " + method + " failed: " + error.Message);
                if (method == "OnDestroy" && ran) { _released = true; CartDriverAuthorityHooks.Remove(this); }
            }

            internal void Quarantine(string detail)
            {
                FailureDetail ??= detail;
                if (!OnSelectedThread() || !Live(_binding.Body) || !Live(_binding.Cart)) return;
                // Acquisition can fail after yielding the old motor but before the stock
                // cart hold publishes. Restore its terminal policy in that exact gap.
                if (_cartMigrationYield != null && !CartWriterHooks.IsHeld(_binding.Cart)
                    && CartDriverAuthorityHooks.ForMigration(_binding.Cart) == null)
                    CartDriverAuthorityHooks.Add(this);
                _binding.Body.isKinematic = true;
                _binding.Cart._accelerationFactorGoal = 0f;
                _binding.Cart._steeringFactorGoal = 0f;
                _binding.Cart._boostInput = false;
                _binding.Cart._handBrakeInput = true;
            }

            internal void ObserveExit(Transform target, Il2CppSystem.Collections.IEnumerator routine)
            {
                try
                {
                    Require(OnSelectedThread() && _dismounting && !_exitObserved && Live(target)
                        && target.Pointer == _driverTransform.Pointer, "Unexpected or duplicate driver exit continuation.");
                    var exact = routine?.TryCast<ExitRoutine>();
                    Require(exact != null && exact.__4__this?.Pointer == _binding.Cart.Pointer
                        && exact.targetTransform?.Pointer == target.Pointer,
                        "Native driver exit did not retain its exact cart and player transform.");
                    _exitRoutine = exact; _exitObserved = true;
                }
                catch (Exception error) { Quarantine(error.Message); }
            }

            internal bool BeforeExit(ExitRoutine routine)
            {
                try
                {
                    RequireCartIdentity();
                    Require(OnSelectedThread() && FailureDetail == null && _dismounting && !_released
                        && _exitObserved && !_exitComplete && routine.Pointer == _exitRoutine.Pointer
                        && _binding.CartState.ConnectedSlots.GetValueNetworkId(0).PackedValue == 0
                        && Live(_driverTransform) && routine.targetTransform?.Pointer == _driverTransform.Pointer,
                        "Native driver exit continuation lost its exact empty seat or retained player.");
                    return true;
                }
                catch (Exception error) { Quarantine(error.Message); return false; }
            }

            internal void AfterExit(bool ran, bool more, Exception error)
            {
                if (error != null) Quarantine("Native driver exit continuation failed: " + error.Message);
                else if (ran && !more) _exitComplete = true;
            }

            /// <summary>Release only after the authenticated lifecycle has completed a native dismount.</summary>
            public bool TryReleaseAfterDismount(out string detail)
            {
                detail = "Cart authority remains held until the exact driver seat is empty.";
                if (!Enter()) return false;
                bool releasing = false;
                try
                {
                    RequireContext();
                    RequireEmptyDriverSeat();
                    RequireExitProposalCurrent();
                    releasing = true; _policyWrite = true;
                    try
                    {
                        _hasInput = false; ApplyInput();
                        _binding.Cart.SetRootTransformSynced(true);
                        _binding.Cart.SetToKinematic(false);
                    }
                    finally { _policyWrite = false; }
                    Require(FailureDetail == null && !_reentered && !_binding.Body.isKinematic,
                        "Native empty-cart release failed or reentered the vehicle operation.");
                    CommitReceivedExit();
                    CartDriverAuthorityHooks.Remove(this); _released = true;
                    detail = "Empty cart returned to its native owner lifecycle.";
                    return true;
                }
                catch (Exception error)
                {
                    if (releasing) Quarantine(error.Message);
                    detail = Failure("Cart authority release rejected", error); return false;
                }
                finally { _busy = false; }
            }

            internal bool GroundExitDispatched => _exitRequested;
            internal bool RequiresOrderedExit => _bridge != null;
            internal bool GroundExitSettled => (!RequiresOrderedExit || _exitDispatchCompleted) && _exitObserved && _exitComplete;

            private void RequireEmptyDriverSeat()
            {
                Require(!_released && FailureDetail == null && !_fixed && !_late && !_power && !_visual && !_audio
                    && _binding.Matches() && _dismounting && GroundExitSettled
                    && _selectedInput.Input.Sequence == 0 && _pendingState.Motion.InputSequence == 0
                    && _binding.CartState.ConnectedSlots.GetValueNetworkId(0).PackedValue == 0
                    && _binding.Cart._externalGolfCartGhostEntity == null
                    && _binding.Cart._externalGolfCartGhostController == null
                    && _binding.Cart._externalDriverPlayerState == null && !_binding.Cart._externalDriverPlayerInitialized
                    && _binding.Cart._externalEntityTransforms[0] == null
                    && _binding.Cart._externalDriverIkTargetsController == null
                    && _binding.Cart._driverConnectIkCoroutine == null && _binding.Cart._pendingDriverIkTargetsController == null
                    && _binding.Cart._localPlayerDriverAction == null && !_binding.Cart._isLocalConnectedSlots[0],
                    "The original driver exit continuation, native seat or selected input has not settled.");
            }

            internal Transform RequireGroundExit(PlayerBridge bridge,
                PlayerModeTransitionProposal proposal)
            {
                RequireCartIdentity();
                Require(_started && !_released && FailureDetail == null && _cartMigrationYield == null
                    && ReferenceEquals(_bridge, bridge) && _peer.IsCurrent && bridge.IsGameplayActive
                    && bridge.TryReadCurrentTarget(out var driver) && SameEntity(driver, _driver)
                    && bridge.TryPeekMovementTransitionProposal(out var current) && current == proposal
                    && proposal.Expected == _mode && _mode.Mode == NativeMode.GolfCartDriver
                    && _mode.TransitionSequence < uint.MaxValue
                    && proposal.Requested == NativeModeKey.Ground(_mode.TransitionSequence + 1)
                    && proposal.MigrationId == bridge.Plan.Id && proposal.Generation == bridge.Plan.Generation
                    && proposal.NetworkId == _driverId && bridge.IsCommittedMovementMode(_mode)
                    && !_fixed && !_late && !_power && !_visual && !_audio && !_exitDispatch
                    && !CartWriterHooks.IsHeld(_binding.Cart) && OccupantsFor(_binding.Cart) == null,
                    "Driver exit lost its exact actor, received ground proposal or native cart boundary.");
                if (_exitRequested) RequireExitProposalCurrent(); else RequireIdentity();
                var locators = _binding.Cart._disconnectPlayerLocators;
                Require(locators != null && !locators.WasCollected && locators.Length > 0 && Live(locators[0]),
                    "The original driver disconnect locator is unavailable.");
                if (GroundExitSettled) RequireEmptyDriverSeat();
                return locators[0];
            }
        }

        private static class CartDriverAuthorityHooks
        {
            private static CartDriverAuthority[] _active = Array.Empty<CartDriverAuthority>();
            internal static MethodInfo[] Qualify()
            {
                _ = CartWriterHooks.Qualify(typeof(GolfCartController), "UpdateSteeringFactor", typeof(void),
                    0x33CA730, 240, "D3AFC01EAB9E8794BBA374628396753B24B1B68F7E51597C80156103CCB99C7E");
                _ = CartWriterHooks.Qualify(typeof(GolfCartController), "GetPlayerSeatPositionAndRotation", typeof(void),
                    0x33CBAF0, 208, "75036EFD34ED7F90C51ED0194593D0BFC26C131169A79F6BB81AAC18ABE7556C",
                    typeof(float), typeof(Vector3).MakeByRefType(), typeof(Quaternion).MakeByRefType(), typeof(int));
                return new[]
                {
                    CartWriterHooks.Qualify(typeof(ExitRoutine), "MoveNext", typeof(bool),
                        0x33D5FB0, 544, "B499DA3F37CD163ED6C8B1A1FA69AAB3EBB75E6F62C6DF0BFAD6B947B6C7C39A"),
                    Method("DelayedFixPlayerProxyRotation", typeof(Il2CppSystem.Collections.IEnumerator),
                        0x33D0720, 256, "DA2727B43DBB186D4C35721BE79C1B412239513A3B035F72DEA90E1DA8E484A6", typeof(Transform)),
                    Method("HasNetworkConnected", typeof(bool), 0x33D2330, 224, "72E7C004CC2A5F5A007E29A44B12D291C257D2D5B052D02185156342FF288F52", typeof(int)),
                    Method("ShouldProxyTransforms", typeof(bool), 0x33CDE60, 96, "DEB4C0109FECE50D782B48CC4D33F026C5297B9793C697C4E399ACE887007585"),
                    Method("SetToKinematic", typeof(void), 0x33C5F00, 464, "E166C08D387B410BAF06AE7EA17234F25CD9E30DEF317F42C6B986DD52D9701F", typeof(bool)),
                    Method("SetRootTransformSynced", typeof(void), 0x33D1EC0, 1120, "090FE89B3A0C7E7E914B01DB992CA45A12E83342B0483EA3E14A34A0EA613CC0", typeof(bool)),
                    Method("UpdateExternalGhostConstraint", typeof(void), 0x33CE780, 1840, "9D26DC439F64B3A46B7E4B6F17B711B9D24651515AE69AC568D9A2747ACB4F10"),
                    Method("UpdateOwnedGolfCartGhost", typeof(void), 0x33CE3F0, 896, "AFBE76A50077F7936FED9437BF311E09C2E0827AB67DD463EA069741ACBF131C"),
                    Method("UpdateAccelerationFactor", typeof(void), 0x33CA820, 816, "3E172FE9B8D5C8814CCC961F64C0668E2CBF716041175D7687FE2355ED02B0CF"),
                    Method("UpdateLocalDynamicTransforms", typeof(void), 0x33CAB50, 960, "BFD12204A52FBE1E306365159C6457C3F79BFAB33F8FD9A6057E786917834324")
                };
            }
            private static MethodInfo Method(string name, Type result, int rva, int length, string hash, params Type[] args)
                => CartWriterHooks.Qualify(typeof(GolfCartController), name, result, rva, length, hash, args);

            internal static void Add(CartDriverAuthority value)
            {
                var current = Volatile.Read(ref _active);
                Require(current.Length < 64 && Find(value._binding.Cart) == null,
                    "This cart already has a driver authority lease or the bounded registry is full.");
                var next = new CartDriverAuthority[current.Length + 1];
                Array.Copy(current, next, current.Length); next[current.Length] = value;
                Volatile.Write(ref _active, next);
            }
            internal static void Remove(CartDriverAuthority value)
            {
                var current = Volatile.Read(ref _active);
                int index = Array.IndexOf(current, value);
                if (index < 0) return;
                var next = new CartDriverAuthority[current.Length - 1];
                Array.Copy(current, 0, next, 0, index);
                Array.Copy(current, index + 1, next, index, next.Length - index);
                Volatile.Write(ref _active, next);
            }
            private static CartDriverAuthority Find(Component component)
            {
                if (component is null) return null;
                foreach (var value in Volatile.Read(ref _active))
                    if (value._binding.Cart.Pointer == component.Pointer) return value;
                return null;
            }
            internal static CartDriverAuthority ForMigration(Component component) => Find(component);
            internal static bool Before(Component component, string method)
                => (Find(component)?.Before(method) ?? CartPlayerProxyAuthority.Find(component)?.Before(method) ?? true)
                    && CartPlayerPassengerAuthority.Before(component, method);
            internal static bool IsAcquired(GolfCartController cart)
                => Find(cart) != null || CartPlayerProxyAuthority.Find(cart) != null
                    || CartPlayerPassengerAuthority.IsAcquired(cart);
            internal static void After(Component component, string method, bool ran, Exception error)
            {
                Find(component)?.After(method, ran, error);
                CartPlayerProxyAuthority.After(component, method, ran, error);
                CartPlayerPassengerAuthority.After(component, method, ran, error);
            }

            internal static void Patch(HarmonyLib.Harmony harmony, MethodInfo[] methods, HarmonyMethod before, HarmonyMethod after)
            {
                foreach (var method in methods)
                {
                    string name = method.Name;
                    if (name == "MoveNext")
                        harmony.Patch(method,
                            prefix: new HarmonyMethod(typeof(CartDriverAuthorityHooks), nameof(BeforeExit)) { priority = HarmonyLib.Priority.Last },
                            finalizer: new HarmonyMethod(typeof(CartDriverAuthorityHooks), nameof(AfterExit)) { priority = HarmonyLib.Priority.Last });
                    else if (name == "DelayedFixPlayerProxyRotation")
                        harmony.Patch(method,
                            postfix: new HarmonyMethod(typeof(CartDriverAuthorityHooks), nameof(AfterExitFactory)) { priority = HarmonyLib.Priority.Last });
                    else if (name == "HasNetworkConnected" || name == "ShouldProxyTransforms"
                        || name == "SetToKinematic" || name == "SetRootTransformSynced")
                        harmony.Patch(method, prefix: new HarmonyMethod(typeof(CartDriverAuthorityHooks),
                            name == "HasNetworkConnected" ? nameof(NetworkConnected)
                            : name == "ShouldProxyTransforms" ? nameof(ProxyTransforms) : nameof(Policy)) { priority = HarmonyLib.Priority.Last });
                    else harmony.Patch(method, prefix: before, finalizer: after);
                }
            }
            private static void AfterExitFactory(GolfCartController __instance, Transform __0,
                Il2CppSystem.Collections.IEnumerator __result, bool __runOriginal)
            {
                if (__runOriginal)
                {
                    CartPlayerCallbackSettlement.Observe(__instance, __0, __result);
                    if (!CartPlayerCallbackSettlement.IsPassengerExit(__result?.TryCast<ExitRoutine>()))
                        Find(__instance)?.ObserveExit(__0, __result);
                }
            }
            private static bool BeforeExit(ExitRoutine __instance, bool __runOriginal,
                ref bool __result, out CartDriverAuthority __state)
            {
                __state = null;
                if (!__runOriginal) return false;
                if (!CartPlayerCallbackSettlement.Before(__instance))
                { __result = true; return false; }
                if (CartPlayerCallbackSettlement.IsPassengerExit(__instance)) return true;
                var value = Find(__instance.__4__this);
                if (value == null) return true;
                if (!value.BeforeExit(__instance))
                {
                    // Keep the native enumerator alive at its current yield; do not consume
                    // or manufacture completion of a quarantined player continuation.
                    __result = true;
                    return false;
                }
                __state = value;
                return true;
            }
            private static Exception AfterExit(ExitRoutine __instance, CartDriverAuthority __state, bool __runOriginal,
                bool __result, Exception __exception)
            {
                CartPlayerCallbackSettlement.After(__instance, __runOriginal, __result, __exception);
                __state?.AfterExit(__runOriginal, __result, __exception);
                return __exception;
            }
            private static bool NetworkConnected(GolfCartController __instance, int __0, bool __runOriginal, ref bool __result)
            {
                if (!__runOriginal) return false;
                var value = Find(__instance);
                if (value == null || !value._fixed || value._audio || __0 != 0) return true;
                if (!OnSelectedThread()) value.Quarantine("A native cart force query left the selected game thread.");
                __result = false; return false;
            }
            private static bool ProxyTransforms(GolfCartController __instance, bool __runOriginal, ref bool __result)
            {
                if (!__runOriginal) return false;
                if (Find(__instance) == null) return true;
                __result = false; return false;
            }
            private static bool Policy(GolfCartController __instance, bool __0, MethodBase __originalMethod, bool __runOriginal)
            {
                if (!__runOriginal) return false;
                var value = Find(__instance);
                if (value == null) return CartPlayerProxyAuthority.Policy(__instance, __originalMethod.Name, __0);
                if (!OnSelectedThread()) { value.Quarantine("A native cart policy write left the selected game thread."); return false; }
                return value._policyWrite;
            }
        }
    }
}
