// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Native stderr redirect, adapted from the BepInEx team's LGPL-2.1
// Runtimes/Unity/BepInEx.Unity.IL2CPP/RuntimeFixes/RedirectStdErrFix.cs.
// Upstream: https://github.com/BepInEx/BepInEx. See THIRD_PARTY_NOTICES.md
// and LICENSES/ for the retained upstream attribution and license texts.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Modstoke.Bootstrap
{
    internal static class StdErrRedirector
    {
        private const int StdErrorHandle = -12;
        private const uint GenericWrite = 0x40000000;
        private const int FileShareRead = 1;
        private const int CreateAlways = 2;
        private const int FileAttributeNormal = 0x80;

        internal static StdErrRedirectState Apply(
            Func<string, string, Type> loadRequiredType,
            Action<string> trace)
        {
            var state = new StdErrRedirectState
            {
                OpenAcaiOwned = true,
                BepInExRedirectStdErrFixUsed = false,
                Windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            };

            if (!state.Windows)
            {
                state.Detail = "Native stderr redirect is Windows-only; leaving stderr unchanged on this platform.";
                trace?.Invoke("Modstoke stderr redirect: " + state.Detail);
                return state;
            }

            var pathsType = loadRequiredType("BepInEx.Core", "BepInEx.Paths");
            var rootProperty = pathsType.GetProperty(
                "BepInExRootPath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (rootProperty == null)
            {
                throw new MissingMemberException(pathsType.FullName, "BepInExRootPath");
            }

            state.ErrorLogPath = Path.Combine((string)rootProperty.GetValue(null), "ErrorLog.log");
            state.RedirectAttempted = true;
            var errorFile = CreateFile(state.ErrorLogPath, GenericWrite, FileShareRead,
                IntPtr.Zero, CreateAlways, FileAttributeNormal, IntPtr.Zero);
            if (errorFile == new IntPtr(-1))
            {
                state.Win32Error = Marshal.GetLastWin32Error();
                state.Detail = "Failed to open error log file; skipping error redirection";
                LogWarning(loadRequiredType, state.Detail);
            }
            else
            {
                state.ErrorFileOpened = true;
                // Preserve upstream's process-lifetime native handle. Do not redirect
                // managed Console.Error or close a handle installed as native stderr.
                state.Redirected = SetStdHandle(StdErrorHandle, errorFile);
                if (!state.Redirected)
                {
                    state.Win32Error = Marshal.GetLastWin32Error();
                    CloseHandle(errorFile);
                    state.Detail = "Failed to redirect stderr; skipping error redirection";
                    LogWarning(loadRequiredType, state.Detail);
                }
                else
                {
                    state.Detail = "Modstoke redirected native stderr to the compatibility error log.";
                }
            }

            trace?.Invoke("Modstoke stderr redirect: " + state.Detail);
            return state;
        }

        private static void LogWarning(Func<string, string, Type> loadRequiredType, string message)
        {
            try
            {
                var loggerType = loadRequiredType("BepInEx.Core", "BepInEx.Logging.Logger");
                var levelType = loadRequiredType("BepInEx.Core", "BepInEx.Logging.LogLevel");
                var method = loggerType.GetMethod(
                    "Log", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { levelType, typeof(object) }, null);
                if (method == null)
                    return;

                method.Invoke(null, new[] { Enum.Parse(levelType, "Warning"), (object)message });
            }
            catch
            {
                // Optional stderr redirection remains nonfatal even if warning delivery fails.
                // The caller still returns Win32Error and writes the original detail to its trace.
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string fileName, uint desiredAccess, int shareMode,
            IntPtr securityAttributes, int creationDisposition, int flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetStdHandle(int standardHandle, IntPtr handle);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }

    internal sealed class StdErrRedirectState
    {
        internal bool OpenAcaiOwned { get; set; }
        internal bool BepInExRedirectStdErrFixUsed { get; set; }
        internal bool Windows { get; set; }
        internal bool RedirectAttempted { get; set; }
        internal bool ErrorFileOpened { get; set; }
        internal bool Redirected { get; set; }
        internal int Win32Error { get; set; }
        internal string ErrorLogPath { get; set; } = "";
        internal string Detail { get; set; } = "";
    }
}
