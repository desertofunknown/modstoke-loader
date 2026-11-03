// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerStatProjection
    {
        private PlayerLifeAuthority _lifeAuthority;
        private PlayerLifePresentation _lifePresentation;
        private PlayerLifeFrame _lifeTicket;
        private bool _lifePending;
        internal uint CompletedLifeSequence { get; private set; }
        private ulong _burnLifeSequence;
        private uint _burnStatFloor;

        internal uint LifePublishedSequence
        {
            get
            {
                RequireCurrent();
                if (!_bridge.IsServer || _pumping) throw new InvalidOperationException("Burning commit crossed stat publication.");
                return _sendSequence;
            }
        }

        private bool LifeStatFrameCurrent(in PlayerStatFrame frame)
            => _burnLifeSequence == 0 || frame.LifeSequence != _burnLifeSequence || frame.Sequence > _burnStatFloor;

        internal bool TryCompleteLifeBurning(PlayerLifePresentation owner,
            in PlayerLifeFrame ticket, in PlayerLifeBurnFrame receipt)
        {
            if (!LifeBurningNumbersCurrent(owner, ticket, receipt)) return false;
            _expected = _expected with { Flags = _expected.Flags & ~PlayerStatValues.Burning };
            _burnLifeSequence = ticket.Sequence; _burnStatFloor = receipt.StatSequenceFloor;
            // The existing receiver rejects <= this sequence. Advance its watermark
            // with the server's pre-stop publication boundary, never rewrite packets.
            _receiveSequence = Math.Max(_receiveSequence, receipt.StatSequenceFloor);
            if (_hasPending && !LifeStatFrameCurrent(_pending)) _hasPending = false;
            return true;
        }

        internal bool LifeBurningNumbersCurrent(PlayerLifePresentation owner,
            in PlayerLifeFrame ticket, in PlayerLifeBurnFrame receipt)
        {
            if (!ReferenceEquals(owner, _lifePresentation) || !_lifePending || ticket != _lifeTicket
                || !receipt.MatchesCommand(ticket) || receipt.Kind != PlayerLifeBurnMessage.Commit || _pumping
                || _bridge.IsServer || IsDisposed || FailureReason != null
                || Environment.CurrentManagedThreadId != _thread) return false;
            RequireCurrent();
            var expected = _expected with { Flags = _expected.Flags & ~PlayerStatValues.Burning };
            var actual = PlayerStatValues.Capture(_stats);
            return (_expected.Flags & PlayerStatValues.Burning) != 0 && !_stats._isOnFire && _stats._burnTime == 0
                && (actual with { Flags = (actual.Flags & ~PlayerStatValues.LifeMask) | expected.Life }) == expected;
        }

        internal bool BindLife(PlayerBridge bridge, PlayerLifeAuthority owner)
        {
            if (!ReferenceEquals(bridge, _bridge) || !_bridge.IsServer || _bridge.IsGameplayActive
                || owner == null || owner.IsDisposed || owner.FailureReason != null || _lifeAuthority != null
                || owner.Migration != bridge.Plan.Id || owner.ActorNetworkId != bridge.NetworkId
                || Environment.CurrentManagedThreadId != _thread || IsDisposed || FailureReason != null) return false;
            RequireCurrent();
            _lifeAuthority = owner;
            return true;
        }

        internal bool BindLife(PlayerBridge bridge, PlayerLifePresentation owner)
        {
            if (!ReferenceEquals(bridge, _bridge) || _bridge.IsServer || _bridge.IsGameplayActive
                || owner == null || _lifePresentation != null || Environment.CurrentManagedThreadId != _thread
                || IsDisposed || FailureReason != null) return false;
            RequireCurrent();
            _lifePresentation = owner;
            return true;
        }

        internal bool TryBeginLife(PlayerLifePresentation owner, in PlayerLifeFrame ticket,
            in PlayerLifeValuesFrame values, out string detail)
        {
            detail = "Life projection awaits its exact ordered action and settled intake boundary.";
            if (!ReferenceEquals(owner, _lifePresentation) || owner == null || _lifePending || _pumping
                || IsDisposed || FailureReason != null || _bridge.IsServer || !_bridge.IsGameplayActive
                || Environment.CurrentManagedThreadId != _thread || !ticket.IsValid
                || ticket.Kind != PlayerLifeMessage.Command || ticket.MigrationId != _bridge.Plan.Id
                || ticket.Generation != _bridge.Plan.Generation || ticket.NetworkId != _bridge.NetworkId
                || ticket.Sequence != (ulong)CompletedLifeSequence + 1
                || !values.MatchesCommand(ticket)
                || ticket.IntakeTransaction != _minimumIntake || !_source.StatProjectionCanApply) return false;
            try
            {
                RequireCurrent();
                if (!SourceNumbersCurrent())
                    throw new InvalidOperationException("Life projection lost its exact numeric baseline or bounds.");
                _lifeTicket = ticket;
                _lifePending = true;
                // The immutable commit snapshot prevents a native heal from using stale
                // sickness/bounds. It never grants permission to write native life flags.
                uint life = PlayerStatValues.Capture(_stats).Life;
                if (!values.Values.TryApplyLifeNumbers(_stats))
                    throw new InvalidOperationException("The exact life numeric snapshot could not be projected.");
                _stats._burnTime = ticket.BurnTime;
                _expected = values.Values with { Flags = (values.Values.Flags & ~PlayerStatValues.LifeMask) | life };
                if (!SourceNumbersCurrent()) throw new InvalidOperationException("Life numeric readback failed.");
                detail = "Exact life numbers projected; native action completion is still required.";
                return true;
            }
            catch (Exception error) { Fail(error.Message); detail = FailureReason; return false; }
        }

        internal bool TryCompleteLife(PlayerLifePresentation owner, in PlayerLifeFrame ticket, out string detail)
        {
            detail = "Life projection requires the retained native action and exact final readback.";
            if (!ReferenceEquals(owner, _lifePresentation) || owner == null || !_lifePending
                || ticket != _lifeTicket || _pumping || IsDisposed || FailureReason != null
                || Environment.CurrentManagedThreadId != _thread) return false;
            try
            {
                RequireCurrent();
                if (!SourceNumbersCurrent()) throw new InvalidOperationException("A life callback changed unowned numeric storage.");
                bool settled = ticket.Phase switch
                {
                    PlayerLifePhase.Downed => _vitals._hasDied && _stats._playerIsDowned
                        && _stats._dyingState == PlayerStats.DyingState.Dying && !_stats._healing && !_stats._isHealing,
                    PlayerLifePhase.Reviving => !_vitals._hasDied && !_stats._playerIsDowned
                        && _stats._dyingState == PlayerStats.DyingState.Alive && !_stats._healing && !_stats._isHealing,
                    _ => false
                };
                if (!settled || _stats._respawning || _stats._doFinalDeath) return false;
                _expected = _expected with { Flags = (_expected.Flags & ~PlayerStatValues.LifeMask)
                    | PlayerStatValues.Capture(_stats).Life };
                CompletedLifeSequence = checked((uint)ticket.Sequence);
                _lifePending = false;
                AwaitingLifePresentation = false;
                if (_hasPending && (_pending.LifeSequence < CompletedLifeSequence || !LifeStatFrameCurrent(_pending)))
                    _hasPending = false;
                detail = "Exact native life completion and numeric readback retained.";
                return true;
            }
            catch (Exception error) { Fail(error.Message); detail = FailureReason; return false; }
        }
    }
}
