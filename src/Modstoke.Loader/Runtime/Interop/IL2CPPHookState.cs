// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Reads the early BepInEx IL2CPP native hook status file. This is separate from
// the normal plugin state because hook failures can happen before OpenACAI loads.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace Modstoke.Loader
{
    public static class IL2CPPHookState
    {
        public const string ServiceKey = "openacai.loader.il2cppHooks";
        public const string RootDirectoryName = "Modstoke";
        [Obsolete("Use RootDirectoryName.")]
        public const string RuntimeDirectoryName = RootDirectoryName;
        public const string RuntimeStateDirectoryName = "Runtime";
        public const string LegacyStateDirectoryName = "OpenACAILoader";
        public const string StateFileName = "openacai-il2cpp-hook-state.json";

        internal static IL2CPPHookStateSnapshot Publish(string pluginPath, string gameRoot)
        {
            var snapshot = Load(pluginPath, gameRoot);
            Modding.SetService(ServiceKey, snapshot);
            PublishShimState(snapshot);
            return snapshot;
        }

        internal static IL2CPPHookStateSnapshot Load(string pluginPath, string gameRoot)
        {
            string statePath = null;
            try
            {
                statePath = ResolveStatePath(pluginPath, gameRoot);
                if (string.IsNullOrWhiteSpace(statePath))
                    return IL2CPPHookStateSnapshot.Missing(statePath);

                // The preloader replaces this file atomically while startup advances.
                using (var stream = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                using (var document = JsonDocument.Parse(reader.ReadToEnd()))
                {
                    var root = document.RootElement;
                    return new IL2CPPHookStateSnapshot(
                        true,
                        false,
                        statePath,
                        ReadNullableInt(root, "schema"),
                        ReadString(root, "generatedUtc"),
                        ReadString(root, "status"),
                        ReadString(root, "detail"),
                        ReadBool(root, "failed"),
                        ReadBool(root, "gameAssemblyLoaded"),
                        ReadBool(root, "runtimeInvokeExportFound"),
                        ReadBool(root, "runtimeInvokePatched"),
                        ReadString(root, "runtimeInvokePointer"),
                        ReadBool(root, "chainloaderExecutionStarted"),
                        ReadBool(root, "chainloaderExecuted"),
                        ReadBool(root, "hookDisposed"),
                        ReadString(root, "selectedProvider"),
                        ReadString(root, "detourProviderMode"),
                        ReadProviderAvailability(root),
                        ReadString(root, "dotnetRuntimeVersion"),
                        ReadString(root, "dotnetFrameworkDescription"),
                        ReadString(root, "gameRoot"),
                        ReadString(root, "processName"),
                        ReadString(root, "lastError"));
                }
            }
            catch (FileNotFoundException)
            {
                return IL2CPPHookStateSnapshot.Missing(statePath);
            }
            catch (DirectoryNotFoundException)
            {
                return IL2CPPHookStateSnapshot.Missing(statePath);
            }
            catch (Exception ex)
            {
                return IL2CPPHookStateSnapshot.ParseFailure(statePath, ex.Message);
            }
        }

        private static void PublishShimState(IL2CPPHookStateSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            if (!snapshot.Found)
            {
                LoaderRuntime.MarkShim(
                    CompatibilityShimIds.Il2CppNativeHooks,
                    "IL2CPP native hooks",
                    "BepInEx IL2CPP",
                    CompatibilityShimStatus.Warning,
                    "status-unavailable",
                    true,
                    false,
                    false,
                    "BepInEx IL2CPP hook status was not found. The runtime may be older than the Modstoke hook-state patch.");
                return;
            }

            if (snapshot.ParseFailed)
            {
                LoaderRuntime.MarkShim(
                    CompatibilityShimIds.Il2CppNativeHooks,
                    "IL2CPP native hooks",
                    "BepInEx IL2CPP",
                    CompatibilityShimStatus.Warning,
                    "status-unreadable",
                    true,
                    false,
                    false,
                    snapshot.Detail);
                return;
            }

            if (snapshot.Failed)
            {
                LoaderRuntime.MarkShimFailed(
                    CompatibilityShimIds.Il2CppNativeHooks,
                    "IL2CPP native hooks",
                    "BepInEx IL2CPP",
                    string.IsNullOrWhiteSpace(snapshot.Status) ? "startup" : snapshot.Status,
                    snapshot.Detail);
                return;
            }

            if (snapshot.Ready)
            {
                var mode = snapshot.ChainloaderExecuted || string.Equals(snapshot.Status, "loaded", StringComparison.OrdinalIgnoreCase)
                    ? "chainloader-executed" : "runtime-invoke-patched";
                LoaderRuntime.MarkShim(
                    CompatibilityShimIds.Il2CppNativeHooks,
                    "IL2CPP native hooks",
                    "BepInEx IL2CPP",
                    string.Equals(snapshot.Status, "warning", StringComparison.OrdinalIgnoreCase)
                        ? CompatibilityShimStatus.Warning : CompatibilityShimStatus.Ready,
                    mode,
                    true,
                    true,
                    false,
                    BuildReadyDetail(snapshot));
                return;
            }

            LoaderRuntime.MarkShim(
                CompatibilityShimIds.Il2CppNativeHooks,
                "IL2CPP native hooks",
                "BepInEx IL2CPP",
                CompatibilityShimStatus.Warning,
                string.IsNullOrWhiteSpace(snapshot.Status) ? "pending" : snapshot.Status,
                true,
                false,
                false,
                snapshot.Detail);
        }

        private static string BuildReadyDetail(IL2CPPHookStateSnapshot snapshot)
        {
            var provider = string.IsNullOrWhiteSpace(snapshot.SelectedProvider)
                ? "unknown provider"
                : snapshot.SelectedProvider;
            var mode = string.IsNullOrWhiteSpace(snapshot.DetourProviderMode)
                ? "auto"
                : snapshot.DetourProviderMode;
            var state = snapshot.HookDisposed
                ? "BepInEx IL2CPP bootstrap completed and its startup detour was released"
                : snapshot.ChainloaderExecuted || string.Equals(snapshot.Status, "loaded", StringComparison.OrdinalIgnoreCase)
                    ? "BepInEx IL2CPP bootstrap completed"
                    : "BepInEx IL2CPP native hook is ready";
            return state + " via " + provider + " (" + mode + "). " + snapshot.Detail;
        }

        private static string ResolveStatePath(string pluginPath, string gameRoot)
        {
            // NativeHookStatusWriter and the compatibility writer publish under the plugin root.
            var pluginRoot = pluginPath;
            if (string.IsNullOrWhiteSpace(pluginRoot) && !string.IsNullOrWhiteSpace(gameRoot))
            {
                pluginRoot = Path.Combine(gameRoot, "BepInEx", "plugins");
            }

            if (string.IsNullOrWhiteSpace(pluginRoot))
            {
                return null;
            }

            return Path.Combine(pluginRoot, LegacyStateDirectoryName, StateFileName);
        }

        private static string ReadString(JsonElement root, string name)
        {
            JsonElement value;
            return root.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static int? ReadNullableInt(JsonElement root, string name)
        {
            JsonElement value;
            return root.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
                ? result
                : (int?)null;
        }

        private static bool ReadBool(JsonElement root, string name)
        {
            JsonElement value;
            if (!root.TryGetProperty(name, out value))
            {
                return false;
            }

            if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                return value.GetBoolean();

            throw new JsonException("Hook status property '" + name + "' must be a Boolean.");
        }

        private static IReadOnlyDictionary<string, bool> ReadProviderAvailability(JsonElement root)
        {
            var providers = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            JsonElement value;
            if (!root.TryGetProperty("providerAvailability", out value) || value.ValueKind != JsonValueKind.Object)
            {
                return providers;
            }

            foreach (var provider in value.EnumerateObject())
            {
                if (provider.Value.ValueKind == JsonValueKind.True || provider.Value.ValueKind == JsonValueKind.False)
                {
                    providers[provider.Name] = provider.Value.GetBoolean();
                }
            }

            return providers;
        }
    }

    public sealed class IL2CPPHookStateSnapshot
    {
        private readonly IReadOnlyDictionary<string, bool> _providerAvailability;

        internal IL2CPPHookStateSnapshot(
            bool found,
            bool parseFailed,
            string path,
            int? schema,
            string generatedUtc,
            string status,
            string detail,
            bool failed,
            bool gameAssemblyLoaded,
            bool runtimeInvokeExportFound,
            bool runtimeInvokePatched,
            string runtimeInvokePointer,
            bool chainloaderExecutionStarted,
            bool chainloaderExecuted,
            bool hookDisposed,
            string selectedProvider,
            string detourProviderMode,
            IReadOnlyDictionary<string, bool> providerAvailability,
            string dotnetRuntimeVersion,
            string dotnetFrameworkDescription,
            string gameRoot,
            string processName,
            string lastError)
        {
            Found = found;
            ParseFailed = parseFailed;
            Path = path;
            Schema = schema;
            GeneratedUtc = generatedUtc;
            Status = status;
            Detail = detail;
            Failed = failed || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);
            GameAssemblyLoaded = gameAssemblyLoaded;
            RuntimeInvokeExportFound = runtimeInvokeExportFound;
            RuntimeInvokePatched = runtimeInvokePatched;
            RuntimeInvokePointer = runtimeInvokePointer;
            ChainloaderExecutionStarted = chainloaderExecutionStarted;
            ChainloaderExecuted = chainloaderExecuted;
            HookDisposed = hookDisposed;
            SelectedProvider = selectedProvider;
            DetourProviderMode = detourProviderMode;
            _providerAvailability = new ReadOnlyDictionary<string, bool>(providerAvailability == null
                ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(providerAvailability, StringComparer.OrdinalIgnoreCase));
            DotnetRuntimeVersion = dotnetRuntimeVersion;
            DotnetFrameworkDescription = dotnetFrameworkDescription;
            GameRoot = gameRoot;
            ProcessName = processName;
            LastError = lastError;
            Ready = !ParseFailed
                && !Failed
                && (ChainloaderExecuted
                    || string.Equals(status, "loaded", StringComparison.OrdinalIgnoreCase)
                    || (!HookDisposed
                        && (RuntimeInvokePatched || string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase))));
        }

        public bool Found { get; }
        public bool ParseFailed { get; }
        public bool Ready { get; }
        public string Path { get; }
        public int? Schema { get; }
        public string GeneratedUtc { get; }
        public string Status { get; }
        public string Detail { get; }
        public bool Failed { get; }
        public bool GameAssemblyLoaded { get; }
        public bool RuntimeInvokeExportFound { get; }
        public bool RuntimeInvokePatched { get; }
        public string RuntimeInvokePointer { get; }
        public bool ChainloaderExecutionStarted { get; }
        public bool ChainloaderExecuted { get; }
        public bool HookDisposed { get; }
        public string SelectedProvider { get; }
        public string DetourProviderMode { get; }
        public IReadOnlyDictionary<string, bool> ProviderAvailability { get { return _providerAvailability; } }
        public string DotnetRuntimeVersion { get; }
        public string DotnetFrameworkDescription { get; }
        public string GameRoot { get; }
        public string ProcessName { get; }
        public string LastError { get; }

        internal static IL2CPPHookStateSnapshot Missing(string path)
        {
            return new IL2CPPHookStateSnapshot(
                false,
                false,
                path,
                null,
                null,
                "missing",
                "BepInEx IL2CPP hook status file was not found.",
                false,
                false,
                false,
                false,
                null,
                false,
                false,
                false,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
        }

        internal static IL2CPPHookStateSnapshot ParseFailure(string path, string error)
        {
            return new IL2CPPHookStateSnapshot(
                true,
                true,
                path,
                null,
                null,
                "unreadable",
                "BepInEx IL2CPP hook status file could not be parsed: " + error,
                false,
                false,
                false,
                false,
                null,
                false,
                false,
                false,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                error);
        }
    }
}

