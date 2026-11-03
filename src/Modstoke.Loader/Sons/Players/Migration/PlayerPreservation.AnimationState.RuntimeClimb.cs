// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using UnityEngine;
using NativeModeKey = Modstoke.Sons.PlayerClientMovement.NativeModeKey;

namespace Modstoke.Sons
{
    public static partial class PlayerPreservation
    {
        internal sealed partial class AnimationRuntimeBinding
        {
            private enum ClimbLeasePhase { Acquiring, Held, Releasing, Faulted, Released }

            private sealed class ClimbRootMotionLease
            {
                internal readonly PlayerClimbMotor Motor;
                internal readonly PlayerClimbDriver Driver;
                internal readonly NativeModeKey Key;
                internal readonly int DriverInstance, ControllerInstance;
                internal readonly AnimatorUpdateMode UpdateMode;
                internal readonly AnimatorCullingMode CullingMode;
                internal ClimbLeasePhase Phase;
                internal bool Evaluating;
                internal AnimationData Handoff;

                internal ClimbRootMotionLease(PlayerClimbMotor motor,
                    PlayerClimbDriver driver, Animator animator)
                {
                    Motor = motor; Driver = driver; Key = motor.Key;
                    DriverInstance = driver.GetInstanceID();
                    ControllerInstance = animator.runtimeAnimatorController.GetInstanceID();
                    UpdateMode = animator.updateMode; CullingMode = animator.cullingMode;
                }
            }

            private readonly PlayerBridge _climbBridge;
            private ClimbRootMotionLease _climbLease;
            internal bool ClimbCallInProgress => _busy || _continuationDepth != 0;

            private void ValidateClimbRootMotion()
            {
                var lease = _climbLease;
                if (lease == null)
                {
                    Require(!Animator.applyRootMotion, "Root motion has no exact server climb lease.");
                    return;
                }
                Require(IsServer && _active && !_disposed && !_failed
                    && RuntimeAnimationBindings.TryGetValue(Animator.Pointer, out var owner) && ReferenceEquals(owner, this)
                    && ClimbBridgeCurrent() && _controller.GetInstanceID() == lease.ControllerInstance
                    && lease.Driver != null && !lease.Driver.WasCollected && lease.Driver
                    && lease.Driver.GetInstanceID() == lease.DriverInstance && lease.Driver.isActiveAndEnabled
                    && lease.Driver.IsBoundTo(lease.Motor)
                    && lease.Motor.MatchesAnimationLease(this, _climbBridge, Animator, _controller, lease.Driver,
                        lease.Key, lease.Phase == ClimbLeasePhase.Releasing),
                    "The exact climb binding, mode, motor or root-motion receiver changed.");
                var receivers = Animator.GetComponents<PlayerClimbDriver>();
                Require(receivers.Length == 1 && receivers[0].Pointer == lease.Driver.Pointer
                    && Animator.GetComponent<PlayerAnimatorControl>() == null,
                    "Climb root motion requires its sole actor-owned receiver, without a native local-player writer.");
                if (lease.Phase == ClimbLeasePhase.Held)
                    Require(Animator.applyRootMotion && Animator.updateMode == AnimatorUpdateMode.AnimatePhysics
                        && Animator.cullingMode == AnimatorCullingMode.AlwaysAnimate,
                        "Climb animation settings lost their exact physics owner.");
                else
                    Require((lease.Phase == ClimbLeasePhase.Acquiring || lease.Phase == ClimbLeasePhase.Releasing)
                        && _busy && lease.Motor.IsExecuting && lease.Motor.IsPaused,
                        "A changing or retired climb lease cannot authorize animation writers.");
            }

            internal bool TryValidateClimbMotor(PlayerClimbMotor motor)
            {
                if (_thread != Environment.CurrentManagedThreadId || !Enter()) return false;
                try
                {
                    Require(_climbLease?.Phase == ClimbLeasePhase.Held && ReferenceEquals(_climbLease.Motor, motor),
                        "The motor no longer owns this animation lease.");
                    Validate(true, false);
                    return true;
                }
                catch (Exception) { return false; }
                finally { _busy = false; }
            }

            private bool ClimbBridgeCurrent()
                => _climbBridge?.IsServer == true && _climbBridge.IsGameplayActive && _climbBridge.NetworkId == _id
                    && _climbBridge.TryReadCurrentTarget(out var entity) && entity.Pointer == _entity.Pointer
                    && entity.GetInstanceID() == _entityInstance;

            // Only this synchronous zero-time restore may absorb a callback while the motor
            // is busy. Ordinary physics callbacks must pass through the motor's step guards.
            internal bool IsClimbRestoreEvaluation(PlayerClimbMotor motor,
                PlayerClimbDriver driver)
                => _thread == Environment.CurrentManagedThreadId && _busy && !_reentered && !_disposed && !_failed
                    && _climbLease?.Evaluating == true && ReferenceEquals(_climbLease.Motor, motor)
                    && ReferenceEquals(_climbLease.Driver, driver) && driver.IsBoundTo(motor)
                    && motor.IsExecuting && motor.IsPaused;

            internal bool OwnsClimbSafetyStop(PlayerClimbMotor motor, PlayerClimbDriver driver)
                => _thread == Environment.CurrentManagedThreadId && !_disposed
                    && _climbLease != null && _climbLease.Phase != ClimbLeasePhase.Released
                    && ReferenceEquals(_climbLease.Motor, motor) && ReferenceEquals(_climbLease.Driver, driver)
                    && RuntimeAnimationBindings.TryGetValue(Animator.Pointer, out var owner) && ReferenceEquals(owner, this)
                    && Animator is not null && !Animator.WasCollected && Animator && Animator.GetInstanceID() == _animatorInstance
                    && !_state.WasCollected && _state.Animator?.Pointer == Animator.Pointer
                    && !_attachment.WasCollected && _attachment.Serializer?.Pointer == _state.Pointer
                    && _controller is not null && !_controller.WasCollected && _controller && _controller.GetInstanceID() == _climbLease.ControllerInstance
                    && Animator.runtimeAnimatorController?.Pointer == _controller.Pointer
                    && driver is not null && !driver.WasCollected && driver && driver.GetInstanceID() == _climbLease.DriverInstance
                    && driver.gameObject.Pointer == Animator.gameObject.Pointer && driver.IsBoundTo(motor);

            internal bool TryAcquireClimbRootMotion(PlayerClimbMotor motor,
                PlayerClimbDriver driver, out string detail)
            {
                detail = "Climb requires its active server animation binding and a drained settled action boundary.";
                if (_thread != Environment.CurrentManagedThreadId || !Enter()) return false;
                bool attempted = false;
                ClimbRootMotionLease acquired = null;
                try
                {
                    Validate(true);
                    Require(IsServer && _climbLease == null && ClimbBridgeCurrent() && _continuationDepth == 0
                        && !HasActiveNativeAnimationCall && !Time.inFixedTimeStep && motor != null && driver != null
                        && motor.IsExecuting && motor.IsPaused
                        && motor.MatchesAnimationLease(this, _climbBridge, Animator, _controller, driver, motor.Key, false),
                        "Climb preparation does not match the active server binding and committed actor mode.");
                    var snapshot = CaptureClimbSnapshot();
                    var position = _entity.transform.position;
                    var rotation = _entity.transform.rotation;
                    _climbLease = acquired = new ClimbRootMotionLease(motor, driver, Animator);
                    ValidateClimbRootMotion();
                    attempted = true;
                    Animator.speed = 0;
                    RequireClimbMutationCurrent();
                    Animator.updateMode = AnimatorUpdateMode.AnimatePhysics;
                    RequireClimbMutationCurrent();
                    Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    RequireClimbMutationCurrent();
                    Animator.applyRootMotion = true;
                    RestoreClimbSnapshot(snapshot, true, AnimatorUpdateMode.AnimatePhysics, AnimatorCullingMode.AlwaysAnimate);
                    Require(position.Equals(_entity.transform.position) && rotation.Equals(_entity.transform.rotation),
                        "Climb animation restoration moved the actor outside its physics consumer.");
                    _climbLease.Phase = ClimbLeasePhase.Held;
                    Validate(true);
                    detail = "Exact climb root-motion lease acquired; all settled layers and parameters restored with the clock paused.";
                    return true;
                }
                catch (Exception error)
                {
                    if (attempted) FailClimbLease();
                    else if (acquired != null && ReferenceEquals(_climbLease, acquired)) _climbLease = null;
                    detail = Describe(error);
                    return false;
                }
                finally { _busy = false; }
            }

            internal bool TryCaptureClimbHandoff(PlayerClimbMotor motor, out string detail)
            {
                detail = "The exact paused climb lease is required for an animation handoff.";
                if (_thread != Environment.CurrentManagedThreadId || !Enter()) return false;
                try
                {
                    Require(_climbLease?.Phase == ClimbLeasePhase.Held && ReferenceEquals(_climbLease.Motor, motor)
                        && motor.IsPaused && motor.IsExecuting && Animator.speed == 0, detail);
                    Validate(true);
                    var snapshot = CaptureClimbSnapshot();
                    _climbLease.Handoff = snapshot;
                    detail = "All settled climb layers and parameters captured for this same-lease cancellation boundary.";
                    return true;
                }
                catch (Exception error) { detail = Describe(error); return false; }
                finally { _busy = false; }
            }

            internal bool TryValidateClimbHandoff(PlayerClimbMotor motor)
            {
                if (_thread != Environment.CurrentManagedThreadId || !Enter()) return false;
                try
                {
                    Require(_climbLease?.Phase == ClimbLeasePhase.Held && ReferenceEquals(_climbLease.Motor, motor)
                        && _climbLease.Handoff != null && motor.IsPaused, "No exact climb animation handoff remains.");
                    Validate(true);
                    VerifyAnimation(_climbLease.Handoff, CaptureAnimation(_entity, _state, Animator));
                    Validate(true);
                    return true;
                }
                catch (Exception) { return false; }
                finally { _busy = false; }
            }

            internal bool TryReleaseClimbRootMotion(PlayerClimbMotor motor,
                uint committedExitSequence, out string detail)
            {
                detail = "Climb release requires a drained native exit and its exact next committed ground mode.";
                if (_thread != Environment.CurrentManagedThreadId || !Enter()) return false;
                bool attempted = false;
                var lease = _climbLease;
                try
                {
                    Require(lease?.Phase == ClimbLeasePhase.Held && ReferenceEquals(lease.Motor, motor)
                        && lease.Handoff != null && lease.Key.TransitionSequence != uint.MaxValue
                        && committedExitSequence == lease.Key.TransitionSequence + 1
                        && motor.IsExecuting && motor.IsPaused && Animator.speed == 0
                        && !Time.inFixedTimeStep && _continuationDepth == 0 && !HasActiveNativeAnimationCall
                        && motor.MatchesAnimationLease(this, _climbBridge, Animator, _controller, lease.Driver, lease.Key, true), detail);
                    lease.Phase = ClimbLeasePhase.Releasing;
                    Validate(true);
                    // Preserve the current native exit state, never replay the entry snapshot.
                    var snapshot = CaptureClimbSnapshot();
                    var position = _entity.transform.position;
                    var rotation = _entity.transform.rotation;
                    RequireClimbMutationCurrent();
                    attempted = true;
                    Animator.applyRootMotion = false;
                    RequireClimbMutationCurrent();
                    Animator.updateMode = lease.UpdateMode;
                    RequireClimbMutationCurrent();
                    Animator.cullingMode = lease.CullingMode;
                    RestoreClimbSnapshot(snapshot, false, lease.UpdateMode, lease.CullingMode);
                    Require(position.Equals(_entity.transform.position) && rotation.Equals(_entity.transform.rotation),
                        "Ground animation restoration moved the actor outside its physics consumer.");
                    lease.Phase = ClimbLeasePhase.Released;
                    _climbLease = null;
                    Validate(true);
                    detail = "Climb root motion released; current settled exit animation preserved and left paused for ground activation.";
                    return true;
                }
                catch (Exception error)
                {
                    if (attempted) { _climbLease = lease; FailClimbLease(); }
                    else if (lease?.Phase == ClimbLeasePhase.Releasing) lease.Phase = ClimbLeasePhase.Held;
                    detail = Describe(error);
                    return false;
                }
                finally { _busy = false; }
            }

            private void RequireClimbMutationCurrent()
            {
                Require(_climbLease != null && (_climbLease.Phase == ClimbLeasePhase.Acquiring
                    || _climbLease.Phase == ClimbLeasePhase.Releasing), "No exact climb settings operation is active.");
                Validate(true, false);
            }

            private AnimationData CaptureClimbSnapshot()
            {
                var snapshot = CaptureAnimation(_entity, _state, Animator);
                // Reuse the canonical value/schema bounds, including non-networked parameters.
                snapshot = DecodeAnimation(EncodeAnimation(snapshot), _id);
                VerifyAnimation(snapshot, CaptureAnimation(_entity, _state, Animator));
                Validate(true);
                return snapshot;
            }

            private void RestoreClimbSnapshot(AnimationData snapshot, bool rootMotion,
                AnimatorUpdateMode updateMode, AnimatorCullingMode cullingMode)
            {
                RequireClimbMutationCurrent();
                Animator.speed = 0;
                // The shared schema includes these three settings. Change only their encoded
                // fields; retain and verify every controller, avatar, clip and other setting.
                var schema = (byte[])snapshot.ControllerSchema.Clone();
                using (var stream = new MemoryStream(schema, true))
                using (var reader = new BinaryReader(stream, Utf8, true))
                using (var writer = new BinaryWriter(stream, Utf8, true))
                {
                    ReadString(reader); ReadString(reader); ReadString(reader); ReadBoolean(reader);
                    writer.Write(rootMotion); writer.Write((int)updateMode); writer.Write((int)cullingMode);
                }
                ValidateControllerSchema(schema);
                var expected = snapshot with { ControllerSchema = schema, Speed = 0 };
                var current = CaptureAnimation(_entity, _state, Animator);
                RequireAnimationSchema(expected, current);
                for (int i = 0; i < expected.Layers.Length; i++)
                    Require(expected.Layers[i].State == 0 ? current.Layers[i].State == 0
                        : Animator.HasState(i, expected.Layers[i].State), "A captured climb layer state is unavailable.");
                foreach (var parameter in expected.Parameters)
                {
                    RequireClimbMutationCurrent();
                    if (parameter.Kind == AnimatorControllerParameterType.Trigger) Animator.ResetTrigger(parameter.Hash);
                    else if (!parameter.Curve) WriteAnimationParameter(Animator, parameter);
                }
                for (int i = 0; i < expected.Layers.Length; i++)
                {
                    RequireClimbMutationCurrent();
                    var layer = expected.Layers[i];
                    Animator.SetLayerWeight(i, layer.Weight);
                    RequireClimbMutationCurrent();
                    if (layer.State != 0) Animator.Play(layer.State, i, layer.Time);
                }
                RequireClimbMutationCurrent();
                var lease = _climbLease;
                lease.Evaluating = true;
                try { Animator.Update(0f); }
                finally { lease.Evaluating = false; }
                RequireClimbMutationCurrent();
                VerifyAnimation(expected, CaptureAnimation(_entity, _state, Animator));
                RequireClimbMutationCurrent();
            }

            private void FailClimbLease()
            {
                _failed = true;
                if (_climbLease != null) _climbLease.Phase = ClimbLeasePhase.Faulted;
            }
        }
    }
}
