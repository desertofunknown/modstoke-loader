// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Player;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// The model shown in the player's hand and the native item whose hold animation it borrows. Only data is
    /// taken from the donor: hand, animator variables, two-handedness, its hand locator and render layer.
    /// The donor item is never equipped and none of its scripts run.
    /// </summary>
    public sealed class HeldModel
    {
        public HeldModel(int poseDonorItemId, Func<GameObject> model = null)
        {
            PoseDonorItemId = poseDonorItemId;
            Model = model;
        }

        /// <summary>A native hand item, such as the GPS tracker (412), walkie-talkie (486) or radio (590).</summary>
        public int PoseDonorItemId { get; }

        /// <summary>The held model; null uses <see cref="ItemModel.Model"/>. Copied inert, like the mat model.</summary>
        public Func<GameObject> Model { get; }

        /// <summary>Model placement relative to the donor's hand locator (metres, locator space).</summary>
        public Vector3 GripOffset { get; init; }

        public Quaternion GripRotation { get; init; } = Quaternion.identity;

        public float GripScale { get; init; } = 1;

        /// <summary>Null keeps the donor's. Two-handed items put the other hand's item away while held.</summary>
        public bool? RequiresBothHands { get; init; }

        /// <summary>Whether the hand's animator item id shows the donor's id (default) or the item's own.</summary>
        public HeldAnimatorId AnimatorId { get; init; } = HeldAnimatorId.Donor;

        /// <summary>Lets the game's quick-select wheel offer it, in the donor's wheel slot.</summary>
        public bool AllowQuickSelect { get; init; }

        public bool DropsOnDeath { get; init; }

        /// <summary>Whether picking it up from the world puts it straight into the hand, as the game does with tools.</summary>
        public bool AutoEquipOnPickup { get; init; }

        /// <summary>
        /// A held instance appeared in a hand, the local player's or another player's; runs on the Unity thread on
        /// every equip.
        /// </summary>
        public Action<HeldItem> Shown { get; init; }

        /// <summary>The instance is going away, usually after the hand has lowered it; its objects may already be destroyed.</summary>
        public Action<HeldItem> Hidden { get; init; }

        /// <summary>Another player's left-hand item turned active or inactive (the game's NetworkActivation).</summary>
        public Action<HeldItem, bool> Activation { get; init; }
    }

    public enum HeldAnimatorId
    {
        /// <summary>The hand's item id parameter reads as the donor's, so animations keyed on it play.</summary>
        Donor,
        /// <summary>The item's own id, for donors whose pose is driven by animator variables alone.</summary>
        Own
    }

    /// <summary>One live held instance. Valid until <see cref="HeldModel.Hidden"/>, where it may already be destroyed.</summary>
    public sealed class HeldItem
    {
        private readonly InventoryProps _props;

        internal HeldItem(int itemId, GameObject root, Transform grip, ItemEquipmentSlot slot, bool local, InventoryProps props)
        {
            ItemId = itemId;
            Root = root;
            Grip = grip;
            Slot = slot;
            IsLocalPlayer = local;
            _props = props;
        }

        public int ItemId { get; }

        /// <summary>The game's instance under the hand bone, placed on the donor's locator.</summary>
        public GameObject Root { get; }

        /// <summary>The child placed by the profile's grip offset, rotation and scale; the model's copy is its only child.</summary>
        public Transform Grip { get; }

        /// <summary>The copy of the mod's model in the hand, or null once it is gone.</summary>
        public GameObject Model => Grip != null && !Grip.WasCollected && Grip && Grip.childCount > 0 ? Grip.GetChild(0).gameObject : null;

        public ItemEquipmentSlot Slot { get; }

        public bool IsLocalPlayer { get; }

        /// <summary>
        /// Whether this is still the player's instance of the item: false from the moment it is put away, while the
        /// hand is still lowering it, and once another instance replaces it.
        /// </summary>
        public bool IsCurrent
        {
            get
            {
                if (Hidden || Root is null || Root.WasCollected || !Root) return false;
                if (_props is null || _props.WasCollected || !_props) return true;
                var instances = _props._gameObjectInstances;
                return instances != null && instances.TryGetValue(ItemId, out var current)
                    && current != null && current.Pointer == Root.Pointer;
            }
        }

        internal bool Hidden { get; set; }
    }

    /// <summary>A copy of the held-item registry and the local player's hands.</summary>
    public sealed class HeldItemsSnapshot
    {
        internal HeldItemsSnapshot(IReadOnlyList<HeldItemEntry> items, int leftHandItemId,
            int rightHandItemId, int rememberedItemId, int rememberedLeftItemId, int liveInstances)
        {
            Items = items;
            LeftHandItemId = leftHandItemId;
            RightHandItemId = rightHandItemId;
            RememberedItemId = rememberedItemId;
            RememberedLeftItemId = rememberedLeftItemId;
            LiveInstances = liveInstances;
        }

        public IReadOnlyList<HeldItemEntry> Items { get; }

        /// <summary>The local player's hand items, or -1 when empty or no player is in a world.</summary>
        public int LeftHandItemId { get; }

        public int RightHandItemId { get; }

        /// <summary>The item a two-handed held item put away and gives back when it is put away, or -1.</summary>
        public int RememberedItemId { get; }

        /// <summary>The left-hand item a two-handed held item replaced, given back once its put-away has settled, or -1.</summary>
        public int RememberedLeftItemId { get; }

        /// <summary>Held instances alive on every player, local and remote.</summary>
        public int LiveInstances { get; }
    }

    public sealed class HeldItemEntry
    {
        internal HeldItemEntry(int itemId, int poseDonorItemId, ItemEquipmentSlot slot, bool requiresBothHands, bool held)
        {
            ItemId = itemId;
            PoseDonorItemId = poseDonorItemId;
            Slot = slot;
            RequiresBothHands = requiresBothHands;
            Held = held;
        }

        public int ItemId { get; }
        public int PoseDonorItemId { get; }
        public ItemEquipmentSlot Slot { get; }
        public bool RequiresBothHands { get; }

        /// <summary>Whether the local player holds it.</summary>
        public bool Held { get; }
    }
}
