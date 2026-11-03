// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    internal sealed partial class StrictCombatLocalLife
    {
        private Transform _respawnTransform, _respawnForceTarget, _respawnForceTransform;
        private Rigidbody _respawnRigidbody;
        private ForceLocalPosZero _respawnForce;
        private ulong _placedLife;

        internal bool CanCompleteRespawn(in StrictCombatBinding binding, in StrictCombatSnapshot state)
        {
            try
            {
                int frame = Time.frameCount;
                return _placedLife != 0 && _placedLife == _barrier.LifeGeneration
                    && _phase == Phase.Downed && _controlsRepeated && ControlsCurrent() && DownedMatches()
                    && !_movement._isAnimControllerSwimming && _animation._knockdownTime <= 0f
                    && !_movement._activeHandlingLanded
                    && Same(_respawnTransform, LocalPlayer._Transform_k__BackingField)
                    && Same(_respawnRigidbody, LocalPlayer._Rigidbody_k__BackingField)
                    && Same(_respawnRigidbody, _rigidbody) && Same(_respawnForce, _animation._forceLocalPosZero)
                    && Same(_respawnForceTarget, _respawnForce.target) && Same(_respawnForceTransform, _respawnForce.tr)
                    && _barrier.Owner.TryRead(out var currentBinding, out var currentState)
                    && currentBinding.SameBinding(in binding)
                    && currentState.Revision == state.Revision && currentState.LifeGeneration == state.LifeGeneration
                    && frame == Time.frameCount;
            }
            catch (Exception) { return false; }
        }

        internal bool CanPlaceRespawn(in StrictCombatBinding binding, in StrictCombatSnapshot state)
        {
            try
            {
                int frame = Time.frameCount;
                return _phase == Phase.Downed && _controlsRepeated
                    && state.LifeStage == StrictCombatLifeStage.Dead && state.LifeGeneration != 0
                    && _accepted.LifeGeneration == state.LifeGeneration
                    && _barrier.LifeGeneration == state.LifeGeneration
                    && ControlsCurrent() && DownedMatches()
                    && !_movement._isAnimControllerSwimming && _animation._knockdownTime <= 0f
                    && !_movement._activeHandlingLanded
                    && _barrier.Owner.TryRead(out var currentBinding, out var currentState)
                    && currentBinding.SameBinding(in binding)
                    && currentState.Revision == state.Revision
                    && currentState.LifeGeneration == state.LifeGeneration
                    && currentState.LifeStage == StrictCombatLifeStage.Dead && frame == Time.frameCount;
            }
            catch (Exception) { return false; }
        }

        internal StrictCombatRespawnPlacement PlaceRespawn(in StrictCombatBinding binding,
            in StrictCombatSnapshot state, in StrictCombatRespawnOffer offer)
        {
            bool attempted = false;
            try
            {
                if (!CanPlaceRespawn(in binding, in state) || !offer.IsValid)
                    return StrictCombatRespawnPlacement.Rejected;

                _binding = binding;
                _accepted = state;
                _frame = Time.frameCount;
                RequireCurrent();
                var transform = LocalPlayer._Transform_k__BackingField;
                var rigidbody = LocalPlayer._Rigidbody_k__BackingField;
                var force = _animation._forceLocalPosZero;
                if (!Same(transform, transform) || !Same(rigidbody, _rigidbody) || !Same(force, force))
                    return StrictCombatRespawnPlacement.Rejected;
                var forceTarget = force.target;
                var forceTransform = force.tr;

                void RequireRespawnCurrent()
                {
                    RequireCurrent();
                    if (!CanPlaceRespawn(in _binding, in _accepted)
                        || !Same(transform, LocalPlayer._Transform_k__BackingField)
                        || !Same(rigidbody, LocalPlayer._Rigidbody_k__BackingField)
                        || !Same(rigidbody, _rigidbody)
                        || !Same(force, _animation._forceLocalPosZero)
                        || !Same(forceTarget, force.target) || !Same(forceTransform, force.tr))
                        throw new InvalidOperationException("The respawn placement lost its captured context.");
                    RequireCurrent();
                }

                var position = new Vector3(offer.Position.X, offer.Position.Y, offer.Position.Z);
                var rotation = new Quaternion(offer.Rotation.X, offer.Rotation.Y, offer.Rotation.Z, offer.Rotation.W);
                RequireRespawnCurrent();
                // The caller has already consumed this attempt; native failure must never cause a retry.
                attempted = true;
                LocalPlayer.TeleportToForPlayerSpawn(position, rotation);
                RequireRespawnCurrent();
                var actual = transform.position;
                RequireRespawnCurrent();
                double dx = (double)actual.x - position.x;
                double dy = (double)actual.y - position.y;
                double dz = (double)actual.z - position.z;
                if (!float.IsFinite(actual.x) || !float.IsFinite(actual.y) || !float.IsFinite(actual.z)
                    || dx * dx + dy * dy + dz * dz > 1d)
                    throw new InvalidOperationException("The respawn placement could not be observed at its destination.");

                // This is a same-frame local position observation, not terrain or server arrival proof.
                _respawnTransform = transform;
                _respawnRigidbody = rigidbody;
                _respawnForce = force;
                _respawnForceTarget = forceTarget;
                _respawnForceTransform = forceTransform;
                _placedLife = state.LifeGeneration;
                return StrictCombatRespawnPlacement.Placed;
            }
            catch (Exception)
            {
                if (!attempted) return StrictCombatRespawnPlacement.Rejected;
                _barrier.Retire();
                return StrictCombatRespawnPlacement.Uncertain;
            }
        }
    }
}
