// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Public Modstoke installed-file integrity surface. This turns the package file
// manifest into an SDK-visible runtime service instead of a manager-only detail.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using BepInEx;

namespace Modstoke.Loader
{
    public static class FileIntegrity
    {
        public const string ServiceKey = Modding.FileIntegrityServiceKey;

        private static readonly object SyncRoot = new object();
        private static FileIntegritySnapshot _snapshot;
        private static long _refreshSequence;
        private static long _publishedSequence;

        public static FileIntegritySnapshot Snapshot => EnsureSnapshot();
        public static bool Ready => Snapshot.Ready;

        public static IReadOnlyList<FileIntegrityEntrySnapshot> Entries => Snapshot.Entries;
        public static IReadOnlyList<FileIntegrityEntrySnapshot> IssueEntries => Snapshot.IssueEntries;

        public static bool TryFindEntry(string relativePath, out FileIntegrityEntrySnapshot entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return false;
            }

            var normalized = NormalizeRelativePath(relativePath);
            return Snapshot.TryFindEntry(normalized, out entry);
        }

        public static FileIntegritySnapshot Refresh()
        {
            // Hash outside the lock so Snapshot readers never wait on disk I/O; a slower,
            // older refresh must not replace a newer one.
            var sequence = Interlocked.Increment(ref _refreshSequence);
            var snapshot = CreateSnapshot();
            lock (SyncRoot)
            {
                if (sequence > _publishedSequence)
                {
                    _publishedSequence = sequence;
                    _snapshot = snapshot;
                    Modding.SetService(ServiceKey, _snapshot);
                }

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

        private static FileIntegritySnapshot EnsureSnapshot()
        {
            lock (SyncRoot)
            {
                if (_snapshot == null)
                {
                    _snapshot = CreateSnapshot();
                    Modding.SetService(ServiceKey, _snapshot);
                }

                return _snapshot;
            }
        }

        private static FileIntegritySnapshot CreateSnapshot()
        {
            var manifestPath = SafeReadPath(() => global::Modstoke.Loader.LoaderPaths.FileManifestPath);
            var mirrorManifestPath = SafeReadPath(() => global::Modstoke.Loader.LoaderPaths.LegacyBootstrapFileManifestPath);
            var gameRoot = SafeReadPath(() => Paths.GameRootPath);
            var updatedUtc = DateTimeOffset.UtcNow.ToString("O");

            var manifestPresent = !string.IsNullOrWhiteSpace(manifestPath) && File.Exists(manifestPath);
            var manifest = ReadManifest(manifestPath, out var primaryManifestHash);
            var mirrorPresent = !string.IsNullOrWhiteSpace(mirrorManifestPath) && File.Exists(mirrorManifestPath);
            var mirrorManifestHash = HashFileSafe(mirrorManifestPath);
            var manifestsMatch = !string.IsNullOrWhiteSpace(primaryManifestHash)
                && !string.IsNullOrWhiteSpace(mirrorManifestHash)
                && string.Equals(primaryManifestHash, mirrorManifestHash, StringComparison.OrdinalIgnoreCase);

            if (manifest == null)
            {
                return FileIntegritySnapshot.CreateUnavailable(
                    manifestPath,
                    mirrorManifestPath,
                    mirrorPresent,
                    primaryManifestHash,
                    mirrorManifestHash,
                    manifestsMatch,
                    updatedUtc,
                    manifestPresent);
            }

            var entries = manifest.Files
                .Select(entry => VerifyEntry(gameRoot, entry))
                .ToArray();

            return FileIntegritySnapshot.Create(
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

        private static ManifestState ReadManifest(string manifestPath, out string manifestHash)
        {
            manifestHash = null;
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            {
                return null;
            }

            try
            {
                // Parse and fingerprint the same bytes, even if an installer replaces the manifest.
                var bytes = File.ReadAllBytes(manifestPath);
                manifestHash = Convert.ToHexString(SHA256.HashData(bytes));
                var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                using (var document = ParseManifest(bytes, offset))
                {
                    var root = document.RootElement;
                    var files = new List<ManifestFileEntry>();
                    var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (root.ValueKind != JsonValueKind.Object
                        || !root.TryGetProperty("files", out var fileArray)
                        || fileArray.ValueKind != JsonValueKind.Array)
                    {
                        throw new InvalidDataException("The file manifest must contain a files array.");
                    }

                    foreach (var file in fileArray.EnumerateArray())
                    {
                        if (file.ValueKind != JsonValueKind.Object)
                            throw new InvalidDataException("The file manifest contains a non-object entry.");
                        var path = ReadString(file, "path");
                        if (!IsValidManifestPath(path))
                            throw new InvalidDataException("The file manifest contains an invalid relative path.");
                        path = NormalizeRelativePath(path);
                        if (!paths.Add(path))
                            throw new InvalidDataException("The file manifest contains a duplicate path: " + path);
                        var hash = ReadString(file, "sha256")?.Trim();
                        if (hash == null || hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))
                            throw new InvalidDataException("The file manifest contains an invalid SHA256 value: " + path);
                        var size = ReadInt64(file, "size");
                        if (file.TryGetProperty("size", out _) && size < 0)
                            throw new InvalidDataException("The file manifest contains an invalid size: " + path);
                        files.Add(new ManifestFileEntry(path, hash, size));
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
                global::Modstoke.Loader.RuntimeEntryPoint.LogSource?.LogWarning("Modstoke file integrity manifest read failed: " + ex.Message);
                return null;
            }
        }

        private static JsonDocument ParseManifest(byte[] bytes, int utf8Offset)
        {
            // UTF-16/32 manifests need a BOM-aware decoder; UTF-8 bytes are parsed directly.
            if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE)
                || (bytes[0] == 0xFE && bytes[1] == 0xFF))
                || bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            {
                using var reader = new StreamReader(new MemoryStream(bytes, false), detectEncodingFromByteOrderMarks: true);
                return JsonDocument.Parse(reader.ReadToEnd());
            }
            return JsonDocument.Parse(bytes.AsMemory(utf8Offset));
        }

        private static FileIntegrityEntrySnapshot VerifyEntry(string gameRoot, ManifestFileEntry manifestEntry)
        {
            var relativePath = NormalizeRelativePath(manifestEntry.Path);
            var critical = IsCriticalPath(relativePath);
            var fullPath = ResolveInstalledPath(gameRoot, relativePath);
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return new FileIntegrityEntrySnapshot(
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
                return new FileIntegrityEntrySnapshot(
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
                using var stream = File.OpenRead(fullPath);
                var actualSize = stream.Length;
                var actualHash = Convert.ToHexString(SHA256.HashData(stream));
                var sizeMatches = manifestEntry.Size < 0 || actualSize == manifestEntry.Size;
                var hashMatches = !string.IsNullOrWhiteSpace(manifestEntry.Sha256)
                    && string.Equals(actualHash, manifestEntry.Sha256, StringComparison.OrdinalIgnoreCase);
                var verified = sizeMatches && hashMatches;
                var status = verified ? "READY" : "FAILED";
                var detail = verified
                    ? "Installed file matches the package manifest."
                    : !sizeMatches ? "Installed file size differs from the package manifest." : "Installed file hash differs from the package manifest.";

                return new FileIntegrityEntrySnapshot(
                    relativePath,
                    manifestEntry.Sha256,
                    actualHash,
                    manifestEntry.Size,
                    actualSize,
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
                return new FileIntegrityEntrySnapshot(
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
            if (string.IsNullOrWhiteSpace(gameRoot) || !IsValidManifestPath(relativePath))
            {
                return null;
            }

            try
            {
                var root = Path.GetFullPath(gameRoot);
                var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
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
                using var stream = File.OpenRead(path);
                return Convert.ToHexString(SHA256.HashData(stream));
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

        private static bool IsValidManifestPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !string.Equals(path, path.Trim(), StringComparison.Ordinal)
                || Path.IsPathRooted(path)
                || path.IndexOf(':') >= 0 || path.IndexOf('\0') >= 0)
                return false;
            foreach (var component in path.Replace('\\', '/').Split('/'))
            {
                if (component.Length == 0 || component == "." || component == ".."
                    || component.EndsWith(' ') || component.EndsWith('.'))
                    return false;
            }
            return true;
        }

        private static bool IsCriticalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (path.StartsWith("Modstoke/Runtime/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("OpenACAI/Runtime/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("BepInEx/plugins/OpenACAILoader/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            switch (path.ToLowerInvariant())
            {
                case ".doorstop_version":
                case "doorstop_config.ini":
                case "winhttp.dll":
                case "bepinex/core/bepinex.core.dll":
                case "bepinex/core/bepinex.unity.il2cpp.dll":
                case "bepinex/core/dobby.dll":
                case "bepinex/core/runtimes/win-x64/native/dobby.dll":
                case "bepinex/core/monomod.core.dll":
                case "bepinex/core/monomod.runtimedetour.dll":
                case "bepinex/core/monomod.utils.dll":
                case "bepinex/core/0harmony.dll":
                case "bepinex/core/il2cppinterop.runtime.dll":
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

    public sealed class FileIntegritySnapshot
    {
        private readonly IReadOnlyList<FileIntegrityEntrySnapshot> _entries;
        private readonly IReadOnlyList<FileIntegrityEntrySnapshot> _issueEntries;
        private readonly Dictionary<string, FileIntegrityEntrySnapshot> _entriesByPath;

        private FileIntegritySnapshot(
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
            IEnumerable<FileIntegrityEntrySnapshot> entries,
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
            _entries = Array.AsReadOnly((entries ?? Array.Empty<FileIntegrityEntrySnapshot>()).Where(entry => entry != null).ToArray());
            _entriesByPath = new Dictionary<string, FileIntegrityEntrySnapshot>(_entries.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _entries)
            {
                _entriesByPath.TryAdd(entry.Path, entry);
                if (entry.Verified)
                    VerifiedFileCount++;
                if (!entry.Exists)
                    MissingFileCount++;
                if (entry.ActualSize.HasValue && !entry.SizeMatches)
                    SizeMismatchFileCount++;
                if (!string.IsNullOrEmpty(entry.ActualSha256) && !entry.HashMatches)
                    HashMismatchFileCount++;
                if (entry.Critical)
                {
                    CriticalFileCount++;
                    if (!entry.Verified)
                        CriticalIssueCount++;
                }
            }
            TotalFileCount = _entries.Count;
            _issueEntries = Array.AsReadOnly(_entries.Where(entry => !entry.Verified).ToArray());
            IssueFileCount = _issueEntries.Count;
            Ready = string.Equals(status, "READY", StringComparison.OrdinalIgnoreCase);
            Status = status;
            Detail = detail;
            UpdatedUtc = updatedUtc;
        }

        public int Schema => 1;
        public string ServiceKey => FileIntegrity.ServiceKey;
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
        public IReadOnlyList<FileIntegrityEntrySnapshot> Entries => _entries;
        public IReadOnlyList<FileIntegrityEntrySnapshot> IssueEntries => _issueEntries;

        internal bool TryFindEntry(string path, out FileIntegrityEntrySnapshot entry)
        {
            return _entriesByPath.TryGetValue(path, out entry);
        }

        internal static FileIntegritySnapshot Create(
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
            IEnumerable<FileIntegrityEntrySnapshot> entries,
            string updatedUtc)
        {
            var entryArray = (entries ?? Array.Empty<FileIntegrityEntrySnapshot>()).ToArray();
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

            return new FileIntegritySnapshot(
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

        internal static FileIntegritySnapshot CreateUnavailable(
            string manifestPath,
            string compatibilityManifestPath,
            bool compatibilityManifestPresent,
            string manifestSha256,
            string compatibilityManifestSha256,
            bool manifestsMatch,
            string updatedUtc,
            bool manifestPresent)
        {
            return new FileIntegritySnapshot(
                manifestPath,
                compatibilityManifestPath,
                null,
                null,
                null,
                null,
                manifestPresent,
                compatibilityManifestPresent,
                manifestSha256,
                compatibilityManifestSha256,
                manifestsMatch,
                Array.Empty<FileIntegrityEntrySnapshot>(),
                manifestPresent ? "CHECK" : "MISSING",
                manifestPresent ? "The Modstoke Loader file manifest could not be read or is invalid."
                    : "The Modstoke Loader file manifest is missing.",
                updatedUtc);
        }
    }

    public sealed class FileIntegrityEntrySnapshot
    {
        public FileIntegrityEntrySnapshot(
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
