// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;

namespace Modstoke.Sons.Combat
{
    /// <summary>Explicitly negotiated owned-armour pages containing unchanged version 4 receipts.</summary>
    public static class StrictCombatArmourProtocol
    {
        public const ulong Capability = 1UL << 5;
        public const ulong RequiredCapabilities = StrictCombatProtocol.RequiredCapabilities | Capability;
        public const byte Version = 1;
        public const int MaximumPieces = 12;
        public const int PiecesPerPage = 6;
        public const int MaximumPageLength = 240;
        private const uint Magic = 0x4153534D;
        private const int HeaderLength = 120;
        private const int PieceLength = 20;

        public static bool TryWriteReceiptPage(in StrictCombatReceipt receipt, StrictCombatOwnedArmourState armour,
            int pageIndex, Span<byte> destination, out int written)
        {
            written = 0;
            if (armour == null || !StrictCombatProtocol.IsValidReceipt(in receipt)
                || !IsValidEnvelope(in receipt, armour.IsProvisioned, armour.Revision, armour.Golden,
                    armour.GeneralMultiplier, armour.DemonicMultiplier, armour.Count)
                || (uint)pageIndex >= (uint)PageCount(armour.Count)) return false;

            int pieceCount = Math.Min(PiecesPerPage, armour.Count - pageIndex * PiecesPerPage);
            int length = HeaderLength + pieceCount * PieceLength;
            if (destination.Length < length) return false;
            Span<byte> record = destination.Slice(0, length);
            record.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(record, Magic);
            record[4] = Version;
            record[5] = (byte)pageIndex;
            record[6] = (byte)armour.Count;
            record[7] = (byte)((armour.IsProvisioned ? 1 : 0) | (armour.Golden ? 2 : 0));
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(8), armour.Revision);
            WriteFloat(record.Slice(16), armour.GeneralMultiplier);
            WriteFloat(record.Slice(20), armour.DemonicMultiplier);
            if (!StrictCombatProtocol.TryWriteReceipt(in receipt, record.Slice(24, StrictCombatProtocol.ReceiptLength), out _))
                return false;
            for (int i = 0; i < pieceCount; i++)
            {
                armour.TryGetPiece(pageIndex * PiecesPerPage + i, out StrictCombatArmourResource piece);
                Span<byte> target = record.Slice(HeaderLength + i * PieceLength, PieceLength);
                target[0] = (byte)piece.Slot;
                target[1] = (byte)(piece.IsBroken ? 1 : 0);
                BinaryPrimitives.WriteInt32LittleEndian(target.Slice(4), piece.ItemId);
                BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(8), piece.ResourceId);
                WriteFloat(target.Slice(16), piece.RemainingPoints);
            }
            written = length;
            return true;
        }

        public static bool TryReadReceiptPage(ReadOnlySpan<byte> source, out StrictCombatArmourReceiptPage page)
        {
            page = null;
            if (source.Length < HeaderLength || source.Length > MaximumPageLength
                || BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic || source[4] != Version
                || source[6] > MaximumPieces || (source[7] & ~3) != 0) return false;
            int pageIndex = source[5], totalCount = source[6];
            if (pageIndex >= PageCount(totalCount)) return false;
            int pieceCount = Math.Min(PiecesPerPage, totalCount - pageIndex * PiecesPerPage);
            if (source.Length != HeaderLength + pieceCount * PieceLength
                || !StrictCombatProtocol.TryReadReceipt(source.Slice(24, StrictCombatProtocol.ReceiptLength), out var receipt))
                return false;
            ulong revision = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(8));
            bool isProvisioned = (source[7] & 1) != 0, golden = (source[7] & 2) != 0;
            float generalMultiplier = ReadFloat(source.Slice(16)), demonicMultiplier = ReadFloat(source.Slice(20));
            if (!IsValidEnvelope(in receipt, isProvisioned, revision, golden, generalMultiplier, demonicMultiplier, totalCount))
                return false;
            var pieces = new StrictCombatArmourResource[pieceCount];
            for (int i = 0; i < pieceCount; i++)
            {
                ReadOnlySpan<byte> item = source.Slice(HeaderLength + i * PieceLength, PieceLength);
                if (item[1] > 1 || item[2] != 0 || item[3] != 0) return false;
                pieces[i] = new StrictCombatArmourResource(item[0], BinaryPrimitives.ReadUInt64LittleEndian(item.Slice(8)),
                    BinaryPrimitives.ReadInt32LittleEndian(item.Slice(4)), ReadFloat(item.Slice(16)), item[1] != 0);
            }
            if (!StrictCombatOwnedArmourState.AreValidPieces(pieces)) return false;
            page = new StrictCombatArmourReceiptPage(receipt, revision, isProvisioned, golden,
                generalMultiplier, demonicMultiplier, totalCount, pageIndex, pieces);
            return true;
        }

        public static bool SameReceipt(in StrictCombatReceipt first, in StrictCombatReceipt second)
        {
            Span<byte> firstBytes = stackalloc byte[StrictCombatProtocol.ReceiptLength];
            Span<byte> secondBytes = stackalloc byte[StrictCombatProtocol.ReceiptLength];
            return StrictCombatProtocol.TryWriteReceipt(in first, firstBytes, out _)
                && StrictCombatProtocol.TryWriteReceipt(in second, secondBytes, out _)
                && firstBytes.SequenceEqual(secondBytes);
        }

        private static bool IsValidEnvelope(in StrictCombatReceipt receipt, bool isProvisioned, ulong revision,
            bool golden, float generalMultiplier, float demonicMultiplier, int totalCount)
        {
            if (!isProvisioned)
                return revision == 0 && !golden && totalCount == 0
                    && BitConverter.SingleToInt32Bits(generalMultiplier) == 0
                    && BitConverter.SingleToInt32Bits(demonicMultiplier) == 0;
            return receipt.State.LifeGeneration != 0 && receipt.State.LifeStage != StrictCombatLifeStage.Uninitialized
                && StrictCombatOwnedArmourState.IsValidMultiplier(generalMultiplier)
                && StrictCombatOwnedArmourState.IsValidMultiplier(demonicMultiplier);
        }

        private static int PageCount(int count) => Math.Max(1, (count + PiecesPerPage - 1) / PiecesPerPage);
        private static float ReadFloat(ReadOnlySpan<byte> source)
            => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source));
        private static void WriteFloat(Span<byte> destination, float value)
            => BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(value));
    }
}
