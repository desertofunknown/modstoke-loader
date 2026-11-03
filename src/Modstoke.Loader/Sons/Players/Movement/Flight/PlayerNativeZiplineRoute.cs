// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;
using RedLoader;
using global::Sons.Gameplay.ZipLines;
using UnityEngine;
using NativeMode = Modstoke.Sons.PlayerClientMovement.NativeMode;
using NativeModeKey = Modstoke.Sons.PlayerClientMovement.NativeModeKey;
using NativeState = Modstoke.Sons.PlayerClientMovement.NativeState;
using NativeZiplineHook = global::Sons.Gameplay.ZipLines.ZiplineHook;

namespace Modstoke.Sons
{
    /// <summary>Concrete authenticated direct-rope admission and delivery around the retained flight motor.</summary>
    public sealed class PlayerNativeZiplineRoute : IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerMigrationAdapter _owner;
        private readonly PlayerBridge _bridge;
        private readonly PlayerGroundMovement _ground;
        private readonly PlayerCarrier _carrier;
        private readonly PlayerInventoryAuthority _inventory;
        private PlayerFlightZipline _motor;
        private NativeZiplineHook _hook;
        private PlayerModeTransitionProposal _entry, _exitProposal;
        private PlayerZiplineRecord? _entryRecord, _exitRecord;
        private PlayerFlightZiplineExit? _exit;
        private PlayerFlightZipline.ExitContinuation _exitContinuation;
        private NativeState _pendingState;
        private long _nextRecord;
        private double _lastExitFixedTime = double.NaN;
        private uint _exitMotionSequence;
        private bool _groundResumed;
        private bool _entryAttempted, _entryApproved, _exitApproved, _disposed;

        public bool IsDisposed => _disposed;
        public string Detail { get; private set; }
        public PlayerFlightZipline Motor => _motor;
        public NativeZiplineHook RetainedHook => _hook;
        public PlayerFlightZiplineExit? CompletedExit => _exit;

        private PlayerNativeZiplineRoute(PlayerMigrationAdapter owner,
            PlayerGroundMovement ground)
        {
            _owner = owner; _bridge = owner.Bridge; _ground = ground;
            _carrier = owner.Carrier; _inventory = owner.ServerInventory;
        }

        public static bool TryCreate(PlayerMigrationAdapter owner, PlayerGroundMovement ground,
            out PlayerNativeZiplineRoute route, out string detail)
        {
            route = null;
            detail = "Zipline routing requires this destination adapter's active bridge, restored actor, inventory and ground motor.";
            if (owner?.Role != BoltMigrationRole.Destination || owner.Bridge?.IsServer != true
                || !owner.Bridge.IsGameplayActive || owner.Carrier == null || owner.ServerInventory == null
                || ground?.IsActive != true || ground.IsYieldedToNativeMode || Time.inFixedTimeStep) return false;
            var candidate = new PlayerNativeZiplineRoute(owner, ground);
            if (!candidate.Current()) return false;
            try
            {
                // Protocol/custody pump only. The existing motor owns its one LateUpdate subscription.
                owner.Bridge.Retired += candidate.Dispose;
                GlobalEvents.OnUpdate.Subscribe(candidate.Pump);
                route = candidate;
                detail = "Zipline proposals now have exact-actor rope admission, existing motor ownership and retained source receipts.";
                return true;
            }
            catch (Exception error)
            { candidate.Dispose(); detail = "Zipline route binding failed (" + error.GetType().Name + ")."; return false; }
        }

        private bool Current() => !_disposed && Environment.CurrentManagedThreadId == _thread && !Time.inFixedTimeStep
            && ReferenceEquals(_owner.Bridge, _bridge) && ReferenceEquals(_owner.Carrier, _carrier)
            && ReferenceEquals(_owner.ServerInventory, _inventory) && !_owner.IsCancelled && !_owner.IsUnconfirmed
            && _bridge.IsGameplayActive && _ground.IsActive && !_inventory.IsDisposed && !_inventory.IsFaulted
            && _carrier.IsCurrentForMotor && _bridge.TryReadCurrentTarget(out var actor) && Same(actor, _carrier.Entity);

        private bool OwnsRide() => Current() && _ground.YieldedNativeMode == _entry.Requested
            && (_bridge.IsCommittedMovementMode(_entry.Requested)
                || (!_entryApproved && _bridge.IsCommittedMovementMode(_entry.Expected)));

        private void Pump()
        {
            if (_disposed || Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep) return;
            try
            {
                if (!Current()) { Stop("Zipline route lost its retained actor or authority owners."); return; }
                if (_exitApproved)
                {
                    if (!ResumeExit()) return;
                    if (_exitContinuation.Failure != null) { Stop(_exitContinuation.Failure); return; }
                    if (_bridge.IsZiplineTransitionPending(_exitProposal))
                    {
                        PublishRecord(_exitRecord);
                        PublishExitMotion();
                        return;
                    }
                    if (_exitContinuation.ForceActive) return;
                    // Receipt settlement never reapplies the old detach pose or impulse.
                    _exitContinuation.Dispose(); _exitContinuation = null;
                    _motor = null; _hook = null;
                    _entry = _exitProposal = default; _entryRecord = _exitRecord = null; _exit = null;
                    _entryAttempted = _entryApproved = _exitApproved = false;
                    _groundResumed = false; _exitMotionSequence = 0; _lastExitFixedTime = double.NaN;
                    Detail = "Original source exit acknowledged; continuous server ground motion was retained.";
                    return;
                }
                if (_bridge.TryPeekMovementTransitionProposal(out var proposal))
                {
                    if (proposal.Expected.Mode == NativeMode.Ground
                        && PlayerClientMovement.IsZiplineMode(proposal.Requested.Mode))
                        AdmitEntry(proposal);
                    else if (_motor != null && proposal.Expected == _entry.Requested && proposal.Requested.Mode == NativeMode.Ground)
                        AdmitExit(proposal);
                }
                if (_disposed || !_entryApproved || _exitApproved || _exit != null) return;
                if (_motor == null || _motor.IsDisposed) { Stop("The retained zipline motor stopped before ordered exit."); return; }
                if (_bridge.HasPendingMovementTransition && _exitProposal.RequestSequence == 0) PublishRecord(_entryRecord);
                if (_motor.ExitRequired)
                {
                    // A terminal cue asks the source for the ordinary ordered exit; it cannot commit one.
                    if (_entryRecord is { } entry)
                        PublishRecord(entry with { Kind = PlayerZiplineRecordKind.Terminal, RequestSequence = 0 });
                }
                PublishMotion();
            }
            catch (Exception error) { Stop("Zipline route failed (" + error.GetType().Name + "); retained bodies held."); }
        }

        private void AdmitEntry(PlayerModeTransitionProposal proposal)
        {
            if (_entryAttempted)
            {
                if (proposal != _entry) { Stop("A different zipline request overtook the retained entry."); return; }
            }
            else
            {
                if (_motor != null || !_bridge.IsCommittedMovementMode(proposal.Expected)) return;
                if (!_bridge.TryReadZiplineEntryRequest(proposal, out var request))
                { Detail = "Awaiting the exact source action's retained native hanging height."; return; }
                if (request.ConnectedHeightOffset != _carrier.Profile.ZiplineConnectedHeightOffset)
                {
                    Detail = "Zipline hanging height differs from this migration's independently approved movement profile.";
                    _bridge.TryRejectMovementTransition(proposal, out _);
                    return;
                }
                if (!TryAdmitRope(proposal.Requested, out var controller, out var destination, out var reason))
                { Detail = reason; _bridge.TryRejectMovementTransition(proposal, out _); return; }
                if (!_ground.TryYieldToNativeMode(proposal.Expected, proposal.Requested, out reason)) { Detail = reason; return; }
                _entry = proposal; _entryAttempted = true;
                if (!PlayerFlightZipline.TryEnter(_bridge, controller, proposal.Requested.AnchorNetworkId,
                        destination, proposal.Requested.TransitionSequence, _carrier.Profile.ZiplineConnectedHeightOffset,
                        OwnsRide, out _motor, out _hook, out reason))
                { Stop(reason); return; }
            }
            if (_entryRecord == null)
            {
                if (!_motor.TryCapture(out var snapshot, out var reason)) { Detail = reason; return; }
                _entryRecord = new(PlayerZiplineRecordKind.Entry, snapshot.Identity, proposal.RequestSequence, 0,
                    snapshot.HookPosition, snapshot.HookRotation, snapshot.Velocity,
                    snapshot.RiderLocalPosition, snapshot.RiderLocalRotation, snapshot.ReachedDestination);
                if (!_bridge.TryReadZiplineEntryRequest(proposal, out var request))
                { Stop("The retained zipline action profile disappeared after hook creation."); return; }
                _entryRecord = _entryRecord.Value with { ConnectedHeightOffset = request.ConnectedHeightOffset };
            }
            if (!_bridge.TryApproveMovementTransition(proposal, out _, out var detail)) { Detail = detail; return; }
            _entryApproved = true; _nextRecord = 0;
            PublishRecord(_entryRecord);
            Detail = "Exact rope endpoint admitted; existing server hook motor adopted and source entry retained.";
        }

        private bool TryAdmitRope(NativeModeKey key, out ZipLineController controller, out ZipLineAnchor destination, out string detail)
        {
            controller = null; destination = null;
            detail = "The exact server rope, reachable trigger, clear native attachment and admitted rope gun are required.";
            if (key.SeatIndex != -1 || !PlayerClientMovement.IsZiplineMode(key.Mode)
                || !_inventory.HasAcceptedBaseline || _inventory.MutationEnforcement?.HasPendingCustody != false
                || _inventory.Equipment?.PendingCount != 0
                || !PlayerAuthorityDynamics.TryReadCarrierActions(_carrier, out var actions, out _)
                || !actions.CanUseInventory || !_inventory.TryReadItems(out var items)) return false;
            bool ropeGun = false;
            foreach (var item in items)
            {
                if (item.Consumed || item.Quantity <= 0) continue;
                if (item.HeldSlot == 3 && item.ItemId == 630) return false;
                if (item.ItemId == 522) ropeGun = true;
            }
            if (!ropeGun || _carrier.Entity.GetState<IPlayerState>().HeadIsUnderwater) return false;
            var entity = BoltNetwork.FindEntity(new Bolt.NetworkId(key.AnchorNetworkId));
            if (!Alive(entity) || !entity.isAttached || !entity.isOwner || entity.source != null
                || entity.networkId.PackedValue != key.AnchorNetworkId) return false;
            controller = entity.GetComponent<ZipLineController>();
            if (!DirectRope(controller) || !Same(controller.entity, entity)
                || controller.gameObject.scene.handle != _carrier.Entity.gameObject.scene.handle
                || !Alive(controller._ropeTrigger) || !controller._ropeTrigger.enabled
                || !controller._ropeTrigger.bounds.Intersects(_carrier.PrimaryCollider.bounds)
                || !controller._ropeTrigger.gameObject.activeInHierarchy || !Alive(controller._connectionPoint)
                || controller._raycastHits == null || controller._raycastHits.Length == 0) return false;
            var capsule = _carrier.PrimaryCollider;
            var trigger = controller._ropeTrigger;
            if (!Physics.ComputePenetration(capsule, capsule.transform.position, capsule.transform.rotation,
                    trigger, trigger.transform.position, trigger.transform.rotation, out _, out _)) return false;
            destination = key.Mode == NativeMode.ZiplineToAnchorA ? controller._anchorA : controller._anchorB;
            if (!Alive(destination)) return false;
            if (!PlayerFlightZipline.TryGetConnectionFrame(controller, _carrier.Entity.transform,
                    out var connection, out var rotation)
                || !Same(controller.GetDestinationAnchor(connection, rotation * Vector3.forward), destination)) return false;
            return Finite(connection) && float.IsFinite(controller._playerGroundClearance)
                && controller._playerGroundClearance > 0
                && controller.CanClearGround(connection, controller._playerGroundClearance, controller._raycastHits, controller._collideMask);
        }

        private void AdmitExit(PlayerModeTransitionProposal proposal)
        {
            if (!_entryApproved || _bridge.HasPendingMovementTransition && proposal.Expected != _entry.Requested) return;
            if (_exitProposal.RequestSequence != 0 && _exitProposal != proposal)
            { Stop("A different exit overtook the retained native detach request."); return; }
            _exitProposal = proposal;
            if (!FlushMotion()) return;
            if (_exit == null)
            {
                if (!_motor.TryCapture(out _, out var captureDetail)) { Detail = captureDetail; return; }
                if (!_motor.TryDetach(_motor.Identity, proposal.Requested.TransitionSequence, out var exit, out var reason))
                { Detail = reason; return; }
                var forward = exit.Rotation * Vector3.forward;
                forward.y = 0;
                if (!Finite(forward) || forward.sqrMagnitude < 0.000001f)
                { Stop("Zipline exit has no upright ground heading."); return; }
                exit = exit with { Rotation = Quaternion.LookRotation(forward.normalized, Vector3.up) };
                _exit = exit;
                _exitRecord = new(PlayerZiplineRecordKind.Exit, exit.Identity, proposal.RequestSequence, exit.ExitSequence,
                    exit.Position, exit.Rotation, exit.Velocity, Vector3.zero, Quaternion.identity, exit.ReachedDestination);
            }
            if (_exitProposal != proposal) { Stop("A different exit overtook the completed native detach."); return; }
            if (!_bridge.TryApproveMovementTransition(proposal, out _, out var detail)) { Detail = detail; return; }
            _exitApproved = true; _nextRecord = 0;
            if (!ResumeExit()) return;
            PublishRecord(_exitRecord);
            PublishExitMotion();
        }

        private bool ResumeExit()
        {
            if (_groundResumed) return true;
            var exit = _exit.Value;
            _carrier.Entity.transform.SetPositionAndRotation(exit.Position, exit.Rotation);
            if (!_ground.TryResumeGroundMode(_exitProposal.Requested, exit.Velocity, Vector3.zero,
                    false, false, out var detail)) { Detail = detail; return false; }
            _groundResumed = true;
            if (!PlayerFlightZipline.ExitContinuation.TryStart(_bridge, _carrier.Body, exit,
                    out _exitContinuation, out detail)) { Stop(detail); return false; }
            Detail = "Native detach committed; the existing ground motor advances while source cleanup is delivered.";
            return true;
        }

        private void PublishExitMotion()
        {
            if (!_groundResumed || _lastExitFixedTime == Time.fixedTimeAsDouble || _exitRecord is not { } receipt) return;
            if (_exitMotionSequence == uint.MaxValue) { Stop("Zipline exit pose ordinal exhausted."); return; }
            var body = _carrier.Body;
            var motion = receipt with { Kind = PlayerZiplineRecordKind.ExitMotion, MotionSequence = _exitMotionSequence + 1,
                Position = body.position, Rotation = Canonical(body.rotation), Velocity = body.velocity,
                ExitElapsed = _exitContinuation.Elapsed, ExitMultiplier = _exitContinuation.Multiplier,
                ExitForce = _exitContinuation.Force, ExitForceActive = _exitContinuation.ForceActive };
            var result = _bridge.SendZiplineRecord(motion);
            if (result == DatagramSendResult.WindowFull) return;
            if (result != DatagramSendResult.Queued) { Stop("Zipline exit pose publication failed: " + result); return; }
            _exitMotionSequence = motion.MotionSequence;
            _lastExitFixedTime = Time.fixedTimeAsDouble;
        }

        private void PublishMotion()
        {
            if (!FlushMotion() || _bridge.HasPendingMovementTransition
                || !_bridge.TryPeekCurrentNativeInput(out var peek) || peek.Key != _entry.Requested
                || !_motor.TryCapture(out var snapshot, out _) || snapshot.Identity != _entryRecord.Value.Identity
                || !_bridge.TryReadCurrentNativeInput(out var input) || input != peek
                || !_bridge.TryCommitSimulatedNativeInput(input.Key, input.Input.Sequence)) return;
            if (_bridge.LastSentStateSequence == uint.MaxValue) { Stop("Zipline state ordinal exhausted."); return; }
            _pendingState = new(input.Key, new PlayerMotionState(_bridge.Plan.Id, _bridge.Plan.Generation,
                _bridge.NetworkId, _bridge.LastSentStateSequence + 1, input.Input.Sequence,
                snapshot.HookPosition, Canonical(snapshot.HookRotation), snapshot.Velocity, PlayerMotionFlags.None)
                { MovementTransitionSequence = input.Key.TransitionSequence });
            FlushMotion();
        }

        private bool FlushMotion()
        {
            if (_pendingState.Motion.Sequence == 0) return true;
            var result = _bridge.SendNativeState(_pendingState);
            if (result == DatagramSendResult.WindowFull) return false;
            if (result != DatagramSendResult.Queued) { Stop("Zipline state publication failed: " + result); return false; }
            _pendingState = default;
            return true;
        }

        private void PublishRecord(PlayerZiplineRecord? record)
        {
            if (record is not { } value || Stopwatch.GetTimestamp() < _nextRecord) return;
            var result = _bridge.SendZiplineRecord(value);
            if (result == DatagramSendResult.WindowFull) return;
            if (result != DatagramSendResult.Queued) { Stop("Zipline receipt publication failed: " + result); return; }
            _nextRecord = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4;
        }

        internal static bool DirectRope(ZipLineController controller) => Alive(controller) && controller.gameObject.activeInHierarchy
            && Alive(controller.entity) && controller.entity.isAttached && Alive(controller._anchorA) && Alive(controller._anchorB)
            && !controller._shouldDaisiyChainZipline && !controller._connectingToAnotherZipline
            && !Alive(controller._ziplineConnectionForAnchorA) && !Alive(controller._ziplineConnectionForAnchorB);

        private void Stop(string reason) { Detail = reason; Dispose(); }
        public void Dispose()
        {
            if (_disposed || Environment.CurrentManagedThreadId != _thread) return;
            _disposed = true;
            GlobalEvents.OnUpdate.Unsubscribe(Pump);
            _bridge.Retired -= Dispose;
            _exitContinuation?.Dispose();
            _motor?.Dispose();
            if (_groundResumed)
            {
                try { if (_carrier.IsCurrentForMotor && Alive(_carrier.Body)) _carrier.Body.isKinematic = true; }
                catch (Exception) { }
            }
        }
        private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        private static Quaternion Canonical(Quaternion q)
            => (q.w != 0 ? q.w < 0 : q.x != 0 ? q.x < 0 : q.y != 0 ? q.y < 0 : q.z < 0)
                ? new Quaternion(-q.x, -q.y, -q.z, -q.w) : q;
        private static bool Alive(UnityEngine.Object value) => value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero && value != null;
        private static bool Same(UnityEngine.Object a, UnityEngine.Object b) => Alive(a) && Alive(b) && a.Pointer == b.Pointer;
    }
}
