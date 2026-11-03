// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Sons
{
    public enum PlayerCorrectionStatus
    {
        Prepared, Unavailable, WrongThread, InvalidState, StaleState,
        HistoryUnavailable, CorrectionPending, ResynchronizationRequired
    }

    /// <summary>
    /// Translation and velocity error at an acknowledged prediction boundary. Apply these offsets
    /// to the current body, not the old server position. View rotation and camera state are untouched.
    /// </summary>
    public readonly record struct PlayerCorrection(
        Guid MigrationId, ulong Generation, ulong NetworkId, ulong Ticket,
        uint StateSequence, uint InputSequence, Vector3 PositionOffset, Vector3 VelocityOffset);

    /// <summary>
    /// Bounded, game-thread prediction history for a single migrated player. An entry must be the
    /// post-simulation pose for its input, and the server must acknowledge that same boundary.
    /// This computes additive corrections, not collision-aware command replay or a locomotion motor.
    /// Nothing is applied automatically and no camera, rotation, input or native state is changed here.
    /// </summary>
    public sealed class PlayerMotionHistory
    {
        public const int Capacity = 128;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly Guid _migrationId;
        private readonly ulong _generation;
        private readonly ulong _networkId;
        private readonly Prediction[] _history = new Prediction[Capacity];
        private int _head;
        private int _count;
        private uint _lastPrediction;
        private uint _lastState;
        private uint _lastAcknowledgement;
        private uint _movementTransitionSequence;
        private bool _hasMovementMode;
        private ulong _nextTicket;
        private PlayerCorrection _pending;
        private bool _invalidated;

        public PlayerMotionHistory(Guid migrationId, ulong generation, ulong networkId)
        {
            if (migrationId == Guid.Empty || generation == 0 || networkId == 0)
                throw new ArgumentException("A complete migrated-player identity is required.");
            _migrationId = migrationId;
            _generation = generation;
            _networkId = networkId;
        }

        internal bool TryBeginMode(uint transitionSequence)
        {
            if (Environment.CurrentManagedThreadId != _thread || _invalidated || _pending.Ticket != 0
                || transitionSequence <= _movementTransitionSequence) return false;
            if (!TryClearPredictions()) return false;
            _movementTransitionSequence = transitionSequence;
            _hasMovementMode = true;
            return true;
        }

        internal bool TryClearPredictions()
        {
            if (Environment.CurrentManagedThreadId != _thread || _invalidated || _pending.Ticket != 0) return false;
            Array.Clear(_history);
            _head = _count = 0;
            return true;
        }

        internal bool ContainsPrediction(uint sequence)
        {
            if (Environment.CurrentManagedThreadId != _thread || _invalidated || sequence == 0) return false;
            return FindPredictionOffset(sequence) >= 0;
        }

        private int FindPredictionOffset(uint sequence)
        {
            for (int i = 0; i < _count; i++)
                if (_history[(_head + i) % Capacity].Sequence == sequence) return i;
            return -1;
        }

        /// <summary>Record once after simulation. Sequence gaps are permitted, wraparound is not.</summary>
        public bool TryRecordPrediction(uint inputSequence, Vector3 position, Vector3 velocity)
            => RecordPrediction(inputSequence, position, velocity, null);

        /// <summary>Also retain native mode flags; an ACK with different flags requires resynchronization.</summary>
        public bool TryRecordPrediction(uint inputSequence, Vector3 position, Vector3 velocity,
            PlayerMotionFlags flags)
            => PlayerBridgeProtocol.IsValidFlags(flags)
                && RecordPrediction(inputSequence, position, velocity, flags);

        private bool RecordPrediction(uint inputSequence, Vector3 position, Vector3 velocity,
            PlayerMotionFlags? flags)
        {
            if (Environment.CurrentManagedThreadId != _thread || _invalidated || _pending.Ticket != 0
                || inputSequence == 0 || inputSequence <= _lastPrediction
                || !Finite(position) || !Finite(velocity)) return false;
            int index = (_head + _count) % Capacity;
            if (_count == Capacity)
            {
                index = _head;
                _head = (_head + 1) % Capacity;
            }
            else _count++;
            _history[index] = new Prediction(inputSequence, position, velocity, flags);
            _lastPrediction = inputSequence;
            return true;
        }

        /// <summary>
        /// Prepare without consuming history. Limits are supplied by the game's movement mode;
        /// exceeding them requires explicit resynchronization, never an implicit teleport.
        /// Commit only after the body accepted the entire correction. Do not record another frame
        /// between preparation and commit. Invalidate if native application is partial or uncertain.
        /// </summary>
        public PlayerCorrectionStatus TryPrepareCorrection(PlayerMotionState state,
            float maximumPositionCorrection, float maximumVelocityCorrection,
            out PlayerCorrection correction)
        {
            correction = default;
            if (Environment.CurrentManagedThreadId != _thread)
                return PlayerCorrectionStatus.WrongThread;
            if (_invalidated) return PlayerCorrectionStatus.Unavailable;
            if (_pending.Ticket != 0) return PlayerCorrectionStatus.CorrectionPending;
            Span<byte> encoded = stackalloc byte[PlayerBridgeProtocol.StateLength];
            if (state.MigrationId != _migrationId || state.Generation != _generation || state.NetworkId != _networkId
                || !float.IsFinite(maximumPositionCorrection) || maximumPositionCorrection < 0
                || !float.IsFinite(maximumVelocityCorrection) || maximumVelocityCorrection < 0
                || !PlayerBridgeProtocol.TryWriteState(state, encoded, out _))
                return PlayerCorrectionStatus.InvalidState;
            if (state.Sequence <= _lastState || state.InputSequence <= _lastAcknowledgement)
                return PlayerCorrectionStatus.StaleState;
            if (_hasMovementMode && state.MovementTransitionSequence != _movementTransitionSequence)
                return PlayerCorrectionStatus.StaleState;
            int found = FindPredictionOffset(state.InputSequence);
            if (found < 0) return PlayerCorrectionStatus.HistoryUnavailable;

            Prediction acknowledged = _history[(_head + found) % Capacity];
            if (acknowledged.Flags.HasValue && acknowledged.Flags.Value != state.Flags)
                return PlayerCorrectionStatus.ResynchronizationRequired;
            Vector3 positionOffset = Subtract(state.Position, acknowledged.Position);
            Vector3 velocityOffset = Subtract(state.Velocity, acknowledged.Velocity);
            if (!Within(positionOffset, maximumPositionCorrection) || !Within(velocityOffset, maximumVelocityCorrection))
                return PlayerCorrectionStatus.ResynchronizationRequired;
            // A committed correction also changes the coordinate baseline of unacknowledged frames.
            // Preflight all of them before returning an offset that a caller may apply to a native body.
            for (int i = found + 1; i < _count; i++)
            {
                Prediction future = _history[(_head + i) % Capacity];
                // Landing and capsule-height changes are not translation-invariant. A
                // correction through either boundary needs native replay, not an offset.
                if (acknowledged.Flags.HasValue && future.Flags != acknowledged.Flags
                    && (positionOffset.x != 0 || positionOffset.y != 0 || positionOffset.z != 0
                        || velocityOffset.x != 0 || velocityOffset.y != 0 || velocityOffset.z != 0))
                    return PlayerCorrectionStatus.ResynchronizationRequired;
                if (!Finite(Add(future.Position, positionOffset)) || !Finite(Add(future.Velocity, velocityOffset)))
                    return PlayerCorrectionStatus.ResynchronizationRequired;
            }
            if (_nextTicket == ulong.MaxValue) return PlayerCorrectionStatus.Unavailable;
            correction = _pending = new PlayerCorrection(_migrationId, _generation, _networkId, ++_nextTicket,
                state.Sequence, state.InputSequence, positionOffset, velocityOffset);
            return PlayerCorrectionStatus.Prepared;
        }

        /// <summary>
        /// Commit the exact prepared value after complete application. Shifts remaining predictions
        /// by the same offsets so the next acknowledgement cannot apply the previous error twice.
        /// </summary>
        public bool TryCommitCorrection(PlayerCorrection correction)
        {
            if (Environment.CurrentManagedThreadId != _thread || _invalidated || _pending.Ticket == 0
                || !correction.Equals(_pending)) return false;
            while (_count > 0 && _history[_head].Sequence <= correction.InputSequence)
            {
                _history[_head] = default;
                _head = (_head + 1) % Capacity;
                _count--;
            }
            for (int i = 0; i < _count; i++)
            {
                int index = (_head + i) % Capacity;
                Prediction old = _history[index];
                _history[index] = new Prediction(old.Sequence, Add(old.Position, correction.PositionOffset),
                    Add(old.Velocity, correction.VelocityOffset), old.Flags);
            }
            _lastState = correction.StateSequence;
            _lastAcknowledgement = correction.InputSequence;
            _pending = default;
            return true;
        }

        /// <summary>Cancel only when no native mutation was attempted. Uncertain writes require Invalidate.</summary>
        public bool TryCancelCorrection(PlayerCorrection correction)
        {
            if (Environment.CurrentManagedThreadId != _thread || _invalidated || _pending.Ticket == 0
                || !correction.Equals(_pending)) return false;
            _pending = default;
            return true;
        }

        /// <summary>Retire this history after teleport, respawn, role change or uncertain application.</summary>
        public void Invalidate()
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("Invalidate prediction history on its game thread.");
            _invalidated = true;
            Array.Clear(_history);
            _head = _count = 0;
            _pending = default;
        }

        private readonly record struct Prediction(uint Sequence, Vector3 Position, Vector3 Velocity,
            PlayerMotionFlags? Flags);
        private static Vector3 Add(Vector3 a, Vector3 b) => new() { x = a.x + b.x, y = a.y + b.y, z = a.z + b.z };
        private static Vector3 Subtract(Vector3 a, Vector3 b) => new() { x = a.x - b.x, y = a.y - b.y, z = a.z - b.z };
        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        private static bool Within(Vector3 value, float limit) => Finite(value)
            && (double)value.x * value.x + (double)value.y * value.y + (double)value.z * value.z <= (double)limit * limit;
    }
}
