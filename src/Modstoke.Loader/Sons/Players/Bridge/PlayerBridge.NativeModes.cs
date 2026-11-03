// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;
using NativeInput = Modstoke.Sons.PlayerClientMovement.NativeInput;
using NativeMode = Modstoke.Sons.PlayerClientMovement.NativeMode;
using NativeModeKey = Modstoke.Sons.PlayerClientMovement.NativeModeKey;
using NativeState = Modstoke.Sons.PlayerClientMovement.NativeState;

namespace Modstoke.Sons
{
    public sealed partial class PlayerBridge
    {
        private NativeModeKey _movementModeKey = NativeModeKey.Ground();
        private uint _movementInputFloor;
        private NativeInput _nativeInput, _selectedNativeInput, _simulatedNativeInput;
        private NativeState _receivedNativeState;
        private long _nativeInputReceivedAt, _selectedNativeInputAt;
        private readonly NativeSentInput[] _nativeSentInputs = new NativeSentInput[PlayerMotionHistory.Capacity];
        private int _nativeSentCursor;
        private BoltMigrationSession _cartRebindSession;
        private CartPlayerMigrationContext _cartRebindContext;
        private NativeModeKey _cartRebindExpected, _cartRebindReplacement;

        internal bool TryPrepareCartMigrationRebind(BoltMigrationSession session,
            CartPlayerMigrationContext context, NativeModeKey expected,
            out NativeModeKey replacement, out string detail)
        {
            replacement = default;
            detail = "The exact held cart mode must be drained before preparing a physical-cart rebind.";
            if (session == null || expected != _movementModeKey || !CartMigrationBoundaryDrained
                || !session.TryAuthorizeCartPlayerRebind(this, context, expected, out replacement, out detail)) return false;
            if (_cartRebindSession != null)
                return ReferenceEquals(_cartRebindSession, session) && ReferenceEquals(_cartRebindContext, context)
                    && _cartRebindExpected == expected && _cartRebindReplacement == replacement;
            _cartRebindSession = session; _cartRebindContext = context;
            _cartRebindExpected = expected; _cartRebindReplacement = replacement;
            return true;
        }

        internal bool TryCommitCartMigrationRebind(BoltMigrationSession session,
            CartPlayerMigrationContext context, NativeModeKey expected,
            out NativeModeKey replacement, out string detail)
        {
            replacement = default;
            detail = "Commit requires the prepared exact session and its still-current repaired authorization.";
            if (session == null || !ReferenceEquals(_cartRebindSession, session) || !ReferenceEquals(_cartRebindContext, context)
                || expected != _cartRebindExpected || !CartMigrationBoundaryDrained
                || !session.TryAuthorizeCartPlayerRebind(this, context, expected, out replacement, out detail)
                || replacement != _cartRebindReplacement || _movementModeKey != expected) return false;
            SelectMovementMode(replacement);
            _cartRebindSession = null; _cartRebindContext = null;
            _cartRebindExpected = _cartRebindReplacement = default;
            detail = "Physical cart anchor committed without changing player identity or resetting input/ACK sequences.";
            return true;
        }

        /// <summary>
        /// Select only the exact pending proposal on the server or received approval on
        /// the client, after the lifecycle owner completes its native handoff. Prefer the
        /// typed transition APIs so the caller retains the complete transaction identity.
        /// An arbitrary client key change can never grant movement authority.
        /// </summary>
        public bool TryCommitMovementMode(NativeModeKey expected, NativeModeKey committed, out string detail)
        {
            detail = "The exact pending proposal or authenticated approval is required; direct mode selection is not allowed.";
            if (!CanAccessModeTransitions() || !HasPendingMovementTransition
                || _modeProposal.Expected != expected || _modeProposal.Requested != committed) return false;
            return _server ? TryApproveMovementTransition(_modeProposal, out _, out detail)
                : _modeApproval.Decision == PlayerModeTransitionDecision.Approved
                    && TryCommitMovementTransition(_modeApproval, out detail);
        }

        // The transition state machine owns this selection. Never call it from packet ingress
        // or continuous input; callers already hold _busy and have validated the exact transaction.
        private bool TrySelectCommittedMovementMode(NativeModeKey expected, NativeModeKey committed, out string detail)
        {
            detail = "The exact next movement key and completed native authority handoff are required.";
            if (expected != _movementModeKey
                || !PlayerBridgeProtocol.IsValidMovementModeKey(committed)
                || expected.TransitionSequence == uint.MaxValue
                || committed.TransitionSequence != expected.TransitionSequence + 1
                || (expected.Mode == NativeMode.Ground) == (committed.Mode == NativeMode.Ground)) return false;
            if (_server && (expected.Mode == NativeMode.Ground
                    ? _takenInputSequence > _simulatedInputSequence
                    : _selectedNativeInput.Input.Sequence > _simulatedInputSequence))
            {
                detail = "The selected authoritative physics step must finish before changing movement mode.";
                return false;
            }
            // Keep transport, simulation and ACK high-water marks across mode changes.
            // Only inboxes/read tickets/history are retired; no old mode can be ACKed
            // or committed through the new mode, even if a delayed packet arrives.
            SelectMovementMode(committed);
            detail = "Committed movement key selected; original action binding and server simulation remain required.";
            return true;
        }

        private void SelectMovementMode(NativeModeKey committed)
        {
            _movementInputFloor = _server ? _receivedSequence : _sentSequence;
            _movementModeKey = committed;
            ClearNativeMovementInboxes();
            _input = default;
            _receivedState = default;
            _hasInput = _hasState = false;
            _takenInputSequence = 0;
        }

        public bool IsCommittedMovementMode(NativeModeKey key)
            => Environment.CurrentManagedThreadId == _thread && !_disposed && !_busy && IsGameplayActive
                && ValidateCurrent() && key == _movementModeKey;

        // The parent ground paths use these as well as their existing role/identity gates.
        internal bool IsGroundMovementMode => IsGameplayActive && _movementModeKey.Mode == NativeMode.Ground;
        /// <summary>Preserve the server's publication high-water mark across movement modes.</summary>
        public uint LastSentStateSequence => _server ? _sentSequence : 0;
        private bool MatchesGroundInput(PlayerMotionInput input)
            => IsGroundMovementMode && input.MovementTransitionSequence == _movementModeKey.TransitionSequence
                && input.Sequence > _movementInputFloor;
        private bool MatchesGroundState(PlayerMotionState state)
            => IsGroundMovementMode && state.MovementTransitionSequence == _movementModeKey.TransitionSequence
                && state.InputSequence > _movementInputFloor;

        /// <summary>
        /// Publishes continuous native intent using the enrolled, exact-peer datagram route.
        /// WindowFull is pre-send backpressure; only this result may be retried/coalesced.
        /// Enter/exit and other one-shot actions have no representation in this packet.
        /// </summary>
        public DatagramSendResult SendNativeInput(NativeInput input)
        {
            if (Environment.CurrentManagedThreadId != _thread) return DatagramSendResult.WrongThread;
            if (_disposed || _busy || !IsGameplayActive) return DatagramSendResult.Unavailable;
            _busy = true;
            try
            {
                if (!ValidateCurrent()) return DatagramSendResult.PeerUnavailable;
                var motion = input.Input;
                if (_server || input.Key != _movementModeKey
                    || !Matches(motion.MigrationId, motion.Generation, motion.NetworkId)
                    || motion.Sequence <= _sentSequence || motion.Sequence <= _movementInputFloor)
                    return DatagramSendResult.InvalidPayload;
                Span<byte> bytes = stackalloc byte[PlayerBridgeProtocol.NativeInputLength];
                if (!PlayerBridgeProtocol.TryWriteNativeInput(input, bytes, out var length))
                    return DatagramSendResult.InvalidPayload;
                if (!ReserveCredit(Stopwatch.GetTimestamp(), ref _lastSend, ref _sendCredit, 2))
                    return DatagramSendResult.WindowFull;
                var result = Send(bytes.Slice(0, length));
                if (result != DatagramSendResult.WindowFull) _sentSequence = motion.Sequence;
                if (result == DatagramSendResult.Queued)
                {
                    _nativeSentInputs[_nativeSentCursor] = new NativeSentInput(motion.Sequence, input.SeatSettled);
                    _nativeSentCursor = (_nativeSentCursor + 1) % _nativeSentInputs.Length;
                }
                return result;
            }
            catch (Exception error) { Retire(error); return DatagramSendResult.Failed; }
            finally { _busy = false; }
        }

        /// <summary>
        /// Publish only the last explicitly committed simulation boundary. Glider state is
        /// guide-body world pose/velocity; seat state is the player's cart-local pose and
        /// zero relative velocity. The vehicle's world motion has its own authority path.
        /// </summary>
        public DatagramSendResult SendNativeState(NativeState state)
        {
            if (Environment.CurrentManagedThreadId != _thread) return DatagramSendResult.WrongThread;
            if (_disposed || _busy || !IsGameplayActive) return DatagramSendResult.Unavailable;
            _busy = true;
            try
            {
                if (!ValidateCurrent()) return DatagramSendResult.PeerUnavailable;
                var motion = state.Motion;
                if (!_server || state.Key != _movementModeKey || _simulatedNativeInput.Key != state.Key
                    || !Matches(motion.MigrationId, motion.Generation, motion.NetworkId)
                    || motion.Sequence <= _sentSequence || motion.InputSequence <= _movementInputFloor
                    || motion.InputSequence != _simulatedNativeInput.Input.Sequence
                    || motion.InputSequence != _simulatedInputSequence
                    || motion.InputSequence <= _acknowledgedInputSequence
                    || (PlayerBridgeProtocol.IsCartMode(state.Key.Mode) && !_simulatedNativeInput.SeatSettled))
                    return DatagramSendResult.InvalidPayload;
                Span<byte> bytes = stackalloc byte[PlayerBridgeProtocol.NativeStateLength];
                if (!PlayerBridgeProtocol.TryWriteNativeState(state, bytes, out var length))
                    return DatagramSendResult.InvalidPayload;
                if (!ReserveCredit(Stopwatch.GetTimestamp(), ref _lastSend, ref _sendCredit, 2))
                    return DatagramSendResult.WindowFull;
                var result = Send(bytes.Slice(0, length));
                if (result != DatagramSendResult.WindowFull)
                {
                    _sentSequence = motion.Sequence;
                    _acknowledgedInputSequence = motion.InputSequence;
                }
                return result;
            }
            catch (Exception error) { Retire(error); return DatagramSendResult.Failed; }
            finally { _busy = false; }
        }

        /// <summary>Observe levels without replacing a pending simulation's selected input.</summary>
        public bool TryPeekCurrentNativeInput(out NativeInput input)
            => TryReadNativeInput(false, out input);

        /// <summary>
        /// Select the exact input for an authoritative step. False means stop applying
        /// levels, including boost/brake, rather than retaining an expired command.
        /// Use server-owned time; the client's glider DeltaTime is not simulation authority.
        /// </summary>
        public bool TryReadCurrentNativeInput(out NativeInput input)
            => TryReadNativeInput(true, out input);

        private bool TryReadNativeInput(bool selectForSimulation, out NativeInput input)
        {
            input = default;
            if (Environment.CurrentManagedThreadId != _thread || _disposed || _busy || !IsGameplayActive) return false;
            _busy = true;
            try
            {
                if (!ValidateCurrent() || !_server) return false;
                ExpireNativeInput(Stopwatch.GetTimestamp());
                if (_nativeInput.Input.Sequence == 0 || _nativeInput.Key != _movementModeKey) return false;
                input = _nativeInput;
                if (selectForSimulation)
                {
                    _selectedNativeInput = input;
                    _selectedNativeInputAt = _nativeInputReceivedAt;
                }
                return true;
            }
            finally { _busy = false; }
        }

        /// <summary>
        /// Commit after the exact selected native step succeeds. A peek, a newer incoming
        /// packet or a mode transition cannot acknowledge an earlier pending step.
        /// Repeated reads can maintain levels, but the same sequence is committed once.
        /// </summary>
        public bool TryCommitSimulatedNativeInput(NativeModeKey key, uint inputSequence)
        {
            if (Environment.CurrentManagedThreadId != _thread || _disposed || _busy || !IsGameplayActive) return false;
            _busy = true;
            try
            {
                if (!ValidateCurrent() || !_server || key != _movementModeKey || key != _selectedNativeInput.Key
                    || inputSequence == 0 || inputSequence != _selectedNativeInput.Input.Sequence
                    || inputSequence <= _simulatedInputSequence || inputSequence <= _movementInputFloor
                    || Stopwatch.GetTimestamp() - _selectedNativeInputAt >= InputLifetime) return false;
                _simulatedInputSequence = inputSequence;
                _simulatedNativeInput = _selectedNativeInput;
                return true;
            }
            finally { _busy = false; }
        }

        /// <summary>Take authenticated native state once; the client applies it only at its native pose boundary.</summary>
        public bool TryTakeNativeState(out NativeState state)
        {
            state = default;
            if (Environment.CurrentManagedThreadId != _thread || _disposed || _busy || !IsGameplayActive) return false;
            _busy = true;
            try
            {
                if (!ValidateCurrent() || _server || _receivedNativeState.Motion.Sequence == 0
                    || _receivedNativeState.Key != _movementModeKey) return false;
                state = _receivedNativeState;
                _receivedNativeState = default;
                return true;
            }
            finally { _busy = false; }
        }

        // Dispatch only from the parent's OnReceived -> Receive path, after exact peer
        // reference lookup. No separate subscription bypasses connection/enrollment checks.
        private bool TryReceiveNativeModes(ReadOnlySpan<byte> bytes)
        {
            if (TryReceiveZipline(bytes)) return true;
            if (!PlayerBridgeProtocol.MatchesNativeEnvelope(bytes)) return false;
            if (Environment.CurrentManagedThreadId != _thread || _disposed || _busy || !IsGameplayActive) return true;
            _busy = true;
            try
            {
                // Wire and sequence checks come first; the live binding check is costly.
                long now = Stopwatch.GetTimestamp();
                if (_server)
                {
                    if (!PlayerBridgeProtocol.TryReadNativeInput(bytes, out var input)
                        || input.Key != _movementModeKey
                        || !Matches(input.Input.MigrationId, input.Input.Generation, input.Input.NetworkId)
                        || input.Input.Sequence <= _receivedSequence || input.Input.Sequence <= _movementInputFloor
                        || !ValidateCurrent()
                        || !ReserveCredit(now, ref _lastReceive, ref _receiveCredit, 4)) return true;
                    _receivedSequence = input.Input.Sequence;
                    _nativeInput = input;
                    _nativeInputReceivedAt = now;
                }
                else
                {
                    if (!PlayerBridgeProtocol.TryReadNativeState(bytes, out var state)
                        || state.Key != _movementModeKey
                        || !Matches(state.Motion.MigrationId, state.Motion.Generation, state.Motion.NetworkId)
                        || state.Motion.Sequence <= _receivedSequence || state.Motion.InputSequence > _sentSequence
                        || state.Motion.InputSequence <= _acknowledgedInputSequence
                        || state.Motion.InputSequence <= _movementInputFloor
                        || !WasNativeInputSent(state.Motion.InputSequence, PlayerBridgeProtocol.IsCartMode(state.Key.Mode))
                        || !ValidateCurrent()
                        || !ReserveCredit(now, ref _lastReceive, ref _receiveCredit, 4)) return true;
                    _receivedSequence = state.Motion.Sequence;
                    _acknowledgedInputSequence = state.Motion.InputSequence;
                    _receivedNativeState = state;
                }
            }
            catch (Exception error) { Retire(error); }
            finally { _busy = false; }
            return true;
        }

        private bool WasNativeInputSent(uint sequence, bool requireSettledSeat)
        {
            foreach (var sent in _nativeSentInputs)
                if (sent.Sequence == sequence) return !requireSettledSeat || sent.SeatSettled;
            return false;
        }

        private void ExpireNativeInput(long now)
        {
            if (_nativeInput.Input.Sequence != 0 && now - _nativeInputReceivedAt >= InputLifetime)
                _nativeInput = default;
        }

        private void ClearNativeMovementInboxes()
        {
            _nativeInput = _selectedNativeInput = _simulatedNativeInput = default;
            _receivedNativeState = default;
            _nativeInputReceivedAt = _selectedNativeInputAt = 0;
            Array.Clear(_nativeSentInputs);
            _nativeSentCursor = 0;
        }

        // Called by parent Dispose before retirement notifications. No hooks/subscriptions
        // are installed by this partial; parent bridge registration is the only route.
        private void DisposeNativeModes()
        {
            ClearZiplineReceipts();
            ClearNativeMovementInboxes();
            _cartRebindSession = null; _cartRebindContext = null;
            _cartRebindExpected = _cartRebindReplacement = default;
        }

        private readonly record struct NativeSentInput(uint Sequence, bool SeatSettled);
    }
}
