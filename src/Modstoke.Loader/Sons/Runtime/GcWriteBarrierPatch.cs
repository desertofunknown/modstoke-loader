// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>
    /// The game is built with Unity's incremental garbage collector, so every reference the engine stores goes through
    /// il2cpp_gc_wbarrier_set_field. It marks the written page in a dirty-page bitmap with a locked compare-exchange
    /// loop, even when the page is already marked, and engine bindings take that path on almost every call that
    /// touches a managed object. A CPU profile put 5.5% of the game thread in play, and nearly all of the grass
    /// setup's 330 ms at world load, on that one locked instruction.
    /// The page is almost always marked already, so the barrier now tests the bit with a plain read and returns when
    /// it is set; an unmarked page still takes the original atomic loop. The pointer store happens before the test,
    /// and the collector reads and clears the bitmap only while every thread is stopped, so no mark can be lost.
    /// </summary>
    internal static class GcWriteBarrierPatch
    {
        private const string Export = "il2cpp_gc_wbarrier_set_field";
        private const int RegionOffset = 0x30;
        private const int RegionLength = 16;
        private const uint PageExecuteReadWrite = 0x40;

        // cmp dword [rip+incremental],0; mov r9,rdx; mov [rdx],r8; je ret; shr r9,12; lea rcx,[rip+bitmap];
        // and r9d,1FFFFFh; mov eax,r9d; shr rax,6; and r9d,3Fh; lea rdx,[rcx+rax*8];
        // prefetchw [rdx]; nop; nop; loop: mov rax,[rdx]; mov rcx,rax; bts rcx,r9; lock cmpxchg [rdx],rcx; jne loop; ret.
        // -1 marks the build-specific RIP displacements.
        private static readonly short[] Expected =
        {
            0x83, 0x3D, -1, -1, -1, -1, 0x00, 0x4C, 0x8B, 0xCA, 0x4C, 0x89, 0x02, 0x74, 0x42, 0x49,
            0xC1, 0xE9, 0x0C, 0x48, 0x8D, 0x0D, -1, -1, -1, -1, 0x41, 0x81, 0xE1, 0xFF, 0xFF, 0x1F,
            0x00, 0x41, 0x8B, 0xC1, 0x48, 0xC1, 0xE8, 0x06, 0x41, 0x83, 0xE1, 0x3F, 0x48, 0x8D, 0x14, 0xC1,
            0x0F, 0x0D, 0x0A, 0x0F, 0x1F, 0x40, 0x00, 0x66, 0x0F, 0x1F, 0x84, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x48, 0x8B, 0x02, 0x48, 0x8B, 0xC8, 0x4C, 0x0F, 0xAB, 0xC9, 0xF0, 0x48, 0x0F, 0xB1, 0x0A, 0x75,
            0xEF, 0xC3
        };

        // Replaces the prefetch and the alignment padding in front of the loop: nop3; bt qword [rdx],r9; jc ret; nop7.
        // The old instruction starts at 3 and 7 stay instruction starts: a thread paused there before the write resumes
        // on the bit test, or on the branch with the carry flag still clear from "and r9d,3Fh", which falls through to
        // the original loop. The 16 bytes are written in one aligned store, which AVX processors perform atomically.
        private static readonly byte[] Replacement =
        {
            0x0F, 0x1F, 0x00, 0x4C, 0x0F, 0xA3, 0x0A, 0x72, 0x18, 0x0F, 0x1F, 0x80, 0x00, 0x00, 0x00, 0x00
        };

        internal static void Apply()
        {
            if (string.Equals(Environment.GetEnvironmentVariable("MODSTOKE_NATIVE_GC_BARRIER"), "1", StringComparison.Ordinal))
            {
                RuntimeEntryPoint.LogSource?.LogInfo("GC write barrier left as built (MODSTOKE_NATIVE_GC_BARRIER=1).");
                return;
            }
            try
            {
                var result = Patch();
                RuntimeEntryPoint.LogSource?.LogInfo("GC write barrier: " + result);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("GC write barrier left unchanged: " + error.Message);
            }
        }

        private static unsafe string Patch()
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64 || !Avx.IsSupported)
                throw new PlatformNotSupportedException("the patch needs Windows on an x64 processor with AVX.");
            var module = GetModuleHandle("GameAssembly.dll");
            var entry = module == 0 ? 0 : GetProcAddress(module, Export);
            if (entry == 0)
                throw new InvalidOperationException(Export + " was not found.");
            var region = entry + RegionOffset;
            if (region % RegionLength != 0)
                throw new InvalidOperationException("the instructions to replace are not 16-byte aligned.");

            var code = new byte[Expected.Length];
            Marshal.Copy(entry, code, 0, code.Length);
            if (code.AsSpan(RegionOffset, RegionLength).SequenceEqual(Replacement))
                return "already tests the dirty-page bit before its locked update.";
            for (var index = 0; index < Expected.Length; index++)
                if (Expected[index] >= 0 && code[index] != Expected[index])
                    throw new InvalidOperationException("its instructions differ from the build this patch knows.");

            var original = code.AsSpan(RegionOffset, RegionLength).ToArray();
            Store(region, Replacement);
            var written = new byte[RegionLength];
            Marshal.Copy(region, written, 0, RegionLength);
            if (!written.AsSpan().SequenceEqual(Replacement))
            {
                Store(region, original);
                throw new InvalidOperationException("the written instructions did not verify, so the originals were put back.");
            }
            return "tests the dirty-page bit before its locked update.";
        }

        private static unsafe void Store(nint address, byte[] bytes)
        {
            if (!VirtualProtect(address, RegionLength, PageExecuteReadWrite, out var protection))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "the instructions could not be made writable");
            try
            {
                var value = Vector128.Create(BitConverter.ToUInt64(bytes, 0), BitConverter.ToUInt64(bytes, 8));
                Sse2.StoreAligned((ulong*)address, value);
                FlushInstructionCache(GetCurrentProcess(), address, RegionLength);
            }
            finally
            {
                VirtualProtect(address, RegionLength, protection, out _);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern nint GetModuleHandle(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern nint GetProcAddress(nint module, string name);

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
