// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Animation;
using global::Sons.Inventory;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// The game's held-item interface on a mod item's held instance: the game initializes it with the player's
    /// animator, which is when the left-hand pose layer is activated and the mod hears of the instance. It holds no
    /// state, since Unity clones it without the managed side; everything is looked up from the item id.
    /// </summary>
    public sealed class HeldItemBehaviour : MonoBehaviour
    {
        public HeldItemBehaviour(IntPtr pointer) : base(pointer) { }

        public void Initialize(Animator playerAnimator, PlayerAnimatorControl playerAnimatorControl,
            AnimationEventReceiver playerAnimEventReceiver, Transform rootTransform, Transform baseTransform,
            ItemInstance itemInstance)
            => HeldItems.Initialized(this, playerAnimator, playerAnimatorControl, rootTransform, itemInstance);

        public void NetworkActivation(bool isActive) => HeldItems.NetworkActivated(this, isActive);

        private void OnDestroy() => HeldItems.Destroyed(this);
    }
}
