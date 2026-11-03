// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using global::Sons.Player;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatLifeHooks
    {
        private sealed class EquipmentPoseOwner
        {
            internal EquipmentPoseOwner(StrictCombatLifeBarrier barrier, PlayerInventory inventory,
                InventoryProps props, Animator animator)
            {
                Barrier = barrier;
                Inventory = inventory;
                Props = props;
                Animator = animator;
            }

            internal StrictCombatLifeBarrier Barrier { get; }
            internal PlayerInventory Inventory { get; }
            internal InventoryProps Props { get; }
            internal Animator Animator { get; }
        }

        private static EquipmentPoseOwner[] _equipmentPoseOwners = Array.Empty<EquipmentPoseOwner>();

        private static (MethodInfo Method, string Prefix)[] NativeEquipmentTargets()
            => new (MethodInfo, string)[]
            {
                (AccessTools.Method(typeof(InventoryProps._SmoothBlendLeftHandWidePose_d__41),
                    "MoveNext", Type.EmptyTypes), nameof(BeforeEquipmentPoseMoveNext))
            };

        internal static bool TryOwnEquipmentPose(StrictCombatLifeBarrier barrier,
            PlayerInventory inventory, InventoryProps props, Animator animator)
        {
            if (!OnOwnerThread || !_prepared || !_hooksInstalled) return false;
            try
            {
                int frame = Time.frameCount;
                if (!EquipmentPoseCaptureCurrent(barrier, inventory, props, animator, frame)) return false;
                var owners = Volatile.Read(ref _equipmentPoseOwners);
                foreach (var existing in owners)
                {
                    if (!EquipmentPoseSame(existing.Props, props)
                        && !EquipmentPoseSame(existing.Animator, animator)) continue;
                    return ReferenceEquals(existing.Barrier, barrier) && !existing.Barrier.IsRetired
                        && EquipmentPoseSame(existing.Inventory, inventory)
                        && EquipmentPoseSame(existing.Props, props)
                        && EquipmentPoseSame(existing.Animator, animator)
                        && EquipmentPoseCaptureCurrent(barrier, inventory, props, animator, frame);
                }
                if (owners.Length >= 32) return false;
                var next = new EquipmentPoseOwner[owners.Length + 1];
                Array.Copy(owners, next, owners.Length);
                next[^1] = new EquipmentPoseOwner(barrier, inventory, props, animator);
                if (!EquipmentPoseCaptureCurrent(barrier, inventory, props, animator, frame)) return false;
                // Keep every capture, including retired barriers, for delayed native iterators.
                Volatile.Write(ref _equipmentPoseOwners, next);
                return true;
            }
            catch (Exception) { return false; }
        }

        private static bool EquipmentPoseCaptureCurrent(StrictCombatLifeBarrier barrier,
            PlayerInventory inventory, InventoryProps props, Animator animator, int frame)
        {
            if (!OnOwnerThread || !_prepared || !_hooksInstalled || barrier is null
                || barrier.IsRetired || !barrier.Owner.IsCurrentNativeHealthOwner(barrier)
                || !EquipmentPoseLive(barrier.Stats) || !EquipmentPoseLive(inventory)
                || !EquipmentPoseLive(props) || !EquipmentPoseLive(animator)) return false;
            return EquipmentPoseSame(LocalPlayer.Inventory, inventory)
                && EquipmentPoseSame(barrier.Stats._inventory, inventory)
                && EquipmentPoseSame(inventory.InventoryProps, props)
                && EquipmentPoseSame(props._playerInventory, inventory)
                && EquipmentPoseSame(props._playerAnimator, animator)
                && EquipmentPoseSame(LocalPlayer.Animator, animator)
                && EquipmentPoseSame(barrier.Stats._animator, animator)
                && props._leftHandWideBlendCoroutine is null
                && barrier.Owner.IsCurrentNativeHealthOwner(barrier) && Time.frameCount == frame;
        }

        private static bool EquipmentPoseLive(UnityEngine.Object value)
            => value is not null && !value.WasCollected && value;

        private static bool EquipmentPoseSame(UnityEngine.Object first, UnityEngine.Object second)
            => first is not null && second is not null && first.Pointer == second.Pointer;

        private static bool BeforeEquipmentPoseMoveNext(
            InventoryProps._SmoothBlendLeftHandWidePose_d__41 __instance, bool __runOriginal, ref bool __result)
        {
            if (!__runOriginal) return true;
            var owners = Volatile.Read(ref _equipmentPoseOwners);
            if (owners.Length == 0) return true;
            if (!OnOwnerThread) return EquipmentPoseUnreadable(ref __result);
            try
            {
                if (__instance is null || __instance.WasCollected)
                    return EquipmentPoseUnreadable(ref __result);
                var props = __instance.__4__this;
                var animator = __instance.animator;
                foreach (var owner in owners)
                {
                    if (!EquipmentPoseSame(owner.Props, props)
                        && !EquipmentPoseSame(owner.Animator, animator)) continue;
                    // No native cleanup: even a retired iterator must not write a final pose.
                    __result = false;
                    return false;
                }
                if (props is null || animator is null || props.WasCollected || animator.WasCollected)
                    return EquipmentPoseUnreadable(ref __result);
                return true;
            }
            catch (Exception) { return EquipmentPoseUnreadable(ref __result); }
        }

        private static bool EquipmentPoseUnreadable(ref bool result)
        {
            AllowUnreadableOwner();
            result = false;
            return false;
        }
    }
}
