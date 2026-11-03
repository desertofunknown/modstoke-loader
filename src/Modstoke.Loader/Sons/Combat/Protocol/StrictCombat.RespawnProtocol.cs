// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Modstoke.Sons.Combat
{
    /// <summary>Correlated respawn requests, placement offers, client claims, and server outcomes.</summary>
    public static class StrictCombatRespawnProtocol
    {
        public const ulong Capability = 1UL << 4;
        public const ulong RequiredCapabilities = StrictCombatProtocol.RequiredCapabilities | Capability;
        private const uint Magic = 0x5253534D;
        public const byte Version = 1;
        private const byte RequestKind = 1;
        private const byte OfferKind = 2;
        private const byte ReportKind = 3;
        private const byte OutcomeKind = 4;
        public const int RequestLength = 72;
        public const int OfferLength = 120;
        public const int ReportLength = 96;
        public const int OutcomeLength = 152;
        public const uint MaximumLifetimeMs = 15000;
        public const uint RetryIntervalMs = 500;
        public const int MaximumAttempts = 8;

        public static bool TryWriteRequest(in StrictCombatRespawnRequest value, Span<byte> destination, out int written)
        {
            written = 0;
            if (destination.Length < RequestLength || !value.IsValid)
                return false;
            WriteCommon(destination.Slice(0, RequestLength), RequestKind, value);
            written = RequestLength;
            return true;
        }

        public static bool TryReadRequest(ReadOnlySpan<byte> source, out StrictCombatRespawnRequest value)
            => TryReadCommon(source, RequestLength, RequestKind, out value);

        public static bool TryWriteOffer(in StrictCombatRespawnOffer value, Span<byte> destination, out int written)
        {
            written = 0;
            if (destination.Length < OfferLength || !value.IsValid)
                return false;
            Span<byte> record = destination.Slice(0, OfferLength);
            WriteCommon(record, OfferKind, value.Request);
            value.OperationId.TryWriteBytes(record.Slice(72, 16));
            WriteFloat(record, 88, value.Position.X);
            WriteFloat(record, 92, value.Position.Y);
            WriteFloat(record, 96, value.Position.Z);
            WriteFloat(record, 100, value.Rotation.X);
            WriteFloat(record, 104, value.Rotation.Y);
            WriteFloat(record, 108, value.Rotation.Z);
            WriteFloat(record, 112, value.Rotation.W);
            written = OfferLength;
            return true;
        }

        public static bool TryReadOffer(ReadOnlySpan<byte> source, out StrictCombatRespawnOffer value)
        {
            value = default;
            if (!TryReadCommon(source, OfferLength, OfferKind, out StrictCombatRespawnRequest request)
                || !IsZero(source.Slice(116, 4)))
                return false;
            var candidate = new StrictCombatRespawnOffer(request, new Guid(source.Slice(72, 16)),
                new Vector3(ReadFloat(source, 88), ReadFloat(source, 92), ReadFloat(source, 96)),
                new Quaternion(ReadFloat(source, 100), ReadFloat(source, 104), ReadFloat(source, 108), ReadFloat(source, 112)));
            if (!candidate.IsValid)
                return false;
            value = candidate;
            return true;
        }

        public static bool TryWriteReport(in StrictCombatRespawnReport value, Span<byte> destination, out int written)
        {
            written = 0;
            if (destination.Length < ReportLength || !value.IsValid)
                return false;
            Span<byte> record = destination.Slice(0, ReportLength);
            WriteCommon(record, ReportKind, value.Request);
            value.OperationId.TryWriteBytes(record.Slice(72, 16));
            record[88] = (byte)value.Placement;
            written = ReportLength;
            return true;
        }

        public static bool TryReadReport(ReadOnlySpan<byte> source, out StrictCombatRespawnReport value)
        {
            value = default;
            if (!TryReadCommon(source, ReportLength, ReportKind, out StrictCombatRespawnRequest request)
                || !IsZero(source.Slice(89, 7)))
                return false;
            var candidate = new StrictCombatRespawnReport(request, new Guid(source.Slice(72, 16)),
                (StrictCombatRespawnPlacement)source[88]);
            if (!candidate.IsValid)
                return false;
            value = candidate;
            return true;
        }

        public static bool TryWriteOutcome(in StrictCombatRespawnOutcome value, Span<byte> destination, out int written)
        {
            written = 0;
            if (destination.Length < OutcomeLength || !value.IsValid)
                return false;
            Span<byte> record = destination.Slice(0, OutcomeLength);
            WriteCommon(record, OutcomeKind, value.Request);
            value.OperationId.TryWriteBytes(record.Slice(72, 16));
            record[88] = (byte)value.Result;
            StrictCombatSnapshot state = value.State;
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(96), state.Revision);
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(104), state.LifeGeneration);
            WriteFloat(record, 112, state.Health);
            WriteFloat(record, 116, state.MaximumHealth);
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(120), state.Magazine);
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(124), state.Reserve);
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(128), state.ReloadRounds);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(132), state.ReloadRemainingMs);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(136), state.FireRemainingMs);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(140), state.DownedRemainingMs);
            record[144] = (byte)state.LifeStage;
            record[145] = (byte)((state.OwnsPistol ? 1 : 0) | (state.Equipped ? 2 : 0));
            written = OutcomeLength;
            return true;
        }

        public static bool TryReadOutcome(ReadOnlySpan<byte> source, out StrictCombatRespawnOutcome value)
        {
            value = default;
            if (!TryReadCommon(source, OutcomeLength, OutcomeKind, out StrictCombatRespawnRequest request)
                || !IsZero(source.Slice(89, 7)) || !IsZero(source.Slice(146, 6)) || (source[145] & ~3) != 0)
                return false;
            byte flags = source[145];
            var state = new StrictCombatSnapshot(
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(96)), (flags & 1) != 0, (flags & 2) != 0,
                BinaryPrimitives.ReadInt32LittleEndian(source.Slice(120)),
                BinaryPrimitives.ReadInt32LittleEndian(source.Slice(124)),
                BinaryPrimitives.ReadInt32LittleEndian(source.Slice(128)),
                BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(132)),
                BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(136)),
                (StrictCombatLifeStage)source[144], BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(104)),
                ReadFloat(source, 112), ReadFloat(source, 116), BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(140)));
            var candidate = new StrictCombatRespawnOutcome(request, new Guid(source.Slice(72, 16)),
                (StrictCombatRespawnResult)source[88], state);
            if (!candidate.IsValid)
                return false;
            value = candidate;
            return true;
        }

        private static void WriteCommon(Span<byte> record, byte kind, in StrictCombatRespawnRequest request)
        {
            record.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(record, Magic);
            record[4] = Version;
            record[5] = kind;
            request.Binding.Epoch.TryWriteBytes(record.Slice(8, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(24), request.Binding.Generation);
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(32), request.Binding.ActorNetworkId);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(40), request.Binding.ReloadDurationMs);
            request.RequestId.TryWriteBytes(record.Slice(48, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(64), request.LifeGeneration);
        }

        private static bool TryReadCommon(ReadOnlySpan<byte> source, int length, byte kind,
            out StrictCombatRespawnRequest request)
        {
            request = default;
            if (source.Length != length || BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic
                || source[4] != Version || source[5] != kind || !IsZero(source.Slice(6, 2))
                || !IsZero(source.Slice(44, 4)))
                return false;
            var binding = new StrictCombatBinding(new Guid(source.Slice(8, 16)),
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(24)),
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(32)),
                BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(40)));
            var candidate = new StrictCombatRespawnRequest(binding, new Guid(source.Slice(48, 16)),
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(64)));
            if (!candidate.IsValid)
                return false;
            request = candidate;
            return true;
        }

        private static bool IsZero(ReadOnlySpan<byte> bytes)
        {
            foreach (byte value in bytes)
                if (value != 0)
                    return false;
            return true;
        }

        private static void WriteFloat(Span<byte> record, int offset, float value)
            => BinaryPrimitives.WriteInt32LittleEndian(record.Slice(offset), BitConverter.SingleToInt32Bits(value));

        private static float ReadFloat(ReadOnlySpan<byte> record, int offset)
            => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(record.Slice(offset)));
    }
}
