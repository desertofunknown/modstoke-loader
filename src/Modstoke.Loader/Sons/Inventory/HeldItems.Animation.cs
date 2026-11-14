// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Text;
using global::Sons.Animation;
using global::Sons.Items.Core;
using TheForest.Items.Inventory;
using TheForest.Utils;
using TheForest.Utils.Items;
using UnityEngine;
using SlotOverride = Il2CppSystem.Nullable<global::Sons.Items.Core.EquipmentSlot>;

namespace Modstoke.Sons
{
    public static partial class HeldItems
    {
        // The game writes the item's own id into the hand's animator id and clears it on unequip only while it
        // still holds that id. Transitions keyed on the donor's id play when the hand reads as the donor, so the
        // id is aliased while held and cleared here, leaving nothing behind.
        private static void AfterApplyAnimVars(ItemData __0, bool __1, Animator __2, SlotOverride __3)
        {
            if (__0 == null || Profiles.Count == 0) return;
            try
            {
                var id = __0._id;
                if (id < ItemIds.FirstModItemId || !Profiles.TryGetValue(id, out var profile)
                    || profile.Model.AnimatorId == HeldAnimatorId.Own || !Live(__2)) return;
                var slot = __3 != null && __3.HasValue ? __3.Value : profile.Slot;
                if (slot is not (EquipmentSlot.LeftHand or EquipmentSlot.RightHand)) return;
                var hash = slot == EquipmentSlot.LeftHand ? AnimationHashes.LeftHandHeldItemIdHash : AnimationHashes.RightHandHeldItemIdHash;
                if (__1)
                {
                    __2.SetInteger(hash, profile.DonorId);
                    if (slot == EquipmentSlot.RightHand) __2.SetFloat(AnimationHashes.RightHandHeldItemIdFloatHash, profile.DonorId);
                    return;
                }
                var current = __2.GetInteger(hash);
                if (current == profile.DonorId || current == id) __2.SetInteger(hash, -1);
            }
            catch (Exception error)
            {
                LogOnce("animator id", "The held item animator id could not be set: " + error.Message);
            }
        }

        /// <summary>One line on the local player's hands, held instances and left-hand pose layer, for test runs.</summary>
        internal static string DescribeLocal()
        {
            if (!Inventory.TryGetCurrentInventory(out var inventory, out var status)) return status;
            var text = new StringBuilder();
            try
            {
                text.Append("Held: equip state ").Append(inventory._currentEquipState);
                var animator = LocalPlayer.Animator;
                foreach (var slot in new[] { EquipmentSlot.LeftHand, EquipmentSlot.RightHand })
                {
                    var current = SlotItem(inventory, slot, false);
                    text.Append("; ").Append(Hand(slot)).Append(' ').Append(Id(current))
                        .Append(" (next ").Append(Id(SlotItem(inventory, slot, true)))
                        .Append(", previous ").Append(Id(PreviousItem(inventory, slot))).Append(')');
                    if (current > 0)
                    {
                        text.Append(", carried ").Append(inventory.AmountOf(current, false, false));
                        DescribeInstance(text, current, slot);
                        if (Live(animator) && ItemDatabaseManager.TryFindItemById(current, out var data) && Live(data))
                            DescribePoses(text, animator, data);
                    }
                }
                if (Live(animator))
                {
                    text.Append("; LeftHandHeldItemId ").Append(animator.GetInteger(AnimationHashes.LeftHandHeldItemIdHash))
                        .Append(", RightHandHeldItemId ").Append(animator.GetInteger(AnimationHashes.RightHandHeldItemIdHash))
                        .Append(", RightHandHeldItemIdFloat ").Append(animator.GetFloat(AnimationHashes.RightHandHeldItemIdFloatHash).ToString("0.##"));
                    DescribeLayer(text, animator);
                }
                text.Append("; remembered ").Append(Id(_remembered)).Append(", left ").Append(Id(_rememberedLeft))
                    .Append(_leftGiveBackUntil > 0 ? " (giving back)" : "").Append("; live instances ").Append(Instances.Count);
                foreach (var profile in Profiles.Values)
                    text.Append("; item ").Append(profile.ItemId).Append(" carried ").Append(inventory.AmountOf(profile.ItemId, true, false));
            }
            catch (Exception error)
            {
                text.Append("; reading failed: ").Append(error.Message);
            }
            return text.ToString();
        }

        /// <summary>
        /// The local animator's parameters that differ from zero, each layer's weight and state, and the components
        /// of the held instances, for comparing a held item with its donor.
        /// </summary>
        internal static string DescribeAnimator()
        {
            var animator = LocalPlayer.Animator;
            if (!Live(animator)) return "There is no local player animator.";
            var text = new StringBuilder("Held animator:");
            try
            {
                var parameters = animator.parameters;
                for (var index = 0; parameters != null && index < parameters.Length; index++)
                {
                    var parameter = parameters[index];
                    if (parameter == null) continue;
                    var hash = parameter.nameHash;
                    var value = parameter.type switch
                    {
                        AnimatorControllerParameterType.Bool => animator.GetBool(hash) ? "true" : null,
                        AnimatorControllerParameterType.Int => animator.GetInteger(hash) is var number && number != 0 ? number.ToString() : null,
                        AnimatorControllerParameterType.Float => animator.GetFloat(hash) is var real && Math.Abs(real) > 0.001f ? real.ToString("0.###") : null,
                        _ => null
                    };
                    if (value != null) text.Append(' ').Append(parameter.name).Append('=').Append(value);
                }
                text.Append("; layers");
                for (var layer = 0; layer < animator.layerCount; layer++)
                {
                    var state = animator.GetCurrentAnimatorStateInfo(layer);
                    text.Append(' ').Append(layer).Append(':').Append(animator.GetLayerWeight(layer).ToString("0.##"))
                        .Append('/').Append(state.shortNameHash);
                }
                text.Append("; tracker states idle ").Append(AnimationHashes.TrackerIdleheldHash)
                    .Append(" toHeld ").Append(AnimationHashes.TrackerIdleToHeldHash);
                var props = LocalProps();
                var instances = Live(props) ? props._gameObjectInstances : null;
                if (instances != null)
                    foreach (var pair in instances)
                    {
                        if (!Live(pair.Value)) continue;
                        var names = new SortedSet<string>(StringComparer.Ordinal);
                        foreach (var component in pair.Value.GetComponentsInChildren<Component>(true))
                            if (component != null) names.Add(component.GetIl2CppType().Name);
                        text.Append("; item ").Append(pair.Key).Append(" components ").Append(string.Join(", ", names));
                    }
            }
            catch (Exception error)
            {
                text.Append("; reading failed: ").Append(error.Message);
            }
            return text.ToString();
        }

        private static void DescribeInstance(StringBuilder text, int itemId, EquipmentSlot slot)
        {
            var props = LocalProps();
            var instances = Live(props) ? props._gameObjectInstances : null;
            if (instances == null || !instances.TryGetValue(itemId, out var instance) || !Live(instance))
            {
                text.Append(", no instance");
                return;
            }
            var transform = instance.transform;
            text.Append(", instance ").Append(transform.name).Append(" under ").Append(Live(transform.parent) ? transform.parent.name : "nothing")
                .Append(" at ").Append(transform.localPosition.ToString("F3")).Append(", lossy scale ").Append(transform.lossyScale.ToString("F3"));
            // The hand locators hang below the hand bone, as in .../LeftHand/LeftHandWeapon/leftHandHeld/LeftHandItemLocators.
            var bone = slot == EquipmentSlot.LeftHand ? "LeftHand" : "RightHand";
            var hand = transform.parent;
            while (Live(hand) && hand.name != bone) hand = hand.parent;
            if (!Live(hand))
            {
                text.Append(", no ").Append(bone).Append(" bone above it");
                return;
            }
            // Metres along the bone's axes, as a grip in hand space is authored.
            var toHand = Quaternion.Inverse(hand.rotation);
            text.Append(", from the ").Append(bone).Append(" bone ").Append((toHand * (transform.position - hand.position)).ToString("F3"))
                .Append(" m turned ").Append((toHand * transform.rotation).eulerAngles.ToString("F1"))
                .Append(" (bone lossy scale ").Append(hand.lossyScale.ToString("F3")).Append(')');
        }

        // The item's animator variables as the animator holds them, read by the parameter's own type.
        private static void DescribePoses(StringBuilder text, Animator animator, ItemData data)
        {
            var poses = data._equippedAnimVars;
            if (poses == null || poses.Length == 0) return;
            var hashes = ItemAnimatorHashHelper._hashes;
            var parameters = animator.parameters;
            for (var index = 0; index < poses.Length; index++)
            {
                var pose = poses[index];
                var hash = hashes != null && (int)pose < hashes.Length ? hashes[(int)pose] : Animator.StringToHash(pose.ToString());
                text.Append(", ").Append(pose).Append(' ');
                var found = false;
                for (var p = 0; parameters != null && p < parameters.Length && !found; p++)
                {
                    var parameter = parameters[p];
                    if (parameter == null || parameter.nameHash != hash) continue;
                    found = true;
                    text.Append(parameter.type switch
                    {
                        AnimatorControllerParameterType.Bool => animator.GetBool(hash).ToString(),
                        AnimatorControllerParameterType.Int => animator.GetInteger(hash).ToString(),
                        AnimatorControllerParameterType.Float => animator.GetFloat(hash).ToString("0.##"),
                        _ => "trigger"
                    });
                }
                if (!found) text.Append("missing");
            }
        }

        private static void DescribeLayer(StringBuilder text, Animator animator)
        {
            text.Append("; layer ").Append(LeftHandLayer).Append(" weight ");
            text.Append(LeftHandLayer < animator.layerCount ? animator.GetLayerWeight(LeftHandLayer).ToString("0.##") : "none");
            var control = LocalPlayer.AnimControl;
            var layers = Live(control) ? control.GetComponent<AnimatorLayersController>() : null;
            if (!Live(layers) || layers._layersData == null || !layers._layersData.TryGetValue(LeftHandLayer, out var data) || data == null)
                return;
            text.Append(", activators [");
            var names = new List<string>();
            var activators = data.Activators;
            for (var index = 0; activators != null && index < activators.Count; index++)
                if (activators[index] != null) names.Add(activators[index].DebugName);
            text.Append(string.Join(", ", names)).Append("], disablers ").Append(data.Disablers?.Count ?? 0);
        }

        private static int PreviousItem(PlayerInventory inventory, EquipmentSlot slot)
        {
            var previous = slot == EquipmentSlot.LeftHand ? inventory.PreviousLeftHandItem : inventory.PreviousRightHandItem;
            return previous != null && !previous.WasCollected ? previous._itemID : -1;
        }

        private static string Id(int itemId) => itemId > 0 ? itemId.ToString() : "-";
    }
}
