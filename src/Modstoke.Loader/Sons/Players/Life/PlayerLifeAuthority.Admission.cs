// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Modstoke.Loader;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerSurvivalSnapshot
    {
        /// <summary>The correlated life observation evaluated by the existing survival approval policy.</summary>
        public PlayerLifeAdmission LifeAdmission { get; private set; }

        internal bool TryBindLifeAdmission(PlayerLifeAdmission admission)
        {
            if (admission == null || !admission.Matches(this)) return false;
            if (LifeAdmission != null)
                return LifeAdmission.Export().AsSpan().SequenceEqual(admission.Export());
            LifeAdmission = admission;
            return true;
        }
    }

    /// <summary>
    /// Source observation, not authorization. Transport alongside the survival baseline;
    /// the server must approve both together before passing this to life preparation.
    /// HitDetection is absent from the survival payload and cannot be inferred from collider enablement.
    /// </summary>
    public sealed class PlayerLifeAdmission
    {
        public const int EncodedLength = 48;
        private const uint Magic = 0x31464C50; // PLF1
        private readonly byte[] _survivalDigest;
        public ulong SourceNetworkId { get; }
        public bool HitDetection { get; }

        private PlayerLifeAdmission(ulong source, bool hitDetection, byte[] digest)
        { SourceNetworkId = source; HitDetection = hitDetection; _survivalDigest = digest; }

        public static bool TryCapture(BoltEntity source, PlayerLifecycle lifecycle,
            PlayerSurvivalSnapshot survival, out PlayerLifeAdmission admission,
            out string detail)
        {
            admission = null;
            detail = "The exact held local source and its survival baseline are required.";
            if (!GameBuild.IsRecognized || !PlayerRoles.IsAvailable
                || lifecycle?.IsPaused != true || survival == null) return false;
            try
            {
                int frame = Time.frameCount;
                var script = LocalPlayer.ScriptSetup;
                var stats = LocalPlayer.Stats;
                if (!source || source.WasCollected || !source.isAttached || !source.isOwner
                    || lifecycle.OriginalEntity?.Pointer != source.Pointer
                    || LocalPlayer.Entity?.Pointer != source.Pointer
                    || source.networkId.PackedValue != survival.SourceNetworkId
                    || !script || script.WasCollected || !stats || stats.WasCollected
                    || !script.transform.IsChildOf(source.transform) || !stats.transform.IsChildOf(source.transform)
                    || stats._healing || stats._isHealing
                    || !PlayerAuthorityDynamics.TryCaptureRuntimeState(stats, lifecycle, out var current, out detail)
                    || current != survival.Runtime)
                    return false;
                bool hitDetection = script._HitDetection_k__BackingField;
                var candidate = new PlayerLifeAdmission(survival.SourceNetworkId,
                    hitDetection, SHA256.HashData(survival.Export()));
                if (frame != Time.frameCount || !lifecycle.IsPaused
                    || LocalPlayer.ScriptSetup?.Pointer != script.Pointer
                    || LocalPlayer.Stats?.Pointer != stats.Pointer
                    || LocalPlayer.Entity?.Pointer != source.Pointer
                    || script._HitDetection_k__BackingField != hitDetection) return false;
                admission = candidate;
                detail = "Exact source hit-detection observation captured and tied to the survival payload; server approval is still required.";
                return true;
            }
            catch (Exception error) { detail = "Life admission capture refused: " + error.Message; return false; }
        }

        public byte[] Export()
        {
            var bytes = new byte[EncodedLength];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), HitDetection ? 1u : 0u);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), SourceNetworkId);
            _survivalDigest.CopyTo(bytes, 16);
            return bytes;
        }

        /// <summary>Decoding establishes structure only, never authentication or server policy.</summary>
        public static bool TryDecode(ReadOnlySpan<byte> bytes, out PlayerLifeAdmission admission)
        {
            admission = null;
            if (bytes.Length != EncodedLength || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4)) > 1
                || BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(8)) == 0) return false;
            admission = new(BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(8)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4)) != 0, bytes.Slice(16, 32).ToArray());
            return true;
        }

        internal bool Matches(PlayerSurvivalSnapshot survival)
            => survival != null && SourceNetworkId == survival.SourceNetworkId
                && CryptographicOperations.FixedTimeEquals(_survivalDigest, SHA256.HashData(survival.Export()));
    }
}
