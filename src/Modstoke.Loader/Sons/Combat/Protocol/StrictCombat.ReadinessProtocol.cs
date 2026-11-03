// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;

namespace Modstoke.Sons.Combat
{
    public enum StrictCombatReadinessResult : byte
    {
        Accepted, StaleLife, FutureRevision, Incapacitated, Conflict
    }

    public readonly struct StrictCombatReadinessRequest
    {
        public StrictCombatReadinessRequest(StrictCombatBinding binding, Guid requestId,
            ulong lifeGeneration, ulong observedRevision)
        {
            Binding = binding;
            RequestId = requestId;
            LifeGeneration = lifeGeneration;
            ObservedRevision = observedRevision;
        }

        public StrictCombatBinding Binding { get; }
        public Guid RequestId { get; }
        public ulong LifeGeneration { get; }
        public ulong ObservedRevision { get; }
        public bool IsValid => StrictCombatProtocol.IsValidBinding(Binding)
            && RequestId != Guid.Empty && LifeGeneration != 0 && ObservedRevision != 0;

        public bool SameRequest(in StrictCombatReadinessRequest other)
            => Binding.SameBinding(other.Binding)
                && RequestId == other.RequestId && LifeGeneration == other.LifeGeneration
                && ObservedRevision == other.ObservedRevision;
    }

    public readonly struct StrictCombatReadinessResponse
    {
        public StrictCombatReadinessResponse(StrictCombatReadinessRequest request,
            StrictCombatReadinessResult result, ulong currentRevision)
        {
            Request = request;
            Result = result;
            CurrentRevision = currentRevision;
        }

        public StrictCombatReadinessRequest Request { get; }
        public StrictCombatReadinessResult Result { get; }
        public ulong CurrentRevision { get; }
        public bool IsValid => Request.IsValid && Result <= StrictCombatReadinessResult.Conflict
            && CurrentRevision != 0 && (Result != StrictCombatReadinessResult.Accepted
                || CurrentRevision >= Request.ObservedRevision);

        public bool SameResponse(in StrictCombatReadinessResponse other)
            => Request.SameRequest(other.Request) && Result == other.Result
                && CurrentRevision == other.CurrentRevision;
    }

    /// <summary>Optional life-bound readiness requests and authoritative responses; no native gameplay effects.</summary>
    public static class StrictCombatReadinessProtocol
    {
        public const byte Version = 1;
        public const ulong Capability = 1UL << 13;
        public const ulong RequiredCapabilities = StrictCombatProtocol.RequiredCapabilities | Capability;
        public const int RequestLength = 104;
        public const int ResponseLength = 112;
        private const uint Magic = 0x5952534D;
        private const byte RequestKind = 1;
        private const byte ResponseKind = 2;

        public static bool TryWriteRequest(in StrictCombatReadinessRequest value,
            Span<byte> destination, out int written)
        {
            written = 0;
            if (destination.Length < RequestLength || !value.IsValid
                || !TryWriteCommon(destination.Slice(0, RequestLength), RequestKind, value))
                return false;
            written = RequestLength;
            return true;
        }

        public static bool TryReadRequest(ReadOnlySpan<byte> source, out StrictCombatReadinessRequest value)
        {
            value = default;
            if (!TryReadCommon(source, RequestLength, RequestKind, out var request) || source[6] != 0)
                return false;
            value = request;
            return true;
        }

        public static bool TryWriteResponse(in StrictCombatReadinessResponse value,
            Span<byte> destination, out int written)
        {
            written = 0;
            if (destination.Length < ResponseLength || !value.IsValid)
                return false;
            Span<byte> record = destination.Slice(0, ResponseLength);
            if (!TryWriteCommon(record, ResponseKind, value.Request))
                return false;
            record[6] = (byte)value.Result;
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(104), value.CurrentRevision);
            written = ResponseLength;
            return true;
        }

        public static bool TryReadResponse(ReadOnlySpan<byte> source, out StrictCombatReadinessResponse value)
        {
            value = default;
            if (!TryReadCommon(source, ResponseLength, ResponseKind, out var request))
                return false;
            var candidate = new StrictCombatReadinessResponse(request, (StrictCombatReadinessResult)source[6],
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(104)));
            if (!candidate.IsValid)
                return false;
            value = candidate;
            return true;
        }

        private static bool TryWriteCommon(Span<byte> record, byte kind, in StrictCombatReadinessRequest request)
        {
            record.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(record, Magic);
            record[4] = Version;
            record[5] = kind;
            var binding = request.Binding;
            if (!StrictCombatProtocol.TryWriteBinding(in binding, record.Slice(16, 56), out int bindingWritten)
                || bindingWritten != StrictCombatProtocol.BindingLength)
                return false;
            request.RequestId.TryWriteBytes(record.Slice(72, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(88), request.LifeGeneration);
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(96), request.ObservedRevision);
            return true;
        }

        private static bool TryReadCommon(ReadOnlySpan<byte> source, int length, byte kind,
            out StrictCombatReadinessRequest request)
        {
            request = default;
            if (source.Length != length || BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic
                || source[4] != Version || source[5] != kind || !IsZero(source.Slice(7, 9))
                || !StrictCombatProtocol.TryReadBinding(source.Slice(16, 56), out var binding))
                return false;
            var candidate = new StrictCombatReadinessRequest(binding, new Guid(source.Slice(72, 16)),
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(88)),
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(96)));
            if (!candidate.IsValid)
                return false;
            request = candidate;
            return true;
        }

        private static bool IsZero(ReadOnlySpan<byte> bytes)
        {
            foreach (byte value in bytes)
                if (value != 0)
                    return false;
            return true;
        }
    }
}
