// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;

namespace Modstoke.Sons.Combat
{
    public enum StrictCombatRebreatherWearResult : byte
    {
        Accepted, Denied, StaleLife, StaleRevision, Incapacitated, Unavailable,
        Conflict, StaleSequence, OutOfOrder, Unchanged
    }

    public readonly struct StrictCombatRebreatherWearRequest
    {
        public StrictCombatRebreatherWearRequest(StrictCombatBinding binding, ulong sequence,
            ulong lifeGeneration, ulong expectedRevision, bool isWorn)
            : this(binding, sequence, lifeGeneration, expectedRevision, isWorn, false) { }

        public StrictCombatRebreatherWearRequest(StrictCombatBinding binding, ulong sequence,
            ulong lifeGeneration, ulong expectedRevision, bool isWorn, bool allowsTimedProgress)
        {
            Binding = binding;
            Sequence = sequence;
            LifeGeneration = lifeGeneration;
            ExpectedRevision = expectedRevision;
            IsWorn = isWorn;
            AllowsTimedProgress = allowsTimedProgress;
        }

        public StrictCombatBinding Binding { get; }
        public ulong Sequence { get; }
        public ulong LifeGeneration { get; }
        public ulong ExpectedRevision { get; }
        public bool IsWorn { get; }
        public bool AllowsTimedProgress { get; }
        public bool IsValid => StrictCombatProtocol.IsValidBinding(Binding)
            && Sequence != 0 && LifeGeneration != 0;

        public bool SameRequest(in StrictCombatRebreatherWearRequest other)
            => Binding.SameBinding(other.Binding)
                && Sequence == other.Sequence && LifeGeneration == other.LifeGeneration
                && ExpectedRevision == other.ExpectedRevision && IsWorn == other.IsWorn
                && AllowsTimedProgress == other.AllowsTimedProgress;
    }

    public readonly struct StrictCombatRebreatherWearResponse
    {
        public StrictCombatRebreatherWearResponse(StrictCombatRebreatherWearRequest request,
            StrictCombatRebreatherWearResult result, ulong currentLifeGeneration,
            StrictCombatOwnedRebreatherState resources)
        {
            Request = request;
            Result = result;
            CurrentLifeGeneration = currentLifeGeneration;
            Resources = resources;
        }

        public StrictCombatRebreatherWearRequest Request { get; }
        public StrictCombatRebreatherWearResult Result { get; }
        public ulong CurrentLifeGeneration { get; }
        public StrictCombatOwnedRebreatherState Resources { get; }
        public bool IsValid => Request.IsValid && Result <= StrictCombatRebreatherWearResult.Unchanged
            && Resources != null
            && (!Resources.IsProvisioned || (CurrentLifeGeneration != 0 && Resources.HasWearState))
            && (Result != StrictCombatRebreatherWearResult.Accepted
                || (CurrentLifeGeneration == Request.LifeGeneration && Resources.IsProvisioned
                    && Resources.IsWorn == Request.IsWorn && Request.ExpectedRevision < ulong.MaxValue
                    && (Request.AllowsTimedProgress ? Resources.Revision > Request.ExpectedRevision
                        : Resources.Revision == Request.ExpectedRevision + 1)))
            && (Result != StrictCombatRebreatherWearResult.Unchanged
                || (CurrentLifeGeneration == Request.LifeGeneration && Resources.IsProvisioned
                    && Resources.IsWorn == Request.IsWorn
                    && (Request.AllowsTimedProgress ? Resources.Revision >= Request.ExpectedRevision
                        : Resources.Revision == Request.ExpectedRevision)));
    }

    /// <summary>Life-bound wear requests and authoritative resource responses.</summary>
    public static class StrictCombatRebreatherWearProtocol
    {
        public const byte Version = 1;
        public const ulong Capability = 1UL << 10;
        public const ulong RequiredCapabilities = StrictCombatRebreatherProtocol.RequiredCapabilities | Capability;
        public const int RequestLength = 96;
        public const int ResponseLength = 168;
        private const uint Magic = 0x5752534D;

        public static bool TryWriteRequest(in StrictCombatRebreatherWearRequest value,
            Span<byte> destination, out int written)
            => TryWriteRequest(in value, destination, false, out written);

        public static bool TryWriteProgressRequest(in StrictCombatRebreatherWearRequest value,
            Span<byte> destination, out int written)
            => TryWriteRequest(in value, destination, true, out written);

        private static bool TryWriteRequest(in StrictCombatRebreatherWearRequest value,
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
            record[6] = value.IsWorn ? (byte)1 : (byte)0;
            var binding = value.Binding;
            if (!StrictCombatProtocol.TryWriteBinding(in binding, record.Slice(8, 56), out int bindingWritten)
                || bindingWritten != StrictCombatProtocol.BindingLength) return false;
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(64), value.Sequence);
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(72), value.LifeGeneration);
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(80), value.ExpectedRevision);
            written = RequestLength;
            return true;
        }

        public static bool TryReadRequest(ReadOnlySpan<byte> source, out StrictCombatRebreatherWearRequest value)
            => TryReadRequest(source, false, out value);

        public static bool TryReadProgressRequest(ReadOnlySpan<byte> source, out StrictCombatRebreatherWearRequest value)
            => TryReadRequest(source, true, out value);

        private static bool TryReadRequest(ReadOnlySpan<byte> source, bool progress, out StrictCombatRebreatherWearRequest value)
        {
            value = default;
            if (source.Length != RequestLength || BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic
                || source[4] != (progress ? StrictCombatRebreatherProgressProtocol.Version : Version) || source[5] != 1 || source[6] > 1 || source[7] != 0
                || BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(88)) != 0
                || !StrictCombatProtocol.TryReadBinding(source.Slice(8, 56), out var binding)) return false;
            var candidate = new StrictCombatRebreatherWearRequest(binding,
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(64)),
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(72)),
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(80)), source[6] == 1, progress);
            if (!candidate.IsValid) return false;
            value = candidate;
            return true;
        }

        public static bool TryWriteResponse(in StrictCombatRebreatherWearResponse value,
            Span<byte> destination, out int written)
            => TryWriteResponse(in value, destination, false, out written);

        public static bool TryWriteProgressResponse(in StrictCombatRebreatherWearResponse value,
            Span<byte> destination, out int written)
            => TryWriteResponse(in value, destination, true, out written);

        private static bool TryWriteResponse(in StrictCombatRebreatherWearResponse value,
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
                record[114] = resources.IsWorn ? (byte)1 : (byte)0;
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

        public static bool TryReadResponse(ReadOnlySpan<byte> source, out StrictCombatRebreatherWearResponse value)
            => TryReadResponse(source, false, out value);

        public static bool TryReadProgressResponse(ReadOnlySpan<byte> source, out StrictCombatRebreatherWearResponse value)
            => TryReadResponse(source, true, out value);

        private static bool TryReadResponse(ReadOnlySpan<byte> source, bool progress, out StrictCombatRebreatherWearResponse value)
        {
            value = default;
            if (source.Length != ResponseLength || BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic
                || source[4] != (progress ? StrictCombatRebreatherProgressProtocol.Version : Version) || source[5] != 2 || source[7] != 0 || source[112] > 1
                || source[114] > 1 || source[115] != 0
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
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(152)), source[114] == 1, out resources)) return false;
            var candidate = new StrictCombatRebreatherWearResponse(request,
                (StrictCombatRebreatherWearResult)source[6],
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(104)), resources);
            if (!candidate.IsValid) return false;
            value = candidate;
            return true;
        }
    }
}
