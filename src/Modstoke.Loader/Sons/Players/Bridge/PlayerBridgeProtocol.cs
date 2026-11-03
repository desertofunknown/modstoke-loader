// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using UnityEngine;
using NativeInput = Modstoke.Sons.PlayerClientMovement.NativeInput;
using NativeMode = Modstoke.Sons.PlayerClientMovement.NativeMode;
using NativeModeKey = Modstoke.Sons.PlayerClientMovement.NativeModeKey;
using NativeState = Modstoke.Sons.PlayerClientMovement.NativeState;

namespace Modstoke.Sons
{
    [Flags]
    public enum PlayerMoveButtons : uint
    {
        None = 0,
        Sprint = 1 << 0,
        Crouch = 1 << 1,
        Jump = 1 << 2,
        Use = 1 << 3,
        Primary = 1 << 4,
        Secondary = 1 << 5
    }

    [Flags]
    public enum PlayerMotionFlags : uint
    {
        None = 0,
        Grounded = 1 << 0,
        Crouched = 1 << 1,
        Swimming = 1 << 2,
        Underwater = 1 << 3
    }

    /// <summary>Movement components are in [-1, 1]; pitch/yaw are degrees in [-90, 90]/[-180, 180].</summary>
    public readonly record struct PlayerMotionInput(Guid MigrationId, ulong Generation,
        ulong NetworkId, uint Sequence, Vector2 Movement, float ViewPitch, float ViewYaw,
        PlayerMoveButtons MoveButtons, float DeltaTime)
    {
        /// <summary>Monotonic accepted-jump ordinal; zero retains explicit button-edge producers.</summary>
        public uint JumpSequence { get; init; }
        /// <summary>Input-sequence marker of the latest observed native Run press; repeated packets retain it.</summary>
        public uint RunPressSequence { get; init; }
        /// <summary>Exact committed movement transition; zero is the initial ground mode.</summary>
        public uint MovementTransitionSequence { get; init; }
    }

    /// <summary>
    /// Authoritative state acknowledging a nonzero input sequence, with its own nonzero sequence.
    /// Unrelated to the game's Sons.Player.PlayerState save state and Bolt's PlayerState.
    /// </summary>
    public readonly record struct PlayerMotionState(Guid MigrationId, ulong Generation,
        ulong NetworkId, uint Sequence, uint InputSequence, Vector3 Position,
        Quaternion Rotation, Vector3 Velocity, PlayerMotionFlags Flags)
    {
        public uint MovementTransitionSequence { get; init; }
    }

    /// <summary>A request, never authority to enter an action or occupy an anchor/seat.</summary>
    public readonly record struct PlayerModeTransitionProposal(Guid MigrationId, ulong Generation,
        ulong NetworkId, uint RequestSequence, NativeModeKey Expected, NativeModeKey Requested);

    public enum PlayerModeTransitionDecision : byte { None = 0, Approved = 1, Rejected = 2 }

    /// <summary>The server's immutable decision for the complete, exact proposal.</summary>
    public readonly record struct PlayerModeTransitionApproval(
        PlayerModeTransitionProposal Proposal, PlayerModeTransitionDecision Decision);

    /// <summary>Receipt of that decision, after successful client application when approved.</summary>
    public readonly record struct PlayerModeTransitionAck(PlayerModeTransitionApproval Approval);

    /// <summary>
    /// Managed wire data only; does not enable a bridge or complete migration. Callers must authorize
    /// the peer, identity and sequence before applying input or accepting authoritative state.
    /// </summary>
    public static class PlayerBridgeProtocol
    {
        public const ulong Capability = 1UL << 16;
        public const byte Version = 5;
        public const int InputLength = 80;
        public const int StateLength = 96;
        public const int NativeInputLength = InputLength + 16;
        public const int NativeStateLength = StateLength + 16;
        public const byte ModeTransitionVersion = 1;
        public const int ModeTransitionLength = 88;
        private const uint ModeTransitionMagic = 0x544D504D;
        internal enum ModeTransitionPacketKind : byte { Proposal = 1, Approval = 2, Ack = 3, Receipt = 4 }
        private const uint Magic = 0x504F534D;
        private const byte InputKind = 1;
        private const byte StateKind = 2;
        private const byte NativeInputKind = 3;
        private const byte NativeStateKind = 4;
        private const PlayerMoveButtons AllMoveButtons = PlayerMoveButtons.Sprint
            | PlayerMoveButtons.Crouch | PlayerMoveButtons.Jump
            | PlayerMoveButtons.Use | PlayerMoveButtons.Primary
            | PlayerMoveButtons.Secondary;
        private const PlayerMotionFlags AllStateFlags = PlayerMotionFlags.Grounded
            | PlayerMotionFlags.Crouched | PlayerMotionFlags.Swimming
            | PlayerMotionFlags.Underwater;

        internal static bool IsValidFlags(PlayerMotionFlags flags)
            => (flags & ~AllStateFlags) == 0
                && ((flags & PlayerMotionFlags.Underwater) == 0
                    || (flags & PlayerMotionFlags.Swimming) != 0);

        internal static bool MatchesEnvelope(ReadOnlySpan<byte> source) => source.Length >= 8
            && BinaryPrimitives.ReadUInt32LittleEndian(source) == Magic;

        public static bool TryWriteInput(PlayerMotionInput input, Span<byte> destination, out int written)
            => TryWriteInput(input, destination, InputKind, out written);

        private static bool TryWriteInput(PlayerMotionInput input, Span<byte> destination, byte kind, out int written)
        {
            written = 0;
            if (destination.Length < InputLength || !IsValid(input))
                return false;

            Span<byte> record = destination.Slice(0, InputLength);
            WriteHeader(record, kind, input.MigrationId, input.Generation, input.NetworkId, input.Sequence);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(44, 4), (uint)input.MoveButtons);
            BinaryPrimitives.WriteSingleLittleEndian(record.Slice(48, 4), input.Movement.x);
            BinaryPrimitives.WriteSingleLittleEndian(record.Slice(52, 4), input.Movement.y);
            BinaryPrimitives.WriteSingleLittleEndian(record.Slice(56, 4), input.ViewPitch);
            BinaryPrimitives.WriteSingleLittleEndian(record.Slice(60, 4), input.ViewYaw);
            BinaryPrimitives.WriteSingleLittleEndian(record.Slice(64, 4), input.DeltaTime);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(68, 4), input.JumpSequence);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(72, 4), input.MovementTransitionSequence);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(76, 4), input.RunPressSequence);
            written = InputLength;
            return true;
        }

        public static bool TryReadInput(ReadOnlySpan<byte> source, out PlayerMotionInput input)
            => TryReadInput(source, InputKind, out input);

        private static bool TryReadInput(ReadOnlySpan<byte> source, byte kind, out PlayerMotionInput input)
        {
            input = default;
            if (source.Length != InputLength || !HasValidHeader(source, kind))
                return false;

            var candidate = new PlayerMotionInput(new Guid(source.Slice(8, 16)),
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(24, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(32, 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(40, 4)),
                new Vector2
                {
                    x = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(48, 4)),
                    y = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(52, 4))
                },
                BinaryPrimitives.ReadSingleLittleEndian(source.Slice(56, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(source.Slice(60, 4)),
                (PlayerMoveButtons)BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(44, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(source.Slice(64, 4)))
            {
                JumpSequence = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(68, 4)),
                MovementTransitionSequence = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(72, 4)),
                RunPressSequence = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(76, 4))
            };
            if (!IsValid(candidate))
                return false;

            input = candidate;
            return true;
        }

        public static bool TryWriteState(PlayerMotionState state, Span<byte> destination, out int written)
            => TryWriteState(state, destination, StateKind, out written);

        private static bool TryWriteState(PlayerMotionState state, Span<byte> destination, byte kind, out int written)
        {
            written = 0;
            if (destination.Length < StateLength || !IsValid(state))
                return false;

            Span<byte> record = destination.Slice(0, StateLength);
            WriteHeader(record, kind, state.MigrationId, state.Generation, state.NetworkId, state.Sequence);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(44, 4), state.InputSequence);
            WriteVector3(record.Slice(48, 12), state.Position);
            BinaryPrimitives.WriteSingleLittleEndian(record.Slice(60, 4), state.Rotation.x);
            BinaryPrimitives.WriteSingleLittleEndian(record.Slice(64, 4), state.Rotation.y);
            BinaryPrimitives.WriteSingleLittleEndian(record.Slice(68, 4), state.Rotation.z);
            BinaryPrimitives.WriteSingleLittleEndian(record.Slice(72, 4), state.Rotation.w);
            WriteVector3(record.Slice(76, 12), state.Velocity);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(88, 4), (uint)state.Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(92, 4), state.MovementTransitionSequence);
            written = StateLength;
            return true;
        }

        public static bool TryReadState(ReadOnlySpan<byte> source, out PlayerMotionState state)
            => TryReadState(source, StateKind, out state);

        private static bool TryReadState(ReadOnlySpan<byte> source, byte kind, out PlayerMotionState state)
        {
            state = default;
            if (source.Length != StateLength || !HasValidHeader(source, kind))
                return false;

            var candidate = new PlayerMotionState(new Guid(source.Slice(8, 16)),
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(24, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(32, 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(40, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(44, 4)),
                ReadVector3(source.Slice(48, 12)),
                new Quaternion
                {
                    x = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(60, 4)),
                    y = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(64, 4)),
                    z = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(68, 4)),
                    w = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(72, 4))
                },
                ReadVector3(source.Slice(76, 12)),
                (PlayerMotionFlags)BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(88, 4)))
            { MovementTransitionSequence = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(92, 4)) };
            if (!IsValid(candidate))
                return false;

            state = candidate;
            return true;
        }

        internal static bool MatchesNativeEnvelope(ReadOnlySpan<byte> source)
            => MatchesEnvelope(source) && (source[5] == NativeInputKind || source[5] == NativeStateKind);

        internal static bool MatchesModeTransitionEnvelope(ReadOnlySpan<byte> source)
            => source.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(source) == ModeTransitionMagic;

        public static bool TryWriteModeTransitionProposal(PlayerModeTransitionProposal proposal,
            Span<byte> destination, out int written)
            => TryWriteModeTransition(ModeTransitionPacketKind.Proposal, proposal,
                PlayerModeTransitionDecision.None, destination, out written);

        public static bool TryWriteModeTransitionApproval(PlayerModeTransitionApproval approval,
            Span<byte> destination, out int written)
            => TryWriteModeTransition(ModeTransitionPacketKind.Approval, approval.Proposal,
                approval.Decision, destination, out written);

        public static bool TryWriteModeTransitionAck(PlayerModeTransitionAck ack,
            Span<byte> destination, out int written)
            => TryWriteModeTransition(ModeTransitionPacketKind.Ack, ack.Approval.Proposal,
                ack.Approval.Decision, destination, out written);

        public static bool TryReadModeTransitionProposal(ReadOnlySpan<byte> source,
            out PlayerModeTransitionProposal proposal)
        {
            proposal = default;
            if (!TryReadModeTransition(source, out var kind, out var value, out _)
                || kind != ModeTransitionPacketKind.Proposal) return false;
            proposal = value;
            return true;
        }

        public static bool TryReadModeTransitionApproval(ReadOnlySpan<byte> source,
            out PlayerModeTransitionApproval approval)
        {
            approval = default;
            if (!TryReadModeTransition(source, out var kind, out var proposal, out var decision)
                || kind != ModeTransitionPacketKind.Approval) return false;
            approval = new(proposal, decision);
            return true;
        }

        public static bool TryReadModeTransitionAck(ReadOnlySpan<byte> source,
            out PlayerModeTransitionAck ack)
        {
            ack = default;
            if (!TryReadModeTransition(source, out var kind, out var proposal, out var decision)
                || kind != ModeTransitionPacketKind.Ack) return false;
            ack = new(new(proposal, decision));
            return true;
        }

        internal static bool IsValidMovementTransition(PlayerModeTransitionProposal proposal)
            => IsValidIdentity(proposal.MigrationId, proposal.Generation, proposal.NetworkId, proposal.RequestSequence)
                && IsValidMovementModeKey(proposal.Expected) && IsValidMovementModeKey(proposal.Requested)
                && proposal.Expected.TransitionSequence != uint.MaxValue
                && proposal.Requested.TransitionSequence == proposal.Expected.TransitionSequence + 1
                && (proposal.Expected.Mode == NativeMode.Ground) != (proposal.Requested.Mode == NativeMode.Ground);

        private static bool IsValidModeTransitionDecision(ModeTransitionPacketKind kind,
            PlayerModeTransitionDecision decision)
            => kind == ModeTransitionPacketKind.Proposal ? decision == PlayerModeTransitionDecision.None
                : (kind == ModeTransitionPacketKind.Approval || kind == ModeTransitionPacketKind.Ack
                    || kind == ModeTransitionPacketKind.Receipt)
                    && (decision == PlayerModeTransitionDecision.Approved
                        || decision == PlayerModeTransitionDecision.Rejected);

        internal static bool TryWriteModeTransition(ModeTransitionPacketKind kind,
            PlayerModeTransitionProposal proposal, PlayerModeTransitionDecision decision,
            Span<byte> destination, out int written)
        {
            written = 0;
            if (destination.Length < ModeTransitionLength || !IsValidMovementTransition(proposal)
                || !IsValidModeTransitionDecision(kind, decision)) return false;
            var record = destination.Slice(0, ModeTransitionLength);
            record.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(record, ModeTransitionMagic);
            record[4] = ModeTransitionVersion;
            record[5] = (byte)kind;
            proposal.MigrationId.TryWriteBytes(record.Slice(8, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(24, 8), proposal.Generation);
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(32, 8), proposal.NetworkId);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(40, 4), proposal.RequestSequence);
            record[44] = (byte)decision;
            WriteModeTransitionKey(record.Slice(48, 20), proposal.Expected);
            WriteModeTransitionKey(record.Slice(68, 20), proposal.Requested);
            written = ModeTransitionLength;
            return true;
        }

        internal static bool TryReadModeTransition(ReadOnlySpan<byte> source, out ModeTransitionPacketKind kind,
            out PlayerModeTransitionProposal proposal, out PlayerModeTransitionDecision decision)
        {
            kind = default;
            proposal = default;
            decision = default;
            if (source.Length != ModeTransitionLength || !MatchesModeTransitionEnvelope(source)
                || source[4] != ModeTransitionVersion || source[6] != 0 || source[7] != 0
                || source[45] != 0 || source[46] != 0 || source[47] != 0
                || !TryReadModeTransitionKey(source.Slice(48, 20), out var expected)
                || !TryReadModeTransitionKey(source.Slice(68, 20), out var requested)) return false;
            var packetKind = (ModeTransitionPacketKind)source[5];
            var disposition = (PlayerModeTransitionDecision)source[44];
            var candidate = new PlayerModeTransitionProposal(new Guid(source.Slice(8, 16)),
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(24, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(32, 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(40, 4)), expected, requested);
            if (!IsValidModeTransitionDecision(packetKind, disposition) || !IsValidMovementTransition(candidate)) return false;
            kind = packetKind;
            proposal = candidate;
            decision = disposition;
            return true;
        }

        private static void WriteModeTransitionKey(Span<byte> destination, NativeModeKey key)
        {
            destination[0] = (byte)key.Mode;
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(4, 4), key.TransitionSequence);
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(8, 8), key.AnchorNetworkId);
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(16, 4), key.SeatIndex);
        }

        private static bool TryReadModeTransitionKey(ReadOnlySpan<byte> source, out NativeModeKey key)
        {
            key = default;
            if (source[1] != 0 || source[2] != 0 || source[3] != 0) return false;
            var candidate = new NativeModeKey((NativeMode)source[0],
                BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(4, 4)),
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(8, 8)),
                BinaryPrimitives.ReadInt32LittleEndian(source.Slice(16, 4)));
            if (!IsValidMovementModeKey(candidate)) return false;
            key = candidate;
            return true;
        }

        /// <summary>Ground is a selection key only, never a native-input packet.</summary>
        internal static bool IsValidMovementModeKey(NativeModeKey key)
            => key.Mode == NativeMode.Ground ? key.AnchorNetworkId == 0 && key.SeatIndex == -1
                : key.TransitionSequence != 0 && key.AnchorNetworkId != 0
                    && (key.Mode == NativeMode.HangGlider || IsClimbingMode(key.Mode)
                        || PlayerClientMovement.IsZiplineMode(key.Mode)
                        ? key.SeatIndex == -1 : IsCartMode(key.Mode) && key.SeatIndex >= 0);

        internal static bool IsCartMode(NativeMode mode)
            => mode == NativeMode.GolfCartDriver || mode == NativeMode.GolfCartPassenger;

        internal static bool IsClimbingMode(NativeMode mode)
            => mode == NativeMode.RopeClimb || mode == NativeMode.WallClimb || mode == NativeMode.CraneClimb;

        public static bool TryWriteNativeInput(NativeInput input, Span<byte> destination, out int written)
        {
            written = 0;
            if (destination.Length < NativeInputLength || !IsValid(input)
                || !TryWriteInput(input.Input, destination, NativeInputKind, out _)) return false;
            byte levels = (byte)((input.CartBoost ? 1 : 0) | (input.CartHandBrake ? 2 : 0) | (input.SeatSettled ? 4 : 0));
            WriteNativeKey(destination.Slice(InputLength, 16), input.Key, levels);
            written = NativeInputLength;
            return true;
        }

        public static bool TryReadNativeInput(ReadOnlySpan<byte> source, out NativeInput input)
        {
            input = default;
            if (source.Length != NativeInputLength
                || !TryReadInput(source.Slice(0, InputLength), NativeInputKind, out var motion)
                || !TryReadNativeKey(source.Slice(InputLength, 16), motion.MovementTransitionSequence,
                    out var key, out var levels)) return false;
            var candidate = new NativeInput(key, motion, (levels & 1) != 0, (levels & 2) != 0, (levels & 4) != 0);
            if (!IsValid(candidate)) return false;
            input = candidate;
            return true;
        }

        public static bool TryWriteNativeState(NativeState state, Span<byte> destination, out int written)
        {
            written = 0;
            if (destination.Length < NativeStateLength || !IsValid(state)
                || !TryWriteState(state.Motion, destination, NativeStateKind, out _)) return false;
            WriteNativeKey(destination.Slice(StateLength, 16), state.Key, 0);
            written = NativeStateLength;
            return true;
        }

        public static bool TryReadNativeState(ReadOnlySpan<byte> source, out NativeState state)
        {
            state = default;
            if (source.Length != NativeStateLength
                || !TryReadState(source.Slice(0, StateLength), NativeStateKind, out var motion)
                || !TryReadNativeKey(source.Slice(StateLength, 16), motion.MovementTransitionSequence,
                    out var key, out var levels) || levels != 0) return false;
            var candidate = new NativeState(key, motion);
            if (!IsValid(candidate)) return false;
            state = candidate;
            return true;
        }

        private static bool IsValid(NativeInput input)
            => IsValidMovementModeKey(input.Key) && input.Key.Mode != NativeMode.Ground
                && input.Input.MovementTransitionSequence == input.Key.TransitionSequence && IsValid(input.Input)
                && input.Input.MoveButtons == PlayerMoveButtons.None
                && input.Input.JumpSequence == 0 && input.Input.RunPressSequence == 0
                && (input.Key.Mode == NativeMode.HangGlider
                    ? !input.CartBoost && !input.CartHandBrake && !input.SeatSettled
                        && IsInRange(input.Input.DeltaTime, 0.001f, 0.1f)
                    : input.Input.DeltaTime == 0 && (IsCartMode(input.Key.Mode)
                        || (!input.CartBoost && !input.CartHandBrake && !input.SeatSettled)));

        private static bool IsValid(NativeState state)
            => IsValidMovementModeKey(state.Key) && state.Key.Mode != NativeMode.Ground
                && state.Motion.MovementTransitionSequence == state.Key.TransitionSequence && IsValid(state.Motion)
                && state.Motion.Flags == PlayerMotionFlags.None
                && (!IsCartMode(state.Key.Mode)
                    || (state.Motion.Velocity.x == 0 && state.Motion.Velocity.y == 0 && state.Motion.Velocity.z == 0));

        // Footer: mode:u8, continuous levels:u8, reserved:u16, anchor:u64, seat:i32.
        // The transition sequence is in the common motion record, not a sampled edge.
        private static void WriteNativeKey(Span<byte> destination, NativeModeKey key, byte levels)
        {
            destination.Clear();
            destination[0] = (byte)key.Mode;
            destination[1] = levels;
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(4, 8), key.AnchorNetworkId);
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(12, 4), key.SeatIndex);
        }

        private static bool TryReadNativeKey(ReadOnlySpan<byte> source, uint transition,
            out NativeModeKey key, out byte levels)
        {
            key = default;
            levels = source[1];
            if (source[2] != 0 || source[3] != 0 || (levels & ~7) != 0) return false;
            key = new NativeModeKey((NativeMode)source[0], transition,
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(4, 8)),
                BinaryPrimitives.ReadInt32LittleEndian(source.Slice(12, 4)));
            return IsValidMovementModeKey(key) && key.Mode != NativeMode.Ground;
        }

        private static bool IsValid(PlayerMotionInput input)
        {
            return IsValidIdentity(input.MigrationId, input.Generation, input.NetworkId, input.Sequence)
                && input.JumpSequence <= input.Sequence
                && input.RunPressSequence <= input.Sequence
                && (input.MoveButtons & ~AllMoveButtons) == 0
                && IsInRange(input.Movement.x, -1f, 1f) && IsInRange(input.Movement.y, -1f, 1f)
                && IsInRange(input.ViewPitch, -90f, 90f) && IsInRange(input.ViewYaw, -180f, 180f)
                && IsInRange(input.DeltaTime, 0f, 0.1f);
        }

        private static bool IsValid(PlayerMotionState state)
        {
            return IsValidIdentity(state.MigrationId, state.Generation, state.NetworkId, state.Sequence)
                && state.InputSequence != 0 && IsValidFlags(state.Flags)
                && IsFinite(state.Position) && IsFinite(state.Velocity) && IsCanonicalRotation(state.Rotation);
        }

        private static bool IsValidIdentity(Guid migrationId, ulong generation, ulong networkId, uint sequence)
        {
            return migrationId != Guid.Empty && generation != 0 && networkId != 0 && sequence != 0;
        }

        private static bool IsInRange(float value, float minimum, float maximum)
        {
            return float.IsFinite(value) && value >= minimum && value <= maximum;
        }

        private static bool IsFinite(Vector3 value)
        {
            return float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        }

        private static bool IsCanonicalRotation(Quaternion rotation)
        {
            if (!float.IsFinite(rotation.x) || !float.IsFinite(rotation.y)
                || !float.IsFinite(rotation.z) || !float.IsFinite(rotation.w))
                return false;

            double lengthSquared = (double)rotation.x * rotation.x + (double)rotation.y * rotation.y
                + (double)rotation.z * rotation.z + (double)rotation.w * rotation.w;
            // Match migration's unit-length tolerance without silently normalizing received values.
            if (Math.Abs(lengthSquared - 1.0) > 0.001)
                return false;

            // q and -q encode the same rotation: the first nonzero component in w,x,y,z must be positive.
            return rotation.w != 0 ? rotation.w > 0
                : rotation.x != 0 ? rotation.x > 0
                : rotation.y != 0 ? rotation.y > 0 : rotation.z > 0;
        }

        private static void WriteHeader(Span<byte> record, byte kind, Guid migrationId,
            ulong generation, ulong networkId, uint sequence)
        {
            record.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(record, Magic);
            record[4] = Version;
            record[5] = kind;
            migrationId.TryWriteBytes(record.Slice(8, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(24, 8), generation);
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(32, 8), networkId);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(40, 4), sequence);
        }

        private static bool HasValidHeader(ReadOnlySpan<byte> source, byte kind)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(source) == Magic && source[4] == Version
                && source[5] == kind && source[6] == 0 && source[7] == 0;
        }

        private static Vector3 ReadVector3(ReadOnlySpan<byte> source)
        {
            return new Vector3
            {
                x = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(0, 4)),
                y = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(4, 4)),
                z = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(8, 4))
            };
        }

        private static void WriteVector3(Span<byte> destination, Vector3 value)
        {
            BinaryPrimitives.WriteSingleLittleEndian(destination.Slice(0, 4), value.x);
            BinaryPrimitives.WriteSingleLittleEndian(destination.Slice(4, 4), value.y);
            BinaryPrimitives.WriteSingleLittleEndian(destination.Slice(8, 4), value.z);
        }
    }
}
