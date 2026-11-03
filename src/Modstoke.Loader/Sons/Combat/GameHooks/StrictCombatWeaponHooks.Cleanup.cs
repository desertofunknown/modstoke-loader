// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Weapon;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatWeaponHooks
    {
        private static RangedWeaponController[] _controlledCleanup = Array.Empty<RangedWeaponController>();

        private static (MethodInfo Method, string Prefix)[] CleanupTargets()
            => new (MethodInfo, string)[]
            {
                (AccessTools.Method(typeof(RangedWeaponController), "OnDisable", Type.EmptyTypes), nameof(BeforeCleanup)),
                (AccessTools.Method(typeof(RangedWeaponController), "OnDestroy", Type.EmptyTypes), nameof(BeforeCleanup)),
                (AccessTools.Method(typeof(HeldControllerBase), "OnDestroy", Type.EmptyTypes), nameof(BeforeHeldCleanup))
            };

        internal static bool TrySuppressCleanup(StrictCombatLocalWeapon capture)
        {
            if (!OnOwnerThread || !_prepared || !_hooksInstalled || capture == null
                || !capture.Owner.IsCurrentNativeHealthOwner(capture.Life)) return false;
            var owner = FindController(capture.Controller);
            if (owner == null || owner.IsRetired || !ReferenceEquals(owner.Capture, capture)) return false;
            var retained = Volatile.Read(ref _controlledCleanup);
            foreach (var controller in retained)
                if (Same(controller, capture.Controller)) return false; // Never repeat a cleanup attempt.
            if (retained.Length >= MaximumOwners) return false;
            var next = new RangedWeaponController[retained.Length + 1];
            Array.Copy(retained, next, retained.Length);
            next[^1] = capture.Controller;
            // Unity destruction is deferred. These callbacks must remain suppressed even
            // when a later step fails or the captured life is replaced.
            Volatile.Write(ref _controlledCleanup, next);
            return true;
        }

        private static bool BeforeCleanup(RangedWeaponController __instance, bool __runOriginal)
            => !__runOriginal || AllowCleanup(__instance);

        private static bool BeforeHeldCleanup(HeldControllerBase __instance, bool __runOriginal)
            => !__runOriginal || AllowCleanup(__instance);

        private static bool AllowCleanup(Il2CppObjectBase instance)
        {
            // Only retained pointer identity is inspected, including for retired receivers.
            if (FindPreparedController(instance) != null) return false;
            foreach (var controller in Volatile.Read(ref _controlledCleanup))
                if (Same(controller, instance)) return false;
            return true;
        }
    }
}
