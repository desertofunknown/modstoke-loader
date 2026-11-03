// Copyright (c) 2026 Alex Cooper. MIT license.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;

namespace HarmonyLib;

// No CLR objects, handles, delegates or callback pointers are passed to Rust.
// The native library is retained for process lifetime, like the generated patch methods.
internal static unsafe partial class RustKernel
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Node
    {
        internal uint Method, BeforeStart, BeforeCount, AfterStart, AfterCount;
    }

    internal enum ArgumentAction : uint
    {
        LoadArgument, Box, BoxTemporary, Address, Indirect, IndirectBox, IndirectBoxTemporary
    }

    private sealed class Binding
    {
        internal nint Handle;
        internal delegate* unmanaged[Cdecl]<Node*, uint, uint*, uint, uint*, uint, uint*, uint, uint*, uint> Order;
        internal readonly ushort[] Branches = new ushort[256];
        internal readonly uint[] Variables = new uint[36];
        internal readonly uint[] Arguments = new uint[16];
        internal readonly Dictionary<short, OpCode> OpCodes = new();
    }

    private static readonly Lazy<Binding> Current = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);
    internal static void EnsureAvailable() => _ = Current.Value;

    // Native entry points are resolved against the same verified, process-lifetime module.
    private static nint Export(string name) => NativeLibrary.GetExport(Current.Value.Handle, name);

    private static Binding Create()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new RustBackendException("This Modstoke Harmony candidate requires Windows x64 and its matching Rust library.");
        var location = typeof(RustKernel).Assembly.Location;
        if (string.IsNullOrEmpty(location))
            throw new RustBackendException("Modstoke Harmony requires a file-backed assembly to locate its Rust library.");
        var path = Path.Combine(Path.GetDirectoryName(location), "modstoke_harmony_core.dll");
        var binding = new Binding();
        try
        {
            // Absolute, assembly-relative load: no PATH/current-directory fallback.
            binding.Handle = NativeLibrary.Load(path);
            var abi = (delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(binding.Handle, "mh_abi_v1");
            var nodeSize = (delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(binding.Handle, "mh_node_size_v1");
            var actual = abi();
            if (actual != 4 || nodeSize() != sizeof(Node))
                throw new RustBackendException($"Modstoke Harmony Rust ABI mismatch: expected version 4 and node size {sizeof(Node)}, received version {actual} and size {nodeSize()}.");
            binding.Order = (delegate* unmanaged[Cdecl]<Node*, uint, uint*, uint, uint*, uint, uint*, uint, uint*, uint>)
                NativeLibrary.GetExport(binding.Handle, "mh_order_v1");
            var tables = (delegate* unmanaged[Cdecl]<ushort*, uint, uint*, uint, uint*, uint, uint>)
                NativeLibrary.GetExport(binding.Handle, "mh_tables_v1");
            fixed (ushort* branches = binding.Branches)
            fixed (uint* variables = binding.Variables)
            fixed (uint* arguments = binding.Arguments)
                Check(tables(branches, 256, variables, 36, arguments, 16), "instruction tables");
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (field.GetValue(null) is OpCode opcode) binding.OpCodes[opcode.Value] = opcode;
            foreach (var value in binding.Variables)
                if ((value >> 16) > 2 || !binding.OpCodes.ContainsKey(unchecked((short)value)))
                    throw new RustBackendException("Modstoke Harmony received an invalid variable instruction recipe.");
            foreach (var value in binding.Arguments)
                if (value > (uint)ArgumentAction.IndirectBoxTemporary)
                    throw new RustBackendException("Modstoke Harmony received an invalid argument recipe.");
            return binding;
        }
        catch (Exception exception)
        {
            if (binding.Handle != 0) NativeLibrary.Free(binding.Handle);
            if (exception is RustBackendException) throw;
            throw new RustBackendException($"Cannot initialize Modstoke Harmony Rust backend at '{path}'. Keep 0Harmony.dll and its matching native library together.", exception);
        }
    }

    internal static (uint[] Order, uint[] Cycles) Sort(Node[] nodes, uint[] edges)
    {
        var binding = Current.Value;
        var order = new uint[nodes.Length];
        // Every cull removes an after edge; two ordinal values describe each removed edge.
        var cycles = new uint[checked(edges.Length * 2)];
        uint cycleCount = 0;
        fixed (Node* nativeNodes = nodes)
        fixed (uint* nativeEdges = edges)
        fixed (uint* nativeOrder = order)
        fixed (uint* nativeCycles = cycles)
            Check(binding.Order(nativeNodes, (uint)nodes.Length, nativeEdges, (uint)edges.Length,
                nativeOrder, (uint)order.Length, nativeCycles, (uint)cycles.Length, &cycleCount), "patch ordering");
        if (cycleCount > cycles.Length || (cycleCount & 1) != 0)
            throw new RustBackendException("Modstoke Harmony received invalid cycle diagnostics.");
        foreach (var ordinal in order)
            if (ordinal >= nodes.Length) throw new RustBackendException("Modstoke Harmony received an invalid patch ordinal.");
        for (var i = 0; i < cycleCount; i++)
            if (cycles[i] >= nodes.Length) throw new RustBackendException("Modstoke Harmony received an invalid cycle ordinal.");
        Array.Resize(ref cycles, checked((int)cycleCount));
        return (order, cycles);
    }

    internal static OpCode Normalize(OpCode opcode)
    {
        var value = unchecked((ushort)opcode.Value);
        if (value >= 256) return opcode;
        var binding = Current.Value;
        var normalized = binding.Branches[value];
        return normalized == value ? opcode : binding.OpCodes[unchecked((short)normalized)];
    }

    internal static CodeInstruction Variable(int operation, int index)
    {
        if (index < 0) _ = Convert.ToByte(index); // Preserve upstream exception behavior.
        var binding = Current.Value;
        var category = index < 4 ? index : index < 256 ? 4 : 5;
        var recipe = binding.Variables[operation * 6 + category];
        var opcode = binding.OpCodes[unchecked((short)recipe)];
        object operand = (recipe >> 16) switch
        {
            0 => null,
            1 => (object)Convert.ToByte(index),
            _ => index,
        };
        return new CodeInstruction(opcode, operand);
    }

    internal static ArgumentAction Argument(bool originalNormal, bool patchNormal, bool originalValue, bool patchValue)
    {
        var bits = (originalNormal ? 1 : 0) | (patchNormal ? 2 : 0)
            | (originalValue ? 4 : 0) | (patchValue ? 8 : 0);
        return (ArgumentAction)Current.Value.Arguments[bits];
    }

    private static void Check(uint status, string operation)
    {
        if (status == 0) return;
        var detail = status switch { 1 => "invalid descriptor", 2 => "allocation failed",
            3 => "native panic was contained", 4 => "buffer capacity mismatch", _ => "unknown status" };
        throw new RustBackendException($"Modstoke Harmony Rust {operation} failed: {detail} ({status}).");
    }
}

internal sealed class RustBackendException : HarmonyException
{
    internal RustBackendException(string message) : base(message) { }
    internal RustBackendException(string message, Exception inner) : base(message, inner) { }
}
