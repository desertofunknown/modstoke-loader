// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Modstoke.Sons
{
    internal enum PlayerCraftMessage : byte { Request = 1, Reserved, Applied, Rejected, Unconfirmed, Settled, Ready }

    internal readonly record struct PlayerCraftFrame(Guid MigrationId, ulong Generation, ulong NetworkId,
        string BaselineFingerprint, ulong Request, int RecipeIndex, int SelectionCount, string SelectionFingerprint,
        PlayerCraftMessage Kind, ulong FirstTransaction = 0, ulong LastTransaction = 0,
        int Offset = 0, CraftIngredient[] Rows = null, string RecipeFingerprint = null)
    {
        internal bool SameRequest(in PlayerCraftFrame other) => MigrationId == other.MigrationId
            && Generation == other.Generation && NetworkId == other.NetworkId && Request == other.Request
            && BaselineFingerprint == other.BaselineFingerprint && RecipeIndex == other.RecipeIndex
            && SelectionCount == other.SelectionCount && SelectionFingerprint == other.SelectionFingerprint
            && RecipeFingerprint == other.RecipeFingerprint;
    }

    internal static class PlayerCraftWire
    {
        internal const int Length = 256, RowsPerPacket = 7, MaximumSelections = 256;
        private const uint Magic = 0x4643504D; // MPCF

        internal static bool IsFrame(ReadOnlySpan<byte> bytes)
            => bytes.Length == Length && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == Magic;

        internal static void Write(in PlayerCraftFrame frame, Span<byte> bytes)
        {
            bytes.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
            bytes[4] = 3;
            bytes[5] = (byte)frame.Kind;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(6), checked((ushort)frame.Offset));
            frame.MigrationId.TryWriteBytes(bytes.Slice(8, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(24), frame.Generation);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(32), frame.NetworkId);
            Convert.FromHexString(frame.BaselineFingerprint).CopyTo(bytes.Slice(40, 32));
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(72), frame.Request);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(80), frame.RecipeIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(84), checked((ushort)frame.SelectionCount));
            int count = frame.Rows?.Length ?? 0;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(86), checked((ushort)count));
            Convert.FromHexString(frame.SelectionFingerprint).CopyTo(bytes.Slice(88, 32));
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(120), frame.FirstTransaction);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(128), frame.LastTransaction);
            Convert.FromHexString(frame.RecipeFingerprint).CopyTo(bytes.Slice(136, 32));
            for (int i = 0; i < count; i++) WriteRow(frame.Rows[i], bytes.Slice(168 + i * 12, 12));
        }

        internal static bool TryRead(ReadOnlySpan<byte> bytes, out PlayerCraftFrame frame)
        {
            frame = default;
            if (!IsFrame(bytes) || bytes[4] != 3 || bytes[5] < (byte)PlayerCraftMessage.Request
                || bytes[5] > (byte)PlayerCraftMessage.Ready) return false;
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(6));
            int total = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(84));
            int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(86));
            var kind = (PlayerCraftMessage)bytes[5];
            ulong first = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(120));
            ulong last = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(128));
            if (total is <= 0 or > MaximumSelections || count > RowsPerPacket) return false;
            if (kind == PlayerCraftMessage.Request)
            {
                if (first != 0 || last != 0 || count <= 0 || offset % RowsPerPacket != 0
                    || offset >= total || count != System.Math.Min(RowsPerPacket, total - offset)) return false;
            }
            else if (offset != 0 || count != 0) return false;
            else if (kind == PlayerCraftMessage.Rejected)
            {
                if (first != 0 || last != 0) return false;
            }
            else if (first < 2 || last < first || last - first >= MaximumSelections + InventoryCraftRecipe.MaximumRows)
                return false;
            for (int i = 168 + count * 12; i < Length; i++) if (bytes[i] != 0) return false;
            var rows = count == 0 ? null : new CraftIngredient[count];
            for (int i = 0; i < count; i++)
            {
                var row = bytes.Slice(168 + i * 12, 12);
                rows[i] = new(BinaryPrimitives.ReadUInt64LittleEndian(row), BinaryPrimitives.ReadInt32LittleEndian(row.Slice(8)));
                if (rows[i].InstanceKey == 0 || rows[i].Quantity <= 0) return false;
            }
            frame = new(new Guid(bytes.Slice(8, 16)), BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(24)),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(32)), Convert.ToHexString(bytes.Slice(40, 32)),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(72)), BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(80)),
                total, Convert.ToHexString(bytes.Slice(88, 32)), kind, first, last, offset, rows,
                Convert.ToHexString(bytes.Slice(136, 32)));
            return frame.MigrationId != Guid.Empty && frame.Generation != 0 && frame.NetworkId != 0
                && frame.Request != 0 && frame.RecipeIndex is >= 0 and < 4096;
        }

        internal static string HashSelections(CraftIngredient[] rows)
        {
            Span<byte> bytes = stackalloc byte[rows.Length * 12];
            for (int i = 0; i < rows.Length; i++) WriteRow(rows[i], bytes.Slice(i * 12, 12));
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(bytes, hash);
            return Convert.ToHexString(hash);
        }

        private static void WriteRow(CraftIngredient row, Span<byte> bytes)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, row.InstanceKey);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(8), row.Quantity);
        }
    }
}
