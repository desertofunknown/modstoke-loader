// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;

namespace Modstoke.Sons.Combat
{
    /// <summary>Rebreather resources paired with an unchanged version 4 combat receipt.</summary>
    public static class StrictCombatRebreatherProtocol
    {
        public const ulong Capability = 1UL << 8;
        public const ulong RequiredCapabilities = StrictCombatProtocol.RequiredCapabilities | Capability;
        public const byte Version = 1;
        public const int ReceiptLength = 152;
        private const uint Magic = 0x5242534D;

        public static bool TryWriteReceipt(in StrictCombatReceipt receipt,
            StrictCombatOwnedRebreatherState rebreather, Span<byte> destination, out int written)
            => TryWriteReceipt(in receipt, rebreather, destination, false, out written);

        public static bool TryWriteWearReceipt(in StrictCombatReceipt receipt,
            StrictCombatOwnedRebreatherState rebreather, Span<byte> destination, out int written)
            => TryWriteReceipt(in receipt, rebreather, destination, true, out written);

        private static bool TryWriteReceipt(in StrictCombatReceipt receipt,
            StrictCombatOwnedRebreatherState rebreather, Span<byte> destination, bool includeWear, out int written)
        {
            written = 0;
            if (destination.Length < ReceiptLength || rebreather == null
                || (includeWear && rebreather.IsProvisioned && !rebreather.HasWearState)
                || !StrictCombatProtocol.IsValidReceipt(in receipt)
                || (rebreather.IsProvisioned && (receipt.State.LifeGeneration == 0
                    || receipt.State.LifeStage == StrictCombatLifeStage.Uninitialized))) return false;
            Span<byte> record = destination.Slice(0, ReceiptLength);
            record.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(record, Magic);
            record[4] = includeWear ? (byte)2 : Version;
            if (rebreather.IsProvisioned)
            {
                record[5] = 1;
                record[6] = (byte)rebreather.RefillMode;
                record[7] = includeWear && rebreather.IsWorn ? (byte)1 : (byte)0;
                BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(8), rebreather.Revision);
                BinaryPrimitives.WriteInt64LittleEndian(record.Slice(16), BitConverter.DoubleToInt64Bits(rebreather.Capacity));
                BinaryPrimitives.WriteInt64LittleEndian(record.Slice(24), BitConverter.DoubleToInt64Bits(rebreather.Air));
                BinaryPrimitives.WriteInt64LittleEndian(record.Slice(32), BitConverter.DoubleToInt64Bits(rebreather.RemainingRefillAir));
                BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(40), rebreather.LastRefillSequence);
                BinaryPrimitives.WriteInt32LittleEndian(record.Slice(48), rebreather.Canisters);
            }
            if (!StrictCombatProtocol.TryWriteReceipt(in receipt, record.Slice(56), out int receiptWritten)
                || receiptWritten != StrictCombatProtocol.ReceiptLength) return false;
            written = ReceiptLength;
            return true;
        }

        public static bool TryReadReceipt(ReadOnlySpan<byte> source, out StrictCombatReceipt receipt,
            out StrictCombatOwnedRebreatherState rebreather)
            => TryReadReceipt(source, false, out receipt, out rebreather);

        public static bool TryReadWearReceipt(ReadOnlySpan<byte> source, out StrictCombatReceipt receipt,
            out StrictCombatOwnedRebreatherState rebreather)
            => TryReadReceipt(source, true, out receipt, out rebreather);

        private static bool TryReadReceipt(ReadOnlySpan<byte> source, bool includeWear,
            out StrictCombatReceipt receipt, out StrictCombatOwnedRebreatherState rebreather)
        {
            receipt = default;
            rebreather = null;
            if (source.Length != ReceiptLength || BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic
                || source[4] != (includeWear ? (byte)2 : Version) || source[5] > 1 || source[6] > (byte)StrictCombatRebreatherRefillMode.Worn
                || source[7] > (includeWear ? 1 : 0) || BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(52)) != 0
                || !StrictCombatProtocol.TryReadReceipt(source.Slice(56), out var candidateReceipt)) return false;
            StrictCombatOwnedRebreatherState candidate;
            if (source[5] == 0)
            {
                if (source[6] != 0 || source[7] != 0) return false;
                for (int i = 8; i < 52; i++)
                    if (source[i] != 0) return false;
                candidate = StrictCombatOwnedRebreatherState.Unprovisioned;
            }
            else
            {
                if (candidateReceipt.State.LifeGeneration == 0
                    || candidateReceipt.State.LifeStage == StrictCombatLifeStage.Uninitialized) return false;
                ulong revision = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(8));
                double capacity = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(source.Slice(16)));
                double air = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(source.Slice(24)));
                int canisters = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(48));
                var mode = (StrictCombatRebreatherRefillMode)source[6];
                double remaining = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(source.Slice(32)));
                ulong lastSequence = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(40));
                bool created = includeWear
                    ? StrictCombatOwnedRebreatherState.TryCreate(revision, capacity, air, canisters,
                        mode, remaining, lastSequence, source[7] == 1, out candidate)
                    : StrictCombatOwnedRebreatherState.TryCreate(revision, capacity, air, canisters,
                        mode, remaining, lastSequence, out candidate);
                if (!created) return false;
            }
            receipt = candidateReceipt;
            rebreather = candidate;
            return true;
        }
    }
}
