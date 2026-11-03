// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;

namespace Modstoke.Sons.Combat
{
    public enum StrictCombatRebreatherRefillResult : byte
    {
        Accepted, Denied, StaleLife, StaleRevision, Incapacitated, Unavailable,
        Conflict, StaleSequence, OutOfOrder
    }

    public readonly struct StrictCombatRebreatherRefillRequest
    {
        public StrictCombatRebreatherRefillRequest(StrictCombatBinding binding, ulong sequence,
            ulong lifeGeneration, ulong expectedRevision, StrictCombatRebreatherRefillMode mode)
            : this(binding, sequence, lifeGeneration, expectedRevision, mode, false) { }

        public StrictCombatRebreatherRefillRequest(StrictCombatBinding binding, ulong sequence,
            ulong lifeGeneration, ulong expectedRevision, StrictCombatRebreatherRefillMode mode, bool allowsTimedProgress)
        {
            Binding = binding;
            Sequence = sequence;
            LifeGeneration = lifeGeneration;
            ExpectedRevision = expectedRevision;
            Mode = mode;
            AllowsTimedProgress = allowsTimedProgress;
        }

        public StrictCombatBinding Binding { get; }
        public ulong Sequence { get; }
        public ulong LifeGeneration { get; }
        public ulong ExpectedRevision { get; }
        public StrictCombatRebreatherRefillMode Mode { get; }
        public bool AllowsTimedProgress { get; }
        public bool IsValid => StrictCombatProtocol.IsValidBinding(Binding)
            && Sequence != 0 && LifeGeneration != 0
            && (Mode == StrictCombatRebreatherRefillMode.Inventory || Mode == StrictCombatRebreatherRefillMode.Worn);

        public bool SameRequest(in StrictCombatRebreatherRefillRequest other)
            => Binding.SameBinding(other.Binding)
                && Sequence == other.Sequence && LifeGeneration == other.LifeGeneration
                && ExpectedRevision == other.ExpectedRevision && Mode == other.Mode
                && AllowsTimedProgress == other.AllowsTimedProgress;
    }

    public readonly struct StrictCombatRebreatherRefillResponse
    {
        public StrictCombatRebreatherRefillResponse(StrictCombatRebreatherRefillRequest request,
            StrictCombatRebreatherRefillResult result, ulong currentLifeGeneration,
            StrictCombatOwnedRebreatherState resources)
        {
            Request = request;
            Result = result;
            CurrentLifeGeneration = currentLifeGeneration;
            Resources = resources;
        }

        public StrictCombatRebreatherRefillRequest Request { get; }
        public StrictCombatRebreatherRefillResult Result { get; }
        public ulong CurrentLifeGeneration { get; }
        public StrictCombatOwnedRebreatherState Resources { get; }
        public bool IsValid => Request.IsValid && Result <= StrictCombatRebreatherRefillResult.OutOfOrder
            && Resources != null && (!Resources.IsProvisioned || CurrentLifeGeneration != 0)
            && (Result != StrictCombatRebreatherRefillResult.Accepted
                || (CurrentLifeGeneration == Request.LifeGeneration && Resources.IsProvisioned
                    && Request.ExpectedRevision < ulong.MaxValue
                    && (Request.AllowsTimedProgress ? Resources.Revision > Request.ExpectedRevision
                        : Resources.Revision == Request.ExpectedRevision + 1)
                    && Resources.RefillMode == Request.Mode && Resources.LastRefillSequence != 0));
    }

    /// <summary>Life-bound refill requests and authoritative resource responses.</summary>
    public static class StrictCombatRebreatherRefillProtocol
    {
        public const byte Version = 1;
        public const ulong Capability = 1UL << 9;
        public const ulong RequiredCapabilities = StrictCombatRebreatherProtocol.RequiredCapabilities | Capability;
        public const int RequestLength = 96;
        public const int ResponseLength = 168;
        private const uint Magic = 0x4652534D;

        public static bool TryWriteRequest(in StrictCombatRebreatherRefillRequest value,
            Span<byte> destination, out int written)
            => TryWriteRequest(in value, destination, false, out written);

        public static bool TryWriteProgressRequest(in StrictCombatRebreatherRefillRequest value,
            Span<byte> destination, out int written)
            => TryWriteRequest(in value, destination, true, out written);

        private static bool TryWriteRequest(in StrictCombatRebreatherRefillRequest value,
            Span<byte> destination, bool progress, out int written)
        {
            written = 0;
            if (destination.Length < RequestLength || !value.IsValid
                || value.AllowsTimedProgress != progress) return false;
            Span<byte> record = destination.Slice(0, RequestLength);
            record.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(record, Magic);
            record[4] = progress ? StrictCombatRebreatherProgressProtocol.Version : Version;
            record[5] = 1;
            record[6] = (byte)value.Mode;
            var binding = value.Binding;
            if (!StrictCombatProtocol.TryWriteBinding(in binding, record.Slice(8, 56), out int bindingWritten)
                || bindingWritten != StrictCombatProtocol.BindingLength) return false;
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(64), value.Sequence);
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(72), value.LifeGeneration);
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(80), value.ExpectedRevision);
            written = RequestLength;
            return true;
        }

        public static bool TryReadRequest(ReadOnlySpan<byte> source, out StrictCombatRebreatherRefillRequest value)
            => TryReadRequest(source, false, out value);

        public static bool TryReadProgressRequest(ReadOnlySpan<byte> source, out StrictCombatRebreatherRefillRequest value)
            => TryReadRequest(source, true, out value);

        private static bool TryReadRequest(ReadOnlySpan<byte> source, bool progress, out StrictCombatRebreatherRefillRequest value)
        {
            value = default;
            if (source.Length != RequestLength || BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic
                || source[4] != (progress ? StrictCombatRebreatherProgressProtocol.Version : Version) || source[5] != 1 || source[7] != 0
                || BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(88)) != 0
                || !StrictCombatProtocol.TryReadBinding(source.Slice(8, 56), out var binding)) return false;
            var candidate = new StrictCombatRebreatherRefillRequest(binding,
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(64)),
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(72)),
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(80)), (StrictCombatRebreatherRefillMode)source[6], progress);
            if (!candidate.IsValid) return false;
            value = candidate;
            return true;
        }

        public static bool TryWriteResponse(in StrictCombatRebreatherRefillResponse value,
            Span<byte> destination, out int written)
            => TryWriteResponse(in value, destination, false, out written);

        public static bool TryWriteProgressResponse(in StrictCombatRebreatherRefillResponse value,
            Span<byte> destination, out int written)
            => TryWriteResponse(in value, destination, true, out written);

        private static bool TryWriteResponse(in StrictCombatRebreatherRefillResponse value,
            Span<byte> destination, bool progress, out int written)
        {
            written = 0;
            if (destination.Length < ResponseLength || !value.IsValid
                || value.Request.AllowsTimedProgress != progress) return false;
            Span<byte> record = destination.Slice(0, ResponseLength);
            record.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(record, Magic);
            record[4] = progress ? StrictCombatRebreatherProgressProtocol.Version : Version;
            record[5] = 2;
            record[6] = (byte)value.Result;
            var request = value.Request;
            if (!TryWriteRequest(in request, record.Slice(8, RequestLength), progress, out int requestWritten)
                || requestWritten != RequestLength) return false;
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(104), value.CurrentLifeGeneration);
            var resources = value.Resources;
            if (resources.IsProvisioned)
            {
                record[112] = 1;
                record[113] = (byte)resources.RefillMode;
                BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(120), resources.Revision);
                BinaryPrimitives.WriteInt64LittleEndian(record.Slice(128), BitConverter.DoubleToInt64Bits(resources.Capacity));
                BinaryPrimitives.WriteInt64LittleEndian(record.Slice(136), BitConverter.DoubleToInt64Bits(resources.Air));
                BinaryPrimitives.WriteInt64LittleEndian(record.Slice(144), BitConverter.DoubleToInt64Bits(resources.RemainingRefillAir));
                BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(152), resources.LastRefillSequence);
                BinaryPrimitives.WriteInt32LittleEndian(record.Slice(160), resources.Canisters);
            }
            written = ResponseLength;
            return true;
        }

        public static bool TryReadResponse(ReadOnlySpan<byte> source, out StrictCombatRebreatherRefillResponse value)
            => TryReadResponse(source, false, out value);

        public static bool TryReadProgressResponse(ReadOnlySpan<byte> source, out StrictCombatRebreatherRefillResponse value)
            => TryReadResponse(source, true, out value);

        private static bool TryReadResponse(ReadOnlySpan<byte> source, bool progress, out StrictCombatRebreatherRefillResponse value)
        {
            value = default;
            if (source.Length != ResponseLength || BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic
                || source[4] != (progress ? StrictCombatRebreatherProgressProtocol.Version : Version) || source[5] != 2 || source[7] != 0 || source[112] > 1
                || BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(114)) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(116)) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(164)) != 0
                || !TryReadRequest(source.Slice(8, RequestLength), progress, out var request)) return false;
            StrictCombatOwnedRebreatherState resources;
            if (source[112] == 0)
            {
                for (int i = 113; i < ResponseLength; i++)
                    if (source[i] != 0) return false;
                resources = StrictCombatOwnedRebreatherState.Unprovisioned;
            }
            else if (!StrictCombatOwnedRebreatherState.TryCreate(
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(120)),
                BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(source.Slice(128))),
                BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(source.Slice(136))),
                BinaryPrimitives.ReadInt32LittleEndian(source.Slice(160)),
                (StrictCombatRebreatherRefillMode)source[113],
                BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(source.Slice(144))),
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(152)), out resources)) return false;
            var candidate = new StrictCombatRebreatherRefillResponse(request,
                (StrictCombatRebreatherRefillResult)source[6],
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(104)), resources);
            if (!candidate.IsValid) return false;
            value = candidate;
            return true;
        }
    }
}
