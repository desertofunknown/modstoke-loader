// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Common;
using Modstoke.Loader;
using global::Sons.Crafting;
using global::Sons.Inventory;
using UnityEngine.InputSystem;
using NativeEnumerator = Il2CppSystem.Collections.IEnumerator;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        private static bool _craftHookAttempted, _craftHooksInstalled;

        internal static void InstallCraftingPatches(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (_craftHookAttempted) return;
            _craftHookAttempted = true;
            if (support?.SafeForHarmonyPatches != true || !GameBuild.IsRecognized
                || !OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
            HarmonyLib.Harmony harmony = null;
            try
            {
                var type = typeof(CraftingSystem);
                var recipe = typeof(CraftingRecipe);
                var targets = new (MethodInfo Method, string Prefix, string Finalizer)[]
                {
                    (CraftMethod(type, "TryCombineIngredients", 0x3249860, typeof(bool)), nameof(BeforeCraftCombine), nameof(AfterCraftEntry)),
                    (CraftMethod(type, "OnCraftCompleteInternal", 0x324A0E0, typeof(void), recipe), nameof(BeforeAuthoritativeCraftComplete), nameof(AfterAuthoritativeCraftComplete)),
                    (CraftMethod(type, "ForceCompleteCurrentCraft", 0x3249DB0, typeof(void)), nameof(BlockCraftMutation), nameof(AfterCraftEntry)),
                    (CraftMethod(type, "CraftResultingItems", 0x324AB20, typeof(void), recipe), nameof(BeforeCraftResults), null),
                    (CraftMethod(type, "CraftResultingItem", 0x324AC90, typeof(void), recipe, typeof(CraftingRecipe.ResultingItem)), nameof(BlockCraftMutation), nameof(AfterCraftEntry)),
                    (CraftMethod(type, "DismantleWeaponMods", 0x324B150, typeof(void), recipe), nameof(BlockCraftMutation), nameof(AfterCraftEntry)),
                    (CraftMethod(type, "ConsumeIngredientsForRecipe", 0x324A920, typeof(void), recipe), nameof(BeforeCraftConsume), null),
                    (CraftMethod(type, "OnRevealResultingItemInternal", 0x324A8C0, typeof(void), recipe), nameof(BeforeCraftReveal), null),
                    (CraftMethod(type, "AddIngredient", 0x32480E0, typeof(void), typeof(ItemInstance)), nameof(BeforeMatAdd), nameof(AfterCraftEntry)),
                    (CraftMethod(type, "RemoveIngredient", 0x3248690, typeof(void), typeof(int), typeof(int)), nameof(BeforeMatRemove), nameof(AfterCraftEntry)),
                    (CraftMethod(type, "StashAllIngredients", 0x3248A00, typeof(void)), nameof(BeforeMatStash), null),
                    (CraftMethod(typeof(InventoryLayoutItemGroup), "OnAddItemToMat", 0x31D76F0, typeof(void), typeof(InputAction.CallbackContext)), nameof(BeforeMatStage), nameof(AfterMatStage)),
                    (CraftMethod(typeof(IngredientLayoutItemGroup), "OnRemoveItemFromMat", 0x31CE960, typeof(void), typeof(InputAction.CallbackContext)), nameof(BeforeMatReturnAction), null),
                    (CraftMethod(typeof(IngredientLayoutItemGroup), "AddBackToInventory", 0x31CE370, typeof(void), typeof(int)), nameof(BeforeVariantReuseReturn), null),
                    (CraftMethod(typeof(IngredientLayoutItemGroup), "OnInteractWithItem", 0x31CE740, typeof(void), typeof(InputAction.CallbackContext)), nameof(BlockMatReturn), null),
                    (CraftMethod(typeof(CraftingResultLayoutItem), "StashToInventory", 0x32525D0, typeof(void)), nameof(BeforeResultStash), null),
                    (CraftMethod(typeof(CraftingResultLayoutItem), "OnRemoveItemFromMat", 0x32524B0, typeof(void), typeof(InputAction.CallbackContext)), nameof(BeforeResultAction), null),
                    (CraftMethod(typeof(CraftingResultLayoutItem), "OnInteractWithItem", 0x3252380, typeof(void), typeof(InputAction.CallbackContext)), nameof(BeforeResultAction), null),
                    (CraftMethod(typeof(CraftingAnimationEventsProxy), "TriggerCraftingAnimationComplete", 0x32444A0, typeof(void)), nameof(BeforeCraftProxy), nameof(AfterCraftProxy)),
                    (CraftMethod(typeof(CraftingAnimationEventsProxy), "TriggerRevealResultingItem", 0x32444F0, typeof(void)), nameof(BeforeCraftProxy), nameof(AfterCraftProxy)),
                    (CraftMethod(recipe, "GenerateResultingItems", 0x32460A0, typeof(Il2CppSystem.Collections.Generic.List<CraftingRecipe.ResultingItem>)), nameof(BeforeVariantGeneratedResults), null),
                    (CraftMethod(recipe, "GenerateResultingItemList", 0x32464C0, typeof(void)), nameof(BeforeVariantGenerateList), null),
                    (CraftMethod(type, "StopCogWhenTutorialIsComplete", 0x3248520, typeof(NativeEnumerator)), nameof(BeforeCraftFactory), nameof(AfterCraftFactory)),
                    (CraftMethod(type, "TriggerFlash", 0x324EAA0, typeof(NativeEnumerator)), nameof(BeforeCraftFactory), nameof(AfterCraftFactory)),
                    (CraftMethod(typeof(CraftingSystem._StopCogWhenTutorialIsComplete_d__81), "MoveNext", 0x324F8F0, typeof(bool)), nameof(BeforeCraftCogStep), nameof(AfterCraftStep)),
                    (CraftMethod(typeof(CraftingSystem._TriggerFlash_d__108), "MoveNext", 0x324FAC0, typeof(bool)), nameof(BeforeCraftFlashStep), nameof(AfterCraftStep))
                };
                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-native-craft-route");
                foreach (var target in targets)
                    harmony.Patch(target.Method,
                        prefix: new HarmonyMethod(typeof(PlayerInventoryEnforcement), target.Prefix) { priority = Priority.First },
                        finalizer: target.Finalizer == null ? null : new HarmonyMethod(typeof(PlayerInventoryEnforcement), target.Finalizer) { priority = Priority.Last });
                _craftHooksInstalled = true;
            }
            catch (Exception error)
            {
                string rollback = "";
                try { harmony?.UnpatchSelf(); }
                catch (Exception cleanup) { rollback = " Patch rollback is unconfirmed: " + cleanup.Message; }
                warning?.Invoke("Native crafting route unavailable: " + error.Message + rollback);
            }
        }

        private static MethodInfo CraftMethod(Type type, string name, int rva, Type result, params Type[] arguments)
        {
            var method = AccessTools.DeclaredMethod(type, name, arguments);
            if (method == null || method.IsStatic || method.ReturnType != result) throw new MissingMethodException(type.FullName, name);
            var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
            nint module = GetModuleHandle("GameAssembly.dll");
            if (module == 0 || field?.GetValue(null) is not nint info || info == 0 || Marshal.ReadIntPtr(info) != module + rva)
                throw new InvalidOperationException("Native craft entry differs: " + type.Name + "." + name);
            return method;
        }

        private static PlayerInventoryEnforcement CraftOwner(CraftingSystem system)
        {
            var guard = _guard;
            // Disposal never releases a migrated source writer guard. Old-load objects are
            // separate lifetimes; do not redirect another actor's CraftingSystem singleton.
            return system != null && guard?._craftSource != null && system.Pointer == guard._craftSource.Pointer
                && guard._load == SaveLifecycle.State.BeforeLoadCount ? guard : null;
        }

        private static bool EnterCraft(CraftingSystem system, bool runOriginal,
            out PlayerInventoryEnforcement state, out bool protectedSource)
        {
            state = null;
            var owner = CraftOwner(system);
            protectedSource = owner != null;
            if (!runOriginal) return false;
            if (owner == null) return true;
            if (!owner.OnThread) return false;
            owner._craftNativeEntries++;
            state = owner;
            return !owner.IsDisposed && !owner.IsFaulted;
        }

        private static bool BeforeCraftCombine(CraftingSystem __instance, bool __runOriginal, ref bool __result,
            out PlayerInventoryEnforcement __state)
        {
            bool allowed = EnterCraft(__instance, __runOriginal, out __state, out bool protectedSource);
            if (!protectedSource) return allowed;
            if (allowed && __state._craftStartingNative && __state._craftBeginPermit && __state._craftNativeEntries == 1)
            {
                __state._craftBeginPermit = false;
                return true;
            }
            __result = false;
            if (allowed && __state._craftNativeEntries == 1)
                try { __state.CaptureCraftIntent(); }
                catch (Exception error) { __state.Detail = "Native craft intent declined: " + error.Message; }
            // The user's click records intent. The same native body is entered once later,
            // after final custody/effects, rather than returning a fabricated native success.
            return false;
        }

        private static bool BlockCraftMutation(CraftingSystem __instance, bool __runOriginal,
            out PlayerInventoryEnforcement __state)
        {
            bool allowed = EnterCraft(__instance, __runOriginal, out __state, out bool protectedSource);
            return allowed && !protectedSource;
        }

        private static Exception AfterCraftEntry(Exception __exception, PlayerInventoryEnforcement __state)
        {
            if (__state != null)
            {
                __state._craftNativeEntries--;
                if (__exception != null) __state.Fail("Native crafting entry failed: " + __exception.Message);
            }
            return __exception;
        }

        private static bool BeforeCraftFactory(CraftingSystem __instance, bool __runOriginal,
            out PlayerInventoryEnforcement __state)
        {
            return EnterCraft(__instance, __runOriginal, out __state, out _);
        }

        private static Exception AfterCraftFactory(Exception __exception, bool __runOriginal, NativeEnumerator __result,
            PlayerInventoryEnforcement __state)
        {
            if (__state != null && __runOriginal && __exception == null)
            {
                if (__result == null || __result.WasCollected || !__state._craftContinuations.TryAdd(__result.Pointer, __result))
                    __state.Fail("Crafting continuation identity was not captured exactly once.");
            }
            return AfterCraftEntry(__exception, __state);
        }

        private static bool BeforeCraftCogStep(CraftingSystem._StopCogWhenTutorialIsComplete_d__81 __instance,
            bool __runOriginal, ref bool __result, out PlayerInventoryEnforcement __state)
            => BeforeCraftStep(__instance.__4__this, __instance.Cast<NativeEnumerator>(), __instance.__1__state, __runOriginal, ref __result, out __state);

        private static bool BeforeCraftFlashStep(CraftingSystem._TriggerFlash_d__108 __instance,
            bool __runOriginal, ref bool __result, out PlayerInventoryEnforcement __state)
            => BeforeCraftStep(__instance.__4__this, __instance.Cast<NativeEnumerator>(), __instance.__1__state, __runOriginal, ref __result, out __state);

        private static bool BeforeCraftStep(CraftingSystem owner, NativeEnumerator routine, int iteratorState, bool runOriginal, ref bool result,
            out PlayerInventoryEnforcement state)
        {
            bool allowed = EnterCraft(owner, runOriginal, out state, out bool protectedSource);
            if (!protectedSource) return allowed;
            // AddIngredient inlines the tutorial factory in this build. Its first MoveNext
            // still has a qualified native caller and the actual state-zero iterator identity.
            bool initial = iteratorState == 0 && state != null && state._busy
                && (state._craftTransfer != null || state._craftCompleting || state._craftStartingNative);
            if (allowed && initial) state._craftContinuations.TryAdd(routine.Pointer, routine);
            if (!allowed || !state._craftContinuations.ContainsKey(routine.Pointer) || (state._craftNativeEntries != 1 && !initial))
            {
                result = false;
                state?.Fail("Untracked, faulted or reentrant native crafting continuation; no terminal receipt.");
                return false;
            }
            return true;
        }

        private static Exception AfterCraftStep(Il2CppSystem.Object __instance, bool __runOriginal, bool __result,
            Exception __exception, PlayerInventoryEnforcement __state)
        {
            // A stopped/suppressed/throwing iterator remains unresolved. Do not infer completion
            // from CraftComplete, a timeout, StopCoroutine or the absence of another MoveNext.
            if (__state != null && __runOriginal && __exception == null && !__result)
                __state._craftContinuations.Remove(__instance.Pointer);
            return AfterCraftEntry(__exception, __state);
        }
    }
}
