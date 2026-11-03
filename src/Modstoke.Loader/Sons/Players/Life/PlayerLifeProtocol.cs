// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using global::Sons.Player.Stats;

namespace Modstoke.Sons
{
    internal enum PlayerLifeDropMessage : byte { Prepare = 3, Prepared, Execute, Settled, Release, Released }

    // MPLF's forced-drop prelude uses the existing life route and enrolled peer.
    // It is not a numeric life command and carries no substitute custody snapshot.
    internal readonly record struct PlayerLifeDropFrame(PlayerLifeDropMessage Kind, Guid MigrationId,
        ulong Generation, ulong NetworkId, ulong LifeGeneration, ulong Transition,
        PlayerHeldOnlyAuthority.MutationKind DropKind)
    {
        internal const int Length = 64;
        private const uint Magic = 0x464C504D; // MPLF
        internal bool IsValid => Enum.IsDefined(Kind) && MigrationId != Guid.Empty && Generation != 0
            && NetworkId != 0 && LifeGeneration != 0 && Transition != 0
            && PlayerHeldOnlyAuthority.IsForcedDrop(DropKind);
        internal bool SameTransition(in PlayerLifeDropFrame other)
            => (this with { Kind = PlayerLifeDropMessage.Prepare }) == (other with { Kind = PlayerLifeDropMessage.Prepare });
        internal static bool Matches(ReadOnlySpan<byte> bytes)
            => bytes.Length == Length && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == Magic;
        internal void Write(Span<byte> bytes)
        {
            if (bytes.Length != Length || !IsValid) throw new ArgumentException("An exact forced life/water transition is required.");
            bytes.Clear(); BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
            bytes[4] = 3; bytes[5] = (byte)Kind; bytes[6] = (byte)DropKind;
            MigrationId.TryWriteBytes(bytes[8..24]);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], Generation);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], NetworkId);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[40..], LifeGeneration);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[48..], Transition);
        }
        internal static bool TryRead(ReadOnlySpan<byte> bytes, out PlayerLifeDropFrame frame)
        {
            frame = default;
            if (!Matches(bytes) || bytes[4] != 3 || bytes[7] != 0
                || BinaryPrimitives.ReadUInt64LittleEndian(bytes[56..]) != 0) return false;
            frame = new((PlayerLifeDropMessage)bytes[5], new Guid(bytes[8..24]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[48..]),
                (PlayerHeldOnlyAuthority.MutationKind)bytes[6]);
            return frame.IsValid;
        }
    }

    internal enum PlayerLifeMessage : byte { Command = 1, Acknowledgement = 2 }

    internal readonly record struct PlayerLifeValuesFrame(Guid MigrationId, ulong Generation, ulong NetworkId,
        ulong Sequence, ulong IntakeTransaction, PlayerStatValues Values)
    {
        internal const int Length = 256;
        private const uint Magic = 0x564C504D; // MPLV
        internal bool IsValid => MigrationId != Guid.Empty && Generation != 0 && NetworkId != 0
            && Sequence is > 0 and <= uint.MaxValue && Values.IsValid;
        internal bool MatchesCommand(in PlayerLifeFrame command)
            => IsValid && MigrationId == command.MigrationId && Generation == command.Generation
                && NetworkId == command.NetworkId && Sequence == command.Sequence
                && IntakeTransaction == command.IntakeTransaction && Values.Health == command.Health
                && Values.Intake.TargetHealth == command.TargetHealth;
        internal static bool Matches(ReadOnlySpan<byte> bytes)
            => bytes.Length == Length && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == Magic;
        internal void Write(Span<byte> bytes)
        {
            if (bytes.Length != Length || !IsValid) throw new ArgumentException("An exact valid life snapshot is required.");
            bytes.Clear(); BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic); bytes[4] = 1;
            MigrationId.TryWriteBytes(bytes[8..24]);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], Generation);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], NetworkId);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[40..], Sequence);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[48..], IntakeTransaction);
            Values.Write(bytes[56..]);
        }
        internal static bool TryRead(ReadOnlySpan<byte> bytes, out PlayerLifeValuesFrame frame)
        {
            frame = default;
            if (!Matches(bytes) || bytes[4] != 1 || bytes[5] != 0 || bytes[6] != 0 || bytes[7] != 0) return false;
            frame = new(new Guid(bytes[8..24]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[48..]), PlayerStatValues.Read(bytes[56..]));
            return frame.IsValid;
        }
    }

    // Acknowledgements echo the exact command. Numeric snapshots cannot acknowledge
    // callbacks, and a newer command cannot replace an earlier unfinished transition.
    internal readonly record struct PlayerLifeFrame(PlayerLifeMessage Kind, Guid MigrationId,
        ulong Generation, ulong NetworkId, ulong LifeGeneration, ulong Sequence,
        ulong SurvivalRevision, ulong IntakeTransaction, PlayerLifePhase Phase,
        PlayerLifeSourceAction Action, DeathType DeathType, float Health, float TargetHealth, float BurnTime)
    {
        internal const int Length = 96;
        private const uint Magic = 0x464C504D; // MPLF
        internal bool IsValid => Kind is PlayerLifeMessage.Command or PlayerLifeMessage.Acknowledgement
            && MigrationId != Guid.Empty && Generation != 0 && NetworkId != 0 && LifeGeneration != 0
            && Sequence is > 0 and <= uint.MaxValue && float.IsFinite(Health) && Health >= 0
            && float.IsFinite(TargetHealth) && TargetHealth >= 0 && Enum.IsDefined(DeathType)
            && float.IsFinite(BurnTime) && BurnTime >= 0
            && ((Action == PlayerLifeSourceAction.StartDeath && Phase == PlayerLifePhase.Downed)
                || (Action == PlayerLifeSourceAction.ResetFromHeal && Phase == PlayerLifePhase.Reviving
                    && Health > 0 && TargetHealth > 0)
                || (Action == PlayerLifeSourceAction.Respawn && Phase == PlayerLifePhase.Dead));

        internal bool SameCommand(in PlayerLifeFrame other)
            => (this with { Kind = PlayerLifeMessage.Command }) == (other with { Kind = PlayerLifeMessage.Command });

        internal static bool Matches(ReadOnlySpan<byte> bytes)
            => bytes.Length == Length && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == Magic;

        internal void Write(Span<byte> bytes)
        {
            if (bytes.Length != Length || !IsValid) throw new ArgumentException("An exact valid life frame is required.");
            bytes.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
            bytes[4] = 2; bytes[5] = (byte)Kind; bytes[6] = (byte)Phase; bytes[7] = (byte)Action;
            MigrationId.TryWriteBytes(bytes[8..24]);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], Generation);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], NetworkId);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[40..], LifeGeneration);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[48..], Sequence);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[56..], SurvivalRevision);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[64..], IntakeTransaction);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[72..], Health);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[76..], TargetHealth);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[80..], (int)DeathType);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[84..], BurnTime);
        }

        internal static bool TryRead(ReadOnlySpan<byte> bytes, out PlayerLifeFrame frame)
        {
            frame = default;
            if (!Matches(bytes) || bytes[4] != 2 || BinaryPrimitives.ReadUInt64LittleEndian(bytes[88..]) != 0) return false;
            frame = new((PlayerLifeMessage)bytes[5], new Guid(bytes[8..24]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[48..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[56..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[64..]),
                (PlayerLifePhase)bytes[6], (PlayerLifeSourceAction)bytes[7],
                (DeathType)BinaryPrimitives.ReadInt32LittleEndian(bytes[80..]),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[72..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[76..]),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[84..]));
            return frame.IsValid;
        }
    }

    internal enum PlayerLifeBurnMessage : byte { Commit = 1, Acknowledgement = 2 }
    internal enum PlayerLifeBurnCause : byte { Expired = 1, DeathPrompt = 2 }

    // A server commit and its source callback receipt. No acknowledgement changes
    // the server clock, and no latest-state snapshot may substitute for this edge.
    internal readonly record struct PlayerLifeBurnFrame(PlayerLifeBurnMessage Kind, Guid MigrationId,
        ulong Generation, ulong NetworkId, ulong LifeGeneration, ulong Sequence, ulong SurvivalRevision,
        uint StatSequenceFloor, float Elapsed, float BurnTimeBefore, PlayerLifeBurnCause Cause)
    {
        internal const int Length = 96;
        private const uint Magic = 0x424C504D; // MPLB
        internal bool IsValid => Kind is PlayerLifeBurnMessage.Commit or PlayerLifeBurnMessage.Acknowledgement
            && MigrationId != Guid.Empty && Generation != 0 && NetworkId != 0 && LifeGeneration != 0
            && Sequence is > 0 and <= uint.MaxValue && SurvivalRevision != 0 && StatSequenceFloor < uint.MaxValue
            && float.IsFinite(Elapsed) && Elapsed >= 0 && float.IsFinite(BurnTimeBefore)
            && ((Cause == PlayerLifeBurnCause.Expired && BurnTimeBefore <= 0)
                || (Cause == PlayerLifeBurnCause.DeathPrompt && BurnTimeBefore > 0));
        internal bool MatchesCommand(in PlayerLifeFrame command) => IsValid
            && command.Action == PlayerLifeSourceAction.StartDeath
            && MigrationId == command.MigrationId && Generation == command.Generation && NetworkId == command.NetworkId
            && LifeGeneration == command.LifeGeneration && Sequence == command.Sequence
            && SurvivalRevision > command.SurvivalRevision;
        internal bool SameCommit(in PlayerLifeBurnFrame other)
            => (this with { Kind = PlayerLifeBurnMessage.Commit }) == (other with { Kind = PlayerLifeBurnMessage.Commit });
        internal static bool Matches(ReadOnlySpan<byte> bytes)
            => bytes.Length == Length && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == Magic;
        internal void Write(Span<byte> bytes)
        {
            if (bytes.Length != Length || !IsValid) throw new ArgumentException("An exact burning receipt is required.");
            bytes.Clear(); BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
            bytes[4] = 1; bytes[5] = (byte)Kind; bytes[6] = (byte)Cause;
            MigrationId.TryWriteBytes(bytes[8..24]);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], Generation);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], NetworkId);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[40..], LifeGeneration);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[48..], Sequence);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[56..], SurvivalRevision);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[64..], StatSequenceFloor);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[68..], Elapsed);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[72..], BurnTimeBefore);
        }
        internal static bool TryRead(ReadOnlySpan<byte> bytes, out PlayerLifeBurnFrame frame)
        {
            frame = default;
            if (!Matches(bytes) || bytes[4] != 1 || bytes[7] != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes[76..]) != 0
                || BinaryPrimitives.ReadUInt64LittleEndian(bytes[80..]) != 0
                || BinaryPrimitives.ReadUInt64LittleEndian(bytes[88..]) != 0) return false;
            frame = new((PlayerLifeBurnMessage)bytes[5], new Guid(bytes[8..24]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[48..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[56..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[64..]),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[68..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[72..]),
                (PlayerLifeBurnCause)bytes[6]);
            return frame.IsValid;
        }
    }
}
