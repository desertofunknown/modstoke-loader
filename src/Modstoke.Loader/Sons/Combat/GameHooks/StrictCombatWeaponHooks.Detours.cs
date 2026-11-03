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
using global::Sons.Items.Core;
using global::Sons.Weapon;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatWeaponHooks
    {
        private static SharedAmmoSetter[] _sharedAmmoSetters;
        private static int _sharedAmmoFailureLogged;

        private static SharedAmmoSetter[] PrepareSharedAmmoSetters()
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("Native ammo ownership requires the supported Windows x64 game.");

            var module = typeof(RangedWeaponItemInstanceModule);
            var targets = new[]
            {
                SharedAmmoSetter.Integer(module, "SetAmmoCount", typeof(int), BlocksModulePointer, 16,
                    "EAC119BFC7BB79FA6CE678BAF54A9DA33F1AC8337C54238E900AA7B41EF0C4E2"),
                SharedAmmoSetter.Integer(module, "SetAmmoType", typeof(int), BlocksModulePointer, 16,
                    "1D9EC8AEFA4778D9A9353AF67B85BC88B9E75DC60E8FE14909652BFC758F9EB9"),
                SharedAmmoSetter.Integer(module, "SetAmmoUpgradeType", typeof(ArrowUpgradeItemData.Type), BlocksModulePointer, 16,
                    "A19B6234E04DE96A6970AB4D3588F60ECD109626E51311D4975E8B7C9A8A5479"),
                SharedAmmoSetter.Reference(typeof(RangedWeapon.Ammo), "SetAmmoProperties", typeof(AmmoProperties), BlocksAmmoPointer, 96,
                    "7D8DDE5AF9195A21D5F46E9AF2DA3BF626E88B66C75F2DDD3438C1708A7F8C6B"),
                SharedAmmoSetter.Reference(typeof(RangedWeapon), "SetAmmo", typeof(RangedWeapon.Ammo), BlocksWeaponPointer, 96,
                    "71EA88B800D6BE52628D63F39A9B6ECFE58EA34FB3B16237D02CB042682C18C6")
            };
            for (int i = 0; i < targets.Length; i++)
                for (int j = 0; j < i; j++)
                    if (targets[i].Entry == targets[j].Entry)
                        throw new InvalidOperationException("Native ammo setter entries unexpectedly overlap.");
            return targets;
        }

        private static void SharedAmmoFailure(Exception error)
        {
            _historyIncomplete = true;
            foreach (var owner in Volatile.Read(ref _owners)) owner.Retire();
            foreach (var prepared in Volatile.Read(ref _preparedEquipment)) prepared.Retire();
            if (Interlocked.Exchange(ref _sharedAmmoFailureLogged, 1) != 0) return;
            try { _warning?.Invoke("Strict combat native ammo setter failed: " + error.Message); }
            catch { }
        }

        // These Win64 bodies share receiver/integer-or-reference/MethodInfo calling conventions
        // across unrelated types, including structs. Compare only retained receiver addresses;
        // never construct an IL2CPP wrapper for an unmatched pointer. Byte qualification
        // rejects changed or previously patched code.
        // Keep every hook, callback and trampoline rooted after any installation attempt.
        private sealed class SharedAmmoSetter
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void IntegerSetter(nint receiver, int value, nint methodInfo);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void ReferenceSetter(nint receiver, nint value, nint methodInfo);

            private readonly string _name;
            private readonly int _length;
            private readonly string _sha256;
            private readonly bool _reference;
            private readonly Func<nint, bool> _blocks;
            private INativeDetour _detour;
            private IntegerSetter _integerCallback, _integerOriginal;
            private ReferenceSetter _referenceCallback, _referenceOriginal;

            private SharedAmmoSetter(Type type, string name, Type argument, bool reference, Func<nint, bool> blocks, int length, string sha256)
            {
                _name = type.Name + "." + name;
                _length = length;
                _sha256 = sha256;
                _reference = reference;
                _blocks = blocks;
                var method = AccessTools.Method(type, name, new[] { argument });
                if (method == null || method.IsStatic || method.ReturnType != typeof(void))
                    throw new MissingMethodException("Native ammo setter unavailable: " + _name);
                var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
                if (field?.GetValue(null) is not nint methodInfo || methodInfo == 0)
                    throw new InvalidOperationException("Native ammo setter has no method info: " + _name);
                Entry = (nint)BitConverter.ToInt64(ReadNativeAmmoMemory(methodInfo, IntPtr.Size), 0);
                if (Entry == 0) throw new InvalidOperationException("Native ammo setter has no entry: " + _name);
                RequireCode();
            }

            internal nint Entry { get; }

            internal static SharedAmmoSetter Integer(Type type, string name, Type argument,
                Func<nint, bool> blocks, int length, string sha256)
                => new(type, name, argument, false, blocks, length, sha256);

            internal static SharedAmmoSetter Reference(Type type, string name, Type argument,
                Func<nint, bool> blocks, int length, string sha256)
                => new(type, name, argument, true, blocks, length, sha256);

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
                    _integerCallback = InvokeInteger;
                    _detour = INativeDetour.Create(Entry, _integerCallback);
                    Volatile.Write(ref _integerOriginal, _detour.GenerateTrampoline<IntegerSetter>());
                }
                _detour.Apply();
            }

            private void RequireCode()
            {
                var hash = Convert.ToHexString(SHA256.HashData(ReadNativeAmmoMemory(Entry, _length)));
                if (!string.Equals(hash, _sha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("Native ammo setter code is unsupported or already patched: " + _name);
            }

            private void InvokeInteger(nint receiver, int value, nint methodInfo)
            {
                try
                {
                    if (!_blocks(receiver))
                        Volatile.Read(ref _integerOriginal)(receiver, value, methodInfo);
                }
                catch (Exception error) { SharedAmmoFailure(error); }
            }

            private void InvokeReference(nint receiver, nint value, nint methodInfo)
            {
                try
                {
                    if (!_blocks(receiver))
                        Volatile.Read(ref _referenceOriginal)(receiver, value, methodInfo);
                }
                catch (Exception error) { SharedAmmoFailure(error); }
            }
        }

        private static byte[] ReadNativeAmmoMemory(nint address, int length)
        {
            var bytes = new byte[length];
            if (!ReadNativeAmmoProcessMemory(new nint(-1), address, bytes, (nuint)length, out var read))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read native ammo setter memory.");
            if (read != (nuint)length)
                throw new InvalidOperationException("Native ammo setter memory read was incomplete.");
            return bytes;
        }

        [DllImport("kernel32.dll", EntryPoint = "ReadProcessMemory", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadNativeAmmoProcessMemory(nint process, nint address,
            [Out] byte[] buffer, nuint size, out nuint read);
    }
}
