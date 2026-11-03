// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Bolt;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class PlayerPreservation
    {
        private sealed record AnimatorScalarBinding(int ScalarIndex, int PropertyIndex,
            NetworkProperty_Mecanim Property, int StorageOffset);
        private sealed record AnimatorBinding(Animator Animator, RuntimeAnimatorController Controller,
            AnimatorScalarBinding[] Scalars);

        // This is a live owner publication binding, not a preserved Animator state machine.
        // Only overallSpeed is getter-only; hSpeed/vSpeed have generated native setters.
        internal sealed class LocomotionBinding
        {
            private readonly PlayerBridge _bridge;
            private readonly BoltEntity _entity;
            private readonly global::PlayerState _state;
            private readonly ulong _id;
            private readonly AnimatorBinding _binding;
            internal Animator Animator => _binding.Animator;
            internal int HorizontalId { get; }
            internal int VerticalId { get; }
            internal int OverallId { get; }

            internal LocomotionBinding(PlayerBridge bridge, BoltEntity entity)
            {
                Require(Enter(), "Locomotion binding was reentered.");
                try
                {
                    _bridge = bridge;
                    _entity = entity;
                    _state = RequireState(entity);
                    _id = entity.networkId.PackedValue;
                    BoltProperties.RequireLayout();
                    RuntimeHelpers.RunClassConstructor(typeof(NetworkProperty_Float).TypeHandle);
                    var animator = _state.Animator;
                    Require(animator != null && !animator.WasCollected && animator && animator.isInitialized,
                        "Locomotion publication requires the carrier's initialized bound animator.");
                    var controller = animator.runtimeAnimatorController;
                    Require(controller != null && !controller.WasCollected && controller,
                        "Locomotion publication requires a live native animator controller.");
                    CapturePose(entity.transform, animator.transform, "PlayerState.Animator");
                    RequireFloatParameter(animator, "hSpeed");
                    RequireFloatParameter(animator, "vSpeed");
                    RequireFloatParameter(animator, "overallSpeed");
                    HorizontalId = UnityEngine.Animator.StringToHash("hSpeed");
                    VerticalId = UnityEngine.Animator.StringToHash("vSpeed");
                    OverallId = UnityEngine.Animator.StringToHash("overallSpeed");
                    int scalar = Array.FindIndex(Scalars, field => field.Name == "overallSpeed");
                    Require(scalar >= 0, "The overallSpeed player schema changed.");
                    _state.GetDynamicInternal("overallSpeed", out int index);
                    var property = ReadAnimatorProperty(_state, index, Scalars[scalar]);
                    _binding = new AnimatorBinding(animator, controller,
                        new[] { new AnimatorScalarBinding(scalar, index, property, property.OffsetStorage) });
                    ValidateCore();
                }
                finally { _busy = false; }
            }

            internal void Validate()
            {
                Require(Enter(), "Locomotion validation was reentered.");
                try { ValidateCore(); }
                finally { _busy = false; }
            }

            private void ValidateCore()
            {
                Require(_bridge.IsServer && _bridge.IsGameplayActive && _bridge.TryReadCurrentTarget(out var entity)
                    && entity.Pointer == _entity.Pointer && PlayerRoles.IsCurrentCarrier(entity),
                    "The server animation bridge changed.");
                Require(!RuntimeAnimationBindings.ContainsKey(Animator.Pointer),
                    "The full animation binding owns this animator; the ground-only publisher must remain stopped.");
                RequireAnimatorBinding(_entity, _state, _id, _binding);
                Require(Animator.enabled && Animator.gameObject.activeInHierarchy && !Animator.applyRootMotion,
                    "The server animator must remain enabled without root motion.");
                var slot = _binding.Scalars[0];
                var property = ReadAnimatorProperty(_state, slot.PropertyIndex, Scalars[slot.ScalarIndex]);
                Require(property.Pointer == slot.Property.Pointer && property.OffsetStorage == slot.StorageOffset,
                    "The locomotion property binding changed.");
                Require(!Animator.IsParameterControlledByCurve(HorizontalId)
                    && !Animator.IsParameterControlledByCurve(VerticalId)
                    && !Animator.IsParameterControlledByCurve(OverallId),
                    "Curve-controlled locomotion requires native animation-state reconciliation.");
            }

            internal void Publish(float horizontal, float vertical, float overall)
            {
                Require(Enter(), "Locomotion publication was reentered.");
                try
                {
                    ValidateCore();
                    Require(float.IsFinite(horizontal) && float.IsFinite(vertical) && float.IsFinite(overall),
                        "The server locomotion sample is not finite.");
                    Animator.SetFloat(HorizontalId, horizontal);
                    ValidateCore();
                    Animator.SetFloat(VerticalId, vertical);
                    ValidateCore();
                    Animator.SetFloat(OverallId, overall);
                    ValidateCore();
                    _state.hSpeed = horizontal;
                    ValidateCore();
                    _state.vSpeed = vertical;
                    ValidateCore();
                    _binding.Scalars[0].Property.OnSimulateAfter(_state);
                    ValidateCore();
                    Require(Animator.GetFloat(HorizontalId) == horizontal && _state.hSpeed == horizontal
                        && Animator.GetFloat(VerticalId) == vertical && _state.vSpeed == vertical
                        && Animator.GetFloat(OverallId) == overall && _state.overallSpeed == overall,
                        "Server locomotion publication readback differs; do not retry partial writes.");
                }
                finally { _busy = false; }
            }
        }

        private static void RequireFloatParameter(Animator animator, string name)
        {
            var parameters = animator.parameters;
            Require(parameters != null && !parameters.WasCollected && parameters.Length <= MaximumElements,
                "The locomotion animator parameter schema is unavailable.");
            int matches = 0;
            for (int i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                Require(parameter != null && !parameter.WasCollected, "The animator parameter schema is stale.");
                if (!string.Equals(parameter.name, name, StringComparison.Ordinal)) continue;
                Require(parameter.type == AnimatorControllerParameterType.Float, name + " is not a float parameter.");
                matches++;
            }
            Require(matches == 1, name + " requires one matching animator parameter.");
        }

        private static AnimatorBinding PrepareAnimatorScalars(BoltEntity entity, global::PlayerState state, int[] values)
        {
            BoltProperties.RequireLayout();
            RuntimeHelpers.RunClassConstructor(typeof(NetworkProperty_Float).TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(NetworkProperty_Integer).TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(NetworkProperty_Bool).TypeHandle);
            var animator = state.Animator;
            Require(animator != null && !animator.WasCollected && animator && animator.isInitialized,
                "PlayerState requires a live initialized bound animator for scalar restoration.");
            Require(!RuntimeAnimationBindings.ContainsKey(animator.Pointer),
                "Restore scalar state before preparing its live animation endpoint.");
            var controller = animator.runtimeAnimatorController;
            Require(controller != null && !controller.WasCollected && controller,
                "The bound player animator has no live controller.");
            RequirePreservationAnimatorLocation(entity, animator);
            var parameters = animator.parameters;
            Require(parameters != null && !parameters.WasCollected && parameters.Length <= MaximumElements,
                "The player animator parameter schema is unavailable.");
            var bindings = new List<AnimatorScalarBinding>();
            for (int i = 0; i < Scalars.Length; i++)
            {
                var field = Scalars[i];
                if (field.Write != null) continue;
                // Native lookup selects a root property by exact name. It only reads its value.
                state.GetDynamicInternal(field.Name, out int index);
                var property = ReadAnimatorProperty(state, index, field);
                AnimatorControllerParameterType expectedType = field.Kind switch
                {
                    ScalarKind.Float => AnimatorControllerParameterType.Float,
                    ScalarKind.Integer => AnimatorControllerParameterType.Int,
                    _ => AnimatorControllerParameterType.Bool
                };
                int matches = 0;
                for (int p = 0; p < parameters.Length; p++)
                {
                    var parameter = parameters[p];
                    Require(parameter != null && !parameter.WasCollected, "The animator parameter schema is stale.");
                    if (!string.Equals(parameter.name, field.Name, StringComparison.Ordinal)) continue;
                    Require(parameter.type == expectedType, field.Name + " animator parameter type differs.");
                    matches++;
                }
                Require(matches == 1, field.Name + " requires one matching animator parameter.");
                Require(ReadAnimatorScalar(animator, field) == values[i]
                    || !animator.IsParameterControlledByCurve(field.Name),
                    field.Name + " is curve-controlled and differs; restore its animation state before scalar restoration.");
                Require(!IsRetainedClientAnimator(animator) || ReadAnimatorScalar(animator, field) == values[i],
                    field.Name + " differs on the retained client Animator; it must be preserved in place.");
                bindings.Add(new(i, index, property, property.OffsetStorage));
            }
            Require(bindings.Count == 28, "The getter-only player scalar schema changed.");
            var result = new AnimatorBinding(animator, controller, bindings.ToArray());
            RequireAnimatorBinding(entity, state, entity.networkId.PackedValue, result);
            return result;
        }

        private static unsafe NetworkProperty_Mecanim ReadAnimatorProperty(global::PlayerState state, int index, Scalar field,
            bool ownerPull = true)
        {
            var properties = state.Meta?.Properties;
            var objects = state.RootObjects;
            if (properties == null || properties.WasCollected || properties.Length > MaximumElements
                || index < 0 || index >= properties.Length || objects == null || objects.WasCollected)
                throw new InvalidOperationException(field.Name + " has no current root property descriptor.");
            // The qualified descriptor is an inline value type containing references. Read it using
            // the existing Bolt layout contract; never use its boxing indexer or write its storage.
            byte* info = (byte*)properties.Pointer + 32 + 32L * index;
            int objectIndex = *(int*)(info + 16);
            IntPtr pointer = *(IntPtr*)(info + 24);
            if (pointer == IntPtr.Zero || objectIndex < 0 || objectIndex >= objects.Count
                || objects[objectIndex]?.Pointer != state.Pointer || state.RootState?.Pointer != state.Pointer)
                throw new InvalidOperationException(field.Name + " is not a property of the current root PlayerState.");
            var raw = Il2CppObjectPool.Get<NetworkProperty>(pointer);
            var expectedKind = field.Kind switch
            {
                ScalarKind.Float => BoltScalarKind.Single,
                ScalarKind.Integer => BoltScalarKind.Int32,
                _ => BoltScalarKind.Boolean
            };
            if (BoltProperties.KindOf(raw) != expectedKind)
                throw new InvalidOperationException(field.Name + " native scalar class changed.");
            var property = raw.TryCast<NetworkProperty_Mecanim>();
            if (property == null || property.WasCollected
                || !string.Equals(property.PropertyName, field.Name, StringComparison.Ordinal)
                || (long)state.OffsetProperties + property.OffsetProperties != index
                || property.OffsetStorage < 0 || state.OffsetStorage < 0)
                throw new InvalidOperationException(field.Name + " native scalar descriptor changed.");
            var storage = state.Storage?.Values;
            if (storage == null || storage.WasCollected
                || (long)state.OffsetStorage + property.OffsetStorage >= storage.Length)
                throw new InvalidOperationException(field.Name + " native scalar destination is unavailable.");
            if (property.MecanimMode != MecanimMode.Parameter
                || property.MecanimDirection != MecanimDirection.UsingAnimatorMethods
                || property.ShouldPullDataFromMecanim(state) != ownerPull)
                throw new InvalidOperationException(field.Name + " has the wrong owner/replica animator direction.");
            return property;
        }

        private static void RequireAnimatorBinding(BoltEntity entity, global::PlayerState state, ulong id, AnimatorBinding binding)
        {
            RequireCurrent(entity, state, id);
            Require(!binding.Animator.WasCollected && binding.Animator && binding.Animator.isInitialized
                && !binding.Controller.WasCollected && binding.Controller
                && state.Animator?.Pointer == binding.Animator.Pointer
                && binding.Animator.runtimeAnimatorController?.Pointer == binding.Controller.Pointer,
                "The player animator or controller binding changed during restoration.");
        }

        private static int ReadAnimatorScalar(Animator animator, Scalar field) => field.Kind switch
        {
            ScalarKind.Float => BitConverter.SingleToInt32Bits(animator.GetFloat(field.Name)),
            ScalarKind.Integer => animator.GetInteger(field.Name),
            _ => animator.GetBool(field.Name) ? 1 : 0
        };

        private static void RestoreAnimatorScalars(BoltEntity entity, global::PlayerState state, ulong id,
            AnimatorBinding binding, int[] values)
        {
            foreach (var slot in binding.Scalars)
            {
                var field = Scalars[slot.ScalarIndex];
                int value = values[slot.ScalarIndex];
                RequireAnimatorBinding(entity, state, id, binding);
                var property = ReadAnimatorProperty(state, slot.PropertyIndex, field);
                Require(property.Pointer == slot.Property.Pointer && property.OffsetStorage == slot.StorageOffset,
                    field.Name + " native scalar binding changed during restoration.");
                if (ReadAnimatorScalar(binding.Animator, field) != value)
                {
                    Require(!IsRetainedClientAnimator(binding.Animator),
                        field.Name + " differs on the retained client Animator; it must be preserved in place.");
                    Require(!binding.Animator.IsParameterControlledByCurve(field.Name),
                        field.Name + " became curve-controlled during restoration.");
                    switch (field.Kind)
                    {
                        case ScalarKind.Float:
                            binding.Animator.SetFloat(field.Name, BitConverter.Int32BitsToSingle(value));
                            break;
                        case ScalarKind.Integer:
                            binding.Animator.SetInteger(field.Name, value);
                            break;
                        case ScalarKind.Boolean:
                            binding.Animator.SetBool(field.Name, value != 0);
                            break;
                    }
                }
                RequireAnimatorBinding(entity, state, id, binding);
                Require(ReadAnimatorScalar(binding.Animator, field) == value,
                    field.Name + " animator readback differs; do not retry partial writes.");
                // Native OnSimulateAfter retains the mode/direction/owner gates, then dispatches
                // the typed PullMecanimValue and its change notification. Do not advance Animator
                // time or invoke the whole-state simulation/callback pipeline for this operation.
                property.OnSimulateAfter(state);
                RequireAnimatorBinding(entity, state, id, binding);
                Require(field.Read(state) == value,
                    field.Name + " native animator pull differs; do not retry partial writes.");
            }
        }

        private static void VerifyAnimatorScalars(BoltEntity entity, global::PlayerState state, ulong id,
            AnimatorBinding binding, int[] values)
        {
            RequireAnimatorBinding(entity, state, id, binding);
            foreach (var slot in binding.Scalars)
            {
                var field = Scalars[slot.ScalarIndex];
                Require(ReadAnimatorScalar(binding.Animator, field) == values[slot.ScalarIndex]
                    && field.Read(state) == values[slot.ScalarIndex],
                    field.Name + " animator/state readback differs; do not retry partial writes.");
            }
        }
    }
}
