// SPDX-License-Identifier: GPL-3.0-or-later
// Restored IL translation adapted from Il2CppInterop UnstripTranslator (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool CopyExceptionHandlers(CilMethodBody original)
        {
            foreach (var handler in original.ExceptionHandlers)
            {
                if (!TryCopyBoundary(handler.TryStart, original, false, out var tryStart) ||
                    !TryCopyBoundary(handler.TryEnd, original, true, out var tryEnd) ||
                    !TryCopyBoundary(handler.HandlerStart, original, false, out var handlerStart) ||
                    !TryCopyBoundary(handler.HandlerEnd, original, true, out var handlerEnd) ||
                    !TryCopyBoundary(handler.FilterStart, original, false, out var filterStart))
                    return false;

                var copy = new CilExceptionHandler
                {
                    HandlerType = handler.HandlerType,
                    TryStart = tryStart,
                    TryEnd = tryEnd,
                    HandlerStart = handlerStart,
                    HandlerEnd = handlerEnd,
                    FilterStart = filterStart
                };
                if (handler.ExceptionType != null)
                {
                    // Native exceptions do not yet carry their original managed catch type.
                    if (handler.ExceptionType.ToTypeSignature() is not CorLibTypeSignature { ElementType: ElementType.Object })
                        return false;
                    copy.ExceptionType = imports.Module.CorLibTypeFactory.Object.ToTypeDefOrRef();
                }
                target.CilMethodBody.ExceptionHandlers.Add(copy);
            }
            return true;
        }

        private bool TryCopyBoundary(ICilLabel source, CilMethodBody original, bool allowEnd, out ICilLabel copy)
        {
            copy = null;
            if (source == null)
                return true;
            if (source is CilInstructionLabel { Instruction: not null } instructionLabel)
            {
                copy = new CilInstructionLabel(instructions[instructionLabel.Instruction]);
                return true;
            }
            if (allowEnd && ReferenceEquals(source, original.Instructions.EndLabel))
            {
                // Exclusive region ends can point past the final instruction, even after IL expansion.
                copy = body.EndLabel;
                return true;
            }
            return false;
        }
    }
}
