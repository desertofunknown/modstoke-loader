// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;

namespace Modstoke.Sons.Combat
{
    /// <summary>Versioned pistol intents and server receipts; no native gameplay effects.</summary>
    public static class StrictCombatProtocol
    {
        public const ulong Capability = 1UL << 2;
        public const ulong LifeBoundCapability = 1UL << 3;
        public const ulong RequiredCapabilities = Capability | LifeBoundCapability;
        private const uint Magic = 0x4243534D;
        public const byte Version = 4;
        private const byte BindingKind = 1;
        private const byte IntentKind = 2;
        private const byte ReceiptKind = 3;
        public const int BindingLength = 56;
        public const int IntentLength = 56;
        public const int ReceiptLength = 96;
        public const uint FireIntervalMs = 200;
        public const int MagazineCapacity = 12;

        public static bool TryWriteBinding(in StrictCombatBinding value, Span<byte> destination, out int written)
        {
            written = 0;
            if (destination.Length < BindingLength || !IsValidBinding(in value))
                return false;

            Span<byte> record = destination.Slice(0, BindingLength);
            WriteHeader(record, BindingKind, value.Epoch, 0);
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(32), value.ActorNetworkId);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(40), value.ReloadDurationMs);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(44), FireIntervalMs);
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(48), value.Generation);
            written = BindingLength;
            return true;
        }

        public static bool TryReadBinding(ReadOnlySpan<byte> source, out StrictCombatBinding value)
        {
            value = default;
            if (!TryReadHeader(source, BindingLength, BindingKind, out Guid epoch, out ulong sequence)
                || sequence != 0 || BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(44)) != FireIntervalMs)
                return false;

            var candidate = new StrictCombatBinding(epoch,
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(48)),
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(32)),
                BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(40)));
            if (!IsValidBinding(in candidate))
                return false;
            value = candidate;
            return true;
        }

        public static bool TryWriteIntent(in StrictCombatIntent value, Span<byte> destination, out int written)
        {
            written = 0;
            if (destination.Length < IntentLength || !value.IsValid)
                return false;

            Span<byte> record = destination.Slice(0, IntentLength);
            WriteHeader(record, IntentKind, value.Epoch, value.Sequence);
            record[32] = (byte)value.Action;
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(36), BitConverter.SingleToInt32Bits(value.DirectionX));
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(40), BitConverter.SingleToInt32Bits(value.DirectionY));
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(44), BitConverter.SingleToInt32Bits(value.DirectionZ));
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(48), value.LifeGeneration);
            written = IntentLength;
            return true;
        }

        public static bool TryReadIntent(ReadOnlySpan<byte> source, out StrictCombatIntent value)
        {
            value = default;
            if (!TryReadHeader(source, IntentLength, IntentKind, out Guid epoch, out ulong sequence)
                || source[33] != 0 || source[34] != 0 || source[35] != 0)
                return false;

            var candidate = new StrictCombatIntent(epoch, sequence,
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(48)), (StrictCombatAction)source[32],
                BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source.Slice(36))),
                BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source.Slice(40))),
                BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source.Slice(44))));
            if (!candidate.IsValid)
                return false;
            value = candidate;
            return true;
        }

        public static bool TryWriteReceipt(in StrictCombatReceipt value, Span<byte> destination, out int written)
        {
            written = 0;
            if (destination.Length < ReceiptLength || !IsValidReceipt(in value))
                return false;

            Span<byte> record = destination.Slice(0, ReceiptLength);
            WriteHeader(record, ReceiptKind, value.Epoch, value.Sequence);
            StrictCombatSnapshot state = value.State;
            record[32] = (byte)value.Action;
            record[33] = (byte)value.Result;
            record[34] = (byte)((state.OwnsPistol ? 1 : 0) | (state.Equipped ? 2 : 0)
                | (state.ReloadRounds > 0 ? 4 : 0));
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(36), state.Magazine);
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(40), state.Reserve);
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(44), state.ReloadRounds);
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(48), state.Revision);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(56), state.ReloadRemainingMs);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(60), state.FireRemainingMs);
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(64), BitConverter.SingleToInt32Bits(state.Health));
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(68), BitConverter.SingleToInt32Bits(state.MaximumHealth));
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(72), state.LifeGeneration);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(80), state.DownedRemainingMs);
            record[84] = (byte)state.LifeStage;
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(88), value.RequestLifeGeneration);
            written = ReceiptLength;
            return true;
        }

        public static bool TryReadReceipt(ReadOnlySpan<byte> source, out StrictCombatReceipt value)
        {
            value = default;
            if (!TryReadHeader(source, ReceiptLength, ReceiptKind, out Guid epoch, out ulong sequence)
                || (source[34] & ~7) != 0 || source[35] != 0
                || source[85] != 0 || source[86] != 0 || source[87] != 0)
                return false;

            byte flags = source[34];
            var state = new StrictCombatSnapshot(
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(48)),
                (flags & 1) != 0, (flags & 2) != 0,
                BinaryPrimitives.ReadInt32LittleEndian(source.Slice(36)),
                BinaryPrimitives.ReadInt32LittleEndian(source.Slice(40)),
                BinaryPrimitives.ReadInt32LittleEndian(source.Slice(44)),
                BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(56)),
                BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(60)),
                (StrictCombatLifeStage)source[84],
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(72)),
                BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source.Slice(64))),
                BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source.Slice(68))),
                BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(80)));
            var candidate = new StrictCombatReceipt(epoch, sequence, (StrictCombatAction)source[32],
                (StrictCombatResult)source[33],
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(88)), state);
            if (((flags & 4) != 0) != (state.ReloadRounds > 0) || !IsValidReceipt(in candidate))
                return false;
            value = candidate;
            return true;
        }

        private static void WriteHeader(Span<byte> record, byte kind, Guid epoch, ulong sequence)
        {
            record.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(record, Magic);
            record[4] = Version;
            record[5] = kind;
            epoch.TryWriteBytes(record.Slice(8, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(24), sequence);
        }

        private static bool TryReadHeader(ReadOnlySpan<byte> source, int length, byte kind,
            out Guid epoch, out ulong sequence)
        {
            epoch = default;
            sequence = 0;
            if (source.Length != length || BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic
                || source[4] != Version || source[5] != kind || source[6] != 0 || source[7] != 0)
                return false;

            epoch = new Guid(source.Slice(8, 16));
            sequence = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(24));
            return epoch != Guid.Empty;
        }

        public static bool IsValidBinding(in StrictCombatBinding value)
        {
            return value.Epoch != Guid.Empty && value.Generation != 0 && value.ActorNetworkId != 0
                && value.ReloadDurationMs > 0 && value.ReloadDurationMs <= int.MaxValue;
        }

        public static bool IsValidReceipt(in StrictCombatReceipt value)
        {
            if (value.Epoch == Guid.Empty || value.Sequence == 0
                || value.Action < StrictCombatAction.EquipPistol || value.Action > StrictCombatAction.ReadState
                || value.Result > StrictCombatResult.NotReady
                || ((value.Result == StrictCombatResult.ShotPending || value.Result == StrictCombatResult.EffectFailed)
                    && value.Action != StrictCombatAction.Fire))
                return false;

            StrictCombatSnapshot state = value.State;
            if (value.Action == StrictCombatAction.ReadState ? value.RequestLifeGeneration != 0 : value.RequestLifeGeneration == 0)
                return false;
            if (value.Result == StrictCombatResult.NotReady
                && (value.Action == StrictCombatAction.ReadState || value.Action == StrictCombatAction.EquipPistol
                    || value.RequestLifeGeneration != state.LifeGeneration))
                return false;
            if (value.Result == StrictCombatResult.StaleLife
                && (value.Action == StrictCombatAction.ReadState || value.RequestLifeGeneration == state.LifeGeneration))
                return false;
            if (((value.Result == StrictCombatResult.Accepted && value.Action != StrictCombatAction.ReadState)
                    || value.Result == StrictCombatResult.ShotPending)
                && value.RequestLifeGeneration != state.LifeGeneration)
                return false;
            if (!float.IsFinite(state.Health) || !float.IsFinite(state.MaximumHealth)
                || (state.Health == 0 && BitConverter.SingleToInt32Bits(state.Health) != 0)
                || (state.MaximumHealth == 0 && BitConverter.SingleToInt32Bits(state.MaximumHealth) != 0)
                || (state.LifeStage != StrictCombatLifeStage.Alive && (state.Equipped || state.ReloadRounds != 0)))
                return false;

            switch (state.LifeStage)
            {
                case StrictCombatLifeStage.Uninitialized:
                    if (state.LifeGeneration != 0 || state.Health != 0 || state.MaximumHealth != 0
                        || state.DownedRemainingMs != 0)
                        return false;
                    break;
                case StrictCombatLifeStage.Alive:
                    if (state.LifeGeneration == 0 || state.MaximumHealth <= 0
                        || state.Health <= 0 || state.Health > state.MaximumHealth || state.DownedRemainingMs != 0)
                        return false;
                    break;
                case StrictCombatLifeStage.Downed:
                    if (state.LifeGeneration == 0 || state.MaximumHealth <= 0 || state.Health != 0
                        || state.DownedRemainingMs == 0 || state.DownedRemainingMs > int.MaxValue)
                        return false;
                    break;
                case StrictCombatLifeStage.Dead:
                    if (state.LifeGeneration == 0 || state.MaximumHealth <= 0 || state.Health != 0
                        || state.DownedRemainingMs != 0)
                        return false;
                    break;
                default:
                    return false;
            }

            if (state.Magazine < 0 || state.Magazine > MagazineCapacity || state.Reserve < 0
                || state.ReloadRounds < 0 || state.ReloadRounds > MagazineCapacity - state.Magazine
                || (long)state.Magazine + state.Reserve + state.ReloadRounds > int.MaxValue
                || (state.Equipped && !state.OwnsPistol)
                || (!state.OwnsPistol && (state.Magazine != 0 || state.ReloadRounds != 0 || state.FireRemainingMs != 0))
                || state.ReloadRemainingMs > int.MaxValue || state.FireRemainingMs > FireIntervalMs)
                return false;

            if (state.ReloadRounds > 0)
                return state.OwnsPistol && state.Equipped && state.ReloadRemainingMs > 0;
            return state.ReloadRemainingMs == 0;
        }
    }
}
