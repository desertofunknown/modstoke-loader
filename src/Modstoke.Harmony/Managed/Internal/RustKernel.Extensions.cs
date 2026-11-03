// Copyright (c) 2026 Alex Cooper. MIT license.
using System;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace HarmonyLib;

internal static unsafe partial class RustKernel
{
    [Flags]
    internal enum ExtensionKind : uint
    {
        Calling = 1, LocalAddress = 2, LocalLoad = 4, LocalStore = 8,
        ArgumentAddress = 16, ArgumentLoad = 32, ArgumentStore = 64,
        Branch = 128, Constant = 256, Integer = 512, FloatingPoint = 1024, String = 2048,
        FieldLoad = 8192, FieldAddress = 16384, FieldStore = 32768,
        StaticFieldLoad = 65536, StaticFieldAddress = 131072, StaticFieldStore = 262144
    }

    internal readonly struct ExtensionDescriptor(uint bits)
    {
        internal bool Is(ExtensionKind kind) => (bits & (uint)kind) != 0;
        internal bool IsImplicit => (bits & 4096) != 0;
        internal int ImplicitValue => (int)(bits >> 24) - 1;
    }

    private static class ExtensionTables
    {
        internal static readonly uint[] Values = Create();

        private static uint[] Create()
        {
            var values = new uint[512];
            var populate = (delegate* unmanaged[Cdecl]<uint*, uint, uint>)Export("mh_extensions_tables_v1");
            fixed (uint* output = values)
                Check(populate(output, (uint)values.Length), "extension instruction tables");
            return values;
        }
    }

    internal static ExtensionDescriptor ExtensionInfo(OpCode opcode)
    {
        var value = unchecked((ushort)opcode.Value);
        // The second page contains the two-byte ECMA-335 instruction space.
        return new(value < 256 ? ExtensionTables.Values[value]
            : (value & 0xff00) == 0xfe00 ? ExtensionTables.Values[256 + (value & 255)] : 0);
    }

    internal static HashSet<OpCode> ExtensionOpcodes(ExtensionKind kind)
    {
        var result = new HashSet<OpCode>();
        var binding = Current.Value;
        var values = ExtensionTables.Values;
        for (var index = 0; index < values.Length; index++)
            if ((values[index] & (uint)kind) != 0)
                result.Add(binding.OpCodes[unchecked((short)(index < 256 ? index : 0xfe00 + index - 256))]);
        return result;
    }
}
