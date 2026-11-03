// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Loader runtime layout under the Modstoke folder. Installs from before the
// rename keep working from OpenACAI until Bootstrap has moved their data. The
// BepInEx plugin folder keeps compatibility mirrors of the state and manifest files.

using System;
using System.IO;
using BepInEx;

namespace Modstoke.Loader
{
    internal static class LoaderPaths
    {
        internal const string RootDirectoryName = "Modstoke";
        internal const string LegacyRootDirectoryName = "OpenACAI";
        internal const string RuntimeDirectoryName = "Runtime";
        internal const string CompatibilityDirectoryName = "Compatibility";
        internal const string ModsDirectoryName = "Mods";
        internal const string UserDataDirectoryName = "UserData";
        internal const string ValidationProbesDirectoryName = "ValidationProbes";
        internal const string BootstrapMirrorDirectoryName = "OpenACAILoader";
        internal const string LoaderStateFileName = "openacai-loader-state.json";
        internal const string ChainloaderStateFileName = "openacai-chainloader-state.json";
        internal const string Il2CppPreloaderStateFileName = "openacai-il2cpp-preloader-state.json";
        internal const string Il2CppChainloaderStateFileName = "openacai-il2cpp-chainloader-state.json";
        internal const string LoaderManifestFileName = "openacai-loader.manifest.json";
        internal const string FileManifestFileName = "openacai-loader-files.json";

        private static string _runtimeDirectory;
        private static string _modsDirectory;
        private static string _userDataDirectory;
        private static string _validationProbesDirectory;

        internal static string RootDirectory => Path.Combine(Paths.GameRootPath, RootDirectoryName);
        internal static string LegacyRootDirectory => Path.Combine(Paths.GameRootPath, LegacyRootDirectoryName);
        internal static string RuntimeDirectory => _runtimeDirectory ??= Resolve(RuntimeDirectoryName);
        internal static string RuntimeCompatibilityDirectory => Path.Combine(RuntimeDirectory, CompatibilityDirectoryName);
        internal static string ModsDirectory => _modsDirectory ??= Resolve(ModsDirectoryName);
        internal static string UserDataDirectory => _userDataDirectory ??= Resolve(UserDataDirectoryName);
        internal static string ValidationProbesDirectory => _validationProbesDirectory ??= Resolve(ValidationProbesDirectoryName);
        internal static string BootstrapMirrorDirectory => Path.Combine(Paths.PluginPath, BootstrapMirrorDirectoryName);
        internal static string LoaderStatePath => Path.Combine(RuntimeDirectory, LoaderStateFileName);
        internal static string ChainloaderStatePath => Path.Combine(RuntimeDirectory, ChainloaderStateFileName);
        internal static string Il2CppPreloaderStatePath => Path.Combine(RuntimeDirectory, Il2CppPreloaderStateFileName);
        internal static string Il2CppChainloaderStatePath => Path.Combine(RuntimeDirectory, Il2CppChainloaderStateFileName);
        internal static string LoaderManifestPath => Path.Combine(RuntimeDirectory, LoaderManifestFileName);
        internal static string FileManifestPath => Path.Combine(RuntimeDirectory, FileManifestFileName);
        internal static string LegacyBootstrapStatePath => Path.Combine(BootstrapMirrorDirectory, LoaderStateFileName);
        internal static string LegacyBootstrapManifestPath => Path.Combine(BootstrapMirrorDirectory, LoaderManifestFileName);
        internal static string LegacyBootstrapFileManifestPath => Path.Combine(BootstrapMirrorDirectory, FileManifestFileName);

        // Resolved once per process so that later folder creation cannot flip the choice.
        private static string Resolve(string name)
        {
            var current = Path.Combine(RootDirectory, name);
            if (Directory.Exists(current))
                return current;
            var legacy = Path.Combine(LegacyRootDirectory, name);
            return Directory.Exists(legacy) ? legacy : current;
        }

        internal static bool IsUnderLegacyRoot(string path)
        {
            return IsUnder(path, LegacyRootDirectory);
        }

        // Launchers and tools may still hand over an OpenACAI path that Bootstrap has since moved.
        internal static string MapMovedLegacyPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !IsUnderLegacyRoot(path) || Directory.Exists(path) || File.Exists(path))
                return path;
            var moved = Path.Combine(RootDirectory, Path.GetRelativePath(LegacyRootDirectory, path));
            return Directory.Exists(moved) || File.Exists(moved) ? moved : path;
        }

        private static bool IsUnder(string path, string directory)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }
}
