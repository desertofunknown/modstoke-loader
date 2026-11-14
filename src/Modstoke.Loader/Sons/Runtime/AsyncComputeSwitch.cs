// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Modstoke.Loader;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Sons
{
    /// <summary>
    /// On DirectX 12 HDRP runs its light-list build and other compute passes on the engine's async compute queue, synced
    /// to the graphics queue with fences; DirectX 11 has no such queue and runs them in order. Every DirectX 12 GPU hang
    /// so far stopped on a lit draw that loops over those light lists, so on DirectX 12 the engine's answer to "is async
    /// compute supported" becomes no. HDRP asks every frame and then records the same passes in order on the graphics
    /// queue, as it does on DirectX 11. MODSTOKE_DX12_ASYNC_COMPUTE=1 leaves async compute on.
    /// </summary>
    internal static class AsyncComputeSwitch
    {
        private const string Icall = "UnityEngine.SystemInfo::SupportsAsyncCompute";
        private const uint PageExecuteReadWrite = 0x40;

        // xor eax, eax; ret
        private static readonly byte[] ReturnFalse = { 0x31, 0xC0, 0xC3 };

        internal static void Apply()
        {
            try
            {
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12) return;
                if (string.Equals(Environment.GetEnvironmentVariable("MODSTOKE_DX12_ASYNC_COMPUTE"), "1", StringComparison.Ordinal))
                {
                    RuntimeEntryPoint.LogSource?.LogInfo("Async compute left on (MODSTOKE_DX12_ASYNC_COMPUTE=1).");
                    return;
                }
                RuntimeEntryPoint.LogSource?.LogInfo("Async compute: " + Patch());
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Async compute left as the engine reports it: " + error.Message);
            }
        }

        private static unsafe string Patch()
        {
            var before = SystemInfo.supportsAsyncCompute;
            var entry = ResolveIcall(Icall);
            if (entry == 0)
                throw new InvalidOperationException(Icall + " is not registered.");
            // The three bytes go in with one aligned 8-byte store, so no thread can run a half-written instruction.
            var offset = (int)(entry % 8);
            if (offset > 8 - ReturnFalse.Length)
                throw new InvalidOperationException("the engine function's first bytes cross an 8-byte boundary.");
            var head = (long*)(entry - offset);
            var original = BitConverter.GetBytes(*head);
            var shown = BitConverter.ToString(original, offset, ReturnFalse.Length);
            if (original.AsSpan(offset, ReturnFalse.Length).SequenceEqual(ReturnFalse))
                return "already reported unsupported.";

            var patched = (byte[])original.Clone();
            ReturnFalse.CopyTo(patched, offset);
            if (!VirtualProtect((nint)head, 8, PageExecuteReadWrite, out var protection))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "the engine function could not be made writable");
            try
            {
                Interlocked.Exchange(ref *head, BitConverter.ToInt64(patched, 0));
                FlushInstructionCache(GetCurrentProcess(), (nint)head, 8);
            }
            finally
            {
                VirtualProtect((nint)head, 8, protection, out _);
            }
            var after = SystemInfo.supportsAsyncCompute;
            return $"the engine reported it {(before ? "supported" : "unsupported")}, now {(after ? "supported" : "unsupported")}"
                + $" (function began {shown}).";
        }

        [DllImport("GameAssembly.dll", EntryPoint = "il2cpp_resolve_icall", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern nint ResolveIcall(string name);

        [DllImport("kernel32.dll")]
        private static extern nint GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint oldProtection);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
    }
}
