// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections;
using Modstoke.Loader;
using global::Sons.Gameplay.ZipLines;
using UnityEngine;
using NativeZiplineHook = global::Sons.Gameplay.ZipLines.ZiplineHook;

namespace Modstoke.Sons
{
    public readonly record struct PlayerFlightZiplineIdentity(Guid MigrationId, ulong Generation,
        ulong NetworkId, uint TransitionSequence, ulong ControllerNetworkId, ulong HookNetworkId, bool DestinationIsAnchorA);

    public readonly record struct PlayerFlightZiplineSnapshot(PlayerFlightZiplineIdentity Identity,
        Vector3 HookPosition, Quaternion HookRotation, Vector3 Velocity, Vector3 AngularVelocity,
        Vector3 RiderLocalPosition, Quaternion RiderLocalRotation, float ImpactDelayRemaining,
        RigidbodyInterpolation OriginalInterpolation, bool ReachedDestination, bool ExitRequired);

    public readonly record struct PlayerFlightZiplineExit(PlayerFlightZiplineIdentity Identity,
        uint ExitSequence, Vector3 Position, Quaternion Rotation, Vector3 Velocity, bool ReachedDestination);

    public sealed partial class PlayerFlightZipline
    {
        private PlayerFlightZiplineExit? _exit;
        private double _forceQueuedAtFixedTime = double.NaN;

        /// <summary>
        /// After server reach/rope/entry admission and rider hold, create the native hook
        /// for this exact carrier. The returned hook is also returned on a partial failure
        /// so the aggregate can quarantine it; never retry entry blindly after a failure.
        /// hasExclusiveAuthority must validate this ordered transition, not just the actor.
        /// </summary>
        public static bool TryEnter(PlayerBridge bridge, ZipLineController controller,
            ulong controllerNetworkId, ZipLineAnchor destination, uint transitionSequence,
            float connectedHeightOffset, Func<bool> hasExclusiveAuthority, out PlayerFlightZipline motor,
            out NativeZiplineHook retainedHook, out string detail)
        {
            motor = null; retainedHook = null;
            detail = "Zipline entry requires an admitted exact rope/anchor and held authoritative rider.";
            if (!GameBuild.IsRecognized || bridge?.IsServer != true || !bridge.IsGameplayActive
                || transitionSequence == 0 || controllerNetworkId == 0 || !float.IsFinite(connectedHeightOffset)
                || hasExclusiveAuthority == null) return false;
            try
            {
                if (Time.inFixedTimeStep || !bridge.TryReadCurrentTarget(out var entity) || !Alive(entity)
                    || !PlayerRoles.IsCurrentCarrier(entity) || !hasExclusiveAuthority()
                    || Active.ContainsKey(entity.Pointer) || !Alive(controller) || !Alive(controller.entity)
                    || !controller.entity.isAttached || !controller.entity.isOwner || controller.entity.source != null
                    || controller.entity.networkId.PackedValue != controllerNetworkId
                    || !Alive(destination) || (!Same(destination, controller._anchorA) && !Same(destination, controller._anchorB))
                    || controller.gameObject.scene.handle != entity.gameObject.scene.handle
                    || !controller.gameObject.activeInHierarchy || controller._shouldDaisiyChainZipline
                    || controller._connectingToAnotherZipline || Alive(controller._ziplineConnectionForAnchorA)
                    || Alive(controller._ziplineConnectionForAnchorB)
                    || !Alive(controller._ziplineHookAsset) || entity.transform.parent != null) return false;
                var rider = entity.GetComponent<Rigidbody>();
                var hooks = controller._hooks;
                if (!Alive(rider) || !rider.isKinematic || hooks == null || hooks.WasCollected) return false;
                for (int i = 0; i < hooks.Count; i++)
                    if (Alive(hooks[i]) && Same(hooks[i].RiderTransform, entity.transform)) return false;
                if (!TryGetConnectionFrame(controller, entity.transform, out var position, out var rotation)
                    || !Same(controller.GetDestinationAnchor(position, rotation * Vector3.forward), destination)) return false;
                // PlayerZipLineAction.EnterZipLineMode offsets WORLD y before attaching
                // with repositionRider:false. Never borrow the host's focused connection.
                position.y += connectedHeightOffset;
                if (!Finite(position)) return false;
                // EnterZipLineMode finally sets PlayerBase.localEulerAngles to (0,y,0).
                // This route admits the original unparented PlayerBase/Transform identity.
                var riderRotation = Quaternion.Euler(0, rotation.eulerAngles.y, 0);
                entity.transform.SetPositionAndRotation(position, riderRotation);
                rider.position = position;
                rider.rotation = riderRotation;
                retainedHook = controller.CreateAndAttachToHook(entity.transform, destination, false, true, 0);
                if (!Alive(retainedHook)) { detail = "Native zipline creation returned no hook; inspect retained controller custody before retrying."; return false; }
                retainedHook.enabled = false;
                if (TryAdopt(bridge, controller, retainedHook, transitionSequence, hasExclusiveAuthority, out motor, out detail))
                {
                    detail = "Native player hook created and adopted once; source action entry remains reliable-stream-owned.";
                    return true;
                }
                HoldEntry(retainedHook, rider);
                detail = "Native zipline entry did not qualify; retained hook/rider held for aggregate recovery. " + detail;
                return false;
            }
            catch (Exception error)
            {
                try
                {
                    if (bridge.TryReadCurrentTarget(out var entity)) HoldEntry(retainedHook, entity.GetComponent<Rigidbody>());
                }
                catch (Exception) { }
                detail = "Zipline entry failed (" + error.GetType().Name + "); inspect exact controller/hook custody before retrying.";
                return false;
            }
        }

        internal static bool TryGetConnectionFrame(ZipLineController controller, Transform actor,
            out Vector3 position, out Quaternion rotation)
        {
            position = default; rotation = default;
            if (!Alive(controller) || !Alive(actor) || !Finite(actor.position) || !Finite(actor.forward)
                || !Finite(controller._ropeDirection) || controller._ropeDirection.sqrMagnitude < 0.000001f) return false;
            // Controller.LateUpdate 183401B63 / 183401CD0: 2m ahead, <=90 degrees.
            position = controller.CalculateClosestPointOnRope(actor.position + actor.forward * 2f);
            var direction = Vector3.Angle(controller._ropeDirection, actor.forward) <= 90f
                ? controller._ropeDirection : -controller._ropeDirection;
            rotation = Quaternion.LookRotation(direction, Vector3.up);
            return Finite(position) && Finite(rotation);
        }

        /// <summary>
        /// The existing ground motor calls this in its grounded dry steering branch instead
        /// of ordinary Acceleration. It owns all other ground/air/contact/gravity work.
        /// </summary>
        public static bool TryApplyGroundExitForces(PlayerBridge bridge, Rigidbody body, Vector3 change)
        {
            if (bridge == null || !ExitContinuations.TryGetValue(bridge, out var exit) || !exit.ForceActive) return false;
            exit.ApplyGroundForces(body, change);
            return true;
        }

        private static readonly System.Collections.Generic.Dictionary<PlayerBridge, ExitContinuation> ExitContinuations = new();

        internal sealed class ExitContinuation : IDisposable
        {
            private readonly PlayerBridge _bridge;
            private readonly Rigidbody _body;
            private readonly int _bodyId, _thread = Environment.CurrentManagedThreadId;
            private readonly PlayerFlightZiplineExit _exit;
            private Coroutine _routine;
            private float _elapsed, _multiplier = 1;
            private Vector3 _force;
            private double _forceStep = double.NaN;
            private bool _disposed, _inside;
            internal bool ForceActive { get; private set; } = true;
            internal string Failure { get; private set; }
            internal float Elapsed => _elapsed;
            internal float Multiplier => _multiplier;
            internal Vector3 Force => _force;

            private ExitContinuation(PlayerBridge bridge, Rigidbody body, PlayerFlightZiplineExit exit)
            { _bridge = bridge; _body = body; _bodyId = body.GetInstanceID(); _exit = exit; _force = exit.Velocity; }

            internal static bool TryStart(PlayerBridge bridge, Rigidbody body,
                PlayerFlightZiplineExit exit, out ExitContinuation continuation, out string detail)
            {
                continuation = null;
                detail = "Zipline exit requires the resumed exact ground body and a single native force lifetime.";
                if (bridge == null || !Alive(body) || Time.inFixedTimeStep || ExitContinuations.ContainsKey(bridge)) return false;
                var candidate = new ExitContinuation(bridge, body, exit);
                if (!candidate.Current()) return false;
                ExitContinuations.Add(bridge, candidate);
                bridge.Retired += candidate.Dispose;
                if (!global::Modstoke.Loader.Coroutines.TryStartObserved(candidate.PreserveExitForce(), candidate.Completed,
                        out var token, out detail))
                { candidate.Dispose(); return false; }
                candidate._routine = token;
                if (candidate.Failure != null) { detail = candidate.Failure; candidate.Dispose(); return false; }
                continuation = candidate;
                return true;
            }

            private bool Current() => !_disposed && Environment.CurrentManagedThreadId == _thread
                && _bridge.IsGameplayActive && _bridge.IsServer
                && _bridge.IsCommittedMovementMode(PlayerClientMovement.NativeModeKey.Ground(_exit.ExitSequence))
                && _bridge.TryReadCurrentTarget(out var actor) && Alive(actor) && PlayerRoles.IsCurrentCarrier(actor)
                && Alive(_body) && _body.GetInstanceID() == _bodyId && Same(actor.GetComponent<Rigidbody>(), _body)
                && !_body.isKinematic && _body.detectCollisions && actor.transform.parent == null;

            private IEnumerator PreserveExitForce()
            {
                var wait = new WaitForFixedUpdate();
                // Exact <PreserveExitVelocity>d__17: cumulative Lerp and cumulative
                // vector scaling, first tick synchronous, then after each fixed step.
                while (_elapsed < 1f)
                {
                    _inside = true;
                    try
                    {
                        if (!Current() || !float.IsFinite(Time.deltaTime) || Time.deltaTime < 0)
                            throw new InvalidOperationException("Zipline exit force lost its exact active ground body or clock.");
                        _multiplier = Mathf.Lerp(_multiplier, 0, _elapsed);
                        _force *= _multiplier;
                        _elapsed += Time.deltaTime;
                        if (!Finite(_force) || !float.IsFinite(_elapsed))
                            throw new InvalidOperationException("Zipline exit force became non-finite.");
                    }
                    finally { _inside = false; }
                    yield return wait;
                }
                ForceActive = false;
            }

            internal void ApplyGroundForces(Rigidbody body, Vector3 change)
            {
                if (!Current() || !Same(body, _body) || !Time.inFixedTimeStep || !Finite(change)
                    || _forceStep == Time.fixedTimeAsDouble)
                    throw new InvalidOperationException("Zipline exit steering was not applied once to its retained ground body.");
                // FPC.FixedUpdate 182F80821..182F80883, grounded/dry branch only.
                _body.AddForce(change, ForceMode.VelocityChange);
                _body.AddForce(_force, ForceMode.VelocityChange);
                _forceStep = Time.fixedTimeAsDouble;
            }

            private void Completed(bool completed, Exception error)
            {
                _routine = null;
                if (_disposed) return;
                if (!completed || error != null)
                {
                    Failure = "Native zipline exit force lifetime ended unexpectedly" + (error == null ? "." : " (" + error.GetType().Name + ").");
                    try { if (Alive(_body) && _body.GetInstanceID() == _bodyId) _body.isKinematic = true; }
                    catch (Exception) { }
                }
                ForceActive = false;
            }

            public void Dispose()
            {
                if (_disposed || Environment.CurrentManagedThreadId != _thread) return;
                _disposed = true; ForceActive = false;
                _bridge.Retired -= Dispose;
                if (ExitContinuations.TryGetValue(_bridge, out var current) && ReferenceEquals(current, this))
                    ExitContinuations.Remove(_bridge);
                var routine = _routine; _routine = null;
                if (routine != null && !_inside)
                {
                    try { global::Modstoke.Loader.Coroutines.StopOwned(routine); }
                    catch (Exception error) { Failure = "Zipline exit iterator retirement failed (" + error.GetType().Name + ")."; }
                }
            }
        }

        /// <summary>Capture the retained native hook without recreating its impulse, delay or rider attachment.</summary>
        public bool TryCapture(out PlayerFlightZiplineSnapshot snapshot, out string detail)
        {
            snapshot = default;
            detail = "Zipline capture requires the current retained hook outside a fixed step.";
            if (_disposed || Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep
                || (!double.IsNaN(_forceQueuedAtFixedTime) && Time.fixedTimeAsDouble <= _forceQueuedAtFixedTime)) return false;
            try
            {
                if (!Current()) return Stop("Zipline capture binding changed.", out detail);
                if (!Finite(_body.position) || !Finite(_body.rotation) || !Finite(_body.velocity)
                    || !Finite(_body.angularVelocity)) return Stop("Zipline capture pose is not finite.", out detail);
                snapshot = new(Identity, _body.position, _body.rotation, _body.velocity, _body.angularVelocity,
                    _riderLocalPosition, _riderLocalRotation, _hook._delayImpactCheckTime,
                    _hook._originalInterpolation, _hook.HasReachedDestination, ExitRequired);
                detail = "Exact zipline hook pose, momentum, impact delay and destination state preserved.";
                return true;
            }
            catch (Exception error) { return Stop("Zipline capture failed (" + error.GetType().Name + ").", out detail); }
        }

        /// <summary>
        /// Adopt the same retained native hook after the aggregate restored the approved
        /// capture. A newly created hook has a different identity and is not preservation.
        /// </summary>
        public static bool TryRestore(PlayerBridge bridge, ZipLineController controller,
            NativeZiplineHook hook, PlayerFlightZiplineSnapshot snapshot,
            Func<bool> hasExclusiveAuthority, out PlayerFlightZipline motor, out string detail)
        {
            motor = null;
            detail = "Zipline restoration requires the same approved hook and ordered identity.";
            var identity = snapshot.Identity;
            if (bridge == null || identity.MigrationId != bridge.Plan.Id || identity.Generation != bridge.Plan.Generation
                || identity.NetworkId != bridge.NetworkId || !Finite(snapshot.HookPosition) || !Finite(snapshot.HookRotation)
                || !Finite(snapshot.Velocity) || !Finite(snapshot.AngularVelocity) || !Finite(snapshot.RiderLocalPosition)
                || !Finite(snapshot.RiderLocalRotation) || !float.IsFinite(snapshot.ImpactDelayRemaining)) return false;
            try
            {
                if (!Alive(controller) || !Alive(controller.entity) || !ValidHook(hook)
                    || controller.entity.networkId.PackedValue != identity.ControllerNetworkId
                    || hook.entity.networkId.PackedValue != identity.HookNetworkId
                    || !Same(hook.DestinationAnchor, identity.DestinationIsAnchorA ? controller._anchorA : controller._anchorB)
                    || (hook._rigidbody.position - snapshot.HookPosition).sqrMagnitude > 0.000001f
                    || Quaternion.Angle(hook._rigidbody.rotation, snapshot.HookRotation) > 0.01f
                    || (hook._rigidbody.velocity - snapshot.Velocity).sqrMagnitude > 0.000001f
                    || (hook._rigidbody.angularVelocity - snapshot.AngularVelocity).sqrMagnitude > 0.000001f
                    || !Alive(hook.RiderTransform)
                    || (hook.RiderTransform.localPosition - snapshot.RiderLocalPosition).sqrMagnitude > 0.000001f
                    || Quaternion.Angle(hook.RiderTransform.localRotation, snapshot.RiderLocalRotation) > 0.01f
                    || hook._delayImpactCheckTime != snapshot.ImpactDelayRemaining
                    || hook._originalInterpolation != snapshot.OriginalInterpolation
                    || hook.HasReachedDestination != snapshot.ReachedDestination) return false;
                if (!TryAdopt(bridge, controller, hook, identity.TransitionSequence, hasExclusiveAuthority, out motor, out detail)) return false;
                motor.ExitRequired = snapshot.ExitRequired;
                return true;
            }
            catch (Exception error) { detail = "Zipline restore failed (" + error.GetType().Name + ")."; return false; }
        }

        private static void HoldEntry(NativeZiplineHook hook, Rigidbody rider)
        {
            if (Alive(hook))
            {
                hook.enabled = false;
                if (Alive(hook._rigidbody)) hook._rigidbody.isKinematic = true;
            }
            if (Alive(rider)) rider.isKinematic = true;
        }

        private static bool Finite(Quaternion value)
            => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z) && float.IsFinite(value.w)
                && value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w > 0.0001f;
    }
}
