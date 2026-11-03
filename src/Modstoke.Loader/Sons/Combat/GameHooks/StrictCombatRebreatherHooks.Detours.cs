// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using BepInEx.Unity.IL2CPP.Hook;
using HarmonyLib;
using Il2CppInterop.Common;
using global::Sons.Inventory;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatRebreatherHooks
    {
        private static RebreatherVolumeSetter[] PrepareVolumeSetters()
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("Native rebreather ownership requires the supported Windows x64 game.");

            var type = typeof(VolumeContainerItemInstanceModule);
            var targets = new[]
            {
                new RebreatherVolumeSetter(type, "set_MaxVolume",
                    "B204593A7FA696723D9F509241888201778B9B0355F4F6C817CA86307F902431"),
                new RebreatherVolumeSetter(type, "set_CurrentVolume",
                    "67C78CD773DC030CA86552B3CF8F59CB7AA65C3F44A3AB59AA6C585D802CFA09")
            };
            if (targets[0].Entry == targets[1].Entry)
                throw new InvalidOperationException("Native rebreather setter entries unexpectedly overlap.");
            return targets;
        }

        // These Win64 bodies also serve unrelated receivers. Compare only retained
        // module pointers and preserve the original arguments for every other caller.
        private sealed class RebreatherVolumeSetter
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void FloatSetter(nint receiver, float value, nint methodInfo);

            private readonly string _name;
            private readonly string _sha256;
            private INativeDetour _detour;
            private FloatSetter _callback, _original;

            internal RebreatherVolumeSetter(Type type, string name, string sha256)
            {
                _name = type.Name + "." + name;
                _sha256 = sha256;
                var method = AccessTools.Method(type, name, new[] { typeof(float) });
                if (method == null || method.IsStatic || method.ReturnType != typeof(void))
                    throw new MissingMethodException("Native rebreather setter unavailable: " + _name);
                var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
                if (field?.GetValue(null) is not nint methodInfo || methodInfo == 0)
                    throw new InvalidOperationException("Native rebreather setter has no method info: " + _name);
                Entry = (nint)BitConverter.ToInt64(ReadNativeRebreatherMemory(methodInfo, IntPtr.Size), 0);
                if (Entry == 0) throw new InvalidOperationException("Native rebreather setter has no entry: " + _name);
                RequireCode();
            }

            internal nint Entry { get; }

            internal void Install()
            {
                RequireCode();
                // The owning array retains this instance after any installation attempt.
                _callback = Invoke;
                _detour = INativeDetour.Create(Entry, _callback);
                Volatile.Write(ref _original, _detour.GenerateTrampoline<FloatSetter>());
                _detour.Apply();
            }

            private void RequireCode()
            {
                var hash = Convert.ToHexString(SHA256.HashData(ReadNativeRebreatherMemory(Entry, 16)));
                if (!string.Equals(hash, _sha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("Native rebreather setter code is unsupported or already patched: " + _name);
            }

            private void Invoke(nint receiver, float value, nint methodInfo)
            {
                try
                {
                    if (!BlocksModulePointer(receiver))
                        Volatile.Read(ref _original)(receiver, value, methodInfo);
                }
                catch (Exception error) { OwnershipFailure(error); }
            }
        }

        private static byte[] ReadNativeRebreatherMemory(nint address, int length)
        {
            if (address == 0 || length <= 0 || length > 16)
                throw new ArgumentOutOfRangeException(nameof(length), "Native rebreather memory reads require a nonzero address and at most 16 bytes.");
            var bytes = new byte[length];
            if (!ReadNativeRebreatherProcessMemory(new nint(-1), address, bytes, (nuint)length, out var read))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read native rebreather setter memory.");
            if (read != (nuint)length)
                throw new InvalidOperationException("Native rebreather setter memory read was incomplete.");
            return bytes;
        }

        [DllImport("kernel32.dll", EntryPoint = "ReadProcessMemory", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadNativeRebreatherProcessMemory(nint process, nint address,
            [Out] byte[] buffer, nuint size, out nuint read);
    }
}
