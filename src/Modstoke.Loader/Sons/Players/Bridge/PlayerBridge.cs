// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Bolt;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// Explicit game-thread-only transport of player intent and server state for one completed
    /// migration. This neither selects datagrams nor patches input, applies transforms or invokes
    /// simulation. A complete vanilla locomotion and preservation adapter is still required.
    /// </summary>
    public sealed partial class PlayerBridge : IDisposable
    {
        private const ulong RequiredCapabilities = PlayerBridgeProtocol.Capability
            | BoltMigrationProtocol.Capability;
        private static readonly long InputLifetime = Stopwatch.Frequency / 4;
        private static readonly Dictionary<Guid, PlayerBridge> Bridges = new();
        private static readonly HashSet<Guid> UsedMigrations = new();
        private static readonly List<PlayerBridge> TickSnapshot = new();
        private static int _ownerThread;
        private static bool _creating;
        private static bool _ticking;

        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly bool _server;
        private readonly UniqueId _serializerId;
        private BoltEntity _entity;
        private Bolt.Entity _attachment;
        private GameObject _gameObject;
        private NetworkState _state;
        private IPlayerState _playerState;
        private NetworkObj_Meta _meta;
        private PlayerHandle _playerBinding;
        private GameObject _retainedRoot;
        private FirstPersonCharacter _retainedController;
        private Rigidbody _retainedBody;
        private bool _disposed;
        private bool _busy;
        private IDisposable _eventRoute;
        private readonly BoltMigrationSession _migrationSession;
        private bool _gameplayActive;
        private long _lastSend;
        private long _lastReceive;
        private double _sendCredit = 2;
        private double _receiveCredit = 4;
        private long _inputReceivedAt;
        private uint _sentSequence;
        private uint _receivedSequence;
        private uint _takenInputSequence;
        private uint _simulatedInputSequence;
        private uint _acknowledgedInputSequence;
        private bool _hasInput;
        private bool _hasState;
        private PlayerMotionInput _input;
        private PlayerMotionState _receivedState;

        public ControlPeer Peer { get; }
        public BoltMigrationPlan Plan { get; }
        public ulong NetworkId { get; }
        /// <summary>True for the destination server; false for the retained-rig source client.</summary>
        public bool IsServer => _server;
        /// <summary>Cached local disposition, not a claim that native bindings remain current.</summary>
        public bool IsDisposed => _disposed;
        /// <summary>Prepared endpoints may exchange the inventory baseline, but cannot drive gameplay.</summary>
        public bool IsGameplayActive => _gameplayActive && !_disposed
            && _migrationSession.Status == BoltMigrationStatus.Completed;
        internal event Action Retired;
        /// <summary>First unexpected failure that retired this bridge, if any.</summary>
        internal string RetireReason { get; private set; }

        private PlayerBridge(ControlPeer peer,
            BoltMigrationPlan plan, ulong networkId, BoltEntity entity,
            PlayerHandle playerBinding, BoltMigrationSession session, bool prepared)
        {
            Peer = peer;
            Plan = plan;
            NetworkId = networkId;
            _server = peer.ServerSession != null;
            _entity = entity;
            _attachment = entity._entity;
            _gameObject = entity.gameObject;
            _state = _attachment?.Serializer?.TryCast<NetworkState>();
            _playerState = entity.GetState<IPlayerState>();
            _meta = _state?.Meta;
            _serializerId = entity.serializerGuid;
            _playerBinding = playerBinding;
            _migrationSession = session;
            _gameplayActive = !prepared;
            if (!_server)
            {
                _retainedRoot = LocalPlayer.GameObject;
                _retainedController = LocalPlayer.FpCharacter;
                _retainedBody = LocalPlayer.Rigidbody;
            }
        }

        /// <summary>
        /// Binds only the completed session's exact replacement. One bridge per peer; no automatic
        /// selection, migration, control assignment, state copying or simulation occurs here.
        /// </summary>
        public static bool TryCreate(BoltMigrationSession completedMigrationSession,
            out PlayerBridge bridge, out string detail)
            => TryCreateCore(completedMigrationSession, false, out bridge, out detail);

        internal static bool TryCreatePrepared(BoltMigrationSession session,
            out PlayerBridge bridge, out string detail)
            => TryCreateCore(session, true, out bridge, out detail);

        private static bool TryCreateCore(BoltMigrationSession completedMigrationSession,
            bool prepared, out PlayerBridge bridge, out string detail)
        {
            bridge = null;
            detail = "A completed player migration on the game thread is required.";
            if (!PlayerRoles.IsAvailable || _creating || completedMigrationSession == null)
                return false;
            _creating = true;
            PlayerBridge candidate = null;
            try
            {
                if (!completedMigrationSession.TryReadPlayerBridgeTarget(out var entity, out detail, prepared))
                    return false;
                var peer = completedMigrationSession.Peer;
                if (peer == null || !peer.IsCurrent || Bridges.ContainsKey(peer.PeerId))
                {
                    detail = "The peer is unavailable or already has a player bridge.";
                    return false;
                }
                var plan = completedMigrationSession.Snapshot.Plan;
                ulong networkId = completedMigrationSession.NewNetworkId;
                if (!plan.IsValid || plan.PrefabId != BoltPrefabs.PlayerNetwork.Value
                    || networkId == 0 || networkId == plan.OldNetworkId || UsedMigrations.Contains(plan.Id))
                    return false;

                PlayerHandle playerBinding = null;
                if (peer.ServerSession != null
                    && (!ServerWorld.TryCapturePlayerBinding(peer.ServerSession, out playerBinding, out _)
                        || !ReferenceEquals(playerBinding.PlayerSession, peer.ServerSession)))
                {
                    detail = "The destination must be mapped to the exact remote player's session.";
                    return false;
                }
                candidate = new PlayerBridge(peer, plan, networkId, entity, playerBinding,
                    completedMigrationSession, prepared);
                if (!candidate.ValidateCurrent())
                {
                    detail = "The player attachment, control role or enrolled capabilities are not current.";
                    return false;
                }
                if (!prepared && PlayerEventRouting.IsSelected
                    && !PlayerEventRouting.TryBind(candidate, completedMigrationSession,
                    out candidate._eventRoute, out detail)) return false;
                if (Bridges.Count == 0)
                {
                    _ownerThread = Environment.CurrentManagedThreadId;
                    Datagrams.Received += OnReceived;
                    ControlTransport.Closed += OnClosed;
                }
                UsedMigrations.Add(plan.Id);
                Bridges.Add(peer.PeerId, candidate);
                bridge = candidate;
                detail = "Player intent/state transport bound; locomotion and preservation remain adapter responsibilities.";
                return true;
            }
            catch (Exception error)
            {
                candidate?.Dispose();
                detail = "Player bridge binding failed (" + error.GetType().Name + ").";
                return false;
            }
            finally { _creating = false; }
        }

        internal bool TryActivateCompleted(out string detail)
        {
            detail = "Gameplay activation requires the exact completed migration and current endpoint.";
            if (Environment.CurrentManagedThreadId != _thread || _disposed || _busy
                || !_migrationSession.TryReadPlayerBridgeTarget(out var entity, out detail)
                || entity.Pointer != _entity.Pointer || !ValidateCurrent()) return false;
            if (IsGameplayActive) return true;
            if (!PlayerEventRouting.IsSelected)
            { detail = "Gameplay activation requires the qualified late-event identity router."; return false; }
            if (!PlayerEventRouting.TryBind(this, _migrationSession, out _eventRoute, out detail))
                return false;
            _gameplayActive = true;
            detail = "Completed player endpoint activated; prepared traffic never drove gameplay.";
            return true;
        }

        /// <summary>
        /// Sends client intent only. WindowFull also denotes the 50 Hz rate limit. Every sequence
        /// passed to native transport is consumed even if delivery is uncertain. WindowFull is a
        /// pre-send rejection and may retry the same value. Other failures must not be replayed.
        /// </summary>
        public DatagramSendResult SendInput(PlayerMotionInput input)
        {
            if (Environment.CurrentManagedThreadId != _thread) return DatagramSendResult.WrongThread;
            if (_disposed || _busy || !IsGameplayActive) return DatagramSendResult.Unavailable;
            _busy = true;
            try
            {
                if (!ValidateCurrent()) return DatagramSendResult.PeerUnavailable;
                if (_server || !MatchesGroundInput(input) || !Matches(input.MigrationId, input.Generation, input.NetworkId)
                    || input.Sequence <= _sentSequence) return DatagramSendResult.InvalidPayload;
                long now = Stopwatch.GetTimestamp();
                Span<byte> bytes = stackalloc byte[PlayerBridgeProtocol.InputLength];
                if (!PlayerBridgeProtocol.TryWriteInput(input, bytes, out var length))
                    return DatagramSendResult.InvalidPayload;
                if (!ReserveCredit(now, ref _lastSend, ref _sendCredit, 2))
                    return DatagramSendResult.WindowFull;
                var result = Send(bytes.Slice(0, length));
                if (result != DatagramSendResult.WindowFull) _sentSequence = input.Sequence;
                return result;
            }
            catch (Exception error) { Retire(error); return DatagramSendResult.Failed; }
            finally { _busy = false; }
        }

        /// <summary>
        /// Sends authoritative server state. Its input acknowledgement must not regress or exceed
        /// the latest input explicitly committed after simulation, rather than merely read from the
        /// inbox. Each acknowledgement describes one post-input boundary and is published once.
        /// The adapter must publish only state it has simulated; this method does not simulate it.
        /// </summary>
        public DatagramSendResult SendState(PlayerMotionState state)
        {
            if (Environment.CurrentManagedThreadId != _thread) return DatagramSendResult.WrongThread;
            if (_disposed || _busy || !IsGameplayActive) return DatagramSendResult.Unavailable;
            _busy = true;
            try
            {
                if (!ValidateCurrent()) return DatagramSendResult.PeerUnavailable;
                if (!_server || !MatchesGroundState(state) || !Matches(state.MigrationId, state.Generation, state.NetworkId)
                    || state.Sequence <= _sentSequence || state.InputSequence != _simulatedInputSequence
                    || state.InputSequence <= _acknowledgedInputSequence)
                    return DatagramSendResult.InvalidPayload;
                long now = Stopwatch.GetTimestamp();
                Span<byte> bytes = stackalloc byte[PlayerBridgeProtocol.StateLength];
                if (!PlayerBridgeProtocol.TryWriteState(state, bytes, out var length))
                    return DatagramSendResult.InvalidPayload;
                if (!ReserveCredit(now, ref _lastSend, ref _sendCredit, 2))
                    return DatagramSendResult.WindowFull;
                var result = Send(bytes.Slice(0, length));
                if (result != DatagramSendResult.WindowFull)
                {
                    _sentSequence = state.Sequence;
                    _acknowledgedInputSequence = state.InputSequence;
                }
                return result;
            }
            catch (Exception error) { Retire(error); return DatagramSendResult.Failed; }
            finally { _busy = false; }
        }

        private DatagramSendResult Send(ReadOnlySpan<byte> payload)
        {
            var result = Datagrams.Send(Peer, payload);
            if (result is DatagramSendResult.Failed or DatagramSendResult.PeerUnavailable
                or DatagramSendResult.NotEnrolled) Dispose();
            return result;
        }

        /// <summary>
        /// Takes the latest unread input once, expiring it after at most 250 ms of local monotonic time.
        /// False means no new input. Fixed-step movement can use TryReadCurrentInput until expiry;
        /// discrete actions must not be replayed for the same sequence.
        /// </summary>
        public bool TryTakeInput(out PlayerMotionInput input)
        {
            input = default;
            if (Environment.CurrentManagedThreadId != _thread || _disposed || _busy) return false;
            _busy = true;
            try
            {
                if (!IsGroundMovementMode || !ValidateCurrent() || !_server) return false;
                ExpireInput(Stopwatch.GetTimestamp());
                if (!_hasInput) return false;
                input = _input;
                _takenInputSequence = input.Sequence;
                _hasInput = false;
                return true;
            }
            finally { _busy = false; }
        }

        /// <summary>
        /// Reads the latest movement intent until its 250 ms deadline, independent of packet cadence.
        /// False means stop movement; never keep a separately cached command past this deadline.
        /// Button edges and simulation time remain the authoritative adapter's responsibility.
        /// </summary>
        public bool TryReadCurrentInput(out PlayerMotionInput input)
            => TryReadInput(selectForSimulation: true, out input);

        /// <summary>
        /// Observes current input without changing the input selected for a pending physics step.
        /// Use for action policy and ordinary Update; only simulation reads can be committed.
        /// </summary>
        public bool TryPeekCurrentInput(out PlayerMotionInput input)
            => TryReadInput(selectForSimulation: false, out input);

        internal bool TryPeekSelectedInput(out PlayerMotionInput input)
        {
            if (TryPeekCurrentInput(out input) && input.Sequence == _takenInputSequence) return true;
            input = default;
            return false;
        }

        private bool TryReadInput(bool selectForSimulation, out PlayerMotionInput input)
        {
            input = default;
            if (Environment.CurrentManagedThreadId != _thread || _disposed || _busy) return false;
            _busy = true;
            try
            {
                if (!IsGroundMovementMode || !ValidateCurrent() || !_server) return false;
                ExpireInput(Stopwatch.GetTimestamp());
                if (_input.Sequence == 0) return false;
                input = _input;
                if (selectForSimulation) _takenInputSequence = input.Sequence;
                return true;
            }
            finally { _busy = false; }
        }

        /// <summary>
        /// Commit the exact most recently read input after its authoritative simulation step succeeds.
        /// Reading input alone is never an acknowledgement. Use server-owned time for simulation;
        /// client DeltaTime is untrusted. Do not commit partial, skipped or failed simulation, and do
        /// not replay discrete actions when reading the same latched input on subsequent steps.
        /// </summary>
        public bool TryCommitSimulatedInput(uint inputSequence)
        {
            if (Environment.CurrentManagedThreadId != _thread || _disposed || _busy) return false;
            _busy = true;
            try
            {
                if (!IsGroundMovementMode || !ValidateCurrent() || !_server || inputSequence == 0
                    || inputSequence != _takenInputSequence || inputSequence <= _simulatedInputSequence)
                    return false;
                _simulatedInputSequence = inputSequence;
                return true;
            }
            finally { _busy = false; }
        }

        /// <summary>Takes the latest unread server state once without applying it to any native object.</summary>
        public bool TryTakeState(out PlayerMotionState state)
        {
            state = default;
            if (Environment.CurrentManagedThreadId != _thread || _disposed || _busy) return false;
            _busy = true;
            try
            {
                if (!IsGroundMovementMode || !ValidateCurrent() || _server || !_hasState) return false;
                state = _receivedState;
                _receivedState = default;
                _hasState = false;
                return true;
            }
            finally { _busy = false; }
        }

        private static void OnReceived(Datagram datagram)
        {
            if (Environment.CurrentManagedThreadId != _ownerThread || datagram?.Peer == null
                || !Bridges.TryGetValue(datagram.Peer.PeerId, out var bridge)
                || !ReferenceEquals(bridge.Peer, datagram.Peer)) return;
            bridge.Receive(datagram.Payload.Span);
        }

        private void Receive(ReadOnlySpan<byte> bytes)
        {
            if (_disposed || _busy || !IsGameplayActive) return;
            if (TryReceiveModeTransition(bytes)) return;
            if (TryReceiveNativeModes(bytes) || !IsGroundMovementMode) return;
            // This shared datagram channel may also carry other application protocols.
            if (bytes.Length != (_server ? PlayerBridgeProtocol.InputLength
                : PlayerBridgeProtocol.StateLength)
                || !PlayerBridgeProtocol.MatchesEnvelope(bytes)) return;
            long now = Stopwatch.GetTimestamp();
            _busy = true;
            try
            {
                if (!ValidateCurrent()) return;
                if (_server)
                {
                    ExpireInput(now);
                    if (!PlayerBridgeProtocol.TryReadInput(bytes, out var input)
                        || !MatchesGroundInput(input)
                        || !Matches(input.MigrationId, input.Generation, input.NetworkId)
                        || input.Sequence <= _receivedSequence
                        || !ReserveCredit(now, ref _lastReceive, ref _receiveCredit, 4)) return;
                    _receivedSequence = input.Sequence;
                    _input = input;
                    _inputReceivedAt = now;
                    _hasInput = true;
                }
                else
                {
                    if (!PlayerBridgeProtocol.TryReadState(bytes, out var state)
                        || !MatchesGroundState(state)
                        || !Matches(state.MigrationId, state.Generation, state.NetworkId)
                        || state.Sequence <= _receivedSequence || state.InputSequence > _sentSequence
                        || state.InputSequence < _acknowledgedInputSequence
                        || !ReserveCredit(now, ref _lastReceive, ref _receiveCredit, 4)) return;
                    _receivedSequence = state.Sequence;
                    _acknowledgedInputSequence = state.InputSequence;
                    _receivedState = state;
                    _hasState = true;
                }
            }
            catch (Exception error) { Retire(error); }
            finally { _busy = false; }
        }

        private void ExpireInput(long now)
        {
            if (_input.Sequence == 0 || now - _inputReceivedAt < InputLifetime) return;
            _input = default;
            _hasInput = false;
        }

        // Preserve the 50 Hz average while tolerating packet/FixedUpdate scheduling jitter.
        // Invalid or stale records never consume another valid record's receive allowance.
        private static bool ReserveCredit(long now, ref long last, ref double credit, double capacity)
        {
            if (last != 0 && now >= last)
                credit = System.Math.Min(capacity, credit + (now - last) * (50.0 / Stopwatch.Frequency));
            last = now;
            if (credit < 1) return false;
            credit--;
            return true;
        }

        private bool Matches(Guid migrationId, ulong generation, ulong networkId)
            => migrationId == Plan.Id && generation == Plan.Generation && networkId == NetworkId;

        internal bool TryReadCurrentTarget(out BoltEntity entity)
        {
            entity = null;
            if (!ValidateCurrent()) return false;
            entity = _entity;
            return true;
        }

        private bool ValidateCurrent()
        {
            if (_disposed || Environment.CurrentManagedThreadId != _thread) return false;
            try
            {
                if (MatchesCurrent() && !_disposed) return true;
            }
            catch (Exception error) { Retire(error); return false; }
            Dispose();
            return false;
        }

        // Unexpected faults retire every dependent owner, so record the first cause once.
        private void Retire(Exception error)
        {
            if (!_disposed && RetireReason == null)
            {
                RetireReason = error.GetType().Name + ": " + error.Message;
                global::Modstoke.Loader.RuntimeEntryPoint.LogSource?.LogWarning(
                    "Player bridge " + Plan.Id + " retired after an unexpected failure (" + RetireReason + ").");
            }
            Dispose();
        }

        private bool MatchesCurrent()
        {
            if (!_migrationSession.PlayerBridgeLifetimeCurrent || !PlayerRoles.IsAvailable || !Peer.IsCurrent
                || ClientEnrollment.TryRead(Peer, out var enrollment) != EnrollmentReadStatus.Success
                || enrollment == null || !ReferenceEquals(enrollment.Peer, Peer)
                || enrollment.State != EnrollmentState.Enrolled
                || (enrollment.RequiredCapabilities & RequiredCapabilities) != RequiredCapabilities
                || (enrollment.OfferedCapabilities & RequiredCapabilities) != RequiredCapabilities
                || !ClientEnrollment.AllowsGameTraffic(Peer)) return false;
            var connection = ControlTransport.Resolve(Peer);
            if (connection == null || connection.WasCollected || !BoltNetwork.isRunning
                || (_server ? !BoltNetwork.isServer : !BoltNetwork.isClient || BoltNetwork.isServer)
                || _entity == null || _entity.WasCollected || !_entity || !_entity.isAttached
                || _attachment == null || _attachment.WasCollected || !_attachment.IsAttached
                || _entity._entity?.Pointer != _attachment.Pointer
                || _attachment.UnityObject?.Pointer != _entity.Pointer
                || _gameObject == null || _gameObject.WasCollected || !_gameObject
                || _entity.gameObject?.Pointer != _gameObject.Pointer
                || _entity.networkId.PackedValue != NetworkId || _attachment.NetworkId.PackedValue != NetworkId
                || _entity.prefabId != BoltPrefabs.PlayerNetwork || _attachment.PrefabId.Value != Plan.PrefabId
                || _entity.isOwner != _server || _attachment.IsOwner != _server
                || _state == null || _state.WasCollected || _attachment.Serializer?.Pointer != _state.Pointer
                || _state.Entity?.Pointer != _attachment.Pointer
                || _playerState == null || _playerState.WasCollected || _playerState.Pointer != _state.Pointer
                || _entity.GetState<IPlayerState>()?.Pointer != _playerState.Pointer
                || _meta == null || _meta.WasCollected || _state.Meta?.Pointer != _meta.Pointer
                || _meta.TypeId.Value != Plan.StateTypeId || _entity.serializerGuid != _serializerId)
                return false;

            if (!_server)
                return _retainedRoot != null && !_retainedRoot.WasCollected && _retainedRoot
                    && _retainedController != null && !_retainedController.WasCollected && _retainedController
                    && _retainedBody != null && !_retainedBody.WasCollected && _retainedBody
                    && LocalPlayer.GameObject?.Pointer == _retainedRoot.Pointer
                    && LocalPlayer.FpCharacter?.Pointer == _retainedController.Pointer
                    && LocalPlayer.Rigidbody?.Pointer == _retainedBody.Pointer
                    && _retainedController._rigidbody?.Pointer == _retainedBody.Pointer
                    && LocalPlayer.Entity?.Pointer == _entity.Pointer
                    && LocalPlayer.State?.Pointer == _playerState.Pointer
                    && _entity.hasControl && BoltNetwork.server?.Pointer == connection.Pointer
                    && _entity.source?.Pointer == connection.Pointer
                    && _attachment.Source?.Pointer == connection.Pointer;

            var controller = _entity.controller;
            if (!Peer.ServerSession.IsCurrent || _entity.source != null || _attachment.Source != null
                || controller == null || controller.WasCollected || controller.Pointer != connection.Pointer
                || controller.ConnectionId != Peer.ConnectionId
                || _attachment.Controller?.Pointer != connection.Pointer
                || _playerBinding == null || _playerBinding.IsRetired || _playerBinding.IsLocalPlayer
                || !ReferenceEquals(_playerBinding.PlayerSession, Peer.ServerSession)
                || _playerBinding.Player?.Pointer != _entity.Pointer
                || _playerBinding.Attachment?.Pointer != _attachment.Pointer
                || _playerBinding.NetworkId != NetworkId) return false;
            return ServerWorld.IsCurrentMigratedPlayerBinding(_playerBinding, Peer);
        }

        private static void OnClosed(ControlPeer peer)
            => RetirePeer(peer);

        internal static void RetirePeer(ControlPeer peer)
        {
            if (Environment.CurrentManagedThreadId == _ownerThread && peer != null
                && Bridges.TryGetValue(peer.PeerId, out var bridge) && ReferenceEquals(bridge.Peer, peer))
                bridge.Dispose();
        }

        internal static void Tick()
        {
            if (_ticking || Bridges.Count == 0 || Environment.CurrentManagedThreadId != _ownerThread) return;
            _ticking = true;
            try
            {
                // Retirement handlers may add/remove a bridge. Reuse a peer-sized snapshot
                // instead of enumerating a mutating dictionary or allocating each frame.
                TickSnapshot.AddRange(Bridges.Values);
                foreach (var bridge in TickSnapshot)
                    if (bridge.ValidateCurrent()) bridge.TickModeTransitions();
            }
            finally { TickSnapshot.Clear(); _ticking = false; }
        }

        /// <summary>Releases the inbox and exact-peer registration. Must run on the binding's game thread.</summary>
        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("Dispose the player bridge on its game thread.");
            if (_disposed) return;
            _disposed = true;
            _gameplayActive = false;
            DisposeModeTransitions();
            DisposeNativeModes();
            _eventRoute?.Dispose();
            _eventRoute = null;
            var retired = Retired;
            Retired = null;
            bool handlerReported = false;
            if (retired != null)
                foreach (Action handler in retired.GetInvocationList())
                    try { handler(); }
                    catch (Exception error)
                    {
                        // Every handler still runs; report only the first failure.
                        if (handlerReported) continue;
                        handlerReported = true;
                        global::Modstoke.Loader.RuntimeEntryPoint.LogSource?.LogWarning(
                            "Player bridge " + Plan.Id + " retirement handler failed (" + error.GetType().Name + ": " + error.Message + ").");
                    }
            _input = default;
            _receivedState = default;
            _hasInput = _hasState = false;
            _entity = null;
            _attachment = null;
            _gameObject = null;
            _state = null;
            _playerState = null;
            _meta = null;
            _playerBinding = null;
            _retainedRoot = null;
            _retainedController = null;
            _retainedBody = null;
            if (Bridges.TryGetValue(Peer.PeerId, out var registered) && ReferenceEquals(registered, this))
                Bridges.Remove(Peer.PeerId);
            if (Bridges.Count != 0) return;
            Datagrams.Received -= OnReceived;
            ControlTransport.Closed -= OnClosed;
        }

        internal static void ResetForStart()
        {
            if (Environment.CurrentManagedThreadId != _ownerThread) return;
            // Snapshot only on lifecycle teardown, never on the per-message path.
            foreach (var bridge in new List<PlayerBridge>(Bridges.Values)) bridge.Dispose();
            UsedMigrations.Clear();
        }
    }
}
