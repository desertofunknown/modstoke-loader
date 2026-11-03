// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Animation;
using global::Sons.Weapon;
using UnityEngine.InputSystem;

namespace Modstoke.Sons.Combat
{
    // Captures one equipment lifetime. Failed mutations keep their delegates and
    // attempted flags so an explicit detach can address those exact sources. The dispatch
    // predicate must allow native attack-state transitions while checking the same life,
    // completed weapon and renderer; an idle-only predicate would reject AttackStart.
    internal sealed class StrictCombatEquipmentEvents
    {
        private readonly RangedWeaponController _controller;
        private readonly AnimationEventReceiver _receiver;
        private readonly InputAction _reload, _secondary;
        private readonly Action _requireOperation, _retire;
        private readonly Func<bool> _canDispatch;
        private readonly int _ownerThread;
        private readonly Il2CppSystem.Action _rangeReload, _rangeShoot, _attackStart,
            _attackEnd, _leavesHand, _showRenderable, _hideRenderable;
        private readonly Il2CppSystem.Action<InputAction.CallbackContext> _reloadAction, _secondaryAction;
        private int _attempted;
        private bool _registered, _dispatching, _retired;

        internal StrictCombatEquipmentEvents(RangedWeaponController controller,
            AnimationEventReceiver receiver, InputAction reload, InputAction secondary,
            Action requireOperation, Func<bool> canDispatch, Action retire)
        {
            _controller = controller;
            _receiver = receiver;
            _reload = reload;
            _secondary = secondary;
            _requireOperation = requireOperation ?? throw new ArgumentNullException(nameof(requireOperation));
            _canDispatch = canDispatch ?? throw new ArgumentNullException(nameof(canDispatch));
            _retire = retire ?? throw new ArgumentNullException(nameof(retire));
            _ownerThread = Environment.CurrentManagedThreadId;
            _rangeReload = CreateCallback(() => _controller.RangeReloadCallback());
            _rangeShoot = CreateCallback(() => _controller.RangeShootCallback());
            _attackStart = CreateCallback(() => _controller.AttackStartCallback());
            _attackEnd = CreateCallback(() => _controller.AttackEndCallback());
            _leavesHand = CreateCallback(() => _controller.OnItemLeavesHandCallback());
            _showRenderable = CreateCallback(() => _controller.OnItemShowRenderable());
            _hideRenderable = CreateCallback(() => _controller.OnItemHideRenderable());
            _reloadAction = (Action<InputAction.CallbackContext>)(context =>
                Dispatch(() => _controller.OnReloadAction(context)));
            _secondaryAction = (Action<InputAction.CallbackContext>)(context =>
                Dispatch(() => _controller.OnSecondaryAction(context)));
        }

        internal bool RegistrationAttempted { get; private set; }
        internal bool DetachAttempted { get; private set; }
        internal bool IsUncertain { get; private set; }
        internal bool IsRegistered => _registered && !DetachAttempted && !IsUncertain;
        internal bool IsDispatching => _dispatching;

        internal bool TryRegister()
        {
            if (Environment.CurrentManagedThreadId != _ownerThread
                || RegistrationAttempted || DetachAttempted || IsUncertain) return false;
            RegistrationAttempted = true;
            try
            {
                for (int index = 0; index < 9; index++)
                {
                    RequireRegistrationSources();
                    _attempted |= 1 << index;
                    Add(index);
                    RequireRegistrationSources();
                }
                _registered = true;
                return true;
            }
            catch (Exception)
            {
                Retire();
                return false;
            }
        }

        internal bool TryDetach()
        {
            if (Environment.CurrentManagedThreadId != _ownerThread || DetachAttempted) return false;
            DetachAttempted = true;
            _registered = false;
            bool complete = true;
            for (int index = 0; index < 9; index++)
            {
                if ((_attempted & (1 << index)) == 0) continue;
                try
                {
                    // Cleanup deliberately does not require the adopted weapon to
                    // remain current; only the exact subscription source must live.
                    RequireDetachSource(index);
                    Remove(index);
                    RequireDetachSource(index);
                }
                catch (Exception)
                {
                    complete = false;
                    Retire();
                }
            }
            return complete;
        }

        private void RequireRegistrationSources()
        {
            _requireOperation();
            if (DetachAttempted || IsUncertain || !SourcesCurrent()
                || !Live(_reload) || !Live(_secondary))
                throw new InvalidOperationException("The equipment event sources changed.");
        }

        private void RequireDetachSource(int index)
        {
            if (index < 7 ? !Live(_receiver) : !Live(index == 7 ? _reload : _secondary))
                throw new InvalidOperationException("The equipment event source is unavailable.");
        }

        private bool SourcesCurrent()
        {
            if (!Live(_controller) || !Live(_receiver)) return false;
            AnimationEventReceiver current = _controller._receiver;
            return Live(current) && current.Pointer == _receiver.Pointer;
        }

        private Il2CppSystem.Action CreateCallback(Action handler)
            => (Action)(() => Dispatch(handler));

        private void Dispatch(Action handler)
        {
            if (Environment.CurrentManagedThreadId != _ownerThread
                || !IsRegistered || _dispatching) return;
            _dispatching = true;
            try
            {
                if (!_canDispatch() || !IsRegistered || !SourcesCurrent()) return;
                handler();
                if (!_canDispatch() || !SourcesCurrent()) Retire();
            }
            catch (Exception) { Retire(); }
            finally { _dispatching = false; }
        }

        private void Retire()
        {
            _registered = false;
            IsUncertain = true;
            if (_retired) return;
            _retired = true;
            try { _retire(); }
            catch (Exception) { }
        }

        private void Add(int index)
        {
            switch (index)
            {
                case 0: _receiver.add_OnRangeReload(_rangeReload); break;
                case 1: _receiver.add_OnRangeShoot(_rangeShoot); break;
                case 2: _receiver.add_OnAttackStart(_attackStart); break;
                case 3: _receiver.add_OnAttackEnd(_attackEnd); break;
                case 4: _receiver.add_OnItemLeavesHand(_leavesHand); break;
                case 5: _receiver.add_OnItemShowRenderable(_showRenderable); break;
                case 6: _receiver.add_OnItemHideRenderable(_hideRenderable); break;
                case 7: _reload.add_performed(_reloadAction); break;
                case 8: _secondary.add_performed(_secondaryAction); break;
            }
        }

        private void Remove(int index)
        {
            switch (index)
            {
                case 0: _receiver.remove_OnRangeReload(_rangeReload); break;
                case 1: _receiver.remove_OnRangeShoot(_rangeShoot); break;
                case 2: _receiver.remove_OnAttackStart(_attackStart); break;
                case 3: _receiver.remove_OnAttackEnd(_attackEnd); break;
                case 4: _receiver.remove_OnItemLeavesHand(_leavesHand); break;
                case 5: _receiver.remove_OnItemShowRenderable(_showRenderable); break;
                case 6: _receiver.remove_OnItemHideRenderable(_hideRenderable); break;
                case 7: _reload.remove_performed(_reloadAction); break;
                case 8: _secondary.remove_performed(_secondaryAction); break;
            }
        }

        private static bool Live(UnityEngine.Object value)
            => value is not null && !value.WasCollected && value;

        private static bool Live(InputAction value)
            => value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero;
    }
}
