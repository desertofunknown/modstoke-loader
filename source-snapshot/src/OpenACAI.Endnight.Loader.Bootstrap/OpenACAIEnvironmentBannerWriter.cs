// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned startup banner and platform fact writer. This replaces the
// BepInEx Preloader ChainloaderLogHelper dependency while preserving the log
// lines users expect to see during early IL2CPP startup.

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace OpenACAI.Endnight.Loader.Bootstrap
{
    internal static class OpenACAIEnvironmentBannerWriter
    {
        private const string ManifestFileName = "openacai-loader.manifest.json";
        private const string FileManifestFileName = "openacai-loader-files.json";
        private const string DefaultLoaderName = "Modstoke Loader";
        private const string DefaultLoaderVersion = "0.4.0-net11-preview";
        private const string BepInExCompatibilityRuntimeVersion = "6.0.0";

        internal static OpenACAIEnvironmentBannerState Write(
            string gameRoot,
            string runtimeDirectory,
            object preloaderLog,
            Action<string> trace)
        {
            var state = new OpenACAIEnvironmentBannerState
            {
                OpenAcaiOwned = true,
                BepInExChainloaderLogHelperUsed = false
            };

            var manifestPath = Path.Combine(runtimeDirectory ?? "", ManifestFileName);
            var fileManifestPath = Path.Combine(runtimeDirectory ?? "", FileManifestFileName);
            state.ManifestPath = manifestPath;
            state.FileManifestPath = fileManifestPath;
            var manifestName = ReadRootString(manifestPath, "name");
            state.LoaderName = manifestName == "OpenACAI Endnight Loader"
                ? DefaultLoaderName
                : manifestName ?? DefaultLoaderName;
            state.LoaderVersion = ReadRootString(manifestPath, "version") ?? DefaultLoaderVersion;
            state.BepInExCompatibilityRuntimeVersion = BepInExCompatibilityRuntimeVersion;
            state.BuiltFromCommit = ReadSourceTrailCommit(fileManifestPath) ?? "";
            state.ExecutablePath = FindExecutablePath(gameRoot);
            state.ExecutableLastWriteTime = GetLastWriteTime(state.ExecutablePath, gameRoot);
            state.ConsoleTitle = BuildConsoleTitle(state.LoaderName, state.LoaderVersion, state.ExecutablePath);
            state.Platform = GetPlatformString();
            state.ProcessBitness = GetProcessBitnessString();

            state.ConsoleTitleSet = TrySetConsoleTitle(state.ConsoleTitle);
            Log(preloaderLog, "LogMessage", state.ConsoleTitle + " (" + state.ExecutableLastWriteTime + ")");
            Log(
                preloaderLog,
                "LogMessage",
                "BepInEx compatibility runtime " + state.BepInExCompatibilityRuntimeVersion
                    + "; RedLoader/SonsSDK compatibility is handled by " + state.LoaderName);

            if (!string.IsNullOrWhiteSpace(state.BuiltFromCommit))
            {
                Log(preloaderLog, "LogMessage", "Built from commit " + state.BuiltFromCommit);
            }

            Log(preloaderLog, "LogInfo", "System platform: " + state.Platform);
            Log(preloaderLog, "LogInfo", "Process bitness: " + state.ProcessBitness);

            state.BannerWritten = true;
            state.Detail = "OpenACAI wrote the loader banner and platform facts without BepInEx ChainloaderLogHelper.";
            trace?.Invoke("OpenACAI environment banner initialized: " + state.Detail);
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

        private static string FindExecutablePath(string gameRoot)
        {
            try
            {
                var mainModule = Process.GetCurrentProcess().MainModule?.FileName;
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

        private static string ReadRootString(string path, string propertyName)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                using (var document = JsonDocument.Parse(File.ReadAllBytes(path)))
                {
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty(propertyName, out var property)
                        && property.ValueKind == JsonValueKind.String)
                    {
                        return property.GetString();
                    }
                }
            }
            catch
            {
            }

            return null;
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

        private static void Log(object logSource, string methodName, object data)
        {
            if (logSource == null)
            {
                return;
            }

            try
            {
                var method = logSource.GetType().GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(object) },
                    null);
                method?.Invoke(logSource, new[] { data });
            }
            catch
            {
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

    internal sealed class OpenACAIEnvironmentBannerState
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
        internal string ExecutablePath { get; set; } = "";
        internal string ExecutableLastWriteTime { get; set; } = "";
        internal string ManifestPath { get; set; } = "";
        internal string FileManifestPath { get; set; } = "";
        internal string Detail { get; set; } = "";
    }
}
