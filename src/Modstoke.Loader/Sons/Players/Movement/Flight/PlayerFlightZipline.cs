// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Loader;
using System.Collections.Generic;
using global::Sons.Gameplay.ZipLines;
using NativeZiplineHook = global::Sons.Gameplay.ZipLines.ZiplineHook;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// Drives an exact retained server hook for one player without the native LateUpdate's
    /// ActiveWorldLocation position lookup. Native clearance and impact helpers use the hook's
    /// rider-collider exclusions. Client input, camera, rope-gun prop and exit presentation stay
    /// on the original client. This is not an active-hook recreation or daisy-chain adapter.
    /// </summary>
    public sealed partial class PlayerFlightZipline : IDisposable
    {
        private static readonly Dictionary<IntPtr, PlayerFlightZipline> Active = new();
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerBridge _bridge;
        private readonly BoltEntity _entity, _hookEntity;
        private readonly ZipLineController _controller;
        private readonly NativeZiplineHook _hook;
        private readonly ZipLineAnchor _destination;
        private readonly Transform _rider, _hookTransform;
        private readonly Rigidbody _body, _riderBody;
        private readonly Func<bool> _hasExclusiveAuthority;
        private readonly int _entityId, _hookId, _bodyId, _riderBodyId, _scene;
        private readonly ulong _hookNetworkId;
        private readonly BoltEntity _controllerEntity;
        private readonly int _controllerId, _destinationId;
        private readonly IntPtr _detachRequest, _colliders, _hits;
        private readonly Vector3 _riderLocalPosition;
        private readonly Quaternion _riderLocalRotation;
        private int _lastFrame = -1;
        private bool _disposed;

        public bool IsDisposed => _disposed;
        public bool IsDetached { get; private set; }
        public bool ReachedDestination { get; private set; }
        public Vector3 ExitVelocity { get; private set; }
        public string StopReason { get; private set; }
        public PlayerFlightZiplineIdentity Identity { get; }
        public bool ExitRequired { get; private set; }

        private PlayerFlightZipline(PlayerBridge bridge, BoltEntity entity,
            ZipLineController controller, NativeZiplineHook hook, Rigidbody riderBody, uint transitionSequence, Func<bool> hasExclusiveAuthority)
        {
            _bridge = bridge; _entity = entity; _controller = controller; _hook = hook;
            _rider = entity.transform; _body = hook._rigidbody; _riderBody = riderBody;
            _hookTransform = hook.transform; _hookEntity = hook.entity; _destination = hook.DestinationAnchor;
            _hasExclusiveAuthority = hasExclusiveAuthority;
            _entityId = entity.GetInstanceID(); _hookId = hook.GetInstanceID();
            _bodyId = _body.GetInstanceID(); _riderBodyId = riderBody.GetInstanceID();
            _hookNetworkId = _hookEntity.networkId.PackedValue; _scene = entity.gameObject.scene.handle;
            _controllerEntity = controller.entity; _controllerId = controller.GetInstanceID();
            _destinationId = _destination.GetInstanceID();
            Identity = new(bridge.Plan.Id, bridge.Plan.Generation, bridge.NetworkId, transitionSequence,
                _controllerEntity.networkId.PackedValue, _hookNetworkId, Same(_destination, controller._anchorA));
            _detachRequest = hook.OnDetachRequested.Pointer;
            _colliders = hook._riderColliders.Pointer; _hits = hook._raycastHits.Pointer;
            _riderLocalPosition = _rider.localPosition; _riderLocalRotation = _rider.localRotation;
        }

        /// <summary>
        /// The aggregate must first admit the rope/actor/anchor, suspend its ordinary body motor,
        /// and acquire a native CreateAndAttachToHook result (riderIsAPlayer:true). Hold that hook's
        /// enabled flag false before adoption. Its original server Rigidbody must remain dynamic;
        /// a replicated client hook has no authoritative Rigidbody and is rejected. OnDetached
        /// must be empty: native PlayerZipLineAction callbacks cannot run for remote players.
        /// Existing client callbacks, input subscriptions and hooks are never cleared or replaced.
        /// </summary>
        public static bool TryAdopt(PlayerBridge bridge, ZipLineController controller,
            NativeZiplineHook hook, uint transitionSequence, Func<bool> hasExclusiveAuthority,
            out PlayerFlightZipline motor, out string detail)
        {
            motor = null;
            detail = "An exact disabled server hook, admitted rider and exclusive movement authority are required.";
            if (!GameBuild.IsRecognized || bridge?.IsServer != true
                || !bridge.IsGameplayActive || transitionSequence == 0 || hasExclusiveAuthority == null) return false;
            try
            {
                if (!bridge.TryReadCurrentTarget(out var entity) || Time.inFixedTimeStep || !Alive(entity) || !Alive(controller)
                    || !Alive(hook) || Active.ContainsKey(entity.Pointer) || !ValidHook(hook)
                    || !Same(hook.RiderTransform, entity.transform) || !ControllerOwns(controller, hook)
                    || !NativeControllerCallback(controller, hook)) return false;
                var riderBody = entity.GetComponent<Rigidbody>();
                if (!Alive(riderBody) || !riderBody.isKinematic) return false;
                var candidate = new PlayerFlightZipline(bridge, entity, controller,
                    hook, riderBody, transitionSequence, hasExclusiveAuthority);
                if (!candidate.Current()) return false;
                Active.Add(entity.Pointer, candidate);
                bridge.Retired += candidate.Dispose;
                if (!candidate.BindDispatch(out detail)) return false;
                motor = candidate;
                detail = "Retained server hook adopted; client action callbacks and hook recreation are not covered.";
                return true;
            }
            catch (Exception error)
            { detail = "Zipline adoption failed (" + error.GetType().Name + ")."; return false; }
        }

        /// <summary>
        /// Call once from LateUpdate, not FixedUpdate: native zipline acceleration uses frame dt.
        /// The Unity physics step still advances the hook; this never translates at a fixed speed.
        /// Terminal clearance/impact/destination detection latches ExitRequired. The aggregate
        /// settles the ordered exit through TryDetach and delivers its exact-identity receipt.
        /// </summary>
        public bool TryLateUpdate(out string detail)
        {
            detail = "Zipline motion requires a new LateUpdate.";
            if (_disposed || Environment.CurrentManagedThreadId != _thread
                || Time.inFixedTimeStep || _lastFrame == Time.frameCount || ExitRequired) return false;
            try
            {
                if (!Current()) return Stop("Zipline actor, hook or callback binding changed.", out detail);
                float dt = Time.deltaTime;
                Vector3 position = _hookTransform.position, riderPosition = _rider.position;
                Vector3 destination = _destination.transform.position;
                Vector3 acceleration = _hookTransform.forward * (_hook._accelerationForce * dt);
                Vector3 velocity = _body.velocity;
                if (!float.IsFinite(dt) || dt <= 0 || !Finite(position) || !Finite(riderPosition)
                    || !Finite(destination) || !Finite(acceleration) || !Finite(velocity))
                    return Stop("Zipline motion contains non-finite geometry or time.", out detail);
                _lastFrame = Time.frameCount;
                // ZiplineHook.LateUpdate queues VelocityChange, then clamps the current velocity.
                // Do not fold acceleration into the clamp: Unity applies AddForce at simulation.
                _body.AddForce(acceleration, ForceMode.VelocityChange);
                _forceQueuedAtFixedTime = Time.fixedTimeAsDouble;
                if (velocity.sqrMagnitude > _hook._maxVelocitySqr)
                    _body.velocity = velocity.normalized * _hook._maxVelocity;
                float clearance = _hook._playerGroundClearance;
                bool mustDetach = !_hook.CanClearGround(position, clearance, _hook._raycastHits, _hook._collideMask);
                if (!mustDetach)
                    mustDetach = _hook.ShouldDetachToAvoidImpact(riderPosition, (destination - position).normalized,
                        _hook._playerCollisionRadius, clearance, _hook._raycastHits, _hook._collideMask);
                if (!mustDetach && Vector3.Distance(position, destination) <= _hook._playerEvacuationDistanceToDestination)
                {
                    _hook.HasReachedDestination = true;
                    mustDetach = true;
                }
                if (mustDetach)
                {
                    ExitRequired = true;
                    detail = "Zipline terminal contact requires the exact ordered detach transaction.";
                    return true;
                }
                detail = "Actor-relative native zipline dynamics applied; physics completion is still pending.";
                return true;
            }
            catch (Exception error) { return Stop("Zipline step failed (" + error.GetType().Name + ").", out detail); }
        }

        /// <summary>
        /// Route an authenticated native Take.performed/Jump.performed intent here, or settle
        /// the terminal exit latched by TryLateUpdate. The aggregate still owns the original client's
        /// ExitZipLineMode and PreserveExitVelocity coroutine. The server continuation ports
        /// only the iterator's force phase into the existing ground motor; neither native
        /// LocalPlayer action is invoked on the server.
        /// </summary>
        public bool TryDetach(PlayerFlightZiplineIdentity identity, uint exitSequence,
            out PlayerFlightZiplineExit exit, out string detail)
        {
            exit = default;
            detail = "An exact active hook and rider are required for detach.";
            if (Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep
                || identity != Identity || exitSequence <= Identity.TransitionSequence) return false;
            if (_exit is { } completed && completed.ExitSequence == exitSequence)
            { exit = completed; detail = "Zipline detach already completed."; return true; }
            if (_disposed) return false;
            if (!double.IsNaN(_forceQueuedAtFixedTime) && Time.fixedTimeAsDouble <= _forceQueuedAtFixedTime)
            { detail = "Accepted hook forces must finish their native physics step before detach."; return false; }
            try
            {
                if (!Current()) return Stop("Zipline detach binding changed.", out detail);
                ExitVelocity = _hook.GetVelocity();
                ReachedDestination = _hook.HasReachedDestination;
                var interpolation = _hook._originalInterpolation;
                if (!Finite(ExitVelocity)) return Stop("Zipline exit velocity is not finite.", out detail);
                // DetachRider alone omits list removal/destruction. No player-facing callbacks
                // are admitted, so controller cleanup cannot invoke the host's player action.
                _controller.DetachRiderFromHook(_rider);
                if (!Alive(_rider) || _rider.parent != null || CountHook(_controller, _hook) != 0
                    || !Alive(_riderBody) || _riderBody.isKinematic || !_riderBody.useGravity
                    || _riderBody.interpolation != interpolation
                    || (_riderBody.velocity - ExitVelocity).sqrMagnitude > 0.000001f)
                    return Stop("Native zipline detach did not complete its exact rider/body cleanup.", out detail);
                IsDetached = true;
                ExitRequired = true;
                exit = new(Identity, exitSequence, _rider.position, _rider.rotation, ExitVelocity, ReachedDestination);
                _riderBody.isKinematic = true;
                _exit = exit;
                Retire();
                detail = "Hook detached and controller cleanup completed; original-client exit effects still require delivery.";
                return true;
            }
            catch (Exception error) { return Stop("Zipline detach failed (" + error.GetType().Name + ").", out detail); }
        }

        private bool Current()
        {
            if (_disposed || Environment.CurrentManagedThreadId != _thread || !Physics.autoSimulation
                || !BoltNetwork.isServer || !PlayerRoles.IsAvailable || !_hasExclusiveAuthority()) return false;
            return _bridge.IsGameplayActive && _bridge.TryReadCurrentTarget(out var entity) && Same(entity, _entity)
                && _entity.GetInstanceID() == _entityId && PlayerRoles.IsCurrentCarrier(entity)
                && Alive(_rider) && Same(entity.transform, _rider) && ValidHook(_hook)
                && _hook.GetInstanceID() == _hookId && Same(_hook._rigidbody, _body)
                && _body.GetInstanceID() == _bodyId && Same(_hook.transform, _hookTransform)
                && Same(_hook.entity, _hookEntity) && _hookEntity.networkId.PackedValue == _hookNetworkId
                && Same(_hook.DestinationAnchor, _destination) && _destination.GetInstanceID() == _destinationId
                && Same(_hook.RiderTransform, _rider)
                && Same(_rider.parent, _hookTransform) && (_rider.localPosition - _riderLocalPosition).sqrMagnitude < 0.000001f
                && Quaternion.Angle(_rider.localRotation, _riderLocalRotation) < 0.01f
                && Alive(_riderBody) && _riderBody.GetInstanceID() == _riderBodyId && _riderBody.isKinematic
                && Same(_riderBody.gameObject, entity.gameObject) && _hook.OnDetachRequested?.Pointer == _detachRequest
                && _hook._riderColliders.Pointer == _colliders && _hook._raycastHits.Pointer == _hits
                && entity.gameObject.scene.handle == _scene && entity.gameObject.activeInHierarchy
                && _hook.gameObject.scene.handle == _scene && _hook.gameObject.activeInHierarchy
                && Alive(_controller) && _controller.GetInstanceID() == _controllerId
                && Same(_controller.entity, _controllerEntity)
                && _controllerEntity.networkId.PackedValue == Identity.ControllerNetworkId
                && Same(_destination, Identity.DestinationIsAnchorA ? _controller._anchorA : _controller._anchorB)
                && NativeControllerCallback(_controller, _hook)
                && PhysicsSceneExtensions.GetPhysicsScene(entity.gameObject.scene) == Physics.defaultPhysicsScene
                && ControllerOwns(_controller, _hook);
        }

        private static bool ValidHook(NativeZiplineHook hook)
            => Alive(hook) && !hook.enabled && hook._riderIsAPlayer && hook.OnDetached == null
                && Alive(hook.entity) && hook.entity.isAttached && hook.entity.isOwner && hook.entity.source == null
                && Alive(hook._rigidbody) && !hook._rigidbody.isKinematic && !hook._rigidbody.useGravity
                && Same(hook._rigidbody.gameObject, hook.gameObject) && Alive(hook.DestinationAnchor)
                && hook._riderColliders != null && !hook._riderColliders.WasCollected
                && hook._raycastHits != null && !hook._raycastHits.WasCollected && hook._raycastHits.Length > 0
                && Nonnegative(hook._accelerationForce) && Positive(hook._maxVelocity)
                && float.IsFinite(hook._maxVelocitySqr) && hook._maxVelocitySqr == hook._maxVelocity * hook._maxVelocity
                && Positive(hook._playerGroundClearance) && Positive(hook._playerCollisionRadius)
                && Nonnegative(hook._playerEvacuationDistanceToDestination) && float.IsFinite(hook._delayImpactCheckTime);

        private static bool ControllerOwns(ZipLineController controller, NativeZiplineHook hook)
            => Alive(controller) && Alive(controller.entity) && controller.entity.isAttached && controller.entity.isOwner
                && controller.entity.source == null
                && !controller._shouldDaisiyChainZipline && !controller._connectingToAnotherZipline
                && !Alive(controller._ziplineConnectionForAnchorA) && !Alive(controller._ziplineConnectionForAnchorB)
                && (Same(hook.DestinationAnchor, controller._anchorA) || Same(hook.DestinationAnchor, controller._anchorB))
                && CountHook(controller, hook) == 1;

        private static int CountHook(ZipLineController controller, NativeZiplineHook hook)
        {
            if (!Alive(controller)) return -1;
            var hooks = controller._hooks;
            if (hooks == null || hooks.WasCollected) return -1;
            int matches = 0;
            for (int i = 0; i < hooks.Count; i++)
                if (hooks[i] is { } entry && entry.Pointer == hook.Pointer) matches++;
            return matches;
        }

        private static bool NativeControllerCallback(ZipLineController controller, NativeZiplineHook hook)
        {
            var callback = hook.OnDetachRequested;
            return callback != null && !callback.WasCollected
                && callback.TryCast<Il2CppSystem.MulticastDelegate>()?.delegates == null
                && callback.Target != null && !callback.Target.WasCollected && callback.Target.Pointer == controller.Pointer
                && callback.Method?.Name == nameof(ZipLineController.OnHookDetachRequested)
                && callback.Method.DeclaringType?.FullName == typeof(ZipLineController).FullName;
        }

        /// <summary>Holds both borrowed bodies. Does not detach, recreate, resume native callbacks or destroy objects.</summary>
        public void Dispose()
        {
            if (_disposed || Environment.CurrentManagedThreadId != _thread) return;
            Stop("Zipline motor retired; retained hook and rider held for aggregate cleanup.", out _);
        }

        private bool Stop(string reason, out string detail)
        {
            detail = StopReason = reason;
            if (Environment.CurrentManagedThreadId != _thread) return false;
            Retire();
            try { if (Alive(_hook) && _hook.GetInstanceID() == _hookId) _hook.enabled = false; }
            catch (Exception) { StopReason += " Hook writer hold could not be confirmed."; }
            try { if (Alive(_body) && _body.GetInstanceID() == _bodyId) _body.isKinematic = true; }
            catch (Exception) { StopReason += " Hook body hold could not be confirmed."; }
            try { if (Alive(_riderBody) && _riderBody.GetInstanceID() == _riderBodyId) _riderBody.isKinematic = true; }
            catch (Exception) { StopReason += " Rider body hold could not be confirmed."; }
            detail = StopReason;
            return false;
        }

        private void Retire()
        {
            _disposed = true;
            UnbindDispatch();
            _bridge.Retired -= Dispose;
            if (Active.TryGetValue(_entity.Pointer, out var active) && ReferenceEquals(active, this)) Active.Remove(_entity.Pointer);
        }

        private static bool Positive(float v) => float.IsFinite(v) && v > 0;
        private static bool Nonnegative(float v) => float.IsFinite(v) && v >= 0;
        private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        private static bool Alive(UnityEngine.Object v) => v is not null && !v.WasCollected && v.Pointer != IntPtr.Zero && v != null;
        private static bool Same(UnityEngine.Object a, UnityEngine.Object b) => Alive(a) && Alive(b) && a.Pointer == b.Pointer;
    }
}
