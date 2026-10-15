// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned runtime layout. Legacy loader folders are compatibility
// mirrors only while the native IL2CPP bootstrap replacement is being built.

using System.IO;
using BepInEx;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenACAIPaths
    {
        internal const string RootDirectoryName = "OpenACAI";
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

        internal static string RootDirectory => Path.Combine(Paths.GameRootPath, RootDirectoryName);
        internal static string RuntimeDirectory => Path.Combine(RootDirectory, RuntimeDirectoryName);
        internal static string RuntimeCompatibilityDirectory => Path.Combine(RuntimeDirectory, CompatibilityDirectoryName);
        internal static string ModsDirectory => Path.Combine(RootDirectory, ModsDirectoryName);
        internal static string UserDataDirectory => Path.Combine(RootDirectory, UserDataDirectoryName);
        internal static string ValidationProbesDirectory => Path.Combine(RootDirectory, ValidationProbesDirectoryName);
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
    }
}
