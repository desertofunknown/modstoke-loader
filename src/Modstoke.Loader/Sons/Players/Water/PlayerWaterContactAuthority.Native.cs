// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Common;
using Modstoke.Loader;
using global::Sons.Physics;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerWaterContactAuthority
    {
        private static readonly Dictionary<nint, PlayerWaterContactAuthority> Owners = new();
        private static readonly Dictionary<nint, int> NativeDepth = new();
        private static bool _hooksAttempted, _hooksReady;
        private static int _hookThread, _hooksFrame, _offThreadWriter;
        private static string _hookFailure;

        private static bool EnsureHooks(HarmonySupportSnapshot support, out string detail)
        {
            detail = "Water holds require recognized Windows x64 code and qualified native Harmony support.";
            if (_hooksAttempted)
            { detail = _hookFailure ?? detail; return _hooksReady && _hookThread == Environment.CurrentManagedThreadId; }
            if (support?.SafeForHarmonyPatches != true || !OperatingSystem.IsWindows()
                || RuntimeInformation.ProcessArchitecture != Architecture.X64 || !GameBuild.IsRecognized)
                return false;
            _hooksAttempted = true; _hookThread = Environment.CurrentManagedThreadId;
            HarmonyLib.Harmony harmony = null;
            try
            {
                var methods = new[]
                {
                    Entry("OnEnable", 0x2E7BC50, 160, "508659E0D3916D425B85FE9C5937B5A48558326735A16284889469DC284A1C87"),
                    Entry("OnDisable", 0x2E7BCF0, 48, "8D0BE26C4A5233A7FFBB0124587D74E06D28D27B006DEBE8A56AEFC0ADE11E5A"),
                    Entry("ManagedUpdate", 0x2E7BD20, 784, "4D4F24FED4C4CE41B7D0DDB48F06718A89ED58CAC020DA43C429437A46653B9E"),
                    Entry("OnTriggerEnter", 0x2E7EF00, 256, "6831384AD2F5637AF4DBD76A9B63DA08A7500FE11DFEFE4DC279F11984C4498A", typeof(Collider)),
                    // TryRemoveCollider is this SAME native entry. Do not hook the alias twice.
                    Entry("OnTriggerExit", 0x2E7F000, 144, "F3338DA6EBBF3920847407BBC7F0F23932D9DFD45CC074DD7798C433DF74713C", typeof(Collider)),
                    Entry("UpdateTriggerStay", 0x2E7C030, 880, "3D10081CAF28A37D09299FD4B5AAB9410D4E99C1BDD416BA74D309502233DE38"),
                    Entry("UpdateWaterDataCache", 0x2E7C6B0, 1952, "874F7078C6E78FCE4563F3DA3424B2E745119BCB9CB744330487BF0F2B088D8C"),
                    Entry("ForceReset", 0x2E7B830, 240, "211EB83DB975EF497C0EB67C883F6D57986F72580225C7D4670D3A2579403841"),
                    Entry("Reset", 0x2E7CFF0, 144, "7E3DEC58E3C65E383F842429E8A460890477F3CFBCB4008DA8F78EA76CDF2025"),
                    Entry("CheckInCave", 0x2E7D080, 464, "EFC0AF5317972D0438D97A8251EE805851E586C11B50D9BFDFE314E7E2ADF545"),
                    Entry("CheckInCaveInternal", 0x2E7D250, 304, "B15717F60B7B88AE8AEA16B67A3034C3370565914882AE7EE597104E4B9B31AB"),
                    Entry("CleanUpTrackedColliders", 0x2E7C3A0, 784, "10951F4A43E24ABEF86CBBAE7A6C8BF72F2F287415D1BEB18F5A2EA2A3554130"),
                    Entry("TryAddCollider", 0x2E7F090, 480, "DC60EA77BC35A3F1EFD9B7EE737B0BCFF6964DB18B881BF45C24C55B019D7D33", typeof(Collider)),
                    Entry("RemoveCollider", 0x2E7F270, 752, "7E914D5FEFB54C7B7A1B10F43A536DDB5EFBC2C13760893A44821FD7BC3B1EC8", typeof(Collider), typeof(int)),
                    Entry("ProcessInsideColliderResults", 0x2E7B450, 992, "8DFCA3CF8E47E9BC9BBD605D4DF998FCE81763284E66FF8E2E26AA37DFBE8CC7", typeof(int)),
                    Entry("ManualOnTriggerStayCheckColliderBox", 0x2E7AA60, 1152, "88264DF9CBF75145C4D6D15C1E6E27A589E2940D6EC30AF41B6170C482FA6E4C", typeof(BoxCollider)),
                    Entry("ManualOnTriggerStayCheckColliderCapsule", 0x2E7AEE0, 1392, "269261ACD66A2CB90ADD70C537DB8E1DDEDB92C3CB19A5D0EDA6D33BED32F879", typeof(CapsuleCollider)),
                    Entry("RemoveOverrideCheckInCaveAction", 0x2E7A9D0, 144, "9A2A74BDFEF06B740640A30211735486BBC5060A1C47A3FE55D8F662B1AE396D", typeof(Il2CppSystem.Func<bool>))
                };
                // SetOverrideCheckInCaveAction is a reference-store body at 0x7AD8B0.
                // Do not add a typed hook without qualifying all aliases of that store.
                // Its exact delegate reference is retained/rechecked, never invoked or copied.
                var inWater = Entry("get_InWater", 0x2E7A840, 272,
                    "0EEC805B22BC9C53E4650F3543777D37D01E34DDAA6A2CA935BB59E5518C7349");
                var inOcean = Entry("get_InOcean", 0x2E7A950, 96,
                    "AD1B1B28013453E7D3BDA01043A8849354226D74679F08CDF4EAAB70DB5A8175");
                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-player-water-contacts");
                foreach (var method in methods)
                    harmony.Patch(method,
                        prefix: new HarmonyMethod(typeof(PlayerWaterContactAuthority), nameof(BeforeWriter)) { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(PlayerWaterContactAuthority), nameof(AfterWriter)));
                harmony.Patch(AccessTools.DeclaredMethod(typeof(WaterLevelSensor), "ProcessInsideColliderResults", new[] { typeof(int) }),
                    prefix: new HarmonyMethod(typeof(PlayerWaterContactAuthority), nameof(BeforeServerResults)));
                harmony.Patch(AccessTools.DeclaredMethod(typeof(WaterLevelSensor), "OnTriggerEnter", new[] { typeof(Collider) }),
                    prefix: new HarmonyMethod(typeof(PlayerWaterContactAuthority), nameof(BeforeServerEnter)));
                harmony.Patch(inWater, prefix: new HarmonyMethod(typeof(PlayerWaterContactAuthority),
                    nameof(BeforePresentationInWater)) { priority = Priority.First });
                harmony.Patch(inOcean, prefix: new HarmonyMethod(typeof(PlayerWaterContactAuthority),
                    nameof(BeforePresentationInOcean)) { priority = Priority.First });
                _hooksFrame = Time.frameCount; _hooksReady = true;
                return true;
            }
            catch (Exception error)
            {
                _hookFailure = detail = "Native source-water gate installation refused: " + error.Message;
                try { harmony?.UnpatchSelf(); } catch (Exception) { }
                return false;
            }
        }

        private static MethodInfo Entry(string name, int rva, int length, string hash, params Type[] parameters)
        {
            var method = AccessTools.DeclaredMethod(typeof(WaterLevelSensor), name, parameters);
            var result = name.StartsWith("ManualOnTriggerStayCheck", StringComparison.Ordinal)
                || name is "get_InWater" or "get_InOcean" ? typeof(bool) : typeof(void);
            if (method == null || method.IsStatic || method.ReturnType != result)
                throw new MissingMethodException(typeof(WaterLevelSensor).FullName, name);
            var patches = HarmonyLib.Harmony.GetPatchInfo(method);
            if (patches != null && patches.Owners.Count != 0)
            {
                // The existing cart owner has already qualified these exact native bodies.
                // Compose a player-scoped Harmony prefix, never a second raw native detour.
                foreach (string owner in patches.Owners)
                    if (owner != RuntimeEntryPoint.PluginGuid + ".sons-cart-writers")
                        throw new InvalidOperationException("Water writer has an unknown patch owner: " + name);
                return method;
            }
            var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
            if (field?.GetValue(null) is not nint info || info == 0) throw new InvalidOperationException("Missing water method info: " + name);
            var entry = (nint)BitConverter.ToInt64(ReadMemory(info, IntPtr.Size), 0);
            var module = GetWaterModuleHandle("GameAssembly.dll");
            if (module == 0 || entry != module + rva || Convert.ToHexString(SHA256.HashData(ReadMemory(entry, length))) != hash)
                throw new InvalidOperationException("Water writer code changed or was already detoured: " + name);
            return method;
        }

        private static bool BeforeWriter(WaterLevelSensor __instance, MethodBase __originalMethod,
            bool __runOriginal, out nint __state)
        {
            __state = 0;
            if (!_hooksReady || !__runOriginal || __instance is null) return true;
            if (Environment.CurrentManagedThreadId != _hookThread)
            { Interlocked.Exchange(ref _offThreadWriter, 1); return true; }
            nint pointer = __instance.Pointer;
            if (PreparingServer != null && __originalMethod.Name == "OnEnable"
                && Same(__instance.gameObject, PreparingServer._target.gameObject))
            {
                PreparingServer._sensor = __instance;
                ServerSensors.Add(pointer, PreparingServer);
                return false;
            }
            if (ServerSensors.TryGetValue(pointer, out var server)
                && !BeforeServerWriter(server, __originalMethod.Name)) return false;
            if (Owners.TryGetValue(pointer, out var owner) && owner._retiredSource)
            {
                if (__originalMethod.Name != "OnDisable" || !owner._retiringSource || owner._runnerRemoved) return false;
            }
            else if (owner != null && !owner._nativeMutation)
            {
                if (__originalMethod.Name is "ManagedUpdate" or "UpdateTriggerStay" or "UpdateWaterDataCache"
                    or "CheckInCave" or "CheckInCaveInternal" or "CleanUpTrackedColliders") return false;
                // Contact, disable and explicit reset operations are not silently lost or
                // replayed. Invalidation makes this gate passive, including nested cache
                // writes, so an accepted native callback completes normally. The adapter
                // must discard the export instead of committing this continuation.
                owner._invalidated = true; owner._nativeMutation = true;
            }
            NativeDepth.TryGetValue(pointer, out int depth);
            NativeDepth[pointer] = checked(depth + 1); __state = pointer;
            return true;
        }

        private static Exception AfterWriter(nint __state, Exception __exception, MethodBase __originalMethod)
        {
            if (__state != 0 && Environment.CurrentManagedThreadId == _hookThread
                && ServerSensors.TryGetValue(__state, out var server))
            {
                if (__exception != null) server.Fail("Native water writer failed: " + __exception.Message);
                else if (__originalMethod.Name == "OnDisable") server._registered = false;
            }
            if (__state != 0 && __exception == null && Environment.CurrentManagedThreadId == _hookThread
                && Owners.TryGetValue(__state, out var source))
            {
                if (__originalMethod.Name == "OnDisable") source._runnerRemoved = true;
                else if (__originalMethod.Name == "OnEnable") source._runnerRemoved = false;
            }
            if (__state != 0 && Environment.CurrentManagedThreadId == _hookThread
                && NativeDepth.TryGetValue(__state, out int depth))
            {
                if (depth <= 1) NativeDepth.Remove(__state); else NativeDepth[__state] = depth - 1;
            }
            return __exception;
        }

        private static bool BeforeServerWriter(ServerContinuation server, string method)
        {
            if (method == "OnDisable")
            {
                if (!server._registered) return false;
                if (!server._disposing) server.Fail("The active server water sensor was disabled outside its owner.");
                return true;
            }
            if (server._registering && method is "CheckInCave" or "CheckInCaveInternal") return false;
            if (method == "OnEnable") return server._registering;
            if (NativeDepth.TryGetValue(server._sensor.Pointer, out int depth) && depth > 0) return true;
            if (!server.IsActive) return false;
            try { server.RequireCurrent(); return true; }
            catch (Exception error) { server.Fail(error.Message); return false; }
        }

        private static bool BeforeServerResults(WaterLevelSensor __instance, int count)
        {
            if (Environment.CurrentManagedThreadId != _hookThread) return true;
            if (!ServerSensors.TryGetValue(__instance.Pointer, out var owner)) return true;
            if (count >= 0 && __instance._insideColliderResult != null && count < __instance._insideColliderResult.Length)
                return true;
            owner.Fail("Native water overlap buffer is saturated; incomplete results cannot remove accepted contacts.");
            return false;
        }

        private static bool BeforeServerEnter(WaterLevelSensor __instance, Collider __0)
        {
            if (Environment.CurrentManagedThreadId != _hookThread || !ServerSensors.ContainsKey(__instance.Pointer)) return true;
            // Initial carrier physics can report an enter for a restored contact. It has
            // already been admitted; do not re-run that contact's native admission callback.
            return __instance._trackedColliders == null || !__instance._trackedColliders.Contains(__0);
        }

        private static byte[] ReadMemory(nint address, int length)
        {
            var bytes = new byte[length];
            if (!ReadWaterMemory(new nint(-1), address, bytes, (nuint)length, out var read) || read != (nuint)length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Native water code read failed.");
            return bytes;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
        private static extern nint GetWaterModuleHandle(string name);
        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "ReadProcessMemory")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadWaterMemory(nint process, nint address, [Out] byte[] bytes, nuint length, out nuint read);
    }
}
