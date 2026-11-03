// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Runtime guard for HarmonyX/MonoMod patch support. Patch-driven features stay
// off unless the installed HarmonyX/MonoMod set meets the tested baseline and
// the IL2CPP native hook state is healthy.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Modstoke.Loader
{
    public static class HarmonySupport
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

        private static HarmonySupportSnapshot LastSnapshot;

        internal static HarmonySupportSnapshot Publish(
            string bepinexCorePath,
            IL2CPPHookStateSnapshot hookState,
            bool forceEnableSonsMenuHarmony,
            bool forceDisableSonsMenuHarmony)
        {
            var snapshot = Probe(bepinexCorePath, hookState, forceEnableSonsMenuHarmony, forceDisableSonsMenuHarmony);
            lock (SyncRoot)
            {
                LastSnapshot = snapshot;
            }

            Modding.SetService(ServiceKey, snapshot);
            PublishShimState(snapshot);
            return snapshot;
        }

        internal static HarmonySupportSnapshot CreateState()
        {
            lock (SyncRoot)
            {
                return LastSnapshot;
            }
        }

        private static HarmonySupportSnapshot Probe(
            string bepinexCorePath,
            IL2CPPHookStateSnapshot hookState,
            bool forceEnableSonsMenuHarmony,
            bool forceDisableSonsMenuHarmony)
        {
            var monoModRuntimeBridge = MonoModRuntimeBootstrap.EnsureRuntimeBridge();
            var loadContext = AssemblyLoadContext.GetLoadContext(typeof(HarmonySupport).Assembly) ?? AssemblyLoadContext.Default;
            var loadedAssemblies = new List<Assembly>(loadContext.Assemblies);
            if (loadContext != AssemblyLoadContext.Default)
                loadedAssemblies.AddRange(AssemblyLoadContext.Default.Assemblies);
            var harmony = ProbeAssembly(bepinexCorePath, HarmonyAssemblyName + ".dll", HarmonyAssemblyName, TestedHarmonyXVersion, loadedAssemblies);
            var core = ProbeAssembly(bepinexCorePath, CoreAssemblyName + ".dll", CoreAssemblyName, TestedCoreVersion, loadedAssemblies);
            var runtimeDetour = ProbeAssembly(bepinexCorePath, RuntimeDetourAssemblyName + ".dll", RuntimeDetourAssemblyName, TestedRuntimeDetourVersion, loadedAssemblies);
            var utils = ProbeAssembly(bepinexCorePath, UtilsAssemblyName + ".dll", UtilsAssemblyName, TestedUtilsVersion, loadedAssemblies);
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
                warnings.Add("Harmony patch support dependency versions do not meet the tested Modstoke baseline.");
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
            else if (forceEnableSonsMenuHarmony && !sonsMenuHarmonyEnabled)
            {
                warnings.Add("Sons menu Harmony patches were requested by environment override but remain disabled because required assemblies or the MonoMod runtime bridge are unavailable.");
            }
            else if (sonsMenuHarmonyEnabled && !safeForHarmonyPatches)
            {
                warnings.Add("Sons menu Harmony patches were forced on before the runtime passed the full safety probe.");
            }

            var detail = BuildDetail(allPresent, meetsBaseline, hookReady, runtimeBridgeReady, monoModRuntimeBridge);
            if (forceDisableSonsMenuHarmony)
            {
                detail += " Sons menu Harmony patches are disabled by environment override.";
            }
            else if (forceEnableSonsMenuHarmony)
            {
                detail += sonsMenuHarmonyEnabled
                    ? " Sons menu Harmony patches are forced on by environment override."
                    : " The Sons menu Harmony enable override could not be applied.";
            }
            return new HarmonySupportSnapshot(
                bepinexCorePath,
                TestedHarmonyXVersion.ToString(3),
                TestedCoreVersion.ToString(3),
                TestedRuntimeDetourVersion.ToString(3),
                TestedUtilsVersion.ToString(3),
                monoModRuntimeBridge,
                Array.AsReadOnly(dependencies),
                allPresent,
                meetsBaseline,
                hookReady,
                safeForHarmonyPatches,
                sonsMenuHarmonyEnabled,
                forceEnableSonsMenuHarmony,
                forceDisableSonsMenuHarmony,
                detail,
                warnings.AsReadOnly());
        }

        private static HarmonyDependencySnapshot ProbeAssembly(
            string bepinexCorePath,
            string fileName,
            string assemblyName,
            Version testedBaseline,
            IReadOnlyCollection<Assembly> loadedAssemblies)
        {
            string path = null;
            Version assemblyVersion = null;
            string productVersion = null;
            string fileVersion = null;
            bool present = false;

            // Loaded metadata describes the code actually executing, even if its file was replaced.
            var loaded = FindLoadedAssembly(assemblyName, loadedAssemblies);
            if (loaded != null)
            {
                present = true;
                assemblyVersion = loaded.GetName().Version;
                try
                {
                    path = loaded.Location;
                }
                catch
                {
                }

                try
                {
                    productVersion = loaded.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                    fileVersion = loaded.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
                }
                catch
                {
                }
            }
            else
            {
                try
                {
                    path = string.IsNullOrWhiteSpace(bepinexCorePath) ? null : Path.Combine(bepinexCorePath, fileName);
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        var identity = AssemblyName.GetAssemblyName(path);
                        if (string.Equals(identity.Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                        {
                            assemblyVersion = identity.Version;
                            present = true;
                            var versionInfo = FileVersionInfo.GetVersionInfo(path);
                            productVersion = versionInfo.ProductVersion;
                            fileVersion = versionInfo.FileVersion;
                        }
                    }
                }
                catch
                {
                }
            }

            var bestVersion = ParseVersion(productVersion) ?? ParseVersion(fileVersion) ?? assemblyVersion;
            var meetsBaseline = present && bestVersion != null && CompareVersions(bestVersion, testedBaseline) >= 0;
            return new HarmonyDependencySnapshot(
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

        private static Assembly FindLoadedAssembly(string assemblyName, IReadOnlyCollection<Assembly> loadedAssemblies)
        {
            foreach (var assembly in loadedAssemblies)
            {
                try
                {
                    if (!assembly.IsDynamic && string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                    {
                        return assembly;
                    }
                }
                catch
                {
                }
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
            MonoModRuntimeBridgeState monoModRuntimeBridge)
        {
            if (!allPresent)
            {
                return "One or more required HarmonyX/MonoMod assemblies are missing or invalid.";
            }

            if (!meetsBaseline)
            {
                return "HarmonyX/MonoMod assemblies are present, but their versions do not meet the Modstoke tested baseline.";
            }

            if (!runtimeBridgeReady)
            {
                return monoModRuntimeBridge == null
                    ? "MonoMod runtime bridge is not ready."
                    : monoModRuntimeBridge.Detail;
            }

            if (!hookReady)
            {
                return "HarmonyX/MonoMod dependencies are modern, but the IL2CPP native hook state is not ready yet.";
            }

            return "HarmonyX/MonoMod dependencies, runtime bridge and IL2CPP hook state are ready for Harmony patches.";
        }

        private static void PublishShimState(HarmonySupportSnapshot snapshot)
        {
            if (snapshot.SafeForHarmonyPatches)
            {
                LoaderRuntime.MarkShimReady(
                    CompatibilityShimIds.HarmonyPatches,
                    "Harmony patches",
                    "HarmonyX/MonoMod",
                    "modern-runtime-detour",
                    snapshot.Detail);
                return;
            }

            // A dedicated server without patches runs with no server-authority hooks, which
            // attestation must report as a failure rather than a warning.
            var failed = GameBuild.IsDedicatedServer;
            LoaderRuntime.MarkShim(
                CompatibilityShimIds.HarmonyPatches,
                "Harmony patches",
                "HarmonyX/MonoMod",
                failed ? CompatibilityShimStatus.Failed : CompatibilityShimStatus.Warning,
                !snapshot.AllDependenciesPresent ? "dependencies-missing"
                    : !snapshot.MeetsTestedBaseline ? "dependency-baseline"
                    : snapshot.MonoModRuntimeBridge != null && !snapshot.MonoModRuntimeBridge.Ready ? "runtime-bridge"
                    : "waiting-for-il2cpp-hook-state",
                true,
                false,
                failed,
                snapshot.Detail);
            if (failed)
                RuntimeEntryPoint.LogSource?.LogError("Dedicated server started without safe Harmony patches; server-authority hooks are off. " + snapshot.Detail);
        }
    }

    public sealed class HarmonySupportSnapshot
    {
        internal HarmonySupportSnapshot(
            string bepinexCorePath,
            string testedHarmonyXVersion,
            string testedCoreVersion,
            string testedRuntimeDetourVersion,
            string testedUtilsVersion,
            MonoModRuntimeBridgeState monoModRuntimeBridge,
            IReadOnlyCollection<HarmonyDependencySnapshot> dependencies,
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
        public MonoModRuntimeBridgeState MonoModRuntimeBridge { get; }
        public IReadOnlyCollection<HarmonyDependencySnapshot> Dependencies { get; }
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

    public sealed class HarmonyDependencySnapshot
    {
        internal HarmonyDependencySnapshot(
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

