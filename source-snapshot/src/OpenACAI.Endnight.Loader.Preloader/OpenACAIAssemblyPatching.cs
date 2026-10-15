// SPDX-License-Identifier: GPL-3.0-or-later
// Startup sequence adapted from BepInEx's IL2CPP Preloader.cs, copyright BepInEx contributors.
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using BepInEx;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class OpenACAIAssemblyPatching
    {
        internal static void Run(string interopDirectory, AssemblyPatchingState state, Func<byte[], string, Assembly> loadAssembly = null)
        {
            using (var engine = new OpenACAIAssemblyPatcher(state, loadAssembly))
            {
                engine.DiscoverPatchers(Paths.PatcherPluginPath);
                state.PatcherCount = engine.Context.PatcherPlugins.Count;
                AssemblyPatcherDiagnostics.Write(() =>
                    OpenACAIPreloaderLog.Log.LogInfo($"{state.PatcherCount} patcher plugins loaded"));

                engine.ReadAssemblies(interopDirectory);
                state.AvailableAssemblyCount = engine.Context.AvailableAssemblies.Count;
                AssemblyPatcherDiagnostics.Write(() =>
                    OpenACAIPreloaderLog.Log.LogInfo($"{state.AvailableAssemblyCount} assemblies discovered"));

                engine.PatchAndLoad();
                state.LoadedAssemblyCount = engine.Context.LoadedAssemblies.Count;
            }

            state.Status = state.FailureCount != 0 ? "completed-with-errors"
                : state.SkippedPatchCount != 0 || state.EarlyLoadedAssemblyCount != 0 ? "completed-with-skips"
                : "completed";
            state.Detail = $"OpenACAI patcher engine completed: {state.PatcherCount} plugins, " +
                $"{state.AppliedPatchCount} patch applications, {state.LoadedAssemblyCount} assemblies loaded, " +
                $"{state.FailureCount} failures, {state.SkippedPatchCount} skipped patches, " +
                $"{state.EarlyLoadedAssemblyCount} assemblies already loaded too early. See the preloader log for details.";
        }
    }

    public sealed class AssemblyPatchingState
    {
        public bool CompatibilityEngineUsed { get; internal set; }
        public int PatcherCount { get; internal set; }
        public int AvailableAssemblyCount { get; internal set; }
        public int PatchDefinitionCount { get; internal set; }
        public int AppliedPatchCount { get; internal set; }
        public int LoadedAssemblyCount { get; internal set; }
        public int DiscoveryFailureCount { get; internal set; }
        public int LifecycleFailureCount { get; internal set; }
        public int PatchFailureCount { get; internal set; }
        public int AssemblyLoadFailureCount { get; internal set; }
        public int DumpFailureCount { get; internal set; }
        public int CleanupFailureCount { get; internal set; }
        public int SkippedPatchCount { get; internal set; }
        public int EarlyLoadedAssemblyCount { get; internal set; }
        public int FailureCount => DiscoveryFailureCount + LifecycleFailureCount + PatchFailureCount
            + AssemblyLoadFailureCount + DumpFailureCount + CleanupFailureCount;
        public string Status { get; internal set; }
        public string Detail { get; internal set; }

        internal bool Completed => Status == "completed" || Status == "completed-with-errors"
            || Status == "completed-with-skips";
    }
}
