// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using Modstoke.Loader;
using global::Sons.StatSystem;
using TheForest.Utils.Settings;
using UnityEngine;

namespace Modstoke.Sons
{
    public readonly record struct PlayerSurvivalRate(float Fade, float Regeneration);

    /// <summary>Observed native configuration owned by one survival snapshot. Not approval.</summary>
    public sealed class PlayerSurvivalSettings
    {
        internal const int EncodedLength = 9 * 8 + 8 + 8 + 16
            + 2 * PlayerSurvivalOverrideProfile.EncodedLength;
        private readonly PlayerSurvivalRate[] _rates;
        public float HealthRegenerationPerSecond { get; }
        public float ThirstyThreshold { get; }
        public bool IsAsleep { get; }
        public uint FadeDelegateMask { get; }
        public Vector2 IndoorRestGainComfortRange { get; }
        public Vector2 OutdoorRestGainComfortRange { get; }
        public PlayerSurvivalOverrideProfile AwakeOverrides { get; }
        public PlayerSurvivalOverrideProfile AsleepOverrides { get; }
        public PlayerSurvivalRate Health => _rates[0];
        public PlayerSurvivalRate Sickness => _rates[1];
        public PlayerSurvivalRate Stamina => _rates[2];
        public PlayerSurvivalRate Vitality => _rates[3];
        public PlayerSurvivalRate Hydration => _rates[4];
        public PlayerSurvivalRate Fullness => _rates[5];
        public PlayerSurvivalRate Rested => _rates[6];
        public PlayerSurvivalRate Strength => _rates[7];
        public PlayerSurvivalRate Temperature => _rates[8];
        internal PlayerSurvivalRate Rate(int index) => _rates[index];

        private PlayerSurvivalSettings(PlayerSurvivalRate[] rates, float regeneration, float thirst,
            bool asleep, uint fadeDelegateMask, Vector2 indoor, Vector2 outdoor,
            PlayerSurvivalOverrideProfile awake, PlayerSurvivalOverrideProfile sleeping)
        {
            _rates = rates; HealthRegenerationPerSecond = regeneration; ThirstyThreshold = thirst;
            IsAsleep = asleep; FadeDelegateMask = fadeDelegateMask;
            IndoorRestGainComfortRange = indoor; OutdoorRestGainComfortRange = outdoor;
            AwakeOverrides = awake; AsleepOverrides = sleeping;
        }

        internal bool IsValid
        {
            get
            {
                if (_rates.Length != 9 || !float.IsFinite(HealthRegenerationPerSecond)
                    || HealthRegenerationPerSecond < 0 || !float.IsFinite(ThirstyThreshold)
                    || (FadeDelegateMask & ~0x170u) != 0 || !Finite(IndoorRestGainComfortRange)
                    || !Finite(OutdoorRestGainComfortRange) || AwakeOverrides?.IsValid != true
                    || AsleepOverrides?.IsValid != true || (IsAsleep && !AsleepOverrides.IsPresent)) return false;
                foreach (var rate in _rates)
                    if (!float.IsFinite(rate.Fade) || !float.IsFinite(rate.Regeneration)) return false;
                return true;
            }
        }

        internal void Encode(Span<byte> bytes)
        {
            for (int i = 0; i < _rates.Length; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(i * 8), _rates[i].Fade);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(4 + i * 8), _rates[i].Regeneration);
            }
            BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(72), HealthRegenerationPerSecond);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(76), ThirstyThreshold);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(80), IsAsleep ? 1u : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(84), FadeDelegateMask);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(88), IndoorRestGainComfortRange.x);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(92), IndoorRestGainComfortRange.y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(96), OutdoorRestGainComfortRange.x);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(100), OutdoorRestGainComfortRange.y);
            AwakeOverrides.Encode(bytes.Slice(104, PlayerSurvivalOverrideProfile.EncodedLength));
            AsleepOverrides.Encode(bytes.Slice(104 + PlayerSurvivalOverrideProfile.EncodedLength));
        }

        internal static bool TryDecode(ReadOnlySpan<byte> bytes, out PlayerSurvivalSettings settings)
        {
            settings = null;
            if (bytes.Length != EncodedLength) return false;
            var rates = new PlayerSurvivalRate[9];
            for (int i = 0; i < rates.Length; i++)
            {
                float fade = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(i * 8));
                float regen = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(4 + i * 8));
                if (!float.IsFinite(fade) || !float.IsFinite(regen)) return false;
                rates[i] = new(fade, regen);
            }
            float health = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(72));
            float thirst = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(76));
            if (!float.IsFinite(health) || health < 0 || !float.IsFinite(thirst)) return false;
            uint asleep = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(80));
            uint delegates = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(84));
            var indoor = new Vector2(BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(88)),
                BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(92)));
            var outdoor = new Vector2(BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(96)),
                BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(100)));
            if (asleep > 1 || !PlayerSurvivalOverrideProfile.TryDecode(
                    bytes.Slice(104, PlayerSurvivalOverrideProfile.EncodedLength), out var awake)
                || !PlayerSurvivalOverrideProfile.TryDecode(
                    bytes.Slice(104 + PlayerSurvivalOverrideProfile.EncodedLength), out var sleeping)) return false;
            var candidate = new PlayerSurvivalSettings(rates, health, thirst,
                asleep != 0, delegates, indoor, outdoor, awake, sleeping);
            if (!candidate.IsValid) return false;
            settings = candidate;
            return true;
        }

        /// <summary>
        /// Capture both mutable native profiles alongside the held snapshot, including the
        /// active profile and comfort multipliers. No Initialize/OnValidate or delegate is invoked.
        /// The server must independently approve these observations before preparing its clock.
        /// </summary>
        internal static bool TryCapture(PlayerStats source, PlayerLifecycle lifecycle,
            PlayerAuthorityRuntimeState runtime, out PlayerSurvivalSettings settings,
            out string detail)
        {
            settings = null;
            detail = "The exact held source snapshot and initialized native survival settings are required.";
            if (!GameBuild.IsRecognized || (lifecycle != null && !lifecycle.IsPaused)
                || !Alive(source) || !Alive(source._vitals)) return false;
            try
            {
                if (!PlayerAuthorityDynamics.TryCaptureRuntimeState(source, lifecycle, out var state, out detail)
                    || (state with { StaminaActionAge = runtime.StaminaActionAge }) != runtime) return false;
                int frame = Time.frameCount;
                var v = source._vitals;
                var manager = v._statsManager;
                var project = StatProjectSettings._instance;
                var survival = GameSettings.Survival;
                var thirst = v.ThirstSettings;
                if (manager == null || manager.WasCollected || !StatProjectSettings._initialized || !Alive(project)
                    || !GameSettings.InitDone
                    || survival == null || survival.WasCollected || thirst == null || thirst.WasCollected) return false;
                var overrides = manager._overrides;
                var awake = v._playerAwakeStatOverrides;
                var sleeping = v._playerAsleepStatOverrides;
                if ((awake is not null && !Alive(awake)) || (sleeping is not null && !Alive(sleeping))
                    || (awake is not null && sleeping is not null && awake.Pointer == sleeping.Pointer)) return false;
                bool asleep = sleeping is not null && overrides is not null && overrides.Pointer == sleeping.Pointer;
                if (!asleep && ((overrides is null) != (awake is null)
                    || (overrides is not null && (!Alive(overrides) || overrides.Pointer != awake.Pointer))))
                { detail = "The active override is not the exact awake or asleep native set."; return false; }
                var stats = new Stat[] { v._health, v._sickness, v._stamina, v._vitality,
                    v._hydration, v._fullness, v._rested, v._strength, v._temperature };
                var awakeProfile = PlayerSurvivalOverrideProfile.Capture(awake, stats);
                var sleepProfile = PlayerSurvivalOverrideProfile.Capture(sleeping, stats);
                var rates = new PlayerSurvivalRate[stats.Length];
                uint delegateMask = 0;
                for (int i = 0; i < stats.Length; i++)
                {
                    var stat = stats[i];
                    if (stat == null || stat.WasCollected) return false;
                    string name = stat.GetName();
                    if (string.IsNullOrEmpty(name)) return false;
                    float fade = stat._fadeRate, regen = stat._regenRate;
                    if (stat._fadeRateMethod != null)
                    {
                        // OnValidate installs these four native project-setting delegates.
                        // No delegate of an unqualified stat is executed on a remote actor.
                        fade = ReadProjectFade(i);
                        delegateMask |= 1u << i;
                        var method = stat._fadeRateMethod.Method;
                        string expected = i switch { 4 => "GetHydrationFadeRate", 5 => "GetFullnessFadeRate",
                            6 => "GetRestFadeRate", _ => "GetTemperatureFadeRate" };
                        if (method == null || method.Name != expected
                            || method.DeclaringType?.FullName != "Sons.StatSystem.StatProjectSettings")
                            throw new InvalidOperationException("A custom fade delegate is not the qualified native settings reader.");
                    }
                    if (overrides is not null && overrides.TryGetFadeRate(name, out float overriddenFade))
                        fade = overriddenFade;
                    if (overrides is not null && overrides.TryGetRegenRate(name, out float overriddenRegen))
                        regen = overriddenRegen;
                    if (!float.IsFinite(fade) || !float.IsFinite(regen)) return false;
                    rates[i] = new(fade, regen);
                }
                float healthRate = survival.HealthRegenPerSecond, threshold = thirst.ThirstyNotificationThreshold;
                if (!float.IsFinite(healthRate) || healthRate < 0 || !float.IsFinite(threshold)
                    || frame != Time.frameCount || (lifecycle != null && !lifecycle.IsPaused)
                    || source._vitals.Pointer != v.Pointer || v._statsManager.Pointer != manager.Pointer) return false;
                var candidate = new PlayerSurvivalSettings(rates, healthRate, threshold,
                    asleep, delegateMask, source._indoorRestGainComfortRangeModifier,
                    source._outdoorRestGainComfortRangeModifier, awakeProfile, sleepProfile);
                if (!candidate.IsValid || !awakeProfile.Matches(awake, stats) || !sleepProfile.Matches(sleeping, stats)
                    || (v._playerAwakeStatOverrides?.Pointer ?? IntPtr.Zero) != (awake?.Pointer ?? IntPtr.Zero)
                    || (v._playerAsleepStatOverrides?.Pointer ?? IntPtr.Zero) != (sleeping?.Pointer ?? IntPtr.Zero)
                    || (manager._overrides?.Pointer ?? IntPtr.Zero) != (overrides?.Pointer ?? IntPtr.Zero)) return false;
                settings = candidate;
                detail = "Native awake/asleep profiles, mutable multipliers and health/thirst settings captured; server approval is still required.";
                return true;
            }
            catch (Exception error) { detail = "Survival settings capture refused: " + error.Message; return false; }
        }

        private static bool Alive(UnityEngine.Object value) => value is not null && !value.WasCollected && value;
        private static bool Finite(Vector2 value) => float.IsFinite(value.x) && float.IsFinite(value.y);

        internal static float ReadProjectFade(int index)
        {
            var project = StatProjectSettings._instance;
            if (!StatProjectSettings._initialized || !Alive(project))
                throw new InvalidOperationException("The initialized native fade settings are unavailable.");
            return index switch
            {
                4 => project._hydrationFadeRate,
                5 => project._fullnessFadeRate,
                6 => project._restFadeRate,
                8 => project._temperatureFadeRate,
                _ => throw new InvalidOperationException("An unsupported stat fade delegate needs its own owner.")
            };
        }
    }
}
