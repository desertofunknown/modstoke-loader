// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
// Cecil registration preserves the AppDomain contract used by BepInEx Utility.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace OpenACAI.Endnight.Loader.Bootstrap
{
    internal static class OpenACAIBootstrapEnvironment
    {
        private const string TrustedPlatformAssemblies = "TRUSTED_PLATFORM_ASSEMBLIES";

        internal static OpenACAIBootstrapPlatformState CapturePlatform() => new()
        {
            OsDescription = RuntimeInformation.OSDescription,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Is64BitProcess = Environment.Is64BitProcess,
            Is64BitOperatingSystem = Environment.Is64BitOperatingSystem
        };

        internal static OpenACAICecilPlatformAssemblyState RegisterCecilDirectory(string directory)
        {
            var state = new OpenACAICecilPlatformAssemblyState { Directory = directory ?? "" };
            try
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    state.Status = "no-directory";
                    return state;
                }

                state.Directory = Path.GetFullPath(directory);
                if (!Directory.Exists(state.Directory))
                {
                    state.Status = "missing-directory";
                    return state;
                }

                var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                var files = Directory.GetFiles(state.Directory, "*.dll", SearchOption.TopDirectoryOnly);
                Array.Sort(files, comparer);
                var data = AppDomain.CurrentDomain.GetData(TrustedPlatformAssemblies);
                if (data != null && data is not string)
                    throw new InvalidOperationException("The Cecil platform assembly list is not a string; leaving it unchanged.");

                var current = data as string ?? "";
                var known = new HashSet<string>(current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries), comparer);
                var added = files.Where(known.Add).ToArray();
                if (added.Length != 0)
                {
                    // Preserve the host's existing order and only append new paths after enumeration succeeds.
                    var suffix = string.Join(Path.PathSeparator, added);
                    var updated = current.Length == 0 ? suffix : current + Path.PathSeparator + suffix;
                    AppDomain.CurrentDomain.SetData(TrustedPlatformAssemblies, updated);
                }

                state.AddedCount = added.Length;
                state.Status = added.Length == 0 ? "unchanged" : "registered";
            }
            catch (Exception ex)
            {
                state.Status = "failed";
                state.Error = ex.GetType().Name + ": " + ex.Message;
            }
            return state;
        }
    }

    internal sealed class OpenACAIBootstrapPlatformState
    {
        public string Source => "System.Runtime.InteropServices.RuntimeInformation";
        public bool BepInExPlatformSetupUsed => false;
        public bool MonoModPlatformStateChanged => false;
        public string OsDescription { get; init; }
        public string OsArchitecture { get; init; }
        public string ProcessArchitecture { get; init; }
        public bool Is64BitProcess { get; init; }
        public bool Is64BitOperatingSystem { get; init; }
    }

    internal sealed class OpenACAICecilPlatformAssemblyState
    {
        public string Directory { get; set; } = "";
        public string Status { get; set; } = "not-started";
        public int AddedCount { get; set; }
        public string Error { get; set; } = "";
    }
}
