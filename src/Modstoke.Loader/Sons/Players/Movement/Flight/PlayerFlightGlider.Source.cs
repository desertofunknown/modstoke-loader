// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Gameplay;
using TheForest.Utils;
using UnityEngine;
using NativeMode = Modstoke.Sons.PlayerClientMovement.NativeMode;
using NativeModeKey = Modstoke.Sons.PlayerClientMovement.NativeModeKey;

namespace Modstoke.Sons
{
    public sealed partial class PlayerFlightGlider
    {
        private static readonly Dictionary<IntPtr, SourceAction> SourceActions = new();
        /// <summary>
        /// Bind the original source action only after reliable admission of this key.
        /// The source keeps native controls, camera, animation, guide and subscriptions.
        /// No server or observer may execute these LocalPlayer-dependent native effects.
        /// </summary>
        public static bool TryBindSourceAction(PlayerBridge bridge, NativeModeKey key,
            PlayerHangGliderAction action, out SourceAction source, out string detail)
        {
            source = null;
            detail = "Glider effects require the original source action and committed key.";
            if (bridge == null || bridge.IsServer || key.Mode != NativeMode.HangGlider) return false;
            try
            {
                if (!SourceKeyAuthorized(bridge, key) || !bridge.TryReadCurrentTarget(out var entity)
                    || !Alive(action) || !action.enabled || !Same(LocalPlayer.Transform, entity.transform)
                    || !action.transform.IsChildOf(entity.transform) || !Alive(action._hangGliderTransform)) return false;
                if (SourceActions.TryGetValue(action.Pointer, out var retained))
                {
                    if (!retained.Matches(bridge, key, action, entity)) return false;
                    source = retained;
                    detail = "Existing source action completion retained; effects will not be replayed.";
                    return true;
                }
                source = new SourceAction(bridge, key, action, entity);
                SourceActions.Add(action.Pointer, source);
                bridge.Retired += source.Retire;
                detail = "Original source glider action retained without replaying entry or resetting controls.";
                return true;
            }
            catch (Exception error) { detail = "Glider source binding failed (" + error.GetType().Name + ")."; return false; }
        }

        private static bool SourceKeyAuthorized(PlayerBridge bridge, NativeModeKey key)
            => bridge.IsCommittedMovementMode(key)
                || (bridge.TryPeekMovementTransitionApproval(out var approval)
                    && approval.Decision == PlayerModeTransitionDecision.Approved
                    && approval.Proposal.Expected.Mode == NativeMode.Ground && approval.Proposal.Requested == key
                    && bridge.IsCommittedMovementMode(approval.Proposal.Expected));

        public sealed class SourceAction
        {
            private readonly int _thread = Environment.CurrentManagedThreadId;
            private readonly PlayerBridge _bridge;
            private readonly PlayerHangGliderAction _action;
            private readonly BoltEntity _entity;
            private readonly int _actionId, _entityId;
            private readonly Transform _item;
            private readonly int _itemId;
            private Rigidbody _guideBody;
            private int _guideBodyId;
            private bool _entryAttempted, _failed, _drop;
            private uint _exitSequence;
            private PlayerModeTransitionApproval? _committedApproval;
            private bool _movementBound;

            public NativeModeKey Key { get; }

            /// <summary>
            /// Consume the actual retained bridge decision, run native effects once, commit
            /// the decision, then bind/resume the original movement projection. Retain this
            /// object across retries. approvedDrop comes from the aggregate's correlated
            /// item/drop transaction; movement transition packets do not encode that effect.
            /// </summary>
            public bool TryApplyApproval(PlayerClientMovement movement, bool approvedDrop, out string detail)
            {
                detail = "Source glide needs its exact received approval and retained movement projection.";
                if (Environment.CurrentManagedThreadId != _thread || _failed || movement == null || Time.inFixedTimeStep) return false;
                try
                {
                    if (_bridge.TryPeekMovementTransitionApproval(out var approval))
                    {
                        var proposal = approval.Proposal;
                        bool enter = proposal.Expected.Mode == NativeMode.Ground && proposal.Requested == Key;
                        bool leave = proposal.Expected == Key && proposal.Requested.Mode == NativeMode.Ground;
                        if (!enter && !leave) return false;
                        if (approval.Decision == PlayerModeTransitionDecision.Rejected)
                            return _bridge.TryCommitMovementTransition(approval, out detail);
                        if (approval.Decision != PlayerModeTransitionDecision.Approved) return false;
                        if (enter ? !TryEnter(out detail)
                            : !TryApplyExit(Key, proposal.Requested.TransitionSequence, approvedDrop, out detail)) return false;
                        if (!_bridge.TryCommitMovementTransition(approval, out detail)) return false;
                        _committedApproval = approval;
                        _movementBound = false;
                    }
                    if (_committedApproval is not { } committed
                        || !_bridge.IsCommittedMovementMode(committed.Proposal.Requested)) return false;
                    if (_movementBound) { detail = "Native glide effects and movement binding already completed."; return true; }
                    bool bound = committed.Proposal.Requested == Key
                        ? movement.TryBeginHangGlider(Key, _action, out detail)
                        : movement.TryResumeGround(committed.Proposal.Requested.TransitionSequence, out detail);
                    _movementBound = bound;
                    if (bound && committed.Proposal.Requested.Mode == NativeMode.Ground) Retire();
                    return bound;
                }
                catch (Exception error) { _failed = true; detail = "Source glide decision failed (" + error.GetType().Name + ")."; return false; }
            }

            internal SourceAction(PlayerBridge bridge, NativeModeKey key,
                PlayerHangGliderAction action, BoltEntity entity)
            {
                _bridge = bridge; Key = key; _action = action; _entity = entity;
                _actionId = action.GetInstanceID(); _entityId = entity.GetInstanceID();
                _item = action._hangGliderTransform; _itemId = _item.GetInstanceID();
                if (action._isGliding) RememberGuide();
            }

            /// <summary>Existing active glides are retained; a newly admitted entry runs BeginGliding once.</summary>
            public bool TryEnter(out string detail)
            {
                detail = "Source glider entry requires its original admitted action.";
                try
                {
                    if (!Current() || _exitSequence != 0 || !Alive(_item) || _item.GetInstanceID() != _itemId
                        || !Same(_action._hangGliderTransform, _item)) return false;
                    if (_action._isGliding && MatchesGuide()) { detail = "Existing source glide retained without uplift replay."; return true; }
                    if (_entryAttempted || _action._isGliding) return false;
                    _entryAttempted = true;
                    _action.BeginGliding();
                    RememberGuide();
                    if (!Current() || !_action._isGliding || !MatchesGuide())
                    { _failed = true; detail = "Native source entry did not complete; do not replay it."; return false; }
                    detail = "Native source entry completed with original camera, controls and guide.";
                    return true;
                }
                catch (Exception error) { _failed = true; detail = "Source glider entry failed (" + error.GetType().Name + ")."; return false; }
            }

            /// <summary>Supply this capture to aggregate validation; it is not server approval of a client pose.</summary>
            public bool TryCapture(out PlayerFlightGliderSnapshot snapshot, out string detail)
            {
                snapshot = default;
                detail = "Source glide capture requires the exact retained native guide at a settled frame boundary.";
                try
                {
                    if (!Current() || Time.inFixedTimeStep || !_action._isGliding || !MatchesGuide()) return false;
                    var state = new PlayerFlightGliderState(_action._heading, _action._forwardMomentum,
                        _action._pitch, _action._tilt);
                    if (!Finite(state) || !Finite(_guideBody.position) || !Finite(_guideBody.rotation)
                        || !Finite(_guideBody.velocity) || !Finite(_guideBody.angularVelocity)
                        || !Finite(_entity.transform.position) || !Finite(_entity.transform.rotation)) return false;
                    snapshot = new(_bridge.Plan.Id, _bridge.Plan.Generation, _bridge.NetworkId, Key, state,
                        _guideBody.position, _guideBody.rotation, _guideBody.velocity, _guideBody.angularVelocity,
                        _entity.transform.position, _entity.transform.rotation, _action._hasHitObstacle, _action._hasHitObstacle);
                    detail = "Original source glide captured for aggregate validation without mutating it.";
                    return true;
                }
                catch (Exception error) { _failed = true; detail = "Source glide capture failed (" + error.GetType().Name + ")."; return false; }
            }

            /// <summary>
            /// Dispatch only from the authenticated reliable exit stream before committing
            /// ground. Drop uses the original native unequip/drop path, which the aggregate's
            /// inventory authority must route and settle; do not also remove the source item.
            /// Success confirms native action effects, NOT inventory custody completion.
            /// </summary>
            public bool TryApplyExit(NativeModeKey key, uint exitSequence, bool drop, out string detail)
            {
                detail = "Source glide exit requires the exact ordered action and native inventory routing.";
                try
                {
                    if (key != Key || exitSequence <= Key.TransitionSequence || !Current()) return false;
                    if (_exitSequence != 0)
                    {
                        if (_exitSequence != exitSequence || _drop != drop) return false;
                        detail = "Source glide exit was already dispatched; native custody settlement remains separate.";
                        return !_action._isGliding;
                    }
                    if (!_action._isGliding || !MatchesGuide() || !Alive(_item) || _item.GetInstanceID() != _itemId
                        || !Same(_action._hangGliderTransform, _item)) return false;
                    _exitSequence = exitSequence; _drop = drop;
                    if (drop) _action.TriggerHitObstacle();
                    else _action.StopGliding();
                    if (_action._isGliding)
                    { _failed = true; detail = "Native source exit did not complete; do not replay its inventory effects."; return false; }
                    detail = "Original source glider exit effects dispatched once; inventory settlement remains aggregate-owned.";
                    return true;
                }
                catch (Exception error) { _failed = true; detail = "Source glider exit failed (" + error.GetType().Name + ")."; return false; }
            }

            private bool Current()
                => !_failed && Environment.CurrentManagedThreadId == _thread && !_bridge.IsServer
                    && SourceKeyAuthorized(_bridge, Key) && _bridge.TryReadCurrentTarget(out var entity)
                    && Same(entity, _entity) && entity.GetInstanceID() == _entityId
                    && Same(LocalPlayer.Transform, entity.transform) && Alive(_action) && _action.GetInstanceID() == _actionId
                    && _action.transform.IsChildOf(entity.transform);

            internal bool Matches(PlayerBridge bridge, NativeModeKey key, PlayerHangGliderAction action, BoltEntity entity)
                => ReferenceEquals(bridge, _bridge) && key == Key && Same(action, _action) && Same(entity, _entity)
                    && action.GetInstanceID() == _actionId && entity.GetInstanceID() == _entityId;

            internal void Retire()
            {
                _bridge.Retired -= Retire;
                if (SourceActions.TryGetValue(_action.Pointer, out var current) && ReferenceEquals(current, this))
                    SourceActions.Remove(_action.Pointer);
            }

            private void RememberGuide()
            { _guideBody = _action._rigidbody; _guideBodyId = Alive(_guideBody) ? _guideBody.GetInstanceID() : 0; }

            private bool MatchesGuide()
                => Alive(_guideBody) && _guideBody.GetInstanceID() == _guideBodyId && Same(_action._rigidbody, _guideBody)
                    && Same(_action._guideTransform, _guideBody.transform) && !_guideBody.isKinematic;
        }
    }
}
