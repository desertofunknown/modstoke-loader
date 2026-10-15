// SPDX-License-Identifier: GPL-3.0-or-later
// Console compatibility behavior follows BepInEx's Windows console helpers.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class WindowsConsoleSession : IDisposable
    {
        private static readonly int[] StandardHandleIds = { -10, -11, -12 };
        private readonly IntPtr[] _originalHandles = new IntPtr[3];
        private readonly IntPtr[] _installedHandles = new IntPtr[3];
        private bool _allocatedConsole;

        internal SafeFileHandle ConsoleHandle { get; private set; }
        internal SafeFileHandle OriginalOutput { get; private set; }

        internal WindowsConsoleSession()
        {
            for (var i = 0; i < StandardHandleIds.Length; i++)
                _installedHandles[i] = _originalHandles[i] = GetStdHandle(StandardHandleIds[i]);

            var output = _originalHandles[1];
            if (output != IntPtr.Zero && output != new IntPtr(-1))
                OriginalOutput = new SafeFileHandle(output, ownsHandle: false);
        }

        internal void Attach()
        {
            if (GetConsoleWindow() == IntPtr.Zero)
            {
                var foreground = GetForegroundWindow();
                _allocatedConsole = AllocConsole();
                var error = Marshal.GetLastWin32Error();
                if (!_allocatedConsole && error != 5)
                    throw new Win32Exception(error, "Could not allocate a console.");

                if (_allocatedConsole)
                {
                    for (var i = 0; i < StandardHandleIds.Length; i++)
                        _installedHandles[i] = GetStdHandle(StandardHandleIds[i]);
                    if (foreground != IntPtr.Zero)
                        SetForegroundWindow(foreground);
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

        internal Encoding SetOutputEncoding(uint codepage)
        {
            var encoding = codepage == 65001
                ? new UTF8Encoding(false)
                : CodePagesEncodingProvider.Instance.GetEncoding(checked((int)codepage))
                    ?? Encoding.GetEncoding(checked((int)codepage));
            if (!SetConsoleOutputCP(codepage))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not set console output encoding.");
            return encoding;
        }

        internal void SetForegroundColor(ConsoleColor color)
        {
            if (GetConsoleScreenBufferInfo(ConsoleHandle, out var info))
                SetConsoleTextAttribute(ConsoleHandle, (ushort)((info.Attributes & ~15) | (int)color));
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
            // Restore only slots still holding our handles. In particular, stderr may
            // already belong to the preloader's later error-file redirect.
            for (var i = 0; i < StandardHandleIds.Length; i++)
            {
                if (_installedHandles[i] != _originalHandles[i]
                    && GetStdHandle(StandardHandleIds[i]) == _installedHandles[i]
                    && !SetStdHandle(StandardHandleIds[i], _originalHandles[i]))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not restore a standard handle.");
                }
            }

            // Failure retains ownership: never close a handle still installed as stdout.
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
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeConsole();
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, int share,
            IntPtr security, int disposition, int flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleOutputCP(uint codepage);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleTitle(string title);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetConsoleScreenBufferInfo(SafeFileHandle handle, out ScreenBufferInfo info);
        [DllImport("kernel32.dll")]
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
