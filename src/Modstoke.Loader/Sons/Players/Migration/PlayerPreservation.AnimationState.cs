// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using Il2CppInterop.Runtime;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class PlayerPreservation
    {
        private sealed record AnimationParameter(string Name, int Hash, AnimatorControllerParameterType Kind,
            int DefaultValue, bool Curve, int Value);
        private sealed record AnimationLayer(string Name, int State, int ShortName, int Tag, bool Loop,
            float Time, float Weight, float Length, float Speed, float Multiplier);
        private sealed record AnimationData(ulong SourceId, int StateType, byte[] ControllerSchema,
            float Speed, AnimationParameter[] Parameters, AnimationLayer[] Layers);

        /// <summary>
        /// Capture only after the orchestrator drains meaningful actions, pending triggers and native
        /// Mecanim transition events, then quiesces writers. Active transitions are rejected, not flattened.
        /// Trigger queues, transition coroutines and recorder/playable state are not serialized here.
        /// </summary>
        public static bool TryCaptureSettledAnimationState(ControlPeer peer, BoltEntity source,
            out byte[] payload, out string detail)
            => CaptureSettledAnimationState(peer, source, null, out payload, out detail);

        /// <summary>
        /// Capture a lifecycle-paused retained player without serializing its temporary zero
        /// playback speed as the speed the replacement should use after resuming.
        /// </summary>
        public static bool TryCaptureSettledAnimationState(ControlPeer peer, BoltEntity source,
            PlayerLifecycle lifecycle, out byte[] payload, out string detail)
        {
            if (lifecycle == null)
            {
                payload = null;
                detail = "The exact paused retained lifecycle is required.";
                return false;
            }
            return CaptureSettledAnimationState(peer, source, lifecycle, out payload, out detail);
        }

        private static bool CaptureSettledAnimationState(ControlPeer peer, BoltEntity source,
            PlayerLifecycle lifecycle, out byte[] payload, out string detail)
        {
            payload = null;
            detail = "Settled animation capture is unavailable or reentrant.";
            if (!Enter()) return false;
            try
            {
                RequireContext(peer);
                var state = RequireState(source);
                ulong id = source.networkId.PackedValue;
                var animator = RequireSettledAnimator(state);
                var controller = animator.runtimeAnimatorController;
                var data = CaptureAnimation(source, state, animator);
                byte[] encoded = EncodeAnimation(data);
                RequireAnimationIdentity(source, state, id, animator, controller);
                Require(encoded.AsSpan().SequenceEqual(EncodeAnimation(CaptureAnimation(source, state, animator))),
                    "Animator state changed during settled capture.");
                RequireAnimationIdentity(source, state, id, animator, controller);
                if (lifecycle != null)
                {
                    Require(source.Pointer == lifecycle.OriginalEntity?.Pointer && lifecycle.IsPaused
                        && animator.speed == 0, "The exact retained Animator must still be lifecycle-paused.");
                    Require(lifecycle.TryReadResumeAnimatorSpeed(out float resumeSpeed) && float.IsFinite(resumeSpeed),
                        "The paused Animator's original playback speed is unavailable.");
                    encoded = EncodeAnimation(data with { Speed = resumeSpeed });
                    Require(lifecycle.IsPaused, "The retained lifecycle changed during animation capture.");
                    RequireAnimationIdentity(source, state, id, animator, controller);
                }
                payload = encoded;
                detail = "Captured settled Animator layers and parameters; meaningful-action and trigger drain remain caller-owned.";
                return true;
            }
            catch (Exception error) { detail = Describe(error); return false; }
            finally { _busy = false; }
        }

        public static bool TryValidateSettledAnimationState(ReadOnlySpan<byte> payload, ulong expectedSourceNetworkId,
            out string detail)
        {
            try
            {
                DecodeAnimation(payload, expectedSourceNetworkId);
                detail = "Settled animation payload is structurally valid; live controller/schema matching remains required.";
                return true;
            }
            catch (Exception error) { detail = Describe(error); return false; }
        }

        internal static bool TryReadAnimationResumeSpeed(ReadOnlySpan<byte> payload, ulong expectedSourceNetworkId,
            out float speed, out string detail)
        {
            speed = 0;
            try
            {
                speed = DecodeAnimation(payload, expectedSourceNetworkId).Speed;
                detail = "Playback speed read from the exact canonical animation capture.";
                return true;
            }
            catch (Exception error) { detail = Describe(error); return false; }
        }

        /// <summary>
        /// Restore before scalar-state restoration and before any replacement writer resumes.
        /// The replacement must be quiescent after meaningful-action drain. The retained client Animator
        /// is verified in place and never played, evaluated or written. New carrier evaluation may run
        /// native state callbacks; any failed readback is Unconfirmed and must not be retried.
        /// </summary>
        public static BoltMigrationMutationResult TryRestoreSettledAnimationState(
            ControlPeer peer, BoltEntity replacement, ulong expectedSourceNetworkId,
            ReadOnlySpan<byte> payload, out string detail)
        {
            detail = "Settled animation restoration is unavailable or reentrant.";
            if (!Enter()) return BoltMigrationMutationResult.Rejected;
            bool attempted = false;
            try
            {
                RequireContext(peer);
                var data = DecodeAnimation(payload, expectedSourceNetworkId);
                var state = RequireState(replacement);
                ulong id = replacement.networkId.PackedValue;
                Require(id != data.SourceId && data.StateType == state.Meta.TypeId.Value,
                    "Animator restoration requires a distinct identity with the same PlayerState schema.");
                var animator = RequireSettledAnimator(state);
                var controller = animator.runtimeAnimatorController;
                bool retained = IsRetainedClientAnimator(animator);
                Require(!RuntimeAnimationBindings.ContainsKey(animator.Pointer),
                    "Restore settled animation before preparing its live animation endpoint.");
                RequirePreservationAnimatorLocation(replacement, animator);
                var current = CaptureAnimation(replacement, state, animator);
                RequireAnimationSchema(data, current);
                for (int i = 0; i < data.Layers.Length; i++)
                    Require(data.Layers[i].State == 0 ? current.Layers[i].State == 0
                        : animator.HasState(i, data.Layers[i].State), "The destination controller lacks a captured layer state.");
                if (retained)
                {
                    VerifyAnimation(data, current);
                    RequireAnimationIdentity(replacement, state, id, animator, controller);
                    detail = "The original client Animator remains in place with matching settled state; no Animator writes were made.";
                    return BoltMigrationMutationResult.Applied;
                }

                RequireAnimationIdentity(replacement, state, id, animator, controller);
                attempted = true;
                foreach (var parameter in data.Parameters)
                {
                    RequireAnimationIdentity(replacement, state, id, animator, controller);
                    Require(!IsRetainedClientAnimator(animator), "The destination became the retained client Animator.");
                    if (parameter.Kind == AnimatorControllerParameterType.Trigger)
                        animator.ResetTrigger(parameter.Hash);
                    else if (!parameter.Curve) WriteAnimationParameter(animator, parameter);
                }
                for (int i = 0; i < data.Layers.Length; i++)
                {
                    RequireAnimationIdentity(replacement, state, id, animator, controller);
                    Require(!IsRetainedClientAnimator(animator), "The destination became the retained client Animator.");
                    var layer = data.Layers[i];
                    animator.SetLayerWeight(i, layer.Weight);
                    if (layer.State != 0) animator.Play(layer.State, i, layer.Time);
                }
                RequireAnimationIdentity(replacement, state, id, animator, controller);
                Require(!IsRetainedClientAnimator(animator), "The destination became the retained client Animator.");
                animator.speed = data.Speed;
                animator.Update(0f);
                RequireAnimationIdentity(replacement, state, id, animator, controller);
                VerifyAnimation(data, CaptureAnimation(replacement, state, animator));
                RequireAnimationIdentity(replacement, state, id, animator, controller);
                detail = "Settled Animator layers, normalized times, weights, speed and parameters restored and read back.";
                return BoltMigrationMutationResult.Applied;
            }
            catch (Exception error)
            {
                detail = Describe(error);
                return attempted ? BoltMigrationMutationResult.Unconfirmed : BoltMigrationMutationResult.Rejected;
            }
            finally { _busy = false; }
        }

        private static bool IsRetainedClientAnimator(Animator animator)
        {
            var original = LocalPlayer.Animator;
            return original != null && !original.WasCollected && original && original.Pointer == animator.Pointer;
        }

        private static void RequirePreservationAnimatorLocation(BoltEntity entity, Animator animator)
        {
            if (!IsRetainedClientAnimator(animator)) CapturePose(entity.transform, animator.transform, "PlayerState.Animator");
        }

        private static Animator RequireSettledAnimator(global::PlayerState state)
        {
            var animator = state.Animator;
            Require(animator != null && !animator.WasCollected && animator && animator.isInitialized
                && animator.runtimeAnimatorController != null && !animator.runtimeAnimatorController.WasCollected
                && animator.runtimeAnimatorController, "An initialized, live bound player Animator is required.");
            Require(!animator.hasBoundPlayables && animator.recorderMode == AnimatorRecorderMode.Offline
                && !animator.isMatchingTarget, "Playable, recording or target-matching animation has not settled.");
            Require(animator.layerCount > 0 && animator.layerCount <= MaximumElements, "Unsupported Animator layer count.");
            for (int i = 0; i < animator.layerCount; i++)
                Require(!animator.IsInTransition(i), "Animator layer " + i + " is in transition; drain it before capture or restore.");
            return animator;
        }

        private static void RequireAnimationIdentity(BoltEntity entity, global::PlayerState state, ulong id,
            Animator animator, RuntimeAnimatorController controller)
        {
            RequireCurrent(entity, state, id);
            Require(!animator.WasCollected && animator && animator.isInitialized && !controller.WasCollected && controller
                && state.Animator?.Pointer == animator.Pointer && animator.runtimeAnimatorController?.Pointer == controller.Pointer,
                "Player Animator/controller identity changed during preservation.");
        }

        private static AnimationData CaptureAnimation(BoltEntity source, global::PlayerState state, Animator animator)
        {
            Require(RequireSettledAnimator(state).Pointer == animator.Pointer, "The bound player Animator changed.");
            var raw = animator.parameters;
            Require(raw != null && !raw.WasCollected && raw.Length == animator.parameterCount && raw.Length <= MaximumElements,
                "Animator parameter schema is unavailable.");
            var parameters = new AnimationParameter[raw.Length];
            var hashes = new HashSet<int>();
            for (int i = 0; i < raw.Length; i++)
            {
                var parameter = raw[i];
                Require(parameter != null && !parameter.WasCollected && !string.IsNullOrEmpty(parameter.name)
                    && hashes.Add(parameter.nameHash) && parameter.nameHash == Animator.StringToHash(parameter.name),
                    "Animator parameter names or hashes are ambiguous.");
                var kind = parameter.type;
                Require(IsAnimationParameterKind(kind), "Unsupported Animator parameter type.");
                int defaultValue = kind switch
                {
                    AnimatorControllerParameterType.Float => BitConverter.SingleToInt32Bits(parameter.defaultFloat),
                    AnimatorControllerParameterType.Int => parameter.defaultInt,
                    AnimatorControllerParameterType.Bool => parameter.defaultBool ? 1 : 0,
                    _ => 0
                };
                parameters[i] = new(parameter.name, parameter.nameHash, kind, defaultValue,
                    kind != AnimatorControllerParameterType.Trigger && animator.IsParameterControlledByCurve(parameter.nameHash),
                    ReadAnimationParameter(animator, parameter.nameHash, kind));
            }
            var layers = new AnimationLayer[animator.layerCount];
            for (int i = 0; i < layers.Length; i++)
            {
                Require(!animator.IsInTransition(i), "Animator entered a transition during capture.");
                var value = animator.GetCurrentAnimatorStateInfo(i);
                layers[i] = new(animator.GetLayerName(i), value.fullPathHash, value.shortNameHash, value.tagHash,
                    value.loop, value.normalizedTime, animator.GetLayerWeight(i), value.length, value.speed, value.speedMultiplier);
            }
            return new(source.networkId.PackedValue, state.Meta.TypeId.Value, CaptureControllerSchema(animator), animator.speed, parameters, layers);
        }

        private static bool IsAnimationParameterKind(AnimatorControllerParameterType kind) => kind == AnimatorControllerParameterType.Float
            || kind == AnimatorControllerParameterType.Int || kind == AnimatorControllerParameterType.Bool || kind == AnimatorControllerParameterType.Trigger;

        private static int ReadAnimationParameter(Animator animator, int hash, AnimatorControllerParameterType kind) => kind switch
        {
            AnimatorControllerParameterType.Float => BitConverter.SingleToInt32Bits(animator.GetFloat(hash)),
            AnimatorControllerParameterType.Int => animator.GetInteger(hash),
            AnimatorControllerParameterType.Bool => animator.GetBool(hash) ? 1 : 0,
            _ => 0
        };

        private static void WriteAnimationParameter(Animator animator, AnimationParameter parameter)
            => WriteAnimationParameter(animator, parameter.Hash, parameter.Kind, parameter.Value);

        private static void WriteAnimationParameter(Animator animator, int hash,
            AnimatorControllerParameterType kind, int value)
        {
            switch (kind)
            {
                case AnimatorControllerParameterType.Float: animator.SetFloat(hash, BitConverter.Int32BitsToSingle(value)); break;
                case AnimatorControllerParameterType.Int: animator.SetInteger(hash, value); break;
                case AnimatorControllerParameterType.Bool: animator.SetBool(hash, value != 0); break;
            }
        }

        private static void RequireAnimationSchema(AnimationData expected, AnimationData actual)
        {
            Require(expected.ControllerSchema.AsSpan().SequenceEqual(actual.ControllerSchema)
                && expected.Parameters.Length == actual.Parameters.Length && expected.Layers.Length == actual.Layers.Length,
                "Animator controller, avatar, settings or schema changed.");
            for (int i = 0; i < expected.Parameters.Length; i++)
            {
                var a = expected.Parameters[i]; var b = actual.Parameters[i];
                Require(a.Name == b.Name && a.Hash == b.Hash && a.Kind == b.Kind && a.DefaultValue == b.DefaultValue,
                    "Animator parameter schema changed at " + a.Name + ".");
            }
            for (int i = 0; i < expected.Layers.Length; i++)
                Require(expected.Layers[i].Name == actual.Layers[i].Name, "Animator layer ordering changed.");
        }

        private static void VerifyAnimation(AnimationData expected, AnimationData actual)
        {
            RequireAnimationSchema(expected, actual);
            Require(expected.Speed == actual.Speed, "Animator speed readback differs.");
            for (int i = 0; i < expected.Parameters.Length; i++)
            {
                var a = expected.Parameters[i]; var b = actual.Parameters[i];
                Require(a.Curve == b.Curve && a.Value == b.Value, "Animator parameter readback differs at " + a.Name + ".");
            }
            for (int i = 0; i < expected.Layers.Length; i++)
            {
                var a = expected.Layers[i]; var b = actual.Layers[i];
                Require(a.State == b.State && a.ShortName == b.ShortName && a.Tag == b.Tag && a.Loop == b.Loop
                    && AnimationFloatMatches(a.Time, b.Time) && a.Weight == b.Weight
                    && AnimationFloatMatches(a.Length, b.Length) && a.Speed == b.Speed && a.Multiplier == b.Multiplier,
                    "Animator layer readback differs at " + a.Name + "; do not retry partial writes.");
            }
        }

        private static bool AnimationFloatMatches(float a, float b) => float.IsFinite(a) && float.IsFinite(b)
            && MathF.Abs(a - b) <= MathF.Max(0.00001f, 2f * MathF.Abs(MathF.BitIncrement(a) - a));
    }
}
