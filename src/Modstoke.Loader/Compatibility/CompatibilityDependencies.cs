// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Modstoke-owned dependency probe for curated RedLoader/SonsSdk compatibility
// and SDK-support assemblies.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Modstoke.Loader;

namespace Modstoke.Loader
{
    public static class CompatibilityDependencies
    {
        public const string ServiceKey = "openacai.loader.compatibilityDependencies";

        private static readonly object SyncRoot = new object();
        private static readonly DependencyDescriptor[] RequiredDependencies =
        {
            new DependencyDescriptor(
                "Tomlet",
                "Tomlet.dll",
                new Version(5, 4, 0, 0),
                "Legacy RedLoader TOML config files"),
            new DependencyDescriptor(
                "SemanticVersioning",
                "SemanticVersioning.dll",
                new Version(3, 0, 0, 0),
                "Legacy RedLoader metadata and version APIs"),
            new DependencyDescriptor(
                "Alt.Json",
                "Alt.Json.dll",
                new Version(1, 0, 0, 0),
                "RedLoader/SonsSdk manifest and SDK helper APIs"),
            new DependencyDescriptor(
                "GLTF",
                "GLTF.dll",
                new Version(0, 8, 6, 0),
                "SonsSdk asset helper APIs")
        };

        private static CompatibilityDependencySnapshot LastSnapshot =
            CompatibilityDependencySnapshot.Unknown(ServiceKey);

        public static CompatibilityDependencySnapshot Snapshot => CreateState();
        public static bool Ready => Snapshot.AllReady;
        public static IReadOnlyCollection<CompatibilityDependencyEntry> Entries => Snapshot.Dependencies;
        public static IReadOnlyCollection<string> Warnings => Snapshot.Warnings;

        public static CompatibilityDependencySnapshot Refresh()
        {
            var snapshot = Probe();
            lock (SyncRoot)
            {
                LastSnapshot = snapshot;
            }

            Modding.SetService(ServiceKey, snapshot);
            return snapshot;
        }

        internal static CompatibilityDependencySnapshot Publish(Action<string> addStartupWarning)
        {
            var snapshot = Refresh();
            foreach (var warning in snapshot.Warnings)
            {
                addStartupWarning?.Invoke(warning);
            }

            return snapshot;
        }

        internal static CompatibilityDependencySnapshot CreateState()
        {
            lock (SyncRoot)
            {
                return LastSnapshot;
            }
        }

        private static CompatibilityDependencySnapshot Probe()
        {
            var warnings = new List<string>();
            var loadedAssemblies = FindLoadedAssemblies(warnings);
            var dependencies = RequiredDependencies
                .Select(descriptor => ProbeDependency(descriptor, loadedAssemblies[descriptor.AssemblyName]))
                .ToArray();
            warnings.AddRange(dependencies.SelectMany(dependency => dependency.Warnings));
            var allPackaged = dependencies.All(dependency => dependency.Packaged);
            var allReady = dependencies.All(dependency => dependency.Ready);

            return new CompatibilityDependencySnapshot(
                ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                LoaderPaths.RuntimeCompatibilityDirectory,
                LoaderPaths.BootstrapMirrorDirectory,
                allPackaged,
                allReady,
                dependencies,
                warnings);
        }

        private static CompatibilityDependencyEntry ProbeDependency(
            DependencyDescriptor descriptor, LoadedDependencyAssembly loaded)
        {
            var runtimePath = Path.Combine(LoaderPaths.RuntimeCompatibilityDirectory, descriptor.FileName);
            var mirrorPath = Path.Combine(LoaderPaths.BootstrapMirrorDirectory, descriptor.FileName);
            var runtime = ProbeFile(runtimePath, descriptor.AssemblyName);
            var mirror = ProbeFile(mirrorPath, descriptor.AssemblyName);
            var loadedPath = loaded?.Path;
            var loadedVersion = loaded?.Version;
            var bestPackagedVersion = MaxVersion(runtime.BestVersion, mirror.BestVersion);
            var bestVersion = MaxVersion(bestPackagedVersion, loadedVersion);
            var present = runtime.Present || mirror.Present || loaded != null;
            var packaged = runtime.Present && mirror.Present;
            // Both locations are resolver inputs; a newer copy cannot validate the other.
            var meetsBaseline = MeetsBaseline(runtime.BestVersion, descriptor.RequiredVersion)
                && MeetsBaseline(mirror.BestVersion, descriptor.RequiredVersion);
            var loadedMeetsBaseline = loaded != null && MeetsBaseline(loadedVersion, descriptor.RequiredVersion);
            var bindingConflict = loaded != null && !loadedMeetsBaseline && meetsBaseline;
            var ready = packaged && meetsBaseline;
            var warnings = new List<string>();

            AddFileWarnings(warnings, descriptor, runtime, "runtime compatibility directory");
            AddFileWarnings(warnings, descriptor, mirror, "bootstrap mirror");

            var bindingDetail = bindingConflict
                ? descriptor.FileName + " is packaged at the Modstoke baseline, but an older loaded assembly is already present from " + loadedPath + "."
                : null;
            var detail = ready
                ? descriptor.FileName + " is packaged for Modstoke compatibility."
                : descriptor.FileName + " is not fully packaged with the expected assembly identity and version in both runtime roots.";

            return new CompatibilityDependencyEntry(
                descriptor.AssemblyName,
                descriptor.FileName,
                descriptor.Purpose,
                VersionToString(descriptor.RequiredVersion),
                present,
                packaged,
                meetsBaseline,
                ready,
                runtime.Path,
                runtime.Present,
                VersionToString(runtime.BestVersion),
                mirror.Path,
                mirror.Present,
                VersionToString(mirror.BestVersion),
                loadedPath,
                loaded != null,
                VersionToString(loadedVersion),
                VersionToString(bestVersion),
                detail,
                loadedMeetsBaseline,
                bindingConflict,
                bindingDetail,
                warnings);
        }

        private static void AddFileWarnings(List<string> warnings, DependencyDescriptor descriptor,
            ProbedDependencyFile file, string location)
        {
            if (!file.Present)
            {
                warnings.Add("Modstoke compatibility dependency is missing from the " + location + ": " + descriptor.FileName + ".");
                return;
            }

            if (file.Issue != null)
            {
                warnings.Add("Modstoke compatibility dependency metadata issue at '" + file.Path + "': " + file.Issue);
                if (file.BestVersion == null)
                    return;
            }

            if (!MeetsBaseline(file.BestVersion, descriptor.RequiredVersion))
            {
                warnings.Add("Modstoke compatibility dependency is below the expected version in the " + location + ": "
                    + descriptor.AssemblyName + " "
                    + (file.BestVersion == null ? "unknown" : VersionToString(file.BestVersion))
                    + " < " + VersionToString(descriptor.RequiredVersion) + ".");
            }
        }

        private static ProbedDependencyFile ProbeFile(string path, string assemblyName)
        {
            Version assemblyVersion = null;
            string productVersion = null;
            string fileVersion = null;
            string issue = null;
            var present = false;

            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    present = true;
                    var identity = AssemblyName.GetAssemblyName(path);
                    if (!string.Equals(identity.Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                        return new ProbedDependencyFile(path, true, null,
                            "Expected assembly '" + assemblyName + "', found '" + identity.Name + "'.");
                    assemblyVersion = identity.Version;
                    var fileInfo = FileVersionInfo.GetVersionInfo(path);
                    productVersion = fileInfo.ProductVersion;
                    fileVersion = fileInfo.FileVersion;
                }
            }
            catch (Exception error)
            {
                issue = "The dependency metadata could not be read (" + error.GetType().Name + ").";
            }

            var bestVersion = ParseVersion(productVersion) ?? ParseVersion(fileVersion) ?? NormalizeVersion(assemblyVersion);
            return new ProbedDependencyFile(path, present, bestVersion, issue);
        }

        private static Dictionary<string, LoadedDependencyAssembly> FindLoadedAssemblies(List<string> warnings)
        {
            var loaded = RequiredDependencies.ToDictionary(
                descriptor => descriptor.AssemblyName, _ => (LoadedDependencyAssembly)null, StringComparer.OrdinalIgnoreCase);
            Assembly[] assemblies;
            try
            {
                assemblies = AppDomain.CurrentDomain.GetAssemblies();
            }
            catch (Exception error)
            {
                warnings.Add("Modstoke could not enumerate loaded compatibility dependencies (" + error.GetType().Name + ").");
                return loaded;
            }

            var remaining = loaded.Count;
            foreach (var assembly in assemblies)
            {
                try
                {
                    var identity = assembly.GetName();
                    if (!string.IsNullOrEmpty(identity.Name) && loaded.TryGetValue(identity.Name, out var previous) && previous == null)
                    {
                        loaded[identity.Name] = new LoadedDependencyAssembly(
                            SafeAssemblyLocation(assembly), NormalizeVersion(identity.Version));
                        if (--remaining == 0)
                            break;
                    }
                }
                catch (Exception error)
                {
                    warnings.Add("Modstoke could not inspect a loaded assembly for compatibility dependencies (" + error.GetType().Name + ").");
                }
            }

            return loaded;
        }

        private static string SafeAssemblyLocation(Assembly assembly)
        {
            try
            {
                var location = assembly?.Location;
                return string.IsNullOrWhiteSpace(location) ? null : location;
            }
            catch
            {
                return null;
            }
        }

        private static Version MaxVersion(params Version[] versions)
        {
            Version result = null;
            foreach (var version in versions)
            {
                var normalized = NormalizeVersion(version);
                if (normalized == null)
                {
                    continue;
                }

                if (result == null || normalized.CompareTo(result) > 0)
                {
                    result = normalized;
                }
            }

            return result;
        }

        private static bool MeetsBaseline(Version version, Version requiredVersion)
        {
            var normalized = NormalizeVersion(version);
            var normalizedRequired = NormalizeVersion(requiredVersion);
            return normalized != null
                && normalizedRequired != null
                && normalized.CompareTo(normalizedRequired) >= 0;
        }

        private static Version ParseVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var start = 0;
            while (start < value.Length && !char.IsDigit(value[start]) && value[start] != '.')
            {
                start++;
            }

            var end = start;
            while (end < value.Length && (char.IsDigit(value[end]) || value[end] == '.'))
            {
                end++;
            }

            var cleaned = value.AsSpan(start, end - start).Trim('.');
            Version version;
            return Version.TryParse(cleaned, out version) ? NormalizeVersion(version) : null;
        }

        private static Version NormalizeVersion(Version version)
        {
            if (version == null || (version.Build >= 0 && version.Revision >= 0))
            {
                return version;
            }

            return new Version(
                Math.Max(version.Major, 0),
                Math.Max(version.Minor, 0),
                Math.Max(version.Build, 0),
                Math.Max(version.Revision, 0));
        }

        private static string VersionToString(Version version)
        {
            var normalized = NormalizeVersion(version);
            return normalized == null ? null : normalized.ToString();
        }

        private sealed class DependencyDescriptor
        {
            internal DependencyDescriptor(string assemblyName, string fileName, Version requiredVersion, string purpose)
            {
                AssemblyName = assemblyName;
                FileName = fileName;
                RequiredVersion = requiredVersion;
                Purpose = purpose;
            }

            internal string AssemblyName { get; }
            internal string FileName { get; }
            internal Version RequiredVersion { get; }
            internal string Purpose { get; }
        }

        private sealed class ProbedDependencyFile
        {
            internal ProbedDependencyFile(string path, bool present, Version bestVersion, string issue)
            {
                Path = path;
                Present = present;
                BestVersion = bestVersion;
                Issue = issue;
            }

            internal string Path { get; }
            internal bool Present { get; }
            internal Version BestVersion { get; }
            internal string Issue { get; }
        }

        private sealed class LoadedDependencyAssembly
        {
            internal LoadedDependencyAssembly(string path, Version version)
            {
                Path = path;
                Version = version;
            }

            internal string Path { get; }
            internal Version Version { get; }
        }
    }

    public sealed class CompatibilityDependencySnapshot
    {
        internal CompatibilityDependencySnapshot(
            string serviceKey,
            string generatedUtc,
            string runtimeCompatibilityDirectory,
            string bootstrapMirrorDirectory,
            bool allPackaged,
            bool allReady,
            IReadOnlyCollection<CompatibilityDependencyEntry> dependencies,
            IReadOnlyCollection<string> warnings)
        {
            ServiceKey = serviceKey;
            GeneratedUtc = generatedUtc;
            RuntimeCompatibilityDirectory = runtimeCompatibilityDirectory;
            BootstrapMirrorDirectory = bootstrapMirrorDirectory;
            AllPackaged = allPackaged;
            AllReady = allReady;
            Dependencies = Array.AsReadOnly((dependencies ?? Array.Empty<CompatibilityDependencyEntry>()).ToArray());
            Warnings = Array.AsReadOnly((warnings ?? Array.Empty<string>()).ToArray());
            DependencyCount = Dependencies.Count;
            ReadyDependencyCount = Dependencies.Count(dependency => dependency.Ready);
            PackagedDependencyCount = Dependencies.Count(dependency => dependency.Packaged);
            WarningCount = Warnings.Count;
            BindingConflictCount = Dependencies.Count(dependency => dependency.BindingConflict);
        }

        public string ServiceKey { get; }
        public string GeneratedUtc { get; }
        public string RuntimeCompatibilityDirectory { get; }
        public string BootstrapMirrorDirectory { get; }
        public bool AllPackaged { get; }
        public bool AllReady { get; }
        public int DependencyCount { get; }
        public int ReadyDependencyCount { get; }
        public int PackagedDependencyCount { get; }
        public int WarningCount { get; }
        public int BindingConflictCount { get; }
        public IReadOnlyCollection<CompatibilityDependencyEntry> Dependencies { get; }
        public IReadOnlyCollection<string> Warnings { get; }

        internal static CompatibilityDependencySnapshot Unknown(string serviceKey)
        {
            return new CompatibilityDependencySnapshot(
                serviceKey,
                null,
                null,
                null,
                false,
                false,
                Array.Empty<CompatibilityDependencyEntry>(),
                Array.Empty<string>());
        }
    }

    public sealed class CompatibilityDependencyEntry
    {
        internal CompatibilityDependencyEntry(
            string assemblyName,
            string fileName,
            string purpose,
            string requiredVersion,
            bool present,
            bool packaged,
            bool meetsBaseline,
            bool ready,
            string runtimePath,
            bool runtimePresent,
            string runtimeVersion,
            string bootstrapMirrorPath,
            bool bootstrapMirrorPresent,
            string bootstrapMirrorVersion,
            string loadedPath,
            bool loaded,
            string loadedVersion,
            string bestVersion,
            string detail,
            bool loadedMeetsBaseline,
            bool bindingConflict,
            string bindingDetail,
            IReadOnlyCollection<string> warnings)
        {
            AssemblyName = assemblyName;
            FileName = fileName;
            Purpose = purpose;
            RequiredVersion = requiredVersion;
            Present = present;
            Packaged = packaged;
            MeetsBaseline = meetsBaseline;
            Ready = ready;
            RuntimePath = runtimePath;
            RuntimePresent = runtimePresent;
            RuntimeVersion = runtimeVersion;
            BootstrapMirrorPath = bootstrapMirrorPath;
            BootstrapMirrorPresent = bootstrapMirrorPresent;
            BootstrapMirrorVersion = bootstrapMirrorVersion;
            LoadedPath = loadedPath;
            Loaded = loaded;
            LoadedVersion = loadedVersion;
            BestVersion = bestVersion;
            Detail = detail;
            LoadedMeetsBaseline = loadedMeetsBaseline;
            BindingConflict = bindingConflict;
            BindingDetail = bindingDetail;
            Warnings = Array.AsReadOnly((warnings ?? Array.Empty<string>()).ToArray());
        }

        public string AssemblyName { get; }
        public string FileName { get; }
        public string Purpose { get; }
        public string RequiredVersion { get; }
        public bool Present { get; }
        public bool Packaged { get; }
        public bool MeetsBaseline { get; }
        public bool Ready { get; }
        public string RuntimePath { get; }
        public bool RuntimePresent { get; }
        public string RuntimeVersion { get; }
        public string BootstrapMirrorPath { get; }
        public bool BootstrapMirrorPresent { get; }
        public string BootstrapMirrorVersion { get; }
        public string LoadedPath { get; }
        public bool Loaded { get; }
        public string LoadedVersion { get; }
        public string BestVersion { get; }
        public string Detail { get; }
        public bool LoadedMeetsBaseline { get; }
        public bool BindingConflict { get; }
        public string BindingDetail { get; }
        public IReadOnlyCollection<string> Warnings { get; }
    }
}
