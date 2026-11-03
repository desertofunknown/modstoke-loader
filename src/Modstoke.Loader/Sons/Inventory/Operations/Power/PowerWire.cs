// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;

namespace Modstoke.Sons
{
    internal enum PowerMessage : byte { Project = 1, Applied, Unconfirmed }

    internal readonly record struct PowerFrame(Guid MigrationId, ulong Generation, ulong NetworkId,
        string Fingerprint, ulong Transaction, FlashlightPowerState State, float BeforeCharge,
        string BeforeModules, string AfterModules, PowerMessage Kind);

    internal static class PowerWire
    {
        internal const int Length = 184;
        private const uint Magic = 0x5049504D; // MPIP, separate from inventory and intake.

        internal static void Write(in PowerFrame frame, Span<byte> bytes)
        {
            bytes.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
            bytes[4] = 1;
            bytes[5] = (byte)frame.Kind;
            frame.MigrationId.TryWriteBytes(bytes[8..24]);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], frame.Generation);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], frame.NetworkId);
            Convert.FromHexString(frame.Fingerprint).CopyTo(bytes[40..72]);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[72..], frame.Transaction);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[80..], frame.State.InstanceKey);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[88..], frame.State.Revision);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[96..], frame.State.ItemId);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[100..], frame.State.Capacity);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[104..], frame.BeforeCharge);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[108..], frame.State.Charge);
            bytes[112] = frame.State.Active ? (byte)1 : (byte)0;
            bytes[113] = frame.State.NeedsBattery ? (byte)1 : (byte)0;
            Convert.FromHexString(frame.BeforeModules).CopyTo(bytes[120..152]);
            Convert.FromHexString(frame.AfterModules).CopyTo(bytes[152..184]);
        }

        internal static bool TryRead(ReadOnlySpan<byte> bytes, out PowerFrame frame)
        {
            frame = default;
            if (bytes.Length != Length || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
                || bytes[4] != 1 || bytes[5] < 1 || bytes[5] > 3 || bytes[6] != 0 || bytes[7] != 0
                || bytes[112] > 1 || bytes[113] > 1
                || BinaryPrimitives.ReadUInt16LittleEndian(bytes[114..]) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes[116..]) != 0) return false;
            var state = new FlashlightPowerState(BinaryPrimitives.ReadUInt64LittleEndian(bytes[80..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[88..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[96..]),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[100..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[108..]),
                bytes[112] != 0, bytes[113] != 0);
            float before = BinaryPrimitives.ReadSingleLittleEndian(bytes[104..]);
            frame = new(new Guid(bytes[8..24]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]), Convert.ToHexString(bytes[40..72]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[72..]), state, before,
                Convert.ToHexString(bytes[120..152]), Convert.ToHexString(bytes[152..184]), (PowerMessage)bytes[5]);
            return frame.MigrationId != Guid.Empty && frame.Generation != 0 && frame.NetworkId != 0
                && frame.Transaction != 0 && state.InstanceKey != 0 && state.Revision != 0 && state.ItemId > 0
                && float.IsFinite(state.Capacity) && state.Capacity > 0
                && float.IsFinite(state.Charge) && state.Charge >= 0 && state.Charge <= state.Capacity
                && float.IsFinite(before) && before >= 0 && before <= state.Capacity
                && (!state.NeedsBattery || state.Active);
        }
    }
}
