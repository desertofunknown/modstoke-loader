// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Loader;
using global::Sons.Inventory;
using global::Sons.Wearable;
using global::Sons.Wearable.Armour;
using global::Sons.Wearable.Clothing;
using Environment = System.Environment;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerArmourSource
    {
        private static readonly List<PlayerArmourSource> Owners = new();
        private static bool _installAttempted, _installed;
        private static int _hookThread;
        private bool _breakPermit, _breakEntered, _breakCompleted, _techEntered, _techCompleted;
        private ArmourPiece _breakPiece;

        internal static void Install(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (_installAttempted) return;
            _installAttempted = true;
            if (support?.SafeForHarmonyPatches != true || !GameBuild.IsRecognized) return;
            HarmonyLib.Harmony harmony = null;
            try
            {
                _hookThread = Environment.CurrentManagedThreadId;
                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-player-armour-source");
                void Patch(Type type, string name, int rva, Type result, string prefix, string finalizer, params Type[] args)
                {
                    var method = PlayerLifecycle.QualifyNative(type, name, rva, false, result, args);
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(PlayerArmourSource), prefix) { priority = Priority.First },
                        finalizer: finalizer == null ? null : new HarmonyMethod(typeof(PlayerArmourSource), finalizer) { priority = Priority.Last });
                }
                var armour = typeof(PlayerArmourSystem);
                Patch(armour, "EquipToNextAvailableSlot", 0x31B03A0, typeof(bool), nameof(BlockBoolean), null, typeof(ArmourPiece));
                Patch(armour, "EquipToSlot", 0x31B0480, typeof(bool), nameof(BlockBoolean), null, typeof(int), typeof(WearableSlots));
                Patch(armour, "EquipToSlot", 0x31B05D0, typeof(bool), nameof(BlockBoolean), null, typeof(ArmourPiece), typeof(WearableSlots));
                Patch(armour, "TryUnequipSlot", 0x31B1070, typeof(bool), nameof(BeforeBreak), nameof(AfterBreak), typeof(WearableSlots), typeof(bool));
                Patch(armour, "CalculateRemainingDamageAfterArmourHit", 0x31B16C0, typeof(float), nameof(BlockDamage), null, typeof(float), typeof(bool));
                Patch(armour, "WearArmourList", 0x31B2EC0, typeof(void), nameof(BlockMutation), null, typeof(Il2CppStructArray<int>));
                Patch(armour, "Sons_Save_ISaveGameSerializer_Sons_Wearable_Armour_PlayerArmourSystem_PlayerArmourSaveData__OnDeserialize",
                    0x31B4000, typeof(void), nameof(BlockMutation), null, typeof(PlayerArmourSystem.PlayerArmourSaveData));
                Patch(armour, "OnClothingEquippedEvent", 0x31AFBF0, typeof(void), nameof(BlockMutation), null, typeof(int), typeof(ItemInstance));
                Patch(armour, "OnValidate", 0x31AFDE0, typeof(void), nameof(BlockMutation), null);
                Patch(armour, "CheckForTechArmour", 0x31B2800, typeof(void), nameof(BeforeTech), nameof(AfterTech));
                // Observe the actual native subcalls, including suppression/exception by another hook.
                Patch(typeof(PlayerClothingSystem), "ApplyBlendShapeWeight", 0x31A8CF0, typeof(void),
                    nameof(BeforeBlend), nameof(AfterEffect), typeof(int), typeof(int), typeof(int), typeof(int));
                Patch(typeof(TechArmourLightController), "EnableLights", 0x31B4A10, typeof(void),
                    nameof(BeforeLight), nameof(AfterEffect), typeof(bool));
                _installed = true;
            }
            catch (Exception error)
            {
                try { harmony?.UnpatchSelf(); } catch (Exception) { }
                warning?.Invoke("Ownership-migration armour hooks unavailable: " + error.Message);
            }
        }

        private static PlayerArmourSource Find(PlayerArmourSystem system)
        {
            foreach (var owner in Owners) if (Same(owner.System, system)) return owner;
            return null;
        }

        private static bool BlockMutation(PlayerArmourSystem __instance)
        {
            var owner = Find(__instance);
            if (owner?._executing == true) owner.Fail("A reentrant unowned armour writer entered native break execution.");
            return owner == null;
        }
        private static bool BlockBoolean(PlayerArmourSystem __instance, ref bool __result)
        { if (BlockMutation(__instance)) return true; __result = false; return false; }
        private static bool BlockDamage(PlayerArmourSystem __instance, ref float __result)
        { if (BlockMutation(__instance)) return true; __result = 0; return false; }

        private void BeginNativeBreak()
        {
            _breakPiece = _original.GetRetainedSlotData(_command.Numbers.GetChange(_change).Before.Index).ArmourPiece;
            if (!Live(_breakPiece)) throw new InvalidOperationException("Original armour definition is unavailable.");
            _breakPermit = true; _breakEntered = _breakCompleted = _techEntered = _techCompleted = false;
        }

        private static bool BeforeBreak(PlayerArmourSystem __instance, WearableSlots __0, bool __1,
            bool __runOriginal, ref bool __result, out PlayerArmourSource __state)
        {
            __state = null;
            var owner = Find(__instance);
            if (owner == null) return true;
            if (__runOriginal && owner.Current() && owner._executing && owner._change >= 0 && owner._breakPermit
                && !owner._breakEntered && __1 && (int)__0 == owner._command.Numbers.GetChange(owner._change).Before.Index)
            {
                owner._breakPermit = false; owner._breakEntered = true; __state = owner; return true;
            }
            if (owner._executing) owner.Fail("Exact once-only native armour break permit was refused.");
            __result = false; return false;
        }

        private static Exception AfterBreak(PlayerArmourSource __state, bool __runOriginal, bool __result, Exception __exception)
        {
            if (__state == null) return __exception;
            __state._breakEntered = false;
            if (!__runOriginal || !__result || __exception != null || !__state._techCompleted)
                __state.Fail("Native armour break did not complete its clothing/tech tail.");
            else __state._breakCompleted = true;
            return __exception;
        }

        private static bool BeforeTech(PlayerArmourSystem __instance, bool __runOriginal, out PlayerArmourSource __state)
        {
            __state = null;
            var owner = Find(__instance);
            if (owner == null) return true;
            if (__runOriginal && owner.Current() && owner._breakEntered && !owner._techEntered && !owner._techCompleted)
            { owner._techEntered = true; __state = owner; return true; }
            if (owner._executing) owner.Fail("Native tech-armour update was not owned by the exact break.");
            return false;
        }

        private static Exception AfterTech(PlayerArmourSource __state, bool __runOriginal, Exception __exception)
        {
            if (__state == null) return __exception;
            try
            {
                if (!__runOriginal || __exception != null) throw new InvalidOperationException("Native tech-armour update was interrupted.");
                // Matched native CheckForTechArmour skips slots 2/3 and requires item 554
                // everywhere else. The native body itself updates remaining tech lights.
                bool complete = true;
                foreach (var slot in __state.System._armourSlotData)
                    if ((int)slot.Slot is not (2 or 3) && (!Live(slot.ArmourPiece) || slot.ArmourPiece._itemId != 554)) complete = false;
                if (__state.System._techArmourComplete != complete)
                    throw new InvalidOperationException("Native tech-armour completion readback differs.");
                __state._techCompleted = true;
            }
            catch (Exception error) { __state.Fail(error.Message); }
            finally { __state._techEntered = false; }
            return __exception;
        }

        private static void BeforeBlend(PlayerClothingSystem __instance, out PlayerArmourSource __state)
        {
            __state = null;
            foreach (var owner in Owners)
                if (owner._breakEntered && Same(owner._clothing, __instance)) { __state = owner; return; }
        }
        private static void BeforeLight(TechArmourLightController __instance, out PlayerArmourSource __state)
        {
            __state = null;
            foreach (var owner in Owners)
            {
                if (!owner._techEntered) continue;
                __state = owner;
                try
                {
                    foreach (var slot in owner.System._armourSlotData)
                        if (Live(slot.ArmourPiece) && slot.ArmourPiece._itemId == 554 && Live(slot.ArmourInstance)
                            && Same(slot.ArmourInstance.GetComponentInChildren<TechArmourLightController>(), __instance)) return;
                    owner.Fail("Tech-armour light callback does not belong to a remaining exact native piece.");
                }
                catch (Exception error) { owner.Fail(error.Message); }
                return;
            }
        }
        private static Exception AfterEffect(PlayerArmourSource __state, bool __runOriginal, Exception __exception)
        {
            if (__state != null && (!__runOriginal || __exception != null))
                __state.Fail("Native armour clothing/light effect was suppressed or failed; no replay is authorized.");
            return __exception;
        }
    }
}
