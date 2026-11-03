// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Modstoke.Bootstrap
{
    internal static class JobTempTrace
    {
        private const string TraceArgument = "--modstoke-native-allocation-trace";
        private const string SupportedUnitySha256 = "DD22770D205DF6125A8BE8F391DC8B55B2E7AE0B7BAABCFCCDDC3945A923EEB6";
        private const string CoverageNote = "Begins after il2cpp_init; early allocations not attributed.";
        private static IntPtr _nativeModule;

        internal static void Initialize(string gameRoot, string runtimeDirectory, Action<string> trace)
        {
            string sessionDirectory = null;
            string nativeModulePath = null;
            int? returnCode = null;
            try
            {
                var args = Environment.GetCommandLineArgs();
                var argumentIndex = Array.IndexOf(args, TraceArgument);
                if (argumentIndex < 0 || argumentIndex + 1 >= args.Length)
                    return;

                var captureRoot = Path.GetFullPath(Path.Combine(RootLayout.DataDirectory(gameRoot, "UserData"), "NativeAllocationCaptures"));
                var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootLayout.MapMovedLegacyPath(gameRoot, args[argumentIndex + 1])));
                if (!Directory.Exists(candidate) ||
                    !string.Equals(Path.GetDirectoryName(candidate), captureRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Trace session must be an existing direct child of NativeAllocationCaptures.");

                // Do not let junctions or symbolic links redirect the validated capture path.
                for (var directory = new DirectoryInfo(candidate); directory != null; directory = directory.Parent)
                {
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException("Trace session path must not contain reparse points.");
                }

                sessionDirectory = candidate;
                nativeModulePath = Path.GetFullPath(Path.Combine(runtimeDirectory, "Diagnostics", "Modstoke.JobTempTrace.dll"));
                var acceptedPath = Path.Combine(sessionDirectory, "accepted.json");
                File.Move(Path.Combine(sessionDirectory, "request.json"), acceptedPath);

                using var request = JsonDocument.Parse(File.ReadAllText(acceptedPath));
                var unitySha256 = request.RootElement.GetProperty("UnitySha256").GetString();
                var nativeSha256 = request.RootElement.GetProperty("NativeSha256").GetString();
                if (!string.Equals(unitySha256, SupportedUnitySha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Trace request does not target the supported UnityPlayer hash.");

                ValidateHash(Path.Combine(gameRoot, "UnityPlayer.dll"), unitySha256);
                ValidateHash(nativeModulePath, nativeSha256);
                if (GetModuleHandleW("UnityPlayer.dll") == IntPtr.Zero)
                    throw new InvalidOperationException("UnityPlayer.dll is not loaded.");

                // Never release this handle: Begin may publish hooks even if it fails later.
                _nativeModule = NativeLibrary.Load(nativeModulePath);
                var begin = Marshal.GetDelegateForFunctionPointer<BeginJobTempTrace>(
                    NativeLibrary.GetExport(_nativeModule, "ModstokeBeginJobTempTrace"));
                returnCode = begin(Path.Combine(sessionDirectory, "native-allocations.tsv"));
                if (returnCode != 0)
                    throw new InvalidOperationException("Native allocation trace returned " + returnCode + ".");

                WriteStatus(sessionDirectory, nativeModulePath, returnCode, null);
                Log(trace, "Native allocation trace started. " + CoverageNote);
            }
            catch (Exception ex)
            {
                Log(trace, "Native allocation trace failed: " + ex.GetType().Name + ": " + ex.Message);
                if (sessionDirectory == null)
                    return;

                try
                {
                    WriteStatus(sessionDirectory, nativeModulePath, returnCode, ex);
                }
                catch (Exception statusException)
                {
                    // Keep the original failure above even when its status cannot be written.
                    Log(trace, "Native allocation trace status write failed: " + statusException.Message);
                }
            }
        }

        private static void ValidateHash(string path, string expected)
        {
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Native allocation trace hash mismatch: " + Path.GetFileName(path) + ".");
        }

        private static void WriteStatus(string sessionDirectory, string nativeModulePath, int? returnCode, Exception error)
        {
            File.WriteAllText(Path.Combine(sessionDirectory, "bootstrap-trace.json"), JsonSerializer.Serialize(new
            {
                utc = DateTimeOffset.UtcNow.ToString("O"),
                pid = Environment.ProcessId,
                status = error == null ? "SUCCESS" : "FAILED",
                returnCode,
                nativeModulePath,
                coverageNote = CoverageNote,
                error = error == null ? null : error.GetType().FullName + ": " + error.Message
            }));
        }

        private static void Log(Action<string> trace, string message)
        {
            try
            {
                trace?.Invoke(message);
            }
            catch
            {
                // Optional diagnostics must not interrupt ordinary loader startup.
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private delegate int BeginJobTempTrace([MarshalAs(UnmanagedType.LPWStr)] string traceFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr GetModuleHandleW(string moduleName);
    }
}
