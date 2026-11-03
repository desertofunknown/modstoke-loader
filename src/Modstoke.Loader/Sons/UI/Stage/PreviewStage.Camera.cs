// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using System.Linq;
using Modstoke.Loader;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;

namespace Modstoke.Sons
{
    public sealed partial class PreviewStage
    {
        private const float FieldOfView = 30f;
        private const int DefaultWidth = 900;
        private const int DefaultHeight = 1000;
        private const int MinimumSize = 16;
        private const int ResizeThreshold = 8;
        private const float ResizeDelay = .2f;
        private const int MissedFrameLimit = 10;
        private const int ReadbackFrameLimit = 240;
        // EV100 of a lit studio or a bright overcast day. The rig's lux and the sky's ambient are authored for it.
        private const float StageExposure = 10f;
        // Lux from the stage sky on any surface, so the side away from the key is filled rather than black.
        private const float StageAmbient = 800f;
        // HDRP reads a changed sky's ambient probe back from the GPU a few frames later; until then the copy gets
        // a stale or black one.
        private const int AmbientSettleFrames = 6;
        private const float VolumePriority = 10000f;
        // World, history and lens effects only. Lit shader mode, decals, probe volumes, subsurface scattering and
        // the other features that pick material shader variants follow the game's camera, because the build only
        // kept the variants the game's own settings use.
        private static readonly FrameSettingsField[] DisabledEffects =
        {
            FrameSettingsField.AtmosphericScattering, FrameSettingsField.Volumetrics,
            FrameSettingsField.VolumetricClouds, FrameSettingsField.ReflectionProbe,
            FrameSettingsField.PlanarProbe, FrameSettingsField.SSR, FrameSettingsField.TransparentSSR,
            FrameSettingsField.SSGI, FrameSettingsField.RayTracing,
            FrameSettingsField.CustomPass, FrameSettingsField.CustomPostProcess,
            FrameSettingsField.Water, FrameSettingsField.MotionVectors,
            FrameSettingsField.ObjectMotionVectors, FrameSettingsField.MotionBlur,
            FrameSettingsField.DepthOfField, FrameSettingsField.ChromaticAberration,
            FrameSettingsField.LensDistortion, FrameSettingsField.PaniniProjection,
            FrameSettingsField.FilmGrain, FrameSettingsField.Vignette,
            FrameSettingsField.Bloom, FrameSettingsField.LensFlareDataDriven
        };
        // Sky reflection reflects the stage's own sky, never the world's.
        private static readonly FrameSettingsField[] EnabledEffects =
        {
            FrameSettingsField.OpaqueObjects, FrameSettingsField.TransparentObjects,
            FrameSettingsField.Postprocess, FrameSettingsField.Tonemapping,
            FrameSettingsField.ExposureControl, FrameSettingsField.SkyReflection
        };

        private Il2CppSystem.Action<ScriptableRenderContext, Camera> _beforeCamera;
        private Il2CppSystem.Action<ScriptableRenderContext, Camera> _afterCamera;
        private bool _beginSubscribed;
        private bool _endSubscribed;
        private GameObject _cameraObject;
        private Camera _camera;
        private IntPtr _cameraPointer;
        private RenderTexture _target;
        private int _width;
        private int _height;
        private int _requestedWidth;
        private int _requestedHeight;
        private float _resizeAt = -1f;
        private bool _dirty;
        private bool _pending;
        private bool _rendered;
        private int _scheduledFrame;
        private int _renderedFrame;
        private int _missedFrames;
        private float _nextFrameAt;
        private DateTimeOffset? _lastRender;
        private Exception _renderFailure;
        private string _frameSource = "default";
        private string _litShaderMode = "";
        private string _exposure = "";
        private bool _reportSettings = true;
        private int _settleFrames;
        private VolumeProfile _profile;
        private Exposure _stageExposure;
        private GradientSky _stageSky;
        private Action<byte[], string> _readback;
        private int _readbackDeadline;

        /// <summary>Sets the displayed size in pixels. The texture follows after a short debounce.</summary>
        public void SetViewportPixels(int width, int height)
        {
            if (!Usable() || width <= 0 || height <= 0) return;
            ClampSize(width, height, out int targetWidth, out int targetHeight);
            if (Math.Abs(targetWidth - _width) <= ResizeThreshold && Math.Abs(targetHeight - _height) <= ResizeThreshold)
            {
                _resizeAt = -1f;
                return;
            }
            if (_resizeAt >= 0f && targetWidth == _requestedWidth && targetHeight == _requestedHeight) return;
            _requestedWidth = targetWidth;
            _requestedHeight = targetHeight;
            _resizeAt = Time.unscaledTime + ResizeDelay;
        }

        // Writes the next completed frame as PNG bytes, or reports why none arrived.
        internal void RequestReadback(Action<byte[], string> completed)
        {
            if (!Usable(out string status))
            {
                completed(null, status);
                return;
            }
            if (_readback != null)
            {
                completed(null, "A readback is already waiting for a frame.");
                return;
            }
            _readback = completed;
            _readbackDeadline = Time.frameCount + ReadbackFrameLimit;
            _dirty = true;
        }

        private void CreateCamera()
        {
            _cameraObject = new GameObject("Camera") { layer = _layer };
            _cameraObject.transform.SetParent(_root.transform, false);
            _camera = _cameraObject.AddComponent<Camera>();
            _cameraPointer = _camera.Pointer;
            _camera.enabled = false;
            _camera.orthographic = false;
            _camera.fieldOfView = FieldOfView;
            _camera.depth = -20;
            _camera.cullingMask = 1 << _layer;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = _background;
            _camera.allowHDR = false;
            _camera.allowMSAA = false;
            _camera.allowDynamicResolution = false;
            _camera.useOcclusionCulling = false;
            _camera.stereoTargetEye = StereoTargetEyeMask.None;
            var data = _cameraObject.GetComponent<HDAdditionalCameraData>();
            if (data == null) data = _cameraObject.AddComponent<HDAdditionalCameraData>();
            ConfigurePipeline(data);
            CreateVolume();

            ClampSize(DefaultWidth, DefaultHeight, out int width, out int height);
            SwapTarget(width, height);

            _beforeCamera = (Action<ScriptableRenderContext, Camera>)BeforeCamera;
            _afterCamera = (Action<ScriptableRenderContext, Camera>)AfterCamera;
            _beginSubscribed = true;
            RenderPipelineManager.add_beginCameraRendering(_beforeCamera);
            _endSubscribed = true;
            RenderPipelineManager.add_endCameraRendering(_afterCamera);
        }

        private void ConfigurePipeline(HDAdditionalCameraData data)
        {
            // HDRP clears to this after exposure, so the grey holds at any EV; only the tonemapper darkens it a little.
            data.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
            data.backgroundColorHDR = _background;
            data.clearDepth = true;
            // On-demand frames have no history, so TAA is never usable here.
            data.antialiasing = HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            data.probeLayerMask = (LayerMask)0;
            data.volumeAnchorOverride = _root.transform;
            // The camera is off between frames; without this HDRP rebuilds its HDCamera and volume stack every frame.
            data.hasPersistentHistory = true;
            data.allowDynamicResolution = false;
            data.allowDeepLearningSuperSampling = false;

            var source = FindFrameSource();
            // The game's volumes keep the copy's grading and diffusion profiles. The stage volume then pins
            // exposure, tonemapping and the sky on top of them.
            int volumes = source != null ? source.volumeLayerMask.value : 0;
            data.volumeLayerMask = (LayerMask)(volumes | 1 << _layer);
            if (source != null && source.customRenderingSettings)
            {
                // Assignment copies the native values, so the edits below change only the stage's copy.
                data.m_RenderingPathCustomFrameSettings = source.m_RenderingPathCustomFrameSettings;
                data.renderingPathCustomFrameSettingsOverrideMask = source.renderingPathCustomFrameSettingsOverrideMask;
            }
            else
            {
                if (source != null)
                {
                    _frameSource += " (asset defaults)";
                }
                data.m_RenderingPathCustomFrameSettings = FrameSettings.NewDefaultCamera();
                data.renderingPathCustomFrameSettingsOverrideMask = new FrameSettingsOverrideMask();
            }
            var settings = data.m_RenderingPathCustomFrameSettings;
            var mask = data.renderingPathCustomFrameSettingsOverrideMask;
            foreach (var field in DisabledEffects)
            {
                settings.SetEnabled(field, false);
                mask.mask[(uint)field] = true;
            }
            foreach (var field in EnabledEffects)
            {
                settings.SetEnabled(field, true);
                mask.mask[(uint)field] = true;
            }
            if (_lightLayers)
            {
                settings.SetEnabled(FrameSettingsField.LightLayers, true);
                mask.mask[(uint)FrameSettingsField.LightLayers] = true;
            }
            data.m_RenderingPathCustomFrameSettings = settings;
            data.renderingPathCustomFrameSettingsOverrideMask = mask;
            data.customRenderingSettings = true;
        }

        // The InventoryCamera already renders the player well; the main camera renders it in the world.
        private HDAdditionalCameraData FindFrameSource()
        {
            Camera inventory = null;
            try { inventory = PreviewStageProbe.FindInventoryCamera(); }
            catch { }
            var source = inventory != null ? inventory.GetComponent<HDAdditionalCameraData>() : null;
            if (source != null)
            {
                _frameSource = "inventory-camera";
                return source;
            }
            var main = Camera.main;
            source = main != null ? main.GetComponent<HDAdditionalCameraData>() : null;
            _frameSource = source != null ? "main-camera" : "default";
            return source;
        }

        // Local to the stage origin, where only the stage camera anchors its volumes, so no other camera's
        // exposure or lighting changes whatever volume layers it reads.
        private void CreateVolume()
        {
            var node = new GameObject("Volume") { layer = _layer };
            node.transform.SetParent(_root.transform, false);
            var box = node.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = Vector3.one;
            _profile = ScriptableObject.CreateInstance<VolumeProfile>();
            _profile.name = "Modstoke preview stage";
            _profile.hideFlags = HideFlags.HideAndDontSave;
            _stageExposure = _profile.Add<Exposure>(false);
            _stageExposure.active = true;
            _stageExposure.mode.Override(ExposureMode.Fixed);
            _stageExposure.fixedExposure.Override(StageExposure);
            _stageExposure.compensation.Override(0f);
            var grading = _profile.Add<ColorAdjustments>(false);
            grading.active = true;
            grading.postExposure.Override(0f);
            // The main camera has HDRP tonemapping off and the stage skips custom post-processes, so this is the
            // copy's only tonemapper. Neutral keeps skin hue and reaches white only at 5.3, far above lit skin.
            var tonemapping = _profile.Add<Tonemapping>(false);
            tonemapping.active = true;
            tonemapping.mode.Override(TonemappingMode.Neutral);
            // A uniform white sky replaces the world's, whose ambient changes with the time of day and weather.
            // Dynamic ambient takes the probe from the sky in this camera's own volume stack, never the baked
            // static sky, and HDRP sets it per camera, so no other camera's ambient changes.
            var environment = _profile.Add<VisualEnvironment>(false);
            environment.active = true;
            environment.skyType.Override((int)SkyType.Gradient);
            environment.cloudType.Override(0);
            environment.skyAmbientMode.Override(SkyAmbientMode.Dynamic);
            _stageSky = _profile.Add<GradientSky>(false);
            _stageSky.active = true;
            _stageSky.top.Override(Color.white);
            _stageSky.middle.Override(Color.white);
            _stageSky.bottom.Override(Color.white);
            _stageSky.skyIntensityMode.Override(SkyIntensityMode.Multiplier);
            // Luminance in nits; a uniform sky of L gives pi * L lux on every surface.
            _stageSky.multiplier.Override(StageAmbient / Mathf.PI);
            var indirect = _profile.Add<IndirectLightingController>(false);
            indirect.active = true;
            indirect.indirectDiffuseLightingMultiplier.Override(1f);
            indirect.reflectionLightingMultiplier.Override(1f);
            _settleFrames = AmbientSettleFrames;
            var volume = node.AddComponent<Volume>();
            volume.isGlobal = false;
            volume.priority = VolumePriority;
            volume.weight = 1f;
            volume.blendDistance = 0f;
            volume.sharedProfile = _profile;
        }

        private void ReleaseVolume()
        {
            var profile = _profile;
            _profile = null;
            _stageExposure = null;
            _stageSky = null;
            if (!Live(profile)) return;
            var components = profile.components;
            for (int i = 0; i < components.Count; i++)
                if (Live(components[i])) Object.Destroy(components[i]);
            components.Clear();
            Object.Destroy(profile);
        }

        // Reports what HDRP rendered with after the asset sanitised the frame settings and the volumes blended.
        private void ReadEffectiveSettings(Camera camera)
        {
            string subsurface, tonemapping;
            try
            {
                var hd = HDCamera.GetOrCreate(camera, 0);
                var frame = hd.frameSettings;
                _litShaderMode = frame.litShaderMode == LitShaderMode.Deferred ? "deferred" : "forward";
                subsurface = frame.IsEnabled(FrameSettingsField.SubsurfaceScattering) ? "on" : "off";
                var tonemapper = hd.volumeStack.GetComponent<Tonemapping>();
                tonemapping = !frame.IsEnabled(FrameSettingsField.Tonemapping) ? "off"
                    : tonemapper != null ? tonemapper.mode.value.ToString().ToLowerInvariant() : "unknown";
                var exposure = hd.volumeStack.GetComponent<Exposure>();
                if (!frame.IsEnabled(FrameSettingsField.ExposureControl)) _exposure = "off";
                else if (exposure == null) _exposure = "unknown";
                else if (exposure.mode.value != ExposureMode.Fixed) _exposure = exposure.mode.value.ToString().ToLowerInvariant();
                else _exposure = "fixed " + exposure.fixedExposure.value.ToString("0.###", CultureInfo.InvariantCulture) + " EV";
            }
            catch (Exception error)
            {
                _litShaderMode = "unknown";
                _exposure = "unknown";
                tonemapping = "unknown";
                subsurface = "unknown (" + error.Message + ")";
            }
            RuntimeEntryPoint.LogSource?.LogInfo("Preview stage for " + _ownerId + " renders " + _litShaderMode
                + " · exposure " + _exposure + " · tonemapping " + tonemapping + " · subsurface " + subsurface + ".");
        }

        private void ClampSize(int width, int height, out int clampedWidth, out int clampedHeight)
        {
            float w = width * _qualityScale;
            float h = height * _qualityScale;
            // The texture never exceeds the screen, so HDRP's shared RTHandles never grow for it.
            float fit = Mathf.Min(1f, Mathf.Min(Mathf.Max(Screen.width, MinimumSize) / w, Mathf.Max(Screen.height, MinimumSize) / h));
            clampedWidth = Math.Max(MinimumSize, Mathf.RoundToInt(w * fit));
            clampedHeight = Math.Max(MinimumSize, Mathf.RoundToInt(h * fit));
        }

        private void UpdateTarget()
        {
            if (_resizeAt < 0f || _pending || Time.unscaledTime < _resizeAt) return;
            _resizeAt = -1f;
            SwapTarget(_requestedWidth, _requestedHeight);
            RefreshFocus();
            Publish();
        }

        private void SwapTarget(int width, int height)
        {
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = "Modstoke preview stage",
                antiAliasing = 1,
                useMipMap = false,
                autoGenerateMips = false
            };
            if (!target.Create())
            {
                Object.Destroy(target);
                throw new InvalidOperationException("The preview stage render target could not be created.");
            }
            var previous = _target;
            _camera.targetTexture = target;
            _camera.ResetAspect();
            _target = target;
            _width = width;
            _height = height;
            _dirty = true;
            if (Live(previous))
            {
                previous.Release();
                Object.Destroy(previous);
            }
            if (previous == null) return;
            try { TextureChanged?.Invoke(target); }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Preview stage TextureChanged handler failed: " + error.Message);
            }
        }

        private void UpdateFrame()
        {
            if (!_pending)
            {
                if (_readback != null && Time.frameCount > _readbackDeadline)
                    CompleteReadback("no frame was rendered (is the game window focused?)");
                return;
            }
            if (Time.frameCount <= _scheduledFrame) return;
            _camera.enabled = false;
            if (!_rendered)
            {
                // HDRP skipped the camera; ask again rather than waiting forever.
                _pending = false;
                _dirty = true;
                if (++_missedFrames == MissedFrameLimit)
                {
                    _status = "render-missed";
                    Publish();
                }
                return;
            }
            if (Time.frameCount <= _renderedFrame) return;
            _pending = false;
            _rendered = false;
            if (_missedFrames >= MissedFrameLimit) _status = "ready";
            _missedFrames = 0;
            Publish();
            // A readback waits for the settled frame, so test images never show the stale ambient.
            if (_settleFrames > 0 && --_settleFrames > 0) _dirty = true;
            else if (_readback != null) CompleteReadback(null);
        }

        private void ScheduleFrame()
        {
            if (_pending || !_visible || !Application.isFocused) return;
            float now = Time.unscaledTime;
            bool due = _dirty || _readback != null || _renderMode == PreviewRenderMode.Continuous;
            // Settle frames run back to back, so a low ContinuousMaxHz can't hold a readback past its deadline.
            if (!due || (now < _nextFrameAt && _settleFrames == 0)) return;
            _nextFrameAt = now + 1f / _continuousMaxHz;
            _dirty = false;
            _rendered = false;
            _pending = true;
            _scheduledFrame = Time.frameCount;
            _camera.enabled = true;
        }

        private void AfterCamera(ScriptableRenderContext context, Camera camera)
        {
            if (_disposed || Environment.CurrentManagedThreadId != _ownerThread) return;
            try
            {
                if (camera == null || camera.Pointer != _cameraPointer) return;
                SetGate(false);
                _camera.enabled = false;
                if (!_pending) return;
                _rendered = true;
                _renderedFrame = Time.frameCount;
                _lastRender = DateTimeOffset.UtcNow;
                if (_reportSettings)
                {
                    _reportSettings = false;
                    ReadEffectiveSettings(camera);
                }
            }
            catch (Exception error)
            {
                _renderFailure ??= error;
                try { if (Live(_camera)) _camera.enabled = false; }
                catch { }
            }
        }

        private void CompleteReadback(string failure)
        {
            var completed = _readback;
            _readback = null;
            byte[] png = null;
            if (failure == null)
            {
                try { png = ReadPng(); }
                catch (Exception error) { failure = error.Message; }
            }
            try { completed(png, failure); }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Preview stage readback handler failed: " + error.Message);
            }
        }

        private byte[] ReadPng()
        {
            var pixels = new Texture2D(_width, _height, TextureFormat.RGBA32, false, false);
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = _target;
                pixels.ReadPixels(new Rect(0, 0, _width, _height), 0, 0, false);
                pixels.Apply(false);
                var encoded = ImageConversion.EncodeToPNG(pixels);
                return encoded != null ? encoded.ToArray() : null;
            }
            finally
            {
                RenderTexture.active = previous;
                Object.Destroy(pixels);
            }
        }

        private void Unsubscribe()
        {
            Exception failure = null;
            if (_beginSubscribed)
            {
                _beginSubscribed = false;
                try { RenderPipelineManager.remove_beginCameraRendering(_beforeCamera); }
                catch (Exception error) { failure = error; }
            }
            if (_endSubscribed)
            {
                _endSubscribed = false;
                try { RenderPipelineManager.remove_endCameraRendering(_afterCamera); }
                catch (Exception error) { failure ??= error; }
            }
            if (failure != null) throw failure;
        }
    }
}
