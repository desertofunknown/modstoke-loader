// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Modstoke.Sons
{
    internal enum PlayerInventoryMessage : byte
    {
        Ready = 1, Armed, Remove, Applied, Unconfirmed,
        GrantPrepare, GrantReady, GrantPoll, GrantApplied, GrantRejected, GrantUnconfirmed,
        StowHeld, StowApplied, StowUnconfirmed, StowCommitted, StowSettled,
        Perishable, PerishableApplied, PerishableUnconfirmed, StowRequest, StowRequestAccepted, StowRequestRejected,
        EquipHeld, EquipApplied, EquipUnconfirmed, EquipCommitted, EquipSettled,
        EquipRequest, EquipRequestAccepted, EquipRequestRejected, EquipRejected,
        ConsumeRequest, ConsumePrepare, ConsumeRemoved, ConsumeProject, ConsumeApplied, ConsumeSettled, ConsumeRejected, ConsumeUnconfirmed
    }

    internal readonly record struct PlayerInventoryFrame(Guid MigrationId, ulong Generation, ulong NetworkId,
        string Fingerprint, ulong Transaction, ulong Key, int ItemId, int Quantity, int BeforeQuantity,
        PlayerInventoryMessage Kind, string ModuleFingerprint = null,
        string BeforeModuleFingerprint = null, int PerishableState = 0, float PerishableRemaining = 0, bool PauseDecay = false,
        byte EquipmentOptions = 0);

    internal static class PlayerInventoryWire
    {
        internal const int Length = 184;
        internal const int MaximumGrantModuleBytes = 32768;
        private const uint Magic = 0x5649504D; // MPIV

        internal static void Write(in PlayerInventoryFrame frame, Span<byte> bytes,
            PlayerInventoryModuleState grantState = null)
        {
            bytes.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
            bytes[4] = 6;
            bytes[5] = (byte)frame.Kind;
            frame.MigrationId.TryWriteBytes(bytes.Slice(8, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(24), frame.Generation);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(32), frame.NetworkId);
            Convert.FromHexString(frame.Fingerprint).CopyTo(bytes.Slice(40, 32));
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(72), frame.Transaction);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(80), frame.Key);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(88), frame.ItemId);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(92), frame.Quantity);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(96), frame.BeforeQuantity);
            bytes[100] = frame.EquipmentOptions;
            if (frame.ModuleFingerprint != null) Convert.FromHexString(frame.ModuleFingerprint).CopyTo(bytes.Slice(108, 32));
            if (frame.BeforeModuleFingerprint != null) Convert.FromHexString(frame.BeforeModuleFingerprint).CopyTo(bytes.Slice(140, 32));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(172), frame.PerishableState);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(176), frame.PerishableRemaining);
            bytes[180] = frame.PauseDecay ? (byte)1 : (byte)0;
            if (frame.Kind is PlayerInventoryMessage.GrantPrepare or PlayerInventoryMessage.GrantPoll)
            {
                if (grantState == null) throw new InvalidOperationException("Grant preparation needs exact supplemental module state.");
                bytes[140] = (byte)(1 | (grantState.ContainerVolume.HasValue ? 2 : 0)
                    | (grantState.LimbIsFresh.HasValue ? 4 : 0) | (grantState.LimbBloodReveal.HasValue ? 8 : 0));
                bytes[141] = grantState.LimbIsFresh == true ? (byte)1 : (byte)0;
                BinaryPrimitives.WriteSingleLittleEndian(bytes[144..], grantState.ContainerVolume ?? 0);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes[148..], grantState.LimbBloodReveal ?? 0);
            }
        }

        internal static bool TryRead(ReadOnlySpan<byte> bytes, out PlayerInventoryFrame frame,
            out PlayerInventoryModuleState grantState)
        {
            frame = default;
            grantState = null;
            if (bytes.Length != Length || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
                || bytes[4] != 6 || bytes[5] < 1 || bytes[5] > (byte)PlayerInventoryMessage.EquipRejected
                || bytes[6] != 0 || bytes[7] != 0 || bytes[100] > 3 || bytes[101] != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(102)) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(104)) != 0
                || bytes[180] > 1 || bytes[181] != 0 || bytes[182] != 0 || bytes[183] != 0)
                return false;
            frame = new(new Guid(bytes.Slice(8, 16)), BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(24)),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(32)), Convert.ToHexString(bytes.Slice(40, 32)),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(72)), BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(80)),
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(88)), BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(92)),
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(96)), (PlayerInventoryMessage)bytes[5],
                bytes[5] >= (byte)PlayerInventoryMessage.GrantPrepare && bytes[5] <= (byte)PlayerInventoryMessage.EquipRejected
                    ? Convert.ToHexString(bytes.Slice(108, 32)) : null,
                bytes[5] >= (byte)PlayerInventoryMessage.Perishable && bytes[5] <= (byte)PlayerInventoryMessage.PerishableUnconfirmed
                    ? Convert.ToHexString(bytes.Slice(140, 32)) : null,
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(172)), BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(176)),
                bytes[180] != 0, bytes[100]);
            if (frame.MigrationId == Guid.Empty || frame.Generation == 0 || frame.NetworkId == 0) return false;
            if ((frame.Kind < PlayerInventoryMessage.EquipHeld || frame.Kind > PlayerInventoryMessage.EquipRejected)
                && frame.EquipmentOptions != 0) return false;
            if (frame.Kind is >= PlayerInventoryMessage.Perishable and <= PlayerInventoryMessage.PerishableUnconfirmed)
                return frame.Transaction > 0 && frame.Key > 0 && frame.ItemId > 0 && frame.Quantity == 1
                    && frame.BeforeQuantity == 1 && frame.PerishableState >= 0 && float.IsFinite(frame.PerishableRemaining);
            if (frame.Kind is PlayerInventoryMessage.GrantPrepare or PlayerInventoryMessage.GrantPoll)
            {
                byte flags = bytes[140];
                if ((flags & 1) == 0 || flags > 15 || bytes[141] > 1 || bytes[142] != 0 || bytes[143] != 0
                    || ((flags & 4) == 0 && bytes[141] != 0)) return false;
                float volume = BinaryPrimitives.ReadSingleLittleEndian(bytes[144..]);
                uint blood = BinaryPrimitives.ReadUInt32LittleEndian(bytes[148..]);
                if (!float.IsFinite(volume) || volume < 0 || ((flags & 2) == 0 && volume != 0)
                    || ((flags & 8) == 0 && blood != 0)) return false;
                for (int i = 152; i < Length; i++) if (bytes[i] != 0) return false;
                grantState = new((flags & 2) != 0 ? volume : null,
                    (flags & 4) != 0 ? bytes[141] != 0 : null, (flags & 8) != 0 ? blood : null);
                return frame.Transaction > 0 && frame.Key == frame.Transaction && frame.ItemId > 0
                    && frame.Quantity == 1 && frame.BeforeQuantity >= 0 && frame.BeforeQuantity < int.MaxValue;
            }
            for (int i = 140; i < Length; i++) if (bytes[i] != 0) return false;
            if (frame.Kind is >= PlayerInventoryMessage.EquipHeld and <= PlayerInventoryMessage.EquipRejected
                or >= PlayerInventoryMessage.StowRequest and <= PlayerInventoryMessage.StowRequestRejected
                or >= PlayerInventoryMessage.StowHeld and <= PlayerInventoryMessage.StowSettled)
                return frame.Transaction > 0 && frame.Key > 0 && frame.ItemId > 0 && frame.Quantity == 1
                    && frame.BeforeQuantity is >= 0 and < 6;
            if (frame.ModuleFingerprint != null)
                return frame.Transaction > 0 && frame.Key == frame.Transaction && frame.ItemId > 0
                    && frame.Quantity == 1 && frame.BeforeQuantity >= 0 && frame.BeforeQuantity < int.MaxValue;
            for (int i = 108; i < 140; i++) if (bytes[i] != 0) return false;
            return frame.Kind is PlayerInventoryMessage.Ready or PlayerInventoryMessage.Armed
                ? frame.Key == 0 && frame.ItemId == 0 && frame.Quantity == 0 && frame.BeforeQuantity == 0
                : frame.Transaction > 0 && frame.Key > 0 && frame.ItemId > 0 && frame.Quantity > 0
                    && frame.BeforeQuantity >= frame.Quantity;
        }

        internal static string HashModules(string modules)
        {
            if (modules == null || Encoding.UTF8.GetByteCount(modules) > MaximumGrantModuleBytes)
                throw new InvalidOperationException("Grant module data exceeds its bound; nothing was truncated.");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(modules)));
        }
    }

    internal readonly record struct PlayerConsumptionFrame(PlayerInventoryFrame Action, ulong Request,
        ConsumptionNumbers Before, ConsumptionNumbers After, ConsumptionDebit Debit = default)
    {
        internal PlayerConsumptionFrame WithKind(PlayerInventoryMessage kind) => this with { Action = Action with { Kind = kind } };

        internal bool SameReservation(in PlayerConsumptionFrame other)
            => Action == (other.Action with { Kind = Action.Kind }) && Request == other.Request
                && Before == other.Before && Debit == other.Debit;
    }

    internal static class PlayerConsumptionWire
    {
        private const uint Magic = 0x4349504D; // MPIC, distinct from the padded inventory codec.
        private const byte Version = 7;
        // Before pins the retained source state; Project.After is finalized at server commit,
        // not copied from the earlier advisory Prepare.After. Neither is source authority.
        // Header/custody/request 104, two exact 84-byte snapshots, 76-byte reserved debit.
        internal const int Length = 348;
        internal static void Write(in PlayerConsumptionFrame frame, Span<byte> bytes)
        {
            bytes.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
            bytes[4] = Version;
            bytes[5] = (byte)frame.Action.Kind;
            frame.Action.MigrationId.TryWriteBytes(bytes[8..24]);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], frame.Action.Generation);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], frame.Action.NetworkId);
            Convert.FromHexString(frame.Action.Fingerprint).CopyTo(bytes[40..72]);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[72..], frame.Action.Transaction);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[80..], frame.Action.Key);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[88..], frame.Action.ItemId);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[92..], frame.Action.BeforeQuantity);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[96..], frame.Request);
            frame.Before.Write(bytes[104..188]);
            frame.After.Write(bytes[188..272]);
            frame.Debit.Write(bytes[272..348]);
        }
        internal static bool TryRead(ReadOnlySpan<byte> bytes, out PlayerConsumptionFrame frame)
        {
            frame = default;
            if (bytes.Length != Length || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
                || bytes[4] != Version || bytes[5] < (byte)PlayerInventoryMessage.ConsumeRequest
                || bytes[5] > (byte)PlayerInventoryMessage.ConsumeUnconfirmed || bytes[6] != 0 || bytes[7] != 0) return false;
            var action = new PlayerInventoryFrame(new Guid(bytes[8..24]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]),
                Convert.ToHexString(bytes[40..72]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[72..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[80..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[88..]),
                1, BinaryPrimitives.ReadInt32LittleEndian(bytes[92..]), (PlayerInventoryMessage)bytes[5]);
            if (!ConsumptionDebit.TryRead(bytes[272..348], out var debit)
                || (debit == default && action.Kind is not (PlayerInventoryMessage.ConsumeRequest or PlayerInventoryMessage.ConsumeRejected))
                || (action.Kind == PlayerInventoryMessage.ConsumeRequest && debit != default)) return false;
            frame = new(action, BinaryPrimitives.ReadUInt64LittleEndian(bytes[96..]),
                ConsumptionNumbers.Read(bytes[104..188]), ConsumptionNumbers.Read(bytes[188..272]), debit);
            return action.MigrationId != Guid.Empty && action.Generation > 0 && action.NetworkId > 0
                && action.Transaction > 0 && action.Key > 0 && action.ItemId > 0 && action.BeforeQuantity > 0
                && frame.Request > 0 && frame.Before.IsFinite && frame.After.IsFinite;
        }
    }
}
