// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.Injection;
using global::Sons.Items.Core;
using global::Sons.Weapon;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>Held prefabs for items with a <see cref="HeldModel"/>.</summary>
    internal static class HeldItemTemplates
    {
        internal const string GripName = "Grip";

        /// <summary>
        /// A root the game places on the donor's hand locator, a grip child with the profile's placement (the game
        /// overwrites the root's own pose) and an inert copy of the model, all on the donor's render layer.
        /// </summary>
        /// <remarks>
        /// The root also carries the game's inert template controller: its left-hand toggle (the GPS, flashlight and
        /// book keys) asks the held item's controller whether it is idle without checking that one exists, and the
        /// controller gives the stash sound on unequip.
        /// </remarks>
        internal static Transform Create(string name, HeldModel held, Func<GameObject> model, int layer, Transform parent)
        {
            EnsureBehaviourType();
            var root = new GameObject(name + " held");
            root.layer = layer;
            root.transform.SetParent(parent, false);
            var grip = new GameObject(GripName).transform;
            grip.gameObject.layer = layer;
            grip.SetParent(root.transform, false);
            grip.localPosition = held.GripOffset;
            grip.localRotation = held.GripRotation;
            grip.localScale = Vector3.one * held.GripScale;
            ItemModelCopies.Create(held.Model ?? model, grip, layer);
            root.AddComponent<HeldItemBehaviour>();
            root.AddComponent<TemplateSBController>();
            return root.transform;
        }

        /// <summary>The layer the donor's held model draws on: its renderable, else its first renderer, else its root.</summary>
        internal static int RenderLayer(ItemData donor)
        {
            var prefab = donor._heldPrefab;
            var renderable = prefab.GetComponentInChildren<ItemRenderable>(true);
            if (renderable != null && renderable) return renderable.gameObject.layer;
            var renderer = prefab.GetComponentInChildren<Renderer>(true);
            return renderer != null && renderer ? renderer.gameObject.layer : prefab.gameObject.layer;
        }

        /// <summary>The donor's held controller type, for diagnostics.</summary>
        internal static string ControllerName(ItemData item)
        {
            var prefab = item._heldPrefab;
            if (prefab is null || prefab.WasCollected || !prefab) return "";
            var controller = prefab.GetComponentInChildren<HeldControllerBase>(true);
            return controller != null && controller ? controller.GetIl2CppType().FullName : "";
        }

        private static void EnsureBehaviourType()
        {
            if (ClassInjector.IsTypeRegisteredInIl2Cpp(typeof(HeldItemBehaviour))) return;
            ClassInjector.RegisterTypeInIl2Cpp<HeldItemBehaviour>(new RegisterTypeOptions { Interfaces = new[] { typeof(IHeldItem) } });
        }
    }
}
