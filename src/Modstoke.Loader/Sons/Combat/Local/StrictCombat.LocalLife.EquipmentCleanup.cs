// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Animation;
using global::Sons.Gui;
using global::Sons.Input;
using global::Sons.Weapon;
using UnityEngine;
using UnityEngine.InputSystem;
using GameInput = global::Sons.Input.InputSystem;

namespace Modstoke.Sons.Combat
{
    internal sealed partial class StrictCombatLocalLife
    {
        private sealed class EquipmentCleanup
        {
            private readonly StrictCombatLocalLife _life;
            private readonly StrictCombatLocalWeapon _weapon;
            private readonly RangedWeaponController _controller;
            private readonly Animator _animator;
            private readonly PlayerAnimatorControl _animation;
            private readonly AnimationEventReceiver _receiver;
            private readonly SonsInputMapping _mapping;
            private readonly InputActionMap _defaultMap;
            private readonly InputAction _reload, _secondary;
            private readonly Il2CppSystem.Collections.Generic.List<IHitReactReceiver> _hitReceivers;
            private readonly IHitReactReceiver _hitReceiver;
            private readonly GameObject _aimStimuli;
            private readonly HudGui _hud;
            private readonly PlayerTutorials _tutorials;
            private readonly GameObject _reloading;
            private readonly UiManager _ui;
            private readonly Il2CppSystem.Collections.Generic.List<UiElement> _elements;
            private readonly UiElement _swapAmmo;
            private readonly GameObject _swapTarget;
            private readonly bool _registered;
            private bool _attempted, _removed;

            internal EquipmentCleanup(StrictCombatLocalLife life, StrictCombatLocalWeapon weapon)
            {
                _life = life;
                _weapon = weapon;
                _controller = weapon.Controller;
                _animator = _controller._playerAnimator;
                _animation = _controller._playerAnimatorControl;
                _receiver = _controller._receiver;
                _mapping = GameInput._sonsInputMapping;
                _defaultMap = _mapping.m_default;
                _reload = _mapping.m_default_Reload;
                _secondary = _mapping.m_default_SecondaryAction;
                _hitReceivers = life._stats._hitReactReceivers;
                _hitReceiver = _controller.Cast<IHitReactReceiver>();
                int registrations = CountHitRegistrations();
                if (registrations > 1)
                    throw new InvalidOperationException("The held controller has duplicate hit-react registrations.");
                _registered = registrations == 1;
                _aimStimuli = life._stimuli._playerAimStimuli;
                _hud = HudGui._instance;
                if (_hud != null)
                {
                    _tutorials = _hud._tutorials;
                    if (_tutorials != null) _reloading = _tutorials._reloading;
                }
                _ui = UiManager._instance;
                if (_ui != null)
                {
                    _elements = _ui._elements;
                    _swapAmmo = _ui.FindElement("screen.swapAmmo", null);
                    if (_swapAmmo != null) _swapTarget = _swapAmmo._target;
                }
                RequireCurrent();
            }

            internal void RequireCurrent()
            {
                _life.RequireCurrent();
                if (!Same(_controller, _weapon.Controller)
                    || !Same(_animator, _controller._playerAnimator) || !Same(_animator, _life._animator)
                    || !Same(_animation, _controller._playerAnimatorControl) || !Same(_animation, _life._animation)
                    || !Same(_receiver, _controller._receiver)
                    || _controller._firstLookIsRunning || _controller._itemWasThrown
                    || !Same(_mapping, GameInput._sonsInputMapping)
                    || !Same(_defaultMap, _mapping.m_default)
                    || !Same(_reload, _mapping.m_default_Reload)
                    || !Same(_secondary, _mapping.m_default_SecondaryAction)
                    || !Same(_hitReceivers, _life._stats._hitReactReceivers)
                    || !Same(_hitReceiver, _controller)
                    || CountHitRegistrations() != (_registered && !_removed ? 1 : 0)
                    || !SameOptional(_aimStimuli, _life._stimuli._playerAimStimuli)
                    || !SameOptional(_hud, HudGui._instance)
                    || !SameOptional(_ui, UiManager._instance))
                    throw new InvalidOperationException("Equipment cleanup lost its captured sources.");
                if (_hud != null && (!SameOptional(_tutorials, _hud._tutorials)
                    || (_tutorials != null && !SameOptional(_reloading, _tutorials._reloading))))
                    throw new InvalidOperationException("The captured reload tutorial changed.");
                if (_ui != null)
                {
                    if (!Same(_elements, _ui._elements))
                        throw new InvalidOperationException("The captured UI registrations changed.");
                    var swapAmmo = _ui.FindElement("screen.swapAmmo", null);
                    if (_swapAmmo is null ? swapAmmo is not null
                        : !Same(_swapAmmo, swapAmmo) || !SameOptional(_swapTarget, _swapAmmo._target))
                        throw new InvalidOperationException("The captured ammunition prompt changed.");
                }
                _life.RequireCurrent();
            }

            internal void Execute(Action<Action> change)
            {
                RequireCurrent();
                if (_attempted) throw new InvalidOperationException("Equipment cleanup was already attempted.");
                _attempted = true;
                // Native delegate removal preserves the original target and method identity.
                // The caller guards both sides of each operation and suppresses deferred
                // controller cleanup before any interruption or destruction callbacks.
                change(() => _controller.UnregisterEvents());
                change(() => _animator.SetBool(AnimationHashes.EmptyWeaponHash, false));
                change(() => _animator.SetBool(AnimationHashes.attackHash, false));
                change(() => _animator.SetBool(AnimationHashes.drawBowBoolHash, false));
                change(() => _controller.SetIsReloading(false));
                change(() => _controller.SetClampedSpine(false));
                // Hide the captured targets immediately. Native UI helpers can reparent
                // through callbacks or start a tutorial slide-out coroutine.
                if (_swapTarget != null) change(() => _swapTarget.SetActive(false));
                if (_reloading != null) change(() => _reloading.SetActive(false));
                if (_controller._shouldSuppressLeftHandFirstLook)
                    change(() => _life._inventory._suppressLeftHandFirstLookAts = false);
                if (_aimStimuli != null) change(() => _life._stimuli.DisableAimStimuli());
                change(() => GameInput.SetRangedWeaponAimingState(false));
                change(() => _controller.StopAllCoroutines());
                change(() =>
                {
                    bool removed = _hitReceivers.Remove(_hitReceiver);
                    _removed = true;
                    if (removed != _registered || CountHitRegistrations() != 0)
                        throw new InvalidOperationException("The held controller's hit-react removal could not be confirmed.");
                });
            }

            private int CountHitRegistrations()
            {
                int count = 0;
                for (int i = 0; i < _hitReceivers.Count; i++)
                    if (Same(_hitReceiver, _hitReceivers[i])) count++;
                return count;
            }

            private static bool SameOptional(UnityEngine.Object captured, UnityEngine.Object current)
                => captured is null ? current is null : Same(captured, current);
        }
    }
}
