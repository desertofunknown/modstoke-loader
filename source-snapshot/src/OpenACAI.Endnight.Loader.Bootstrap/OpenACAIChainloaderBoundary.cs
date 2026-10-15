// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned chainloader boundary journal. The current runtime owns native
// and managed preloader orchestration, while the remaining BepInEx IL2CPP
// components are compatibility dependencies until their internals are ported.

using System;
using System.IO;

namespace OpenACAI.Endnight.Loader.Bootstrap
{
    internal static class OpenACAIChainloaderBoundary
    {
        private const string StateRelativePath = "OpenACAI\\Runtime\\openacai-chainloader-state.json";
        private const string Il2CppPreloaderStateRelativePath = "OpenACAI\\Runtime\\openacai-il2cpp-preloader-state.json";
        private const string RuntimeCoreAssemblyName = "OpenACAI.Endnight.Loader.Core.dll";
        private const string BootstrapAssemblyName = "OpenACAI.Endnight.Loader.Bootstrap.dll";

        internal static void WriteState(
            string gameRoot,
            string runtimeDirectory,
            string compatibilityDirectory,
            string bepInExCoreDirectory,
            string enteredUtc,
            string mode,
            string status,
            string compatibilityBridgeAssembly,
            bool delegatedToCompatibilityBridge,
            bool compatibilityBridgeReturned,
            string exceptionType,
            string detail,
            bool openAcaiIl2CppChainloaderPortComplete = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(gameRoot))
                {
                    gameRoot = Environment.CurrentDirectory;
                }

                runtimeDirectory = string.IsNullOrWhiteSpace(runtimeDirectory)
                    ? Path.Combine(gameRoot, "OpenACAI", "Runtime")
                    : runtimeDirectory;
                compatibilityDirectory = string.IsNullOrWhiteSpace(compatibilityDirectory)
                    ? Path.Combine(runtimeDirectory, "Compatibility")
                    : compatibilityDirectory;
                bepInExCoreDirectory = string.IsNullOrWhiteSpace(bepInExCoreDirectory)
                    ? Path.Combine(gameRoot, "BepInEx", "core")
                    : bepInExCoreDirectory;
                compatibilityBridgeAssembly = string.IsNullOrWhiteSpace(compatibilityBridgeAssembly)
                    ? Path.Combine(bepInExCoreDirectory, "BepInEx.Unity.IL2CPP.dll")
                    : compatibilityBridgeAssembly;

                var statePath = Path.Combine(gameRoot, StateRelativePath);
                var stateDirectory = Path.GetDirectoryName(statePath);
                if (!string.IsNullOrWhiteSpace(stateDirectory))
                {
                    Directory.CreateDirectory(stateDirectory);
                }

                var bootstrapAssembly = Path.Combine(runtimeDirectory, BootstrapAssemblyName);
                var runtimeCoreAssembly = Path.Combine(runtimeDirectory, RuntimeCoreAssemblyName);
                var bridgeExists = File.Exists(compatibilityBridgeAssembly);
                var runtimeCoreExists = File.Exists(runtimeCoreAssembly);
                var bootstrapExists = File.Exists(bootstrapAssembly);
                var il2CppPreloaderStatePath = Path.Combine(gameRoot, Il2CppPreloaderStateRelativePath);
                var il2CppPreloaderStateExists = File.Exists(il2CppPreloaderStatePath);

                var nativeMode = string.Equals(mode, "openacai-native", StringComparison.OrdinalIgnoreCase);
                var readyStatus = string.Equals(status, "READY", StringComparison.OrdinalIgnoreCase);
                var chainloaderReady = readyStatus
                    && (nativeMode || delegatedToCompatibilityBridge && compatibilityBridgeReturned && bridgeExists);
                var compatibilityBridgeMode = nativeMode
                    ? "fallback-only"
                    : "delegated-bepinex-il2cpp";
                var executionPath = nativeMode
                    ? "openacai-rust-native-host -> openacai-managed-bootstrap -> openacai-owned-preloader-orchestration -> openacai-il2cpp-preloader-phase-runner -> openacai-direct-core-trigger -> openacai-core-runtime (fallback: bepInEx-il2cpp-chainloader-instance)"
                    : "openacai-rust-native-host -> openacai-managed-bootstrap -> compatibility-bridge -> bepInEx-il2cpp-chainloader -> openacai-core-plugin";
                var statusDetail = string.IsNullOrWhiteSpace(detail)
                    ? "OpenACAI chainloader boundary recorded."
                    : detail;

                var json =
                    "{" + Environment.NewLine
                    + "  \"schema\": 1," + Environment.NewLine
                    + "  \"serviceKey\": \"openacai.loader.chainloader\"," + Environment.NewLine
                    + "  \"enteredUtc\": \"" + Escape(enteredUtc) + "\"," + Environment.NewLine
                    + "  \"updatedUtc\": \"" + DateTimeOffset.UtcNow.ToString("O") + "\"," + Environment.NewLine
                    + "  \"status\": \"" + Escape(status) + "\"," + Environment.NewLine
                    + "  \"detail\": \"" + Escape(statusDetail) + "\"," + Environment.NewLine
                    + "  \"exceptionType\": \"" + Escape(exceptionType) + "\"," + Environment.NewLine
                    + "  \"owner\": \"OpenACAI\"," + Environment.NewLine
                    + "  \"mode\": \"" + Escape(mode) + "\"," + Environment.NewLine
                    + "  \"chainloaderReady\": " + JsonBool(chainloaderReady) + "," + Environment.NewLine
                    + "  \"doorstopTargetOwnedByOpenAcai\": true," + Environment.NewLine
                    + "  \"nativeHostOwnedByOpenAcai\": true," + Environment.NewLine
                    + "  \"managedBootstrapOwnedByOpenAcai\": true," + Environment.NewLine
                    + "  \"openAcaiPreloaderOrchestrationOwned\": " + JsonBool(nativeMode) + "," + Environment.NewLine
                    + "  \"openAcaiIl2CppPreloaderPhaseRunnerOwned\": " + JsonBool(nativeMode) + "," + Environment.NewLine
                    + "  \"openAcaiPreloaderPortComplete\": false," + Environment.NewLine
                    + "  \"openAcaiIl2CppChainloaderPortComplete\": " + JsonBool(openAcaiIl2CppChainloaderPortComplete) + "," + Environment.NewLine
                    + "  \"bepInExPreloaderRunApiDependencyUsed\": false," + Environment.NewLine
                    + "  \"bepInExIl2CppComponentReflectionUsed\": " + JsonBool(nativeMode) + "," + Environment.NewLine
                    + "  \"delegatedToCompatibilityBridge\": " + JsonBool(delegatedToCompatibilityBridge) + "," + Environment.NewLine
                    + "  \"compatibilityBridgeReturned\": " + JsonBool(compatibilityBridgeReturned) + "," + Environment.NewLine
                    + "  \"compatibilityBridgeMode\": \"" + Escape(compatibilityBridgeMode) + "\"," + Environment.NewLine
                    + "  \"nextPortBoundary\": \"openacai-owned-preloader-and-il2cpp-chainloader\"," + Environment.NewLine
                    + "  \"currentExecutionPath\": \"" + Escape(executionPath) + "\"," + Environment.NewLine
                    + "  \"bootstrapAssembly\": \"" + Escape(bootstrapAssembly) + "\"," + Environment.NewLine
                    + "  \"bootstrapAssemblyExists\": " + JsonBool(bootstrapExists) + "," + Environment.NewLine
                    + "  \"runtimeCoreAssembly\": \"" + Escape(runtimeCoreAssembly) + "\"," + Environment.NewLine
                    + "  \"runtimeCoreAssemblyExists\": " + JsonBool(runtimeCoreExists) + "," + Environment.NewLine
                    + "  \"il2CppPreloaderStateFile\": \"" + Escape(il2CppPreloaderStatePath) + "\"," + Environment.NewLine
                    + "  \"il2CppPreloaderStateFileExists\": " + JsonBool(il2CppPreloaderStateExists) + "," + Environment.NewLine
                    + "  \"compatibilityBridgeAssembly\": \"" + Escape(compatibilityBridgeAssembly) + "\"," + Environment.NewLine
                    + "  \"compatibilityBridgeAssemblyExists\": " + JsonBool(bridgeExists) + "," + Environment.NewLine
                    + "  \"runtimeDirectory\": \"" + Escape(runtimeDirectory) + "\"," + Environment.NewLine
                    + "  \"compatibilityDirectory\": \"" + Escape(compatibilityDirectory) + "\"," + Environment.NewLine
                    + "  \"bepInExCoreDirectory\": \"" + Escape(bepInExCoreDirectory) + "\"," + Environment.NewLine
                    + "  \"plannedStages\": [" + Environment.NewLine
                    + "    \"openacai.resolve-runtime-layout\"," + Environment.NewLine
                    + "    \"openacai.install-managed-resolvers\"," + Environment.NewLine
                    + "    \"openacai.prepare-doorstop-environment\"," + Environment.NewLine
                    + "    \"openacai.run-il2cpp-preloader-phases\"," + Environment.NewLine
                    + "    \"openacai.plan-direct-core-invocation\"," + Environment.NewLine
                    + "    \"openacai.arm-direct-core-invocation-trigger\"," + Environment.NewLine
                    + "    \"openacai.execute-direct-core-runtime\"," + Environment.NewLine
                    + "    \"compatibility.invoke-bepinex-il2cpp-chainloader-instance\"," + Environment.NewLine
                    + "    \"openacai.load-core-runtime-plugin\"" + Environment.NewLine
                    + "  ]" + Environment.NewLine
                    + "}" + Environment.NewLine;
                File.WriteAllText(statePath, json);
            }
            catch
            {
                // Chainloader state is diagnostic and must never affect launch.
            }
        }

        private static string JsonBool(bool value)
        {
            return value ? "true" : "false";
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }
    }
}
