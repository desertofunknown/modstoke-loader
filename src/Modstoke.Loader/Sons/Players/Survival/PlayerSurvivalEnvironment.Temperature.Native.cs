// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Unity.IL2CPP.Hook;
using HarmonyLib;
using Il2CppInterop.Common;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerSurvivalEnvironmentTemperature
    {
        private static bool _hooksAttempted, _hooksInstalled;
        private static int _hookThread;
        private static string _hookFailure;
        // Retain callbacks/trampolines even after a partial installation failure. With
        // no registered owner, both detours are transparent to all native receivers.
        private static TemperatureCallback[] _callbacks;

        private static bool EnsureHooks(HarmonySupportSnapshot support, out string detail)
        {
            detail = "Temperature receiver hooks require recognized Windows x64 native code and safe hook support.";
            if (_hooksAttempted)
            {
                detail = _hookFailure ?? detail;
                return _hooksInstalled && _hookThread == Environment.CurrentManagedThreadId;
            }
            if (support?.SafeForHarmonyPatches != true || !OperatingSystem.IsWindows()
                || RuntimeInformation.ProcessArchitecture != Architecture.X64 || !GameBuild.IsRecognized)
                return false;
            _hooksAttempted = true;
            _hookThread = Environment.CurrentManagedThreadId;
            try
            {
                var enter = Entry(nameof(PlayerStats.OnEnteredTemperatureVolume), 0x311EAA0);
                var stay = Entry(nameof(PlayerStats.OnStayedTemperatureVolume), 0x311EAA0);
                var exit = Entry(nameof(PlayerStats.OnExitedTemperatureVolume), 0x311EAB0);
                if (enter != stay || enter == exit)
                    throw new InvalidOperationException("Native temperature callback aliasing changed.");
                // Enter and stay are the SAME 16-byte native body. Never detour it twice.
                _callbacks = new[]
                {
                    new TemperatureCallback(enter, true, "F30F1189A8020000C681B002000001C3"),
                    new TemperatureCallback(exit, false, "C681B002000000C3CCCCCCCCCCCCCCCC")
                };
                foreach (var callback in _callbacks) callback.Install();
                _hooksInstalled = true;
                return true;
            }
            catch (Exception error)
            {
                _hookFailure = detail = "Native temperature callback installation failed: " + error.Message;
                return false;
            }
        }

        private static nint Entry(string name, int rva)
        {
            var method = AccessTools.DeclaredMethod(typeof(PlayerStats), name, new[] { typeof(float) });
            if (method == null || method.IsStatic || method.ReturnType != typeof(void))
                throw new MissingMethodException(typeof(PlayerStats).FullName, name);
            var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
            if (field?.GetValue(null) is not nint info || info == 0)
                throw new InvalidOperationException("Temperature callback method information is unavailable: " + name);
            var module = GetTemperatureModuleHandle("GameAssembly.dll");
            var entry = (nint)BitConverter.ToInt64(ReadTemperatureMemory(info, IntPtr.Size), 0);
            if (module == 0 || entry != module + rva)
                throw new InvalidOperationException("Temperature callback entry changed: " + name);
            return entry;
        }

        private sealed class TemperatureCallback
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void Callback(nint receiver, float temperature, nint methodInfo);

            private readonly nint _entry;
            private readonly bool _inside;
            private readonly string _code;
            private INativeDetour _detour;
            private Callback _callback, _original;

            internal TemperatureCallback(nint entry, bool inside, string code)
            { _entry = entry; _inside = inside; _code = code; RequireCode(); }

            internal void Install()
            {
                RequireCode();
                _callback = Invoke;
                _detour = INativeDetour.Create(_entry, _callback);
                Volatile.Write(ref _original, _detour.GenerateTrampoline<Callback>());
                _detour.Apply();
            }

            private void RequireCode()
            {
                if (!string.Equals(Convert.ToHexString(ReadTemperatureMemory(_entry, 16)), _code, StringComparison.Ordinal))
                    throw new InvalidOperationException("Native temperature callback code is unsupported or already patched.");
            }

            private void Invoke(nint receiver, float temperature, nint methodInfo)
            {
                if (Owners.TryGetValue(receiver, out var owner))
                {
                    // Consume only the exact bound actor's writes. The survival runtime
                    // projects these fields later, under its single stat serialization owner.
                    owner.Receive(receiver, _inside, temperature);
                    return;
                }
                Volatile.Read(ref _original)(receiver, temperature, methodInfo);
            }
        }

        private static byte[] ReadTemperatureMemory(nint address, int length)
        {
            if (address == 0 || length <= 0 || length > 16) throw new ArgumentOutOfRangeException(nameof(length));
            var bytes = new byte[length];
            if (!ReadTemperatureProcessMemory(new nint(-1), address, bytes, (nuint)length, out var read))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read temperature callback memory.");
            if (read != (nuint)length) throw new InvalidOperationException("Temperature callback memory read was incomplete.");
            return bytes;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
        private static extern nint GetTemperatureModuleHandle(string name);

        [DllImport("kernel32.dll", EntryPoint = "ReadProcessMemory", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadTemperatureProcessMemory(nint process, nint address,
            [Out] byte[] buffer, nuint size, out nuint read);
    }
}
