// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Bolt;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// Explicit game-thread callback lifetime and ordered equipment publication for one migration.
    /// Does not select transports, enable capabilities, migrate a player, own inventory, or replace
    /// a complete migration adapter. The caller owns quiescence and the initial state baseline.
    /// Successfully created endpoints are pumped by the loader on the game thread.
    /// Item-module changes that do not emit these callbacks, held-only ownership, save/respawn
    /// reconstruction and local presentation after an authority rejection remain outside this helper.
    /// </summary>
    public sealed partial class PlayerEquipment : IDisposable
    {
        public const int QueueCapacity = 64;
        private static readonly List<PlayerEquipment> Active = new();
        private static bool _pumpingAll;
        private static readonly long SendInterval = Stopwatch.Frequency / 20;
        private static readonly long RetryInterval = Stopwatch.Frequency / 5;
        private static readonly long ReceiptTimeout = Stopwatch.Frequency * 8;
        // A disposed endpoint cannot reset sequence numbers on a still-live bridge.
        private static readonly ConditionalWeakTable<PlayerBridge, PlayerEquipment> UsedBridges = new();
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerEquipmentChange[] _queue = new PlayerEquipmentChange[QueueCapacity];
        private readonly BoltMigrationPlan _plan;
        private PlayerBridge _bridge;
        private PlayerEquipmentAuthorize _authorize;
        private SourceSubscriptions _source;
        private BoltPlayerSetup _setup;
        private bool _server, _disposed, _busy, _registered, _inFlight, _hasSent, _hasReceived;
        private bool _tracked;
        private int _head, _count, _wrongThreadCallback;
        private uint _sequence;
        private long _lastSend, _firstAttempt, _lastReceive;
        private PlayerEquipmentFrame _pending, _lastReceipt;

        public bool IsFaulted { get; private set; }
        public bool IsDisposed => _disposed;
        public bool IsBound => _registered && !_disposed;
        public bool SubscriptionsDetached => _source == null || _source.IsDetached;
        public int PendingCount => _count;
        public string Detail { get; private set; } = "Captured callbacks await a completed migration.";

        private PlayerEquipment(BoltMigrationPlan plan) { _plan = plan; }

        /// <summary>
        /// Call on the quiesced original local client before Bolt.Detached removes vanilla delegates.
        /// Establish the preservation baseline before this call; queued callbacks are deltas after it.
        /// This only adds observers and never removes or invokes vanilla callbacks. On partial failure
        /// the returned object must still be disposed and SubscriptionsDetached checked.
        /// </summary>
        public static bool TryCaptureSource(BoltMigrationPlan plan,
            out PlayerEquipment equipment, out string detail)
        {
            equipment = null;
            detail = "A quiesced, locally owned original player and valid migration plan are required.";
            if (!PlayerRoles.IsAvailable) return false;
            try
            {
                var entity = LocalPlayer.Entity;
                if (!plan.IsValid || plan.PrefabId != BoltPrefabs.PlayerNetwork.Value
                    || !BoltNetwork.isRunning || !BoltNetwork.isClient || BoltNetwork.isServer
                    || !Live(entity) || !entity.isAttached || !entity.isOwner
                    || entity.networkId.PackedValue != plan.OldNetworkId) return false;
                equipment = new(plan);
                equipment._source = SourceSubscriptions.Capture(equipment);
                if (equipment._source == null || !equipment._source.TrySubscribe())
                    equipment.Fail("The original player's callback sources could not be retained.");
                if (!equipment.IsFaulted) equipment.Track();
                detail = equipment.Detail;
                return !equipment.IsFaulted;
            }
            catch (Exception error)
            {
                detail = "Equipment capture failed (" + error.GetType().Name + ").";
                equipment?.Fail(detail);
                return false;
            }
        }

        /// <summary>
        /// Bind only after vanilla Detached and exact local reference repair. A prepared bridge
        /// keeps publication paused until the completed migration barrier.
        /// LocalAttached must NOT be replayed on the retained nonowner. The exact retained source
        /// objects must survive. No state is written locally, including while this is unbound.
        /// </summary>
        public bool TryBindSource(PlayerBridge bridge, out string detail)
        {
            detail = "The repaired bridge does not match the retained equipment lifetime.";
            if (!OnThread || _disposed || IsFaulted || _busy || _bridge != null || _source == null) return false;
            try
            {
                if (bridge == null || bridge.IsServer || bridge.Plan != _plan
                    || !bridge.TryReadCurrentTarget(out var target) || !Same(target, LocalPlayer.Entity)
                    || !_source.IsCurrent() || UsedBridges.TryGetValue(bridge, out _)) return false;
                Register(bridge);
                Detail = detail = "Retained equipment callbacks bound; awaiting authorized server receipts.";
                return true;
            }
            catch (Exception error)
            {
                Fail("Equipment bridge binding failed (" + error.GetType().Name + ").");
                detail = Detail;
                return false;
            }
        }

        /// <summary>
        /// Create the receiving publisher for a completed server bridge. Authorization is mandatory;
        /// this helper does not infer an inventory ledger from client notifications or player state.
        /// </summary>
        public static bool TryCreateServer(PlayerBridge bridge,
            PlayerEquipmentAuthorize authorize,
            out PlayerEquipment equipment, out string detail)
        {
            equipment = null;
            detail = "A completed server bridge, exact setup and inventory authorization are required.";
            if (bridge == null || !bridge.IsServer || authorize == null) return false;
            try
            {
                if (!bridge.TryReadCurrentTarget(out var target) || UsedBridges.TryGetValue(bridge, out _)) return false;
                var setup = target.GetComponent<BoltPlayerSetup>();
                if (!Live(setup) || !Same(setup.entity, target)) return false;
                equipment = new(bridge.Plan) { _server = true, _authorize = authorize, _setup = setup };
                equipment.Register(bridge);
                equipment.Detail = detail = "Server equipment publisher bound; no client claim is preauthorized.";
                return true;
            }
            catch (Exception error)
            {
                detail = "Equipment publisher binding failed (" + error.GetType().Name + ").";
                equipment?.Dispose();
                return false;
            }
        }

        private void Register(PlayerBridge bridge)
        {
            UsedBridges.Add(bridge, this);
            _bridge = bridge;
            bridge.Retired += OnBridgeRetired;
            Datagrams.Received += Receive;
            _registered = true;
            Track();
        }

        private void Track()
        {
            if (_tracked) return;
            Active.Add(this);
            _tracked = true;
        }

        internal static void Tick()
        {
            if (Active.Count == 0 || _pumpingAll || !PlayerRoles.IsAvailable) return;
            _pumpingAll = true;
            try
            {
                int count = Active.Count;
                for (int i = 0; i < count; i++) Active[i].Pump();
            }
            finally
            {
                _pumpingAll = false;
                for (int i = Active.Count - 1; i >= 0; i--)
                    if (Active[i]._disposed) Active.RemoveAt(i);
            }
        }

        /// <summary>
        /// Explicitly replace our delegates on the SAME retained sources while the caller has them
        /// quiesced. Old delegates stay inert. Changed rig/inventory objects need a new migration, not
        /// automatic adoption. An uncertain unsubscribe prevents rebinding.
        /// </summary>
        public bool TryRebindSourceSubscriptions(out string detail)
        {
            detail = "Equipment subscriptions cannot be rebound.";
            if (!OnThread || _disposed || IsFaulted || _busy || _server || _source == null) return false;
            try
            {
                if ((_bridge != null && !Current(out _)) || !_source.IsCurrent() || !_source.TryDetach())
                {
                    Fail("Equipment sources changed or subscription teardown is unconfirmed.");
                    detail = Detail;
                    return false;
                }
                _source = _source.Replacement();
                if (!_source.TrySubscribe())
                {
                    Fail("Equipment callback rebinding failed.");
                    detail = Detail;
                    return false;
                }
                detail = "Equipment callbacks rebound to the exact retained sources.";
                return true;
            }
            catch (Exception error)
            {
                Fail("Equipment rebinding failed (" + error.GetType().Name + ").");
                detail = Detail;
                return false;
            }
        }

        /// <summary>
        /// The loader calls this on its game-thread driver, including while idle. At most one queued operation is in
        /// flight. Retransmissions repeat its identity, not its native effects. Overflow, rejection or
        /// eight seconds without a receipt requires explicit migration recovery, never local writes.
        /// </summary>
        public void Pump()
        {
            if (!OnThread || _disposed || _busy) return;
            _busy = true;
            try
            {
                if (Volatile.Read(ref _wrongThreadCallback) != 0)
                    Fail("An equipment callback arrived off the game thread; capture is incomplete.");
                if (IsFaulted || (_source != null && !_source.IsCurrent()))
                {
                    if (!IsFaulted) Fail("The retained equipment source lifetime ended.");
                    return;
                }
                if (_bridge == null) return;
                if (!Current(out _)) { Dispose(); return; }
                if (!_bridge.IsGameplayActive) return;
                if (_server || _count == 0) return;
                long now = Stopwatch.GetTimestamp();
                if (!_inFlight)
                {
                    if (_hasSent && now - _lastSend < SendInterval) return;
                    if (_sequence == uint.MaxValue) { Fail("Equipment sequence exhausted."); return; }
                    _pending = new(_plan.Id, _plan.Generation, _bridge.NetworkId, ++_sequence, _queue[_head]);
                    _inFlight = true;
                    _firstAttempt = now;
                }
                else
                {
                    if (now - _firstAttempt >= ReceiptTimeout)
                    {
                        Fail("Equipment receipt timed out; server application may be unconfirmed.");
                        return;
                    }
                    if (_hasSent && now - _lastSend < RetryInterval) return;
                }
                Send(_pending, now);
            }
            catch (Exception error) { Fail("Equipment pump failed (" + error.GetType().Name + ")."); }
            finally { _busy = false; }
        }

        private void Enqueue(in PlayerEquipmentChange change)
        {
            if (!OnThread || _disposed || IsFaulted || _server) return;
            if (PlayerArmourSource.TryObserveEquipment(_bridge, in change)) return;
            if (!change.IsValid) { Fail("Unsupported or invalid equipment callback arguments."); return; }
            if (_count == QueueCapacity) { Fail("Equipment callback queue overflow; resynchronization required."); return; }
            _queue[(_head + _count) % QueueCapacity] = change;
            _count++;
        }

        private bool Current(out BoltEntity target)
        {
            target = null;
            return _bridge != null && _bridge.TryReadCurrentTarget(out target) && !_disposed
                && (_server ? BoltNetwork.isServer && target.isOwner && Live(_setup)
                    && Same(_setup.entity, target) : !target.isOwner && Same(target, LocalPlayer.Entity));
        }

        private void Receive(Datagram datagram)
        {
            if (!OnThread || _disposed || _busy || datagram == null || _bridge == null || !_bridge.IsGameplayActive
                || !ReferenceEquals(datagram.Peer, _bridge.Peer)
                || !PlayerEquipmentWire.TryRead(datagram.Payload.Span, out var frame)
                || frame.MigrationId != _plan.Id || frame.Generation != _plan.Generation
                || frame.NetworkId != _bridge.NetworkId) return;
            _busy = true;
            try
            {
                if (!Current(out var target)) { Dispose(); return; }
                if (!_server)
                {
                    if (IsFaulted || !_inFlight || frame.Receipt == PlayerEquipmentReceipt.None
                        || frame.Sequence != _pending.Sequence || frame.Change != _pending.Change) return;
                    if (frame.Receipt is not (PlayerEquipmentReceipt.Applied or PlayerEquipmentReceipt.Ignored))
                    {
                        Fail("Server equipment publication " + frame.Receipt + "; resynchronization required.");
                        return;
                    }
                    _queue[_head] = default;
                    _head = (_head + 1) % QueueCapacity;
                    _count--;
                    _inFlight = false;
                    return;
                }
                if (frame.Receipt != PlayerEquipmentReceipt.None) return;
                long now = Stopwatch.GetTimestamp();
                if (_hasReceived && now - _lastReceive < SendInterval) return;
                _lastReceive = now;
                _hasReceived = true;
                if (frame.Sequence == _lastReceipt.Sequence && frame.Change == _lastReceipt.Change)
                {
                    Send(_lastReceipt, now);
                    return;
                }
                if (IsFaulted || _sequence == uint.MaxValue || frame.Sequence != _sequence + 1) return;
                // Reserve before entering any native mutation. An uncertain write is never replayed.
                _sequence = frame.Sequence;
                var receipt = Publish(target, frame.Change, out var detail);
                _lastReceipt = frame with { Receipt = receipt };
                if (receipt is not (PlayerEquipmentReceipt.Applied or PlayerEquipmentReceipt.Ignored)) Fail(detail);
                Send(_lastReceipt, now);
            }
            catch (Exception error) { Fail("Equipment receive failed (" + error.GetType().Name + ")."); }
            finally { _busy = false; }
        }

        private void Send(in PlayerEquipmentFrame frame, long now)
        {
            if (_disposed || _bridge?.IsGameplayActive != true) return;
            Span<byte> bytes = stackalloc byte[PlayerEquipmentWire.Length];
            PlayerEquipmentWire.Write(frame, bytes);
            _lastSend = now;
            _hasSent = true;
            var result = Datagrams.Send(_bridge.Peer, bytes);
            if (result is DatagramSendResult.Failed or DatagramSendResult.NotEnrolled
                or DatagramSendResult.PeerUnavailable) Fail("Equipment transport became unavailable (" + result + ").");
        }

        private void Fail(string detail)
        {
            if (!IsFaulted) Detail = detail;
            IsFaulted = true;
            _source?.TryDetach();
        }

        private bool OnThread => Environment.CurrentManagedThreadId == _thread;
        private bool OnCallbackThread
        {
            get
            {
                if (OnThread) return true;
                Interlocked.Exchange(ref _wrongThreadCallback, 1);
                return false;
            }
        }
        private void OnBridgeRetired() => Dispose();

        public void Dispose()
        {
            if (!OnThread) throw new InvalidOperationException("Dispose equipment on its game thread.");
            _disposed = true;
            if (_source != null && !_source.TryDetach())
            {
                IsFaulted = true;
                Detail = "Equipment callbacks are inert, but native subscription teardown is unconfirmed.";
            }
            if (_source != null && _source.IsDetached) _source = null;
            if (_registered)
            {
                _bridge.Retired -= OnBridgeRetired;
                Datagrams.Received -= Receive;
                _registered = false;
            }
            Array.Clear(_queue);
            _count = 0;
            _inFlight = false;
            _authorize = null;
            _setup = null;
            _preparedHeldModules = null;
            if (_tracked && !_pumpingAll) Active.Remove(this);
            _tracked = false;
        }

        private static bool Live(UnityEngine.Object value) => value != null && !value.WasCollected && value;
        private static bool Same(UnityEngine.Object a, UnityEngine.Object b) => Live(a) && Live(b) && a.Pointer == b.Pointer;
    }
}
