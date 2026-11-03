// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Text;
using Il2CppInterop.Runtime;
using Modstoke.Loader;
using global::Sons.Physics;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>Native sensor values. IgnoreInCave is NOT the SDK's IgnoreBelowTerrain.</summary>
    public enum PlayerNativeOceanMode
    {
        Float = 0,
        Ignore = 1,
        IgnoreInCave = 2
    }

    /// <summary>
    /// Immutable source observations, not server water policy. A null RequiredWaterTag
    /// represents the native null/empty whitelist; a multi-entry whitelist is unsupported.
    /// No server carrier or host LocalPlayer is consulted when composing settings.
    /// </summary>
    public sealed class PlayerWaterConfiguration
    {
        internal const int MaximumTagBytes = 128;
        internal const int EncodedLength = 40 + MaximumTagBytes;
        private static readonly UTF8Encoding TagEncoding = new(false, true);

        public int WaterLayerMask { get; }
        public int BlockerLayerMask { get; }
        public int WaterAndBlockerLayerMask { get; }
        public string RequiredWaterTag { get; }
        public PlayerNativeOceanMode NativeOceanMode { get; }
        public float ObjectWidth { get; }
        public float WaterlineOffset { get; }
        public float SwimEnterOffset { get; }
        public float SwimExitOffset { get; }
        /// <summary>Seconds after a positive native _disableSwimmingTimer exit timestamp.</summary>
        public float ForceDisableSwimmingAfter { get; }

        private PlayerWaterConfiguration(int waterMask, int blockerMask, int combinedMask,
            string tag, PlayerNativeOceanMode ocean, float width, float waterline,
            float enter, float exit, float disableAfter)
        {
            WaterLayerMask = waterMask; BlockerLayerMask = blockerMask;
            WaterAndBlockerLayerMask = combinedMask; RequiredWaterTag = tag;
            NativeOceanMode = ocean; ObjectWidth = width; WaterlineOffset = waterline;
            SwimEnterOffset = enter; SwimExitOffset = exit; ForceDisableSwimmingAfter = disableAfter;
        }

        internal static bool TryCapture(BoltEntity source, PlayerLifecycle lifecycle,
            FirstPersonCharacter character, PlayerAnimatorControl controller, PlayerInventory inventory,
            out PlayerWaterConfiguration configuration, out WaterLevelSensor sensor, out string detail)
        {
            configuration = null;
            sensor = null;
            detail = "Water configuration requires the exact paused source actor's native sensor binding.";
            if (!PlayerRoles.IsAvailable || !GameBuild.IsRecognized) return false;
            sensor = LocalPlayer._WaterLevelSensor_k__BackingField;
            if (!SourceBound(source, lifecycle, character, controller, inventory, sensor)) return false;
            if (!TryRead(controller, sensor, out var candidate, out detail)) return false;
            if (!candidate.IsCurrentSource(source, lifecycle, character, controller, inventory, sensor))
            { detail = "The source actor's water configuration changed during capture."; return false; }
            configuration = candidate;
            detail = "Exact source water masks, tag policy, ocean mode, width and thresholds captured; server approval remains required.";
            return true;
        }

        internal bool IsCurrentSource(BoltEntity source, PlayerLifecycle lifecycle,
            FirstPersonCharacter character, PlayerAnimatorControl controller, PlayerInventory inventory,
            WaterLevelSensor sensor)
            => SourceBound(source, lifecycle, character, controller, inventory, sensor)
                && TryRead(controller, sensor, out var current, out _) && SameConfiguration(current);

        private static bool SourceBound(BoltEntity source, PlayerLifecycle lifecycle,
            FirstPersonCharacter character, PlayerAnimatorControl controller, PlayerInventory inventory,
            WaterLevelSensor sensor)
        {
            if (!Alive(source) || !source.isAttached || !source.isOwner
                || source.prefabId != BoltPrefabs.PlayerNetwork || PlayerRoles.IsCurrentCarrier(source)
                || !Alive(character) || !Alive(controller) || !Alive(inventory) || !Alive(sensor)
                || !sensor.enabled || !sensor.gameObject.activeInHierarchy
                || IL2CPP.il2cpp_object_get_class(controller.Pointer) != Il2CppClassPointerStore<PlayerAnimatorControl>.NativeClassPtr
                || IL2CPP.il2cpp_object_get_class(sensor.Pointer) != Il2CppClassPointerStore<WaterLevelSensor>.NativeClassPtr)
                return false;
            if (lifecycle != null)
            {
                if (!lifecycle.IsPaused || !Same(lifecycle.OriginalEntity, source)) return false;
            }
            else if (character.enabled || controller.enabled) return false;
            if (!PlayerLifecycle.IsInventoryIngressFenced(inventory, out bool frozen) || !frozen)
                return false;

            // Native PAC.GetWaterLevel reads this singleton, not a sensor selected by name.
            // Admit it only on the source machine and only with this exact actor chain.
            var local = LocalPlayer._instance;
            var root = source.transform;
            return Alive(local) && Same(LocalPlayer._Entity_k__BackingField, source)
                && Same(LocalPlayer._AnimControl_k__BackingField, controller)
                && Same(LocalPlayer._WaterLevelSensor_k__BackingField, sensor)
                && Same(local._animControl, controller) && Same(local._fpc, character)
                && Same(local._inventory, inventory) && Same(local._waterLevelSensor, sensor)
                && Same(local.GetComponentInParent<BoltEntity>(true), source)
                && Same(character.GetComponentInParent<BoltEntity>(true), source)
                && Same(inventory.GetComponentInParent<BoltEntity>(true), source)
                && Same(controller.GetComponentInParent<BoltEntity>(true), source)
                && Same(sensor.GetComponentInParent<BoltEntity>(true), source)
                && Same(controller._transform, controller.transform)
                && Same(controller.rootTr, root) && Same(controller._transform.root, root);
        }

        private static bool TryRead(PlayerAnimatorControl controller, WaterLevelSensor sensor,
            out PlayerWaterConfiguration configuration, out string detail)
        {
            configuration = null;
            detail = "The native water settings are unavailable or outside the bounded capture format.";
            var tags = sensor._tagFilter;
            if (tags != null && (tags.WasCollected || tags.Count < 0)) return false;
            if (tags != null && tags.Count > 1)
            { detail = "The native water tag whitelist has multiple entries; the SDK single-tag query cannot represent it."; return false; }
            string tag = tags == null || tags.Count == 0 ? null : tags[0];
            if (tags != null && tags.Count == 1 && string.IsNullOrEmpty(tag))
            { detail = "A null/empty whitelist entry cannot be converted to an unrestricted tag policy."; return false; }
            if (!float.IsFinite(controller._disableSwimmingTimer) || controller._disableSwimmingTimer > 0)
            { detail = "An active native swimming-exit timestamp needs its own continuation handoff."; return false; }
            var range = controller._waterMinHeightRange;
            var candidate = new PlayerWaterConfiguration(sensor._waterLayerMask.value,
                sensor._blockerLayerMask.value, sensor._waterAndBlockerMask.value, tag,
                (PlayerNativeOceanMode)(int)sensor._oceanInteraction, sensor._objectWidth,
                controller._underWaterOffset, range.y, range.x, controller._forceDisableSwimmingAfter);
            if (!candidate.IsValid) return false;
            configuration = candidate;
            return true;
        }

        internal bool IsValid
        {
            get
            {
                if (NativeOceanMode is not (PlayerNativeOceanMode.Float
                        or PlayerNativeOceanMode.Ignore or PlayerNativeOceanMode.IgnoreInCave)
                    || !float.IsFinite(ObjectWidth) || !float.IsFinite(WaterlineOffset)
                    || !float.IsFinite(SwimEnterOffset) || !float.IsFinite(SwimExitOffset)
                    || !float.IsFinite(ForceDisableSwimmingAfter)) return false;
                if (RequiredWaterTag == null) return true;
                try { return RequiredWaterTag.Length > 0 && TagEncoding.GetByteCount(RequiredWaterTag) <= MaximumTagBytes; }
                catch (EncoderFallbackException) { return false; }
            }
        }

        internal bool SupportsNativeMovement
            => IsValid && ObjectWidth >= 0
                && MathF.Abs(WaterlineOffset) <= 10 && MathF.Abs(SwimEnterOffset) <= 10
                && MathF.Abs(SwimExitOffset) <= 10 && SwimEnterOffset >= SwimExitOffset
                && ForceDisableSwimmingAfter >= 0 && ForceDisableSwimmingAfter <= 60;

        internal bool TryValidateNativeMovement(PlayerBridge bridge,
            PlayerCarrier carrier,
            PlayerWaterContactAuthority.ServerContinuation contacts, out string detail)
        {
            detail = "Native movement water must retain this exact carrier's restored configuration and sensor owner.";
            if (!SupportsNativeMovement || bridge?.IsServer != true || carrier == null || contacts == null
                || !ReferenceEquals(contacts.Carrier, carrier) || !carrier.IsSurvivalRestored
                || !ReferenceEquals(carrier.RestoredSurvival.Water, this)) return false;
            try
            {
                contacts.RequireCurrent();
                if (!bridge.TryReadCurrentTarget(out var entity) || !Same(entity, carrier.Entity)) return false;
                detail = "Exact restored native water configuration retained without point-query or ocean-enum conversion.";
                return true;
            }
            catch (Exception error)
            { detail = "Native movement water binding changed (" + error.GetType().Name + ")."; return false; }
        }

        // This conversion is for legacy point-query diagnostics only. Native movement
        // retains this configuration and the exact ServerContinuation without enum mapping.
        internal bool TryCreateSettings(out PlayerMovementModeSettings settings, out string detail)
        {
            settings = default;
            detail = "The captured water configuration is outside the movement owner's supported ranges.";
            if (!SupportsNativeMovement || WaterLayerMask == 0) return false;
            if (WaterAndBlockerLayerMask != (WaterLayerMask | BlockerLayerMask))
            { detail = "The native cached combined water mask differs from the SDK's water/blocker union."; return false; }
            if (NativeOceanMode == PlayerNativeOceanMode.IgnoreInCave)
            { detail = "Native IgnoreInCave depends on cached cave state/overrides; SDK IgnoreBelowTerrain is not equivalent."; return false; }
            settings = new(WaterLayerMask, BlockerLayerMask, RequiredWaterTag,
                NativeOceanMode == PlayerNativeOceanMode.Float
                    ? OceanWaterMode.Include : OceanWaterMode.Ignore,
                ObjectWidth, WaterlineOffset, SwimEnterOffset, SwimExitOffset, ForceDisableSwimmingAfter);
            detail = "Captured water settings are representable; explicit server qualification is still required.";
            return true;
        }

        internal bool Matches(PlayerMovementModeSettings settings)
            => TryCreateSettings(out var expected, out _) && settings.WaterLayerMask == expected.WaterLayerMask
                && settings.BlockerLayerMask == expected.BlockerLayerMask
                && string.Equals(settings.RequiredWaterTag, expected.RequiredWaterTag, StringComparison.Ordinal)
                && settings.OceanMode == expected.OceanMode && Bits(settings.MinimumOceanSpatialLength, ObjectWidth)
                && Bits(settings.WaterlineOffset, WaterlineOffset) && Bits(settings.SwimEnterOffset, SwimEnterOffset)
                && Bits(settings.SwimExitOffset, SwimExitOffset) && Bits(settings.ForceDisableSwimmingAfter, ForceDisableSwimmingAfter);

        private bool SameConfiguration(PlayerWaterConfiguration other)
            => other != null && WaterLayerMask == other.WaterLayerMask && BlockerLayerMask == other.BlockerLayerMask
                && WaterAndBlockerLayerMask == other.WaterAndBlockerLayerMask && NativeOceanMode == other.NativeOceanMode
                && string.Equals(RequiredWaterTag, other.RequiredWaterTag, StringComparison.Ordinal)
                && Bits(ObjectWidth, other.ObjectWidth) && Bits(WaterlineOffset, other.WaterlineOffset)
                && Bits(SwimEnterOffset, other.SwimEnterOffset) && Bits(SwimExitOffset, other.SwimExitOffset)
                && Bits(ForceDisableSwimmingAfter, other.ForceDisableSwimmingAfter);

        internal void Encode(Span<byte> bytes)
        {
            bytes = bytes.Slice(0, EncodedLength);
            bytes.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(bytes, WaterLayerMask);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(4), BlockerLayerMask);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(8), WaterAndBlockerLayerMask);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(12), (int)NativeOceanMode);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(16), ObjectWidth);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(20), WaterlineOffset);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(24), SwimEnterOffset);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(28), SwimExitOffset);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(32), ForceDisableSwimmingAfter);
            int length = RequiredWaterTag == null ? 0 : TagEncoding.GetBytes(RequiredWaterTag.AsSpan(), bytes.Slice(40));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(36), length);
        }

        internal static bool TryDecode(ReadOnlySpan<byte> bytes, out PlayerWaterConfiguration configuration)
        {
            configuration = null;
            if (bytes.Length != EncodedLength) return false;
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(36));
            if (length < 0 || length > MaximumTagBytes) return false;
            foreach (byte unused in bytes.Slice(40 + length)) if (unused != 0) return false;
            string tag;
            try { tag = length == 0 ? null : TagEncoding.GetString(bytes.Slice(40, length)); }
            catch (DecoderFallbackException) { return false; }
            var candidate = new PlayerWaterConfiguration(BinaryPrimitives.ReadInt32LittleEndian(bytes),
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(4)), BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(8)),
                tag, (PlayerNativeOceanMode)BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(12)),
                BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(16)), BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(20)),
                BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(24)), BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(28)),
                BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(32)));
            if (!candidate.IsValid) return false;
            configuration = candidate;
            return true;
        }

        private static bool Bits(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
        private static bool Alive(UnityEngine.Object o) => o != null && !o.WasCollected && o;
        private static bool Same(UnityEngine.Object a, UnityEngine.Object b) => Alive(a) && Alive(b) && a.Pointer == b.Pointer;
    }
}
