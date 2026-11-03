// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Modstoke.Sons
{
    internal static class ControlWire
    {
        internal const int MaxPayloadBytes = 256;
        internal const int BlockHeaderBytes = 20;

        internal static bool TryReadBlock(Il2CppStructArray<byte> buffer, int bytes, int offset, out byte[] payload)
        {
            payload = Array.Empty<byte>();
            if (buffer is null || buffer.WasCollected || bytes < 0 || bytes > buffer.Length
                || offset < 0 || offset > bytes)
                return false;

            var remaining = bytes - offset;
            if (remaining < BlockHeaderBytes)
                return false;

            try
            {
                ReadOnlySpan<byte> source = buffer.AsSpan();
                Span<byte> header = stackalloc byte[BlockHeaderBytes];
                source.Slice(offset, BlockHeaderBytes).CopyTo(header);

                // The first eight bytes are an operation key, not authentication.
                var declaredLength = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8, 4));
                var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12, 4));
                var block = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(16, 4));
                if (declaredLength < 1 || declaredLength > MaxPayloadBytes || block != 0
                    || remaining - BlockHeaderBytes != declaredLength)
                    return false;

                Span<byte> storage = stackalloc byte[MaxPayloadBytes];
                var data = storage.Slice(0, declaredLength);
                source.Slice(offset + BlockHeaderBytes, declaredLength).CopyTo(data);

                if (ComputeCrc(data) != expectedCrc)
                    return false;

                payload = data.ToArray();
                return true;
            }
            finally { GC.KeepAlive(buffer); }
        }

        internal static uint ComputeCrc(ReadOnlySpan<byte> data)
        {
            // Native Crc32.Compute(byte[]) uses reflected CRC-32 with this seed and final complement.
            var crc = uint.MaxValue;
            foreach (var value in data)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc & 1u) == 0 ? crc >> 1 : (crc >> 1) ^ 0xEDB88320u;
            }
            return ~crc;
        }
    }
}
