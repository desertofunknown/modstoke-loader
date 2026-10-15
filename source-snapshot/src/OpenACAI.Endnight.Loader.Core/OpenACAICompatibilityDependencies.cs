// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned dependency probe for curated RedLoader/SonsSdk compatibility
// and SDK-support assemblies.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using OpenACAI.Endnight.Loader;

namespace OpenACAI.Loader
{
    public static class OpenACAICompatibilityDependencies
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

        private static OpenACAICompatibilityDependencySnapshot LastSnapshot =
            OpenACAICompatibilityDependencySnapshot.Unknown(ServiceKey);

        public static OpenACAICompatibilityDependencySnapshot Snapshot => CreateState();
        public static bool Ready => Snapshot.AllReady;
        public static IReadOnlyCollection<OpenACAICompatibilityDependencyEntry> Entries => Snapshot.Dependencies;
        public static IReadOnlyCollection<string> Warnings => Snapshot.Warnings;

        public static OpenACAICompatibilityDependencySnapshot Refresh()
        {
            var snapshot = Probe();
            lock (SyncRoot)
            {
                LastSnapshot = snapshot;
            }

            OpenACAIModding.SetService(ServiceKey, snapshot);
            return snapshot;
        }

        internal static OpenACAICompatibilityDependencySnapshot Publish(Action<string> addStartupWarning)
        {
            var snapshot = Refresh();
            foreach (var warning in snapshot.Warnings)
            {
                addStartupWarning?.Invoke(warning);
            }

            return snapshot;
        }

        internal static OpenACAICompatibilityDependencySnapshot CreateState()
        {
            lock (SyncRoot)
            {
                return LastSnapshot;
            }
        }

        private static OpenACAICompatibilityDependencySnapshot Probe()
        {
            var dependencies = RequiredDependencies
                .Select(ProbeDependency)
                .ToArray();
            var warnings = dependencies
                .SelectMany(dependency => dependency.Warnings)
                .ToArray();
            var allPackaged = dependencies.All(dependency => dependency.Packaged);
            var allReady = dependencies.All(dependency => dependency.Ready);

            return new OpenACAICompatibilityDependencySnapshot(
                ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                OpenACAIPaths.RuntimeCompatibilityDirectory,
                OpenACAIPaths.BootstrapMirrorDirectory,
                allPackaged,
                allReady,
                dependencies,
                warnings);
        }

        private static OpenACAICompatibilityDependencyEntry ProbeDependency(DependencyDescriptor descriptor)
        {
            var runtimePath = Path.Combine(OpenACAIPaths.RuntimeCompatibilityDirectory, descriptor.FileName);
            var mirrorPath = Path.Combine(OpenACAIPaths.BootstrapMirrorDirectory, descriptor.FileName);
            var runtime = ProbeFile(runtimePath);
            var mirror = ProbeFile(mirrorPath);
            var loaded = FindLoadedAssembly(descriptor.AssemblyName);
            var loadedPath = SafeAssemblyLocation(loaded);
            var loadedVersion = NormalizeVersion(loaded?.GetName().Version);
            var bestPackagedVersion = MaxVersion(runtime.BestVersion, mirror.BestVersion);
            var bestVersion = MaxVersion(bestPackagedVersion, loadedVersion);
            var present = runtime.Present || mirror.Present || loaded != null;
            var packaged = runtime.Present && mirror.Present;
            var meetsBaseline = MeetsBaseline(bestPackagedVersion, descriptor.RequiredVersion);
            var loadedMeetsBaseline = loaded != null && MeetsBaseline(loadedVersion, descriptor.RequiredVersion);
            var bindingConflict = loaded != null && !loadedMeetsBaseline && meetsBaseline;
            var ready = packaged && meetsBaseline;
            var warnings = new List<string>();

            if (!runtime.Present)
            {
                warnings.Add("OpenACAI compatibility dependency is missing from the runtime compatibility directory: " + descriptor.FileName + ".");
            }

            if (!mirror.Present)
            {
                warnings.Add("OpenACAI compatibility dependency is missing from the bootstrap mirror: " + descriptor.FileName + ".");
            }

            if (present && !meetsBaseline)
            {
                warnings.Add(
                    "OpenACAI compatibility dependency is below the expected version: "
                    + descriptor.AssemblyName + " "
                    + (bestPackagedVersion == null ? "unknown" : VersionToString(bestPackagedVersion))
                    + " < " + VersionToString(descriptor.RequiredVersion) + ".");
            }

            var bindingDetail = bindingConflict
                ? descriptor.FileName + " is packaged at the OpenACAI baseline, but an older loaded assembly is already present from " + loadedPath + "."
                : null;
            var detail = ready
                ? descriptor.FileName + " is packaged for OpenACAI compatibility."
                : descriptor.FileName + " is not fully packaged or is below the expected packaged version.";

            return new OpenACAICompatibilityDependencyEntry(
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
                warnings.ToArray());
        }

        private static ProbedDependencyFile ProbeFile(string path)
        {
            Version assemblyVersion = null;
            string productVersion = null;
            string fileVersion = null;
            var present = false;

            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    present = true;
                    assemblyVersion = AssemblyName.GetAssemblyName(path).Version;
                    var fileInfo = FileVersionInfo.GetVersionInfo(path);
                    productVersion = fileInfo.ProductVersion;
                    fileVersion = fileInfo.FileVersion;
                }
            }
            catch
            {
            }

            var bestVersion = ParseVersion(productVersion) ?? ParseVersion(fileVersion) ?? NormalizeVersion(assemblyVersion);
            return new ProbedDependencyFile(path, present, bestVersion);
        }

        private static Assembly FindLoadedAssembly(string assemblyName)
        {
            try
            {
                return AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return null;
            }
        }

        private static string SafeAssemblyLocation(Assembly assembly)
        {
            try
            {
                return assembly == null || string.IsNullOrWhiteSpace(assembly.Location)
                    ? null
                    : assembly.Location;
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

            var chars = new List<char>(value.Length);
            foreach (var c in value)
            {
                if (char.IsDigit(c) || c == '.')
                {
                    chars.Add(c);
                    continue;
                }

                if (chars.Count > 0)
                {
                    break;
                }
            }

            var cleaned = new string(chars.ToArray()).Trim('.');
            Version version;
            return Version.TryParse(cleaned, out version) ? NormalizeVersion(version) : null;
        }

        private static Version NormalizeVersion(Version version)
        {
            if (version == null)
            {
                return null;
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
            internal ProbedDependencyFile(string path, bool present, Version bestVersion)
            {
                Path = path;
                Present = present;
                BestVersion = bestVersion;
            }

            internal string Path { get; }
            internal bool Present { get; }
            internal Version BestVersion { get; }
        }
    }

    public sealed class OpenACAICompatibilityDependencySnapshot
    {
        internal OpenACAICompatibilityDependencySnapshot(
            string serviceKey,
            string generatedUtc,
            string runtimeCompatibilityDirectory,
            string bootstrapMirrorDirectory,
            bool allPackaged,
            bool allReady,
            IReadOnlyCollection<OpenACAICompatibilityDependencyEntry> dependencies,
            IReadOnlyCollection<string> warnings)
        {
            ServiceKey = serviceKey;
            GeneratedUtc = generatedUtc;
            RuntimeCompatibilityDirectory = runtimeCompatibilityDirectory;
            BootstrapMirrorDirectory = bootstrapMirrorDirectory;
            AllPackaged = allPackaged;
            AllReady = allReady;
            Dependencies = dependencies ?? Array.Empty<OpenACAICompatibilityDependencyEntry>();
            Warnings = warnings ?? Array.Empty<string>();
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
        public IReadOnlyCollection<OpenACAICompatibilityDependencyEntry> Dependencies { get; }
        public IReadOnlyCollection<string> Warnings { get; }

        internal static OpenACAICompatibilityDependencySnapshot Unknown(string serviceKey)
        {
            return new OpenACAICompatibilityDependencySnapshot(
                serviceKey,
                null,
                null,
                null,
                false,
                false,
                Array.Empty<OpenACAICompatibilityDependencyEntry>(),
                Array.Empty<string>());
        }
    }

    public sealed class OpenACAICompatibilityDependencyEntry
    {
        internal OpenACAICompatibilityDependencyEntry(
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
            Warnings = warnings ?? Array.Empty<string>();
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
