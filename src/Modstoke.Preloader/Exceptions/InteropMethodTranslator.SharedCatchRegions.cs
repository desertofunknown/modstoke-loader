// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private readonly record struct CatchRange(int Start, int End)
        {
            internal bool Contains(int position) => position >= Start && position < End;
            internal bool Contains(CatchRange other) => other.Start >= Start && other.End <= End;
            internal bool Overlaps(CatchRange other) => Start < other.End && other.Start < End;
        }

        private sealed class CatchCopyClause
        {
            internal readonly CilExceptionHandler Handler;
            internal readonly int Index;
            // Try, handler, then optional filter. The order also identifies control-flow roles.
            internal readonly CatchRange[] Regions;

            internal CatchCopyClause(CilExceptionHandler handler, int index, CatchRange[] regions)
            {
                Handler = handler;
                Index = index;
                Regions = regions;
            }

            internal bool Fits(CatchRange parent)
            {
                foreach (var region in Regions)
                    if (!parent.Contains(region)) return false;
                return true;
            }

            internal bool Overlaps(CatchRange other)
            {
                foreach (var region in Regions)
                    if (region.Overlaps(other)) return true;
                return false;
            }
        }

        private bool ValidateHandlerCopyRegion(CilMethodBody method, List<CilExceptionHandler> family,
            ICilLabel start, ICilLabel end, ICilLabel payloadStart,
            Dictionary<CilExceptionHandler, CilExceptionHandler> aliases, bool source,
            out List<CilExceptionHandler> children)
        {
            children = new();
            var code = method.Instructions;
            var positions = new Dictionary<CilInstruction, int>(ReferenceEqualityComparer.Instance);
            for (var index = 0; index < code.Count; index++)
                positions.Add(code[index], index);
            if (!TryGetCopyPosition(start, positions, code.EndLabel, out var first) ||
                !TryGetCopyPosition(end, positions, code.EndLabel, out var last) ||
                !TryGetCopyPosition(payloadStart, positions, code.EndLabel, out var payload) ||
                first <= 0 || first > payload || payload >= last)
                return Fail("unsupported-shared-native-handler-range", "A copied catch requires a distinct, nonempty instruction range.");

            var envelope = new CatchRange(first, last);
            var contents = new CatchRange(payload, last);
            var clauses = new List<CatchCopyClause>();
            var descendants = new List<CatchCopyClause>();
            var rootIndex = method.ExceptionHandlers.IndexOf(family[0]);
            for (var index = 0; index < method.ExceptionHandlers.Count; index++)
            {
                var handler = method.ExceptionHandlers[index];
                if (aliases?.ContainsKey(handler) == true) continue;
                if (!ReadRange(handler.TryStart, handler.TryEnd, out var protectedRange) ||
                    !ReadRange(handler.HandlerStart, handler.HandlerEnd, out var handlerRange))
                    return Fail("unsupported-shared-native-handler-range", "An exception clause has unresolved or empty boundaries.");
                CatchRange[] regions;
                if (handler.HandlerType == CilExceptionHandlerType.Filter)
                {
                    if (!ReadRange(handler.FilterStart, handler.HandlerStart, out var filterRange) ||
                        code[filterRange.End - 1].OpCode.Code != CilCode.Endfilter)
                        return Fail("unsupported-shared-native-handler-filter", "A filter requires a complete range ending in endfilter.");
                    regions = new[] { protectedRange, handlerRange, filterRange };
                }
                else
                {
                    if (handler.FilterStart != null || handler.HandlerType is not
                            (CilExceptionHandlerType.Exception or CilExceptionHandlerType.Finally or CilExceptionHandlerType.Fault))
                        return Fail("unsupported-shared-native-handler-kind", "An exception clause has an unsupported handler kind.");
                    regions = new[] { protectedRange, handlerRange };
                }
                for (var left = 0; left < regions.Length; left++)
                    for (var right = left + 1; right < regions.Length; right++)
                        if (regions[left].Overlaps(regions[right]))
                            return Fail("unsupported-shared-native-handler-regions", "An exception clause overlaps its own protected, filter or handler region.");

                var clause = new CatchCopyClause(handler, index, regions);
                clauses.Add(clause);
                if (family.Contains(handler))
                {
                    if (protectedRange.Overlaps(envelope))
                        return Fail("unsupported-shared-native-handler-regions", "A shared catch overlaps its protected region.");
                    continue;
                }
                if (!clause.Overlaps(envelope)) continue;
                if (!clause.Fits(contents) || index >= rootIndex)
                    return Fail("unsupported-shared-native-handler-regions", "A copied catch requires complete contained clauses preceding their owner.");
                descendants.Add(clause);
                children.Add(handler);
            }

            for (var left = 0; left < descendants.Count; left++)
                for (var right = left + 1; right < descendants.Count; right++)
                    if (!ValidateCatchNesting(descendants[left], descendants[right])) return false;
            return ValidateCatchFlow(method, positions, clauses, envelope, source);

            bool ReadRange(ICilLabel from, ICilLabel to, out CatchRange range)
            {
                range = default;
                if (!TryGetCopyPosition(from, positions, code.EndLabel, out var begin) ||
                    !TryGetCopyPosition(to, positions, code.EndLabel, out var limit) || begin >= limit)
                    return false;
                range = new CatchRange(begin, limit);
                return true;
            }
        }

        private bool ValidateCatchNesting(CatchCopyClause left, CatchCopyClause right)
        {
            if (left.Regions[0] == right.Regions[0])
            {
                if (IsCatch(left.Handler) && IsCatch(right.Handler))
                {
                    var disjoint = true;
                    for (var first = 1; first < left.Regions.Length; first++)
                        for (var second = 1; second < right.Regions.Length; second++)
                            disjoint &= !left.Regions[first].Overlaps(right.Regions[second]);
                    if (disjoint) return true;
                }
            }
            else
            {
                // CLI clauses may nest in a try or handler, never in a filter.
                for (var region = 0; region < 2; region++)
                {
                    if (left.Fits(right.Regions[region]))
                        return left.Index < right.Index || BadLayout();
                    if (right.Fits(left.Regions[region]))
                        return right.Index < left.Index || BadLayout();
                }
                var disjoint = true;
                foreach (var region in left.Regions)
                    disjoint &= !right.Overlaps(region);
                if (disjoint) return true;
            }
            return BadLayout();

            bool BadLayout() => Fail("unsupported-shared-native-handler-nesting",
                "Nested clauses require ordered containment or disjoint handlers; crossing regions and nested shared bodies are unsupported.");
        }

        private static bool IsCatch(CilExceptionHandler handler) =>
            handler.HandlerType is CilExceptionHandlerType.Exception or CilExceptionHandlerType.Filter;

        private bool ValidateCatchFlow(CilMethodBody method, Dictionary<CilInstruction, int> positions,
            List<CatchCopyClause> clauses, CatchRange envelope, bool source)
        {
            var code = method.Instructions;
            for (var index = 0; index < code.Count; index++)
            {
                var instruction = code[index];
                if (envelope.Contains(index))
                {
                    if (instruction.OpCode.Code is CilCode.Ret or CilCode.Jmp)
                        return BadFlow("A copied catch cannot return or jump out of its method.");
                    if (instruction.OpCode.Code is CilCode.Endfilter or CilCode.Endfinally or CilCode.Rethrow)
                    {
                        CatchCopyClause nearest = null;
                        var nearestRegion = -1;
                        var width = int.MaxValue;
                        var caught = false;
                        foreach (var clause in clauses)
                            for (var region = 0; region < clause.Regions.Length; region++)
                            {
                                var range = clause.Regions[region];
                                if (!range.Contains(index)) continue;
                                caught |= region == 1 && IsCatch(clause.Handler);
                                if (range.End - range.Start >= width) continue;
                                nearest = clause;
                                nearestRegion = region;
                                width = range.End - range.Start;
                            }
                        if (instruction.OpCode.Code == CilCode.Rethrow && !caught ||
                            instruction.OpCode.Code == CilCode.Endfilter &&
                                (nearestRegion != 2 || nearest.Regions[2].End - 1 != index) ||
                            instruction.OpCode.Code == CilCode.Endfinally &&
                                (nearestRegion != 1 || nearest.Handler.HandlerType is not
                                    (CilExceptionHandlerType.Finally or CilExceptionHandlerType.Fault)))
                            return BadFlow("An exception exit is outside its owning handler or filter.");
                    }
                }
                switch (instruction.OpCode.OperandType)
                {
                    case CilOperandType.InlineBrTarget:
                    case CilOperandType.ShortInlineBrTarget:
                        if (!CheckLabel(instruction.Operand as ICilLabel, index, instruction)) return false;
                        break;
                    case CilOperandType.InlineSwitch:
                        if (instruction.Operand is not IReadOnlyList<ICilLabel> targets)
                            return BadFlow("A switch requires resolved instruction labels.");
                        foreach (var destination in targets)
                            if (!CheckLabel(destination, index, instruction)) return false;
                        break;
                    default:
                        if (instruction.Operand is ICilLabel or IReadOnlyList<ICilLabel>)
                            return BadFlow("An unsupported operand holds a handler label.");
                        break;
                }
                if (instruction.OpCode.FlowControl is not
                        (CilFlowControl.Branch or CilFlowControl.Return or CilFlowControl.Throw) &&
                    instruction.OpCode.Code is not (CilCode.Jmp or CilCode.Endfilter or CilCode.Endfinally) &&
                    !CheckEdge(index, index + 1, false))
                    return false;
            }
            return true;

            bool CheckLabel(ICilLabel label, int from, CilInstruction instruction)
            {
                if (label is not CilInstructionLabel { Instruction: not null } destination ||
                    !positions.TryGetValue(destination.Instruction, out var to))
                    return BadFlow("A copied catch requires resolved instruction labels.");
                return CheckEdge(from, to, instruction.OpCode.Code is CilCode.Leave or CilCode.Leave_S);
            }

            bool CheckEdge(int from, int to, bool leave)
            {
                var inside = envelope.Contains(from);
                var targetInside = envelope.Contains(to);
                if (!inside && !targetInside) return true;
                if (inside != targetInside && (!inside || !leave))
                    return BadFlow("Only leave may cross a copied catch's boundary; external entry is unsupported.");
                foreach (var clause in clauses)
                    for (var region = 0; region < clause.Regions.Length; region++)
                    {
                        var range = clause.Regions[region];
                        var containsFrom = range.Contains(from);
                        var containsTo = range.Contains(to);
                        if (containsFrom == containsTo) continue;
                        if (containsFrom)
                        {
                            if (!leave || region == 2 || region == 1 && !IsCatch(clause.Handler))
                                return BadFlow("Control flow cannot leave this exception region through an ordinary branch or fallthrough.");
                        }
                        else
                        {
                            if (region != 0 || to != range.Start &&
                                (!leave || !IsCatch(clause.Handler) || !clause.Regions[1].Contains(from)))
                                return BadFlow("Control flow enters an unrelated handler, filter or protected block.");
                            if (source && to == range.Start && !leave &&
                                (!inputStacks.TryGetValue(code[to], out var stack) || stack == null || stack.Count != 0))
                                return BadFlow("Entry to a nested protected block requires a proven empty evaluation stack.");
                        }
                    }
                return true;
            }

            bool BadFlow(string message) => Fail("unsupported-shared-native-handler-flow", message);
        }
    }
}
