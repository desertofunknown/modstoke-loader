// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Endnight.Animation;
using Endnight.Utilities;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Weapon;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    // Owns the proxy created for this equipment. Existing proxies on the player's
    // animator belong to their original receivers and are never reused or removed.
    internal sealed class StrictCombatEquipmentAnimation
    {
        private readonly StrictCombatPreparedEquipment _reservation;
        private readonly StrictCombatEquipmentRenderer _renderer;
        private readonly Action _requireOperation;
        private readonly Func<bool> _canDispatch;
        private readonly RangedWeaponController _controller;
        private readonly GameObject _loaded, _root, _driverObject;
        private readonly Animator _driver, _target;
        private readonly AnimationSync _sync, _initialControllerSync;
        private readonly Il2CppSystem.Collections.Generic.List<AnimationSyncData> _data;
        private readonly AnimationSyncData[] _entries;
        private readonly AnimationRule[] _rules;
        private readonly Il2CppSystem.Collections.Generic.List<int> _hashes;
        private readonly int[] _hashEntries;
        private readonly int _thread;
        private readonly bool _initialEnabled;
        private readonly int _registrationId;
        private readonly MainThreadRunner _runner;
        private readonly Il2CppSystem.Collections.Generic.List<MainThreadRunner.ActionLink> _actions;
        private readonly Il2CppSystem.Collections.Generic.List<int> _actionKeys;
        private readonly MainThreadRunner.ActionLink _actionLink;
        private readonly Il2CppSystem.Action _action;
        private AnimatorMoveProxy _proxy;
        private IAnimatorMoveProxyReceiver _receiver;
        private bool _attempted, _mutationAttempted, _controllerWritten, _driverWritten, _proxyWritten, _receiverWritten;
        private bool _linked, _dispatching, _retired, _uncertain, _detachAttempted;
        private int _frame;
        private ulong _revision;

        private StrictCombatEquipmentAnimation(StrictCombatPreparedEquipment reservation,
            StrictCombatEquipmentRenderer renderer, GameObject loaded, Action requireOperation, Func<bool> canDispatch)
        {
            _reservation = reservation;
            _renderer = renderer;
            _loaded = loaded;
            _root = reservation.Stage.Root;
            _requireOperation = requireOperation;
            _canDispatch = canDispatch;
            _thread = Environment.CurrentManagedThreadId;
            _controller = reservation.Controller;
            _driver = _controller._playerAnimator;
            if (!Live(_driver)) throw new InvalidOperationException("The equipment animator is unavailable.");
            _driverObject = _driver.gameObject;
            _initialControllerSync = _controller._animationSync;
            var syncs = _controller.GetComponentsInChildren<AnimationSync>(true);
            if (syncs is null || syncs.WasCollected || syncs.Length > 1)
                throw new InvalidOperationException("The equipment has an unsupported animation layout.");
            _sync = syncs.Length == 0 ? null : syncs[0];
            if (_sync is null)
            {
                if (_initialControllerSync is not null)
                    throw new InvalidOperationException("The controller refers to an external animation component.");
                return;
            }
            if (!Live(_sync) || _sync._driver is not null || _sync._driverMoveProxyInstance is not null
                || (_initialControllerSync is not null && !Same(_initialControllerSync, _sync)))
                throw new InvalidOperationException("The equipment animation already has an owner.");
            _target = _sync._target;
            _data = _sync._data;
            if (_data is null || _data.WasCollected || _data.Count > 32)
                throw new InvalidOperationException("The equipment animation data is unavailable.");
            _entries = new AnimationSyncData[_data.Count];
            _rules = new AnimationRule[_entries.Length];
            for (int i = 0; i < _entries.Length; i++)
            {
                _entries[i] = _data[i];
                if (_entries[i] is null || _entries[i].WasCollected)
                    throw new InvalidOperationException("An equipment animation entry is unavailable.");
                _rules[i] = new AnimationRule(_entries[i]);
            }
            _hashes = _sync._syncParameterHashes;
            if (_hashes is not null)
            {
                if (_hashes.WasCollected || _hashes.Count > 64)
                    throw new InvalidOperationException("The equipment animation parameters are unavailable.");
                _hashEntries = new int[_hashes.Count];
                for (int i = 0; i < _hashEntries.Length; i++) _hashEntries[i] = _hashes[i];
            }
            _initialEnabled = _sync.enabled;
            _registrationId = _sync._cachedThreadRunnerInstanceId;
            if (_registrationId == 0)
            {
                if (_initialEnabled)
                    throw new InvalidOperationException("The active animation has no fixed-update registration.");
                return;
            }
            if (!_initialEnabled)
                throw new InvalidOperationException("The inactive animation still has a fixed-update registration.");
            _runner = MainThreadRunner._instance;
            if (!Live(_runner)) throw new InvalidOperationException("The animation scheduler is unavailable.");
            _actions = _runner._fixedUpdateActions;
            _actionKeys = _runner._fixedUpdateActionKeys;
            int index = FindRegistration();
            if (index < 0) throw new InvalidOperationException("The animation subscription is unavailable.");
            _actionLink = _actions[index];
            if (_actionLink is null || _actionLink.WasCollected)
                throw new InvalidOperationException("The animation subscription is unavailable.");
            _action = _actionLink.TargetAction;
        }

        internal AnimationSync Sync => _sync;
        internal bool IsLinked => _linked && !_retired && !_uncertain && !_detachAttempted;
        internal bool IsUncertain => _uncertain;
        internal bool IsDispatching => _dispatching;

        internal bool IsDetached
        {
            get
            {
                if (Environment.CurrentManagedThreadId != _thread || !_detachAttempted || _uncertain) return false;
                try
                {
                    if (Live(_proxy) || (Live(_sync) && (_sync.enabled || _sync._cachedThreadRunnerInstanceId != 0))) return false;
                    return _registrationId == 0 || !Live(_runner) || FindRegistration() == -1;
                }
                catch (Exception) { return false; }
            }
        }

        internal static bool TryCapture(StrictCombatPreparedEquipment reservation,
            StrictCombatEquipmentRenderer renderer, Action requireOperation, Func<bool> canDispatch,
            out StrictCombatEquipmentAnimation animation)
        {
            animation = null;
            try
            {
                if (reservation == null || renderer == null || requireOperation == null || canDispatch == null
                    || !reservation.IsCurrentOwnership) return false;
                requireOperation();
                if (renderer.Read(out var loaded) != StrictCombatEquipmentRendererStatus.Ready) return false;
                var candidate = new StrictCombatEquipmentAnimation(reservation, renderer, loaded, requireOperation, canDispatch);
                candidate.BeginFrame();
                candidate.RequireCurrent();
                if (candidate._sync is not null)
                    foreach (var entry in candidate._entries)
                        if (!entry.Validate(candidate._driver, candidate._target, out _)) return false;
                candidate.RequireCurrent();
                if (!StrictCombatWeaponHooks.TryOwnEquipmentAnimation(candidate)) return false;
                animation = candidate;
                return true;
            }
            catch (Exception) { return false; }
        }

        internal bool TryLink()
        {
            if (Environment.CurrentManagedThreadId != _thread || _attempted || _retired || _detachAttempted) return false;
            _attempted = true;
            try
            {
                BeginFrame();
                RequireCurrent();
                if (_sync is not null)
                {
                    // Publish ownership before any reference can enable a native update.
                    _mutationAttempted = true;
                    _proxy = _driverObject.AddComponent<AnimatorMoveProxy>();
                    if (!Live(_proxy) || !Same(_proxy.gameObject, _driverObject) || _proxy._receiver is not null)
                        throw new InvalidOperationException("The owned animation proxy is unavailable.");
                    RequireCurrent();
                    _receiver = _sync.Cast<IAnimatorMoveProxyReceiver>();
                    _proxy._receiver = _receiver;
                    _receiverWritten = true;
                    RequireCurrent();
                    _sync._driverMoveProxyInstance = _proxy;
                    _proxyWritten = true;
                    RequireCurrent();
                    _sync._driver = _driver;
                    _driverWritten = true;
                    RequireCurrent();
                    _controller._animationSync = _sync;
                    _controllerWritten = true;
                    RequireCurrent();
                    _sync.RevalidateSyncData();
                    RequireCurrent();
                }
                _linked = true;
                return true;
            }
            catch (Exception)
            {
                if (_mutationAttempted) Fail();
                else _retired = true;
                return false;
            }
        }

        internal bool ReferencesCurrent()
        {
            if (!IsLinked || _dispatching) return false;
            try { BeginFrame(); RequireCurrent(); return true; }
            catch (Exception) { return false; }
        }

        internal bool TryEnterUpdate(Animator driver, Animator target)
        {
            if (Environment.CurrentManagedThreadId != _thread || !IsLinked || _dispatching) return false;
            try
            {
                if (!Same(driver, _driver) || !Same(target, _target)) { Fail(); return false; }
                BeginFrame();
                RequireCurrent();
                if (!_sync.isActiveAndEnabled || !_canDispatch()) return false;
                RequireCurrent();
                _dispatching = true;
                return true;
            }
            catch (Exception) { Fail(); return false; }
        }

        internal void EndUpdate(Exception error)
        {
            if (!_dispatching) return;
            try
            {
                if (error != null) throw new InvalidOperationException("Equipment animation failed.", error);
                RequireCurrent();
                if (!_canDispatch()) throw new InvalidOperationException("The equipment animation phase changed.");
                RequireCurrent();
            }
            catch (Exception) { Fail(); }
            finally { _dispatching = false; }
        }

        // Requests cleanup of retained components, including after life retirement.
        // Destroy is deferred: the caller must observe IsDetached before completing
        // teardown. No other receiver's proxy is discovered or destroyed.
        internal bool TryDetach()
        {
            if (Environment.CurrentManagedThreadId != _thread || _detachAttempted || _dispatching) return false;
            _detachAttempted = true;
            _linked = false;
            _retired = true;
            bool complete = true;
            try
            {
                if (Live(_sync))
                {
                    if (!Live(_root) || !Within(_sync.transform, _root.transform))
                        throw new InvalidOperationException("The owned animation component moved.");
                    RequireRegistration();
                    _sync.enabled = false; // Native OnDisable removes its fixed-update subscription.
                    if (_sync.enabled || _sync._cachedThreadRunnerInstanceId != 0
                        || (_registrationId != 0 && FindRegistration() != -1))
                        throw new InvalidOperationException("The animation subscription was not removed.");
                }
                else if (_registrationId != 0 && Live(_runner) && FindRegistration() != -1)
                    throw new InvalidOperationException("The destroyed animation still has a subscription.");
            }
            catch (Exception) { complete = false; }
            try
            {
                if (Live(_proxy))
                {
                    if (!Live(_driverObject) || !Same(_proxy.gameObject, _driverObject)
                        || (_receiverWritten && !Same(_proxy._receiver, _receiver)))
                        throw new InvalidOperationException("The owned animation proxy changed.");
                    _proxy.enabled = false;
                    UnityEngine.Object.Destroy(_proxy);
                    // Keep the receiver intact until deferred destruction; its updates
                    // are already rejected by the retained animation owner.
                }
            }
            catch (Exception) { complete = false; }
            if (!complete) Fail();
            return complete;
        }

        private void BeginFrame()
        {
            if (Environment.CurrentManagedThreadId != _thread || !_reservation.IsCurrentOwnership)
                throw new InvalidOperationException("The equipment life is not current on this thread.");
            _requireOperation();
            if (!_reservation.Source.Owner.TryRead(out _, out var state))
                throw new InvalidOperationException("Accepted equipment state is unavailable.");
            _revision = state.Revision;
            _frame = Time.frameCount;
        }

        private void RequireCurrent()
        {
            if (_retired || _uncertain || _detachAttempted || Environment.CurrentManagedThreadId != _thread
                || !_reservation.IsCurrentOwnership)
                throw new InvalidOperationException("The equipment animation owner changed.");
            _requireOperation();
            if (!Live(_controller) || !Live(_root) || !Same(_root, _reservation.Stage.Root)
                || !Live(_driver) || !Same(_driver, _controller._playerAnimator)
                || !Same(_driver, _reservation.Source.Props._playerAnimator)
                || !Same(_driver.gameObject, _driverObject)
                || _renderer.Read(out var loaded) != StrictCombatEquipmentRendererStatus.Ready || !Same(loaded, _loaded))
                throw new InvalidOperationException("The equipment animation sources changed.");
            var syncs = _controller.GetComponentsInChildren<AnimationSync>(true);
            if (syncs is null || syncs.WasCollected || syncs.Length != (_sync is null ? 0 : 1)
                || (_sync is not null && !Same(syncs[0], _sync))
                || !SameOptional(_controller._animationSync, _controllerWritten ? _sync : _initialControllerSync))
                throw new InvalidOperationException("The controller animation association changed.");
            if (_sync is not null)
            {
                if (!Live(_sync) || !Within(_sync.transform, _root.transform)
                    || !Live(_target) || !Same(_sync._target, _target) || Same(_target, _driver)
                    || !Within(_target.transform, _root.transform)
                    || !SameOptional(_sync._driver, _driverWritten ? _driver : null)
                    || !SameOptional(_sync._driverMoveProxyInstance, _proxyWritten ? _proxy : null)
                    || !Same(_data, _sync._data) || _data.Count != _entries.Length
                    || !SameOptional(_hashes, _sync._syncParameterHashes))
                    throw new InvalidOperationException("The captured animation binding changed.");
                for (int i = 0; i < _entries.Length; i++)
                {
                    if (!Same(_entries[i], _data[i]))
                        throw new InvalidOperationException("An animation data entry changed.");
                    _rules[i].RequireCurrent();
                }
                if (_hashes is not null)
                {
                    if (_hashes.Count != _hashEntries.Length)
                        throw new InvalidOperationException("The animation parameter list changed.");
                    for (int i = 0; i < _hashEntries.Length; i++)
                        if (_hashes[i] != _hashEntries[i])
                            throw new InvalidOperationException("An animation parameter changed.");
                }
                if (_proxy is not null && (!Live(_proxy) || !Same(_proxy.gameObject, _driverObject)
                    || !SameOptional(_proxy._receiver, _receiverWritten ? _receiver : null)))
                    throw new InvalidOperationException("The captured animation proxy changed.");
                RequireRegistration();
            }
            _requireOperation();
            if (Time.frameCount != _frame || !_reservation.IsCurrentOwnership
                || !_reservation.Source.Owner.TryRead(out _, out var state) || state.Revision != _revision)
                throw new InvalidOperationException("Accepted equipment state changed during animation.");
        }

        private void Fail()
        {
            _uncertain = true;
            _retired = true;
            _reservation.Retire();
            _reservation.Source.Life.Retire();
        }

        private int FindRegistration()
        {
            if (!Live(_runner) || !Same(MainThreadRunner._instance, _runner)
                || !Same(_runner._fixedUpdateActions, _actions) || !Same(_runner._fixedUpdateActionKeys, _actionKeys)
                || _actions is null || _actionKeys is null || _actions.WasCollected || _actionKeys.WasCollected
                || _actions.Count != _actionKeys.Count || _actions.Count > 8192)
                throw new InvalidOperationException("The captured animation scheduler changed.");
            int found = -1;
            for (int i = 0; i < _actionKeys.Count; i++)
                if (_actionKeys[i] == _registrationId)
                {
                    if (found != -1) throw new InvalidOperationException("The animation registration is not unique.");
                    found = i;
                }
            return found;
        }

        private void RequireRegistration()
        {
            if (_sync.enabled != _initialEnabled || _sync._cachedThreadRunnerInstanceId != _registrationId)
                throw new InvalidOperationException("The animation registration changed.");
            if (_registrationId == 0) return;
            int index = FindRegistration();
            if (index < 0 || !Same(_actions[index], _actionLink)
                || _actionLink._actionLinkType != MainThreadRunner.ActionLinkType.Component
                || !Same(_actionLink.ComponentReference, _sync) || _action is null || _action.WasCollected
                || !Same(_actionLink.TargetAction, _action))
                throw new InvalidOperationException("The animation callback owner changed.");
        }

        private sealed class AnimationRule
        {
            private readonly AnimationSyncData _data;
            private readonly (int, int, int, int, int, bool, bool, int, string, string) _configuration;
            private readonly Il2CppSystem.Collections.Generic.List<string> _parameters;
            private readonly string[] _names;

            internal AnimationRule(AnimationSyncData data)
            {
                _data = data;
                _configuration = Read();
                _parameters = data._syncParameters;
                if (_parameters is null) return;
                if (_parameters.WasCollected || _parameters.Count > 64)
                    throw new InvalidOperationException("The animation rule parameters are unavailable.");
                _names = new string[_parameters.Count];
                for (int i = 0; i < _names.Length; i++) _names[i] = _parameters[i];
            }

            private (int, int, int, int, int, bool, bool, int, string, string) Read()
                => (_data._driverLayer, _data._driverState, _data._targetLayer, _data._targetState,
                    _data._maxEvaluateCount, _data._useParameterToSyncTime, _data._syncToNextState,
                    BitConverter.SingleToInt32Bits(_data._lagMinOffsetThreshold), _data._driverStateName, _data._targetStateName);

            internal void RequireCurrent()
            {
                // Evaluation legitimately changes its counters and lag values. Those
                // runtime values are deliberately excluded from the configuration.
                if (Read() != _configuration || !SameOptional(_data._syncParameters, _parameters))
                    throw new InvalidOperationException("An animation rule changed.");
                if (_parameters is null) return;
                if (_parameters.Count != _names.Length)
                    throw new InvalidOperationException("An animation rule parameter list changed.");
                for (int i = 0; i < _names.Length; i++)
                    if (!string.Equals(_parameters[i], _names[i], StringComparison.Ordinal))
                        throw new InvalidOperationException("An animation rule parameter changed.");
            }
        }

        private static bool Within(Transform child, Transform parent)
            => Live(child) && Live(parent) && (Same(child, parent) || child.IsChildOf(parent));
        private static bool Live(UnityEngine.Object value) => value is not null && !value.WasCollected && value;
        private static bool Same(Il2CppObjectBase first, Il2CppObjectBase second)
            => first is not null && second is not null && !first.WasCollected && !second.WasCollected
                && first.Pointer != 0 && first.Pointer == second.Pointer;
        private static bool SameOptional(Il2CppObjectBase first, Il2CppObjectBase second)
            => first is null ? second is null : Same(first, second);
    }
}
