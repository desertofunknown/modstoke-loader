// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Gameplay;
using UnityEngine;
using NativeModeKey = Modstoke.Sons.PlayerClientMovement.NativeModeKey;
using NativeInput = Modstoke.Sons.PlayerClientMovement.NativeInput;
using NativeState = Modstoke.Sons.PlayerClientMovement.NativeState;

namespace Modstoke.Sons
{
    public readonly record struct PlayerFlightGliderSnapshot(Guid MigrationId, ulong Generation,
        ulong NetworkId, NativeModeKey Key, PlayerFlightGliderState Dynamics,
        Vector3 GuidePosition, Quaternion GuideRotation, Vector3 Velocity, Vector3 AngularVelocity,
        Vector3 RiderPosition, Quaternion RiderRotation, bool DropRequired, bool ExitRequired);

    public readonly record struct PlayerFlightGliderExit(Guid MigrationId, ulong Generation,
        ulong NetworkId, NativeModeKey Key, uint ExitSequence, Vector3 Position, Quaternion Rotation,
        Vector3 Velocity, bool DropRequired);

    public sealed partial class PlayerFlightGlider
    {
        private uint _inputSequence, _lastCommittedInput;
        private bool _awaitingPhysics, _dropRequired, _rotationPending;
        private NativeState _publication;
        private PlayerFlightGliderExit? _exit;

        /// <summary>
        /// Ordinary Update, before LateUpdate. Complete the preceding physics result before
        /// selecting another input. Missing continuous input becomes neutral, never a drop.
        /// </summary>
        public bool TryUpdateFromBridge(out string detail)
        {
            detail = "Glider Update requires its exact committed movement identity.";
            if (_disposed || Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep
                || _inputFrame == Time.frameCount || _lateFrame == Time.frameCount) return false;
            try
            {
                TryCompletePhysicsStep(out detail);
                if (_disposed || _awaitingPhysics || ExitRequired) return false;
                Vector2 move = Vector2.zero;
                bool canSelect = _publication.Motion.Sequence == 0;
                NativeInput input;
                bool hasInput = canSelect ? _bridge.TryReadCurrentNativeInput(out input)
                    : _bridge.TryPeekCurrentNativeInput(out input);
                if (hasInput)
                {
                    if (input.Key != Key) return Stop("Glider input belongs to a different mode.", out detail);
                    move = input.Input.Movement;
                    _inputSequence = canSelect && input.Input.Sequence > _lastCommittedInput ? input.Input.Sequence : 0;
                }
                else _inputSequence = 0;
                return TryUpdateInput(move, out detail);
            }
            catch (Exception error) { return Stop("Glider bridge input failed (" + error.GetType().Name + ").", out detail); }
        }

        /// <summary>
        /// Run from ordinary Update after Unity physics, before native frame damping. A
        /// full send window retains the exact publication and prevents selecting a new ACK.
        /// </summary>
        public bool TryCompletePhysicsStep(out string detail)
        {
            detail = "Glider post-physics completion requires the ordinary frame phase.";
            if (_disposed || Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep) return false;
            try
            {
                if (!Current()) return Stop("Glider post-physics binding changed.", out detail);
                if (!_awaitingPhysics)
                {
                    if (_publication.Motion.Sequence != 0) return TrySendPublication(out detail);
                    detail = "No glider physics result is pending.";
                    return true;
                }
                if (double.IsNaN(_fixedTime)) { detail = "Glider entry impulse is waiting for its first physics step."; return false; }
                Vector3 position = _body.position, velocity = _body.velocity;
                Quaternion rotation = _body.rotation;
                if (!Finite(position) || !Finite(velocity) || !Finite(rotation))
                    return Stop("Glider physics returned a non-finite pose.", out detail);
                _root.SetPositionAndRotation(position + rotation * _guideOffset, rotation * _guideRotation);
                _awaitingPhysics = false;
                _rotationPending = false;
                if (_inputSequence == 0)
                {
                    if (_publication.Motion.Sequence != 0) return TrySendPublication(out detail);
                    detail = "Glider coast step completed without an input ACK.";
                    return true;
                }
                if (!_bridge.TryCommitSimulatedNativeInput(Key, _inputSequence))
                    return Stop("Glider physics input could not be committed.", out detail);
                _lastCommittedInput = _inputSequence;
                _inputSequence = 0;
                if (_bridge.LastSentStateSequence == uint.MaxValue)
                    return Stop("Glider publication sequence exhausted.", out detail);
                var motion = new PlayerMotionState(_bridge.Plan.Id, _bridge.Plan.Generation,
                    _bridge.NetworkId, _bridge.LastSentStateSequence + 1, _lastCommittedInput,
                    position, rotation, velocity, PlayerMotionFlags.None)
                    { MovementTransitionSequence = Key.TransitionSequence };
                _publication = new NativeState(Key, motion);
                return TrySendPublication(out detail);
            }
            catch (Exception error) { return Stop("Glider physics completion failed (" + error.GetType().Name + ").", out detail); }
        }

        private bool TrySendPublication(out string detail)
        {
            var result = _bridge.SendNativeState(_publication);
            if (result == DatagramSendResult.WindowFull)
            { detail = "Glider physics publication is waiting for transport credit."; return false; }
            if (result != DatagramSendResult.Queued)
                return Stop("Glider physics publication failed (" + result + ").", out detail);
            _publication = default;
            detail = "Exact glider physics input committed and guide state queued.";
            return true;
        }

        /// <summary>Capture between completed steps; adoption must not replay entry uplift.</summary>
        public bool TryCapture(out PlayerFlightGliderSnapshot snapshot, out string detail)
        {
            snapshot = default;
            detail = "Finish glider physics/publication before capturing its active state.";
            if (_disposed || Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep
                || _awaitingPhysics || _inputSequence != 0 || _publication.Motion.Sequence != 0
                || _rotationPending || _lateFrame == Time.frameCount) return false;
            try
            {
                if (!Current()) return Stop("Glider capture binding changed.", out detail);
                snapshot = new(_bridge.Plan.Id, _bridge.Plan.Generation, _bridge.NetworkId, Key, _state,
                    _body.position, _body.rotation, _body.velocity, _body.angularVelocity,
                    _root.position, _root.rotation, _dropRequired, ExitRequired);
                detail = "Active glider guide, rider, dynamics and pending exit preserved.";
                return true;
            }
            catch (Exception error) { return Stop("Glider capture failed (" + error.GetType().Name + ").", out detail); }
        }

        /// <summary>
        /// Restore an approved capture onto already prepared bodies before any step. This
        /// preserves the original guide frame and does not call BeginGliding or apply uplift.
        /// </summary>
        public static bool TryRestore(PlayerBridge bridge, Rigidbody guideBody,
            HangGliderSettings settings, PlayerFlightGliderSnapshot snapshot,
            Func<bool> hasExclusiveAuthority, out PlayerFlightGlider motor, out string detail)
        {
            motor = null;
            detail = "An approved exact-identity active glider capture and prepared bodies are required.";
            if (bridge == null || snapshot.MigrationId != bridge.Plan.Id || snapshot.Generation != bridge.Plan.Generation
                || snapshot.NetworkId != bridge.NetworkId || !Finite(snapshot.GuidePosition) || !Finite(snapshot.GuideRotation)
                || !Finite(snapshot.RiderPosition) || !Finite(snapshot.RiderRotation) || !Finite(snapshot.Velocity)
                || !Finite(snapshot.AngularVelocity) || (snapshot.DropRequired && !snapshot.ExitRequired)) return false;
            try
            {
                if (!bridge.TryReadCurrentTarget(out var entity) || !Alive(guideBody)
                    || (guideBody.position - snapshot.GuidePosition).sqrMagnitude > 0.000001f
                    || Quaternion.Angle(guideBody.rotation, snapshot.GuideRotation) > 0.01f
                    || (entity.transform.position - snapshot.RiderPosition).sqrMagnitude > 0.000001f
                    || Quaternion.Angle(entity.transform.rotation, snapshot.RiderRotation) > 0.01f
                    || (guideBody.velocity - snapshot.Velocity).sqrMagnitude > 0.000001f
                    || (guideBody.angularVelocity - snapshot.AngularVelocity).sqrMagnitude > 0.000001f) return false;
                if (!TryAdopt(bridge, snapshot.Key, guideBody, guideBody.transform, settings, snapshot.Dynamics,
                    hasExclusiveAuthority, out motor, out detail)) return false;
                motor._dropRequired = snapshot.DropRequired;
                motor.ExitRequired = snapshot.ExitRequired;
                return true;
            }
            catch (Exception error) { detail = "Glider restore failed (" + error.GetType().Name + ")."; return false; }
        }

        /// <summary>
        /// Enter only after aggregate custody/airborne admission. Existing flights use
        /// TryAdopt/TryRestore. Native entry adds normalized (forward + up) VelocityChange.
        /// </summary>
        public static bool TryEnter(PlayerBridge bridge, NativeModeKey key, Rigidbody guideBody,
            HangGliderSettings settings, Func<bool> hasExclusiveAuthority,
            out PlayerFlightGlider motor, out string detail)
        {
            motor = null;
            detail = "Glider entry requires finite native uplift and an admitted guide.";
            try
            {
                if (!Alive(settings) || !Nonnegative(settings.InitialUplift) || !Alive(guideBody)) return false;
                if (!TryAdopt(bridge, key, guideBody, guideBody.transform, settings, default,
                    hasExclusiveAuthority, out motor, out detail)) return false;
                guideBody.AddForce((guideBody.transform.forward + Vector3.up).normalized * settings.InitialUplift,
                    ForceMode.VelocityChange);
                motor._awaitingPhysics = true;
                detail = "Admitted glider entered with one native uplift; client entry effects remain source-owned.";
                return true;
            }
            catch (Exception error)
            {
                motor?.Stop("Glider entry failed (" + error.GetType().Name + ").", out _);
                detail = "Glider entry failed (" + error.GetType().Name + ").";
                return false;
            }
        }

        /// <summary>Call only for the authenticated ordered exit action, never a continuous input edge.</summary>
        public bool TryRequestExit(NativeModeKey key, bool drop, out string detail)
        {
            detail = "Glider exit requires its exact active key.";
            if (_disposed || Environment.CurrentManagedThreadId != _thread || key != Key) return false;
            try
            {
                if (!Current()) return Stop("Glider exit binding changed.", out detail);
                ExitRequired = true;
                _dropRequired |= drop;
                detail = "Glider exit latched; finish physics and reserve any item drop before approving source effects.";
                return true;
            }
            catch (Exception error) { return Stop("Glider exit request failed (" + error.GetType().Name + ").", out detail); }
        }

        /// <summary>
        /// After the aggregate has admitted/reserved any drop, retain native
        /// StopGliding's normalized guide velocity. Return this receipt through the reliable
        /// exit stream, commit Ground(exitSequence), then await source effects and item
        /// custody settlement before explicitly resuming the ground motor.
        /// This does not drop inventory, destroy borrowed objects or call host-global actions.
        /// </summary>
        public bool TryCompleteExit(NativeModeKey key, uint exitSequence,
            out PlayerFlightGliderExit exit, out string detail)
        {
            exit = default;
            detail = "Glider exit requires settled physics/publication and ordered drop admission.";
            if (Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep || key != Key
                || exitSequence <= Key.TransitionSequence) return false;
            if (_exit is { } completed && completed.ExitSequence == exitSequence)
            { exit = completed; detail = "Glider exit already completed."; return true; }
            if (_disposed || !ExitRequired || _awaitingPhysics || _rotationPending
                || _inputSequence != 0 || _publication.Motion.Sequence != 0) return false;
            try
            {
                if (!Current()) return Stop("Glider exit completion binding changed.", out detail);
                Vector3 velocity = _body.velocity.normalized;
                _root.SetPositionAndRotation(_body.position + _body.rotation * _guideOffset, _body.rotation * _guideRotation);
                _body.isKinematic = true;
                // The ground motor owns physics release; never give two modes a dynamic rider.
                _riderBody.isKinematic = true;
                exit = new(_bridge.Plan.Id, _bridge.Plan.Generation, _bridge.NetworkId, Key, exitSequence,
                    _root.position, _root.rotation, velocity, _dropRequired);
                _exit = exit;
                Retire();
                detail = "Glider guide held and exit velocity retained; ground motor exclusively owns rider release.";
                return true;
            }
            catch (Exception error) { return Stop("Glider exit completion failed (" + error.GetType().Name + ").", out detail); }
        }
    }
}
