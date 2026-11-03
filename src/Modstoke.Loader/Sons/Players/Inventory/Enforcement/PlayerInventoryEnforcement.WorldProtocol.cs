// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using WorldAction = Modstoke.Sons.InventoryStorage.TransferKind;

namespace Modstoke.Sons
{
    internal enum PlayerWorldMessage : byte { Request = 1, Reserved, Applied, Rejected, Unconfirmed, Settled, Closed }

    internal readonly record struct PlayerWorldIntent(WorldAction Action, ulong World, ulong Key,
        int ItemId, int BeforeQuantity, int Slot, string ModuleFingerprint)
    {
        internal bool IsGrant => Action is WorldAction.Pickup or WorldAction.Withdrawal;
    }

    internal readonly record struct PlayerWorldFrame(Guid MigrationId, ulong Generation, ulong NetworkId,
        string Fingerprint, ulong Request, PlayerWorldIntent Intent, PlayerWorldMessage Kind,
        ulong Transaction = 0, ulong ResultWorld = 0)
    {
        internal bool SameRequest(in PlayerWorldFrame other) => MigrationId == other.MigrationId
            && Generation == other.Generation && NetworkId == other.NetworkId && Fingerprint == other.Fingerprint
            && Request == other.Request && Intent == other.Intent;
    }

    internal static class PlayerWorldWire
    {
        internal const int Length = 160;
        private const uint Magic = 0x5749504D; // MPIW

        internal static bool IsFrame(ReadOnlySpan<byte> bytes)
            => bytes.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == Magic;

        internal static void Write(in PlayerWorldFrame frame, Span<byte> bytes)
        {
            bytes.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
            bytes[4] = 1;
            bytes[5] = (byte)frame.Kind;
            bytes[6] = (byte)frame.Intent.Action;
            frame.MigrationId.TryWriteBytes(bytes.Slice(8, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(24), frame.Generation);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(32), frame.NetworkId);
            Convert.FromHexString(frame.Fingerprint).CopyTo(bytes.Slice(40, 32));
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(72), frame.Request);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(80), frame.Intent.World);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(88), frame.Intent.Key);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(96), frame.Intent.ItemId);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(100), frame.Intent.BeforeQuantity);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(104), frame.Intent.Slot);
            Convert.FromHexString(frame.Intent.ModuleFingerprint).CopyTo(bytes.Slice(112, 32));
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(144), frame.Transaction);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(152), frame.ResultWorld);
        }

        internal static bool TryRead(ReadOnlySpan<byte> bytes, out PlayerWorldFrame frame)
        {
            frame = default;
            if (bytes.Length != Length || !IsFrame(bytes) || bytes[4] != 1 || bytes[7] != 0
                || bytes[5] < (byte)PlayerWorldMessage.Request || bytes[5] > (byte)PlayerWorldMessage.Closed
                || bytes[6] > (byte)WorldAction.Drop || BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(108)) != 0)
                return false;
            var intent = new PlayerWorldIntent((WorldAction)bytes[6], BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(80)),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(88)), BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(96)),
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(100)), BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(104)),
                Convert.ToHexString(bytes.Slice(112, 32)));
            if (intent.ItemId <= 0 || intent.ItemId > ServerInventory.MaxItemId || intent.BeforeQuantity < 0
                || (intent.IsGrant ? intent.Key != 0 : intent.Key == 0 || intent.BeforeQuantity == 0)
                || (intent.Action == WorldAction.Drop ? intent.World != 0 : intent.World == 0)
                || (intent.Action is WorldAction.Drop or WorldAction.Pickup ? intent.Slot != -1 : intent.Slot is < 0 or > 11))
                return false;
            frame = new(new Guid(bytes.Slice(8, 16)), BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(24)),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(32)), Convert.ToHexString(bytes.Slice(40, 32)),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(72)), intent, (PlayerWorldMessage)bytes[5],
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(144)), BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(152)));
            if (frame.MigrationId == Guid.Empty || frame.Generation == 0 || frame.NetworkId == 0 || frame.Request == 0) return false;
            if (frame.Kind == PlayerWorldMessage.Request) return frame.Transaction == 0 && frame.ResultWorld == 0;
            if (frame.Kind == PlayerWorldMessage.Reserved) return frame.Transaction != 0 && frame.ResultWorld == 0;
            if (frame.Kind == PlayerWorldMessage.Applied)
                return frame.Transaction != 0 && frame.ResultWorld != 0
                    && (intent.Action == WorldAction.Drop || frame.ResultWorld == intent.World);
            if (frame.Kind is PlayerWorldMessage.Rejected or PlayerWorldMessage.Unconfirmed) return frame.ResultWorld == 0;
            return frame.ResultWorld == 0 || frame.Transaction != 0
                && (intent.Action == WorldAction.Drop || frame.ResultWorld == intent.World);
        }
    }
}
