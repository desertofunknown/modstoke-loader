// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Common;
using RedLoader;
using Sons.Multiplayer;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenACAISonsPlayerLimitPatch
    {
        private const int ScanLength = 500;
        private const int PatchOffset = 3;
        private const int PatchLength = 9;
        private const uint MemoryCommit = 0x1000;
        private const uint PageExecuteReadWrite = 0x40;
        private const uint PageGuard = 0x100;
        private static readonly byte[] Signature = { 0x8B, 0x40, 0x28, 0x83, 0xF8, 0x08 };
        private const string SignatureMask = "?x?xxx";

        internal static void Apply()
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("The player-limit patch requires Windows x64.");

            var method = AccessTools.Method(typeof(ServerDedicatedConfig), nameof(ServerDedicatedConfig.Validate));
            if (method == null)
                throw new InvalidOperationException("Could not find ServerDedicatedConfig.Validate.");

            var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
            if (field?.GetValue(null) is not nint methodInfo || methodInfo == 0)
                throw new InvalidOperationException("ServerDedicatedConfig.Validate has no native method info.");

            var process = GetCurrentProcess();
            // IL2CPP method info starts with the native entry pointer, as in the SDK resolver.
            var entry = (nint)BitConverter.ToInt64(ReadMemory(process, methodInfo, IntPtr.Size), 0);
            if (entry == 0)
                throw new InvalidOperationException("ServerDedicatedConfig.Validate has no native entry point.");

            var bytes = ReadMemory(process, entry, ScanLength);
            var offset = FindPatchOffset(bytes);
            var address = checked(entry + offset);
            RLog.Msg(Color.Pink, $"Player Limit Address: {address:X}");
            WritePatch(process, address);
        }

        private static byte[] ReadMemory(nint process, nint address, int length)
        {
            var bytes = new byte[length];
            if (!ReadProcessMemory(process, address, bytes, (nuint)length, out var bytesRead))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read player-limit patch memory.");
            if (bytesRead != (nuint)length)
                throw new InvalidOperationException("The player-limit patch memory read was incomplete.");
            return bytes;
        }

        private static int FindPatchOffset(byte[] bytes)
        {
            for (var start = 0; start <= bytes.Length - Signature.Length; start++)
            {
                var matches = true;
                for (var i = 0; i < Signature.Length; i++)
                {
                    if (SignatureMask[i] != '?' && bytes[start + i] != Signature[i])
                    {
                        matches = false;
                        break;
                    }
                }

                if (!matches)
                    continue;

                var offset = start + PatchOffset;
                if (offset > bytes.Length - PatchLength)
                    throw new InvalidOperationException("The player-limit patch extends beyond its scan window.");
                return offset;
            }

            throw new InvalidOperationException("Could not find the player-limit patch signature.");
        }

        private static void WritePatch(nint process, nint address)
        {
            var infoSize = (nuint)Marshal.SizeOf<MemoryBasicInformation>();
            var queried = VirtualQuery(address, out var region, infoSize);
            if (queried == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not query player-limit patch memory.");
            if (queried != infoSize)
                throw new InvalidOperationException("The player-limit patch memory query was incomplete.");

            var regionOffset = checked((nuint)address - (nuint)region.BaseAddress);
            // VirtualProtect returns only the first page's protection. Keep the patch
            // within one uniform region so restoring it cannot alter a neighboring page.
            if (region.State != MemoryCommit || (region.Protect & PageGuard) != 0
                || (region.Protect & 0xF0) == 0 || regionOffset > region.RegionSize
                || region.RegionSize - regionOffset < PatchLength)
                throw new InvalidOperationException("The player-limit patch is not within one executable memory region.");

            var patch = new byte[PatchLength];
            Array.Fill(patch, (byte)0x90);
            if (!VirtualProtect(address, PatchLength, PageExecuteReadWrite, out var oldProtection))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not make player-limit patch memory writable.");

            Exception writeError = null;
            try
            {
                Marshal.Copy(patch, 0, address, patch.Length);
                if (!FlushInstructionCache(process, address, PatchLength))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not flush the player-limit instruction cache.");
            }
            catch (Exception error)
            {
                writeError = error;
                throw;
            }
            finally
            {
                if (!VirtualProtect(address, PatchLength, oldProtection, out _))
                {
                    var restoreError = new Win32Exception(Marshal.GetLastWin32Error(),
                        "Could not restore player-limit patch memory protection.");
                    if (writeError != null)
                        throw new AggregateException("Player-limit patching and protection restoration failed.", writeError, restoreError);
                    throw restoreError;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            public nint BaseAddress;
            public nint AllocationBase;
            public uint AllocationProtect;
            public ushort PartitionId;
            public nuint RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [DllImport("kernel32.dll")]
        private static extern nint GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(nint process, nint address, [Out] byte[] buffer,
            nuint size, out nuint bytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nuint VirtualQuery(nint address, out MemoryBasicInformation info, nuint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint oldProtection);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
    }
}
