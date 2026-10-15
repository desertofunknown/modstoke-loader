// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Built-in public status provider for disclosed OpenACAI protected-runtime
// capability state. Private admin/anti-cheat implementation details stay out
// of the public loader; this provider exposes the stable signals they can use.

using System;
using System.Collections.Generic;
using System.Linq;
using OpenACAI.Loader;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenACAIProtectedRuntimeStatus
    {
        internal static void Initialize()
        {
            if (!OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("OpenACAI protected-runtime status provider could not be registered.");
            }
        }

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            var sections = new List<OpenACAIMenuSectionSnapshot>
            {
                CreateServerPolicySection(),
                CreateRuntimeAttestationSection(),
                CreateTimingSection(),
                CreateFileIntegritySection(),
                CreateRuntimeSection(),
                CreateCompatibilitySection(),
                CreateDependencySection(),
                CreateHarmonySection(),
                CreatePathSection()
            };

            return new OpenACAIMenuContentSnapshot(
                "Protected Runtime",
                "Disclosed Modstoke Loader capability, compatibility, and protected-server readiness signals.",
                sections);
        }

        private static OpenACAIMenuSectionSnapshot CreateRuntimeSection()
        {
            return new OpenACAIMenuSectionSnapshot(
                "Runtime",
                new[]
                {
                    Row(
                        "Loader",
                        OpenACAILoaderCapabilities.LoaderName,
                        OpenACAILoaderCapabilities.IsReady ? "READY" : OpenACAILoaderCapabilities.StartupFailed ? "FAILED" : "PENDING",
                        OpenACAILoaderCapabilities.CompatibilityMode + " v" + OpenACAIEndnightRuntime.Version),
                    Row(
                        "Startup",
                        OpenACAILoaderCapabilities.StartupFailed ? "Failed" : OpenACAILoaderCapabilities.IsReady ? "Ready" : "Starting",
                        OpenACAILoaderCapabilities.StartupFailed ? "FAILED" : OpenACAILoaderCapabilities.IsReady ? "READY" : "PENDING",
                        string.IsNullOrWhiteSpace(OpenACAILoaderCapabilities.StartupError) ? "No startup error recorded." : OpenACAILoaderCapabilities.StartupError),
                    Row(
                        "Public Reflection API",
                        "Exposed",
                        "READY",
                        "OpenACAI public runtime snapshots and service registry are available to mods and companion tools."),
                    Row(
                        "Protected Server Layer",
                        "Disclosed",
                        "READY",
                        "The public loader exposes capability signals only; private admin or anti-cheat payloads are not packaged here."),
                    Row(
                        "Companion Safety",
                        "Public signals",
                        "READY",
                        "Private server/admin tools should consume these public signals instead of depending on hidden loader behavior."),
                    Row(
                        "Hosted RedLoader Mods",
                        OpenACAILoaderCapabilities.HostedRedLoaderModCount.ToString(),
                        OpenACAILoaderCapabilities.HostedRedLoaderModCount > 0 ? "READY" : "INFO",
                        "Legacy RedLoader mod hosting count reported by the OpenACAI compatibility shim."),
                    Row(
                        "Native RedLoader Unity",
                        OpenACAILoaderCapabilities.NativeRedLoaderUnitySupportActive ? "Active" : "OpenACAI driver",
                        OpenACAILoaderCapabilities.NativeRedLoaderUnitySupportActive ? "READY" : "INFO",
                        "When inactive, OpenACAI owns Unity lifecycle dispatch through its runtime driver.")
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateServerPolicySection()
        {
            var rows = new List<OpenACAIMenuRowSnapshot>();
            foreach (var policy in OpenACAIServerPolicy.PoliciesSnapshot())
            {
                rows.Add(Row(
                    policy.Title,
                    policy.RequiredFailureCount == 0 ? "Satisfied" : policy.RequiredFailureCount + " required issue(s)",
                    policy.Status,
                    policy.Detail));

                foreach (var requirement in policy.Requirements)
                {
                    rows.Add(Row(
                        requirement.DisplayName,
                        requirement.Satisfied ? "Satisfied" : "Missing",
                        requirement.Status,
                        (requirement.Required ? "Required. " : "Optional. ") + requirement.Detail));
                }
            }

            if (rows.Count == 0)
            {
                rows.Add(Row(
                    "Server Policy Providers",
                    "None",
                    "INFO",
                    "No server or protected-runtime policy providers are registered."));
            }

            return new OpenACAIMenuSectionSnapshot("Server Policy", rows);
        }

        private static OpenACAIMenuSectionSnapshot CreateRuntimeAttestationSection()
        {
            var snapshot = OpenACAIRuntimeAttestation.Snapshot;
            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                Row(
                    "Attestation",
                    snapshot.Ready ? "Ready" : "Waiting",
                    snapshot.Status,
                    snapshot.Detail),
                Row(
                    "Evidence Digest",
                    ShortHash(snapshot.EvidenceDigestSha256),
                    string.IsNullOrWhiteSpace(snapshot.EvidenceDigestSha256) ? "CHECK" : "READY",
                    snapshot.AttestationToken),
                Row(
                    "Manifest",
                    string.IsNullOrWhiteSpace(snapshot.ManifestVersion) ? "Unavailable" : snapshot.ManifestVersion,
                    snapshot.ManifestsMatch ? "READY" : "FAILED",
                    "Manifest " + ShortHash(snapshot.ManifestSha256) + "; mirror " + ShortHash(snapshot.CompatibilityManifestSha256)),
                Row(
                    "Files",
                    snapshot.VerifiedFileCount + "/" + snapshot.TotalFileCount,
                    snapshot.FileIntegrityReady ? "READY" : "FAILED",
                    snapshot.IssueFileCount + " file issue(s), " + snapshot.CriticalIssueCount + " critical issue(s)."),
                Row(
                    "Dependencies",
                    snapshot.ReadyDependencyCount + "/" + snapshot.DependencyCount,
                    snapshot.CompatibilityDependenciesReady ? "READY" : snapshot.PackagedDependencyCount == snapshot.DependencyCount ? "CHECK" : "FAILED",
                    snapshot.DependencyWarningCount + " warning(s), " + snapshot.DependencyBindingConflictCount + " binding conflict(s)."),
                Row(
                    "Inventory Boundary",
                    snapshot.ModCount + " mods | " + snapshot.LibraryCount + " libs",
                    snapshot.LibrarySeparationReady ? "READY" : "CHECK",
                    snapshot.CompatibilityEntryCount + " compatibility entr" + (snapshot.CompatibilityEntryCount == 1 ? "y" : "ies") + "; hosted " + snapshot.HostedCount + "."),
                Row(
                    "Compatibility Shims",
                    snapshot.ReadyShimCount + "/" + snapshot.CompatibilityShimCount,
                    snapshot.FailedShimCount == 0 ? "READY" : "FAILED",
                    snapshot.FailedShimCount + " enabled shim failure(s).")
            };

            foreach (var dependency in snapshot.Dependencies.Where(dependency => !dependency.Ready || dependency.BindingConflict).Take(5))
            {
                rows.Add(Row(
                    dependency.AssemblyName,
                    string.IsNullOrWhiteSpace(dependency.BestVersion) ? "unknown" : dependency.BestVersion,
                    dependency.Ready ? "CHECK" : "FAILED",
                    "Required " + dependency.RequiredVersion + "; packaged " + dependency.Packaged + "; binding conflict " + dependency.BindingConflict + "."));
            }

            foreach (var shim in snapshot.CompatibilityShims.Where(shim => shim.Failed).Take(5))
            {
                rows.Add(Row(
                    shim.DisplayName,
                    StatusLabel(shim.Status),
                    "FAILED",
                    shim.CompatibilityTarget + " | " + shim.Mode));
            }

            return new OpenACAIMenuSectionSnapshot("Runtime Attestation", rows);
        }

        private static OpenACAIMenuSectionSnapshot CreateTimingSection()
        {
            var snapshot = OpenACAITimings.State;
            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                Row(
                    "Timing Service",
                    snapshot.MetricCount + " metrics | " + snapshot.EventCount + " events",
                    snapshot.RecentEventCount > 0 ? "READY" : "CHECK",
                    "Bounded OpenACAI startup, UI, and runtime timing diagnostics are published for comparison runs."),
                Row(
                    "Process Entry",
                    FormatDuration(snapshot.TimingsStartedAfterProcessStartMs),
                    snapshot.TimingsStartedAfterProcessStartMs.HasValue ? "READY" : "CHECK",
                    "Elapsed from SonsOfTheForest process start to OpenACAI timing initialization. Process: "
                    + (string.IsNullOrWhiteSpace(snapshot.ProcessName) ? "unknown" : snapshot.ProcessName)
                    + "."),
                Row(
                    "Runtime Elapsed",
                    FormatDuration(snapshot.ElapsedSinceStartMs),
                    "INFO",
                    "OpenACAI timing elapsed; process elapsed "
                    + FormatDuration(snapshot.ElapsedSinceProcessStartMs)
                    + ".")
            };

            rows.Add(MetricRow(snapshot, "Modstoke Load", "loader.load-total", "Total managed Modstoke Loader startup duration."));
            rows.Add(MetricRow(snapshot, "State Write", "loader.state.write", "Loader state write duration."));
            rows.Add(MetricRow(snapshot, "Title Mods Attach", "ui.sons-menu-bridge.title.mods-attach-latency", "Title-screen Mods button attach latency after OpenACAI startup."));

            var latest = snapshot.RecentEvents.LastOrDefault();
            if (latest != null)
            {
                rows.Add(Row(
                    "Latest Event",
                    latest.Sequence + " " + latest.Area + "." + latest.Phase,
                    "INFO",
                    FormatDuration(latest.ElapsedSinceStartMs)
                    + " after OpenACAI start; "
                    + FormatDuration(latest.ElapsedSinceProcessStartMs)
                    + " after process start."));
            }

            return new OpenACAIMenuSectionSnapshot("Timing", rows);
        }

        private static OpenACAIMenuSectionSnapshot CreateFileIntegritySection()
        {
            var snapshot = OpenACAIFileIntegrity.Snapshot;
            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                Row(
                    "Manifest",
                    string.IsNullOrWhiteSpace(snapshot.ManifestVersion) ? "Unavailable" : snapshot.ManifestVersion,
                    snapshot.Status,
                    snapshot.Detail),
                Row(
                    "Payload Files",
                    snapshot.VerifiedFileCount + "/" + snapshot.TotalFileCount,
                    snapshot.IssueFileCount == 0 && snapshot.TotalFileCount > 0 ? "READY" : snapshot.Status,
                    "Installed Modstoke Loader files verified against the packaged SHA-256 manifest."),
                Row(
                    "Critical Files",
                    (snapshot.CriticalFileCount - snapshot.CriticalIssueCount) + "/" + snapshot.CriticalFileCount,
                    snapshot.CriticalIssueCount == 0 && snapshot.CriticalFileCount > 0 ? "READY" : "FAILED",
                    "Bootstrap, runtime, hook, and OpenACAI compatibility files that must remain intact."),
                Row(
                    "Manifest Mirror",
                    snapshot.ManifestsMatch ? "Matched" : snapshot.CompatibilityManifestPresent ? "Different" : "Missing",
                    snapshot.ManifestsMatch && snapshot.CompatibilityManifestPresent ? "READY" : "FAILED",
                    "The primary OpenACAI runtime manifest and bootstrap compatibility mirror must match.")
            };

            if (snapshot.IssueEntries.Count == 0)
            {
                rows.Add(Row(
                    "Issues",
                    "None",
                    "READY",
                    "No installed loader file mismatches were reported."));
            }
            else
            {
                rows.Add(Row(
                    "Issues",
                    snapshot.IssueFileCount.ToString(),
                    snapshot.CriticalIssueCount > 0 ? "FAILED" : "CHECK",
                    snapshot.CriticalIssueCount + " critical issue(s), " + snapshot.IssueFileCount + " total issue(s)."));

                foreach (var issue in snapshot.IssueEntries.Take(8))
                {
                    rows.Add(Row(
                        issue.Path,
                        FileIssueValue(issue),
                        issue.Critical ? "FAILED" : issue.Status,
                        issue.Detail));
                }
            }

            return new OpenACAIMenuSectionSnapshot("File Integrity", rows);
        }

        private static OpenACAIMenuSectionSnapshot CreateCompatibilitySection()
        {
            var rows = OpenACAIRuntime.CompatibilityShimsSnapshot()
                .Values
                .OrderBy(shim => shim.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(shim => Row(
                    shim.DisplayName,
                    StatusLabel(shim.Status),
                    RuntimeStatus(shim.Status, shim.Ready, shim.Failed, shim.Enabled),
                    shim.CompatibilityTarget + " | " + shim.Mode + " | " + shim.Detail))
                .ToArray();

            if (rows.Length == 0)
            {
                rows = new[] { Row("Compatibility Shims", "No shims", "PENDING", "OpenACAI runtime shim state has not been published yet.") };
            }

            return new OpenACAIMenuSectionSnapshot("Compatibility", rows);
        }

        private static OpenACAIMenuSectionSnapshot CreateDependencySection()
        {
            var snapshot = OpenACAICompatibilityDependencies.Snapshot;
            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                Row(
                    "Dependency Set",
                    snapshot.AllReady ? "Ready" : snapshot.AllPackaged ? "Packaged" : "Incomplete",
                    snapshot.AllReady ? "READY" : snapshot.AllPackaged ? "CHECK" : "FAILED",
                    "Curated RedLoader/SonsSdk compatibility dependencies packaged under OpenACAI runtime roots.")
            };

            foreach (var dependency in snapshot.Dependencies.OrderBy(dependency => dependency.AssemblyName, StringComparer.OrdinalIgnoreCase))
            {
                rows.Add(Row(
                    dependency.AssemblyName,
                    string.IsNullOrWhiteSpace(dependency.BestVersion) ? "unknown" : dependency.BestVersion,
                    dependency.Ready ? "READY" : dependency.Packaged ? "CHECK" : "FAILED",
                    dependency.BindingConflict
                        ? dependency.BindingDetail
                        : dependency.Purpose + " | required " + dependency.RequiredVersion));
            }

            if (snapshot.Warnings.Count > 0)
            {
                rows.Add(Row("Warnings", snapshot.Warnings.Count.ToString(), "CHECK", string.Join(" ", snapshot.Warnings)));
            }

            return new OpenACAIMenuSectionSnapshot("Compatibility Dependencies", rows);
        }

        private static OpenACAIMenuSectionSnapshot CreateHarmonySection()
        {
            var snapshot = OpenACAIHarmonySupport.CreateState();
            if (snapshot == null)
            {
                return new OpenACAIMenuSectionSnapshot(
                    "Patch Host",
                    new[] { Row("Harmony Patch Host", "Unknown", "PENDING", "HarmonyX/MonoMod support has not published a snapshot yet.") });
            }

            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                Row(
                    "Harmony Patch Host",
                    snapshot.SafeForHarmonyPatches ? "Safe" : "Guarded",
                    snapshot.SafeForHarmonyPatches ? "READY" : "CHECK",
                    snapshot.Detail),
                Row(
                    "Sons Menu Harmony",
                    snapshot.SonsMenuHarmonyEnabled ? "Enabled" : "Disabled",
                    snapshot.SonsMenuHarmonyEnabled ? "READY" : "OFF",
                    "OpenACAI owns title/pause Mods button patches through the Harmony safety gate."),
                Row(
                    "IL2CPP Hook",
                    snapshot.Il2CppHookReady ? "Ready" : "Waiting",
                    snapshot.Il2CppHookReady ? "READY" : "CHECK",
                    "Harmony patches are gated by the published IL2CPP native hook state."),
                Row(
                    "MonoMod Runtime Bridge",
                    snapshot.MonoModRuntimeBridge == null ? "Not required" : snapshot.MonoModRuntimeBridge.Status,
                    snapshot.MonoModRuntimeBridge == null || snapshot.MonoModRuntimeBridge.Ready ? "READY" : "CHECK",
                    snapshot.MonoModRuntimeBridge == null ? "No bridge detail was published." : snapshot.MonoModRuntimeBridge.Detail)
            };

            foreach (var dependency in snapshot.Dependencies.OrderBy(dependency => dependency.AssemblyName, StringComparer.OrdinalIgnoreCase))
            {
                rows.Add(Row(
                    dependency.AssemblyName,
                    string.IsNullOrWhiteSpace(dependency.DetectedVersion) ? "unknown" : dependency.DetectedVersion,
                    dependency.MeetsTestedBaseline ? "READY" : dependency.Present ? "CHECK" : "FAILED",
                    "Tested baseline " + dependency.TestedBaseline));
            }

            return new OpenACAIMenuSectionSnapshot("Patch Host", rows);
        }

        private static OpenACAIMenuSectionSnapshot CreatePathSection()
        {
            var paths = OpenACAILoaderCapabilities.PathsSnapshot;
            var rows = new[]
            {
                PathRow(paths, "OpenACAI Root", "openAcaiRoot"),
                PathRow(paths, "Runtime", "openAcaiRuntimeDirectory"),
                PathRow(paths, "Compatibility", "openAcaiRuntimeCompatibilityDirectory"),
                PathRow(paths, "Modstoke Mods", "openAcaiModsDirectory"),
                PathRow(paths, "User Data", "openAcaiUserDataDirectory"),
                PathRow(paths, "Legacy Mods", "modsDirectory"),
                PathRow(paths, "Legacy Libs", "libsDirectory"),
                PathRow(paths, "File Manifest", "loaderFileManifest"),
                PathRow(paths, "State File", "loaderStateFile")
            };

            return new OpenACAIMenuSectionSnapshot("Runtime Paths", rows);
        }

        private static OpenACAIMenuRowSnapshot MetricRow(OpenACAITimingStateSnapshot snapshot, string label, string metricKey, string detail)
        {
            OpenACAITimingMetricSnapshot metric;
            if (snapshot.Metrics == null || !snapshot.Metrics.TryGetValue(metricKey, out metric))
            {
                return Row(label, "Unavailable", "CHECK", detail);
            }

            return Row(
                label,
                FormatDuration(metric.LastDurationMs),
                "INFO",
                detail
                + " Count "
                + metric.Count
                + "; average "
                + FormatDuration(metric.AverageDurationMs)
                + "; max "
                + FormatDuration(metric.MaxDurationMs)
                + ".");
        }

        private static OpenACAIMenuRowSnapshot PathRow(IReadOnlyDictionary<string, string> paths, string label, string key)
        {
            string value;
            var found = paths.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value);
            return Row(
                label,
                found ? value : "Unavailable",
                found ? "READY" : "CHECK",
                key);
        }

        private static OpenACAIMenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new OpenACAIMenuRowSnapshot(label, value, status, detail);
        }

        private static string FileIssueValue(OpenACAIFileIntegrityEntrySnapshot issue)
        {
            if (!issue.Exists)
            {
                return "Missing";
            }

            if (!issue.SizeMatches)
            {
                return issue.ActualSize.HasValue ? issue.ActualSize.Value + "/" + issue.ExpectedSize + " bytes" : "Size mismatch";
            }

            return issue.HashMatches ? StatusLabel(issue.Status) : "Hash mismatch";
        }

        private static string RuntimeStatus(string status, bool ready, bool failed, bool enabled)
        {
            if (failed)
            {
                return "FAILED";
            }

            if (!enabled || string.Equals(status, OpenACAICompatibilityShimStatus.Disabled, StringComparison.OrdinalIgnoreCase))
            {
                return "OFF";
            }

            if (ready || string.Equals(status, OpenACAICompatibilityShimStatus.Ready, StringComparison.OrdinalIgnoreCase))
            {
                return "READY";
            }

            if (string.Equals(status, OpenACAICompatibilityShimStatus.Warning, StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, OpenACAICompatibilityShimStatus.InventoryOnly, StringComparison.OrdinalIgnoreCase))
            {
                return "CHECK";
            }

            return string.IsNullOrWhiteSpace(status) ? "INFO" : status.Trim().ToUpperInvariant();
        }

        private static string StatusLabel(string status)
        {
            return string.IsNullOrWhiteSpace(status) ? "unknown" : status;
        }

        private static string FormatDuration(double durationMs)
        {
            return durationMs.ToString("0.##") + "ms";
        }

        private static string FormatDuration(double? durationMs)
        {
            return durationMs.HasValue ? FormatDuration(durationMs.Value) : "unavailable";
        }

        private static string ShortHash(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "unavailable";
            }

            return value.Length <= 12 ? value : value.Substring(0, 12);
        }

        private sealed class Provider : IOpenACAIMenuContentProvider
        {
            public string Id => OpenACAIMenuProviderIds.ProtectedRuntime;
            public string DisplayName => "Protected Runtime";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 700;
            public string Source => "Modstoke Loader";
            public string Description => "Show disclosed protected-server and loader capability status.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAIProtectedRuntimeStatus.CreateContent();
            }
        }
    }
}
