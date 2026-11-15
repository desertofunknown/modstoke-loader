// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Loader
{
    /// <summary>A depth of field's four distances and its two blur radii, ready for HDRP's manual mode.</summary>
    internal readonly struct DofRanges
    {
        internal DofRanges(float nearStart, float nearEnd, float farStart, float farEnd, float nearBlur, float farBlur)
        {
            NearStart = nearStart;
            NearEnd = nearEnd;
            FarStart = farStart;
            FarEnd = farEnd;
            NearBlur = nearBlur;
            FarBlur = farBlur;
        }

        internal float NearStart { get; }
        internal float NearEnd { get; }
        internal float FarStart { get; }
        internal float FarEnd { get; }
        internal float NearBlur { get; }
        internal float FarBlur { get; }
    }

    /// <summary>
    /// Lens arithmetic for a full-frame camera: focal length against the vertical field of view, the usual
    /// f-stops, and thin-lens depth of field. The game's own camera is not a physical one (its stored focal
    /// length does not match its field of view), so depth of field runs in HDRP's manual mode from these numbers.
    /// </summary>
    internal static class PhotoLens
    {
        internal const float MinFocal = 12f, MaxFocal = 200f;
        // Half of a 24 mm frame height, and a full frame's circle of confusion.
        private const float HalfHeight = 12f, Confusion = 0.03f;
        // Past this the far limit counts as infinity; HDRP still wants a far range, so it is pushed out of the world.
        private const float Beyond = 10000f;

        internal static readonly float[] Stops = { 1f, 1.2f, 1.4f, 1.8f, 2f, 2.8f, 4f, 5.6f, 8f, 11f, 16f, 22f };

        /// <summary>The vertical field of view, in degrees, of a lens of <paramref name="focal"/> millimetres.</summary>
        internal static float Fov(float focal) => 2f * Mathf.Atan(HalfHeight / Mathf.Max(focal, 1f)) * Mathf.Rad2Deg;

        /// <summary>The focal length, in millimetres, that gives this vertical field of view.</summary>
        internal static float Focal(float fov)
            => Mathf.Clamp(HalfHeight / Mathf.Tan(Mathf.Clamp(fov, 1f, 170f) * 0.5f * Mathf.Deg2Rad), MinFocal, MaxFocal);

        /// <summary>
        /// Where the picture is sharp and how far it blurs either side, for a lens of <paramref name="focal"/> mm at
        /// f/<paramref name="aperture"/> focused at <paramref name="distance"/> metres; <paramref name="strength"/>
        /// scales the blur beyond what the lens alone gives.
        /// </summary>
        internal static DofRanges Ranges(float focal, float aperture, float distance, float strength)
        {
            var s = Mathf.Max(distance, 0.05f);
            var hyperfocal = focal * focal / (aperture * Confusion) / 1000f;
            var near = s * hyperfocal / (hyperfocal + s);
            var far = s < hyperfocal ? s * hyperfocal / (hyperfocal - s) : float.PositiveInfinity;
            float farStart, farEnd;
            if (float.IsInfinity(far) || far > Beyond)
            {
                farStart = Beyond;
                farEnd = Beyond + 100f;
            }
            else
            {
                farStart = far;
                farEnd = Math.Min(s + 4f * (far - s), far + 60f);
            }
            // Background blur as a share of a 1080-line frame, in HDRP's blur radius units.
            var reach = 1000f * s - focal;
            var farBlur = reach <= 0 ? 16f : 0.5f * 1080f * focal * focal / (aperture * reach) / 24f * strength;
            farBlur = Mathf.Clamp(farBlur, 0f, 16f);
            return new DofRanges(0.5f * near, near, farStart, Mathf.Max(farEnd, farStart + 0.01f), Mathf.Clamp(0.5f * farBlur, 0f, 8f), farBlur);
        }
    }
}
