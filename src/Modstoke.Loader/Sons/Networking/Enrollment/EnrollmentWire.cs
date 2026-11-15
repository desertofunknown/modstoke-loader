// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;

namespace Modstoke.Sons
{
    internal enum EnrollmentMessageKind : byte
    {
        Challenge = 1,
        Response = 2,
        Accepted = 3,
        Rejected = 4,
        // Version 2: the client opens with Hello, and the host releases its gameplay with Admitted.
        Hello = 5,
        Admitted = 6
    }

    internal enum EnrollmentRejection : byte
    {
        None = 0,
        IncompatibleCapabilities = 1,
        InvalidMessage = 2,
        Expired = 3,
        NotParticipating = 4
    }

    internal sealed class EnrollmentFrame
    {
        internal readonly EnrollmentMessageKind Kind;
        internal readonly ulong RequiredCapabilities;
        internal readonly ulong OfferedCapabilities;
        internal readonly EnrollmentRejection Rejection;
        private readonly byte[] _nonce;

        internal ReadOnlySpan<byte> Nonce => _nonce;

        internal EnrollmentFrame(EnrollmentMessageKind kind,
            ReadOnlySpan<byte> nonce, ulong requiredCapabilities, ulong offeredCapabilities,
            EnrollmentRejection rejection)
        {
            Kind = kind;
            _nonce = nonce.ToArray();
            RequiredCapabilities = requiredCapabilities;
            OfferedCapabilities = offeredCapabilities;
            Rejection = rejection;
        }
    }

    internal static class EnrollmentWire
    {
        // Version 1 peers registered the channel ahead of the game's and had the host speak first; the two cannot
        // share a channel, so version 2 is told apart before Bolt starts (see ContentAgreement.Fingerprint).
        internal const ushort ProtocolVersion = 2;
        internal const int NonceBytes = 32;
        internal const int FrameBytes = 56;
        private const uint Magic = 0x4E45534D;

        internal static bool MatchesMagic(ReadOnlySpan<byte> payload)
            => payload.Length >= 6 && BinaryPrimitives.ReadUInt32LittleEndian(payload) == Magic;

        internal static ushort ReadVersion(ReadOnlySpan<byte> payload)
            => BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4, 2));

        internal static bool IsKind(ReadOnlySpan<byte> payload, EnrollmentMessageKind kind)
            => payload.Length == FrameBytes && MatchesMagic(payload) && ReadVersion(payload) == ProtocolVersion
                && payload[6] == (byte)kind;

        internal static bool TryRead(ReadOnlySpan<byte> payload, out EnrollmentFrame frame)
        {
            frame = null;
            if (payload.Length != FrameBytes || !MatchesMagic(payload) || ReadVersion(payload) != ProtocolVersion)
                return false;

            var kind = (EnrollmentMessageKind)payload[6];
            var rejection = (EnrollmentRejection)payload[7];
            var nonce = payload.Slice(8, NonceBytes);
            var requiredCapabilities = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(40, 8));
            var offeredCapabilities = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(48, 8));
            if (!IsValid(kind, nonce, requiredCapabilities, offeredCapabilities, rejection))
                return false;

            frame = new EnrollmentFrame(kind, nonce, requiredCapabilities,
                offeredCapabilities, rejection);
            return true;
        }

        internal static byte[] Write(EnrollmentMessageKind kind, ReadOnlySpan<byte> nonce,
            ulong requiredCapabilities, ulong offeredCapabilities,
            EnrollmentRejection rejection = EnrollmentRejection.None)
        {
            if (!IsValid(kind, nonce, requiredCapabilities, offeredCapabilities, rejection))
                throw new ArgumentException("Invalid enrollment frame fields.");

            var payload = new byte[FrameBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), ProtocolVersion);
            payload[6] = (byte)kind;
            payload[7] = (byte)rejection;
            nonce.CopyTo(payload.AsSpan(8, NonceBytes));
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(40, 8), requiredCapabilities);
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(48, 8), offeredCapabilities);
            return payload;
        }

        private static bool IsValid(EnrollmentMessageKind kind, ReadOnlySpan<byte> nonce,
            ulong requiredCapabilities, ulong offeredCapabilities, EnrollmentRejection rejection)
        {
            if (nonce.Length != NonceBytes)
                return false;

            switch (kind)
            {
                case EnrollmentMessageKind.Challenge:
                    if (offeredCapabilities != 0 || rejection != EnrollmentRejection.None)
                        return false;
                    break;
                case EnrollmentMessageKind.Hello:
                    if (requiredCapabilities != 0 || rejection != EnrollmentRejection.None)
                        return false;
                    break;
                case EnrollmentMessageKind.Response:
                case EnrollmentMessageKind.Accepted:
                case EnrollmentMessageKind.Admitted:
                    if (rejection != EnrollmentRejection.None)
                        return false;
                    break;
                case EnrollmentMessageKind.Rejected:
                    if (offeredCapabilities != 0
                        || rejection is not (EnrollmentRejection.IncompatibleCapabilities
                            or EnrollmentRejection.InvalidMessage
                            or EnrollmentRejection.Expired
                            or EnrollmentRejection.NotParticipating))
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
    }
}
