// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// The player in the shot. Only for the main camera's cull, and put back as soon as it has rendered: what
    /// hangs off the camera (a held tablet, the rebreather, overlay quads and their lights) is hidden, the held
    /// item and the player can be, and with the player in view the head, hair and hats the first-person view
    /// only casts shadows with are drawn, unless the camera is still inside the head.
    /// </summary>
    internal sealed class PhotoSubject
    {
        private const float HeadClearance = 0.35f;
        private readonly List<Renderer> _cameraParts = new(), _held = new(), _body = new(), _shadowOnly = new();
        private readonly List<Light> _cameraLights = new();
        // What the gate changed, put back only where the value is still the one set here.
        private readonly List<Renderer> _hidden = new(), _shown = new();
        private readonly List<Light> _dimmed = new();
        private bool _open;

        internal bool ShowPlayer { get; set; } = true;
        internal bool ShowHeld { get; set; } = true;
        /// <summary>Whether the camera is far enough from the player's eye to draw the head.</summary>
        internal bool HeadClear { get; set; }

        internal static bool IsClear(Vector3 camera, Vector3 eye) => (camera - eye).sqrMagnitude > HeadClearance * HeadClearance;

        /// <summary>Lists the renderers the gate works on; again whenever the player's gear may have changed.</summary>
        internal void Collect()
        {
            CloseGate();
            _cameraParts.Clear();
            _cameraLights.Clear();
            _held.Clear();
            _body.Clear();
            _shadowOnly.Clear();
            var camera = LocalPlayer.MainCamTr;
            var seen = new HashSet<IntPtr>();
            if (Live(camera))
            {
                foreach (var renderer in camera.GetComponentsInChildren<Renderer>(true))
                {
                    if (!Live(renderer) || !seen.Add(renderer.Pointer)) continue;
                    // The game's low-resolution stencil quad belongs to the frame itself.
                    if (renderer.name.Contains("LowResStencil", StringComparison.OrdinalIgnoreCase)) continue;
                    _cameraParts.Add(renderer);
                }
                foreach (var light in camera.GetComponentsInChildren<Light>(true))
                    if (Live(light)) _cameraLights.Add(light);
            }
            var inventory = LocalPlayer.Inventory;
            if (Live(inventory))
            {
                AddHeld(inventory.RightHandItem?.ItemObject, seen);
                AddHeld(inventory.LeftHandItem?.ItemObject, seen);
            }
            var player = LocalPlayer.Transform;
            if (!Live(player)) return;
            foreach (var renderer in player.GetComponentsInChildren<Renderer>(true))
            {
                if (!Live(renderer) || !seen.Add(renderer.Pointer)) continue;
                _body.Add(renderer);
                if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly) _shadowOnly.Add(renderer);
            }
        }

        /// <summary>What the gate works on, with the shadow-only renderers it draws for the player, for checking in game.</summary>
        internal string Describe()
        {
            var names = new List<string>();
            foreach (var renderer in _shadowOnly)
            {
                if (names.Count == 12)
                {
                    names.Add("...");
                    break;
                }
                if (Live(renderer)) names.Add(renderer.name);
            }
            return _cameraParts.Count + " renderer(s) on the camera, " + _cameraLights.Count + " camera light(s), " + _held.Count
                + " held, " + _body.Count + " on the player (shadow-only: " + string.Join(", ", names) + ")";
        }

        /// <summary>Opens the gate for the main camera's cull and closes it for any other camera.</summary>
        internal void Gate(bool main)
        {
            if (!main)
            {
                CloseGate();
                return;
            }
            if (_open) return;
            _open = true;
            foreach (var renderer in _cameraParts) Hide(renderer);
            foreach (var light in _cameraLights)
            {
                if (!Live(light) || !light.enabled) continue;
                light.enabled = false;
                _dimmed.Add(light);
            }
            if (!ShowHeld) foreach (var renderer in _held) Hide(renderer);
            if (!ShowPlayer)
            {
                foreach (var renderer in _body) Hide(renderer);
                return;
            }
            if (!HeadClear) return;
            foreach (var renderer in _shadowOnly)
            {
                if (!Live(renderer) || renderer.shadowCastingMode != ShadowCastingMode.ShadowsOnly) continue;
                renderer.shadowCastingMode = ShadowCastingMode.On;
                _shown.Add(renderer);
            }
        }

        internal void CloseGate()
        {
            if (!_open) return;
            _open = false;
            foreach (var renderer in _hidden)
                if (Live(renderer) && renderer.forceRenderingOff) renderer.forceRenderingOff = false;
            foreach (var renderer in _shown)
                if (Live(renderer) && renderer.shadowCastingMode == ShadowCastingMode.On) renderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            foreach (var light in _dimmed)
                if (Live(light) && !light.enabled) light.enabled = true;
            _hidden.Clear();
            _shown.Clear();
            _dimmed.Clear();
        }

        private void AddHeld(GameObject item, HashSet<IntPtr> seen)
        {
            if (!Live(item)) return;
            foreach (var renderer in item.GetComponentsInChildren<Renderer>(true))
                if (Live(renderer) && seen.Add(renderer.Pointer)) _held.Add(renderer);
        }

        private void Hide(Renderer renderer)
        {
            if (!Live(renderer) || renderer.forceRenderingOff) return;
            renderer.forceRenderingOff = true;
            _hidden.Add(renderer);
        }

        private static bool Live(Object value) => value is not null && !value.WasCollected && value;
    }
}
