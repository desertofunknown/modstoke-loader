// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Loader;
using RedLoader;
using global::Sons.Gameplay;
using UnityEngine;
using NativeMode = Modstoke.Sons.PlayerClientMovement.NativeMode;
using TransitionProposal = Modstoke.Sons.PlayerModeTransitionProposal;

namespace Modstoke.Sons
{
    public sealed partial class PlayerFlightGlider
    {
        private bool _dispatchBound;

        /// <summary>
        /// Connect the real bridge proposal to a prepared actor-owned guide. The aggregate
        /// must first validate item custody/airborne eligibility and prepare the approved
        /// guide pose/physics. This method yields ground, acquires the native writer, then
        /// approves the exact retained proposal. It does not infer admission from input.
        /// </summary>
        public static bool TryApproveEntry(PlayerBridge bridge, PlayerGroundMovement ground,
            TransitionProposal proposal, Rigidbody guideBody, HangGliderSettings settings,
            PlayerFlightGliderSnapshot? approvedActiveState,
            Func<bool> hasExclusiveAuthority, out PlayerFlightGlider motor, out string detail)
        {
            motor = null;
            detail = "Glider admission requires the exact proposal, prepared guide and validated actor custody.";
            PlayerFlightGlider candidate = null;
            try
            {
                bool preserveActive = approvedActiveState.HasValue;
                var state = approvedActiveState?.Dynamics ?? default;
                if (!GameBuild.IsRecognized || bridge?.IsServer != true || ground == null
                    || hasExclusiveAuthority == null || Time.inFixedTimeStep
                    || proposal.Expected.Mode != NativeMode.Ground || proposal.Requested.Mode != NativeMode.HangGlider
                    || !bridge.TryPeekMovementTransitionProposal(out var pending) || pending != proposal
                    || !bridge.IsCommittedMovementMode(proposal.Expected)
                    || !bridge.TryReadCurrentTarget(out var entity) || !Alive(guideBody) || !Valid(settings)
                    || !Finite(state) || MathF.Abs(state.Pitch) > settings.MinMaxPitch
                    || MathF.Abs(state.Tilt) > settings.MinMaxTilt
                    || (!preserveActive && !Nonnegative(settings.InitialUplift))
                    || Active.ContainsKey(entity.Pointer) || Same(guideBody.gameObject, entity.gameObject)
                    || guideBody.transform.parent != null || guideBody.isKinematic) return false;
                if (approvedActiveState is { } snapshot)
                {
                    if (snapshot.MigrationId != bridge.Plan.Id || snapshot.Generation != bridge.Plan.Generation
                        || snapshot.NetworkId != bridge.NetworkId || snapshot.Key != proposal.Requested
                        || !Finite(snapshot.GuidePosition) || !Finite(snapshot.GuideRotation) || !Finite(snapshot.Velocity)
                        || !Finite(snapshot.AngularVelocity) || !Finite(snapshot.RiderPosition) || !Finite(snapshot.RiderRotation)
                        || (snapshot.DropRequired && !snapshot.ExitRequired)
                        || (guideBody.position - snapshot.GuidePosition).sqrMagnitude > 0.000001f
                        || Quaternion.Angle(guideBody.rotation, snapshot.GuideRotation) > 0.01f
                        || (guideBody.velocity - snapshot.Velocity).sqrMagnitude > 0.000001f
                        || (guideBody.angularVelocity - snapshot.AngularVelocity).sqrMagnitude > 0.000001f
                        || (entity.transform.position - snapshot.RiderPosition).sqrMagnitude > 0.000001f
                        || Quaternion.Angle(entity.transform.rotation, snapshot.RiderRotation) > 0.01f) return false;
                }
                if (!ground.TryYieldToNativeMode(proposal.Expected, proposal.Requested, out detail)) return false;
                var rider = entity.GetComponent<Rigidbody>();
                if (!Alive(rider) || !rider.isKinematic)
                { detail = "Ground handoff did not retain its exact held rider."; return false; }
                candidate = new(bridge, proposal.Requested, entity, guideBody, rider, guideBody.transform,
                    settings, state, hasExclusiveAuthority);
                candidate._dropRequired = approvedActiveState?.DropRequired ?? false;
                candidate.ExitRequired = approvedActiveState?.ExitRequired ?? false;
                if (!candidate.Current(false))
                    return candidate.Stop("Prepared glider writer did not retain exclusive authority; ground remains yielded.", out detail);
                Active.Add(entity.Pointer, candidate);
                bridge.Retired += candidate.Dispose;
                if (!bridge.TryApproveMovementTransition(proposal, out _, out detail))
                {
                    candidate.Dispose();
                    detail = "Glider writer held after approval failed; resolve the retained handoff before retrying. " + detail;
                    return false;
                }
                if (!candidate.BindDispatch(out detail)) return false;
                if (!preserveActive)
                {
                    guideBody.AddForce((guideBody.transform.forward + Vector3.up).normalized * settings.InitialUplift,
                        ForceMode.VelocityChange);
                    candidate._awaitingPhysics = true;
                }
                motor = candidate;
                detail = "Exact glider proposal approved and server Update/physics/LateUpdate dispatch connected.";
                return true;
            }
            catch (Exception error)
            {
                candidate?.Stop("Glider admission failed (" + error.GetType().Name + ").", out _);
                detail = "Glider admission failed (" + error.GetType().Name + "); retain the native/ground hold.";
                return false;
            }
        }

        /// <summary>
        /// Call after actor item/drop admission and reservation. Drain selected physics, hold the native
        /// writer and approve the bridge's exact exit proposal. The retained ground motor
        /// resumes only after source application and item custody settle and its profile is
        /// restored, using the returned velocity. Approval authorizes source effects; it
        /// cannot require their completion before that authorization has been delivered.
        /// </summary>
        public bool TryApproveExit(TransitionProposal proposal, out PlayerFlightGliderExit exit, out string detail)
        {
            exit = default;
            detail = "Glider exit requires the exact pending reliable proposal and admitted drop reservation.";
            if (Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep
                || proposal.Expected != Key || proposal.Requested.Mode != NativeMode.Ground
                || !_bridge.TryPeekMovementTransitionProposal(out var pending) || pending != proposal) return false;
            if (_exit == null)
            {
                if (!TryRequestExit(Key, false, out detail) || !TryCompletePhysicsStep(out detail)) return false;
            }
            if (!TryCompleteExit(Key, proposal.Requested.TransitionSequence, out exit, out detail)) return false;
            return _bridge.TryApproveMovementTransition(proposal, out _, out detail);
        }

        private bool BindDispatch(out string detail)
        {
            detail = "Glider frame dispatch is already bound.";
            if (_dispatchBound) return true;
            try
            {
                _dispatchBound = true;
                GlobalEvents.OnUpdate.Subscribe(DispatchUpdate);
                GlobalEvents.OnFixedUpdate.Subscribe(DispatchFixed);
                GlobalEvents.OnLateUpdate.Subscribe(DispatchLate);
                return true;
            }
            catch (Exception error) { return Stop("Glider dispatch binding failed (" + error.GetType().Name + ").", out detail); }
        }

        private void UnbindDispatch()
        {
            if (!_dispatchBound) return;
            _dispatchBound = false;
            GlobalEvents.OnUpdate.Unsubscribe(DispatchUpdate);
            GlobalEvents.OnFixedUpdate.Unsubscribe(DispatchFixed);
            GlobalEvents.OnLateUpdate.Unsubscribe(DispatchLate);
        }

        private void DispatchUpdate() { TryUpdateFromBridge(out _); }
        private void DispatchFixed() { TryFixedUpdate(out _); }
        private void DispatchLate() { TryLateUpdate(out _); }
    }

    public sealed partial class PlayerFlightZipline
    {
        private bool _dispatchBound;

        private bool BindDispatch(out string detail)
        {
            detail = "Zipline frame dispatch is already bound.";
            if (_dispatchBound) return true;
            try
            {
                _dispatchBound = true;
                GlobalEvents.OnLateUpdate.Subscribe(DispatchLate);
                return true;
            }
            catch (Exception error) { return Stop("Zipline dispatch binding failed (" + error.GetType().Name + ").", out detail); }
        }

        private void UnbindDispatch()
        {
            if (!_dispatchBound) return;
            _dispatchBound = false;
            GlobalEvents.OnLateUpdate.Unsubscribe(DispatchLate);
        }

        private void DispatchLate() { TryLateUpdate(out _); }
    }
}
