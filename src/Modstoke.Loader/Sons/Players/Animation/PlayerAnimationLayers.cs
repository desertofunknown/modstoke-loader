// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using Modstoke.Loader;
using Sons.Animation;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>Local-player suppression of existing native Enable animation layers.</summary>
    public static class PlayerAnimationLayers
    {
        /// <summary>Registers a native disabler and applies zero weight on the initialized Unity thread.</summary>
        /// <remarks>
        /// A non-null handle must be released even when this returns false: registration or weight
        /// updates may have partially completed. This affects local animation only, not equipment,
        /// player authority, saves or networking. The caller owns acquisition and release timing.
        /// </remarks>
        public static bool TrySuppressLayer(int layer, out PlayerAnimationLayerSuppression suppression,
            out string status)
        {
            suppression = null;
            if (layer < 0)
            {
                status = "The animation layer index must be non-negative.";
                return false;
            }
            var readiness = ManagerAccess.GetReadiness();
            if (readiness != ManagerAccessStatus.Ready)
            {
                status = "Animation layer suppression requires the initialized Unity thread and active world: " + readiness + ".";
                return false;
            }

            status = "The local player must have the requested native Enable animation layer.";
            try
            {
                var player = LocalPlayer.GameObject;
                var animation = LocalPlayer.AnimControl;
                var animator = LocalPlayer.Animator;
                if (!PlayerAnimationLayerSuppression.Alive(player)
                    || !PlayerAnimationLayerSuppression.Alive(animation)
                    || !PlayerAnimationLayerSuppression.Alive(animator) || layer >= animator.layerCount) return false;
                var controller = animation.GetComponent<AnimatorLayersController>();
                if (!PlayerAnimationLayerSuppression.Alive(controller)
                    || !PlayerAnimationLayerSuppression.Same(controller._animator, animator)
                    || controller._layersData == null || !controller._layersData.TryGetValue(layer, out var data)
                    || data == null || data.Layer != layer
                    || data.Behaviour != AnimatorLayersController.LayerBehaviours.Enable) return false;

                suppression = new PlayerAnimationLayerSuppression(player, animation, animator, controller, data, layer);
                suppression.Register();
                if (!suppression.IsCurrent || data.CurrentWeight != 0f || animator.GetLayerWeight(layer) != 0f)
                {
                    status = "The animation layer suppression could not be confirmed; release the returned handle.";
                    return false;
                }
                status = "The local animation layer is suppressed.";
                return true;
            }
            catch (Exception error)
            {
                status = "Animation layer suppression failed; release any returned handle: " + error.Message;
                RuntimeEntryPoint.LogSource.LogWarning("Animation layer " + layer + " suppression failed: " + error);
                return false;
            }
        }
    }

    /// <summary>Owns one native disabler on a captured local-player animation layer.</summary>
    /// <remarks>
    /// Use on the acquisition thread. Release remains valid after player replacement and must be
    /// retried after failure. Disposal throws when release fails, retaining cleanup ownership.
    /// </remarks>
    public sealed class PlayerAnimationLayerSuppression : IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly GameObject _player;
        private readonly PlayerAnimatorControl _animation;
        private readonly Animator _animator;
        private readonly AnimatorLayersController _controller;
        private readonly AnimatorLayersController.LayerData _layer;
        private readonly LayerBehaviourActivator _disabler;
        private readonly ILayerBehaviourActivator _registration;
        private bool _releasing;
        private bool _released;

        public int Layer { get; }
        public bool IsReleased => _released;

        internal PlayerAnimationLayerSuppression(GameObject player, PlayerAnimatorControl animation, Animator animator,
            AnimatorLayersController controller, AnimatorLayersController.LayerData layer, int index)
        {
            _player = player;
            _animation = animation;
            _animator = animator;
            _controller = controller;
            _layer = layer;
            Layer = index;
            _disabler = new LayerBehaviourActivator("Modstoke", index, true, -1f);
            _registration = _disabler.Cast<ILayerBehaviourActivator>();
        }

        /// <summary>Whether this active registration still belongs to the current local player; false off-thread.</summary>
        public bool IsCurrent => Environment.CurrentManagedThreadId == _thread && !_releasing && !_released
            && ManagerAccess.GetReadiness() == ManagerAccessStatus.Ready
            && Alive(_player) && Alive(_animation) && Alive(_animator)
            && Same(_player, LocalPlayer.GameObject) && Same(_animation, LocalPlayer.AnimControl)
            && Same(_animator, LocalPlayer.Animator)
            && Same(_controller, _animation.GetComponent<AnimatorLayersController>())
            && LayerBound && _disabler.IsActive && _registration.IsActive && Registered;

        internal void Register()
        {
            _controller.RegisterLayerBehaviourDisabler(_registration);
            _layer.RefreshState(_animator);
            _layer.WeightAlpha = 1f;
            _layer.UpdateCurrentWeight();
            _layer.ApplyTo(_animator);
        }

        private bool LayerBound => Alive(_controller) && Same(_controller._animator, _animator)
            && _controller._layersData != null && _controller._layersData.TryGetValue(Layer, out var layer)
            && Same(_layer, layer) && _layer.Layer == Layer && Layer < _animator.layerCount
            && _layer.Behaviour == AnimatorLayersController.LayerBehaviours.Enable;

        private bool Registered
        {
            get
            {
                var disablers = _layer.Disablers;
                if (disablers == null) return false;
                for (var i = 0; i < disablers.Count; i++)
                    if (Same(disablers[i], _registration)) return true;
                return false;
            }
        }

        /// <summary>Removes only this disabler and recomputes weight from remaining native owners.</summary>
        /// <remarks>
        /// A false return retains the handle for retry, including when removal succeeded but weight
        /// recomputation failed. A replaced or destroyed binding is never used to update another layer.
        /// </remarks>
        public bool TryRelease(out string status)
        {
            if (Environment.CurrentManagedThreadId != _thread)
            {
                status = "Animation layer suppression must be released on its acquisition thread.";
                return false;
            }
            status = "Animation layer suppression released.";
            if (_released) return true;
            _releasing = true;
            try
            {
                // Remove the captured registration even when the local player has changed.
                if (Registered) _layer.RemoveDisabler(_registration);
                if (Registered)
                {
                    status = "The captured animation layer still contains this disabler; retry release.";
                    return false;
                }
                // Removal may already have succeeded on an earlier attempt. Never restore an old weight.
                if (Alive(_animator) && LayerBound)
                {
                    _layer.RefreshState(_animator);
                    _layer.UpdateCurrentWeight();
                    _layer.ApplyTo(_animator);
                }
                _released = true;
                return true;
            }
            catch (Exception error)
            {
                status = "Animation layer " + Layer + " suppression release failed; retry release: " + error;
                return false;
            }
        }

        public void Dispose()
        {
            if (!TryRelease(out var status)) throw new InvalidOperationException(status);
        }

        internal static bool Alive(UnityEngine.Object value) => value is not null
            && !value.WasCollected && value.Pointer != IntPtr.Zero && value != null;

        internal static bool Same(Il2CppObjectBase first, Il2CppObjectBase second) => first is not null
            && second is not null && !first.WasCollected && !second.WasCollected
            && first.Pointer != IntPtr.Zero && first.Pointer == second.Pointer;
    }
}
