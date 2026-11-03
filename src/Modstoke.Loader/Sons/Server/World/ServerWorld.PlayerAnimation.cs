// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>Maximum copied entries from each replicated clothing or armour array per read.</summary>
        public const int MaximumPlayerPresentationEntries = 32;

        /// <summary>
        /// Copies animation and equipment presentation fields for an exact local or remote player
        /// attachment on the server game thread. Uses the same binding and world guards as movement reads.
        /// Returns true with Success or Truncated; all other statuses leave the snapshot null.
        /// Values may be initial, stale or client-controlled. The read is not an atomic replication sample
        /// and establishes neither rendered pose, inventory ownership, protection nor permission to act.
        /// </summary>
        public static bool TryReadPlayerAnimation(PlayerHandle binding,
            out PlayerAnimationSnapshot snapshot, out PlayerAnimationReadStatus status)
        {
            snapshot = null;
            try
            {
                if (!TryValidatePlayerBinding(binding, out var bindingStatus))
                {
                    status = PlayerAnimationStatus(bindingStatus);
                    return false;
                }
                if (!WorldRecords.TryGetCurrent(out var world, out _))
                {
                    status = PlayerAnimationReadStatus.WorldUnavailable;
                    return false;
                }
                var frame = UnityEngine.Time.frameCount;
                var state = binding.Player.GetState<IPlayerState>();
                if (state is null || state.WasCollected)
                {
                    status = PlayerAnimationReadStatus.PlayerStateUnavailable;
                    return false;
                }

                var clothing = state.PlayerClothingIds;
                var armour = state.PlayerArmourIds;
                if (clothing is null || clothing.WasCollected || armour is null || armour.WasCollected)
                {
                    status = PlayerAnimationReadStatus.EquipmentUnavailable;
                    return false;
                }
                var clothingCount = clothing.Length;
                var armourCount = armour.Length;
                if (clothingCount < 0 || armourCount < 0)
                {
                    status = PlayerAnimationReadStatus.EquipmentUnavailable;
                    return false;
                }
                var clothingIds = clothingCount == 0 ? Array.Empty<int>()
                    : new int[Math.Min(clothingCount, MaximumPlayerPresentationEntries)];
                var armourIds = armourCount == 0 ? Array.Empty<int>()
                    : new int[Math.Min(armourCount, MaximumPlayerPresentationEntries)];
                for (var index = 0; index < clothingIds.Length; index++)
                    clothingIds[index] = clothing[index];
                for (var index = 0; index < armourIds.Length; index++)
                    armourIds[index] = armour[index];

                var spineBlendX = state.spineBlendX;
                var clampSpine = state.clampSpine;
                var tiredFloat = state.tiredFloat;
                var coldFloat = state.coldFloat;
                var treeHeightBlend = state.treeHeightBlend;
                var expression = state.Expression;
                var rightHandHeldItemIdFloat = state.RightHandHeldItemIdFloat;
                var rightHandActive = state.rightHandActive;
                var rightHandItemIsLit = state.RightHandItemIsLit;
                var leftHandItemIsInactive = state.LeftHandItemIsInactive;
                var hasBackpack = state.HasBackpack;
                var rightHandVisualVariantNum = state.RightHandVisualVariantNum;
                var rightHandItemPlatingState = state.RightHandItemPlatingState;
                var isGoldArmorPlated = state.IsGoldArmorPlated;

                if (!TryValidatePlayerBinding(binding, out bindingStatus))
                {
                    status = PlayerAnimationStatus(bindingStatus);
                    return false;
                }
                var currentState = binding.Player.GetState<IPlayerState>();
                if (state.WasCollected || currentState is null || currentState.WasCollected
                    || currentState.Pointer != state.Pointer)
                {
                    status = PlayerAnimationReadStatus.PlayerStateUnavailable;
                    return false;
                }
                var currentClothing = currentState.PlayerClothingIds;
                var currentArmour = currentState.PlayerArmourIds;
                if (clothing.WasCollected || armour.WasCollected
                    || currentClothing is null || currentClothing.WasCollected
                    || currentArmour is null || currentArmour.WasCollected)
                {
                    status = PlayerAnimationReadStatus.EquipmentUnavailable;
                    return false;
                }
                if (currentClothing.Pointer != clothing.Pointer || currentClothing.Length != clothingCount
                    || currentArmour.Pointer != armour.Pointer || currentArmour.Length != armourCount
                    || !world.IsCurrent || UnityEngine.Time.frameCount != frame)
                {
                    status = PlayerAnimationReadStatus.ContextChanged;
                    return false;
                }
                if (!float.IsFinite(spineBlendX) || !float.IsFinite(tiredFloat)
                    || !float.IsFinite(coldFloat) || !float.IsFinite(treeHeightBlend)
                    || !float.IsFinite(rightHandHeldItemIdFloat))
                {
                    status = PlayerAnimationReadStatus.NonFiniteValues;
                    return false;
                }

                snapshot = new PlayerAnimationSnapshot(binding, world, frame,
                    spineBlendX, clampSpine, tiredFloat, coldFloat, treeHeightBlend, expression,
                    rightHandHeldItemIdFloat, rightHandActive, rightHandItemIsLit, leftHandItemIsInactive,
                    hasBackpack, rightHandVisualVariantNum, rightHandItemPlatingState, isGoldArmorPlated,
                    clothingCount, clothingIds, armourCount, armourIds);
                status = snapshot.Truncated ? PlayerAnimationReadStatus.Truncated
                    : PlayerAnimationReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                RetireEndedPlayerBinding(binding);
                status = PlayerAnimationReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        private static PlayerAnimationReadStatus PlayerAnimationStatus(WorldReadStatus status) => status switch
        {
            WorldReadStatus.Success => PlayerAnimationReadStatus.Success,
            WorldReadStatus.WrongThread => PlayerAnimationReadStatus.WrongThread,
            WorldReadStatus.NotServer => PlayerAnimationReadStatus.NotServer,
            WorldReadStatus.BindingChanged => PlayerAnimationReadStatus.BindingChanged,
            WorldReadStatus.Failed => PlayerAnimationReadStatus.Failed,
            _ => PlayerAnimationReadStatus.Unavailable
        };
    }

    public enum PlayerAnimationReadStatus
    {
        Unavailable,
        Success,
        /// <summary>A snapshot is available, but at least one equipment array exceeds the copied-entry limit.</summary>
        Truncated,
        WrongThread,
        NotServer,
        WorldUnavailable,
        BindingChanged,
        PlayerStateUnavailable,
        /// <summary>A clothing or armour array could not be read. No empty list or partial snapshot is substituted.</summary>
        EquipmentUnavailable,
        /// <summary>The world, Unity frame, equipment array identity or equipment array length changed during the read.</summary>
        ContextChanged,
        /// <summary>At least one numeric animation field was NaN or infinite. No partial snapshot is returned.</summary>
        NonFiniteValues,
        Failed
    }

    /// <summary>
    /// Managed copies of selected IPlayerState animation and equipment presentation fields.
    /// All values are replicated observations, not confirmed renderer state or gameplay authority.
    /// No native player-state or equipment-array wrapper is exposed; the opaque binding retains its normal lifetime.
    /// </summary>
    public sealed class PlayerAnimationSnapshot
    {
        internal PlayerAnimationSnapshot(PlayerHandle binding,
            WorldRecordContext world, int frame, float spineBlendX, bool clampSpine,
            float tiredFloat, float coldFloat, float treeHeightBlend, int expression,
            float rightHandHeldItemIdFloat, bool rightHandActive, bool rightHandItemIsLit,
            bool leftHandItemIsInactive, bool hasBackpack, int rightHandVisualVariantNum,
            int rightHandItemPlatingState, bool isGoldArmorPlated,
            int clothingEntryCount, int[] clothingIds, int armourEntryCount, int[] armourIds)
        {
            Binding = binding;
            World = world;
            Frame = frame;
            RawSpineBlendX = spineBlendX;
            ClampSpine = clampSpine;
            RawTiredFloat = tiredFloat;
            RawColdFloat = coldFloat;
            RawTreeHeightBlend = treeHeightBlend;
            RawExpression = expression;
            RawRightHandHeldItemIdFloat = rightHandHeldItemIdFloat;
            RightHandActive = rightHandActive;
            RightHandItemIsLit = rightHandItemIsLit;
            LeftHandItemIsInactive = leftHandItemIsInactive;
            HasBackpack = hasBackpack;
            RawRightHandVisualVariantNum = rightHandVisualVariantNum;
            RawRightHandItemPlatingState = rightHandItemPlatingState;
            IsGoldArmorPlated = isGoldArmorPlated;
            ClothingEntryCount = clothingEntryCount;
            PlayerClothingIds = Array.AsReadOnly(clothingIds);
            ArmourEntryCount = armourEntryCount;
            PlayerArmourIds = Array.AsReadOnly(armourIds);
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>The exact input attachment and its remote session or local identity. May retire after the read.</summary>
        public PlayerHandle Binding { get; }
        /// <summary>The world lifetime observed during the read, not a reusable save ID.</summary>
        public WorldRecordContext World { get; }
        /// <summary>Unity frame of the read, not the frame in which replication last changed.</summary>
        public int Frame { get; }
        /// <summary>Raw IPlayerState.spineBlendX; finite but not clamped or converted into a bone angle.</summary>
        public float RawSpineBlendX { get; }
        /// <summary>Raw IPlayerState.clampSpine, not evidence of an applied animator constraint.</summary>
        public bool ClampSpine { get; }
        /// <summary>Raw IPlayerState.tiredFloat; finite but not clamped or interpreted as stamina.</summary>
        public float RawTiredFloat { get; }
        /// <summary>Raw IPlayerState.coldFloat; finite but not clamped or interpreted as temperature.</summary>
        public float RawColdFloat { get; }
        /// <summary>Raw IPlayerState.treeHeightBlend; finite but not a world-space height.</summary>
        public float RawTreeHeightBlend { get; }
        /// <summary>Raw IPlayerState.Expression. Unknown codes are preserved; no expression names are inferred.</summary>
        public int RawExpression { get; }
        /// <summary>Raw finite IPlayerState.RightHandHeldItemIdFloat; not rounded or substituted for the integer item ID.</summary>
        public float RawRightHandHeldItemIdFloat { get; }
        /// <summary>Raw IPlayerState.rightHandActive; not proof that a renderer is visible or equipment is ready.</summary>
        public bool RightHandActive { get; }
        /// <summary>Raw IPlayerState.RightHandItemIsLit; not a verified light, fuel or damage state.</summary>
        public bool RightHandItemIsLit { get; }
        /// <summary>Raw IPlayerState.LeftHandItemIsInactive; not proof of an empty hand.</summary>
        public bool LeftHandItemIsInactive { get; }
        /// <summary>Raw IPlayerState.HasBackpack; not an inventory ownership or access verdict.</summary>
        public bool HasBackpack { get; }
        /// <summary>Raw IPlayerState.RightHandVisualVariantNum. No variant lookup or range is inferred.</summary>
        public int RawRightHandVisualVariantNum { get; }
        /// <summary>Raw IPlayerState.RightHandItemPlatingState. Unknown codes remain uninterpreted.</summary>
        public int RawRightHandItemPlatingState { get; }
        /// <summary>Raw IPlayerState.IsGoldArmorPlated; not armour protection, durability or ownership.</summary>
        public bool IsGoldArmorPlated { get; }
        /// <summary>Observed native clothing-array length, including empty entries, not an equipped-item count.</summary>
        public int ClothingEntryCount { get; }
        /// <summary>Bounded raw clothing-ID prefix in native order, including zeros, negatives and duplicates. Slots are not inferred.</summary>
        public IReadOnlyList<int> PlayerClothingIds { get; }
        /// <summary>Observed native armour-array length, including empty entries, not a protection or item count.</summary>
        public int ArmourEntryCount { get; }
        /// <summary>Bounded raw armour-ID prefix in native order, including zeros, negatives and duplicates. No protection values are read.</summary>
        public IReadOnlyList<int> PlayerArmourIds { get; }
        public bool ClothingTruncated => PlayerClothingIds.Count < ClothingEntryCount;
        public bool ArmourTruncated => PlayerArmourIds.Count < ArmourEntryCount;
        public bool Truncated => ClothingTruncated || ArmourTruncated;
        /// <summary>UTC read completion time, not a simulation, replication or last-change timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
