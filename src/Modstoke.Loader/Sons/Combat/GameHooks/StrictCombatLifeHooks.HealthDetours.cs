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
using global::Sons.StatSystem;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatLifeHooks
    {
        private static SharedHealthSetter[] _sharedHealthSetters;
        private static int _sharedHealthFailureLogged;

        private static SharedHealthSetter[] PrepareSharedHealthSetters()
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("Native health ownership requires the supported Windows x64 game.");

            var stat = typeof(Stat);
            var targets = new[]
            {
                SharedHealthSetter.Float(stat, "SetDefaultValue", 16, "67F5B45569885F4172D24F2655A50DDC5964F269960E88E189377903CBD45999"),
                SharedHealthSetter.Float(stat, "SetBaseValue", 16, "CB1AC930AAECE30635E2122F621797AA1F7B744060651E01187EA0E06E5AE4B4"),
                SharedHealthSetter.Float(stat, "SetMin", 16, "E400A077AA42BD23541F90D81D6B8108854FC5DD481AF1FA6E511A4B3FAF276F"),
                SharedHealthSetter.Float(stat, "SetMax", 16, "3E57042F93367FDD22D5745A5527930455A15B47B9E2498C1AA1A67A577C3951"),
                SharedHealthSetter.Float(stat, "SetRegenRate", 16, "BD87C86A15434C5EDF2F5CD37F15F664A0A573F412952131B10C0DBB9C78EBE3"),
                SharedHealthSetter.Float(typeof(HealthStat), "SetMax", 64, "70A477074AAC6F0B7E71FC285D35351C96D180F1EA37CCE4650819F138A0B643"),
                SharedHealthSetter.Reference(stat, "SetFadeRateMethod", typeof(Il2CppSystem.Func<float>), 96,
                    "8B0E084A27B5B5C4C32C76B22E17359BF51B30E6C353B431266737605FD22107")
            };
            for (int i = 0; i < targets.Length; i++)
                for (int j = 0; j < i; j++)
                    if (targets[i].Entry == targets[j].Entry)
                        throw new InvalidOperationException("Native health setter entries unexpectedly overlap.");
            return targets;
        }

        private static bool BlocksHealthPointer(nint receiver)
        {
            // Other callers include structs. Do not read an object header or construct an
            // IL2CPP wrapper for an unmatched address. Captures retain their health object.
            foreach (var owner in Volatile.Read(ref _owners))
                if (owner.HealthPointer == receiver) return true;
            return false;
        }

        private static void SharedHealthFailure(Exception error)
        {
            _historyIncomplete = true;
            foreach (var owner in Volatile.Read(ref _owners)) owner.Retire();
            if (Interlocked.Exchange(ref _sharedHealthFailureLogged, 1) != 0) return;
            try { _warning?.Invoke("Strict combat native health setter failed: " + error.Message); }
            catch { }
        }

        // These Win64 bodies share receiver/float-or-reference/MethodInfo calling conventions
        // across unrelated types. Byte qualification rejects changed or previously patched code.
        // Keep every hook, callback and trampoline rooted after any installation attempt.
        private sealed class SharedHealthSetter
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void FloatSetter(nint receiver, float value, nint methodInfo);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void ReferenceSetter(nint receiver, nint value, nint methodInfo);

            private readonly string _name;
            private readonly int _length;
            private readonly string _sha256;
            private readonly bool _reference;
            private INativeDetour _detour;
            private FloatSetter _floatCallback, _floatOriginal;
            private ReferenceSetter _referenceCallback, _referenceOriginal;

            private SharedHealthSetter(Type type, string name, Type argument, bool reference, int length, string sha256)
            {
                _name = type.Name + "." + name;
                _length = length;
                _sha256 = sha256;
                _reference = reference;
                var method = AccessTools.Method(type, name, new[] { argument });
                if (method == null || method.IsStatic || method.ReturnType != typeof(void))
                    throw new MissingMethodException("Native health setter unavailable: " + _name);
                var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
                if (field?.GetValue(null) is not nint methodInfo || methodInfo == 0)
                    throw new InvalidOperationException("Native health setter has no method info: " + _name);
                Entry = (nint)BitConverter.ToInt64(ReadNativeHealthMemory(methodInfo, IntPtr.Size), 0);
                if (Entry == 0) throw new InvalidOperationException("Native health setter has no entry: " + _name);
                RequireCode();
            }

            internal nint Entry { get; }

            internal static SharedHealthSetter Float(Type type, string name, int length, string sha256)
                => new(type, name, typeof(float), false, length, sha256);

            internal static SharedHealthSetter Reference(Type type, string name, Type argument, int length, string sha256)
                => new(type, name, argument, true, length, sha256);

            internal void Install()
            {
                RequireCode();
                if (_reference)
                {
                    _referenceCallback = InvokeReference;
                    _detour = INativeDetour.Create(Entry, _referenceCallback);
                    Volatile.Write(ref _referenceOriginal, _detour.GenerateTrampoline<ReferenceSetter>());
                }
                else
                {
                    _floatCallback = InvokeFloat;
                    _detour = INativeDetour.Create(Entry, _floatCallback);
                    Volatile.Write(ref _floatOriginal, _detour.GenerateTrampoline<FloatSetter>());
                }
                _detour.Apply();
            }

            private void RequireCode()
            {
                var hash = Convert.ToHexString(SHA256.HashData(ReadNativeHealthMemory(Entry, _length)));
                if (!string.Equals(hash, _sha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("Native health setter code is unsupported or already patched: " + _name);
            }

            private void InvokeFloat(nint receiver, float value, nint methodInfo)
            {
                try
                {
                    if (!BlocksHealthPointer(receiver))
                        Volatile.Read(ref _floatOriginal)(receiver, value, methodInfo);
                }
                catch (Exception error) { SharedHealthFailure(error); }
            }

            private void InvokeReference(nint receiver, nint value, nint methodInfo)
            {
                try
                {
                    if (!BlocksHealthPointer(receiver))
                        Volatile.Read(ref _referenceOriginal)(receiver, value, methodInfo);
                }
                catch (Exception error) { SharedHealthFailure(error); }
            }
        }

        private static byte[] ReadNativeHealthMemory(nint address, int length)
        {
            var bytes = new byte[length];
            if (!ReadNativeHealthProcessMemory(new nint(-1), address, bytes, (nuint)length, out var read))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read native health setter memory.");
            if (read != (nuint)length)
                throw new InvalidOperationException("Native health setter memory read was incomplete.");
            return bytes;
        }

        [DllImport("kernel32.dll", EntryPoint = "ReadProcessMemory", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadNativeHealthProcessMemory(nint process, nint address,
            [Out] byte[] buffer, nuint size, out nuint read);
    }
}
