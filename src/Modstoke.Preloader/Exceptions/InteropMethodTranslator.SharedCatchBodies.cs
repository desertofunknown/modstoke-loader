// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private List<SharedCatchBody> sharedCatchBodies;

        private sealed class SharedCatchBody
        {
            internal readonly List<CilExceptionHandler> Clauses;
            internal readonly List<TypeSignature> CatchTypes;
            internal readonly TypeSignature EntryType;
            internal readonly List<CilExceptionHandler> SourceChildren;
            internal List<CilExceptionHandler> Handlers;
            internal List<CilExceptionHandler> Children;
            internal List<CilInstruction> Payload;

            internal SharedCatchBody(List<CilExceptionHandler> clauses, List<TypeSignature> catchTypes,
                TypeSignature entryType, List<CilExceptionHandler> children)
            {
                Clauses = clauses;
                CatchTypes = catchTypes;
                EntryType = entryType;
                SourceChildren = children;
            }
        }

        private static bool TryGetCopyPosition(ICilLabel label, Dictionary<CilInstruction, int> positions,
            ICilLabel end, out int position)
        {
            if (ReferenceEquals(label, end))
            {
                position = positions.Count;
                return true;
            }
            position = -1;
            return label is CilInstructionLabel { Instruction: not null } instruction &&
                positions.TryGetValue(instruction.Instruction, out position);
        }

        private bool CompleteSharedCatchBodies()
        {
            if (sharedCatchBodies == null) return true;
            // Translation can add cleanup regions and expand instructions. Validate the final
            // ranges before copying, so every clone retains the complete lowered operations.
            foreach (var group in sharedCatchBodies)
            {
                var handler = group.Handlers[0];
                var sourceStart = ((CilInstructionLabel)group.Clauses[0].HandlerStart).Instruction;
                var payloadStart = new CilInstructionLabel(instructions[sourceStart]);
                if (!ValidateHandlerCopyRegion(target.CilMethodBody, group.Handlers,
                        handler.FilterStart, handler.HandlerEnd, payloadStart, null, false, out var children))
                    return false;
                var expected = new HashSet<CilExceptionHandler>(ReferenceEqualityComparer.Instance);
                foreach (var child in group.SourceChildren)
                    expected.Add(translatedExceptionHandlers[child]);
                if (!expected.SetEquals(children))
                    return Fail("unsupported-shared-native-handler-lowering", "A copied catch's emitted clauses differ from its source-owned subtree.");
                group.Children = children.ConvertAll(SnapshotCatchClause);
                var first = body.IndexOf(payloadStart.Instruction);
                var last = ReferenceEquals(handler.HandlerEnd, body.EndLabel) ? body.Count :
                    body.IndexOf(((CilInstructionLabel)handler.HandlerEnd).Instruction);
                group.Payload = new List<CilInstruction>(last - first);
                for (var index = first; index < last; index++)
                    group.Payload.Add(body[index]);
                if (!ValidateCatchLocals(group.Payload)) return false;
            }
            recordExceptionBridgeLocals = false;
            foreach (var group in sharedCatchBodies)
            {
                var oldEnd = group.Handlers[0].HandlerEnd;
                var last = ReferenceEquals(oldEnd, body.EndLabel) ? body.Count :
                    body.IndexOf(((CilInstructionLabel)oldEnd).Instruction);
                var added = new List<CilInstruction>();
                var entries = new List<(ICilLabel Filter, ICilLabel Handler, List<CilInstruction> Prefix)>();
                for (var index = 1; index < group.Handlers.Count; index++)
                {
                    var prefix = CreateNativeCatchPrefix(new[] { group.CatchTypes[index] }, group.EntryType,
                        out var filter, out var entry);
                    entries.Add((filter, entry, prefix));
                }
                // Exclusive ends stop before the copies. Starts and ordinary branch targets
                // stay at the original destination, even when that destination is another catch.
                var newEnd = entries[0].Filter;
                foreach (var handler in target.CilMethodBody.ExceptionHandlers)
                {
                    if (SameExceptionBoundary(handler.TryEnd, oldEnd)) handler.TryEnd = newEnd;
                    if (SameExceptionBoundary(handler.HandlerEnd, oldEnd)) handler.HandlerEnd = newEnd;
                }
                for (var index = 0; index < entries.Count; index++)
                {
                    var handler = group.Handlers[index + 1];
                    var end = index + 1 < entries.Count ? entries[index + 1].Filter : oldEnd;
                    if (!TryCopyCatchBody(group.Payload, group.Children, oldEnd, end,
                            out var copy, out var children)) return false;
                    added.AddRange(entries[index].Prefix);
                    added.AddRange(copy);
                    var slot = target.CilMethodBody.ExceptionHandlers.IndexOf(handler);
                    foreach (var child in children)
                        target.CilMethodBody.ExceptionHandlers.Insert(slot++, child);
                    handler.FilterStart = entries[index].Filter;
                    handler.HandlerStart = entries[index].Handler;
                    handler.HandlerEnd = end;
                }
                foreach (var instruction in added)
                    body.Insert(last++, instruction);
            }
            return true;
        }

        private bool ValidateCatchLocals(List<CilInstruction> payload)
        {
            if (exceptionBridgeLocals == null) return true;
            var owned = new HashSet<CilInstruction>(payload, ReferenceEqualityComparer.Instance);
            var locals = new HashSet<CilLocalVariable>(ReferenceEqualityComparer.Instance);
            foreach (var (prefix, variables) in exceptionBridgeLocals)
                if (owned.Contains(prefix))
                    foreach (var variable in variables)
                        locals.Add(variable);
            foreach (var instruction in body)
                if (instruction.Operand is CilLocalVariable local && locals.Contains(local) && !owned.Contains(instruction))
                    return Fail("unsupported-shared-native-handler-local", "A nested exception bridge local escapes its copied body.");
            return true;
        }

        private static CilExceptionHandler SnapshotCatchClause(CilExceptionHandler handler) => new()
        {
            HandlerType = handler.HandlerType,
            ExceptionType = handler.ExceptionType,
            TryStart = handler.TryStart,
            TryEnd = handler.TryEnd,
            HandlerStart = handler.HandlerStart,
            HandlerEnd = handler.HandlerEnd,
            FilterStart = handler.FilterStart
        };

        private bool TryCopyCatchBody(List<CilInstruction> original, List<CilExceptionHandler> children,
            ICilLabel originalEnd, ICilLabel copiedEnd, out List<CilInstruction> copy,
            out List<CilExceptionHandler> copiedChildren)
        {
            copy = new List<CilInstruction>(original.Count);
            copiedChildren = new List<CilExceptionHandler>(children.Count);
            var mapped = new Dictionary<CilInstruction, CilInstruction>(ReferenceEqualityComparer.Instance);
            var locals = new Dictionary<CilLocalVariable, CilLocalVariable>(ReferenceEqualityComparer.Instance);
            foreach (var instruction in original)
            {
                var cloned = new CilInstruction(instruction.OpCode, instruction.Operand);
                mapped.Add(instruction, cloned);
                copy.Add(cloned);
                if (exceptionBridgeLocals != null && exceptionBridgeLocals.TryGetValue(instruction, out var variables))
                    foreach (var variable in variables)
                    {
                        var local = new CilLocalVariable(variable.VariableType);
                        target.CilMethodBody.LocalVariables.Add(local);
                        locals.Add(variable, local);
                    }
            }
            foreach (var instruction in original)
            {
                if (instruction.Operand is ICilLabel label)
                {
                    if (!CopyLabel(instruction, label, out var cloned)) return false;
                    mapped[instruction].Operand = cloned;
                }
                else if (instruction.Operand is IReadOnlyList<ICilLabel> targets)
                {
                    var cloned = new List<ICilLabel>(targets.Count);
                    foreach (var destination in targets)
                    {
                        if (!CopyLabel(instruction, destination, out var targetLabel)) return false;
                        cloned.Add(targetLabel);
                    }
                    mapped[instruction].Operand = cloned;
                }
                else if (instruction.Operand is CilLocalVariable variable && locals.TryGetValue(variable, out var local))
                    mapped[instruction].Operand = local;
            }
            foreach (var child in children)
            {
                if (!CopyBoundary(child.TryStart, false, out var tryStart) ||
                    !CopyBoundary(child.TryEnd, true, out var tryEnd) ||
                    !CopyBoundary(child.HandlerStart, false, out var handlerStart) ||
                    !CopyBoundary(child.HandlerEnd, true, out var handlerEnd) ||
                    !CopyBoundary(child.FilterStart, false, out var filterStart)) return false;
                copiedChildren.Add(new CilExceptionHandler
                {
                    HandlerType = child.HandlerType,
                    ExceptionType = child.ExceptionType,
                    TryStart = tryStart,
                    TryEnd = tryEnd,
                    HandlerStart = handlerStart,
                    HandlerEnd = handlerEnd,
                    FilterStart = filterStart
                });
            }
            return true;

            bool CopyBoundary(ICilLabel label, bool end, out ICilLabel cloned)
            {
                cloned = null;
                if (label == null) return true;
                if (end && SameExceptionBoundary(label, originalEnd))
                {
                    cloned = copiedEnd;
                    return true;
                }
                if (label is CilInstructionLabel { Instruction: not null } destination &&
                    mapped.TryGetValue(destination.Instruction, out var local))
                {
                    cloned = new CilInstructionLabel(local);
                    return true;
                }
                return Fail("unsupported-shared-native-handler-boundary", "A nested exception boundary is outside its copied body.");
            }

            bool CopyLabel(CilInstruction instruction, ICilLabel label, out ICilLabel cloned)
            {
                cloned = null;
                if (label is not CilInstructionLabel { Instruction: not null } destination)
                    return Fail("unsupported-shared-native-handler-label", "A copied catch contains an unresolved label.");
                if (mapped.TryGetValue(destination.Instruction, out var local))
                    cloned = new CilInstructionLabel(local);
                else if (instruction.OpCode.Code is CilCode.Leave or CilCode.Leave_S)
                    cloned = new CilInstructionLabel(destination.Instruction);
                else
                    return Fail("unsupported-shared-native-handler-branch", "A copied catch branches outside its source body.");
                return true;
            }
        }
    }
}
