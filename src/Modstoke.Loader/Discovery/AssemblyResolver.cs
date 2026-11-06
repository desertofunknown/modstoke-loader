// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Managed assembly resolver for native Modstoke packages and legacy
// compatibility shims.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;

namespace Modstoke.Loader
{
    internal static class AssemblyResolver
    {
        private const string ReservedCompanionDirectoryReason = "Reserved Modstoke companion directory is not part of the public loader resolver list.";

        private static readonly object SearchDirectoriesLock = new object();
        private static volatile ResolverSearchDirectory[] _searchDirectoriesSnapshot = Array.Empty<ResolverSearchDirectory>();
        private static readonly List<ResolverSearchDirectory> SearchDirectories = new List<ResolverSearchDirectory>();
        private static readonly List<ResolverSkippedDirectory> SkippedDirectories = new List<ResolverSkippedDirectory>();
        private static readonly HashSet<string> SearchDirectoryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> RedLoaderDependencyDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Dependencies",
            "Deps",
            "Lib",
            "Libs",
            "Libraries",
            "Managed",
            "Mods"
        };

        private static ManualLogSource _log;
        private static readonly char[] InvalidAssemblyNameCharacters = Path.GetInvalidFileNameChars();
        private static bool _installed;
        [ThreadStatic]
        private static HashSet<string> _resolvingNames;

        internal static void Install(ManualLogSource log)
        {
            int searchDirectoryCount;
            lock (SearchDirectoriesLock)
            {
                if (_installed)
                {
                    return;
                }

                _log = log;
                SearchDirectories.Clear();
                SkippedDirectories.Clear();
                SearchDirectoryKeys.Clear();

                Add("openacai.bridge", Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
                Add("openacai.runtime", LoaderPaths.RuntimeDirectory);
                Add("openacai.runtime-compatibility", LoaderPaths.RuntimeCompatibilityDirectory);
                Add("openacai.bootstrap-mirror", LoaderPaths.BootstrapMirrorDirectory);
                Add("bepinex.core", Path.Combine(Paths.BepInExRootPath, "core"));
                Add("bepinex.interop", Path.Combine(Paths.BepInExRootPath, "interop"));
                Add("bepinex.unity-libs", Path.Combine(Paths.BepInExRootPath, "unity-libs"));
                Add("unity.managed", Paths.ManagedPath);
                Add("openacai.mods-root", LoaderPaths.ModsDirectory);
                Add("redloader.libs", Path.Combine(Paths.GameRootPath, "Libs"));
                Add("redloader.mods-root", Path.Combine(Paths.GameRootPath, "Mods"));
                Add("redloader.legacy-root", Path.Combine(Paths.GameRootPath, "_RedLoader"));
                Add("redloader.legacy-net6", Path.Combine(Paths.GameRootPath, "_RedLoader", "net6"));
                Add("redloader.legacy-root-altcase", Path.Combine(Paths.GameRootPath, "_Redloader"));
                Add("redloader.legacy-net6-altcase", Path.Combine(Paths.GameRootPath, "_Redloader", "net6"));
                AddSubdirectories("openacai.runtime-subdir", LoaderPaths.RuntimeDirectory, 1);
                AddSubdirectories("openacai.bootstrap-mirror-subdir", LoaderPaths.BootstrapMirrorDirectory, 1);
                AddSubdirectories("openacai.mod-package", LoaderPaths.ModsDirectory, 2);
                AddRedLoaderPackageDirectories(Path.Combine(Paths.GameRootPath, "Mods"));
                _searchDirectoriesSnapshot = SearchDirectories.ToArray();
                searchDirectoryCount = _searchDirectoriesSnapshot.Length;
                AppDomain.CurrentDomain.AssemblyResolve += Resolve;
                _installed = true;
            }

            log?.LogDebug("Modstoke assembly resolver installed with " + searchDirectoryCount + " search directories.");
        }

        internal static void AddPluginDirectories(IEnumerable<string> directories)
        {
            lock (SearchDirectoriesLock)
            {
                foreach (var directory in directories)
                {
                    Add("bepinex.dynamic-plugin", directory);
                }

                _searchDirectoriesSnapshot = SearchDirectories.ToArray();
            }
        }

        internal static string[] SearchDirectoriesSnapshot()
        {
            var directories = _searchDirectoriesSnapshot;
            return directories.Select(entry => entry.Path).ToArray();
        }

        internal static bool TryFindAssemblyPath(string requestedName, string requestingDirectory, out string path, out string source)
        {
            var directories = _searchDirectoriesSnapshot;
            path = null;
            source = null;
            if (!IsSimpleAssemblyName(requestedName))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(requestingDirectory)
                && TryFindAssemblyPathInDirectory(requestingDirectory, "requesting-assembly", requestedName, true, out path, out source))
            {
                return true;
            }

            foreach (var directory in directories)
            {
                if (TryFindAssemblyPathInDirectory(directory.Path, directory.Source, requestedName, false, out path, out source))
                {
                    return true;
                }
            }

            return false;
        }

        internal static object CreateState()
        {
            lock (SearchDirectoriesLock)
            {
                return new
                {
                    schema = 1,
                    installed = _installed,
                    owner = "Modstoke Loader",
                    runtimeRoot = LoaderPaths.RuntimeDirectory,
                    nativePackageRoot = LoaderPaths.ModsDirectory,
                    legacyCompatibilityRoots = new[]
                    {
                        Path.Combine(Paths.GameRootPath, "Mods"),
                        Path.Combine(Paths.GameRootPath, "Libs"),
                        LoaderPaths.BootstrapMirrorDirectory,
                        Paths.PluginPath
                    },
                    searchDirectoryCount = SearchDirectories.Count,
                    searchDirectories = SearchDirectories.Select(entry => entry.CreateState()).ToArray(),
                    skippedDirectoryCount = SkippedDirectories.Count,
                    skippedDirectories = SkippedDirectories.Select(entry => entry.CreateState()).ToArray()
                };
            }
        }

        private static bool Add(string source, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string fullPath;
            try
            {
                fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            catch (Exception ex)
            {
                SkippedDirectories.Add(new ResolverSkippedDirectory(path, source, "invalid-path", ex.Message));
                return false;
            }

            if (!Directory.Exists(fullPath))
            {
                SkippedDirectories.Add(new ResolverSkippedDirectory(fullPath, source, "missing", "Directory does not exist."));
                return false;
            }

            var excludedReason = GetResolverPathExclusionReason(fullPath);
            if (!string.IsNullOrWhiteSpace(excludedReason))
            {
                SkippedDirectories.Add(new ResolverSkippedDirectory(fullPath, source, "excluded", excludedReason));
                return false;
            }

            if (SearchDirectoryKeys.Add(fullPath))
            {
                SearchDirectories.Add(new ResolverSearchDirectory(SearchDirectories.Count, source, fullPath));
                return true;
            }

            return false;
        }

        private static void AddSubdirectories(string source, string root, int maxDepth)
        {
            if (string.IsNullOrWhiteSpace(root) || maxDepth <= 0)
            {
                return;
            }

            string fullRoot;
            try
            {
                fullRoot = Path.GetFullPath(root);
            }
            catch (Exception ex)
            {
                SkippedDirectories.Add(new ResolverSkippedDirectory(root, source, "invalid-path", ex.Message));
                return;
            }

            if (!Directory.Exists(fullRoot))
            {
                SkippedDirectories.Add(new ResolverSkippedDirectory(fullRoot, source, "missing", "Directory does not exist."));
                return;
            }

            var excludedReason = GetResolverPathExclusionReason(fullRoot);
            if (!string.IsNullOrWhiteSpace(excludedReason))
            {
                SkippedDirectories.Add(new ResolverSkippedDirectory(fullRoot, source, "excluded", excludedReason));
                return;
            }

            foreach (var directory in EnumerateDirectoriesSafe(fullRoot, source))
            {
                // Add records excluded children as skipped; only included ones are searched deeper.
                Add(source, directory);
                if (maxDepth > 1 && string.IsNullOrWhiteSpace(GetResolverPathExclusionReason(directory)))
                {
                    AddSubdirectories(source, directory, maxDepth - 1);
                }
            }
        }

        private static void AddRedLoaderPackageDirectories(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            string fullRoot;
            try
            {
                fullRoot = Path.GetFullPath(root);
            }
            catch (Exception ex)
            {
                SkippedDirectories.Add(new ResolverSkippedDirectory(root, "redloader.mod-package", "invalid-path", ex.Message));
                return;
            }

            if (!Directory.Exists(fullRoot))
            {
                SkippedDirectories.Add(new ResolverSkippedDirectory(fullRoot, "redloader.mod-package", "missing", "Directory does not exist."));
                return;
            }

            foreach (var directory in EnumerateDirectoriesSafe(fullRoot, "redloader.mod-package"))
            {
                AddRedLoaderPackageDirectory(directory);
            }
        }

        private static void AddRedLoaderPackageDirectory(string packageDirectory)
        {
            var excludedReason = GetResolverPathExclusionReason(packageDirectory);
            if (!string.IsNullOrWhiteSpace(excludedReason))
            {
                Add("redloader.mod-package", packageDirectory);
                return;
            }

            if (!IsLikelyRedLoaderPackageDirectory(packageDirectory))
            {
                SkippedDirectories.Add(new ResolverSkippedDirectory(
                    SafeFullPath(packageDirectory),
                    "redloader.mod-package",
                    "non-package",
                    "No RedLoader manifest or assembly-shaped dependency folder was detected."));
                return;
            }

            Add("redloader.mod-package", packageDirectory);
            AddRedLoaderDependencyDirectories(packageDirectory);
        }

        private static void AddRedLoaderDependencyDirectories(string packageDirectory)
        {
            foreach (var directory in EnumerateDirectoriesSafe(packageDirectory, "redloader.mod-dependency"))
            {
                var excludedReason = GetResolverPathExclusionReason(directory);
                if (!string.IsNullOrWhiteSpace(excludedReason))
                {
                    Add("redloader.mod-dependency", directory);
                    continue;
                }

                var name = Path.GetFileName(directory);
                if (!RedLoaderDependencyDirectoryNames.Contains(name))
                {
                    if (HasTopLevelDll(directory))
                    {
                        Add("redloader.mod-dependency", directory);
                    }

                    continue;
                }

                if (HasTopLevelDll(directory))
                {
                    Add("redloader.mod-dependency", directory);
                }

                foreach (var nested in EnumerateDirectoriesSafe(directory, "redloader.mod-dependency"))
                {
                    if (HasTopLevelDll(nested))
                    {
                        Add("redloader.mod-dependency", nested);
                    }
                }
            }
        }

        private static bool IsLikelyRedLoaderPackageDirectory(string packageDirectory)
        {
            if (File.Exists(Path.Combine(packageDirectory, "manifest.json")) || HasTopLevelDll(packageDirectory))
            {
                return true;
            }

            foreach (var directory in EnumerateDirectoriesSafe(packageDirectory, "redloader.mod-package"))
            {
                var name = Path.GetFileName(directory);
                if (RedLoaderDependencyDirectoryNames.Contains(name) && HasTopLevelDll(directory))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasTopLevelDll(string directory)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(directory)
                    && Directory.Exists(directory)
                    && Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly).Any();
            }
            catch (Exception ex)
            {
                SkippedDirectories.Add(new ResolverSkippedDirectory(SafeFullPath(directory), "redloader.mod-package", "enumeration-failed", ex.Message));
                return false;
            }
        }

        private static IEnumerable<string> EnumerateDirectoriesSafe(string directory, string source)
        {
            try
            {
                return Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex)
            {
                SkippedDirectories.Add(new ResolverSkippedDirectory(SafeFullPath(directory), source, "enumeration-failed", ex.Message));
                return Array.Empty<string>();
            }
        }

        private static string SafeFullPath(string path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        private static string GetResolverPathExclusionReason(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            string reservedReason = null;
            string previous = null;
            foreach (var segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (string.Equals(segment, "_Disabled", StringComparison.OrdinalIgnoreCase))
                {
                    return "Disabled mod/plugin directory.";
                }

                // The native installer keeps replaced files and queued archives here; none of them may load.
                if (string.Equals(segment, "native-history", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(segment, "pending", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(previous, "Runtime", StringComparison.OrdinalIgnoreCase))
                {
                    return "Native installer history or queued change.";
                }
                previous = segment;

                if (string.Equals(segment, ".vs", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase))
                {
                    return "Development metadata/build output directory.";
                }

                if (string.Equals(segment, "OpenACAIAdminTool", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(segment, "OpenACAIAdmin", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(segment, "OpenACAIAntiCheat", StringComparison.OrdinalIgnoreCase))
                {
                    reservedReason = ReservedCompanionDirectoryReason;
                }
            }

            return reservedReason;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            var directories = _searchDirectoriesSnapshot;
            AssemblyName requestedIdentity;
            try
            {
                requestedIdentity = new AssemblyName(args.Name);
            }
            catch
            {
                return null;
            }

            var requestedName = requestedIdentity.Name;
            if (!IsSimpleAssemblyName(requestedName))
            {
                return null;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (MatchesRequestedIdentity(requestedIdentity, assembly.GetName()))
                {
                    return assembly;
                }
            }

            var resolvingNames = _resolvingNames ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!resolvingNames.Add(requestedName))
                return null;
            try
            {
                return ResolveFromDirectories(requestedIdentity, args.RequestingAssembly, directories);
            }
            finally
            {
                resolvingNames.Remove(requestedName);
            }
        }

        private static Assembly ResolveFromDirectories(AssemblyName requestedIdentity, Assembly requestingAssembly, ResolverSearchDirectory[] directories)
        {
            var requestingDirectory = SafeAssemblyDirectory(requestingAssembly);
            if (!string.IsNullOrWhiteSpace(requestingDirectory))
            {
                var resolved = TryLoadFromDirectory(requestingDirectory, "requesting-assembly", requestedIdentity, true);
                if (resolved != null)
                {
                    return resolved;
                }
            }

            foreach (var directory in directories)
            {
                var resolved = TryLoadFromDirectory(directory.Path, directory.Source, requestedIdentity);
                if (resolved != null)
                {
                    return resolved;
                }
            }

            return null;
        }

        private static Assembly TryLoadFromDirectory(string directory, string source, AssemblyName requestedIdentity, bool allowReservedOpenAcaiDirectory = false)
        {
            string assemblyPath;
            string resolvedSource;
            if (!TryFindAssemblyPathInDirectory(directory, source, requestedIdentity.Name, allowReservedOpenAcaiDirectory,
                out assemblyPath, out resolvedSource, requestedIdentity))
            {
                return null;
            }

            try
            {
                return Assembly.LoadFrom(assemblyPath);
            }
            catch (Exception ex)
            {
                _log?.LogWarning("Failed to load dependency '" + requestedIdentity.Name + "' from '" + assemblyPath + "' (" + source + "): " + ex.Message);
                return null;
            }
        }

        private static bool TryFindAssemblyPathInDirectory(
            string directory,
            string source,
            string requestedName,
            bool allowReservedOpenAcaiDirectory,
            out string path,
            out string resolvedSource,
            AssemblyName requestedIdentity = null)
        {
            path = null;
            resolvedSource = null;
            if (string.IsNullOrWhiteSpace(directory) || !IsSimpleAssemblyName(requestedName))
            {
                return false;
            }

            try
            {
                directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            }
            catch (Exception)
            {
                return false;
            }

            var exclusionReason = GetResolverPathExclusionReason(directory);
            if (!string.IsNullOrWhiteSpace(exclusionReason)
                && !(allowReservedOpenAcaiDirectory && string.Equals(exclusionReason, ReservedCompanionDirectoryReason, StringComparison.Ordinal)))
            {
                return false;
            }

            string assemblyPath;
            try
            {
                assemblyPath = Path.Combine(directory, requestedName + ".dll");
                if (!File.Exists(assemblyPath))
                    return false;

                // Read identity only; inspection must not load a native or unrelated DLL.
                // Preserve version-flexible binding, but do not substitute a different culture
                // or public-key token. Metadata identity is not signature verification.
                var identity = AssemblyName.GetAssemblyName(assemblyPath);
                if (!string.Equals(identity.Name, requestedName, StringComparison.OrdinalIgnoreCase)
                    || (requestedIdentity != null && !MatchesRequestedIdentity(requestedIdentity, identity)))
                    return false;
            }
            catch (Exception)
            {
                return false;
            }

            path = assemblyPath;
            resolvedSource = string.IsNullOrWhiteSpace(source) ? "resolver" : source;
            return true;
        }

        private static bool IsSimpleAssemblyName(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(InvalidAssemblyNameCharacters) < 0
                && name.IndexOf('/') < 0 && name.IndexOf('\\') < 0;
        }

        private static bool MatchesRequestedIdentity(AssemblyName requested, AssemblyName candidate)
        {
            if (!string.Equals(requested.Name, candidate.Name, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(requested.CultureName ?? "", candidate.CultureName ?? "", StringComparison.OrdinalIgnoreCase))
                return false;
            var requestedToken = requested.GetPublicKeyToken() ?? Array.Empty<byte>();
            var candidateToken = candidate.GetPublicKeyToken() ?? Array.Empty<byte>();
            return requestedToken.AsSpan().SequenceEqual(candidateToken);
        }

        private static string SafeAssemblyDirectory(Assembly assembly)
        {
            try
            {
                var location = assembly?.Location;
                return string.IsNullOrWhiteSpace(location) ? null : Path.GetDirectoryName(location);
            }
            catch
            {
                return null;
            }
        }

        private sealed class ResolverSearchDirectory
        {
            internal ResolverSearchDirectory(int order, string source, string path)
            {
                Order = order;
                Source = source;
                Path = path;
            }

            internal int Order { get; }
            internal string Source { get; }
            internal string Path { get; }

            internal object CreateState()
            {
                return new
                {
                    order = Order,
                    source = Source,
                    path = Path
                };
            }
        }

        private sealed class ResolverSkippedDirectory
        {
            internal ResolverSkippedDirectory(string path, string source, string reason, string detail)
            {
                Path = path;
                Source = source;
                Reason = reason;
                Detail = detail;
            }

            internal string Path { get; }
            internal string Source { get; }
            internal string Reason { get; }
            internal string Detail { get; }

            internal object CreateState()
            {
                return new
                {
                    source = Source,
                    path = Path,
                    reason = Reason,
                    detail = Detail
                };
            }
        }
    }
}
