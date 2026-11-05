// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using BepInEx;
using Modstoke.Diagnostics;

namespace Modstoke.Loader
{
    internal sealed record HostModLoad(string Id, string Name, string Version, string Loader, string AssemblyPath,
        string Status, double LoadMs, string Error)
    {
        public bool Ok => Status == "loaded";
    }

    /// <summary>Per-mod load results, the <c>mods</c> query and <c>requirements generate</c> for server hosts.</summary>
    internal static class HostMods
    {
        private const int MaxLoads = 1024;
        private const int MaxErrorLength = 512;
        private const int MaxManifestBytes = 256 * 1024;
        private const string DefaultRequirementsName = "Modstoke server";
        private static readonly object Sync = new object();
        private static readonly List<HostModLoad> Loads = new List<HostModLoad>();
        private static readonly ConcurrentDictionary<(string Path, long Size, DateTime Written), string> Hashes = new();

        internal static IReadOnlyList<HostModLoad> LoadResults
        {
            get { lock (Sync) return Loads.ToArray(); }
        }

        internal static void RecordLoad(string id, string name, string version, string loader, string assemblyPath,
            string status, double loadMs, string error)
        {
            var load = new HostModLoad(id ?? "", name, version, loader, assemblyPath, status,
                Math.Round(loadMs, 3), FirstLine(error));
            lock (Sync)
            {
                if (Loads.Count < MaxLoads)
                    Loads.Add(load);
            }
            // Failures recorded before Host.Initialize are emitted there.
            if (status == "failed" && Host.Initialized)
                HostEvents.Emit("mod-error", new { id = load.Id, name = load.Name, loader = load.Loader, error = load.Error });
            HostStatus.MarkChanged();
        }

        internal static HostModLoad FindByAssembly(string assemblyPath)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath))
                return null;
            var loads = LoadResults;
            var entries = LoaderState.CurrentInventory?.Entries ?? Array.Empty<ModInventoryEntry>();
            var fullPath = FullPath(assemblyPath);
            var fileName = Path.GetFileName(assemblyPath);
            return Find(loads, entries, path => string.Equals(FullPath(path), fullPath, StringComparison.OrdinalIgnoreCase))
                ?? Find(loads, entries, path => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase));
        }

        private static HostModLoad Find(IReadOnlyList<HostModLoad> loads, ModInventoryEntry[] entries, Func<string, bool> matches)
        {
            var load = loads.FirstOrDefault(item => !string.IsNullOrEmpty(item.AssemblyPath) && matches(item.AssemblyPath));
            if (load != null)
                return load;
            var entry = entries.FirstOrDefault(item => item != null && !string.IsNullOrEmpty(item.AssemblyPath) && matches(item.AssemblyPath));
            return entry == null ? null : new HostModLoad(entry.Id ?? "", entry.DisplayName, entry.Version,
                LoaderName(entry.LoaderType), entry.AssemblyPath, null, 0, null);
        }

        internal static void RunMods(ConsoleSubmission request)
        {
            var mods = Snapshot();
            var loads = LoadResults;
            var root = GameRoot();
            var listed = ConsoleHub.Read(long.MaxValue, 1).Mods;
            var human = listed.Length == 0 ? "No mods have been discovered yet." : string.Join(Environment.NewLine, listed);
            Task.Run(() =>
            {
                try
                {
                    var described = mods.Select(mod => Describe(mod, loads, root)).ToArray();
                    HostCommands.Respond(request, true, human, new { mods = described });
                }
                catch (Exception error)
                {
                    HostCommands.Respond(request, false, "Could not list mods: " + error.Message);
                }
            });
        }

        internal static void RunRequirements(ConsoleSubmission request)
        {
            if (!TryReadName(request.Arguments, out var name))
            {
                HostCommands.Respond(request, false, "Usage: requirements generate [--name <text>]",
                    error: "invalid-arguments");
                return;
            }

            var mods = Snapshot();
            var root = GameRoot();
            Task.Run(() =>
            {
                try
                {
                    GenerateRequirements(request, name, mods, root);
                }
                catch (Exception error)
                {
                    HostCommands.Respond(request, false, "Could not write server requirements: " + error.Message);
                }
            });
        }

        private static void GenerateRequirements(ConsoleSubmission request, string name, ModFacts[] mods, string root)
        {
            var packages = new List<(ModFacts Mod, string Sha256)>();
            var excluded = new List<object>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in mods)
            {
                var reason = ExcludeReason(mod, ids, packages.Count);
                if (reason != null)
                {
                    excluded.Add(new { id = mod.Id, loader = LoaderName(mod.LoaderType) ?? mod.LoaderType, reason });
                    continue;
                }
                packages.Add((mod, HashAssembly(mod.AssemblyPath)));
            }

            var json = WriteRequirements(name, packages.Select(package => package.Mod));
            if (!ModRequirements.TryParse(json, out var document, out var parseError))
            {
                HostCommands.Respond(request, false, "Could not generate server requirements: " + parseError,
                    new { excluded }, "failed");
                return;
            }

            var canonical = document.GetCanonicalUtf8();
            var path = LoaderPaths.GeneratedRequirementsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            HostOutput.WriteFileAtomic(path, canonical);

            var shown = Relative(path, root);
            var count = document.Packages.Count;
            HostCommands.Respond(request, true,
                "Wrote " + count + (count == 1 ? " required package" : " required packages") + " to " + shown
                    + ". Copy it to the Admin mod folder as RequiredModsFile.",
                new
                {
                    path = shown,
                    document = JsonNode.Parse(canonical),
                    packages = packages.Select(package => new
                    {
                        id = package.Mod.Id,
                        version = package.Mod.Version,
                        sha256 = package.Sha256,
                        source = package.Mod.InstallSource
                    }).ToArray(),
                    excluded
                });
        }

        private static string ExcludeReason(ModFacts mod, HashSet<string> ids, int count)
        {
            if (LoaderName(mod.LoaderType) != "modstoke")
                return "not a native Modstoke package";
            if (KindName(mod.LoaderType) != "mod")
                return "library";
            if (!mod.Enabled)
                return "disabled";
            if (string.IsNullOrWhiteSpace(mod.Id))
                return "no id";
            if (string.IsNullOrWhiteSpace(mod.Version))
                return "no version";
            var client = ClientRequirement(mod);
            if (client != "required")
                return "client requirement is " + client;
            if (!ids.Add(mod.Id))
                return "duplicate id";
            return count >= ModRequirements.MaxPackages ? "package limit reached" : null;
        }

        private static string WriteRequirements(string name, IEnumerable<ModFacts> packages)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
                { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                writer.WriteStartObject();
                writer.WriteString("format", "modstoke.server-requirements");
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteString("name", name);
                writer.WriteStartArray("packages");
                foreach (var package in packages)
                {
                    writer.WriteStartObject();
                    writer.WriteString("loader", "modstoke");
                    writer.WriteString("id", package.Id);
                    writer.WriteString("version", package.Version);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static bool TryReadName(string arguments, out string name)
        {
            name = DefaultRequirementsName;
            var text = (arguments ?? "").Trim();
            if (!text.StartsWith("generate", StringComparison.OrdinalIgnoreCase)
                || (text.Length > 8 && !char.IsWhiteSpace(text[8])))
                return false;
            var rest = text.Substring(8).Trim();
            if (rest.Length == 0)
                return true;
            if (!rest.StartsWith("--name", StringComparison.Ordinal) || rest.Length <= 6 || !char.IsWhiteSpace(rest[6]))
                return false;
            name = rest.Substring(6).Trim();
            return name.Length > 0;
        }

        private static object Describe(ModFacts mod, IReadOnlyList<HostModLoad> loads, string root)
        {
            var loader = LoaderName(mod.LoaderType);
            var fullPath = FullPath(mod.AssemblyPath);
            var load = (fullPath == null ? null : loads.LastOrDefault(item =>
                    string.Equals(FullPath(item.AssemblyPath), fullPath, StringComparison.OrdinalIgnoreCase)))
                ?? (string.IsNullOrEmpty(mod.Id) ? null : loads.LastOrDefault(item =>
                    string.Equals(item.Id, mod.Id, StringComparison.OrdinalIgnoreCase)
                    && (item.Loader == null || item.Loader == loader)));
            var status = load?.Status ?? (mod.Enabled ? null : "skipped");
            return new
            {
                id = mod.Id,
                name = mod.Name,
                version = mod.Version,
                loader,
                kind = KindName(mod.LoaderType),
                enabled = mod.Enabled,
                status,
                ok = load?.Ok ?? mod.Enabled,
                loadMs = load?.LoadMs,
                error = load?.Error,
                sha256 = HashAssembly(mod.AssemblyPath),
                assemblyPath = Relative(mod.AssemblyPath, root),
                clientRequirement = ClientRequirement(mod),
                source = mod.InstallSource
            };
        }

        internal static string LoaderName(string loaderType) =>
            ModLoaderTypes.IsOpenAcai(loaderType) ? "modstoke"
            : ModLoaderTypes.IsLegacyRedLoader(loaderType) ? "redloader"
            : ModLoaderTypes.IsLegacyBepInEx(loaderType) ? "bepinex"
            : null;

        private static string KindName(string loaderType) =>
            ModLoaderTypes.IsLibrary(loaderType) ? "library"
            : ModLoaderTypes.IsLegacyBepInEx(loaderType) ? "plugin"
            : "mod";

        private static string ClientRequirement(ModFacts mod)
        {
            ReadManifest(mod.ManifestPath, out var client, out var platform);
            if (client != null)
                return client;
            if (ModLoaderTypes.IsLegacyRedLoader(mod.LoaderType)
                && string.Equals(platform, "Server", StringComparison.OrdinalIgnoreCase))
                return "server";
            if (ModLoaderTypes.IsLibrary(mod.LoaderType))
                return "server";
            return ModLoaderTypes.IsOpenAcai(mod.LoaderType) ? "required" : "unknown";
        }

        private static void ReadManifest(string manifestPath, out string client, out string platform)
        {
            client = null;
            platform = null;
            try
            {
                if (string.IsNullOrWhiteSpace(manifestPath))
                    return;
                var file = new FileInfo(manifestPath);
                if (!file.Exists || file.Length > MaxManifestBytes)
                    return;
                using var document = JsonDocument.Parse(File.ReadAllBytes(file.FullName), new JsonDocumentOptions
                    { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return;
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                        continue;
                    var value = property.Value.GetString();
                    if (property.Name == "client" && value is "required" or "optional" or "server")
                        client = value;
                    else if (string.Equals(property.Name, "platform", StringComparison.OrdinalIgnoreCase))
                        platform = value;
                }
            }
            catch
            {
                // A manifest that cannot be read keeps the loader's default.
            }
        }

        private static string HashAssembly(string assemblyPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(assemblyPath))
                    return null;
                var file = new FileInfo(assemblyPath);
                if (!file.Exists)
                    return null;
                return Hashes.GetOrAdd((file.FullName, file.Length, file.LastWriteTimeUtc), key =>
                {
                    using var stream = File.OpenRead(key.Path);
                    return Convert.ToHexString(SHA256.HashData(stream));
                });
            }
            catch
            {
                return null;
            }
        }

        private static ModFacts[] Snapshot() =>
            (LoaderState.CurrentInventory?.Entries ?? Array.Empty<ModInventoryEntry>())
                .Where(entry => entry != null)
                .Select(entry => new ModFacts(entry.Id, entry.DisplayName, entry.Version, entry.LoaderType, entry.Enabled,
                    entry.AssemblyPath, entry.ManifestPath, entry.InstallSource))
                .ToArray();

        private static string GameRoot()
        {
            try { return FullPath(Paths.GameRootPath); }
            catch { return null; }
        }

        private static string Relative(string path, string root)
        {
            var full = FullPath(path);
            if (full == null || string.IsNullOrEmpty(root))
                return full ?? path;
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(prefix.Length).Replace('\\', '/')
                : full;
        }

        private static string FullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }

        private static string FirstLine(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
                return null;
            var text = error.Trim();
            var end = text.IndexOfAny(new[] { '\r', '\n' });
            if (end >= 0)
                text = text.Substring(0, end).TrimEnd();
            return text.Length > MaxErrorLength ? text.Substring(0, MaxErrorLength) : text;
        }

        private sealed record ModFacts(string Id, string Name, string Version, string LoaderType, bool Enabled,
            string AssemblyPath, string ManifestPath, string InstallSource);
    }
}
