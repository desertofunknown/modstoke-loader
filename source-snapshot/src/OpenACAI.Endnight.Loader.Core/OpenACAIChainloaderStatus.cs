// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned chainloader status reader. The bootstrap writes this before
// the core plugin starts; the core runtime republishes it as menu and SDK state.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenACAI.Loader;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenACAIChainloaderStatus
    {
        internal const string ServiceKey = OpenACAIModding.ChainloaderServiceKey;
        internal const string Il2CppPreloaderServiceKey = "openacai.loader.il2cppPreloader";
        internal const string Il2CppChainloaderServiceKey = "openacai.loader.il2CppChainloaderAdapter";

        internal static OpenACAIChainloaderStateSnapshot State => CreateState();

        internal static void Initialize()
        {
            Publish();
            if (!OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke chainloader status provider could not be registered.");
            }
        }

        internal static OpenACAIChainloaderStateSnapshot CreateState()
        {
            var state = ReadState();
            OpenACAIModding.SetService(ServiceKey, state);
            return state;
        }

        internal static OpenACAIChainloaderStateSnapshot Publish()
        {
            var state = CreateState();
            CreateIl2CppPreloaderState();
            CreateIl2CppChainloaderState();
            return state;
        }

        internal static OpenACAIIL2CPPPreloaderStateSnapshot CreateIl2CppPreloaderState()
        {
            var state = ReadIl2CppPreloaderState();
            OpenACAIModding.SetService(Il2CppPreloaderServiceKey, state);
            return state;
        }

        internal static OpenACAIIL2CPPChainloaderAdapterStateSnapshot CreateIl2CppChainloaderState()
        {
            var state = ReadIl2CppChainloaderState();
            OpenACAIModding.SetService(Il2CppChainloaderServiceKey, state);
            return state;
        }

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            var snapshot = CreateState();
            var il2CppPreloader = CreateIl2CppPreloaderState();
            var il2CppChainloader = CreateIl2CppChainloaderState();
            return new OpenACAIMenuContentSnapshot(
                "Modstoke Chainloader",
                "OpenACAI-owned bootstrap, preloader, and chainloader boundary state.",
                new[]
                {
                    CreateOwnershipSection(snapshot),
                    CreateExecutionSection(snapshot),
                    CreateIl2CppPreloaderSection(snapshot, il2CppPreloader),
                    CreateIl2CppChainloaderSection(il2CppChainloader),
                    CreateFilesSection(snapshot),
                    CreatePlanSection(snapshot)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateOwnershipSection(OpenACAIChainloaderStateSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Ownership",
                new[]
                {
                    Row("Owner", snapshot.Owner, snapshot.Ready ? "READY" : snapshot.Status, snapshot.Detail),
                    Row("Mode", snapshot.Mode, snapshot.Ready ? "READY" : snapshot.Status, "Bootstrap mode selected before IL2CPP preloader startup."),
                    Row("Doorstop Target", snapshot.DoorstopTargetOwnedByOpenAcai ? "OpenACAI" : "Compatibility", snapshot.DoorstopTargetOwnedByOpenAcai ? "READY" : "CHECK", "The managed bootstrap target is owned by OpenACAI."),
                    Row("Native Host", snapshot.NativeHostOwnedByOpenAcai ? "OpenACAI" : "Compatibility", snapshot.NativeHostOwnedByOpenAcai ? "READY" : "CHECK", "The proxy DLL/native host is the Rust OpenACAI implementation."),
                    Row("Managed Bootstrap", snapshot.ManagedBootstrapOwnedByOpenAcai ? "OpenACAI" : "Compatibility", snapshot.ManagedBootstrapOwnedByOpenAcai ? "READY" : "CHECK", "The managed bootstrap entrypoint is OpenACAI-owned."),
                    Row("Preloader Orchestration", snapshot.OpenAcaiPreloaderOrchestrationOwned ? "OpenACAI" : "Compatibility bridge", snapshot.OpenAcaiPreloaderOrchestrationOwned ? "READY" : "CHECK", "OpenACAI now selects and prepares the IL2CPP preloader path before invoking compatibility APIs."),
                    Row("IL2CPP Phase Runner", snapshot.OpenAcaiIl2CppPreloaderPhaseRunnerOwned ? "OpenACAI" : "Compatibility", snapshot.OpenAcaiIl2CppPreloaderPhaseRunnerOwned ? "READY" : "CHECK", "OpenACAI runs IL2CPP preloader phases directly instead of calling the old monolithic preloader API."),
                    Row("Modstoke Preloader Port", snapshot.OpenAcaiPreloaderPortComplete ? "Complete" : "In progress", snapshot.OpenAcaiPreloaderPortComplete ? "READY" : "CHECK", "Remaining target: replace BepInEx preloader internals with OpenACAI code."),
                    Row("Modstoke IL2CPP Chainloader Port", snapshot.OpenAcaiIl2CppChainloaderPortComplete ? "Complete" : "In progress", snapshot.OpenAcaiIl2CppChainloaderPortComplete ? "READY" : "CHECK", "Remaining target: replace BepInEx IL2CPP chainloader internals with OpenACAI code.")
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateExecutionSection(OpenACAIChainloaderStateSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Execution",
                new[]
                {
                    Row("Status", snapshot.Status, snapshot.Status, snapshot.Detail),
                    Row("Ready", snapshot.ChainloaderReady ? "Ready" : "Waiting", snapshot.ChainloaderReady ? "READY" : snapshot.Status, "Chainloader boundary readiness from the bootstrap journal."),
                    Row("Compatibility Bridge", snapshot.DelegatedToCompatibilityBridge ? "Delegated" : "Not delegated", snapshot.DelegatedToCompatibilityBridge ? "INFO" : "READY", snapshot.CompatibilityBridgeMode),
                    Row("Bridge Return", snapshot.CompatibilityBridgeReturned ? "Returned" : "Not returned", snapshot.DelegatedToCompatibilityBridge ? (snapshot.CompatibilityBridgeReturned ? "READY" : "CHECK") : "INFO", "Whether the compatibility bridge call returned to the OpenACAI bootstrap."),
                    Row("Execution Path", snapshot.CurrentExecutionPath, snapshot.Ready ? "READY" : "INFO", snapshot.NextPortBoundary),
                    Row("Updated", ShortTime(snapshot.UpdatedUtc), "INFO", string.IsNullOrWhiteSpace(snapshot.ExceptionType) ? "No chainloader exception recorded." : snapshot.ExceptionType)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateIl2CppPreloaderSection(OpenACAIChainloaderStateSnapshot snapshot, OpenACAIIL2CPPPreloaderStateSnapshot preloader)
        {
            var phaseStatus = preloader.FailedPhaseCount > 0
                ? "FAILED"
                : preloader.Ready
                    ? "READY"
                    : preloader.StateFileExists ? preloader.Status : "CHECK";
            var currentPhase = string.IsNullOrWhiteSpace(preloader.CurrentPhase) ? "Complete" : preloader.CurrentPhase;
            return new OpenACAIMenuSectionSnapshot(
                "IL2CPP Preloader",
                new[]
                {
                    Row("State", preloader.StateFileExists ? "Present" : "Missing", preloader.StateFileExists ? "READY" : "CHECK", preloader.StatePath),
                    Row("Status", preloader.Status, preloader.Status, preloader.Detail),
                    Row("Current Phase", currentPhase, phaseStatus, string.IsNullOrWhiteSpace(preloader.ExceptionType) ? "No IL2CPP preloader exception recorded." : preloader.ExceptionType),
                    Row("Phases", preloader.CompletedPhaseCount + "/" + preloader.PhaseCount + " ready", phaseStatus, preloader.FailedPhaseCount + " failed phase(s)."),
                    Row("BepInEx Preloader API", preloader.BepInExPreloaderRunApiDependencyUsed ? "Used" : "Not used", preloader.BepInExPreloaderRunApiDependencyUsed ? "CHECK" : "READY", "The OpenACAI native path should not call BepInEx.Unity.IL2CPP.Preloader.Run()."),
                    Row("Interop Components", preloader.BepInExIl2CppCompatibilityComponentsUsed || snapshot.BepInExIl2CppComponentReflectionUsed ? "Compatibility" : "OpenACAI", preloader.BepInExIl2CppCompatibilityComponentsUsed || snapshot.BepInExIl2CppComponentReflectionUsed ? "INFO" : "READY", "Remaining target: port reflected IL2CPP compatibility components into OpenACAI-owned code."),
                    Row("Chainloader Execution", preloader.BepInExIl2CppChainloaderInstanceUsed ? "Compatibility" : "OpenACAI", preloader.BepInExIl2CppChainloaderInstanceUsed ? "CHECK" : "READY", "Execution ownership; the owned path may expose a BepInEx registry without running upstream discovery or hooks."),
                    Row("Updated", ShortTime(preloader.UpdatedUtc), "INFO", preloader.Runner)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateIl2CppChainloaderSection(OpenACAIIL2CPPChainloaderAdapterStateSnapshot chainloader)
        {
            var phaseStatus = chainloader.FailedPhaseCount > 0
                ? "FAILED"
                : chainloader.Ready
                    ? "READY"
                    : chainloader.StateFileExists ? chainloader.Status : "CHECK";
            var currentPhase = string.IsNullOrWhiteSpace(chainloader.CurrentPhase) ? "Complete" : chainloader.CurrentPhase;
            var directTrigger = chainloader.OpenAcaiDirectCoreInvocationExecuted
                ? "Executed"
                : chainloader.OpenAcaiDirectCoreInvocationArmed
                    ? "Armed"
                    : chainloader.OpenAcaiDirectCoreInvocationPlanReady ? "Scaffolded" : chainloader.OpenAcaiDirectCoreInvocationPlanCreated ? "Partial" : "Waiting";
            return new OpenACAIMenuSectionSnapshot(
                "IL2CPP Chainloader",
                new[]
                {
                    Row("State", chainloader.StateFileExists ? "Present" : "Missing", chainloader.StateFileExists ? "READY" : "CHECK", chainloader.StatePath),
                    Row("Status", chainloader.Status, chainloader.Status, chainloader.Detail),
                    Row("Current Phase", currentPhase, phaseStatus, string.IsNullOrWhiteSpace(chainloader.ExceptionType) ? "No IL2CPP chainloader adapter exception recorded." : chainloader.ExceptionType),
                    Row("Phases", chainloader.CompletedPhaseCount + "/" + chainloader.PhaseCount + " ready", phaseStatus, chainloader.FailedPhaseCount + " failed phase(s)."),
                    Row("Adapter", chainloader.OpenAcaiIl2CppChainloaderAdapterOwned ? "OpenACAI" : "Compatibility", chainloader.OpenAcaiIl2CppChainloaderAdapterOwned ? "READY" : "CHECK", chainloader.Adapter),
                    Row("Port Readiness", chainloader.OpenAcaiIl2CppChainloaderPortPlanReady ? "Ready" : chainloader.OpenAcaiIl2CppChainloaderPortPlanCreated ? "Partial" : "Waiting", chainloader.OpenAcaiIl2CppChainloaderPortPlanReady ? "READY" : "CHECK", chainloader.ChainloaderPortPlanDetail),
                    Row("Runtime Invoke", chainloader.RuntimeInvokeExportResolvable ? "Resolvable" : "Waiting", chainloader.RuntimeInvokeExportResolvable ? "READY" : "CHECK", chainloader.GameAssemblyResolvable ? "GameAssembly is visible to the OpenACAI adapter." : "GameAssembly is not visible to the OpenACAI adapter yet."),
                    Row("Direct Core Entry", chainloader.OpenAcaiCoreEntrypointResolvable ? "Resolvable" : "Waiting", chainloader.OpenAcaiCoreEntrypointResolvable ? "READY" : "CHECK", chainloader.OpenAcaiCoreAssembly),
                    Row("Direct Trigger", directTrigger, chainloader.OpenAcaiDirectCoreInvocationPlanReady ? "READY" : "CHECK", chainloader.DirectCoreInvocationPlanDetail),
                    Row("Interop Preload", chainloader.InteropPreloadResolvable ? "Resolvable" : "Waiting", chainloader.InteropPreloadResolvable ? "READY" : "CHECK", "OpenACAI direct chainloader path will preload generated IL2CPP interop assemblies before core runtime load."),
                    Row("Execution Fallback", chainloader.CompatibilityExecutionFallbackUsed ? "Used" : chainloader.CompatibilityExecutionFallbackRequired ? "Compatibility" : "OpenACAI", chainloader.CompatibilityExecutionFallbackRequired || chainloader.CompatibilityExecutionFallbackUsed ? "CHECK" : "READY", chainloader.OpenAcaiDirectCoreInvocationArmed ? "Direct OpenACAI invocation is armed at " + chainloader.RuntimeInvokePointer + "." : "Direct OpenACAI invocation is not armed yet."),
                    Row("Execution Path", chainloader.BepInExIl2CppChainloaderInstanceUsed ? "BepInEx fallback" : "OpenACAI", chainloader.BepInExIl2CppChainloaderInstanceUsed ? "CHECK" : "READY", chainloader.CompatibilityChainloaderType),
                    Row("Preloader Binding", chainloader.PreloaderChainloaderPropertyBound ? "Bound" : "Missing", chainloader.PreloaderChainloaderPropertyBound ? "READY" : "CHECK", "Legacy preloader Chainloader property binding."),
                    Row("Initialize", chainloader.InitializeReturned ? "Returned" : chainloader.InitializeInvoked ? "Invoked" : "Waiting", chainloader.InitializeReturned ? "READY" : chainloader.Status, "Compatibility chainloader Initialize call through the OpenACAI adapter."),
                    Row("Updated", ShortTime(chainloader.UpdatedUtc), "INFO", chainloader.RuntimeInvokeHookStateFile)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateFilesSection(OpenACAIChainloaderStateSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Files",
                new[]
                {
                    Row("State File", snapshot.StateFileExists ? "Present" : "Missing", snapshot.StateFileExists ? "READY" : "CHECK", snapshot.StatePath),
                    Row("IL2CPP Preloader State", snapshot.Il2CppPreloaderStateFileExists ? "Present" : "Missing", snapshot.Il2CppPreloaderStateFileExists ? "READY" : "CHECK", snapshot.Il2CppPreloaderStateFile),
                    Row("IL2CPP Chainloader State", File.Exists(OpenACAIPaths.Il2CppChainloaderStatePath) ? "Present" : "Missing", File.Exists(OpenACAIPaths.Il2CppChainloaderStatePath) ? "READY" : "CHECK", OpenACAIPaths.Il2CppChainloaderStatePath),
                    Row("Bootstrap Assembly", snapshot.BootstrapAssemblyExists ? ShortPath(snapshot.BootstrapAssembly) : "Missing", snapshot.BootstrapAssemblyExists ? "READY" : "CHECK", snapshot.BootstrapAssembly),
                    Row("Runtime Core", snapshot.RuntimeCoreAssemblyExists ? ShortPath(snapshot.RuntimeCoreAssembly) : "Missing", snapshot.RuntimeCoreAssemblyExists ? "READY" : "CHECK", snapshot.RuntimeCoreAssembly),
                    Row("Compatibility Bridge", snapshot.CompatibilityBridgeAssemblyExists ? ShortPath(snapshot.CompatibilityBridgeAssembly) : "Missing", snapshot.CompatibilityBridgeAssemblyExists ? "INFO" : "CHECK", snapshot.CompatibilityBridgeAssembly),
                    Row("Runtime Directory", Directory.Exists(snapshot.RuntimeDirectory) ? "Present" : "Missing", Directory.Exists(snapshot.RuntimeDirectory) ? "READY" : "CHECK", snapshot.RuntimeDirectory),
                    Row("BepInEx Core Directory", Directory.Exists(snapshot.BepInExCoreDirectory) ? "Present" : "Missing", Directory.Exists(snapshot.BepInExCoreDirectory) ? "INFO" : "CHECK", snapshot.BepInExCoreDirectory)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreatePlanSection(OpenACAIChainloaderStateSnapshot snapshot)
        {
            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                Row("Next Boundary", snapshot.NextPortBoundary, snapshot.OpenAcaiIl2CppChainloaderPortComplete ? "READY" : "CHECK", "The next rewrite target tracked by the bootstrap journal.")
            };

            foreach (var stage in snapshot.PlannedStages.Take(10))
            {
                rows.Add(Row(stage, "planned", "INFO", "Recorded bootstrap stage."));
            }

            if (snapshot.PlannedStages.Count == 0)
            {
                rows.Add(Row("Stages", "None", "CHECK", "No planned chainloader stages were present in the bootstrap journal."));
            }

            return new OpenACAIMenuSectionSnapshot("Rewrite Plan", rows);
        }

        private static OpenACAIIL2CPPPreloaderStateSnapshot ReadIl2CppPreloaderState()
        {
            var statePath = OpenACAIPaths.Il2CppPreloaderStatePath;
            try
            {
                if (!File.Exists(statePath))
                {
                    return OpenACAIIL2CPPPreloaderStateSnapshot.Missing(statePath);
                }

                using (var document = JsonDocument.Parse(File.ReadAllText(statePath)))
                {
                    var root = document.RootElement;
                    return new OpenACAIIL2CPPPreloaderStateSnapshot(
                        statePath,
                        true,
                        GetInt(root, "schema", 1),
                        GetString(root, "serviceKey", Il2CppPreloaderServiceKey),
                        GetString(root, "enteredUtc", ""),
                        GetString(root, "updatedUtc", ""),
                        NormalizeStatus(GetString(root, "status", "CHECK")),
                        GetString(root, "detail", "OpenACAI IL2CPP preloader state was read."),
                        GetString(root, "exceptionType", ""),
                        GetString(root, "mode", ""),
                        GetString(root, "runner", ""),
                        GetString(root, "currentPhase", ""),
                        GetInt(root, "phaseCount", 0),
                        GetInt(root, "completedPhaseCount", 0),
                        GetInt(root, "failedPhaseCount", 0),
                        GetBool(root, "openAcaiPreloaderPhaseRunnerOwned", false),
                        GetBool(root, "bepInExPreloaderRunApiDependencyUsed", true),
                        GetBool(root, "bepInExIl2CppCompatibilityComponentsUsed", false),
                        GetBool(root, "bepInExIl2CppChainloaderInstanceUsed", false),
                        GetBool(root, "openAcaiPreloaderPortComplete", false),
                        GetBool(root, "openAcaiIl2CppChainloaderPortComplete", false),
                        GetString(root, "compatibilityBridgeAssembly", ""),
                        GetString(root, "runtimeDirectory", ""),
                        GetString(root, "compatibilityDirectory", ""),
                        GetString(root, "bepInExCoreDirectory", ""),
                        GetIl2CppPreloaderPhases(root));
                }
            }
            catch (Exception ex)
            {
                return OpenACAIIL2CPPPreloaderStateSnapshot.Failed(statePath, ex);
            }
        }

        private static IReadOnlyList<OpenACAIIL2CPPPreloaderPhaseSnapshot> GetIl2CppPreloaderPhases(JsonElement root)
        {
            JsonElement value;
            if (!root.TryGetProperty("phases", out value) || value.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<OpenACAIIL2CPPPreloaderPhaseSnapshot>();
            }

            var phases = new List<OpenACAIIL2CPPPreloaderPhaseSnapshot>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                phases.Add(new OpenACAIIL2CPPPreloaderPhaseSnapshot(
                    GetString(item, "name", ""),
                    NormalizeStatus(GetString(item, "status", "CHECK")),
                    GetString(item, "startedUtc", ""),
                    GetString(item, "completedUtc", ""),
                    GetString(item, "exceptionType", ""),
                    GetString(item, "detail", "")));
            }

            return phases;
        }

        private static OpenACAIIL2CPPChainloaderAdapterStateSnapshot ReadIl2CppChainloaderState()
        {
            var statePath = OpenACAIPaths.Il2CppChainloaderStatePath;
            try
            {
                if (!File.Exists(statePath))
                {
                    return OpenACAIIL2CPPChainloaderAdapterStateSnapshot.Missing(statePath);
                }

                using (var document = JsonDocument.Parse(File.ReadAllText(statePath)))
                {
                    var root = document.RootElement;
                    return new OpenACAIIL2CPPChainloaderAdapterStateSnapshot(
                        statePath,
                        true,
                        GetInt(root, "schema", 1),
                        GetString(root, "serviceKey", Il2CppChainloaderServiceKey),
                        GetString(root, "enteredUtc", ""),
                        GetString(root, "updatedUtc", ""),
                        NormalizeStatus(GetString(root, "status", "CHECK")),
                        GetString(root, "detail", "OpenACAI IL2CPP chainloader adapter state was read."),
                        GetString(root, "exceptionType", ""),
                        GetString(root, "mode", ""),
                        GetString(root, "adapter", ""),
                        GetString(root, "currentPhase", ""),
                        GetInt(root, "phaseCount", 0),
                        GetInt(root, "completedPhaseCount", 0),
                        GetInt(root, "failedPhaseCount", 0),
                        GetBool(root, "openAcaiIl2CppChainloaderAdapterOwned", false),
                        GetBool(root, "bepInExIl2CppChainloaderInstanceUsed", false),
                        GetBool(root, "openAcaiIl2CppChainloaderPortComplete", false),
                        GetBool(root, "adapterCreated", false),
                        GetBool(root, "openAcaiIl2CppChainloaderPortPlanCreated", false),
                        GetBool(root, "openAcaiIl2CppChainloaderPortPlanReady", false),
                        GetBool(root, "openAcaiDirectCoreInvocationPlanCreated", false),
                        GetBool(root, "openAcaiDirectCoreInvocationPlanReady", false),
                        GetBool(root, "openAcaiDirectCoreInvocationArmed", false),
                        GetBool(root, "openAcaiDirectCoreInvocationTriggerOwned", false),
                        GetBool(root, "openAcaiDirectCoreInvocationExecuted", false),
                        GetBool(root, "directCoreEntrypointSignatureResolvable", false),
                        GetBool(root, "directCoreLogSourceFactoryResolvable", false),
                        GetBool(root, "interopPreloadResolvable", false),
                        GetString(root, "runtimeInvokePointer", ""),
                        GetString(root, "directCoreTriggerMethod", ""),
                        GetString(root, "directCoreInvocationPlanDetail", ""),
                        GetBool(root, "gameAssemblyResolvable", false),
                        GetBool(root, "runtimeInvokeExportResolvable", false),
                        GetBool(root, "nativeDetourFactoryResolvable", false),
                        GetBool(root, "basePluginResolvable", false),
                        GetBool(root, "manualLogSourceResolvable", false),
                        GetBool(root, "openAcaiCoreAssemblyResolvable", false),
                        GetBool(root, "openAcaiCoreEntrypointResolvable", false),
                        GetBool(root, "compatibilityExecutionFallbackRequired", true),
                        GetBool(root, "compatibilityExecutionFallbackUsed", false),
                        GetString(root, "chainloaderPortPlanDetail", ""),
                        GetString(root, "openAcaiCoreAssembly", ""),
                        GetBool(root, "compatibilityInstanceCreated", false),
                        GetBool(root, "preloaderChainloaderPropertyBound", false),
                        GetBool(root, "initializeInvoked", false),
                        GetBool(root, "initializeReturned", false),
                        GetString(root, "compatibilityChainloaderType", ""),
                        GetString(root, "compatibilityBridgeAssembly", ""),
                        GetString(root, "runtimeInvokeHookStateFile", ""),
                        GetString(root, "runtimeDirectory", ""),
                        GetString(root, "compatibilityDirectory", ""),
                        GetString(root, "bepInExCoreDirectory", ""),
                        GetIl2CppChainloaderPhases(root));
                }
            }
            catch (Exception ex)
            {
                return OpenACAIIL2CPPChainloaderAdapterStateSnapshot.Failed(statePath, ex);
            }
        }

        private static IReadOnlyList<OpenACAIIL2CPPChainloaderAdapterPhaseSnapshot> GetIl2CppChainloaderPhases(JsonElement root)
        {
            JsonElement value;
            if (!root.TryGetProperty("phases", out value) || value.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<OpenACAIIL2CPPChainloaderAdapterPhaseSnapshot>();
            }

            var phases = new List<OpenACAIIL2CPPChainloaderAdapterPhaseSnapshot>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                phases.Add(new OpenACAIIL2CPPChainloaderAdapterPhaseSnapshot(
                    GetString(item, "name", ""),
                    NormalizeStatus(GetString(item, "status", "CHECK")),
                    GetString(item, "startedUtc", ""),
                    GetString(item, "completedUtc", ""),
                    GetString(item, "exceptionType", ""),
                    GetString(item, "detail", "")));
            }

            return phases;
        }

        private static OpenACAIChainloaderStateSnapshot ReadState()
        {
            var statePath = OpenACAIPaths.ChainloaderStatePath;
            try
            {
                if (!File.Exists(statePath))
                {
                    return OpenACAIChainloaderStateSnapshot.Missing(statePath);
                }

                using (var document = JsonDocument.Parse(File.ReadAllText(statePath)))
                {
                    var root = document.RootElement;
                    return new OpenACAIChainloaderStateSnapshot(
                        statePath,
                        true,
                        GetInt(root, "schema", 1),
                        GetString(root, "serviceKey", ServiceKey),
                        GetString(root, "enteredUtc", ""),
                        GetString(root, "updatedUtc", ""),
                        NormalizeStatus(GetString(root, "status", "CHECK")),
                        GetString(root, "detail", "Modstoke chainloader boundary state was read."),
                        GetString(root, "exceptionType", ""),
                        GetString(root, "owner", "OpenACAI"),
                        GetString(root, "mode", ""),
                        GetBool(root, "chainloaderReady", false),
                        GetBool(root, "doorstopTargetOwnedByOpenAcai", false),
                        GetBool(root, "nativeHostOwnedByOpenAcai", false),
                        GetBool(root, "managedBootstrapOwnedByOpenAcai", false),
                        GetBool(root, "openAcaiPreloaderOrchestrationOwned", false),
                        GetBool(root, "openAcaiIl2CppPreloaderPhaseRunnerOwned", false),
                        GetBool(root, "openAcaiPreloaderPortComplete", false),
                        GetBool(root, "openAcaiIl2CppChainloaderPortComplete", false),
                        GetBool(root, "bepInExPreloaderRunApiDependencyUsed", true),
                        GetBool(root, "bepInExIl2CppComponentReflectionUsed", false),
                        GetBool(root, "delegatedToCompatibilityBridge", false),
                        GetBool(root, "compatibilityBridgeReturned", false),
                        GetString(root, "compatibilityBridgeMode", ""),
                        GetString(root, "nextPortBoundary", ""),
                        GetString(root, "currentExecutionPath", ""),
                        GetString(root, "bootstrapAssembly", ""),
                        GetBool(root, "bootstrapAssemblyExists", false),
                        GetString(root, "runtimeCoreAssembly", ""),
                        GetBool(root, "runtimeCoreAssemblyExists", false),
                        GetString(root, "il2CppPreloaderStateFile", OpenACAIPaths.Il2CppPreloaderStatePath),
                        GetBool(root, "il2CppPreloaderStateFileExists", File.Exists(OpenACAIPaths.Il2CppPreloaderStatePath)),
                        GetString(root, "compatibilityBridgeAssembly", ""),
                        GetBool(root, "compatibilityBridgeAssemblyExists", false),
                        GetString(root, "runtimeDirectory", ""),
                        GetString(root, "compatibilityDirectory", ""),
                        GetString(root, "bepInExCoreDirectory", ""),
                        GetStringArray(root, "plannedStages"));
                }
            }
            catch (Exception ex)
            {
                return OpenACAIChainloaderStateSnapshot.Failed(statePath, ex);
            }
        }

        private static string NormalizeStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return "CHECK";
            }

            return status.Trim().ToUpperInvariant();
        }

        private static int GetInt(JsonElement root, string propertyName, int fallback)
        {
            JsonElement value;
            if (!root.TryGetProperty(propertyName, out value))
            {
                return fallback;
            }

            try
            {
                return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) ? result : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static bool GetBool(JsonElement root, string propertyName, bool fallback)
        {
            JsonElement value;
            if (!root.TryGetProperty(propertyName, out value))
            {
                return fallback;
            }

            return value.ValueKind == JsonValueKind.True
                ? true
                : value.ValueKind == JsonValueKind.False
                    ? false
                    : fallback;
        }

        private static string GetString(JsonElement root, string propertyName, string fallback)
        {
            JsonElement value;
            if (!root.TryGetProperty(propertyName, out value) || value.ValueKind == JsonValueKind.Null)
            {
                return fallback;
            }

            try
            {
                return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.ToString();
            }
            catch
            {
                return fallback;
            }
        }

        private static IReadOnlyList<string> GetStringArray(JsonElement root, string propertyName)
        {
            JsonElement value;
            if (!root.TryGetProperty(propertyName, out value) || value.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var results = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var text = item.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        results.Add(text);
                    }
                }
            }

            return results;
        }

        private static string ShortPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "Unavailable";
            }

            try
            {
                var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return Path.GetFileName(trimmed);
            }
            catch
            {
                return path;
            }
        }

        private static string ShortTime(string value)
        {
            DateTimeOffset parsed;
            return DateTimeOffset.TryParse(value, out parsed) ? parsed.ToLocalTime().ToString("u") : value;
        }

        private static OpenACAIMenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new OpenACAIMenuRowSnapshot(label, value, NormalizeStatus(status), detail);
        }

        private sealed class Provider : IOpenACAIMenuContentProvider
        {
            public string Id => OpenACAIMenuProviderIds.ChainloaderStatus;
            public string DisplayName => "Chainloader";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 690;
            public string Source => "Modstoke Loader";
            public string Description => "Show OpenACAI bootstrap, preloader, and chainloader ownership state.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAIChainloaderStatus.CreateContent();
            }
        }
    }

    public sealed class OpenACAIChainloaderStateSnapshot
    {
        internal OpenACAIChainloaderStateSnapshot(
            string statePath,
            bool stateFileExists,
            int schema,
            string serviceKey,
            string enteredUtc,
            string updatedUtc,
            string status,
            string detail,
            string exceptionType,
            string owner,
            string mode,
            bool chainloaderReady,
            bool doorstopTargetOwnedByOpenAcai,
            bool nativeHostOwnedByOpenAcai,
            bool managedBootstrapOwnedByOpenAcai,
            bool openAcaiPreloaderOrchestrationOwned,
            bool openAcaiIl2CppPreloaderPhaseRunnerOwned,
            bool openAcaiPreloaderPortComplete,
            bool openAcaiIl2CppChainloaderPortComplete,
            bool bepInExPreloaderRunApiDependencyUsed,
            bool bepInExIl2CppComponentReflectionUsed,
            bool delegatedToCompatibilityBridge,
            bool compatibilityBridgeReturned,
            string compatibilityBridgeMode,
            string nextPortBoundary,
            string currentExecutionPath,
            string bootstrapAssembly,
            bool bootstrapAssemblyExists,
            string runtimeCoreAssembly,
            bool runtimeCoreAssemblyExists,
            string il2CppPreloaderStateFile,
            bool il2CppPreloaderStateFileExists,
            string compatibilityBridgeAssembly,
            bool compatibilityBridgeAssemblyExists,
            string runtimeDirectory,
            string compatibilityDirectory,
            string bepInExCoreDirectory,
            IReadOnlyList<string> plannedStages)
        {
            StatePath = statePath ?? "";
            StateFileExists = stateFileExists;
            Schema = schema;
            ServiceKey = serviceKey ?? OpenACAIChainloaderStatus.ServiceKey;
            EnteredUtc = enteredUtc ?? "";
            UpdatedUtc = updatedUtc ?? "";
            Status = string.IsNullOrWhiteSpace(status) ? "CHECK" : status;
            Detail = detail ?? "";
            ExceptionType = exceptionType ?? "";
            Owner = string.IsNullOrWhiteSpace(owner) ? "OpenACAI" : owner;
            Mode = mode ?? "";
            ChainloaderReady = chainloaderReady;
            DoorstopTargetOwnedByOpenAcai = doorstopTargetOwnedByOpenAcai;
            NativeHostOwnedByOpenAcai = nativeHostOwnedByOpenAcai;
            ManagedBootstrapOwnedByOpenAcai = managedBootstrapOwnedByOpenAcai;
            OpenAcaiPreloaderOrchestrationOwned = openAcaiPreloaderOrchestrationOwned;
            OpenAcaiIl2CppPreloaderPhaseRunnerOwned = openAcaiIl2CppPreloaderPhaseRunnerOwned;
            OpenAcaiPreloaderPortComplete = openAcaiPreloaderPortComplete;
            OpenAcaiIl2CppChainloaderPortComplete = openAcaiIl2CppChainloaderPortComplete;
            BepInExPreloaderRunApiDependencyUsed = bepInExPreloaderRunApiDependencyUsed;
            BepInExIl2CppComponentReflectionUsed = bepInExIl2CppComponentReflectionUsed;
            DelegatedToCompatibilityBridge = delegatedToCompatibilityBridge;
            CompatibilityBridgeReturned = compatibilityBridgeReturned;
            CompatibilityBridgeMode = compatibilityBridgeMode ?? "";
            NextPortBoundary = nextPortBoundary ?? "";
            CurrentExecutionPath = currentExecutionPath ?? "";
            BootstrapAssembly = bootstrapAssembly ?? "";
            BootstrapAssemblyExists = bootstrapAssemblyExists;
            RuntimeCoreAssembly = runtimeCoreAssembly ?? "";
            RuntimeCoreAssemblyExists = runtimeCoreAssemblyExists;
            Il2CppPreloaderStateFile = string.IsNullOrWhiteSpace(il2CppPreloaderStateFile) ? OpenACAIPaths.Il2CppPreloaderStatePath : il2CppPreloaderStateFile;
            Il2CppPreloaderStateFileExists = il2CppPreloaderStateFileExists;
            CompatibilityBridgeAssembly = compatibilityBridgeAssembly ?? "";
            CompatibilityBridgeAssemblyExists = compatibilityBridgeAssemblyExists;
            RuntimeDirectory = runtimeDirectory ?? "";
            CompatibilityDirectory = compatibilityDirectory ?? "";
            BepInExCoreDirectory = bepInExCoreDirectory ?? "";
            PlannedStages = plannedStages ?? Array.Empty<string>();
        }

        public string StatePath { get; }
        public bool StateFileExists { get; }
        public int Schema { get; }
        public string ServiceKey { get; }
        public string EnteredUtc { get; }
        public string UpdatedUtc { get; }
        public string Status { get; }
        public bool Ready => ChainloaderReady && string.Equals(Status, "READY", StringComparison.OrdinalIgnoreCase);
        public string Detail { get; }
        public string ExceptionType { get; }
        public string Owner { get; }
        public string Mode { get; }
        public bool ChainloaderReady { get; }
        public bool DoorstopTargetOwnedByOpenAcai { get; }
        public bool NativeHostOwnedByOpenAcai { get; }
        public bool ManagedBootstrapOwnedByOpenAcai { get; }
        public bool OpenAcaiPreloaderOrchestrationOwned { get; }
        public bool OpenAcaiIl2CppPreloaderPhaseRunnerOwned { get; }
        public bool OpenAcaiPreloaderPortComplete { get; }
        public bool OpenAcaiIl2CppChainloaderPortComplete { get; }
        public bool BepInExPreloaderRunApiDependencyUsed { get; }
        public bool BepInExIl2CppComponentReflectionUsed { get; }
        public bool DelegatedToCompatibilityBridge { get; }
        public bool CompatibilityBridgeReturned { get; }
        public string CompatibilityBridgeMode { get; }
        public string NextPortBoundary { get; }
        public string CurrentExecutionPath { get; }
        public string BootstrapAssembly { get; }
        public bool BootstrapAssemblyExists { get; }
        public string RuntimeCoreAssembly { get; }
        public bool RuntimeCoreAssemblyExists { get; }
        public string Il2CppPreloaderStateFile { get; }
        public bool Il2CppPreloaderStateFileExists { get; }
        public string CompatibilityBridgeAssembly { get; }
        public bool CompatibilityBridgeAssemblyExists { get; }
        public string RuntimeDirectory { get; }
        public string CompatibilityDirectory { get; }
        public string BepInExCoreDirectory { get; }
        public IReadOnlyList<string> PlannedStages { get; }

        internal static OpenACAIChainloaderStateSnapshot Missing(string statePath)
        {
            return new OpenACAIChainloaderStateSnapshot(
                statePath,
                false,
                1,
                OpenACAIChainloaderStatus.ServiceKey,
                "",
                DateTimeOffset.UtcNow.ToString("O"),
                "CHECK",
                "Modstoke chainloader state has not been written yet.",
                "",
                "OpenACAI",
                "",
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                "",
                "openacai-owned-preloader-and-il2cpp-chainloader",
                "",
                "",
                false,
                "",
                false,
                OpenACAIPaths.Il2CppPreloaderStatePath,
                File.Exists(OpenACAIPaths.Il2CppPreloaderStatePath),
                "",
                false,
                "",
                "",
                "",
                Array.Empty<string>());
        }

        internal static OpenACAIChainloaderStateSnapshot Failed(string statePath, Exception exception)
        {
            return new OpenACAIChainloaderStateSnapshot(
                statePath,
                File.Exists(statePath),
                1,
                OpenACAIChainloaderStatus.ServiceKey,
                "",
                DateTimeOffset.UtcNow.ToString("O"),
                "FAILED",
                "Modstoke chainloader state could not be read: " + (exception == null ? "unknown error" : exception.Message),
                exception == null ? "" : exception.GetType().FullName,
                "OpenACAI",
                "",
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                "",
                "openacai-owned-preloader-and-il2cpp-chainloader",
                "",
                "",
                false,
                "",
                false,
                OpenACAIPaths.Il2CppPreloaderStatePath,
                File.Exists(OpenACAIPaths.Il2CppPreloaderStatePath),
                "",
                false,
                "",
                "",
                "",
                Array.Empty<string>());
        }
    }

    public sealed class OpenACAIIL2CPPPreloaderStateSnapshot
    {
        internal OpenACAIIL2CPPPreloaderStateSnapshot(
            string statePath,
            bool stateFileExists,
            int schema,
            string serviceKey,
            string enteredUtc,
            string updatedUtc,
            string status,
            string detail,
            string exceptionType,
            string mode,
            string runner,
            string currentPhase,
            int phaseCount,
            int completedPhaseCount,
            int failedPhaseCount,
            bool openAcaiPreloaderPhaseRunnerOwned,
            bool bepInExPreloaderRunApiDependencyUsed,
            bool bepInExIl2CppCompatibilityComponentsUsed,
            bool bepInExIl2CppChainloaderInstanceUsed,
            bool openAcaiPreloaderPortComplete,
            bool openAcaiIl2CppChainloaderPortComplete,
            string compatibilityBridgeAssembly,
            string runtimeDirectory,
            string compatibilityDirectory,
            string bepInExCoreDirectory,
            IReadOnlyList<OpenACAIIL2CPPPreloaderPhaseSnapshot> phases)
        {
            StatePath = statePath ?? "";
            StateFileExists = stateFileExists;
            Schema = schema;
            ServiceKey = serviceKey ?? OpenACAIChainloaderStatus.Il2CppPreloaderServiceKey;
            EnteredUtc = enteredUtc ?? "";
            UpdatedUtc = updatedUtc ?? "";
            Status = string.IsNullOrWhiteSpace(status) ? "CHECK" : status;
            Detail = detail ?? "";
            ExceptionType = exceptionType ?? "";
            Mode = mode ?? "";
            Runner = runner ?? "";
            CurrentPhase = currentPhase ?? "";
            PhaseCount = phaseCount;
            CompletedPhaseCount = completedPhaseCount;
            FailedPhaseCount = failedPhaseCount;
            OpenAcaiPreloaderPhaseRunnerOwned = openAcaiPreloaderPhaseRunnerOwned;
            BepInExPreloaderRunApiDependencyUsed = bepInExPreloaderRunApiDependencyUsed;
            BepInExIl2CppCompatibilityComponentsUsed = bepInExIl2CppCompatibilityComponentsUsed;
            BepInExIl2CppChainloaderInstanceUsed = bepInExIl2CppChainloaderInstanceUsed;
            OpenAcaiPreloaderPortComplete = openAcaiPreloaderPortComplete;
            OpenAcaiIl2CppChainloaderPortComplete = openAcaiIl2CppChainloaderPortComplete;
            CompatibilityBridgeAssembly = compatibilityBridgeAssembly ?? "";
            RuntimeDirectory = runtimeDirectory ?? "";
            CompatibilityDirectory = compatibilityDirectory ?? "";
            BepInExCoreDirectory = bepInExCoreDirectory ?? "";
            Phases = phases ?? Array.Empty<OpenACAIIL2CPPPreloaderPhaseSnapshot>();
        }

        public string StatePath { get; }
        public bool StateFileExists { get; }
        public int Schema { get; }
        public string ServiceKey { get; }
        public string EnteredUtc { get; }
        public string UpdatedUtc { get; }
        public string Status { get; }
        public bool Ready => StateFileExists
            && string.Equals(Status, "READY", StringComparison.OrdinalIgnoreCase)
            && FailedPhaseCount == 0;
        public string Detail { get; }
        public string ExceptionType { get; }
        public string Mode { get; }
        public string Runner { get; }
        public string CurrentPhase { get; }
        public int PhaseCount { get; }
        public int CompletedPhaseCount { get; }
        public int FailedPhaseCount { get; }
        public bool OpenAcaiPreloaderPhaseRunnerOwned { get; }
        public bool BepInExPreloaderRunApiDependencyUsed { get; }
        public bool BepInExIl2CppCompatibilityComponentsUsed { get; }
        public bool BepInExIl2CppChainloaderInstanceUsed { get; }
        public bool OpenAcaiPreloaderPortComplete { get; }
        public bool OpenAcaiIl2CppChainloaderPortComplete { get; }
        public string CompatibilityBridgeAssembly { get; }
        public string RuntimeDirectory { get; }
        public string CompatibilityDirectory { get; }
        public string BepInExCoreDirectory { get; }
        public IReadOnlyList<OpenACAIIL2CPPPreloaderPhaseSnapshot> Phases { get; }

        internal static OpenACAIIL2CPPPreloaderStateSnapshot Missing(string statePath)
        {
            return new OpenACAIIL2CPPPreloaderStateSnapshot(
                statePath,
                false,
                1,
                OpenACAIChainloaderStatus.Il2CppPreloaderServiceKey,
                "",
                DateTimeOffset.UtcNow.ToString("O"),
                "CHECK",
                "OpenACAI IL2CPP preloader state has not been written yet.",
                "",
                "",
                "",
                "",
                0,
                0,
                0,
                false,
                false,
                false,
                false,
                false,
                false,
                "",
                "",
                "",
                "",
                Array.Empty<OpenACAIIL2CPPPreloaderPhaseSnapshot>());
        }

        internal static OpenACAIIL2CPPPreloaderStateSnapshot Failed(string statePath, Exception exception)
        {
            return new OpenACAIIL2CPPPreloaderStateSnapshot(
                statePath,
                File.Exists(statePath),
                1,
                OpenACAIChainloaderStatus.Il2CppPreloaderServiceKey,
                "",
                DateTimeOffset.UtcNow.ToString("O"),
                "FAILED",
                "OpenACAI IL2CPP preloader state could not be read: " + (exception == null ? "unknown error" : exception.Message),
                exception == null ? "" : exception.GetType().FullName,
                "",
                "",
                "",
                0,
                0,
                0,
                false,
                false,
                false,
                false,
                false,
                false,
                "",
                "",
                "",
                "",
                Array.Empty<OpenACAIIL2CPPPreloaderPhaseSnapshot>());
        }
    }

    public sealed class OpenACAIIL2CPPPreloaderPhaseSnapshot
    {
        internal OpenACAIIL2CPPPreloaderPhaseSnapshot(
            string name,
            string status,
            string startedUtc,
            string completedUtc,
            string exceptionType,
            string detail)
        {
            Name = name ?? "";
            Status = string.IsNullOrWhiteSpace(status) ? "CHECK" : status;
            StartedUtc = startedUtc ?? "";
            CompletedUtc = completedUtc ?? "";
            ExceptionType = exceptionType ?? "";
            Detail = detail ?? "";
        }

        public string Name { get; }
        public string Status { get; }
        public string StartedUtc { get; }
        public string CompletedUtc { get; }
        public string ExceptionType { get; }
        public string Detail { get; }
    }

    public sealed class OpenACAIIL2CPPChainloaderAdapterStateSnapshot
    {
        internal OpenACAIIL2CPPChainloaderAdapterStateSnapshot(
            string statePath,
            bool stateFileExists,
            int schema,
            string serviceKey,
            string enteredUtc,
            string updatedUtc,
            string status,
            string detail,
            string exceptionType,
            string mode,
            string adapter,
            string currentPhase,
            int phaseCount,
            int completedPhaseCount,
            int failedPhaseCount,
            bool openAcaiIl2CppChainloaderAdapterOwned,
            bool bepInExIl2CppChainloaderInstanceUsed,
            bool openAcaiIl2CppChainloaderPortComplete,
            bool adapterCreated,
            bool openAcaiIl2CppChainloaderPortPlanCreated,
            bool openAcaiIl2CppChainloaderPortPlanReady,
            bool openAcaiDirectCoreInvocationPlanCreated,
            bool openAcaiDirectCoreInvocationPlanReady,
            bool openAcaiDirectCoreInvocationArmed,
            bool openAcaiDirectCoreInvocationTriggerOwned,
            bool openAcaiDirectCoreInvocationExecuted,
            bool directCoreEntrypointSignatureResolvable,
            bool directCoreLogSourceFactoryResolvable,
            bool interopPreloadResolvable,
            string runtimeInvokePointer,
            string directCoreTriggerMethod,
            string directCoreInvocationPlanDetail,
            bool gameAssemblyResolvable,
            bool runtimeInvokeExportResolvable,
            bool nativeDetourFactoryResolvable,
            bool basePluginResolvable,
            bool manualLogSourceResolvable,
            bool openAcaiCoreAssemblyResolvable,
            bool openAcaiCoreEntrypointResolvable,
            bool compatibilityExecutionFallbackRequired,
            bool compatibilityExecutionFallbackUsed,
            string chainloaderPortPlanDetail,
            string openAcaiCoreAssembly,
            bool compatibilityInstanceCreated,
            bool preloaderChainloaderPropertyBound,
            bool initializeInvoked,
            bool initializeReturned,
            string compatibilityChainloaderType,
            string compatibilityBridgeAssembly,
            string runtimeInvokeHookStateFile,
            string runtimeDirectory,
            string compatibilityDirectory,
            string bepInExCoreDirectory,
            IReadOnlyList<OpenACAIIL2CPPChainloaderAdapterPhaseSnapshot> phases)
        {
            StatePath = statePath ?? "";
            StateFileExists = stateFileExists;
            Schema = schema;
            ServiceKey = serviceKey ?? OpenACAIChainloaderStatus.Il2CppChainloaderServiceKey;
            EnteredUtc = enteredUtc ?? "";
            UpdatedUtc = updatedUtc ?? "";
            Status = string.IsNullOrWhiteSpace(status) ? "CHECK" : status;
            Detail = detail ?? "";
            ExceptionType = exceptionType ?? "";
            Mode = mode ?? "";
            Adapter = adapter ?? "";
            CurrentPhase = currentPhase ?? "";
            PhaseCount = phaseCount;
            CompletedPhaseCount = completedPhaseCount;
            FailedPhaseCount = failedPhaseCount;
            OpenAcaiIl2CppChainloaderAdapterOwned = openAcaiIl2CppChainloaderAdapterOwned;
            BepInExIl2CppChainloaderInstanceUsed = bepInExIl2CppChainloaderInstanceUsed;
            OpenAcaiIl2CppChainloaderPortComplete = openAcaiIl2CppChainloaderPortComplete;
            AdapterCreated = adapterCreated;
            OpenAcaiIl2CppChainloaderPortPlanCreated = openAcaiIl2CppChainloaderPortPlanCreated;
            OpenAcaiIl2CppChainloaderPortPlanReady = openAcaiIl2CppChainloaderPortPlanReady;
            OpenAcaiDirectCoreInvocationPlanCreated = openAcaiDirectCoreInvocationPlanCreated;
            OpenAcaiDirectCoreInvocationPlanReady = openAcaiDirectCoreInvocationPlanReady;
            OpenAcaiDirectCoreInvocationArmed = openAcaiDirectCoreInvocationArmed;
            OpenAcaiDirectCoreInvocationTriggerOwned = openAcaiDirectCoreInvocationTriggerOwned;
            OpenAcaiDirectCoreInvocationExecuted = openAcaiDirectCoreInvocationExecuted;
            DirectCoreEntrypointSignatureResolvable = directCoreEntrypointSignatureResolvable;
            DirectCoreLogSourceFactoryResolvable = directCoreLogSourceFactoryResolvable;
            InteropPreloadResolvable = interopPreloadResolvable;
            RuntimeInvokePointer = runtimeInvokePointer ?? "";
            DirectCoreTriggerMethod = directCoreTriggerMethod ?? "";
            DirectCoreInvocationPlanDetail = directCoreInvocationPlanDetail ?? "";
            GameAssemblyResolvable = gameAssemblyResolvable;
            RuntimeInvokeExportResolvable = runtimeInvokeExportResolvable;
            NativeDetourFactoryResolvable = nativeDetourFactoryResolvable;
            BasePluginResolvable = basePluginResolvable;
            ManualLogSourceResolvable = manualLogSourceResolvable;
            OpenAcaiCoreAssemblyResolvable = openAcaiCoreAssemblyResolvable;
            OpenAcaiCoreEntrypointResolvable = openAcaiCoreEntrypointResolvable;
            CompatibilityExecutionFallbackRequired = compatibilityExecutionFallbackRequired;
            CompatibilityExecutionFallbackUsed = compatibilityExecutionFallbackUsed;
            ChainloaderPortPlanDetail = chainloaderPortPlanDetail ?? "";
            OpenAcaiCoreAssembly = openAcaiCoreAssembly ?? "";
            CompatibilityInstanceCreated = compatibilityInstanceCreated;
            PreloaderChainloaderPropertyBound = preloaderChainloaderPropertyBound;
            InitializeInvoked = initializeInvoked;
            InitializeReturned = initializeReturned;
            CompatibilityChainloaderType = compatibilityChainloaderType ?? "";
            CompatibilityBridgeAssembly = compatibilityBridgeAssembly ?? "";
            RuntimeInvokeHookStateFile = runtimeInvokeHookStateFile ?? "";
            RuntimeDirectory = runtimeDirectory ?? "";
            CompatibilityDirectory = compatibilityDirectory ?? "";
            BepInExCoreDirectory = bepInExCoreDirectory ?? "";
            Phases = phases ?? Array.Empty<OpenACAIIL2CPPChainloaderAdapterPhaseSnapshot>();
        }

        public string StatePath { get; }
        public bool StateFileExists { get; }
        public int Schema { get; }
        public string ServiceKey { get; }
        public string EnteredUtc { get; }
        public string UpdatedUtc { get; }
        public string Status { get; }
        public bool Ready => StateFileExists
            && string.Equals(Status, "READY", StringComparison.OrdinalIgnoreCase)
            && FailedPhaseCount == 0;
        public string Detail { get; }
        public string ExceptionType { get; }
        public string Mode { get; }
        public string Adapter { get; }
        public string CurrentPhase { get; }
        public int PhaseCount { get; }
        public int CompletedPhaseCount { get; }
        public int FailedPhaseCount { get; }
        public bool OpenAcaiIl2CppChainloaderAdapterOwned { get; }
        public bool BepInExIl2CppChainloaderInstanceUsed { get; }
        public bool OpenAcaiIl2CppChainloaderPortComplete { get; }
        public bool AdapterCreated { get; }
        public bool OpenAcaiIl2CppChainloaderPortPlanCreated { get; }
        public bool OpenAcaiIl2CppChainloaderPortPlanReady { get; }
        public bool OpenAcaiDirectCoreInvocationPlanCreated { get; }
        public bool OpenAcaiDirectCoreInvocationPlanReady { get; }
        public bool OpenAcaiDirectCoreInvocationArmed { get; }
        public bool OpenAcaiDirectCoreInvocationTriggerOwned { get; }
        public bool OpenAcaiDirectCoreInvocationExecuted { get; }
        public bool DirectCoreEntrypointSignatureResolvable { get; }
        public bool DirectCoreLogSourceFactoryResolvable { get; }
        public bool InteropPreloadResolvable { get; }
        public string RuntimeInvokePointer { get; }
        public string DirectCoreTriggerMethod { get; }
        public string DirectCoreInvocationPlanDetail { get; }
        public bool GameAssemblyResolvable { get; }
        public bool RuntimeInvokeExportResolvable { get; }
        public bool NativeDetourFactoryResolvable { get; }
        public bool BasePluginResolvable { get; }
        public bool ManualLogSourceResolvable { get; }
        public bool OpenAcaiCoreAssemblyResolvable { get; }
        public bool OpenAcaiCoreEntrypointResolvable { get; }
        public bool CompatibilityExecutionFallbackRequired { get; }
        public bool CompatibilityExecutionFallbackUsed { get; }
        public string ChainloaderPortPlanDetail { get; }
        public string OpenAcaiCoreAssembly { get; }
        public bool CompatibilityInstanceCreated { get; }
        public bool PreloaderChainloaderPropertyBound { get; }
        public bool InitializeInvoked { get; }
        public bool InitializeReturned { get; }
        public string CompatibilityChainloaderType { get; }
        public string CompatibilityBridgeAssembly { get; }
        public string RuntimeInvokeHookStateFile { get; }
        public string RuntimeDirectory { get; }
        public string CompatibilityDirectory { get; }
        public string BepInExCoreDirectory { get; }
        public IReadOnlyList<OpenACAIIL2CPPChainloaderAdapterPhaseSnapshot> Phases { get; }

        internal static OpenACAIIL2CPPChainloaderAdapterStateSnapshot Missing(string statePath)
        {
            return new OpenACAIIL2CPPChainloaderAdapterStateSnapshot(
                statePath: statePath,
                stateFileExists: false,
                schema: 1,
                serviceKey: OpenACAIChainloaderStatus.Il2CppChainloaderServiceKey,
                enteredUtc: "",
                updatedUtc: DateTimeOffset.UtcNow.ToString("O"),
                status: "CHECK",
                detail: "OpenACAI IL2CPP chainloader adapter state has not been written yet.",
                exceptionType: "",
                mode: "",
                adapter: "",
                currentPhase: "",
                phaseCount: 0,
                completedPhaseCount: 0,
                failedPhaseCount: 0,
                openAcaiIl2CppChainloaderAdapterOwned: false,
                bepInExIl2CppChainloaderInstanceUsed: false,
                openAcaiIl2CppChainloaderPortComplete: false,
                adapterCreated: false,
                openAcaiIl2CppChainloaderPortPlanCreated: false,
                openAcaiIl2CppChainloaderPortPlanReady: false,
                openAcaiDirectCoreInvocationPlanCreated: false,
                openAcaiDirectCoreInvocationPlanReady: false,
                openAcaiDirectCoreInvocationArmed: false,
                openAcaiDirectCoreInvocationTriggerOwned: false,
                openAcaiDirectCoreInvocationExecuted: false,
                directCoreEntrypointSignatureResolvable: false,
                directCoreLogSourceFactoryResolvable: false,
                interopPreloadResolvable: false,
                runtimeInvokePointer: "",
                directCoreTriggerMethod: "Internal_ActiveSceneChanged",
                directCoreInvocationPlanDetail: "",
                gameAssemblyResolvable: false,
                runtimeInvokeExportResolvable: false,
                nativeDetourFactoryResolvable: false,
                basePluginResolvable: false,
                manualLogSourceResolvable: false,
                openAcaiCoreAssemblyResolvable: false,
                openAcaiCoreEntrypointResolvable: false,
                compatibilityExecutionFallbackRequired: true,
                compatibilityExecutionFallbackUsed: false,
                chainloaderPortPlanDetail: "",
                openAcaiCoreAssembly: "",
                compatibilityInstanceCreated: false,
                preloaderChainloaderPropertyBound: false,
                initializeInvoked: false,
                initializeReturned: false,
                compatibilityChainloaderType: "",
                compatibilityBridgeAssembly: "",
                runtimeInvokeHookStateFile: "",
                runtimeDirectory: "",
                compatibilityDirectory: "",
                bepInExCoreDirectory: "",
                phases: Array.Empty<OpenACAIIL2CPPChainloaderAdapterPhaseSnapshot>());
        }

        internal static OpenACAIIL2CPPChainloaderAdapterStateSnapshot Failed(string statePath, Exception exception)
        {
            return new OpenACAIIL2CPPChainloaderAdapterStateSnapshot(
                statePath: statePath,
                stateFileExists: File.Exists(statePath),
                schema: 1,
                serviceKey: OpenACAIChainloaderStatus.Il2CppChainloaderServiceKey,
                enteredUtc: "",
                updatedUtc: DateTimeOffset.UtcNow.ToString("O"),
                status: "FAILED",
                detail: "OpenACAI IL2CPP chainloader adapter state could not be read: " + (exception == null ? "unknown error" : exception.Message),
                exceptionType: exception == null ? "" : exception.GetType().FullName,
                mode: "",
                adapter: "",
                currentPhase: "",
                phaseCount: 0,
                completedPhaseCount: 0,
                failedPhaseCount: 0,
                openAcaiIl2CppChainloaderAdapterOwned: false,
                bepInExIl2CppChainloaderInstanceUsed: false,
                openAcaiIl2CppChainloaderPortComplete: false,
                adapterCreated: false,
                openAcaiIl2CppChainloaderPortPlanCreated: false,
                openAcaiIl2CppChainloaderPortPlanReady: false,
                openAcaiDirectCoreInvocationPlanCreated: false,
                openAcaiDirectCoreInvocationPlanReady: false,
                openAcaiDirectCoreInvocationArmed: false,
                openAcaiDirectCoreInvocationTriggerOwned: false,
                openAcaiDirectCoreInvocationExecuted: false,
                directCoreEntrypointSignatureResolvable: false,
                directCoreLogSourceFactoryResolvable: false,
                interopPreloadResolvable: false,
                runtimeInvokePointer: "",
                directCoreTriggerMethod: "Internal_ActiveSceneChanged",
                directCoreInvocationPlanDetail: "",
                gameAssemblyResolvable: false,
                runtimeInvokeExportResolvable: false,
                nativeDetourFactoryResolvable: false,
                basePluginResolvable: false,
                manualLogSourceResolvable: false,
                openAcaiCoreAssemblyResolvable: false,
                openAcaiCoreEntrypointResolvable: false,
                compatibilityExecutionFallbackRequired: true,
                compatibilityExecutionFallbackUsed: false,
                chainloaderPortPlanDetail: "",
                openAcaiCoreAssembly: "",
                compatibilityInstanceCreated: false,
                preloaderChainloaderPropertyBound: false,
                initializeInvoked: false,
                initializeReturned: false,
                compatibilityChainloaderType: "",
                compatibilityBridgeAssembly: "",
                runtimeInvokeHookStateFile: "",
                runtimeDirectory: "",
                compatibilityDirectory: "",
                bepInExCoreDirectory: "",
                phases: Array.Empty<OpenACAIIL2CPPChainloaderAdapterPhaseSnapshot>());
        }
    }

    public sealed class OpenACAIIL2CPPChainloaderAdapterPhaseSnapshot
    {
        internal OpenACAIIL2CPPChainloaderAdapterPhaseSnapshot(
            string name,
            string status,
            string startedUtc,
            string completedUtc,
            string exceptionType,
            string detail)
        {
            Name = name ?? "";
            Status = string.IsNullOrWhiteSpace(status) ? "CHECK" : status;
            StartedUtc = startedUtc ?? "";
            CompletedUtc = completedUtc ?? "";
            ExceptionType = exceptionType ?? "";
            Detail = detail ?? "";
        }

        public string Name { get; }
        public string Status { get; }
        public string StartedUtc { get; }
        public string CompletedUtc { get; }
        public string ExceptionType { get; }
        public string Detail { get; }
    }
}
