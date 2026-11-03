// Copyright (c) 2026 Alex Cooper. MIT license.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace HarmonyLib;

internal static unsafe partial class RustKernel
{
    internal enum DiagnosticArgument : uint { Null, Method, Field, Label, Labels, Local, Text }

    [StructLayout(LayoutKind.Sequential)]
    private struct DiagnosticFormat
    {
        internal DiagnosticArgument Argument;
        internal uint Labels, Blocks, Arguments, Instruction;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DiagnosticFragment
    {
        internal uint Start, Length;
    }

    internal static string FormatDiagnostic(DiagnosticArgument argument, List<string> parts,
        int labels, int blocks, int arguments, bool instruction)
    {
        var format = new DiagnosticFormat
        {
            Argument = argument, Labels = (uint)labels, Blocks = (uint)blocks,
            Arguments = (uint)arguments, Instruction = instruction ? 1u : 0u,
        };
        var fragments = new DiagnosticFragment[parts.Count];
        var count = 0;
        for (var index = 0; index < parts.Count; index++)
        {
            var size = parts[index]?.Length ?? 0;
            fragments[index] = new DiagnosticFragment { Start = (uint)count, Length = (uint)size };
            count = checked(count + size);
        }
        var characters = new char[count];
        for (var index = 0; index < parts.Count; index++)
            parts[index]?.CopyTo(0, characters, (int)fragments[index].Start, (int)fragments[index].Length);

        uint length = 0;
        fixed (DiagnosticFragment* pieces = fragments)
        fixed (char* text = characters)
            Check(DiagnosticExports.Format(&format, pieces, (uint)fragments.Length, text,
                (uint)characters.Length, null, 0, &length), "instruction diagnostic size");
        return string.Create(checked((int)length), (format, fragments, characters), static (output, inputs) =>
        {
            var request = inputs.format;
            uint written = 0;
            fixed (DiagnosticFragment* pieces = inputs.fragments)
            fixed (char* text = inputs.characters)
            fixed (char* destination = output)
                Check(DiagnosticExports.Format(&request, pieces, (uint)inputs.fragments.Length, text,
                    (uint)inputs.characters.Length, destination, (uint)output.Length, &written), "instruction diagnostic");
            if (written != output.Length) throw new RustBackendException("Invalid instruction diagnostic length.");
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DiagnosticState
    {
        internal int Indent;
        internal uint Depth;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DiagnosticResult
    {
        internal uint Length, RequiredDepth, Error;
        internal int Indent;
    }

    // CLR text is resolved one event at a time. Resolving it earlier would move
    // custom Type callbacks or Cecil operand mutations across block boundaries.
    internal sealed class DiagnosticWriter
    {
        private readonly StringBuilder output = new();
        private DiagnosticState state;
        private uint[] handlers = [];
        private char[] lineBuffer = [];

        internal void BeginLocals() => Append(0, 0, null);
        internal void WriteLine(string text) => Append(1, 0, text);
        internal void EndLocals() => Append(2, 0, null);

        internal void WriteBlock(ExceptionBlock block)
        {
            var kind = block.blockType;
            var name = kind == ExceptionBlockType.BeginCatchBlock ? block.catchType.FullName : null;
            Append(3, (int)kind, name);
        }

        private void Append(uint operation, int block, string text)
        {
            var current = state;
            var result = new DiagnosticResult();
            fixed (char* source = text)
            fixed (char* newline = Environment.NewLine)
            {
                var count = (uint)(text?.Length ?? 0);
                var newlineCount = (uint)Environment.NewLine.Length;
                fixed (uint* stack = handlers)
                    Check(DiagnosticExports.Layout(operation, block, source, count, newline, newlineCount,
                        &current, stack, (uint)handlers.Length, null, 0, &result), "diagnostic layout size");
                CheckLayout(result);
                if (result.RequiredDepth > handlers.Length)
                {
                    var capacity = handlers.Length <= int.MaxValue / 2 ? handlers.Length * 2 : int.MaxValue;
                    Array.Resize(ref handlers, Math.Max(4, Math.Max(capacity, checked((int)result.RequiredDepth))));
                }
                var lineLength = checked((int)result.Length);
                if (lineLength > lineBuffer.Length)
                {
                    var capacity = lineBuffer.Length <= int.MaxValue / 2 ? lineBuffer.Length * 2 : int.MaxValue;
                    Array.Resize(ref lineBuffer, Math.Max(capacity, lineLength));
                }
                fixed (uint* stack = handlers)
                fixed (char* destination = lineBuffer)
                    Check(DiagnosticExports.Layout(operation, block, source, count, newline, newlineCount,
                        &current, stack, (uint)handlers.Length, destination, (uint)lineBuffer.Length, &result), "diagnostic layout");
                CheckLayout(result);
                if (result.Length != lineLength) throw new RustBackendException("Invalid diagnostic layout length.");
                output.Append(lineBuffer, 0, lineLength);
                state = current;
            }
        }

        private static void CheckLayout(DiagnosticResult result)
        {
            switch (result.Error)
            {
                case 0: return;
                case 1: throw new ArgumentOutOfRangeException();
                // Use the CLR operations for their original exception types,
                // parameter names, and localized messages.
                case 2: _ = new Stack<string>().Pop(); break;
                case 3: _ = new string(' ', result.Indent); break;
            }
            throw new RustBackendException("Invalid diagnostic layout result.");
        }

        public override string ToString() => output.ToString();
    }

    private static class DiagnosticExports
    {
        internal static readonly delegate* unmanaged[Cdecl]<DiagnosticFormat*, DiagnosticFragment*, uint,
            char*, uint, char*, uint, uint*, uint> Format =
            (delegate* unmanaged[Cdecl]<DiagnosticFormat*, DiagnosticFragment*, uint,
                char*, uint, char*, uint, uint*, uint>)Export("mh_diagnostic_format_v1");
        internal static readonly delegate* unmanaged[Cdecl]<uint, int, char*, uint, char*, uint,
            DiagnosticState*, uint*, uint, char*, uint, DiagnosticResult*, uint> Layout =
            (delegate* unmanaged[Cdecl]<uint, int, char*, uint, char*, uint,
                DiagnosticState*, uint*, uint, char*, uint, DiagnosticResult*, uint>)Export("mh_diagnostic_layout_v1");
    }
}
