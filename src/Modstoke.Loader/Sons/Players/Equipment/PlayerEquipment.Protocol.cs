// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using global::Sons.Inventory;
using global::Sons.Items.Core;

namespace Modstoke.Sons
{
    public enum PlayerEquipmentKind : byte
    {
        HeldInitialized = 1, HeldRemoved, SlotTransferred, RightHandLit,
        ClothingEquipped, ClothingUnequipped, ArmourEquipped, ArmourUnequipped,
        ArmourBroken, RaceChanged
    }

    /// <summary>A copied callback notification, not an inventory grant or an authority claim.</summary>
    public readonly record struct PlayerEquipmentChange(
        PlayerEquipmentKind Kind, int Value, int FromSlot = 0, int ToSlot = 0,
        bool Flag = false)
    {
        // Value is an item ID except for RaceChanged (race) and RightHandLit (zero).
        // Flag is mpIgnoreCallback for HeldRemoved and isLit for RightHandLit.
        internal bool IsValid => Kind switch
        {
            PlayerEquipmentKind.HeldRemoved => Value > 0 && FromSlot == 0 && ToSlot == 0,
            PlayerEquipmentKind.RightHandLit => Value == 0 && FromSlot == 0 && ToSlot == 0,
            PlayerEquipmentKind.RaceChanged => Value is >= 0 and <= 7
                && FromSlot == 0 && ToSlot == 0 && !Flag,
            PlayerEquipmentKind.SlotTransferred => Value > 0 && !Flag
                && IsEquipmentSlot(FromSlot) && IsEquipmentSlot(ToSlot) && FromSlot != ToSlot,
            PlayerEquipmentKind.ArmourEquipped or PlayerEquipmentKind.ArmourUnequipped
                or PlayerEquipmentKind.ArmourBroken => Value > 0 && FromSlot == 0
                    && ToSlot is >= 0 and < 12 && !Flag,
            PlayerEquipmentKind.HeldInitialized or PlayerEquipmentKind.ClothingEquipped
                or PlayerEquipmentKind.ClothingUnequipped => Value > 0
                    && FromSlot == 0 && ToSlot == 0 && !Flag,
            _ => false
        };

        private static bool IsEquipmentSlot(int slot) => (EquipmentSlot)slot is EquipmentSlot.RightHand
            or EquipmentSlot.LeftHand or EquipmentSlot.Chest or EquipmentSlot.Feet
            or EquipmentSlot.FullBody or EquipmentSlot.Eyes;
    }

    public enum PlayerEquipmentDecision { Rejected, Approved, Unsupported }

    /// <summary>
    /// Server-owned policy must validate this exact operation against this peer's authoritative
    /// inventory/appearance ledger, including possession, slot transitions, armour damage and race
    /// policy. Never approve from client payloads, local host inventory, or catalog membership alone.
    /// For HeldInitialized, return the server's exact authorized ItemInstance, including its module
    /// state. No other operation uses that output. Do not mutate native state or reenter the publisher.
    /// </summary>
    public delegate PlayerEquipmentDecision PlayerEquipmentAuthorize(
        PlayerBridge bridge, in PlayerEquipmentChange change,
        out ItemInstance authoritativeHeldItem, out string detail);

    internal enum PlayerEquipmentReceipt : byte { None, Applied, Ignored, Rejected, Unsupported, Unconfirmed }

    internal readonly record struct PlayerEquipmentFrame(Guid MigrationId, ulong Generation,
        ulong NetworkId, uint Sequence, PlayerEquipmentChange Change,
        PlayerEquipmentReceipt Receipt = PlayerEquipmentReceipt.None);

    internal static class PlayerEquipmentWire
    {
        internal const int Length = 60;
        private const uint Magic = 0x5145504D; // MPEQ, independent of the movement protocol.

        internal static void Write(in PlayerEquipmentFrame frame, Span<byte> bytes)
        {
            bytes.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
            bytes[4] = 1;
            bytes[5] = (byte)frame.Receipt;
            bytes[6] = (byte)frame.Change.Kind;
            bytes[7] = frame.Change.Flag ? (byte)1 : (byte)0;
            frame.MigrationId.TryWriteBytes(bytes.Slice(8, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(24), frame.Generation);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(32), frame.NetworkId);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(40), frame.Sequence);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(44), frame.Change.Value);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(48), frame.Change.FromSlot);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(52), frame.Change.ToSlot);
        }

        internal static bool TryRead(ReadOnlySpan<byte> bytes, out PlayerEquipmentFrame frame)
        {
            frame = default;
            if (bytes.Length != Length || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
                || bytes[4] != 1 || bytes[5] > (byte)PlayerEquipmentReceipt.Unconfirmed || bytes[7] > 1
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(56)) != 0) return false;
            var change = new PlayerEquipmentChange((PlayerEquipmentKind)bytes[6],
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(44)),
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(48)),
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(52)), bytes[7] != 0);
            if (!change.IsValid) return false;
            frame = new(new Guid(bytes.Slice(8, 16)), BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(24)),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(32)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(40)), change, (PlayerEquipmentReceipt)bytes[5]);
            return frame.MigrationId != Guid.Empty && frame.Generation != 0
                && frame.NetworkId != 0 && frame.Sequence != 0;
        }
    }
}
