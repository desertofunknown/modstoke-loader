// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Reads the early BepInEx IL2CPP native hook status file. This is separate from
// the normal plugin state because hook failures can happen before OpenACAI loads.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace OpenACAI.Loader
{
    public static class OpenACAIIL2CPPHookState
    {
        public const string ServiceKey = "openacai.loader.il2cppHooks";
        public const string RuntimeDirectoryName = "OpenACAI";
        public const string RuntimeStateDirectoryName = "Runtime";
        public const string LegacyStateDirectoryName = "OpenACAILoader";
        public const string StateFileName = "openacai-il2cpp-hook-state.json";

        internal static OpenACAIIL2CPPHookStateSnapshot Publish(string pluginPath, string gameRoot)
        {
            var snapshot = Load(pluginPath, gameRoot);
            OpenACAIModding.SetService(ServiceKey, snapshot);
            PublishShimState(snapshot);
            return snapshot;
        }

        internal static OpenACAIIL2CPPHookStateSnapshot Load(string pluginPath, string gameRoot)
        {
            var statePath = ResolveStatePath(pluginPath, gameRoot);
            if (string.IsNullOrWhiteSpace(statePath) || !File.Exists(statePath))
            {
                return OpenACAIIL2CPPHookStateSnapshot.Missing(statePath);
            }

            try
            {
                using (var document = JsonDocument.Parse(File.ReadAllText(statePath)))
                {
                    var root = document.RootElement;
                    return new OpenACAIIL2CPPHookStateSnapshot(
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
            catch (Exception ex)
            {
                return OpenACAIIL2CPPHookStateSnapshot.ParseFailure(statePath, ex.Message);
            }
        }

        private static void PublishShimState(OpenACAIIL2CPPHookStateSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            if (!snapshot.Found)
            {
                OpenACAIRuntime.MarkShim(
                    OpenACAICompatibilityShimIds.Il2CppNativeHooks,
                    "IL2CPP native hooks",
                    "BepInEx IL2CPP",
                    OpenACAICompatibilityShimStatus.Warning,
                    "status-unavailable",
                    true,
                    false,
                    false,
                    "BepInEx IL2CPP hook status was not found. The runtime may be older than the OpenACAI hook-state patch.");
                return;
            }

            if (snapshot.ParseFailed)
            {
                OpenACAIRuntime.MarkShim(
                    OpenACAICompatibilityShimIds.Il2CppNativeHooks,
                    "IL2CPP native hooks",
                    "BepInEx IL2CPP",
                    OpenACAICompatibilityShimStatus.Warning,
                    "status-unreadable",
                    true,
                    false,
                    false,
                    snapshot.Detail);
                return;
            }

            if (snapshot.Failed)
            {
                OpenACAIRuntime.MarkShimFailed(
                    OpenACAICompatibilityShimIds.Il2CppNativeHooks,
                    "IL2CPP native hooks",
                    "BepInEx IL2CPP",
                    string.IsNullOrWhiteSpace(snapshot.Status) ? "startup" : snapshot.Status,
                    snapshot.Detail);
                return;
            }

            if (snapshot.Ready)
            {
                var mode = snapshot.ChainloaderExecuted ? "chainloader-executed" : "runtime-invoke-patched";
                OpenACAIRuntime.MarkShimReady(
                    OpenACAICompatibilityShimIds.Il2CppNativeHooks,
                    "IL2CPP native hooks",
                    "BepInEx IL2CPP",
                    mode,
                    BuildReadyDetail(snapshot));
                return;
            }

            OpenACAIRuntime.MarkShim(
                OpenACAICompatibilityShimIds.Il2CppNativeHooks,
                "IL2CPP native hooks",
                "BepInEx IL2CPP",
                OpenACAICompatibilityShimStatus.Warning,
                string.IsNullOrWhiteSpace(snapshot.Status) ? "pending" : snapshot.Status,
                true,
                false,
                false,
                snapshot.Detail);
        }

        private static string BuildReadyDetail(OpenACAIIL2CPPHookStateSnapshot snapshot)
        {
            var provider = string.IsNullOrWhiteSpace(snapshot.SelectedProvider)
                ? "unknown provider"
                : snapshot.SelectedProvider;
            var mode = string.IsNullOrWhiteSpace(snapshot.DetourProviderMode)
                ? "auto"
                : snapshot.DetourProviderMode;
            return "BepInEx IL2CPP native hook is ready via " + provider + " (" + mode + "). " + snapshot.Detail;
        }

        private static string ResolveStatePath(string pluginPath, string gameRoot)
        {
            if (!string.IsNullOrWhiteSpace(gameRoot))
            {
                var runtimeStatePath = Path.Combine(gameRoot, RuntimeDirectoryName, RuntimeStateDirectoryName, StateFileName);
                if (File.Exists(runtimeStatePath))
                {
                    return runtimeStatePath;
                }
            }

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

            return value.ValueKind == JsonValueKind.True;
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
                if (provider.Value.ValueKind == JsonValueKind.True)
                {
                    providers[provider.Name] = true;
                }
                else if (provider.Value.ValueKind == JsonValueKind.False)
                {
                    providers[provider.Name] = false;
                }
            }

            return providers;
        }
    }

    public sealed class OpenACAIIL2CPPHookStateSnapshot
    {
        private readonly IReadOnlyDictionary<string, bool> _providerAvailability;

        internal OpenACAIIL2CPPHookStateSnapshot(
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
            _providerAvailability = new Dictionary<string, bool>(providerAvailability ?? new Dictionary<string, bool>(), StringComparer.OrdinalIgnoreCase);
            DotnetRuntimeVersion = dotnetRuntimeVersion;
            DotnetFrameworkDescription = dotnetFrameworkDescription;
            GameRoot = gameRoot;
            ProcessName = processName;
            LastError = lastError;
            Ready = !ParseFailed
                && !Failed
                && (RuntimeInvokePatched
                    || ChainloaderExecuted
                    || string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "loaded", StringComparison.OrdinalIgnoreCase));
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

        internal static OpenACAIIL2CPPHookStateSnapshot Missing(string path)
        {
            return new OpenACAIIL2CPPHookStateSnapshot(
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
                new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
                null,
                null,
                null,
                null,
                null);
        }

        internal static OpenACAIIL2CPPHookStateSnapshot ParseFailure(string path, string error)
        {
            return new OpenACAIIL2CPPHookStateSnapshot(
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
                new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
                null,
                null,
                null,
                null,
                error);
        }
    }
}

