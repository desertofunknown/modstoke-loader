// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Items.Core;
using global::Sons.Player;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;
using GameInput = global::Sons.Input.InputSystem;

namespace Modstoke.Sons.Combat
{
    // One retained operation owns the transfer, presentation and cancellation. It never
    // substitutes an inventory item or retries an effect whose outcome is uncertain.
    internal sealed class StrictCombatEquipmentOperation
    {
        private enum Phase { Preparing, Loading, Settling, Committing, Active, Cancelling, Finished }
        private readonly StrictCombatBinding _binding;
        private readonly ulong _life;
        private readonly int _thread;
        private Phase _phase;
        private StrictCombatEquipmentTransfer _transfer;
        private StrictCombatEquipmentPrefab _stage;
        private StrictCombatPreparedEquipment _reservation;
        private StrictCombatEquipmentBinding _references;
        private StrictCombatEquipmentHandoff _handoff;
        private StrictCombatEquipmentActivation _activation;
        private StrictCombatEquipmentRenderer _renderer;
        private StrictCombatEquipmentAppearance _appearance;
        private StrictCombatEquipmentAnimation _animation;
        private StrictCombatEquipmentController _controller;
        private StrictCombatEquipmentEvents _events;
        private GameObject _loaded;
        private bool _started, _executing, _uncertain, _returned;
        private bool _eventsDetached, _animationDetachAttempted, _controllerDetached;
        private bool _inventoryCancelled, _returnAttempted, _destroyAttempted;
        private bool _cleanupFailed;

        internal StrictCombatEquipmentOperation(StrictCombatLocalWeapon source,
            in StrictCombatBinding binding, in StrictCombatSnapshot accepted)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            _binding = binding;
            _life = accepted.LifeGeneration;
            _thread = Environment.CurrentManagedThreadId;
        }

        internal StrictCombatLocalWeapon Source { get; }
        internal StrictCombatPreparedEquipment Reservation => _reservation;
        internal bool IsActive => _phase == Phase.Active && !_uncertain;
        internal bool IsFinished => _phase == Phase.Finished;
        internal bool IsReturned => IsFinished && _returned && !_uncertain;
        internal bool IsUncertain => _uncertain;
        internal bool RequiresCleanup => _phase == Phase.Cancelling;
        internal bool IsExecuting => _executing;
        internal bool CanAdopt => _executing && _phase == Phase.Committing && !_uncertain;
        internal StrictCombatEquipmentStatus Status => _uncertain ? StrictCombatEquipmentStatus.MutationUncertain : IsFinished
            ? (IsReturned ? StrictCombatEquipmentStatus.Returned : StrictCombatEquipmentStatus.MutationUncertain)
            : IsActive ? StrictCombatEquipmentStatus.Equipped
            : _phase == Phase.Cancelling ? StrictCombatEquipmentStatus.Cancelling
            : StrictCombatEquipmentStatus.Pending;

        internal void RequestCancellation()
        {
            if (Environment.CurrentManagedThreadId != _thread || IsFinished) return;
            _phase = Phase.Cancelling;
            // Stop native input immediately. Retiring the retained reservation only
            // changes ownership flags; native teardown remains in AdvanceCancellation.
            _reservation?.Retire();
        }

        internal StrictCombatEquipmentStatus Advance()
        {
            if (Environment.CurrentManagedThreadId != _thread) return StrictCombatEquipmentStatus.WrongThread;
            if (_executing) return StrictCombatEquipmentStatus.Busy;
            if (IsFinished) return Status;
            _executing = true;
            try
            {
                if (_phase == Phase.Cancelling) return AdvanceCancellation();
                RequireOperation();
                if (!_started)
                {
                    _started = true;
                    Prepare();
                }
                if (_phase == Phase.Loading)
                {
                    var status = _renderer.Read(out _loaded);
                    if (status == StrictCombatEquipmentRendererStatus.Pending) return Status;
                    Require(status == StrictCombatEquipmentRendererStatus.Ready);
                    InitializePresentation();
                    _phase = Phase.Settling;
                }
                if (_phase == Phase.Settling)
                {
                    RequirePreparation();
                    Require(_handoff.TryAdvancePose(out bool settled));
                    if (!settled) return Status;
                    _phase = Phase.Committing;
                    Require(_handoff.TryNotifyInitialized() && _handoff.TryCommit());
                    Require(_activation.TryEnableController(_handoff));
                    Require(Source.Owner.TryAdoptEquipment(this));
                    _phase = Phase.Active;
                }
                RequirePreparation();
                return Status;
            }
            catch (Exception)
            {
                // Context loss before an attempted mutation does not establish damage.
                // Each child retains its own attempted/uncertain result for cancellation.
                _uncertain |= _transfer?.IsUncertain == true || _stage?.IsUncertain == true
                    || _references?.IsUncertain == true || _handoff?.IsUncertain == true
                    || _activation?.IsUncertain == true || _appearance?.IsUncertain == true
                    || _animation?.IsUncertain == true || _controller?.IsUncertain == true
                    || _events?.IsUncertain == true;
                RequestCancellation();
                return Status;
            }
            finally { _executing = false; }
        }

        private void Prepare()
        {
            RequireOperation();
            Require(Source.Owner.TryRead(out var binding, out var state));
            Require(StrictCombatEquipmentTransfer.TryCapture(Source, RequireOperation,
                in binding, in state, out _transfer));
            var data = Source.Item._data;
            var definition = Source.Props.GetPropDefinitionForItem(355, null);
            Require(definition is not null && !definition.WasCollected && Live(definition._heldLocator));
            var parent = definition._heldLocator.parent;
            Require(Live(parent));
            Require(StrictCombatEquipmentPrefab.TryCreate(data, definition, parent, RequireOperation, out _stage));
            Require(_transfer.Take() == StrictCombatEquipmentTransferResult.Taken);
            Require(StrictCombatWeaponHooks.TryReserveEquipment(Source, _stage, _transfer, out _reservation));
            Require(StrictCombatEquipmentBinding.TryCapture(_reservation, RequireOperation, out _references)
                && _references.TryBind());
            Require(StrictCombatEquipmentHandoff.TryCapture(_reservation, RequireOperation, out _handoff));
            Require(StrictCombatEquipmentActivation.TryCapture(_reservation, RequireOperation,
                CanReleaseRenderer, out _activation));
            _renderer = new StrictCombatEquipmentRenderer(_reservation, RequireOperation);
            Require(_handoff.TryRegisterObject() && _handoff.TryApplyPose());
            Require(_activation.TryActivate());
            _phase = Phase.Loading;
        }

        private void InitializePresentation()
        {
            RequireOperation();
            Require(StrictCombatEquipmentController.TryCapture(_reservation, RequireOperation, out _controller));
            Require(StrictCombatEquipmentAppearance.TryCapture(_reservation, _renderer, RequireOperation, out _appearance)
                && _appearance.Apply());
            Require(StrictCombatEquipmentAnimation.TryCapture(_reservation, _renderer, RequireOperation,
                CanAnimate, out _animation) && _animation.TryLink());
            Require(_controller.TryInitialize());
            var mapping = GameInput._sonsInputMapping;
            Require(mapping is not null && !mapping.WasCollected && mapping.m_default is not null);
            _events = new StrictCombatEquipmentEvents(_reservation.Controller,
                Source.Props._playerAnimEventReceiver, mapping.m_default_Reload, mapping.m_default_SecondaryAction,
                RequireOperation, CanDispatch, RequestCancellation);
            Require(_events.TryRegister());
            Require(_controller.TryPlayEquipAudio());
        }

        private bool CanAnimate()
        {
            if (_phase != Phase.Settling && _phase != Phase.Committing && _phase != Phase.Active) return false;
            try { RequireOperation(); return true; }
            catch (Exception) { RequestCancellation(); return false; }
        }

        private bool CanDispatch()
        {
            if (!IsActive || _executing) return false;
            try
            {
                RequireOperation();
                return Source.Owner.IsAdoptedEquipmentCurrent(this);
            }
            catch (Exception) { RequestCancellation(); return false; }
        }

        private void RequirePreparation()
        {
            RequireOperation();
            Require(_references.IsBound && _references.ReferencesCurrent()
                && _activation.ReferencesCurrent()
                && _handoff.ReferencesCurrent()
                && _controller.IsInitialized && _controller.ReferencesCurrent()
                && _appearance.IsApplied && _appearance.ReferencesCurrent()
                && _animation.IsLinked && _animation.ReferencesCurrent() && _events.IsRegistered);
        }

        // This validator reads only owner/context fields. Calling child validators here
        // would recurse from native enable/load callbacks and from the child operations.
        private void RequireOperation()
        {
            Require(Environment.CurrentManagedThreadId == _thread && _phase != Phase.Finished
                && Source.Owner.IsEquipmentOperationCurrent(this)
                && Source.Owner.IsCurrentNativeHealthOwner(Source.Life));
            Require(Source.Owner.TryRead(out var binding, out var state));
            Require(binding.SameBinding(in _binding) && state.LifeGeneration == _life
                && Source.Life.LifeGeneration == _life && state.OwnsPistol);
            if (_phase != Phase.Cancelling)
                Require(!_uncertain && state.LifeStage == StrictCombatLifeStage.Alive && state.Equipped);
            else
                Require(state.LifeStage == StrictCombatLifeStage.Alive
                    || state.LifeStage == StrictCombatLifeStage.Downed || state.LifeStage == StrictCombatLifeStage.Dead);
            var inventory = Source.Inventory;
            Require(Live(inventory) && Same(inventory, LocalPlayer._Inventory_k__BackingField)
                && Same(inventory, Source.Life.Stats._inventory)
                && Same(Source.Props, inventory._InventoryProps_k__BackingField)
                && inventory.isActiveAndEnabled && inventory._currentView == PlayerInventory.PlayerViews.World
                && inventory._currentEquipState == PlayerInventory.HandEquipState.Idle
                && inventory._equipSequenceRight is null && !inventory._IsThrowing_k__BackingField
                && !inventory._IsInQuickSelect_k__BackingField);
            var slots = inventory._equipmentSlots;
            var next = inventory._equipmentSlotsNext;
            Require(slots is not null && !slots.WasCollected && slots.Length > (int)EquipmentSlot.LeftHand
                && next is not null && !next.WasCollected && next.Length <= 32
                && slots[(int)EquipmentSlot.LeftHand] is null);
            var right = slots[(int)EquipmentSlot.RightHand];
            Require(right is null || ((_phase == Phase.Committing || _phase == Phase.Active
                || _phase == Phase.Cancelling) && Same(right, Source.Item)));
            foreach (var item in next) Require(item is null);
        }

        private StrictCombatEquipmentStatus AdvanceCancellation()
        {
            if (_events?.IsDispatching == true || _animation?.IsDispatching == true) return Status;
            try
            {
                if (_activation is not null)
                {
                    if (!_activation.Retire())
                    { _uncertain |= _activation.IsUncertain; return Status; }
                }
                else _reservation?.Retire();
                if (!_eventsDetached)
                {
                    _eventsDetached = _events is null || _events.TryDetach();
                    if (!_eventsDetached) _cleanupFailed = true;
                }
                if (!_animationDetachAttempted)
                {
                    _animationDetachAttempted = true;
                    if (_animation is not null && !_animation.TryDetach()) _cleanupFailed = true;
                }
                if (!_controllerDetached)
                {
                    _controllerDetached = _controller is null || _controller.TryDetach();
                    if (!_controllerDetached) _cleanupFailed = true;
                }
                if (_cleanupFailed) { _uncertain = true; return StrictCombatEquipmentStatus.MutationUncertain; }
                if (_animation is not null && !_animation.IsDetached) return Status;
                if (_activation is not null)
                {
                    if (!_activation.ReleaseAttempted && !_activation.TryRelease())
                    {
                        _uncertain |= _activation.IsUncertain;
                        return Status;
                    }
                    if (!_activation.IsReferenceReleased) return Status;
                }
                if (!_inventoryCancelled)
                {
                    if (_handoff is not null && !_handoff.TryCancel(RequireOperation, out _))
                    {
                        _uncertain = true;
                        _returnAttempted = true;
                    }
                    _inventoryCancelled = true;
                }
                if (!_returnAttempted)
                {
                    _returnAttempted = true;
                    if (_transfer?.IsTaken == true)
                        _returned = _transfer.ReturnForCancellation() == StrictCombatEquipmentTransferResult.Returned;
                    else _returned = _transfer is null || !_transfer.IsUncertain;
                    if (!_returned) _uncertain = true;
                }
                if (!_destroyAttempted)
                {
                    _destroyAttempted = true;
                    if (_stage is not null && Live(_stage.Root))
                    {
                        // The root was created for this operation; native rendering can
                        // produce a sibling of the held clone beneath this same root.
                        if (Live(_stage.Clone))
                            Require(Same(_stage.Clone.transform.parent, _stage.Root.transform));
                        if (Live(_loaded))
                            Require(_loaded.transform.IsChildOf(_stage.Root.transform));
                        UnityEngine.Object.Destroy(_stage.Root);
                    }
                }
                if ((_stage is not null && (Live(_stage.Root) || Live(_stage.Clone))) || Live(_loaded)) return Status;
                _phase = Phase.Finished;
                return Status;
            }
            catch (Exception)
            {
                _uncertain = true;
                return StrictCombatEquipmentStatus.MutationUncertain;
            }
        }

        private bool CanReleaseRenderer()
            => _phase == Phase.Cancelling && _eventsDetached && _controllerDetached
                && !_cleanupFailed && (_animation is null || _animation.IsDetached);

        private static bool Live(UnityEngine.Object value) => value is not null && !value.WasCollected && value;
        private static bool Same(Il2CppObjectBase first, Il2CppObjectBase second)
            => first is not null && second is not null && !first.WasCollected && !second.WasCollected
                && first.Pointer == second.Pointer;
        private static void Require(bool value)
        {
            if (!value) throw new InvalidOperationException("The owned equipment transaction changed.");
        }
    }
}
