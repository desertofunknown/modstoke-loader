// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Crafting;
using global::Sons.Inventory;
using TheForest.Utils;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        private static bool BeforeMatStage(InventoryLayoutItemGroup __instance, bool __runOriginal,
            out PlayerInventoryEnforcement __state)
        {
            __state = null;
            var owner = CraftOwner(_guard?._craftSource);
            if (owner == null || __instance?._inventory?.Pointer != owner._inventory.Pointer) return __runOriginal;
            if (!__runOriginal) return false;
            try
            {
                if (!owner.BeginNativeMatStage(__instance)) return false;
                __state = owner;
                return true;
            }
            catch (Exception error) { owner.Fail("Mat staging failed: " + error.Message); return false; }
        }

        private static Exception AfterMatStage(bool __runOriginal, Exception __exception,
            PlayerInventoryEnforcement __state)
        {
            __state?.FinishNativeMatStage(__runOriginal, __exception);
            return __exception;
        }

        private static bool BeforeMatAdd(CraftingSystem __instance, ItemInstance __0, bool __runOriginal,
            out PlayerInventoryEnforcement __state)
        {
            bool allowed = EnterCraft(__instance, __runOriginal, out __state, out bool protectedSource);
            if (!protectedSource) return allowed;
            if (!allowed) return false;
            try
            {
                if (__state.AdmitNativeMatIngredient(__0)) return true;
                __state.Fail("Unreserved native mat addition refused; no ingredient credit created.");
            }
            catch (Exception error) { __state.Fail(error.Message); }
            return false;
        }

        private static bool BeforeMatRemove(CraftingSystem __instance, int __0, int __1, bool __runOriginal,
            out PlayerInventoryEnforcement __state)
        {
            bool allowed = EnterCraft(__instance, __runOriginal, out __state, out bool protectedSource);
            if (!protectedSource) return allowed;
            if (!allowed || !__state._busy || __1 <= 0 || __state._craftMatRemoveId != __0
                || __state._craftMatRemoveCount != __1) return false;
            __state._craftMatRemoveCount = 0;
            return true;
        }

        private static bool BeforeMatStash(CraftingSystem __instance, bool __runOriginal)
        {
            var owner = CraftOwner(__instance);
            if (owner == null) return __runOriginal;
            if (__runOriginal) owner.ReturnCraftIngredients(0, true);
            return false;
        }

        private static bool BeforeMatReturnAction(IngredientLayoutItemGroup __instance, bool __runOriginal)
        {
            var owner = CraftOwner(_guard?._craftSource);
            if (owner == null || !owner.OwnsIngredientGroup(__instance)) return __runOriginal;
            if (__runOriginal && owner.OnThread && !LocalPlayer.IsInMidAction && __instance._layoutItems != null)
                foreach (var layout in __instance._layoutItems)
                    if (layout != null && !layout.WasCollected && layout && layout.IsHighlighted)
                    { owner.ReturnCraftIngredients(__instance._itemId, false); break; }
            return false;
        }

        private bool OwnsIngredientGroup(IngredientLayoutItemGroup group)
        {
            if (group == null || _craftSource?._ingredientLayoutItemGroups == null) return false;
            foreach (var current in _craftSource._ingredientLayoutItemGroups)
                if (current?.Pointer == group.Pointer) return true;
            return false;
        }

        private static bool BlockMatReturn(IngredientLayoutItemGroup __instance, bool __runOriginal)
        {
            var owner = CraftOwner(_guard?._craftSource);
            return __runOriginal && (owner == null || !owner.OwnsIngredientGroup(__instance));
        }

        private bool OwnsResultLayout(CraftingResultLayoutItem layout)
        {
            if (layout == null || _craftSource?._craftingResultLayoutItemGroups == null) return false;
            foreach (var group in _craftSource._craftingResultLayoutItemGroups)
                if (group?._layoutItems != null)
                    foreach (var candidate in group._layoutItems)
                        if (candidate?.Pointer == layout.Pointer) return true;
            return false;
        }

        private static bool BeforeResultAction(CraftingResultLayoutItem __instance, bool __runOriginal)
        {
            var owner = CraftOwner(_guard?._craftSource);
            return __runOriginal && (owner == null || !owner.OwnsResultLayout(__instance) || owner.CraftMatIdle());
        }

        private static bool BeforeResultStash(CraftingResultLayoutItem __instance, bool __runOriginal)
        {
            var owner = CraftOwner(_guard?._craftSource);
            if (owner == null || !owner.OwnsResultLayout(__instance)) return __runOriginal;
            if (!__runOriginal || !owner.CraftMatIdle() || !__instance.gameObject.activeSelf) return false;
            var item = owner._craftResults.Find(candidate => candidate.Layout?.Pointer == __instance.Pointer);
            if (item == null) { owner.Fail("Native result layout has no exact authoritative custody."); return false; }
            owner._busy = true;
            try
            {
                owner.ReturnCraftMatItem(item);
                if (!owner.Unchanged() || !owner.CraftMatCurrent()) throw new InvalidOperationException("Result stash readback differs.");
            }
            catch (Exception error) { owner.Fail("Craft result custody is uncertain: " + error.Message); }
            finally { owner._busy = false; }
            return false;
        }
    }
}
