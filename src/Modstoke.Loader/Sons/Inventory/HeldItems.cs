// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Loader;
using global::Sons.Animation;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using global::Sons.Player;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Modstoke.Sons
{
    /// <summary>
    /// Local-player equipment for items registered with a <see cref="HeldModel"/>, through the game's own equip
    /// paths. The game's stashes, swaps, drops and quick select treat these items as its own.
    /// </summary>
    /// <remarks>
    /// Each player's props get the item's hand definition on first lookup, reusing the donor's hand locator, so
    /// other players running the same registration see the item in the hand too. Held instances are made and
    /// destroyed by the game on every equip; keep mod state outside them and attach to each in
    /// <see cref="HeldModel.Shown"/>.
    /// </remarks>
    public static partial class HeldItems
    {
        // Every left-hand hold lives on this animator layer, which only weighs in while something activates it.
        private const int LeftHandLayer = 5;

        private sealed class Profile
        {
            internal ItemData Item;
            internal HeldModel Model;
            internal int ItemId, DonorId;
            internal EquipmentSlot Slot;
            internal bool BothHands;
        }

        private sealed class Instance
        {
            internal HeldItemBehaviour Behaviour;
            internal Profile Profile;
            internal HeldItem Held;
            internal AnimatorLayersController Layers;
            internal ILayerBehaviourActivator Activator;
        }

        private static readonly Dictionary<int, Profile> Profiles = new();
        private static readonly Dictionary<IntPtr, Instance> Instances = new();
        // The right-hand item a left-hand two-handed item put away; it comes back when that item is put away, to the
        // same player's inventory only (a world load makes a new one).
        private static int _remembered = -1;
        private static IntPtr _rememberedBy;
        private static float _sweepAt;

        private const string ClientLocalRefusal = "its id is client-local, and in multiplayer the game shows a held item to every player";
        private const string StrictRefusal = "strict inventory has no server equip for held mod items yet";

        private static bool ClientLocalInSession(int itemId) => BoltNetwork.isRunning && !ItemIds.IsNetworked(itemId);

        // A client under strict inventory: its equips go to the server, which can't carry out one for a held mod item.
        private static bool StrictClient => BoltNetwork.isRunning && !BoltNetwork.isServer && PlayerInventoryEnforcement.StrictInventoryArmed;

        internal static void Add(ItemData item, HeldModel model, ItemData donor)
        {
            var profile = new Profile
            {
                Item = item, Model = model, ItemId = item._id, DonorId = donor._id,
                Slot = item._equipmentSlot, BothHands = item._requiresBothHands
            };
            Profiles[profile.ItemId] = profile;
            EnsureHooks();
            RuntimeEntryPoint.LogSource?.LogInfo("Item " + profile.ItemId + " is held in the " + Hand(profile.Slot)
                + " hand with item " + profile.DonorId + "'s pose" + (profile.BothHands ? ", using both hands." : "."));
        }

        public static bool IsHeldModelItem(int itemId) => Profiles.ContainsKey(itemId);

        /// <summary>Whether the local player has it in its hand, not counting one still being equipped.</summary>
        public static bool IsHeld(int itemId)
        {
            if (!Profiles.TryGetValue(itemId, out var profile) || !Inventory.TryGetCurrentInventory(out var inventory, out _))
                return false;
            try { return inventory.HasInSlot(profile.Slot, itemId); }
            catch (Exception) { return false; }
        }

        /// <summary>The local player's live instance of the item, including one the hand is still lowering.</summary>
        public static bool TryGetLocal(int itemId, out HeldItem held)
        {
            held = null;
            foreach (var instance in Instances.Values)
            {
                if (instance.Profile.ItemId != itemId || !instance.Held.IsLocalPlayer || instance.Held.Hidden
                    || !Live(instance.Held.Root)) continue;
                // A fresh instance wins over one still being lowered.
                if (held == null || instance.Held.IsCurrent) held = instance.Held;
            }
            return held != null;
        }

        /// <summary>
        /// The game's TryEquip, after its own check that the hand can take it now. Never removes the item when the
        /// equip cannot start.
        /// </summary>
        public static bool TryEquip(int itemId, out string status) => TryBringOut(itemId, false, out status);

        /// <summary>Puts it away when held; otherwise brings it out through the game's toggle for its hand.</summary>
        public static bool TryToggle(int itemId, out string status)
            => IsHeld(itemId) ? TryPutAway(itemId, out status) : TryBringOut(itemId, true, out status);

        /// <summary>
        /// Stashes it as the game's toggle does and gives back the other hand's item a two-handed item put away.
        /// </summary>
        public static bool TryPutAway(int itemId, out string status)
        {
            if (!Profiles.TryGetValue(itemId, out var profile))
            {
                status = "Item " + itemId + " has no held model.";
                return false;
            }
            if (!Inventory.TryGetCurrentInventory(out var inventory, out status)) return false;
            try
            {
                if (!inventory.HasInSlot(profile.Slot, itemId))
                {
                    status = inventory.HasInSlotOrNextSlot(profile.Slot, itemId)
                        ? "It is still being equipped." : "It is not in the hand.";
                    return false;
                }
                var giveBack = profile.Slot == EquipmentSlot.LeftHand && profile.BothHands && _rememberedBy == inventory.Pointer
                    ? _remembered : -1;
                if (profile.Slot == EquipmentSlot.LeftHand) inventory.UnequipItemAtSlot(EquipmentSlot.LeftHand, false, true, false);
                else inventory.StashRightHandItem(true, false, false);
                if (inventory.HasInSlot(profile.Slot, itemId))
                {
                    status = "The game did not put it away.";
                    return false;
                }
                _remembered = -1;
                status = "Put away." + (giveBack > 0 ? GiveBack(inventory, giveBack) : "");
                return true;
            }
            catch (Exception error)
            {
                status = "Putting it away failed: " + error.Message;
                return false;
            }
        }

        public static HeldItemsSnapshot Snapshot()
        {
            int left = -1, right = -1;
            var hasInventory = Inventory.TryGetCurrentInventory(out var inventory, out _);
            if (hasInventory)
            {
                left = SlotItem(inventory, EquipmentSlot.LeftHand, false);
                right = SlotItem(inventory, EquipmentSlot.RightHand, false);
            }
            var items = new List<HeldItemEntry>();
            foreach (var profile in Profiles.Values)
                items.Add(new HeldItemEntry(profile.ItemId, profile.DonorId, (ItemEquipmentSlot)(int)profile.Slot,
                    profile.BothHands, (profile.Slot == EquipmentSlot.LeftHand ? left : right) == profile.ItemId));
            items.Sort((a, b) => a.ItemId.CompareTo(b.ItemId));
            return new HeldItemsSnapshot(items.AsReadOnly(), left, right, _remembered, Instances.Count);
        }

        private static bool TryBringOut(int itemId, bool toggle, out string status)
        {
            if (!Profiles.TryGetValue(itemId, out var profile))
            {
                status = "Item " + itemId + " has no held model.";
                return false;
            }
            if (!Inventory.TryGetCurrentInventory(out var inventory, out status)) return false;
            try
            {
                if (inventory.HasInSlotOrNextSlot(profile.Slot, itemId))
                {
                    status = "It is already in the hand.";
                    return true;
                }
                if (inventory.AmountOf(itemId, false, false) < 1)
                {
                    status = "The player does not carry it.";
                    return false;
                }
                if (!CanBringOut(inventory, profile, out status)) return false;
                if (!toggle) inventory.TryEquip(itemId, false, true, false);
                else if (profile.Slot == EquipmentSlot.LeftHand) inventory.ToggleLeftHandItem(itemId);
                else inventory.ToggleRightHandItem(itemId);
                if (inventory.HasInSlotOrNextSlot(profile.Slot, itemId))
                {
                    status = "Equipping it in the " + Hand(profile.Slot) + " hand.";
                    return true;
                }
                status = "The game did not equip it.";
                return false;
            }
            catch (Exception error)
            {
                status = "Equipping failed: " + error.Message;
                return false;
            }
        }

        // The game's own readiness checks for the hand, then the hand definition, so its TryEquip never starts
        // with nothing to show.
        private static bool CanBringOut(PlayerInventory inventory, Profile profile, out string status)
        {
            if (ClientLocalInSession(profile.ItemId) || StrictClient)
            {
                status = "It can't be held here: " + (StrictClient ? StrictRefusal : ClientLocalRefusal) + ".";
                return false;
            }
            if (profile.BothHands && inventory.HeldOnlyItemController?.HasItem == true)
            {
                status = "The player is carrying something in both hands.";
                return false;
            }
            var ready = profile.Slot == EquipmentSlot.LeftHand
                ? inventory.CanEquipItemInLeftHand(profile.ItemId) : inventory.CanEquipItemInRightHand(profile.ItemId);
            if (!ready)
            {
                status = LocalPlayer.IsInMidAction ? "The player is busy with another action."
                    : LocalPlayer.IsUnderwater ? "The player is underwater."
                    : "The game will not equip it now; the hand may still be busy or on cooldown.";
                return false;
            }
            if (inventory.GetPropDefinitionForItem(profile.ItemId, null) == null)
            {
                status = "This player has no hand definition for it: the pose donor " + profile.DonorId + " has none.";
                return false;
            }
            status = "";
            return true;
        }

        private static string GiveBack(PlayerInventory inventory, int itemId)
        {
            if (inventory.TryGetItemInSlot(EquipmentSlot.RightHand, out _) || inventory.TryGetItemInNextSlot(EquipmentSlot.RightHand, out _))
                return " The right hand already holds something, so item " + itemId + " stays put away.";
            if (inventory.AmountOf(itemId, false, false) < 1) return " Item " + itemId + " is no longer carried.";
            inventory.TryEquip(itemId, false, false, false);
            return " Giving back item " + itemId + ".";
        }

        // Called by the game through IHeldItem, on every player's props, before it applies the item's animator variables.
        internal static void Initialized(HeldItemBehaviour behaviour, Animator animator, PlayerAnimatorControl control,
            Transform root, ItemInstance item)
        {
            try
            {
                if (behaviour == null || Instances.ContainsKey(behaviour.Pointer)) return;
                var id = item?._itemID ?? -1;
                if (!Profiles.TryGetValue(id, out var profile))
                {
                    RuntimeEntryPoint.LogSource?.LogWarning("A held instance was initialized for item " + id + ", which has no held model.");
                    return;
                }
                var local = Same(animator, LocalPlayer.Animator);
                var props = local ? LocalProps() : Live(root) ? root.GetComponentInChildren<InventoryProps>(true) : null;
                var gameObject = behaviour.gameObject;
                var entry = new Instance
                {
                    Behaviour = behaviour,
                    Profile = profile,
                    Held = new HeldItem(id, gameObject, Grip(gameObject.transform), (ItemEquipmentSlot)(int)profile.Slot, local, props)
                };
                Instances[behaviour.Pointer] = entry;
                if (profile.Slot == EquipmentSlot.LeftHand) Activate(entry, control, animator);
                Notify(profile.Model.Shown, entry.Held, profile, "Shown");
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("A held instance could not be set up: " + error);
            }
        }

        internal static void NetworkActivated(HeldItemBehaviour behaviour, bool active)
        {
            if (behaviour == null || !Instances.TryGetValue(behaviour.Pointer, out var entry)) return;
            var callback = entry.Profile.Model.Activation;
            if (callback == null) return;
            try { callback(entry.Held, active); }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Held item " + entry.Profile.ItemId + "'s Activation callback failed: " + error);
            }
        }

        internal static void Destroyed(HeldItemBehaviour behaviour)
        {
            if (behaviour == null || !Instances.Remove(behaviour.Pointer, out var entry)) return;
            Retire(entry);
        }

        // The donors register this activator when they initialize and drop it when destroyed, after the arm has
        // lowered; the held instance has the same lifetime.
        private static void Activate(Instance entry, PlayerAnimatorControl control, Animator animator)
        {
            AnimatorLayersController layers = null;
            if (Live(control)) layers = control.gameObject.GetComponent<AnimatorLayersController>();
            if (!Live(layers) && Live(animator)) layers = animator.GetComponent<AnimatorLayersController>();
            if (!Live(layers))
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Held item " + entry.Profile.ItemId
                    + " found no animation layer controller; its left-hand pose will not show.");
                return;
            }
            var activator = new LayerBehaviourActivator("Modstoke held " + entry.Profile.ItemId, LeftHandLayer, true, -1f)
                .Cast<ILayerBehaviourActivator>();
            layers.RegisterLayerBehaviourActivator(activator);
            entry.Layers = layers;
            entry.Activator = activator;
        }

        private static void Retire(Instance entry)
        {
            if (entry.Held.Hidden) return;
            entry.Held.Hidden = true;
            try
            {
                if (entry.Activator != null && Live(entry.Layers)) entry.Layers.UnregisterLayerBehaviourActivator(entry.Activator);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Held item " + entry.Profile.ItemId + "'s pose layer could not be released: " + error.Message);
            }
            entry.Activator = null;
            entry.Layers = null;
            Notify(entry.Profile.Model.Hidden, entry.Held, entry.Profile, "Hidden");
        }

        /// <summary>Releases instances that went without OnDestroy, such as on a failed unload.</summary>
        internal static void Tick()
        {
            TickSelfTest();
            if (Instances.Count == 0 || Time.unscaledTime < _sweepAt) return;
            _sweepAt = Time.unscaledTime + 1;
            List<IntPtr> gone = null;
            foreach (var pair in Instances)
                if (!Live(pair.Value.Behaviour)) (gone ??= new()).Add(pair.Key);
            if (gone == null) return;
            foreach (var key in gone)
                if (Instances.Remove(key, out var entry)) Retire(entry);
        }

        private static void Notify(Action<HeldItem> callback, HeldItem held, Profile profile, string name)
        {
            if (callback == null) return;
            try { callback(held); }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Held item " + profile.ItemId + "'s " + name + " callback failed: " + error);
            }
        }

        private static Transform Grip(Transform root)
        {
            for (var index = 0; index < root.childCount; index++)
            {
                var child = root.GetChild(index);
                if (child.name == HeldItemTemplates.GripName) return child;
            }
            return root;
        }

        private static InventoryProps LocalProps()
        {
            var inventory = LocalPlayer.Inventory;
            return Live(inventory) ? inventory._InventoryProps_k__BackingField : null;
        }

        private static int SlotItem(PlayerInventory inventory, EquipmentSlot slot, bool next)
        {
            try
            {
                var found = next ? inventory.TryGetItemInNextSlot(slot, out var item) : inventory.TryGetItemInSlot(slot, out item);
                return found && item != null && !item.WasCollected ? item._itemID : -1;
            }
            catch (Exception) { return -1; }
        }

        private static string Hand(EquipmentSlot slot) => slot == EquipmentSlot.LeftHand ? "left" : "right";

        private static bool Live(Object value) => value is not null && !value.WasCollected && value;

        private static bool Same(Object left, Object right) => Live(left) && Live(right) && left.Pointer == right.Pointer;
    }
}
