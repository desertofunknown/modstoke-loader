// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Bolt;
using Il2CppInterop.Runtime;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class PlayerPreservation
    {
        internal enum AnimationRuntimeRole { ServerPublisher, SourceReplica, ObserverReplica }

        // Values come from the server's committed gameplay domains, never from a client pose packet.
        internal readonly struct AnimationParameterWrite
        {
            internal readonly string Name;
            private readonly ScalarKind _kind;
            private readonly int _value;

            private AnimationParameterWrite(string name, ScalarKind kind, int value)
            { Name = name; _kind = kind; _value = value; }

            internal static AnimationParameterWrite Float(string name, float value)
                => new(name, ScalarKind.Float, BitConverter.SingleToInt32Bits(value));
            internal static AnimationParameterWrite Integer(string name, int value)
                => new(name, ScalarKind.Integer, value);
            internal static AnimationParameterWrite Boolean(string name, bool value)
                => new(name, ScalarKind.Boolean, value ? 1 : 0);

            internal bool Matches(AnimatorControllerParameterType kind) => kind switch
            {
                AnimatorControllerParameterType.Float => _kind == ScalarKind.Float,
                AnimatorControllerParameterType.Int => _kind == ScalarKind.Integer,
                AnimatorControllerParameterType.Bool => _kind == ScalarKind.Boolean,
                _ => false
            };
            internal int Value => _value;
        }

        private static readonly Dictionary<IntPtr, AnimationRuntimeBinding> RuntimeAnimationBindings = new();
        [ThreadStatic] private static AnimationRuntimeBinding _nativeAnimationInvocation;
        [ThreadStatic] private static AnimationContinuation _delayedAnimationInvocation;
        internal static bool HasActiveNativeAnimationCall => _nativeAnimationInvocation != null || _delayedAnimationInvocation != null;

        // Lifecycle/adapter prefixes must still honor earlier Harmony vetoes. This permits only
        // the synchronous call tree of an exact active replacement's managed publication/application.
        // Takes any Component so per-frame replica hooks compare pointers before casting.
        internal static bool AllowsNativeAnimationInvocation(Component replicator)
        {
            var binding = _nativeAnimationInvocation;
            if (binding == null || replicator == null || replicator.Pointer != binding._replicator.Pointer) return false;
            try
            {
                binding.Validate(true, false);
                return replicator.GetInstanceID() == binding._replicatorInstance;
            }
            catch (Exception) { binding._failed = true; return false; }
        }

        internal static bool RuntimeAnimationClockReady(CoopMecanimReplicator replicator)
        {
            if (replicator == null || !Enter()) return false;
            try
            {
                var animator = replicator.GetTargetAnimator();
                if (animator == null || !RuntimeAnimationBindings.TryGetValue(animator.Pointer, out var binding)
                    || binding._active || binding._replicator.Pointer != replicator.Pointer) return false;
                binding.Validate(false);
                Require(binding.GameplayActive, "Aggregate animation gameplay is not active.");
                binding.Validate(false, false);
                return true;
            }
            catch (Exception) { return false; }
            finally { _busy = false; }
        }

        internal static AnimationContinuation CaptureNativeAnimationContinuation(CoopMecanimReplicator replicator,
            Il2CppSystem.Collections.IEnumerator value, int hash, int layer, float time, BoltEntity entity, bool anyState, int delay)
        {
            var binding = _nativeAnimationInvocation;
            if (binding == null || !binding.IsServer || !AllowsNativeAnimationInvocation(replicator)) return null;
            try { return new AnimationContinuation(binding, value, hash, layer, time, entity, anyState, delay); }
            catch (Exception) { binding._failed = true; throw; }
        }

        internal static void FailNativeAnimationInvocation(CoopMecanimReplicator replicator)
        {
            var binding = _nativeAnimationInvocation;
            if (binding != null && replicator != null && replicator.Pointer == binding._replicator.Pointer)
                binding._failed = true;
        }

        // Both retained-publisher and replica gates call this before their ordinary hold check.
        // The nested binding dispatch carries the synchronous permit, so it cannot route again.
        internal static bool TryRouteDelayedAnimationTransition(CoopMecanimReplicator replicator,
            int hash, int layer, float time, BoltEntity entity, bool anyState)
        {
            if (_nativeAnimationInvocation != null) return false;
            var continuation = _delayedAnimationInvocation;
            if (continuation == null) return false;
            continuation.RequireSend(replicator, hash, layer, time, entity, anyState);
            Require(!continuation.Routing, "Animation continuation dispatch reentered.");
            continuation.Routing = true;
            try
            {
                var result = continuation.Binding.TryPublishTransition(continuation.Binding._id,
                    layer, hash, anyState, time, out var detail);
                if (result != BoltMigrationMutationResult.Applied)
                { continuation.Fail(); throw new InvalidOperationException(detail); }
                return true;
            }
            finally { continuation.Routing = false; }
        }

        internal static void ObserveNativeAnimationTransition(CoopMecanimReplicator replicator,
            int hash, int layer, float time, BoltEntity entity, bool anyState, bool ran)
        {
            var binding = _nativeAnimationInvocation;
            if (binding == null || replicator == null || binding._replicator.Pointer != replicator.Pointer) return;
            if (!ran) { binding._failed = true; return; }
            binding._transitionDispatchObserved = true;
            var continuation = _delayedAnimationInvocation;
            if (continuation == null) return;
            continuation.RequireSend(replicator, hash, layer, time, entity, anyState);
            continuation.Sent = true;
        }

        internal sealed class AnimationContinuation
        {
            internal readonly AnimationRuntimeBinding Binding;
            private readonly CoopMecanimReplicator._SendTransitionEventDelayed_d__38 _iterator;
            private readonly int _hash, _layer, _time, _delay;
            private readonly bool _anyState;
            private bool _ownsPreservationEntry;
            internal bool Entered { get; private set; }
            internal bool Sent { get; set; }
            internal bool Failed { get; private set; }
            internal bool Routing;

            internal AnimationContinuation(AnimationRuntimeBinding binding, Il2CppSystem.Collections.IEnumerator value,
                int hash, int layer, float time, BoltEntity entity, bool anyState, int delay)
            {
                Binding = binding;
                Require(value != null && !value.WasCollected
                    && IL2CPP.il2cpp_object_get_class(value.Pointer)
                        == Il2CppClassPointerStore<CoopMecanimReplicator._SendTransitionEventDelayed_d__38>.NativeClassPtr,
                    "The native transition factory returned an unknown iterator.");
                _iterator = value.Cast<CoopMecanimReplicator._SendTransitionEventDelayed_d__38>();
                _hash = hash; _layer = layer; _time = BitConverter.SingleToInt32Bits(time); _anyState = anyState; _delay = delay;
                Require(entity != null && entity.Pointer == binding._entity.Pointer && hash != 0
                    && layer >= 0 && layer < binding.LayerCount && float.IsFinite(time)
                    && _iterator.__1__state == 0, "Native transition creation identity or payload changed.");
                RequirePayload(value.Pointer);
            }

            private void RequirePayload(IntPtr pointer)
            {
                Require(!_iterator.WasCollected && _iterator.Pointer == pointer
                    && _iterator.__4__this?.Pointer == Binding._replicator.Pointer
                    && _iterator.boltEntity?.Pointer == Binding._entity.Pointer && _iterator.stateHash == _hash
                    && _iterator.layerArrayIdx == _layer && BitConverter.SingleToInt32Bits(_iterator.normalizedTime) == _time
                    && _iterator.anyState == _anyState && _iterator.delay == _delay,
                    "The retained native transition identity or payload changed.");
            }

            internal bool TryEnter(IntPtr pointer, Component owner, Component scheduler)
            {
                bool acquired = false;
                try
                {
                    Require(!Failed && !Sent && !Entered && _delayedAnimationInvocation == null,
                        "Native animation continuation reentered or was already dispatched.");
                    Require(owner != null && scheduler != null && owner.Pointer == Binding._replicator.Pointer
                        && scheduler.Pointer == owner.Pointer && owner.GetInstanceID() == Binding._replicatorInstance
                        && scheduler.GetInstanceID() == Binding._replicatorInstance,
                        "Native animation continuation lacks its exact observed scheduler.");
                    if (_nativeAnimationInvocation == null)
                    { acquired = Enter(); Require(acquired, "Animation continuation entered another preservation operation."); }
                    else Require(ReferenceEquals(_nativeAnimationInvocation, Binding), "Another animation binding is invoking native code.");
                    Binding.Validate(true, false);
                    RequirePayload(pointer);
                    _ownsPreservationEntry = acquired;
                    Entered = true;
                    _delayedAnimationInvocation = this;
                    Binding._continuationDepth++;
                    return true;
                }
                catch (Exception) { Fail(); return false; }
                finally { if (acquired && !Entered) _busy = false; }
            }

            internal void RequireSend(CoopMecanimReplicator replicator, int hash, int layer, float time,
                BoltEntity entity, bool anyState)
            {
                try
                {
                    Require(Entered && !Failed && !Sent && ReferenceEquals(_delayedAnimationInvocation, this),
                        "Native transition has no admitted continuation call.");
                    Binding.Validate(true, false);
                    RequirePayload(_iterator.Pointer);
                    Require(replicator != null && replicator.Pointer == Binding._replicator.Pointer
                        && entity != null && entity.Pointer == Binding._entity.Pointer && hash == _hash && layer == _layer
                        && BitConverter.SingleToInt32Bits(time) == _time && anyState == _anyState,
                        "Native transition send differs from its admitted creation.");
                }
                catch (Exception) { Fail(); throw; }
            }

            internal void Fail() { Failed = true; Binding._failed = true; }

            internal void Exit()
            {
                if (!Entered) return;
                if (_reentered) Fail();
                Entered = false;
                Binding._continuationDepth--;
                if (ReferenceEquals(_delayedAnimationInvocation, this)) _delayedAnimationInvocation = null;
                if (_ownsPreservationEntry) { _ownsPreservationEntry = false; _busy = false; }
            }
        }

        internal static bool TryPrepareServerAnimation(PlayerBridge bridge, Func<bool> writersHeld,
            out AnimationRuntimeBinding binding, out string detail)
        {
            binding = null;
            detail = "An exact prepared server bridge is required for animation.";
            if (bridge?.IsServer != true || !bridge.TryReadCurrentTarget(out var entity)) return false;
            return TryPrepareRuntimeAnimation(bridge.Peer, entity, AnimationRuntimeRole.ServerPublisher,
                () => bridge.TryReadCurrentTarget(out var current) && current.Pointer == entity.Pointer,
                () => bridge.IsGameplayActive, writersHeld, out binding, out detail, bridge);
        }

        /// <summary>
        /// Preparation is read-only. current must cover the repaired replacement/session lifetime;
        /// gameplayActive must cover aggregate activation. writersHeld must cover competing setup,
        /// remote setup, action continuations and late transition ingress, not just component.enabled.
        /// Keep the replacement Mecanim component disabled and all competing writers held even
        /// after activation. Never pass the original local Animator as a replica target.
        /// </summary>
        internal static bool TryPrepareRuntimeAnimation(ControlPeer peer, BoltEntity replacement,
            AnimationRuntimeRole role, Func<bool> current, Func<bool> gameplayActive, Func<bool> writersHeld,
            out AnimationRuntimeBinding binding, out string detail, PlayerBridge serverBridge = null)
        {
            binding = null;
            detail = "Runtime animation preparation is unavailable or reentrant.";
            if (!Enter()) return false;
            try
            {
                RequireContext(peer);
                var candidate = new AnimationRuntimeBinding(replacement, role, current, gameplayActive, writersHeld, serverBridge);
                Require(!RuntimeAnimationBindings.ContainsKey(candidate.Animator.Pointer),
                    "An animation binding already owns this replacement animator.");
                RuntimeAnimationBindings.Add(candidate.Animator.Pointer, candidate);
                binding = candidate;
                detail = "Animation prepared without writes; gameplay activation and writer holds remain required.";
                return true;
            }
            catch (Exception error) { detail = Describe(error); return false; }
            finally { _busy = false; }
        }

        internal sealed partial class AnimationRuntimeBinding : IDisposable
        {
            private sealed record Parameter(Scalar Field, int Hash, AnimatorControllerParameterType Kind,
                int PropertyIndex, NetworkProperty_Mecanim Property, int StorageOffset);
            private sealed record Layer(MecanimLayerConfig Configuration, int Index, string Name, bool SyncWeight);

            internal readonly int _thread = Environment.CurrentManagedThreadId;
            internal readonly CoopMecanimReplicator _replicator;
            internal bool _active, _failed, _disposed;
            internal bool _transitionDispatchObserved;
            internal int _continuationDepth;
            internal readonly int _replicatorInstance;
            private readonly int _entityInstance, _animatorInstance;
            internal readonly BoltEntity _entity;
            private readonly Entity _attachment;
            private readonly global::PlayerState _state;
            internal readonly ulong _id;
            private readonly AnimationRuntimeRole _role;
            private readonly Func<bool> _current, _gameplayActive, _writersHeld;
            private readonly RuntimeAnimatorController _controller;
            private readonly Animator _retainedSource;
            private readonly PlayerAnimatorControl[] _controls;
            private readonly Parameter[] _parameters;
            private readonly Layer[] _layers;
            private readonly Dictionary<string, int> _parameterLookup = new(StringComparer.Ordinal);
            private readonly int[] _values;
            private readonly IntPtr _nativeLayers, _stateAnimators;
            internal Animator Animator { get; }
            internal bool IsActive => _active && !_failed && !_disposed;

            internal AnimationRuntimeBinding(BoltEntity entity, AnimationRuntimeRole role,
                Func<bool> current, Func<bool> gameplayActive, Func<bool> writersHeld, PlayerBridge serverBridge)
            {
                Require(role == AnimationRuntimeRole.ServerPublisher || role == AnimationRuntimeRole.SourceReplica
                    || role == AnimationRuntimeRole.ObserverReplica, "Unknown animation role.");
                Require(current != null && gameplayActive != null && writersHeld != null,
                    "Animation requires exact live lifetime, activation and writer-hold guards.");
                _role = role; _current = current; _gameplayActive = gameplayActive; _writersHeld = writersHeld;
                _climbBridge = serverBridge;
                _entity = entity;
                Require(entity != null && !entity.WasCollected && entity && entity.isAttached,
                    "An attached animation replacement is required.");
                _entityInstance = entity.GetInstanceID();
                _attachment = entity._entity;
                _id = entity.networkId.PackedValue;
                _state = _attachment?.Serializer?.TryCast<global::PlayerState>();
                Require(_state != null && !_state.WasCollected
                    && IL2CPP.il2cpp_object_get_class(_state.Pointer) == Il2CppClassPointerStore<global::PlayerState>.NativeClassPtr,
                    "The exact replacement PlayerState is required.");
                Animator = _state.Animator;
                Require(Animator != null && !Animator.WasCollected && Animator && Animator.isInitialized,
                    "The replacement needs an initialized state-bound animator.");
                _animatorInstance = Animator.GetInstanceID();
                _controller = Animator.runtimeAnimatorController;
                _retainedSource = role == AnimationRuntimeRole.SourceReplica ? LocalPlayer.Animator : null;
                _stateAnimators = _state.Animators?.Pointer ?? IntPtr.Zero;
                var replicators = entity.GetComponentsInChildren<CoopMecanimReplicator>(true);
                Require(replicators.Length == 1, "Exactly one replacement Mecanim replicator is required.");
                _replicator = replicators[0];
                _replicatorInstance = _replicator.GetInstanceID();
                var controls = entity.GetComponentsInChildren<PlayerAnimatorControl>(true);
                _controls = new PlayerAnimatorControl[controls.Length];
                for (int i = 0; i < controls.Length; i++) _controls[i] = controls[i];
                var layers = _replicator.GetLayersToSync();
                Require(layers != null && !layers.WasCollected && layers.Length > 0 && layers.Length <= 32,
                    "The native Mecanim layer mapping is unavailable.");
                _nativeLayers = layers.Pointer;
                _layers = new Layer[layers.Length];
                var layerIndices = new HashSet<int>();
                for (int i = 0; i < layers.Length; i++)
                {
                    var layer = layers[i];
                    Require(layer != null && !layer.WasCollected && layer.LayerIndex >= 0
                        && layer.LayerIndex < Animator.layerCount && layer.ArrayIndex == i
                        && layerIndices.Add(layer.LayerIndex) && Animator.GetLayerName(layer.LayerIndex) == layer.Name,
                        "Native Mecanim layer indices are uninitialized or ambiguous.");
                    _layers[i] = new(layer, layer.LayerIndex, layer.Name, layer.SyncWeight);
                }
                BoltProperties.RequireLayout();
                RuntimeHelpers.RunClassConstructor(typeof(NetworkProperty_Float).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(NetworkProperty_Integer).TypeHandle);
                RuntimeHelpers.RunClassConstructor(typeof(NetworkProperty_Bool).TypeHandle);
                var parameters = Animator.parameters;
                Require(parameters != null && !parameters.WasCollected && parameters.Length <= MaximumElements,
                    "Animation parameter schema is unavailable.");
                var selected = new List<Parameter>();
                foreach (var field in Scalars)
                {
                    if (field.Write != null && field.Name != "hSpeed" && field.Name != "vSpeed") continue;
                    var kind = field.Kind == ScalarKind.Float ? AnimatorControllerParameterType.Float
                        : field.Kind == ScalarKind.Integer ? AnimatorControllerParameterType.Int : AnimatorControllerParameterType.Bool;
                    int hash = UnityEngine.Animator.StringToHash(field.Name), matches = 0;
                    foreach (var parameter in parameters)
                    {
                        Require(parameter != null && !parameter.WasCollected, "Animation parameter schema is stale.");
                        if (parameter.nameHash != hash && parameter.name != field.Name) continue;
                        Require(parameter.name == field.Name && parameter.nameHash == hash && parameter.type == kind,
                            "Animation parameter type or hash differs: " + field.Name);
                        matches++;
                    }
                    Require(matches == 1, "One exact animation parameter is required: " + field.Name);
                    int propertyIndex = -1;
                    NetworkProperty_Mecanim property = null;
                    if (field.Write == null)
                    {
                        _state.GetDynamicInternal(field.Name, out propertyIndex);
                        property = ReadAnimatorProperty(_state, propertyIndex, field, IsServer);
                    }
                    _parameterLookup.Add(field.Name, selected.Count);
                    selected.Add(new(field, hash, kind, propertyIndex, property, property?.OffsetStorage ?? -1));
                }
                Require(selected.Count == 30, "The player animation scalar schema changed.");
                _parameters = selected.ToArray();
                _values = new int[_parameters.Length];
                Validate(false);
            }

            internal bool IsServer => _role == AnimationRuntimeRole.ServerPublisher;
            internal bool CanDispose => _thread == Environment.CurrentManagedThreadId && !_busy && _continuationDepth == 0
                && (_climbLease == null || !_climbLease.Motor.IsExecuting);
            internal bool GameplayActive => _gameplayActive();
            internal int LayerCount => _layers.Length;

            internal bool TryValidate(out string detail)
            {
                detail = "Animation validation is unavailable or reentrant.";
                if (_thread != Environment.CurrentManagedThreadId || !Enter()) return false;
                try
                {
                    Validate(_active);
                    detail = _active ? "The active animation endpoint remains current." : "The prepared animation endpoint remains current.";
                    return true;
                }
                catch (Exception error) { detail = Describe(error); return false; }
                finally { _busy = false; }
            }

            internal bool TryActivate(out string detail)
            {
                detail = "Animation activation is unavailable or reentrant.";
                if (_thread != Environment.CurrentManagedThreadId || !Enter()) return false;
                try
                {
                    Validate(false);
                    Require(_gameplayActive(), "The prepared animation endpoint cannot precede gameplay activation.");
                    Validate(false);
                    Require(Animator.enabled && Animator.gameObject.activeInHierarchy,
                        "The replacement animation clock is not enabled.");
                    _active = true;
                    detail = "Animation endpoint active; competing writers remain held.";
                    return true;
                }
                catch (Exception error) { detail = Describe(error); return false; }
                finally { _busy = false; }
            }

            /// <summary>Apply committed server parameter decisions before ordinary Animator evaluation.</summary>
            internal BoltMigrationMutationResult TryWriteAuthoritativeParameters(
                ReadOnlySpan<AnimationParameterWrite> writes, out string detail)
            {
                detail = "Authoritative animation write is unavailable or reentrant.";
                if (_thread != Environment.CurrentManagedThreadId || !Enter()) return BoltMigrationMutationResult.Rejected;
                bool attempted = false;
                try
                {
                    Validate(true);
                    Require(IsServer, "Replicas cannot submit animation parameters.");
                    uint seen = 0;
                    foreach (var write in writes)
                    {
                        if (write.Name == null || !_parameterLookup.TryGetValue(write.Name, out int index))
                            throw new InvalidOperationException("Unknown authoritative animation parameter.");
                        Require(_climbLease == null || (write.Name != "climbDirInt" && write.Name != "climbSpeed"
                            && write.Name != "climbTypeInt"), "The exact climb motor owns these animation parameters.");
                        var parameter = _parameters[index];
                        Require((seen & (1u << index)) == 0 && write.Matches(parameter.Kind),
                            "Duplicate or mistyped authoritative animation parameter.");
                        seen |= 1u << index;
                        ValidateAnimationValue(parameter.Kind, write.Value);
                        if (Animator.IsParameterControlledByCurve(parameter.Hash))
                            throw new InvalidOperationException("A curve owns the requested animation parameter: " + write.Name);
                    }
                    foreach (var write in writes)
                    {
                        RequireInvocationCurrent();
                        var parameter = _parameters[_parameterLookup[write.Name]];
                        attempted = true;
                        WriteParameter(parameter, write.Value);
                        Require(ReadAnimationParameter(Animator, parameter.Hash, parameter.Kind) == write.Value,
                            "Authoritative animation parameter readback differs.");
                    }
                    Validate(true, false);
                    detail = "Committed server parameters applied; publish after ordinary animation evaluation.";
                    return BoltMigrationMutationResult.Applied;
                }
                catch (Exception error) { return Failed(error, attempted, out detail); }
                finally { _busy = false; }
            }

            /// <summary>
            /// Invoke once after ordinary animation evaluation. Uses native owner pull and the
            /// native layer/event publisher; never evaluates the Animator or reads LocalPlayer input.
            /// </summary>
            internal BoltMigrationMutationResult TryPublishEvaluatedState(out string detail)
                => Transfer(true, out detail);

            /// <summary>Apply only to the replacement replica, never the retained first-person rig.</summary>
            internal BoltMigrationMutationResult TryApplyReplicaState(out string detail)
                => Transfer(false, out detail);

            /// <summary>
            /// Only the lifecycle-owned, exact replacement's admitted native continuation may call
            /// this. The binding does not manufacture a transition or authorize a source request.
            /// </summary>
            internal BoltMigrationMutationResult TryPublishTransition(ulong networkId,
                int layerArrayIndex, int stateHash, bool anyState, float normalizedTime, out string detail)
                => DispatchTransition(true, networkId, layerArrayIndex, stateHash, anyState, normalizedTime, false, out detail);

            /// <summary>
            /// The event router must authenticate the server and admit the exact current target
            /// before dispatch. Mid-transfer retention and delivery ordering belong to that router.
            /// </summary>
            internal BoltMigrationMutationResult TryApplyReplicaTransition(ulong networkId,
                int layerArrayIndex, int stateHash, bool anyState, float normalizedTime, bool instantTransition,
                out string detail)
                => DispatchTransition(false, networkId, layerArrayIndex, stateHash, anyState, normalizedTime, instantTransition, out detail);

            private BoltMigrationMutationResult DispatchTransition(bool publish, ulong networkId,
                int layerArrayIndex, int stateHash, bool anyState, float normalizedTime, bool instantTransition,
                out string detail)
            {
                detail = "Animation transition dispatch is unavailable or reentrant.";
                // A delayed MoveNext owns preservation until its finalizer. Only its exact
                // send route may reuse that entry; nested native sends use the synchronous permit.
                bool continuationDispatch = publish && _nativeAnimationInvocation == null
                    && _delayedAnimationInvocation?.Routing == true
                    && ReferenceEquals(_delayedAnimationInvocation.Binding, this);
                if (_thread != Environment.CurrentManagedThreadId
                    || (continuationDispatch ? !_busy || _reentered : !Enter()))
                    return BoltMigrationMutationResult.Rejected;
                bool attempted = false;
                try
                {
                    Validate(true);
                    Require(IsServer == publish && networkId == _id && stateHash != 0
                        && layerArrayIndex >= 0 && layerArrayIndex < _layers.Length && float.IsFinite(normalizedTime),
                        "Animation transition identity, direction, layer or time is invalid.");
                    attempted = true;
                    _transitionDispatchObserved = false;
                    _nativeAnimationInvocation = this;
                    try
                    {
                        if (publish) _replicator.SendTransitionEvent(stateHash, layerArrayIndex, normalizedTime, _entity, anyState);
                        else _replicator.ApplyHashToRemote(layerArrayIndex, stateHash, anyState, normalizedTime, instantTransition);
                    }
                    finally { _nativeAnimationInvocation = null; }
                    Validate(true, false);
                    if (publish) Require(_transitionDispatchObserved, "The native transition send was vetoed or not observed.");
                    if (!publish)
                        Require(_layers[layerArrayIndex].Configuration.Hash_Recv == stateHash,
                            "The native replica did not accept the transition hash.");
                    detail = "Exact native animation transition dispatched; remote delivery/render completion is not implied.";
                    return BoltMigrationMutationResult.Applied;
                }
                catch (Exception error) { return Failed(error, attempted, out detail); }
                finally { if (!continuationDispatch) _busy = false; }
            }

            private BoltMigrationMutationResult Transfer(bool publish, out string detail)
            {
                detail = "Animation transfer is unavailable or reentrant.";
                if (_thread != Environment.CurrentManagedThreadId || !Enter()) return BoltMigrationMutationResult.Rejected;
                bool attempted = false;
                try
                {
                    Validate(true);
                    Require(IsServer == publish, "Animation transfer direction does not match the bound role.");
                    for (int i = 0; i < _parameters.Length; i++)
                    {
                        var parameter = _parameters[i];
                        _values[i] = publish ? ReadAnimationParameter(Animator, parameter.Hash, parameter.Kind) : parameter.Field.Read(_state);
                        ValidateAnimationValue(parameter.Kind, _values[i]);
                    }
                    ValidateLayerValues(publish);
                    for (int i = 0; i < _parameters.Length; i++)
                    {
                        RequireInvocationCurrent();
                        var parameter = _parameters[i];
                        attempted = true;
                        if (parameter.Property != null) parameter.Property.OnSimulateAfter(_state);
                        else if (publish) parameter.Field.Write(_state, _values[i]);
                        else WriteParameter(parameter, _values[i]);
                        RequireInvocationCurrent();
                        if (parameter.Field.Read(_state) != _values[i]
                            || ReadAnimationParameter(Animator, parameter.Hash, parameter.Kind) != _values[i])
                            throw new InvalidOperationException("Native animation scalar transfer readback differs: " + parameter.Field.Name);
                    }
                    // Keep native state overrides, event-distance policy, smoothing and transitions.
                    // Disabled components do not receive Unity LateUpdate; this is their sole driver.
                    Validate(true, false);
                    _nativeAnimationInvocation = this;
                    try
                    {
                        if (!publish) _replicator.UpdateMecanimPropertiesFromState();
                        Validate(true, false);
                        _replicator.LateUpdate();
                    }
                    finally { _nativeAnimationInvocation = null; }
                    Validate(true, false);
                    Require(float.IsFinite(Animator.speed) && Animator.speed == _state.MecanimSpeed,
                        "Native animation playback-speed transfer readback differs.");
                    detail = publish ? "Server animation scalar readback matched; native layer/event dispatch invoked."
                        : "Replica animation scalar readback matched; native layer dispatch invoked without writing the retained source rig.";
                    return BoltMigrationMutationResult.Applied;
                }
                catch (Exception error) { return Failed(error, attempted, out detail); }
                finally { _busy = false; }
            }

            internal void Validate(bool requireActive, bool validateSchema = true)
            {
                Require(_thread == Environment.CurrentManagedThreadId && !_disposed && !_failed && !_reentered
                    && _current() && _writersHeld(), "The animation lifetime or exclusive writer hold ended.");
                if (_active)
                    Require(RuntimeAnimationBindings.TryGetValue(Animator.Pointer, out var owner) && ReferenceEquals(owner, this),
                        "The animation endpoint no longer owns its registered animator.");
                Require(_entity != null && !_entity.WasCollected && _entity && _entity.GetInstanceID() == _entityInstance && _entity.isAttached
                    && _entity.isOwner == IsServer && _entity.prefabId == BoltPrefabs.PlayerNetwork
                    && _id != 0 && _entity.networkId.PackedValue == _id
                    && _attachment != null && !_attachment.WasCollected && _attachment.IsAttached
                    && _entity._entity?.Pointer == _attachment.Pointer && _attachment.UnityObject?.Pointer == _entity.Pointer
                    && _attachment.NetworkId.PackedValue == _id && _attachment.IsOwner == IsServer
                    && !_state.WasCollected && _attachment.Serializer?.Pointer == _state.Pointer
                    && _state.Entity?.Pointer == _attachment.Pointer
                    && BoltNetwork.FindEntity(new NetworkId(_id))?.Pointer == _entity.Pointer,
                    "The animation attachment or authority changed.");
                Require(IsServer ? BoltNetwork.isServer && PlayerRoles.IsCurrentCarrier(_entity)
                    : BoltNetwork.isClient && !BoltNetwork.isServer && !_attachment.HasPredictedControl,
                    "The animation endpoint has the wrong network control role.");
                Require(!Animator.WasCollected && Animator && Animator.GetInstanceID() == _animatorInstance && Animator.isInitialized
                    && _controller != null && !_controller.WasCollected && _controller
                    && Animator.runtimeAnimatorController?.Pointer == _controller.Pointer
                    && _state.Animator?.Pointer == Animator.Pointer && !IsRetainedClientAnimator(Animator)
                    && Animator.transform.IsChildOf(_entity.transform)
                    && !Animator.hasBoundPlayables && Animator.recorderMode == AnimatorRecorderMode.Offline,
                    "The replacement animation target changed or acquired another motion owner.");
                ValidateClimbRootMotion();
                var animators = _state.Animators;
                Require(animators != null && !animators.WasCollected && animators.Pointer == _stateAnimators
                    && animators.Count == 1 && animators[0]?.Pointer == Animator.Pointer,
                    "Bolt must target only the replacement animator, not the retained local rig.");
                if (_role == AnimationRuntimeRole.SourceReplica)
                {
                    var retained = LocalPlayer.Animator;
                    Require(_retainedSource != null && !_retainedSource.WasCollected && _retainedSource
                        && retained != null && !retained.WasCollected && retained
                        && retained.Pointer == _retainedSource.Pointer && retained.Pointer != Animator.Pointer,
                        "The retained source animator must remain separate from replication.");
                }
                Require(_replicator != null && !_replicator.WasCollected && _replicator && !_replicator.enabled
                    && _replicator.GetInstanceID() == _replicatorInstance && _replicator.transform.IsChildOf(_entity.transform)
                    && _replicator.entity?.Pointer == _entity.Pointer && _replicator._hasSetLayerIndices
                    && _replicator.GetTargetAnimator()?.Pointer == Animator.Pointer,
                    "The replacement Mecanim component must remain disabled, bound and exclusively driven.");
                foreach (var control in _controls)
                    Require(control != null && !control.WasCollected && control && !control.enabled
                        && control.transform.IsChildOf(_entity.transform), "A native carrier animation writer resumed.");
                if (validateSchema)
                {
                    var layers = _replicator.GetLayersToSync();
                    Require(layers != null && !layers.WasCollected && layers.Pointer == _nativeLayers && layers.Length == _layers.Length,
                        "The native animation layer mapping changed.");
                    for (int i = 0; i < _layers.Length; i++)
                    {
                        var layer = _layers[i];
                        Require(!layer.Configuration.WasCollected && layers[i]?.Pointer == layer.Configuration.Pointer
                            && layer.Configuration.ArrayIndex == i && layer.Configuration.LayerIndex == layer.Index
                            && layer.Configuration.Name == layer.Name && layer.Configuration.SyncWeight == layer.SyncWeight
                            && layer.Index < Animator.layerCount && Animator.GetLayerName(layer.Index) == layer.Name,
                            "The native animation layer identity changed.");
                    }
                    foreach (var parameter in _parameters)
                    {
                        if (parameter.Property == null) continue;
                        var property = ReadAnimatorProperty(_state, parameter.PropertyIndex, parameter.Field, IsServer);
                        Require(property.Pointer == parameter.Property.Pointer && property.OffsetStorage == parameter.StorageOffset,
                            "An animation property descriptor changed.");
                    }
                }
                if (requireActive)
                    Require(_active && _gameplayActive() && Animator.enabled && Animator.gameObject.activeInHierarchy
                        && _replicator.gameObject.activeInHierarchy,
                        "Prepared or suspended animation cannot publish or apply gameplay.");
                Require(!_reentered, "Animation guard reentered preservation.");
            }

            private void RequireInvocationCurrent()
            {
                // No frame, network pump or Animator evaluation occurs inside a scalar batch.
                // Recheck native identity/reentry after each native call, without rescanning the
                // complete schema and enrollment for every individual parameter.
                Require(!_reentered && !_disposed && !_failed && _entity && _entity.isAttached
                    && _entity._entity?.Pointer == _attachment.Pointer && _attachment.IsOwner == IsServer
                    && _attachment.Serializer?.Pointer == _state.Pointer && Animator
                    && _state.Animator?.Pointer == Animator.Pointer && Animator.runtimeAnimatorController?.Pointer == _controller.Pointer,
                    "The animation endpoint changed inside a scalar batch.");
                ValidateClimbRootMotion();
            }

            private void ValidateLayerValues(bool publish)
            {
                var hashes = _state.MecanimLayerHashes;
                var times = _state.MecanimLayerTimes;
                var weights = _state.MecanimLayerWeights;
                Require(hashes != null && !hashes.WasCollected && hashes.Length >= _layers.Length && hashes.Length <= MaximumElements
                    && times != null && !times.WasCollected && times.Length >= _layers.Length && times.Length <= MaximumElements
                    && weights != null && !weights.WasCollected && weights.Length >= _layers.Length && weights.Length <= MaximumElements,
                    "The network animation layer arrays do not cover the native layer mapping.");
                Require(float.IsFinite(publish ? Animator.speed : _state.MecanimSpeed), "Animation playback speed is not finite.");
                for (int i = 0; i < _layers.Length; i++)
                {
                    var layer = _layers[i];
                    var state = publish ? _replicator.GetNextOrCurrentAnimatorState(layer.Index) : default;
                    float time = publish ? state.normalizedTime : times[i];
                    float weight = publish ? Animator.GetLayerWeight(layer.Index) : weights[i];
                    Require(float.IsFinite(time) && float.IsFinite(weight) && weight >= 0 && weight <= 1,
                        "Animation layer timing or weight is invalid.");
                }
            }

            private void WriteParameter(Parameter parameter, int value)
                => WriteAnimationParameter(Animator, parameter.Hash, parameter.Kind, value);

            private BoltMigrationMutationResult Failed(Exception error, bool attempted, out string detail)
            {
                if (attempted) _failed = true;
                detail = Describe(error);
                return attempted ? BoltMigrationMutationResult.Unconfirmed : BoltMigrationMutationResult.Rejected;
            }

            public void Dispose()
            {
                Require(CanDispose,
                    "Dispose the animation endpoint on its game thread outside native callbacks.");
                if (_disposed) return;
                _climbLease?.Motor.Dispose();
                _disposed = true; _active = false;
                if (RuntimeAnimationBindings.TryGetValue(Animator.Pointer, out var binding) && ReferenceEquals(binding, this))
                    RuntimeAnimationBindings.Remove(Animator.Pointer);
            }
        }
    }
}
