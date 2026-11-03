// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;
using GameInput = global::Sons.Input.InputSystem;

namespace Modstoke.Sons
{
    public sealed partial class PlayerLifecycle
    {
        private static readonly Dictionary<MethodBase, RoutineSpec> RoutineMethods = new();
        private static readonly Dictionary<IntPtr, RoutineSpec> RoutineClasses = new();
        private static readonly Dictionary<IntPtr, ObservedRoutine> Routines = new();
        private static readonly Dictionary<IntPtr, RetainedPublisher> ObservedActors = new();
        private static readonly Dictionary<IntPtr, RetainedPublisher> UnqualifiedActors = new();
        private static bool _routineTrackingFailed, _actionInstallFailed;
        private static HarmonyLib.Harmony _actionHarmony;
        private static bool ActionHooksAvailable => HooksAvailable && _actionHarmony != null && !_actionInstallFailed;
        private static PlayerLifecycle _actionFence;
        [ThreadStatic] private static int _maskedInputDepth;
        [ThreadStatic] private static RoutineStart _routineStart;
        [ThreadStatic] private static int _nativeActionDepth;

        private readonly List<Component> _actionActors = new();
        private readonly List<Component> _coreWriters = new();
        private readonly List<RetainedPublisher> _actionIdentities = new();
        private RetainedPublisher _inventoryIdentity;
        private FirstPersonCharacter _character;
        private PlayerAnimatorControl _animationControl;
        private playerHitReactions _hitReactions;
        private Animator _animator;
        private Rigidbody _body;

        /// <summary>
        /// Inventory ingress integration for the exact captured native inventory. While draining,
        /// reject new transactions but let previously accepted work settle. Once frozen, hold all
        /// ordinary writers until resume. This does not itself certify inventory quiescence.
        /// Independent inventory custody/authority guards remain in force after resume.
        /// </summary>
        public static bool IsInventoryIngressFenced(PlayerInventory inventory, out bool frozen)
        {
            frozen = false;
            var fence = _actionFence;
            if (fence == null || inventory == null || fence._inventoryIdentity == null
                || inventory.Pointer != fence._inventoryIdentity.Pointer) return false;
            try
            {
                if (!fence._inventoryIdentity.Matches(inventory)) return false;
                frozen = fence._freezeStarted;
                return true;
            }
            catch (Exception)
            {
                fence._fenceViolation = "The captured inventory ingress identity became unreadable.";
                frozen = true;
                return true;
            }
        }

        /// <summary>
        /// Explicit migration-mode activation, before Bolt start AND local-player creation.
        /// SDK startup does not install these observers. Late opt-in cannot reconstruct native
        /// routine history and is rejected. The parent retains the selection until run teardown.
        /// </summary>
        public static bool SelectActionTrackingForNextStart(out string detail)
        {
            detail = "Select native action tracking before Bolt start and local-player creation.";
            if (!HooksAvailable || BoltNetwork.isRunning || Alive(LocalPlayer.GameObject) || _actionFence != null || _sourceInputHold != null) return false;
            if (_actionInstallFailed)
            { detail = "A partial action-hook install was not removed; release it after stop before selecting again."; return false; }
            if (_actionHarmony != null) { detail = "Native action tracking is already selected for the next run."; return true; }
            var harmony = new HarmonyLib.Harmony(global::Modstoke.Loader.RuntimeEntryPoint.PluginGuid + ".sons-retained-player-actions");
            try
            {
                VehiclePreservation.QualifyCartPassengerExitHooks();
                InstallActionHooks(harmony);
                InstallIngressHooks(harmony);
                PlayerEventRouting.Install(harmony, QualifyNative);
                PlayerMigrationAdapter.InstallHooks(harmony, QualifyNative);
                _routineTrackingFailed = false;
                _actionHarmony = harmony;
                detail = "Native action tracking selected before player initialization; ordinary SDK startup remains inert.";
                return true;
            }
            catch (Exception error)
            {
                try
                {
                    harmony.UnpatchSelf();
                    PlayerEventRouting.ReleaseAfterStop();
                    PlayerMigrationAdapter.ReleaseHooksAfterStop();
                    RoutineMethods.Clear(); RoutineClasses.Clear();
                }
                catch (Exception) { _actionHarmony = harmony; _actionInstallFailed = true; }
                detail = "Native action tracking could not be installed (" + error.GetType().Name + ").";
                return false;
            }
        }

        /// <summary>Parent calls from its selected mode's driver; managed bookkeeping only.</summary>
        public static void PruneActionTracking()
        {
            if (!ActionHooksAvailable) return;
            try { PruneDeadActors(); PlayerMigrationAdapter.PruneReplicaTracking(); }
            catch (Exception) { _routineTrackingFailed = true; }
        }

        /// <summary>Explicit mode teardown after Bolt and every observed player have stopped.</summary>
        public static bool TryReleaseActionTrackingAfterStop(out string detail)
        {
            detail = "Action tracking still owns a live player, Bolt run or pause.";
            if (!HooksAvailable || BoltNetwork.isRunning || Alive(LocalPlayer.GameObject)) return false;
            try
            {
                PruneDeadActors();
                if (_actionFence != null || _sourceInputHold != null || ObservedActors.Count != 0 || Routines.Count != 0 || UnqualifiedActors.Count != 0) return false;
                PlayerEventRouting.ReleaseAfterStop();
                PlayerMigrationAdapter.ReleaseHooksAfterStop();
                _actionHarmony?.UnpatchSelf();
                _actionHarmony = null;
                RoutineMethods.Clear(); RoutineClasses.Clear();
                UnqualifiedActors.Clear();
                _actionInstallFailed = false;
                _routineTrackingFailed = false;
                detail = "Selected action tracking released after player teardown.";
                return true;
            }
            catch (Exception error)
            { detail = "Action tracking teardown is unconfirmed (" + error.GetType().Name + ")."; return false; }
        }

        private static void InstallActionHooks(HarmonyLib.Harmony harmony)
        {
            // Matched 2026-10-19 binary/metadata. Every entry below has one native implementation.
            // Do not hook FPC.LateUpdate: its shared body only clears per-frame collision records.
            var witnesses = new[]
            {
                Qualify(typeof(FirstPersonCharacter), "Awake", 0x2F7D990),
                Qualify(typeof(PlayerAnimatorControl), "Awake", 0x35A2170),
                Qualify(typeof(playerHitReactions), "Awake", 0x35C1780),
                Qualify(typeof(CoopMecanimReplicator), "Awake", 0x30F9C80),
                Qualify(typeof(BoltPlayerSetup), "Attached", 0x32BD390)
            };
            var writers = new[]
            {
                Qualify(typeof(FirstPersonCharacter), "Update", 0x2F7E640),
                Qualify(typeof(FirstPersonCharacter), "FixedUpdate", 0x2F7FF20),
                Qualify(typeof(PlayerAnimatorControl), "Update", 0x35A2D90),
                Qualify(typeof(PlayerAnimatorControl), "FixedUpdate", 0x35A44D0),
                Qualify(typeof(PlayerAnimatorControl), "LateUpdate", 0x35A4CF0),
                Qualify(typeof(PlayerAnimatorControl), "OnAnimatorMove", 0x35A5B50),
                Qualify(typeof(PlayerStats), "Update", 0x3111330),
                Qualify(typeof(SimpleMouseRotator), "Update", 0x2F911A0)
            };
            var booleanReads = new[]
            {
                QualifyNative(typeof(GameInput), "IsPressed", 0x2DCFBD0, true, typeof(bool), typeof(GameInput.Action)),
                QualifyNative(typeof(GameInput), "GetButtonDown", 0x2DCFCC0, true, typeof(bool), typeof(GameInput.Action)),
                QualifyNative(typeof(GameInput), "GetButtonPress", 0x2DCFFD0, true, typeof(bool), typeof(GameInput.Action)),
                QualifyNative(typeof(GameInput), "IsPastButtonPress", 0x2DD0010, true, typeof(bool), typeof(GameInput.Action)),
                QualifyNative(typeof(GameInput), "GetButton", 0x2DD0230, true, typeof(bool), typeof(GameInput.Action)),
                QualifyNative(typeof(GameInput), "GetNewInputAction", 0x2DD3430, true, typeof(bool), typeof(GameInput.Action)),
                QualifyNative(typeof(GameInput), "GetNewInputAction", 0x2DD3370, true, typeof(bool), typeof(InputState), typeof(GameInput.Action))
            };
            var axis = QualifyNative(typeof(GameInput), "GetAxis", 0x2DD0CD0, true, typeof(float), typeof(GameInput.Action));
            var startEnumerator = QualifyNative(typeof(MonoBehaviour), "StartCoroutineManaged2", 0x44F1840,
                false, typeof(Coroutine), typeof(Il2CppSystem.Collections.IEnumerator));
            var startName = QualifyNative(typeof(MonoBehaviour), "StartCoroutineManaged", 0x44F17D0,
                false, typeof(Coroutine), typeof(string), typeof(Il2CppSystem.Object));
            var stopEnumerator = Qualify(typeof(MonoBehaviour), "StopCoroutineFromEnumeratorManaged", 0x44F1900,
                typeof(Il2CppSystem.Collections.IEnumerator));
            var stopHandle = Qualify(typeof(MonoBehaviour), "StopCoroutineManaged", 0x44F18A0, typeof(Coroutine));
            var stopName = Qualify(typeof(MonoBehaviour), "StopCoroutine", 0x44F15A0, typeof(string));
            // This is the SAME verified native body as MonoBehaviour.StopAllCoroutines, not an
            // empty stub. Use the declaration already hooked by Roles so Harmony composes it.
            var stopAll = Qualify(typeof(CoopMecanimReplicator), "Detached", 0x30FA5A0);
            var routines = new[]
            {
                R(typeof(FirstPersonCharacter._DisableCrouch_d__350), 0x2F8B930, typeof(FirstPersonCharacter), "DisableCrouch", 0x2F87DC0),
                R(typeof(FirstPersonCharacter._DisconnectedSledRoutine_d__369), 0x2F8BD80, typeof(FirstPersonCharacter), "DisconnectedSledRoutine", 0x2F88F50),
                R(typeof(FirstPersonCharacter._DoForceStopRun_d__368), 0x2F8BEC0, typeof(FirstPersonCharacter), "DoForceStopRun", 0x2F88EC0),
                R(typeof(FirstPersonCharacter._EnableCrouch_d__349), 0x2F8C040, typeof(FirstPersonCharacter), "EnableCrouch", 0x2F87D30),
                R(typeof(FirstPersonCharacter._clampInput_d__372), 0x2F8C3F0, typeof(FirstPersonCharacter), "clampInput", 0x2F89110),
                R(typeof(FirstPersonCharacter._smoothEnableSpine_d__365), 0x2F8C570, typeof(FirstPersonCharacter), "smoothEnableSpine", 0x2F88C50),
                R(typeof(FirstPersonCharacter._startJumpTimer_d__371), 0x2F8C890, typeof(FirstPersonCharacter), "startJumpTimer", 0x2F89080),
                R(typeof(PlayerAnimatorControl._FinalizeSledding_d__393), 0x35BD990, typeof(PlayerAnimatorControl), "FinalizeSledding", 0x35B6930),
                R(typeof(PlayerAnimatorControl._ScaleCapsuleForSledding_d__399), 0x35BE080, typeof(PlayerAnimatorControl), "ScaleCapsuleForSledding", 0x35B8850, typeof(bool)),
                R(typeof(PlayerAnimatorControl._SmoothResetCameraYPositionRoutine_d__222), 0x35BE480, typeof(PlayerAnimatorControl), "SmoothResetCameraYPositionRoutine", 0x35A67C0),
                R(typeof(PlayerAnimatorControl._StandUpFromCrashRoutine_d__329), 0x35BE820, typeof(PlayerAnimatorControl), "StandUpFromCrashRoutine", 0x35B3F10, typeof(int)),
                R(typeof(PlayerAnimatorControl._fixExitClimbPosition_d__262), 0x35C0160, typeof(PlayerAnimatorControl), "fixExitClimbPosition", 0x35AF3E0, typeof(Vector3)),
                R(typeof(PlayerAnimatorControl._smoothDisableLayer_d__285), 0x35C0540, typeof(PlayerAnimatorControl), "smoothDisableLayer", 0x35B05B0, typeof(int), typeof(float)),
                R(typeof(PlayerAnimatorControl._smoothDisableLayerNew_d__297), 0x35C0830, typeof(PlayerAnimatorControl), "smoothDisableLayerNew", 0x35B07D0, typeof(int)),
                R(typeof(PlayerAnimatorControl._smoothDisableLayerNew_d__298), 0x35C0B50, typeof(PlayerAnimatorControl), "smoothDisableLayerNew", 0x35B0890, typeof(int), typeof(float)),
                R(typeof(PlayerAnimatorControl._smoothEnableLayer_d__284), 0x35C0E70, typeof(PlayerAnimatorControl), "smoothEnableLayer", 0x35B0500, typeof(int), typeof(float)),
                R(typeof(PlayerAnimatorControl._smoothEnableLayerNew_d__286), 0x35C1160, typeof(PlayerAnimatorControl), "smoothEnableLayerNew", 0x35B0660, typeof(int)),
                R(typeof(PlayerAnimatorControl._smoothEnableLayerNew_d__287), 0x35C1470, typeof(PlayerAnimatorControl), "smoothEnableLayerNew", 0x35B0720, typeof(int), typeof(float)),
                R(typeof(playerHitReactions._doHardfallRoutine_d__19), 0x35C2640, typeof(playerHitReactions), "doHardfallRoutine", 0x35C1FB0),
                R(typeof(playerHitReactions._enableExplodeCamera_d__11), 0x35C28E0, typeof(playerHitReactions), "enableExplodeCamera", 0x35C1B00),
                R(typeof(playerHitReactions._enablePushBack_d__22), 0x35C3AD0, typeof(playerHitReactions), "enablePushBack", 0x35C2160, typeof(float)),
                R(typeof(BoltPlayerSetup._WaitForInv_d__28), 0x344EA30, typeof(BoltPlayerSetup), "WaitForInv", 0x32BBD90, typeof(Il2CppSystem.Action)),
                R(typeof(CoopMecanimReplicator._SendTransitionEventDelayed_d__38), 0x30FE550, typeof(CoopMecanimReplicator), "SendTransitionEventDelayed", 0x30FBFD0,
                    typeof(int), typeof(int), typeof(float), typeof(BoltEntity), typeof(bool), typeof(int))
            };
            foreach (var method in witnesses)
                harmony.Patch(method, postfix: Hook(nameof(AfterActorInitialized), Priority.Last));
            foreach (var method in writers)
                harmony.Patch(method, prefix: Hook(nameof(BeforeCoreWriter)), finalizer: Hook(nameof(FinishCoreWriter)));
            foreach (var method in booleanReads) harmony.Patch(method, prefix: Hook(nameof(BeforeActionRead)));
            harmony.Patch(axis, prefix: Hook(nameof(BeforeAxisRead)));
            harmony.Patch(startEnumerator, prefix: Hook(nameof(BeforeStartEnumerator)),
                postfix: Hook(nameof(AfterStartRoutine)), finalizer: Hook(nameof(FinishStartRoutine)));
            harmony.Patch(startName, prefix: Hook(nameof(BeforeStartNamed)),
                postfix: Hook(nameof(AfterStartRoutine)), finalizer: Hook(nameof(FinishStartRoutine)));
            harmony.Patch(stopEnumerator, postfix: Hook(nameof(AfterStopEnumerator)));
            harmony.Patch(stopHandle, postfix: Hook(nameof(AfterStopHandle)));
            harmony.Patch(stopName, postfix: Hook(nameof(AfterStopNamed)));
            harmony.Patch(stopAll, postfix: Hook(nameof(AfterStopAll)));
            foreach (var routine in routines)
            {
                RoutineMethods.Add(routine.MoveNext, routine);
                RoutineClasses.Add(routine.NativeClass, routine);
                harmony.Patch(routine.MoveNext, prefix: Hook(nameof(BeforeRoutineMove)),
                    postfix: Hook(nameof(AfterRoutineMove)), finalizer: Hook(nameof(FinishRoutineMove)));
                if (routine.IsAnimationTransition)
                    harmony.Patch(routine.Factory, postfix: Hook(nameof(AfterAnimationContinuationCreated), Priority.Last));
            }
        }

        private static void AfterAnimationContinuationCreated(CoopMecanimReplicator __instance,
            int __0, int __1, float __2, BoltEntity __3, bool __4, int __5,
            Il2CppSystem.Collections.IEnumerator __result, bool __runOriginal)
        {
            if (!ActionHooksAvailable) return;
            if (!__runOriginal)
            { PlayerPreservation.FailNativeAnimationInvocation(__instance); return; }
            var continuation = PlayerPreservation.CaptureNativeAnimationContinuation(
                __instance, __result, __0, __1, __2, __3, __4, __5);
            if (continuation == null) return;
            try
            {
                var spec = RoutineClasses[IL2CPP.il2cpp_object_get_class(__result.Pointer)];
                if (Routines.ContainsKey(__result.Pointer))
                    throw new InvalidOperationException("Native animation factory reused an observed iterator.");
                RememberRoutine(__result, __instance, spec, null).Animation = continuation;
            }
            catch (Exception) { continuation.Fail(); UnqualifyActor(__instance); throw; }
        }

        private static HarmonyMethod Hook(string name, int priority = Priority.First)
            => new(typeof(PlayerLifecycle), name) { priority = priority };

        private static RoutineSpec R(Type iterator, int moveRva, Type owner, string factory, int factoryRva, params Type[] args)
            => new(iterator, QualifyNative(iterator, "MoveNext", moveRva, false, typeof(bool)),
                QualifyNative(owner, factory, factoryRva, false, typeof(Il2CppSystem.Collections.IEnumerator), args));

        private void CaptureActionActors()
        {
            _character = LocalPlayer.FpCharacter;
            _animationControl = LocalPlayer.AnimControl;
            _hitReactions = LocalPlayer.HitReactions;
            _animator = LocalPlayer.Animator;
            _body = LocalPlayer.Rigidbody;
            _stats = LocalPlayer.Stats;
            _actionActors.AddRange(new Component[] { _character, _animationControl, _hitReactions, _setup });
            _actionActors.AddRange(_animations);
            _coreWriters.AddRange(new Component[] { _character, _animationControl, LocalPlayer.CamRotator, LocalPlayer.MainRotator });
            _coreWriters.Add(_stats);
            foreach (var actor in _actionActors) _actionIdentities.Add(new RetainedPublisher(actor));
            foreach (var writer in _coreWriters)
                if (!_actionActors.Contains(writer)) _actionIdentities.Add(new RetainedPublisher(writer));
            _inventoryIdentity = new RetainedPublisher(LocalPlayer.Inventory);
            CaptureInteractionBoundaries();
            _identities.Add(new Identity(() => LocalPlayer.HitReactions));
            _identities.Add(new Identity(() => LocalPlayer.CamRotator));
            _identities.Add(new Identity(() => LocalPlayer.MainRotator));
        }

        private bool OwnsActor(Component component)
        {
            foreach (var actor in _actionActors)
                if (Same(actor, component)) return OwnsActionIdentity(component);
            return false;
        }

        private bool OwnsActionIdentity(Component component)
        {
            if (component == null) return false;
            foreach (var identity in _actionIdentities)
                if (identity.Pointer == component.Pointer) return identity.Matches(component);
            return false;
        }

        private static bool HasActiveNativeCall() => _nativeActionDepth != 0;

        internal static BoltMigrationAdmission ReadReplicaActionBoundary(
            BoltPlayerSetup setup, CoopMecanimReplicator replicator, out string detail)
        {
            detail = "Replica action observation was not selected before native initialization.";
            if (!ActionHooksAvailable || _routineTrackingFailed || !Alive(setup) || !Alive(replicator))
                return BoltMigrationAdmission.Rejected;
            if (!Observed(setup) || !Observed(replicator))
                return BoltMigrationAdmission.Rejected;
            if (Unqualified(setup) || Unqualified(replicator))
            {
                detail = "Replica coroutine ownership or cancellation could not be qualified.";
                return BoltMigrationAdmission.Rejected;
            }
            if (HasActiveNativeCall() || _routineStart != null
                || PlayerMigrationAdapter.HasActiveReplicaCall)
            {
                detail = "Wait for the active native replica call to return.";
                return BoltMigrationAdmission.Pending;
            }
            foreach (var routine in Routines.Values)
            {
                bool owned = (Same(routine.Owner, setup) || Same(routine.Owner, replicator))
                    && Alive(routine.Owner) && routine.Owner.GetInstanceID() == routine.Instance;
                bool scheduled = (Same(routine.Scheduler, setup) || Same(routine.Scheduler, replicator))
                    && Alive(routine.Scheduler) && routine.Scheduler.GetInstanceID() == routine.SchedulerInstance;
                if (!owned && !scheduled) continue;
                detail = "Replica continuation has not terminated: " + routine.Spec.Factory.Name + ".";
                // A continuation arriving after the hold cannot drain while suspended.
                // Reject that boundary instead of waiting forever or consuming its MoveNext.
                return routine.Failed || routine.Suspended ? BoltMigrationAdmission.Rejected
                    : BoltMigrationAdmission.Pending;
            }
            if (setup.IsInvoking() || replicator.IsInvoking())
            {
                detail = "A native replica Invoke remains scheduled.";
                return BoltMigrationAdmission.Pending;
            }
            detail = "Observed replica actions have no live continuations, invokes or native reentry.";
            return BoltMigrationAdmission.Ready;

            static bool Observed(Component actor) => ObservedActors.TryGetValue(actor.Pointer, out var value)
                && value.Matches(actor);
            static bool Unqualified(Component actor) => UnqualifiedActors.TryGetValue(actor.Pointer, out var value)
                && value.Matches(actor);
        }

        private bool OwnsPublisher(Component component)
        {
            if (Same(_setup, component)) return OwnsActionIdentity(component);
            foreach (var animation in _animations)
                if (Same(animation, component)) return OwnsActionIdentity(component);
            return false;
        }

        private static void AfterActorInitialized(Component __instance, bool __runOriginal)
        {
            if (!ActionHooksAvailable || !__runOriginal) return;
            try
            {
                if (Alive(__instance)) ObservedActors[__instance.Pointer] = new RetainedPublisher(__instance);
            }
            catch (Exception) { _routineTrackingFailed = true; }
        }

        private static bool BeforeCoreWriter(Component __instance, bool __runOriginal, out NativeActionCall __state)
        {
            __state = default;
            if (!__runOriginal) return false;
            if (!ActionHooksAvailable) return true;
            var fence = _actionFence;
            if (__instance == null) return true;
            bool mask = false;
            if (fence != null && fence.OwnsActionIdentity(__instance))
            {
                foreach (var writer in fence._coreWriters)
                {
                    if (!Same(writer, __instance)) continue;
                    if (fence._freezeStarted) return false;
                    // Camera look and existing continuations keep running while draining.
                    mask = Same(fence._character, __instance);
                    break;
                }
            }
            var sourceHold = _sourceInputHold;
            if (sourceHold?.HoldsWriter(__instance) == true) return false;
            mask |= sourceHold?.MasksInput(__instance) == true;
            __state = new NativeActionCall(mask);
            _nativeActionDepth++;
            if (mask) _maskedInputDepth++;
            return true;
        }

        private static void FinishCoreWriter(NativeActionCall __state)
        {
            if (!__state.Entered) return;
            if (__state.MaskedInput) _maskedInputDepth--;
            _nativeActionDepth--;
        }

        private static bool BeforeActionRead(bool __runOriginal, ref bool __result)
        {
            if (!__runOriginal) return false;
            if (_maskedInputDepth == 0) return true;
            __result = false;
            return false;
        }

        private static bool BeforeAxisRead(bool __runOriginal, ref float __result)
        {
            if (!__runOriginal) return false;
            if (_maskedInputDepth == 0) return true;
            __result = 0;
            return false;
        }

        private static bool TrackedActor(Component actor)
            => Alive(actor) && (actor.TryCast<FirstPersonCharacter>() != null
                || actor.TryCast<PlayerAnimatorControl>() != null || actor.TryCast<playerHitReactions>() != null
                || actor.TryCast<BoltPlayerSetup>() != null || actor.TryCast<CoopMecanimReplicator>() != null);

        private static void BeforeStartEnumerator(MonoBehaviour __instance, Il2CppSystem.Collections.IEnumerator __0,
            bool __runOriginal, out RoutineStart __state)
        {
            __state = null;
            if (!ActionHooksAvailable) return;
            if (!__runOriginal)
            {
                if (Live(__0) && Routines.TryGetValue(__0.Pointer, out var vetoed) && vetoed.Animation != null)
                { vetoed.Failed = true; vetoed.Animation.Fail(); }
                return;
            }
            try
            {
                YieldSourceHoldForActor(__instance);
                if (!Live(__0) || !RoutineClasses.TryGetValue(IL2CPP.il2cpp_object_get_class(__0.Pointer), out var spec))
                { if (TrackedActor(__instance)) UnqualifyActor(__instance); return; }
                // A known action iterator can be scheduled by a different MonoBehaviour. Keep
                // its scheduler separate from the actor it writes, so StopCoroutine is exact.
                var owner = spec.ReadOwner(__0) ?? (TrackedActor(__instance) ? __instance : null);
                if (!Alive(owner)) { _routineTrackingFailed = true; return; }
                YieldSourceHoldForActor(owner);
                __state = _routineStart = new RoutineStart(__instance, owner, null, _routineStart);
                if (Routines.TryGetValue(__0.Pointer, out var existing)
                    && (existing.Animation == null || existing.Scheduler != null))
                { UnqualifyActor(owner); existing.Animation?.Fail(); if (existing.Animation != null) existing.Failed = true; }
                __state.Routine = RememberRoutine(__0, owner, spec, __instance);
            }
            catch (Exception)
            {
                if (__state != null) UnqualifyActor(__state.Owner);
                else _routineTrackingFailed = true;
            }
        }

        private static void BeforeStartNamed(MonoBehaviour __instance, string __0, bool __runOriginal, out RoutineStart __state)
        {
            __state = null;
            if (!ActionHooksAvailable || !__runOriginal) return;
            try
            {
                YieldSourceHoldForActor(__instance);
                if (TrackedActor(__instance))
                    __state = _routineStart = new RoutineStart(__instance, __instance, __0, _routineStart);
            }
            catch (Exception) { UnqualifyActor(__instance); }
        }

        private static void AfterStartRoutine(RoutineStart __state, Coroutine __result)
        {
            if (__state == null) return;
            try
            {
                if (__state.Routine == null)
                { if (Live(__result)) UnqualifyActor(__state.Owner); return; }
                if (Live(__result)) __state.Routine.Handle = __result.Pointer;
                else if (Routines.ContainsKey(__state.Routine.Pointer))
                { __state.Routine.Failed = true; __state.Routine.Animation?.Fail(); }
            }
            catch (Exception) { UnqualifyActor(__state.Owner); }
        }

        private static void FinishStartRoutine(RoutineStart __state, Exception __exception)
        {
            if (__state == null) return;
            _routineStart = __state.Previous;
            if (__exception != null)
            {
                UnqualifyActor(__state.Owner);
                if (__state.Routine != null)
                { __state.Routine.Failed = true; __state.Routine.Animation?.Fail(); }
            }
        }

        private static void AfterStopEnumerator(MonoBehaviour __instance, Il2CppSystem.Collections.IEnumerator __0, bool __runOriginal)
        {
            if (!ActionHooksAvailable || !__runOriginal || Routines.Count == 0) return;
            try { if (Live(__0)) RemoveRoutines(__instance, __0.Pointer, static (r, pointer) => r.Pointer == pointer); }
            catch (Exception) { UnqualifyActor(__instance); }
        }

        private static void AfterStopHandle(MonoBehaviour __instance, Coroutine __0, bool __runOriginal)
        {
            if (!ActionHooksAvailable || !__runOriginal || Routines.Count == 0) return;
            try { if (Live(__0)) RemoveRoutines(__instance, __0.Pointer, static (r, handle) => r.Handle == handle); }
            catch (Exception) { UnqualifyActor(__instance); }
        }

        private static void AfterStopNamed(MonoBehaviour __instance, string __0, bool __runOriginal)
        {
            if (!ActionHooksAvailable || !__runOriginal || Routines.Count == 0) return;
            try { RemoveRoutines(__instance, __0, static (r, name) => r.StartedByName != null && r.StartedByName == name); }
            catch (Exception) { UnqualifyActor(__instance); }
        }

        private static void AfterStopAll(Component __instance, bool __runOriginal)
        {
            if (!ActionHooksAvailable || !__runOriginal || Routines.Count == 0) return;
            try { RemoveRoutines(__instance, true, static (_, _) => true); }
            catch (Exception) { UnqualifyActor(__instance); }
        }

        private static void RemoveRoutines<T>(Component owner, T key, Func<ObservedRoutine, T, bool> match)
        {
            if (!Alive(owner)) return;
            int instance = owner.GetInstanceID();
            List<IntPtr> remove = null;
            foreach (var pair in Routines)
                if (Same(pair.Value.Scheduler, owner) && pair.Value.SchedulerInstance == instance && match(pair.Value, key))
                {
                    if (pair.Value.Animation != null && !pair.Value.Animation.Sent)
                    { pair.Value.Animation.Fail(); pair.Value.Failed = true; }
                    else (remove ??= new()).Add(pair.Key);
                }
            if (remove != null)
                foreach (var pointer in remove) Routines.Remove(pointer);
        }

        private static void PruneDeadActors()
        {
            List<IntPtr> dead = null;
            foreach (var pair in Routines)
                if (!Alive(pair.Value.Owner) || pair.Value.Owner.GetInstanceID() != pair.Value.Instance
                    || (pair.Value.Scheduler != null && (!Alive(pair.Value.Scheduler)
                        || pair.Value.Scheduler.GetInstanceID() != pair.Value.SchedulerInstance))) (dead ??= new()).Add(pair.Key);
            if (dead != null)
            {
                foreach (var pointer in dead) Routines.Remove(pointer);
                dead.Clear();
            }
            foreach (var pair in ObservedActors) if (!pair.Value.IsAlive) (dead ??= new()).Add(pair.Key);
            if (dead != null)
            {
                foreach (var pointer in dead) ObservedActors.Remove(pointer);
                dead.Clear();
            }
            foreach (var pair in UnqualifiedActors) if (!pair.Value.IsAlive) (dead ??= new()).Add(pair.Key);
            if (dead != null)
                foreach (var pointer in dead) UnqualifiedActors.Remove(pointer);
            PruneRetainedPublishers();
            if (_sourceInputHold?.TargetsGone() == true)
            {
                _sourceInputHold.Bridge.Retired -= _sourceInputHold.OnBridgeRetired;
                _sourceInputHold = null;
            }
            var fence = _actionFence;
            if (fence != null && !Alive(fence.Root) && !Alive(fence._body) && !Alive(fence._animator))
            {
                foreach (var actor in fence._actionActors) if (Alive(actor)) return;
                foreach (var writer in fence._coreWriters) if (Alive(writer)) return;
                if (fence._inventoryIdentity?.IsAlive == true) return;
                // Teardown is the only non-resume release: every owned native target is gone.
                // Never drop a partial pause merely because Bolt stopped or a scene disabled it.
                _actionFence = null;
            }
        }

        private static void UnqualifyActor(Component owner)
        {
            try
            {
                if (!Alive(owner)) { _routineTrackingFailed = true; return; }
                // Untracked iterators re-enter here on every MoveNext; keep the existing fault.
                if (!UnqualifiedActors.TryGetValue(owner.Pointer, out var fault) || !fault.Matches(owner))
                    UnqualifiedActors[owner.Pointer] = new RetainedPublisher(owner);
                if (_actionFence != null && _actionFence.OwnsActor(owner))
                    _actionFence._fenceViolation = "A retained native coroutine start or cancellation could not be qualified.";
                if (_sourceInputHold?.Owns(owner) == true)
                    _sourceInputHold.Fail("A rebound source coroutine start or cancellation could not be qualified.");
            }
            catch (Exception) { _routineTrackingFailed = true; }
        }

        private static ObservedRoutine RememberRoutine(Il2CppObjectBase value, Component owner, RoutineSpec spec, Component scheduler)
        {
            if (Routines.TryGetValue(value.Pointer, out var existing))
            {
                if (existing.Animation != null && existing.Scheduler == null && Same(existing.Owner, owner)
                    && existing.Instance == owner.GetInstanceID() && ReferenceEquals(existing.Spec, spec)
                    && Same(scheduler, owner))
                {
                    existing.Scheduler = scheduler;
                    existing.SchedulerInstance = scheduler.GetInstanceID();
                    return existing;
                }
                if (!Same(existing.Owner, owner) || existing.Instance != owner.GetInstanceID()
                    || !ReferenceEquals(existing.Spec, spec) || !Same(existing.Scheduler, scheduler))
                { UnqualifyActor(existing.Owner); UnqualifyActor(owner); }
                return existing;
            }
            var result = new ObservedRoutine(value, owner, spec, scheduler);
            Routines.Add(value.Pointer, result);
            return result;
        }

        private static bool BeforeRoutineMove(Il2CppObjectBase __instance, MethodBase __originalMethod,
            bool __runOriginal, out RoutineMove __state, ref bool __result)
        {
            __state = default;
            if (!ActionHooksAvailable) return __runOriginal;
            if (!__runOriginal)
            {
                if (Routines.TryGetValue(__instance.Pointer, out var vetoed) && vetoed.Animation != null)
                { vetoed.Failed = true; vetoed.Animation.Fail(); __state = new RoutineMove(vetoed, false); }
                return false;
            }
            ObservedRoutine routine = null;
            bool animationEntered = false;
            try
            {
                if (!Routines.TryGetValue(__instance.Pointer, out routine))
                {
                    var spec = RoutineMethods[__originalMethod];
                    var start = _routineStart;
                    // Only the root MoveNext of a named start may claim its scheduler. A nested
                    // direct MoveNext is not proof of shared Unity cancellation ownership.
                    bool namedRoot = start?.Name != null && start.Routine == null
                        && spec.Factory.Name == start.Name;
                    var owner = spec.ReadOwner(__instance) ?? (namedRoot ? start.Owner : null);
                    if (!Alive(owner)) { _routineTrackingFailed = true; return true; }
                    YieldSourceHoldForActor(owner);
                    if (!namedRoot)
                    {
                        // No observed StartCoroutine means no qualified cancellation lifetime
                        // (for example a manually driven or nested iterator). Keep one actor
                        // fault, not an unbounded set of handles Unity may never advance again.
                        UnqualifyActor(owner);
                        if (VehiclePreservation.IsOccupiedPlayerWriterHeld(owner)
                            || VehiclePreservation.IsOccupiedPlayerWriterHeld(start?.Scheduler)
                            || (spec.IsAnimationTransition && PlayerMigrationAdapter.IsReplicaWriterHeld(owner)))
                        { __result = true; return false; }
                        return true;
                    }
                    routine = RememberRoutine(__instance, owner, spec, start.Scheduler);
                    start.Routine = routine;
                    routine.StartedByName = start.Name;
                }
                YieldSourceHoldForActor(routine.Owner);
                if (VehiclePreservation.IsOccupiedPlayerWriterHeld(routine.Owner)
                    || VehiclePreservation.IsOccupiedPlayerWriterHeld(routine.Scheduler))
                { routine.Suspended = true; __result = true; return false; }
                // Factory-time admission is not interchangeable with starting an old iterator
                // under a later binding. Only the retained new-owner creation carries this token.
                if (routine.Animation != null)
                {
                    var start = _routineStart;
                    if (routine.Scheduler == null && start?.Name == routine.Spec.Factory.Name && start.Routine == null
                        && Same(start.Owner, routine.Owner) && Same(start.Scheduler, routine.Owner))
                    {
                        routine.Scheduler = start.Scheduler;
                        routine.SchedulerInstance = start.Scheduler.GetInstanceID();
                        routine.StartedByName = start.Name;
                        start.Routine = routine;
                    }
                    if (routine.Failed || !routine.Animation.TryEnter(__instance.Pointer, routine.Owner, routine.Scheduler))
                    { routine.Suspended = true; __result = true; return false; }
                    routine.Suspended = false;
                    animationEntered = true;
                    _nativeActionDepth++;
                    return true;
                }
                if (routine.Spec.IsAnimationTransition)
                    PlayerPreservation.FailNativeAnimationInvocation(routine.Owner.TryCast<CoopMecanimReplicator>());
                var fence = _actionFence;
                if (PlayerMigrationAdapter.IsReplicaWriterHeld(routine.Owner)
                    || PlayerMigrationAdapter.IsReplicaWriterHeld(routine.Scheduler))
                {
                    routine.Suspended = true;
                    __result = true;
                    return false;
                }
                if (fence != null && fence._freezeStarted && fence.OwnsActor(routine.Owner))
                {
                    // No MoveNext is consumed, state rewritten, or coroutine stopped. A new
                    // continuation invalidates admission and remains suspended for rollback.
                    fence._fenceViolation = "A retained native continuation arrived after the freeze.";
                    routine.Suspended = true;
                    __result = true;
                    return false;
                }
                routine.Suspended = false;
            }
            catch (Exception)
            {
                UnqualifyActor(routine?.Owner);
                if (routine?.Animation != null)
                { routine.Animation.Fail(); routine.Suspended = true; __result = true; return false; }
            }
            finally { __state = new RoutineMove(routine, animationEntered); }
            return true;
        }

        private static void AfterRoutineMove(RoutineMove __state, bool __result, bool __runOriginal)
        {
            var routine = __state.Routine;
            if (routine == null || routine.Suspended) return;
            if (routine.Animation != null && (!__runOriginal || (!__result && !routine.Animation.Sent)))
            { routine.Failed = true; routine.Animation.Fail(); return; }
            if (__runOriginal && !__result) Routines.Remove(routine.Pointer);
            else if (!__runOriginal) routine.Failed = true;
        }

        private static void FinishRoutineMove(RoutineMove __state, Exception __exception)
        {
            var routine = __state.Routine;
            if (routine == null) return;
            if (__state.AnimationEntered)
            { routine.Animation.Exit(); _nativeActionDepth--; }
            if (__exception != null)
            { routine.Failed = true; routine.Animation?.Fail(); Routines[routine.Pointer] = routine; }
        }

        private readonly struct RoutineMove(ObservedRoutine routine, bool animationEntered)
        {
            internal readonly ObservedRoutine Routine = routine;
            internal readonly bool AnimationEntered = animationEntered;
        }

        private sealed class RoutineSpec
        {
            internal readonly MethodInfo MoveNext, Factory;
            internal readonly IntPtr NativeClass;
            internal bool IsAnimationTransition => Factory.DeclaringType == typeof(CoopMecanimReplicator)
                && Factory.Name == nameof(CoopMecanimReplicator.SendTransitionEventDelayed);
            private readonly Func<IntPtr, Component> _owner;
            internal RoutineSpec(Type type, MethodInfo moveNext, MethodInfo factory)
            {
                MoveNext = moveNext; Factory = factory;
                NativeClass = (IntPtr)typeof(Il2CppClassPointerStore<>).MakeGenericType(type).GetField("NativeClassPtr").GetValue(null);
                if (NativeClass == IntPtr.Zero) throw new InvalidOperationException("Missing native coroutine class.");
                var wrapper = type.GetConstructor(new[] { typeof(IntPtr) }) ?? throw new MissingMethodException(type.FullName, ".ctor(IntPtr)");
                var owner = type.GetProperty("__4__this");
                if (owner != null)
                {
                    var pointer = Expression.Parameter(typeof(IntPtr), "pointer");
                    _owner = Expression.Lambda<Func<IntPtr, Component>>(Expression.TypeAs(Expression.Convert(
                        Expression.Property(Expression.New(wrapper, pointer), owner), typeof(object)), typeof(Component)),
                        pointer).Compile();
                }
            }
            internal Component ReadOwner(Il2CppObjectBase value)
            {
                if (_owner != null) return _owner(value.Pointer);
                // These two native bodies explicitly use LocalPlayer. WaitForInv has an arbitrary
                // callback, so only its observed native StartCoroutine may establish ownership.
                if (Factory.DeclaringType == typeof(PlayerAnimatorControl)
                    && Factory.Name == "SmoothResetCameraYPositionRoutine") return LocalPlayer.AnimControl;
                if (Factory.DeclaringType == typeof(playerHitReactions)
                    && Factory.Name == "doHardfallRoutine") return LocalPlayer.HitReactions;
                return null;
            }
        }

        private sealed class ObservedRoutine
        {
            internal readonly IntPtr Pointer;
            internal readonly Component Owner;
            internal readonly int Instance;
            internal Component Scheduler;
            internal int SchedulerInstance;
            internal readonly RoutineSpec Spec;
            internal PlayerPreservation.AnimationContinuation Animation;
            internal bool Suspended, Failed;
            internal IntPtr Handle;
            internal string StartedByName;
            internal ObservedRoutine(Il2CppObjectBase value, Component owner, RoutineSpec spec, Component scheduler)
            {
                Pointer = value.Pointer; Owner = owner; Instance = owner.GetInstanceID(); Spec = spec;
                Scheduler = scheduler; SchedulerInstance = scheduler == null ? 0 : scheduler.GetInstanceID();
            }
        }

        private sealed class RoutineStart
        {
            internal readonly Component Scheduler, Owner;
            internal readonly string Name;
            internal readonly RoutineStart Previous;
            internal ObservedRoutine Routine;
            internal RoutineStart(Component scheduler, Component owner, string name, RoutineStart previous)
            { Scheduler = scheduler; Owner = owner; Name = name; Previous = previous; }
        }

        private struct NativeActionCall(bool maskedInput)
        {
            internal readonly bool Entered = true;
            internal readonly bool MaskedInput = maskedInput;
        }
    }
}
