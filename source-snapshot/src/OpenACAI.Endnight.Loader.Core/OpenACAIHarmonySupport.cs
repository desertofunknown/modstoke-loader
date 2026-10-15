// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Runtime guard for HarmonyX/MonoMod patch support. This keeps patch-driven
// features controlled on the .NET modernization branch instead of relying on
// whichever legacy BepInEx dependency set happens to be installed.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace OpenACAI.Loader
{
    public static class OpenACAIHarmonySupport
    {
        public const string ServiceKey = "openacai.loader.harmony";
        public const string HarmonyAssemblyName = "0Harmony";
        public const string CoreAssemblyName = "MonoMod.Core";
        public const string RuntimeDetourAssemblyName = "MonoMod.RuntimeDetour";
        public const string UtilsAssemblyName = "MonoMod.Utils";

        private static readonly Version TestedHarmonyXVersion = new Version(2, 16, 1, 0);
        private static readonly Version TestedCoreVersion = new Version(1, 3, 5, 0);
        private static readonly Version TestedRuntimeDetourVersion = new Version(25, 3, 5, 0);
        private static readonly Version TestedUtilsVersion = new Version(25, 0, 13, 0);
        private static readonly object SyncRoot = new object();

        private static OpenACAIHarmonySupportSnapshot LastSnapshot;

        internal static OpenACAIHarmonySupportSnapshot Publish(
            string bepinexCorePath,
            OpenACAIIL2CPPHookStateSnapshot hookState,
            bool forceEnableSonsMenuHarmony,
            bool forceDisableSonsMenuHarmony)
        {
            var snapshot = Probe(bepinexCorePath, hookState, forceEnableSonsMenuHarmony, forceDisableSonsMenuHarmony);
            lock (SyncRoot)
            {
                LastSnapshot = snapshot;
            }

            OpenACAIModding.SetService(ServiceKey, snapshot);
            PublishShimState(snapshot);
            return snapshot;
        }

        internal static OpenACAIHarmonySupportSnapshot CreateState()
        {
            lock (SyncRoot)
            {
                return LastSnapshot;
            }
        }

        private static OpenACAIHarmonySupportSnapshot Probe(
            string bepinexCorePath,
            OpenACAIIL2CPPHookStateSnapshot hookState,
            bool forceEnableSonsMenuHarmony,
            bool forceDisableSonsMenuHarmony)
        {
            var monoModRuntimeBridge = OpenACAIMonoModRuntimeBootstrap.EnsureRuntimeBridge();
            var harmony = ProbeAssembly(bepinexCorePath, HarmonyAssemblyName + ".dll", HarmonyAssemblyName, TestedHarmonyXVersion);
            var core = ProbeAssembly(bepinexCorePath, CoreAssemblyName + ".dll", CoreAssemblyName, TestedCoreVersion);
            var runtimeDetour = ProbeAssembly(bepinexCorePath, RuntimeDetourAssemblyName + ".dll", RuntimeDetourAssemblyName, TestedRuntimeDetourVersion);
            var utils = ProbeAssembly(bepinexCorePath, UtilsAssemblyName + ".dll", UtilsAssemblyName, TestedUtilsVersion);
            var dependencies = new[] { harmony, core, runtimeDetour, utils };
            var allPresent = harmony.Present && core.Present && runtimeDetour.Present && utils.Present;
            var meetsBaseline = allPresent
                && harmony.MeetsTestedBaseline
                && core.MeetsTestedBaseline
                && runtimeDetour.MeetsTestedBaseline
                && utils.MeetsTestedBaseline;
            var hookReady = hookState != null && hookState.Ready;
            var runtimeBridgeReady = monoModRuntimeBridge == null || monoModRuntimeBridge.Ready;
            var safeForHarmonyPatches = allPresent && meetsBaseline && hookReady && runtimeBridgeReady;
            var sonsMenuHarmonyEnabled = !forceDisableSonsMenuHarmony
                && allPresent
                && runtimeBridgeReady
                && (safeForHarmonyPatches || forceEnableSonsMenuHarmony);

            var warnings = new List<string>();
            if (!allPresent)
            {
                warnings.Add("Harmony patch support is missing one or more required assemblies.");
            }

            if (allPresent && !meetsBaseline)
            {
                warnings.Add("Harmony patch support is below the tested OpenACAI baseline.");
            }

            if (allPresent && meetsBaseline && !hookReady)
            {
                warnings.Add("Harmony patch support is waiting for a healthy IL2CPP native hook state.");
            }

            if (!runtimeBridgeReady && monoModRuntimeBridge != null)
            {
                warnings.Add("MonoMod runtime bridge is not ready: " + monoModRuntimeBridge.Detail);
            }

            if (forceDisableSonsMenuHarmony)
            {
                warnings.Add("Sons menu Harmony patches were disabled by environment override.");
            }
            else if (forceEnableSonsMenuHarmony && !safeForHarmonyPatches)
            {
                warnings.Add("Sons menu Harmony patches were forced on before the runtime passed the full safety probe.");
            }

            var detail = BuildDetail(allPresent, meetsBaseline, hookReady, runtimeBridgeReady, monoModRuntimeBridge, forceEnableSonsMenuHarmony, forceDisableSonsMenuHarmony);
            return new OpenACAIHarmonySupportSnapshot(
                bepinexCorePath,
                TestedHarmonyXVersion.ToString(3),
                TestedCoreVersion.ToString(3),
                TestedRuntimeDetourVersion.ToString(3),
                TestedUtilsVersion.ToString(3),
                monoModRuntimeBridge,
                dependencies,
                allPresent,
                meetsBaseline,
                hookReady,
                safeForHarmonyPatches,
                sonsMenuHarmonyEnabled,
                forceEnableSonsMenuHarmony,
                forceDisableSonsMenuHarmony,
                detail,
                warnings.ToArray());
        }

        private static OpenACAIHarmonyDependencySnapshot ProbeAssembly(
            string bepinexCorePath,
            string fileName,
            string assemblyName,
            Version testedBaseline)
        {
            var path = string.IsNullOrWhiteSpace(bepinexCorePath) ? null : Path.Combine(bepinexCorePath, fileName);
            Version assemblyVersion = null;
            string productVersion = null;
            string fileVersion = null;
            bool present = false;

            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    present = true;
                    var versionInfo = FileVersionInfo.GetVersionInfo(path);
                    productVersion = versionInfo.ProductVersion;
                    fileVersion = versionInfo.FileVersion;
                    assemblyVersion = AssemblyName.GetAssemblyName(path).Version;
                }
            }
            catch
            {
            }

            if (!present)
            {
                var loaded = FindLoadedAssembly(assemblyName);
                if (loaded != null)
                {
                    present = true;
                    assemblyVersion = loaded.GetName().Version;
                    path = loaded.Location;
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                        {
                            var versionInfo = FileVersionInfo.GetVersionInfo(path);
                            productVersion = versionInfo.ProductVersion;
                            fileVersion = versionInfo.FileVersion;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            var bestVersion = ParseVersion(productVersion) ?? ParseVersion(fileVersion) ?? assemblyVersion;
            var meetsBaseline = present && bestVersion != null && CompareVersions(bestVersion, testedBaseline) >= 0;
            return new OpenACAIHarmonyDependencySnapshot(
                assemblyName,
                path,
                present,
                assemblyVersion == null ? null : assemblyVersion.ToString(),
                productVersion,
                fileVersion,
                bestVersion == null ? null : bestVersion.ToString(),
                testedBaseline.ToString(3),
                meetsBaseline);
        }

        private static Assembly FindLoadedAssembly(string assemblyName)
        {
            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                    {
                        return assembly;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static Version ParseVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var chars = new List<char>(value.Length);
            foreach (var c in value)
            {
                if (char.IsDigit(c) || c == '.')
                {
                    chars.Add(c);
                    continue;
                }

                if (chars.Count > 0)
                {
                    break;
                }
            }

            var cleaned = new string(chars.ToArray()).Trim('.');
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return null;
            }

            Version parsed;
            return Version.TryParse(cleaned, out parsed) ? parsed : null;
        }

        private static int CompareVersions(Version left, Version right)
        {
            return NormalizeVersion(left).CompareTo(NormalizeVersion(right));
        }

        private static Version NormalizeVersion(Version version)
        {
            return new Version(
                Math.Max(version.Major, 0),
                Math.Max(version.Minor, 0),
                Math.Max(version.Build, 0),
                Math.Max(version.Revision, 0));
        }

        private static string BuildDetail(
            bool allPresent,
            bool meetsBaseline,
            bool hookReady,
            bool runtimeBridgeReady,
            OpenACAIMonoModRuntimeBridgeState monoModRuntimeBridge,
            bool forceEnableSonsMenuHarmony,
            bool forceDisableSonsMenuHarmony)
        {
            if (forceDisableSonsMenuHarmony)
            {
                return "Sons menu Harmony patches are disabled by environment override.";
            }

            if (!allPresent)
            {
                return "HarmonyX/MonoMod dependency files are incomplete.";
            }

            if (!meetsBaseline)
            {
                return "HarmonyX/MonoMod dependency files are present but older than the OpenACAI tested baseline.";
            }

            if (!hookReady)
            {
                return "HarmonyX/MonoMod dependencies are modern, but the IL2CPP native hook state is not ready yet.";
            }

            if (!runtimeBridgeReady)
            {
                return monoModRuntimeBridge == null
                    ? "MonoMod runtime bridge is not ready."
                    : monoModRuntimeBridge.Detail;
            }

            if (forceEnableSonsMenuHarmony)
            {
                return "HarmonyX/MonoMod dependencies and IL2CPP hook state are ready; Sons menu Harmony patches are forced on.";
            }

            return "HarmonyX/MonoMod dependencies and IL2CPP hook state are ready; Sons menu Harmony patches may run by default.";
        }

        private static void PublishShimState(OpenACAIHarmonySupportSnapshot snapshot)
        {
            if (snapshot.SafeForHarmonyPatches)
            {
                OpenACAIRuntime.MarkShimReady(
                    OpenACAICompatibilityShimIds.HarmonyPatches,
                    "Harmony patches",
                    "HarmonyX/MonoMod",
                    "modern-runtime-detour",
                    snapshot.Detail);
                return;
            }

            OpenACAIRuntime.MarkShim(
                OpenACAICompatibilityShimIds.HarmonyPatches,
                "Harmony patches",
                "HarmonyX/MonoMod",
                snapshot.ForceDisableSonsMenuHarmony ? OpenACAICompatibilityShimStatus.Disabled : OpenACAICompatibilityShimStatus.Warning,
                snapshot.MeetsTestedBaseline ? "waiting-for-il2cpp-hook-state" : "dependency-baseline",
                !snapshot.ForceDisableSonsMenuHarmony,
                false,
                false,
                snapshot.Detail);
        }
    }

    public sealed class OpenACAIHarmonySupportSnapshot
    {
        internal OpenACAIHarmonySupportSnapshot(
            string bepinexCorePath,
            string testedHarmonyXVersion,
            string testedCoreVersion,
            string testedRuntimeDetourVersion,
            string testedUtilsVersion,
            OpenACAIMonoModRuntimeBridgeState monoModRuntimeBridge,
            IReadOnlyCollection<OpenACAIHarmonyDependencySnapshot> dependencies,
            bool allDependenciesPresent,
            bool meetsTestedBaseline,
            bool il2CppHookReady,
            bool safeForHarmonyPatches,
            bool sonsMenuHarmonyEnabled,
            bool forceEnableSonsMenuHarmony,
            bool forceDisableSonsMenuHarmony,
            string detail,
            IReadOnlyCollection<string> warnings)
        {
            BepInExCorePath = bepinexCorePath;
            TestedHarmonyXVersion = testedHarmonyXVersion;
            TestedCoreVersion = testedCoreVersion;
            TestedRuntimeDetourVersion = testedRuntimeDetourVersion;
            TestedUtilsVersion = testedUtilsVersion;
            MonoModRuntimeBridge = monoModRuntimeBridge;
            Dependencies = dependencies;
            AllDependenciesPresent = allDependenciesPresent;
            MeetsTestedBaseline = meetsTestedBaseline;
            Il2CppHookReady = il2CppHookReady;
            SafeForHarmonyPatches = safeForHarmonyPatches;
            SonsMenuHarmonyEnabled = sonsMenuHarmonyEnabled;
            ForceEnableSonsMenuHarmony = forceEnableSonsMenuHarmony;
            ForceDisableSonsMenuHarmony = forceDisableSonsMenuHarmony;
            Detail = detail;
            Warnings = warnings;
            GeneratedUtc = DateTimeOffset.UtcNow.ToString("O");
        }

        public string BepInExCorePath { get; }
        public string TestedHarmonyXVersion { get; }
        public string TestedCoreVersion { get; }
        public string TestedRuntimeDetourVersion { get; }
        public string TestedUtilsVersion { get; }
        public OpenACAIMonoModRuntimeBridgeState MonoModRuntimeBridge { get; }
        public IReadOnlyCollection<OpenACAIHarmonyDependencySnapshot> Dependencies { get; }
        public bool AllDependenciesPresent { get; }
        public bool MeetsTestedBaseline { get; }
        public bool Il2CppHookReady { get; }
        public bool SafeForHarmonyPatches { get; }
        public bool SonsMenuHarmonyEnabled { get; }
        public bool ForceEnableSonsMenuHarmony { get; }
        public bool ForceDisableSonsMenuHarmony { get; }
        public string Detail { get; }
        public IReadOnlyCollection<string> Warnings { get; }
        public string GeneratedUtc { get; }
    }

    public sealed class OpenACAIHarmonyDependencySnapshot
    {
        internal OpenACAIHarmonyDependencySnapshot(
            string assemblyName,
            string path,
            bool present,
            string assemblyVersion,
            string productVersion,
            string fileVersion,
            string detectedVersion,
            string testedBaseline,
            bool meetsTestedBaseline)
        {
            AssemblyName = assemblyName;
            Path = path;
            Present = present;
            AssemblyVersion = assemblyVersion;
            ProductVersion = productVersion;
            FileVersion = fileVersion;
            DetectedVersion = detectedVersion;
            TestedBaseline = testedBaseline;
            MeetsTestedBaseline = meetsTestedBaseline;
        }

        public string AssemblyName { get; }
        public string Path { get; }
        public bool Present { get; }
        public string AssemblyVersion { get; }
        public string ProductVersion { get; }
        public string FileVersion { get; }
        public string DetectedVersion { get; }
        public string TestedBaseline { get; }
        public bool MeetsTestedBaseline { get; }
    }
}

