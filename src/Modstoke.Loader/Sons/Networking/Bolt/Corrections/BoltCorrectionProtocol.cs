// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;

namespace Modstoke.Sons
{
    /// <summary>A caller-authorized property binding that must be fresh for each life.</summary>
    public readonly record struct BoltCorrectionBinding(Guid Id, ulong Generation,
        ulong EntityNetworkId, int StateTypeId, int PropertyIndex, BoltScalarKind Kind)
    {
        public bool IsValid => Id != Guid.Empty && Generation > 0 && EntityNetworkId != 0
            && StateTypeId >= 0 && PropertyIndex >= 0 && PropertyIndex <= 1023
            && (Kind == BoltScalarKind.Boolean || Kind == BoltScalarKind.Int32
                || Kind == BoltScalarKind.Single);
    }

    public readonly record struct BoltCorrection(BoltCorrectionBinding Binding,
        ulong Revision, BoltScalar Value)
    {
        public bool IsValid => Binding.IsValid && Revision > 0 && Value.Kind == Binding.Kind
            && Value.IsValidServerValue && (Value.Kind != BoltScalarKind.Boolean || Value.Bits <= 1);
    }

    public enum BoltCorrectionApplyResult
    {
        None = 0,
        Applied = 1,
        Unchanged = 2,
        Rejected = 3,
        Unconfirmed = 4
    }

    /// <summary>Copied data only, with no native effects. Receipts are not proof of client trust.</summary>
    public static class BoltCorrectionProtocol
    {
        public const ulong Capability = 1UL << 14;
        public const byte Version = 1;
        public const int MessageLength = 64;
        private const uint Magic = 0x4350534D;
        private const byte BindingKind = 1;
        private const byte CorrectionKind = 2;
        private const byte ReceiptKind = 3;

        public static bool TryWriteBinding(in BoltCorrectionBinding value,
            Span<byte> destination, out int written)
        {
            written = 0;
            if (destination.Length < MessageLength || !value.IsValid)
                return false;

            WriteBinding(destination.Slice(0, MessageLength), BindingKind, in value);
            written = MessageLength;
            return true;
        }

        public static bool TryReadBinding(ReadOnlySpan<byte> source, out BoltCorrectionBinding value)
        {
            value = default;
            if (!TryReadHeader(source, BindingKind, out var binding) || source[7] != 0
                || BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(48)) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(56)) != 0)
                return false;

            value = binding;
            return true;
        }

        public static bool TryWriteCorrection(in BoltCorrection value,
            Span<byte> destination, out int written)
        {
            written = 0;
            if (destination.Length < MessageLength || !value.IsValid)
                return false;

            WriteCorrection(destination.Slice(0, MessageLength), CorrectionKind, in value);
            written = MessageLength;
            return true;
        }

        public static bool TryReadCorrection(ReadOnlySpan<byte> source, out BoltCorrection value)
        {
            value = default;
            if (!TryReadHeader(source, CorrectionKind, out var binding) || source[7] != 0)
                return false;

            return TryReadValue(source, in binding, out value);
        }

        public static bool TryWriteReceipt(in BoltCorrection value,
            BoltCorrectionApplyResult result, Span<byte> destination, out int written)
        {
            written = 0;
            if (destination.Length < MessageLength || !value.IsValid || !IsValidResult(result))
                return false;

            Span<byte> record = destination.Slice(0, MessageLength);
            WriteCorrection(record, ReceiptKind, in value);
            record[7] = (byte)result;
            written = MessageLength;
            return true;
        }

        public static bool TryReadReceipt(ReadOnlySpan<byte> source, out BoltCorrection value,
            out BoltCorrectionApplyResult result)
        {
            value = default;
            result = BoltCorrectionApplyResult.None;
            if (!TryReadHeader(source, ReceiptKind, out var binding))
                return false;

            var candidateResult = (BoltCorrectionApplyResult)source[7];
            if (!IsValidResult(candidateResult) || !TryReadValue(source, in binding, out value))
                return false;

            result = candidateResult;
            return true;
        }

        private static void WriteBinding(Span<byte> record, byte kind, in BoltCorrectionBinding value)
        {
            record.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(record, Magic);
            record[4] = Version;
            record[5] = kind;
            record[6] = (byte)value.Kind;
            value.Id.TryWriteBytes(record.Slice(8, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(24), value.EntityNetworkId);
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(32), value.StateTypeId);
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(36), value.PropertyIndex);
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(40), value.Generation);
        }

        private static void WriteCorrection(Span<byte> record, byte kind, in BoltCorrection value)
        {
            BoltCorrectionBinding binding = value.Binding;
            WriteBinding(record, kind, in binding);
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(48), value.Revision);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(56), value.Value.Bits);
        }

        private static bool TryReadHeader(ReadOnlySpan<byte> source, byte kind,
            out BoltCorrectionBinding binding)
        {
            binding = default;
            if (source.Length != MessageLength || BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic
                || source[4] != Version || source[5] != kind
                || BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(60)) != 0)
                return false;

            var candidate = new BoltCorrectionBinding(new Guid(source.Slice(8, 16)),
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(40)),
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(24)),
                BinaryPrimitives.ReadInt32LittleEndian(source.Slice(32)),
                BinaryPrimitives.ReadInt32LittleEndian(source.Slice(36)),
                (BoltScalarKind)source[6]);
            if (!candidate.IsValid)
                return false;

            binding = candidate;
            return true;
        }

        private static bool TryReadValue(ReadOnlySpan<byte> source, in BoltCorrectionBinding binding,
            out BoltCorrection value)
        {
            value = default;
            var candidate = new BoltCorrection(binding,
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(48)),
                new BoltScalar(binding.Kind, BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(56))));
            if (!candidate.IsValid)
                return false;

            value = candidate;
            return true;
        }

        private static bool IsValidResult(BoltCorrectionApplyResult result)
        {
            return result == BoltCorrectionApplyResult.Applied
                || result == BoltCorrectionApplyResult.Unchanged
                || result == BoltCorrectionApplyResult.Rejected
                || result == BoltCorrectionApplyResult.Unconfirmed;
        }
    }
}
