// Copyright (c) 2026 Alex Cooper. MIT license.
using System;
using System.Collections.Generic;

namespace HarmonyLib;

internal static unsafe partial class RustKernel
{
    internal static uint[] OrderExceptionOffsets(ReadOnlySpan<KeyValuePair<int, CodeInstruction>> instructions)
    {
        if (instructions.Length == 0) return [];
        if (instructions.Length == 1) return [0];
        Span<int> offsets = instructions.Length <= 256 ? stackalloc int[instructions.Length] : new int[instructions.Length];
        for (var index = 0; index < instructions.Length; index++) offsets[index] = instructions[index].Key;
        var order = new uint[instructions.Length];
        fixed (int* source = offsets)
        fixed (uint* destination = order)
            Check(ExceptionOrderExports.Order(source, (uint)offsets.Length, destination,
                (uint)order.Length), "IL error instruction ordering");
        foreach (var ordinal in order)
            if (ordinal >= instructions.Length) throw new RustBackendException("Invalid IL error instruction ordinal.");
        return order;
    }

    internal static int FindExceptionOffset(string trimmedMessage)
    {
        var digits = ExceptionExports.DecimalDigits;
        var offset = -1;
        fixed (char* text = trimmedMessage)
        fixed (byte* digitMap = digits)
            Check(ExceptionExports.FindOffset(text, (uint)trimmedMessage.Length,
                digitMap, (uint)digits.Length, &offset), "IL error location matching");
        if (offset < -1 || (offset >= 0 && offset > trimmedMessage.Length - 7))
            throw new RustBackendException("Invalid IL error location capture.");
        return offset;
    }

    private static class ExceptionOrderExports
    {
        internal static readonly delegate* unmanaged[Cdecl]<int*, uint, uint*, uint, uint> Order =
            (delegate* unmanaged[Cdecl]<int*, uint, uint*, uint, uint>)Export("mh_exception_order_v1");
    }

    private static class ExceptionExports
    {
        internal static readonly delegate* unmanaged[Cdecl]<char*, uint, byte*, uint, int*, uint> FindOffset =
            (delegate* unmanaged[Cdecl]<char*, uint, byte*, uint, int*, uint>)Export("mh_exception_offset_v1");
        internal static readonly byte[] DecimalDigits = CreateDecimalDigits();

        private static byte[] CreateDecimalDigits()
        {
            // Regex \d uses DecimalDigitNumber on UTF-16 code units, including
            // non-ASCII digits which Int32.Parse(HexNumber) later rejects. Cache
            // the active CLR's categories rather than a different Unicode table.
            var digits = new byte[0x10000 / 8];
            for (var value = 0; value <= char.MaxValue; value++)
                if (char.IsDigit((char)value)) digits[value >> 3] |= (byte)(1 << (value & 7));
            return digits;
        }
    }
}
