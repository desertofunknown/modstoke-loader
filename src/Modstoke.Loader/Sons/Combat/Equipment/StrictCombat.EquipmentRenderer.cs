// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Items.Core;
using global::Sons.Weapon;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Modstoke.Sons.Combat
{
    internal enum StrictCombatEquipmentRendererStatus
    {
        Pending, Ready, Failed, ContextChanged
    }

    // Observes native loading without acquiring the handle or dispatching callbacks.
    internal sealed class StrictCombatEquipmentRenderer
    {
        private readonly StrictCombatPreparedEquipment _reservation;
        private readonly StrictCombatEquipmentPrefab _stage;
        private readonly Action _requireOperation;
        private readonly RangedWeaponController _controller;
        private readonly RangedWeapon _weapon;
        private readonly GameObject _itemObject, _root;
        private readonly ItemRenderable _renderer;
        private AsyncOperationBase<GameObject> _operation;
        private int _version;
        private GameObject _loaded;
        private StrictCombatEquipmentRendererStatus _status;

        internal StrictCombatEquipmentRenderer(StrictCombatPreparedEquipment reservation, Action requireOperation)
        {
            _reservation = reservation ?? throw new ArgumentNullException(nameof(reservation));
            _requireOperation = requireOperation ?? throw new ArgumentNullException(nameof(requireOperation));
            RequireOwnership();
            _requireOperation();
            _stage = reservation.Stage;
            _controller = reservation.Controller;
            _weapon = reservation.Weapon;
            _itemObject = reservation.ItemObject;
            _root = _stage?.Root;
            if (!Live(_controller)) throw new InvalidOperationException("The prepared controller is unavailable.");
            _renderer = _controller.GetComponent<ItemRenderable>();
            RequireSources();
        }

        internal StrictCombatEquipmentRendererStatus Read(out GameObject loaded)
        {
            loaded = null;
            if (_status == StrictCombatEquipmentRendererStatus.ContextChanged
                || _status == StrictCombatEquipmentRendererStatus.Failed) return _status;
            if (!_reservation.Source.Owner.IsCurrentNativeHealthOwner(_reservation.Source.Life))
                return StrictCombatEquipmentRendererStatus.ContextChanged;
            try
            {
                int frame = Time.frameCount;
                if (!_reservation.Source.Owner.TryRead(out _, out var accepted))
                    throw new InvalidOperationException("Accepted equipment state is unavailable.");
                ulong revision = accepted.Revision;
                RequireSources();
                var operation = ReadOperation(out int version);
                if (operation is not null && _operation is null)
                {
                    _operation = operation;
                    _version = version;
                }
                RequireRetainedOperation(operation, version);
                var status = StrictCombatEquipmentRendererStatus.Pending;
                GameObject candidate = null;
                if (operation is not null)
                {
                    int nativeStatus = (int)operation.m_Status;
                    var result = operation._Result_k__BackingField;
                    if (_loaded is not null && (!Live(_loaded) || !Same(result, _loaded)
                        || !Same(_renderer._cachedLoadedObject, _loaded)))
                        throw new InvalidOperationException("The loaded equipment object changed.");
                    if (nativeStatus == 2) status = StrictCombatEquipmentRendererStatus.Failed;
                    else if (nativeStatus == 1 && IsReady(result))
                    {
                        candidate = result;
                        status = StrictCombatEquipmentRendererStatus.Ready;
                    }
                    else if (nativeStatus != 0 && nativeStatus != 1)
                        throw new InvalidOperationException("The equipment load status is invalid.");
                }

                RequireSources();
                var finalOperation = ReadOperation(out int finalVersion);
                RequireRetainedOperation(finalOperation, finalVersion);
                if ((operation is null) != (finalOperation is null)
                    || (operation is not null && (!Same(operation, finalOperation) || version != finalVersion)))
                    throw new InvalidOperationException("The equipment load operation changed.");
                if (status == StrictCombatEquipmentRendererStatus.Ready
                    && ((int)finalOperation.m_Status != 1
                        || !Same(finalOperation._Result_k__BackingField, candidate) || !IsReady(candidate)))
                    throw new InvalidOperationException("The equipment load completion changed.");
                RequireOwnership();
                if (Time.frameCount != frame || !_reservation.Source.Owner.TryRead(out _, out accepted)
                    || accepted.Revision != revision)
                    throw new InvalidOperationException("Accepted equipment state changed during rendering.");
                _status = status;
                if (status == StrictCombatEquipmentRendererStatus.Ready)
                {
                    _loaded = candidate;
                    loaded = candidate;
                }
                return _status;
            }
            catch (Exception)
            {
                _status = StrictCombatEquipmentRendererStatus.ContextChanged;
                return _status;
            }
        }

        private AsyncOperationBase<GameObject> ReadOperation(out int version)
        {
            version = 0;
            // This getter boxes a fresh wrapper. Only the underlying operation and
            // its version identify the load; the handle wrapper pointer does not.
            var handle = _renderer._instantiateAsyncOperationHandle;
            if (handle is null || handle.WasCollected) return null;
            var operation = handle.m_InternalOp;
            if (operation is null || operation.WasCollected || operation.Pointer == 0) return null;
            version = handle.m_Version;
            if (operation.m_Version != version)
                throw new InvalidOperationException("The equipment load operation was recycled.");
            return operation;
        }

        private void RequireRetainedOperation(AsyncOperationBase<GameObject> operation, int version)
        {
            if (_operation is not null && (!Same(operation, _operation) || version != _version
                || _operation.m_Version != _version))
                throw new InvalidOperationException("The retained equipment load operation changed.");
        }

        private void RequireSources()
        {
            RequireOwnership();
            _requireOperation();
            if (!_reservation.IsCurrentOwnership || !ReferenceEquals(_reservation.Stage, _stage)
                || _stage is null || !Live(_root) || !Same(_stage.Root, _root)
                || !Live(_itemObject) || !Same(_stage.Clone, _itemObject)
                || !Same(_reservation.ItemObject, _itemObject)
                || !Same(_itemObject.transform.parent, _root.transform)
                || !Live(_controller) || !Same(_reservation.Controller, _controller)
                || !Live(_weapon) || !Same(_reservation.Weapon, _weapon)
                || !Within(_controller.transform, _itemObject.transform)
                || !Within(_weapon.transform, _itemObject.transform)
                || !Live(_renderer) || !Same(_controller.GetComponent<ItemRenderable>(), _renderer)
                || !Same(_renderer.gameObject, _controller.gameObject))
                throw new InvalidOperationException("The equipment renderer sources changed.");
            var renderers = _weapon.GetComponentsInChildren<ItemRenderable>(true);
            if (renderers is null || renderers.WasCollected || renderers.Length != 1
                || !Same(renderers[0], _renderer))
                throw new InvalidOperationException("The equipment renderer association changed.");
            _requireOperation();
            RequireOwnership();
        }

        private void RequireOwnership()
        {
            if (!_reservation.IsCurrentOwnership)
                throw new InvalidOperationException("The equipment life is not current on this thread.");
        }

        private bool IsReady(GameObject result)
            => Live(result) && Same(_renderer._cachedLoadedObject, result)
                && Live(result.transform) && Same(_controller._renderableTransform, result.transform)
                && _weapon._renderableIsLoaded && !Same(result, _root)
                && result.transform.IsChildOf(_root.transform);

        private static bool Within(Transform child, Transform parent)
            => Live(child) && Live(parent) && (Same(child, parent) || child.IsChildOf(parent));

        private static bool Live(UnityEngine.Object value)
            => value is not null && !value.WasCollected && value;

        private static bool Same(Il2CppObjectBase first, Il2CppObjectBase second)
            => first is not null && second is not null && !first.WasCollected && !second.WasCollected
                && first.Pointer != 0 && first.Pointer == second.Pointer;
    }
}
