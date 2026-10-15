// SPDX-License-Identifier: GPL-3.0-or-later
// Config names/defaults adapted from BepInEx's Il2CppInteropManager, copyright BepInEx contributors.
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.Common;
using MonoMod.Utils;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class InteropSettings
    {
        private readonly ConfigEntry<bool> _update;
        private readonly ConfigEntry<bool> _scanMethods;
        private readonly ConfigEntry<bool> _dumpAssemblies;
        private readonly ConfigEntry<bool> _preload;
        private readonly ConfigEntry<string> _librarySource;
        private readonly ConfigEntry<string> _obfuscationRegex;
        private readonly ConfigEntry<string> _metadataPath;

        internal InteropSettings()
        {
            var config = ConfigFile.CoreConfig;
            _update = config.Bind("IL2CPP", "UpdateInteropAssemblies", true,
                "Regenerate outdated IL2CPP support assemblies automatically. Disabling this keeps valid existing output without checking freshness.");
            _librarySource = config.Bind("IL2CPP", "UnityBaseLibrariesSource", "https://unity.bepinex.dev/libraries/{VERSION}.zip",
                "Unity base-library ZIP URL or cached filename. {VERSION} expands to the Unity version. Place a ZIP in unity-libs for offline use; an empty source keeps existing libraries.");
            _obfuscationRegex = config.Bind("IL2CPP", "UnhollowerDeobfuscationRegex", string.Empty,
                "Regex matching obfuscated names to replace with stable signature-based names.");
            _scanMethods = config.Bind("IL2CPP", "ScanMethodRefs", Environment.Is64BitProcess,
                "Use native cross-references to find dead methods and generate CallerCount attributes.");
            _dumpAssemblies = config.Bind("IL2CPP", "DumpDummyAssemblies", false,
                "Save Cpp2IL dummy assemblies into BepInEx/dummy.");
            var basePath = config.Bind("IL2CPP", "IL2CPPInteropAssembliesPath", "{BepInEx}",
                "Base folder for interop and unity-libs. Supports {BepInEx} and {ProcessName}.");
            _preload = config.Bind("IL2CPP", "PreloadIL2CPPInteropAssemblies", true,
                "Load interop assemblies before plugins. Some plugins require this; it can cause issues in some games.");
            _metadataPath = config.Bind("IL2CPP", "GlobalMetadataPath", "{GameDataPath}/il2cpp_data/Metadata/global-metadata.dat",
                "IL2CPP metadata path. Supports {BepInEx}, {ProcessName} and {GameDataPath}.");

            var configuredBase = ReadInteropPathOverride() ?? basePath.Value;
            var directory = Path.GetFullPath(configuredBase
                .Replace("{BepInEx}", Paths.BepInExRootPath).Replace("{ProcessName}", Paths.ProcessName));
            InteropDirectory = Path.Combine(directory, "interop");
            BaseLibraryDirectory = Path.Combine(directory, "unity-libs");
        }

        private static string ReadInteropPathOverride()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var i = 1; i + 1 < arguments.Length; i++)
                if (arguments[i] == "--unhollowed-path")
                    return arguments[i + 1];
            return null;
        }

        internal string InteropDirectory { get; }
        internal string BaseLibraryDirectory { get; }
        internal string GameAssemblyPath => Path.GetFullPath(Environment.GetEnvironmentVariable("BEPINEX_GAME_ASSEMBLY_PATH")
            ?? Path.Combine(Paths.GameRootPath, "GameAssembly." + PlatformHelper.LibrarySuffix));
        internal string RenameMapPath => Path.Combine(Paths.BepInExRootPath, "DeobfuscationMap.csv.gz");
        internal bool UpdateEnabled => _update.Value;
        internal bool ScanMethodRefs => _scanMethods.Value;
        internal bool DumpDummyAssemblies => _dumpAssemblies.Value;
        internal bool PreloadEnabled => _preload.Value;
        internal string ObfuscationRegex => _obfuscationRegex.Value;
        internal string UnityVersion => UnityInfo.Version.ToString();
        internal string MetadataPath => Path.GetFullPath(Path.Combine(Paths.GameRootPath, _metadataPath.Value
            .Replace("{BepInEx}", Paths.BepInExRootPath).Replace("{ProcessName}", Paths.ProcessName)
            .Replace("{GameDataPath}", Paths.GameDataPath)));
        internal string BaseLibrarySource
        {
            get
            {
                var version = UnityInfo.Version;
                return _librarySource.Value.Replace("{VERSION}", $"{version.Major}.{version.Minor}.{version.Build}");
            }
        }
    }
}
