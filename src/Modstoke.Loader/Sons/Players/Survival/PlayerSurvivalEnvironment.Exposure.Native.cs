// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Common;
using Modstoke.Loader;
using TheForest.World;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerSurvivalEnvironmentExposure
    {
        private static bool _hooksAttempted, _hooksInstalled;
        private static string _hookFailure;
        private static int _hookThread;
        private static HarmonyLib.Harmony _exposureHarmony;
        [ThreadStatic] private static int _rainDecisionDepth;

        internal static bool EnsureExposureHooks(HarmonySupportSnapshot support, out string detail)
        {
            detail = _hookFailure ?? "Exposure requires recognized native Windows x64 entries and safe hook support.";
            if (_hooksAttempted) return _hooksInstalled && _hookThread == Environment.CurrentManagedThreadId;
            if (support?.SafeForHarmonyPatches != true || !GameBuild.IsRecognized
                || !OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return false;
            _hooksAttempted = true; _hookThread = Environment.CurrentManagedThreadId;
            try
            {
                var wetness = Entry(typeof(PlayerStats), nameof(PlayerStats.UpdateWetness), 0x311DE50, typeof(void), typeof(float));
                var update = Entry(typeof(WeatherSystem), nameof(WeatherSystem.UpdateEnvironmentWetness), 0x3064AC0, typeof(void));
                var add = Entry(typeof(WeatherSystem), nameof(WeatherSystem.AddWetnessOverride), 0x3069C90,
                    typeof(void), typeof(WeatherSystemWetnessOverride));
                var remove = Entry(typeof(WeatherSystem), nameof(WeatherSystem.RemoveWetnessOverride), 0x3069EF0,
                    typeof(void), typeof(WeatherSystemWetnessOverride));
                var disable = Entry(typeof(WeatherSystemWetnessOverride), nameof(WeatherSystemWetnessOverride.OnDisable), 0x306B2F0, typeof(void));
                var start = Entry(typeof(WeatherSystem), nameof(WeatherSystem.StartRaining), 0x3066F70, typeof(void), typeof(WeatherSystem.RainType));
                var stop = Entry(typeof(WeatherSystem), nameof(WeatherSystem.StopRaining), 0x3065A10, typeof(void));
                var check = Entry(typeof(WeatherSystem), nameof(WeatherSystem.CheckForRainToSnow), 0x3068AF0, typeof(void));
                var snow = Entry(typeof(WeatherSystem), "set_IsSnowing", 0x30633E0, typeof(void), typeof(bool));
                var classify = Entry(typeof(WeatherSystem), nameof(WeatherSystem.ShouldSnow), 0x30675B0, typeof(bool));
                _exposureHarmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-player-exposure");
                _exposureHarmony.Patch(wetness, prefix: Hook(nameof(BeforeWetness), Priority.First));
                foreach (var method in new[] { update, add, remove, stop })
                    _exposureHarmony.Patch(method, postfix: Hook(nameof(AfterWeather), Priority.Last));
                _exposureHarmony.Patch(disable, postfix: Hook(nameof(AfterDisable), Priority.Last));
                foreach (var method in new[] { start, check })
                    _exposureHarmony.Patch(method, prefix: Hook(nameof(BeforeRainDecision), Priority.First),
                        postfix: Hook(nameof(AfterWeather), Priority.Last), finalizer: Hook(nameof(FinishRainDecision), Priority.Last));
                _exposureHarmony.Patch(snow, prefix: Hook(nameof(BeforeSnowSet), Priority.First),
                    postfix: Hook(nameof(AfterSnowSet), Priority.Last));
                // Observe the actual classification point, including cache priming by
                // callers outside StartRaining. Do not replace the host's result.
                _exposureHarmony.Patch(classify, prefix: Hook(nameof(BeforeSnowClassification), Priority.First));
                InstallSpatialHooks();
                _hooksInstalled = true;
                detail = "Exact native exposure writers observed; no actor selected or shared weather modified.";
                return true;
            }
            catch (Exception error)
            {
                try { _exposureHarmony?.UnpatchSelf(); } catch (Exception) { }
                _hookFailure = detail = "Exposure hooks unavailable: " + error.Message;
                return false;
            }
        }

        private static bool BeforeWetness(PlayerStats __instance)
        {
            if (__instance == null) return true;
            var source = _sourceOwner;
            if (source != null && __instance.Pointer == source._stats.Pointer)
            {
                try { source.RequireSource(); }
                catch (Exception error) { source.Fail(error.Message); }
                // UpdateWetness reads LocalPlayer flags before writing +140h. It must
                // never run on a remote carrier or overwrite held/projected source stats.
                return false;
            }
            if (PlayerSurvivalEnvironmentExposureServer.Owners.TryGetValue(__instance.Pointer, out var server))
            { server.ObserveHeldWetness(); return false; }
            return true;
        }

        private static void BeforeRainDecision(out bool __state)
        { _rainDecisionDepth++; __state = true; }

        private static Exception FinishRainDecision(Exception __exception, bool __state)
        {
            if (__state) _rainDecisionDepth--;
            if (__exception != null) FailObservers("A native precipitation writer failed before completion.");
            return __exception;
        }

        private static void BeforeSnowClassification(WeatherSystem __instance)
        {
            if (__instance == null) return;
            try
            {
                bool apply = _rainDecisionDepth > 0 && (int)__instance._cloudState == 3 && Alive(WeatherSystem._rainTypesCache);
                foreach (var pair in PlayerSurvivalEnvironmentExposureServer.Owners)
                    pair.Value.ObserveSnowClassification(__instance.Pointer, apply);
            }
            catch (Exception error) { FailObservers(error.Message); }
        }

        private static void AfterWeather(WeatherSystem __instance, MethodBase __originalMethod)
        {
            var source = _sourceOwner;
            if (source == null || __instance == null || __instance.Pointer != source._weather.Pointer) return;
            var writer = __originalMethod.Name switch
            {
                nameof(WeatherSystem.AddWetnessOverride) => PlayerExposureWriter.OverrideAdded,
                nameof(WeatherSystem.RemoveWetnessOverride) => PlayerExposureWriter.OverrideRemoved,
                nameof(WeatherSystem.StartRaining) => PlayerExposureWriter.RainStarted,
                nameof(WeatherSystem.StopRaining) => PlayerExposureWriter.RainStopped,
                nameof(WeatherSystem.CheckForRainToSnow) => PlayerExposureWriter.RainToSnowChecked,
                _ => PlayerExposureWriter.Wetness
            };
            source.Observe(writer);
        }

        private static void AfterDisable()
        {
            // OnDisable removes directly instead of calling RemoveWetnessOverride.
            _sourceOwner?.Observe(PlayerExposureWriter.OverrideDisabled);
        }

        private static void BeforeSnowSet(WeatherSystem __instance, out bool __state)
            => __state = __instance != null && __instance._isSnowing;

        private static void AfterSnowSet(WeatherSystem __instance, bool __state)
        {
            if (__instance == null) return;
            try
            {
                var source = _sourceOwner;
                if (source != null && __instance.Pointer == source._weather.Pointer)
                    source.Observe(PlayerExposureWriter.SnowSet);
                if (__state == __instance._isSnowing) return;
                foreach (var pair in PlayerSurvivalEnvironmentExposureServer.Owners)
                    pair.Value.ObserveDirectSnowSet(__instance.Pointer);
            }
            catch (Exception error) { FailObservers(error.Message); }
        }

        private static void FailObservers(string detail)
        {
            _sourceOwner?.Fail(detail);
            foreach (var pair in PlayerSurvivalEnvironmentExposureServer.Owners) pair.Value.Fail(detail);
        }

        private static HarmonyMethod Hook(string method, int priority)
            => new(typeof(PlayerSurvivalEnvironmentExposure), method) { priority = priority };

        private static MethodInfo Entry(Type type, string name, int rva, Type result, params Type[] arguments)
        {
            var method = AccessTools.DeclaredMethod(type, name, arguments);
            if (method == null || method.IsStatic || method.ReturnType != result) throw new MissingMethodException(type.FullName, name);
            var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
            if (field?.GetValue(null) is not nint info || info == 0) throw new InvalidOperationException("Missing native exposure method: " + name);
            var module = ExposureModule("GameAssembly.dll");
            if (module == 0 || Marshal.ReadIntPtr(info) != module + rva)
                throw new InvalidOperationException("Native exposure entry changed: " + name);
            return method;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
        private static extern nint ExposureModule(string name);
    }
}
