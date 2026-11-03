// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Items.Core;
using global::Sons.Gameplay;
using global::Sons.Multiplayer;
using global::Sons.Player.Stats;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerLifePresentation
    {
        private static readonly List<PlayerLifePresentation> Owners = new();
        private static readonly Dictionary<string, MethodInfo> StatsMethods = new(StringComparer.Ordinal);
        private static bool _installAttempted, _installed;
        private static int _hookThread;
        private readonly record struct HookState(PlayerLifePresentation Owner, string Call, int Depth);

        internal static void Install(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (_installAttempted) return;
            _installAttempted = true;
            HarmonyLib.Harmony harmony = null;
            try
            {
                if (support?.SafeForHarmonyPatches != true || !GameBuild.IsRecognized
                    || !OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
                _hookThread = Environment.CurrentManagedThreadId;
                var targets = new List<(MethodInfo Method, string Prefix, string Finalizer)>();
                void Stats(string name, int rva, params Type[] args)
                {
                    var method = Qualify(typeof(PlayerStats), name, rva, typeof(void), false, args);
                    StatsMethods.Add(name, method);
                    targets.Add((method, nameof(BeforeLifeStats), nameof(AfterLifeStats)));
                }
                Stats(nameof(PlayerStats.CheckDeath), 0x3114BF0, typeof(DeathType));
                Stats(nameof(PlayerStats.StartDeathRoutine), 0x3119B90, typeof(DeathType));
                Stats(nameof(PlayerStats.DoBasicDeath), 0x311AD50, typeof(bool));
                Stats(nameof(PlayerStats.CheckFirstDeathRoutine), 0x3118FD0);
                Stats(nameof(PlayerStats.CheckFirstDeathOption), 0x3118FE0, typeof(bool));
                Stats(nameof(PlayerStats.KillPlayer), 0x311B250);
                Stats(nameof(PlayerStats.KillPlayerWithRespawnPrompt), 0x311B270, typeof(bool));
                Stats(nameof(PlayerStats.resetInjuredBool), 0x3115020);
                Stats(nameof(PlayerStats.disablePlayerControl), 0x3114E50);
                Stats(nameof(PlayerStats.ResetHit), 0x3115E00);
                Stats(nameof(PlayerStats.HealedMp), 0x311BCD0, typeof(bool));
                Stats(nameof(PlayerStats.SetDyingState), 0x311C950, typeof(PlayerStats.DyingState));
                Stats(nameof(PlayerStats.StopBurning), 0x3115780);
                Stats(nameof(PlayerStats.SetOnFireState), 0x3115A40, typeof(bool));
                Stats(nameof(PlayerStats.StartResetFromHeal), 0x311C160);
                Stats(nameof(PlayerStats.FinalizeResetFromHeal), 0x311C3B0);
                // These remain forbidden even after disposal; all can escape into global
                // LocalPlayer or a new cutscene/actor before a simple flag gate can stop them.
                Stats(nameof(PlayerStats.MultiplayerRespawn), 0x311B790);
                Stats(nameof(PlayerStats.FirstDeathRoutine), 0x31190B0);
                Stats(nameof(PlayerStats.OnDeathMarkerRequestResponse), 0x3119640, typeof(DeathMarkerRequest));
                Stats(nameof(PlayerStats.StartDrownWakeup), 0x3119AC0);
                Stats(nameof(PlayerStats.InstantRespawnRoutine), 0x3118DD0);
                Stats(nameof(PlayerStats.FinalizeKillPlayer), 0x311B8D0);
                Stats(nameof(PlayerStats.EndDeathRoutine), 0x31198F0);
                Stats(nameof(PlayerStats.EndSwimmingDeathRoutine), 0x3119990);
                Stats(nameof(PlayerStats.CheckFirstDeathRoutineNoPrompt), 0x3118FC0);
                Stats(nameof(PlayerStats.QuickKillPlayer), 0x311B260);
                Stats(nameof(PlayerStats.SetFinalDeath), 0x311C8F0);
                void Hook(Type type, string name, int rva, Type returns, bool isStatic,
                    string prefix, string finalizer, params Type[] args)
                    => targets.Add((Qualify(type, name, rva, returns, isStatic, args), prefix, finalizer));
                Hook(typeof(PlayerStats), nameof(PlayerStats.TryGetPlayerState), 0x31187D0, typeof(bool), false,
                    nameof(BeforeLifePlayerState), null, typeof(IPlayerState).MakeByRefType());
                Hook(typeof(PlayerEffects), nameof(PlayerEffects.ToggleBurningFx), 0x3367180, typeof(void), false,
                    nameof(BeforeBurningFx), nameof(AfterBurningFx), typeof(bool));
                Hook(typeof(PlayerEffects), nameof(PlayerEffects.DisableFireObject), 0x2CA2530, typeof(void), false,
                    nameof(BeforeDisableFire), nameof(AfterDisableFire));
                Hook(typeof(Vitals), nameof(Vitals.SetHealth), 0x32A4900, typeof(void), false,
                    nameof(BeforeLifeHealth), null, typeof(float));
                Hook(typeof(MonoBehaviour), "InvokeDelayed", 0x44F1700, typeof(void), true,
                    nameof(BeforeLifeInvoke), null, typeof(MonoBehaviour), typeof(string), typeof(float), typeof(float));
                Hook(typeof(MonoBehaviour), nameof(MonoBehaviour.StartCoroutine), 0x44F11A0, typeof(Coroutine), false,
                    nameof(BeforeLifeCoroutine), null, typeof(Il2CppSystem.Collections.IEnumerator));
                Hook(typeof(PlayerStats), nameof(PlayerStats.ResetPlayerFromHeal), 0x311C0D0,
                    typeof(Il2CppSystem.Collections.IEnumerator), false, nameof(BeforeResetFactory), nameof(AfterResetFactory));
                Hook(typeof(PlayerStats._ResetPlayerFromHeal_d__264), "MoveNext", 0x32A23C0, typeof(bool), false,
                    nameof(BeforeResetWorker), nameof(AfterLifeWorker));
                Hook(typeof(PlayerStats._DyingMpAudio_d__248), "MoveNext", 0x32A17F0, typeof(bool), false,
                    nameof(BeforeAudioWorker), nameof(AfterLifeWorker));
                Hook(typeof(PlayerAnimatorControl), nameof(PlayerAnimatorControl.smoothEnableLayerNew), 0x35B0660,
                    typeof(Il2CppSystem.Collections.IEnumerator), false, nameof(BeforeLayerFactory), nameof(AfterLayerFactory), typeof(int));
                Hook(typeof(PlayerAnimatorControl._smoothEnableLayerNew_d__286), "MoveNext", 0x35C1160, typeof(bool), false,
                    nameof(BeforeLayerWorker), nameof(AfterLifeWorker));
                Hook(typeof(PlayerStats._MultiplayerRespawnWorker_d__260), "MoveNext", 0x32A1A50, typeof(bool), false,
                    nameof(BeforeRespawnWorker), null);
                Hook(typeof(MultiplayerRespawnTimer), nameof(MultiplayerRespawnTimer.DoRespawn), 0x2E53C10, typeof(void), false,
                    nameof(BeforeLifeRespawnTimer), null);
                Hook(typeof(PlayerInventory), nameof(PlayerInventory.MemorizeEquippedSlot), 0x30EB520, typeof(void), false,
                    nameof(BeforeLifeInventory), null, typeof(EquipmentSlot));
                Hook(typeof(PlayerInventory), nameof(PlayerInventory.UnequipItemAtSlot), 0x30EABF0, typeof(void), false,
                    nameof(BeforeLifeInventory), null, typeof(EquipmentSlot), typeof(bool), typeof(bool), typeof(bool),
                    typeof(bool), typeof(bool), typeof(bool));
                Hook(typeof(PlayerInventory), nameof(PlayerInventory.StashRightHandItem), 0x30EBD30, typeof(void), false,
                    nameof(BeforeLifeInventory), null, typeof(bool), typeof(bool), typeof(bool));
                Hook(typeof(PlayerInventory), nameof(PlayerInventory.StashHeldItems), 0x30EBED0, typeof(void), false,
                    nameof(BeforeLifeInventory), null, typeof(bool), typeof(bool));
                Hook(typeof(PlayerInventory), nameof(PlayerInventory.EquipPreviousLeftHandItem), 0x30EB710, typeof(void), false,
                    nameof(BeforeLifeInventory), null, typeof(bool));
                Hook(typeof(PlayerInventory), nameof(PlayerInventory.EquipPreviousRightHandItem), 0x30EB810, typeof(void), false,
                    nameof(BeforeLifeInventory), null, typeof(bool));
                // Qualify the whole graph before installing any gate. Shared hooks call the
                // typed dispatchers below; this owner does not install competing mid-action patches.
                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-player-life-presentation");
                foreach (var target in targets)
                    harmony.Patch(target.Method,
                        prefix: new HarmonyMethod(typeof(PlayerLifePresentation), target.Prefix) { priority = Priority.First + 100 },
                        finalizer: target.Finalizer == null ? null
                            : new HarmonyMethod(typeof(PlayerLifePresentation), target.Finalizer) { priority = Priority.Last });
                _installed = true;
            }
            catch (Exception error)
            {
                try { harmony?.UnpatchSelf(); } catch (Exception) { }
                warning?.Invoke("Source native life presentation unavailable: " + error.Message);
            }
        }

        private static MethodInfo Qualify(Type type, string name, int rva, Type returns, bool isStatic, params Type[] args)
            => PlayerLifecycle.QualifyNative(type, name, rva, isStatic, returns, args);

        private static PlayerLifePresentation FindStats(PlayerStats stats)
        {
            if (stats == null || stats.WasCollected) return null;
            foreach (var owner in Owners)
                if (owner._stats.Pointer == stats.Pointer) return owner;
            // These native methods use globals even when the receiver is an old actor.
            foreach (var owner in Owners)
                if (!owner.IsDisposed && Same(owner._stats, LocalPlayer.Stats)) return owner;
            return null;
        }

        private bool CanEnter(string name, object[] args)
        {
            if (!_pending || !_projected || !Same(_stats, LocalPlayer.Stats)) return false;
            string parent = Call;
            bool allowed = parent == null ? _entry == name : name switch
            {
                nameof(PlayerStats.StartDeathRoutine) => parent == nameof(PlayerStats.CheckDeath),
                nameof(PlayerStats.DoBasicDeath) => parent == nameof(PlayerStats.StartDeathRoutine),
                nameof(PlayerStats.CheckFirstDeathOption) => parent == nameof(PlayerStats.CheckFirstDeathRoutine),
                nameof(PlayerStats.KillPlayerWithRespawnPrompt) => parent == nameof(PlayerStats.KillPlayer),
                nameof(PlayerStats.SetDyingState) => parent is nameof(PlayerStats.HealedMp) or nameof(PlayerStats.KillPlayerWithRespawnPrompt),
                nameof(PlayerStats.StopBurning) => parent == nameof(PlayerStats.KillPlayerWithRespawnPrompt),
                nameof(PlayerStats.SetOnFireState) => parent == nameof(PlayerStats.StopBurning),
                nameof(PlayerStats.StartResetFromHeal) or nameof(PlayerStats.FinalizeResetFromHeal) => parent == "ResetWorker",
                _ => false
            };
            if (!allowed || (_seen.Contains(name) && !(name == nameof(PlayerStats.StopBurning)
                && _burnExecuted && _burnStopCalls == 1 && parent == nameof(PlayerStats.KillPlayerWithRespawnPrompt)))) return false;
            return name switch
            {
                nameof(PlayerStats.CheckDeath) or nameof(PlayerStats.StartDeathRoutine)
                    => args.Length == 1 && (DeathType)args[0] == _ticket.DeathType,
                nameof(PlayerStats.DoBasicDeath) or nameof(PlayerStats.CheckFirstDeathOption)
                    => args.Length == 1 && (bool)args[0],
                nameof(PlayerStats.HealedMp) or nameof(PlayerStats.KillPlayerWithRespawnPrompt)
                    or nameof(PlayerStats.SetOnFireState)
                    => args.Length == 1 && !(bool)args[0],
                nameof(PlayerStats.SetDyingState) => args.Length == 1 && (PlayerStats.DyingState)args[0]
                    == (_ticket.Action == PlayerLifeSourceAction.StartDeath
                        ? PlayerStats.DyingState.Dying : PlayerStats.DyingState.Alive),
                _ => true
            };
        }

        private HookState Enter(string call)
        {
            var state = new HookState(this, call, _calls.Count);
            _calls.Add(call);
            return state;
        }

        private static bool BeforeLifeStats(PlayerStats __instance, MethodBase __originalMethod,
            object[] __args, bool __runOriginal, out HookState __state)
        {
            __state = default;
            var owner = FindStats(__instance);
            if (owner == null) return true;
            if (__originalMethod.Name == nameof(PlayerStats.SetOnFireState)
                && owner.Call != nameof(PlayerStats.StopBurning)) return true;
            try
            {
                owner.RequireCurrent();
                if (!__runOriginal || !Same(owner._stats, __instance) || !owner.CanEnter(__originalMethod.Name, __args))
                    return false;
                __state = owner.Enter(__originalMethod.Name);
                owner._seen.Add(__originalMethod.Name);
                if (__originalMethod.Name == nameof(PlayerStats.StopBurning))
                    owner.BeforeNativeBurning();
                if (__originalMethod.Name == nameof(PlayerStats.SetOnFireState) && ++owner._burnStateCalls != 1)
                    throw new InvalidOperationException("Native life repeated its burning state callback.");
                return true;
            }
            catch (Exception error) { owner.Fail(error.Message, out _); return false; }
        }

        private static Exception AfterLifeStats(bool __runOriginal, Exception __exception, HookState __state)
        {
            var owner = __state.Owner;
            if (owner == null) return __exception;
            try
            {
                owner.RequireCurrent();
                if (!__runOriginal || __exception != null || owner.Call != __state.Call)
                    throw new InvalidOperationException("Native life callback failed, was suppressed or changed unowned numbers: " + __state.Call);
                if (__state.Call == nameof(PlayerStats.StopBurning))
                    owner.AfterNativeBurning();
                if (__state.Call == nameof(PlayerStats.SetOnFireState)
                    ? !owner._projection.LifeBurningNumbersCurrent(owner, owner._ticket, owner._burnReceipt)
                    : !owner._projection.SourceNumbersCurrent())
                    throw new InvalidOperationException("Native life callback changed unowned numbers: " + __state.Call);
                if (__state.Call == nameof(PlayerStats.FinalizeResetFromHeal))
                {
                    if (owner._layerWorker?.Epoch != owner._epoch)
                        throw new InvalidOperationException("Native get-up did not retain its actual layer blend.");
                    owner._finalized = true;
                }
                if (__state.Call == nameof(PlayerStats.KillPlayerWithRespawnPrompt))
                {
                    var timer = owner._stats._mpRespawnTimerGui;
                    if (!Live(timer) || !timer.gameObject.activeInHierarchy
                        || owner._stats.IsInvoking(nameof(PlayerStats.HitPoison)) || owner._stats.IsInvoking(nameof(PlayerStats.HitFire)))
                        throw new InvalidOperationException("Native downed prompt did not retain its actual timer.");
                    owner._timer = timer;
                }
            }
            catch (Exception error) { owner.Fail(error.Message, out _); }
            finally { owner.Leave(__state); }
            return __exception;
        }

        private void Leave(HookState state)
        {
            if (_calls.Count != state.Depth + 1 || Call != state.Call)
            { Fail("Native life callback scope became unbalanced.", out _); _calls.Clear(); }
            else _calls.RemoveAt(_calls.Count - 1);
        }

        // Shared BeforeConsumptionMidAction calls this first and skips the native writer
        // when handled. SetIsInMidAction's other effect is an unauthorized Bolt publication.
        internal static bool TryHandleNativeLifeMidAction(FirstPersonCharacter character, bool value)
        {
            foreach (var owner in Owners)
            {
                if (character == null || character.WasCollected || character.Pointer != owner._character.Pointer
                    || owner.Call == null) continue;
                try
                {
                    owner.RequireCurrent();
                    bool expected = owner.Call is nameof(PlayerStats.StartDeathRoutine) or nameof(PlayerStats.HealedMp);
                    if ((!expected && owner.Call is not (nameof(PlayerStats.FinalizeResetFromHeal)
                        or nameof(PlayerStats.KillPlayerWithRespawnPrompt))) || value != expected)
                        throw new InvalidOperationException("Unexpected native life mid-action write.");
                    character._isInMidAction = value;
                    owner._midActionCalls++;
                }
                catch (Exception error) { owner.Fail(error.Message, out _); }
                return true;
            }
            return false;
        }

        private static bool BeforeLifePlayerState(PlayerStats __instance, ref IPlayerState __0, ref bool __result)
        {
            var owner = FindStats(__instance);
            if (owner == null || owner.Call is not (nameof(PlayerStats.SetDyingState) or nameof(PlayerStats.SetOnFireState))) return true;
            try
            {
                owner.RequireCurrent();
                bool fire = owner.Call == nameof(PlayerStats.SetOnFireState);
                if (!Same(owner._stats, __instance) || (fire ? ++owner._burnPublications : ++owner._dyingWrites) != 1)
                    throw new InvalidOperationException("Unexpected native life Bolt write boundary.");
            }
            catch (Exception error) { owner.Fail(error.Message, out _); }
            // These exact native scopes use this lookup only for Dying or FX_Fire
            // publication. The server publishes; all source effects remain native.
            __0 = null; __result = false;
            return false;
        }

        private static bool BeforeLifeHealth(Vitals __instance, float __0)
        {
            foreach (var owner in Owners)
            {
                if (__instance == null || __instance.WasCollected || owner._vitals.Pointer != __instance.Pointer
                    || owner.Call == null) continue;
                try
                {
                    owner.RequireCurrent();
                    if (owner.Call != nameof(PlayerStats.HealedMp) || !float.IsFinite(__0) || ++owner._healthSetters > 2
                        || (owner._healthSetters == 1 && __0 != 28f)
                        || (owner._healthSetters == 2 && __0 != owner._vitals._lowHealthThreshold + 1f))
                        throw new InvalidOperationException("Unexpected numeric writer inside native life.");
                    // Vitals.SetHealth changes only health and target. The exact command
                    // already projected their final values; never expose its intermediate heal.
                }
                catch (Exception error) { owner.Fail(error.Message, out _); }
                return false;
            }
            return true;
        }

    }
}
