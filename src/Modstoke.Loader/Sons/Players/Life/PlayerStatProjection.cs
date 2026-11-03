// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;
using global::Sons.StatSystem;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// One ordered absolute stat stream per exact migrated actor. Numeric projection never
    /// executes native life, item, cooking, or health callbacks and never replaces HUD objects.
    /// The aggregate still owns all server writers and source life presentation.
    /// </summary>
    internal sealed partial class PlayerStatProjection : IDisposable
    {
        private static readonly long SendInterval = Math.Max(1, Stopwatch.Frequency / 50);
        private static readonly long HeartbeatInterval = Math.Max(1, Stopwatch.Frequency / 4);
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerBridge _bridge;
        private readonly PlayerCarrier _carrier;
        private readonly InventoryConsumption _consumption;
        private readonly PlayerInventoryEnforcement _source;
        private readonly PlayerStats _stats;
        private readonly Vitals _vitals;
        private readonly Stat[] _statsIdentity;
        private readonly int _statsId, _vitalsId, _scene;
        private readonly nint _root;
        private PlayerStatValues _expected, _lastSent;
        private PlayerStatFrame _pending;
        private uint _sendSequence, _receiveSequence, _appliedSequence, _lastSentLife;
        private ulong _minimumIntake, _lastSentIntake;
        private long _nextSend, _sentAt;
        private bool _hasPending, _pumping, _registered;

        internal bool IsDisposed { get; private set; }
        internal string FailureReason { get; private set; }
        internal bool AwaitingLifePresentation { get; private set; }
        internal ConsumptionNumbers ExpectedIntake => _expected.Intake;
        internal bool HasAppliedState => _appliedSequence != 0;

        private PlayerStatProjection(PlayerBridge bridge, PlayerStats stats,
            PlayerCarrier carrier, InventoryConsumption consumption,
            PlayerInventoryEnforcement source)
        {
            _bridge = bridge; _stats = stats; _vitals = stats._vitals;
            _carrier = carrier; _consumption = consumption; _source = source;
            _statsId = stats.GetInstanceID(); _vitalsId = _vitals.GetInstanceID();
            _scene = stats.gameObject.scene.handle;
            _root = bridge.IsServer ? carrier.Entity.gameObject.Pointer : LocalPlayer.GameObject.Pointer;
            _statsIdentity = new Stat[] { _vitals._health, _vitals._sickness, _vitals._stamina, _vitals._vitality,
                _vitals._hydration, _vitals._fullness, _vitals._rested, _vitals._strength, _vitals._temperature };
            RequireCurrent();
            _expected = PlayerStatValues.Capture(stats);
            if (!_expected.IsValid) throw new InvalidOperationException("Initial projected stats are invalid.");
        }

        internal static bool TryPrepareServer(PlayerBridge bridge, PlayerCarrier carrier,
            InventoryConsumption consumption, out PlayerStatProjection projection,
            out string detail)
        {
            projection = null;
            detail = "Stat publication requires the exact restored carrier and prepared consumption owner.";
            if (bridge?.IsServer != true || bridge.IsGameplayActive || carrier == null
                || consumption == null || consumption.IsDisposed || consumption.IsFaulted
                || !carrier.TryReadRestoredServerStats(out var stats)) return false;
            try
            {
                var candidate = new PlayerStatProjection(bridge, stats, carrier, consumption, null);
                candidate.Register();
                projection = candidate;
                detail = "Server stat publisher prepared without advancing or sending gameplay.";
                return true;
            }
            catch (Exception error) { detail = "Server stat preparation refused: " + error.Message; return false; }
        }

        internal static bool TryPrepareSource(PlayerBridge bridge, PlayerInventoryEnforcement source,
            out PlayerStatProjection projection, out string detail)
        {
            projection = null;
            detail = "Stat projection requires the exact guarded retained source and disabled native stat clocks.";
            if (bridge == null || bridge.IsServer || bridge.IsGameplayActive || source == null
                || !source.TryReadStatProjectionActor(bridge, out var stats)) return false;
            PlayerStatProjection candidate = null;
            try
            {
                candidate = new(bridge, stats, null, null, source);
                if (!source.TryBindStatProjection(bridge, candidate)) { candidate.Dispose(); return false; }
                candidate.Register();
                projection = candidate;
                detail = "Source stat projection prepared on original HUD/stat objects.";
                return true;
            }
            catch (Exception error)
            { candidate?.Dispose(); detail = "Source stat preparation refused: " + error.Message; return false; }
        }

        private void Register()
        {
            _bridge.Retired += Dispose;
            if (!_bridge.IsServer) Datagrams.Received += Receive;
            _registered = true;
        }

        // Called once from the aggregate's settled render phase, after its stat writers.
        internal bool TryPump(out string detail)
        {
            detail = FailureReason ?? "Stat stream is not active.";
            if (IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread
                || _pumping || Time.inFixedTimeStep || !_bridge.IsGameplayActive) return false;
            _pumping = true;
            try
            {
                RequireCurrent();
                if (_bridge.IsServer) return Publish(out detail);
                if (_lifePending || !_source.StatProjectionCanApply)
                { detail = "Stat projection retained behind the exact source action boundary."; return true; }
                if (!SourceNumbersCurrent()) throw new InvalidOperationException("A competing source writer changed authoritative stats.");
                if (!_hasPending) { detail = "No newer authoritative stat frame."; return true; }
                return ApplyPending(out detail);
            }
            catch (Exception error) { Fail(error.Message); detail = FailureReason; return false; }
            finally { _pumping = false; }
        }

        private bool Publish(out string detail)
        {
            detail = "No stat publication is due.";
            long now = Stopwatch.GetTimestamp();
            if (now < _nextSend) return true;
            _nextSend = now + SendInterval;
            var values = PlayerStatValues.Capture(_stats);
            if (!values.IsValid) throw new InvalidOperationException("Authoritative stat values are invalid.");
            ulong intake = _consumption.LastCommittedReceipt.Transaction;
            uint life = checked((uint)(_lifeAuthority?.TransitionSequence ?? 0));
            if (_lifeAuthority == null && values.Life != _expected.Life)
                throw new InvalidOperationException("A life transition has no ordered callback owner.");
            if (intake < _lastSentIntake) throw new InvalidOperationException("Authoritative consumption order regressed.");
            if (_sendSequence != 0 && intake == _lastSentIntake && life == _lastSentLife
                && values == _lastSent && now - _sentAt < HeartbeatInterval)
                return true;
            if (_sendSequence == uint.MaxValue) throw new InvalidOperationException("Stat sequence exhausted.");
            var frame = new PlayerStatFrame(_bridge.Plan.Id, _bridge.Plan.Generation, _bridge.NetworkId,
                _sendSequence + 1, life, intake, values);
            Span<byte> bytes = stackalloc byte[PlayerStatFrame.Length];
            frame.Write(bytes);
            var sent = Datagrams.Send(_bridge.Peer, bytes);
            if (sent == DatagramSendResult.WindowFull)
            { detail = "Stat transport is busy; the next attempt will carry current absolute state."; return true; }
            if (sent != DatagramSendResult.Queued)
                throw new InvalidOperationException("Stat transport unavailable: " + sent);
            _sendSequence = frame.Sequence; _lastSent = values; _lastSentIntake = intake;
            _lastSentLife = life; _sentAt = now;
            detail = "Authoritative absolute stats queued; delivery is not implied.";
            return true;
        }

        private void Receive(Datagram datagram)
        {
            if (IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread
                || datagram == null || !ReferenceEquals(datagram.Peer, _bridge.Peer) || !_bridge.IsGameplayActive
                || !PlayerStatFrame.Matches(datagram.Payload.Span)) return;
            try
            {
                RequireCurrent();
                if (!PlayerStatFrame.TryRead(datagram.Payload.Span, out var frame)
                    || frame.MigrationId != _bridge.Plan.Id || frame.Generation != _bridge.Plan.Generation
                    || frame.NetworkId != _bridge.NetworkId || frame.Sequence <= _receiveSequence) return;
                _receiveSequence = frame.Sequence;
                if (frame.IntakeTransaction < _minimumIntake || frame.LifeSequence < CompletedLifeSequence) return;
                _pending = frame; _hasPending = true;
            }
            catch (Exception error) { Fail("Stat reception failed: " + error.Message); }
        }

        private bool ApplyPending(out string detail)
        {
            detail = "The latest stat frame is awaiting its consumption or life presentation.";
            if (!_hasPending) return true;
            if (_pending.LifeSequence < CompletedLifeSequence) { _hasPending = false; return true; }
            if (_lifePending || _pending.LifeSequence != CompletedLifeSequence)
            { AwaitingLifePresentation = true; return true; }
            if (_pending.IntakeTransaction < _minimumIntake) { _hasPending = false; return true; }
            if (_pending.IntakeTransaction != _minimumIntake) return true;
            AwaitingLifePresentation = !_pending.Values.LifeMatches(_stats);
            if (AwaitingLifePresentation) return true;
            var frame = _pending;
            if (!PlayerLifecycle.TryYieldSourceInputHoldForAcceptedWork(_bridge, out detail))
                throw new InvalidOperationException("Authoritative stats could not yield the source body hold: " + detail);
            if (!frame.Values.TryApply(_stats)) throw new InvalidOperationException("Authoritative stat readback failed.");
            _expected = frame.Values;
            _appliedSequence = frame.Sequence;
            _hasPending = false;
            detail = "Authoritative stats projected once to the retained native objects.";
            return true;
        }

        internal bool TryReadPendingLife(out ulong sequence, out PlayerStatValues values)
        {
            sequence = 0; values = default;
            if (IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread
                || _bridge.IsServer || !_hasPending || _pending.IntakeTransaction != _minimumIntake) return false;
            sequence = _pending.Sequence; values = _pending.Values;
            return true;
        }

        // Called only by the authenticated inventory endpoint, after its exact native debit.
        internal bool TryProjectIntake(in PlayerConsumptionFrame frame)
        {
            if (IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread
                || _bridge.IsServer || _pumping || _lifePending || !_bridge.IsGameplayActive
                || frame.Action.Kind != PlayerInventoryMessage.ConsumeProject
                || frame.Action.MigrationId != _bridge.Plan.Id || frame.Action.Generation != _bridge.Plan.Generation
                || frame.Action.NetworkId != _bridge.NetworkId || frame.Action.Transaction <= _minimumIntake) return false;
            _pumping = true;
            try
            {
                RequireCurrent();
                if (!SourceNumbersCurrent() || frame.Before != _expected.Intake) return false;
                _minimumIntake = frame.Action.Transaction;
                // A newer complete snapshot already includes this effect. Do not first apply
                // the older receipt's numbers, and never replay native effect deltas.
                if (_hasPending && _pending.IntakeTransaction == _minimumIntake
                    && _pending.LifeSequence == CompletedLifeSequence && _pending.Values.LifeMatches(_stats))
                    return ApplyPending(out _);
                if (!InventoryConsumption.TryProjectIntake(_stats, frame.Before, frame.After)) return false;
                _expected = PlayerStatValues.Capture(_stats);
                if (!_expected.IsValid) throw new InvalidOperationException("Consumption left invalid projected stats.");
                return true;
            }
            catch (Exception error) { Fail("Consumption/stat ordering failed: " + error.Message); return false; }
            finally { _pumping = false; }
        }

        internal bool SourceNumbersCurrent()
        {
            if (_bridge.IsServer || IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread) return false;
            RequireCurrent();
            // Life flags are written only by the separate native life presentation owner.
            // They may settle before its matching numeric snapshot arrives.
            var current = PlayerStatValues.Capture(_stats);
            return (current with { Flags = (current.Flags & ~PlayerStatValues.LifeMask)
                | (_expected.Flags & PlayerStatValues.LifeMask) }) == _expected;
        }

        private void RequireCurrent()
        {
            if (IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread
                || _bridge.IsDisposed || !_bridge.TryReadCurrentTarget(out var target)
                || !Live(_stats) || !Live(_vitals) || _stats.enabled || _vitals.enabled
                || _stats.GetInstanceID() != _statsId || _vitals.GetInstanceID() != _vitalsId
                || _stats.gameObject.scene.handle != _scene || _stats._vitals?.Pointer != _vitals.Pointer
                || Combat.StrictCombatLifeHooks.HasConsumptionHealthOwner(_vitals))
                throw new InvalidOperationException("The stat stream lost its exact actor or exclusive numeric clocks.");
            if (_bridge.IsServer)
            {
                if (_carrier == null || _carrier.IsDisposed || _carrier.Entity?.Pointer != target.Pointer
                    || _carrier.Entity.gameObject.Pointer != _root
                    || !_carrier.TryReadRestoredServerStats(out var stats) || stats.Pointer != _stats.Pointer
                    || _consumption.IsDisposed || _consumption.IsFaulted)
                    throw new InvalidOperationException("The authoritative stat actor retired.");
            }
            else if (LocalPlayer.GameObject?.Pointer != _root || LocalPlayer.Stats?.Pointer != _stats.Pointer
                || !_source.TryReadStatProjectionActor(_bridge, out var stats) || stats.Pointer != _stats.Pointer)
                throw new InvalidOperationException("The original source stat/HUD binding retired.");
            Span<nint> current = stackalloc nint[] { _vitals._health?.Pointer ?? 0, _vitals._sickness?.Pointer ?? 0,
                _vitals._stamina?.Pointer ?? 0, _vitals._vitality?.Pointer ?? 0, _vitals._hydration?.Pointer ?? 0,
                _vitals._fullness?.Pointer ?? 0, _vitals._rested?.Pointer ?? 0, _vitals._strength?.Pointer ?? 0,
                _vitals._temperature?.Pointer ?? 0 };
            for (int i = 0; i < current.Length; i++)
                if (_statsIdentity[i] == null || _statsIdentity[i].WasCollected || current[i] == 0
                    || current[i] != _statsIdentity[i].Pointer)
                    throw new InvalidOperationException("A native Stat object was replaced.");
        }

        private static bool Live(UnityEngine.Object value) => value != null && !value.WasCollected && value;
        private void Fail(string detail)
        {
            FailureReason ??= detail;
            _source?.FailStatProjection(this, FailureReason);
        }

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("Retire stat projection on the game thread.");
            if (IsDisposed) return;
            IsDisposed = true;
            if (_registered)
            {
                _bridge.Retired -= Dispose;
                if (!_bridge.IsServer) Datagrams.Received -= Receive;
                _registered = false;
            }
            _hasPending = false;
        }
    }
}
