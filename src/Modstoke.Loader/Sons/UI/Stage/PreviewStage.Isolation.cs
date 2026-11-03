// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Modstoke.Sons
{
    public sealed partial class PreviewStage
    {
        // Off-map, above the sea and below the snow line. The short far clip keeps world geometry out of frame.
        // Under 4096 m floats still resolve a quarter millimetre, which face close-ups need between bones.
        internal static readonly Vector3 StageOrigin = new(-3500f, 300f, -3500f);
        private const uint StageRenderingLayers = 0x80u;
        private static readonly string[] LayerCandidates = { "HUD", "Inventory" };

        private readonly List<Light> _directionalLights = new();
        private readonly List<Light> _heldLights = new();
        private int _layer;
        private bool _lightLayers;
        private bool _gateOpen;

        private void ChooseIsolation()
        {
            _layer = ChooseLayer();
            var asset = HDRenderPipeline.currentAsset;
            _lightLayers = asset != null && asset.currentPlatformRenderPipelineSettings.supportLightLayers;
            CollectDirectionalLights();
        }

        // Distance and light layers keep local lights off the copies. Only the gate keeps directional lights off:
        // HDRP ignores Light.cullingMask, and a sun on every light layer shares the stage's.
        private void CollectDirectionalLights()
        {
            foreach (var light in Resources.FindObjectsOfTypeAll<Light>())
                if (light != null && light.type == LightType.Directional && light.gameObject.scene.IsValid())
                    _directionalLights.Add(light);
        }

        private void HoldOff(Light light)
        {
            _directionalLights.Add(light);
            if (_gateOpen && light.enabled)
            {
                light.enabled = false;
                _heldLights.Add(light);
            }
        }

        private void ReleaseHeldLights()
        {
            for (int i = 0; i < _heldLights.Count; i++)
                if (Live(_heldLights[i])) _heldLights[i].enabled = true;
            _heldLights.Clear();
        }

        // The brightest enabled directional light, as the world is lit by it, or null.
        private Light MainDirectionalLight()
        {
            Light main = null;
            foreach (var light in _directionalLights)
                if (Live(light) && light.isActiveAndEnabled && (main == null || light.intensity > main.intensity))
                    main = light;
            return main;
        }

        private static uint LightLayersOf(Light light)
        {
            var data = light != null ? light.GetComponent<HDAdditionalLightData>() : null;
            return data != null ? (uint)data.lightlayersMask : (uint)LightLayerEnum.LightLayerDefault;
        }

        // Each directional light's light layers beside the stage's, so one line shows which would reach the copies
        // without the gate.
        private string DescribeDirectionalLights()
        {
            var text = new StringBuilder("stage light layers ").Append(Hex(StageRenderingLayers))
                .Append(" · directional lights held off for the stage camera:");
            int listed = 0;
            foreach (var light in _directionalLights)
            {
                if (!Live(light)) continue;
                uint mask = LightLayersOf(light);
                text.Append(listed++ == 0 ? " " : ", ").Append(light.name).Append(' ').Append(Hex(mask))
                    .Append(light.isActiveAndEnabled ? "" : " (off)")
                    .Append(!_lightLayers || (mask & StageRenderingLayers) != 0 ? " (reaches the stage layer)" : "");
            }
            return listed == 0 ? text.Append(" none").ToString() : text.ToString();
        }

        private static string Hex(uint value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture);

        // The layer only helps; the render gate is what keeps stage renderers out of other cameras.
        private static int ChooseLayer()
        {
            var main = Camera.main;
            int mask = main != null ? main.cullingMask : 0;
            int fallback = -1;
            foreach (var name in LayerCandidates)
            {
                int layer = LayerMask.NameToLayer(name);
                if (layer < 0) continue;
                if ((mask & (1 << layer)) == 0) return layer;
                if (fallback < 0) fallback = layer;
            }
            if (fallback < 0) throw new InvalidOperationException("Neither the HUD nor the Inventory layer exists.");
            return fallback;
        }

        private void PrepareRenderer(Renderer renderer)
        {
            ConfigureRenderer(renderer);
            renderer.enabled = true;
            Track(renderer);
        }

        // Copies always cast shadows: a first-person source is ShadowsOnly, which would hide it here. Without light
        // probes a renderer gets the camera's ambient probe, the stage sky, never baked world probes off the map.
        private void ConfigureRenderer(Renderer renderer)
        {
            renderer.gameObject.layer = _layer;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            if (_lightLayers) renderer.renderingLayerMask = StageRenderingLayers;
            var skinned = renderer.TryCast<SkinnedMeshRenderer>();
            if (skinned != null) skinned.updateWhenOffscreen = true;
        }

        private void Track(Renderer renderer)
        {
            renderer.forceRenderingOff = !_gateOpen;
            _renderers.Add(renderer);
            _dirty = true;
        }

        // HDRP raises beginCameraRendering before it culls each camera, so stage renderers exist only for the
        // stage camera's cull, including its shadow casters, and never for the world's. World directional lights
        // are off for that cull only.
        private void BeforeCamera(ScriptableRenderContext context, Camera camera)
        {
            if (_disposed || Environment.CurrentManagedThreadId != _ownerThread) return;
            try { SetGate(camera != null && camera.Pointer == _cameraPointer); }
            catch (Exception error) { _renderFailure ??= error; }
        }

        private void SetGate(bool open)
        {
            if (open == _gateOpen) return;
            _gateOpen = open;
            for (int i = 0; i < _renderers.Count; i++)
            {
                var renderer = _renderers[i];
                if (Live(renderer)) renderer.forceRenderingOff = !open;
            }
            if (!open)
            {
                ReleaseHeldLights();
                return;
            }
            for (int i = 0; i < _directionalLights.Count; i++)
            {
                var light = _directionalLights[i];
                if (!Live(light) || !light.enabled) continue;
                light.enabled = false;
                _heldLights.Add(light);
            }
        }
    }
}
