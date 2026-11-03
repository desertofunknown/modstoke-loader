// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Environment;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        // All fields are values. Timer starts and water-cache frames are normalized to the
        // destination clock; no source process clock, callbacks or audio handles are imported.
        private sealed record CartLifecycleData(CartTimerState[] Timers, CartWaterState[] Water,
            CartFloatState[] Floating, string[] Paths, bool[] Enabled, float Cloud, long TimeTicks,
            bool Lights, bool DistanceActive, bool Kinematic, bool Shove, int Zoom, int TerrainAudio,
            System.Numerics.Quaternion WaterRotation);

        private sealed partial class CartLocalState
        {
            internal CartLifecycleData Export()
            {
                var timers = new CartTimerState[_timers.Length];
                for (int i = 0; i < timers.Length; i++) timers[i] = _timers[i] with { Start = 0 };
                var water = new CartWaterState[_water.Length];
                for (int i = 0; i < water.Length; i++)
                    if (_water[i] is { } value)
                        water[i] = value with { CacheFrame = value.CacheFrame < 0 ? int.MinValue : value.CacheFrame - value.CapturedFrame,
                            CaveFrame = value.CaveFrame < 0 ? int.MinValue : value.CaveFrame - value.CapturedFrame, CapturedFrame = 0 };
                return new(timers, water, (CartFloatState[])_floating.Clone(), (string[])_paths.Clone(),
                    (bool[])_enabled.Clone(), _cloud, _time.Ticks, _lights, _distanceActive, _kinematic,
                    _shove, _zoom, _terrainAudio, _waterRotation);
            }

            internal static void Validate(CartLifecycleData data)
            {
                Require(data != null && data.Timers?.Length == 3 && data.Paths != null
                    && data.Paths.Length is > 0 and <= 32 && data.Enabled?.Length == data.Paths.Length
                    && data.Water?.Length == data.Paths.Length && data.Floating?.Length == data.Paths.Length,
                    "Cart lifecycle schema is incomplete or exceeds its bound.");
                foreach (var timer in data.Timers)
                    Require(timer != null && timer.Start == 0 && float.IsFinite(timer.Interval) && timer.Interval >= 0
                        && float.IsFinite(timer.Elapsed) && timer.Elapsed >= 0, "Unmapped or invalid cart timer.");
                for (int i = 0; i < data.Paths.Length; i++)
                {
                    Require(data.Paths[i] != null && data.Paths[i].Length <= 512
                        && (i == 0 ? data.Water[i] == null && data.Floating[i] == null
                            : (data.Water[i] == null) != (data.Floating[i] == null)), "Invalid native writer topology.");
                    if (data.Water[i] is { } water)
                        Require(water.CapturedFrame == 0 && water.CacheFrame <= 0 && water.CaveFrame <= 0,
                            "Water-cache frame ages are invalid.");
                }
                Require(data.TimeTicks >= 0 && data.TimeTicks <= TimeSpan.MaxValue.Ticks
                    && data.WaterRotation.LengthSquared() > 0.99f && data.WaterRotation.LengthSquared() < 1.01f,
                    "Cart time or water rotation is invalid.");
            }

            private CartLocalState(CartLifecycleData data)
            {
                Validate(data);
                _timers = new CartTimerState[3];
                for (int i = 0; i < 3; i++) _timers[i] = data.Timers[i] with { Start = Finite(Time.time - data.Timers[i].Elapsed) };
                _water = new CartWaterState[data.Water.Length];
                for (int i = 0; i < _water.Length; i++)
                    if (data.Water[i] is { } water)
                        _water[i] = water with { CapturedFrame = Time.frameCount,
                            CacheFrame = RebaseFrame(water.CacheFrame), CaveFrame = RebaseFrame(water.CaveFrame) };
                _floating = (CartFloatState[])data.Floating.Clone();
                _paths = (string[])data.Paths.Clone(); _enabled = (bool[])data.Enabled.Clone();
                var time = new TimeSpan(data.TimeTicks);
                _time = new TimeOfDay(time.Days, time.Hours, time.Minutes, time.Seconds, time.Milliseconds);
                Require(_time.Ticks == data.TimeTicks, "Cart time precision cannot be reproduced.");
                _cloud = data.Cloud; _lights = data.Lights; _distanceActive = data.DistanceActive;
                _kinematic = data.Kinematic; _shove = data.Shove; _zoom = data.Zoom;
                _terrainAudio = data.TerrainAudio; _waterRotation = data.WaterRotation;
            }

            internal static CartLocalState Import(CartLifecycleData data) => new(data);

            private static int RebaseFrame(int age)
            {
                if (age == int.MinValue) return -1;
                int frame = checked(Time.frameCount + age);
                return frame < 0 ? -1 : frame;
            }
        }
    }
}
