// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UdpKit;

namespace Modstoke.Sons
{
    /// <summary>Copies a whole payload into or out of a Bolt packet in one native call.</summary>
    /// <remarks>
    /// Each <see cref="UdpPacket"/> call crosses into native code, so writing a payload a byte at a time costs one call
    /// per byte: a few hundred per datagram, tens of thousands a second for a busy stream. These go through one reused
    /// native buffer instead. Game thread only, like the packets themselves.
    /// </remarks>
    public static class PacketPayloads
    {
        private static Il2CppStructArray<byte> _native;

        /// <summary>Writes <paramref name="bytes"/> at the packet's current position, as WriteByte would one by one.</summary>
        public static void Write(UdpPacket packet, ReadOnlySpan<byte> bytes)
        {
            ArgumentNullException.ThrowIfNull(packet);
            if (bytes.IsEmpty) return;
            var native = Buffer(bytes.Length);
            bytes.CopyTo(native.AsSpan());
            packet.WriteByteArray(native, 0, bytes.Length);
        }

        /// <summary>Fills <paramref name="into"/> from the packet's current position, as ReadByte would one by one.</summary>
        public static void Read(UdpPacket packet, Span<byte> into)
        {
            ArgumentNullException.ThrowIfNull(packet);
            if (into.IsEmpty) return;
            var native = Buffer(into.Length);
            packet.ReadByteArray(native, into.Length);
            native.AsSpan()[..into.Length].CopyTo(into);
        }

        private static Il2CppStructArray<byte> Buffer(int length)
        {
            if (_native == null || _native.Length < length) _native = new Il2CppStructArray<byte>(Math.Max(length, 2048));
            return _native;
        }
    }
}
