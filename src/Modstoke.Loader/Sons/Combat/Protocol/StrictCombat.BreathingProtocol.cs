// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;

namespace Modstoke.Sons.Combat
{
    /// <summary>Breathing resources paired with an unchanged version 4 combat receipt.</summary>
    public static class StrictCombatBreathingProtocol
    {
        public const ulong Capability = 1UL << 12;
        public const ulong RequiredCapabilities = StrictCombatProtocol.RequiredCapabilities | Capability;
        public const byte Version = 1;
        public const int ReceiptLength = 128;
        private const uint Magic = 0x4C42534D;

        public static bool TryWriteReceipt(in StrictCombatReceipt receipt,
            StrictCombatOwnedBreathingState breathing, Span<byte> destination, out int written)
        {
            written = 0;
            if (destination.Length < ReceiptLength || breathing == null
                || !StrictCombatProtocol.IsValidReceipt(in receipt)
                || (breathing.IsProvisioned && (receipt.State.LifeGeneration == 0
                    || receipt.State.LifeStage == StrictCombatLifeStage.Uninitialized))) return false;
            Span<byte> record = destination.Slice(0, ReceiptLength);
            record.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(record, Magic);
            record[4] = Version;
            if (breathing.IsProvisioned)
            {
                record[5] = 1;
                BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(8), breathing.Revision);
                BinaryPrimitives.WriteInt64LittleEndian(record.Slice(16), BitConverter.DoubleToInt64Bits(breathing.Capacity));
                BinaryPrimitives.WriteInt64LittleEndian(record.Slice(24), BitConverter.DoubleToInt64Bits(breathing.Air));
            }
            if (!StrictCombatProtocol.TryWriteReceipt(in receipt, record.Slice(32), out int receiptWritten)
                || receiptWritten != StrictCombatProtocol.ReceiptLength) return false;
            written = ReceiptLength;
            return true;
        }

        public static bool TryReadReceipt(ReadOnlySpan<byte> source, out StrictCombatReceipt receipt,
            out StrictCombatOwnedBreathingState breathing)
        {
            receipt = default;
            breathing = null;
            if (source.Length != ReceiptLength || BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic
                || source[4] != Version || source[5] > 1 || source[6] != 0 || source[7] != 0
                || !StrictCombatProtocol.TryReadReceipt(source.Slice(32), out var candidateReceipt)) return false;
            StrictCombatOwnedBreathingState candidate;
            if (source[5] == 0)
            {
                for (int i = 8; i < 32; i++)
                    if (source[i] != 0) return false;
                candidate = StrictCombatOwnedBreathingState.Unprovisioned;
            }
            else
            {
                if (candidateReceipt.State.LifeGeneration == 0
                    || candidateReceipt.State.LifeStage == StrictCombatLifeStage.Uninitialized
                    || !StrictCombatOwnedBreathingState.TryCreate(
                        BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(8)),
                        BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(source.Slice(16))),
                        BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(source.Slice(24))),
                        out candidate)) return false;
            }
            receipt = candidateReceipt;
            breathing = candidate;
            return true;
        }
    }
}
