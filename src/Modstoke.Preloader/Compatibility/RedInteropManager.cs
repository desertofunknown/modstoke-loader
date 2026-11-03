// SPDX-License-Identifier: GPL-3.0-or-later
// Compatibility behavior adapted from RedLoader's Il2CppInteropManager.
// Copyright (c) RedLoader and BepInEx contributors. Copyright (C) 2026 Alex Cooper.

extern alias RedCompatibility;

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cpp2IL.Core;
using Il2CppInterop.Common;
using Il2CppInterop.Generator.Runners;
using Il2CppInterop.HarmonySupport;
using Il2CppInterop.Runtime.Startup;
using RedCompatibility::RedLoader;
using RedCompatibility::RedLoader.Unity.Common;
using RedCompatibility::RedLoader.Unity.IL2CPP;
using RedCompatibility::RedLoader.Unity.IL2CPP.Hook;
using RedCompatibility::RedLoader.Utils;
using LogLevel = BepInEx.Logging.LogLevel;
using PlatformHelper = RedCompatibility::MonoMod.Utils.PlatformHelper;

namespace Modstoke.Preloader;

internal static class RedInteropManager
{
    private static readonly ConfigCategory Category = new("il2cppinteropmanager", "Il2CppInteropManager");
    private static readonly ConfigEntry<bool> UpdateInteropAssemblies = Category.CreateEntry(
        "update_interop_assemblies", true, "Update Interop Assemblies",
        "Whether to generate IL2CPP support assemblies when they are outdated." + Environment.NewLine +
        "If disabled, assemblies in `_Redloader/Game` will not be updated between game or loader updates.");

    // Published for existing configuration consumers; the legacy manager did not implement dumping.
    private static readonly ConfigEntry<bool> DumpDummyAssemblies = Category.CreateEntry(
        "dump_dummy_assemblies", false, "Dump Dummy Assemblies",
        "Reserved compatibility setting for saving Cpp2IL dummy assemblies.");

    private static string _basePath;
    private static int _initializationAttempted;

    static RedInteropManager()
    {
        InteropConversion.Initialize();
    }

    internal static string GetGameAssemblyPath() => Environment.GetEnvironmentVariable("GAME_ASSEMBLY_PATH") ??
        Path.Combine(LoaderEnvironment.GameRootDirectory, "GameAssembly." + PlatformHelper.LibrarySuffix);

    private static string BasePath => _basePath ??= (Utility.GetCommandLineArgValue("--unhollowed-path") ?? "{Redloader}")
        .Replace("{Redloader}", LoaderEnvironment.LoaderDirectory)
        .Replace("{ProcessName}", LoaderEnvironment.GameExecutableName);

    internal static string GetInteropAssemblyPath() => Path.Combine(BasePath, "Game");

    private static string UnityBaseDirectory => Path.Combine(BasePath, "unity-libs");
    private static string RenameMapPath => Path.Combine(LoaderEnvironment.LoaderDirectory, "DeobfuscationMap.csv.gz");
    private static string HashPath => Path.Combine(GetInteropAssemblyPath(), "assembly-hash.txt");

    internal static void Initialize()
    {
        if (Interlocked.CompareExchange(ref _initializationAttempted, 1, 0) != 0)
            throw new InvalidOperationException("IL2CPP interop initialization has already been attempted.");

        const string databaseLocationVariable = "IL2CPP_INTEROP_DATABASES_LOCATION";
        var previousDatabaseLocation = Environment.GetEnvironmentVariable(databaseLocationVariable);
        Il2CppInteropRuntime runtime = null;
        try
        {
            Environment.SetEnvironmentVariable(databaseLocationVariable, GetInteropAssemblyPath());
            AppDomain.CurrentDomain.AssemblyResolve += ResolveInteropAssemblies;
            GenerateInteropAssemblies();

            var version = UnityInfo.Version;
            runtime = Il2CppInteropRuntime.Create(new RuntimeConfiguration
            {
                UnityVersion = new Version(version.Major, version.Minor, version.Build),
                DetourProvider = new Il2CppInteropDetourProvider()
            });
            runtime.AddLogger(new HarmonyExceptionLogger()).AddHarmonySupport().Start();
        }
        catch (Exception error)
        {
            var failure = ExceptionDispatchInfo.Capture(error);
            InteropConversion.Cleanup(ref failure, "dispose failed compatibility interop runtime", () => runtime?.Dispose());
            InteropConversion.Cleanup(ref failure, "detach compatibility interop assembly resolver", () =>
                AppDomain.CurrentDomain.AssemblyResolve -= ResolveInteropAssemblies);
            InteropConversion.Cleanup(ref failure, "restore compatibility interop database location", () =>
                Environment.SetEnvironmentVariable(databaseLocationVariable, previousDatabaseLocation));
            throw;
        }
    }

    private static Assembly ResolveInteropAssemblies(object sender, ResolveEventArgs args)
    {
        return Utility.TryResolveDllAssembly(new AssemblyName(args.Name), GetInteropAssemblyPath(), out var assembly)
            ? assembly
            : null;
    }

    private static string ComputeHash()
    {
        using var hash = MD5.Create();
        var fileBuffer = new byte[81920];

        static void HashFile(ICryptoTransform target, string path, byte[] buffer)
        {
            using var input = File.OpenRead(path);
            int count;
            while ((count = input.Read(buffer)) > 0)
                target.TransformBlock(buffer, 0, count, buffer, 0);
        }

        static void HashString(ICryptoTransform target, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            target.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
        }

        HashFile(hash, GetGameAssemblyPath(), fileBuffer);
        if (Directory.Exists(UnityBaseDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(UnityBaseDirectory, "*.dll", SearchOption.TopDirectoryOnly))
            {
                HashString(hash, Path.GetFileName(file));
                HashFile(hash, file, fileBuffer);
            }
        }
        if (File.Exists(RenameMapPath))
            HashFile(hash, RenameMapPath, fileBuffer);

        // Retain the existing marker recipe, including its historical generator-version input.
        HashString(hash, typeof(InteropAssemblyGenerator).Assembly.GetName().Version.ToString());
        HashString(hash, typeof(Cpp2IlApi).Assembly.GetName().Version.ToString());
        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Utility.ByteArrayToString(hash.Hash);
    }

    private static bool CheckIfGenerationRequired()
    {
        if (!File.Exists(HashPath))
            return true;

        if (ComputeHash() == File.ReadAllText(HashPath))
            return false;
        RLog.Msg("Detected outdated interop assemblies, will regenerate them now");
        return true;
    }

    private static void GenerateInteropAssemblies()
    {
        var directory = Path.GetFullPath(GetInteropAssemblyPath());
        Directory.CreateDirectory(Path.GetDirectoryName(directory));
        using var generationLock = new FileStream(directory + ".openacai-generation.lock",
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        StagedDirectory.Recover(directory);
        var existingDirectory = Directory.Exists(directory);
        var existingUsable = TryValidateExisting(directory, out var cacheIssue);
        if (!UpdateInteropAssemblies.Value && existingDirectory)
        {
            if (!existingUsable)
                throw new InvalidDataException("Existing interop output is invalid and automatic updates are disabled: " + cacheIssue);
            InteropGenerationDiagnostics.Log(LogLevel.Info,
                "Using validated existing interop files. Automatic updates are disabled; input freshness was not checked.");
            return;
        }

        try
        {
            StagedDirectory.Recover(UnityBaseDirectory);
            if (existingUsable && !CheckIfGenerationRequired())
                return;

            var version = UnityInfo.Version;
            var source = InteropSettings.DefaultLibrarySource.Replace("{VERSION}", $"{version.Major}.{version.Minor}.{version.Build}");
            UnityBaseLibraries.EnsureAvailable(UnityBaseDirectory, source, PreloaderLog.Log);
            InteropConversion.AddPlatformAssemblies(UnityBaseDirectory);
            var inputHash = ComputeHash();

            using var output = new StagedDirectory(directory);
            if (Directory.Exists(directory))
                CopySupportFiles(directory, output.StagingPath, true);

            var metadata = Path.Combine(LoaderEnvironment.GameRootDirectory,
                LoaderEnvironment.UnityGameDataDirectory, "il2cpp_data", "Metadata", "global-metadata.dat");
            var options = new InteropGenerationOptions
            {
                GameAssemblyPath = GetGameAssemblyPath(),
                Source = InteropConversion.BuildDummyAssemblies(GetGameAssemblyPath(), metadata, version),
                OutputDir = output.StagingPath,
                UnityBaseLibsDir = Directory.Exists(UnityBaseDirectory) ? UnityBaseDirectory : null
            };
            if (File.Exists(RenameMapPath))
            {
                RLog.Msg("Parsing deobfuscation rename mappings");
                options.ReadRenameMap(RenameMapPath);
            }

            RLog.Msg("Generating interop assemblies");
            InteropAssemblyGeneration.Run(options);
            InteropOutputValidator.Validate(output.StagingPath);
            if (ComputeHash() != inputHash)
                throw new IOException("Interop inputs changed during generation; the previous assemblies were retained.");
            File.WriteAllText(Path.Combine(output.StagingPath, "assembly-hash.txt"), inputHash);
            output.Promote();
            output.DeleteBackup();
        }
        catch (Exception error)
        {
            InteropGenerationDiagnostics.Log(LogLevel.Error, "Failed to generate Il2Cpp interop assemblies.", error);
            // Promotion can fail after moving the previous output; require a usable configured path.
            if (existingUsable && TryValidateExisting(directory, out _))
            {
                InteropGenerationDiagnostics.Log(LogLevel.Warning,
                    "Generation failed. Continuing with validated existing interop files; they may be stale.");
                return;
            }
            throw;
        }
    }

    private static bool TryValidateExisting(string directory, out string issue)
    {
        try
        {
            InteropOutputValidator.Validate(directory);
            issue = "";
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or BadImageFormatException or ArgumentException)
        {
            issue = InteropGenerationDiagnostics.Message(error);
            return false;
        }
    }

    private static void CopySupportFiles(string source, string destination, bool isRoot)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Cannot stage a linked interop directory: " + source);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cannot stage a linked interop file: " + entry);
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(target);
                CopySupportFiles(entry, target, false);
            }
            else if (!isRoot || !string.Equals(Path.GetExtension(entry), ".dll", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(entry, target);
            }
        }
    }

    internal static void PreloadInteropAssemblies()
    {
        var stopwatch = Stopwatch.StartNew();
        var loaded = 0;
        Parallel.ForEach(Directory.EnumerateFiles(GetInteropAssemblyPath()), file =>
        {
            if (!file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                return;
            var name = Path.GetFileName(file);
            if (name.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Il2Cppnetstandard.dll", StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                Assembly.LoadFrom(file);
                Interlocked.Increment(ref loaded);
            }
            catch (Exception error)
            {
                RLog.Warning($"Failed to preload {file} - {error}");
            }
        });
        RLog.Debug($"Preloaded {loaded} interop assemblies in {stopwatch.ElapsedMilliseconds}ms");
    }
}
