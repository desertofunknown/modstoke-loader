// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Endnight.Utilities;
using HarmonyLib;
using global::Sons.Gameplay;
using global::Sons.Items.Core;
using TheForest.Utils;
using UnityEngine;
using Input = global::Sons.Input.InputSystem;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerWaterPresentation
    {
        private bool _nativeSwimmingPrepared;
        private UnityEngine.Object[] _nativeSwimmingObjects;
        private int[] _nativeSwimmingObjectIds;

        private bool PrepareNativeSwimming(out string detail)
        {
            detail = "Native swimming requires the original water, inventory and animation graph.";
            RequireSourceScheduleCurrent();
            var a = _sourceAnimation;
            if (!SameSourceObject(a.rootTr, LocalPlayer.Transform)
                || !SameSourceObject(a.rb, LocalPlayer.Rigidbody)
                || !SameSourceObject(a._animator, LocalPlayer.Animator)
                || !SameSourceObject(a._playerScriptSetup, LocalPlayer.ScriptSetup)
                || !Live(a._playerScriptSetup.targetInfo) || !Live(a._aboveWaterGroup)
                || !Live(a._underWaterGroup) || !Live(LocalPlayer.Stats)
                || !SameSourceObject(LocalPlayer.Stats._inventory, LocalPlayer.Inventory)) return false;
            _nativeSwimmingObjects = new UnityEngine.Object[] { a.rootTr, a.rb, a._animator,
                a._playerScriptSetup, a._playerScriptSetup.targetInfo, a._aboveWaterGroup,
                a._underWaterGroup, LocalPlayer.Stats, LocalPlayer.Inventory, LocalPlayer.WaterLevelSensor };
            _nativeSwimmingObjectIds = new int[_nativeSwimmingObjects.Length];
            for (int i = 0; i < _nativeSwimmingObjects.Length; i++)
            {
                if (!Live(_nativeSwimmingObjects[i])) return false;
                _nativeSwimmingObjectIds[i] = _nativeSwimmingObjects[i].GetInstanceID();
            }
            _nativeSwimmingPrepared = true;
            return true;
        }

        private void RequireNativeSwimmingCurrent()
        {
            var a = _sourceAnimation;
            if (!_nativeSwimmingPrepared || !SameSourceObject(a.rootTr, LocalPlayer.Transform)
                || !SameSourceObject(a.rb, LocalPlayer.Rigidbody)
                || !NativeObject(0, a.rootTr) || !NativeObject(1, a.rb)
                || !NativeObject(2, a._animator) || !NativeObject(2, LocalPlayer.Animator)
                || !NativeObject(3, a._playerScriptSetup) || !NativeObject(3, LocalPlayer.ScriptSetup)
                || !NativeObject(4, a._playerScriptSetup.targetInfo)
                || !NativeObject(5, a._aboveWaterGroup) || !NativeObject(6, a._underWaterGroup)
                || !NativeObject(7, LocalPlayer.Stats) || !NativeObject(8, LocalPlayer.Inventory)
                || !NativeObject(8, LocalPlayer.Stats._inventory)
                || !NativeObject(9, LocalPlayer.WaterLevelSensor))
                throw new InvalidOperationException("The original swimming component graph changed.");
        }

        private bool NativeObject(int index, UnityEngine.Object current)
            => SameSourceObject(_nativeSwimmingObjects[index], current)
                && current.GetInstanceID() == _nativeSwimmingObjectIds[index];

        private static void InstallNativeSwimming(HarmonyLib.Harmony harmony)
        {
            var method = PlayerLifecycle.QualifyNative(typeof(PlayerAnimatorControl),
                "UpdateInWaterControl", 0x35B9DB0, false, typeof(void));
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(PlayerWaterPresentation),
                nameof(BeforeNativeSwimming)) { priority = Priority.Last });
        }

        private static bool BeforeNativeSwimming(PlayerAnimatorControl __instance, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            var owner = _scheduledSource;
            if (owner == null || !owner._nativeSwimmingPrepared
                || !SourceIdentity(owner._sourceAnimation, owner._sourceAnimationId, __instance)) return true;
            // A prepared owner does not take over ordinary gameplay before activation.
            if (!owner._active) return true;
            try
            {
                owner.RequireCurrent();
                owner.RequireSourceScheduleCurrent();
                owner.RequireNativeSwimmingCurrent();
                owner.UpdateOwnedWaterControl();
            }
            catch (Exception error) { owner.Fail("Native swimming continuation failed: " + error.Message); }
            return false;
        }

        // Same-build UpdateInWaterControl (35B9DB0), with the entry region split
        // from its ordinary continuation. No temporary flags or native replay.
        private void UpdateOwnedWaterControl()
        {
            var a = _sourceAnimation;
            if (_swimmingNativeAttempted && !_swimmingNativeApplied) return;
            if (a.introCutScene)
            {
                if (a._wasInWater) a.DisableSwimming();
                return;
            }
            if (!Live(a.rootTr)) return;
            if (a.onRope || a._disableSwimmingTimer > 0
                && Time.time - a._disableSwimmingTimer > a._forceDisableSwimmingAfter)
                a.DisableSwimming();
            a._rootPosition = a.rootTr.position;
            if (LocalPlayer.InWater && !a.onRopeWithGroundBelow && !a.onRope)
            {
                float waterHeight = LocalPlayer.WaterLevelSensor._cachedWaterHeight;
                if (a._rootPosition.y + a._waterMinHeightRange.y < waterHeight)
                {
                    a._playerScriptSetup.targetInfo.inWater = true;
                    if (!a._swimLayerChange && !a.waterBlock)
                    {
                        // Only the ordered server commit may begin this new branch.
                        // Prelude/exit timers above still run; no entry side effect has run.
                        return;
                    }
                    ContinueOwnedSwimming(waterHeight);
                    return;
                }
                if (waterHeight < a._rootPosition.y + a._waterMinHeightRange.x)
                {
                    a._playerScriptSetup.targetInfo.inWater = false;
                    if (a.waterBlock)
                    {
                        if (_sourceCharacter._isGrounded || _sourceCharacter._isJumping)
                            a.DisableSwimming();
                        else a._disableSwimmingTimer = Time.time;
                        a.waterBlock = false;
                    }
                }
                return;
            }
            a._playerScriptSetup.targetInfo.inWater = false;
            if (!a.waterBlock) return;
            if (!_sourceCharacter._isGrounded && !_sourceCharacter._isJumping)
                a._disableSwimmingTimer = Time.time;
            else if (a._wasInWater) a.DisableSwimming();
            a.waterBlock = false;
        }

        private void ContinueOwnedSwimming(float waterHeight)
        {
            var a = _sourceAnimation;
            float headHeight = a._rootPosition.y + a._underWaterOffset;
            float vertical = Input.GetAxis(Input.Actions.Vertical);
            a.UpdateUnderSurface(headHeight - waterHeight);
            bool diving = a._isUnderwater || a._absCamX > 0.65f && vertical > 0;
            if (LocalPlayer.InWater) a._wasInWater = true;
            var inventory = LocalPlayer.Inventory;
            if (!diving)
            {
                a._aboveWaterGroup.SetActive(true);
                a._underWaterGroup.SetActive(false);
                if (a._isSwimmingUnderwater)
                {
                    bool restore = a._divingStarted;
                    if (restore && inventory.HasInSlot((EquipmentSlot)2, 444))
                    {
                        var item = inventory._equipmentSlots[2];
                        if (TryGetComponentExtensions.TryGetComponentInChildren<RebreatherController>(
                            item.ItemObject, out var rebreather, false) && !rebreather._mouthpieceIsLowered)
                            restore = false;
                    }
                    if (restore)
                    {
                        if (a._divingStashedLeft)
                        {
                            a._divingStashedLeft = false;
                            inventory.EquipPreviousLeftHandItem(false);
                        }
                        if (a._divingStashedRight && !LocalPlayer.InWater)
                        {
                            a._divingStashedRight = false;
                            inventory.EquipPreviousRightHandItem(false);
                        }
                    }
                    a._divingStarted = false;
                    a._animator.SetBool("swimDiveBool", false);
                    if (headHeight > waterHeight)
                    {
                        if (!a.IsSledding) a.rb.useGravity = true;
                        a._isSwimmingUnderwater = false;
                        _sourceCharacter._canJump = true;
                    }
                }
                if ((a._swimmingStashedRight || a._divingStashedRight) && !inventory.IsRightHandEmpty())
                    a.SwimmingStashItems();
                return;
            }
            if (!a.IsSledding) a.rb.useGravity = false;
            a._aboveWaterGroup.SetActive(false);
            a._underWaterGroup.SetActive(true);
            a._animator.SetBool("swimDiveBool", true);
            a._isSwimmingUnderwater = true;
            if (!a._divingStarted)
            {
                var right = inventory._equipmentSlots[0];
                if (!a._divingStashedLeft && inventory._equipmentSlots[1] != null)
                {
                    a._divingStashedLeft = true;
                    if (inventory._equipmentSlots[1] != null)
                    {
                        inventory.MemorizeEquippedSlot(EquipmentSlot.LeftHand);
                        inventory.UnequipItemAtSlot(EquipmentSlot.LeftHand, false, true, false, true, false, false);
                    }
                }
                if (!a._divingStashedRight && right != null)
                {
                    a._divingStashedRight = true;
                    inventory.StashRightHandItem(false, true, false);
                }
            }
            a._divingStarted = true;
            _sourceCharacter._canJump = false;
            _sourceCharacter._isJumping = false;
            if (!_sourceCharacter._stepHelperEnabled)
            {
                _sourceCharacter._stepHelperEnabled = true;
                _sourceCharacter.UpdateStepHelperEnabled();
            }
            var velocity = _sourceCharacter._velocity;
            velocity.y = 0;
            _sourceCharacter._velocity = velocity;
            if (inventory.Owns(444, null) && LocalPlayer.IsInCaves)
                inventory.TryEquip(444, false, true, false);
        }

        private void InvokeNativeSwimmingEntry()
        {
            RequireNativeSwimmingCurrent();
            var stats = LocalPlayer.Stats;
            var a = _sourceAnimation;
            stats._doGotCleanCheck = true;
            stats._inventory.CleanWeapons(true);
            a._animator.SetBool("swimmingBool", true);
            a.SetIsSwimming(true);
            a.SwimmingStashItems();
            a._disableSwimmingTimer = -1f;
            a.waterBlock = true;
            var root = LocalPlayer.Transform;
            root.localEulerAngles = new Vector3(0, root.localEulerAngles.y, 0);
        }
    }
}
