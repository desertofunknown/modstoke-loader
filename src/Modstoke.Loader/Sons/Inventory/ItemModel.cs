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
    /// instantiates its own copies for the inventory mat and the world pickup and never destroys the template.
    /// Copies are made inert: scripts, rigidbodies, joints and colliders are removed, and the SDK adds hit boxes
    /// fitted to the meshes. Pickups of networked ids keep the donor pickup's Bolt entity under a prefab id derived
    /// from the item id, so every peer that registered the item resolves it; client-local ids get local pickups.
    /// </remarks>
    public sealed class ItemModel
    {
        public ItemModel(Func<GameObject> model)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
        }

        /// <summary>The model shown on the inventory mat, and in the world when no pickup model is given.</summary>
        public Func<GameObject> Model { get; }

        /// <summary>Optional separate world model, for example a lower-detail copy.</summary>
        public Func<GameObject> PickupModel { get; init; }

        /// <summary>Inventory icon. It must be readable, as the game copies icon pixels when it builds its UI.</summary>
        public Texture2D Icon { get; init; }

        public string Description { get; init; } = "";

        /// <summary>The native item whose mat group supplies the position, hover and audio settings.</summary>
        public int MatAnchorItemId { get; init; } = 412;

        /// <summary>Position relative to the anchor group, in the mat's local space (metres).</summary>
        public Vector3 MatOffset { get; init; }

        public Quaternion MatRotation { get; init; } = Quaternion.identity;

        public float MatScale { get; init; } = 1;

        /// <summary>Most items one inventory slot holds.</summary>
        public int MaxAmount { get; init; } = 1;

        /// <summary>
        /// For spawnable items: clicking the item on the mat closes the inventory and drops one in front of the
        /// player. The game has no drop action for plain items. Host and singleplayer only for now.
        /// </summary>
        public bool DropFromMat { get; init; } = true;
    }
}
