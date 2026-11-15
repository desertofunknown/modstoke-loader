// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// Photo mode's look: one global volume above every game volume (its menu blur at 1000 and cutscene fades at
    /// 100 included). Each setting is switched on only while it changes something, so at rest the game's own
    /// grade, the player's brightness and gamma, and time of day all show through. Colour sliders add to the
    /// game's blended values as they stood once the pause menu's blur had gone; motion blur is always off and
    /// exposure no longer adapts, as the paused world would freeze its adaptation half way.
    /// </summary>
    internal sealed class PhotoLook : IDisposable
    {
        private const float Priority = 2000f;
        // How far light carries in the World tab's thickest fog, in metres.
        private const float ThickFog = 10f;
        // HDRP's quality level that means "use the values set on this override".
        private const int CustomQuality = 3;
        private static readonly Color Neutral = new(0.5f, 0.5f, 0.5f, 1f);
        private static readonly Vector4 Unchanged = new(1, 1, 1, 0);
        private static readonly float[] Identity = { 100, 0, 0, 0, 100, 0, 0, 0, 100 };

        private readonly Camera _camera;
        private readonly GameObject _root;
        private readonly VolumeProfile _profile;
        private readonly ColorAdjustments _color;
        private readonly WhiteBalance _white;
        private readonly Exposure _exposure;
        private readonly DepthOfField _focus;
        private readonly Vignette _vignette;
        private readonly FilmGrain _grain;
        private readonly ChromaticAberration _fringe;
        private readonly Bloom _bloom;
        private readonly LensDistortion _distortion;
        private readonly PaniniProjection _panini;
        private readonly SplitToning _split;
        private readonly ChannelMixer _mixer;
        private readonly LiftGammaGain _liftGain;
        private readonly MotionBlur _motion;
        private readonly Fog _fog;
        private readonly BloodColdPostProcess _screen;
        private readonly SonsVHSTimecodePostProcess _timecode;
        private readonly ClampedFloatParameter[] _channels;
        private int _clearFrame = -1;
        private bool? _tintsHeld;

        internal PhotoLook(Camera camera)
        {
            _camera = camera;
            // A first reading, for the rows to show until the pause menu's blur has gone and the base is read again.
            ReadBase();
            _root = new GameObject("Modstoke Photo Look");
            Object.DontDestroyOnLoad(_root);
            var volume = _root.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = Priority;
            volume.weight = 1f;
            _profile = ScriptableObject.CreateInstance(Il2CppType.Of<VolumeProfile>()).Cast<VolumeProfile>();
            _profile.hideFlags = HideFlags.HideAndDontSave;
            _color = Add<ColorAdjustments>();
            _white = Add<WhiteBalance>();
            _exposure = Add<Exposure>();
            _focus = Add<DepthOfField>();
            _vignette = Add<Vignette>();
            _grain = Add<FilmGrain>();
            _fringe = Add<ChromaticAberration>();
            _bloom = Add<Bloom>();
            _distortion = Add<LensDistortion>();
            _panini = Add<PaniniProjection>();
            _split = Add<SplitToning>();
            _mixer = Add<ChannelMixer>();
            _liftGain = Add<LiftGammaGain>();
            _motion = Add<MotionBlur>();
            _fog = Add<Fog>();
            _channels = new[]
            {
                _mixer.redOutRedIn, _mixer.redOutGreenIn, _mixer.redOutBlueIn,
                _mixer.greenOutRedIn, _mixer.greenOutGreenIn, _mixer.greenOutBlueIn,
                _mixer.blueOutRedIn, _mixer.blueOutGreenIn, _mixer.blueOutBlueIn
            };
            // The game's own screen effects; photo mode works without them if they cannot be added.
            _screen = Optional<BloodColdPostProcess>();
            _timecode = Optional<SonsVHSTimecodePostProcess>();
            volume.sharedProfile = _profile;
        }

        /// <summary>
        /// Whether the base values below come from a frame without the pause menu's blur, and with the low-health
        /// and drowning tints as the player has them. Until then the grade holds back, so nothing builds on the
        /// pause menu's own look.
        /// </summary>
        internal bool Settled { get; private set; }

        // The game's look, blended for the main camera.
        internal float BaseContrast { get; private set; }
        internal float BaseSaturation { get; private set; }
        internal float BaseTemperature { get; private set; }
        internal float BaseTint { get; private set; }
        internal float BaseVignette { get; private set; } = 0.413f;
        internal float BaseSmoothness { get; private set; } = 0.2f;
        internal float BaseGrain { get; private set; } = 0.42f;
        internal float BaseResponse { get; private set; } = 0.8f;
        internal int BaseGrainType { get; private set; }
        internal float BaseChromatic { get; private set; } = 0.02f;
        internal float BaseBloom { get; private set; } = 0.25f;
        internal float BaseFogDistance { get; private set; } = 400f;

        /// <summary>Whether the game draws fog at all at its current settings; the World tab's fog builds on it.</summary>
        internal bool FogShown { get; private set; } = true;

        /// <summary>The lens as rendered this frame, for depth of field.</summary>
        internal float Focal { get; set; } = 17f;
        internal float FocusDistance { get; set; } = 10f;

        /// <summary>
        /// Reads the game's look again once a frame has rendered with the pause menu's blur gone
        /// (<paramref name="clear"/>) and the grade held back, and again whenever the tints are switched, as they
        /// are part of what the sliders add to. True when the base was just read.
        /// </summary>
        internal bool Settle(bool clear, bool tintsHeld)
        {
            if (_tintsHeld != tintsHeld)
            {
                _tintsHeld = tintsHeld;
                Settled = false;
                _clearFrame = -1;
            }
            if (Settled) return false;
            if (!clear)
            {
                _clearFrame = -1;
                return false;
            }
            // The camera's stack holds the last frame rendered: this frame renders clear, the next one reads it.
            var frame = Time.frameCount;
            if (_clearFrame < 0) _clearFrame = frame;
            if (frame <= _clearFrame) return false;
            ReadBase();
            Settled = true;
            return true;
        }

        // HDRP keeps a volume stack per camera, so the main camera's is the one to read.
        private void ReadBase()
        {
            try
            {
                var stack = HDCamera.GetOrCreate(_camera, 0).volumeStack;
                if (stack == null) return;
                if (Stacked<ColorAdjustments>(stack) is { } color)
                {
                    BaseContrast = color.contrast.value;
                    BaseSaturation = color.saturation.value;
                }
                if (Stacked<WhiteBalance>(stack) is { } white)
                {
                    BaseTemperature = white.temperature.value;
                    BaseTint = white.tint.value;
                }
                if (Stacked<Vignette>(stack) is { } vignette)
                {
                    BaseVignette = vignette.intensity.value;
                    BaseSmoothness = vignette.smoothness.value;
                }
                if (Stacked<FilmGrain>(stack) is { } grain)
                {
                    BaseGrain = grain.intensity.value;
                    BaseResponse = grain.response.value;
                    BaseGrainType = (int)grain.type.value;
                }
                if (Stacked<ChromaticAberration>(stack) is { } fringe) BaseChromatic = fringe.intensity.value;
                if (Stacked<Bloom>(stack) is { } bloom) BaseBloom = bloom.intensity.value;
                if (Stacked<Fog>(stack) is { } fog)
                {
                    FogShown = fog.enabled.value;
                    BaseFogDistance = fog.meanFreePath.value;
                }
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Photo mode could not read the game's look; its sliders start from typical values: " + error.Message);
            }
        }

        internal string DescribeBase() => string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "contrast {0:0.#}, saturation {1:0.#}, temperature {2:0.#}, tint {3:0.#}, vignette {4:0.###}, grain {5:0.###} (type {6}), "
            + "colour fringing {7:0.###}, bloom {8:0.###}", BaseContrast, BaseSaturation, BaseTemperature, BaseTint, BaseVignette,
            BaseGrain, BaseGrainType, BaseChromatic, BaseBloom);

        /// <summary>Writes the settings into the volume; a photo renders with the same look as the preview.</summary>
        internal void Apply(PhotoSettings settings)
        {
            var preset = settings.Filter;
            var strength = preset.Id == "none" ? 0f : Mathf.Clamp01(settings.Strength);

            _motion.intensity.overrideState = true;
            _motion.intensity.value = 0f;
            _exposure.adaptationMode.overrideState = true;
            _exposure.adaptationMode.value = AdaptationMode.Fixed;
            Set(_exposure.compensation, settings.Exposure, settings.Exposure != 0);

            // The grade builds on the base values, so it waits for them.
            var graded = Settled;
            var contrast = strength * preset.Contrast + settings.Contrast;
            Set(_color.contrast, Mathf.Clamp(BaseContrast + contrast, -100, 100), graded && contrast != 0);
            var saturation = (preset.SaturationTo is { } to ? Mathf.Lerp(BaseSaturation, to, strength) : BaseSaturation + strength * preset.Saturation)
                + settings.Saturation;
            Set(_color.saturation, Mathf.Clamp(saturation, -100, 100), graded && !Mathf.Approximately(saturation, BaseSaturation));
            var temperature = strength * preset.Temperature + settings.Temperature;
            Set(_white.temperature, Mathf.Clamp(BaseTemperature + temperature, -100, 100), graded && temperature != 0);
            var tint = strength * preset.Tint + settings.Tint;
            Set(_white.tint, Mathf.Clamp(BaseTint + tint, -100, 100), graded && tint != 0);

            SetKept(_vignette.intensity, settings.Vignette, graded);
            SetKept(_vignette.smoothness, settings.VignetteSmoothness, graded);
            SetKept(_grain.intensity, settings.Grain, graded);
            SetKept(_grain.response, settings.GrainResponse, graded);
            _grain.type.overrideState = graded && settings.GrainType >= 0;
            if (settings.GrainType >= 0) _grain.type.value = (FilmGrainLookup)settings.GrainType;
            SetKept(_fringe.intensity, settings.Chromatic, graded);
            SetKept(_bloom.intensity, settings.Bloom, graded);
            Set(_distortion.intensity, settings.Distortion, settings.Distortion != 0);
            Set(_distortion.scale, 1f + 0.3f * Mathf.Abs(settings.Distortion), settings.Distortion != 0);
            Set(_panini.distance, settings.Panini, settings.Panini > 0);
            Set(_panini.cropToFit, 1f, settings.Panini > 0);

            // The World tab's fog shortens the distance light carries through the game's own, evenly on a log scale.
            var fog = PhotoWorld.FogAmount(settings);
            var thick = Mathf.Min(ThickFog, BaseFogDistance);
            Set(_fog.meanFreePath, BaseFogDistance * Mathf.Pow(thick / BaseFogDistance, fog), graded && FogShown && fog > 0.005f);

            var toned = graded && strength > 0 && preset.Shadows != null;
            Set(_split.shadows, Color.Lerp(Neutral, preset.Shadows ?? Neutral, strength), toned);
            Set(_split.highlights, Color.Lerp(Neutral, preset.Highlights ?? Neutral, strength), toned);
            Set(_split.balance, preset.Balance * strength, toned);
            var mixed = graded && strength > 0 && preset.Mixer != null;
            for (var i = 0; i < _channels.Length; i++)
                Set(_channels[i], mixed ? Mathf.Lerp(Identity[i], preset.Mixer[i], strength) : Identity[i], mixed);
            Set(_liftGain.lift, Blend(Unchanged, preset.Lift ?? Unchanged, strength), graded && strength > 0 && preset.Lift != null);
            Set(_liftGain.gain, Blend(Unchanged, preset.Gain ?? Unchanged, strength), graded && strength > 0 && preset.Gain != null);

            var clean = !settings.ScreenEffects;
            if (_screen != null)
            {
                Set(_screen.iceIntensity, 0f, clean);
                Set(_screen.bloodIntensity, 0f, clean);
                Set(_screen.wetlensProgress, 0f, clean);
                Set(_screen.rainAmount, 0f, clean);
            }
            if (_timecode != null) Set(_timecode.intensity, 0f, clean);

            ApplyFocus(settings);
        }

        // Always overridden, so the pause menu's own blur never reaches the shot.
        private void ApplyFocus(PhotoSettings settings)
        {
            _focus.focusMode.overrideState = true;
            _focus.focusMode.value = settings.Dof ? DepthOfFieldMode.Manual : DepthOfFieldMode.Off;
            var stop = PhotoLens.Stops[Math.Clamp(settings.Aperture, 0, PhotoLens.Stops.Length - 1)];
            var ranges = PhotoLens.Ranges(Focal, stop, FocusDistance, settings.Bokeh);
            var on = settings.Dof;
            Set(_focus.nearFocusStart, ranges.NearStart, on);
            Set(_focus.nearFocusEnd, ranges.NearEnd, on);
            Set(_focus.farFocusStart, ranges.FarStart, on);
            Set(_focus.farFocusEnd, ranges.FarEnd, on);
            // The blur, sample counts, filtering and resolution properties only write their parameter's value, so
            // each parameter is switched on here as well; left off, the stack keeps HDRP's defaults, or the pause
            // menu's blur while its volume still has weight.
            _focus.quality.overrideState = on;
            _focus.m_NearSampleCount.overrideState = on;
            _focus.m_FarSampleCount.overrideState = on;
            _focus.m_NearMaxBlur.overrideState = on;
            _focus.m_FarMaxBlur.overrideState = on;
            _focus.m_HighQualityFiltering.overrideState = on;
            _focus.m_Resolution.overrideState = on;
            if (!on) return;
            _focus.quality.value = CustomQuality;
            _focus.nearSampleCount = 8;
            _focus.farSampleCount = 16;
            _focus.nearMaxBlur = ranges.NearBlur;
            _focus.farMaxBlur = ranges.FarBlur;
            _focus.highQualityFiltering = true;
            // Half in the photo too: at full resolution this build's manual depth of field leaves the photo sharp
            // while the preview shows the blur, and the photo must match what was framed.
            _focus.resolution = DepthOfFieldResolution.Half;
        }

        private static void Set(FloatParameter parameter, float value, bool on)
        {
            parameter.overrideState = on;
            if (on) parameter.value = value;
        }

        private static void Set(ClampedFloatParameter parameter, float value, bool on)
        {
            parameter.overrideState = on;
            if (on) parameter.value = value;
        }

        private static void Set(MinFloatParameter parameter, float value, bool on)
        {
            parameter.overrideState = on;
            if (on) parameter.value = value;
        }

        private static void Set(ColorParameter parameter, Color value, bool on)
        {
            parameter.overrideState = on;
            if (on) parameter.value = value;
        }

        private static void Set(Vector4Parameter parameter, Vector4 value, bool on)
        {
            parameter.overrideState = on;
            if (on) parameter.value = value;
        }

        // Vector4.Lerp has no compiled body in this game, so the blend is written out.
        private static Vector4 Blend(Vector4 from, Vector4 to, float t)
            => new(from.x + (to.x - from.x) * t, from.y + (to.y - from.y) * t, from.z + (to.z - from.z) * t, from.w + (to.w - from.w) * t);

        // A row that keeps the game's value until the player or a filter sets one.
        private static void SetKept(ClampedFloatParameter parameter, float value, bool graded) => Set(parameter, value, graded && !float.IsNaN(value));

        private T Add<T>() where T : VolumeComponent => _profile.Add(Il2CppType.Of<T>(), false).Cast<T>();

        private T Optional<T>() where T : VolumeComponent
        {
            try
            {
                return Add<T>();
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Photo mode cannot hold back " + typeof(T).Name + ": " + error.Message);
                return null;
            }
        }

        private static T Stacked<T>(VolumeStack stack) where T : VolumeComponent
            => stack.GetComponent(Il2CppType.Of<T>())?.TryCast<T>();

        public void Dispose()
        {
            if (_root != null && _root) Object.Destroy(_root);
            if (_profile != null && _profile) Object.Destroy(_profile);
        }
    }
}
