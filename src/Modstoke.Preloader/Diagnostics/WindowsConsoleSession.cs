// SPDX-License-Identifier: GPL-3.0-or-later
// Console compatibility behavior follows BepInEx's Windows console helpers.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx.Logging;
using Microsoft.Win32.SafeHandles;

namespace Modstoke.Preloader
{
    internal sealed class WindowsConsoleSession : IDisposable
    {
        private const ushort SwShowNoActivate = 4;
        private const uint EnableExtendedFlags = 0x80;
        private const uint EnableQuickEditMode = 0x40;
        private const string OriginalOutputKey = "Modstoke.OriginalStandardOutput";
        private static readonly int[] StandardHandleIds = { -10, -11, -12 };
        private readonly IntPtr[] _originalHandles = new IntPtr[3];
        private readonly IntPtr[] _installedHandles = new IntPtr[3];
        private bool _allocatedConsole;
        private bool _disposed;
        private bool _restoreForeground;
        private ushort _originalForeground;
        private ushort _installedForeground;
        private bool _restoreOutputCodepage;
        private uint _originalOutputCodepage;
        private uint _installedOutputCodepage;

        internal SafeFileHandle ConsoleHandle { get; private set; }
        internal SafeFileHandle OriginalOutput { get; private set; }

        internal WindowsConsoleSession()
        {
            for (var i = 0; i < StandardHandleIds.Length; i++)
                _installedHandles[i] = _originalHandles[i] = GetStdHandle(StandardHandleIds[i]);

            var output = _originalHandles[1];
            if (output != IntPtr.Zero && output != new IntPtr(-1))
            {
                OriginalOutput = new SafeFileHandle(output, ownsHandle: false);
                // Host machine lines must reach the stdout the hosting agent reads, not this console.
                if (AppDomain.CurrentDomain.GetData(OriginalOutputKey) == null)
                    AppDomain.CurrentDomain.SetData(OriginalOutputKey, output);
            }
        }

        internal void Attach()
        {
            if (GetConsoleWindow() == IntPtr.Zero)
            {
                var foreground = GetForegroundWindow();
                _allocatedConsole = AllocateConsole();

                if (_allocatedConsole)
                {
                    for (var i = 0; i < StandardHandleIds.Length; i++)
                        _installedHandles[i] = GetStdHandle(StandardHandleIds[i]);
                    var consoleWindow = GetConsoleWindow();
                    if (foreground != IntPtr.Zero && consoleWindow != IntPtr.Zero
                        && GetForegroundWindow() == consoleWindow)
                        SetForegroundWindow(foreground);
                    DisableQuickEdit();
                }
            }

            ConsoleHandle = CreateFile("CONOUT$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (ConsoleHandle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open console output.");

            var output = ConsoleHandle.DangerousGetHandle();
            if (!SetStdHandle(StandardHandleIds[1], output))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not redirect native stdout.");
            _installedHandles[1] = output;
        }

        private static bool AllocateConsole()
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
            {
                var options = new AllocConsoleOptions
                {
                    Mode = AllocConsoleMode.NewWindow,
                    UseShowWindow = 1,
                    ShowWindow = SwShowNoActivate
                };
                int error;
                AllocConsoleResult result;
                try
                {
                    error = AllocConsoleWithOptions(ref options, out result);
                }
                catch (EntryPointNotFoundException)
                {
                    // Some Windows variants lack the export despite reporting a supported version.
                    return AllocateLegacyConsole();
                }

                Marshal.ThrowExceptionForHR(error, new IntPtr(-1));
                return result switch
                {
                    AllocConsoleResult.NewConsole => true,
                    AllocConsoleResult.ExistingConsole => false,
                    _ => throw new InvalidOperationException("Console allocation did not return a console.")
                };
            }

            return AllocateLegacyConsole();
        }

        private static bool AllocateLegacyConsole()
        {
            var allocated = AllocConsole();
            var lastError = Marshal.GetLastWin32Error();
            if (!allocated && lastError != 5)
                throw new Win32Exception(lastError, "Could not allocate a console.");
            return allocated;
        }

        private static void DisableQuickEdit()
        {
            var input = GetStdHandle(StandardHandleIds[0]);
            if (!GetConsoleMode(input, out var mode))
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error(),
                    "Could not read console input mode to disable Quick Edit.");
                InteropGenerationDiagnostics.Log(LogLevel.Warning, "Console input mode was left unchanged.", error);
                return;
            }

            if (!SetConsoleMode(input, (mode | EnableExtendedFlags) & ~EnableQuickEditMode))
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error(),
                    "Could not disable console Quick Edit.");
                InteropGenerationDiagnostics.Log(LogLevel.Warning, "Console Quick Edit remains unchanged.", error);
            }
        }

        internal Encoding SetOutputEncoding(uint codepage)
        {
            var encoding = codepage == 65001
                ? new UTF8Encoding(false)
                : CodePagesEncodingProvider.Instance.GetEncoding(checked((int)codepage))
                    ?? Encoding.GetEncoding(checked((int)codepage));
            var previousCodepage = _allocatedConsole ? 0 : GetConsoleOutputCP();
            if (!_allocatedConsole && previousCodepage == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read console output encoding.");
            if (!SetConsoleOutputCP(codepage))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not set console output encoding.");
            if (!_allocatedConsole && previousCodepage != codepage)
            {
                if (!_restoreOutputCodepage)
                    _originalOutputCodepage = previousCodepage;
                _installedOutputCodepage = codepage;
                _restoreOutputCodepage = true;
            }
            return encoding;
        }

        internal void SetForegroundColor(ConsoleColor color)
        {
            if (!GetConsoleScreenBufferInfo(ConsoleHandle, out var info)
                || (info.Attributes & 15) == (int)color
                || !SetConsoleTextAttribute(ConsoleHandle, (ushort)((info.Attributes & ~15) | (int)color)))
                return;
            if (!_allocatedConsole)
            {
                if (!_restoreForeground)
                    _originalForeground = (ushort)(info.Attributes & 15);
                _installedForeground = (ushort)color;
                _restoreForeground = true;
            }
        }

        internal static void PreventClose()
        {
            var window = GetConsoleWindow();
            if (window == IntPtr.Zero)
                return;
            var menu = GetSystemMenu(window, false);
            if (menu != IntPtr.Zero)
                DeleteMenu(menu, 0xF060, 0);
        }

        internal static void SetTitle(string title)
        {
            ArgumentNullException.ThrowIfNull(title);
            if (title.Length > 24500)
                throw new ArgumentOutOfRangeException(nameof(title), "Console titles cannot exceed 24500 characters.");
            if (!SetConsoleTitle(title))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not set the console title.");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            List<Exception> errors = null;
            // Restore only slots still holding our handles. In particular, stderr may
            // already belong to the preloader's later error-file redirect.
            for (var i = 0; i < StandardHandleIds.Length; i++)
            {
                if (_installedHandles[i] == _originalHandles[i])
                    continue;
                if (GetStdHandle(StandardHandleIds[i]) == _installedHandles[i]
                    && !SetStdHandle(StandardHandleIds[i], _originalHandles[i]))
                {
                    var error = new Win32Exception(Marshal.GetLastWin32Error(),
                        "Could not restore standard handle " + StandardHandleIds[i] + ".");
                    (errors ??= new List<Exception>()).Add(error);
                    continue;
                }
                // Retire each restored or superseded slot even if a later step fails.
                _installedHandles[i] = _originalHandles[i];
            }

            if (_restoreForeground)
            {
                if (GetConsoleScreenBufferInfo(ConsoleHandle, out var info)
                    && (info.Attributes & 15) == _installedForeground
                    && !SetConsoleTextAttribute(ConsoleHandle,
                        (ushort)((info.Attributes & ~15) | _originalForeground)))
                {
                    var error = new Win32Exception(Marshal.GetLastWin32Error(),
                        "Could not restore the console foreground color.");
                    (errors ??= new List<Exception>()).Add(error);
                }
                else
                    _restoreForeground = false;
            }

            if (_restoreOutputCodepage)
            {
                var currentCodepage = GetConsoleOutputCP();
                if (currentCodepage == 0)
                {
                    var error = new Win32Exception(Marshal.GetLastWin32Error(),
                        "Could not read the console output encoding for restoration.");
                    (errors ??= new List<Exception>()).Add(error);
                }
                else if (currentCodepage == _installedOutputCodepage
                    && !SetConsoleOutputCP(_originalOutputCodepage))
                {
                    var error = new Win32Exception(Marshal.GetLastWin32Error(),
                        "Could not restore the console output encoding.");
                    (errors ??= new List<Exception>()).Add(error);
                }
                else
                    _restoreOutputCodepage = false;
            }

            // Do not close handles needed by any remaining restoration step.
            if (errors != null)
            {
                if (errors.Count == 1)
                    ExceptionDispatchInfo.Capture(errors[0]).Throw();
                throw new AggregateException("Could not restore the console session.", errors);
            }
            ConsoleHandle?.Dispose();
            ConsoleHandle = null;
            if (_allocatedConsole)
            {
                if (!FreeConsole())
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not release the owned console.");
                _allocatedConsole = false;
            }
            OriginalOutput?.Dispose();
            OriginalOutput = null;
            _disposed = true;
        }

        private enum AllocConsoleMode : int { NewWindow = 1 }

        private enum AllocConsoleResult : int { NoConsole = 0, NewConsole = 1, ExistingConsole = 2 }

        [StructLayout(LayoutKind.Sequential)]
        private struct AllocConsoleOptions
        {
            public AllocConsoleMode Mode;
            public int UseShowWindow;
            public ushort ShowWindow;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Coord { public short X; public short Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SmallRect { public short Left; public short Top; public short Right; public short Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ScreenBufferInfo
        {
            public Coord Size;
            public Coord CursorPosition;
            public ushort Attributes;
            public SmallRect Window;
            public Coord MaximumWindowSize;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int id);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetStdHandle(int id, IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllocConsole();
        [DllImport("kernel32.dll")]
        private static extern int AllocConsoleWithOptions(ref AllocConsoleOptions options, out AllocConsoleResult result);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleMode(IntPtr handle, uint mode);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeConsole();
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, int share,
            IntPtr security, int disposition, int flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetConsoleOutputCP();
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleOutputCP(uint codepage);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleTitle(string title);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetConsoleScreenBufferInfo(SafeFileHandle handle, out ScreenBufferInfo info);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleTextAttribute(SafeFileHandle handle, ushort attributes);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr GetSystemMenu(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool revert);
        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteMenu(IntPtr menu, uint position, uint flags);
    }
}
