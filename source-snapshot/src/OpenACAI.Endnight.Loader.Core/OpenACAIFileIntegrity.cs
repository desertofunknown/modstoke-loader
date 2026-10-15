// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Public OpenACAI installed-file integrity surface. This turns the package file
// manifest into an SDK-visible runtime service instead of a manager-only detail.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using BepInEx;

namespace OpenACAI.Loader
{
    public static class OpenACAIFileIntegrity
    {
        public const string ServiceKey = OpenACAIModding.FileIntegrityServiceKey;

        private static readonly object SyncRoot = new object();
        private static OpenACAIFileIntegritySnapshot _snapshot;

        public static OpenACAIFileIntegritySnapshot Snapshot => EnsureSnapshot();
        public static bool Ready => Snapshot.Ready;

        public static IReadOnlyList<OpenACAIFileIntegrityEntrySnapshot> Entries => Snapshot.Entries;
        public static IReadOnlyList<OpenACAIFileIntegrityEntrySnapshot> IssueEntries => Snapshot.IssueEntries;

        public static bool TryFindEntry(string relativePath, out OpenACAIFileIntegrityEntrySnapshot entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return false;
            }

            var normalized = NormalizeRelativePath(relativePath);
            entry = Snapshot.Entries.FirstOrDefault(candidate => string.Equals(candidate.Path, normalized, StringComparison.OrdinalIgnoreCase));
            return entry != null;
        }

        public static OpenACAIFileIntegritySnapshot Refresh()
        {
            lock (SyncRoot)
            {
                _snapshot = CreateSnapshot();
                OpenACAIModding.SetService(ServiceKey, _snapshot);
                return _snapshot;
            }
        }

        internal static void InitializeDefaults()
        {
            Refresh();
        }

        internal static object CreateState()
        {
            return Snapshot;
        }

        private static OpenACAIFileIntegritySnapshot EnsureSnapshot()
        {
            lock (SyncRoot)
            {
                if (_snapshot == null)
                {
                    _snapshot = CreateSnapshot();
                    OpenACAIModding.SetService(ServiceKey, _snapshot);
                }

                return _snapshot;
            }
        }

        private static OpenACAIFileIntegritySnapshot CreateSnapshot()
        {
            var manifestPath = SafeReadPath(() => OpenACAI.Endnight.Loader.OpenACAIPaths.FileManifestPath);
            var mirrorManifestPath = SafeReadPath(() => OpenACAI.Endnight.Loader.OpenACAIPaths.LegacyBootstrapFileManifestPath);
            var gameRoot = SafeReadPath(() => Paths.GameRootPath);
            var updatedUtc = DateTimeOffset.UtcNow.ToString("O");

            var manifest = ReadManifest(manifestPath);
            var mirrorPresent = !string.IsNullOrWhiteSpace(mirrorManifestPath) && File.Exists(mirrorManifestPath);
            var primaryManifestHash = HashFileSafe(manifestPath);
            var mirrorManifestHash = HashFileSafe(mirrorManifestPath);
            var manifestsMatch = !string.IsNullOrWhiteSpace(primaryManifestHash)
                && !string.IsNullOrWhiteSpace(mirrorManifestHash)
                && string.Equals(primaryManifestHash, mirrorManifestHash, StringComparison.OrdinalIgnoreCase);

            if (manifest == null)
            {
                return OpenACAIFileIntegritySnapshot.CreateUnavailable(
                    manifestPath,
                    mirrorManifestPath,
                    mirrorPresent,
                    primaryManifestHash,
                    mirrorManifestHash,
                    manifestsMatch,
                    updatedUtc);
            }

            var entries = manifest.Files
                .Select(entry => VerifyEntry(gameRoot, entry))
                .ToArray();

            return OpenACAIFileIntegritySnapshot.Create(
                manifestPath,
                mirrorManifestPath,
                manifest.Version,
                manifest.GeneratedUtc,
                manifest.DotnetRuntimeVersion,
                manifest.NativeRuntimeAssetPolicy,
                mirrorPresent,
                primaryManifestHash,
                mirrorManifestHash,
                manifestsMatch,
                entries,
                updatedUtc);
        }

        private static ManifestState ReadManifest(string manifestPath)
        {
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            {
                return null;
            }

            try
            {
                using (var document = JsonDocument.Parse(File.ReadAllText(manifestPath)))
                {
                    var root = document.RootElement;
                    var files = new List<ManifestFileEntry>();
                    JsonElement fileArray;
                    if (root.TryGetProperty("files", out fileArray) && fileArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var file in fileArray.EnumerateArray())
                        {
                            var path = ReadString(file, "path");
                            if (string.IsNullOrWhiteSpace(path))
                            {
                                continue;
                            }

                            files.Add(new ManifestFileEntry(
                                NormalizeRelativePath(path),
                                ReadString(file, "sha256"),
                                ReadInt64(file, "size")));
                        }
                    }

                    return new ManifestState(
                        ReadString(root, "version"),
                        ReadString(root, "generatedUtc"),
                        ReadString(root, "dotnetRuntimeVersion"),
                        ReadString(root, "nativeRuntimeAssetPolicy"),
                        files);
                }
            }
            catch (Exception ex)
            {
                OpenACAI.Endnight.Loader.OpenACAIEndnightRuntime.LogSource?.LogWarning("OpenACAI file integrity manifest read failed: " + ex.Message);
                return null;
            }
        }

        private static OpenACAIFileIntegrityEntrySnapshot VerifyEntry(string gameRoot, ManifestFileEntry manifestEntry)
        {
            var relativePath = NormalizeRelativePath(manifestEntry.Path);
            var critical = IsCriticalPath(relativePath);
            var fullPath = ResolveInstalledPath(gameRoot, relativePath);
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return new OpenACAIFileIntegrityEntrySnapshot(
                    relativePath,
                    manifestEntry.Sha256,
                    null,
                    manifestEntry.Size,
                    null,
                    false,
                    false,
                    false,
                    false,
                    critical,
                    "CHECK",
                    "Path could not be resolved inside the game root.");
            }

            if (!File.Exists(fullPath))
            {
                return new OpenACAIFileIntegrityEntrySnapshot(
                    relativePath,
                    manifestEntry.Sha256,
                    null,
                    manifestEntry.Size,
                    null,
                    false,
                    false,
                    false,
                    false,
                    critical,
                    "MISSING",
                    "Installed file is missing.");
            }

            try
            {
                var info = new FileInfo(fullPath);
                var actualHash = HashFileSafe(fullPath);
                var sizeMatches = manifestEntry.Size < 0 || info.Length == manifestEntry.Size;
                var hashMatches = !string.IsNullOrWhiteSpace(manifestEntry.Sha256)
                    && string.Equals(actualHash, manifestEntry.Sha256, StringComparison.OrdinalIgnoreCase);
                var verified = sizeMatches && hashMatches;
                var status = verified ? "READY" : "FAILED";
                var detail = verified
                    ? "Installed file matches the package manifest."
                    : !sizeMatches ? "Installed file size differs from the package manifest." : "Installed file hash differs from the package manifest.";

                return new OpenACAIFileIntegrityEntrySnapshot(
                    relativePath,
                    manifestEntry.Sha256,
                    actualHash,
                    manifestEntry.Size,
                    info.Length,
                    true,
                    sizeMatches,
                    hashMatches,
                    verified,
                    critical,
                    status,
                    detail);
            }
            catch (Exception ex)
            {
                return new OpenACAIFileIntegrityEntrySnapshot(
                    relativePath,
                    manifestEntry.Sha256,
                    null,
                    manifestEntry.Size,
                    null,
                    true,
                    false,
                    false,
                    false,
                    critical,
                    "CHECK",
                    "Installed file could not be verified: " + ex.Message);
            }
        }

        private static string ResolveInstalledPath(string gameRoot, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(gameRoot) || string.IsNullOrWhiteSpace(relativePath))
            {
                return null;
            }

            try
            {
                var root = Path.GetFullPath(gameRoot);
                var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                    ? root
                    : root + Path.DirectorySeparatorChar;
                return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
            }
            catch
            {
                return null;
            }
        }

        private static string HashFileSafe(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                using (var stream = File.OpenRead(path))
                using (var sha = SHA256.Create())
                {
                    return Convert.ToHexString(sha.ComputeHash(stream));
                }
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeRelativePath(string path)
        {
            return (path ?? "").Trim().Replace('\\', '/').TrimStart('/');
        }

        private static bool IsCriticalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (path.StartsWith("OpenACAI/Runtime/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("BepInEx/plugins/OpenACAILoader/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            switch (path)
            {
                case ".doorstop_version":
                case "doorstop_config.ini":
                case "winhttp.dll":
                case "BepInEx/core/BepInEx.Core.dll":
                case "BepInEx/core/BepInEx.Unity.IL2CPP.dll":
                case "BepInEx/core/dobby.dll":
                case "BepInEx/core/runtimes/win-x64/native/dobby.dll":
                case "BepInEx/core/MonoMod.Core.dll":
                case "BepInEx/core/MonoMod.RuntimeDetour.dll":
                case "BepInEx/core/MonoMod.Utils.dll":
                case "BepInEx/core/0Harmony.dll":
                case "BepInEx/core/Il2CppInterop.Runtime.dll":
                    return true;
                default:
                    return false;
            }
        }

        private static string ReadString(JsonElement element, string name)
        {
            JsonElement value;
            return element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        private static long ReadInt64(JsonElement element, string name)
        {
            JsonElement value;
            if (!element.TryGetProperty(name, out value) || value.ValueKind != JsonValueKind.Number)
            {
                return -1L;
            }

            long result;
            return value.TryGetInt64(out result) ? result : -1L;
        }

        private static string SafeReadPath(Func<string> read)
        {
            try
            {
                return read();
            }
            catch
            {
                return null;
            }
        }

        private sealed class ManifestState
        {
            public ManifestState(
                string version,
                string generatedUtc,
                string dotnetRuntimeVersion,
                string nativeRuntimeAssetPolicy,
                IReadOnlyList<ManifestFileEntry> files)
            {
                Version = version;
                GeneratedUtc = generatedUtc;
                DotnetRuntimeVersion = dotnetRuntimeVersion;
                NativeRuntimeAssetPolicy = nativeRuntimeAssetPolicy;
                Files = files ?? Array.Empty<ManifestFileEntry>();
            }

            public string Version { get; }
            public string GeneratedUtc { get; }
            public string DotnetRuntimeVersion { get; }
            public string NativeRuntimeAssetPolicy { get; }
            public IReadOnlyList<ManifestFileEntry> Files { get; }
        }

        private sealed class ManifestFileEntry
        {
            public ManifestFileEntry(string path, string sha256, long size)
            {
                Path = path;
                Sha256 = string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim();
                Size = size;
            }

            public string Path { get; }
            public string Sha256 { get; }
            public long Size { get; }
        }
    }

    public sealed class OpenACAIFileIntegritySnapshot
    {
        private readonly IReadOnlyList<OpenACAIFileIntegrityEntrySnapshot> _entries;
        private readonly IReadOnlyList<OpenACAIFileIntegrityEntrySnapshot> _issueEntries;

        private OpenACAIFileIntegritySnapshot(
            string manifestPath,
            string compatibilityManifestPath,
            string manifestVersion,
            string manifestGeneratedUtc,
            string dotnetRuntimeVersion,
            string nativeRuntimeAssetPolicy,
            bool manifestPresent,
            bool compatibilityManifestPresent,
            string manifestSha256,
            string compatibilityManifestSha256,
            bool manifestsMatch,
            IEnumerable<OpenACAIFileIntegrityEntrySnapshot> entries,
            string status,
            string detail,
            string updatedUtc)
        {
            ManifestPath = manifestPath;
            CompatibilityManifestPath = compatibilityManifestPath;
            ManifestVersion = manifestVersion;
            ManifestGeneratedUtc = manifestGeneratedUtc;
            DotnetRuntimeVersion = dotnetRuntimeVersion;
            NativeRuntimeAssetPolicy = nativeRuntimeAssetPolicy;
            ManifestPresent = manifestPresent;
            CompatibilityManifestPresent = compatibilityManifestPresent;
            ManifestSha256 = manifestSha256;
            CompatibilityManifestSha256 = compatibilityManifestSha256;
            ManifestsMatch = manifestsMatch;
            _entries = (entries ?? Array.Empty<OpenACAIFileIntegrityEntrySnapshot>()).Where(entry => entry != null).ToArray();
            TotalFileCount = _entries.Count;
            VerifiedFileCount = _entries.Count(entry => entry.Verified);
            MissingFileCount = _entries.Count(entry => !entry.Exists);
            SizeMismatchFileCount = _entries.Count(entry => entry.Exists && !entry.SizeMatches);
            HashMismatchFileCount = _entries.Count(entry => entry.Exists && !entry.HashMatches);
            CriticalFileCount = _entries.Count(entry => entry.Critical);
            CriticalIssueCount = _entries.Count(entry => entry.Critical && !entry.Verified);
            _issueEntries = _entries.Where(entry => !entry.Verified).ToArray();
            IssueFileCount = _issueEntries.Count;
            Ready = string.Equals(status, "READY", StringComparison.OrdinalIgnoreCase);
            Status = status;
            Detail = detail;
            UpdatedUtc = updatedUtc;
        }

        public int Schema => 1;
        public string ServiceKey => OpenACAIFileIntegrity.ServiceKey;
        public string ManifestPath { get; }
        public string CompatibilityManifestPath { get; }
        public string ManifestVersion { get; }
        public string ManifestGeneratedUtc { get; }
        public string DotnetRuntimeVersion { get; }
        public string NativeRuntimeAssetPolicy { get; }
        public bool ManifestPresent { get; }
        public bool CompatibilityManifestPresent { get; }
        public string ManifestSha256 { get; }
        public string CompatibilityManifestSha256 { get; }
        public bool ManifestsMatch { get; }
        public bool Ready { get; }
        public string Status { get; }
        public string Detail { get; }
        public int TotalFileCount { get; }
        public int VerifiedFileCount { get; }
        public int MissingFileCount { get; }
        public int SizeMismatchFileCount { get; }
        public int HashMismatchFileCount { get; }
        public int IssueFileCount { get; }
        public int CriticalFileCount { get; }
        public int CriticalIssueCount { get; }
        public string UpdatedUtc { get; }
        public IReadOnlyList<OpenACAIFileIntegrityEntrySnapshot> Entries => _entries;
        public IReadOnlyList<OpenACAIFileIntegrityEntrySnapshot> IssueEntries => _issueEntries;

        internal static OpenACAIFileIntegritySnapshot Create(
            string manifestPath,
            string compatibilityManifestPath,
            string manifestVersion,
            string manifestGeneratedUtc,
            string dotnetRuntimeVersion,
            string nativeRuntimeAssetPolicy,
            bool compatibilityManifestPresent,
            string manifestSha256,
            string compatibilityManifestSha256,
            bool manifestsMatch,
            IEnumerable<OpenACAIFileIntegrityEntrySnapshot> entries,
            string updatedUtc)
        {
            var entryArray = (entries ?? Array.Empty<OpenACAIFileIntegrityEntrySnapshot>()).ToArray();
            var missing = entryArray.Count(entry => !entry.Exists);
            var issue = entryArray.Count(entry => !entry.Verified);
            var ready = compatibilityManifestPresent
                && manifestsMatch
                && entryArray.Length > 0
                && issue == 0;
            var status = ready ? "READY" : missing > 0 || issue > 0 ? "FAILED" : "CHECK";
            var detail = ready
                ? "Installed Modstoke Loader files match the package file manifest."
                : !compatibilityManifestPresent
                    ? "The bootstrap compatibility file manifest mirror is missing."
                    : !manifestsMatch
                        ? "The primary and bootstrap compatibility file manifests differ."
                        : entryArray.Length == 0
                            ? "The file manifest did not list any installed files."
                            : issue + " installed file(s) do not match the package file manifest.";

            return new OpenACAIFileIntegritySnapshot(
                manifestPath,
                compatibilityManifestPath,
                manifestVersion,
                manifestGeneratedUtc,
                dotnetRuntimeVersion,
                nativeRuntimeAssetPolicy,
                true,
                compatibilityManifestPresent,
                manifestSha256,
                compatibilityManifestSha256,
                manifestsMatch,
                entryArray,
                status,
                detail,
                updatedUtc);
        }

        internal static OpenACAIFileIntegritySnapshot CreateUnavailable(
            string manifestPath,
            string compatibilityManifestPath,
            bool compatibilityManifestPresent,
            string manifestSha256,
            string compatibilityManifestSha256,
            bool manifestsMatch,
            string updatedUtc)
        {
            return new OpenACAIFileIntegritySnapshot(
                manifestPath,
                compatibilityManifestPath,
                null,
                null,
                null,
                null,
                false,
                compatibilityManifestPresent,
                manifestSha256,
                compatibilityManifestSha256,
                manifestsMatch,
                Array.Empty<OpenACAIFileIntegrityEntrySnapshot>(),
                "MISSING",
                "The Modstoke Loader file manifest is missing or unreadable.",
                updatedUtc);
        }
    }

    public sealed class OpenACAIFileIntegrityEntrySnapshot
    {
        public OpenACAIFileIntegrityEntrySnapshot(
            string path,
            string expectedSha256,
            string actualSha256,
            long expectedSize,
            long? actualSize,
            bool exists,
            bool sizeMatches,
            bool hashMatches,
            bool verified,
            bool critical,
            string status,
            string detail)
        {
            Path = path ?? "";
            ExpectedSha256 = expectedSha256;
            ActualSha256 = actualSha256;
            ExpectedSize = expectedSize;
            ActualSize = actualSize;
            Exists = exists;
            SizeMatches = sizeMatches;
            HashMatches = hashMatches;
            Verified = verified;
            Critical = critical;
            Status = status ?? "";
            Detail = detail ?? "";
        }

        public string Path { get; }
        public string ExpectedSha256 { get; }
        public string ActualSha256 { get; }
        public long ExpectedSize { get; }
        public long? ActualSize { get; }
        public bool Exists { get; }
        public bool SizeMatches { get; }
        public bool HashMatches { get; }
        public bool Verified { get; }
        public bool Critical { get; }
        public string Status { get; }
        public string Detail { get; }
    }
}
