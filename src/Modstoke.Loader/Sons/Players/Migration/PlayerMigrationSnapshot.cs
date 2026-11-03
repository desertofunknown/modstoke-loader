// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;

namespace Modstoke.Sons
{
    /// <summary>
    /// Immutable, correlated player-state, animation, native motion, survival and inventory observations for one migration.
    /// Decoding validates content, not possession or server approval. Native lifecycle admission
    /// and explicit approval of Inventory.Fingerprint must precede source retirement.
    /// Motion parameters are observations too: inventory approval does not authorize client physics.
    /// </summary>
    public sealed class PlayerMigrationSnapshot
    {
        private const uint Magic = 0x32504D41;
        private const uint Version = 7;
        private const int HeaderLength = 44;
        private readonly byte[] _state;
        private readonly byte[] _animation;
        private readonly byte[] _encoded;

        public BoltMigrationPlan Plan => Inventory.Plan;
        public PlayerInventoryBaseline Inventory { get; }
        public PlayerCarrierProfile CarrierProfile { get; }
        public PlayerSurvivalSnapshot Survival { get; }
        public PlayerHeldOnlyAuthority.Snapshot HeldOnly { get; }
        public PlayerArmourAuthority.Snapshot Armour { get; }
        public byte[] Export() => (byte[])_encoded.Clone();
        public byte[] ExportPlayerState() => (byte[])_state.Clone();
        public byte[] ExportAnimationState() => (byte[])_animation.Clone();
        internal ReadOnlySpan<byte> PlayerState => _state;
        internal ReadOnlySpan<byte> AnimationState => _animation;
        internal ReadOnlySpan<byte> Encoded => _encoded;

        private PlayerMigrationSnapshot(PlayerInventoryBaseline inventory,
            PlayerCarrierProfile carrierProfile, PlayerSurvivalSnapshot survival,
            PlayerHeldOnlyAuthority.Snapshot heldOnly,
            PlayerArmourAuthority.Snapshot armour,
            ReadOnlySpan<byte> state, ReadOnlySpan<byte> animation, ReadOnlySpan<byte> inventoryBytes,
            ReadOnlySpan<byte> profileBytes, ReadOnlySpan<byte> survivalBytes,
            ReadOnlySpan<byte> exposureBytes, ReadOnlySpan<byte> waterBytes, ReadOnlySpan<byte> heldOnlyBytes,
            ReadOnlySpan<byte> armourBytes)
        {
            Inventory = inventory;
            CarrierProfile = carrierProfile;
            Survival = survival;
            HeldOnly = heldOnly;
            Armour = armour;
            _state = state.ToArray();
            _animation = animation.ToArray();
            byte[] lifeBytes = survival.LifeAdmission.Export();
            _encoded = new byte[checked(HeaderLength + state.Length + animation.Length + inventoryBytes.Length
                + profileBytes.Length + survivalBytes.Length + lifeBytes.Length + exposureBytes.Length + waterBytes.Length
                + heldOnlyBytes.Length + armourBytes.Length)];
            var bytes = _encoded.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(4), Version);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(8), state.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(12), animation.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(16), inventoryBytes.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(20), profileBytes.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(24), survivalBytes.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(28), exposureBytes.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(32), waterBytes.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(36), heldOnlyBytes.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(40), armourBytes.Length);
            state.CopyTo(bytes.Slice(HeaderLength));
            animation.CopyTo(bytes.Slice(HeaderLength + state.Length));
            inventoryBytes.CopyTo(bytes.Slice(HeaderLength + state.Length + animation.Length));
            profileBytes.CopyTo(bytes.Slice(HeaderLength + state.Length + animation.Length + inventoryBytes.Length));
            survivalBytes.CopyTo(bytes.Slice(HeaderLength + state.Length + animation.Length + inventoryBytes.Length + profileBytes.Length));
            int lifeOffset = HeaderLength + state.Length + animation.Length + inventoryBytes.Length + profileBytes.Length + survivalBytes.Length;
            lifeBytes.CopyTo(bytes.Slice(lifeOffset));
            exposureBytes.CopyTo(bytes.Slice(lifeOffset + lifeBytes.Length));
            waterBytes.CopyTo(bytes.Slice(lifeOffset + lifeBytes.Length + exposureBytes.Length));
            heldOnlyBytes.CopyTo(bytes.Slice(lifeOffset + lifeBytes.Length + exposureBytes.Length + waterBytes.Length));
            armourBytes.CopyTo(bytes.Slice(lifeOffset + lifeBytes.Length + exposureBytes.Length + waterBytes.Length + heldOnlyBytes.Length));
        }

        /// <summary>
        /// Capture the profile's resume values immediately before acquiring the maintained native
        /// pause; capture state/animation/inventory under that same hold. Do not serialize temporary
        /// kinematic, collision-mode or zero-velocity values introduced by quiescence.
        /// </summary>
        public static bool TryCreate(BoltMigrationPlan plan, ReadOnlySpan<byte> playerState,
            ReadOnlySpan<byte> animationState, PlayerInventoryBaseline inventory,
            PlayerCarrierProfile carrierProfile,
            PlayerSurvivalSnapshot survival,
            PlayerHeldOnlyAuthority.Snapshot heldOnly,
            PlayerArmourAuthority.Snapshot armour,
            out PlayerMigrationSnapshot snapshot, out string detail)
        {
            snapshot = null;
            detail = "Complete observations for the exact player migration are required.";
            if (!plan.IsValid || inventory == null || inventory.Plan != plan
                || heldOnly == null || heldOnly.Plan != plan
                || armour == null || !armour.MatchesInventory(inventory)
                || !ProfileMatches(plan, carrierProfile, out detail)
                || !SurvivalMatches(plan, survival, out detail) || survival.LifeAdmission?.Matches(survival) != true
                || survival.ExposureBaseline == null || survival.WaterContacts == null
                || !PlayerPreservation.TryValidateMigrationState(plan, playerState,
                    animationState, out detail)) return false;
            byte[] inventoryBytes = inventory.Export();
            byte[] survivalBytes = survival.Export();
            byte[] exposureBytes = survival.ExposureBaseline.Export();
            byte[] heldOnlyBytes = heldOnly.Export();
            byte[] armourBytes = armour.Export();
            Span<byte> profileBytes = stackalloc byte[PlayerCarrierProfile.EncodedLength];
            if (!carrierProfile.TryEncode(profileBytes, out int written) || written != profileBytes.Length)
            { detail = "The complete native motion profile could not be encoded."; return false; }
            long fixedBytes = HeaderLength + (long)playerState.Length + animationState.Length + inventoryBytes.Length
                + profileBytes.Length + survivalBytes.Length + PlayerLifeAdmission.EncodedLength
                + exposureBytes.Length + heldOnlyBytes.Length + armourBytes.Length;
            if (fixedBytes >= BoltMigrationProtocol.MaxPayloadBytes
                || !survival.WaterContacts.TryExportForEnvelope((int)(BoltMigrationProtocol.MaxPayloadBytes - fixedBytes), out var waterBytes))
            {
                detail = "The complete environment history exceeds the remaining migration payload bound; nothing was omitted.";
                return false;
            }
            if (!ValidLengths(playerState.Length, animationState.Length, inventoryBytes.Length, profileBytes.Length, survivalBytes.Length,
                exposureBytes.Length, waterBytes.Length, heldOnlyBytes.Length, armourBytes.Length, fixedBytes + waterBytes.Length))
            {
                detail = "The complete player capture exceeds the reliable migration payload bound; nothing was truncated.";
                return false;
            }
            snapshot = new(inventory, carrierProfile, survival, heldOnly, armour, playerState, animationState, inventoryBytes, profileBytes, survivalBytes,
                exposureBytes, waterBytes, heldOnlyBytes, armourBytes);
            detail = "Player, animation, motion, survival, environment history and inventory correlated; explicit server approval is still required.";
            return true;
        }

        public static bool TryRead(BoltMigrationSnapshot migration,
            out PlayerMigrationSnapshot snapshot, out string detail)
        {
            snapshot = null;
            detail = "A correlated player migration snapshot is required.";
            if (migration == null || !migration.Plan.IsValid) return false;
            var bytes = migration.PayloadSpan;
            if (bytes.Length < HeaderLength
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4)) != Version) return false;
            int stateLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(8));
            int animationLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(12));
            int inventoryLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(16));
            int profileLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(20));
            int survivalLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(24));
            int exposureLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(28));
            int waterLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(32));
            int heldOnlyLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(36));
            int armourLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(40));
            if (!ValidLengths(stateLength, animationLength, inventoryLength, profileLength, survivalLength,
                exposureLength, waterLength, heldOnlyLength, armourLength, bytes.Length)) return false;
            var state = bytes.Slice(HeaderLength, stateLength);
            var animation = bytes.Slice(HeaderLength + stateLength, animationLength);
            var inventoryBytes = bytes.Slice(HeaderLength + stateLength + animationLength, inventoryLength);
            var profileBytes = bytes.Slice(HeaderLength + stateLength + animationLength + inventoryLength, profileLength);
            var survivalBytes = bytes.Slice(HeaderLength + stateLength + animationLength + inventoryLength + profileLength, survivalLength);
            int lifeOffset = HeaderLength + stateLength + animationLength + inventoryLength + profileLength + survivalLength;
            var lifeBytes = bytes.Slice(lifeOffset, PlayerLifeAdmission.EncodedLength);
            var exposureBytes = bytes.Slice(lifeOffset + lifeBytes.Length, exposureLength);
            var waterBytes = bytes.Slice(lifeOffset + lifeBytes.Length + exposureLength, waterLength);
            var heldOnlyBytes = bytes.Slice(lifeOffset + lifeBytes.Length + exposureLength + waterLength, heldOnlyLength);
            var armourBytes = bytes.Slice(lifeOffset + lifeBytes.Length + exposureLength + waterLength + heldOnlyLength, armourLength);
            if (!PlayerWaterContactSnapshot.TryImport(waterBytes, out var water))
            { detail = "The native water contact observation is malformed or exceeds its bounds."; return false; }
            if (!PlayerPreservation.TryValidateMigrationState(migration.Plan, state, animation, out detail)
                || !PlayerInventoryBaseline.TryReadObservation(migration.Plan, inventoryBytes.ToArray(),
                    out var inventory, out detail)
                || !PlayerCarrierProfile.TryDecode(profileBytes, out var profile, out detail)
                || !ProfileMatches(migration.Plan, profile, out detail)
                || !PlayerSurvivalSnapshot.TryDecode(survivalBytes, out var survival, out detail)
                || !SurvivalMatches(migration.Plan, survival, out detail)
                || !PlayerLifeAdmission.TryDecode(lifeBytes, out var life)
                || !survival.TryBindLifeAdmission(life)
                || !PlayerExposureSnapshot.TryImport(exposureBytes.ToArray(), out var exposure, out detail)
                || !survival.TryBindExposureBaseline(exposure, out detail)
                || !survival.TryBindWaterContacts(water, out detail)
                || !PlayerHeldOnlyAuthority.Snapshot.TryReadObservation(migration.Plan,
                    heldOnlyBytes.ToArray(), out var heldOnly, out detail)
                || !PlayerArmourAuthority.Snapshot.TryReadObservation(migration.Plan,
                    inventory, armourBytes.ToArray(), out var armour, out detail)) return false;
            // The accepted fingerprint names the canonical inventory bytes that actually travelled.
            // Reject alternate encodings rather than approving a normalized but different payload.
            if (!inventory.Export().AsSpan().SequenceEqual(inventoryBytes))
            {
                detail = "Inventory observation is not in the canonical form used by its approval fingerprint.";
                return false;
            }
            Span<byte> canonicalProfile = stackalloc byte[PlayerCarrierProfile.EncodedLength];
            if (!profile.TryEncode(canonicalProfile, out int written) || written != canonicalProfile.Length
                || !canonicalProfile.SequenceEqual(profileBytes))
            { detail = "The native motion profile is not canonically encoded."; return false; }
            if (!survival.Export().AsSpan().SequenceEqual(survivalBytes))
            { detail = "The survival observation is not canonically encoded."; return false; }
            if (!exposure.Export().AsSpan().SequenceEqual(exposureBytes) || !water.Export().AsSpan().SequenceEqual(waterBytes)
                || !life.Export().AsSpan().SequenceEqual(lifeBytes)
                || !heldOnly.Export().AsSpan().SequenceEqual(heldOnlyBytes)
                || !armour.Export().AsSpan().SequenceEqual(armourBytes))
            { detail = "The environment, life, held-item or armour observation is not canonically encoded."; return false; }
            snapshot = new(inventory, profile, survival, heldOnly, armour, state, animation, inventoryBytes, profileBytes, survivalBytes,
                exposureBytes, waterBytes, heldOnlyBytes, armourBytes);
            detail = "Complete correlated observations decoded; no ownership or inventory approval was granted.";
            return true;
        }

        private static bool ProfileMatches(BoltMigrationPlan plan,
            PlayerCarrierProfile profile, out string detail)
        {
            if (!PlayerCarrierProfile.ValidateProfile(profile, out detail)) return false;
            if (profile.SourceNetworkId != plan.OldNetworkId || profile.PrefabId != plan.PrefabId
                || profile.StateTypeId != plan.StateTypeId)
            { detail = "The native motion profile belongs to a different player or schema."; return false; }
            return true;
        }

        private static bool SurvivalMatches(BoltMigrationPlan plan,
            PlayerSurvivalSnapshot survival, out string detail)
        {
            detail = "The survival observation belongs to a different player or schema.";
            return survival != null && survival.SourceNetworkId == plan.OldNetworkId
                && survival.PrefabId == plan.PrefabId && survival.StateTypeId == plan.StateTypeId;
        }

        private static bool ValidLengths(int state, int animation, int inventory, int profile, int survival,
            int exposure, int water, int heldOnly, int armour, long total)
            => state is > 0 and <= PlayerPreservation.MaximumPayloadBytes
                && animation is > 0 and <= PlayerPreservation.MaximumPayloadBytes
                && inventory is > 0 and <= PlayerInventoryBaseline.MaximumBytes
                && profile == PlayerCarrierProfile.EncodedLength
                && survival is > 0 and <= BoltMigrationProtocol.MaxPayloadBytes
                && exposure is > 0 and <= PlayerExposureSnapshot.MaximumBytes
                && water is > 0 and <= PlayerWaterContactSnapshot.MaximumExportBytes
                && heldOnly is > 0 and <= PlayerHeldOnlyAuthority.Snapshot.MaximumBytes
                && armour is > 0 and <= PlayerArmourAuthority.Snapshot.MaximumBytes
                && total == HeaderLength + (long)state + animation + inventory + profile + survival
                    + PlayerLifeAdmission.EncodedLength + exposure + water + heldOnly + armour
                && total <= BoltMigrationProtocol.MaxPayloadBytes;
    }
}
