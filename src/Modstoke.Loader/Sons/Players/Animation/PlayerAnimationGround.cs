// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// Ground locomotion scalar publication for a server carrier, never the retained local rig.
    /// The existing native Mecanim replicator still owns layer/event publication after animation
    /// evaluation. This does not restore animation transitions or reconcile the local animator.
    /// </summary>
    internal sealed class PlayerAnimationGround
    {
        private readonly PlayerBridge _bridge;
        private readonly BoltEntity _entity;
        private readonly PlayerAnimatorControl _configuration;
        private readonly CoopMecanimReplicator _replicator;
        private readonly PlayerPreservation.LocomotionBinding _binding;
        private readonly float _maximumInput;
        private readonly float _directionBlend;
        private readonly float _overallBlend;
        private float _horizontal;
        private float _vertical;
        private float _overall;

        internal PlayerAnimationGround(PlayerBridge bridge)
        {
            _bridge = bridge;
            Require(bridge?.IsServer == true && bridge.TryReadCurrentTarget(out _entity)
                && PlayerRoles.IsCurrentCarrier(_entity), "A completed server carrier is required.");
            var controls = _entity.GetComponentsInChildren<PlayerAnimatorControl>(true);
            Require(controls.Length == 1, "One carrier-local native animation configuration is required.");
            _configuration = controls[0];
            var replicators = _entity.GetComponentsInChildren<CoopMecanimReplicator>(true);
            Require(replicators.Length == 1, "One carrier-local native Mecanim publisher is required.");
            _replicator = replicators[0];
            _binding = new PlayerPreservation.LocomotionBinding(bridge, _entity);
            _maximumInput = _configuration._maxInputSpeed;
            _directionBlend = _configuration._walkBlendSpeed;
            _overallBlend = _configuration._overallBlendSpeed;
            Require(float.IsFinite(_maximumInput) && _maximumInput > 0
                && float.IsFinite(_directionBlend) && _directionBlend >= 0
                && float.IsFinite(_overallBlend) && _overallBlend >= 0,
                "The native carrier animation configuration is invalid.");
            _horizontal = _binding.Animator.GetFloat(_binding.HorizontalId);
            _vertical = _binding.Animator.GetFloat(_binding.VerticalId);
            _overall = _binding.Animator.GetFloat(_binding.OverallId);
            Require(float.IsFinite(_horizontal) && float.IsFinite(_vertical) && float.IsFinite(_overall),
                "The initial locomotion animator values are not finite.");
            Validate();
        }

        internal void Validate()
        {
            BoltEntity entity = null;
            Require(_bridge.IsGameplayActive && _bridge.TryReadCurrentTarget(out entity) && entity.Pointer == _entity.Pointer
                && PlayerRoles.IsCurrentCarrier(entity), "The animation carrier changed.");
            _binding.Validate();
            // Never disable an existing component here. The migration adapter must establish
            // exclusive scalar ownership on the NEW carrier, independently of the local rig.
            Require(_configuration != null && !_configuration.WasCollected && _configuration
                && !_configuration.enabled && _configuration.transform.IsChildOf(entity.transform)
                && _configuration._animator?.Pointer == _binding.Animator.Pointer
                && _configuration._maxInputSpeed == _maximumInput
                && _configuration._walkBlendSpeed == _directionBlend
                && _configuration._overallBlendSpeed == _overallBlend,
                "The carrier's native locomotion writer must be quiescent with unchanged configuration.");
            Require(_replicator != null && !_replicator.WasCollected && _replicator && _replicator.enabled
                && _replicator.gameObject.activeInHierarchy
                && _replicator.transform.IsChildOf(entity.transform)
                && _replicator.entity?.Pointer == entity.Pointer
                && _replicator.GetTargetAnimator()?.Pointer == _binding.Animator.Pointer
                && _replicator._hasSetLayerIndices,
                "The carrier's native animation publisher is not bound and active.");
            Require(_binding.Animator.GetFloat(_binding.HorizontalId) == _horizontal
                && _binding.Animator.GetFloat(_binding.VerticalId) == _vertical
                && _binding.Animator.GetFloat(_binding.OverallId) == _overall,
                "A competing writer changed the carrier's locomotion parameters.");
        }

        internal void Publish(Vector2 movement, Vector3 resolvedVelocity, float deltaTime)
        {
            Validate();
            Require(float.IsFinite(deltaTime) && deltaTime > 0
                && float.IsFinite(movement.x) && float.IsFinite(movement.y)
                && float.IsFinite(resolvedVelocity.x) && float.IsFinite(resolvedVelocity.z),
                "The server locomotion sample is invalid.");
            // Native PlayerAnimatorControl.UpdatePlayerVelocity's ordinary ground branch:
            // input * 2.5 / maxInputSpeed, and horizontal body speed / maxInputSpeed,
            // with independently clamped directional and overall interpolation factors.
            float directionFactor = Math.Clamp(deltaTime * _directionBlend, 0, 1);
            float overallFactor = Math.Clamp(deltaTime * _overallBlend, 0, 1);
            float horizontal = _horizontal + (movement.x * 2.5f / _maximumInput - _horizontal) * directionFactor;
            float vertical = _vertical + (movement.y * 2.5f / _maximumInput - _vertical) * directionFactor;
            float speed = MathF.Sqrt(resolvedVelocity.x * resolvedVelocity.x + resolvedVelocity.z * resolvedVelocity.z);
            float overall = _overall + (speed / _maximumInput - _overall) * overallFactor;
            _binding.Publish(horizontal, vertical, overall);
            _horizontal = horizontal;
            _vertical = vertical;
            _overall = overall;
            Validate();
        }

        private static void Require(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException(detail);
        }
    }
}
