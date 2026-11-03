// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;

namespace Modstoke.Sons
{
    internal sealed partial class ModuleStorageTransport
    {
        private const uint Magic = 0x544D534D; // MSMT, exclusively module-storage transport.
        private const byte Version = 1;
        private const int HeaderBytes = 32;
        private const int FragmentBytes = Datagrams.MaxPayloadBytes - HeaderBytes;
        private const int FingerprintBytes = 32;

        private enum FrameKind : byte { Data = 1, Ack = 2, Complete = 3 }

        private readonly record struct Header(FrameKind Kind, ulong Id, int Length, int Index, uint Mask)
        {
            internal int Count => FragmentCount(Length);
        }

        private static int FragmentCount(int length) => (length + FragmentBytes - 1) / FragmentBytes;
        private static int FragmentLength(int length, int index)
            => Math.Min(FragmentBytes, length - index * FragmentBytes);

        private static bool MatchesMagic(ReadOnlySpan<byte> bytes)
            => bytes.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == Magic;

        private static bool TryReadHeader(ReadOnlySpan<byte> bytes, out Header header)
        {
            header = default;
            if (bytes.Length < HeaderBytes || bytes.Length > Datagrams.MaxPayloadBytes
                || !MatchesMagic(bytes) || bytes[4] != Version
                || BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(6)) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(28)) != 0)
                return false;
            var kind = (FrameKind)bytes[5];
            ulong id = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(8));
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(16));
            int index = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(20));
            uint mask = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(24));
            if (id == 0 || length < 1 || length > MaxPayloadBytes || index < 0) return false;
            int count = FragmentCount(length);
            switch (kind)
            {
                case FrameKind.Data:
                    if (index >= count || mask != 0
                        || bytes.Length != HeaderBytes + FragmentLength(length, index)) return false;
                    break;
                case FrameKind.Ack:
                    if (bytes.Length != HeaderBytes || index > count) return false;
                    int remaining = count - index;
                    if (remaining < WindowFragments && (mask >> remaining) != 0) return false;
                    break;
                case FrameKind.Complete:
                    if (bytes.Length != HeaderBytes || index != count || mask != 0) return false;
                    break;
                default:
                    return false;
            }
            header = new Header(kind, id, length, index, mask);
            return true;
        }

        private static void WriteHeader(Span<byte> bytes, Header header)
        {
            bytes.Slice(0, HeaderBytes).Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
            bytes[4] = Version;
            bytes[5] = (byte)header.Kind;
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(8), header.Id);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(16), header.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(20), header.Index);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(24), header.Mask);
        }
    }
}
