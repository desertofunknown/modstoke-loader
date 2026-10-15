// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Owned activation orchestration over the existing SonsMod metadata and
// registration contracts. Upstream SDK bootstrap remains an explicit fallback.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using Alt.Json;
using RedLoader;
using RedLoader.Utils;
using SonsSdk;
using SonsSdk.Attributes;
using GameVersion = Sons.GameApplication.Version;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenACAIRedLoaderModActivation
    {
        internal static SonsMod Activate(string assemblyPath, string manifestPath)
        {
            var assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
            ManifestData manifest = null;
            try
            {
                manifestPath ??= Path.Combine(
                    Path.GetDirectoryName(assemblyPath) ?? LoaderEnvironment.ModsDirectory,
                    assemblyName, "manifest.json");
                manifest = ReadManifest(manifestPath, assemblyName);
                ValidateManifest(manifest, assemblyName);

                var assembly = Assembly.LoadFrom(assemblyPath);
                var implementation = assembly.GetTypes().FirstOrDefault(type => type.IsSubclassOf(typeof(SonsMod)));
                if (implementation == null)
                    throw new InvalidOperationException("No SonsMod implementation was found in " + assembly.FullName + ".");

                var mod = (SonsMod)Activator.CreateInstance(implementation,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, null, null);
                if (mod == null)
                    throw new InvalidOperationException("Could not create an instance of " + implementation.FullName + ".");

                if (!TryParseVersion(manifest.Version, out var version))
                    throw Reject(manifest.Id, "Invalid mod version",
                        "Invalid version '" + manifest.Version + "' in manifest for " + manifest.Id + ".");

                manifest.VersionObject = version;
                // Legacy registration still requires this exact metadata type.
#pragma warning disable CS0618
                mod.Info = new MelonInfoAttribute(mod.GetType(),
                    string.IsNullOrEmpty(manifest.Name) ? manifest.Id : manifest.Name,
                    version.Major, version.Minor, version.Build, manifest.Author, manifest.Url);
#pragma warning restore CS0618
                mod.Manifest = manifest;
                mod.ModAssembly = assembly;
                mod.Priority = manifest.Priority;
                mod.ConsoleColor = ParseLogColor(manifest.LogColor);
                mod.AuthorConsoleColor = RLog.DefaultTextColor;
                mod.SupportedGameVersion = manifest.GameVersion;
                mod.OptionalDependencies = manifest.Dependencies;
                mod.ID = manifest.Id;
                mod.CatalogLoader = OpenACAIRedLoaderCatalogs.LoadCatalogs;

                // Asset metadata must be complete before Register publishes callbacks.
                mod.AssetBundleAttrs = DiscoverAssetBundles(assembly);
                foreach (var attribute in mod.AssetBundleAttrs)
                {
                    attribute.Mod = mod;
                    attribute.BundleLoader = OpenACAIRedLoaderAssets.LoadBundle;
                }

                if (!mod.Register())
                    throw Reject(manifest.Id, "Mod registration failed", "Mod '" + manifest.Id + "' failed to register.");

                ReportLoaded(mod, assemblyName, manifest);
                return mod;
            }
            catch (Exception error)
            {
                if (error is ReflectionTypeLoadException)
                    TryReport(() => SonsMod.ReportMod(manifest?.Id ?? assemblyName, "Could not load mod assembly"));
                TryReport(() => RLog.Error(error));
                throw;
            }
        }

        private static ManifestData ReadManifest(string path, string assemblyName)
        {
            ManifestData manifest;
            try
            {
                // Keep the serializer and metadata identity used by existing manifests.
                manifest = File.Exists(path)
                    ? JsonConvert.DeserializeObject<ManifestData>(File.ReadAllText(path))
                    : null;
            }
            catch
            {
                TryReport(() => SonsMod.ReportMod(assemblyName, "Invalid manifest.json"));
                throw;
            }

            if (manifest == null)
                throw Reject(assemblyName, "Missing manifest.json",
                    assemblyName + " does not have a manifest.json file. Expected: " + path);

            if (string.IsNullOrWhiteSpace(manifest.Id))
                manifest.Id = assemblyName;
            if (string.IsNullOrWhiteSpace(manifest.Name))
                manifest.Name = manifest.Id;
            if (string.IsNullOrWhiteSpace(manifest.Version))
                manifest.Version = "0.0.0";
            return manifest;
        }

        private static void ValidateManifest(ManifestData manifest, string assemblyName)
        {
            var platform = string.IsNullOrWhiteSpace(manifest.Platform) ? "Client" : manifest.Platform;
            if (string.Equals(platform, "Client", StringComparison.OrdinalIgnoreCase) && LoaderEnvironment.IsDedicatedServer)
                throw Reject(manifest.Id, "Client mod cannot be loaded on a dedicated server.",
                    "Mod " + assemblyName + " is a client mod and cannot be loaded on a dedicated server.");

            if (string.Equals(platform, "Server", StringComparison.OrdinalIgnoreCase) && !LoaderEnvironment.IsDedicatedServer)
                throw Reject(manifest.Id, "Server mod cannot be loaded on a client.",
                    "Mod " + assemblyName + " is a server mod and cannot be loaded on a client.");

            if (!string.IsNullOrEmpty(manifest.LoaderVersion) && !IsLoaderCompatible(manifest.LoaderVersion))
                throw Reject(manifest.Id, "Requires RedLoader >=" + manifest.LoaderVersion,
                    "Mod " + assemblyName + " requires a different version of RedLoader.");

            if (manifest.Type == ManifestData.EAssemblyType.Library)
                throw Reject(manifest.Id, "Is a library but was put into the mods folder.",
                    assemblyName + " is a library but was put into the mods folder.");
        }

        private static bool TryParseVersion(string text, out Version version)
        {
            if (Version.TryParse(text, out version))
                return true;

            var separator = text?.IndexOf('-') ?? -1;
            return separator > 0 && Version.TryParse(text.Substring(0, separator), out version);
        }

        private static bool IsLoaderCompatible(string requiredVersion)
        {
            var version = SemanticVersioning.Version.Parse(requiredVersion);
            var supported = new SemanticVersioning.Range("<=" + LoaderEnvironment.RedloaderVersion);
            return supported.IsSatisfied(version);
        }

        private static List<AssetBundleAttribute> DiscoverAssetBundles(Assembly assembly)
        {
            var bundles = new List<AssetBundleAttribute>();
            foreach (var type in assembly.GetTypes())
            {
                var bundle = type.GetCustomAttribute<AssetBundleAttribute>();
                if (bundle == null)
                    continue;

                // Preserve inherited attributes and the SDK's public-property selection.
                // Property validity is checked later when the bundle actually loads.
                foreach (var property in type.GetProperties())
                {
                    var reference = property.GetCustomAttribute<AssetReferenceAttribute>();
                    if (reference == null)
                        continue;

                    reference.AssetBundle = bundle;
                    reference.Property = property;
                    bundle.AssetReferences.Add(reference);
                }

                bundles.Add(bundle);
            }
            return bundles;
        }

        private static Color ParseLogColor(string text)
        {
            if (string.IsNullOrEmpty(text))
                return RLog.DefaultMelonColor;
            if (text.StartsWith("#"))
                text = text.Substring(1);
            if (text.Length != 6)
                return RLog.DefaultMelonColor;

            return Color.FromArgb(
                Convert.ToInt32(text.Substring(0, 2), 16),
                Convert.ToInt32(text.Substring(2, 2), 16),
                Convert.ToInt32(text.Substring(4, 2), 16));
        }

        private static InvalidOperationException Reject(string id, string reason, string message)
        {
            TryReport(() => SonsMod.ReportMod(id, reason));
            return new InvalidOperationException(message);
        }

        private static void ReportLoaded(SonsMod mod, string assemblyName, ManifestData manifest)
        {
            // Once registration succeeds, diagnostics cannot discard the accepted instance.
            TryReport(() =>
            {
                if (!string.IsNullOrEmpty(manifest.GameVersion)
                    && !manifest.GameVersion.Contains('.')
                    && manifest.GameVersion != GameVersion.GetVersionString())
                {
                    TryReport(() => SonsMod.ReportMod(manifest.Id, "Mod was loaded, but was made for a different game version"));
                    TryReport(() => RLog.Warning(assemblyName + " is made for a different version of the game."));
                    TryReport(() => RLog.Warning("The mod will still load, but may not work correctly."));
                }
            });
            TryReport(() => RLog.Msg(ConsoleColor.Magenta, "Loaded mod " + mod.Manifest.Name + " for game " + manifest.GameVersion));
        }

        private static void TryReport(Action report)
        {
            try { report(); }
            catch { /* Compatibility diagnostics must not change activation outcomes. */ }
        }
    }
}
