// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using Iced.Intel;
using Il2CppInterop.Common;
using Modstoke.Sons;
using global::Sons.Multiplayer;

namespace Modstoke.Loader
{
    internal static class PlayerLimitPatch
    {
        private const int ScanLength = 500;
        private const int PatchOffset = 3;
        private const int PatchLength = 9;
        private const uint MemoryCommit = 0x1000;
        private const uint PageExecuteReadWrite = 0x40;
        private const uint PageGuard = 0x100;
        private const int GuardLength = 21;
        private static readonly object Sync = new();
        private static bool _attempted;
        private static nint _entry;
        private static nint _guard;
        private static byte[] _appliedGuard;
        private static PlayerLimitPatchState _state = new(PlayerLimitPatchStatus.NotAttempted,
            "Dedicated configuration validation has not been changed.");

        internal static PlayerLimitPatchState State => Volatile.Read(ref _state);

        internal static bool TryApply()
        {
            try { Apply(); return true; }
            catch (Exception error)
            {
                try { RuntimeEntryPoint.LogSource?.LogWarning("Dedicated player-limit validation patch is unavailable: " + error.Message); }
                catch (Exception) { }
                return false;
            }
        }

        internal static void Apply()
        {
            lock (Sync)
            {
                if (_attempted && State.Status != PlayerLimitPatchStatus.Applied)
                    throw new InvalidOperationException("Player-limit patching previously failed or changed; restart before retrying.");
                bool verifying = _attempted;
                _attempted = true;
                try
                {
                    if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                        throw new PlatformNotSupportedException("The player-limit patch requires Windows x64.");
                    var process = GetCurrentProcess();
                    var entry = ResolveEntry(process);
                    if (verifying)
                    {
                        if (entry != _entry || !ReadMemory(process, _guard, GuardLength).AsSpan().SequenceEqual(_appliedGuard))
                            throw new InvalidOperationException("The previously applied player-limit patch changed.");
                        return;
                    }
                    SetState(PlayerLimitPatchStatus.Applying, "Checking dedicated configuration validation instructions.");
                    var bytes = ReadMemory(process, entry, ScanLength);
                    int start = FindGuardOffset(bytes, entry);
                    var original = bytes.AsSpan(start, GuardLength).ToArray();
                    _entry = entry;
                    _guard = checked(entry + start);
                    _appliedGuard = (byte[])original.Clone();
                    _appliedGuard.AsSpan(PatchOffset, PatchLength).Fill(0x90);
                    if (!ReadMemory(process, _guard, GuardLength).AsSpan().SequenceEqual(original))
                        throw new InvalidOperationException("Player-limit validation changed before patching.");
                    WritePatch(process, checked(_guard + PatchOffset), original.AsSpan(PatchOffset, PatchLength).ToArray());
                    if (!ReadMemory(process, _guard, GuardLength).AsSpan().SequenceEqual(_appliedGuard))
                        throw new InvalidOperationException("Player-limit patch verification failed.");
                    SetState(PlayerLimitPatchStatus.Applied,
                        "The dedicated configuration upper-bound check was bypassed; the lower bound remains. This does not qualify a player count.");
                }
                catch
                {
                    SetState(verifying ? PlayerLimitPatchStatus.Changed : PlayerLimitPatchStatus.Failed,
                        verifying ? "Previously applied validation instructions changed; no replacement was attempted."
                            : "Validation patching did not complete. Native configuration or memory state may be unchanged or uncertain; no automatic retry.");
                    throw;
                }
            }
        }

        private static nint ResolveEntry(nint process)
        {
            var method = AccessTools.Method(typeof(ServerDedicatedConfig), nameof(ServerDedicatedConfig.Validate));
            if (method == null)
                throw new InvalidOperationException("Could not find ServerDedicatedConfig.Validate.");

            var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
            if (field?.GetValue(null) is not nint methodInfo || methodInfo == 0)
                throw new InvalidOperationException("ServerDedicatedConfig.Validate has no native method info.");

            // IL2CPP method info starts with the native entry pointer, as in the SDK resolver.
            var entry = (nint)BitConverter.ToInt64(ReadMemory(process, methodInfo, IntPtr.Size), 0);
            if (entry == 0)
                throw new InvalidOperationException("ServerDedicatedConfig.Validate has no native entry point.");

            return entry;
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

        private static int FindGuardOffset(byte[] bytes, nint entry)
        {
            // Decode from the method entry so a matching byte pattern inside an immediate
            // or displacement cannot be treated as the beginning of an instruction.
            var decoder = Decoder.Create(64, bytes, (ulong)(nuint)entry);
            int found = -1;
            while (decoder.IP - (ulong)(nuint)entry < (ulong)bytes.Length)
            {
                int start = checked((int)(decoder.IP - (ulong)(nuint)entry));
                decoder.Decode(out var instruction);
                if (instruction.Code == Iced.Intel.Code.INVALID || instruction.FlowControl == FlowControl.Return) break;
                if (start > bytes.Length - GuardLength) break;
                // mov eax,[rax+28]; cmp eax,8; jg error; cmp eax,1; jl error.
                // Both signed branches must name the same forward error path.
                if (bytes[start] != 0x8B || bytes[start + 1] != 0x40 || bytes[start + 2] != 0x28
                    || bytes[start + 3] != 0x83 || bytes[start + 4] != 0xF8 || bytes[start + 5] != 0x08
                    || bytes[start + 6] != 0x0F || bytes[start + 7] != 0x8F
                    || bytes[start + 12] != 0x83 || bytes[start + 13] != 0xF8 || bytes[start + 14] != 0x01
                    || bytes[start + 15] != 0x0F || bytes[start + 16] != 0x8C) continue;
                int upper = BitConverter.ToInt32(bytes, start + 8);
                int lower = BitConverter.ToInt32(bytes, start + 17);
                if (upper < 9 || lower < 0 || (long)upper + 12 != (long)lower + GuardLength) continue;
                if (found >= 0)
                {
                    throw new InvalidOperationException("More than one dedicated player-limit guard matched; memory was not changed.");
                }
                found = start;
            }
            if (found < 0) throw new InvalidOperationException("The dedicated player-limit instructions were not recognized; memory was not changed.");
            return found;
        }

        private static void WritePatch(nint process, nint address, byte[] original)
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
            bool writeStarted = false;
            try
            {
                if (!ReadMemory(process, address, PatchLength).AsSpan().SequenceEqual(original))
                    throw new InvalidOperationException("Player-limit instructions changed before the write.");
                writeStarted = true;
                Marshal.Copy(patch, 0, address, patch.Length);
                if (!FlushInstructionCache(process, address, PatchLength))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not flush the player-limit instruction cache.");
                if (!ReadMemory(process, address, PatchLength).AsSpan().SequenceEqual(patch))
                    throw new InvalidOperationException("The player-limit write could not be verified.");
            }
            catch (Exception error)
            {
                writeError = error;
                if (writeStarted)
                {
                    try
                    {
                        Marshal.Copy(original, 0, address, original.Length);
                        if (!FlushInstructionCache(process, address, PatchLength)
                            || !ReadMemory(process, address, PatchLength).AsSpan().SequenceEqual(original))
                            throw new InvalidOperationException("Original player-limit instructions could not be restored and verified.");
                    }
                    catch (Exception restoreError)
                    {
                        writeError = new AggregateException("Player-limit writing and instruction restoration failed.", error, restoreError);
                        throw writeError;
                    }
                }
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

        private static void SetState(PlayerLimitPatchStatus status, string detail) =>
            Volatile.Write(ref _state, new PlayerLimitPatchState(status, detail));

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

namespace Modstoke.Sons
{
    public enum PlayerLimitPatchStatus { NotAttempted, Applying, Applied, Failed, Changed }

    /// <summary>Validation-patch evidence only; no player count is qualified by this state.</summary>
    public sealed class PlayerLimitPatchState
    {
        internal PlayerLimitPatchState(PlayerLimitPatchStatus status, string detail)
        { Status = status; Detail = detail; }
        public PlayerLimitPatchStatus Status { get; }
        public string Detail { get; }
    }

    public static partial class ServerWorld
    {
        /// <summary>Last patch observation. This property does not read or modify native memory.</summary>
        public static PlayerLimitPatchState PlayerLimitPatch =>
            global::Modstoke.Loader.PlayerLimitPatch.State;
    }
}
