// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool LowerNativeFilter(CilMethodBody original, CilExceptionHandler handler, TypeSignature entryType,
            out ICilLabel filterStart, out ICilLabel handlerStart)
        {
            filterStart = handlerStart = null;
            if (handler.FilterStart is not CilInstructionLabel { Instruction: not null } filter ||
                handler.HandlerStart is not CilInstructionLabel { Instruction: not null } entry ||
                !instructions.TryGetValue(filter.Instruction, out var translatedFilter) ||
                !instructions.TryGetValue(entry.Instruction, out var translatedEntry))
                return Fail("unsupported-exception-boundary", "A source filter requires instruction-backed filter and handler entries.");
            var first = original.Instructions.IndexOf(filter.Instruction);
            var last = original.Instructions.IndexOf(entry.Instruction) - 1;
            if (first < 0 || last < first || original.Instructions[last].OpCode.Code != CilCode.Endfilter)
                return Fail("unsupported-exception-filter-layout", "A source filter must end immediately before its handler.", filter.Instruction);
            for (var index = first; index < last; index++)
                if (original.Instructions[index].OpCode.Code == CilCode.Endfilter)
                    return Fail("unsupported-exception-filter-layout", "A source filter requires one terminal endfilter.", original.Instructions[index]);
            if (exceptionEntryPrefixes.ContainsKey(filter.Instruction) || exceptionEntryPrefixes.ContainsKey(entry.Instruction))
                return Fail("unsupported-shared-native-filter", "A source filter cannot share its conversion entry with another clause.", filter.Instruction);

            var rejection = new CilLocalVariable(imports.Module.DefaultImporter.ImportTypeSignature(typeof(Exception)));
            var nativeValue = new CilLocalVariable(entryType);
            target.CilMethodBody.LocalVariables.Add(rejection);
            target.CilMethodBody.LocalVariables.Add(nativeValue);
            var match = ImportExceptionHelper(nameof(InteropExceptionSupport.TryMatchNativeException), entryType);
            var unwrap = ImportExceptionHelper(nameof(InteropExceptionSupport.GetNativeException), entryType);
            var end = new CilInstructionLabel(instructions[original.Instructions[last]]);
            var matched = new CilInstruction(CilOpCodes.Ldloc, rejection);
            var convert = new CilInstruction(CilOpCodes.Ldloc, rejection);
            var prefix = new[]
            {
                new CilInstruction(CilOpCodes.Ldnull),
                new CilInstruction(CilOpCodes.Stloc, nativeValue),
                new CilInstruction(CilOpCodes.Dup),
                new CilInstruction(CilOpCodes.Ldloca, rejection),
                new CilInstruction(CilOpCodes.Call, match),
                new CilInstruction(CilOpCodes.Brtrue, new CilInstructionLabel(matched)),
                new CilInstruction(CilOpCodes.Pop),
                new CilInstruction(CilOpCodes.Ldc_I4_0),
                new CilInstruction(CilOpCodes.Br, end),
                matched,
                new CilInstruction(CilOpCodes.Brfalse, new CilInstructionLabel(convert)),
                new CilInstruction(CilOpCodes.Pop),
                new CilInstruction(CilOpCodes.Ldc_I4_1),
                new CilInstruction(CilOpCodes.Br, end),
                convert,
                new CilInstruction(CilOpCodes.Call, unwrap),
                new CilInstruction(CilOpCodes.Dup),
                new CilInstruction(CilOpCodes.Stloc, nativeValue)
            };
            // Only the representation gate is inserted. The source predicate stays in the CLR
            // filter, so any exception it throws is still a failed filter on the first pass.
            var insert = body.IndexOf(translatedFilter);
            foreach (var instruction in prefix)
                body.Insert(insert++, instruction);
            filterStart = new CilInstructionLabel(prefix[0]);
            exceptionEntryPrefixes.Add(filter.Instruction, prefix[0]);
            if (recordExceptionBridgeLocals)
                (exceptionBridgeLocals ??= new(ReferenceEqualityComparer.Instance)).Add(prefix[0], new[] { rejection, nativeValue });

            // Re-enter from the CLR carrier after unwind. The private converted value preserves
            // reference equality; source predicates cannot overwrite this bridge-owned local.
            var handlerPrefix = new CilInstruction(CilOpCodes.Ldloc, rejection);
            insert = body.IndexOf(translatedEntry);
            body.Insert(insert++, handlerPrefix);
            body.Insert(insert++, new CilInstruction(CilOpCodes.Ldloc, nativeValue));
            body.Insert(insert, new CilInstruction(CilOpCodes.Call,
                ImportExceptionHelper(nameof(InteropExceptionSupport.GetNativeFilterException), entryType)));
            handlerStart = new CilInstructionLabel(handlerPrefix);
            exceptionEntryPrefixes.Add(entry.Instruction, handlerPrefix);
            return true;
        }

        private bool TryGetNativeHandlerGroup(CilMethodBody original, CilExceptionHandler handler,
            Dictionary<CilExceptionHandler, TypeSignature> nativeTypes, out List<CilExceptionHandler> group,
            out TypeSignature entryType, out bool adjacent)
        {
            group = new List<CilExceptionHandler>();
            entryType = nativeTypes[handler];
            adjacent = true;
            var firstIndex = original.ExceptionHandlers.IndexOf(handler);
            foreach (var candidate in original.ExceptionHandlers)
            {
                if (!SameExceptionBoundary(handler.HandlerStart, candidate.HandlerStart))
                    continue;
                if (group.Count != 0 || !ReferenceEquals(handler, candidate))
                {
                    if (handler.HandlerType != CilExceptionHandlerType.Exception ||
                        candidate.HandlerType != CilExceptionHandlerType.Exception || nativeTypes[candidate] == null ||
                        !SameExceptionBoundary(handler.TryStart, candidate.TryStart) ||
                        !SameExceptionBoundary(handler.TryEnd, candidate.TryEnd) ||
                        !SameExceptionBoundary(handler.HandlerEnd, candidate.HandlerEnd))
                        return Fail("unsupported-shared-native-handler",
                            "Shared native catches must protect the same try and complete handler range.",
                            (handler.HandlerStart as CilInstructionLabel)?.Instruction);
                }
                adjacent &= original.ExceptionHandlers.IndexOf(candidate) == firstIndex + group.Count;
                group.Add(candidate);
            }
            if (group.Count <= 1)
                return true;
            var sameType = true;
            foreach (var candidate in group)
                sameType &= SignatureComparer.Default.Equals(entryType, nativeTypes[candidate]);
            if (sameType)
                return true;

            TypeSignature sourceEntryType = null;
            if (DiscardsCaughtException(original, handler))
                sourceEntryType = original.Owner.DeclaringModule.CorLibTypeFactory.Object;
            else if (handler.HandlerStart is CilInstructionLabel { Instruction: not null } entry)
            {
                var end = (handler.HandlerEnd as CilInstructionLabel)?.Instruction;
                var first = original.Instructions.IndexOf(entry.Instruction);
                for (var index = first; index >= 0 && index < original.Instructions.Count; index++)
                {
                    var instruction = original.Instructions[index];
                    if (ReferenceEquals(instruction, end)) break;
                    if (instruction.OpCode.Code == CilCode.Nop) continue;
                    if (instruction.OpCode.Code == CilCode.Stloc && instruction.Operand is CilLocalVariable local)
                        sourceEntryType = local.VariableType;
                    else if (instruction.OpCode.Code == CilCode.Castclass && instruction.Operand is ITypeDefOrRef castType)
                        sourceEntryType = castType.ToTypeSignature();
                    break;
                }
            }
            entryType = resolver.Resolve(sourceEntryType);
            if (sourceEntryType == null || entryType == null ||
                GetNativeExceptionRejection(sourceEntryType, entryType, true) != null)
                return Fail("unsupported-shared-native-handler-value",
                    "Different native catches require a discarded value or an initial store or cast to a proven common native base.");
            foreach (var candidate in group)
                if (!HasExceptionBase(nativeTypes[candidate], entryType))
                    return Fail("unsupported-shared-native-handler-value",
                        "The shared handler entry type is not a proven base of every native catch type.");
            return true;
        }

        private static bool SameExceptionBoundary(ICilLabel left, ICilLabel right) =>
            ReferenceEquals(left, right) || left is CilInstructionLabel { Instruction: not null } first &&
                right is CilInstructionLabel second && ReferenceEquals(first.Instruction, second.Instruction);
    }
}
