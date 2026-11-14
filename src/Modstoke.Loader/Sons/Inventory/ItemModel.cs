// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// Mod-supplied visuals for an item the SDK authors itself. Pass it to
    /// <see cref="ItemRegistration.ExecuteItemRegistration(CustomItemDefinition, ItemModel)"/>.
    /// </summary>
    /// <remarks>
    /// Factories return a template the mod keeps alive, such as a prefab loaded from its asset bundle. The SDK
    /// instantiates its own copies for the inventory mat, the world pickup and the hand, and never destroys the
    /// template. Copies are made inert: scripts, rigidbodies, joints and colliders are removed, and mat copies get
    /// hit boxes fitted to the meshes. Pickups of networked ids keep the donor pickup's Bolt entity under a prefab id derived
    /// from the item id, so every peer that registered the item resolves it; client-local ids get local pickups.
    /// </remarks>
    public sealed class ItemModel
    {
        public ItemModel(Func<GameObject> model)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
        }

        /// <summary>
        /// The model shown on the inventory mat, in the world when no pickup model is given, and in the hand when the
        /// held model gives none.
        /// </summary>
        public Func<GameObject> Model { get; }

        /// <summary>Optional separate world model, for example a lower-detail copy.</summary>
        public Func<GameObject> PickupModel { get; init; }

        /// <summary>Inventory icon. It must be readable, as the game copies icon pixels when it builds its UI.</summary>
        public Texture2D Icon { get; init; }

        public string Description { get; init; } = "";

        /// <summary>
        /// Where it lies on the mod gear page. Unset, items that attach or combine go with the weapon parts and
        /// the rest under Other.
        /// </summary>
        /// <remarks>
        /// The SDK picks the place within the category from the model's footprint as it lies, after
        /// <see cref="MatRotation"/> and <see cref="MatScale"/>.
        /// </remarks>
        public MatCategory? MatCategory { get; init; }

        /// <summary>Moves the model from its place, in the mat's space (metres; Y up): usually just a lift.</summary>
        public Vector3 MatOffset { get; init; }

        public Quaternion MatRotation { get; init; } = Quaternion.identity;

        public float MatScale { get; init; } = 1;

        /// <summary>Most items one inventory slot holds.</summary>
        public int MaxAmount { get; init; } = 1;

        /// <summary>
        /// What clicking it on the mat does. The game has no drop action for plain items, so Drop, Attach and
        /// Equip are the SDK's own left clicks. Combine is the game's right click onto the crafting mat; it and
        /// Attach need a <see cref="WeaponAttachments"/> registration. Unset, items with a <see cref="Held"/>
        /// model equip and the rest drop.
        /// </summary>
        public ItemMatAction MatAction
        {
            get => _matAction ?? (Held != null ? ItemMatAction.Equip : ItemMatAction.Drop);
            init => _matAction = value;
        }

        private readonly ItemMatAction? _matAction;

        /// <summary>Makes the item equippable with its own held model and a native item's hold animation.</summary>
        /// <remarks>Requires a spawnable definition, since the game drops held items in several of its paths.</remarks>
        public HeldModel Held { get; init; }
    }

    public enum ItemMatAction
    {
        None,
        /// <summary>Drop one in front of the player; spawnable items only.</summary>
        Drop,
        /// <summary>Attach one to a carried weapon it fits, swapping out whatever held that slot.</summary>
        Attach,
        /// <summary>
        /// Put one on the crafting mat, where it combines with the weapon it fits and comes back when that
        /// weapon is dismantled, as the game's own weapon mods do.
        /// </summary>
        Combine,
        /// <summary>Equip it in the hand; items with a <see cref="ItemModel.Held"/> model only.</summary>
        Equip
    }
}
