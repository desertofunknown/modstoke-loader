// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons
{
    public sealed partial class PlayerLifeAuthority
    {
        internal ulong IntakeTransaction => _survival.LifeIntakeTransaction;
        private PlayerLifeRoute _sourceRoute;
        private bool _boundaryPumping;
        public ulong SourceCompletedSequence { get; private set; }
        internal PlayerStatValues ReadCommittedSourceValues() => PlayerStatValues.Capture(_stats);
        internal float ReadCommittedBurnTime() => _stats._burnTime;

        // Aggregate render entry: survival -> numeric life settlement -> ordered source
        // delivery -> publication gate. Pending is retained work, never successful damage.
        internal PlayerLifeBoundaryResult TryPumpServerBoundary(out string detail)
        {
            detail = FailureReason ?? "The exact server life route must be active on its render thread.";
            if (_boundaryPumping || _busy || IsDisposed || FailureReason != null || _sourceRoute == null
                || Environment.CurrentManagedThreadId != _thread || UnityEngine.Time.inFixedTimeStep)
                return PlayerLifeBoundaryResult.Faulted;
            _boundaryPumping = true;
            try
            {
                RequireCurrent();
                if (!_survival.TryAdvance(out _, out detail))
                    throw new InvalidOperationException(_survival.FailureReason ?? detail);
                bool settled = TrySettleSurvivalBoundary(out detail);
                if (!settled && (FailureReason != null || _survival.FailureReason != null))
                    throw new InvalidOperationException(FailureReason ?? _survival.FailureReason);
                // Receipt processing must not deadlock behind the custody or life
                // action whose completion it is responsible for observing.
                if (!_sourceRoute.TryPump(out string routeDetail))
                    throw new InvalidOperationException(_sourceRoute.FailureReason ?? routeDetail);
                var boundary = _survival.ReadLifePublicationBoundary(this, out detail);
                if (boundary != PlayerLifeBoundaryResult.Settled) return boundary;
                if (!settled || HasPendingSourceAction || !BurningPromptSettled || _sourceRoute.HasPendingForcedTransition)
                {
                    detail = "Life/burning work remains retained; numeric publication is withheld while delivery continues.";
                    return PlayerLifeBoundaryResult.Pending;
                }
                detail = "Current rendered survival, life, burning and source receipts are settled for stat publication.";
                return PlayerLifeBoundaryResult.Settled;
            }
            catch (Exception error)
            {
                Fault("Server life pump is unconfirmed: " + error.Message, out detail);
                return PlayerLifeBoundaryResult.Faulted;
            }
            finally { _boundaryPumping = false; }
        }

        internal bool TryReadSourceValues(in PlayerLifeFrame command, out PlayerLifeValuesFrame values)
        {
            values = default;
            if (!TryReadSourceCommand(out var current) || current != command) return false;
            var transition = _sourceTransitions[0];
            values = new(Migration, _bridge.Plan.Generation, ActorNetworkId, transition.Sequence,
                transition.IntakeTransaction, transition.NumericValues);
            return values.MatchesCommand(command);
        }

        internal bool BindSourceRoute(PlayerBridge bridge, PlayerLifeRoute route,
            PlayerStatProjection projection)
        {
            if (!ReferenceEquals(bridge, _bridge) || route == null || projection == null || _sourceRoute != null
                || bridge.IsGameplayActive || IsDisposed || FailureReason != null
                || Environment.CurrentManagedThreadId != _thread) return false;
            RequireCurrent();
            _sourceRoute = route;
            _lifeProjection = projection;
            return true;
        }

        internal bool TryReadSourceCommand(out PlayerLifeFrame command)
        {
            command = default;
            RequireCurrent();
            if (_sourceTransitions.Count == 0) return false;
            var transition = _sourceTransitions[0];
            command = new(PlayerLifeMessage.Command, Migration, _bridge.Plan.Generation, ActorNetworkId,
                transition.LifeGeneration, transition.Sequence, transition.SurvivalRevision,
                transition.IntakeTransaction, transition.Phase, transition.SourceAction,
                transition.DeathTypeSource, transition.Health, transition.TargetHealth, transition.BurnTime);
            if (!command.IsValid) throw new InvalidOperationException("The committed life action has no valid wire representation.");
            return true;
        }

        internal bool TryAcknowledgeSourceCommand(PlayerLifeRoute route,
            in PlayerLifeFrame acknowledgement, out string detail)
        {
            detail = "The exact sent source action must complete before its retained queue can advance.";
            if (!ReferenceEquals(route, _sourceRoute) || route == null || !Ready()
                || acknowledgement.Kind != PlayerLifeMessage.Acknowledgement) return false;
            _busy = true;
            try
            {
                RequireCurrent();
                if (!TryReadSourceCommand(out var command) || !command.SameCommand(acknowledgement)
                    || command.Sequence != SourceCompletedSequence + 1) return false;
                if (command.Action == PlayerLifeSourceAction.StartDeath
                    && (_sourceTransitions[0].NumericValues.Flags & PlayerStatValues.Burning) != 0
                    && (!BurningPromptSettled || !_burnSourceCompleted || _burnReceipt.Sequence != command.Sequence))
                    return false;
                if (command.Action == PlayerLifeSourceAction.Respawn)
                { detail = "Respawn requires the replacement-life identity and inventory restoration owner."; return false; }
                if (command.Action == PlayerLifeSourceAction.ResetFromHeal)
                {
                    if (_sourceTransitions.Count != 1 || Phase != PlayerLifePhase.Reviving
                        || !MatchesCurrentLifeState()) return false;
                    if (!_survival.TryDrainBeforeLifeChange(this, out detail)) return false;
                    _stats._healing = false;
                    _state.IsInMidAction = false;
                    HitDetection = _aliveHitDetection;
                    Phase = PlayerLifePhase.Alive;
                }
                SourceCompletedSequence = command.Sequence;
                _sourceTransitions.RemoveAt(0);
                if (command.Action == PlayerLifeSourceAction.ResetFromHeal
                    && !_survival.TryCompleteSourceLife(this, command.Sequence, out detail))
                    return Fault("The source get-up completed but its server numeric boundary is unconfirmed: " + detail, out detail);
                detail = "Authenticated completion of the exact native source action retained.";
                return true;
            }
            catch (Exception error) { return Fault("Source life completion is unconfirmed: " + error.Message, out detail); }
            finally { _busy = false; }
        }
    }
}
