// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Common;
using Modstoke.Loader;
using global::Sons.Multiplayer.Client;
using global::Sons.Wearable.Armour;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerCarrier
    {
        private static bool _installAttempted, _installed;
        private static int _ownerThread;
        private static readonly Dictionary<IntPtr, ArmourStart> ArmourStarts = new();
        private static readonly Dictionary<IntPtr, PlayerCarrier> ServerStatsOwners = new();
        private static readonly Dictionary<IntPtr, PlayerCarrier> ServerVitalsOwners = new();
        [ThreadStatic] private static PlayerCarrier PreparingServerStats;
        private static bool HooksAvailable => _installed && Environment.CurrentManagedThreadId == _ownerThread;

        internal static void Install(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (_installAttempted) return;
            _installAttempted = true;
            if (support?.SafeForHarmonyPatches != true || !GameBuild.IsRecognized) return;
            HarmonyLib.Harmony harmony = null;
            try
            {
                _ownerThread = Environment.CurrentManagedThreadId;
                var start = Qualify(typeof(PlayerArmourSystem), nameof(PlayerArmourSystem.Start), 0x31AFB50);
                var owner = Qualify(typeof(CoopMecanimReplicator), nameof(CoopMecanimReplicator.UpdateOwner), 0x30FB4E0);
                var events = Qualify(typeof(CoopMecanimReplicator), nameof(CoopMecanimReplicator.UpdateOwnerEventSync), 0x30FB8F0);
                var detach = Qualify(typeof(BoltPlayerSetup), nameof(BoltPlayerSetup.Detached), 0x32BBE20);
                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-player-carrier-startup");
                harmony.Patch(start, postfix: new HarmonyMethod(typeof(PlayerCarrier), nameof(AfterArmourStart))
                    { priority = Priority.Last });
                var gate = new HarmonyMethod(typeof(PlayerCarrier), nameof(BeforeOwnerPublisher))
                    { priority = Priority.First };
                harmony.Patch(owner, prefix: gate);
                harmony.Patch(events, prefix: gate);
                harmony.Patch(detach, postfix: new HarmonyMethod(typeof(PlayerCarrier), nameof(AfterCarrierDetached))
                    { priority = Priority.Last });
                InstallServerStatsHooks(harmony);
                _installed = true;
            }
            catch (Exception error)
            {
                try { harmony?.UnpatchSelf(); } catch (Exception) { }
                warning?.Invoke("Native player carrier startup unavailable (" + error.GetType().Name + ").");
            }
        }

        private static void InstallServerStatsHooks(HarmonyLib.Harmony harmony)
        {
            Patch(typeof(PlayerStats), nameof(PlayerStats.Awake), 0x310F490, nameof(BeforeServerStatsAwake));
            Patch(typeof(PlayerStats), nameof(PlayerStats.OnEnable), 0x310EB70, nameof(BeforeServerStatsLifecycle));
            Patch(typeof(PlayerStats), nameof(PlayerStats.OnDisable), 0x310F210, nameof(BeforeServerStatsLifecycle));
            Patch(typeof(PlayerStats), nameof(PlayerStats.OnDestroy), 0x3110580, nameof(BeforeServerStatsLifecycle));
            Patch(typeof(PlayerStats), nameof(PlayerStats.Start), 0x3110960, nameof(BeforeServerStatsLifecycle));
            Patch(typeof(PlayerStats), nameof(PlayerStats.Update), 0x3111330, nameof(BeforeServerStatsLifecycle));
            Patch(typeof(Vitals), nameof(Vitals.Awake), 0x32A91A0, nameof(BeforeServerVitalsAwake));
            Patch(typeof(Vitals), nameof(Vitals.Start), 0x32A5DA0, nameof(BeforeServerVitalsLifecycle));
            Patch(typeof(Vitals), nameof(Vitals.Update), 0x32A63A0, nameof(BeforeServerVitalsLifecycle));
            Patch(typeof(Vitals), nameof(Vitals.ValidateStats), 0x32A5E90, nameof(BeforeServerVitalsInitialization));
            var initialize = Qualify(typeof(Vitals), nameof(Vitals.InitializeStats), 0x32A5F40);
            harmony.Patch(initialize,
                prefix: new HarmonyMethod(typeof(PlayerCarrier), nameof(BeforeServerVitalsInitialization))
                    { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(PlayerCarrier), nameof(AfterServerVitalsInitialization))
                    { priority = Priority.Last });

            void Patch(Type type, string name, int rva, string prefix)
                => harmony.Patch(Qualify(type, name, rva),
                    prefix: new HarmonyMethod(typeof(PlayerCarrier), prefix) { priority = Priority.First });
        }

        private static bool BeforeServerStatsAwake(PlayerStats __instance, bool __runOriginal)
        {
            if (__instance is null) return __runOriginal;
            if (TryGetServerStatsOwner(__instance, out var existing))
            {
                existing._statsAwakeCount++;
                existing.FailureReason = "Carrier PlayerStats Awake was invoked more than once.";
                return false;
            }
            var carrier = PreparingServerStats;
            if (carrier == null || !HooksAvailable || !Same(__instance.gameObject, carrier._root)) return __runOriginal;
            // Register before disabling: Unity may synchronously call OnDisable here.
            carrier._serverStats = __instance;
            carrier._serverStatsInstance = __instance.GetInstanceID();
            carrier._statsAwakeCount++;
            ServerStatsOwners.Add(__instance.Pointer, carrier);
            __instance.enabled = false;
            return false;
        }

        private static bool BeforeServerVitalsAwake(Vitals __instance, bool __runOriginal)
        {
            if (__instance is null) return __runOriginal;
            if (TryGetServerVitalsOwner(__instance, out var existing))
            {
                existing._vitalsAwakeCount++;
                existing.FailureReason = "Carrier Vitals Awake was invoked more than once.";
                return false;
            }
            var carrier = PreparingServerStats;
            if (carrier == null || !HooksAvailable || !Same(__instance.gameObject, carrier._root)) return __runOriginal;
            carrier._serverVitals = __instance;
            carrier._serverVitalsInstance = __instance.GetInstanceID();
            carrier._vitalsAwakeCount++;
            ServerVitalsOwners.Add(__instance.Pointer, carrier);
            __instance.enabled = false;
            return false;
        }

        // Keep these exact receivers isolated through disposal/native destruction. Losing the
        // entity attachment must never release them into LocalPlayer-based startup or cleanup.
        private static bool BeforeServerStatsLifecycle(PlayerStats __instance, bool __runOriginal)
            => __runOriginal && !TryGetServerStatsOwner(__instance, out _);

        private static bool BeforeServerVitalsLifecycle(Vitals __instance, bool __runOriginal)
            => __runOriginal && !TryGetServerVitalsOwner(__instance, out _);

        private static bool BeforeServerVitalsInitialization(Vitals __instance, bool __runOriginal)
            => __runOriginal && (__instance is null
                || !TryGetServerVitalsOwner(__instance, out var owner)
                || (HooksAvailable && owner._initializingServerVitals && !owner._disposed
                    && !owner._serverVitalsInitialized && owner.FailureReason == null));

        private static void AfterServerVitalsInitialization(Vitals __instance, bool __runOriginal)
        {
            if (!TryGetServerVitalsOwner(__instance, out var owner)) return;
            if (HooksAvailable && owner._initializingServerVitals && __runOriginal)
                owner._serverVitalsInitialized = true;
        }

        private static bool TryGetServerStatsOwner(PlayerStats stats, out PlayerCarrier owner)
        {
            owner = null;
            if (stats is null || !ServerStatsOwners.TryGetValue(stats.Pointer, out owner)) return false;
            // Do not test Unity's alive flag: OnDestroy still needs isolation. The instance ID
            // distinguishes a future native component which reuses the same object address.
            if (stats.GetInstanceID() == owner._serverStatsInstance) return true;
            ServerStatsOwners.Remove(stats.Pointer);
            owner = null;
            return false;
        }

        private static bool TryGetServerVitalsOwner(Vitals vitals, out PlayerCarrier owner)
        {
            owner = null;
            if (vitals is null || !ServerVitalsOwners.TryGetValue(vitals.Pointer, out owner)) return false;
            if (vitals.GetInstanceID() == owner._serverVitalsInstance) return true;
            ServerVitalsOwners.Remove(vitals.Pointer);
            owner = null;
            return false;
        }

        private static void PruneServerStatOwners()
        {
            foreach (var entry in new List<KeyValuePair<IntPtr, PlayerCarrier>>(ServerStatsOwners))
                if (!Alive(entry.Value._serverStats)) ServerStatsOwners.Remove(entry.Key);
            foreach (var entry in new List<KeyValuePair<IntPtr, PlayerCarrier>>(ServerVitalsOwners))
                if (!Alive(entry.Value._serverVitals)) ServerVitalsOwners.Remove(entry.Key);
        }

        /// <summary>
        /// Read-only native startup admission for a replacement replica or owner. Requires the
        /// observation hooks to have been installed BEFORE creation. No retrospective Start call
        /// or subscription reconstruction is attempted. The original local rig is not a replica.
        /// </summary>
        public static BoltMigrationAdmission ReadNativeInitialization(BoltEntity replacement, out string detail)
        {
            detail = "The replacement's native equipment initialization has not been observed.";
            if (!HooksAvailable || !Alive(replacement) || !replacement.isAttached
                || replacement.prefabId != BoltPrefabs.PlayerNetwork)
                return BoltMigrationAdmission.Rejected;
            try
            {
                var setup = replacement.GetComponent<BoltPlayerSetup>();
                var remotes = replacement.GetComponentsInChildren<CoopPlayerRemoteSetup>(true);
                if (!Alive(setup) || !Same(setup.entity, replacement) || remotes.Length != 1)
                    return BoltMigrationAdmission.Rejected;
                var remote = remotes[0];
                if (!Alive(remote) || !Same(remote.entity, replacement)
                    || !Same(remote._armourSystem, setup._armourSystem)
                    || !Same(remote._clothingSystem, setup._clothingSystem)
                    || !Same(remote._raceSystem, setup._raceSystem)
                    || !Same(remote._inventoryProps, setup._inventoryProps)
                    || !Alive(remote._playerAnimator)) return BoltMigrationAdmission.Rejected;
                var armour = setup._armourSystem;
                if (!ArmourStarts.TryGetValue(armour.Pointer, out var observed))
                    return BoltMigrationAdmission.Pending;
                if (!observed.Completed || observed.Count != 1 || !Same(observed.Component, armour)
                    || observed.Instance != armour.GetInstanceID() || !Same(armour._clothingSystem, setup._clothingSystem)
                    || !HasExactArmourSubscription(armour))
                {
                    detail = "Native armour Start or its exact clothing subscription could not be confirmed.";
                    return BoltMigrationAdmission.Rejected;
                }
                if (!remote._playerAnimator.isInitialized)
                { detail = "The replacement animator has not completed native initialization."; return BoltMigrationAdmission.Pending; }
                var state = replacement._entity?.Serializer?.TryCast<global::PlayerState>();
                if (state == null || state.WasCollected) return BoltMigrationAdmission.Rejected;
                if (state.Animator == null)
                { detail = "The replacement animator has not been bound to native player state."; return BoltMigrationAdmission.Pending; }
                if (!Same(state.Animator, remote._playerAnimator))
                { detail = "The player state's animator does not belong to this replacement's native presentation."; return BoltMigrationAdmission.Rejected; }
                detail = "Native armour Start, exact clothing subscription and replacement presentation bindings are initialized.";
                return BoltMigrationAdmission.Ready;
            }
            catch (Exception error)
            { detail = "Native initialization inspection failed (" + error.GetType().Name + ")."; return BoltMigrationAdmission.Rejected; }
        }

        private static void AfterArmourStart(PlayerArmourSystem __instance, bool __runOriginal)
        {
            if (!HooksAvailable || !Alive(__instance)) return;
            try
            {
                // Keep only live instances. The read-only admission method never prunes or mutates.
                foreach (var entry in new List<KeyValuePair<IntPtr, ArmourStart>>(ArmourStarts))
                    if (!Alive(entry.Value.Component)) ArmourStarts.Remove(entry.Key);
                if (!ArmourStarts.TryGetValue(__instance.Pointer, out var state)
                    || state.Instance != __instance.GetInstanceID())
                {
                    state = new ArmourStart(__instance);
                    ArmourStarts[__instance.Pointer] = state;
                }
                state.Count++;
                state.Completed = __runOriginal && state.Count == 1 && HasExactArmourSubscription(__instance);
            }
            catch (Exception)
            {
                if (ArmourStarts.TryGetValue(__instance.Pointer, out var state)) state.Completed = false;
            }
        }

        private static bool HasExactArmourSubscription(PlayerArmourSystem armour)
        {
            if (!Alive(armour._clothingSystem)) return false;
            var root = armour._clothingSystem.OnClothingEquipped;
            if (root == null || root.WasCollected) return false;
            int count = 0;
            var entries = root.TryCast<Il2CppSystem.MulticastDelegate>()?.delegates;
            if (entries == null) return Matches(root);
            if (entries.WasCollected || entries.Length == 0 || entries.Length > 256) return false;
            foreach (var entry in entries)
            {
                if (entry == null || entry.WasCollected || entry.TryCast<Il2CppSystem.MulticastDelegate>()?.delegates != null)
                    return false;
                if (Matches(entry)) count++;
            }
            return count == 1;

            bool Matches(Il2CppSystem.Delegate entry) => entry.Target != null && !entry.Target.WasCollected
                && entry.Target.Pointer == armour.Pointer && entry.Method != null
                && string.Equals(entry.Method.Name, nameof(PlayerArmourSystem.OnClothingEquippedEvent), StringComparison.Ordinal)
                && entry.Method.DeclaringType?.FullName == typeof(PlayerArmourSystem).FullName
                && entry.Method.GetParameters().Length == 2
                && entry.Method.GetParameters()[0].ParameterType.FullName == "System.Int32"
                && entry.Method.GetParameters()[1].ParameterType.FullName == typeof(global::Sons.Inventory.ItemInstance).FullName;
        }

        private static bool BeforeOwnerPublisher(CoopMecanimReplicator __instance, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!HooksAvailable || __instance == null) return true;
            foreach (var carrier in Carriers.Values)
                if (carrier._replicator.Pointer == __instance.Pointer)
                    return (!carrier._presentationHeld || PlayerPreservation.AllowsNativeAnimationInvocation(__instance))
                        && !carrier._disposed && carrier.FailureReason == null
                        && carrier._survivalRestored && carrier._bridge?.IsGameplayActive == true
                        && carrier.IsCurrentForMotor;
            // RemoteAttached can precede the carrier lease. Ownership alone must not open
            // native owner publication during that prepared creation interval.
            return !PlayerRoles.IsCurrentCarrier(__instance.entity);
        }

        private static void AfterCarrierDetached(BoltPlayerSetup __instance, bool __runOriginal)
        {
            if (!HooksAvailable || __instance == null || !__runOriginal) return;
            PlayerCarrier detached = null;
            foreach (var carrier in Carriers.Values)
                if (carrier._setup.Pointer == __instance.Pointer) { detached = carrier; break; }
            if (detached == null) return;
            detached.Dispose();
            Carriers.Remove(detached._entity.Pointer);
            if (Alive(__instance._armourSystem)) ArmourStarts.Remove(__instance._armourSystem.Pointer);
        }

        private static MethodInfo Qualify(Type type, string name, int rva)
        {
            var method = AccessTools.DeclaredMethod(type, name, Type.EmptyTypes);
            if (method == null || method.IsStatic || method.ReturnType != typeof(void))
                throw new MissingMethodException(type.FullName, name);
            var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
            if (field?.GetValue(null) is not nint info || info == 0)
                throw new InvalidOperationException("Native startup method information is unavailable: " + name);
            var module = GetModuleHandle("GameAssembly.dll");
            if (module == 0 || Marshal.ReadIntPtr(info) != module + rva)
                throw new InvalidOperationException("Native startup entry changed: " + name);
            return method;
        }

        private sealed class ArmourStart
        {
            internal readonly PlayerArmourSystem Component;
            internal readonly int Instance;
            internal int Count;
            internal bool Completed;
            internal ArmourStart(PlayerArmourSystem component) { Component = component; Instance = component.GetInstanceID(); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
        private static extern nint GetModuleHandle(string name);
    }
}
