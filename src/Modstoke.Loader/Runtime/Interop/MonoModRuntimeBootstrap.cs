// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Upstream MonoMod.Core ships runtime strategies only through .NET 10. The
// Modstoke net11 fork adds Core110Runtime, so the loader checks that it is
// active before enabling Harmony-driven runtime detours.

using System;
using System.Reflection;

namespace Modstoke.Loader
{
    internal static class MonoModRuntimeBootstrap
    {
        private const string DisableNet11BridgeEnvironmentVariable = "OPENACAI_DISABLE_MONOMOD_NET11_BRIDGE";
        private const string Core110RuntimeName = "MonoMod.Core.Platforms.Runtimes.Core110Runtime";
        private static readonly object SyncRoot = new object();
        private static MonoModRuntimeBridgeState LastState;

        internal static MonoModRuntimeBridgeState EnsureRuntimeBridge()
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

        private static MonoModRuntimeBridgeState TryEnsureRuntimeBridge()
        {
            var runtimeVersion = Environment.Version;
            if (runtimeVersion.Major < 11)
            {
                return MonoModRuntimeBridgeState.NotRequired(
                    runtimeVersion.ToString(),
                    "MonoMod runtime bridge is not required on " + CurrentDotnetLabel() + ".");
            }

            if (RuntimeEntryPoint.IsTruthy(Environment.GetEnvironmentVariable(DisableNet11BridgeEnvironmentVariable)))
            {
                return MonoModRuntimeBridgeState.Failed(
                    runtimeVersion.ToString(),
                    "MonoMod .NET 11 bridge is disabled by " + DisableNet11BridgeEnvironmentVariable + ".");
            }

            try
            {
                var monoModCore = Assembly.Load("MonoMod.Core");
                var core110RuntimeType = monoModCore.GetType(Core110RuntimeName, false);
                if (core110RuntimeType == null)
                {
                    return MonoModRuntimeBridgeState.Failed(
                        runtimeVersion.ToString(),
                        "MonoMod.Core does not expose Core110Runtime. Install the Modstoke MonoMod net11 fork before enabling Harmony patches on " + CurrentDotnetLabel() + ".");
                }

                var platformTripleType = monoModCore.GetType("MonoMod.Core.Platforms.PlatformTriple", true);
                var currentProperty = platformTripleType.GetProperty("Current", BindingFlags.Static | BindingFlags.Public);
                if (currentProperty == null)
                {
                    return MonoModRuntimeBridgeState.Failed(
                        runtimeVersion.ToString(),
                        "MonoMod PlatformTriple.Current is unavailable; the active runtime strategy cannot be validated.");
                }

                // Use MonoMod's synchronized initialization path before caching readiness.
                var triple = currentProperty.GetValue(null);
                return DescribeTriple(runtimeVersion, triple, core110RuntimeType);
            }
            catch (TargetInvocationException ex)
            {
                return MonoModRuntimeBridgeState.Failed(runtimeVersion.ToString(), UnwrapMessage(ex));
            }
            catch (Exception ex)
            {
                return MonoModRuntimeBridgeState.Failed(runtimeVersion.ToString(), ex.Message);
            }
        }

        private static MonoModRuntimeBridgeState DescribeTriple(Version runtimeVersion, object triple, Type expectedRuntimeType)
        {
            var runtime = triple?.GetType().GetProperty("Runtime", BindingFlags.Instance | BindingFlags.Public)?.GetValue(triple);
            var runtimeType = runtime?.GetType();
            var runtimeTypeName = runtimeType?.FullName;
            if (runtimeType == expectedRuntimeType)
            {
                return MonoModRuntimeBridgeState.Applied(
                    runtimeVersion.ToString(),
                    "MonoMod PlatformTriple is initialized with Core110Runtime for " + CurrentDotnetLabel() + ".");
            }

            var isCore100 = runtimeTypeName != null && runtimeTypeName.EndsWith(".Core100Runtime", StringComparison.Ordinal);
            if (isCore100)
            {
                return MonoModRuntimeBridgeState.Failed(
                    runtimeVersion.ToString(),
                    "MonoMod PlatformTriple was initialized with Core100Runtime on " + CurrentDotnetLabel() + ". That runtime uses .NET 10 JIT slots and is not safe for .NET 11 detours.");
            }

            return MonoModRuntimeBridgeState.Failed(
                runtimeVersion.ToString(),
                "MonoMod PlatformTriple does not expose the required active Core110Runtime strategy"
                + (runtimeTypeName == null ? "." : " (" + runtimeTypeName + ")."));
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
    }

    public sealed class MonoModRuntimeBridgeState
    {
        private MonoModRuntimeBridgeState(string runtimeVersion, string status, bool required, bool ready, string detail)
        {
            RuntimeVersion = runtimeVersion;
            Status = status;
            Required = required;
            Ready = ready;
            Detail = detail;
        }

        internal static MonoModRuntimeBridgeState NotRequired(string runtimeVersion, string detail)
        {
            return new MonoModRuntimeBridgeState(runtimeVersion, "not-required", false, true, detail);
        }

        internal static MonoModRuntimeBridgeState Applied(string runtimeVersion, string detail)
        {
            return new MonoModRuntimeBridgeState(runtimeVersion, "ready", true, true, detail);
        }

        internal static MonoModRuntimeBridgeState Failed(string runtimeVersion, string detail)
        {
            return new MonoModRuntimeBridgeState(runtimeVersion, "failed", true, false, detail);
        }

        public string RuntimeVersion { get; }
        public string Status { get; }
        public bool Required { get; }
        public bool Ready { get; }
        public string Detail { get; }
    }
}

