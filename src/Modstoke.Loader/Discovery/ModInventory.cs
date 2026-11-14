// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Mod inventory scanning for native Modstoke packages and legacy
// compatibility shims.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Modstoke.Loader;
using RedLoader;

namespace Modstoke.Loader
{
    internal sealed class ModInventoryState
    {
        public int Schema { get; set; }
        public string GeneratedUtc { get; set; }
        public bool DiscoveryComplete { get; set; }
        public string[] DiscoveryErrors { get; set; }
        public bool VortexDeploymentDetected { get; set; }
        public string VortexStagingPath { get; set; }
        public ModInventorySummary Summary { get; set; }
        public ModInventoryIssues Issues { get; set; }
        public ModInventoryEntry[] Entries { get; set; }
    }

    internal sealed class ModInventorySummary
    {
        public int Total { get; set; }
        public int OpenAcai { get; set; }
        public int RedLoader { get; set; }
        public int BepInEx { get; set; }
        public int Libraries { get; set; }
        public int LegacyRedLoader { get; set; }
        public int LegacyBepInEx { get; set; }
        public int Vortex { get; set; }
        public int NativeOrManual { get; set; }
        public int Hostable { get; set; }
        public int Hosted { get; set; }
        public int Configurable { get; set; }
        public int LiveSettings { get; set; }
    }

    internal sealed class ModInventoryIssues
    {
        public int Total { get; set; }
        public int MissingAssemblies { get; set; }
        public int DuplicateIds { get; set; }
        public int Disabled { get; set; }
    }

    internal sealed class ModInventoryEntry
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Author { get; set; }
        public string SourceFamily { get; set; }
        public string LoaderType { get; set; }
        public string ManifestType { get; set; }
        public string ExpectedLocation { get; set; }
        public string InstallSource { get; set; }
        public string Store { get; set; }
        public string VortexPackage { get; set; }
        public bool Enabled { get; set; }
        public bool HostedByOpenAcai { get; set; }
        public bool Hostable { get; set; }
        public bool HasAssembly { get; set; }
        public bool HasConfig { get; set; }
        public bool LiveSettingsAvailable { get; set; }
        public bool ModActionsAvailable { get; set; }
        public bool DuplicateId { get; set; }
        public string Status { get; set; }
        public string IssueCode { get; set; }
        public string Issue { get; set; }
        public string ConfigPath { get; set; }
        public string ConfigFormat { get; set; }
        public string ConfigSource { get; set; }
        public long? AssemblySize { get; set; }
        public string AssemblyVersion { get; set; }
        public string ManifestPath { get; set; }
        public string PackagePath { get; set; }
        public string AssemblyPath { get; set; }

        internal bool DataOnly { get; set; }

        /// <summary>The manifest's optional short name for players.</summary>
        public string ManifestDisplayName { get; set; }

        internal string DisplayName => Name == null && Id == null && ManifestDisplayName == null
            ? Path.GetFileNameWithoutExtension(AssemblyPath) ?? "Unknown mod"
            : ModNames.Short(ManifestDisplayName, Name, Id);
        internal bool HasControls => HasConfig || LiveSettingsAvailable || ModActionsAvailable;
    }

    internal sealed class ModInventoryScanner
    {
        private const int MaxManifestDepth = 4;
        private readonly List<string> _discoveryErrors = new List<string>();
        private readonly HashSet<string> _reportedDiscoveryErrors = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> _configFiles = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private Func<string, bool> _hasSettings;
        private static readonly string[] BepInExSupportFolders =
        {
            "OpenACAILoader"
        };

        internal static ModInventoryState Create(string gameRoot, string bepinexPluginRoot, IReadOnlyCollection<ModBase> hostedMods)
        {
            return new ModInventoryScanner().Scan(gameRoot, bepinexPluginRoot, hostedMods);
        }

        private void ReportDiscoveryError(string path, Exception error)
        {
            var message = path + ": " + error.GetType().Name + ": " + error.Message;
            if (_reportedDiscoveryErrors.Add(message))
            {
                _discoveryErrors.Add(message);
            }
        }

        private ModInventoryState Scan(string gameRoot, string bepinexPluginRoot, IReadOnlyCollection<ModBase> hostedMods)
        {
            var vortex = VortexDeploymentIndex.Load(gameRoot, ReportDiscoveryError);
            var entries = new List<ModInventoryEntry>();
            var hostedAssemblyPaths = new HashSet<string>(
                hostedMods
                    .Select(mod => SafeFullPath(mod?.ModAssembly?.Location))
                    .Where(path => path != null),
                StringComparer.OrdinalIgnoreCase);

            entries.AddRange(ScanOpenAcaiPackages(gameRoot, LoaderPaths.ModsDirectory, vortex, hostedAssemblyPaths));
            entries.AddRange(ScanRedLoaderPackages(gameRoot, "Mods", ModLoaderTypes.RedLoaderMod, "Mods", vortex, hostedAssemblyPaths));
            var redLoaderLibraryEntries = ScanRedLoaderPackages(gameRoot, "Libs", ModLoaderTypes.RedLoaderLibrary, "Libs", vortex, hostedAssemblyPaths).ToArray();
            entries.AddRange(redLoaderLibraryEntries);
            entries.AddRange(ScanRedLoaderLibraryAssemblies(gameRoot, "Libs", vortex, hostedAssemblyPaths, redLoaderLibraryEntries));
            entries.AddRange(ScanBepInExPlugins(gameRoot, bepinexPluginRoot, vortex, hostedAssemblyPaths));
            ApplyStatus(entries);
            foreach (var entry in entries)
            {
                entry.ModActionsAvailable = ModActions.HasActions(entry);
            }
            var ordered = entries
                .OrderBy(entry => entry.Name ?? entry.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.AssemblyPath ?? entry.ManifestPath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var redLoaderCount = ordered.Count(entry => IsRedLoader(entry.LoaderType) && !ModLoaderTypes.IsLibrary(entry.LoaderType));
            var bepInExCount = ordered.Count(entry => IsBepInEx(entry.LoaderType));

            return new ModInventoryState
            {
                Schema = 1,
                GeneratedUtc = DateTimeOffset.UtcNow.ToString("O"),
                DiscoveryComplete = _discoveryErrors.Count == 0,
                DiscoveryErrors = _discoveryErrors.ToArray(),
                VortexDeploymentDetected = vortex.IsDetected,
                VortexStagingPath = vortex.StagingPath,
                Summary = new ModInventorySummary
                {
                    Total = ordered.Length,
                    OpenAcai = ordered.Count(entry => IsOpenAcai(entry.LoaderType) && !ModLoaderTypes.IsLibrary(entry.LoaderType)),
                    RedLoader = redLoaderCount,
                    BepInEx = bepInExCount,
                    Libraries = ordered.Count(entry => ModLoaderTypes.IsLibrary(entry.LoaderType)),
                    LegacyRedLoader = redLoaderCount,
                    LegacyBepInEx = bepInExCount,
                    Vortex = ordered.Count(entry => string.Equals(entry.InstallSource, "vortex", StringComparison.OrdinalIgnoreCase)),
                    NativeOrManual = ordered.Count(entry => string.Equals(entry.InstallSource, "openacai", StringComparison.OrdinalIgnoreCase) || string.Equals(entry.InstallSource, "native", StringComparison.OrdinalIgnoreCase) || string.Equals(entry.InstallSource, "manual", StringComparison.OrdinalIgnoreCase)),
                    Hostable = ordered.Count(entry => entry.Hostable),
                    Hosted = ordered.Count(entry => entry.HostedByOpenAcai),
                    Configurable = ordered.Count(entry => entry.HasControls),
                    LiveSettings = ordered.Count(entry => entry.LiveSettingsAvailable)
                },
                Issues = new ModInventoryIssues
                {
                    Total = ordered.Count(entry => !string.IsNullOrWhiteSpace(entry.IssueCode)),
                    MissingAssemblies = ordered.Count(entry => string.Equals(entry.IssueCode, "missing-assembly", StringComparison.OrdinalIgnoreCase)),
                    DuplicateIds = ordered.Count(entry => entry.DuplicateId),
                    Disabled = ordered.Count(entry => !entry.Enabled)
                },
                Entries = ordered
            };
        }

        private IEnumerable<ModInventoryEntry> ScanRedLoaderPackages(
            string gameRoot,
            string relativeRoot,
            string defaultLoaderType,
            string expectedLocation,
            VortexDeploymentIndex vortex,
            ISet<string> hostedAssemblyPaths)
        {
            var root = Path.Combine(gameRoot, relativeRoot);
            foreach (var manifestPath in FindManifestFiles(root, 0))
            {
                ModInventoryEntry entry;
                try
                {
                    var packagePath = Path.GetDirectoryName(manifestPath);
                    if (string.IsNullOrWhiteSpace(packagePath) || IsIgnoredPackagePath(packagePath))
                    {
                        continue;
                    }

                    var manifest = ReadManifest(manifestPath);
                    var manifestType = ReadManifestValue(manifest, "type");
                    var loaderType = string.Equals(manifestType, "Library", StringComparison.OrdinalIgnoreCase)
                        ? ModLoaderTypes.RedLoaderLibrary
                        : defaultLoaderType;
                    var assemblyPath = FindRedLoaderAssembly(packagePath);
                    var source = CreateSourceState(gameRoot, packagePath, assemblyPath, vortex, loaderType);
                    var id = ReadManifestValue(manifest, "id") ?? Path.GetFileName(packagePath);
                    var name = ReadManifestValue(manifest, "name") ?? id ?? Path.GetFileName(packagePath);
                    var author = ReadManifestAuthor(manifest);
                    var configPath = FindConfigPath(
                        Path.Combine(gameRoot, "UserData"),
                        id,
                        name,
                        Path.GetFileNameWithoutExtension(assemblyPath),
                        Path.GetFileName(packagePath));

                    entry = new ModInventoryEntry
                    {
                        Id = id,
                        Name = name,
                        Version = ReadManifestValue(manifest, "version"),
                        Author = author,
                        SourceFamily = ModSourceFamilies.LegacyRedLoader,
                        LoaderType = loaderType,
                        ManifestType = manifestType ?? "Mod",
                        ExpectedLocation = expectedLocation,
                        InstallSource = source.installSource,
                        Store = source.store,
                        VortexPackage = source.vortexPackage,
                        Enabled = !IsDisabledPath(packagePath) && !IsDisabledPath(assemblyPath),
                        HostedByOpenAcai = assemblyPath != null && hostedAssemblyPaths.Contains(SafeFullPath(assemblyPath)),
                        ManifestPath = manifestPath,
                        PackagePath = packagePath,
                        AssemblyPath = assemblyPath,
                        HasAssembly = !string.IsNullOrWhiteSpace(assemblyPath) && File.Exists(assemblyPath),
                        HasConfig = !string.IsNullOrWhiteSpace(configPath),
                        LiveSettingsAvailable = HasLiveSettings(id),
                        ConfigPath = configPath,
                        ConfigFormat = GetConfigFormat(configPath),
                        ConfigSource = !string.IsNullOrWhiteSpace(configPath) ? "RedLoader UserData" : null,
                        AssemblyVersion = GetAssemblyVersion(assemblyPath),
                        AssemblySize = GetAssemblySize(assemblyPath)
                    };
                }
                catch (Exception ex)
                {
                    ReportDiscoveryError(manifestPath, ex);
                    continue;
                }

                yield return entry;
            }
        }

        private IEnumerable<ModInventoryEntry> ScanRedLoaderLibraryAssemblies(
            string gameRoot,
            string relativeRoot,
            VortexDeploymentIndex vortex,
            ISet<string> hostedAssemblyPaths,
            IEnumerable<ModInventoryEntry> manifestBackedEntries)
        {
            var root = Path.Combine(gameRoot, relativeRoot);
            if (!Directory.Exists(root))
            {
                yield break;
            }

            var manifestBackedAssemblies = new HashSet<string>(
                (manifestBackedEntries ?? Array.Empty<ModInventoryEntry>())
                    .Select(entry => SafeFullPath(entry.AssemblyPath))
                    .Where(path => path != null),
                StringComparer.OrdinalIgnoreCase);

            foreach (var assemblyPath in DiscoveryFiles.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly, ReportDiscoveryError))
            {
                var fullAssemblyPath = SafeFullPath(assemblyPath);
                if (fullAssemblyPath == null || manifestBackedAssemblies.Contains(fullAssemblyPath) || IsDisabledPath(fullAssemblyPath))
                {
                    continue;
                }

                var id = Path.GetFileNameWithoutExtension(fullAssemblyPath);
                var packagePath = Path.GetDirectoryName(fullAssemblyPath);
                var source = CreateSourceState(gameRoot, packagePath, fullAssemblyPath, vortex, ModLoaderTypes.RedLoaderLibrary);

                yield return new ModInventoryEntry
                {
                    Id = id,
                    Name = id,
                    Version = null,
                    Author = null,
                    SourceFamily = ModSourceFamilies.LegacyRedLoader,
                    LoaderType = ModLoaderTypes.RedLoaderLibrary,
                    ManifestType = "Library",
                    ExpectedLocation = relativeRoot,
                    InstallSource = source.installSource,
                    Store = source.store,
                    VortexPackage = source.vortexPackage,
                    Enabled = true,
                    HostedByOpenAcai = hostedAssemblyPaths.Contains(fullAssemblyPath),
                    ManifestPath = null,
                    PackagePath = packagePath,
                    AssemblyPath = fullAssemblyPath,
                    HasAssembly = true,
                    HasConfig = false,
                    LiveSettingsAvailable = HasLiveSettings(id),
                    ConfigPath = null,
                    ConfigFormat = null,
                    ConfigSource = null,
                    AssemblyVersion = GetAssemblyVersion(fullAssemblyPath),
                    AssemblySize = GetAssemblySize(fullAssemblyPath)
                };
            }
        }

        private IEnumerable<ModInventoryEntry> ScanOpenAcaiPackages(
            string gameRoot,
            string root,
            VortexDeploymentIndex vortex,
            ISet<string> hostedAssemblyPaths)
        {
            foreach (var manifestPath in FindOpenAcaiManifestFiles(root, 0))
            {
                ModInventoryEntry entry;
                try
                {
                    var packagePath = Path.GetDirectoryName(manifestPath);
                    if (string.IsNullOrWhiteSpace(packagePath) || IsIgnoredPackagePath(packagePath))
                    {
                        continue;
                    }

                    var manifest = ReadManifest(manifestPath);
                    var manifestType = ReadManifestValue(manifest, "type");
                    var assemblyPath = FindOpenAcaiAssembly(packagePath, manifest);
                    var assemblyDisabled = assemblyPath == null && HasDisabledAssembly(packagePath);
                    var loaderType = IsOpenAcaiLibraryManifestType(manifestType)
                        ? ModLoaderTypes.OpenAcaiLibrary
                        : ModLoaderTypes.OpenAcaiMod;
                    var source = CreateSourceState(gameRoot, packagePath, assemblyPath, vortex, loaderType);
                    var id = ReadManifestValue(manifest, "id") ?? Path.GetFileName(packagePath);
                    var name = ReadManifestValue(manifest, "name") ?? id ?? Path.GetFileName(packagePath);
                    var configPath = FindConfigPath(
                        LoaderPaths.UserDataDirectory,
                        id,
                        name,
                        Path.GetFileNameWithoutExtension(assemblyPath),
                        Path.GetFileName(packagePath));

                    entry = new ModInventoryEntry
                    {
                        Id = id,
                        Name = name,
                        ManifestDisplayName = ReadManifestValue(manifest, "displayName"),
                        Version = ReadManifestValue(manifest, "version"),
                        Author = ReadManifestAuthor(manifest),
                        SourceFamily = ModSourceFamilies.OpenAcai,
                        LoaderType = loaderType,
                        ManifestType = manifestType ?? (ModLoaderTypes.IsLibrary(loaderType) ? "OpenACAILibrary" : "OpenACAIMod"),
                        ExpectedLocation = "Modstoke/Mods",
                        InstallSource = source.installSource,
                        Store = source.store,
                        VortexPackage = source.vortexPackage,
                        Enabled = !IsDisabledPath(packagePath) && !IsDisabledPath(assemblyPath) && !assemblyDisabled,
                        HostedByOpenAcai = ModRuntime.IsLoaded(manifestPath, assemblyPath)
                            || (assemblyPath != null && hostedAssemblyPaths.Contains(SafeFullPath(assemblyPath))),
                        ManifestPath = manifestPath,
                        PackagePath = packagePath,
                        AssemblyPath = assemblyPath,
                        HasAssembly = !string.IsNullOrWhiteSpace(assemblyPath) && File.Exists(assemblyPath),
                        DataOnly = ModLoaderTypes.IsLibrary(loaderType)
                            && assemblyPath == null
                            && !assemblyDisabled
                            && !ReadManifestAssemblies(manifest).Any(),
                        HasConfig = !string.IsNullOrWhiteSpace(configPath),
                        LiveSettingsAvailable = false,
                        ConfigPath = configPath,
                        ConfigFormat = GetConfigFormat(configPath),
                        ConfigSource = !string.IsNullOrWhiteSpace(configPath) ? "Modstoke UserData" : null,
                        AssemblyVersion = GetAssemblyVersion(assemblyPath),
                        AssemblySize = GetAssemblySize(assemblyPath)
                    };
                }
                catch (Exception ex)
                {
                    ReportDiscoveryError(manifestPath, ex);
                    continue;
                }

                yield return entry;
            }
        }

        private IEnumerable<ModInventoryEntry> ScanBepInExPlugins(
            string gameRoot,
            string pluginRoot,
            VortexDeploymentIndex vortex,
            ISet<string> hostedAssemblyPaths)
        {
            if (string.IsNullOrWhiteSpace(pluginRoot) || !Directory.Exists(pluginRoot))
            {
                yield break;
            }

            foreach (var assemblyPath in DiscoveryFiles.GetPluginFiles(pluginRoot, ReportDiscoveryError))
            {
                if (IsBepInExSupportAssembly(pluginRoot, assemblyPath))
                {
                    continue;
                }

                var packagePath = ResolveBepInExPackagePath(pluginRoot, assemblyPath);
                var source = CreateSourceState(gameRoot, packagePath, assemblyPath, vortex, ModLoaderTypes.BepInExPlugin);
                var id = Path.GetFileNameWithoutExtension(assemblyPath);
                var configPath = FindConfigPath(Path.Combine(gameRoot, "BepInEx", "config"), id, Path.GetFileName(packagePath));
                var author = GetAssemblyAuthor(assemblyPath);
                var assemblyVersion = GetAssemblyVersion(assemblyPath);
                yield return new ModInventoryEntry
                {
                    Id = id,
                    Name = id,
                    Version = assemblyVersion,
                    Author = author,
                    SourceFamily = ModSourceFamilies.LegacyBepInEx,
                    LoaderType = ModLoaderTypes.BepInExPlugin,
                    ManifestType = "BepInExPlugin",
                    ExpectedLocation = "BepInEx/plugins",
                    InstallSource = source.installSource,
                    Store = source.store,
                    VortexPackage = source.vortexPackage,
                    Enabled = !IsDisabledPath(packagePath) && !IsDisabledPath(assemblyPath),
                    HostedByOpenAcai = hostedAssemblyPaths.Contains(SafeFullPath(assemblyPath)),
                    ManifestPath = null,
                    PackagePath = packagePath,
                    AssemblyPath = assemblyPath,
                    HasAssembly = File.Exists(assemblyPath),
                    HasConfig = !string.IsNullOrWhiteSpace(configPath),
                    LiveSettingsAvailable = false,
                    ConfigPath = configPath,
                    ConfigFormat = GetConfigFormat(configPath),
                    ConfigSource = !string.IsNullOrWhiteSpace(configPath) ? "BepInEx config" : null,
                    AssemblyVersion = assemblyVersion,
                    AssemblySize = GetAssemblySize(assemblyPath)
                };
            }
        }

        private IEnumerable<string> FindOpenAcaiManifestFiles(string root, int depth)
        {
            if (string.IsNullOrWhiteSpace(root) || depth > MaxManifestDepth || !Directory.Exists(root))
            {
                yield break;
            }

            foreach (var fileName in new[] { "openacai.mod.json", "openacai.json", "manifest.json" })
            {
                var manifestPath = Path.Combine(root, fileName);
                if (File.Exists(manifestPath) && !IsDisabledPath(manifestPath))
                {
                    yield return manifestPath;
                }
            }

            if (depth == MaxManifestDepth)
            {
                yield break;
            }

            foreach (var directory in DiscoveryFiles.GetDirectories(root, ReportDiscoveryError))
            {
                var name = Path.GetFileName(directory);
                if (string.Equals(name, "_Disabled", StringComparison.OrdinalIgnoreCase) || string.Equals(name, ".vs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var nested in FindOpenAcaiManifestFiles(directory, depth + 1))
                {
                    yield return nested;
                }
            }
        }

        private IEnumerable<string> FindManifestFiles(string root, int depth)
        {
            if (string.IsNullOrWhiteSpace(root) || depth > MaxManifestDepth || !Directory.Exists(root))
            {
                yield break;
            }

            foreach (var file in DiscoveryFiles.EnumerateFiles(root, "manifest.json", SearchOption.TopDirectoryOnly, ReportDiscoveryError))
            {
                yield return file;
            }

            if (depth == MaxManifestDepth)
            {
                yield break;
            }

            foreach (var directory in DiscoveryFiles.GetDirectories(root, ReportDiscoveryError))
            {
                var name = Path.GetFileName(directory);
                if (string.Equals(name, "_Disabled", StringComparison.OrdinalIgnoreCase) || string.Equals(name, ".vs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var nested in FindManifestFiles(directory, depth + 1))
                {
                    yield return nested;
                }
            }
        }

        private static Dictionary<string, string> ReadManifest(string manifestPath)
        {
            // The package-level caller reports failures and skips the invalid manifest. The size and
            // depth limits match ModHost, so discovery never fully parses a manifest the host rejects.
            using var reader = new StreamReader(new MemoryStream(ModHost.ReadManifestBytes(manifestPath)), System.Text.Encoding.UTF8, true);
            using (var document = JsonDocument.Parse(reader.ReadToEnd(), new JsonDocumentOptions { MaxDepth = ModHost.MaxManifestDepth }))
            {
                return document.RootElement.EnumerateObject()
                    .Where(property => property.Value.ValueKind == JsonValueKind.String)
                    .ToDictionary(property => property.Name, property => property.Value.GetString(), StringComparer.OrdinalIgnoreCase);
            }
        }

        private static string ReadManifestValue(IReadOnlyDictionary<string, string> manifest, string key)
        {
            string value;
            return manifest != null && manifest.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value) ? value : null;
        }

        private static string ReadManifestAuthor(IReadOnlyDictionary<string, string> manifest)
        {
            return ReadManifestValue(manifest, "author")
                ?? ReadManifestValue(manifest, "creator")
                ?? ReadManifestValue(manifest, "createdBy")
                ?? ReadManifestValue(manifest, "created_by")
                ?? ReadManifestValue(manifest, "owner");
        }

        private string FindRedLoaderAssembly(string packagePath)
        {
            var packageName = Path.GetFileName(packagePath);
            var parent = Path.GetDirectoryName(packagePath);
            var siblingDll = !string.IsNullOrWhiteSpace(parent) && !string.IsNullOrWhiteSpace(packageName)
                ? Path.Combine(parent, packageName + ".dll")
                : null;
            if (!string.IsNullOrWhiteSpace(siblingDll) && File.Exists(siblingDll))
            {
                return siblingDll;
            }

            return Directory.Exists(packagePath)
                ? DiscoveryFiles.EnumerateFiles(packagePath, "*.dll", SearchOption.AllDirectories, ReportDiscoveryError).FirstOrDefault()
                : null;
        }

        private static IEnumerable<string> ReadManifestAssemblies(IReadOnlyDictionary<string, string> manifest)
        {
            return new[]
                {
                    ReadManifestValue(manifest, "assembly"),
                    ReadManifestValue(manifest, "entryAssembly"),
                    ReadManifestValue(manifest, "mainAssembly"),
                    ReadManifestValue(manifest, "dll")
                }.Where(value => !string.IsNullOrWhiteSpace(value));
        }

        private bool HasDisabledAssembly(string packagePath)
        {
            return Directory.Exists(packagePath)
                && DiscoveryFiles.EnumerateFiles(packagePath, "*.dll*", SearchOption.AllDirectories, ReportDiscoveryError)
                    .Any(path => IsDisabledPath(path)
                        && (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                            || path.EndsWith(".dll.disabled", StringComparison.OrdinalIgnoreCase)
                            || path.EndsWith(".dll.pending", StringComparison.OrdinalIgnoreCase)));
        }

        private string FindOpenAcaiAssembly(string packagePath, IReadOnlyDictionary<string, string> manifest)
        {
            foreach (var manifestAssembly in ReadManifestAssemblies(manifest))
            {
                foreach (var path in GetOpenAcaiAssemblyCandidates(packagePath, manifestAssembly))
                {
                    if (File.Exists(path) && !IsDisabledPath(path))
                    {
                        return Path.GetFullPath(path);
                    }
                }
            }

            return Directory.Exists(packagePath)
                ? DiscoveryFiles.EnumerateFiles(packagePath, "*.dll", SearchOption.AllDirectories, ReportDiscoveryError)
                    .Where(path => !IsDisabledPath(path))
                    .Select(Path.GetFullPath)
                    .FirstOrDefault()
                : null;
        }

        private static IEnumerable<string> GetOpenAcaiAssemblyCandidates(string packagePath, string manifestAssembly)
        {
            var fileName = manifestAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? manifestAssembly
                : manifestAssembly + ".dll";

            foreach (var candidate in new[]
                     {
                         Path.Combine(packagePath, fileName),
                         Path.Combine(packagePath, "bin", fileName),
                         Path.Combine(packagePath, "bin", "net11.0", fileName),
                         Path.Combine(packagePath, "bin", "net10.0", fileName),
                         Path.Combine(packagePath, "managed", fileName),
                         Path.Combine(packagePath, "Managed", fileName)
                     })
            {
                var fullPath = SafeFullPath(candidate);
                if (IsInsideDirectory(packagePath, fullPath))
                {
                    yield return fullPath;
                }
            }
        }

        private (string installSource, string store, string vortexPackage) CreateSourceState(string gameRoot, string packagePath, string assemblyPath, VortexDeploymentIndex vortex, string loaderType)
        {
            var vortexPackage = vortex.FindPackage(assemblyPath, includeDescendants: false) ?? vortex.FindPackage(packagePath);
            if (!string.IsNullOrWhiteSpace(vortexPackage) || HasVortexMarker(packagePath))
            {
                return ("vortex", "Nexus Mods / Vortex", vortexPackage);
            }

            if (ModLoaderTypes.IsOpenAcai(loaderType))
            {
                return ("openacai", "Modstoke native package", null);
            }

            var installSource = ModLoaderTypes.IsLegacyBepInEx(loaderType)
                ? "manual"
                : "native";

            return (installSource, installSource == "native" ? "Modstoke native store" : "Manual / local", null);
        }

        private bool HasVortexMarker(string path)
        {
            return !string.IsNullOrWhiteSpace(path)
                && Directory.Exists(path)
                && DiscoveryFiles.EnumerateFiles(path, "__folder_managed_by_vortex", SearchOption.AllDirectories, ReportDiscoveryError).Any();
        }

        private static bool IsIgnoredPackagePath(string packagePath)
        {
            var name = Path.GetFileName(packagePath);
            return string.Equals(name, ".vs", StringComparison.OrdinalIgnoreCase)
                || packagePath.IndexOf(Path.DirectorySeparatorChar + ".vs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ApplyStatus(IReadOnlyCollection<ModInventoryEntry> entries)
        {
            var duplicateRedLoaderIds = entries
                .Where(entry => IsRedLoader(entry.LoaderType) && !string.IsNullOrWhiteSpace(entry.Id))
                .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                entry.Hostable = entry.Enabled
                    && IsRedLoader(entry.LoaderType)
                    && !ModLoaderTypes.IsLibrary(entry.LoaderType)
                    && !string.Equals(entry.ManifestType, "Library", StringComparison.OrdinalIgnoreCase)
                    && entry.HasAssembly;

                if (!entry.Enabled)
                {
                    entry.Status = "disabled";
                    entry.IssueCode = "disabled";
                    entry.Issue = "Package or assembly is disabled";
                    continue;
                }

                if (IsOpenAcai(entry.LoaderType) && !entry.HasAssembly && !entry.DataOnly)
                {
                    entry.Status = "missing";
                    entry.IssueCode = "missing-assembly";
                    entry.Issue = "Modstoke manifest was found but no matching assembly was detected";
                    continue;
                }

                if (IsRedLoader(entry.LoaderType) && !entry.HasAssembly)
                {
                    entry.Status = "missing";
                    entry.IssueCode = "missing-assembly";
                    entry.Issue = "Manifest was found but no matching assembly was detected";
                    continue;
                }

                if (IsRedLoader(entry.LoaderType)
                    && !string.IsNullOrWhiteSpace(entry.Id)
                    && duplicateRedLoaderIds.TryGetValue(entry.Id, out var duplicateCount))
                {
                    entry.DuplicateId = true;
                    entry.Status = "warning";
                    entry.IssueCode = "duplicate-id";
                    entry.Issue = "Duplicate RedLoader id appears in " + duplicateCount + " packages";
                    continue;
                }

                if (entry.HostedByOpenAcai)
                {
                    entry.Status = "hosted";
                    continue;
                }

                entry.Status = entry.Hostable ? "ready" : "available";
            }
        }

        private static bool IsDisabledPath(string path)
        {
            return !string.IsNullOrWhiteSpace(path)
                && (path.IndexOf(Path.DirectorySeparatorChar + "_Disabled" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0
                    || path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".pending", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsOpenAcaiLibraryManifestType(string manifestType)
        {
            if (string.IsNullOrWhiteSpace(manifestType))
            {
                return false;
            }

            return string.Equals(manifestType, "Library", StringComparison.OrdinalIgnoreCase)
                || string.Equals(manifestType, "OpenACAILibrary", StringComparison.OrdinalIgnoreCase)
                || string.Equals(manifestType, "OpenACAI.Library", StringComparison.OrdinalIgnoreCase)
                || string.Equals(manifestType, "OpenACAI-Library", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBepInExSupportAssembly(string pluginRoot, string assemblyPath)
        {
            var relative = RelativeTo(pluginRoot, assemblyPath);
            if (relative == null)
            {
                return true;
            }

            var firstSegment = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (BepInExSupportFolders.Contains(firstSegment, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            return relative.IndexOf(Path.DirectorySeparatorChar + "_Disabled" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ResolveBepInExPackagePath(string pluginRoot, string assemblyPath)
        {
            var relative = RelativeTo(pluginRoot, assemblyPath);
            if (string.IsNullOrWhiteSpace(relative) || relative.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) < 0)
            {
                return assemblyPath;
            }

            var firstSegment = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrWhiteSpace(firstSegment) ? assemblyPath : Path.Combine(pluginRoot, firstSegment);
        }

        private string FindConfigPath(string configRoot, params string[] names)
        {
            if (string.IsNullOrWhiteSpace(configRoot) || !Directory.Exists(configRoot))
            {
                return null;
            }

            var candidates = names
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .SelectMany(ExpandConfigName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var candidate in candidates)
            {
                foreach (var extension in new[] { ".cfg", ".json", ".toml" })
                {
                    var exact = Path.Combine(configRoot, candidate + extension);
                    if (File.Exists(exact))
                    {
                        return exact;
                    }
                }
            }

            // Each scanner is used for one inventory refresh, so this cannot retain
            // stale directory contents across refreshes or dynamic plugin loads.
            if (!_configFiles.TryGetValue(configRoot, out var files))
            {
                files = DiscoveryFiles.EnumerateFiles(configRoot, "*.*", SearchOption.TopDirectoryOnly, ReportDiscoveryError)
                    .Where(path => IsConfigExtension(Path.GetExtension(path)))
                    .ToArray();
                _configFiles.Add(configRoot, files);
            }

            foreach (var candidate in candidates)
            {
                var exact = files.FirstOrDefault(path => string.Equals(Path.GetFileNameWithoutExtension(path), candidate, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                {
                    return exact;
                }
            }

            // No substring fallback: the config editor saves into this path, so a near match
            // such as "Tree" -> "TreeRegrowth.cfg" would overwrite another mod's settings.
            return null;
        }

        private static IEnumerable<string> ExpandConfigName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                yield break;
            }

            var trimmed = RemoveInvalidPathNameCharacters(name.Trim());
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                yield break;
            }

            yield return trimmed;
            yield return trimmed.Replace(" ", "");
            yield return trimmed.Replace(" ", "_");
            yield return trimmed.Replace("_", "");
        }

        private static string RemoveInvalidPathNameCharacters(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(value.Where(ch => !invalid.Contains(ch)).ToArray());
        }

        private static bool IsConfigExtension(string extension)
        {
            return string.Equals(extension, ".cfg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".toml", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetConfigFormat(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? null
                : Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        }

        private bool HasLiveSettings(string id)
        {
            if (id == null) return false;
            try
            {
                if (_hasSettings == null)
                {
                    var type = Type.GetType("SUI.SettingsRegistry, SonsSdk");
                    var method = type?.GetMethod("HasSettings", BindingFlags.Static | BindingFlags.Public,
                        null, new[] { typeof(string) }, null);
                    _hasSettings = method?.CreateDelegate<Func<string, bool>>();
                }
                return _hasSettings != null && _hasSettings(id);
            }
            catch
            {
                return false;
            }
        }

        private static string GetAssemblyVersion(string assemblyPath)
        {
            try
            {
                return AssemblyName.GetAssemblyName(assemblyPath).Version?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static string GetAssemblyAuthor(string assemblyPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
                {
                    return null;
                }

                var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(assemblyPath);
                return FirstNonBlank(versionInfo.CompanyName, versionInfo.LegalCopyright);
            }
            catch
            {
                return null;
            }
        }

        private static string FirstNonBlank(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return null;
        }

        private static long? GetAssemblySize(string assemblyPath)
        {
            try
            {
                return string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath)
                    ? (long?)null
                    : new FileInfo(assemblyPath).Length;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsRedLoader(string loaderType)
        {
            return ModLoaderTypes.IsLegacyRedLoader(loaderType);
        }

        private static bool IsBepInEx(string loaderType)
        {
            return ModLoaderTypes.IsLegacyBepInEx(loaderType);
        }

        private static bool IsOpenAcai(string loaderType)
        {
            return ModLoaderTypes.IsOpenAcai(loaderType);
        }

        private static string SafeFullPath(string path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsInsideDirectory(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var rootPath = AppendDirectorySeparator(Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var candidatePath = Path.GetFullPath(path);
                return candidatePath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string RelativeTo(string root, string path)
        {
            try
            {
                return Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
            }
            catch
            {
                return null;
            }
        }

        private static string AppendDirectorySeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
        }
    }

    internal sealed class VortexDeploymentIndex
    {
        private readonly Dictionary<string, string> _files;
        private readonly string _gameRoot;

        private VortexDeploymentIndex(Dictionary<string, string> files, string stagingPath, string gameRoot)
        {
            _files = files;
            StagingPath = stagingPath;
            _gameRoot = gameRoot;
        }

        internal bool IsDetected => _files.Count > 0;
        internal string StagingPath { get; }

        internal static VortexDeploymentIndex Load(string gameRoot, Action<string, Exception> report)
        {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string stagingPath = null;
            var path = Path.Combine(gameRoot, "vortex.deployment.json");

            try
            {
                if (!File.Exists(path))
                {
                    return new VortexDeploymentIndex(files, null, gameRoot);
                }

                using (var document = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    var root = document.RootElement;
                    JsonElement stagingElement;
                    if (root.TryGetProperty("stagingPath", out stagingElement) && stagingElement.ValueKind == JsonValueKind.String)
                    {
                        stagingPath = stagingElement.GetString();
                    }

                    JsonElement fileElements;
                    if (root.TryGetProperty("files", out fileElements) && fileElements.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var file in fileElements.EnumerateArray())
                        {
                            if (file.ValueKind != JsonValueKind.Object)
                            {
                                report?.Invoke(path, new FormatException("Vortex deployment files contains a non-object entry."));
                                continue;
                            }
                            var relPath = ReadJsonString(file, "relPath");
                            var source = ReadJsonString(file, "source");
                            if (!string.IsNullOrWhiteSpace(relPath) && !string.IsNullOrWhiteSpace(source))
                            {
                                files[Normalize(relPath)] = source;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                report?.Invoke(path, ex);
            }

            return new VortexDeploymentIndex(files, stagingPath, gameRoot);
        }

        internal string FindPackage(string absolutePath, bool includeDescendants = true)
        {
            if (string.IsNullOrWhiteSpace(absolutePath) || !IsDetected)
            {
                return null;
            }

            var relative = RelativeToGameRoot(absolutePath);
            if (string.IsNullOrWhiteSpace(relative))
            {
                return null;
            }

            var key = Normalize(relative);
            string exact;
            if (_files.TryGetValue(key, out exact))
            {
                return exact;
            }
            if (!includeDescendants) return null;

            var folderPrefix = key.TrimEnd('/') + "/";
            return _files
                .Where(pair => pair.Key.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
                .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .FirstOrDefault();
        }

        private static string ReadJsonString(JsonElement element, string name)
        {
            JsonElement value;
            return element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        // Deployments made before the folder rename record OpenACAI/ paths; match them as Modstoke/.
        private static string Normalize(string path)
        {
            var normalized = path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
            return normalized.StartsWith(LegacyRootPrefix, StringComparison.Ordinal)
                ? RootPrefix + normalized.Substring(LegacyRootPrefix.Length)
                : normalized;
        }

        private static readonly string RootPrefix = LoaderPaths.RootDirectoryName.ToLowerInvariant() + "/";
        private static readonly string LegacyRootPrefix = LoaderPaths.LegacyRootDirectoryName.ToLowerInvariant() + "/";

        private string RelativeToGameRoot(string absolutePath)
        {
            try
            {
                var relative = Path.GetRelativePath(_gameRoot, absolutePath);
                if (Path.IsPathRooted(relative) || relative == ".."
                    || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                    return null;
                return relative;
            }
            catch
            {
                return null;
            }
        }
    }
}
