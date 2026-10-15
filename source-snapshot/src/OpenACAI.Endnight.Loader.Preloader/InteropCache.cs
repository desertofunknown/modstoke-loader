// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BepInEx;
using BepInEx.Logging;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class InteropCache
    {
        internal const string ManifestName = "openacai-interop-cache.json";
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        internal static string ComputeInputHash(InteropSettings settings)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Add("policy", "openacai-interop-v3");
            Add("openacai-conversion-build", typeof(InteropConversion).Assembly.ManifestModule.ModuleVersionId.ToString());
            AddFile("game-assembly", settings.GameAssemblyPath);
            AddFile("metadata", settings.MetadataPath);
            Add("unity-version", settings.UnityVersion);
            Add("base-library-source", settings.BaseLibrarySource);
            Add("scan-method-refs", settings.ScanMethodRefs.ToString());
            Add("dump-dummy-assemblies", settings.DumpDummyAssemblies.ToString());
            Add("obfuscation-regex", settings.ObfuscationRegex);
            if (File.Exists(settings.RenameMapPath))
                AddFile("rename-map", settings.RenameMapPath);
            else
                Add("rename-map", "absent");

            if (Directory.Exists(settings.BaseLibraryDirectory))
                foreach (var path in Directory.EnumerateFiles(settings.BaseLibraryDirectory, "*.dll").OrderBy(Path.GetFileName, StringComparer.Ordinal))
                    AddFile("unity-library:" + Path.GetFileName(path), path);
            if (!string.IsNullOrEmpty(settings.BaseLibrarySource))
            {
                var source = settings.BaseLibrarySource;
                var isHttp = Uri.TryCreate(source, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
                var archive = Path.Combine(settings.BaseLibraryDirectory, Path.GetFileName(isHttp ? uri.AbsolutePath : source));
                if (File.Exists(archive))
                    AddFile("unity-library-archive", archive);
            }

            // Build identities cover patched dependencies that retain their assembly version.
            foreach (var name in new[] { "Cpp2IL.Core", "LibCpp2IL",
                "Il2CppInterop.Common", "Il2CppInterop.Runtime", "AsmResolver.DotNet", "AsmResolver", "AsmResolver.PE", "AsmResolver.PE.File" })
            {
                var assembly = Assembly.Load(name);
                Add(name, assembly.FullName + ":" + assembly.ManifestModule.ModuleVersionId);
            }
            return Convert.ToHexString(hash.GetHashAndReset());

            void AddFile(string label, string path) => Add(label, HashFile(path));
            void Add(string label, string value)
            {
                // JSON pairs keep adjacent labels and values unambiguous in the hash input.
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { label, value });
                hash.AppendData(bytes);
            }
        }

        // Exact legacy ordering/format is needed only by the explicitly selected BepInEx path.
        // The owned cache uses the complete SHA256 input and output manifest instead.
        internal static string ComputeCompatibilityHash(InteropSettings settings)
        {
            Version generatorVersion;
            try
            {
                const string generatorName = "Il2CppInterop.Generator";
                var generator = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetName())
                    .FirstOrDefault(name => string.Equals(name.Name, generatorName, StringComparison.OrdinalIgnoreCase));
                generator ??= AssemblyName.GetAssemblyName(Path.Combine(Paths.BepInExAssemblyDirectory, generatorName + ".dll"));
                if (!string.Equals(generator.Name, generatorName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The packaged compatibility generator has an unexpected assembly identity.");
                generatorVersion = generator.Version;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException
                || ex is UnauthorizedAccessException || ex is BadImageFormatException)
            {
                InteropGenerationDiagnostics.Log(LogLevel.Warning,
                    "Skipping the legacy interop cache marker because the compatibility generator identity is unavailable. " +
                    "Owned interop generation does not require that generator.", ex);
                return null;
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            AppendFile(settings.GameAssemblyPath);
            if (Directory.Exists(settings.BaseLibraryDirectory))
                foreach (var file in Directory.EnumerateFiles(settings.BaseLibraryDirectory, "*.dll"))
                {
                    hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file)));
                    AppendFile(file);
                }
            if (File.Exists(settings.RenameMapPath))
                AppendFile(settings.RenameMapPath);
            hash.AppendData(Encoding.UTF8.GetBytes(generatorVersion.ToString()));
            hash.AppendData(Encoding.UTF8.GetBytes(Assembly.Load("Cpp2IL.Core").GetName().Version.ToString()));
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

            void AppendFile(string path)
            {
                using var stream = File.OpenRead(path);
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer)) > 0)
                    hash.AppendData(buffer, 0, read);
            }
        }

        internal static InteropCacheManifest ReadAndValidate(string directory)
        {
            var files = InteropOutputValidator.Validate(directory);
            var path = Path.Combine(directory, ManifestName);
            if (!File.Exists(path))
                return null; // A structurally valid legacy cache is available for failure fallback.

            var manifest = JsonSerializer.Deserialize<InteropCacheManifest>(File.ReadAllText(path), JsonOptions);
            if (manifest == null || manifest.Schema != 1 || string.IsNullOrEmpty(manifest.InputHash) || manifest.Files == null)
                throw new InvalidDataException("The interop cache manifest is incomplete or unsupported.");
            var recordedNames = manifest.Files.Select(file => file?.Name).ToArray();
            if (recordedNames.Any(string.IsNullOrEmpty) || recordedNames.Length != files.Length
                || !new HashSet<string>(recordedNames, StringComparer.Ordinal).SetEquals(files))
                throw new InvalidDataException("The interop DLL/database set differs from its cache manifest.");
            foreach (var file in manifest.Files)
                if (!string.Equals(file.Sha256, HashFile(Path.Combine(directory, file.Name)), StringComparison.Ordinal))
                    throw new InvalidDataException("Interop cache content changed: " + file.Name);
            return manifest;
        }

        internal static void Write(string directory, string inputHash)
        {
            var manifest = new InteropCacheManifest
            {
                Schema = 1,
                InputHash = inputHash,
                Files = InteropOutputValidator.Validate(directory).Select(name => new InteropCacheFile
                {
                    Name = name,
                    Sha256 = HashFile(Path.Combine(directory, name))
                }).ToArray()
            };
            File.WriteAllText(Path.Combine(directory, ManifestName), JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8);
        }

        private static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
    }

    internal sealed class InteropCacheManifest
    {
        public InteropCacheManifest() { }
        public int Schema { get; set; }
        public string InputHash { get; set; }
        public InteropCacheFile[] Files { get; set; }
    }

    internal sealed class InteropCacheFile
    {
        public InteropCacheFile() { }
        public string Name { get; set; }
        public string Sha256 { get; set; }
    }
}
