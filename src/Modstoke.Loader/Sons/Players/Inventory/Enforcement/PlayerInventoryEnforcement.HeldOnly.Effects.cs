// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
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
        internal sealed class HeldRoutine
        {
            internal Il2CppObjectBase Instance;
            internal HeldOnlyItemController Controller;
            internal HeldOnlySource Source;
            internal HeldHook Kind;
            internal int Phase, NextPhase, DropIndex;
            internal bool Running, Done, SpawnCaptured;
            internal GameObject Target;
            internal float Delay;
            internal ItemInstance Outgoing;
            internal HeldAuthority.RepresentativeIdentity OutgoingIdentity;
            internal PlayerInventoryBaseline.Entry OutgoingState;
        }

        internal static bool HasObservedHeldOnlyWork(PlayerInventory inventory, bool includeForcedRequests = true)
        {
            if (!_heldHooksInstalled || _heldObservationFailed || Environment.CurrentManagedThreadId != _heldHookThread) return true;
            if (_heldCall?.Controller?._player?.Pointer == inventory?.Pointer) return true;
            foreach (var routine in HeldRoutines.Values)
                if (!routine.Done && routine.Controller?._player?.Pointer == inventory?.Pointer) return true;
            return _heldOnlySource?.Inventory?.Pointer == inventory?.Pointer
                && (_heldOnlySource.HasPendingCustody || includeForcedRequests
                    && (_heldOnlySource.HasUnownedForcedCallback
                        || _heldOnlySource.InventoryOwner?.HeldOnlyRoute?.HasPendingForcedDrop == true));
        }

        private static HeldOnlyItemController RendererOwner(GameObject target)
        {
            if (!HeldOnlySource.Live(target)) return null;
            var controller = _heldOnlySource?.Controller;
            if (HasRenderer(controller, target)) return controller;
            foreach (var guard in HeldSourceGuards.Values)
                if (HasRenderer(guard.Controller, target)) return guard.Controller;
            controller = LocalPlayer.Inventory?.HeldOnlyItemController?.TryCast<HeldOnlyItemController>();
            return HasRenderer(controller, target) ? controller : null;
        }

        private static bool HasRenderer(HeldOnlyItemController controller, GameObject target)
        {
            if (!HeldOnlySource.Live(controller) || controller._heldOnlyItems == null) return false;
            foreach (var item in controller._heldOnlyItems)
                if (item?._held != null) foreach (var renderer in item._held)
                    if (renderer?.Pointer == target.Pointer) return true;
            return false;
        }

        private static bool BeginHeldRoutine(Il2CppObjectBase instance, HeldOnlyItemController controller,
            HeldHook kind, int phase, GameObject target, float delay, bool runOriginal, out HeldCall call)
        {
            call = null;
            if (!runOriginal || instance == null || instance.WasCollected) return false;
            bool protect = ProtectedHeld(controller);
            if (Environment.CurrentManagedThreadId != _heldHookThread) return !protect;
            HeldOnlySource source = null;
            if (controller != null) HeldSourceGuards.TryGetValue(controller.Pointer, out source);
            try
            {
                if (!HeldRoutines.TryGetValue(instance.Pointer, out var routine))
                {
                    if (phase != 0) { if (protect) source?.Fault("Unobserved delayed held-only writer."); else _heldObservationFailed = true; return !protect; }
                    if (source != null)
                    {
                        if (!source.Lifetime(true) || source.Command == null || _heldCall?.Source != source) return false;
                        switch (kind)
                        {
                            case HeldHook.LiftRoutine:
                                if (_heldCall.Kind != HeldHook.Lift || source.Command.After.State.Count != 1
                                    || source.Routines.Count != 0 || target?.Pointer != controller.GetCurrentHeldGo()?.Pointer) return false;
                                break;
                            case HeldHook.RendererRoutine:
                                if (_heldCall.Kind != HeldHook.Remove || _heldCall.CreatedDelay
                                    || target?.Pointer != controller.GetCurrentHeldGo()?.Pointer
                                    || delay != (source.Command.Drop ? controller.GetHeldItem()._dropSpawnDelay : 0f)) return false;
                                _heldCall.CreatedDelay = true;
                                break;
                            case HeldHook.SpawnRoutine:
                                if (_heldCall.Kind is not (HeldHook.PutDown or HeldHook.DropAll or HeldHook.ForcedDrop)
                                    || source.DropStarts >= source.Command.DropCount
                                    || delay != controller.GetHeldItem()._dropSpawnDelay) return false;
                                break;
                        }
                    }
                    else if (protect && PlayerLifecycle.IsInventoryIngressFenced(controller._player, out bool frozen) && frozen)
                        return false;
                    routine = new HeldRoutine { Instance = instance, Controller = controller, Source = source,
                        Kind = kind, NextPhase = 0, Target = target, Delay = delay };
                    if (source != null && kind == HeldHook.SpawnRoutine)
                    {
                        routine.Outgoing = controller._heldItemInstance;
                        routine.OutgoingIdentity = new HeldAuthority.RepresentativeIdentity(routine.Outgoing);
                        routine.OutgoingState = new PlayerInventoryBaseline.Entry
                        { ItemId = routine.Outgoing._itemID,
                            Modules = PlayerInventoryBaseline.CaptureModules(routine.Outgoing),
                            ModuleState = PlayerInventoryBaseline.CaptureModuleState(routine.Outgoing) };
                        routine.DropIndex = source.DropStarts++;
                        source.Drops.Add(null);
                    }
                    HeldRoutines.Add(instance.Pointer, routine);
                    source?.Routines.Add(routine);
                }
                if (routine.Done || routine.Running || routine.Kind != kind || routine.Controller?.Pointer != controller?.Pointer
                    || routine.Instance.WasCollected || routine.NextPhase != phase
                    || routine.Target?.Pointer != target?.Pointer || routine.Delay != delay) return false;
                if (routine.Source != null && (!ReferenceEquals(source, routine.Source) || !source.Lifetime(true)
                    || source.Command == null || _heldCall != null && phase != 0)) return false;
                routine.Phase = phase; routine.Running = true;
                call = new HeldCall { Parent = _heldCall, Source = source, Controller = controller, Kind = kind, Routine = routine };
                _heldCall = call;
                return true;
            }
            catch (Exception error) { source?.Fault(error.Message); if (source == null) _heldObservationFailed = true; return false; }
        }

        private static bool BeforeHeldLiftRoutine(LiftRoutine __instance, bool __runOriginal, ref bool __result, out HeldCall __state)
        {
            __state = null;
            var controller = __instance?.__4__this?.__4__this;
            bool ok = BeginHeldRoutine(__instance, controller, HeldHook.LiftRoutine, __instance?.__1__state ?? -1,
                __instance?.currentHeld, 0, __runOriginal, out __state);
            if (!ok) __result = false;
            return ok;
        }

        private static bool BeforeHeldRendererRoutine(HeldOnlyItemController._DelaySetActive_d__36 __instance,
            bool __runOriginal, ref bool __result, out HeldCall __state)
        {
            __state = null;
            var controller = __instance != null && HeldRoutines.TryGetValue(__instance.Pointer, out var tracked)
                ? tracked.Controller : RendererOwner(__instance?.target);
            if (controller == null) return true;
            if (ProtectedHeld(controller) && (__instance.activeValue || !float.IsFinite(__instance.delay) || __instance.delay < 0))
            { __result = false; return false; }
            bool ok = BeginHeldRoutine(__instance, controller, HeldHook.RendererRoutine, __instance.__1__state,
                __instance.target, __instance.delay, __runOriginal, out __state);
            if (!ok) __result = false;
            return ok;
        }

        private static bool BeforeHeldSpawnRoutine(HeldOnlyItemController._SpawnDropItemDelayed_d__39 __instance,
            bool __runOriginal, ref bool __result, out HeldCall __state)
        {
            __state = null;
            var controller = __instance?.__4__this;
            if (ProtectedHeld(controller) && (__instance.preSpawned != null || __instance.itemId != controller._heldItemId
                || !float.IsFinite(__instance.delay) || __instance.delay < 0)) { __result = false; return false; }
            bool ok = BeginHeldRoutine(__instance, controller, HeldHook.SpawnRoutine, __instance?.__1__state ?? -1,
                null, __instance?.delay ?? 0, __runOriginal, out __state);
            if (!ok) __result = false;
            return ok;
        }

        private static Exception AfterHeldRoutine(bool __runOriginal, bool __result, Exception __exception, HeldCall __state)
        {
            if (__state == null) return __exception;
            var routine = __state.Routine;
            try
            {
                var source = routine.Source;
                if (!__runOriginal || __exception != null) throw new InvalidOperationException("Native held-only coroutine did not return normally.");
                if (source != null)
                {
                    if (!source.Lifetime(true)) throw new InvalidOperationException("Delayed held-only source lifetime changed.");
                    if (routine.Kind == HeldHook.SpawnRoutine)
                    {
                        var native = (HeldOnlyItemController._SpawnDropItemDelayed_d__39)routine.Instance;
                        if (native._heldItemInstance_5__2?.Pointer != routine.Outgoing?.Pointer
                            || !routine.OutgoingIdentity.Matches(routine.Outgoing)
                            || !PlayerInventoryBaseline.MatchesInstance(routine.OutgoingState, routine.Outgoing)
                            || !__result && !routine.SpawnCaptured)
                            throw new InvalidOperationException("Native outgoing representative or delayed drop receipt is missing.");
                    }
                    if (!__result && routine.Kind == HeldHook.RendererRoutine
                        && (!HeldOnlySource.Live(routine.Target) || routine.Target.activeSelf))
                        throw new InvalidOperationException("Native held renderer did not deactivate.");
                    if (!__result && routine.Kind == HeldHook.LiftRoutine
                        && (routine.Phase != 3 || source.MidActionPhases != 3 || !HeldOnlySource.Live(routine.Target) || !routine.Target.activeSelf))
                        throw new InvalidOperationException("Native lift did not finish its original animation/mid-action path.");
                }
                if (__result)
                {
                    routine.NextPhase = routine.Kind == HeldHook.LiftRoutine ? routine.Phase + 1 : 1;
                    if (routine.Phase >= (routine.Kind == HeldHook.LiftRoutine ? 3 : 1))
                        throw new InvalidOperationException("Native held-only routine yielded outside its matched phase graph.");
                }
                else { routine.Done = true; HeldRoutines.Remove(routine.Instance.Pointer); }
            }
            catch (Exception error)
            { if (routine.Source != null) routine.Source.Fault(error.Message); else _heldObservationFailed = true; }
            finally { routine.Running = false; _heldCall = __state.Parent; }
            return __exception;
        }

        private static bool BeforeHeldSpawn(HeldOnlyItemController __instance, GameObject __0, int __1, ItemInstance __2)
        {
            if (!ProtectedHeld(__instance)) return true;
            var source = _heldOnlySource;
            var routine = _heldCall?.Routine;
            try
            {
                if (source == null || !source.Lifetime(true) || _heldCall.Source != source || routine == null
                    || routine.Kind != HeldHook.SpawnRoutine || routine.Phase != 1 || routine.SpawnCaptured
                    || __instance.Pointer != source.Controller.Pointer || __0 != null
                    || __1 != source.Command.Before.State.ItemId || __2?.Pointer != routine.Outgoing?.Pointer
                    || !routine.OutgoingIdentity.Matches(__2)) return false;
                var current = __instance.GetHeldItem();
                var dropped = __instance.GetControlledItem(__1);
                int index = Math.Clamp(__1 == __instance._heldItemId ? __instance._heldCount : 0, 0, 1);
                if (current == null || dropped?._held == null || index >= dropped._held.Length
                    || !HeldOnlySource.Live(dropped._held[index])) throw new InvalidOperationException("Native drop renderer is unavailable.");
                var transform = dropped._held[index].transform;
                var prefab = dropped.GetItemCache().PickupPrefab;
                var prefabEntity = HeldOnlySource.Live(prefab) ? prefab.GetComponent<BoltEntity>() : null;
                if (!HeldOnlySource.Live(prefabEntity)) throw new InvalidOperationException("Native drop prefab identity is unavailable.");
                // Matched SpawnHeldItemDrop's multiplayer geometry, using only the retained
                // source camera. Do not run its unsequenced DropItem.Send or create a world copy.
                var position = (current._useDropOffset ? LocalPlayer.Transform.TransformPoint(__instance._dropSpawnOffset)
                    : transform.position) + current._dropPositionOffset;
                var rotation = transform.rotation;
                if (current._useDropOffset)
                    rotation *= Quaternion.Euler(RandomRange(__instance._dropSpawnRotationOffsetMin, __instance._dropSpawnRotationOffsetMax));
                rotation *= current._dropRotationOffset;
                var velocity = (source.Camera.forward + Vector3.up * __instance._logDropUpForce) * __instance._logDropForce;
                var torque = RandomRange(__instance._logDropTorqueMin, __instance._logDropTorqueMax) * __instance._logDropForce;
                torque = new Vector3(Math.Clamp(torque.x, -512f, 512f), Math.Clamp(torque.y, -512f, 512f), Math.Clamp(torque.z, -512f, 512f));
                if (!FiniteHeld(position) || !FiniteHeld(velocity) || !FiniteHeld(torque)
                    || !float.IsFinite(rotation.x) || !float.IsFinite(rotation.y) || !float.IsFinite(rotation.z) || !float.IsFinite(rotation.w))
                    throw new InvalidOperationException("Native drop geometry is not finite.");
                source.Drops[routine.DropIndex] = new HeldAuthority.NativeDropEffect(routine.DropIndex, __2, prefabEntity.prefabId.Value,
                    position, rotation, velocity, torque) { AudioTransform = transform };
                routine.SpawnCaptured = true;
            }
            catch (Exception error) { source?.Fault(error.Message); }
            return false;
        }

        private static Vector3 RandomRange(Vector3 minimum, Vector3 maximum)
            => new(UnityEngine.Random.Range(minimum.x, maximum.x), UnityEngine.Random.Range(minimum.y, maximum.y),
                UnityEngine.Random.Range(minimum.z, maximum.z));
        private static bool FiniteHeld(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }
}
