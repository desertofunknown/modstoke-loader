// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using UnityEngine;

namespace Modstoke.Sons
{
    public readonly record struct BoltMigrationPlan(Guid Id, ulong Generation,
        ulong OldNetworkId, int PrefabId, int StateTypeId, Guid ContractId, uint ContractVersion)
    {
        public bool IsValid => Id != Guid.Empty && Generation > 0 && OldNetworkId > 0
            && PrefabId >= 0 && StateTypeId >= 0 && ContractId != Guid.Empty && ContractVersion > 0;
    }

    /// <summary>
    /// Explicit server request, not a snapshot or retirement authorization. RequestedPlan.Id is
    /// the request ID; the source allocates its own plan ID when preparing its capture.
    /// Version support must be established by the consuming mode, not inferred from capability 15.
    /// </summary>
    public readonly record struct BoltMigrationPrepareSource(
        BoltMigrationPlan RequestedPlan, ulong RequiredCapabilities)
    {
        public bool IsValid => RequestedPlan.IsValid
            && (RequiredCapabilities == BoltMigrationProtocol.Capability
                || RequiredCapabilities == (BoltMigrationProtocol.Capability
                    | PlayerBridgeProtocol.Capability));

        internal bool Matches(BoltMigrationPlan plan) => plan.IsValid
            && plan.Generation == RequestedPlan.Generation && plan.OldNetworkId == RequestedPlan.OldNetworkId
            && plan.PrefabId == RequestedPlan.PrefabId && plan.StateTypeId == RequestedPlan.StateTypeId
            && plan.ContractId == RequestedPlan.ContractId && plan.ContractVersion == RequestedPlan.ContractVersion;
    }

    public sealed class BoltMigrationSnapshot
    {
        private readonly byte[] _payload;
        private readonly byte[] _digest;

        public BoltMigrationPlan Plan { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }

        // ReadOnlyMemory over our backing arrays would still expose them through MemoryMarshal.
        public ReadOnlyMemory<byte> Payload => _payload.AsSpan().ToArray();
        public ReadOnlyMemory<byte> Digest => _digest.AsSpan().ToArray();
        internal ReadOnlySpan<byte> PayloadSpan => _payload;
        internal ReadOnlySpan<byte> DigestSpan => _digest;

        private BoltMigrationSnapshot(BoltMigrationPlan plan,
            Vector3 position, Quaternion rotation, ReadOnlySpan<byte> payload)
        {
            Plan = plan;
            Position = position;
            Rotation = rotation;
            _payload = payload.ToArray();
            Span<byte> header = stackalloc byte[104];
            BoltMigrationProtocol.WriteSnapshotPrefix(header, plan, position, rotation, _payload.Length);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(header);
            hash.AppendData(_payload);
            _digest = hash.GetHashAndReset();
        }

        internal static bool TryCreate(BoltMigrationPlan plan, Vector3 position,
            Quaternion rotation, ReadOnlySpan<byte> payload, out BoltMigrationSnapshot snapshot)
        {
            snapshot = null;
            if (!plan.IsValid || payload.Length > BoltMigrationProtocol.MaxPayloadBytes
                || !BoltMigrationProtocol.IsValidPose(position, rotation))
                return false;

            snapshot = new BoltMigrationSnapshot(plan, position, rotation, payload);
            return true;
        }

        internal bool MatchesDigest(ReadOnlySpan<byte> digest)
        {
            return CryptographicOperations.FixedTimeEquals(_digest, digest);
        }
    }

    public enum BoltMigrationReceiptKind
    {
        Ready = 1,
        Retired = 2,
        Recreated = 3,
        Repaired = 4,
        Aborted = 5
    }

    /// <summary>Snapshot correlation only, not authentication or proof of native effects.</summary>
    public sealed class BoltMigrationReceipt
    {
        private readonly byte[] _digest;

        public BoltMigrationPlan Plan { get; }
        public BoltMigrationReceiptKind Kind { get; }
        public ulong NewNetworkId { get; }
        public ReadOnlyMemory<byte> Digest => _digest.AsSpan().ToArray();
        internal ReadOnlySpan<byte> DigestSpan => _digest;

        private BoltMigrationReceipt(BoltMigrationPlan plan,
            BoltMigrationReceiptKind kind, ulong newNetworkId, ReadOnlySpan<byte> digest)
        {
            Plan = plan;
            Kind = kind;
            NewNetworkId = newNetworkId;
            _digest = digest.ToArray();
        }

        internal static BoltMigrationReceipt Create(BoltMigrationSnapshot snapshot,
            BoltMigrationReceiptKind kind, ulong newNetworkId = 0)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (!TryCreate(snapshot.Plan, kind, newNetworkId, snapshot.DigestSpan, out var receipt))
                throw new ArgumentException("Receipt kind and new network ID are inconsistent.");

            return receipt;
        }

        internal static bool TryCreate(BoltMigrationPlan plan,
            BoltMigrationReceiptKind kind, ulong newNetworkId, ReadOnlySpan<byte> digest,
            out BoltMigrationReceipt receipt)
        {
            receipt = null;
            if (!plan.IsValid || digest.Length != 32 || !IsValidKindAndId(plan, kind, newNetworkId))
                return false;

            receipt = new BoltMigrationReceipt(plan, kind, newNetworkId, digest);
            return true;
        }

        internal bool Matches(BoltMigrationSnapshot snapshot)
        {
            return snapshot != null && Plan == snapshot.Plan && snapshot.MatchesDigest(_digest);
        }

        private static bool IsValidKindAndId(BoltMigrationPlan plan,
            BoltMigrationReceiptKind kind, ulong newNetworkId)
        {
            return kind switch
            {
                BoltMigrationReceiptKind.Ready
                    or BoltMigrationReceiptKind.Retired
                    or BoltMigrationReceiptKind.Aborted => newNetworkId == 0,
                BoltMigrationReceiptKind.Recreated
                    or BoltMigrationReceiptKind.Repaired => newNetworkId > 0 && newNetworkId != plan.OldNetworkId,
                _ => false
            };
        }
    }

    /// <summary>
    /// Managed wire data only. A receipt digest correlates a snapshot; it does not authenticate a peer.
    /// The caller must authorize the exact peer and transaction before accepting snapshots or receipts.
    /// </summary>
    public static class BoltMigrationProtocol
    {
        public const ulong Capability = 1UL << 15;
        public const byte Version = 1;
        // Snapshot/receipt v1 remains byte-for-byte unchanged. The dispatcher request is v2 only.
        public const byte PrepareSourceVersion = 2;
        public const int PrepareSourceLength = 80;
        public const int MaxPayloadBytes = 1024 * 1024;
        public const int SnapshotHeaderLength = 136;
        public const int ReceiptLength = 112;
        private const uint Magic = 0x4D4F534D;
        private const byte SnapshotKind = 1;
        private const byte ReceiptKind = 2;
        private const byte PrepareSourceKind = 3;

        public static bool TryWritePrepareSource(BoltMigrationPrepareSource request,
            Span<byte> destination, out int written)
        {
            written = 0;
            if (!request.IsValid || destination.Length < PrepareSourceLength) return false;
            var record = destination.Slice(0, PrepareSourceLength);
            WritePlan(record, PrepareSourceKind, request.RequestedPlan, PrepareSourceVersion);
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(68, 8), request.RequiredCapabilities);
            written = PrepareSourceLength;
            return true;
        }

        public static bool TryReadPrepareSource(ReadOnlySpan<byte> source,
            out BoltMigrationPrepareSource request)
        {
            request = default;
            if (source.Length != PrepareSourceLength
                || !TryReadPlan(source, PrepareSourceKind, out var plan, PrepareSourceVersion)
                || BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(76, 4)) != 0) return false;
            var candidate = new BoltMigrationPrepareSource(plan,
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(68, 8)));
            if (!candidate.IsValid) return false;
            request = candidate;
            return true;
        }

        public static bool TryWriteSnapshot(BoltMigrationSnapshot snapshot,
            Span<byte> destination, out int written)
        {
            written = 0;
            if (snapshot == null)
                return false;

            int length = checked(SnapshotHeaderLength + snapshot.PayloadSpan.Length);
            if (destination.Length < length)
                return false;

            WriteSnapshotPrefix(destination.Slice(0, 104), snapshot.Plan, snapshot.Position,
                snapshot.Rotation, snapshot.PayloadSpan.Length);
            snapshot.DigestSpan.CopyTo(destination.Slice(104, 32));
            snapshot.PayloadSpan.CopyTo(destination.Slice(SnapshotHeaderLength, snapshot.PayloadSpan.Length));
            written = length;
            return true;
        }

        public static bool TryReadSnapshot(ReadOnlySpan<byte> source, out BoltMigrationSnapshot snapshot)
        {
            snapshot = null;
            if (source.Length < SnapshotHeaderLength || !TryReadPlan(source, SnapshotKind, out var plan)
                || BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(100, 4)) != 0)
                return false;

            int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(96, 4));
            if (payloadLength < 0 || payloadLength > MaxPayloadBytes
                || source.Length != checked(SnapshotHeaderLength + payloadLength))
                return false;

            var position = new Vector3
            {
                x = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(68, 4)),
                y = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(72, 4)),
                z = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(76, 4))
            };
            var rotation = new Quaternion
            {
                x = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(80, 4)),
                y = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(84, 4)),
                z = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(88, 4)),
                w = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(92, 4))
            };
            if (!BoltMigrationSnapshot.TryCreate(plan, position, rotation,
                    source.Slice(SnapshotHeaderLength, payloadLength), out var candidate)
                || !candidate.MatchesDigest(source.Slice(104, 32)))
                return false;

            snapshot = candidate;
            return true;
        }

        public static bool TryWriteReceipt(BoltMigrationReceipt receipt,
            Span<byte> destination, out int written)
        {
            written = 0;
            if (receipt == null || destination.Length < ReceiptLength)
                return false;

            Span<byte> record = destination.Slice(0, ReceiptLength);
            WritePlan(record, ReceiptKind, receipt.Plan);
            record[68] = (byte)receipt.Kind;
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(72, 8), receipt.NewNetworkId);
            receipt.DigestSpan.CopyTo(record.Slice(80, 32));
            written = ReceiptLength;
            return true;
        }

        public static bool TryReadReceipt(ReadOnlySpan<byte> source, out BoltMigrationReceipt receipt)
        {
            receipt = null;
            if (source.Length != ReceiptLength || !TryReadPlan(source, ReceiptKind, out var plan)
                || source[69] != 0 || source[70] != 0 || source[71] != 0)
                return false;

            return BoltMigrationReceipt.TryCreate(plan,
                (BoltMigrationReceiptKind)source[68],
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(72, 8)), source.Slice(80, 32), out receipt);
        }

        internal static bool IsValidPose(Vector3 position, Quaternion rotation)
        {
            if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)
                || !float.IsFinite(rotation.x) || !float.IsFinite(rotation.y)
                || !float.IsFinite(rotation.z) || !float.IsFinite(rotation.w))
                return false;

            double lengthSquared = (double)rotation.x * rotation.x + (double)rotation.y * rotation.y
                + (double)rotation.z * rotation.z + (double)rotation.w * rotation.w;
            // Keep the original pose bits; allow normal floating-point drift without normalizing on read.
            return double.IsFinite(lengthSquared) && lengthSquared > 0 && Math.Abs(lengthSquared - 1.0) <= 0.001;
        }

        internal static void WriteSnapshotPrefix(Span<byte> header, BoltMigrationPlan plan,
            Vector3 position, Quaternion rotation, int payloadLength)
        {
            WritePlan(header, SnapshotKind, plan);
            BinaryPrimitives.WriteSingleLittleEndian(header.Slice(68, 4), position.x);
            BinaryPrimitives.WriteSingleLittleEndian(header.Slice(72, 4), position.y);
            BinaryPrimitives.WriteSingleLittleEndian(header.Slice(76, 4), position.z);
            BinaryPrimitives.WriteSingleLittleEndian(header.Slice(80, 4), rotation.x);
            BinaryPrimitives.WriteSingleLittleEndian(header.Slice(84, 4), rotation.y);
            BinaryPrimitives.WriteSingleLittleEndian(header.Slice(88, 4), rotation.z);
            BinaryPrimitives.WriteSingleLittleEndian(header.Slice(92, 4), rotation.w);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(96, 4), payloadLength);
        }

        private static void WritePlan(Span<byte> record, byte kind, BoltMigrationPlan plan,
            byte version = Version)
        {
            record.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(record, Magic);
            record[4] = version;
            record[5] = kind;
            plan.Id.TryWriteBytes(record.Slice(8, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(24, 8), plan.Generation);
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(32, 8), plan.OldNetworkId);
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(40, 4), plan.PrefabId);
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(44, 4), plan.StateTypeId);
            plan.ContractId.TryWriteBytes(record.Slice(48, 16));
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(64, 4), plan.ContractVersion);
        }

        private static bool TryReadPlan(ReadOnlySpan<byte> source, byte kind,
            out BoltMigrationPlan plan, byte version = Version)
        {
            plan = default;
            if (source.Length < 68 || BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic
                || source[4] != version || source[5] != kind || source[6] != 0 || source[7] != 0)
                return false;

            var candidate = new BoltMigrationPlan(new Guid(source.Slice(8, 16)),
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(24, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(32, 8)),
                BinaryPrimitives.ReadInt32LittleEndian(source.Slice(40, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(source.Slice(44, 4)),
                new Guid(source.Slice(48, 16)), BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(64, 4)));
            if (!candidate.IsValid)
                return false;

            plan = candidate;
            return true;
        }
    }
}
