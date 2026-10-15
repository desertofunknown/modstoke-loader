// SPDX-License-Identifier: GPL-3.0-or-later
// Generation xref rules adapted from Il2CppInterop (Il2CppInterop contributors, LGPL-3.0).
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Iced.Intel;
using Il2CppInterop.Common.XrefScans;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class InteropXrefScanner
    {
        internal static Decoder CreateDecoder(nint codeStart, int length)
        {
            if (codeStart == nint.Zero)
                throw new NullReferenceException(nameof(codeStart));
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length));
            var decoder = Decoder.Create(IntPtr.Size * 8, new MappedCodeReader(codeStart, length));
            decoder.IP = unchecked((ulong)codeStart.ToInt64());
            return decoder;
        }

        internal static IEnumerable<XrefInstance> Scan(Decoder decoder)
        {
            while (true)
            {
                decoder.Decode(out var instruction);
                if (decoder.LastError == DecoderError.NoMoreBytes || instruction.FlowControl == FlowControl.Return)
                    yield break;
                if (instruction.Mnemonic == Mnemonic.Int || instruction.Mnemonic == Mnemonic.Int1)
                    yield break;
                if (instruction.Mnemonic == Mnemonic.Call || instruction.Mnemonic == Mnemonic.Jmp)
                {
                    var target = ExtractTarget(in instruction);
                    if (target != 0)
                        yield return new XrefInstance(XrefType.Method, (nint)target, (nint)instruction.IP);
                    continue;
                }
                if (instruction.FlowControl == FlowControl.UnconditionalBranch)
                    continue;
                if (instruction.Mnemonic != Mnemonic.Mov &&
                    (instruction.Mnemonic < Mnemonic.Cmova || instruction.Mnemonic > Mnemonic.Cmovs))
                    continue;

                XrefInstance? global = null;
                try
                {
                    if (instruction.Op1Kind == OpKind.Memory && instruction.IsIPRelativeMemoryOperand)
                    {
                        var address = (nint)instruction.IPRelativeMemoryAddress;
                        if (instruction.MemorySize == MemorySize.UInt64)
                            global = new XrefInstance(XrefType.Global, address, (nint)instruction.IP);
                    }
                }
                catch (Exception error)
                {
                    InteropGenerationDiagnostics.Log(LogLevel.Error, "Could not inspect a native global reference.", error);
                }
                if (global.HasValue)
                    yield return global.Value;
            }
        }

        internal static ulong ExtractTarget(in Instruction instruction, bool throwOnUnsupported = false)
        {
            return instruction.Op0Kind switch
            {
                OpKind.NearBranch16 => instruction.NearBranch16,
                OpKind.NearBranch32 => instruction.NearBranch32,
                OpKind.NearBranch64 => instruction.NearBranch64,
                OpKind.FarBranch16 => instruction.FarBranch16,
                OpKind.FarBranch32 => instruction.FarBranch32,
                _ when !throwOnUnsupported => 0,
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        // The enclosing scan owns the mapping; this reader only borrows its bounded byte range.
        private sealed unsafe class MappedCodeReader : CodeReader
        {
            private byte* next;
            private int remaining;

            internal MappedCodeReader(nint start, int length)
            {
                next = (byte*)start;
                remaining = length;
            }

            public override int ReadByte()
            {
                if (remaining == 0)
                    return -1;
                remaining--;
                return *next++;
            }
        }
    }
}
