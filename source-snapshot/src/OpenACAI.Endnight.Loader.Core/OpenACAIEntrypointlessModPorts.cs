// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned compatibility ports for legacy mods without normal entrypoints.

using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using RedLoader;
using RedLoader.Utils;
using Sons.Gameplay.Swimming;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenACAIEntrypointlessModPorts
    {
        private const string EnableEntrypointlessPortsEnvVar = "OPENACAI_ENABLE_ENTRYPOINTLESS_REDLOADER_PORTS";

        private static HarmonyLib.Harmony _compatHarmony;

        internal static void Apply(Action<string> addStartupWarning)
        {
            if (Environment.Version.Major >= 10 && !IsTruthy(Environment.GetEnvironmentVariable(EnableEntrypointlessPortsEnvVar)))
            {
                addStartupWarning?.Invoke("Entrypointless RedLoader mod ports are disabled on " + CurrentDotnetLabel() + " by default. Set " + EnableEntrypointlessPortsEnvVar + "=1 for diagnostics.");
                return;
            }

            var fasterLogsPath = Path.Combine(LoaderEnvironment.ModsDirectory, "FasterLogs.dll");
            if (!File.Exists(fasterLogsPath))
            {
                return;
            }

            try
            {
                var fasterLogsAssembly = Assembly.LoadFrom(fasterLogsPath);
                RegisterTypeInIl2Cpp.RegisterAssembly(fasterLogsAssembly);
                fasterLogsAssembly.GetType("FasterLogs.Config")?.GetMethod("Init", BindingFlags.Static | BindingFlags.Public)?.Invoke(null, null);
                fasterLogsAssembly.GetType("FasterLogs.FasterLogsUI")?.GetMethod("Create", BindingFlags.Static | BindingFlags.Public)?.Invoke(null, null);

                _compatHarmony = new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + ".entrypointless");
                _compatHarmony.Patch(
                    AccessTools.Method(typeof(FloatController), "OnEnable"),
                    postfix: new HarmonyMethod(typeof(FasterLogsFloatControllerPatch), nameof(FasterLogsFloatControllerPatch.Apply)));

                OpenACAIEndnightRuntime.LogSource.LogInfo("FasterLogs compatibility patch active.");
            }
            catch (Exception ex)
            {
                OpenACAIEndnightRuntime.LogSource.LogWarning("FasterLogs compatibility patch failed: " + ex.Message);
            }
        }

        private static string CurrentDotnetLabel()
        {
            return ".NET " + Environment.Version.Major;
        }

        private static bool IsTruthy(string value)
        {
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class FasterLogsFloatControllerPatch
    {
        public static void Apply(FloatController __instance)
        {
            if (__instance == null)
            {
                return;
            }

            __instance._activeDrag = 0.175f;
            __instance._buoyancyFactor = 290f;
            __instance._fluidPressure = 3f;
            __instance._fluidDensity = 1400f;
            __instance._maxWaterForce = 14f;
        }
    }
}
