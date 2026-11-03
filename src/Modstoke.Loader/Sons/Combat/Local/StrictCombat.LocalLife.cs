// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using global::Sons.Ai.Vail.StimuliTypes;
using global::Sons.Animation;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    internal sealed partial class StrictCombatLocalLife
    {
        private enum Phase { Alive, Downed, GetUpDelay, GetUpTag, Blend }
        private readonly StrictCombatLifeBarrier _barrier;
        private readonly PlayerStats _stats;
        private readonly PlayerInventory _inventory;
        private readonly PlayerAnimatorControl _animation;
        private readonly Animator _animator;
        private readonly FirstPersonCharacter _movement;
        private readonly SimpleMouseRotator _bodyRotation, _cameraRotation;
        private readonly camFollowHead _camera;
        private readonly PlayerStimuli _stimuli;
        private readonly Rigidbody _rigidbody;
        private readonly BoltEntity _entity;
        private readonly IPlayerState _networkState;
        private readonly Il2CppReferenceArray<ILayerBehaviourActivator> _reviveLayers;
        private Phase _phase;
        private StrictCombatBinding _binding;
        private StrictCombatSnapshot _accepted;
        private int _frame, _blendFrame = -1;
        private float _now, _lastTime, _started, _blend;
        private bool _wrote, _layersRegistered, _deathBoolCleared, _controlsRepeated;

        private StrictCombatLocalLife(StrictCombatLifeBarrier barrier)
        {
            _barrier = barrier;
            _stats = barrier.Stats;
            _inventory = LocalPlayer._Inventory_k__BackingField;
            _animation = LocalPlayer._AnimControl_k__BackingField;
            _animator = LocalPlayer._Animator_k__BackingField;
            _movement = LocalPlayer._FpCharacter_k__BackingField;
            _bodyRotation = LocalPlayer._MainRotator_k__BackingField;
            _cameraRotation = LocalPlayer._CamRotator_k__BackingField;
            _camera = LocalPlayer._CamFollowHead_k__BackingField;
            _stimuli = LocalPlayer._PlayerStimuli_k__BackingField;
            _entity = LocalPlayer.Entity;
            _networkState = _entity.GetState<IPlayerState>();
            _rigidbody = _animation.controller;
            _reviveLayers = _stats._getupLayerBehaviourActivators;
            _lastTime = Time.time;
        }

        internal bool OwnedBy(StrictCombatLifeBarrier barrier) => ReferenceEquals(_barrier, barrier);

        internal static bool TryCapture(StrictCombatLifeBarrier barrier, out StrictCombatLocalLife life)
        {
            life = null;
            try
            {
                if (!barrier.Owner.IsCurrentNativeHealthOwner(barrier)) return false;
                int frame = Time.frameCount;
                var candidate = new StrictCombatLocalLife(barrier);
                if (!candidate.ControlsCurrent() || !float.IsFinite(candidate._lastTime)
                    || candidate._stats._dyingState != PlayerStats.DyingState.Alive
                    || candidate._stats._playerIsDowned || candidate._stats._healing
                    || candidate._stats._isHealing || candidate._stats._respawning
                    || candidate._animator.layerCount <= 6 || candidate._reviveLayers == null
                    || candidate._reviveLayers.WasCollected || frame != Time.frameCount)
                    return false;
                life = candidate;
                return true;
            }
            catch (Exception) { return false; }
        }

        internal StrictCombatLocalLifeStatus Advance(in StrictCombatBinding binding, in StrictCombatSnapshot state)
        {
            _binding = binding;
            _accepted = state;
            _frame = Time.frameCount;
            _now = Time.time;
            _wrote = false;
            try
            {
                RequireCurrent();
                if (!float.IsFinite(_now) || _now < _lastTime) throw new InvalidOperationException();
                _lastTime = _now;
                // Accepted health does not depend on whether equipment or animation
                // can enter the requested pose. The pose still has its own checks.
                var health = _barrier.Owner.ProjectLocalHealth();
                if (health == StrictCombatHealthProjectionStatus.Applied) _wrote = true;
                else if (health != StrictCombatHealthProjectionStatus.AlreadyCurrent)
                {
                    if (health == StrictCombatHealthProjectionStatus.MutationUncertain)
                        return StrictCombatLocalLifeStatus.MutationUncertain;
                    return StrictCombatLocalLifeStatus.Unavailable;
                }
                RequireCurrent();
                bool incapacitated = state.LifeStage is StrictCombatLifeStage.Downed or StrictCombatLifeStage.Dead;
                if ((incapacitated || _phase != Phase.Alive)
                    && (_movement._isAnimControllerSwimming || _animation._knockdownTime > 0f
                        || _movement._activeHandlingLanded))
                    return StrictCombatLocalLifeStatus.UnsupportedContext;
                if (incapacitated && _phase == Phase.Alive && _animation.doingFullBodyAction)
                    return StrictCombatLocalLifeStatus.UnsupportedContext;
                if (incapacitated && _phase != Phase.Downed && !TryPrepareEquipmentStash())
                    return StrictCombatLocalLifeStatus.UnsupportedContext;
                if (incapacitated)
                {
                    if (_phase != Phase.Downed)
                    {
                        BeginDowned();
                        _phase = Phase.Downed;
                        _started = _now;
                        _deathBoolCleared = _controlsRepeated = false;
                    }
                    if (!_deathBoolCleared && _now - _started >= .5f)
                    {
                        Change(() => _animator.SetBool(AnimationHashes.deathBoolHash, false));
                        _deathBoolCleared = true;
                    }
                    if (!_controlsRepeated && _now - _started >= 1f)
                    {
                        DisableControls();
                        Change(() => _camera.SetFollowAnim(true));
                        Change(() => _animator.ResetTrigger(AnimationHashes.knockBackTriggerHash));
                        _controlsRepeated = true;
                    }
                    RequireCurrent();
                    if (!DownedMatches()) throw new InvalidOperationException();
                    RequireCurrent();
                    if (state.LifeStage == StrictCombatLifeStage.Dead)
                        return StrictCombatLocalLifeStatus.RespawnRequired;
                    return !_controlsRepeated ? StrictCombatLocalLifeStatus.Pending
                        : _wrote ? StrictCombatLocalLifeStatus.Presented : StrictCombatLocalLifeStatus.AlreadyCurrent;
                }
                if (state.LifeStage != StrictCombatLifeStage.Alive)
                    return StrictCombatLocalLifeStatus.UnsupportedContext;
                if (_phase == Phase.Downed)
                {
                    BeginGetUp();
                    _phase = Phase.GetUpDelay;
                    _started = _now;
                }
                if (_phase == Phase.GetUpDelay && _now - _started >= 1f)
                {
                    _phase = Phase.GetUpTag;
                    _started = _now;
                }
                if (_phase == Phase.GetUpTag)
                {
                    bool finished = _now - _started >= 5f;
                    if (!finished)
                    {
                        finished = _animator.GetCurrentAnimatorStateInfo(4).tagHash != AnimationHashes.getupHash;
                        RequireCurrent();
                    }
                    if (finished)
                    {
                        FinishGetUp();
                        _phase = Phase.Blend;
                        _blend = Mathf.Clamp01(_animator.GetLayerWeight(6));
                        RequireCurrent();
                        _blendFrame = -1;
                    }
                }
                if (_phase == Phase.Blend && _blendFrame != _frame)
                {
                    float elapsed = Time.deltaTime;
                    if (!float.IsFinite(elapsed) || elapsed < 0f || !float.IsFinite(_blend))
                        throw new InvalidOperationException();
                    _blend += 2f * elapsed;
                    Change(() => _animator.SetLayerWeight(6, Mathf.Clamp01(_blend)));
                    _blendFrame = _frame;
                    if (_blend >= 1f)
                    {
                        if (!AliveMatches() || _animator.GetLayerWeight(6) != 1f)
                            throw new InvalidOperationException();
                        RequireCurrent();
                        _phase = Phase.Alive;
                    }
                }
                RequireCurrent();
                return _phase != Phase.Alive ? StrictCombatLocalLifeStatus.Pending
                    : _wrote ? StrictCombatLocalLifeStatus.Presented : StrictCombatLocalLifeStatus.AlreadyCurrent;
            }
            catch (Exception)
            {
                _barrier.Retire();
                // No cleanup can safely run against a player whose identity may have changed.
                return _wrote ? StrictCombatLocalLifeStatus.MutationUncertain : StrictCombatLocalLifeStatus.Unavailable;
            }
        }

        private void Change(Action action)
        {
            RequireCurrent();
            _wrote = true;
            action();
            RequireCurrent();
        }

        private void RequireCurrent()
        {
            if (Time.frameCount != _frame || !ControlsCurrent()
                || !_barrier.Owner.TryRead(out var binding, out var state)
                || !binding.SameBinding(in _binding)
                || state.Revision != _accepted.Revision || state.LifeGeneration != _accepted.LifeGeneration)
                throw new InvalidOperationException("The local life operation lost its captured context.");
        }

        private bool ControlsCurrent()
            => _barrier.Owner.IsCurrentNativeHealthOwner(_barrier)
                && Same(_inventory, LocalPlayer._Inventory_k__BackingField) && Same(_inventory, _stats._inventory)
                && Same(_animation, LocalPlayer._AnimControl_k__BackingField)
                && Same(_animator, LocalPlayer._Animator_k__BackingField) && Same(_animator, _stats._animator)
                && Same(_animator, _animation._animator)
                && Same(_movement, LocalPlayer._FpCharacter_k__BackingField)
                && Same(_bodyRotation, LocalPlayer._MainRotator_k__BackingField)
                && Same(_cameraRotation, LocalPlayer._CamRotator_k__BackingField)
                && Same(_camera, LocalPlayer._CamFollowHead_k__BackingField) && Same(_camera, _stats._cameraFollow)
                && Same(_stimuli, LocalPlayer._PlayerStimuli_k__BackingField)
                && Same(_rigidbody, _animation.controller) && Same(_rigidbody, _movement._rigidbody)
                && Same(_entity, LocalPlayer.Entity) && Same(_networkState, _entity.GetState<IPlayerState>())
                && Same(_reviveLayers, _stats._getupLayerBehaviourActivators) && TimerUnchanged();

        private bool TimerUnchanged()
        {
            var current = _stats._mpRespawnTimerGui;
            return _barrier.Timer is null ? current is null
                : Same(_barrier.Timer, current) && !_barrier.Timer.gameObject.activeInHierarchy;
        }

        private bool DownedMatches()
            => _stats._dyingState == PlayerStats.DyingState.Dying && _networkState.Dying == 1
                && _stats._playerIsDowned && _barrier.Vitals._hasDied && !_stats._healing
                && _inventory.RightHandItem == null && _inventory.LeftHandItem == null
                && !_inventory.enabled && !_movement.enabled && !_bodyRotation.enabled && !_cameraRotation.enabled;

        private bool AliveMatches()
            => _stats._dyingState == PlayerStats.DyingState.Alive && _networkState.Dying == 0
                && !_stats._playerIsDowned && !_barrier.Vitals._hasDied && !_stats._healing
                && _inventory.enabled && _movement.enabled && _bodyRotation.enabled && _cameraRotation.enabled;

        private static bool Same(Il2CppObjectBase first, Il2CppObjectBase second)
            => first != null && second != null && !first.WasCollected && !second.WasCollected
                && first.Pointer == second.Pointer;
        private static bool Same(UnityEngine.Object first, UnityEngine.Object second)
            => first != null && second != null && !first.WasCollected && !second.WasCollected
                && first && second && first.Pointer == second.Pointer;
    }
}
