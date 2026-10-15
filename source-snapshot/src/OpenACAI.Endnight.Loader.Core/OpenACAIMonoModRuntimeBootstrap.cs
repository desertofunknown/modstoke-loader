// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// MonoMod 1.3.4 ships runtime strategies through .NET 10. OpenACAI's
// net11 fork adds a real Core110Runtime, so the loader validates that support
// before enabling Harmony-driven runtime detours.

using System;
using System.Reflection;

namespace OpenACAI.Loader
{
    internal static class OpenACAIMonoModRuntimeBootstrap
    {
        private const string DisableNet11BridgeEnvironmentVariable = "OPENACAI_DISABLE_MONOMOD_NET11_BRIDGE";
        private const string Core110RuntimeName = "MonoMod.Core.Platforms.Runtimes.Core110Runtime";
        private static readonly object SyncRoot = new object();
        private static OpenACAIMonoModRuntimeBridgeState LastState;

        internal static OpenACAIMonoModRuntimeBridgeState EnsureRuntimeBridge()
        {
            lock (SyncRoot)
            {
                if (LastState != null)
                {
                    return LastState;
                }

                LastState = TryEnsureRuntimeBridge();
                return LastState;
            }
        }

        private static OpenACAIMonoModRuntimeBridgeState TryEnsureRuntimeBridge()
        {
            var runtimeVersion = Environment.Version;
            if (runtimeVersion.Major < 11)
            {
                return OpenACAIMonoModRuntimeBridgeState.NotRequired(
                    runtimeVersion.ToString(),
                    "MonoMod runtime bridge is not required on " + CurrentDotnetLabel() + ".");
            }

            if (IsTruthy(Environment.GetEnvironmentVariable(DisableNet11BridgeEnvironmentVariable)))
            {
                return OpenACAIMonoModRuntimeBridgeState.Failed(
                    runtimeVersion.ToString(),
                    "MonoMod .NET 11 bridge is disabled by " + DisableNet11BridgeEnvironmentVariable + ".");
            }

            try
            {
                var monoModCore = FindOrLoadAssembly("MonoMod.Core");
                if (monoModCore == null)
                {
                    return OpenACAIMonoModRuntimeBridgeState.Failed(
                        runtimeVersion.ToString(),
                        "MonoMod.Core was not loaded and could not be resolved.");
                }

                var core110RuntimeType = monoModCore.GetType(Core110RuntimeName, false);
                if (core110RuntimeType == null)
                {
                    return OpenACAIMonoModRuntimeBridgeState.Failed(
                        runtimeVersion.ToString(),
                        "MonoMod.Core does not expose Core110Runtime. Install the OpenACAI MonoMod net11 fork before enabling Harmony patches on " + CurrentDotnetLabel() + ".");
                }

                var platformTripleType = monoModCore.GetType("MonoMod.Core.Platforms.PlatformTriple", true);
                var lazyCurrentField = platformTripleType.GetField("lazyCurrent", BindingFlags.Static | BindingFlags.NonPublic);
                var existingTriple = lazyCurrentField == null ? null : lazyCurrentField.GetValue(null);
                if (existingTriple != null)
                {
                    return DescribeExistingTriple(runtimeVersion, existingTriple);
                }

                return OpenACAIMonoModRuntimeBridgeState.Applied(
                    runtimeVersion.ToString(),
                    "MonoMod.Core exposes Core110Runtime for " + CurrentDotnetLabel() + "; PlatformTriple will initialize through MonoMod's native path.");
            }
            catch (TargetInvocationException ex)
            {
                return OpenACAIMonoModRuntimeBridgeState.Failed(runtimeVersion.ToString(), UnwrapMessage(ex));
            }
            catch (Exception ex)
            {
                return OpenACAIMonoModRuntimeBridgeState.Failed(runtimeVersion.ToString(), ex.Message);
            }
        }

        private static OpenACAIMonoModRuntimeBridgeState DescribeExistingTriple(Version runtimeVersion, object triple)
        {
            string runtimeTypeName = null;
            try
            {
                runtimeTypeName = triple.GetType().GetProperty("Runtime", BindingFlags.Instance | BindingFlags.Public)?.GetValue(triple)?.GetType().FullName;
            }
            catch
            {
            }

            var isCore110 = runtimeTypeName != null && runtimeTypeName.EndsWith(".Core110Runtime", StringComparison.Ordinal);
            if (isCore110)
            {
                return OpenACAIMonoModRuntimeBridgeState.Applied(
                    runtimeVersion.ToString(),
                    "MonoMod PlatformTriple was already initialized with Core110Runtime for " + CurrentDotnetLabel() + ".");
            }

            var isCore100 = runtimeTypeName != null && runtimeTypeName.EndsWith(".Core100Runtime", StringComparison.Ordinal);
            if (isCore100)
            {
                return OpenACAIMonoModRuntimeBridgeState.Failed(
                    runtimeVersion.ToString(),
                    "MonoMod PlatformTriple was initialized with Core100Runtime on " + CurrentDotnetLabel() + ". That runtime uses .NET 10 JIT slots and is not safe for .NET 11 detours.");
            }

            return OpenACAIMonoModRuntimeBridgeState.Failed(
                runtimeVersion.ToString(),
                "MonoMod PlatformTriple was already initialized before OpenACAI could apply the .NET 11 bridge"
                + (runtimeTypeName == null ? "." : " (" + runtimeTypeName + ")."));
        }

        private static Assembly FindOrLoadAssembly(string assemblyName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    return assembly;
                }
            }

            try
            {
                return Assembly.Load(assemblyName);
            }
            catch
            {
                return null;
            }
        }

        private static string UnwrapMessage(TargetInvocationException ex)
        {
            var inner = ex.InnerException;
            while (inner is TargetInvocationException nested && nested.InnerException != null)
            {
                inner = nested.InnerException;
            }

            return inner == null ? ex.Message : inner.GetType().Name + ": " + inner.Message;
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

    public sealed class OpenACAIMonoModRuntimeBridgeState
    {
        private OpenACAIMonoModRuntimeBridgeState(string runtimeVersion, string status, bool required, bool ready, string detail)
        {
            RuntimeVersion = runtimeVersion;
            Status = status;
            Required = required;
            Ready = ready;
            Detail = detail;
        }

        internal static OpenACAIMonoModRuntimeBridgeState NotRequired(string runtimeVersion, string detail)
        {
            return new OpenACAIMonoModRuntimeBridgeState(runtimeVersion, "not-required", false, true, detail);
        }

        internal static OpenACAIMonoModRuntimeBridgeState Applied(string runtimeVersion, string detail)
        {
            return new OpenACAIMonoModRuntimeBridgeState(runtimeVersion, "ready", true, true, detail);
        }

        internal static OpenACAIMonoModRuntimeBridgeState Failed(string runtimeVersion, string detail)
        {
            return new OpenACAIMonoModRuntimeBridgeState(runtimeVersion, "failed", true, false, detail);
        }

        public string RuntimeVersion { get; }
        public string Status { get; }
        public bool Required { get; }
        public bool Ready { get; }
        public string Detail { get; }
    }
}

