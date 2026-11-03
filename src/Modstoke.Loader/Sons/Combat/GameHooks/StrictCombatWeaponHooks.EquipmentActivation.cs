// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using System.Threading;
using Endnight.Rendering;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Items.Core;
using global::Sons.Weapon;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatWeaponHooks
    {
        private static StrictCombatEquipmentActivation[] _equipmentActivations = Array.Empty<StrictCombatEquipmentActivation>();

        internal static bool TryOwnEquipmentActivation(StrictCombatEquipmentActivation activation)
        {
            if (!OnOwnerThread || !_prepared || !_hooksInstalled || activation is null || activation.Renderer is null)
                return false;
            var owners = Volatile.Read(ref _equipmentActivations);
            if (owners.Length >= MaximumOwners) return false;
            foreach (var owner in owners)
                if (Same(owner.Renderer, activation.Renderer)) return false;
            var next = new StrictCombatEquipmentActivation[owners.Length + 1];
            Array.Copy(owners, next, owners.Length);
            next[^1] = activation;
            Volatile.Write(ref _equipmentActivations, next);
            return true;
        }

        private static StrictCombatEquipmentActivation FindEquipmentActivation(Il2CppObjectBase renderer)
        {
            foreach (var owner in Volatile.Read(ref _equipmentActivations))
                if (Same(owner.Renderer, renderer)) return owner;
            return null;
        }

        private static void InstallEquipmentActivationHooks(HarmonyLib.Harmony harmony)
        {
            var handle = new[] { typeof(AsyncOperationHandle<GameObject>) };
            var targets = new (MethodInfo Method, string Prefix, string Finalizer)[]
            {
                (AccessTools.Method(typeof(ItemRenderable), "OnEnable", Type.EmptyTypes), nameof(BeforeItemRenderableEnable), nameof(AfterRenderableEnable)),
                (AccessTools.Method(typeof(AssetReferenceRenderable), "OnEnable", Type.EmptyTypes), nameof(BeforeBaseRenderableEnable), nameof(AfterRenderableEnable)),
                (AccessTools.Method(typeof(ItemRenderable), "OnLoadDone", handle), nameof(BeforeItemRenderableLoaded), nameof(AfterRenderableLoaded)),
                (AccessTools.Method(typeof(AssetReferenceRenderable), "OnLoadDone", handle), nameof(BeforeBaseRenderableLoaded), nameof(AfterRenderableLoaded)),
                (AccessTools.Method(typeof(AssetReferenceRenderable), "CleanUpAddressable", Type.EmptyTypes), nameof(BeforeRenderableCleanup), null),
                (AccessTools.Method(typeof(RangedWeaponController), "Update", Type.EmptyTypes), nameof(BeforeEquipmentControllerUpdate), nameof(AfterEquipmentControllerUpdate))
            };
            foreach (var target in targets)
                if (target.Method is null) throw new MissingMethodException("An equipment render lifecycle hook is unavailable.");
            foreach (var target in targets)
                harmony.Patch(target.Method,
                    prefix: new HarmonyMethod(typeof(StrictCombatWeaponHooks), target.Prefix) { priority = Priority.First },
                    finalizer: target.Finalizer is null ? null
                        : new HarmonyMethod(typeof(StrictCombatWeaponHooks), target.Finalizer) { priority = Priority.Last });
        }

        private static bool BeforeItemRenderableEnable(ItemRenderable __instance, bool __runOriginal,
            out StrictCombatEquipmentActivation __state)
            => EnterRenderableEnable(__instance, __runOriginal, true, out __state);

        private static bool BeforeBaseRenderableEnable(AssetReferenceRenderable __instance, bool __runOriginal,
            out StrictCombatEquipmentActivation __state)
            => EnterRenderableEnable(__instance, __runOriginal, false, out __state);

        private static bool EnterRenderableEnable(Il2CppObjectBase renderer, bool runOriginal, bool derived,
            out StrictCombatEquipmentActivation state)
        {
            state = null;
            if (!runOriginal) return true;
            var owner = FindEquipmentActivation(renderer);
            if (owner is null) return true;
            if (!owner.TryEnterEnable(derived)) return false;
            state = owner;
            return true;
        }

        private static Exception AfterRenderableEnable(Exception __exception, StrictCombatEquipmentActivation __state)
        {
            __state?.EndEnable(__exception);
            return __exception;
        }

        private static bool BeforeItemRenderableLoaded(ItemRenderable __instance,
            AsyncOperationHandle<GameObject> __0, bool __runOriginal, out StrictCombatEquipmentActivation __state)
            => EnterRenderableLoaded(__instance, __0, __runOriginal, true, out __state);

        private static bool BeforeBaseRenderableLoaded(AssetReferenceRenderable __instance,
            AsyncOperationHandle<GameObject> __0, bool __runOriginal, out StrictCombatEquipmentActivation __state)
            => EnterRenderableLoaded(__instance, __0, __runOriginal, false, out __state);

        private static bool EnterRenderableLoaded(Il2CppObjectBase renderer, AsyncOperationHandle<GameObject> handle,
            bool runOriginal, bool derived, out StrictCombatEquipmentActivation state)
        {
            state = null;
            if (!runOriginal) return true;
            var owner = FindEquipmentActivation(renderer);
            if (owner is null) return true;
            if (!owner.TryEnterLoad(handle, derived)) return false;
            state = owner;
            return true;
        }

        private static Exception AfterRenderableLoaded(Exception __exception, StrictCombatEquipmentActivation __state)
        {
            __state?.EndLoad(__exception);
            return __exception;
        }

        private static bool BeforeRenderableCleanup(AssetReferenceRenderable __instance, bool __runOriginal)
        {
            if (!__runOriginal) return true;
            var owner = FindEquipmentActivation(__instance);
            if (owner is null) return true;
            owner.SuppressNativeCleanup();
            return false;
        }

        private static bool BeforeEquipmentControllerUpdate(RangedWeaponController __instance, bool __runOriginal,
            out StrictCombatEquipmentActivation __state)
        {
            __state = null;
            if (!__runOriginal) return true;
            foreach (var owner in Volatile.Read(ref _equipmentActivations))
            {
                if (!Same(owner.Controller, __instance)) continue;
                if (!owner.TryEnterUpdate()) return false;
                __state = owner;
                return true;
            }
            // A reservation without an activation owner has no update authority either.
            return FindPreparedController(__instance) is null;
        }

        private static Exception AfterEquipmentControllerUpdate(Exception __exception, StrictCombatEquipmentActivation __state)
        {
            __state?.EndUpdate(__exception);
            return __exception;
        }
    }
}
