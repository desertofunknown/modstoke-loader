// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    internal enum ModAdmissionStream : byte
    {
        None = 0,
        Requirements = 1,
        Report = 2
    }

    internal enum ModAdmissionKind : byte
    {
        Data = 1,
        Ack = 2,
        Accepted = 3,
        Rejected = 4
    }

    internal enum ModAdmissionRejection : byte
    {
        None = 0,
        IncompatibleRequirements = 1,
        InvalidMessage = 2,
        Unavailable = 3,
        Expired = 4,
        LocalModsChanged = 5
    }

    internal sealed class ModAdmissionFrame
    {
        internal readonly ModAdmissionKind Kind;
        internal readonly ModAdmissionStream Stream;
        internal readonly ModAdmissionRejection Rejection;
        internal readonly int Offset;
        internal readonly int Total;
        private readonly byte[] _nonce;
        private readonly byte[] _data;

        internal ReadOnlySpan<byte> Nonce => _nonce;
        internal ReadOnlySpan<byte> Data => _data;

        internal ModAdmissionFrame(ModAdmissionKind kind,
            ModAdmissionStream stream, ModAdmissionRejection rejection,
            ReadOnlySpan<byte> nonce, int offset, int total, ReadOnlySpan<byte> data)
        {
            Kind = kind;
            Stream = stream;
            Rejection = rejection;
            Offset = offset;
            Total = total;
            _nonce = nonce.ToArray();
            _data = data.ToArray();
        }
    }

    internal static class ModAdmissionWire
    {
        internal const ushort ProtocolVersion = 1;
        internal const int NonceBytes = 32;
        internal const int HeaderBytes = 48;
        internal const int MaxData = 208;
        // Bounds both the requirements and report streams.
        internal const int MaxDocumentBytes = ModRequirements.MaxDocumentBytes;
        private const uint Magic = 0x414D534D;

        internal static bool MatchesMagic(ReadOnlySpan<byte> payload)
        {
            return payload.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(payload) == Magic;
        }

        internal static bool TryRead(ReadOnlySpan<byte> payload, out ModAdmissionFrame frame)
        {
            frame = null;
            if (payload.Length < HeaderBytes || !MatchesMagic(payload)
                || BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4, 2)) != ProtocolVersion)
                return false;

            var kind = (ModAdmissionKind)payload[6];
            var selector = payload[7];
            var nonce = payload.Slice(8, NonceBytes);
            var offset = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(40, 4));
            var total = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(44, 4));
            var data = payload.Slice(HeaderBytes);
            if (!IsValid(kind, selector, nonce, offset, total, data.Length))
                return false;

            var stream = kind is ModAdmissionKind.Data or ModAdmissionKind.Ack
                ? (ModAdmissionStream)selector : ModAdmissionStream.None;
            var rejection = kind == ModAdmissionKind.Rejected
                ? (ModAdmissionRejection)selector : ModAdmissionRejection.None;
            frame = new ModAdmissionFrame(kind, stream, rejection, nonce, offset, total, data);
            return true;
        }

        internal static byte[] WriteData(ReadOnlySpan<byte> nonce, ModAdmissionStream stream,
            int offset, ReadOnlySpan<byte> document)
        {
            if (document.Length < 1 || document.Length > MaxDocumentBytes
                || offset < 0 || offset >= document.Length || offset % MaxData != 0)
                throw new ArgumentException("Invalid mod admission frame fields.");

            var data = document.Slice(offset, Math.Min(MaxData, document.Length - offset));
            return Write(ModAdmissionKind.Data, (byte)stream, nonce, offset, document.Length, data);
        }

        internal static byte[] WriteAck(ReadOnlySpan<byte> nonce, ModAdmissionStream stream,
            int offset, int total)
        {
            return Write(ModAdmissionKind.Ack, (byte)stream, nonce, offset, total,
                ReadOnlySpan<byte>.Empty);
        }

        internal static byte[] WriteAccepted(ReadOnlySpan<byte> nonce)
        {
            return Write(ModAdmissionKind.Accepted, 0, nonce, 0, 0, ReadOnlySpan<byte>.Empty);
        }

        internal static byte[] WriteRejected(ReadOnlySpan<byte> nonce, ModAdmissionRejection rejection)
        {
            return Write(ModAdmissionKind.Rejected, (byte)rejection, nonce, 0, 0,
                ReadOnlySpan<byte>.Empty);
        }

        private static byte[] Write(ModAdmissionKind kind, byte selector,
            ReadOnlySpan<byte> nonce, int offset, int total, ReadOnlySpan<byte> data)
        {
            if (!IsValid(kind, selector, nonce, offset, total, data.Length))
                throw new ArgumentException("Invalid mod admission frame fields.");

            var payload = new byte[HeaderBytes + data.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), ProtocolVersion);
            payload[6] = (byte)kind;
            payload[7] = selector;
            nonce.CopyTo(payload.AsSpan(8, NonceBytes));
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(40, 4), offset);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(44, 4), total);
            data.CopyTo(payload.AsSpan(HeaderBytes));
            return payload;
        }

        private static bool IsValid(ModAdmissionKind kind, byte selector,
            ReadOnlySpan<byte> nonce, int offset, int total, int dataLength)
        {
            if (nonce.Length != NonceBytes)
                return false;

            switch (kind)
            {
                case ModAdmissionKind.Data:
                    if (!IsStream(selector) || total < 1 || total > MaxDocumentBytes
                        || offset < 0 || offset >= total || offset % MaxData != 0
                        || dataLength != Math.Min(MaxData, total - offset))
                        return false;
                    break;
                case ModAdmissionKind.Ack:
                    if (!IsStream(selector) || total < 1 || total > MaxDocumentBytes
                        || offset < 1 || offset > total || (offset != total && offset % MaxData != 0)
                        || dataLength != 0)
                        return false;
                    break;
                case ModAdmissionKind.Accepted:
                    if (selector != 0 || offset != 0 || total != 0 || dataLength != 0)
                        return false;
                    break;
                case ModAdmissionKind.Rejected:
                    if (selector < (byte)ModAdmissionRejection.IncompatibleRequirements
                        || selector > (byte)ModAdmissionRejection.LocalModsChanged
                        || offset != 0 || total != 0 || dataLength != 0)
                        return false;
                    break;
                default:
                    return false;
            }

            foreach (var value in nonce)
            {
                if (value != 0)
                    return true;
            }
            return false;
        }

        private static bool IsStream(byte selector)
        {
            return selector is (byte)ModAdmissionStream.Requirements
                or (byte)ModAdmissionStream.Report;
        }
    }
}
