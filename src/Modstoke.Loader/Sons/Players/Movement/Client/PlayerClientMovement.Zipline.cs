// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;
using Il2CppInterop.Runtime;
using RedLoader;
using global::Sons.Gameplay.ZipLines;
using TheForest.Utils;
using UnityEngine;
using NativeZiplineHook = global::Sons.Gameplay.ZipLines.ZiplineHook;

namespace Modstoke.Sons
{
    public sealed partial class PlayerClientMovement
    {
        private bool _ziplineEnabled, _applyingZipline, _ziplineExitCallback, _ziplineExitCompleted;
        private ZiplineBinding _zipline;
        private PlayerZiplineRecord? _ziplineExitReceipt;
        private PlayerZipLineAction._PreserveExitVelocity_d__17 _ziplineExitRoutine;
        private long _ziplineNextRequest;
        private uint _ziplineExitMotionSequence;
        private uint _ziplineExitForceSequence;
        private PlayerModeTransitionProposal _ziplineExitProposal;
        private PlayerZiplineRecord? _ziplineLatestExitMotion;

        /// <summary>Bind original-source native actions only after the aggregate owns zipline admission and custody.</summary>
        public bool TryEnableZiplineActions(out string detail)
        {
            detail = "Zipline source routing requires the retained original rig and recognized native hooks.";
            if (Environment.CurrentManagedThreadId != _thread || _disposed || _bridge.IsServer || _ziplineEnabled
                || !_nativeModesInstalled || _nativeMode != null || _transition != null || CartMigrationHeld
                || Time.inFixedTimeStep || _insideFixed || _awaitingPhysics || !RetainedTargetCurrent(true)
                || (_activated && !CheckMode())) return false;
            _ziplineEnabled = true;
            try
            {
                _bridge.Retired += DisposeZipline;
                GlobalEvents.OnLateUpdate.Subscribe(ZiplineLateUpdate);
                detail = "Original zipline callbacks bound to authenticated transitions and server hook identity.";
                return true;
            }
            catch (Exception error)
            { DisposeZipline(); detail = "Zipline source binding failed (" + error.GetType().Name + ")."; return false; }
        }

        private void QueueZiplineEntry(ZipLineController controller)
        {
            if (_transition != null || _nativeMode != null || _zipline != null || !CheckMode()
                || !Same(LocalPlayer.PlayerBase, _rig.Root)
                || _modeSequence == uint.MaxValue || !_bridge.TryGetCommittedMovementMode(out var expected)
                || expected != NativeModeKey.Ground(_modeSequence)
                || !PlayerNativeZiplineRoute.DirectRope(controller)
                || !controller.PlayerCanGrabZipLine() || !Alive(controller._connectionPoint)) return;
            var action = LocalPlayer.PlayerBase.GetComponentInChildren<PlayerZipLineAction>();
            if (!OwnsAction(action) || !action.enabled || action.IsOnZipLine || !float.IsFinite(action._connectedHeightOffset)) return;
            // EnterZipLineMode adopts this connection frame before native endpoint selection.
            if (!PlayerFlightZipline.TryGetConnectionFrame(controller, _rig.Root.transform,
                    out var position, out var rotation)) return;
            var destination = controller.GetDestinationAnchor(position, rotation * Vector3.forward);
            if (!Same(destination, controller._anchorA) && !Same(destination, controller._anchorB)) return;
            var key = new NativeModeKey(Same(destination, controller._anchorA)
                ? NativeMode.ZiplineToAnchorA : NativeMode.ZiplineToAnchorB,
                expected.TransitionSequence + 1, controller.entity.networkId.PackedValue, -1);
            _zipline = new ZiplineBinding(key, action, controller);
            _ziplineNextRequest = 0;
            _transition = new NativeActionTransition(expected, key, action, null, false, false);
            DiscardCapturedStep();
        }

        private void QueueZiplineExit()
        {
            if (_transition != null || _zipline == null || _nativeMode?.Zipline != _zipline
                || _modeSequence == uint.MaxValue || !Current(true)) return;
            _transition = new NativeActionTransition(_zipline.Key, NativeModeKey.Ground(_modeSequence + 1),
                _zipline.Action, null, false, false);
            DiscardCapturedStep();
        }

        // Dispatch before the glider/cart choreography in PumpNativeTransition.
        private void PumpZiplineTransition()
        {
            var transition = _transition;
            var binding = _zipline;
            if (transition == null || binding == null || !IsZiplineMode(transition.NativeKey.Mode)
                || Time.inFixedTimeStep || _insideFixed || _awaitingPhysics) return;
            if (!RetainedTargetCurrent(true) || !OwnsAction(binding.Action) || binding.Key != transition.NativeKey
                || !Same(transition.Action, binding.Action))
            { Stop("Zipline transition lost its original source action or retained rig."); return; }
            if (transition.Proposal.RequestSequence == 0)
            {
                if (!_bridge.TryProposeMovementTransition(transition.Expected, transition.Requested,
                        out var proposal, out var detail)) { NativeTransitionDetail = detail; return; }
                transition.Proposal = proposal;
            }
            if (transition.Entering && !transition.Attempted && Stopwatch.GetTimestamp() >= _ziplineNextRequest)
            {
                if (binding.Action._connectedHeightOffset != binding.ConnectedHeightOffset)
                { Stop("The original zipline action's native hanging height changed during admission."); return; }
                var identity = new PlayerFlightZiplineIdentity(_bridge.Plan.Id, _bridge.Plan.Generation,
                    _bridge.NetworkId, binding.Key.TransitionSequence, binding.Key.AnchorNetworkId, 0,
                    binding.Key.Mode == NativeMode.ZiplineToAnchorA);
                var request = new PlayerZiplineRecord(PlayerZiplineRecordKind.EntryRequest, identity,
                    transition.Proposal.RequestSequence, 0, Vector3.zero, Quaternion.identity,
                    Vector3.zero, Vector3.zero, Quaternion.identity, false)
                    { ConnectedHeightOffset = binding.ConnectedHeightOffset };
                var result = _bridge.SendZiplineRecord(request);
                if (result != DatagramSendResult.Queued && result != DatagramSendResult.WindowFull)
                { Stop("Zipline native action profile publication failed: " + result); return; }
                if (result == DatagramSendResult.Queued)
                    _ziplineNextRequest = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4;
            }
            if (!transition.Committed)
            {
                if (!_bridge.TryPeekMovementTransitionApproval(out var approval)) return;
                if (approval.Proposal != transition.Proposal) { Stop("Zipline approval changed transaction identity."); return; }
                if (approval.Decision == PlayerModeTransitionDecision.Rejected)
                {
                    if (!_bridge.TryCommitMovementTransition(approval, out var rejection)) { NativeTransitionDetail = rejection; return; }
                    if (transition.Entering) _zipline = null;
                    ClearPredictionEpoch(); _transition = null;
                    NativeTransitionDetail = "Zipline request rejected without replaying a native action.";
                    return;
                }
                if (approval.Decision != PlayerModeTransitionDecision.Approved) return;
                var kind = transition.Entering ? PlayerZiplineRecordKind.Entry : PlayerZiplineRecordKind.Exit;
                if (!_bridge.TryReadZiplineRecord(kind, transition.Proposal, out var record)) return;
                if (!transition.Attempted)
                {
                    if (!PlayerNativeZiplineRoute.DirectRope(binding.Controller)
                        || binding.Controller.entity.networkId.PackedValue != record.Identity.ControllerNetworkId
                        || record.Key != binding.Key)
                    { Stop("Approved zipline controller or destination changed before native application."); return; }
                    transition.Attempted = true;
                    _applyingZipline = true;
                    try
                    {
                        if (transition.Entering) ApplyZiplineEntry(binding, record);
                        else ApplyZiplineExit(binding, record);
                    }
                    finally { _applyingZipline = false; }
                    if (_disposed) return;
                }
                if (transition.Entering)
                {
                    if (!binding.Matches(_rig, binding.Key)) { Stop("Native zipline entry was not confirmed; no replay attempted."); return; }
                    transition.NativeCompleted = true;
                }
                else
                {
                    if (!_ziplineExitCallback || _ziplineExitRoutine == null)
                    { Stop("Native zipline exit did not produce its exact callback and velocity iterator."); return; }
                    ApplyZiplineExitMotion(transition);
                    if (_disposed) return;
                    if (_ziplineExitMotionSequence == 0) return;
                    if (_ziplineExitForceSequence == 0 && !_ziplineExitCompleted) return;
                    if (binding.Action.IsOnZipLine || _rig.Root.transform.parent != null)
                    { Stop("Native zipline exit cleanup was not confirmed."); return; }
                    transition.NativeCompleted = true;
                }
                if (!_bridge.TryCommitMovementTransition(approval, out var commitDetail)) { NativeTransitionDetail = commitDetail; return; }
                transition.Committed = true;
            }
            if (transition.Entering)
            {
                if (!CanBindNative(binding.Key) || !BindNative(new NativeBinding(binding.Key, _rig, binding), out var bindDetail))
                { NativeTransitionDetail = "Zipline action completed; awaiting exact native movement binding."; return; }
                NativeTransitionDetail = bindDetail;
                _transition = null;
            }
            else if (TryResumeGround(transition.Requested.TransitionSequence, out var resumeDetail))
            {
                // The native force iterator overlaps ordinary FPC controls. Its identity
                // remains retained until completion, independent of the mode receipt.
                _transition = null;
                NativeTransitionDetail = resumeDetail;
            }
            else NativeTransitionDetail = resumeDetail;
        }

        private void ApplyZiplineEntry(ZiplineBinding binding, PlayerZiplineRecord record)
        {
            if (_bridge.IsServer || !Same(LocalPlayer.Transform, _rig.Root.transform)
                || !Same(LocalPlayer.PlayerBase, _rig.Root)
                || !Same(LocalPlayer.Rigidbody, _rig.Body) || binding.Action.IsOnZipLine
                || !SupportedMode() || !binding.Controller.PlayerCanGrabZipLine()
                || record.ConnectedHeightOffset != binding.ConnectedHeightOffset
                || binding.Action._connectedHeightOffset != binding.ConnectedHeightOffset)
                throw new InvalidOperationException("Zipline entry is not on the original source rig.");
            binding.Identity = record.Identity;
            binding.Controller.ShowUI(false);
            var connection = binding.Controller._connectionPoint;
            var previousPosition = connection.position;
            var previousRotation = connection.rotation;
            try
            {
                // Use this actor's admitted frame only for the synchronous native action.
                // The rope's focused UI frame belongs to its own native LateUpdate.
                connection.SetPositionAndRotation(record.Position + record.Rotation * record.RiderLocalPosition
                    - Vector3.up * binding.ConnectedHeightOffset, record.Rotation * record.RiderLocalRotation);
                binding.Action.EnterZipLineMode(connection, binding.Controller);
            }
            finally { connection.SetPositionAndRotation(previousPosition, previousRotation); }
            var destination = record.Identity.DestinationIsAnchorA ? binding.Controller._anchorA : binding.Controller._anchorB;
            // Native client hooks are LOCAL objects, not the server Bolt hook. Keep this
            // exact local hook for its native OnDetached callback; never recreate it on ACK.
            binding.Hook = binding.Controller.CreateAndAttachToHook(_rig.Root.transform, destination, false, true, 0);
            if (!Alive(binding.Hook) || !Alive(binding.Hook._rigidbody) || !Alive(binding.Hook.entity)
                || binding.Hook.entity.isAttached || binding.Hook.OnDetached != null)
                throw new InvalidOperationException("Native source hook creation or callback custody changed.");
            binding.HookInstance = binding.Hook.GetInstanceID();
            binding.Hook.enabled = false;
            binding.Hook._rigidbody.isKinematic = true;
            var method = binding.Action.GetIl2CppType().GetMethod(nameof(PlayerZipLineAction.ExitZipLineMode));
            binding.Detached = Il2CppSystem.Delegate.CreateDelegate(Il2CppType.Of<Il2CppSystem.Action<NativeZiplineHook>>(),
                binding.Action, method).Cast<Il2CppSystem.Action<NativeZiplineHook>>();
            if (binding.Detached?.Target?.Pointer != binding.Action.Pointer
                || binding.Detached.Method?.Name != nameof(PlayerZipLineAction.ExitZipLineMode))
                throw new InvalidOperationException("Native source exit callback could not bind the exact action.");
            binding.Hook.OnDetached = binding.Detached;
            binding.DetachRequested = binding.Hook.OnDetachRequested?.Pointer ?? IntPtr.Zero;
            binding.LocalPosition = record.RiderLocalPosition;
            binding.LocalRotation = record.RiderLocalRotation;
            _rig.Root.transform.localPosition = binding.LocalPosition;
            _rig.Root.transform.localRotation = binding.LocalRotation;
            ApplyZiplinePose(binding, record.Position, record.Rotation);
        }

        private void ApplyZiplineExit(ZiplineBinding binding, PlayerZiplineRecord record)
        {
            if (!binding.Matches(_rig, binding.Key) || binding.Identity != record.Identity)
                throw new InvalidOperationException("Zipline exit does not own this exact source/server hook pair.");
            _ziplineExitReceipt = record;
            _ziplineExitCallback = _ziplineExitCompleted = false;
            _ziplineExitRoutine = null;
            _ziplineExitMotionSequence = _ziplineExitForceSequence = 0;
            _ziplineLatestExitMotion = null;
            _ziplineExitProposal = _transition.Proposal;
            var hook = binding.Hook;
            hook.HasReachedDestination = record.ReachedDestination;
            // DetachRider inlines its Rigidbody velocity read. Seed the exact approved
            // velocity synchronously; no Unity simulation occurs before native cleanup.
            hook._rigidbody.isKinematic = false;
            hook._rigidbody.velocity = record.Velocity;
            binding.Controller.DetachRiderFromHook(_rig.Root.transform);
            if (!_ziplineExitCallback || binding.Action.IsOnZipLine || _rig.Root.transform.parent != null)
                throw new InvalidOperationException("Native zipline detach did not complete its original source callback.");
            _rig.Body.position = record.Position;
            _rig.Body.rotation = record.Rotation;
        }

        private void ApplyZiplineExitMotion(NativeActionTransition transition)
        {
            if (!_bridge.TryReadZiplineExitMotion(transition.Proposal, out var motion)
                || motion.MotionSequence <= _ziplineExitMotionSequence) return;
            if (_ziplineExitReceipt is not { } exit || motion.Identity != exit.Identity
                || motion.ExitSequence != exit.ExitSequence || !RetainedTargetCurrent(true)
                || _rig.Root.transform.parent != null || _rig.Body.isKinematic)
            { Stop("Zipline exit projection lost its exact detached source body."); return; }
            _rig.Body.position = motion.Position;
            _rig.Body.rotation = motion.Rotation;
            _rig.Body.velocity = motion.Velocity;
            _rig.Root.transform.SetPositionAndRotation(motion.Position, motion.Rotation);
            _ziplineExitMotionSequence = motion.MotionSequence;
            _ziplineLatestExitMotion = motion;
        }

        // RetainedRig.GroundMode/SwimMode may admit _doingExitVelocity ONLY through
        // this exact original-action/iterator/receipt binding, never a blanket bypass.
        private static bool IsRetainedZiplineExit(FirstPersonCharacter character)
        {
            var active = _active;
            return active != null && !active._disposed && active._ziplineEnabled
                && Environment.CurrentManagedThreadId == active._thread && Same(character, active._rig.Character)
                && active._zipline != null && active._ziplineExitReceipt is { } exit
                && active._zipline.Identity == exit.Identity && active._ziplineExitCallback
                && active._ziplineExitRoutine != null && !active._ziplineExitRoutine.WasCollected
                && Same(active._ziplineExitRoutine.__4__this, active._zipline.Action)
                && active.OwnsAction(active._zipline.Action) && !active._zipline.Action.IsOnZipLine
                && active._bridge.IsCommittedMovementMode(NativeModeKey.Ground(exit.ExitSequence));
        }

        private void ZiplineLateUpdate()
        {
            if (_disposed) { DisposeZipline(); return; }
            if (!_ziplineEnabled || !_activated || _zipline == null || Time.inFixedTimeStep || CartMigrationHeld) return;
            try
            {
                if (_ziplineExitReceipt != null && _transition == null)
                {
                    if (_bridge.TryReadZiplineExitMotion(_ziplineExitProposal, out var motion)
                        && (_ziplineLatestExitMotion is not { } old || old.MotionSequence < motion.MotionSequence))
                        _ziplineLatestExitMotion = motion;
                    if (_ziplineExitCompleted)
                    {
                        _zipline = null; _ziplineExitReceipt = _ziplineLatestExitMotion = null; _ziplineExitRoutine = null;
                        _ziplineExitProposal = default; _ziplineExitCallback = _ziplineExitCompleted = false;
                        _ziplineExitMotionSequence = _ziplineExitForceSequence = 0;
                    }
                    return;
                }
                if (_transition != null || _nativeMode?.Zipline != _zipline) return;
                if (!Current(true)) { Stop("Zipline source hook/action lifetime changed."); return; }
                if (_bridge.HasZiplineTerminal(_zipline.Identity)) { QueueZiplineExit(); return; }
                ReceiveNativeState();
                if (_disposed) return;
                var state = _nativeState;
                if (state.Motion.Sequence != 0)
                {
                    _nativeState = default;
                    if (state.Key != _zipline.Key || FindNativePrediction(state.Motion.InputSequence) < 0)
                    { Stop("Zipline pose lost its exact sent-input correlation."); return; }
                    ApplyZiplinePose(_zipline, state.Motion.Position, state.Motion.Rotation);
                    RetireNativePredictions(state.Motion);
                }
                var input = CaptureNativeInput(Vector2.zero, false, false, false, 0);
                RecordNativePrediction(input, _zipline.Hook.transform.position, _zipline.Hook.transform.rotation, Vector3.zero);
            }
            catch (Exception error) { Stop("Zipline source projection failed (" + error.GetType().Name + ")."); }
        }

        private static void ApplyZiplinePose(ZiplineBinding binding, Vector3 position, Quaternion rotation)
        {
            if (!Finite(position) || !UnitRotation(rotation) || !Alive(binding.Hook) || binding.Hook.enabled
                || !Alive(binding.Hook._rigidbody) || !binding.Hook._rigidbody.isKinematic)
                throw new InvalidOperationException("Zipline projection lost its held native source hook.");
            binding.Hook._rigidbody.position = position;
            binding.Hook._rigidbody.rotation = rotation;
            binding.Hook.transform.SetPositionAndRotation(position, rotation);
        }

        // RetainedRig and RigSlot allow only this exact root-to-hook parent edge.
        private static bool IsRetainedZiplineParent(Transform root, Transform parent)
        {
            var active = _active;
            var binding = active?._zipline;
            return active != null && !active._disposed && Environment.CurrentManagedThreadId == active._thread
                && active._ziplineEnabled && binding != null && Same(root, active._rig.Root.transform)
                && Alive(binding.Hook) && binding.Hook.GetInstanceID() == binding.HookInstance
                && Same(parent, binding.Hook.transform) && Same(binding.Hook.RiderTransform, root);
        }

        private void DisposeZipline()
        {
            if (!_ziplineEnabled || Environment.CurrentManagedThreadId != _thread) return;
            _ziplineEnabled = false;
            GlobalEvents.OnLateUpdate.Unsubscribe(ZiplineLateUpdate);
            _bridge.Retired -= DisposeZipline;
            if (_zipline == null) return;
            try
            {
                if (Alive(_zipline.Hook))
                {
                    _zipline.Hook.enabled = false;
                    if (Alive(_zipline.Hook._rigidbody)) _zipline.Hook._rigidbody.isKinematic = true;
                }
                if (Alive(_rig.Body)) _rig.Body.isKinematic = true;
            }
            catch (Exception) { }
        }

        private sealed class ZiplineBinding
        {
            internal readonly NativeModeKey Key;
            internal readonly PlayerZipLineAction Action;
            internal readonly ZipLineController Controller;
            internal readonly float ConnectedHeightOffset;
            private readonly int _actionId, _controllerId;
            internal PlayerFlightZiplineIdentity Identity;
            internal NativeZiplineHook Hook;
            internal int HookInstance;
            internal Vector3 LocalPosition;
            internal Quaternion LocalRotation;
            internal Il2CppSystem.Action<NativeZiplineHook> Detached;
            internal IntPtr DetachRequested;

            internal ZiplineBinding(NativeModeKey key, PlayerZipLineAction action, ZipLineController controller)
            {
                Key = key; Action = action; Controller = controller;
                ConnectedHeightOffset = action._connectedHeightOffset;
                _actionId = action.GetInstanceID(); _controllerId = controller.GetInstanceID();
            }

            internal bool Matches(RetainedRig rig, NativeModeKey key)
            {
                if (key != Key || !Alive(Action) || Action.GetInstanceID() != _actionId || !Action.IsOnZipLine
                    || !Same(Action._zipLineController, Controller) || !Same(LocalPlayer.Transform, rig.Root.transform)
                    || !PlayerNativeZiplineRoute.DirectRope(Controller) || Controller.GetInstanceID() != _controllerId
                    || Controller.entity.networkId.PackedValue != Key.AnchorNetworkId || !Alive(Hook)
                    || !Alive(Hook.entity) || Hook.entity.isAttached
                    || Hook.GetInstanceID() != HookInstance || Hook.enabled || !Hook._riderIsAPlayer
                    || !Same(Hook.RiderTransform, rig.Root.transform) || !Same(rig.Root.transform.parent, Hook.transform)
                    || !Same(Hook.DestinationAnchor, Key.Mode == NativeMode.ZiplineToAnchorA ? Controller._anchorA : Controller._anchorB)
                    || !Same(Hook.OnDetached, Detached) || (Hook.OnDetachRequested?.Pointer ?? IntPtr.Zero) != DetachRequested
                    || Detached?.Target?.Pointer != Action.Pointer || !Alive(Hook._rigidbody) || !Hook._rigidbody.isKinematic
                    || !rig.Body.isKinematic || rig.Character.enabled || !UnitScale(Hook.transform.lossyScale)
                    || Hook.gameObject.scene.handle != rig.Root.scene.handle
                    || !Exact(rig.Root.transform.localPosition, LocalPosition)
                    || !SameOrientation(rig.Root.transform.localRotation, LocalRotation)) return false;
                var hooks = Controller._hooks;
                if (hooks == null || hooks.WasCollected) return false;
                int count = 0;
                for (int i = 0; i < hooks.Count; i++) if (Same(hooks[i], Hook)) count++;
                return count == 1 && Identity.HookNetworkId != 0;
            }
        }
    }
}
