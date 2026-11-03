// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        private static bool _hookAttempted, _hooksInstalled;
        private static int _hookThread, _writerDepth;

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (_hookAttempted) return;
            _hookAttempted = true;
            if (support?.SafeForHarmonyPatches != true || !GameBuild.IsRecognized) return;
            _hookThread = Environment.CurrentManagedThreadId;
            HarmonyLib.Harmony harmony = null;
            try
            {
                var manager = typeof(ItemInstanceManager);
                var inventory = typeof(PlayerInventory);
                var targets = new (MethodInfo Method, string Prefix)[]
                {
                    (AccessTools.Method(manager, "TryAddItems", new[] { typeof(int), typeof(int), typeof(bool) }), nameof(BeforeAddStack)),
                    (AccessTools.Method(manager, "TryAddItem", new[] { typeof(int) }), nameof(BlockManagerBoolean)),
                    (AccessTools.Method(manager, "TryAddItem", new[] { typeof(ItemInstance), typeof(bool) }), nameof(BeforeAddInstance)),
                    (AccessTools.Method(manager, "RemoveItems", new[] { typeof(int), typeof(int), typeof(bool) }), nameof(BeforeRemoveStack)),
                    (AccessTools.Method(manager, "TryRemoveItem", new[] { typeof(ItemInstance) }), nameof(BeforeRemoveInstance)),
                    (AccessTools.Method(manager, "RemoveAll", Type.EmptyTypes), nameof(BlockManager)),
                    (AccessTools.Method(manager, "RemoveAll", new[] { typeof(int), typeof(bool) }), nameof(BlockManager)),
                    (AccessTools.Method(manager, "SetItemMaxCount", new[] { typeof(int), typeof(int) }), nameof(BlockManager)),
                    (AccessTools.Method(manager, "DeserializeItemsFromExternalData", new[] { typeof(ItemInstanceManager.ItemInstanceManagerSaveData) }), nameof(BlockManager)),
                    (AccessTools.Method(inventory, "AddItem", new[] { typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(ItemInstance) }), nameof(BeforeAddInventory)),
                    (AccessTools.Method(inventory, "RemoveItem", new[] { typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(bool), typeof(ItemInstance), typeof(bool) }), nameof(BeforeRemoveInventory)),
                    (AccessTools.Method(inventory, "TryEquip", new[] { typeof(int), typeof(bool), typeof(bool), typeof(bool) }), nameof(BeforeEquipById)),
                    (AccessTools.Method(inventory, "TryEquip", new[] { typeof(ItemInstance), typeof(bool), typeof(bool), typeof(Il2CppSystem.Nullable<EquipmentSlot>), typeof(bool), typeof(bool) }), nameof(BeforeEquipExact)),
                    (AccessTools.Method(inventory, "TryTransferToSlotEquip", new[] { typeof(ItemInstance), typeof(EquipmentSlot), typeof(EquipmentSlot), typeof(bool) }), nameof(BlockInventoryBoolean)),
                    (AccessTools.Method(inventory, "UnequipItemAtSlot", new[] { typeof(EquipmentSlot), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool) }), nameof(BeforeUnequip)),
                    (AccessTools.Method(inventory, "DropItem", new[] { typeof(ItemInstance), typeof(bool), typeof(int) }), nameof(BlockInventory)),
                    (AccessTools.Method(inventory, "DropItemFromInventory", new[] { typeof(int), typeof(GameObject).MakeByRefType() }), nameof(BlockDrop)),
                    (AccessTools.Method(inventory, "RemoveAllItemsOnDeath", Type.EmptyTypes), nameof(BlockInventory)),
                    (AccessTools.Method(inventory, "Sons_Save_ISaveGameSerializer_TheForest_Items_Inventory_PlayerInventory_PlayerInventorySaveData__OnDeserialize",
                        new[] { typeof(PlayerInventory.PlayerInventorySaveData) }), nameof(BlockInventory)),
                    (AccessTools.Method(typeof(CoopPlayerCallbacks), "OnEvent", new[] { typeof(PlayerAddItem) }), nameof(BeforeNativeGrant)),
                    (AccessTools.Method(typeof(PerishableItemInstanceModule), "Update", Type.EmptyTypes), nameof(BeforePerishableUpdate)),
                    (AccessTools.Method(typeof(VolumeContainerItemInstanceModule), "SetContents", new[] { typeof(float), typeof(StatRecipe) }), nameof(BeforeVolumeMutation)),
                    (AccessTools.Method(typeof(VolumeContainerItemInstanceModule), "Refill", new[] { typeof(float), typeof(StatRecipe) }), nameof(BeforeVolumeMutation)),
                    (AccessTools.Method(typeof(VolumeContainerItemInstanceModule), "Empty", new[] { typeof(float) }), nameof(BeforeVolumeEmpty)),
                    (AccessTools.Method(typeof(PerishableItemInstanceModule), "SetState", new[] { typeof(PerishableItemData.State), typeof(float), typeof(bool) }), nameof(BeforePerishableState)),
                    (AccessTools.Method(typeof(PerishableItemInstanceModule), "Decay", Type.EmptyTypes), nameof(BlockPerishableMutation)),
                    (AccessTools.Method(typeof(PerishableItemInstanceModule), "LoadFromSave", new[] { typeof(ItemInstanceModule.SaveData) }), nameof(BlockPerishableMutation)),
                    (AccessTools.Method(typeof(InventoryEdibleItem), "TryPerformAction", new[] { typeof(ItemInstance), typeof(bool) }), nameof(BeforePlainIntake)),
                    (AccessTools.Method(typeof(TheForest.Player.Actions.PlayerConsumeItemAction), "ConsumeItem", new[] { typeof(ItemInstance), typeof(bool), typeof(TheForest.Player.Actions.PlayerConsumeItemAction.OnConsumeComplete) }), nameof(BeforeConsumptionEntry)),
                    (AccessTools.Method(typeof(TheForest.Player.Actions.PlayerConsumeItemAction), "ApplyStats", new[] { typeof(ItemInstance) }), nameof(BeforeConsumptionStats)),
                    (AccessTools.Method(typeof(TheForest.Player.Actions.PlayerConsumeItemAction), "ConsumeComplete", Type.EmptyTypes), nameof(BeforeConsumptionComplete)),
                    (AccessTools.Method(typeof(InventoryEdibleItem), "OnConsumeComplete", Type.EmptyTypes), nameof(BeforeConsumptionEdibleComplete))
                };
                if (targets.Length != NativeBoundaries.Length || targets[16].Method == null
                    || !targets[16].Method.GetParameters()[1].IsOut)
                    throw new MissingMethodException("Inventory boundary set or drop output signature differs.");
                for (int i = 0; i < targets.Length; i++) QualifyBoundary(targets[i].Method, i);
                QualifyModuleUnregister();
                var equipSequence = AccessTools.Method(typeof(PlayerInventory._EquipSequence_d__190), "MoveNext", Type.EmptyTypes);
                QualifyBoundary(equipSequence, (typeof(bool), 0x30F3310, 2752,
                    "9278B315A7617E086EC18AC308A3F032EE59A9B42DA72DF686A9E45B35F1722C"));
                var closeInventory = AccessTools.Method(inventory, "Close", new[] { typeof(bool) });
                QualifyBoundary(closeInventory, (typeof(void), 0x30E5130, 928,
                    "C970CF790C57BAA2B2D3E3F0E0B836CED4D2B69591AB04022DC58DD4B9E49D8F"));
                var worldHooks = QualifyWorldHooks();
                var swimmingStash = AccessTools.Method(typeof(PlayerAnimatorControl), "SwimmingStashItems", Type.EmptyTypes);
                QualifyBoundary(swimmingStash, (typeof(void), 0x35BB1F0, 400,
                    "8E8661C75E515307DE7D98B4D93D98A1AC7248618BF1CD08D94BC91ED50D06B5"));
                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-player-inventory-authority");
                foreach (var target in targets)
                    harmony.Patch(target.Method, prefix: new HarmonyMethod(typeof(PlayerInventoryEnforcement), target.Prefix)
                    { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(PlayerInventoryEnforcement),
                            target.Prefix == nameof(BeforeRemoveInventory) ? nameof(AfterRemoveInventory)
                            : target.Prefix == nameof(BeforeRemoveInstance) ? nameof(AfterRemoveInstance)
                            : target.Prefix == nameof(BeforeRemoveStack) ? nameof(AfterRemoveStack)
                            : target.Prefix == nameof(BeforeAddInventory) ? nameof(AfterAddInventory)
                            : target.Prefix == nameof(BeforeAddInstance) ? nameof(AfterAddInstance)
                            : target.Prefix == nameof(BeforeUnequip) ? nameof(AfterUnequip)
                            : target.Prefix is nameof(BeforeConsumptionEntry) or nameof(BeforeConsumptionStats)
                                or nameof(BeforeConsumptionComplete) or nameof(BeforeConsumptionEdibleComplete)
                                ? nameof(AfterConsumptionPresentation) : nameof(AfterWriter))
                        { priority = Priority.Last });
                harmony.Patch(equipSequence,
                    prefix: new HarmonyMethod(typeof(PlayerInventoryEnforcement), nameof(BeforeEquipSequence)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(PlayerInventoryEnforcement), nameof(AfterEquipSequence)) { priority = Priority.Last });
                harmony.Patch(closeInventory,
                    prefix: new HarmonyMethod(typeof(PlayerInventoryEnforcement), nameof(BeforeCloseInventory)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(PlayerInventoryEnforcement), nameof(AfterWriter)) { priority = Priority.Last });
                foreach (var target in worldHooks)
                    harmony.Patch(target.Method,
                        prefix: new HarmonyMethod(typeof(PlayerInventoryEnforcement), target.Prefix) { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(PlayerInventoryEnforcement),
                            target.Prefix == nameof(BlockWorldDropInternal) ? nameof(AfterWorldDrop) : nameof(AfterWriter))
                        { priority = Priority.Last });
                InstallConsumptionPresentationHooks(harmony);
                harmony.Patch(swimmingStash,
                    prefix: new HarmonyMethod(typeof(PlayerInventoryEnforcement), nameof(BeforeSwimmingStash)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(PlayerInventoryEnforcement), nameof(AfterSwimmingStash)) { priority = Priority.Last });
                _hooksInstalled = true;
                RuntimeEntryPoint.LogSource?.LogInfo("Inventory enforcement hooks installed for the recognized game build.");
            }
            catch (Exception error)
            {
                _hooksInstalled = false;
                _consumptionPresentationHooksInstalled = false;
                string rollback = "";
                try { RollbackInventoryHooks(harmony); }
                catch (Exception cleanup) { rollback = " Patch rollback is unconfirmed: " + cleanup.GetType().Name + "."; }
                warning?.Invoke("Inventory enforcement hooks unavailable: " + error.Message + rollback);
            }
        }

        private static void RollbackInventoryHooks(HarmonyLib.Harmony harmony)
        {
            if (harmony == null) return;
            // HarmonyX 2.16.1 UnpatchSelf rebuilds after removing each prefix, leaving
            // finalizers without __runOriginal. Remove this owner's paired patches together.
            foreach (var method in new List<MethodBase>(harmony.GetPatchedMethods()))
                harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
        }

        private static bool ProtectsInventory(PlayerInventory instance)
            => Inventory.StoredDrop.ForInventory(instance) != null || IsSpecializedInventoryHeld(instance);

        internal static bool IsSpecializedInventoryHeld(PlayerInventory instance)
        {
            if (IsNativeInventoryLifecycleHeld(instance)) return true;
            var guard = _guard;
            return guard != null && instance != null && guard._inventory != null
                && instance.Pointer == guard._inventory.Pointer
                && guard._load == SaveLifecycle.State.BeforeLoadCount;
        }

        private static bool ProtectsManager(ItemInstanceManager instance)
        {
            if (Inventory.StoredDrop.ForManager(instance) != null) return true;
            if (IsNativeInventoryLifecycleHeld(instance)) return true;
            var guard = _guard;
            if (guard == null || instance == null || guard._manager == null
                || guard._load != SaveLifecycle.State.BeforeLoadCount) return false;
            if (instance.Pointer == guard._manager.Pointer) return true;
            // An unexpected replacement manager on the retained player is not a new authority.
            if (!guard.OnThread || !ProtectsInventory(LocalPlayer.Inventory)
                || LocalPlayer.Inventory._itemInstanceManager?.Pointer != instance.Pointer) return false;
            guard.Fail("Retained player inventory manager was replaced without a custody handoff.");
            return true;
        }

        private static void EnterWriter(out bool entered)
        {
            entered = Environment.CurrentManagedThreadId == _hookThread;
            if (entered) _writerDepth++;
        }
        private static Exception AfterWriter(Exception __exception, bool __state)
        {
            if (__state) _writerDepth--;
            return __exception;
        }
        private static bool BlockManager(ItemInstanceManager __instance, out bool __state)
        { EnterWriter(out __state); return !ProtectsManager(__instance) && !InventoryStorage.IsManagerHeld(__instance); }
        private static bool BlockInventory(PlayerInventory __instance, out bool __state)
        { EnterWriter(out __state); return !ProtectsInventory(__instance); }
        private static bool BeforeCloseInventory(PlayerInventory __instance, out bool __state)
        {
            // Close invokes the native stash writers, which acquire their own custody permits.
            __state = false;
            if (!ProtectsInventory(__instance)) return true;
            return !IsNativeInventoryLifecycleHeld(__instance) && _guard?.CanCloseNativeCraftInventory == true;
        }
        private static bool BlockManagerBoolean(ItemInstanceManager __instance, ref bool __result, out bool __state)
        {
            EnterWriter(out __state);
            if (!ProtectsManager(__instance) && !InventoryStorage.IsManagerHeld(__instance)) return true;
            __result = false;
            return false;
        }
        private static bool BlockInventoryBoolean(PlayerInventory __instance, ref bool __result, out bool __state)
        {
            EnterWriter(out __state);
            if (!ProtectsInventory(__instance)) return true;
            __result = false;
            return false;
        }
        private static bool BlockDrop(PlayerInventory __instance, int __0, ref GameObject __1,
            bool __runOriginal, out bool __state)
        {
            EnterWriter(out __state);
            if (!ProtectsInventory(__instance)) return true;
            __1 = null;
            if (__runOriginal && !IsNativeInventoryLifecycleHeld(__instance)) _guard?.RequestWorldStoredDrop(__0);
            return false;
        }

        private static bool BeforePlainIntake(InventoryEdibleItem __instance, ItemInstance __0, bool __1,
            bool __runOriginal, ref bool __result, out bool __state)
        {
            EnterWriter(out __state);
            if (!ProtectsInventory(LocalPlayer.Inventory)) return true;
            __result = __runOriginal && _guard?.HasPendingNativeWorldIntent != true && !IsNativeInventoryLifecycleHeld(LocalPlayer.Inventory)
                && _guard?.RequestPlainIntake(__instance, __0, __1) == true;
            return false;
        }

        private static bool BlockConsumption(out bool __state)
        {
            EnterWriter(out __state);
            // These native actions use LocalPlayer globals. They may not bypass the earlier
            // custody reservation, including from a coroutine started before the guard.
            return !ProtectsInventory(LocalPlayer.Inventory);
        }

        private static bool BeforeNativeGrant(PlayerAddItem __0, bool __runOriginal, out bool __state)
        {
            EnterWriter(out __state);
            var guard = _guard;
            if (!ProtectsInventory(LocalPlayer.Inventory)) return true;
            try
            {
                if (__0 == null || __0.Player == null || __0.Player.Pointer != LocalPlayer.Entity?.Pointer) return true;
                if (IsNativeInventoryLifecycleHeld(LocalPlayer.Inventory) || guard == null) return false;
                if (__runOriginal) guard.AcceptNativeGrant(__0);
                else guard.Fail("Another native grant participant rejected delivery; no addition was attempted.");
            }
            catch (Exception error) { guard?.Fail("Native grant callback failed: " + error.Message); }
            return false;
        }

        private static bool BeforeAddInventory(PlayerInventory __instance, int __0, int __1, bool __2, bool __3,
            ItemInstance __4, bool __runOriginal, ref bool __result, out EquipmentWriterState __state)
        {
            EnterWriter(out bool entered);
            __state = new(entered, null);
            if (!ProtectsInventory(__instance)) return true;
            var guard = _guard;
            if (__runOriginal && !IsNativeInventoryLifecycleHeld(__instance)
                && guard?._nativeEquip?.TryPermitAddItem(__instance, __0, __1, __2, __3, __4, _writerDepth) == true)
            { __state = new(entered, guard._nativeEquip); return true; }
            if (__runOriginal && !IsNativeInventoryLifecycleHeld(__instance)
                && guard?.TryPermitCraftAddItem(__instance, __0, __1, __2, __3, __4) == true) return true;
            if (__runOriginal && guard != null && !IsNativeInventoryLifecycleHeld(__instance)
                && __instance.Pointer == guard._inventory.Pointer && guard.GrantWriterCurrent(1) && guard._inventoryAddPermit
                && __0 == guard._grantNative._itemID && __1 == 1 && __2 && !__3
                && __4 != null && !__4.WasCollected && __4.Pointer == guard._grantNative.Pointer)
            { guard._inventoryAddPermit = false; return true; }
            __result = false;
            return false;
        }

        private static bool BeforeAddStack(ItemInstanceManager __instance, int __0, int __1, bool __2,
            bool __runOriginal, ref bool __result, out bool __state)
        {
            EnterWriter(out __state);
            if (InventoryStorage.IsManagerHeld(__instance)) { __result = false; return false; }
            if (!ProtectsManager(__instance)) return true;
            var guard = _guard;
            if (__runOriginal && !IsNativeInventoryLifecycleHeld(__instance)
                && guard?.TryPermitCraftAddStack(__instance, __0, __1, __2) == true) return true;
            if (__runOriginal && guard != null && !IsNativeInventoryLifecycleHeld(__instance)
                && guard.GrantWriterCurrent(2) && !guard._grantUnique
                && !guard._inventoryAddPermit && guard._managerAddPermit && __instance.Pointer == guard._manager.Pointer
                && __0 == guard._incomingGrant.ItemId && __1 == 1 && !__2)
            { guard._managerAddPermit = false; return true; }
            __result = false;
            return false;
        }

        private static bool BeforeAddInstance(ItemInstanceManager __instance, ItemInstance __0, bool __1,
            bool __runOriginal, ref bool __result, out EquipmentWriterState __state)
        {
            EnterWriter(out bool entered);
            __state = new(entered, null);
            if (Inventory.StoredDrop.ForManager(__instance) != null) { __result = false; return false; }
            if (InventoryStorage.IsManagerHeld(__instance))
            {
                if (__runOriginal && __0 != null && !__0.WasCollected
                    && (InventoryStorage.PermitWorldVariantAddition(__instance, __0, __1)
                        || InventoryStorage.PermitManagerWrite(__instance, __0._itemID, 1, __0, true, __1))) return true;
                __result = false;
                return false;
            }
            if (!ProtectsManager(__instance)) return true;
            var guard = _guard;
            if (__runOriginal && !IsNativeInventoryLifecycleHeld(__instance)
                && guard?._nativeEquip?.TryPermitAddInstance(__instance, __0, __1, _writerDepth) == true)
            { __state = new(entered, guard._nativeEquip); return true; }
            if (__runOriginal && !IsNativeInventoryLifecycleHeld(__instance)
                && guard?.TryPermitCraftAddInstance(__instance, __0, __1) == true) return true;
            if (__runOriginal && guard != null && !IsNativeInventoryLifecycleHeld(__instance)
                && guard.GrantWriterCurrent(2) && guard._grantUnique
                && !guard._inventoryAddPermit && guard._managerAddPermit && __instance.Pointer == guard._manager.Pointer
                && __0 != null && !__0.WasCollected && __0.Pointer == guard._grantNative.Pointer && !__1)
            { guard._managerAddPermit = false; return true; }
            __result = false;
            return false;
        }

        private bool GrantWriterCurrent(int depth) => OnThread && !IsDisposed && !IsFaulted && IsArmed
            && _bridge?.IsGameplayActive == true && !InventoryFrozen
            && _busy && _hasIncomingGrant && _grantNative != null && !_grantNative.WasCollected
            && _writerDepth == _grantDepth + depth;

        private static bool BeforeUnequip(PlayerInventory __instance, EquipmentSlot __0, bool __1, bool __2,
            bool __3, bool __4, bool __5, bool __6, bool __runOriginal, out EquipmentWriterState __state)
        {
            EnterWriter(out bool entered);
            __state = new(entered, null);
            if (!ProtectsInventory(__instance)) return true;
            var guard = _guard;
            if (guard == null) return false;
            if (IsNativeInventoryLifecycleHeld(__instance))
            {
                if (guard._swimmingStashActive)
                    guard.RejectSwimmingEntry("Swimming stow lost its native lifecycle admission during capture.", out _);
                return false;
            }
            if (__runOriginal && guard._nativeEquip?.TryPermitUnequip(__instance, __0, __1, __2, __3,
                __4, __5, __6, _writerDepth) == true)
            { __state = new(entered, guard._nativeEquip); return true; }
            if (guard.CaptureSwimmingStow(__instance, __0, __1, __2, __3, __4, __5, __6, __runOriginal))
                return false;
            if (__runOriginal && !guard.HasPendingNativeWorldIntent && !__1 && __2 && !__3)
                guard.RequestNativeStow((int)__0);
            return false;
        }

        private static bool BeforeRemoveStack(ItemInstanceManager __instance, int __0, int __1, bool __2,
            bool __runOriginal, ref int __result, out RemovalWriterState __state)
        {
            EnterWriter(out bool entered);
            __state = new(entered, null, null, false);
            var drop = Inventory.StoredDrop.ForManager(__instance);
            if (drop != null)
            {
                if (__runOriginal && drop.PermitRemainder(__0, __1, __2, _writerDepth))
                { __state = new(entered, null, null, false, drop); return true; }
                __result = 0;
                return false;
            }
            if (InventoryStorage.IsManagerHeld(__instance))
            {
                if (__runOriginal && InventoryStorage.PermitManagerWrite(__instance, __0, __1, null, false, __2)) return true;
                __result = 0;
                return false;
            }
            if (!ProtectsManager(__instance)) return true;
            var guard = _guard;
            __state = new(entered, null, guard, false);
            if (__runOriginal && !IsNativeInventoryLifecycleHeld(__instance)
                && guard?._nativeEquip?.TryPermitRemoveRemainder(__instance, __0, __1, __2, _writerDepth) == true)
                return true;
            if (__runOriginal && !IsNativeInventoryLifecycleHeld(__instance)
                && guard?.TryPermitCraftRemoveStack(__instance, __0, __1, __2) == true)
            { __state = new(entered, null, guard, true); return true; }
            if (__runOriginal && guard != null && !IsNativeInventoryLifecycleHeld(__instance)
                && guard.OnThread && !guard.IsDisposed && !guard.IsFaulted && guard._permit
                && !guard.InventoryFrozen
                && __instance.Pointer == guard._manager.Pointer && guard._permitInstance == IntPtr.Zero
                && __0 == guard._permitId && __1 == guard._permitQuantity && !__2)
            {
                guard._permit = false;
                return true;
            }
            __result = 0;
            return false;
        }

        private static bool BeforeRemoveInstance(ItemInstanceManager __instance, ItemInstance __0,
            bool __runOriginal, ref bool __result, out RemovalWriterState __state)
        {
            EnterWriter(out bool entered);
            __state = new(entered, null, null, false);
            var drop = Inventory.StoredDrop.ForManager(__instance);
            if (drop != null)
            {
                if (__runOriginal && drop.PermitInstance(__0, _writerDepth))
                { __state = new(entered, null, null, false, drop); return true; }
                __result = false;
                return false;
            }
            if (InventoryStorage.IsManagerHeld(__instance))
            {
                if (__runOriginal && InventoryStorage.PermitWorldVariantRemoval(__instance, __0)) return true;
                __result = false;
                return false;
            }
            if (!ProtectsManager(__instance)) return true;
            var guard = _guard;
            __state = new(entered, null, guard, false);
            if (__runOriginal && !IsNativeInventoryLifecycleHeld(__instance)
                && guard?._nativeEquip?.TryPermitRemoveInstance(__instance, __0, _writerDepth) == true)
            { __state = new(entered, guard._nativeEquip, guard, false); return true; }
            if (__runOriginal && !IsNativeInventoryLifecycleHeld(__instance)
                && guard?.TryPermitCraftRemoveInstance(__instance, __0) == true)
            { __state = new(entered, null, guard, true); return true; }
            if (__runOriginal && guard != null && !IsNativeInventoryLifecycleHeld(__instance)
                && guard.OnThread && !guard.IsDisposed && !guard.IsFaulted && guard._permit
                && !guard.InventoryFrozen
                && __instance.Pointer == guard._manager.Pointer && __0 != null && !__0.WasCollected
                && __0.Pointer == guard._permitInstance && __0._itemID == guard._permitId && guard._permitQuantity == 1)
            {
                guard._permit = false;
                return true;
            }
            __result = false;
            return false;
        }

        private readonly record struct EquipmentWriterState(bool Entered, EquipmentTransfer Operation);
        private readonly record struct RemovalWriterState(bool Entered, EquipmentTransfer Operation,
            PlayerInventoryEnforcement CraftGuard, bool CraftAdmitted, Inventory.StoredDrop Drop = null);

        private static bool BeforeRemoveInventory(PlayerInventory __instance, int __0, int __1, bool __2,
            bool __3, bool __4, ItemInstance __5, bool __6, bool __runOriginal, ref bool __result,
            out RemovalWriterState __state)
        {
            EnterWriter(out bool entered);
            __state = new(entered, null, null, false);
            var drop = Inventory.StoredDrop.ForInventory(__instance);
            if (drop != null)
            {
                if (__runOriginal && drop.PermitRemoval(__0, __1, __2, __3, __4, __5, __6, _writerDepth))
                { __state = new(entered, null, null, false, drop); return true; }
                __result = false;
                return false;
            }
            if (!ProtectsInventory(__instance)) return true;
            var guard = _guard;
            __state = new(entered, null, guard, false);
            if (__runOriginal && !IsNativeInventoryLifecycleHeld(__instance)
                && guard?._nativeEquip?.TryPermitRemoveItem(__instance, __0, __1, __2, __3,
                __4, __5, __6, _writerDepth) == true)
            { __state = new(entered, guard._nativeEquip, guard, false); return true; }
            if (__runOriginal && !IsNativeInventoryLifecycleHeld(__instance)
                && guard?.TryPermitCraftRemoveItem(__instance, __0, __1, __2, __3, __4, __5, __6) == true)
            { __state = new(entered, null, guard, true); return true; }
            __result = false;
            return false;
        }

        private static Exception AfterRemoveInventory(RemovalWriterState __state, bool __runOriginal,
            bool __result, Exception __exception)
        {
            try
            {
                __state.Operation?.ObserveRemoveItem(true, __runOriginal, __result, __exception);
                __state.Drop?.ObserveRemoval(__runOriginal, __result, __exception);
                __state.CraftGuard?.ObserveCraftRemoveItem(__state.CraftAdmitted, __runOriginal, __result, __exception);
            }
            finally { if (__state.Entered) _writerDepth--; }
            return __exception;
        }

        private static Exception AfterRemoveInstance(RemovalWriterState __state, bool __runOriginal,
            bool __result, Exception __exception)
        {
            try
            {
                __state.Operation?.ObserveRemoveInstance(true, __runOriginal, __result, __exception);
                __state.Drop?.ObserveInstance(__runOriginal, __result, __exception);
                __state.CraftGuard?.ObserveCraftRemoveInstance(__state.CraftAdmitted, __runOriginal, __result, __exception);
            }
            finally { if (__state.Entered) _writerDepth--; }
            return __exception;
        }

        private static Exception AfterRemoveStack(RemovalWriterState __state, bool __runOriginal,
            int __result, Exception __exception)
        {
            try
            {
                __state.CraftGuard?.ObserveCraftRemoveStack(__state.CraftAdmitted, __runOriginal, __result, __exception);
                __state.Drop?.ObserveRemainder(__runOriginal, __result, __exception);
            }
            finally { if (__state.Entered) _writerDepth--; }
            return __exception;
        }

        private static Exception AfterAddInventory(EquipmentWriterState __state, bool __runOriginal,
            bool __result, Exception __exception)
        {
            try { __state.Operation?.ObserveAddItem(true, __runOriginal, __result, __exception); }
            finally { if (__state.Entered) _writerDepth--; }
            return __exception;
        }

        private static Exception AfterAddInstance(EquipmentWriterState __state, bool __runOriginal,
            bool __result, Exception __exception)
        {
            try { __state.Operation?.ObserveAddInstance(true, __runOriginal, __result, __exception); }
            finally { if (__state.Entered) _writerDepth--; }
            return __exception;
        }

        private static Exception AfterUnequip(EquipmentWriterState __state, bool __runOriginal, Exception __exception)
        {
            try { __state.Operation?.ObserveUnequip(true, __runOriginal, __exception); }
            finally { if (__state.Entered) _writerDepth--; }
            return __exception;
        }

        private static bool BeforeEquipById(PlayerInventory __instance, int __0, bool __1, bool __2, bool __3,
            bool __runOriginal, ref bool __result, out bool __state)
        {
            EnterWriter(out __state);
            if (!ProtectsInventory(__instance)) return true;
            if (__runOriginal && _guard?.HasPendingNativeWorldIntent != true
                && !IsNativeInventoryLifecycleHeld(__instance) && !__1 && !__2 && !__3)
                _guard?.RequestNativeEquip(null, __0, -1, __2, __3);
            __result = false;
            return false;
        }

        private static bool BeforeEquipExact(PlayerInventory __instance, ItemInstance __0, bool __1, bool __2,
            Il2CppSystem.Nullable<EquipmentSlot> __3, bool __4, bool __5,
            bool __runOriginal, ref bool __result, out bool __state)
        {
            EnterWriter(out __state);
            if (!ProtectsInventory(__instance)) return true;
            bool hasOverride = __3 != null && __3.HasValue;
            if (__runOriginal && !IsNativeInventoryLifecycleHeld(__instance)
                && _guard?._nativeEquip?.TryPermitEquip(__instance, __0, __1, __2,
                hasOverride, __4, __5, _writerDepth) == true) return true;
            if (__runOriginal && !IsNativeInventoryLifecycleHeld(__instance)
                && _guard?.HasPendingNativeWorldIntent != true
                && !__1 && !__2 && !hasOverride && !__4 && !__5 && __0 != null && !__0.WasCollected)
                _guard?.RequestNativeEquip(__0, __0._itemID, -1, __2, __5);
            __result = false;
            return false;
        }

        private static bool BeforeEquipSequence(PlayerInventory._EquipSequence_d__190 __instance,
            bool __runOriginal, ref bool __result, out EquipmentTransfer __state)
        {
            __state = null;
            if (__instance == null || !ProtectsInventory(__instance.__4__this)) return true;
            var operation = IsNativeInventoryLifecycleHeld(__instance.__4__this) ? null : _guard?._nativeEquip;
            if (__runOriginal && operation?.TryEnterEquipSequence(__instance, _writerDepth) == true)
            { __state = operation; return true; }
            __result = false;
            return false;
        }

        private static Exception AfterEquipSequence(PlayerInventory._EquipSequence_d__190 __instance,
            EquipmentTransfer __state, bool __runOriginal, bool __result, Exception __exception)
        {
            __state?.ExitEquipSequence(__instance, __runOriginal, __result, __exception);
            return __exception;
        }
    }
}
