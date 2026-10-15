// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned runtime state. Upstream loaders are represented here as
// compatibility shims instead of being treated as the runtime identity.

using System;
using System.Collections.Generic;

namespace OpenACAI.Loader
{
    public static class OpenACAIRuntime
    {
        public const string RuntimeServiceKey = "openacai.loader.runtime";
        public const string CompatibilityShimServiceKey = "openacai.loader.compatibilityShims";

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, OpenACAICompatibilityShimSnapshot> Shims =
            new Dictionary<string, OpenACAICompatibilityShimSnapshot>(StringComparer.OrdinalIgnoreCase);

        private static string _loaderVersion;
        private static string _compatibilityMode;

        public static IReadOnlyDictionary<string, OpenACAICompatibilityShimSnapshot> CompatibilityShims
        {
            get { return CompatibilityShimsSnapshot(); }
        }

        internal static void Initialize(string loaderVersion, string compatibilityMode)
        {
            lock (SyncRoot)
            {
                _loaderVersion = loaderVersion;
                _compatibilityMode = compatibilityMode;
                Shims.Clear();
                SetShimNoLock(OpenACAICompatibilityShimSnapshot.Create(
                    OpenACAICompatibilityShimIds.BepInExPlugins,
                    "BepInEx plugins",
                    "BepInEx",
                    OpenACAICompatibilityShimStatus.Pending,
                    "transitional-bootstrap",
                    true,
                    false,
                    false,
                    "Waiting for OpenACAI bridge startup."));
                SetShimNoLock(OpenACAICompatibilityShimSnapshot.Create(
                    OpenACAICompatibilityShimIds.Il2CppNativeHooks,
                    "IL2CPP native hooks",
                    "BepInEx IL2CPP",
                    OpenACAICompatibilityShimStatus.Pending,
                    "startup-detour",
                    true,
                    false,
                    false,
                    "Waiting for BepInEx IL2CPP native hook status."));
                SetShimNoLock(OpenACAICompatibilityShimSnapshot.Create(
                    OpenACAICompatibilityShimIds.HarmonyPatches,
                    "Harmony patches",
                    "HarmonyX/MonoMod",
                    OpenACAICompatibilityShimStatus.Pending,
                    "dependency-probe",
                    true,
                    false,
                    false,
                    "Waiting for HarmonyX/MonoMod dependency probe."));
                SetShimNoLock(OpenACAICompatibilityShimSnapshot.Create(
                    OpenACAICompatibilityShimIds.RedLoaderMods,
                    "RedLoader mods",
                    "RedLoader",
                    OpenACAICompatibilityShimStatus.Pending,
                    "inventory",
                    true,
                    false,
                    false,
                    "Waiting for RedLoader compatibility discovery."));
                SetShimNoLock(OpenACAICompatibilityShimSnapshot.Create(
                    OpenACAICompatibilityShimIds.SonsSdk,
                    "SonsSdk APIs",
                    "SonsSdk",
                    OpenACAICompatibilityShimStatus.Pending,
                    "compatibility",
                    true,
                    false,
                    false,
                    "Waiting for SonsSdk compatibility mapping."));
                PublishServicesNoLock();
            }
        }

        internal static OpenACAICompatibilityShimSnapshot MarkShim(
            string id,
            string displayName,
            string compatibilityTarget,
            string status,
            string mode,
            bool enabled,
            bool ready,
            bool failed,
            string detail)
        {
            lock (SyncRoot)
            {
                var snapshot = OpenACAICompatibilityShimSnapshot.Create(
                    id,
                    displayName,
                    compatibilityTarget,
                    status,
                    mode,
                    enabled,
                    ready,
                    failed,
                    detail);
                SetShimNoLock(snapshot);
                PublishServicesNoLock();
                return snapshot;
            }
        }

        internal static OpenACAICompatibilityShimSnapshot MarkShimReady(
            string id,
            string displayName,
            string compatibilityTarget,
            string mode,
            string detail)
        {
            return MarkShim(
                id,
                displayName,
                compatibilityTarget,
                OpenACAICompatibilityShimStatus.Ready,
                mode,
                true,
                true,
                false,
                detail);
        }

        internal static OpenACAICompatibilityShimSnapshot MarkShimFailed(
            string id,
            string displayName,
            string compatibilityTarget,
            string mode,
            string detail)
        {
            return MarkShim(
                id,
                displayName,
                compatibilityTarget,
                OpenACAICompatibilityShimStatus.Failed,
                mode,
                true,
                false,
                true,
                detail);
        }

        internal static IReadOnlyDictionary<string, OpenACAICompatibilityShimSnapshot> CompatibilityShimsSnapshot()
        {
            lock (SyncRoot)
            {
                return new Dictionary<string, OpenACAICompatibilityShimSnapshot>(Shims, StringComparer.OrdinalIgnoreCase);
            }
        }

        internal static object CreateState()
        {
            lock (SyncRoot)
            {
                return new
                {
                    schema = 1,
                    loaderVersion = _loaderVersion,
                    compatibilityMode = _compatibilityMode,
                    compatibilityShims = new Dictionary<string, OpenACAICompatibilityShimSnapshot>(Shims, StringComparer.OrdinalIgnoreCase)
                };
            }
        }

        private static void SetShimNoLock(OpenACAICompatibilityShimSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Id))
            {
                return;
            }

            Shims[snapshot.Id] = snapshot;
        }

        private static void PublishServicesNoLock()
        {
            OpenACAIModding.SetService(CompatibilityShimServiceKey, new Dictionary<string, OpenACAICompatibilityShimSnapshot>(Shims, StringComparer.OrdinalIgnoreCase));
        }
    }

    public static class OpenACAICompatibilityShimIds
    {
        public const string BepInExPlugins = "bepinex.plugins";
        public const string Il2CppNativeHooks = "il2cpp.nativeHooks";
        public const string HarmonyPatches = "harmony.patches";
        public const string RedLoaderMods = "redloader.mods";
        public const string SonsSdk = "sons.sdk";
    }

    public static class OpenACAICompatibilityShimStatus
    {
        public const string Pending = "pending";
        public const string Ready = "ready";
        public const string InventoryOnly = "inventory-only";
        public const string Warning = "warning";
        public const string Disabled = "disabled";
        public const string Failed = "failed";
    }

    public sealed class OpenACAICompatibilityShimSnapshot
    {
        private OpenACAICompatibilityShimSnapshot(
            string id,
            string displayName,
            string compatibilityTarget,
            string status,
            string mode,
            bool enabled,
            bool ready,
            bool failed,
            string detail,
            string updatedUtc)
        {
            Id = id;
            DisplayName = displayName;
            CompatibilityTarget = compatibilityTarget;
            Status = status;
            Mode = mode;
            Enabled = enabled;
            Ready = ready;
            Failed = failed;
            Detail = detail;
            UpdatedUtc = updatedUtc;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string CompatibilityTarget { get; }
        public string Status { get; }
        public string Mode { get; }
        public bool Enabled { get; }
        public bool Ready { get; }
        public bool Failed { get; }
        public string Detail { get; }
        public string UpdatedUtc { get; }

        internal static OpenACAICompatibilityShimSnapshot Create(
            string id,
            string displayName,
            string compatibilityTarget,
            string status,
            string mode,
            bool enabled,
            bool ready,
            bool failed,
            string detail)
        {
            return new OpenACAICompatibilityShimSnapshot(
                id,
                displayName,
                compatibilityTarget,
                status,
                mode,
                enabled,
                ready,
                failed,
                detail,
                DateTimeOffset.UtcNow.ToString("O"));
        }
    }
}

