// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Gameplay;
using global::Sons.Inventory;
using TheForest.Utils;
using UdpKit;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatRebreatherHooks
    {
        private static RebreatherVolumeSetter[] _volumeSetters;

        private static bool InstallHooks()
        {
            if (_patchAttempted) return _hooksInstalled;
            _patchAttempted = true;
            if (_support == null || !_support.SafeForHarmonyPatches) return false;
            try
            {
                var module = typeof(VolumeContainerItemInstanceModule);
                var recipe = AccessTools.Property(module, "Recipe")?.PropertyType
                    ?? throw new MissingMemberException("The volume recipe type is unavailable.");
                var manual = typeof(InventoryRefillRebreather);
                var targets = new List<(MethodInfo Method, string Prefix)>
                {
                    (AccessTools.Method(typeof(RebreatherController), "UpdateAirConsumption", Type.EmptyTypes), nameof(BeforeConsumption)),
                    (AccessTools.Method(typeof(PlayerStats), "UseRebreather", new[] { typeof(bool) }), nameof(BeforeUseRebreather)),
                    (AccessTools.Method(manual, "TryPerformAction", new[] { typeof(ItemInstance), typeof(bool) }), nameof(BeforeStart)),
                    (AccessTools.Method(manual, "Update", Type.EmptyTypes), nameof(BeforeManualUpdate)),
                    (AccessTools.Method(manual, "OnDisable", Type.EmptyTypes), nameof(BeforeManualUpdate)),
                    (AccessTools.Method(module, "Refresh", new[] { typeof(ItemInstance), typeof(GameObject) }), nameof(BeforeModuleRefresh)),
                    (AccessTools.Method(module, "Instantiate", Type.EmptyTypes), nameof(BeforeModuleMutation)),
                    (AccessTools.Method(module, "Refill", new[] { recipe }), nameof(BeforeModuleMutation)),
                    (AccessTools.Method(module, "SetContentsFromFactor", new[] { typeof(float), recipe }), nameof(BeforeModuleMutation)),
                    (AccessTools.Method(module, "SetContents", new[] { typeof(float), recipe }), nameof(BeforeModuleMutation)),
                    (AccessTools.Method(module, "Refill", new[] { typeof(float), recipe }), nameof(BeforeModuleMutation)),
                    (AccessTools.Method(module, "Empty", new[] { typeof(float) }), nameof(BeforeModuleMutation)),
                    (AccessTools.Method(module, "SetFromUdpPacket", new[] { typeof(UdpPacket).MakeByRefType() }), nameof(BeforeUdpLoad)),
                    (AccessTools.Method(module, "LoadFromSave", new[] { typeof(ItemInstanceModule.SaveData) }), nameof(BeforeModuleMutation))
                };
                targets.AddRange(NativeRebreatherEquipmentTargets());
                foreach (var target in targets)
                    if (target.Method == null || target.Method.IsStatic
                        || target.Method.ReturnType != (target.Prefix == nameof(BeforeStart) ? typeof(bool) : typeof(void)))
                        throw new MissingMethodException("A native rebreather mutation hook is unavailable.");
                _volumeSetters = PrepareVolumeSetters();
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-strict-combat-native-rebreather");
                foreach (var target in targets)
                    harmony.Patch(target.Method,
                        prefix: new HarmonyMethod(typeof(StrictCombatRebreatherHooks), target.Prefix) { priority = Priority.First },
                        finalizer: target.Prefix == nameof(BeforeUdpLoad)
                            ? new HarmonyMethod(typeof(StrictCombatRebreatherHooks), nameof(AfterUdpLoad)) { priority = Priority.Last }
                            : null);
                foreach (var setter in _volumeSetters) setter.Install();
                _hooksInstalled = true;
                return true;
            }
            catch (Exception error)
            {
                try { _warning?.Invoke("Strict combat native rebreather hooks unavailable: " + error.Message); }
                catch { }
                return false;
            }
        }

        private static bool BeforeConsumption(RebreatherController __instance, bool __runOriginal)
        {
            if (!__runOriginal || !IsObserving) return true;
            try
            {
                if (__instance is null) return AllowUnreadableOwner();
                if (BlocksControllerPointer(__instance.Pointer)) return false;
                if (__instance.IsLocalPlayer()) Remember(LocalPlayer._Inventory_k__BackingField);
                return true;
            }
            catch (Exception) { return AllowUnreadableOwner(); }
        }

        private static bool BeforeStart(InventoryRefillRebreather __instance, bool __runOriginal, ref bool __result)
        {
            if (!__runOriginal || !IsObserving) return true;
            if (AllowManual(__instance, observeStart: true)) return true;
            __result = false;
            return false;
        }

        private static bool BeforeUseRebreather(PlayerStats __instance, bool __runOriginal)
        {
            if (!__runOriginal || !IsObserving) return true;
            try
            {
                if (__instance is null) return AllowUnreadableOwner();
                // Both directions copy air as well as changing the active flag.
                // Keep retired receivers blocked without consulting their live state.
                if (BlocksStatsPointer(__instance.Pointer)) return false;
                if (!OnOwnerThread || __instance.WasCollected) return AllowUnreadableOwner();
                var breathing = __instance._vitals?.LungBreathing;
                if (BlocksBreathingPointer(breathing?.Pointer ?? 0)) return false;

                var local = LocalPlayer.Stats;
                if (local is not null && local.Pointer == __instance.Pointer)
                    Remember(__instance._inventory);
                return true;
            }
            catch (Exception) { return AllowUnreadableOwner(); }
        }

        private static bool BeforeManualUpdate(InventoryRefillRebreather __instance, bool __runOriginal)
            => !__runOriginal || !IsObserving || AllowManual(__instance, observeStart: false);

        private static bool AllowManual(InventoryRefillRebreather action, bool observeStart)
        {
            try
            {
                if (action is null) return AllowUnreadableOwner();
                var module = action._volumeModule;
                if (module is not null && BlocksModulePointer(module.Pointer)) return false;
                var inventory = LocalPlayer._Inventory_k__BackingField;
                // Native manual refill chooses GetSingleInstance(444), ignoring the
                // supplied item. Ownership therefore suppresses this inventory's route.
                if (OwnsInventory(inventory)) return false;
                if (observeStart || action._isRefilling) Remember(inventory);
                return true;
            }
            catch (Exception) { return AllowUnreadableOwner(); }
        }

        private static bool BeforeModuleRefresh(VolumeContainerItemInstanceModule __instance,
            ItemInstance __0, GameObject __1, bool __runOriginal)
        {
            if (!__runOriginal || !IsObserving) return true;
            try
            {
                if (BlocksModulePointer(__instance?.Pointer ?? 0)
                    || BlocksItemPointer(__0?.Pointer ?? 0)
                    || BlocksObjectPointer(__1?.Pointer ?? 0)) return false;
                return __instance is not null || AllowUnreadableOwner();
            }
            catch (Exception) { return AllowUnreadableOwner(); }
        }

        private static bool BeforeModuleMutation(VolumeContainerItemInstanceModule __instance, bool __runOriginal)
        {
            if (!__runOriginal || !IsObserving) return true;
            try { return __instance is null ? AllowUnreadableOwner() : !BlocksModulePointer(__instance.Pointer); }
            catch (Exception) { return AllowUnreadableOwner(); }
        }

        private static bool BeforeUdpLoad(VolumeContainerItemInstanceModule __instance,
            ref UdpPacket __0, bool __runOriginal)
        {
            if (!__runOriginal || !IsObserving || __instance is null
                || !BlocksModulePointer(__instance.Pointer)) return true;
            // Preserve the native 48-bit cursor advance without instantiation, recipe
            // lookup or mutation. Malformed packets still throw; never accept a short read.
            __0.ReadUInt(32);
            __0.ReadShort();
            return false;
        }

        private static Exception AfterUdpLoad(VolumeContainerItemInstanceModule __instance, Exception __exception)
        {
            if (__exception != null && __instance is not null && BlocksModulePointer(__instance.Pointer))
                OwnershipFailure(__exception);
            return __exception;
        }
    }
}
