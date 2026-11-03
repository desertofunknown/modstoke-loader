// SPDX-License-Identifier: GPL-3.0-or-later
// Restored IL translation adapted from Il2CppInterop UnstripTranslator (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private readonly Dictionary<CilInstruction, CilInstruction> exceptionEntryPrefixes =
            new(ReferenceEqualityComparer.Instance);
        private Dictionary<CilExceptionHandler, CilExceptionHandler> translatedExceptionHandlers;
        private Dictionary<CilInstruction, IReadOnlyList<CilLocalVariable>> exceptionBridgeLocals;
        private bool recordExceptionBridgeLocals;

        private bool CopyExceptionHandlers(CilMethodBody original)
        {
            if (original.ExceptionHandlers.Count == 0)
                return LowerExceptionThrows(original);
            var catchTypes = new Dictionary<CilExceptionHandler, ITypeDefOrRef>();
            var nativeTypes = new Dictionary<CilExceptionHandler, TypeSignature>();
            var nativeHandlers = new Dictionary<CilExceptionHandler, (ICilLabel Filter, ICilLabel Handler)>();
            var foldedHandlers = new HashSet<CilExceptionHandler>();
            var groups = new List<(List<CilExceptionHandler> Clauses, TypeSignature EntryType, bool Adjacent)>();
            var grouped = new HashSet<CilExceptionHandler>();
            foreach (var handler in original.ExceptionHandlers)
            {
                if (!TryResolveCatchType(original, handler, out var exceptionType, out var nativeCatchType))
                    return false;
                catchTypes.Add(handler, exceptionType);
                nativeTypes.Add(handler, nativeCatchType);
            }
            foreach (var handler in original.ExceptionHandlers)
            {
                if (grouped.Contains(handler) || nativeTypes[handler] == null)
                    continue;
                if (!TryGetNativeHandlerGroup(original, handler, nativeTypes, out var group, out var entryType,
                        out var adjacent))
                    return false;
                groups.Add((group, entryType, adjacent));
                recordExceptionBridgeLocals |= !adjacent;
                foreach (var clause in group)
                    grouped.Add(clause);
            }
            Dictionary<CilExceptionHandler, CilExceptionHandler> sourceAliases = null;
            if (recordExceptionBridgeLocals)
            {
                sourceAliases = new(ReferenceEqualityComparer.Instance);
                foreach (var (group, _, adjacent) in groups)
                    if (adjacent)
                        for (var index = 1; index < group.Count; index++)
                            sourceAliases.Add(group[index], group[0]);
            }
            foreach (var (group, entryType, adjacent) in groups)
            {
                var handler = group[0];
                List<CilExceptionHandler> children = null;
                if (!adjacent && !ValidateHandlerCopyRegion(original, group,
                        handler.HandlerStart, handler.HandlerEnd, handler.HandlerStart, sourceAliases,
                        true, out children))
                    return false;
                var types = group.ConvertAll(clause => nativeTypes[clause]);
                ICilLabel filter, entry;
                if (handler.HandlerType == CilExceptionHandlerType.Filter)
                {
                    if (!LowerNativeFilter(original, handler, entryType, out filter, out entry))
                        return false;
                }
                else if (!LowerNativeCatch(handler, adjacent ? types : new[] { types[0] }, entryType,
                             out filter, out entry))
                    return false;
                nativeHandlers.Add(handler, (filter, entry));
                for (var index = 1; index < group.Count; index++)
                {
                    if (adjacent)
                        foldedHandlers.Add(group[index]);
                    else
                        nativeHandlers.Add(group[index], (filter, entry));
                }
                if (!adjacent)
                    (sharedCatchBodies ??= new()).Add(new SharedCatchBody(group, types, entryType, children));
            }

            // All prefixes must exist before mapping exclusive ends, including an earlier
            // clause's end that coincides with a later native handler's entry.
            if (recordExceptionBridgeLocals)
                translatedExceptionHandlers = new(ReferenceEqualityComparer.Instance);
            foreach (var handler in original.ExceptionHandlers)
            {
                if (foldedHandlers.Contains(handler))
                    continue;
                if (!TryCopyBoundary(handler.TryStart, original, false, "TryStart", out var tryStart) ||
                    !TryCopyBoundary(handler.TryEnd, original, true, "TryEnd", out var tryEnd) ||
                    !TryCopyBoundary(handler.HandlerStart, original, false, "HandlerStart", out var handlerStart) ||
                    !TryCopyBoundary(handler.HandlerEnd, original, true, "HandlerEnd", out var handlerEnd) ||
                    !TryCopyBoundary(handler.FilterStart, original, false, "FilterStart", out var filterStart))
                    return false;

                var native = nativeHandlers.TryGetValue(handler, out var lowered);
                var copy = new CilExceptionHandler
                {
                    HandlerType = native ? CilExceptionHandlerType.Filter : handler.HandlerType,
                    TryStart = tryStart,
                    TryEnd = tryEnd,
                    HandlerStart = native ? lowered.Handler : handlerStart,
                    HandlerEnd = handlerEnd,
                    FilterStart = native ? lowered.Filter : filterStart,
                    ExceptionType = catchTypes[handler]
                };
                target.CilMethodBody.ExceptionHandlers.Add(copy);
                translatedExceptionHandlers?.Add(handler, copy);
            }
            if (translatedExceptionHandlers != null)
                foreach (var (group, _, adjacent) in groups)
                    if (adjacent)
                        for (var index = 1; index < group.Count; index++)
                            translatedExceptionHandlers.Add(group[index], translatedExceptionHandlers[group[0]]);
            if (sharedCatchBodies != null)
                foreach (var group in sharedCatchBodies)
                    group.Handlers = group.Clauses.ConvertAll(clause => translatedExceptionHandlers[clause]);
            return LowerExceptionThrows(original);
        }

        private bool TryResolveCatchType(CilMethodBody original, CilExceptionHandler handler,
            out ITypeDefOrRef exceptionType, out TypeSignature nativeCatchType)
        {
            exceptionType = null;
            nativeCatchType = null;
            var start = (handler.HandlerStart as CilInstructionLabel)?.Instruction;
            if (handler.HandlerType == CilExceptionHandlerType.Filter)
            {
                // CLI filter and filter-handler entries receive System.Object, not the
                // predicate's isinst type. Convert before either source entry executes.
                var sourceObject = original.Owner.DeclaringModule.CorLibTypeFactory.Object;
                nativeCatchType = resolver.Resolve(sourceObject);
                var filterRejection = GetNativeExceptionRejection(sourceObject, nativeCatchType, true);
                return filterRejection == null || Fail("unsupported-exception-filter", filterRejection, start);
            }
            if (handler.HandlerType is CilExceptionHandlerType.Finally or CilExceptionHandlerType.Fault)
                return true;
            if (handler.HandlerType != CilExceptionHandlerType.Exception || handler.ExceptionType == null)
                return Fail("unsupported-exception-handler", "The exception handler has no supported catch type.",
                    start, handler.ExceptionType);

            var sourceType = handler.ExceptionType.ToTypeSignature();
            var catchObject = sourceType is CorLibTypeSignature { ElementType: ElementType.Object };
            var catchException = sourceType.FullName == "System.Exception" &&
                InteropRestoredTypeSupport.GetAssemblyName(sourceType) == "mscorlib";
            if ((catchObject || catchException) && DiscardsCaughtException(original, handler))
            {
                // Preserve existing catch-all behavior, including CLR infrastructure failures.
                // No native value is exposed, so even legacy text-only carriers can be discarded.
                exceptionType = catchObject ? imports.Module.CorLibTypeFactory.Object.ToTypeDefOrRef() :
                    imports.Module.DefaultImporter.ImportType(typeof(Exception));
                return true;
            }

            var rewritten = resolver.Resolve(sourceType);
            var clrException = imports.Module.DefaultImporter.ImportTypeSignature(typeof(Exception));
            if (HasExceptionBase(rewritten, clrException))
            {
                // This is already a CLR exception hierarchy; no native conversion is involved.
                exceptionType = rewritten.ToTypeDefOrRef();
                return true;
            }
            var rejection = GetNativeExceptionRejection(sourceType, rewritten, catchObject);
            if (rejection != null)
                return Fail("unsupported-typed-catch", rejection, start, handler.ExceptionType);
            nativeCatchType = rewritten;
            return true;
        }

        private bool LowerNativeCatch(CilExceptionHandler handler, IReadOnlyList<TypeSignature> catchTypes, TypeSignature entryType,
            out ICilLabel filterStart, out ICilLabel handlerStart)
        {
            filterStart = handlerStart = null;
            if (handler.HandlerStart is not CilInstructionLabel { Instruction: not null } start ||
                !instructions.TryGetValue(start.Instruction, out var translated))
                return Fail("unsupported-exception-boundary", "A native catch requires an instruction-backed handler entry.");
            if (exceptionEntryPrefixes.ContainsKey(start.Instruction))
                return Fail("unsupported-shared-native-handler", "A native handler cannot share another clause's filter conversion entry.", start.Instruction);
            var prefix = CreateNativeCatchPrefix(catchTypes, entryType, out filterStart, out handlerStart);
            var index = body.IndexOf(translated);
            foreach (var instruction in prefix)
                body.Insert(index++, instruction);
            exceptionEntryPrefixes.Add(start.Instruction, prefix[0]);
            // Branches inside the handler already carry its native value; they bypass conversion.
            return true;
        }

        private List<CilInstruction> CreateNativeCatchPrefix(IReadOnlyList<TypeSignature> catchTypes,
            TypeSignature entryType, out ICilLabel filterStart, out ICilLabel handlerStart)
        {
            var rejection = new CilLocalVariable(imports.Module.DefaultImporter.ImportTypeSignature(typeof(Exception)));
            target.CilMethodBody.LocalVariables.Add(rejection);
            var unwrap = ImportExceptionHelper(nameof(InteropExceptionSupport.GetNativeException), entryType);
            var prefix = new List<CilInstruction>();
            if (catchTypes.Count == 1)
            {
                prefix.Add(new CilInstruction(CilOpCodes.Ldloca, rejection));
                prefix.Add(new CilInstruction(CilOpCodes.Call,
                    ImportExceptionHelper(nameof(InteropExceptionSupport.TryMatchNativeException), catchTypes[0])));
                prefix.Add(new CilInstruction(CilOpCodes.Endfilter));
            }
            else
            {
                // Coalescing adjacent catches preserves first-match order and emits one legal
                // CLR handler instead of overlapping ranges or branches into another handler.
                var accepted = new CilInstruction(CilOpCodes.Pop);
                var end = new CilInstruction(CilOpCodes.Endfilter);
                foreach (var catchType in catchTypes)
                {
                    prefix.Add(new CilInstruction(CilOpCodes.Dup));
                    prefix.Add(new CilInstruction(CilOpCodes.Ldloca, rejection));
                    prefix.Add(new CilInstruction(CilOpCodes.Call,
                        ImportExceptionHelper(nameof(InteropExceptionSupport.TryMatchNativeException), catchType)));
                    prefix.Add(new CilInstruction(CilOpCodes.Brtrue, new CilInstructionLabel(accepted)));
                }
                prefix.Add(new CilInstruction(CilOpCodes.Pop));
                prefix.Add(new CilInstruction(CilOpCodes.Ldc_I4_0));
                prefix.Add(new CilInstruction(CilOpCodes.Br, new CilInstructionLabel(end)));
                prefix.Add(accepted);
                prefix.Add(new CilInstruction(CilOpCodes.Ldc_I4_1));
                prefix.Add(end);
            }
            var handlerEntry = new CilInstruction(CilOpCodes.Ldloc, rejection);
            prefix.Add(handlerEntry);
            prefix.Add(new CilInstruction(CilOpCodes.Call, unwrap));
            filterStart = new CilInstructionLabel(prefix[0]);
            handlerStart = new CilInstructionLabel(handlerEntry);
            if (recordExceptionBridgeLocals)
                (exceptionBridgeLocals ??= new(ReferenceEqualityComparer.Instance)).Add(prefix[0], new[] { rejection });
            return prefix;
        }

        private static bool DiscardsCaughtException(CilMethodBody original, CilExceptionHandler handler)
        {
            if (handler.HandlerStart is not CilInstructionLabel { Instruction: not null } start)
                return false;
            var index = original.Instructions.IndexOf(start.Instruction);
            if (index < 0)
                return false;
            var end = handler.HandlerEnd as CilInstructionLabel;
            for (; index < original.Instructions.Count; index++)
            {
                var instruction = original.Instructions[index];
                if (ReferenceEquals(instruction, end?.Instruction))
                    return false;
                if (instruction.OpCode.Code != CilCode.Nop)
                    return instruction.OpCode.Code == CilCode.Pop;
            }
            return false;
        }

        private bool LowerExceptionThrows(CilMethodBody original)
        {
            var clrException = imports.Module.DefaultImporter.ImportTypeSignature(typeof(Exception));
            foreach (var source in original.Instructions)
            {
                if (source.OpCode.Code != CilCode.Throw)
                    continue;
                if (!TryGetStackValue(source, 0, out var value))
                    return Fail("unsupported-throw-value", "The thrown value type is not proven on every incoming stack path.", source);
                if (value.IsNull)
                    continue; // Keep the CLR throw-null behavior, including its exception site.
                var rewritten = resolver.Resolve(value.Type);
                if (HasExceptionBase(rewritten, clrException))
                    continue;
                // Native box/isinst/castclass bridges can produce System.Object even when
                // the runtime value is an exception. Prove that identity at the throw site.
                var nativeObject = value.Type is CorLibTypeSignature { ElementType: ElementType.Object };
                var identityFailure = GetNativeExceptionRejection(value.Type, rewritten, nativeObject);
                if (identityFailure != null)
                    return Fail("unsupported-native-throw", identityFailure, source, value.Type);

                // Mutate the mapped instruction in place: every branch and exclusive EH end
                // still targets the start of this expanded source operation. Rethrow is untouched.
                var translated = instructions[source];
                if (translated.OpCode.Code != CilCode.Throw)
                    return Fail("unsupported-throw-lowering", "The translated throw has already been replaced.", source);
                translated.OpCode = CilOpCodes.Call;
                translated.Operand = nativeObject
                    ? ImportExceptionHelper(nameof(InteropExceptionSupport.WrapNativeExceptionObject),
                        context.GetAssemblyByName("mscorlib").GetTypeByName("System.Exception").NewType.ToTypeSignature())
                    : imports.Module.DefaultImporter.ImportMethod(
                        typeof(InteropExceptionSupport).GetMethod(nameof(InteropExceptionSupport.WrapNativeException)));
                body.Insert(body.IndexOf(translated) + 1, new CilInstruction(CilOpCodes.Throw));
            }
            return true;
        }

        private bool TryCopyBoundary(ICilLabel source, CilMethodBody original, bool allowEnd, string boundary, out ICilLabel copy)
        {
            copy = null;
            if (source == null)
                return true;
            if (source is CilInstructionLabel { Instruction: not null } instructionLabel)
            {
                // An exclusive end at a source handler entry must stop before its new filter,
                // while nested region starts and branch targets retain the original instruction.
                copy = new CilInstructionLabel(allowEnd &&
                    exceptionEntryPrefixes.TryGetValue(instructionLabel.Instruction, out var prefix)
                        ? prefix : instructions[instructionLabel.Instruction]);
                return true;
            }
            if (allowEnd && ReferenceEquals(source, original.Instructions.EndLabel))
            {
                // Exclusive region ends can point past the final instruction, even after IL expansion.
                copy = body.EndLabel;
                return true;
            }
            return Fail("unsupported-exception-boundary", "Exception handler " +
                target.CilMethodBody.ExceptionHandlers.Count + " " + boundary +
                " is not an instruction label" + (allowEnd ? " or the source end label." : "."),
                operand: source.GetType().FullName);
        }
    }
}
