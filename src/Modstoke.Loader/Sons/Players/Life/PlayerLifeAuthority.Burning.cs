// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons
{
    public sealed partial class PlayerLifeAuthority
    {
        private PlayerStatProjection _lifeProjection;
        private PlayerLifeTransition _burnTransition;
        private PlayerLifeBurnFrame _burnReceipt;
        private bool _burnCommitted, _burnSourceCompleted;
        private float _burnStartedAt, _burnDue, _burnExpectedTime, _burnLastTime;
        private int _burnStage, _burnLastFrame;
        private bool BurningPromptSettled => _burnTransition == null || _burnStage == 2;

        internal void BeginBurningDeath(PlayerLifeTransition transition, float time, int frame)
        {
            RequireCurrent();
            if (_sourceRoute == null || _lifeProjection == null || !BurningPromptSettled
                || transition != PendingTransition || transition.SourceAction != PlayerLifeSourceAction.StartDeath
                || !_stats._isOnFire || !float.IsFinite(_stats._burnTime) || _stats._burnTime < 0
                || !float.IsFinite(time) || !_state.FX_Fire)
                throw new InvalidOperationException("Burning death requires its exact active server effect and frame clock.");
            _burnTransition = transition;
            _burnStartedAt = _burnLastTime = time; _burnLastFrame = frame;
            _burnDue = time + 2f; _burnStage = 0; _burnExpectedTime = _stats._burnTime;
            _burnCommitted = false; _burnSourceCompleted = false; _burnReceipt = default;
        }

        // Called inside the ordered survival frame, in PlayerStats.Update's burning
        // position. Frame timestamps are captured at admission, never at backlog drain.
        internal bool AdvanceBurningDeath(float seconds, float time, int frame, ulong revision)
        {
            if (_burnTransition == null || _burnStage == 2) return false;
            RequireCurrent();
            if (!float.IsFinite(seconds) || seconds < 0 || !float.IsFinite(time) || time < _burnLastTime
                || frame == _burnLastFrame || Phase != PlayerLifePhase.Downed
                || _burnTransition != PendingTransition || _stats._burnTime != _burnExpectedTime
                || _stats._isOnFire != !_burnCommitted || _state.FX_Fire != !_burnCommitted)
                throw new InvalidOperationException("The retained burning death timeline lost its exact numeric/effect owner.");
            _burnLastTime = time; _burnLastFrame = frame;
            if (!_burnCommitted)
            {
                _stats._burnTime -= seconds;
                _burnExpectedTime = _stats._burnTime;
                if (_burnExpectedTime <= 0) CommitBurningStop(PlayerLifeBurnCause.Expired, time, revision);
            }
            if (time >= _burnDue)
            {
                if (_burnStage == 0)
                {
                    // DoBasicDeath: +2; CheckFirstDeathOption: +1 FROM its
                    // actual callback frame. A late first callback is not +3.
                    _burnStage = 1; _burnDue = time + 1f;
                }
                else
                {
                    if (!_burnCommitted) CommitBurningStop(PlayerLifeBurnCause.DeathPrompt, time, revision);
                    _burnStage = 2;
                }
            }
            return true;
        }

        private void CommitBurningStop(PlayerLifeBurnCause cause, float time, ulong revision)
        {
            var receipt = new PlayerLifeBurnFrame(PlayerLifeBurnMessage.Commit, Migration, _bridge.Plan.Generation,
                ActorNetworkId, LifeGeneration, _burnTransition.Sequence, revision,
                _lifeProjection.LifePublishedSequence, time - _burnStartedAt, _stats._burnTime, cause);
            if (_burnCommitted || !receipt.IsValid)
                throw new InvalidOperationException("The native burning stop has no valid exact receipt.");
            // StopBurning/SetOnFireState(false) server-owned storage and publication.
            // Never call the LocalPlayer-bearing native body on a remote carrier.
            _stats._burnTime = 0; _stats._isOnFire = false; _state.FX_Fire = false;
            if (_stats._burnTime != 0 || _stats._isOnFire || _state.FX_Fire)
                throw new InvalidOperationException("Server burning stop readback is unconfirmed; never replay.");
            _burnExpectedTime = 0; _burnReceipt = receipt; _burnCommitted = true;
        }

        internal bool TryReadBurningCommit(in PlayerLifeFrame command, out PlayerLifeBurnFrame receipt)
        {
            RequireCurrent(); receipt = default;
            if (!_burnCommitted || !_burnReceipt.MatchesCommand(command)) return false;
            receipt = _burnReceipt; return true;
        }

        internal bool TryAcknowledgeBurning(PlayerLifeRoute route, in PlayerLifeBurnFrame receipt)
        {
            RequireCurrent();
            if (!ReferenceEquals(route, _sourceRoute) || !_burnCommitted
                || receipt.Kind != PlayerLifeBurnMessage.Acknowledgement || !_burnReceipt.SameCommit(receipt)
                || !TryReadSourceCommand(out var command) || !receipt.MatchesCommand(command)) return false;
            _burnSourceCompleted = true;
            return true;
        }
    }
}
