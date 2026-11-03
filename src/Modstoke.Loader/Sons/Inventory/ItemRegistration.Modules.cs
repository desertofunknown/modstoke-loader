// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using ModuleType = global::Sons.Inventory.ItemInstance.ItemInstanceModuleType;

namespace Modstoke.Sons
{
    public static partial class ItemRegistration
    {
        private static readonly Dictionary<int, RuntimeRegistration> NativeModuleContracts = new();

        /// <summary>
        /// Publishes a new caller-authored unique item with an explicit, nonempty native module
        /// shape. Only the ten module families reconstructed by the game's inventory save and
        /// Bolt readers are supported. The item and all native layouts must already be authored
        /// for unique-instance storage; no vanilla definition or uniqueness flag is changed.
        /// Assets and ordinary native initialization must supply the declared modules. This
        /// does not create modules, initialize their state, grant items or install serializers.
        /// Crafting transfers check the exact module classes and retain the original instances.
        /// Republish the same definition before native save loading in each process, and supply
        /// matching assets/definitions on peers. Publication is not save or network acceptance.
        /// </summary>
        public static ItemRegistrationResult ExecuteModuleItemRegistration(
            CustomItemDefinition definition, ItemData nativeItem, params ModuleType[] moduleTypes)
        {
            if (_runtimeOwnerThreadId == 0 || Environment.CurrentManagedThreadId != _runtimeOwnerThreadId)
                return Result(false, false, false, "wrong-thread", "Registration requires the initialized Unity lifecycle thread.");
            if (moduleTypes == null || moduleTypes.Length == 0 || moduleTypes.Length > 10)
                return Result(false, false, false, "invalid-module-contract", "Declare one to ten distinct built-in native module families.");
            int mask = 0;
            foreach (var type in moduleTypes)
            {
                int index = (int)type;
                if (index < 0 || index > 9 || (mask & (1 << index)) != 0)
                    return Result(false, false, false, "invalid-module-contract", "Unknown and repeated native module families are not supported.");
                mask |= 1 << index;
            }
            return ExecuteRegistration(definition, nativeItem, false, mask);
        }

        internal static bool ValidateAuthoredModuleInstance(ItemInstance instance, bool requireContract, out string detail)
        {
            detail = "The exact authored instance and its native module contract must still be available on the Unity thread.";
            if (instance is null || instance.WasCollected) return false;
            RuntimeRegistration contract;
            lock (SyncRoot)
                NativeModuleContracts.TryGetValue(instance._itemID, out contract);
            if (contract == null)
            {
                detail = requireContract ? "Register this authored item with ExecuteModuleItemRegistration first." : "No explicit module contract.";
                return !requireContract;
            }
            if (_runtimeOwnerThreadId == 0 || Environment.CurrentManagedThreadId != _runtimeOwnerThreadId || _runtimeBusy)
                return false;
            var item = instance.Data;
            if (!Same(contract.Item, item) || item._id != contract.NativeModuleItemId || !contract.Published
                || !IsCurrentModuleItem(contract) || !item._eachInstanceIsUnique)
                return false;
            var modules = instance._modules;
            detail = "The instance must contain exactly the declared built-in native module classes, without duplicates or subclasses.";
            if (modules is null || modules.WasCollected || modules.Count == 0 || modules.Count > 10) return false;
            int actual = 0;
            foreach (var module in modules)
            {
                if (module is null || module.WasCollected) return false;
                int bit = NativeModuleBit(IL2CPP.il2cpp_object_get_class(module.Pointer));
                if (bit == 0 || (actual & bit) != 0) return false;
                actual |= bit;
            }
            if (actual != contract.NativeModuleMask) return false;
            detail = "The authored unique instance matches its native save/token module shape; no serialization or remote delivery was performed.";
            return true;
        }

        private static bool IsCurrentModuleItem(RuntimeRegistration contract)
        {
            if (!Same(contract.Database, ItemDatabaseManager._instance)) return false;
            var cache = ItemDatabaseManager._itemsCache;
            var items = contract.Database._itemDataList;
            // Successful SDK registrations only append. Pin the published slot as well as the
            // cache entry, without rescanning the item database on every transfer readback.
            return cache is not null && !cache.WasCollected && items is not null && !items.WasCollected
                && cache.Pointer == contract.Cache.Pointer && items.Pointer == contract.Items.Pointer
                && contract.NativeModuleItemIndex >= 0 && contract.NativeModuleItemIndex < items.Count
                && Same(items[contract.NativeModuleItemIndex], contract.Item)
                && cache.TryGetValue(contract.NativeModuleItemId, out var cached) && Same(cached, contract.Item);
        }

        internal static bool NativeModuleRecordMatches(ItemInstanceModule module, ItemInstanceModule.SaveData record)
        {
            if (module is null || module.WasCollected || record is null || record.WasCollected
                || record.ModuleId < 0 || record.ModuleId > 9) return false;
            if (NativeModuleBit(IL2CPP.il2cpp_object_get_class(module.Pointer)) != (1 << record.ModuleId)) return false;
            var type = IL2CPP.il2cpp_object_get_class(record.Pointer);
            // The native loaders silently ignore a wrong SaveData subtype. Require the exact
            // native record, not a CLR type test or a mod-defined subclass of a known family.
            return (ModuleType)record.ModuleId switch
            {
                ModuleType.BloodReveal => type == Il2CppClassPointerStore<BloodRevealItemInstanceModule.BloodRevealSaveData>.NativeClassPtr,
                ModuleType.DismemberedLimb => type == Il2CppClassPointerStore<DismemberedLimbItemInstanceModule.DismemberedSaveData>.NativeClassPtr,
                ModuleType.GpsLocator => type == Il2CppClassPointerStore<GPSLocatorItemInstanceModule.GpsLocatorSaveData>.NativeClassPtr,
                ModuleType.Perishable => type == Il2CppClassPointerStore<PerishableItemInstanceModule.PerishableSaveData>.NativeClassPtr,
                ModuleType.RangedWeapon => type == Il2CppClassPointerStore<RangedWeaponItemInstanceModule.RangedWeaponSaveData>.NativeClassPtr,
                ModuleType.StructureElement => type == Il2CppClassPointerStore<StructureElementItemInstanceModule.StructureElementSaveData>.NativeClassPtr,
                ModuleType.VisualVariant => type == Il2CppClassPointerStore<VisualVariantItemInstanceModule.VisualVariantSaveData>.NativeClassPtr,
                ModuleType.VolumeContainer => type == Il2CppClassPointerStore<VolumeContainerItemInstanceModule.VolumeContainerSaveData>.NativeClassPtr,
                ModuleType.ItemPlating => type == Il2CppClassPointerStore<ItemPlatingItemInstanceModule.ItemPlatingSaveData>.NativeClassPtr,
                ModuleType.ArrowUpgrade => type == Il2CppClassPointerStore<ArrowUpgradeItemInstanceModule.ArrowUpgradeSaveData>.NativeClassPtr,
                _ => false
            };
        }

        private static int NativeModuleBit(IntPtr nativeClass)
        {
            if (nativeClass == IntPtr.Zero) return 0;
            if (nativeClass == Il2CppClassPointerStore<BloodRevealItemInstanceModule>.NativeClassPtr) return 1 << (int)ModuleType.BloodReveal;
            if (nativeClass == Il2CppClassPointerStore<DismemberedLimbItemInstanceModule>.NativeClassPtr) return 1 << (int)ModuleType.DismemberedLimb;
            if (nativeClass == Il2CppClassPointerStore<GPSLocatorItemInstanceModule>.NativeClassPtr) return 1 << (int)ModuleType.GpsLocator;
            if (nativeClass == Il2CppClassPointerStore<PerishableItemInstanceModule>.NativeClassPtr) return 1 << (int)ModuleType.Perishable;
            if (nativeClass == Il2CppClassPointerStore<RangedWeaponItemInstanceModule>.NativeClassPtr) return 1 << (int)ModuleType.RangedWeapon;
            if (nativeClass == Il2CppClassPointerStore<StructureElementItemInstanceModule>.NativeClassPtr) return 1 << (int)ModuleType.StructureElement;
            if (nativeClass == Il2CppClassPointerStore<VisualVariantItemInstanceModule>.NativeClassPtr) return 1 << (int)ModuleType.VisualVariant;
            if (nativeClass == Il2CppClassPointerStore<VolumeContainerItemInstanceModule>.NativeClassPtr) return 1 << (int)ModuleType.VolumeContainer;
            if (nativeClass == Il2CppClassPointerStore<ItemPlatingItemInstanceModule>.NativeClassPtr) return 1 << (int)ModuleType.ItemPlating;
            if (nativeClass == Il2CppClassPointerStore<ArrowUpgradeItemInstanceModule>.NativeClassPtr) return 1 << (int)ModuleType.ArrowUpgrade;
            return 0;
        }
    }
}
