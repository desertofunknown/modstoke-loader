// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Inventory;
using TheForest.Items.Inventory;
using TheForest.Items.Special;
using TheForest.Utils;
using UnityEngine;
using HeldAuthority = Modstoke.Sons.PlayerHeldOnlyAuthority;
using LiftRoutine = TheForest.Items.Special.HeldOnlyItemController.__c__DisplayClass32_0.ObjectCompilerGeneratedNPrivateSealedIEnumerator1ObjectIEnumeratorIDisposableInObGacuObObUnique;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        private static bool _heldHooksAttempted, _heldHooksInstalled, _heldObservationFailed;
        private static int _heldHookThread;
        [ThreadStatic] private static HeldCall _heldCall;
        private static readonly Dictionary<IntPtr, HeldRoutine> HeldRoutines = new();
        internal enum HeldHook { Lift, PutDown, Remove, DropAll, Infinite, FullLoad, LiftRoutine, RendererRoutine, SpawnRoutine, ForcedDrop }
        internal sealed class HeldCall
        {
            internal HeldCall Parent;
            internal HeldOnlyItemController Controller;
            internal HeldOnlySource Source;
            internal HeldHook Kind;
            internal HeldRoutine Routine;
            internal bool Entry, CreatedDelay;
            internal int BeforeCount;
        }

        /// <summary>Install before Bolt/player creation so pre-freeze native delayed work can drain.</summary>
        internal static void InstallHeldOnlyHooks(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (_heldHooksAttempted) return;
            _heldHooksAttempted = true;
            if (support?.SafeForHarmonyPatches != true || !GameBuild.IsRecognized || BoltNetwork.isRunning
                || HeldOnlySource.Live(LocalPlayer.GameObject)) return;
            HarmonyLib.Harmony harmony = null;
            try
            {
                _heldHookThread = Environment.CurrentManagedThreadId;
                var type = typeof(HeldOnlyItemController);
                var targets = new List<(MethodInfo Method, string Prefix, string Finalizer)>();
                void Add(Type owner, string name, Type[] args, Type result, int rva, int length, string hash, string prefix, string finalizer = null)
                {
                    var method = AccessTools.Method(owner, name, args);
                    QualifyBoundary(method, (result, rva, length, hash));
                    targets.Add((method, prefix, finalizer));
                }
                Add(type, "Lift", new[] { typeof(int), typeof(ItemInstance) }, typeof(bool), 0x30D50D0, 1520,
                    "E61268995D3B23705B603815F808B9D36F7673155A47DBFEBB151D730B3165F6", nameof(BeforeHeldLift), nameof(AfterHeldBoolean));
                Add(type, "PutDown", new[] { typeof(bool), typeof(bool), typeof(bool), typeof(GameObject), typeof(int), typeof(float) }, typeof(bool), 0x30D5C50, 432,
                    "5D9726EAA762433F0A33871C159B94E3369B2B96D50BF1B70238749A27B5F244", nameof(BeforeHeldPutDown), nameof(AfterHeldBoolean));
                Add(type, "RemoveHeldItem", new[] { typeof(bool), typeof(bool) }, typeof(void), 0x30D5750, 592,
                    "523F661AA197384434CD3E9F83AF9239FC36D5F3104A903B45D671A7D9CA3835", nameof(BeforeHeldRemove), nameof(AfterHeldVoid));
                Add(type, "DropAll", Type.EmptyTypes, typeof(void), 0x30D5A50, 512,
                    "AF0EF22286282D6B4AD4C69ED61E2E9E2A81E023265FA2E85C728F95A999C1FA", nameof(BeforeHeldDropAll), nameof(AfterHeldVoid));
                // Each setter and its setting callback share ONE native body. Patch once.
                Add(type, "SetInfiniteItemsHackSetting", new[] { typeof(bool) }, typeof(void), 0x30D7430, 16,
                    "B13302ACBD05ABB55827074296CB9CF483D8BB75B6D9ED82B19A8D662593DB49", nameof(BeforeHeldInfinite), nameof(AfterHeldVoid));
                Add(type, "SetPlaceFullLoadHackSetting", new[] { typeof(bool) }, typeof(void), 0x30D7440, 16,
                    "59AAEFB42060B3F74929DD4D2F7D25597C5C76E6D8291207176E7C6C61E5C634", nameof(BeforeHeldFullLoad), nameof(AfterHeldVoid));
                Add(type, "UpdateBoltEntity", Type.EmptyTypes, typeof(void), 0x30D7140, 448,
                    "6BC4CC4B64CE1757096D989639B1A7B900051A0EA0F2A217B4CCBFB4D1D5549E", nameof(BlockHeldWriter));
                Add(type, "InitSettings", Type.EmptyTypes, typeof(void), 0x30D79D0, 256,
                    "B572548887943133C62863D94B67777D07587B15DC874CD27D93D07F9D4753B0", nameof(BlockHeldWriter));
                Add(type, "Sons_Save_ISaveGameSerializer_TheForest_Items_Special_HeldOnlyItemController_HeldOnlyItemSaveData__OnDeserialize",
                    new[] { typeof(HeldOnlyItemController.HeldOnlyItemSaveData) }, typeof(void), 0x30D7730, 672,
                    "EF252818640F4D764CE0BBDD87A0192C919BDA10D944EA38B958369CA2CB0B43", nameof(BlockHeldWriter));
                // Knockdown/swimming also share one native body, including inlined DropAll.
                Add(type, "IKnockDownReceiver_OnKnockDownStarted", Type.EmptyTypes, typeof(void), 0x30D7450, 480,
                    "50353D82A381E11D83BEB21EFAAC59883C36E1ED72D2A40CE40ABCA8A534C7AD", nameof(BeforeHeldForcedDrop), nameof(AfterHeldVoid));
                Add(type, "DelaySetActive", new[] { typeof(GameObject), typeof(bool), typeof(float), typeof(bool) }, typeof(Il2CppSystem.Collections.IEnumerator), 0x30D59A0, 176,
                    "06EE2A4F9FBDAC9CD81FE550EF478CC29AAFC9F0CC125764B588FF019CC4A1FE", nameof(BlockHeldFactory));
                Add(type, "SpawnDropItemDelayed", new[] { typeof(GameObject), typeof(float), typeof(int) }, typeof(Il2CppSystem.Collections.IEnumerator), 0x30D5E00, 288,
                    "0513E2AE3582B8E3884C260BBF357E220B26F5EB951282CBC02D8CC3FA83A085", nameof(BlockHeldFactory));
                Add(type, "SpawnHeldItemDrop", new[] { typeof(GameObject), typeof(int), typeof(ItemInstance) }, typeof(void), 0x30D5F20, 4640,
                    "F91D2D2D66065FB3DB504AB2F1B81D6B9E981D63DE57808C4FFBE5B68EB2D09A", nameof(BeforeHeldSpawn));
                Add(typeof(LiftRoutine), "MoveNext", Type.EmptyTypes, typeof(bool), 0x30D8510, 1600,
                    "2A809E82D7E39AE507900A3BD85B8A5DA0228BC6D8E13862AC07E05B8F04C238", nameof(BeforeHeldLiftRoutine), nameof(AfterHeldRoutine));
                Add(typeof(HeldOnlyItemController._DelaySetActive_d__36), "MoveNext", Type.EmptyTypes, typeof(bool), 0x30D8B90, 416,
                    "9E5A60D5695837B3477C4419CCFD23FA961267833D487BA3B892326E21E36AAF", nameof(BeforeHeldRendererRoutine), nameof(AfterHeldRoutine));
                Add(typeof(HeldOnlyItemController._SpawnDropItemDelayed_d__39), "MoveNext", Type.EmptyTypes, typeof(bool), 0x30D8D70, 528,
                    "4878113CCBE24F238159865EA58753781F6A9EA5C4293991384A26B4810714E9", nameof(BeforeHeldSpawnRoutine), nameof(AfterHeldRoutine));
                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-player-held-only-authority");
                foreach (var target in targets)
                    harmony.Patch(target.Method,
                        prefix: new HarmonyMethod(typeof(PlayerInventoryEnforcement), target.Prefix) { priority = Priority.First },
                        finalizer: target.Finalizer == null ? null : new HarmonyMethod(typeof(PlayerInventoryEnforcement), target.Finalizer) { priority = Priority.Last });
                _heldHooksInstalled = true;
            }
            catch (Exception error)
            {
                string cleanup = "";
                try { RollbackInventoryHooks(harmony); } catch (Exception rollback) { cleanup = " Rollback unconfirmed: " + rollback.Message; }
                warning?.Invoke("Held-only native gates unavailable: " + error.Message + cleanup);
            }
        }

        private static bool ProtectedHeld(HeldOnlyItemController controller)
        {
            if (controller == null) return false;
            if (HeldSourceGuards.ContainsKey(controller.Pointer)) return true;
            return PlayerLifecycle.IsInventoryIngressFenced(controller._player, out _);
        }

        private static bool BlockHeldWriter(HeldOnlyItemController __instance) => !ProtectedHeld(__instance);
        private static bool BlockHeldFactory(HeldOnlyItemController __instance, ref Il2CppSystem.Collections.IEnumerator __result)
        {
            if (!ProtectedHeld(__instance)) return true;
            // Native Lift/Remove/PutDown/DropAll inline their factories; only their exact
            // first MoveNext is admitted. Standalone delayed writer factories are not permits.
            __result = null;
            return false;
        }

        private static bool BeginHeld(HeldOnlyItemController controller, HeldHook kind, bool runOriginal, bool arguments,
            out HeldCall call)
        {
            call = null;
            if (!runOriginal) return false;
            bool protect = ProtectedHeld(controller);
            if (Environment.CurrentManagedThreadId != _heldHookThread)
            { if (protect) _heldOnlySource?.Fault("Held-only writer entered off its game thread."); return !protect; }
            HeldSourceGuards.TryGetValue(controller.Pointer, out var source);
            bool entry = source != null && source.EntryPermit && _heldCall == null;
            if (protect)
            {
                if (source == null)
                {
                    // Drain already-entered synchronous native work; no new top-level ingress.
                    if (_heldCall?.Controller?.Pointer != controller.Pointer) return false;
                }
                else if (!source.Lifetime(true) || source.Command == null || !arguments) return false;
            }
            if (source != null)
            {
                if (entry)
                {
                    var expected = source.Command.Kind;
                    if (!(kind == HeldHook.Lift && expected == HeldAuthority.MutationKind.Lift
                        || kind == HeldHook.PutDown && expected == HeldAuthority.MutationKind.PutDown
                        || kind == HeldHook.Remove && expected == HeldAuthority.MutationKind.Remove
                        || kind == HeldHook.DropAll && expected == HeldAuthority.MutationKind.DropAll
                        || kind == HeldHook.ForcedDrop && HeldAuthority.IsForcedDrop(expected)
                        || (kind is HeldHook.Infinite or HeldHook.FullLoad) && expected == HeldAuthority.MutationKind.Settings)) return false;
                    source.EntryPermit = false;
                }
                else if (kind != HeldHook.Remove || _heldCall?.Source != source
                    || _heldCall.Kind is not (HeldHook.PutDown or HeldHook.DropAll or HeldHook.ForcedDrop)
                    || source.RemovalCount >= (source.Command.Kind == HeldAuthority.MutationKind.DropAll
                        || HeldAuthority.IsForcedDrop(source.Command.Kind) ? source.Command.Before.State.Count : 1))
                    return false;
            }
            call = new HeldCall { Parent = _heldCall, Controller = controller, Source = source, Kind = kind,
                Entry = entry, BeforeCount = controller._heldCount };
            _heldCall = call;
            return true;
        }

        private static bool BeforeHeldLift(HeldOnlyItemController __instance, int __0, ItemInstance __1,
            bool __runOriginal, ref bool __result, out HeldCall __state)
        {
            var source = _heldOnlySource;
            bool ok = BeginHeld(__instance, HeldHook.Lift, __runOriginal,
                source?.Command != null && __0 == source.Command.After.State.ItemId && __1?.Pointer == source.LiftItem?.Pointer
                    && source.LiftIdentity?.Matches(__1) == true, out __state);
            if (!ok) { __result = false; CaptureHeldIntent(__instance, HeldAuthority.MutationKind.Lift, __0, __1, false, false, false, null, -1); }
            return ok;
        }

        private static bool BeforeHeldPutDown(HeldOnlyItemController __instance, bool __0, bool __1, bool __2,
            GameObject __3, int __4, float __5, bool __runOriginal, ref bool __result, out HeldCall __state)
        {
            bool ok = BeginHeld(__instance, HeldHook.PutDown, __runOriginal,
                !__0 && __1 == _heldOnlySource?.Command?.Drop && !__2 && __3 == null && __4 == -1 && __5 == -1f, out __state);
            if (!ok) { __result = false; CaptureHeldIntent(__instance, HeldAuthority.MutationKind.PutDown, __4, null, __1, __0, __2, __3, __5); }
            return ok;
        }

        private static bool BeforeHeldRemove(HeldOnlyItemController __instance, bool __0, bool __1, bool __runOriginal, out HeldCall __state)
        {
            bool forced = _heldCall?.Kind == HeldHook.ForcedDrop && _heldCall.Controller?.Pointer == __instance.Pointer;
            bool ok = BeginHeld(__instance, HeldHook.Remove, __runOriginal,
                __0 == forced && __1 == _heldOnlySource?.Command?.Drop, out __state);
            if (!ok) CaptureHeldIntent(__instance, HeldAuthority.MutationKind.Remove, __instance._heldItemId, null, __1, false, __0, null, -1);
            return ok;
        }

        private static bool BeforeHeldDropAll(HeldOnlyItemController __instance, bool __runOriginal, out HeldCall __state)
        {
            bool ok = BeginHeld(__instance, HeldHook.DropAll, __runOriginal, true, out __state);
            if (!ok) CaptureHeldIntent(__instance, HeldAuthority.MutationKind.DropAll, __instance._heldItemId, null, true, false, false, null, -1);
            return ok;
        }

        private static bool BeforeHeldForcedDrop(HeldOnlyItemController __instance, bool __runOriginal, out HeldCall __state)
        {
            // Both interface methods have the same body. Only the exact ordered owner
            // chooses the cause; an unsolicited shared-body callback cannot establish it.
            bool ok = BeginHeld(__instance, HeldHook.ForcedDrop, __runOriginal,
                _heldOnlySource?.Command is { } command && HeldAuthority.IsForcedDrop(command.Kind), out __state);
            if (!ok && ProtectedHeld(__instance))
                _heldOnlySource?.NoteUnownedForcedCallback(__instance);
            return ok;
        }

        private static bool BeforeHeldInfinite(HeldOnlyItemController __instance, bool __0, bool __runOriginal, out HeldCall __state)
        {
            bool ok = BeginHeld(__instance, HeldHook.Infinite, __runOriginal, __0 == _heldOnlySource?.Command?.After.State.InfiniteHack, out __state);
            if (!ok) CaptureHeldIntent(__instance, HeldAuthority.MutationKind.Settings, __instance._heldItemId, null, false, false, false, null, -1, infinite: __0);
            return ok;
        }
        private static bool BeforeHeldFullLoad(HeldOnlyItemController __instance, bool __0, bool __runOriginal, out HeldCall __state)
        {
            bool ok = BeginHeld(__instance, HeldHook.FullLoad, __runOriginal, __0 == _heldOnlySource?.Command?.After.State.PlaceFullLoadHack, out __state);
            if (!ok) CaptureHeldIntent(__instance, HeldAuthority.MutationKind.Settings, __instance._heldItemId, null, false, false, false, null, -1, full: __0);
            return ok;
        }

        private static Exception AfterHeldBoolean(bool __runOriginal, bool __result, Exception __exception, HeldCall __state)
            => FinishHeld(__state, __runOriginal && __result, __exception);
        private static Exception AfterHeldVoid(bool __runOriginal, Exception __exception, HeldCall __state)
            => FinishHeld(__state, __runOriginal, __exception);

        private static Exception FinishHeld(HeldCall call, bool success, Exception error)
        {
            if (call == null) return error;
            try
            {
                var source = call.Source;
                if (source != null)
                {
                    if (!success || error != null || !source.Lifetime(true)) source.Fault("Native held-only entry did not return normally.");
                    else
                    {
                        if (call.Kind == HeldHook.Remove)
                        {
                            int debit = source.Command.Drop || !source.Command.Before.State.InfiniteHack || call.BeforeCount > 1 ? 1 : 0;
                            if (call.Controller._heldCount != call.BeforeCount - debit || call.CreatedDelay != (debit == 1))
                                source.Fault("Native removal or renderer permit disagreed with its reservation.");
                            source.RemovalCount++;
                        }
                        if (call.Entry) source.EntryDone = true;
                    }
                }
                else if (error != null) _heldObservationFailed = true;
            }
            catch (Exception failure) { call.Source?.Fault(failure.Message); _heldObservationFailed = true; }
            finally { _heldCall = call.Parent; }
            return error;
        }
    }
}
