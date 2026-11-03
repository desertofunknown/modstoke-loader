// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Sons;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

namespace Modstoke.Loader
{
    // The probe reads the live facts the preview stage depends on and never creates, enables or edits a game
    // object. The test command renders a throwaway stage to PNGs for the image checks, with a test cube or a
    // copy of the local player. The light command tunes the open stage's exposure, rig and sky.
    internal static class PreviewStageProbe
    {
        private const string ProviderId = "openacai.loader.previewStage";
        private const string ProbeCommandId = "probe";
        private const string TestCommandId = "test";
        private const string LightCommandId = "light";
        private const int MaxNodes = 600;
        private const int MaxDepth = 12;
        private static readonly string[] StageLayerCandidates = { "UI", "HUD", "Inventory" };
        private static readonly string[] ArmBones = { "LeftArm", "LeftForeArm", "RightArm", "RightForeArm" };
        private static readonly FrameSettingsField[] StageFields =
        {
            FrameSettingsField.OpaqueObjects, FrameSettingsField.TransparentObjects,
            FrameSettingsField.SubsurfaceScattering, FrameSettingsField.Transmission,
            FrameSettingsField.ShadowMaps, FrameSettingsField.ContactShadows,
            FrameSettingsField.LightLayers, FrameSettingsField.SSAO,
            FrameSettingsField.Postprocess, FrameSettingsField.Tonemapping,
            FrameSettingsField.AtmosphericScattering, FrameSettingsField.Volumetrics,
            FrameSettingsField.VolumetricClouds, FrameSettingsField.ReflectionProbe,
            FrameSettingsField.PlanarProbe, FrameSettingsField.ProbeVolume,
            FrameSettingsField.SSR, FrameSettingsField.TransparentSSR,
            FrameSettingsField.SSGI, FrameSettingsField.CustomPass,
            FrameSettingsField.CustomPostProcess, FrameSettingsField.Water,
            FrameSettingsField.Decals, FrameSettingsField.DecalLayers,
            FrameSettingsField.RayTracing, FrameSettingsField.MotionVectors,
            FrameSettingsField.ObjectMotionVectors, FrameSettingsField.MotionBlur,
            FrameSettingsField.DepthOfField, FrameSettingsField.ChromaticAberration,
            FrameSettingsField.LensDistortion, FrameSettingsField.PaniniProjection,
            FrameSettingsField.FilmGrain, FrameSettingsField.Vignette,
            FrameSettingsField.Bloom, FrameSettingsField.ExposureControl,
            FrameSettingsField.LensFlareDataDriven, FrameSettingsField.SkyReflection
        };
        private static readonly (PreviewFocus Focus, string Name)[] CaptureShots =
        {
            (PreviewFocus.Body, "body"), (PreviewFocus.Face, "face"), (PreviewFocus.Hands, "hands")
        };
        // Steam64 ids are 17 digits; nothing the probe needs is that long.
        private static readonly Regex LongNumber = new Regex(@"\d{15,}", RegexOptions.CultureInvariant);

        private static int _ownerThread;
        private static string _lastStatus = "READY";
        private static string _lastDetail = "No probe has run this session.";

        internal static void Initialize()
        {
            _ownerThread = Environment.CurrentManagedThreadId;
            if (!MenuRegistry.RegisterOrUpdateBuiltInProvider(new Provider()))
                RuntimeEntryPoint.LogSource?.LogWarning("Preview stage probe provider could not be registered.");
        }

        private static bool TryInvoke(string commandId, string arguments, out CommandResultSnapshot result)
        {
            bool test = string.Equals(commandId, TestCommandId, StringComparison.OrdinalIgnoreCase);
            bool light = string.Equals(commandId, LightCommandId, StringComparison.OrdinalIgnoreCase);
            if (!test && !light && !string.Equals(commandId, ProbeCommandId, StringComparison.OrdinalIgnoreCase))
            {
                result = CommandResultSnapshot.Failure("Unknown preview stage command: " + commandId + ".");
                return false;
            }
            if (_ownerThread == 0 || Environment.CurrentManagedThreadId != _ownerThread)
            {
                result = CommandResultSnapshot.Failure("The preview stage probe runs on the game thread only.");
                return false;
            }
            if (GameBuild.IsDedicatedServer)
            {
                result = CommandResultSnapshot.Failure("A dedicated server has no renderer to probe.");
                return false;
            }
            if (test) return TryRunTest(arguments?.Trim(), out result);
            if (light)
            {
                bool tuned = PreviewStage.TryTuneLights(arguments, out string status);
                result = tuned ? CommandResultSnapshot.Success(status) : CommandResultSnapshot.Failure(status);
                return tuned;
            }
            if (!string.IsNullOrEmpty(arguments))
            {
                result = CommandResultSnapshot.Failure("The preview stage probe takes no arguments.");
                return false;
            }

            var utc = DateTime.UtcNow;
            string report = LongNumber.Replace(Build(utc), "<id>");
            RuntimeEntryPoint.LogSource?.LogMessage("Preview stage probe" + Environment.NewLine + report);
            string path = Path.Combine(LoaderPaths.UserDataDirectory, "PreviewStage",
                "probe-" + utc.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".txt");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, report, new UTF8Encoding(false));
            }
            catch (Exception error)
            {
                _lastStatus = "CHECK";
                _lastDetail = "Probe logged; the file could not be written: " + error.Message;
                result = CommandResultSnapshot.Failure(_lastDetail);
                return false;
            }
            _lastStatus = "READY";
            _lastDetail = "Probe written to the Loader log and " + path + ".";
            result = CommandResultSnapshot.Success(_lastDetail);
            return true;
        }

        // Renders a cube with a native lit material once and writes that frame. "red" adds a world-style local
        // light with the sun's light layers beside the cube, and "sun" a red directional light; neither may show on
        // the image if world lights are isolated.
        private static bool TryRunTest(string arguments, out CommandResultSnapshot result)
        {
            if (string.Equals(arguments, "capture", StringComparison.OrdinalIgnoreCase)) return TryRunCaptureTest(out result);
            bool red = string.Equals(arguments, "red", StringComparison.OrdinalIgnoreCase);
            bool sun = string.Equals(arguments, "sun", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(arguments) && !red && !sun)
            {
                result = CommandResultSnapshot.Failure("The preview stage test takes no arguments, \"red\", \"sun\" or \"capture\".");
                return false;
            }
            var material = FindTestMaterial();
            if (material == null)
            {
                result = CommandResultSnapshot.Failure("No loaded opaque HDRP lit material was found for the test cube.");
                return false;
            }
            var options = new PreviewStageOptions { OwnerId = ProviderId, AllowTitleScene = true };
            if (!PreviewStage.TryCreate(options, out var stage, out string status))
            {
                result = CommandResultSnapshot.Failure(status);
                return false;
            }
            if (!stage.TryShowDebugCube(material, out status))
            {
                stage.Dispose();
                result = CommandResultSnapshot.Failure(status);
                return false;
            }
            string excluded = red ? stage.AddExcludedTestLight() : sun ? stage.AddExcludedTestSun() : "";
            stage.SetFocus(PreviewFocus.Body, true);

            var snapshot = stage.Snapshot();
            string facts = snapshot.Width + "x" + snapshot.Height + " · layer " + LayerName(snapshot.Layer)
                + " · light layers " + snapshot.LightLayers + " · frame settings " + snapshot.FrameSource
                + " · material " + material.name + " (" + material.shader.name + ")"
                + (excluded.Length > 0 ? " · " + excluded : "");
            var utc = DateTime.UtcNow;
            string path = Path.Combine(LoaderPaths.UserDataDirectory, "PreviewStage",
                "stage-" + utc.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
                + (red ? "-red" : sun ? "-sun" : "") + ".png");
            stage.RequestReadback((png, error) =>
            {
                facts += Rendered(stage.Snapshot());
                stage.Dispose();
                if (png == null || png.Length == 0)
                {
                    _lastStatus = "CHECK";
                    _lastDetail = "The test stage produced no image: " + (error ?? "empty frame") + ".";
                    RuntimeEntryPoint.LogSource?.LogWarning(_lastDetail);
                    return;
                }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, png);
                    _lastStatus = "READY";
                    _lastDetail = "Test image written to " + path + " (" + facts + ").";
                    RuntimeEntryPoint.LogSource?.LogMessage("Preview stage test: " + _lastDetail);
                }
                catch (Exception writeError)
                {
                    _lastStatus = "CHECK";
                    _lastDetail = "The test image could not be written: " + writeError.Message;
                    RuntimeEntryPoint.LogSource?.LogWarning(_lastDetail);
                }
            });
            result = CommandResultSnapshot.Success("Test stage rendering (" + facts + "). The image goes to " + path
                + " after the next frame.");
            return true;
        }

        // Copies the local player onto a throwaway stage and writes one image per focus, then closes the stage.
        private static bool TryRunCaptureTest(out CommandResultSnapshot result)
        {
            if (!LocalPlayer.IsInWorld)
            {
                result = CommandResultSnapshot.Failure("The capture test needs a local player in the world.");
                return false;
            }
            var options = new PreviewStageOptions { OwnerId = ProviderId };
            if (!PreviewStage.TryCreate(options, out var stage, out string status))
            {
                result = CommandResultSnapshot.Failure(status);
                return false;
            }
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            stage.BuildCompleted += build =>
            {
                if (stage.Parts.Count == 0)
                {
                    stage.Dispose();
                    Report(false, "The capture test built nothing: " + build.Status + ".");
                    return;
                }
                string facts = string.Join(", ", stage.Parts.Select(part => part.PartId + " x" + part.Renderers.Count))
                    + (build.FailedParts.Count > 0 ? " · left out " + string.Join("; ", build.FailedParts.Select(entry => entry.Key + ": " + entry.Value)) : "")
                    + " · bones Head " + (stage.TryFindBone("Head", out _) ? "yes" : "no")
                    + ", LeftHand " + (stage.TryFindBone("LeftHand", out _) ? "yes" : "no")
                    + ", RightHand " + (stage.TryFindBone("RightHand", out _) ? "yes" : "no");
                Shoot(stage, 0, stamp, facts);
            };
            if (!stage.TryCaptureLocalPlayer(new PreviewCaptureOptions(), out _, out status))
            {
                stage.Dispose();
                result = CommandResultSnapshot.Failure("The local player could not be captured: " + status);
                return false;
            }
            result = CommandResultSnapshot.Success("Capturing the local player. Body, face and hands images go to "
                + Path.Combine(LoaderPaths.UserDataDirectory, "PreviewStage") + " over the next frames.");
            return true;
        }

        private static void Shoot(PreviewStage stage, int index, string stamp, string facts)
        {
            var (focus, name) = CaptureShots[index];
            stage.SetFocus(focus, true);
            string path = Path.Combine(LoaderPaths.UserDataDirectory, "PreviewStage", "capture-" + stamp + "-" + name + ".png");
            stage.RequestReadback((png, error) =>
            {
                if (png == null || png.Length == 0)
                {
                    stage.Dispose();
                    Report(false, "The capture test produced no " + name + " image: " + (error ?? "empty frame") + ".");
                    return;
                }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, png);
                }
                catch (Exception writeError)
                {
                    stage.Dispose();
                    Report(false, "The capture image could not be written: " + writeError.Message);
                    return;
                }
                if (index + 1 < CaptureShots.Length)
                {
                    Shoot(stage, index + 1, stamp, facts);
                    return;
                }
                var final = stage.Snapshot();
                string rendered = " · frame settings " + final.FrameSource + Rendered(final);
                stage.Dispose();
                Report(true, "Capture images written to " + Path.GetDirectoryName(path) + " (" + facts + rendered + ").");
            });
        }

        private static string Rendered(PreviewStageSnapshot snapshot) =>
            " · renders " + Or(snapshot.LitShaderMode, "no frame") + " · exposure " + Or(snapshot.Exposure, "no frame");

        private static string Or(string value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;

        private static void Report(bool ready, string detail)
        {
            _lastStatus = ready ? "READY" : "CHECK";
            _lastDetail = LongNumber.Replace(detail, "<id>");
            if (ready) RuntimeEntryPoint.LogSource?.LogMessage("Preview stage capture test: " + _lastDetail);
            else RuntimeEntryPoint.LogSource?.LogWarning(_lastDetail);
        }

        private static Material FindTestMaterial()
        {
            Material fallback = null;
            foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (material == null || material.renderQueue >= 2450) continue;
                var shader = material.shader;
                if (shader == null) continue;
                if (shader.name == "HDRP/Lit") return material;
                if (fallback == null && shader.name == "Sons/HDRPLit") fallback = material;
            }
            return fallback;
        }

        private static string Build(DateTime utc)
        {
            var text = new StringBuilder(64 * 1024);
            text.Append("utc ").AppendLine(utc.ToString("O", CultureInfo.InvariantCulture));
            text.Append("scene ").Append(SceneManager.GetActiveScene().name)
                .Append(" · in world ").Append(LocalPlayer.IsInWorld)
                .Append(" · recognized build ").AppendLine(GameBuild.IsRecognized.ToString());
            Camera inventoryCamera = null;
            try
            {
                inventoryCamera = FindInventoryCamera();
            }
            catch (Exception error)
            {
                text.Append("InventoryCamera lookup failed: ").AppendLine(error.Message);
            }
            text.AppendLine();
            Section(text, "Layers (check 1)", body => WriteLayers(body, inventoryCamera));
            Section(text, "Pipeline (checks 2, 8)", WritePipeline);
            Section(text, "Lights (checks 3, 4)", WriteLights);
            Section(text, "Cameras and custom passes (check 5)", WriteCameras);
            Section(text, "InventoryCamera", body => WriteCamera(body, inventoryCamera, true));
            Section(text, "Main camera frame settings", body => WriteCamera(body, Camera.main, true));
            Section(text, "Volumes", WriteVolumes);
            var root = LocalPlayer.IsInWorld ? LocalPlayer.GameObject : null;
            Section(text, "Local player renderers (check 9)", body => WriteRenderers(body, root));
            Section(text, "Local player hierarchy (check 11)", body => WriteHierarchy(body, root));
            Section(text, "Local player rig and animator (check 12)", body => WriteRig(body, root));
            text.AppendLine("Not covered here: the red-light, day/night and SMAA image checks need the stage camera.");
            return text.ToString();
        }

        private static void Section(StringBuilder text, string title, Action<StringBuilder> write)
        {
            text.Append("## ").AppendLine(title);
            try
            {
                write(text);
            }
            catch (Exception error)
            {
                text.Append("  failed: ").Append(error.GetType().Name).Append(": ").AppendLine(error.Message);
            }
            text.AppendLine();
        }

        private static void WriteLayers(StringBuilder text, Camera inventoryCamera)
        {
            var main = Camera.main;
            int mainMask = main != null ? main.cullingMask : 0;
            int inventoryMask = inventoryCamera != null ? inventoryCamera.cullingMask : 0;
            text.Append("  main camera ").Append(main != null ? main.name : "none").Append(" culling ").AppendLine(Hex(mainMask));
            text.Append("  InventoryCamera culling ").AppendLine(inventoryCamera != null ? Hex(inventoryMask) : "none");
            for (int layer = 0; layer < 32; layer++)
            {
                string name = LayerMask.LayerToName(layer);
                text.Append("  ").Append(layer.ToString(CultureInfo.InvariantCulture).PadLeft(2)).Append(' ')
                    .Append(string.IsNullOrEmpty(name) ? "-" : name)
                    .Append(" · main ").Append(OnOff(mainMask, layer))
                    .Append(" · inventory ").AppendLine(OnOff(inventoryMask, layer));
            }
            foreach (var candidate in StageLayerCandidates)
            {
                int layer = LayerMask.NameToLayer(candidate);
                text.Append("  candidate ").Append(candidate).Append(' ')
                    .AppendLine(layer < 0 ? "missing" : "layer " + layer + ", main " + OnOff(mainMask, layer));
            }
        }

        private static void WritePipeline(StringBuilder text)
        {
            var pipeline = RenderPipelineManager.currentPipeline;
            text.Append("  pipeline ").AppendLine(pipeline != null ? pipeline.GetIl2CppType().FullName : "none");
            var asset = HDRenderPipeline.currentAsset;
            if (asset == null)
            {
                text.AppendLine("  HDRP asset unavailable");
                return;
            }
            var settings = asset.currentPlatformRenderPipelineSettings;
            text.Append("  asset ").AppendLine(asset.name);
            text.Append("  supportLightLayers ").AppendLine(settings.supportLightLayers.ToString());
            text.Append("  colorBufferFormat ").AppendLine(settings.colorBufferFormat.ToString());
            text.Append("  supportCustomPass ").AppendLine(settings.supportCustomPass.ToString());
            text.Append("  supportSSAO ").AppendLine(settings.supportSSAO.ToString());
            text.Append("  supportDecals ").AppendLine(settings.supportDecals.ToString());
            text.Append("  supportedLitShaderMode ").AppendLine(settings.supportedLitShaderMode.ToString());
            text.Append("  antialiasing modes ")
                .AppendLine(string.Join(", ", Enum.GetNames(typeof(HDAdditionalCameraData.AntialiasingMode))));
        }

        private static void WriteLights(StringBuilder text)
        {
            var byLayer = new SortedDictionary<int, int>();
            int lightLayer7 = 0, total = 0;
            foreach (var light in Resources.FindObjectsOfTypeAll<Light>())
            {
                if (light == null || !light.gameObject.scene.IsValid()) continue;
                total++;
                var data = light.GetComponent<HDAdditionalLightData>();
                if (data != null && ((uint)data.lightlayersMask & 0x80u) != 0) lightLayer7++;
                if (light.type != LightType.Directional)
                {
                    byLayer.TryGetValue(light.gameObject.layer, out int count);
                    byLayer[light.gameObject.layer] = count + 1;
                    continue;
                }
                text.Append("  directional ").Append(PathOf(light.transform, null))
                    .Append(light.isActiveAndEnabled ? "" : " (inactive)")
                    .Append(" · layer ").Append(LayerName(light.gameObject.layer))
                    .Append(" · renderingLayerMask ").Append(Hex(light.renderingLayerMask))
                    .Append(" · cullingMask ").Append(Hex(light.cullingMask))
                    .Append(" · intensity ").Append(F(light.intensity))
                    .Append(" · shadows ").Append(light.shadows);
                if (data != null)
                    text.Append(" · lightlayersMask ").Append(Hex((int)(uint)data.lightlayersMask))
                        .Append(" · unit ").Append(data.lightUnit);
                text.AppendLine();
            }
            text.Append("  scene lights ").Append(total).Append(" · with LightLayer7 ").AppendLine(lightLayer7.ToString(CultureInfo.InvariantCulture));
            foreach (var entry in byLayer)
                text.Append("  other lights on ").Append(LayerName(entry.Key)).Append(": ").AppendLine(entry.Value.ToString(CultureInfo.InvariantCulture));
        }

        private static void WriteCameras(StringBuilder text)
        {
            foreach (var camera in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (camera == null || !camera.gameObject.scene.IsValid()) continue;
                WriteCamera(text, camera, false);
            }
            foreach (var volume in Resources.FindObjectsOfTypeAll<CustomPassVolume>())
            {
                if (volume == null || !volume.gameObject.scene.IsValid()) continue;
                text.Append("  custom pass volume ").Append(PathOf(volume.transform, null))
                    .Append(volume.isActiveAndEnabled ? "" : " (inactive)")
                    .Append(" · global ").Append(volume.isGlobal)
                    .Append(" · injection ").Append(volume.injectionPoint)
                    .Append(" · target ").AppendLine(volume.targetCamera != null ? volume.targetCamera.name : "any");
                var passes = volume.customPasses;
                for (int i = 0; passes != null && i < passes.Count; i++)
                {
                    var pass = passes[i];
                    if (pass == null) continue;
                    text.Append("    ").Append(pass.GetIl2CppType().Name).Append(" \"").Append(pass.name).Append('"')
                        .AppendLine(pass.enabled ? "" : " (disabled)");
                }
            }
        }

        private static void WriteCamera(StringBuilder text, Camera camera, bool detailed)
        {
            if (camera == null)
            {
                text.AppendLine("  none");
                return;
            }
            text.Append("  camera ").Append(PathOf(camera.transform, null))
                .Append(camera.isActiveAndEnabled ? "" : " (inactive)")
                .Append(" · depth ").Append(F(camera.depth))
                .Append(" · culling ").Append(Hex(camera.cullingMask))
                .Append(" · target ").Append(camera.targetTexture != null
                    ? camera.targetTexture.width + "x" + camera.targetTexture.height + " " + camera.targetTexture.format : "screen");
            var data = camera.GetComponent<HDAdditionalCameraData>();
            if (data != null)
                text.Append(" · aa ").Append(data.antialiasing)
                    .Append(" · volumes ").Append(Hex(data.volumeLayerMask.value))
                    .Append(" · custom frame ").Append(data.customRenderingSettings)
                    .Append(" · dlss ").Append(data.allowDeepLearningSuperSampling);
            text.AppendLine();
            if (!detailed)
            {
                var components = camera.GetComponents<Component>().Where(component => component != null)
                    .Select(component => component.GetIl2CppType().Name)
                    .Where(name => name != "Transform" && name != "Camera" && name != "HDAdditionalCameraData");
                text.Append("    components ").AppendLine(string.Join(", ", components));
                return;
            }
            text.Append("  fov ").Append(F(camera.fieldOfView)).Append(" · clip ").Append(F(camera.nearClipPlane))
                .Append('-').Append(F(camera.farClipPlane)).Append(" · hdr ").Append(camera.allowHDR)
                .Append(" · msaa ").AppendLine(camera.allowMSAA.ToString());
            if (data == null)
            {
                text.AppendLine("  no HDAdditionalCameraData");
                return;
            }
            text.Append("  clear ").Append(data.clearColorMode).Append(' ').Append(C(data.backgroundColorHDR))
                .Append(" · clearDepth ").Append(data.clearDepth)
                .Append(" · probes ").Append(Hex(data.probeLayerMask.value))
                .Append(" · history ").Append(data.hasPersistentHistory)
                .Append(" · dynamic resolution ").AppendLine(data.allowDynamicResolution.ToString());
            var frame = data.m_RenderingPathCustomFrameSettings;
            var mask = data.renderingPathCustomFrameSettingsOverrideMask;
            var line = new StringBuilder("  frame LitShaderMode=").Append(frame.litShaderMode)
                .Append(mask.mask[(uint)FrameSettingsField.LitShaderMode] ? "*" : "");
            foreach (var field in StageFields)
                line.Append(' ').Append(field).Append('=').Append(frame.IsEnabled(field) ? "on" : "off")
                    .Append(mask.mask[(uint)field] ? "*" : "");
            text.AppendLine(line.ToString());
            text.AppendLine("  (* = overridden; values apply only while custom frame settings are on)");
            // Only a rendering camera already has an HDCamera; asking for an idle one would create it.
            if (camera.isActiveAndEnabled) WriteEffective(text, camera);
        }

        private static void WriteEffective(StringBuilder text, Camera camera)
        {
            var hd = HDCamera.GetOrCreate(camera, 0);
            var frame = hd.frameSettings;
            var line = new StringBuilder("  effective LitShaderMode=").Append(frame.litShaderMode);
            foreach (var field in StageFields)
                line.Append(' ').Append(field).Append('=').Append(frame.IsEnabled(field) ? "on" : "off");
            text.AppendLine(line.ToString());
            var stack = hd.volumeStack;
            var exposure = stack.GetComponent<Exposure>();
            var indirect = stack.GetComponent<IndirectLightingController>();
            text.Append("  effective exposure ")
                .Append(exposure != null ? exposure.mode.value + " fixed " + F(exposure.fixedExposure.value)
                    + " compensation " + F(exposure.compensation.value) + " limits " + F(exposure.limitMin.value)
                    + ".." + F(exposure.limitMax.value) : "none")
                .Append(" · indirect diffuse ").Append(indirect != null ? F(indirect.indirectDiffuseLightingMultiplier.value) : "none")
                .Append(" · reflections ").AppendLine(indirect != null ? F(indirect.reflectionLightingMultiplier.value) : "none");
        }

        private static void WriteVolumes(StringBuilder text)
        {
            foreach (var volume in Resources.FindObjectsOfTypeAll<Volume>())
            {
                if (volume == null || !volume.gameObject.scene.IsValid()) continue;
                // sharedProfile, never profile: reading profile instantiates a copy.
                var profile = volume.sharedProfile;
                text.Append("  volume ").Append(PathOf(volume.transform, null))
                    .Append(volume.isActiveAndEnabled ? "" : " (inactive)")
                    .Append(" · layer ").Append(LayerName(volume.gameObject.layer))
                    .Append(" · global ").Append(volume.isGlobal)
                    .Append(" · priority ").Append(F(volume.priority))
                    .Append(" · weight ").Append(F(volume.weight))
                    .Append(" · profile ").AppendLine(profile != null ? profile.name : "none");
                if (profile == null) continue;
                var components = profile.components;
                var names = new List<string>();
                for (int i = 0; i < components.Count; i++)
                {
                    var component = components[i];
                    if (component != null) names.Add(component.GetIl2CppType().Name + (component.active ? "" : " (off)"));
                }
                text.Append("    ").AppendLine(string.Join(", ", names));
            }
        }

        private static void WriteRenderers(StringBuilder text, GameObject root)
        {
            if (root == null)
            {
                text.AppendLine("  no local player in world");
                return;
            }
            var shaders = new SortedDictionary<string, Shader>(StringComparer.Ordinal);
            var materials = new HashSet<IntPtr>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                bool enabled = renderer.enabled && renderer.gameObject.activeInHierarchy;
                text.Append(enabled ? "  + " : "  - ").Append(PathOf(renderer.transform, root.transform))
                    .Append(" · ").Append(renderer.GetIl2CppType().Name)
                    .Append(" · layer ").Append(LayerName(renderer.gameObject.layer))
                    .Append(" · shadows ").Append(renderer.shadowCastingMode)
                    .Append(" · renderingLayerMask ").Append(Hex((int)renderer.renderingLayerMask));
                if (!enabled)
                {
                    text.AppendLine();
                    continue;
                }
                var skinned = renderer.TryCast<SkinnedMeshRenderer>();
                if (skinned != null)
                {
                    var mesh = skinned.sharedMesh;
                    text.Append(" · mesh ").Append(mesh != null ? mesh.name + " v" + mesh.vertexCount
                            + " sub" + mesh.subMeshCount + " blend" + mesh.blendShapeCount : "none")
                        .Append(" · bones ").Append(skinned.bones.Length)
                        .Append(" · root ").Append(skinned.rootBone != null ? skinned.rootBone.name : "none");
                }
                text.AppendLine();
                var shared = renderer.sharedMaterials;
                for (int slot = 0; slot < shared.Length; slot++)
                {
                    var material = shared[slot];
                    if (material == null)
                    {
                        text.Append("      [").Append(slot).AppendLine("] none");
                        continue;
                    }
                    var shader = material.shader;
                    text.Append("      [").Append(slot).Append("] ").Append(material.name)
                        .Append(" · ").Append(shader != null ? shader.name : "no shader")
                        .Append(" · queue ").AppendLine(material.renderQueue.ToString(CultureInfo.InvariantCulture));
                    if (shader != null) shaders[shader.name] = shader;
                    if (materials.Add(material.Pointer)) WriteMaterial(text, material, shader);
                }
            }
            foreach (var entry in shaders)
            {
                text.Append("  shader ").Append(entry.Key).Append(':');
                int count = entry.Value.GetPropertyCount();
                for (int i = 0; i < count; i++)
                    text.Append(' ').Append(entry.Value.GetPropertyName(i)).Append('/').Append(entry.Value.GetPropertyType(i));
                text.AppendLine();
            }
        }

        private static void WriteMaterial(StringBuilder text, Material material, Shader shader)
        {
            text.Append("        keywords ").AppendLine(string.Join(" ", material.shaderKeywords));
            if (shader == null) return;
            var line = new StringBuilder("        values");
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                string name = shader.GetPropertyName(i);
                line.Append(' ').Append(name).Append('=');
                switch (shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Texture:
                        var texture = material.GetTexture(name);
                        line.Append(texture != null ? texture.name + "(" + texture.width + "x" + texture.height + ")" : "-");
                        break;
                    case ShaderPropertyType.Color:
                        line.Append(C(material.GetColor(name)));
                        break;
                    case ShaderPropertyType.Vector:
                        var vector = material.GetVector(name);
                        line.Append('(').Append(F(vector.x)).Append(',').Append(F(vector.y)).Append(',')
                            .Append(F(vector.z)).Append(',').Append(F(vector.w)).Append(')');
                        break;
                    default:
                        float value = material.GetFloat(name);
                        // Diffusion profile hashes are uint bits stored in a float.
                        line.Append(name.Contains("DiffusionProfile", StringComparison.Ordinal)
                            ? "0x" + BitConverter.SingleToUInt32Bits(value).ToString("X8", CultureInfo.InvariantCulture)
                            : F(value));
                        break;
                }
            }
            text.AppendLine(line.ToString());
        }

        private static void WriteHierarchy(StringBuilder text, GameObject root)
        {
            if (root == null)
            {
                text.AppendLine("  no local player in world");
                return;
            }
            var hips = FindChild(root.transform, "Hips");
            int nodes = 0;
            Walk(text, root.transform, hips, 0, ref nodes);
            if (nodes >= MaxNodes) text.Append("  truncated at ").Append(MaxNodes).AppendLine(" nodes");
            text.Append("  LODGroups ").AppendLine(root.GetComponentsInChildren<LODGroup>(true).Length.ToString(CultureInfo.InvariantCulture));
        }

        private static void Walk(StringBuilder text, Transform node, Transform hips, int depth, ref int nodes)
        {
            if (nodes++ >= MaxNodes) return;
            text.Append(' ', 2 + depth * 2).Append(node.name);
            if (!node.gameObject.activeSelf) text.Append(" (inactive)");
            var components = node.GetComponents<Component>().Where(component => component != null)
                .Select(component => component.GetIl2CppType().Name).Where(name => name != "Transform").ToArray();
            if (components.Length != 0) text.Append(" [").Append(string.Join(", ", components)).Append(']');
            if (node == hips)
            {
                // The bone tree is large and fixed; the rig section reports what the stage needs from it.
                text.Append(" · ").Append(node.GetComponentsInChildren<Transform>(true).Length - 1).AppendLine(" bones below");
                return;
            }
            text.AppendLine();
            if (depth >= MaxDepth) return;
            for (int i = 0; i < node.childCount; i++)
                Walk(text, node.GetChild(i), hips, depth + 1, ref nodes);
        }

        private static void WriteRig(StringBuilder text, GameObject root)
        {
            if (root == null)
            {
                text.AppendLine("  no local player in world");
                return;
            }
            var hips = FindChild(root.transform, "Hips");
            if (hips != null)
            {
                var head = FindChild(hips, "Head");
                text.Append("  hips ").Append(PathOf(hips, root.transform))
                    .Append(" · localScale ").Append(V(hips.localScale))
                    .Append(" · lossyScale ").Append(V(hips.lossyScale))
                    .Append(" · hips to head ").Append(head != null ? F(Vector3.Distance(hips.position, head.position)) + " m" : "no Head")
                    .AppendLine();
                var names = new List<string>();
                foreach (var bone in hips.GetComponentsInChildren<Transform>(true))
                {
                    string name = bone.name;
                    if (name.Contains("Head", StringComparison.Ordinal) || name.Contains("Eye", StringComparison.Ordinal)
                        || name.EndsWith("Hand", StringComparison.Ordinal))
                        names.Add(name);
                }
                text.Append("  head, hand and eye bones ").AppendLine(string.Join(", ", names));
            }
            else
            {
                text.AppendLine("  no Hips under the local player");
            }

            var animator = LocalPlayer.Animator;
            if (animator != null)
            {
                var avatar = animator.avatar;
                text.Append("  animator ").Append(PathOf(animator.transform, root.transform))
                    .Append(" · avatar ").Append(avatar != null ? avatar.name + (avatar.isHuman ? " humanoid" : " generic")
                        + (avatar.isValid ? "" : " invalid") : "none")
                    .Append(" · culling ").Append(animator.cullingMode)
                    .Append(" · root motion ").AppendLine(animator.applyRootMotion.ToString());
                var controller = animator.runtimeAnimatorController;
                if (controller != null)
                {
                    var clips = controller.animationClips;
                    var idle = new SortedSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < clips.Length; i++)
                        if (clips[i] != null && clips[i].name.Contains("idle", StringComparison.OrdinalIgnoreCase)) idle.Add(clips[i].name);
                    text.Append("  controller ").Append(controller.name).Append(" · clips ").Append(clips.Length)
                        .Append(" · idle ").AppendLine(string.Join(", ", idle.Take(40)));
                }
                if (avatar != null && avatar.isHuman)
                    text.Append("  humanoid head ").Append(BoneName(animator, HumanBodyBones.Head))
                        .Append(" · left hand ").Append(BoneName(animator, HumanBodyBones.LeftHand))
                        .Append(" · right hand ").AppendLine(BoneName(animator, HumanBodyBones.RightHand));
            }
            else
            {
                text.AppendLine("  no local player animator");
            }
            WriteRestPose(text, root);
        }

        // Rest arm elevation from bind poses: about 0 is a T-pose, about -40 an A-pose.
        private static void WriteRestPose(StringBuilder text, GameObject root)
        {
            SkinnedMeshRenderer source = null;
            int[] indices = null;
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer == null || !renderer.enabled || renderer.sharedMesh == null) continue;
                var bones = renderer.bones;
                var found = ArmBones.Select(name => IndexOf(bones, name)).ToArray();
                if (found.All(index => index >= 0) && renderer.sharedMesh.bindposes.Length == bones.Length)
                {
                    source = renderer;
                    indices = found;
                    break;
                }
            }
            if (source == null)
            {
                text.AppendLine("  rest pose: no enabled skinned renderer binds both arms");
                return;
            }
            var poses = source.sharedMesh.bindposes;
            var meshToWorld = source.transform.localToWorldMatrix;
            var up = root.transform.up;
            var liveBones = source.bones;
            text.Append("  rest pose from ").Append(source.name);
            for (int side = 0; side < 2; side++)
            {
                int arm = indices[side * 2], forearm = indices[side * 2 + 1];
                var rest = meshToWorld.MultiplyPoint3x4(poses[forearm].inverse.GetColumn(3))
                    - meshToWorld.MultiplyPoint3x4(poses[arm].inverse.GetColumn(3));
                var live = liveBones[forearm].position - liveBones[arm].position;
                text.Append(side == 0 ? " · left " : " · right ").Append(F(90f - Vector3.Angle(rest, up)))
                    .Append("° rest, ").Append(F(90f - Vector3.Angle(live, up))).Append("° live");
            }
            text.AppendLine();
        }

        private static int IndexOf(Il2CppReferenceArray<Transform> bones, string name)
        {
            for (int i = 0; i < bones.Length; i++)
                if (bones[i] != null && bones[i].name == name) return i;
            return -1;
        }

        internal static Camera FindInventoryCamera()
        {
            var inventory = LocalPlayer.IsInWorld ? LocalPlayer.Inventory : null;
            var controller = inventory != null ? inventory.CameraController : null;
            if (controller != null && controller.Camera != null) return controller.Camera;
            return Resources.FindObjectsOfTypeAll<Camera>().FirstOrDefault(camera => camera != null
                && camera.gameObject.scene.IsValid() && camera.name == "InventoryCamera");
        }

        private static Transform FindChild(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name == name) return child;
                var found = FindChild(child, name);
                if (found != null) return found;
            }
            return null;
        }

        private static string PathOf(Transform node, Transform root)
        {
            var parts = new List<string>();
            for (var current = node; current != null && current != root; current = current.parent)
                parts.Add(current.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string BoneName(Animator animator, HumanBodyBones bone)
        {
            var transform = animator.GetBoneTransform(bone);
            return transform != null ? transform.name : "none";
        }

        private static string LayerName(int layer)
        {
            string name = LayerMask.LayerToName(layer);
            return layer + (string.IsNullOrEmpty(name) ? "" : " " + name);
        }

        private static string OnOff(int mask, int layer) => (mask & (1 << layer)) != 0 ? "on" : "off";
        private static string Hex(int value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture);
        private static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        private static string V(Vector3 value) => "(" + F(value.x) + "," + F(value.y) + "," + F(value.z) + ")";
        private static string C(Color value) => "(" + F(value.r) + "," + F(value.g) + "," + F(value.b) + "," + F(value.a) + ")";

        private sealed class Provider : IConsoleCommandProvider
        {
            public string Id => ProviderId;
            public string DisplayName => "Preview Stage";
            public string Kind => MenuProviderKinds.CommandGroup;
            public int Priority => 340;
            public string Source => "Modstoke Loader";
            public string Description => "Rendering and player rig facts for the character preview stage, and live light tuning.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;
            public string ConsolePrefix => "stage";

            public IReadOnlyList<CommandSnapshot> GetCommands() => new[]
            {
                new CommandSnapshot(ProbeCommandId, "Probe",
                    "Write layers, pipeline, lights, cameras and the local player's renderers and rig to the Loader log"
                    + " and Modstoke/UserData/PreviewStage. " + _lastDetail,
                    _lastStatus, !GameBuild.IsDedicatedServer, false),
                new CommandSnapshot(TestCommandId, "Test render",
                    "Render a throwaway stage with a test cube once and write the image to Modstoke/UserData/PreviewStage."
                    + " Pass \"red\" to add a red local light with the sun's light layers, or \"sun\" a red directional"
                    + " light, that must not reach the cube, or \"capture\" to copy the local player and write body,"
                    + " face and hands images.",
                    _lastStatus, !GameBuild.IsDedicatedServer && PreviewStage.Current == null, false),
                new CommandSnapshot(LightCommandId, "Light",
                    "Tune the open stage live: ev <EV100>, key, fill and rim <lux at the subject> and ambient <lux>, in"
                    + " any order and any subset. With no arguments it prints the current values. Changes last until"
                    + " the stage closes and touch nothing else.",
                    PreviewStage.Current != null ? "READY" : "CHECK", !GameBuild.IsDedicatedServer, false)
            };

            public bool TryInvokeCommand(string commandId, out CommandResultSnapshot result) =>
                TryInvoke(commandId, "", out result);

            public bool TryInvokeCommand(string commandId, CommandInvocation invocation, out CommandResultSnapshot result) =>
                TryInvoke(commandId, invocation?.Arguments, out result);
        }
    }
}
