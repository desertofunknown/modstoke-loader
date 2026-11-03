// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Items.Core;
using global::Sons.Weapon;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Modstoke.Sons.Combat
{
    // Owns the first activation and one addressable release. Inventory, event/proxy
    // teardown and destruction of the staging root remain the equip operation's duties.
    internal sealed class StrictCombatEquipmentActivation
    {
        private readonly StrictCombatPreparedEquipment _reservation;
        private readonly Action _requireOperation;
        private readonly Func<bool> _canRelease;
        private readonly int _thread;
        private readonly GameObject _root, _clone;
        private readonly Transform _parent;
        private readonly bool _controllerWasEnabled;
        private AsyncOperationHandle<GameObject> _handle;
        private AsyncOperationBase<GameObject> _operation;
        private GameObject _releasedResult;
        private int _version, _frame, _enableDepth, _loadDepth;
        private ulong _revision;
        private bool _activateAttempted, _controllerDisabled, _rootActive, _cloneActive;
        private bool _activating, _activated, _derivedEnableSeen, _baseEnableSeen;
        private bool _retiring, _releaseAttempted, _releaseReturned, _releaseUncertain, _uncertain;
        private bool _enableControllerAttempted, _controllerEnabled;
        private bool _updating, _disableAttempted;

        private StrictCombatEquipmentActivation(StrictCombatPreparedEquipment reservation,
            Action requireOperation, Func<bool> canRelease)
        {
            _reservation = reservation;
            _requireOperation = requireOperation;
            _canRelease = canRelease;
            _thread = Environment.CurrentManagedThreadId;
            _root = reservation.Stage.Root;
            _clone = reservation.ItemObject;
            _parent = _root.transform.parent;
            Renderer = reservation.Controller.GetComponent<ItemRenderable>();
            _controllerWasEnabled = reservation.Controller.enabled;
        }

        internal ItemRenderable Renderer { get; }
        internal RangedWeaponController Controller => _reservation.Controller;
        internal bool IsActive => _activated && !_retiring && !_uncertain;
        internal bool IsRetiring => _retiring;
        internal bool IsUncertain => _uncertain;
        internal bool ReleaseAttempted => _releaseAttempted;

        internal static bool TryCapture(StrictCombatPreparedEquipment reservation, Action requireOperation,
            Func<bool> canRelease, out StrictCombatEquipmentActivation activation)
        {
            activation = null;
            try
            {
                if (reservation == null || requireOperation == null || canRelease == null
                    || !reservation.IsCurrentPreparation || !reservation.Stage.IsCurrent()) return false;
                requireOperation();
                var candidate = new StrictCombatEquipmentActivation(reservation, requireOperation, canRelease);
                candidate.BeginFrame();
                candidate.RequireCurrent();
                if (!candidate._controllerWasEnabled || !candidate.Renderer.enabled || candidate.ReadOperation(out _, out _) is not null
                    || candidate.Renderer._cachedLoadedObject is not null
                    || reservation.Controller._isInitialised) return false;
                if (!StrictCombatWeaponHooks.TryOwnEquipmentActivation(candidate)) return false;
                activation = candidate;
                return true;
            }
            catch (Exception) { return false; }
        }

        internal bool TryActivate()
        {
            if (!OnThread || _activateAttempted || _retiring || _uncertain) return false;
            try
            {
                BeginFrame();
                RequireCurrent();
                _activateAttempted = true;
                // RangedWeaponController.Update does not consult _isInitialised.
                // Keep it disabled through loading and all subsequent setup phases.
                _reservation.Controller.enabled = false;
                _controllerDisabled = true;
                RequireCurrent();
                _rootActive = true;
                _root.SetActive(true);
                RequireCurrent();
                _cloneActive = true;
                _activating = true;
                try { _clone.SetActive(true); }
                finally { _activating = false; }
                RequireCurrent();
                Require(_derivedEnableSeen && _baseEnableSeen && _enableDepth == 0 && _loadDepth == 0);
                RetainOperation(required: true);
                RequireCurrent();
                _activated = true;
                return true;
            }
            catch (Exception)
            {
                if (_activateAttempted) Fail();
                return false;
            }
        }

        internal bool TryEnableController(StrictCombatEquipmentHandoff handoff)
        {
            if (!OnThread || !IsActive || _enableControllerAttempted || handoff is null) return false;
            try
            {
                BeginFrame();
                RequireCurrent();
                Require(handoff.IsCommittedFor(_reservation) && handoff.ReferencesCurrent()
                    && _reservation.Controller._isInitialised);
                _enableControllerAttempted = true;
                _controllerEnabled = true;
                _reservation.Controller.enabled = true;
                RequireCurrent();
                Require(handoff.IsCommittedFor(_reservation) && handoff.ReferencesCurrent());
                return true;
            }
            catch (Exception)
            {
                if (_enableControllerAttempted) Fail();
                return false;
            }
        }

        // The owner calls this before detaching its events and animation proxy. No
        // inventory or current-player mutation is permitted during retirement.
        internal bool Retire()
        {
            if (!OnThread || _enableDepth != 0 || _loadDepth != 0 || _activating || _updating) return false;
            _retiring = true;
            _reservation.Retire();
            try
            {
                if (_disableAttempted) return !Live(Controller) || !Controller.enabled;
                _disableAttempted = true;
                if (Live(Controller))
                {
                    Require(Live(_clone) && (Same(Controller.transform, _clone.transform)
                        || Controller.transform.IsChildOf(_clone.transform)));
                    Controller.enabled = false;
                    Require(!Controller.enabled);
                }
                return true;
            }
            catch (Exception) { _uncertain = true; return false; }
        }

        // canRelease confirms the owner's exact event/proxy teardown. A pending proxy
        // must not be destroyed indirectly by releasing its rendered parent too early.
        internal bool TryRelease()
        {
            if (!OnThread || !_retiring || _releaseAttempted || _enableDepth != 0 || _loadDepth != 0
                || _activating || _updating) return false;
            try
            {
                if (Live(Controller) && Controller.enabled) return false;
                if (!_canRelease()) return false;
                _releaseAttempted = true;
                RequireRendererIdentity();
                RetainOperation(required: _baseEnableSeen);
                // OnDestroy removes the native virtual completion delegate without
                // release or unloaded-event fan-out. It is safe to repeat on destruction;
                // releasing the handle is not.
                if (Live(Renderer))
                {
                    Renderer.OnDestroy();
                    RequireRendererIdentity();
                }
                if (_operation is not null)
                {
                    RequireRetainedOperation();
                    _releasedResult = _operation._Result_k__BackingField;
                    Addressables.Release<GameObject>(_handle);
                }
                // A pending operation may still hold its own execution reference.
                // Do not release again just because the renderer's handle looks valid.
                if (Live(Renderer))
                {
                    RequireRendererIdentity();
                    Renderer._cachedLoadedObject = null;
                    Require(Renderer._cachedLoadedObject is null);
                    Renderer.enabled = false;
                    Require(!Renderer.enabled);
                }
                _releaseReturned = true;
                return true;
            }
            catch (Exception)
            {
                // Before consuming the attempt, this path has made no native changes.
                // The owner may finish teardown and call again; a release never retries.
                if (_releaseAttempted) _releaseUncertain = true;
                _uncertain = true;
                _reservation.Retire();
                return false;
            }
        }

        // A returned request alone does not prove reference release. Pending instances
        // can be created and queued for destruction between observations; the caller
        // must still destroy and observe the complete owned staging hierarchy.
        internal bool IsReferenceReleased
        {
            get
            {
                if (!OnThread || !_releaseReturned || _releaseUncertain) return false;
                try
                {
                    if (_operation is not null && !_operation.WasCollected
                        && _operation.m_Version == _version) return false;
                    return !Live(_releasedResult) && (!Live(Renderer) || !Renderer.enabled);
                }
                catch (Exception) { return false; }
            }
        }

        internal bool ReferencesCurrent()
        {
            if (!OnThread || !IsActive || _enableDepth != 0 || _loadDepth != 0) return false;
            try { BeginFrame(); RequireCurrent(); RequireRetainedOperation(); return true; }
            catch (Exception) { Fail(); return false; }
        }

        internal bool TryEnterEnable(bool derived)
        {
            if (!OnThread || _retiring || _uncertain) return false;
            try
            {
                Require(_activating && _activateAttempted && !_activated);
                RequireCurrent();
                if (derived)
                {
                    Require(!_derivedEnableSeen && _enableDepth == 0);
                    _derivedEnableSeen = true;
                }
                else
                {
                    Require(_derivedEnableSeen && !_baseEnableSeen && _enableDepth == 1);
                    _baseEnableSeen = true;
                }
                _enableDepth++;
                return true;
            }
            catch (Exception) { Fail(); return false; }
        }

        internal void EndEnable(Exception error)
        {
            if (!OnThread || _enableDepth <= 0) { Fail(); return; }
            try
            {
                if (error != null) throw new InvalidOperationException("Equipment activation failed.", error);
                RequireCurrent();
            }
            catch (Exception) { Fail(); }
            finally { _enableDepth--; }
        }

        internal bool TryEnterLoad(AsyncOperationHandle<GameObject> handle, bool derived)
        {
            if (!OnThread || _retiring || _uncertain) return false;
            try
            {
                Require(_activating || _activated);
                if (_loadDepth == 0 && _enableDepth == 0) BeginFrame();
                RequireCurrent();
                Require(derived ? _loadDepth == 0 : _loadDepth == 1);
                RetainOperation(required: true);
                Require(handle is not null && !handle.WasCollected
                    && Same(handle.m_InternalOp, _operation) && handle.m_Version == _version);
                RequireRetainedOperation();
                Require((int)_operation.m_Status == 1 && Live(_operation._Result_k__BackingField));
                _loadDepth++;
                return true;
            }
            catch (Exception) { Fail(); return false; }
        }

        internal void EndLoad(Exception error)
        {
            if (!OnThread || _loadDepth <= 0) { Fail(); return; }
            try
            {
                if (error != null) throw new InvalidOperationException("Equipment rendering failed.", error);
                RequireCurrent();
                RequireRetainedOperation();
            }
            catch (Exception) { Fail(); }
            finally { _loadDepth--; }
        }

        internal void SuppressNativeCleanup()
        {
            // Native OnDisable and failed-load cleanup share this path. Neither may
            // consume our addressable reference. The owner will perform one release.
            if (!_retiring) Fail();
        }

        internal bool TryEnterUpdate()
        {
            if (!OnThread || !IsActive || !_controllerEnabled || !_reservation.IsAdopted || _updating
                || _activating || _enableDepth != 0 || _loadDepth != 0) return false;
            try
            {
                BeginFrame();
                RequireCurrent();
                RequireRetainedOperation();
                _updating = true;
                return true;
            }
            catch (Exception) { Fail(); return false; }
        }

        internal void EndUpdate(Exception error)
        {
            if (!OnThread || !_updating) { Fail(); return; }
            try
            {
                if (error != null) throw new InvalidOperationException("Equipment controller update failed.", error);
                RequireCurrent();
                RequireRetainedOperation();
            }
            catch (Exception) { Fail(); }
            finally { _updating = false; }
        }

        private void BeginFrame()
        {
            Require(OnThread && !_retiring && !_uncertain && _reservation.IsCurrentOwnership);
            _requireOperation();
            Require(_reservation.Source.Owner.TryRead(out _, out var state));
            _revision = state.Revision;
            _frame = Time.frameCount;
        }

        private void RequireCurrent()
        {
            Require(OnThread && !_retiring && !_uncertain && _reservation.IsCurrentOwnership);
            _requireOperation();
            Require(Time.frameCount == _frame && _reservation.Source.Owner.TryRead(out _, out var state)
                && state.Revision == _revision);
            RequireRendererIdentity();
            Require(Live(Renderer) && Renderer.enabled && Live(_root) && Live(_clone) && Live(_parent)
                && Same(_root, _reservation.Stage.Root) && Same(_clone, _reservation.ItemObject)
                && Same(_clone.transform.parent, _root.transform) && Same(_root.transform.parent, _parent)
                && _root.activeSelf == _rootActive && _clone.activeSelf == _cloneActive
                && _root.activeInHierarchy == _rootActive && _clone.activeInHierarchy == (_rootActive && _cloneActive)
                && Live(_reservation.Controller)
                && _reservation.Controller.enabled == (_controllerEnabled || (!_controllerDisabled && _controllerWasEnabled))
                && Same(_reservation.Controller._itemInstance, _reservation.Source.Item)
                && Same(_reservation.Controller._rangedWeapon, _reservation.Weapon)
                && Same(_reservation.Weapon._ammo, _reservation.Source.Ammo));
            _requireOperation();
            Require(_reservation.IsCurrentOwnership && Time.frameCount == _frame
                && _reservation.Source.Owner.TryRead(out _, out state) && state.Revision == _revision);
        }

        private void RequireRendererIdentity()
        {
            Require(Renderer is not null && !Renderer.WasCollected);
            if (!Live(Renderer)) return; // Retained handles can outlive a destroyed renderer.
            Require(Live(_reservation.Controller) && Same(Renderer, _reservation.Controller.GetComponent<ItemRenderable>())
                && Same(Renderer.gameObject, _reservation.Controller.gameObject));
        }

        private AsyncOperationBase<GameObject> ReadOperation(out AsyncOperationHandle<GameObject> handle, out int version)
        {
            handle = null;
            version = 0;
            if (!Live(Renderer)) return null;
            handle = Renderer._instantiateAsyncOperationHandle;
            if (handle is null || handle.WasCollected) return null;
            var operation = handle.m_InternalOp;
            if (operation is null || operation.WasCollected || operation.Pointer == 0) return null;
            version = handle.m_Version;
            Require(operation.m_Version == version && operation.m_referenceCount > 0);
            return operation;
        }

        private void RetainOperation(bool required)
        {
            var operation = ReadOperation(out var handle, out int version);
            if (_operation is null)
            {
                Require(!required || operation is not null);
                if (operation is null) return;
                _operation = operation;
                _handle = handle;
                _version = version;
            }
            else
            {
                Require(!Live(Renderer) || (Same(operation, _operation) && version == _version));
                RequireRetainedOperation();
            }
        }

        private void RequireRetainedOperation()
            => Require(_operation is not null && !_operation.WasCollected && _operation.m_Version == _version
                && _operation.m_referenceCount > 0 && _handle is not null && !_handle.WasCollected
                && Same(_handle.m_InternalOp, _operation) && _handle.m_Version == _version);

        private bool OnThread => Environment.CurrentManagedThreadId == _thread;

        private void Fail()
        {
            _uncertain = true;
            _retiring = true;
            _reservation.Retire();
            _reservation.Source.Life.Retire();
        }

        private static void Require(bool value)
        {
            if (!value) throw new InvalidOperationException("The equipment activation context changed.");
        }
        private static bool Live(UnityEngine.Object value) => value is not null && !value.WasCollected && value;
        private static bool Same(Il2CppObjectBase first, Il2CppObjectBase second)
            => first is not null && second is not null && !first.WasCollected && !second.WasCollected
                && first.Pointer == second.Pointer;
    }
}
