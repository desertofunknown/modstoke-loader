// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime;
using global::Sons.Inventory;
using global::Sons.Weapon;

namespace Modstoke.Sons
{
    /// <summary>Exact native observation, not permission to adopt client resources or activity.</summary>
    public readonly record struct FlashlightPowerCapture(int ItemId, float Capacity,
        float Charge, float DrainPerSecond, float AutoRefillThreshold)
    {
        public bool IsValid => ItemId > 0 && float.IsFinite(Capacity) && Capacity > 0
            && float.IsFinite(Charge) && Charge >= 0 && Charge <= Capacity
            && float.IsFinite(DrainPerSecond) && DrainPerSecond > 0
            && float.IsFinite(AutoRefillThreshold) && AutoRefillThreshold >= 0 && AutoRefillThreshold <= 1;
    }

    /// <summary>Transport must correlate this exact float state with the actor, migration and baseline.</summary>
    public readonly record struct FlashlightPowerState(ulong InstanceKey, ulong Revision,
        int ItemId, float Capacity, float Charge, bool Active, bool NeedsBattery);

    /// <summary>
    /// One native removal and ledger debit already committed for this authority. For item 469,
    /// pass this once to the existing StrictCombat rebreather owner; it does not grant tank air.
    /// </summary>
    public readonly record struct PowerDebit(ulong Transaction, ulong PoweredInstanceKey,
        ulong SupplyInstanceKey, int SupplyItemId);

    public static class InventoryPower
    {
        public const int BatteryItemId = 527;
        public const int AirCanisterItemId = 469;
        public const int RebreatherItemId = 444;

        /// <summary>
        /// Read on the game thread while this exact source instance is quiesced. Does not call
        /// Instantiate, Refresh, Update, LocalPlayer, or infer inventory membership from the controller.
        /// </summary>
        public static bool TryCaptureFlashlight(ItemInstance item, FlashlightController controller,
            out FlashlightPowerCapture capture, out string detail)
        {
            capture = default;
            detail = "An initialized exact item/controller binding and recipe-free power module are required.";
            try
            {
                var module = FindPowerModule(item);
                if (module == null || controller == null || controller.WasCollected || !controller
                    || controller._itemInstance?.Pointer != item.Pointer
                    || controller._powerCapacity?.Pointer != module.Pointer) return false;
                float duration = controller._fullChargeDurationMinutes;
                float capacity = module._MaxVolume_k__BackingField;
                float rate = controller._powerDrainRate;
                if (!float.IsFinite(duration) || duration <= 0 || !float.IsFinite(duration * 60)
                    || rate != capacity / (duration * 60)) return false;
                capture = new(item._itemID, capacity, module._CurrentVolume_k__BackingField,
                    rate, controller._autoRefilThreshold);
                if (!capture.IsValid) { capture = default; return false; }
                detail = "Exact flashlight values observed; actor custody and server approval are still required.";
                return true;
            }
            catch (Exception error) { detail = "Flashlight capture failed: " + error.Message; return false; }
        }

        internal static VolumeContainerItemInstanceModule FindPowerModule(ItemInstance item)
        {
            if (item == null || item.WasCollected || item._modules == null || item._modules.WasCollected) return null;
            VolumeContainerItemInstanceModule found = null;
            foreach (var entry in item._modules)
            {
                if (entry == null || entry.WasCollected) return null;
                if (IL2CPP.il2cpp_object_get_class(entry.Pointer)
                    != Il2CppClassPointerStore<VolumeContainerItemInstanceModule>.NativeClassPtr) continue;
                if (found != null) return null;
                found = entry.Cast<VolumeContainerItemInstanceModule>();
            }
            return ValidModule(found) ? found : null;
        }

        internal static bool IsDetached(ItemInstance item)
            => item != null && !item.WasCollected && (item._ItemObject_k__BackingField?.Pointer ?? 0) == 0
                && (item._itemRenderable?.Pointer ?? 0) == 0;

        internal static bool ValidModule(VolumeContainerItemInstanceModule module)
            => module != null && !module.WasCollected && module._isInstantiated && (module._statRecipe?.Pointer ?? 0) == 0
                && float.IsFinite(module._MaxVolume_k__BackingField) && module._MaxVolume_k__BackingField > 0
                && float.IsFinite(module._CurrentVolume_k__BackingField) && module._CurrentVolume_k__BackingField >= 0
                && module._CurrentVolume_k__BackingField <= module._MaxVolume_k__BackingField;

        internal static bool Same(float left, float right)
            => BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);
    }
}
