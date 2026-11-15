// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using UnityEngine;

namespace Modstoke.Loader
{
    /// <summary>
    /// A finished look. Its colour layer is added to the game's own grade, scaled by the filter strength; its lens
    /// values are written into the lens and grain rows when it is chosen, so they stay in view and can be changed.
    /// </summary>
    internal sealed record PhotoPreset(string Id)
    {
        internal float Temperature { get; init; }
        internal float Tint { get; init; }
        internal float Saturation { get; init; }
        internal float Contrast { get; init; }
        /// <summary>A saturation the look moves towards rather than adds, as black and white does.</summary>
        internal float? SaturationTo { get; init; }
        internal Color? Shadows { get; init; }
        internal Color? Highlights { get; init; }
        internal float Balance { get; init; }
        /// <summary>Red, green and blue out, each from red, green and blue in, in percent; null leaves the channels alone.</summary>
        internal float[] Mixer { get; init; }
        internal Vector4? Lift { get; init; }
        internal Vector4? Gain { get; init; }

        internal float? Vignette { get; init; }
        internal float? VignetteSmoothness { get; init; }
        internal float? Bloom { get; init; }
        internal float? Grain { get; init; }
        internal float? GrainResponse { get; init; }
        /// <summary>HDRP's film grain pattern: 0-1 thin, 2-7 medium, 8-9 large.</summary>
        internal int? GrainType { get; init; }
        internal float? Chromatic { get; init; }
        internal float? Distortion { get; init; }
    }

    internal static class PhotoPresets
    {
        private static readonly float[] RedFilterMono = { 50, 40, 10, 50, 40, 10, 50, 40, 10 };

        /// <summary>The looks in the order the Filter row steps through them; "none" first.</summary>
        internal static IReadOnlyList<PhotoPreset> All { get; } = new[]
        {
            new PhotoPreset("none"),
            new PhotoPreset("goldenHour")
            {
                Temperature = 18, Tint = 4, Saturation = 15, Contrast = 8,
                Shadows = new Color(0.42f, 0.45f, 0.55f), Highlights = new Color(0.66f, 0.55f, 0.40f), Balance = 15,
                Vignette = 0.30f, VignetteSmoothness = 0.45f, Bloom = 0.35f
            },
            new PhotoPreset("noir")
            {
                SaturationTo = -100, Contrast = 40, Mixer = RedFilterMono, Gain = new Vector4(1, 1, 1, 0.05f),
                Vignette = 0.45f, VignetteSmoothness = 0.40f, GrainType = 4, Grain = 0.45f, GrainResponse = 0.6f
            },
            new PhotoPreset("bleach")
            {
                Saturation = -40, Contrast = 35, Temperature = -6, Gain = new Vector4(1, 1, 1, 0.05f), Lift = new Vector4(1, 1, 1, -0.02f),
                GrainType = 2, Grain = 0.30f, Vignette = 0.35f
            },
            new PhotoPreset("seventies")
            {
                Temperature = 25, Tint = -5, Saturation = -20, Contrast = -10, Lift = new Vector4(1f, 0.98f, 0.92f, 0.04f),
                Shadows = new Color(0.40f, 0.48f, 0.56f), Highlights = new Color(0.65f, 0.55f, 0.40f), Balance = -10,
                GrainType = 8, Grain = 0.55f, GrainResponse = 0.5f, Vignette = 0.45f, VignetteSmoothness = 0.5f, Chromatic = 0.15f
            },
            new PhotoPreset("arctic")
            {
                Temperature = -30, Tint = 5, Saturation = -10, Contrast = 10,
                Shadows = new Color(0.35f, 0.50f, 0.55f), Highlights = new Color(0.55f, 0.55f, 0.50f),
                Bloom = 0.30f, Vignette = 0.25f
            },
            new PhotoPreset("footage")
            {
                Saturation = -30, Contrast = 20, Temperature = -10, Tint = -15, Mixer = new float[] { 100, 0, 0, 0, 110, 0, 0, 0, 100 },
                GrainType = 7, Grain = 0.70f, GrainResponse = 0.4f, Chromatic = 0.35f, Vignette = 0.50f, VignetteSmoothness = 0.35f,
                Distortion = 0.15f
            }
        };

        internal static PhotoPreset Find(string id)
        {
            foreach (var preset in All)
                if (preset.Id == id) return preset;
            return All[0];
        }
    }
}
