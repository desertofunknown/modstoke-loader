// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned Harmony/MonoMod backend selection for the early IL2CPP
// preloader phase. This preserves BepInEx config compatibility without
// reflecting into BepInEx.Preloader.RuntimeFixes.HarmonyBackendFix.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace OpenACAI.Endnight.Loader.Bootstrap
{
    internal static class OpenACAIHarmonyBackendInitializer
    {
        internal const string OpenAcaiHarmonyBackendEnvironmentVariable = "OPENACAI_HARMONY_BACKEND";
        internal const string OpenAcaiMonoModDmdEnvironmentVariable = "OPENACAI_MONOMOD_DMD_TYPE";
        internal const string MonoModDmdEnvironmentVariable = "MONOMOD_DMD_TYPE";

        private const string DefaultBackend = "auto";
        private const string DynamicMethodBackend = "dynamicmethod";
        private const string MethodBuilderBackend = "methodbuilder";
        private const string CecilBackend = "cecil";
        private const string MonoModCoreAssemblyName = "MonoMod.Core";
        private const string Core110RuntimeTypeName = "MonoMod.Core.Platforms.Runtimes.Core110Runtime";

        internal static OpenACAIHarmonyBackendInitializationState Initialize(
            string gameRoot,
            string runtimeDirectory,
            string compatibilityDirectory,
            string bepInExCoreDirectory,
            Action<string> trace)
        {
            var state = new OpenACAIHarmonyBackendInitializationState
            {
                OpenAcaiOwned = true,
                BepInExHarmonyBackendFixUsed = false,
                EnvironmentValueBefore = Environment.GetEnvironmentVariable(MonoModDmdEnvironmentVariable) ?? ""
            };

            var selection = SelectBackend(gameRoot);
            state.RequestedBackend = selection.Value;
            state.BackendSource = selection.Source;
            state.NormalizedBackend = NormalizeBackend(selection.Value);

            if (!IsSupportedBackend(state.NormalizedBackend))
            {
                throw new ArgumentOutOfRangeException(
                    OpenAcaiHarmonyBackendEnvironmentVariable,
                    selection.Value,
                    "OpenACAI Harmony backend must be auto, dynamicmethod, methodbuilder, or cecil.");
            }

            if (!string.Equals(state.NormalizedBackend, DefaultBackend, StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable(MonoModDmdEnvironmentVariable, state.NormalizedBackend);
                state.EnvironmentApplied = true;
            }

            state.EnvironmentValueAfter = Environment.GetEnvironmentVariable(MonoModDmdEnvironmentVariable) ?? "";
            ProbeMonoModCore(runtimeDirectory, compatibilityDirectory, bepInExCoreDirectory, gameRoot, state);
            state.Status = "ready";
            state.Detail = BuildDetail(state);
            trace?.Invoke("OpenACAI Harmony backend initialized: " + state.Detail);
            return state;
        }

        private static BackendSelection SelectBackend(string gameRoot)
        {
            var explicitBackend = Environment.GetEnvironmentVariable(OpenAcaiHarmonyBackendEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(explicitBackend))
            {
                return new BackendSelection("openacai-environment", explicitBackend);
            }

            var explicitDmd = Environment.GetEnvironmentVariable(OpenAcaiMonoModDmdEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(explicitDmd))
            {
                return new BackendSelection("openacai-dmd-environment", explicitDmd);
            }

            var existingDmd = Environment.GetEnvironmentVariable(MonoModDmdEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(existingDmd))
            {
                return new BackendSelection("monomod-environment", existingDmd);
            }

            var configValue = ReadBepInExHarmonyBackend(gameRoot);
            return string.IsNullOrWhiteSpace(configValue)
                ? new BackendSelection("default", DefaultBackend)
                : new BackendSelection("bepinex-config", configValue);
        }

        private static string ReadBepInExHarmonyBackend(string gameRoot)
        {
            try
            {
                var configPath = Path.Combine(gameRoot ?? "", "BepInEx", "config", "BepInEx.cfg");
                if (!File.Exists(configPath))
                {
                    return null;
                }

                var inPreloaderSection = false;
                foreach (var rawLine in File.ReadLines(configPath))
                {
                    var line = rawLine == null ? "" : rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                    {
                        var section = line.Substring(1, line.Length - 2).Trim();
                        inPreloaderSection = string.Equals(section, "Preloader", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    if (!inPreloaderSection)
                    {
                        continue;
                    }

                    var equalsIndex = line.IndexOf('=');
                    if (equalsIndex < 0)
                    {
                        continue;
                    }

                    var key = line.Substring(0, equalsIndex).Trim();
                    if (!string.Equals(key, "HarmonyBackend", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return line.Substring(equalsIndex + 1).Trim();
                }
            }
            catch
            {
            }

            return null;
        }

        private static string NormalizeBackend(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return DefaultBackend;
            }

            var normalized = value.Trim().Trim('"').Trim('\'').Replace("-", "").Replace("_", "").ToLowerInvariant();
            if (string.Equals(normalized, "dynamic", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "dynamicmethods", StringComparison.OrdinalIgnoreCase))
            {
                return DynamicMethodBackend;
            }

            if (string.Equals(normalized, "methodbuilders", StringComparison.OrdinalIgnoreCase))
            {
                return MethodBuilderBackend;
            }

            return normalized;
        }

        private static bool IsSupportedBackend(string backend)
        {
            return string.Equals(backend, DefaultBackend, StringComparison.OrdinalIgnoreCase)
                || string.Equals(backend, DynamicMethodBackend, StringComparison.OrdinalIgnoreCase)
                || string.Equals(backend, MethodBuilderBackend, StringComparison.OrdinalIgnoreCase)
                || string.Equals(backend, CecilBackend, StringComparison.OrdinalIgnoreCase);
        }

        private static void ProbeMonoModCore(
            string runtimeDirectory,
            string compatibilityDirectory,
            string bepInExCoreDirectory,
            string gameRoot,
            OpenACAIHarmonyBackendInitializationState state)
        {
            state.MonoModCore110RuntimeRequired = Environment.Version.Major >= 11;

            var monoModCore = FindOrLoadAssembly(
                MonoModCoreAssemblyName,
                runtimeDirectory,
                compatibilityDirectory,
                bepInExCoreDirectory,
                Path.Combine(gameRoot ?? "", "BepInEx", "plugins", "OpenACAILoader"));
            if (monoModCore == null)
            {
                state.MonoModCorePresent = false;
                state.MonoModRuntimeBridgeStatus = state.MonoModCore110RuntimeRequired ? "guarded" : "not-required";
                state.MonoModRuntimeBridgeDetail = "MonoMod.Core was not resolved during early preloader probing.";
                return;
            }

            state.MonoModCorePresent = true;
            state.MonoModCoreAssemblyPath = monoModCore.Location ?? "";
            state.MonoModCoreAssemblyVersion = monoModCore.GetName().Version?.ToString() ?? "";

            var core110 = monoModCore.GetType(Core110RuntimeTypeName, false, false);
            state.MonoModCore110RuntimeResolvable = core110 != null;
            if (state.MonoModCore110RuntimeRequired && core110 == null)
            {
                state.MonoModRuntimeBridgeStatus = "failed";
                state.MonoModRuntimeBridgeDetail = "MonoMod.Core does not expose Core110Runtime for .NET 11.";
                throw new InvalidOperationException(state.MonoModRuntimeBridgeDetail);
            }

            state.MonoModRuntimeBridgeStatus = state.MonoModCore110RuntimeRequired ? "ready" : "not-required";
            state.MonoModRuntimeBridgeDetail = state.MonoModCore110RuntimeRequired
                ? "MonoMod.Core exposes Core110Runtime for .NET 11."
                : "Core110Runtime is not required on this managed runtime.";
        }

        private static Assembly FindOrLoadAssembly(string assemblyName, params string[] searchDirectories)
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
            if (loaded != null)
            {
                return loaded;
            }

            foreach (var directory in SearchDirectories(searchDirectories))
            {
                var candidate = Path.Combine(directory, assemblyName + ".dll");
                if (!File.Exists(candidate))
                {
                    continue;
                }

                try
                {
                    return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
                }
                catch (FileLoadException)
                {
                    loaded = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
                    if (loaded != null)
                    {
                        return loaded;
                    }

                    throw;
                }
            }

            return null;
        }

        private static IEnumerable<string> SearchDirectories(IEnumerable<string> directories)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in directories ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) || !seen.Add(directory))
                {
                    continue;
                }

                yield return directory;
            }
        }

        private static string BuildDetail(OpenACAIHarmonyBackendInitializationState state)
        {
            var backendDetail = string.Equals(state.NormalizedBackend, DefaultBackend, StringComparison.OrdinalIgnoreCase)
                ? "using MonoMod automatic backend selection"
                : "set MONOMOD_DMD_TYPE=" + state.NormalizedBackend;
            return "OpenACAI-owned Harmony backend selected " + Display(state.NormalizedBackend)
                + " from " + Display(state.BackendSource)
                + "; " + backendDetail
                + "; " + state.MonoModRuntimeBridgeDetail;
        }

        private static string Display(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
        }

        private readonly struct BackendSelection
        {
            internal BackendSelection(string source, string value)
            {
                Source = source;
                Value = value;
            }

            internal string Source { get; }
            internal string Value { get; }
        }
    }

    internal sealed class OpenACAIHarmonyBackendInitializationState
    {
        internal bool OpenAcaiOwned { get; set; }
        internal bool BepInExHarmonyBackendFixUsed { get; set; }
        internal string BackendSource { get; set; } = "";
        internal string RequestedBackend { get; set; } = "";
        internal string NormalizedBackend { get; set; } = "";
        internal string EnvironmentValueBefore { get; set; } = "";
        internal string EnvironmentValueAfter { get; set; } = "";
        internal bool EnvironmentApplied { get; set; }
        internal bool MonoModCorePresent { get; set; }
        internal string MonoModCoreAssemblyPath { get; set; } = "";
        internal string MonoModCoreAssemblyVersion { get; set; } = "";
        internal bool MonoModCore110RuntimeRequired { get; set; }
        internal bool MonoModCore110RuntimeResolvable { get; set; }
        internal string MonoModRuntimeBridgeStatus { get; set; } = "";
        internal string MonoModRuntimeBridgeDetail { get; set; } = "";
        internal string Status { get; set; } = "";
        internal string Detail { get; set; } = "";
    }
}
