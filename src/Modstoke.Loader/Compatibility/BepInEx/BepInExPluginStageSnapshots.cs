// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;

namespace Modstoke.Loader
{
    [Obsolete(BepInExPluginCompatibility.RetiredStageMessage)]
    public sealed class BepInExPluginAssemblyLoadResolverDependencyPlanSnapshot
    {
        internal BepInExPluginAssemblyLoadResolverDependencyPlanSnapshot(
            int sequenceNumber,
            string assemblyName,
            string requiredVersion,
            string requestingDirectory,
            bool resolved,
            string resolvedSource,
            string resolvedVersion,
            string resolvedPath,
            string status,
            string reasonCode,
            string detail)
        {
            SequenceNumber = Math.Max(0, sequenceNumber);
            AssemblyName = assemblyName ?? "";
            RequiredVersion = requiredVersion ?? "";
            RequestingDirectory = requestingDirectory ?? "";
            Resolved = resolved;
            ResolvedSource = resolvedSource ?? "";
            ResolvedVersion = resolvedVersion ?? "";
            ResolvedPath = resolvedPath ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public int SequenceNumber { get; }
        public string AssemblyName { get; }
        public string RequiredVersion { get; }
        public string RequestingDirectory { get; }
        public bool Resolved { get; }
        public string ResolvedSource { get; }
        public string ResolvedVersion { get; }
        public string ResolvedPath { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => !Resolved || string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    [Obsolete(BepInExPluginCompatibility.RetiredStageMessage)]
    public sealed class BepInExPluginLoadInvocationSnapshot
    {
        internal BepInExPluginLoadInvocationSnapshot(
            bool available,
            bool loadInvocationRequested,
            bool loadInvocationAccepted,
            bool pluginLoadInvocationEnabled,
            bool pluginInstanceCreationEnabled,
            bool pluginTypeResolutionEnabled,
            bool assemblyLoadInvocationEnabled,
            bool assemblyLoadContextRegistrationEnabled,
            bool assemblyLoadContextAllocationEnabled,
            bool assemblyLoadContextPlanEnabled,
            bool assemblyLoadOperationEnabled,
            bool assemblyLoadResolverPlanEnabled,
            bool runtimePluginLoadingEnabled,
            bool executorEnabled,
            bool assemblyLoadTransitionRequestEnabled,
            bool gateOpen,
            bool gateBlocked,
            bool pluginInstanceRequired,
            bool pluginInstanceAvailable,
            bool instanceResultAvailable,
            bool pluginInstanceCreated,
            bool instanceCreationCompleted,
            bool loadInvocationPlanned,
            bool loadInvocationStarted,
            bool loadInvocationCompleted,
            bool loadMethodInvoked,
            bool pluginLoadInvoked,
            bool failureIsolated,
            bool rollbackRequired,
            string loadInvocationStrategy,
            string methodName,
            string pluginTypeName,
            string resolvedTypeName,
            string instanceTypeName,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string loadedAssemblyName,
            string loadedAssemblyPath,
            string isolationBoundary,
            string status,
            string reasonCode,
            string detail)
        {
            Available = available;
            LoadInvocationRequested = loadInvocationRequested;
            LoadInvocationAccepted = loadInvocationAccepted;
            PluginLoadInvocationEnabled = pluginLoadInvocationEnabled;
            PluginInstanceCreationEnabled = pluginInstanceCreationEnabled;
            PluginTypeResolutionEnabled = pluginTypeResolutionEnabled;
            AssemblyLoadInvocationEnabled = assemblyLoadInvocationEnabled;
            AssemblyLoadContextRegistrationEnabled = assemblyLoadContextRegistrationEnabled;
            AssemblyLoadContextAllocationEnabled = assemblyLoadContextAllocationEnabled;
            AssemblyLoadContextPlanEnabled = assemblyLoadContextPlanEnabled;
            AssemblyLoadOperationEnabled = assemblyLoadOperationEnabled;
            AssemblyLoadResolverPlanEnabled = assemblyLoadResolverPlanEnabled;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            ExecutorEnabled = executorEnabled;
            AssemblyLoadTransitionRequestEnabled = assemblyLoadTransitionRequestEnabled;
            GateOpen = gateOpen;
            GateBlocked = gateBlocked;
            PluginInstanceRequired = pluginInstanceRequired;
            PluginInstanceAvailable = pluginInstanceAvailable;
            InstanceResultAvailable = instanceResultAvailable;
            PluginInstanceCreated = pluginInstanceCreated;
            InstanceCreationCompleted = instanceCreationCompleted;
            LoadInvocationPlanned = loadInvocationPlanned;
            LoadInvocationStarted = loadInvocationStarted;
            LoadInvocationCompleted = loadInvocationCompleted;
            LoadMethodInvoked = loadMethodInvoked;
            PluginLoadInvoked = pluginLoadInvoked;
            FailureIsolated = failureIsolated;
            RollbackRequired = rollbackRequired;
            LoadInvocationStrategy = loadInvocationStrategy ?? "";
            MethodName = methodName ?? "";
            PluginTypeName = pluginTypeName ?? "";
            ResolvedTypeName = resolvedTypeName ?? "";
            InstanceTypeName = instanceTypeName ?? "";
            PluginId = pluginId ?? "";
            AssemblyName = assemblyName ?? "";
            AssemblyPath = assemblyPath ?? "";
            LoadedAssemblyName = loadedAssemblyName ?? "";
            LoadedAssemblyPath = loadedAssemblyPath ?? "";
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public bool Available { get; }
        public bool LoadInvocationRequested { get; }
        public bool LoadInvocationAccepted { get; }
        public bool PluginLoadInvocationEnabled { get; }
        public bool PluginInstanceCreationEnabled { get; }
        public bool PluginTypeResolutionEnabled { get; }
        public bool AssemblyLoadInvocationEnabled { get; }
        public bool AssemblyLoadContextRegistrationEnabled { get; }
        public bool AssemblyLoadContextAllocationEnabled { get; }
        public bool AssemblyLoadContextPlanEnabled { get; }
        public bool AssemblyLoadOperationEnabled { get; }
        public bool AssemblyLoadResolverPlanEnabled { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool ExecutorEnabled { get; }
        public bool AssemblyLoadTransitionRequestEnabled { get; }
        public bool GateOpen { get; }
        public bool GateBlocked { get; }
        public bool PluginInstanceRequired { get; }
        public bool PluginInstanceAvailable { get; }
        public bool InstanceResultAvailable { get; }
        public bool PluginInstanceCreated { get; }
        public bool InstanceCreationCompleted { get; }
        public bool LoadInvocationPlanned { get; }
        public bool LoadInvocationStarted { get; }
        public bool LoadInvocationCompleted { get; }
        public bool LoadMethodInvoked { get; }
        public bool PluginLoadInvoked { get; }
        public bool FailureIsolated { get; }
        public bool RollbackRequired { get; }
        public string LoadInvocationStrategy { get; }
        public string MethodName { get; }
        public string PluginTypeName { get; }
        public string ResolvedTypeName { get; }
        public string InstanceTypeName { get; }
        public string PluginId { get; }
        public string AssemblyName { get; }
        public string AssemblyPath { get; }
        public string LoadedAssemblyName { get; }
        public string LoadedAssemblyPath { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static BepInExPluginLoadInvocationSnapshot CreateUnavailable(
            string reasonCode,
            string detail)
        {
            return new BepInExPluginLoadInvocationSnapshot(
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
                false,
                false,
                false,
                false,
                true,
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
                true,
                false,
                "none",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "openacai.metadata-gate",
                "INFO",
                reasonCode,
                detail);
        }
    }

    [Obsolete(BepInExPluginCompatibility.RetiredStageMessage)]
    public sealed class BepInExPluginInstanceCreationSnapshot
    {
        internal BepInExPluginInstanceCreationSnapshot(
            bool available,
            bool instanceCreationRequested,
            bool instanceCreationAccepted,
            bool pluginInstanceCreationEnabled,
            bool pluginTypeResolutionEnabled,
            bool assemblyLoadInvocationEnabled,
            bool assemblyLoadContextRegistrationEnabled,
            bool assemblyLoadContextAllocationEnabled,
            bool assemblyLoadContextPlanEnabled,
            bool assemblyLoadOperationEnabled,
            bool assemblyLoadResolverPlanEnabled,
            bool runtimePluginLoadingEnabled,
            bool executorEnabled,
            bool assemblyLoadTransitionRequestEnabled,
            bool gateOpen,
            bool gateBlocked,
            bool typeResultRequired,
            bool typeResultAvailable,
            bool pluginTypeResolved,
            bool resolutionCompleted,
            bool instanceCreationPlanned,
            bool instanceCreationStarted,
            bool instanceCreationCompleted,
            bool pluginInstanceCreated,
            bool instanceResultAvailable,
            bool constructorInvoked,
            bool failureIsolated,
            bool rollbackRequired,
            string instanceCreationStrategy,
            string methodName,
            string pluginTypeName,
            string resolvedTypeName,
            string instanceTypeName,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string loadedAssemblyName,
            string loadedAssemblyPath,
            BepInExPluginLoadInvocationSnapshot pluginLoadInvocation,
            string isolationBoundary,
            string status,
            string reasonCode,
            string detail)
        {
            Available = available;
            InstanceCreationRequested = instanceCreationRequested;
            InstanceCreationAccepted = instanceCreationAccepted;
            PluginInstanceCreationEnabled = pluginInstanceCreationEnabled;
            PluginTypeResolutionEnabled = pluginTypeResolutionEnabled;
            AssemblyLoadInvocationEnabled = assemblyLoadInvocationEnabled;
            AssemblyLoadContextRegistrationEnabled = assemblyLoadContextRegistrationEnabled;
            AssemblyLoadContextAllocationEnabled = assemblyLoadContextAllocationEnabled;
            AssemblyLoadContextPlanEnabled = assemblyLoadContextPlanEnabled;
            AssemblyLoadOperationEnabled = assemblyLoadOperationEnabled;
            AssemblyLoadResolverPlanEnabled = assemblyLoadResolverPlanEnabled;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            ExecutorEnabled = executorEnabled;
            AssemblyLoadTransitionRequestEnabled = assemblyLoadTransitionRequestEnabled;
            GateOpen = gateOpen;
            GateBlocked = gateBlocked;
            TypeResultRequired = typeResultRequired;
            TypeResultAvailable = typeResultAvailable;
            PluginTypeResolved = pluginTypeResolved;
            ResolutionCompleted = resolutionCompleted;
            InstanceCreationPlanned = instanceCreationPlanned;
            InstanceCreationStarted = instanceCreationStarted;
            InstanceCreationCompleted = instanceCreationCompleted;
            PluginInstanceCreated = pluginInstanceCreated;
            InstanceResultAvailable = instanceResultAvailable;
            ConstructorInvoked = constructorInvoked;
            FailureIsolated = failureIsolated;
            RollbackRequired = rollbackRequired;
            InstanceCreationStrategy = instanceCreationStrategy ?? "";
            MethodName = methodName ?? "";
            PluginTypeName = pluginTypeName ?? "";
            ResolvedTypeName = resolvedTypeName ?? "";
            InstanceTypeName = instanceTypeName ?? "";
            PluginId = pluginId ?? "";
            AssemblyName = assemblyName ?? "";
            AssemblyPath = assemblyPath ?? "";
            LoadedAssemblyName = loadedAssemblyName ?? "";
            LoadedAssemblyPath = loadedAssemblyPath ?? "";
            PluginLoadInvocation = pluginLoadInvocation ?? BepInExPluginLoadInvocationSnapshot.CreateUnavailable(
                "plugin-load-invocation-not-requested",
                "No Modstoke BasePlugin.Load invocation boundary was requested for this plugin instance creation.");
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public bool Available { get; }
        public bool InstanceCreationRequested { get; }
        public bool InstanceCreationAccepted { get; }
        public bool PluginInstanceCreationEnabled { get; }
        public bool PluginTypeResolutionEnabled { get; }
        public bool AssemblyLoadInvocationEnabled { get; }
        public bool AssemblyLoadContextRegistrationEnabled { get; }
        public bool AssemblyLoadContextAllocationEnabled { get; }
        public bool AssemblyLoadContextPlanEnabled { get; }
        public bool AssemblyLoadOperationEnabled { get; }
        public bool AssemblyLoadResolverPlanEnabled { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool ExecutorEnabled { get; }
        public bool AssemblyLoadTransitionRequestEnabled { get; }
        public bool GateOpen { get; }
        public bool GateBlocked { get; }
        public bool TypeResultRequired { get; }
        public bool TypeResultAvailable { get; }
        public bool PluginTypeResolved { get; }
        public bool ResolutionCompleted { get; }
        public bool InstanceCreationPlanned { get; }
        public bool InstanceCreationStarted { get; }
        public bool InstanceCreationCompleted { get; }
        public bool PluginInstanceCreated { get; }
        public bool InstanceResultAvailable { get; }
        public bool ConstructorInvoked { get; }
        public bool FailureIsolated { get; }
        public bool RollbackRequired { get; }
        public string InstanceCreationStrategy { get; }
        public string MethodName { get; }
        public string PluginTypeName { get; }
        public string ResolvedTypeName { get; }
        public string InstanceTypeName { get; }
        public string PluginId { get; }
        public string AssemblyName { get; }
        public string AssemblyPath { get; }
        public string LoadedAssemblyName { get; }
        public string LoadedAssemblyPath { get; }
        public BepInExPluginLoadInvocationSnapshot PluginLoadInvocation { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static BepInExPluginInstanceCreationSnapshot CreateUnavailable(
            string reasonCode,
            string detail)
        {
            return new BepInExPluginInstanceCreationSnapshot(
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
                false,
                false,
                false,
                true,
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
                true,
                false,
                "none",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                BepInExPluginLoadInvocationSnapshot.CreateUnavailable(
                    "plugin-load-invocation-not-requested",
                    "No Modstoke BasePlugin.Load invocation boundary was requested for this plugin instance creation."),
                "openacai.metadata-gate",
                "INFO",
                reasonCode,
                detail);
        }
    }

    [Obsolete(BepInExPluginCompatibility.RetiredStageMessage)]
    public sealed class BepInExPluginTypeResolutionSnapshot
    {
        internal BepInExPluginTypeResolutionSnapshot(
            bool available,
            bool resolutionRequested,
            bool resolutionAccepted,
            bool pluginTypeResolutionEnabled,
            bool assemblyLoadInvocationEnabled,
            bool assemblyLoadContextRegistrationEnabled,
            bool assemblyLoadContextAllocationEnabled,
            bool assemblyLoadContextPlanEnabled,
            bool assemblyLoadOperationEnabled,
            bool assemblyLoadResolverPlanEnabled,
            bool runtimePluginLoadingEnabled,
            bool executorEnabled,
            bool assemblyLoadTransitionRequestEnabled,
            bool gateOpen,
            bool gateBlocked,
            bool assemblyResultRequired,
            bool assemblyResultAvailable,
            bool assemblyLoaded,
            bool loadResultAvailable,
            bool invocationCompleted,
            bool resolutionPlanned,
            bool resolutionStarted,
            bool resolutionCompleted,
            bool pluginTypeResolved,
            bool typeResultAvailable,
            bool failureIsolated,
            bool rollbackRequired,
            string resolutionStrategy,
            string methodName,
            string pluginTypeName,
            string resolvedTypeName,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string loadedAssemblyName,
            string loadedAssemblyPath,
            BepInExPluginInstanceCreationSnapshot pluginInstanceCreation,
            string isolationBoundary,
            string status,
            string reasonCode,
            string detail)
        {
            Available = available;
            ResolutionRequested = resolutionRequested;
            ResolutionAccepted = resolutionAccepted;
            PluginTypeResolutionEnabled = pluginTypeResolutionEnabled;
            AssemblyLoadInvocationEnabled = assemblyLoadInvocationEnabled;
            AssemblyLoadContextRegistrationEnabled = assemblyLoadContextRegistrationEnabled;
            AssemblyLoadContextAllocationEnabled = assemblyLoadContextAllocationEnabled;
            AssemblyLoadContextPlanEnabled = assemblyLoadContextPlanEnabled;
            AssemblyLoadOperationEnabled = assemblyLoadOperationEnabled;
            AssemblyLoadResolverPlanEnabled = assemblyLoadResolverPlanEnabled;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            ExecutorEnabled = executorEnabled;
            AssemblyLoadTransitionRequestEnabled = assemblyLoadTransitionRequestEnabled;
            GateOpen = gateOpen;
            GateBlocked = gateBlocked;
            AssemblyResultRequired = assemblyResultRequired;
            AssemblyResultAvailable = assemblyResultAvailable;
            AssemblyLoaded = assemblyLoaded;
            LoadResultAvailable = loadResultAvailable;
            InvocationCompleted = invocationCompleted;
            ResolutionPlanned = resolutionPlanned;
            ResolutionStarted = resolutionStarted;
            ResolutionCompleted = resolutionCompleted;
            PluginTypeResolved = pluginTypeResolved;
            TypeResultAvailable = typeResultAvailable;
            FailureIsolated = failureIsolated;
            RollbackRequired = rollbackRequired;
            ResolutionStrategy = resolutionStrategy ?? "";
            MethodName = methodName ?? "";
            PluginTypeName = pluginTypeName ?? "";
            ResolvedTypeName = resolvedTypeName ?? "";
            PluginId = pluginId ?? "";
            AssemblyName = assemblyName ?? "";
            AssemblyPath = assemblyPath ?? "";
            LoadedAssemblyName = loadedAssemblyName ?? "";
            LoadedAssemblyPath = loadedAssemblyPath ?? "";
            PluginInstanceCreation = pluginInstanceCreation ?? BepInExPluginInstanceCreationSnapshot.CreateUnavailable(
                "plugin-instance-creation-not-requested",
                "No Modstoke plugin instance creation boundary was requested for this plugin type resolution.");
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public bool Available { get; }
        public bool ResolutionRequested { get; }
        public bool ResolutionAccepted { get; }
        public bool PluginTypeResolutionEnabled { get; }
        public bool AssemblyLoadInvocationEnabled { get; }
        public bool AssemblyLoadContextRegistrationEnabled { get; }
        public bool AssemblyLoadContextAllocationEnabled { get; }
        public bool AssemblyLoadContextPlanEnabled { get; }
        public bool AssemblyLoadOperationEnabled { get; }
        public bool AssemblyLoadResolverPlanEnabled { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool ExecutorEnabled { get; }
        public bool AssemblyLoadTransitionRequestEnabled { get; }
        public bool GateOpen { get; }
        public bool GateBlocked { get; }
        public bool AssemblyResultRequired { get; }
        public bool AssemblyResultAvailable { get; }
        public bool AssemblyLoaded { get; }
        public bool LoadResultAvailable { get; }
        public bool InvocationCompleted { get; }
        public bool ResolutionPlanned { get; }
        public bool ResolutionStarted { get; }
        public bool ResolutionCompleted { get; }
        public bool PluginTypeResolved { get; }
        public bool TypeResultAvailable { get; }
        public bool FailureIsolated { get; }
        public bool RollbackRequired { get; }
        public string ResolutionStrategy { get; }
        public string MethodName { get; }
        public string PluginTypeName { get; }
        public string ResolvedTypeName { get; }
        public string PluginId { get; }
        public string AssemblyName { get; }
        public string AssemblyPath { get; }
        public string LoadedAssemblyName { get; }
        public string LoadedAssemblyPath { get; }
        public BepInExPluginInstanceCreationSnapshot PluginInstanceCreation { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static BepInExPluginTypeResolutionSnapshot CreateUnavailable(
            string reasonCode,
            string detail)
        {
            return new BepInExPluginTypeResolutionSnapshot(
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
                false,
                false,
                true,
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
                true,
                false,
                "none",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                BepInExPluginInstanceCreationSnapshot.CreateUnavailable(
                    "plugin-instance-creation-not-requested",
                    "No Modstoke plugin instance creation boundary was requested for this plugin type resolution."),
                "openacai.metadata-gate",
                "INFO",
                reasonCode,
                detail);
        }
    }

    [Obsolete(BepInExPluginCompatibility.RetiredStageMessage)]
    public sealed class BepInExPluginAssemblyLoadInvocationSnapshot
    {
        internal BepInExPluginAssemblyLoadInvocationSnapshot(
            bool available,
            bool invocationRequested,
            bool invocationAccepted,
            bool assemblyLoadInvocationEnabled,
            bool assemblyLoadContextRegistrationEnabled,
            bool assemblyLoadContextAllocationEnabled,
            bool assemblyLoadContextPlanEnabled,
            bool assemblyLoadOperationEnabled,
            bool assemblyLoadResolverPlanEnabled,
            bool runtimePluginLoadingEnabled,
            bool executorEnabled,
            bool assemblyLoadTransitionRequestEnabled,
            bool gateOpen,
            bool gateBlocked,
            bool registeredContextRequired,
            bool registeredContextAvailable,
            bool contextTracked,
            bool invocationPlanned,
            bool invocationStarted,
            bool invocationCompleted,
            bool assemblyLoaded,
            bool loadResultAvailable,
            bool failureIsolated,
            bool rollbackRequired,
            string invocationStrategy,
            string methodName,
            string contextName,
            string contextIdentity,
            string operationKind,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string assemblyDirectory,
            string loadedAssemblyName,
            string loadedAssemblyPath,
            BepInExPluginTypeResolutionSnapshot pluginTypeResolution,
            string isolationBoundary,
            string status,
            string reasonCode,
            string detail)
        {
            Available = available;
            InvocationRequested = invocationRequested;
            InvocationAccepted = invocationAccepted;
            AssemblyLoadInvocationEnabled = assemblyLoadInvocationEnabled;
            AssemblyLoadContextRegistrationEnabled = assemblyLoadContextRegistrationEnabled;
            AssemblyLoadContextAllocationEnabled = assemblyLoadContextAllocationEnabled;
            AssemblyLoadContextPlanEnabled = assemblyLoadContextPlanEnabled;
            AssemblyLoadOperationEnabled = assemblyLoadOperationEnabled;
            AssemblyLoadResolverPlanEnabled = assemblyLoadResolverPlanEnabled;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            ExecutorEnabled = executorEnabled;
            AssemblyLoadTransitionRequestEnabled = assemblyLoadTransitionRequestEnabled;
            GateOpen = gateOpen;
            GateBlocked = gateBlocked;
            RegisteredContextRequired = registeredContextRequired;
            RegisteredContextAvailable = registeredContextAvailable;
            ContextTracked = contextTracked;
            InvocationPlanned = invocationPlanned;
            InvocationStarted = invocationStarted;
            InvocationCompleted = invocationCompleted;
            AssemblyLoaded = assemblyLoaded;
            LoadResultAvailable = loadResultAvailable;
            FailureIsolated = failureIsolated;
            RollbackRequired = rollbackRequired;
            InvocationStrategy = invocationStrategy ?? "";
            MethodName = methodName ?? "";
            ContextName = contextName ?? "";
            ContextIdentity = contextIdentity ?? "";
            OperationKind = operationKind ?? "";
            PluginId = pluginId ?? "";
            AssemblyName = assemblyName ?? "";
            AssemblyPath = assemblyPath ?? "";
            AssemblyDirectory = assemblyDirectory ?? "";
            LoadedAssemblyName = loadedAssemblyName ?? "";
            LoadedAssemblyPath = loadedAssemblyPath ?? "";
            PluginTypeResolution = pluginTypeResolution ?? BepInExPluginTypeResolutionSnapshot.CreateUnavailable(
                "plugin-type-resolution-not-requested",
                "No Modstoke plugin type resolution boundary was requested for this assembly-load invocation.");
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public bool Available { get; }
        public bool InvocationRequested { get; }
        public bool InvocationAccepted { get; }
        public bool AssemblyLoadInvocationEnabled { get; }
        public bool AssemblyLoadContextRegistrationEnabled { get; }
        public bool AssemblyLoadContextAllocationEnabled { get; }
        public bool AssemblyLoadContextPlanEnabled { get; }
        public bool AssemblyLoadOperationEnabled { get; }
        public bool AssemblyLoadResolverPlanEnabled { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool ExecutorEnabled { get; }
        public bool AssemblyLoadTransitionRequestEnabled { get; }
        public bool GateOpen { get; }
        public bool GateBlocked { get; }
        public bool RegisteredContextRequired { get; }
        public bool RegisteredContextAvailable { get; }
        public bool ContextTracked { get; }
        public bool InvocationPlanned { get; }
        public bool InvocationStarted { get; }
        public bool InvocationCompleted { get; }
        public bool AssemblyLoaded { get; }
        public bool LoadResultAvailable { get; }
        public bool FailureIsolated { get; }
        public bool RollbackRequired { get; }
        public string InvocationStrategy { get; }
        public string MethodName { get; }
        public string ContextName { get; }
        public string ContextIdentity { get; }
        public string OperationKind { get; }
        public string PluginId { get; }
        public string AssemblyName { get; }
        public string AssemblyPath { get; }
        public string AssemblyDirectory { get; }
        public string LoadedAssemblyName { get; }
        public string LoadedAssemblyPath { get; }
        public BepInExPluginTypeResolutionSnapshot PluginTypeResolution { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static BepInExPluginAssemblyLoadInvocationSnapshot CreateUnavailable(
            string reasonCode,
            string detail)
        {
            return new BepInExPluginAssemblyLoadInvocationSnapshot(
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
                false,
                true,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                true,
                false,
                "none",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                BepInExPluginTypeResolutionSnapshot.CreateUnavailable(
                    "plugin-type-resolution-not-requested",
                    "No Modstoke plugin type resolution boundary was requested for this assembly-load invocation."),
                "openacai.metadata-gate",
                "INFO",
                reasonCode,
                detail);
        }
    }

    [Obsolete(BepInExPluginCompatibility.RetiredStageMessage)]
    public sealed class BepInExPluginAssemblyLoadContextRegistrationSnapshot
    {
        internal BepInExPluginAssemblyLoadContextRegistrationSnapshot(
            bool available,
            bool registrationRequested,
            bool registrationAccepted,
            bool assemblyLoadContextRegistrationEnabled,
            bool assemblyLoadContextAllocationEnabled,
            bool assemblyLoadContextPlanEnabled,
            bool assemblyLoadOperationEnabled,
            bool assemblyLoadResolverPlanEnabled,
            bool runtimePluginLoadingEnabled,
            bool executorEnabled,
            bool assemblyLoadTransitionRequestEnabled,
            bool gateOpen,
            bool gateBlocked,
            bool constructorResultRequired,
            bool constructorResultAvailable,
            bool constructorCalled,
            bool contextCreated,
            bool contextTracked,
            bool unloadRegistered,
            bool weakReferencePlanned,
            bool weakReferenceCreated,
            bool ownershipRegistered,
            bool failureIsolated,
            bool rollbackRequired,
            string registrationStrategy,
            string trackingCollection,
            string contextName,
            string contextIdentity,
            string operationKind,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string assemblyDirectory,
            BepInExPluginAssemblyLoadInvocationSnapshot assemblyLoadInvocation,
            string isolationBoundary,
            string status,
            string reasonCode,
            string detail)
        {
            Available = available;
            RegistrationRequested = registrationRequested;
            RegistrationAccepted = registrationAccepted;
            AssemblyLoadContextRegistrationEnabled = assemblyLoadContextRegistrationEnabled;
            AssemblyLoadContextAllocationEnabled = assemblyLoadContextAllocationEnabled;
            AssemblyLoadContextPlanEnabled = assemblyLoadContextPlanEnabled;
            AssemblyLoadOperationEnabled = assemblyLoadOperationEnabled;
            AssemblyLoadResolverPlanEnabled = assemblyLoadResolverPlanEnabled;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            ExecutorEnabled = executorEnabled;
            AssemblyLoadTransitionRequestEnabled = assemblyLoadTransitionRequestEnabled;
            GateOpen = gateOpen;
            GateBlocked = gateBlocked;
            ConstructorResultRequired = constructorResultRequired;
            ConstructorResultAvailable = constructorResultAvailable;
            ConstructorCalled = constructorCalled;
            ContextCreated = contextCreated;
            ContextTracked = contextTracked;
            UnloadRegistered = unloadRegistered;
            WeakReferencePlanned = weakReferencePlanned;
            WeakReferenceCreated = weakReferenceCreated;
            OwnershipRegistered = ownershipRegistered;
            FailureIsolated = failureIsolated;
            RollbackRequired = rollbackRequired;
            RegistrationStrategy = registrationStrategy ?? "";
            TrackingCollection = trackingCollection ?? "";
            ContextName = contextName ?? "";
            ContextIdentity = contextIdentity ?? "";
            OperationKind = operationKind ?? "";
            PluginId = pluginId ?? "";
            AssemblyName = assemblyName ?? "";
            AssemblyPath = assemblyPath ?? "";
            AssemblyDirectory = assemblyDirectory ?? "";
            AssemblyLoadInvocation = assemblyLoadInvocation ?? BepInExPluginAssemblyLoadInvocationSnapshot.CreateUnavailable(
                "assembly-load-invocation-unavailable",
                "No Modstoke assembly-load invocation boundary was available for this context registration.");
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public bool Available { get; }
        public bool RegistrationRequested { get; }
        public bool RegistrationAccepted { get; }
        public bool AssemblyLoadContextRegistrationEnabled { get; }
        public bool AssemblyLoadContextAllocationEnabled { get; }
        public bool AssemblyLoadContextPlanEnabled { get; }
        public bool AssemblyLoadOperationEnabled { get; }
        public bool AssemblyLoadResolverPlanEnabled { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool ExecutorEnabled { get; }
        public bool AssemblyLoadTransitionRequestEnabled { get; }
        public bool GateOpen { get; }
        public bool GateBlocked { get; }
        public bool ConstructorResultRequired { get; }
        public bool ConstructorResultAvailable { get; }
        public bool ConstructorCalled { get; }
        public bool ContextCreated { get; }
        public bool ContextTracked { get; }
        public bool UnloadRegistered { get; }
        public bool WeakReferencePlanned { get; }
        public bool WeakReferenceCreated { get; }
        public bool OwnershipRegistered { get; }
        public bool FailureIsolated { get; }
        public bool RollbackRequired { get; }
        public string RegistrationStrategy { get; }
        public string TrackingCollection { get; }
        public string ContextName { get; }
        public string ContextIdentity { get; }
        public string OperationKind { get; }
        public string PluginId { get; }
        public string AssemblyName { get; }
        public string AssemblyPath { get; }
        public string AssemblyDirectory { get; }
        public BepInExPluginAssemblyLoadInvocationSnapshot AssemblyLoadInvocation { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static BepInExPluginAssemblyLoadContextRegistrationSnapshot CreateUnavailable(
            string reasonCode,
            string detail)
        {
            return new BepInExPluginAssemblyLoadContextRegistrationSnapshot(
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
                true,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                true,
                false,
                "none",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                BepInExPluginAssemblyLoadInvocationSnapshot.CreateUnavailable(
                    "assembly-load-invocation-not-requested",
                    "No Modstoke assembly-load invocation boundary was requested."),
                "openacai.metadata-gate",
                "INFO",
                reasonCode,
                detail);
        }
    }

    [Obsolete(BepInExPluginCompatibility.RetiredStageMessage)]
    public sealed class BepInExPluginAssemblyLoadContextAllocationSnapshot
    {
        internal BepInExPluginAssemblyLoadContextAllocationSnapshot(
            bool available,
            bool allocationRequested,
            bool allocationAccepted,
            bool assemblyLoadContextAllocationEnabled,
            bool assemblyLoadContextPlanEnabled,
            bool assemblyLoadOperationEnabled,
            bool assemblyLoadResolverPlanEnabled,
            bool runtimePluginLoadingEnabled,
            bool executorEnabled,
            bool assemblyLoadTransitionRequestEnabled,
            bool gateOpen,
            bool gateBlocked,
            bool constructorPlanned,
            bool constructorCalled,
            bool contextCreated,
            bool contextTracked,
            bool unloadRegistered,
            bool collectibleContextPlanned,
            bool dedicatedContextPlanned,
            bool defaultContextBlocked,
            bool failureIsolated,
            bool rollbackRequired,
            string allocationStrategy,
            string constructorSignature,
            string contextName,
            string contextIdentity,
            string operationKind,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string assemblyDirectory,
            BepInExPluginAssemblyLoadContextRegistrationSnapshot assemblyLoadContextRegistration,
            string isolationBoundary,
            string status,
            string reasonCode,
            string detail)
        {
            Available = available;
            AllocationRequested = allocationRequested;
            AllocationAccepted = allocationAccepted;
            AssemblyLoadContextAllocationEnabled = assemblyLoadContextAllocationEnabled;
            AssemblyLoadContextPlanEnabled = assemblyLoadContextPlanEnabled;
            AssemblyLoadOperationEnabled = assemblyLoadOperationEnabled;
            AssemblyLoadResolverPlanEnabled = assemblyLoadResolverPlanEnabled;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            ExecutorEnabled = executorEnabled;
            AssemblyLoadTransitionRequestEnabled = assemblyLoadTransitionRequestEnabled;
            GateOpen = gateOpen;
            GateBlocked = gateBlocked;
            ConstructorPlanned = constructorPlanned;
            ConstructorCalled = constructorCalled;
            ContextCreated = contextCreated;
            ContextTracked = contextTracked;
            UnloadRegistered = unloadRegistered;
            CollectibleContextPlanned = collectibleContextPlanned;
            DedicatedContextPlanned = dedicatedContextPlanned;
            DefaultContextBlocked = defaultContextBlocked;
            FailureIsolated = failureIsolated;
            RollbackRequired = rollbackRequired;
            AllocationStrategy = allocationStrategy ?? "";
            ConstructorSignature = constructorSignature ?? "";
            ContextName = contextName ?? "";
            ContextIdentity = contextIdentity ?? "";
            OperationKind = operationKind ?? "";
            PluginId = pluginId ?? "";
            AssemblyName = assemblyName ?? "";
            AssemblyPath = assemblyPath ?? "";
            AssemblyDirectory = assemblyDirectory ?? "";
            AssemblyLoadContextRegistration = assemblyLoadContextRegistration ?? BepInExPluginAssemblyLoadContextRegistrationSnapshot.CreateUnavailable(
                "assembly-load-context-registration-unavailable",
                "No Modstoke assembly-load context registration boundary was available for this allocation attempt.");
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public bool Available { get; }
        public bool AllocationRequested { get; }
        public bool AllocationAccepted { get; }
        public bool AssemblyLoadContextAllocationEnabled { get; }
        public bool AssemblyLoadContextPlanEnabled { get; }
        public bool AssemblyLoadOperationEnabled { get; }
        public bool AssemblyLoadResolverPlanEnabled { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool ExecutorEnabled { get; }
        public bool AssemblyLoadTransitionRequestEnabled { get; }
        public bool GateOpen { get; }
        public bool GateBlocked { get; }
        public bool ConstructorPlanned { get; }
        public bool ConstructorCalled { get; }
        public bool ContextCreated { get; }
        public bool ContextTracked { get; }
        public bool UnloadRegistered { get; }
        public bool CollectibleContextPlanned { get; }
        public bool DedicatedContextPlanned { get; }
        public bool DefaultContextBlocked { get; }
        public bool FailureIsolated { get; }
        public bool RollbackRequired { get; }
        public string AllocationStrategy { get; }
        public string ConstructorSignature { get; }
        public string ContextName { get; }
        public string ContextIdentity { get; }
        public string OperationKind { get; }
        public string PluginId { get; }
        public string AssemblyName { get; }
        public string AssemblyPath { get; }
        public string AssemblyDirectory { get; }
        public BepInExPluginAssemblyLoadContextRegistrationSnapshot AssemblyLoadContextRegistration { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static BepInExPluginAssemblyLoadContextAllocationSnapshot CreateUnavailable(
            string reasonCode,
            string detail)
        {
            return new BepInExPluginAssemblyLoadContextAllocationSnapshot(
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
                true,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                true,
                true,
                false,
                "none",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                BepInExPluginAssemblyLoadContextRegistrationSnapshot.CreateUnavailable(
                    "assembly-load-context-registration-not-requested",
                    "No Modstoke assembly-load context registration boundary was requested."),
                "openacai.metadata-gate",
                "INFO",
                reasonCode,
                detail);
        }
    }

    [Obsolete(BepInExPluginCompatibility.RetiredStageMessage)]
    public sealed class BepInExPluginAssemblyLoadContextPlanSnapshot
    {
        internal BepInExPluginAssemblyLoadContextPlanSnapshot(
            bool available,
            bool planRequested,
            bool planAccepted,
            bool assemblyLoadContextPlanEnabled,
            bool assemblyLoadOperationEnabled,
            bool assemblyLoadResolverPlanEnabled,
            bool runtimePluginLoadingEnabled,
            bool executorEnabled,
            bool assemblyLoadTransitionRequestEnabled,
            bool gateOpen,
            bool gateBlocked,
            bool contextAllocationPlanned,
            bool contextCreated,
            bool collectibleContextPlanned,
            bool dedicatedContextPlanned,
            bool defaultContextBlocked,
            bool failureIsolated,
            bool rollbackRequired,
            string allocationStrategy,
            string contextName,
            string contextIdentity,
            string operationKind,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string assemblyDirectory,
            BepInExPluginAssemblyLoadContextAllocationSnapshot assemblyLoadContextAllocation,
            string isolationBoundary,
            string status,
            string reasonCode,
            string detail)
        {
            Available = available;
            PlanRequested = planRequested;
            PlanAccepted = planAccepted;
            AssemblyLoadContextPlanEnabled = assemblyLoadContextPlanEnabled;
            AssemblyLoadOperationEnabled = assemblyLoadOperationEnabled;
            AssemblyLoadResolverPlanEnabled = assemblyLoadResolverPlanEnabled;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            ExecutorEnabled = executorEnabled;
            AssemblyLoadTransitionRequestEnabled = assemblyLoadTransitionRequestEnabled;
            GateOpen = gateOpen;
            GateBlocked = gateBlocked;
            ContextAllocationPlanned = contextAllocationPlanned;
            ContextCreated = contextCreated;
            CollectibleContextPlanned = collectibleContextPlanned;
            DedicatedContextPlanned = dedicatedContextPlanned;
            DefaultContextBlocked = defaultContextBlocked;
            FailureIsolated = failureIsolated;
            RollbackRequired = rollbackRequired;
            AllocationStrategy = allocationStrategy ?? "";
            ContextName = contextName ?? "";
            ContextIdentity = contextIdentity ?? "";
            OperationKind = operationKind ?? "";
            PluginId = pluginId ?? "";
            AssemblyName = assemblyName ?? "";
            AssemblyPath = assemblyPath ?? "";
            AssemblyDirectory = assemblyDirectory ?? "";
            AssemblyLoadContextAllocation = assemblyLoadContextAllocation ?? BepInExPluginAssemblyLoadContextAllocationSnapshot.CreateUnavailable(
                "assembly-load-context-allocation-unavailable",
                "No Modstoke assembly-load context allocation attempt was available for this plan.");
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public bool Available { get; }
        public bool PlanRequested { get; }
        public bool PlanAccepted { get; }
        public bool AssemblyLoadContextPlanEnabled { get; }
        public bool AssemblyLoadOperationEnabled { get; }
        public bool AssemblyLoadResolverPlanEnabled { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool ExecutorEnabled { get; }
        public bool AssemblyLoadTransitionRequestEnabled { get; }
        public bool GateOpen { get; }
        public bool GateBlocked { get; }
        public bool ContextAllocationPlanned { get; }
        public bool ContextCreated { get; }
        public bool CollectibleContextPlanned { get; }
        public bool DedicatedContextPlanned { get; }
        public bool DefaultContextBlocked { get; }
        public bool FailureIsolated { get; }
        public bool RollbackRequired { get; }
        public string AllocationStrategy { get; }
        public string ContextName { get; }
        public string ContextIdentity { get; }
        public string OperationKind { get; }
        public string PluginId { get; }
        public string AssemblyName { get; }
        public string AssemblyPath { get; }
        public string AssemblyDirectory { get; }
        public BepInExPluginAssemblyLoadContextAllocationSnapshot AssemblyLoadContextAllocation { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static BepInExPluginAssemblyLoadContextPlanSnapshot CreateUnavailable(
            string reasonCode,
            string detail)
        {
            return new BepInExPluginAssemblyLoadContextPlanSnapshot(
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
                true,
                false,
                false,
                false,
                false,
                true,
                true,
                false,
                "none",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                BepInExPluginAssemblyLoadContextAllocationSnapshot.CreateUnavailable(
                    "assembly-load-context-allocation-not-requested",
                    "No Modstoke assembly-load context allocation attempt was requested."),
                "openacai.metadata-gate",
                "INFO",
                reasonCode,
                detail);
        }
    }

    [Obsolete(BepInExPluginCompatibility.RetiredStageMessage)]
    public sealed class BepInExPluginAssemblyLoadOperationSnapshot
    {
        internal BepInExPluginAssemblyLoadOperationSnapshot(
            bool available,
            bool operationRequested,
            bool operationAccepted,
            bool assemblyLoadOperationEnabled,
            bool assemblyLoadResolverPlanEnabled,
            bool runtimePluginLoadingEnabled,
            bool executorEnabled,
            bool assemblyLoadTransitionRequestEnabled,
            bool gateOpen,
            bool gateBlocked,
            bool contextCreated,
            bool operationStarted,
            bool operationCompleted,
            bool assemblyLoaded,
            bool assemblyAlreadyLoaded,
            bool failureIsolated,
            bool rollbackRequired,
            string operationKind,
            string methodName,
            string contextName,
            string contextIdentity,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string assemblyDirectory,
            bool assemblyExists,
            long assemblyFileSize,
            int assemblyReferenceCount,
            int resolvedAssemblyReferenceCount,
            int unresolvedAssemblyReferenceCount,
            BepInExPluginAssemblyLoadContextPlanSnapshot assemblyLoadContextPlan,
            string rollbackStrategy,
            string isolationBoundary,
            string status,
            string reasonCode,
            string detail)
        {
            Available = available;
            OperationRequested = operationRequested;
            OperationAccepted = operationAccepted;
            AssemblyLoadOperationEnabled = assemblyLoadOperationEnabled;
            AssemblyLoadResolverPlanEnabled = assemblyLoadResolverPlanEnabled;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            ExecutorEnabled = executorEnabled;
            AssemblyLoadTransitionRequestEnabled = assemblyLoadTransitionRequestEnabled;
            GateOpen = gateOpen;
            GateBlocked = gateBlocked;
            ContextCreated = contextCreated;
            OperationStarted = operationStarted;
            OperationCompleted = operationCompleted;
            AssemblyLoaded = assemblyLoaded;
            AssemblyAlreadyLoaded = assemblyAlreadyLoaded;
            FailureIsolated = failureIsolated;
            RollbackRequired = rollbackRequired;
            OperationKind = operationKind ?? "";
            MethodName = methodName ?? "";
            ContextName = contextName ?? "";
            ContextIdentity = contextIdentity ?? "";
            PluginId = pluginId ?? "";
            AssemblyName = assemblyName ?? "";
            AssemblyPath = assemblyPath ?? "";
            AssemblyDirectory = assemblyDirectory ?? "";
            AssemblyExists = assemblyExists;
            AssemblyFileSize = Math.Max(0, assemblyFileSize);
            AssemblyReferenceCount = Math.Max(0, assemblyReferenceCount);
            ResolvedAssemblyReferenceCount = Math.Max(0, resolvedAssemblyReferenceCount);
            UnresolvedAssemblyReferenceCount = Math.Max(0, unresolvedAssemblyReferenceCount);
            AssemblyLoadContextPlan = assemblyLoadContextPlan ?? BepInExPluginAssemblyLoadContextPlanSnapshot.CreateUnavailable(
                "assembly-load-context-plan-unavailable",
                "No Modstoke assembly-load context allocation plan was available for this operation.");
            RollbackStrategy = rollbackStrategy ?? "";
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public bool Available { get; }
        public bool OperationRequested { get; }
        public bool OperationAccepted { get; }
        public bool AssemblyLoadOperationEnabled { get; }
        public bool AssemblyLoadResolverPlanEnabled { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool ExecutorEnabled { get; }
        public bool AssemblyLoadTransitionRequestEnabled { get; }
        public bool GateOpen { get; }
        public bool GateBlocked { get; }
        public bool ContextCreated { get; }
        public bool OperationStarted { get; }
        public bool OperationCompleted { get; }
        public bool AssemblyLoaded { get; }
        public bool AssemblyAlreadyLoaded { get; }
        public bool FailureIsolated { get; }
        public bool RollbackRequired { get; }
        public string OperationKind { get; }
        public string MethodName { get; }
        public string ContextName { get; }
        public string ContextIdentity { get; }
        public string PluginId { get; }
        public string AssemblyName { get; }
        public string AssemblyPath { get; }
        public string AssemblyDirectory { get; }
        public bool AssemblyExists { get; }
        public long AssemblyFileSize { get; }
        public int AssemblyReferenceCount { get; }
        public int ResolvedAssemblyReferenceCount { get; }
        public int UnresolvedAssemblyReferenceCount { get; }
        public BepInExPluginAssemblyLoadContextPlanSnapshot AssemblyLoadContextPlan { get; }
        public string RollbackStrategy { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static BepInExPluginAssemblyLoadOperationSnapshot CreateUnavailable(
            string reasonCode,
            string detail)
        {
            return new BepInExPluginAssemblyLoadOperationSnapshot(
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                true,
                false,
                false,
                false,
                false,
                false,
                true,
                false,
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                false,
                0,
                0,
                0,
                0,
                BepInExPluginAssemblyLoadContextPlanSnapshot.CreateUnavailable(
                    "assembly-load-context-plan-not-requested",
                    "No Modstoke assembly-load context allocation plan was requested."),
                "none",
                "openacai.metadata-gate",
                "INFO",
                reasonCode,
                detail);
        }
    }

    [Obsolete(BepInExPluginCompatibility.RetiredStageMessage)]
    public sealed class BepInExPluginAssemblyLoadResolverPlanSnapshot
    {
        internal BepInExPluginAssemblyLoadResolverPlanSnapshot(
            bool available,
            bool planRequested,
            bool planAccepted,
            bool assemblyLoadResolverPlanEnabled,
            bool runtimePluginLoadingEnabled,
            bool executorEnabled,
            bool assemblyLoadTransitionRequestEnabled,
            bool gateOpen,
            bool gateBlocked,
            bool assemblyLoadStarted,
            bool assemblyLoaded,
            bool failureIsolated,
            bool rollbackRequired,
            string contextName,
            string pluginId,
            string assemblyName,
            string assemblyPath,
            string assemblyDirectory,
            bool assemblyExists,
            long assemblyFileSize,
            int resolverSearchDirectoryCount,
            IReadOnlyList<BepInExPluginAssemblyLoadResolverDependencyPlanSnapshot> dependencyPlans,
            BepInExPluginAssemblyLoadOperationSnapshot assemblyLoadOperation,
            string isolationBoundary,
            string status,
            string reasonCode,
            string detail)
        {
            Available = available;
            PlanRequested = planRequested;
            PlanAccepted = planAccepted;
            AssemblyLoadResolverPlanEnabled = assemblyLoadResolverPlanEnabled;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            ExecutorEnabled = executorEnabled;
            AssemblyLoadTransitionRequestEnabled = assemblyLoadTransitionRequestEnabled;
            GateOpen = gateOpen;
            GateBlocked = gateBlocked;
            AssemblyLoadStarted = assemblyLoadStarted;
            AssemblyLoaded = assemblyLoaded;
            FailureIsolated = failureIsolated;
            RollbackRequired = rollbackRequired;
            ContextName = contextName ?? "";
            PluginId = pluginId ?? "";
            AssemblyName = assemblyName ?? "";
            AssemblyPath = assemblyPath ?? "";
            AssemblyDirectory = assemblyDirectory ?? "";
            AssemblyExists = assemblyExists;
            AssemblyFileSize = Math.Max(0, assemblyFileSize);
            ResolverSearchDirectoryCount = Math.Max(0, resolverSearchDirectoryCount);
            DependencyPlans = dependencyPlans ?? Array.Empty<BepInExPluginAssemblyLoadResolverDependencyPlanSnapshot>();
            AssemblyReferenceCount = DependencyPlans.Count;
            ResolvedAssemblyReferenceCount = DependencyPlans.Count(plan => plan != null && plan.Resolved);
            UnresolvedAssemblyReferenceCount = DependencyPlans.Count(plan => plan != null && !plan.Resolved);
            FirstUnresolvedAssemblyReference = DependencyPlans.FirstOrDefault(plan => plan != null && !plan.Resolved);
            DependenciesResolved = AssemblyReferenceCount == ResolvedAssemblyReferenceCount;
            AssemblyLoadOperation = assemblyLoadOperation ?? BepInExPluginAssemblyLoadOperationSnapshot.CreateUnavailable(
                "assembly-load-operation-unavailable",
                "No Modstoke assembly-load operation plan was available for this resolver plan.");
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public bool Available { get; }
        public bool PlanRequested { get; }
        public bool PlanAccepted { get; }
        public bool AssemblyLoadResolverPlanEnabled { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool ExecutorEnabled { get; }
        public bool AssemblyLoadTransitionRequestEnabled { get; }
        public bool GateOpen { get; }
        public bool GateBlocked { get; }
        public bool AssemblyLoadStarted { get; }
        public bool AssemblyLoaded { get; }
        public bool FailureIsolated { get; }
        public bool RollbackRequired { get; }
        public string ContextName { get; }
        public string PluginId { get; }
        public string AssemblyName { get; }
        public string AssemblyPath { get; }
        public string AssemblyDirectory { get; }
        public bool AssemblyExists { get; }
        public long AssemblyFileSize { get; }
        public int ResolverSearchDirectoryCount { get; }
        public IReadOnlyList<BepInExPluginAssemblyLoadResolverDependencyPlanSnapshot> DependencyPlans { get; }
        public int AssemblyReferenceCount { get; }
        public int ResolvedAssemblyReferenceCount { get; }
        public int UnresolvedAssemblyReferenceCount { get; }
        public bool DependenciesResolved { get; }
        public BepInExPluginAssemblyLoadResolverDependencyPlanSnapshot FirstUnresolvedAssemblyReference { get; }
        public BepInExPluginAssemblyLoadOperationSnapshot AssemblyLoadOperation { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static BepInExPluginAssemblyLoadResolverPlanSnapshot CreateUnavailable(
            string reasonCode,
            string detail)
        {
            return new BepInExPluginAssemblyLoadResolverPlanSnapshot(
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                true,
                false,
                false,
                true,
                false,
                "",
                "",
                "",
                "",
                "",
                false,
                0,
                0,
                Array.Empty<BepInExPluginAssemblyLoadResolverDependencyPlanSnapshot>(),
                BepInExPluginAssemblyLoadOperationSnapshot.CreateUnavailable(
                    "assembly-load-operation-not-requested",
                    "No Modstoke assembly-load operation plan was requested for this resolver plan."),
                "openacai.metadata-gate",
                "INFO",
                reasonCode,
                detail);
        }
    }

    [Obsolete(BepInExPluginCompatibility.RetiredStageMessage)]
    public sealed class BepInExPluginInvocationExecutorTransitionSnapshot
    {
        internal BepInExPluginInvocationExecutorTransitionSnapshot(
            int sequenceNumber,
            string stageName,
            string stageKind,
            string fromState,
            string toState,
            string action,
            bool gateOpen,
            bool gateBlocked,
            bool executionStarted,
            bool executionCompleted,
            bool failureIsolated,
            bool rollbackRequired,
            string isolationBoundary,
            string status,
            string reasonCode,
            string detail)
        {
            SequenceNumber = Math.Max(0, sequenceNumber);
            StageName = stageName ?? "";
            StageKind = stageKind ?? "";
            FromState = fromState ?? "";
            ToState = toState ?? "";
            Action = action ?? "";
            GateOpen = gateOpen;
            GateBlocked = gateBlocked;
            ExecutionStarted = executionStarted;
            ExecutionCompleted = executionCompleted;
            FailureIsolated = failureIsolated;
            RollbackRequired = rollbackRequired;
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public int SequenceNumber { get; }
        public string StageName { get; }
        public string StageKind { get; }
        public string FromState { get; }
        public string ToState { get; }
        public string Action { get; }
        public bool GateOpen { get; }
        public bool GateBlocked { get; }
        public bool ExecutionStarted { get; }
        public bool ExecutionCompleted { get; }
        public bool FailureIsolated { get; }
        public bool RollbackRequired { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    [Obsolete(BepInExPluginCompatibility.RetiredStageMessage)]
    public sealed class BepInExPluginInvocationExecutorTransitionRequestSnapshot
    {
        internal BepInExPluginInvocationExecutorTransitionRequestSnapshot(
            string requestId,
            int sequenceNumber,
            string stageName,
            string stageKind,
            string requestedStageKind,
            string requestSource,
            string action,
            bool requested,
            bool accepted,
            bool stageMatched,
            bool stageAvailable,
            bool assemblyLoadTransitionRequestEnabled,
            bool executorEnabled,
            bool runtimePluginLoadingEnabled,
            bool gateOpen,
            bool gateBlocked,
            bool executionStarted,
            bool executionCompleted,
            bool assemblyLoaded,
            BepInExPluginAssemblyLoadResolverPlanSnapshot assemblyLoadResolverPlan,
            bool failureIsolated,
            bool rollbackRequired,
            string isolationBoundary,
            string status,
            string reasonCode,
            string detail)
        {
            RequestId = requestId ?? "";
            SequenceNumber = Math.Max(0, sequenceNumber);
            StageName = stageName ?? "";
            StageKind = stageKind ?? "";
            RequestedStageKind = requestedStageKind ?? "";
            RequestSource = requestSource ?? "";
            Action = action ?? "";
            Requested = requested;
            Accepted = accepted;
            StageMatched = stageMatched;
            StageAvailable = stageAvailable;
            AssemblyLoadTransitionRequestEnabled = assemblyLoadTransitionRequestEnabled;
            ExecutorEnabled = executorEnabled;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            GateOpen = gateOpen;
            GateBlocked = gateBlocked;
            ExecutionStarted = executionStarted;
            ExecutionCompleted = executionCompleted;
            AssemblyLoaded = assemblyLoaded;
            AssemblyLoadResolverPlan = assemblyLoadResolverPlan ?? BepInExPluginAssemblyLoadResolverPlanSnapshot.CreateUnavailable(
                "assembly-load-resolver-plan-unavailable",
                "No Modstoke assembly-load resolver plan was available for this transition request.");
            FailureIsolated = failureIsolated;
            RollbackRequired = rollbackRequired;
            IsolationBoundary = isolationBoundary ?? "";
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public string RequestId { get; }
        public int SequenceNumber { get; }
        public string StageName { get; }
        public string StageKind { get; }
        public string RequestedStageKind { get; }
        public string RequestSource { get; }
        public string Action { get; }
        public bool Requested { get; }
        public bool Accepted { get; }
        public bool StageMatched { get; }
        public bool StageAvailable { get; }
        public bool AssemblyLoadTransitionRequestEnabled { get; }
        public bool ExecutorEnabled { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool GateOpen { get; }
        public bool GateBlocked { get; }
        public bool ExecutionStarted { get; }
        public bool ExecutionCompleted { get; }
        public bool AssemblyLoaded { get; }
        public BepInExPluginAssemblyLoadResolverPlanSnapshot AssemblyLoadResolverPlan { get; }
        public bool FailureIsolated { get; }
        public bool RollbackRequired { get; }
        public string IsolationBoundary { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);
    }

    [Obsolete(BepInExPluginCompatibility.RetiredStageMessage)]
    public sealed class BepInExPluginInvocationExecutorSnapshot
    {
        internal BepInExPluginInvocationExecutorSnapshot(
            bool available,
            bool executorEnabled,
            bool runtimePluginLoadingEnabled,
            bool assemblyLoadTransitionRequestEnabled,
            bool executionGateOpen,
            bool executionGateBlocked,
            string stateMachineVersion,
            string initialState,
            string finalState,
            bool rollbackRequired,
            string isolationBoundary,
            BepInExPluginActivationPolicySnapshot activationPolicy,
            IReadOnlyList<BepInExPluginInvocationExecutorTransitionSnapshot> transitions,
            IReadOnlyList<BepInExPluginInvocationExecutorTransitionRequestSnapshot> transitionRequests,
            string status,
            string reasonCode,
            string detail)
        {
            Available = available;
            ExecutorEnabled = executorEnabled;
            RuntimePluginLoadingEnabled = runtimePluginLoadingEnabled;
            AssemblyLoadTransitionRequestEnabled = assemblyLoadTransitionRequestEnabled;
            ExecutionGateOpen = executionGateOpen;
            ExecutionGateBlocked = executionGateBlocked;
            StateMachineVersion = stateMachineVersion ?? "";
            InitialState = initialState ?? "";
            FinalState = finalState ?? "";
            RollbackRequired = rollbackRequired;
            IsolationBoundary = isolationBoundary ?? "";
            ActivationPolicy = activationPolicy ?? BepInExPluginActivationPolicySnapshot.CreateUnavailable("activation-policy-unavailable", "BepInEx plugin activation policy was not available for this executor snapshot.");
            Transitions = transitions ?? Array.Empty<BepInExPluginInvocationExecutorTransitionSnapshot>();
            TransitionCount = Transitions.Count;
            BlockedTransitionCount = Transitions.Count(transition => transition != null && transition.GateBlocked);
            OpenTransitionCount = Transitions.Count(transition => transition != null && transition.GateOpen);
            StartedTransitionCount = Transitions.Count(transition => transition != null && transition.ExecutionStarted);
            CompletedTransitionCount = Transitions.Count(transition => transition != null && transition.ExecutionCompleted);
            FailedIsolatedTransitionCount = Transitions.Count(transition => transition != null && transition.FailureIsolated && transition.Issue);
            FirstBlockedTransition = Transitions.FirstOrDefault(transition => transition != null && transition.GateBlocked);
            TransitionRequests = transitionRequests ?? Array.Empty<BepInExPluginInvocationExecutorTransitionRequestSnapshot>();
            TransitionRequestCount = TransitionRequests.Count;
            AcceptedTransitionRequestCount = TransitionRequests.Count(request => request != null && request.Accepted);
            BlockedTransitionRequestCount = TransitionRequests.Count(request => request != null && request.GateBlocked);
            OpenTransitionRequestCount = TransitionRequests.Count(request => request != null && request.GateOpen);
            StartedTransitionRequestCount = TransitionRequests.Count(request => request != null && request.ExecutionStarted);
            CompletedTransitionRequestCount = TransitionRequests.Count(request => request != null && request.ExecutionCompleted);
            AssemblyLoadedTransitionRequestCount = TransitionRequests.Count(request => request != null && request.AssemblyLoaded);
            FirstBlockedTransitionRequest = TransitionRequests.FirstOrDefault(request => request != null && request.GateBlocked);
            AssemblyLoadResolverPlanCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.Available);
            AcceptedAssemblyLoadResolverPlanCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.Available && request.AssemblyLoadResolverPlan.PlanAccepted);
            BlockedAssemblyLoadResolverPlanCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.Available && request.AssemblyLoadResolverPlan.GateBlocked);
            OpenAssemblyLoadResolverPlanCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.Available && request.AssemblyLoadResolverPlan.GateOpen);
            StartedAssemblyLoadResolverPlanCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.Available && request.AssemblyLoadResolverPlan.AssemblyLoadStarted);
            LoadedAssemblyLoadResolverPlanCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.Available && request.AssemblyLoadResolverPlan.AssemblyLoaded);
            FirstBlockedAssemblyLoadResolverPlan = TransitionRequests
                .Select(request => request == null ? null : request.AssemblyLoadResolverPlan)
                .FirstOrDefault(plan => plan != null && plan.Available && plan.GateBlocked);
            AssemblyLoadOperationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.Available);
            AcceptedAssemblyLoadOperationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.OperationAccepted);
            BlockedAssemblyLoadOperationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.GateBlocked);
            OpenAssemblyLoadOperationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.GateOpen);
            StartedAssemblyLoadOperationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.OperationStarted);
            CompletedAssemblyLoadOperationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.OperationCompleted);
            LoadedAssemblyLoadOperationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoaded);
            FirstBlockedAssemblyLoadOperation = TransitionRequests
                .Select(request => request == null || request.AssemblyLoadResolverPlan == null ? null : request.AssemblyLoadResolverPlan.AssemblyLoadOperation)
                .FirstOrDefault(operation => operation != null && operation.Available && operation.GateBlocked);
            AssemblyLoadContextPlanCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.Available);
            AcceptedAssemblyLoadContextPlanCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.PlanAccepted);
            BlockedAssemblyLoadContextPlanCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.GateBlocked);
            OpenAssemblyLoadContextPlanCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.GateOpen);
            CreatedAssemblyLoadContextPlanCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.ContextCreated);
            FirstBlockedAssemblyLoadContextPlan = TransitionRequests
                .Select(request => request == null || request.AssemblyLoadResolverPlan == null || request.AssemblyLoadResolverPlan.AssemblyLoadOperation == null ? null : request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan)
                .FirstOrDefault(plan => plan != null && plan.Available && plan.GateBlocked);
            AssemblyLoadContextAllocationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation.Available);
            AcceptedAssemblyLoadContextAllocationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation.AllocationAccepted);
            BlockedAssemblyLoadContextAllocationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation.GateBlocked);
            OpenAssemblyLoadContextAllocationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation.GateOpen);
            ConstructorCalledAssemblyLoadContextAllocationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation.ConstructorCalled);
            CreatedAssemblyLoadContextAllocationCount = TransitionRequests.Count(request => request != null && request.AssemblyLoadResolverPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation != null && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation.Available && request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation.ContextCreated);
            FirstBlockedAssemblyLoadContextAllocation = TransitionRequests
                .Select(request => request == null || request.AssemblyLoadResolverPlan == null || request.AssemblyLoadResolverPlan.AssemblyLoadOperation == null || request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan == null ? null : request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation)
                .FirstOrDefault(allocation => allocation != null && allocation.Available && allocation.GateBlocked);
            var assemblyLoadContextRegistrations = TransitionRequests
                .Select(request => request == null || request.AssemblyLoadResolverPlan == null || request.AssemblyLoadResolverPlan.AssemblyLoadOperation == null || request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan == null ? null : request.AssemblyLoadResolverPlan.AssemblyLoadOperation.AssemblyLoadContextPlan.AssemblyLoadContextAllocation)
                .Select(allocation => allocation == null ? null : allocation.AssemblyLoadContextRegistration)
                .Where(registration => registration != null && registration.Available)
                .ToArray();
            AssemblyLoadContextRegistrationCount = assemblyLoadContextRegistrations.Length;
            AcceptedAssemblyLoadContextRegistrationCount = assemblyLoadContextRegistrations.Count(registration => registration != null && registration.RegistrationAccepted);
            BlockedAssemblyLoadContextRegistrationCount = assemblyLoadContextRegistrations.Count(registration => registration != null && registration.GateBlocked);
            OpenAssemblyLoadContextRegistrationCount = assemblyLoadContextRegistrations.Count(registration => registration != null && registration.GateOpen);
            TrackedAssemblyLoadContextRegistrationCount = assemblyLoadContextRegistrations.Count(registration => registration != null && registration.ContextTracked);
            UnloadRegisteredAssemblyLoadContextRegistrationCount = assemblyLoadContextRegistrations.Count(registration => registration != null && registration.UnloadRegistered);
            FirstBlockedAssemblyLoadContextRegistration = assemblyLoadContextRegistrations.FirstOrDefault(registration => registration != null && registration.GateBlocked);
            var assemblyLoadInvocations = assemblyLoadContextRegistrations
                .Select(registration => registration == null ? null : registration.AssemblyLoadInvocation)
                .Where(invocation => invocation != null && invocation.Available)
                .ToArray();
            AssemblyLoadInvocationCount = assemblyLoadInvocations.Length;
            AcceptedAssemblyLoadInvocationCount = assemblyLoadInvocations.Count(invocation => invocation != null && invocation.InvocationAccepted);
            BlockedAssemblyLoadInvocationCount = assemblyLoadInvocations.Count(invocation => invocation != null && invocation.GateBlocked);
            OpenAssemblyLoadInvocationCount = assemblyLoadInvocations.Count(invocation => invocation != null && invocation.GateOpen);
            StartedAssemblyLoadInvocationCount = assemblyLoadInvocations.Count(invocation => invocation != null && invocation.InvocationStarted);
            CompletedAssemblyLoadInvocationCount = assemblyLoadInvocations.Count(invocation => invocation != null && invocation.InvocationCompleted);
            LoadedAssemblyLoadInvocationCount = assemblyLoadInvocations.Count(invocation => invocation != null && invocation.AssemblyLoaded);
            FirstBlockedAssemblyLoadInvocation = assemblyLoadInvocations.FirstOrDefault(invocation => invocation != null && invocation.GateBlocked);
            var pluginTypeResolutions = assemblyLoadInvocations
                .Select(invocation => invocation == null ? null : invocation.PluginTypeResolution)
                .Where(resolution => resolution != null && resolution.Available)
                .ToArray();
            PluginTypeResolutionCount = pluginTypeResolutions.Length;
            AcceptedPluginTypeResolutionCount = pluginTypeResolutions.Count(resolution => resolution != null && resolution.ResolutionAccepted);
            BlockedPluginTypeResolutionCount = pluginTypeResolutions.Count(resolution => resolution != null && resolution.GateBlocked);
            OpenPluginTypeResolutionCount = pluginTypeResolutions.Count(resolution => resolution != null && resolution.GateOpen);
            StartedPluginTypeResolutionCount = pluginTypeResolutions.Count(resolution => resolution != null && resolution.ResolutionStarted);
            CompletedPluginTypeResolutionCount = pluginTypeResolutions.Count(resolution => resolution != null && resolution.ResolutionCompleted);
            ResolvedPluginTypeResolutionCount = pluginTypeResolutions.Count(resolution => resolution != null && resolution.PluginTypeResolved);
            FirstBlockedPluginTypeResolution = pluginTypeResolutions.FirstOrDefault(resolution => resolution != null && resolution.GateBlocked);
            var pluginInstanceCreations = pluginTypeResolutions
                .Select(resolution => resolution == null ? null : resolution.PluginInstanceCreation)
                .Where(creation => creation != null && creation.Available)
                .ToArray();
            PluginInstanceCreationCount = pluginInstanceCreations.Length;
            AcceptedPluginInstanceCreationCount = pluginInstanceCreations.Count(creation => creation != null && creation.InstanceCreationAccepted);
            BlockedPluginInstanceCreationCount = pluginInstanceCreations.Count(creation => creation != null && creation.GateBlocked);
            OpenPluginInstanceCreationCount = pluginInstanceCreations.Count(creation => creation != null && creation.GateOpen);
            StartedPluginInstanceCreationCount = pluginInstanceCreations.Count(creation => creation != null && creation.InstanceCreationStarted);
            CompletedPluginInstanceCreationCount = pluginInstanceCreations.Count(creation => creation != null && creation.InstanceCreationCompleted);
            CreatedPluginInstanceCreationCount = pluginInstanceCreations.Count(creation => creation != null && creation.PluginInstanceCreated);
            FirstBlockedPluginInstanceCreation = pluginInstanceCreations.FirstOrDefault(creation => creation != null && creation.GateBlocked);
            var pluginLoadInvocations = pluginInstanceCreations
                .Select(creation => creation == null ? null : creation.PluginLoadInvocation)
                .Where(invocation => invocation != null && invocation.Available)
                .ToArray();
            PluginLoadInvocationCount = pluginLoadInvocations.Length;
            AcceptedPluginLoadInvocationCount = pluginLoadInvocations.Count(invocation => invocation != null && invocation.LoadInvocationAccepted);
            BlockedPluginLoadInvocationCount = pluginLoadInvocations.Count(invocation => invocation != null && invocation.GateBlocked);
            OpenPluginLoadInvocationCount = pluginLoadInvocations.Count(invocation => invocation != null && invocation.GateOpen);
            StartedPluginLoadInvocationCount = pluginLoadInvocations.Count(invocation => invocation != null && invocation.LoadInvocationStarted);
            CompletedPluginLoadInvocationCount = pluginLoadInvocations.Count(invocation => invocation != null && invocation.LoadInvocationCompleted);
            InvokedPluginLoadInvocationCount = pluginLoadInvocations.Count(invocation => invocation != null && invocation.PluginLoadInvoked);
            FirstBlockedPluginLoadInvocation = pluginLoadInvocations.FirstOrDefault(invocation => invocation != null && invocation.GateBlocked);
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
        }

        public int Schema => 1;
        public bool Available { get; }
        public bool ExecutorEnabled { get; }
        public bool RuntimePluginLoadingEnabled { get; }
        public bool AssemblyLoadTransitionRequestEnabled { get; }
        public bool ExecutionGateOpen { get; }
        public bool ExecutionGateBlocked { get; }
        public string StateMachineVersion { get; }
        public string InitialState { get; }
        public string FinalState { get; }
        public bool RollbackRequired { get; }
        public string IsolationBoundary { get; }
        public BepInExPluginActivationPolicySnapshot ActivationPolicy { get; }
        public IReadOnlyList<BepInExPluginInvocationExecutorTransitionSnapshot> Transitions { get; }
        public int TransitionCount { get; }
        public int BlockedTransitionCount { get; }
        public int OpenTransitionCount { get; }
        public int StartedTransitionCount { get; }
        public int CompletedTransitionCount { get; }
        public int FailedIsolatedTransitionCount { get; }
        public BepInExPluginInvocationExecutorTransitionSnapshot FirstBlockedTransition { get; }
        public IReadOnlyList<BepInExPluginInvocationExecutorTransitionRequestSnapshot> TransitionRequests { get; }
        public int TransitionRequestCount { get; }
        public int AcceptedTransitionRequestCount { get; }
        public int BlockedTransitionRequestCount { get; }
        public int OpenTransitionRequestCount { get; }
        public int StartedTransitionRequestCount { get; }
        public int CompletedTransitionRequestCount { get; }
        public int AssemblyLoadedTransitionRequestCount { get; }
        public BepInExPluginInvocationExecutorTransitionRequestSnapshot FirstBlockedTransitionRequest { get; }
        public int AssemblyLoadResolverPlanCount { get; }
        public int AcceptedAssemblyLoadResolverPlanCount { get; }
        public int BlockedAssemblyLoadResolverPlanCount { get; }
        public int OpenAssemblyLoadResolverPlanCount { get; }
        public int StartedAssemblyLoadResolverPlanCount { get; }
        public int LoadedAssemblyLoadResolverPlanCount { get; }
        public BepInExPluginAssemblyLoadResolverPlanSnapshot FirstBlockedAssemblyLoadResolverPlan { get; }
        public int AssemblyLoadOperationCount { get; }
        public int AcceptedAssemblyLoadOperationCount { get; }
        public int BlockedAssemblyLoadOperationCount { get; }
        public int OpenAssemblyLoadOperationCount { get; }
        public int StartedAssemblyLoadOperationCount { get; }
        public int CompletedAssemblyLoadOperationCount { get; }
        public int LoadedAssemblyLoadOperationCount { get; }
        public BepInExPluginAssemblyLoadOperationSnapshot FirstBlockedAssemblyLoadOperation { get; }
        public int AssemblyLoadContextPlanCount { get; }
        public int AcceptedAssemblyLoadContextPlanCount { get; }
        public int BlockedAssemblyLoadContextPlanCount { get; }
        public int OpenAssemblyLoadContextPlanCount { get; }
        public int CreatedAssemblyLoadContextPlanCount { get; }
        public BepInExPluginAssemblyLoadContextPlanSnapshot FirstBlockedAssemblyLoadContextPlan { get; }
        public int AssemblyLoadContextAllocationCount { get; }
        public int AcceptedAssemblyLoadContextAllocationCount { get; }
        public int BlockedAssemblyLoadContextAllocationCount { get; }
        public int OpenAssemblyLoadContextAllocationCount { get; }
        public int ConstructorCalledAssemblyLoadContextAllocationCount { get; }
        public int CreatedAssemblyLoadContextAllocationCount { get; }
        public BepInExPluginAssemblyLoadContextAllocationSnapshot FirstBlockedAssemblyLoadContextAllocation { get; }
        public int AssemblyLoadContextRegistrationCount { get; }
        public int AcceptedAssemblyLoadContextRegistrationCount { get; }
        public int BlockedAssemblyLoadContextRegistrationCount { get; }
        public int OpenAssemblyLoadContextRegistrationCount { get; }
        public int TrackedAssemblyLoadContextRegistrationCount { get; }
        public int UnloadRegisteredAssemblyLoadContextRegistrationCount { get; }
        public BepInExPluginAssemblyLoadContextRegistrationSnapshot FirstBlockedAssemblyLoadContextRegistration { get; }
        public int AssemblyLoadInvocationCount { get; }
        public int AcceptedAssemblyLoadInvocationCount { get; }
        public int BlockedAssemblyLoadInvocationCount { get; }
        public int OpenAssemblyLoadInvocationCount { get; }
        public int StartedAssemblyLoadInvocationCount { get; }
        public int CompletedAssemblyLoadInvocationCount { get; }
        public int LoadedAssemblyLoadInvocationCount { get; }
        public BepInExPluginAssemblyLoadInvocationSnapshot FirstBlockedAssemblyLoadInvocation { get; }
        public int PluginTypeResolutionCount { get; }
        public int AcceptedPluginTypeResolutionCount { get; }
        public int BlockedPluginTypeResolutionCount { get; }
        public int OpenPluginTypeResolutionCount { get; }
        public int StartedPluginTypeResolutionCount { get; }
        public int CompletedPluginTypeResolutionCount { get; }
        public int ResolvedPluginTypeResolutionCount { get; }
        public BepInExPluginTypeResolutionSnapshot FirstBlockedPluginTypeResolution { get; }
        public int PluginInstanceCreationCount { get; }
        public int AcceptedPluginInstanceCreationCount { get; }
        public int BlockedPluginInstanceCreationCount { get; }
        public int OpenPluginInstanceCreationCount { get; }
        public int StartedPluginInstanceCreationCount { get; }
        public int CompletedPluginInstanceCreationCount { get; }
        public int CreatedPluginInstanceCreationCount { get; }
        public BepInExPluginInstanceCreationSnapshot FirstBlockedPluginInstanceCreation { get; }
        public int PluginLoadInvocationCount { get; }
        public int AcceptedPluginLoadInvocationCount { get; }
        public int BlockedPluginLoadInvocationCount { get; }
        public int OpenPluginLoadInvocationCount { get; }
        public int StartedPluginLoadInvocationCount { get; }
        public int CompletedPluginLoadInvocationCount { get; }
        public int InvokedPluginLoadInvocationCount { get; }
        public BepInExPluginLoadInvocationSnapshot FirstBlockedPluginLoadInvocation { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public bool Issue => string.Equals(Status, "CHECK", StringComparison.OrdinalIgnoreCase);

        internal static readonly BepInExPluginInvocationExecutorSnapshot Retired = CreateUnavailable(
            "invocation-executor-retired",
            BepInExPluginCompatibility.RetiredStageMessage);

        internal static BepInExPluginInvocationExecutorSnapshot CreateUnavailable(
            string reasonCode,
            string detail)
        {
            return new BepInExPluginInvocationExecutorSnapshot(
                false,
                false,
                false,
                false,
                false,
                true,
                "openacai.bepinex-stage-executor.v1",
                "unavailable",
                "unavailable",
                false,
                "openacai.metadata-gate",
                BepInExPluginActivationPolicySnapshot.CreateUnavailable(reasonCode, detail),
                Array.Empty<BepInExPluginInvocationExecutorTransitionSnapshot>(),
                Array.Empty<BepInExPluginInvocationExecutorTransitionRequestSnapshot>(),
                "INFO",
                reasonCode,
                detail);
        }
    }
}
