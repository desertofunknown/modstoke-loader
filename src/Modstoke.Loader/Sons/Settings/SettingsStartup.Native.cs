// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HarmonyLib;
using Il2CppInterop.Common;

namespace Modstoke.Loader
{
    internal static partial class SettingsStartup
    {
        // Qualify the exact native entry before Harmony takes ownership of its IL2CPP ABI.
        private static MethodInfo QualifyMethod(Type type, string name, int rva, int length, string hash,
            Type[] parameters = null, bool isStatic = false)
        {
            var method = AccessTools.DeclaredMethod(type, name, parameters ?? Type.EmptyTypes);
            if (method == null || method.IsStatic != isStatic || method.ReturnType != typeof(void))
                throw new MissingMethodException(type.FullName, name);
            var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
            if (field?.GetValue(null) is not nint methodInfo || methodInfo == 0)
                throw new InvalidOperationException("Missing settings lifecycle method information.");
            var entry = (nint)BitConverter.ToInt64(ReadMemory(methodInfo, IntPtr.Size), 0);
            var module = GetModuleHandle("GameAssembly.dll");
            if (module == 0 || entry != module + rva)
                throw new InvalidOperationException("Settings lifecycle native entry does not match the verified build: " + name);
            if (Convert.ToHexString(SHA256.HashData(ReadMemory(entry, length))) != hash)
                throw new InvalidOperationException("Settings lifecycle code changed or is already hooked: " + name);
            return method;
        }

        private static byte[] ReadMemory(nint address, int length)
        {
            var bytes = new byte[length];
            if (!ReadProcessMemory(new nint(-1), address, bytes, (nuint)length, out nuint read) || read != (nuint)length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Settings lifecycle code validation failed.");
            return bytes;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
        private static extern nint GetModuleHandle(string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(nint process, nint address, [Out] byte[] bytes, nuint size, out nuint read);
    }
}
