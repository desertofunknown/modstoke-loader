// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Modstoke.Loader;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace Modstoke.Sons
{
    public sealed partial class PreviewStage
    {
        private const float LightDistance = 2.5f;
        private const float LightRange = 6f;
        // The subject stays inside the full-intensity core of each cone, from head to feet.
        private const float InnerSpotPercent = 60f;
        private const float MinExposure = 2f;
        private const float MaxExposure = 16f;
        private const float MaxLux = 120000f;
        private const string TuningUsage = "Use stage/light with any of ev <EV100>, key <lux>, fill <lux>, rim <lux>"
            + " and ambient <lux>, or with no arguments for the current values.";
        private static readonly Vector3 TestLightOffset = new(.6f, .3f, .6f);
        // Lux at the pivot, for the stage exposure. Spot lights only: a directional light would reach the world if
        // culling failed. Key to fill is 2.5:1 before the sky's ambient fills both sides.
        private static readonly LightSpec[] NeutralRig =
        {
            new("Key", 40f, 35f, 70f, 4000f, new Color(1f, .96f, .9f), true),
            new("Fill", -55f, 10f, 90f, 1600f, new Color(.95f, .97f, 1f), false),
            new("Rim", 160f, 40f, 60f, 2000f, new Color(.9f, .95f, 1f), false)
        };

        private readonly List<HDAdditionalLightData> _lightData = new();
        private GameObject _lights;

        private void CreateLights()
        {
            _lights = new GameObject("Lights") { layer = _layer };
            _lights.transform.SetParent(_root.transform, false);
            foreach (var spec in NeutralRig) AddSpot(spec);
        }

        private void AddSpot(LightSpec spec)
        {
            var node = new GameObject(spec.Name) { layer = _layer };
            node.transform.SetParent(_lights.transform, false);
            var offset = Quaternion.Euler(-spec.Elevation, spec.Azimuth, 0f) * new Vector3(0f, 0f, LightDistance);
            node.transform.localPosition = offset;
            node.transform.localRotation = Quaternion.LookRotation(-offset);
            var light = node.AddComponent<Light>();
            light.type = LightType.Spot;
            light.color = spec.Color;
            light.range = LightRange;
            light.spotAngle = spec.Angle;
            light.shadows = spec.Shadows ? LightShadows.Soft : LightShadows.None;
            var hd = node.AddComponent<HDAdditionalLightData>();
            hd.type = HDLightType.Spot;
            hd.lightUnit = LightUnit.Candela;
            hd.intensity = Candela(spec.Lux);
            hd.innerSpotPercent = InnerSpotPercent;
            hd.range = LightRange;
            hd.affectsVolumetric = false;
            hd.EnableShadows(spec.Shadows);
            if (spec.Shadows)
            {
                // A softbox-sized source; PCSS filtering uses it for the penumbra.
                hd.shapeRadius = .25f;
                hd.SetShadowResolution(1024);
            }
            if (_lightLayers) hd.lightlayersMask = LightLayerEnum.LightLayer7;
            _lightData.Add(hd);
        }

        // A local light on the default layer and the main directional light's light layers, as bright as the key at
        // the cube. If it shows on the test image, light layers alone don't keep a light with the sun's mask out.
        internal string AddExcludedTestLight()
        {
            if (!Usable()) return "";
            var sun = MainDirectionalLight();
            uint mask = sun != null ? LightLayersOf(sun) : (uint)LightLayerEnum.LightLayerDefault;
            var node = new GameObject("Excluded test light") { layer = 0 };
            node.transform.SetParent(_root.transform, false);
            node.transform.localPosition = _pivotTarget + TestLightOffset;
            var light = node.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = Color.red;
            light.range = 4f;
            light.shadows = LightShadows.None;
            var hd = node.AddComponent<HDAdditionalLightData>();
            hd.type = HDLightType.Point;
            hd.lightUnit = LightUnit.Candela;
            hd.intensity = 3500f;
            hd.lightlayersMask = (LightLayerEnum)mask;
            _dirty = true;
            return "test light layers " + Hex(mask) + (sun != null ? " from " + sun.name : " (no directional light)");
        }

        // A red directional light as bright as the key, on the stage's light layer so it reaches the copies if the
        // gate fails. The gate holds it off like a world sun; with light layers off the world sees it until the
        // stage closes.
        internal string AddExcludedTestSun()
        {
            if (!Usable()) return "";
            var node = new GameObject("Excluded test sun") { layer = 0 };
            node.transform.SetParent(_root.transform, false);
            node.transform.localRotation = Quaternion.LookRotation(-TestLightOffset);
            var light = node.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = Color.red;
            light.shadows = LightShadows.None;
            var hd = node.AddComponent<HDAdditionalLightData>();
            hd.type = HDLightType.Directional;
            hd.lightUnit = LightUnit.Lux;
            hd.intensity = NeutralRig[0].Lux;
            hd.affectsVolumetric = false;
            uint mask = _lightLayers ? StageRenderingLayers : (uint)LightLayerEnum.LightLayerDefault;
            hd.lightlayersMask = (LightLayerEnum)mask;
            HoldOff(light);
            _dirty = true;
            return "test sun " + Number(NeutralRig[0].Lux) + " lx, light layers " + Hex(mask);
        }

        private void FollowLights(Vector3 pivot)
        {
            // Yaw only, so the key light stays on the face whatever the pitch.
            if (_lights != null) _lights.transform.SetPositionAndRotation(pivot, Quaternion.Euler(0f, _yaw, 0f));
        }

        // Live tuning for stage/light. Values are clamped, change the open stage only and are never saved.
        internal static bool TryTuneLights(string arguments, out string status)
        {
            if (!OnOwnerThread(out status)) return false;
            var stage = _instance;
            if (stage == null || !stage.IsAlive)
            {
                status = "No preview stage is open.";
                return false;
            }
            return stage.TryTune(arguments, out status);
        }

        private bool TryTune(string arguments, out string status)
        {
            var words = (arguments ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            var edits = new List<(string Name, float Value)>();
            for (int i = 0; i < words.Length; i += 2)
            {
                string name = words[i].ToLowerInvariant();
                if (i + 1 >= words.Length || (name != "ev" && name != "ambient" && RigIndex(name) < 0)
                    || !float.TryParse(words[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                    || !float.IsFinite(value))
                {
                    status = TuningUsage;
                    return false;
                }
                if (RigIndex(name) >= _lightData.Count)
                {
                    status = "This preview stage has no light rig.";
                    return false;
                }
                edits.Add((name, value));
            }
            foreach (var (name, value) in edits)
            {
                int light = RigIndex(name);
                if (light >= 0) _lightData[light].intensity = Candela(Mathf.Clamp(value, 0f, MaxLux));
                else if (name == "ev") _stageExposure.fixedExposure.Override(Mathf.Clamp(value, MinExposure, MaxExposure));
                else
                {
                    _stageSky.multiplier.Override(Mathf.Clamp(value, 0f, MaxLux) / Mathf.PI);
                    _settleFrames = AmbientSettleFrames;
                    if (_readback != null) _readbackDeadline = Time.frameCount + ReadbackFrameLimit;
                }
            }
            if (edits.Count > 0)
            {
                _dirty = true;
                _reportSettings = true;
            }
            status = "Preview stage lights for " + _ownerId + ": " + DescribeLights() + " (EV100, lux at the subject).";
            RuntimeEntryPoint.LogSource?.LogMessage(status);
            return true;
        }

        // In stage/light argument order, so a logged line can be pasted back.
        private string DescribeLights()
        {
            var text = new StringBuilder("ev ").Append(Number(_stageExposure.fixedExposure.value));
            for (int i = 0; i < _lightData.Count; i++)
                text.Append(' ').Append(NeutralRig[i].Name.ToLowerInvariant()).Append(' ')
                    .Append(Number(_lightData[i].intensity / Candela(1f)));
            return text.Append(" ambient ").Append(Number(_stageSky.multiplier.value * Mathf.PI)).ToString();
        }

        private static int RigIndex(string name) =>
            Array.FindIndex(NeutralRig, spec => string.Equals(spec.Name, name, StringComparison.OrdinalIgnoreCase));

        private static float Candela(float lux) => lux * LightDistance * LightDistance;

        private static string Number(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        private readonly record struct LightSpec(string Name, float Azimuth, float Elevation, float Angle,
            float Lux, Color Color, bool Shadows);
    }
}
