// SPDX-License-Identifier: GPL-3.0-or-later
// Metadata scan rules adapted from Il2CppInterop (Il2CppInterop contributors, LGPL-3.0).
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Linq;
using Iced.Intel;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class InteropMetadataScanner
    {
        private static readonly (string Assembly, string Type, string Method)[] Candidates =
        {
            ("UnityEngine.CoreModule", "UnityEngine.Object", ".cctor"),
            ("mscorlib", "System.Exception", "get_Message"),
            ("mscorlib", "System.IntPtr", "Equals")
        };

        private readonly nint baseAddress;
        private readonly long fileLength;
        private readonly nint metadataInitPointer;

        internal InteropMetadataScanner(InteropRewriteContext context, nint baseAddress, long fileLength)
        {
            this.baseAddress = baseAddress;
            this.fileLength = fileLength;

            foreach (var candidate in Candidates)
            {
                var assembly = context.Assemblies.FirstOrDefault(value => value.OriginalAssembly.Name == candidate.Assembly);
                var method = assembly?.TryGetTypeByName(candidate.Type)?.OriginalType.Methods
                    .FirstOrDefault(value => value.Name == candidate.Method);
                if (method == null)
                    continue;

                var offset = method.ExtractOffset();
                metadataInitPointer = FindFirstTarget(offset)
                    ?? throw new InvalidOperationException("The metadata initialization candidate has no call or jump target.");
                MetadataInitRva = metadataInitPointer.ToInt64() - baseAddress.ToInt64() - offset + method.ExtractRva();
                if (MetadataInitRva == 0)
                    throw new InvalidDataException("Metadata initialization discovery returned its unresolved RVA sentinel.");
                return;
            }
            throw new ApplicationException("Unable to find a method with metadata init reference");
        }

        internal long MetadataInitRva { get; }

        internal (long FlagRva, long TokenRva) Read(InteropMethodContext method)
        {
            var firstTarget = FindFirstTarget(method.FileOffset) ?? nint.Zero;
            if (firstTarget == nint.Zero || firstTarget != metadataInitPointer)
                return (0, 0);

            var token = FindTokenRead(method.FileOffset);
            var flag = FindFlagWrite(method.FileOffset);
            if (token == nint.Zero || flag == nint.Zero)
                return (0, 0);
            return (flag.ToInt64() - baseAddress.ToInt64() - method.FileOffset + method.Rva,
                token.ToInt64() - baseAddress.ToInt64() - method.FileOffset + method.Rva);
        }

        private Decoder DecoderAt(long offset)
        {
            if (offset < 0 || offset >= fileLength)
                throw new InvalidDataException("A metadata scan offset is outside the native assembly: " + offset);
            return InteropXrefScanner.CreateDecoder(baseAddress + (nint)offset, (int)Math.Min(1000, fileLength - offset));
        }

        private nint? FindFirstTarget(long offset)
        {
            var decoder = DecoderAt(offset);
            while (true)
            {
                decoder.Decode(out var instruction);
                if (decoder.LastError == DecoderError.NoMoreBytes || instruction.Mnemonic == Mnemonic.Int3 ||
                    instruction.FlowControl == FlowControl.Return)
                    return null;
                if ((instruction.FlowControl == FlowControl.UnconditionalBranch || instruction.FlowControl == FlowControl.Call) &&
                    !instruction.IsJmpShort)
                    return (nint)InteropXrefScanner.ExtractTarget(in instruction, throwOnUnsupported: true);
            }
        }

        private nint FindTokenRead(long offset)
        {
            var decoder = DecoderAt(offset);
            var lastRead = nint.Zero;
            while (true)
            {
                decoder.Decode(out var instruction);
                if (decoder.LastError == DecoderError.NoMoreBytes || instruction.FlowControl == FlowControl.Return)
                    return nint.Zero;
                if (instruction.FlowControl == FlowControl.UnconditionalBranch)
                    continue;
                if (instruction.Mnemonic == Mnemonic.Int || instruction.Mnemonic == Mnemonic.Int1)
                    return nint.Zero;
                if (instruction.Mnemonic == Mnemonic.Call && (nint)InteropXrefScanner.ExtractTarget(in instruction) == metadataInitPointer)
                    return lastRead;

                if (instruction.Mnemonic == Mnemonic.Mov && instruction.Op0Kind == OpKind.Register &&
                    instruction.Op0Register == Register.ECX && instruction.Op1Kind == OpKind.Memory &&
                    instruction.IsIPRelativeMemoryOperand &&
                    (instruction.MemorySize == MemorySize.UInt32 || instruction.MemorySize == MemorySize.Int32))
                    lastRead = (nint)instruction.IPRelativeMemoryAddress;
            }
        }

        private nint FindFlagWrite(long offset)
        {
            var decoder = DecoderAt(offset);
            var seenCall = false;
            while (true)
            {
                decoder.Decode(out var instruction);
                if (decoder.LastError == DecoderError.NoMoreBytes || instruction.FlowControl == FlowControl.Return)
                    return nint.Zero;
                if (instruction.FlowControl == FlowControl.UnconditionalBranch)
                    continue;
                if (instruction.Mnemonic == Mnemonic.Int || instruction.Mnemonic == Mnemonic.Int1)
                    return nint.Zero;
                if (instruction.Mnemonic == Mnemonic.Call && (nint)InteropXrefScanner.ExtractTarget(in instruction) == metadataInitPointer)
                    seenCall = true;

                // Keep the installed byte-store rule, including its lack of an IP-relative or adjacency requirement.
                if (seenCall && instruction.Mnemonic == Mnemonic.Mov && instruction.Op0Kind == OpKind.Memory &&
                    (instruction.MemorySize == MemorySize.Int8 || instruction.MemorySize == MemorySize.UInt8))
                    return (nint)instruction.IPRelativeMemoryAddress;
            }
        }
    }
}
