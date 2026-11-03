// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Reflection;
using Bolt;
using HarmonyLib;
using global::Sons.Multiplayer.Client;
using TheForest.Utils;
using UnityEngine;
using Priority = HarmonyLib.Priority;

namespace Modstoke.Sons
{
    public sealed partial class PlayerMigrationAdapter
    {
        private static readonly Dictionary<IntPtr, ReplicaHold> ReplicaWriters = new();
        private static readonly Dictionary<ulong, PlayerMigrationAdapter> ExpectedReplicas = new();
        private static int _replicaThread;
        private static bool _replicaHooksInstalled;
        [ThreadStatic] private static int _replicaNativeDepth;
        internal static bool HasActiveReplicaCall => _replicaNativeDepth != 0;
        private ReplicaHold _originalReplica, _replacementReplica;

        // Installed by the explicit lifecycle action-tracking selection. Qualification and
        // rollback belong to that Harmony owner; these are the existing matched publisher RVAs.
        internal static void InstallHooks(HarmonyLib.Harmony harmony,
            Func<Type, string, int, bool, Type, Type[], MethodInfo> qualify)
        {
            if (harmony == null || qualify == null) throw new ArgumentNullException();
            Require(ReplicaWriters.Count == 0 && ExpectedReplicas.Count == 0,
                "Cannot replace replica hooks while a player hold exists.");
            _replicaHooksInstalled = false;
            _replicaThread = Environment.CurrentManagedThreadId;
            var frames = new[]
            {
                Q(typeof(BoltPlayerSetup), nameof(BoltPlayerSetup.Update), 0x32BB9A0),
                Q(typeof(BoltPlayerSetup), nameof(BoltPlayerSetup.UpdateState), 0x32C3CF0),
                Q(typeof(CoopMecanimReplicator), nameof(CoopMecanimReplicator.LateUpdate), 0x30FB190),
                Q(typeof(CoopMecanimReplicator), nameof(CoopMecanimReplicator.UpdateOwner), 0x30FB4E0),
                Q(typeof(CoopMecanimReplicator), nameof(CoopMecanimReplicator.UpdateOwnerEventSync), 0x30FB8F0),
                Q(typeof(CoopMecanimReplicator), nameof(CoopMecanimReplicator.UpdateRemote), 0x30FD6B0, typeof(bool)),
                Q(typeof(CoopMecanimReplicator), nameof(CoopMecanimReplicator.UpdateMecanimPropertiesFromState), 0x30FAD30)
            };
            var transitions = new[]
            {
                Q(typeof(CoopMecanimReplicator), nameof(CoopMecanimReplicator.ApplyTransitionRemote), 0x30FC850,
                    typeof(int), typeof(int), typeof(bool), typeof(float), typeof(bool), typeof(float)),
                Q(typeof(CoopMecanimReplicator), nameof(CoopMecanimReplicator.ApplyHashToRemote), 0x30FC6F0,
                    typeof(int), typeof(int), typeof(bool), typeof(float), typeof(bool)),
                Q(typeof(CoopMecanimReplicator), nameof(CoopMecanimReplicator.DoCrossFade), 0x30FD090,
                    typeof(int), typeof(int), typeof(float), typeof(float), typeof(bool)),
                Q(typeof(CoopMecanimReplicator), nameof(CoopMecanimReplicator.SendStateEventsForCurrentState), 0x30FAAE0, typeof(int))
            };
            var send = Q(typeof(CoopMecanimReplicator), nameof(CoopMecanimReplicator.SendTransitionEvent), 0x30FC110,
                typeof(int), typeof(int), typeof(float), typeof(BoltEntity), typeof(bool));
            var attached = Q(typeof(BoltPlayerSetup), nameof(BoltPlayerSetup.Attached), 0x32BD390);
            var receive = Q(typeof(CoopClientCallbacks), nameof(CoopClientCallbacks.OnEvent), 0x2F79D60,
                typeof(updateMecanimRemoteState));
            harmony.Patch(receive, prefix: new HarmonyMethod(typeof(PlayerMigrationAdapter),
                nameof(BeforeReplicaAnimationEvent)) { priority = Priority.First });
            foreach (var method in frames)
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(PlayerMigrationAdapter),
                    nameof(BeforeReplicaFrame)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(PlayerMigrationAdapter), nameof(FinishReplicaCall)));
            foreach (var method in transitions)
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(PlayerMigrationAdapter),
                    nameof(BeforeReplicaTransition)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(PlayerMigrationAdapter), nameof(FinishReplicaCall)));
            harmony.Patch(send, prefix: new HarmonyMethod(typeof(PlayerMigrationAdapter),
                nameof(BeforeReplicaSendTransition)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(PlayerMigrationAdapter), nameof(FinishReplicaCall)));
            harmony.Patch(attached,
                prefix: new HarmonyMethod(typeof(PlayerMigrationAdapter), nameof(BeforeReplicaInitialization)),
                postfix: new HarmonyMethod(typeof(PlayerMigrationAdapter),
                    nameof(AfterReplicaAttached)) { priority = Priority.Last },
                finalizer: new HarmonyMethod(typeof(PlayerMigrationAdapter), nameof(FinishReplicaCall)));
            _replicaHooksInstalled = true;

            MethodInfo Q(Type type, string name, int rva, params Type[] args)
                => qualify(type, name, rva, false, typeof(void), args);
        }

        // The lifecycle's qualified MoveNext hook uses this to yield without consuming the
        // continuation. It must not stop the coroutine or replay its factory on cancellation.
        internal static bool IsReplicaWriterHeld(Component component)
            => component is not null && ReplicaWriters.TryGetValue(component.Pointer, out var hold)
                && hold.Owns(component);

        private static bool BeforeReplicaFrame(Component __instance, bool __runOriginal, out bool __state)
        {
            __state = false;
            if (!__runOriginal) return false;
            if (VehiclePreservation.IsOccupiedPlayerWriterHeld(__instance)) return false;
            if (!PlayerPreservation.AllowsNativeAnimationInvocation(__instance)
                && FindReplicaHold(__instance, out _)) return false;
            BeforeReplicaInitialization(true, out __state);
            return true;
        }

        private static bool BeforeReplicaTransition(Component __instance, MethodBase __originalMethod, bool __runOriginal, out bool __state)
        {
            __state = false;
            if (!__runOriginal) return false;
            if (VehiclePreservation.TryBlockOccupiedPlayerEffect(__instance, __originalMethod.Name)) return false;
            if (PlayerPreservation.AllowsNativeAnimationInvocation(__instance)
                || !FindReplicaHold(__instance, out var hold))
            {
                BeforeReplicaInitialization(true, out __state);
                return true;
            }
            // Frame gates cannot make a committed transition lossless. Never certify or
            // cancel this transaction after an unexpected effect reached the held writer.
            hold?.Fault("A committed animation transition reached a held replica; native ingress was not closed.");
            return false;
        }

        private static bool BeforeReplicaSendTransition(CoopMecanimReplicator __instance, int __0, int __1,
            float __2, BoltEntity __3, bool __4, MethodBase __originalMethod, bool __runOriginal, out bool __state)
        {
            __state = false;
            if (!__runOriginal) return false;
            if (VehiclePreservation.TryBlockOccupiedPlayerEffect(__instance, __originalMethod.Name)) return false;
            if (PlayerPreservation.TryRouteDelayedAnimationTransition(__instance, __0, __1, __2, __3, __4))
                return false;
            return BeforeReplicaTransition(__instance, __originalMethod, true, out __state);
        }

        private static void BeforeReplicaInitialization(bool __runOriginal, out bool __state)
        {
            __state = __runOriginal;
            if (__state) _replicaNativeDepth++;
        }

        private static void FinishReplicaCall(bool __state)
        {
            if (__state) _replicaNativeDepth--;
        }

        internal static void PruneReplicaTracking()
        {
            if (!_replicaHooksInstalled || Environment.CurrentManagedThreadId != _replicaThread) return;
            List<IntPtr> dead = null;
            foreach (var entry in ReplicaWriters)
                if (entry.Value.NativeTargetsDestroyed()) (dead ??= new()).Add(entry.Key);
            if (dead != null)
                foreach (var pointer in dead) ReplicaWriters.Remove(pointer);
            List<ulong> expired = null;
            foreach (var entry in ExpectedReplicas)
            {
                var owner = entry.Value;
                if ((owner._factory.IsDisposed || !owner._peer.IsCurrent)
                    && !Alive(owner._original) && !Alive(owner._replacement)
                    && (owner._originalReplica == null || owner._originalReplica.NativeTargetsDestroyed())
                    && (owner._replacementReplica == null || owner._replacementReplica.NativeTargetsDestroyed()))
                    (expired ??= new()).Add(entry.Key);
            }
            if (expired != null)
                foreach (var id in expired) ExpectedReplicas.Remove(id);
        }

        internal static void ReleaseHooksAfterStop()
        {
            PruneReplicaTracking();
            Require(!BoltNetwork.isRunning && ReplicaWriters.Count == 0 && ExpectedReplicas.Count == 0
                && !HasActiveReplicaCall, "Replica writer custody has not reached native teardown.");
            _replicaHooksInstalled = false;
        }

        private static bool FindReplicaHold(Component component, out ReplicaHold hold)
        {
            hold = null;
            if (component is null) return false;
            if (ReplicaWriters.TryGetValue(component.Pointer, out hold) && hold.Owns(component)) return true;
            hold = null;
            if (!_replicaHooksInstalled || ExpectedReplicas.Count == 0
                || Environment.CurrentManagedThreadId != _replicaThread) return false;
            PlayerMigrationAdapter owner = null;
            try
            {
                var entity = component.TryCast<BoltPlayerSetup>()?.entity
                    ?? component.TryCast<CoopMecanimReplicator>()?.entity;
                if (!Alive(entity) || !entity.isAttached
                    || !ExpectedReplicas.TryGetValue(entity.networkId.PackedValue, out owner)) return false;
                if (!owner._peer.IsCurrent || owner._factory.IsDisposed) return false;
                owner.AcquireReplacementReplica(entity);
                hold = owner._replacementReplica;
                return hold.Owns(component);
            }
            catch (Exception error)
            {
                if (owner == null) return false;
                owner.RetainReplicaFailure("Authenticated replacement writer hold failed: " + error.Message);
                return true;
            }
        }

        private static void AfterReplicaAttached(BoltPlayerSetup __instance, bool __runOriginal)
        {
            if (__runOriginal && _replicaHooksInstalled && Environment.CurrentManagedThreadId == _replicaThread)
                FindReplicaHold(__instance, out _);
        }

        private void RetainReplicaFailure(string detail)
        { _unconfirmed = true; Detail = detail; }

        private BoltMigrationAdmission ReadOriginalReplicaBoundary(out string detail)
        {
            detail = "Replica action hooks must be selected before this player's native initialization.";
            if (!_replicaHooksInstalled || Environment.CurrentManagedThreadId != _replicaThread)
                return BoltMigrationAdmission.Rejected;
            _originalReplica ??= ReplicaHold.Capture(this, _original, false);
            if (!_originalReplica.Current()) return BoltMigrationAdmission.Rejected;
            return _originalReplica.ReadBoundary(out detail);
        }

        public bool TryQuiesceReplica(BoltEntity original, BoltMigrationRole role, out string detail)
        {
            if (!Enter(out detail)) return false;
            try
            {
                Require(role == Role && Role != BoltMigrationRole.Source && _plan.IsValid
                    && !_quiesceAttempted && Same(original, _original) && OriginalCurrent(),
                    "Replica hold requires the exact prepared non-owner original.");
                if (ReadOriginalReplicaBoundary(out detail) != BoltMigrationAdmission.Ready) return false;
                _quiesceAttempted = true;
                _originalReplica.Acquire();
                RequireContext();
                Require(_originalReplica.Held(), "Replica hold readback failed.");
                _held = true;
                detail = "Exact original replica clock and setup/Mecanim writers held; callbacks and enabled states unchanged.";
                return true;
            }
            catch (Exception error) { detail = Failure("Replica hold rejected or partial", error); return false; }
            finally { Exit(detail); }
        }

        public bool TryCancelReplica(BoltEntity original, out string detail)
        {
            if (!Enter(out detail, allowUnconfirmed: true)) return false;
            try
            {
                Require(Role != BoltMigrationRole.Source && !_rebindAttempted && _replacementId == 0
                    && Same(original, _original) && OriginalCurrent(), "Only the intact original replica can cancel.");
                _originalReplica?.Cancel();
                RequireContext();
                _held = false;
                _cancelled = true;
                detail = "Original replica clock restored and exact writer gates released; no native startup or coroutine replay.";
                return true;
            }
            catch (Exception error) { _unconfirmed = true; detail = Failure("Replica cancellation unconfirmed", error); return false; }
            finally { Exit(detail); }
        }

        private void ExpectReplacementReplica()
        {
            Require(_replicaHooksInstalled && Environment.CurrentManagedThreadId == _replicaThread,
                "Replacement writer hooks are unavailable.");
            if (ExpectedReplicas.TryGetValue(_replacementId, out var stale)
                && !ReferenceEquals(stale, this) && (!stale._peer.IsCurrent || stale._factory.IsDisposed))
                ExpectedReplicas.Remove(_replacementId);
            if (ExpectedReplicas.TryGetValue(_replacementId, out var prior))
                Require(ReferenceEquals(prior, this), "Replacement ID already belongs to another hold.");
            else ExpectedReplicas.Add(_replacementId, this);
            var existing = BoltNetwork.FindEntity(new NetworkId(_replacementId));
            if (Alive(existing)) AcquireReplacementReplica(existing);
        }

        private void AcquireReplacementReplica(BoltEntity replacement)
        {
            // Early authenticated receipts may precede control assignment or old retirement.
            // Those remain admission checks, not grounds for selecting a different object.
            Require(_held && _plan.IsValid && _peer.IsCurrent && !_factory.IsDisposed
                && _replacementId != 0 && Alive(replacement)
                && replacement.isAttached && replacement.networkId.PackedValue == _replacementId
                && replacement.prefabId.Value == _plan.PrefabId && !Same(replacement, _original)
                && replacement.isOwner == (Role == BoltMigrationRole.Destination)
                && Same(BoltNetwork.FindEntity(new NetworkId(_replacementId)), replacement),
                "Only the authenticated exact replacement can acquire writer custody.");
            _replacementReplica ??= ReplicaHold.Capture(this, replacement, true);
            Require(Same(_replacementReplica.Entity, replacement), "Replacement object identity changed.");
            if (!_replacementReplica.Active) _replacementReplica.Acquire();
            Require(_replacementReplica.Held() || _replacementReplica.RuntimeAnimationWritersHeld(),
                "Replacement writer custody changed.");
        }

        private sealed class ReplicaHold
        {
            internal readonly BoltEntity Entity;
            internal readonly BoltPlayerSetup Setup;
            internal readonly CoopMecanimReplicator Replicator;
            internal readonly Animator Animator;
            private readonly PlayerMigrationAdapter _owner;
            private readonly CoopPlayerRemoteSetup _remote;
            private readonly Bolt.Entity _attachment;
            private readonly NetworkState _state;
            private readonly GameObject _root;
            private readonly RuntimeAnimatorController _controller;
            private readonly int _entityInstance, _setupInstance, _replicatorInstance, _animatorInstance;
            private readonly ulong _id;
            private readonly bool _replacement, _setupEnabled, _replicatorEnabled, _animatorEnabled;
            private bool _runtimeWritersAttempted, _runtimeWritersPrepared, _runtimeClockAttempted, _runtimeClockResumed;
            private bool _runtimeQuarantined;
            private float _speed;
            private string _fault;
            internal bool Active { get; private set; }

            private ReplicaHold(PlayerMigrationAdapter owner, BoltEntity entity, bool replacement,
                BoltPlayerSetup setup, CoopMecanimReplicator replicator, CoopPlayerRemoteSetup remote, Animator animator)
            {
                _owner = owner; Entity = entity; _replacement = replacement;
                Setup = setup; Replicator = replicator; _remote = remote; Animator = animator;
                _attachment = entity._entity; _state = _attachment.Serializer.TryCast<NetworkState>();
                _root = entity.gameObject; _controller = animator.runtimeAnimatorController;
                _entityInstance = entity.GetInstanceID(); _setupInstance = setup.GetInstanceID();
                _replicatorInstance = replicator.GetInstanceID(); _animatorInstance = animator.GetInstanceID();
                _id = entity.networkId.PackedValue; _setupEnabled = setup.enabled;
                _replicatorEnabled = replicator.enabled; _animatorEnabled = animator.enabled;
            }

            internal static ReplicaHold Capture(PlayerMigrationAdapter owner, BoltEntity entity, bool replacement)
            {
                Require(Alive(entity) && entity.isAttached && entity.prefabId == BoltPrefabs.PlayerNetwork
                    && !Same(entity.gameObject, LocalPlayer.GameObject), "Replica must not own the retained local rig.");
                var setups = entity.GetComponentsInChildren<BoltPlayerSetup>(true);
                var replicators = entity.GetComponentsInChildren<CoopMecanimReplicator>(true);
                var remotes = entity.GetComponentsInChildren<CoopPlayerRemoteSetup>(true);
                Require(setups.Length == 1 && replicators.Length == 1 && remotes.Length == 1
                    && entity.GetComponentsInChildren<BoltEntity>(true).Length == 1
                    && entity.GetComponentsInChildren<FirstPersonCharacter>(true).Length == 0
                    && entity.GetComponentsInChildren<PlayerAnimatorControl>(true).Length == 0,
                    "Replica contains extra or local player writers requiring their own lifecycle.");
                var animator = replicators[0].GetTargetAnimator();
                Require(Alive(animator) && Same(animator, remotes[0]._playerAnimator)
                    && Alive(animator.runtimeAnimatorController), "Replica Animator binding is unavailable.");
                var result = new ReplicaHold(owner, entity, replacement, setups[0], replicators[0], remotes[0], animator);
                Require(result.Current(), "Replica native identities changed during capture.");
                return result;
            }

            internal bool Current() => Alive(Entity) && Entity.GetInstanceID() == _entityInstance
                && Entity.isAttached && Entity.networkId.PackedValue == _id && Same(Entity._entity, _attachment)
                && Live(_attachment) && _attachment.IsAttached && Same(_attachment.Serializer, _state)
                && _state.Meta?.TypeId.Value == _owner._request.StateTypeId
                && Same(Entity.gameObject, _root) && Same(BoltNetwork.FindEntity(new NetworkId(_id)), Entity)
                && Alive(Setup) && Setup.GetInstanceID() == _setupInstance
                && Alive(Replicator) && Replicator.GetInstanceID() == _replicatorInstance
                && Alive(Animator) && Animator.GetInstanceID() == _animatorInstance
                && Same(Replicator.GetTargetAnimator(), Animator) && Same(_remote._playerAnimator, Animator)
                && Same(Animator.runtimeAnimatorController, _controller)
                && Setup.enabled == _setupEnabled && Replicator.enabled == (_runtimeWritersAttempted ? false : _replicatorEnabled)
                && Animator.enabled == _animatorEnabled;

            internal BoltMigrationAdmission ReadBoundary(out string detail)
            {
                detail = _fault ?? "Replica identity, animator or native setup bindings changed.";
                if (_fault != null || !Current() || !Same(Setup.entity, Entity) || !Same(Replicator.entity, Entity)
                    || !Same(_remote.entity, Entity)) return BoltMigrationAdmission.Rejected;
                var initialized = PlayerCarrier.ReadNativeInitialization(Entity, out detail);
                if (initialized != BoltMigrationAdmission.Ready) return initialized;
                var actions = PlayerLifecycle.ReadReplicaActionBoundary(Setup, Replicator, out detail);
                if (actions != BoltMigrationAdmission.Ready) return actions;
                if (Animator.hasBoundPlayables || Animator.recorderMode != AnimatorRecorderMode.Offline
                    || Animator.isMatchingTarget || Animator.layerCount < 1 || Animator.layerCount > 32
                    || !float.IsFinite(Animator.speed) || Animator.speed < 0)
                { detail = "Replica Animator has an unsupported playback owner."; return BoltMigrationAdmission.Rejected; }
                for (int i = 0; i < Animator.layerCount; i++)
                    if (Animator.IsInTransition(i))
                    { detail = "Replica Animator transition has not settled."; return BoltMigrationAdmission.Pending; }
                detail = "Exact replica setup, native continuation boundary and presentation writers admitted; not gameplay coverage.";
                return BoltMigrationAdmission.Ready;
            }

            internal void Acquire()
            {
                Require(!Active && Current() && _fault == null, "Replica hold cannot be replayed.");
                Require(!ReplicaWriters.ContainsKey(Setup.Pointer) && !ReplicaWriters.ContainsKey(Replicator.Pointer),
                    "An exact replica writer already belongs to another hold.");
                _speed = Animator.speed;
                Require(float.IsFinite(_speed) && _speed >= 0, "Replica playback speed is invalid.");
                ReplicaWriters.Add(Setup.Pointer, this);
                ReplicaWriters.Add(Replicator.Pointer, this);
                Active = true;
                Animator.speed = 0;
                Require(Held(), "Replica clock hold was not confirmed.");
            }

            internal bool Owns(Component component)
            {
                if (!Active || component is null) return false;
                if (component.Pointer != Setup.Pointer && component.Pointer != Replicator.Pointer) return false;
                try
                {
                    return component.GetInstanceID() == (component.Pointer == Setup.Pointer ? _setupInstance : _replicatorInstance);
                }
                catch (Exception)
                { Fault("An exact replica writer identity became unreadable; its gate remains held."); return true; }
            }

            internal bool Held() => Active && _fault == null && Current() && Animator.speed == 0;

            internal void PrepareRuntimeAnimationWriters()
            {
                Require(_replacement && !_runtimeWritersAttempted && Held() && RuntimeOwnerCurrent()
                    && !HasActiveReplicaCall && !PlayerPreservation.HasActiveNativeAnimationCall,
                    "Only the exact held replacement can prepare its animation writer.");
                Require(PlayerLifecycle.ReadReplicaActionBoundary(Setup, Replicator, out var detail)
                    == BoltMigrationAdmission.Ready, detail);
                _runtimeWritersAttempted = true;
                try
                {
                    Replicator.enabled = false;
                    Require(Held() && RuntimeOwnerCurrent(), "Replacement animation writer isolation was not confirmed.");
                    _runtimeWritersPrepared = true;
                }
                catch (Exception error)
                { Fault("Replacement animation writer preparation is unconfirmed: " + error.Message); throw; }
            }

            internal bool RuntimeAnimationWritersHeld() => _replacement && _runtimeWritersPrepared && !_runtimeQuarantined
                && Active && _fault == null && RuntimeOwnerCurrent() && Current() && !Replicator.enabled
                && ReplicaWriters.TryGetValue(Setup.Pointer, out var setupHold) && ReferenceEquals(setupHold, this)
                && ReplicaWriters.TryGetValue(Replicator.Pointer, out var replicatorHold) && ReferenceEquals(replicatorHold, this)
                && (_runtimeClockResumed || Animator.speed == 0);

            internal void ResumeRuntimeAnimationClock(float approvedSpeed)
            {
                Require(!_runtimeClockAttempted && RuntimeAnimationWritersHeld() && Held() && !HasActiveReplicaCall
                    && !PlayerPreservation.HasActiveNativeAnimationCall
                    && float.IsFinite(approvedSpeed) && approvedSpeed >= 0
                    && PlayerPreservation.RuntimeAnimationClockReady(Replicator),
                    "Animation clock resume requires the exact prepared, aggregate-active replacement.");
                _runtimeClockAttempted = true;
                try
                {
                    Animator.speed = approvedSpeed;
                    _runtimeClockResumed = true;
                    Require(RuntimeAnimationWritersHeld() && Animator.speed == approvedSpeed,
                        "Replacement animation clock restoration was not confirmed.");
                }
                catch (Exception error)
                { Fault("Replacement animation clock resume is unconfirmed: " + error.Message); throw; }
            }

            internal void QuarantineRuntimeAnimation()
            {
                try
                {
                    Require(_replacement && _runtimeWritersPrepared && Active
                        && Environment.CurrentManagedThreadId == _replicaThread && !HasActiveReplicaCall
                        && !PlayerPreservation.HasActiveNativeAnimationCall,
                        "Animation quarantine requires the prepared replacement outside native callbacks.");
                    _runtimeQuarantined = true;
                    Require(Alive(Animator) && Animator.GetInstanceID() == _animatorInstance && Alive(_root)
                        && Animator.transform.IsChildOf(_root.transform)
                        && ReplicaWriters.TryGetValue(Setup.Pointer, out var setupHold) && ReferenceEquals(setupHold, this)
                        && ReplicaWriters.TryGetValue(Replicator.Pointer, out var replicatorHold) && ReferenceEquals(replicatorHold, this),
                        "Animation quarantine lost the exact replacement animator or writer gates.");
                    if (Animator.speed != 0) Animator.speed = 0;
                    Require(Animator.speed == 0 && Alive(Replicator) && Replicator.GetInstanceID() == _replicatorInstance
                        && !Replicator.enabled, "Replacement animation quarantine was not confirmed.");
                }
                catch (Exception error)
                { Fault("Replacement animation quarantine is unconfirmed: " + error.Message); throw; }
            }

            private bool RuntimeOwnerCurrent() => _replicaHooksInstalled
                && Environment.CurrentManagedThreadId == _replicaThread && !_owner._cancelled && !_owner._unconfirmed
                && !_owner._factory.IsDisposed && _owner._peer.IsCurrent
                && _owner._replacementId == _id && ReferenceEquals(_owner._replacementReplica, this)
                && Same(Setup.entity, Entity) && Same(Replicator.entity, Entity) && Alive(_remote)
                && Same(_remote.entity, Entity);

            internal bool NativeTargetsDestroyed() => !Alive(Entity) && !Alive(_root)
                && !Alive(Setup) && !Alive(Replicator) && !Alive(Animator) && !Alive(_remote);

            internal void Fault(string detail)
            { _fault ??= detail; _owner.RetainReplicaFailure(detail); }

            internal void Cancel()
            {
                Require(!_replacement && _fault == null && Current() && _owner.OriginalCurrent(),
                    "Only this unchanged original replica can release its writer hold.");
                if (!Active) return;
                Require(Animator.speed == 0 || Animator.speed == _speed, "A foreign writer changed replica playback speed.");
                Animator.speed = _speed;
                Require(Current() && Animator.speed == _speed, "Original replica clock restoration failed.");
                ReleaseGates();
            }

            internal void ReleaseGates()
            {
                ReplicaWriters.Remove(Setup.Pointer);
                ReplicaWriters.Remove(Replicator.Pointer);
                Active = false;
            }
        }
    }
}
