// Copyright (c) 2026 Alex Cooper. MIT license.
using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

namespace HarmonyLib;

internal static unsafe partial class RustKernel
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MatchOpcodeCandidate
    {
        internal int Value;
        internal uint Present;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MatchJumpSource
    {
        internal int OperandLabel;
        internal uint State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MatchJumpNode
    {
        internal uint LabelStart, LabelCount, State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MatchJumpQuery
    {
        internal uint LabelStart, LabelCount;
        internal int OperandLabel;
        internal uint CodesPresent, ToPresent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MatchJumpResult
    {
        internal uint Outcome, Ordinal;
    }

    private static class CodeMatchExports
    {
        internal static readonly delegate* unmanaged[Cdecl]<MatchOpcodeCandidate*, uint, short*, uint, int, uint*, uint>
            Candidates = (delegate* unmanaged[Cdecl]<MatchOpcodeCandidate*, uint, short*, uint, int, uint*, uint>)
                Export("mh_codematch_candidates_v1");
        internal static readonly delegate* unmanaged[Cdecl]<MatchJumpQuery*, MatchJumpNode*, uint, uint*, uint, MatchJumpSource*, uint, int*, uint, MatchJumpResult*, uint>
            Jumps = (delegate* unmanaged[Cdecl]<MatchJumpQuery*, MatchJumpNode*, uint, uint*, uint, MatchJumpSource*, uint, int*, uint, MatchJumpResult*, uint>)
                Export("mh_codematch_jumps_v1");
    }

    // The caller has established that the current candidate fails a callback-free
    // opcode prefix. Bounded windows avoid rescanning the entire stream after an
    // operand predicate changes the instructions or match metadata.
    internal static int FindOpcodeCandidate(List<CodeInstruction> codes, int start, int direction,
        HashSet<OpCode> opcodeSet, OpCode opcode)
    {
        if (direction != 1 && direction != -1) throw new ArgumentOutOfRangeException(nameof(direction));
        Span<short> allowed = opcodeSet.Count <= 256
            ? stackalloc short[opcodeSet.Count] : new short[opcodeSet.Count];
        var ordinal = 0;
        foreach (var value in opcodeSet) allowed[ordinal++] = value.Value;
        var required = opcode.Size == 0 ? int.MinValue : opcode.Value;
        Span<MatchOpcodeCandidate> window = stackalloc MatchOpcodeCandidate[256];
        fixed (short* allowedPointer = allowed)
        fixed (MatchOpcodeCandidate* windowPointer = window)
        {
            var position = start;
            while (position >= 0 && position < codes.Count)
            {
                var count = Math.Min(window.Length, direction > 0 ? codes.Count - position : position + 1);
                for (var index = 0; index < count; index++)
                {
                    var instruction = codes[position + index * direction];
                    window[index] = new MatchOpcodeCandidate
                    {
                        Value = instruction?.opcode.Value ?? 0,
                        Present = instruction == null ? 0u : 1u,
                    };
                }
                uint found = 0;
                Check(CodeMatchExports.Candidates(windowPointer, (uint)count, allowedPointer,
                    (uint)allowed.Length, required, &found), "opcode candidate search");
                if (found > count) throw new RustBackendException("Invalid opcode candidate ordinal.");
                if (found < count) return position + (int)found * direction;
                position += count * direction;
            }
            return position;
        }
    }

    internal static bool MatchJumps(CodeMatch match, List<CodeInstruction> codes, CodeInstruction instruction)
    {
        // Preserve failures from publicly replaceable null lists. A null jumpsTo
        // is only an error after jumpsFrom succeeds; encode it without dereferencing.
        var fromCount = match.jumpsFrom.Count;
        var toCount = match.jumpsTo?.Count ?? -1;
        if (fromCount == 0)
        {
            if (match.jumpsTo.Count == 0) return true;
            if (instruction.operand is not Label) return false;
        }
        else
            ArgumentNullException.ThrowIfNull(instruction.labels, "second");

        var labelIds = new Dictionary<Label, int>();
        int LabelId(Label label)
        {
            if (!labelIds.TryGetValue(label, out var id))
            {
                id = labelIds.Count;
                labelIds.Add(label, id);
            }
            return id;
        }

        // Label equality is a CLR value operation with no user callbacks. IDs are
        // scoped to this call; they do not depend on Label's private representation
        // or on its hash code being unique.
        var labels = new List<uint>();
        if (fromCount > 0)
            foreach (var label in instruction.labels) labels.Add((uint)LabelId(label));
        var query = new MatchJumpQuery
        {
            LabelStart = 0,
            LabelCount = (uint)labels.Count,
            OperandLabel = instruction.operand is Label operandLabel ? LabelId(operandLabel) : -1,
            CodesPresent = codes == null ? 0u : 1u,
            ToPresent = match.jumpsTo == null ? 0u : 1u,
        };
        // A jumpsFrom-only query snapshots just the listed sources, not the whole
        // instruction stream. Fault markers let Rust preserve early termination.
        ReadOnlySpan<int> from = CollectionsMarshal.AsSpan(match.jumpsFrom);
        var sources = new MatchJumpSource[from.Length];
        for (var ordinal = 0; ordinal < from.Length; ordinal++)
        {
            var source = new MatchJumpSource { OperandLabel = -1 };
            if (codes == null) source.State = 2;
            else if ((uint)from[ordinal] >= (uint)codes.Count) source.State = 3;
            else
            {
                var code = codes[from[ordinal]];
                if (code == null) source.State = 4;
                else if (code.operand is Label label) source.OperandLabel = LabelId(label);
            }
            sources[ordinal] = source;
        }
        var nodes = new MatchJumpNode[toCount > 0 ? codes?.Count ?? 0 : 0];
        for (var index = 0; index < nodes.Length; index++)
        {
            var code = codes[index];
            var node = new MatchJumpNode { LabelStart = (uint)labels.Count };
            if (code == null) node.State = 1;
            else if (code.labels == null) node.State = 2;
            else
            {
                foreach (var codeLabel in code.labels) labels.Add((uint)LabelId(codeLabel));
                node.LabelCount = (uint)labels.Count - node.LabelStart;
            }
            nodes[index] = node;
        }

        // All managed callbacks have finished, and these lists remain unchanged
        // through the synchronous call. Borrow their storage instead of copying.
        ReadOnlySpan<uint> labelSpan = CollectionsMarshal.AsSpan(labels);
        ReadOnlySpan<int> to = CollectionsMarshal.AsSpan(match.jumpsTo);
        var result = new MatchJumpResult { Outcome = 0, Ordinal = 0 };
        fixed (MatchJumpNode* nodePointer = nodes)
        fixed (uint* labelPointer = labelSpan)
        fixed (MatchJumpSource* sourcePointer = sources)
        fixed (int* toPointer = to)
            Check(CodeMatchExports.Jumps(&query, nodePointer, (uint)nodes.Length,
                labelPointer, (uint)labelSpan.Length, sourcePointer, (uint)sources.Length,
                toPointer, (uint)to.Length, &result), "jump matching");

        switch (result.Outcome)
        {
            case 0: return false;
            case 1: return true;
            case 2:
            case 3:
            case 4:
                if (result.Ordinal >= from.Length) break;
                // Reproduce the original List indexer/null access exception only
                // when the native ordered scan actually reaches this source.
                _ = codes[from[(int)result.Ordinal]].operand;
                break;
            case 5:
                _ = match.jumpsTo.Count;
                break;
            case 6:
                _ = codes.Count;
                break;
            case 7:
            case 8:
                if (result.Ordinal >= nodes.Length) break;
                _ = codes[(int)result.Ordinal].labels.Contains((Label)instruction.operand);
                break;
        }
        throw new RustBackendException("Invalid jump matching result.");
    }
}
