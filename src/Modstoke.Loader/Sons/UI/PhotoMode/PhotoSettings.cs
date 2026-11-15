// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Modstoke.Loader
{
    /// <summary>
    /// What the photo mode panel is set to. The look and framing last until the player leaves the world, so a
    /// second visit starts where the first ended; the capture choices are kept in the Loader settings. The camera
    /// itself always starts at the player.
    /// </summary>
    internal sealed class PhotoSettings
    {
        private static PhotoSettings _shared;

        /// <summary>The settings for this world, made on first use.</summary>
        internal static PhotoSettings Shared => _shared ??= Load();

        /// <summary>The player left the world; the next one starts from the game's own look.</summary>
        internal static void Forget() => _shared = null;

        internal static readonly string[] Guides = { "none", "thirds", "golden", "centre", "diagonal" };
        internal static readonly string[] Aspects = { "screen", "1:1", "4:5", "3:2", "16:9", "2.39:1", "9:16" };

        // Camera. The focal length is NaN until a session sets it from the view it opened on.
        internal float Focal = float.NaN;
        internal float Roll;
        internal float Speed = 1f;
        internal bool ShowPlayer = true, ShowHeld = true;

        // Lens. NaN keeps the game's own value; the focus distance is NaN until depth of field is first switched on.
        internal bool Dof, FollowCentre;
        internal float FocusDistance = float.NaN;
        internal int Aperture = Array.IndexOf(PhotoLens.Stops, 2.8f);
        internal float Bokeh = 2f;
        internal float Vignette = float.NaN, VignetteSmoothness = float.NaN, Chromatic = float.NaN, Bloom = float.NaN;
        internal float Distortion, Panini;

        // Look: the filter's colour layer and the player's own changes, both added to the game's grade.
        internal string Preset = "none";
        internal float Strength = 1f;
        internal float Exposure, Contrast, Saturation, Temperature, Tint;
        internal float Grain = float.NaN, GrainResponse = float.NaN;
        internal int GrainType = -1;
        internal bool ScreenEffects;

        // Frame.
        internal string Guide = "none", Aspect = "screen";
        internal bool BlackMask;

        // Capture, kept in the Loader settings.
        internal string Resolution;
        internal bool Fine = true, Pictures = true, Steam = true;

        internal PhotoPreset Filter => PhotoPresets.Find(Preset);

        /// <summary>The frame's width over its height, or null for the screen's own shape.</summary>
        internal float? AspectRatio
        {
            get
            {
                if (Aspect == "screen") return null;
                var parts = Aspect.Split(':');
                return parts.Length == 2 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
                    && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var height) && height > 0
                    ? width / height
                    : null;
            }
        }

        /// <summary>Takes up a filter: its colour layer, and its lens values written into their rows.</summary>
        internal void Choose(PhotoPreset preset)
        {
            Preset = preset.Id;
            if (preset.Vignette is { } vignette) Vignette = vignette;
            if (preset.VignetteSmoothness is { } smoothness) VignetteSmoothness = smoothness;
            if (preset.Bloom is { } bloom) Bloom = bloom;
            if (preset.Grain is { } grain) Grain = grain;
            if (preset.GrainResponse is { } response) GrainResponse = response;
            if (preset.GrainType is { } type) GrainType = type;
            if (preset.Chromatic is { } chromatic) Chromatic = chromatic;
            if (preset.Distortion is { } distortion) Distortion = distortion;
        }

        /// <summary>The filter, colour and lens back as the game shows them.</summary>
        internal void ResetLook()
        {
            var fresh = new PhotoSettings();
            Dof = fresh.Dof;
            FollowCentre = fresh.FollowCentre;
            Aperture = fresh.Aperture;
            Bokeh = fresh.Bokeh;
            Vignette = VignetteSmoothness = Chromatic = Bloom = float.NaN;
            Distortion = Panini = 0;
            Preset = fresh.Preset;
            Strength = fresh.Strength;
            Exposure = Contrast = Saturation = Temperature = Tint = 0;
            Grain = GrainResponse = float.NaN;
            GrainType = -1;
            ScreenEffects = false;
        }

        /// <summary>A snapshot of every setting, for a photo's own record of how it was made.</summary>
        internal Dictionary<string, object> Describe() => new()
        {
            ["focalLength"] = Focal, ["roll"] = Roll, ["player"] = ShowPlayer, ["heldItem"] = ShowHeld,
            ["depthOfField"] = Dof, ["followCentre"] = FollowCentre, ["focusDistance"] = FocusDistance,
            ["aperture"] = PhotoLens.Stops[Math.Clamp(Aperture, 0, PhotoLens.Stops.Length - 1)], ["blurStrength"] = Bokeh,
            ["vignette"] = Vignette, ["chromatic"] = Chromatic, ["distortion"] = Distortion, ["bloom"] = Bloom, ["panini"] = Panini,
            ["filter"] = Preset, ["filterStrength"] = Strength, ["exposure"] = Exposure, ["contrast"] = Contrast,
            ["saturation"] = Saturation, ["temperature"] = Temperature, ["tint"] = Tint, ["grain"] = Grain, ["grainType"] = GrainType,
            ["screenEffects"] = ScreenEffects, ["aspect"] = Aspect, ["resolution"] = Resolution, ["fine"] = Fine
        };

        internal void SaveCapture()
        {
            Write("photoResolution", Resolution ?? "");
            Write("photoDetail", Fine ? "fine" : "standard");
            Write("photoFolder", Pictures ? "pictures" : "modstoke");
            Write("photoSteam", Steam ? "true" : "false");
        }

        private static PhotoSettings Load() => new()
        {
            Resolution = RuntimePreferences.ReadText("photoResolution", ""),
            Fine = RuntimePreferences.ReadText("photoDetail", "fine") != "standard",
            Pictures = RuntimePreferences.ReadText("photoFolder", "pictures") != "modstoke",
            Steam = RuntimePreferences.ReadBoolean("photoSteam", true)
        };

        private static void Write(string key, string value)
        {
            if (!RuntimePreferences.WriteText(key, value, out var error))
                RuntimeEntryPoint.LogSource?.LogWarning("Photo mode could not keep " + key + ": " + error);
        }
    }
}
