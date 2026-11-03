// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using global::Sons.Animation;
using global::Sons.Items.Core;
using TheForest.Items.Utils;
using TheForest.Utils.Items;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    internal sealed partial class StrictCombatEquipmentHandoff
    {
        private bool _cancelAttempted, _cancelMutationAttempted, _cancelled;
        private bool _poseReversible, _poseReversed, _widePoseCleared;
        private float _ownedWidePose;
        private RuntimeAnimatorController _poseController;
        private Il2CppStructArray<AnimatorVariables> _poseVariables;
        private Il2CppStructArray<int> _animationHashes;
        private AnimatorVariables[] _copiedPoseVariables;
        private int[] _poseVariableHashes;
        private (int Hash, AnimatorControllerParameterType Type)[] _poseParameters;

        internal bool IsCancelled => _cancelled && !_uncertain;

        // The caller first gates input and detaches its owned callbacks. Its cancellation
        // validator must be read-only and must not call this handoff's normal validator:
        // retirement of the prepared reservation is expected, retirement of its life is not.
        // Success establishes empty hands/map, not item return or object destruction.
        internal bool TryCancel(Action requireCancellation, out bool uncertain)
        {
            uncertain = _uncertain;
            if (_cancelled) return !_uncertain;
            if (_cancelAttempted || _mutating || requireCancellation == null) return false;
            _cancelAttempted = true;
            if (_uncertain || (_commitAttempted && !_committed))
            {
                _uncertain = true;
                uncertain = true;
                return false;
            }
            _mutating = true;
            try
            {
                RequireCancellationLife();
                requireCancellation();
                RequireCancellationLife();
                if (!_reservation.Source.Owner.TryRead(out _, out var accepted))
                    throw new InvalidOperationException("Accepted equipment state is unavailable.");
                _revision = accepted.Revision;
                _frame = Time.frameCount;
                RequireCancellation(requireCancellation);
                // A trigger cannot be read back or safely undone by inventing ResetTrigger.
                // Reject the complete cancellation before removing any effects or objects.
                if (_poseApplied && !_poseReversible)
                {
                    _uncertain = true;
                    uncertain = true;
                    return false;
                }
                if (_committed)
                    foreach (var effect in _copiedEffects)
                        CancelChange(requireCancellation, () => ItemUtils.ApplyEffectsToPlayerStats(effect, false));
                if (_poseApplied)
                {
                    CancelChange(requireCancellation, () =>
                    {
                        // Native removal checks the animator's held ID, not the slot.
                        // It keeps RightHandHeldItemIdFloat at 355, matching native stash.
                        ItemAnimatorHashHelper.ApplyAnimVars(_data, false, _animator, null);
                        _poseReversed = true;
                    });
                    CancelChange(requireCancellation, () =>
                    {
                        _animator.SetFloat(AnimationHashes.LeftHandWidePoseBlendHash, 0f);
                        _widePoseCleared = true;
                    });
                }
                if (_registered)
                {
                    CancelChange(requireCancellation, () =>
                    {
                        if (!_objects.TryGetValue(355, out var current) || !Same(_object, current)
                            || !_objects.Remove(355))
                            throw new InvalidOperationException("The captured held object could not be removed.");
                        _registered = false;
                    });
                    if (_removed is not null)
                        CancelChange(requireCancellation, () => _removed.Invoke(355, false));
                }
                if (_slotWritten)
                    CancelChange(requireCancellation, () =>
                    {
                        if (!Same(_item, _slots[(int)EquipmentSlot.RightHand]))
                            throw new InvalidOperationException("The captured equipped item changed.");
                        _slots[(int)EquipmentSlot.RightHand] = null;
                        _slotWritten = false;
                    });
                if (_committed && _unequipped is not null)
                    CancelChange(requireCancellation, () => _unequipped.Invoke(_item, 355));
                RequireCancellation(requireCancellation);
                _cancelled = true;
                return true;
            }
            catch (Exception)
            {
                if (_cancelMutationAttempted)
                {
                    _uncertain = true;
                    _reservation.Retire();
                    _reservation.Source.Life.Retire();
                }
                uncertain = _uncertain;
                return false;
            }
            finally { _mutating = false; }
        }

        private void CancelChange(Action requireCancellation, Action action)
        {
            RequireCancellation(requireCancellation);
            _cancelMutationAttempted = true;
            action();
            RequireCancellation(requireCancellation);
        }

        private void RequireCancellationLife()
        {
            var source = _reservation.Source;
            if (!OnCurrentOwner() || source.Life.LifeGeneration != _life || _life == 0
                || !source.Owner.TryRead(out var binding, out var state)
                || !source.MatchesEquipmentBinding(in binding) || state.LifeGeneration != _life
                || !state.OwnsPistol
                || state.LifeStage is not (StrictCombatLifeStage.Alive or StrictCombatLifeStage.Downed
                    or StrictCombatLifeStage.Dead))
                throw new InvalidOperationException("The captured equipment life is unavailable for cancellation.");
        }

        private void RequireCancellation(Action requireCancellation)
        {
            RequireCancellationLife();
            requireCancellation();
            RequireCancellationLife();
            if (_uncertain || Time.frameCount != _frame
                || !_reservation.Source.Owner.TryRead(out _, out var accepted) || accepted.Revision != _revision)
                throw new InvalidOperationException("Accepted equipment state changed during cancellation.");
            RequireCapturedReferences();
            if (_stats._playerIsDowned || _stats._dyingState != PlayerStats.DyingState.Alive
                || _stats._healing || _stats._isHealing || _stats._respawning || _reservation.Source.Life.Vitals._hasDied)
                throw new InvalidOperationException("Native life presentation already changed.");
            if (_poseApplied && _poseReversible) RequireCancellationPose();
            requireCancellation();
            RequireCancellationLife();
            if (Time.frameCount != _frame || !_reservation.Source.Owner.TryRead(out _, out accepted)
                || accepted.Revision != _revision)
                throw new InvalidOperationException("Accepted equipment state changed during cancellation.");
        }

        private void CaptureCancellationPose()
        {
            _poseController = _animator.runtimeAnimatorController;
            _poseVariables = _data._equippedAnimVars;
            _animationHashes = ItemAnimatorHashHelper._hashes;
            var parameters = _animator.parameters;
            if (!Live(_poseController) || _poseVariables is null || _poseVariables.WasCollected
                || _poseVariables.Length > 128 || _animationHashes is null || _animationHashes.WasCollected
                || parameters is null || parameters.WasCollected || parameters.Length > 1024)
                throw new InvalidOperationException("The captured equipment pose layout is unavailable.");
            _copiedPoseVariables = new AnimatorVariables[_poseVariables.Length];
            _poseVariableHashes = new int[_poseVariables.Length];
            for (int i = 0; i < _poseVariables.Length; i++)
            {
                var variable = _poseVariables[i];
                int index = (int)variable;
                if (index < 0 || index >= _animationHashes.Length)
                    throw new InvalidOperationException("An equipment pose variable is unavailable.");
                _copiedPoseVariables[i] = variable;
                _poseVariableHashes[i] = _animationHashes[index];
            }
            _poseParameters = new (int, AnimatorControllerParameterType)[parameters.Length];
            _poseReversible = true;
            int held = 0, heldFloat = 0, wide = 0;
            for (int i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                if (parameter is null || parameter.WasCollected)
                    throw new InvalidOperationException("An equipment animator parameter is unavailable.");
                int hash = parameter.nameHash;
                var type = parameter.type;
                _poseParameters[i] = (hash, type);
                if (hash == AnimationHashes.RightHandHeldItemIdHash)
                { held++; if (type != AnimatorControllerParameterType.Int) _poseReversible = false; }
                if (hash == AnimationHashes.RightHandHeldItemIdFloatHash)
                { heldFloat++; if (type != AnimatorControllerParameterType.Float) _poseReversible = false; }
                if (hash == AnimationHashes.LeftHandWidePoseBlendHash)
                { wide++; if (type != AnimatorControllerParameterType.Float) _poseReversible = false; }
                foreach (int variableHash in _poseVariableHashes)
                    if (variableHash == hash && (type is not (AnimatorControllerParameterType.Float
                        or AnimatorControllerParameterType.Int or AnimatorControllerParameterType.Bool)
                        || hash == AnimationHashes.RightHandHeldItemIdHash
                        || hash == AnimationHashes.RightHandHeldItemIdFloatHash
                        || hash == AnimationHashes.LeftHandWidePoseBlendHash)) _poseReversible = false;
            }
            _ownedWidePose = _animator.GetFloat(AnimationHashes.LeftHandWidePoseBlendHash);
            _poseReversible &= held == 1 && heldFloat == 1 && wide == 1 && float.IsFinite(_ownedWidePose);
        }

        private void RequireCancellationPose()
        {
            var parameters = _animator.parameters;
            if (!Same(_poseController, _animator.runtimeAnimatorController)
                || !Same(_poseVariables, _data._equippedAnimVars)
                || !Same(_animationHashes, ItemAnimatorHashHelper._hashes)
                || _poseVariables.Length != _copiedPoseVariables.Length
                || parameters is null || parameters.WasCollected || parameters.Length != _poseParameters.Length)
                throw new InvalidOperationException("The captured equipment pose layout changed.");
            for (int i = 0; i < _copiedPoseVariables.Length; i++)
                if (_poseVariables[i] != _copiedPoseVariables[i]
                    || _animationHashes[(int)_copiedPoseVariables[i]] != _poseVariableHashes[i])
                    throw new InvalidOperationException("The captured equipment pose variables changed.");
            for (int i = 0; i < _poseParameters.Length; i++)
            {
                var parameter = parameters[i];
                var captured = _poseParameters[i];
                if (parameter is null || parameter.WasCollected || parameter.nameHash != captured.Hash
                    || parameter.type != captured.Type)
                    throw new InvalidOperationException("The captured animator parameters changed.");
                foreach (int hash in _poseVariableHashes)
                {
                    if (hash != captured.Hash) continue;
                    bool expected = !_poseReversed;
                    bool matches = captured.Type switch
                    {
                        AnimatorControllerParameterType.Float => _animator.GetFloat(hash) == (expected ? 1f : 0f),
                        AnimatorControllerParameterType.Int => _animator.GetInteger(hash) == (expected ? 1 : 0),
                        AnimatorControllerParameterType.Bool => _animator.GetBool(hash) == expected,
                        _ => false
                    };
                    if (!matches) throw new InvalidOperationException("The owned equipment pose changed.");
                }
            }
            if (_animator.GetInteger(AnimationHashes.RightHandHeldItemIdHash) != (_poseReversed ? -1 : 355)
                || _animator.GetFloat(AnimationHashes.RightHandHeldItemIdFloatHash) != 355f
                || _animator.GetFloat(AnimationHashes.LeftHandWidePoseBlendHash) != (_widePoseCleared ? 0f : _ownedWidePose))
                throw new InvalidOperationException("The owned held-item pose changed.");
        }
    }
}
