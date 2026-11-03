// Copyright (c) 2026 Alex Cooper. MIT license.
using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

namespace HarmonyLib;

internal static unsafe partial class RustKernel
{
    internal enum MatcherMode : uint { Search, Match, Sequence }
    internal enum MatcherAction : uint { Begin, SearchPredicate, MatchPredicate, NullMatches, Done }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MatcherState
    {
        internal MatcherMode Mode;
        internal MatcherAction Action;
        internal int Direction;
        internal uint UseEnd;
        internal int MatchCount, MatchIndex, InstructionIndex, Position;
        internal uint ResetCaptures, Matched;
    }

    private static class MatcherNative
    {
        internal static readonly delegate* unmanaged[Cdecl]<MatcherState*, int, int, uint, uint> Step =
            (delegate* unmanaged[Cdecl]<MatcherState*, int, int, uint, uint>)Export("mh_matcher_step_v1");
        internal static readonly delegate* unmanaged[Cdecl]<MatcherState*, int, uint, int*, uint, short*, uint, uint> ExactOpcodes =
            (delegate* unmanaged[Cdecl]<MatcherState*, int, uint, int*, uint, short*, uint, uint>)Export("mh_matcher_exact_opcodes_v1");
    }

    internal static void StepMatcher(ref MatcherState state, int position, int length, uint response = 0)
    {
        fixed (MatcherState* pointer = &state)
            Check(MatcherNative.Step(pointer, position, length, response), "matcher traversal");
    }

    internal static void RunExactOpcodeMatcher(ref MatcherState state, List<CodeInstruction> codes, CodeMatch[] matches)
    {
        // A bounded all-or-nothing eligibility check keeps callbacks, custom
        // equality, named captures, and invalid metadata on the live path.
        if (state.Action != MatcherAction.MatchPredicate || matches.Length < 2 || matches.Length > 128)
            return;
        Span<short> pattern = stackalloc short[matches.Length];
        for (var index = 0; index < matches.Length; index++)
        {
            var match = matches[index];
            if (match == null || match.predicate != null || match.name != null || match.opcode.Size == 0 ||
                match.opcodeSet == null || match.operands?.Count != 0 || match.operand != null ||
                match.labels?.Count != 0 || match.blocks?.Count != 0 ||
                match.jumpsFrom?.Count != 0 || match.jumpsTo?.Count != 0)
                return;
            if (match.opcodeSet.Count != 0 &&
                (!ReferenceEquals(match.opcodeSet.Comparer, EqualityComparer<OpCode>.Default) ||
                 !match.opcodeSet.Contains(match.opcode)))
                return;
            pattern[index] = match.opcode.Value;
        }

        Span<int> window = stackalloc int[256];
        fixed (MatcherState* statePointer = &state)
        fixed (short* patternPointer = pattern)
        fixed (int* windowPointer = window)
        {
            while (state.Action == MatcherAction.MatchPredicate)
            {
                // Leave room after each backward candidate: sequence elements
                // always match forwards, regardless of the candidate direction.
                var start = state.InstructionIndex - state.MatchIndex;
                if (state.Mode == MatcherMode.Match && state.Direction < 0)
                    start = Math.Max(0, start - (window.Length - pattern.Length));
                var count = Math.Min(window.Length, codes.Count - start);
                for (var index = 0; index < count; index++)
                {
                    var instruction = codes[start + index];
                    window[index] = instruction == null ? int.MinValue : instruction.opcode.Value;
                }

                Check(MatcherNative.ExactOpcodes(statePointer, codes.Count, (uint)start,
                    windowPointer, (uint)count, patternPointer, (uint)pattern.Length), "exact opcode matching");
                if (state.Action == MatcherAction.Done)
                    return;
                if (state.InstructionIndex >= start && state.InstructionIndex - start < count)
                    return; // Null instruction: let ordinary matching raise its exception.
            }
        }
    }
}
