// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Startup banner and platform fact writer. This replaces the
// BepInEx Preloader ChainloaderLogHelper dependency while preserving the log
// lines users expect to see during early IL2CPP startup.

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Modstoke.Bootstrap
{
    internal static class EnvironmentBannerWriter
    {
        private const string ManifestFileName = "openacai-loader.manifest.json";
        private const string FileManifestFileName = "openacai-loader-files.json";
        private const string DefaultLoaderName = "Modstoke Loader";
        // Matches Branding's fallback so the console title and banner agree without a manifest.
        private const string DefaultLoaderVersion = "development";
        private const string BepInExCompatibilityRuntimeVersion = "6.0.0";

        internal static EnvironmentBannerState Write(
            string gameRoot,
            string runtimeDirectory,
            object preloaderLog,
            Action<string> trace)
        {
            var state = new EnvironmentBannerState
            {
                OpenAcaiOwned = true,
                BepInExChainloaderLogHelperUsed = false
            };

            var manifestPath = Path.Combine(runtimeDirectory ?? "", ManifestFileName);
            var fileManifestPath = Path.Combine(runtimeDirectory ?? "", FileManifestFileName);
            state.ManifestPath = manifestPath;
            state.FileManifestPath = fileManifestPath;
            var manifest = ReadManifestIdentity(manifestPath);
            var (coreVersion, coreCommit) = ReadComponentVersion(runtimeDirectory, "Modstoke.Loader.dll");
            state.LoaderName = manifest.Name ?? DefaultLoaderName;
            state.LoaderVersion = ChooseLoaderVersion(manifest.Version, coreVersion);
            state.BepInExCompatibilityRuntimeVersion = BepInExCompatibilityRuntimeVersion;
            state.BuiltFromCommit = coreCommit ?? ReadSourceTrailCommit(fileManifestPath) ?? "";
            state.Components = DescribeComponents(runtimeDirectory, coreVersion);
            state.ExpectedDotnetRuntime = manifest.DotnetRuntime ?? "";
            state.DotnetRuntime = RunningDotnetRuntime();
            state.ExecutablePath = FindExecutablePath(gameRoot);
            state.ExecutableLastWriteTime = GetLastWriteTime(state.ExecutablePath, gameRoot);
            state.ConsoleTitle = BuildConsoleTitle(state.LoaderName, state.LoaderVersion, state.ExecutablePath);
            state.Platform = GetPlatformString();
            state.ProcessBitness = GetProcessBitnessString();

            state.ConsoleTitleSet = TrySetConsoleTitle(state.ConsoleTitle);
            var bannerWritten = Log(preloaderLog, "LogMessage", state.ConsoleTitle);
            bannerWritten &= Log(
                preloaderLog,
                "LogMessage",
                "BepInEx compatibility runtime " + state.BepInExCompatibilityRuntimeVersion
                    + "; RedLoader/SonsSDK compatibility is handled by " + state.LoaderName);

            if (!string.IsNullOrWhiteSpace(state.BuiltFromCommit))
            {
                bannerWritten &= Log(preloaderLog, "LogMessage", "Built from commit " + state.BuiltFromCommit);
            }

            if (!string.IsNullOrWhiteSpace(state.Components))
            {
                bannerWritten &= Log(preloaderLog, "LogInfo", "Loader components: " + state.Components);
            }

            // The loader package ships its runtime, hostfxr and runtimeconfig as one set; a partial
            // update leaves the game on the old runtime while everything else expects the new one.
            if (state.ExpectedDotnetRuntime.Length > 0
                && state.DotnetRuntime.Length > 0
                && !string.Equals(state.ExpectedDotnetRuntime, state.DotnetRuntime, StringComparison.OrdinalIgnoreCase))
            {
                bannerWritten &= Log(
                    preloaderLog,
                    "LogWarning",
                    "This loader package expects .NET " + state.ExpectedDotnetRuntime + " but the game started .NET "
                        + state.DotnetRuntime + ". Reinstall the loader's dotnet folder, doorstop_config.ini and "
                        + "Modstoke/Runtime/Modstoke.Loader.runtimeconfig.json together.");
            }

            if (!string.IsNullOrWhiteSpace(state.ExecutableLastWriteTime))
            {
                bannerWritten &= Log(
                    preloaderLog,
                    "LogInfo",
                    "Game executable: " + Path.GetFileName(state.ExecutablePath) + ", dated " + state.ExecutableLastWriteTime);
            }

            bannerWritten &= Log(preloaderLog, "LogInfo", "System platform: " + state.Platform);
            bannerWritten &= Log(preloaderLog, "LogInfo", "Process bitness: " + state.ProcessBitness);

            state.BannerWritten = bannerWritten;
            state.Detail = bannerWritten
                ? "Modstoke submitted the loader banner and platform facts to the preloader logger without BepInEx ChainloaderLogHelper."
                : "Modstoke could not submit every loader banner line to the preloader logger.";
            trace?.Invoke("Modstoke environment banner initialized: " + state.Detail);
            return state;
        }

        private static string BuildConsoleTitle(string loaderName, string loaderVersion, string executablePath)
        {
            var processName = string.IsNullOrWhiteSpace(executablePath)
                ? "SonsOfTheForest"
                : Path.GetFileNameWithoutExtension(executablePath);
            if (string.IsNullOrWhiteSpace(processName))
            {
                processName = "SonsOfTheForest";
            }

            return loaderName + " v" + loaderVersion + " by OpenACAI - " + processName;
        }

        // Core is stamped with the loader version and an optional build label at compile time, so it
        // names the build that actually runs; the manifest only changes when a package is made.
        // An unstamped Core reports the SDK default (1.0.0) and never starts with the manifest version.
        private static string ChooseLoaderVersion(string manifestVersion, string coreVersion)
        {
            if (manifestVersion == null)
                return coreVersion ?? DefaultLoaderVersion;

            return coreVersion != null && coreVersion.StartsWith(manifestVersion, StringComparison.OrdinalIgnoreCase)
                ? coreVersion
                : manifestVersion;
        }

        // Startup files are installed separately from Core, so name each one's build.
        private static string DescribeComponents(string runtimeDirectory, string coreVersion)
        {
            var (bootstrapVersion, _) = ReadComponentVersion(runtimeDirectory, "Modstoke.Bootstrap.dll");
            var (preloaderVersion, _) = ReadComponentVersion(runtimeDirectory, "Modstoke.Preloader.dll");
            if (bootstrapVersion == null && preloaderVersion == null && coreVersion == null)
                return "";

            return "Bootstrap " + (bootstrapVersion ?? "missing")
                + ", Preloader " + (preloaderVersion ?? "missing")
                + ", Core " + (coreVersion ?? "missing");
        }

        private static (string Version, string Commit) ReadComponentVersion(string runtimeDirectory, string fileName)
        {
            try
            {
                var path = Path.Combine(runtimeDirectory ?? "", fileName);
                if (!File.Exists(path))
                    return (null, null);

                // Reads the version resource only; the assembly is not loaded.
                var productVersion = FileVersionInfo.GetVersionInfo(path).ProductVersion;
                if (string.IsNullOrWhiteSpace(productVersion))
                    return (null, null);

                var separator = productVersion.IndexOf('+');
                return separator < 0
                    ? (productVersion, null)
                    : (productVersion.Substring(0, separator), productVersion.Substring(separator + 1));
            }
            catch
            {
                return (null, null);
            }
        }

        private static string RunningDotnetRuntime()
        {
            const string prefix = ".NET ";
            var description = RuntimeInformation.FrameworkDescription ?? "";
            return description.StartsWith(prefix, StringComparison.Ordinal) ? description.Substring(prefix.Length) : description;
        }

        private static string FindExecutablePath(string gameRoot)
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                var mainModule = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(mainModule) && File.Exists(mainModule))
                {
                    return mainModule;
                }
            }
            catch
            {
            }

            try
            {
                var sonsExe = Path.Combine(gameRoot ?? "", "SonsOfTheForest.exe");
                if (File.Exists(sonsExe))
                {
                    return sonsExe;
                }

                if (Directory.Exists(gameRoot))
                {
                    foreach (var executable in Directory.GetFiles(gameRoot, "*.exe", SearchOption.TopDirectoryOnly))
                    {
                        if (!Path.GetFileName(executable).StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase))
                        {
                            return executable;
                        }
                    }
                }
            }
            catch
            {
            }

            return "";
        }

        private static string GetLastWriteTime(string executablePath, string gameRoot)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
                {
                    return File.GetLastWriteTime(executablePath).ToString();
                }

                if (!string.IsNullOrWhiteSpace(gameRoot) && Directory.Exists(gameRoot))
                {
                    return Directory.GetLastWriteTime(gameRoot).ToString();
                }
            }
            catch
            {
            }

            return "";
        }

        private static bool TrySetConsoleTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return false;
            }

            try
            {
                Console.Title = title;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetPlatformString()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var version = GetWindowsVersion();
                var windowsName = "Windows";
                if (version.Major >= 10 && version.Build >= 22000)
                {
                    windowsName = "Windows 11";
                }
                else if (version.Major >= 10)
                {
                    windowsName = "Windows 10";
                }
                else if (version.Major == 6 && version.Minor == 3)
                {
                    windowsName = "Windows 8.1";
                }
                else if (version.Major == 6 && version.Minor == 2)
                {
                    windowsName = "Windows 8";
                }
                else if (version.Major == 6 && version.Minor == 1)
                {
                    windowsName = "Windows 7";
                }

                return windowsName + " " + GetOperatingSystemBitnessSuffix();
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return "macOS " + GetOperatingSystemBitnessSuffix();
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return "Linux " + GetOperatingSystemBitnessSuffix();
            }

            return RuntimeInformation.OSDescription + " " + GetOperatingSystemBitnessSuffix();
        }

        private static Version GetWindowsVersion()
        {
            try
            {
                var info = new RtlOsVersionInfoEx
                {
                    Size = (uint)Marshal.SizeOf<RtlOsVersionInfoEx>()
                };
                if (RtlGetVersion(ref info) == 0)
                {
                    return new Version((int)info.MajorVersion, (int)info.MinorVersion, (int)info.BuildNumber);
                }
            }
            catch
            {
            }

            return Environment.OSVersion.Version;
        }

        private static string GetOperatingSystemBitnessSuffix()
        {
            var suffix = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit";
            if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
            {
                return suffix + " ARM64";
            }

            if (RuntimeInformation.OSArchitecture == Architecture.Arm)
            {
                return suffix + " ARM";
            }

            return suffix;
        }

        private static string GetProcessBitnessString()
        {
            return (Environment.Is64BitProcess ? "64-bit" : "32-bit") + " (" + GetArchitectureName(RuntimeInformation.ProcessArchitecture) + ")";
        }

        private static string GetArchitectureName(Architecture architecture)
        {
            if (architecture == Architecture.X64)
            {
                return "x64";
            }

            if (architecture == Architecture.X86)
            {
                return "x86";
            }

            if (architecture == Architecture.Arm64)
            {
                return "arm64";
            }

            if (architecture == Architecture.Arm)
            {
                return "arm";
            }

            return architecture.ToString().ToLowerInvariant();
        }

        private static (string Name, string Version, string DotnetRuntime) ReadManifestIdentity(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return (null, null, null);
                }

                using (var document = JsonDocument.Parse(File.ReadAllBytes(path)))
                {
                    var root = document.RootElement;
                    var dotnetRuntime = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("runtime", out var runtime)
                        ? ReadRootString(runtime, "dotnetRuntime")
                        : null;
                    return (ReadRootString(root, "name"), ReadRootString(root, "version"), dotnetRuntime);
                }
            }
            catch
            {
            }

            return (null, null, null);
        }

        private static string ReadRootString(JsonElement root, string propertyName)
        {
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(propertyName, out var property)
                || property.ValueKind != JsonValueKind.String)
                return null;

            var value = property.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static string ReadSourceTrailCommit(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                using (var document = JsonDocument.Parse(File.ReadAllBytes(path)))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object
                        || !document.RootElement.TryGetProperty("sourceTrail", out var sourceTrail)
                        || sourceTrail.ValueKind != JsonValueKind.Object
                        || !sourceTrail.TryGetProperty("openAcaiLoader", out var openAcaiLoader)
                        || openAcaiLoader.ValueKind != JsonValueKind.Object
                        || !openAcaiLoader.TryGetProperty("commit", out var commit)
                        || commit.ValueKind != JsonValueKind.String)
                    {
                        return null;
                    }

                    return commit.GetString();
                }
            }
            catch
            {
                return null;
            }
        }

        private static bool Log(object logSource, string methodName, object data)
        {
            if (logSource == null)
            {
                return false;
            }

            try
            {
                var method = logSource.GetType().GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(object) },
                    null);
                if (method == null)
                    return false;

                method.Invoke(logSource, new[] { data });
                return true;
            }
            catch
            {
                return false;
            }
        }

        [DllImport("ntdll.dll", ExactSpelling = true)]
        private static extern int RtlGetVersion(ref RtlOsVersionInfoEx versionInfo);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RtlOsVersionInfoEx
        {
            public uint Size;
            public uint MajorVersion;
            public uint MinorVersion;
            public uint BuildNumber;
            public uint PlatformId;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string CsdVersion;

            public ushort ServicePackMajor;
            public ushort ServicePackMinor;
            public ushort SuiteMask;
            public byte ProductType;
            public byte Reserved;
        }
    }

    internal sealed class EnvironmentBannerState
    {
        internal bool OpenAcaiOwned { get; set; }
        internal bool BepInExChainloaderLogHelperUsed { get; set; }
        internal bool BannerWritten { get; set; }
        internal bool ConsoleTitleSet { get; set; }
        internal string LoaderName { get; set; } = "";
        internal string LoaderVersion { get; set; } = "";
        internal string BepInExCompatibilityRuntimeVersion { get; set; } = "";
        internal string ConsoleTitle { get; set; } = "";
        internal string Platform { get; set; } = "";
        internal string ProcessBitness { get; set; } = "";
        internal string BuiltFromCommit { get; set; } = "";
        internal string Components { get; set; } = "";
        internal string ExpectedDotnetRuntime { get; set; } = "";
        internal string DotnetRuntime { get; set; } = "";
        internal string ExecutablePath { get; set; } = "";
        internal string ExecutableLastWriteTime { get; set; } = "";
        internal string ManifestPath { get; set; } = "";
        internal string FileManifestPath { get; set; } = "";
        internal string Detail { get; set; } = "";
    }
}
