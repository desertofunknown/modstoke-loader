// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private sealed record ArrayInitializer(TypeSignature Element, ElementType Storage, int Width, byte[] Data);

        private readonly Dictionary<CilInstruction, ArrayInitializer> arrayInitializers =
            new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<CilInstruction> arrayInitializerCalls = new(ReferenceEqualityComparer.Instance);

        private void FindArrayInitializers(CilMethodBody original)
        {
            var boundaries = new HashSet<CilInstruction>(ReferenceEqualityComparer.Instance);
            foreach (var instruction in original.Instructions)
            {
                if (instruction.Operand is ICilLabel label) AddBoundary(label);
                else if (instruction.Operand is IReadOnlyList<ICilLabel> labels)
                    foreach (var branch in labels) AddBoundary(branch);
            }
            foreach (var handler in original.ExceptionHandlers)
            {
                AddBoundary(handler.TryStart);
                AddBoundary(handler.TryEnd);
                AddBoundary(handler.HandlerStart);
                AddBoundary(handler.HandlerEnd);
                AddBoundary(handler.FilterStart);
            }

            var source = original.Instructions;
            for (var index = 3; index + 1 < source.Count; index++)
            {
                var token = source[index];
                var call = source[index + 1];
                if (token.OpCode.Code != CilCode.Ldtoken || token.Operand is not IFieldDescriptor field
                    || source[index - 1].OpCode.Code != CilCode.Dup
                    || source[index - 2].OpCode.Code != CilCode.Newarr
                    || source[index - 3].OpCode.Code != CilCode.Ldc_I4
                    || source[index - 3].Operand is not int count || count <= 0 || count > 4096
                    || call.OpCode.Code != CilCode.Call || call.Operand is not IMethodDescriptor method
                    || !IsArrayInitializerCall(method)
                    || boundaries.Contains(source[index - 2]) || boundaries.Contains(source[index - 1])
                    || boundaries.Contains(token) || boundaries.Contains(call))
                    continue;

                // Lower only a whole constant-length allocation/initialization sequence. Arbitrary
                // field handles, interior entries and potentially observable partial fills stay rejected.
                if (!TryGetArrayOperand(token, 0, out var array, out _, out var element)) continue;
                var storage = ArrayStorageType(array.BaseType);
                if (storage != ArrayStorageType(element)) continue;
                var width = storage switch
                {
                    ElementType.I1 or ElementType.U1 => 1,
                    ElementType.I2 or ElementType.U2 or ElementType.Char => 2,
                    ElementType.I4 or ElementType.U4 => 4,
                    ElementType.I8 or ElementType.U8 => 8,
                    _ => 0
                };
                if (width == 0) continue;
                var definition = field.Resolve();
                if (definition?.IsStatic != true || definition.FieldRva is not DataSegment data
                    || data.Data.Length != count * width)
                    continue;
                arrayInitializers.Add(token, new ArrayInitializer(element, storage.Value, width, data.Data));
                arrayInitializerCalls.Add(call);
            }

            void AddBoundary(ICilLabel label)
            {
                if (label is CilInstructionLabel { Instruction: not null } entry)
                    boundaries.Add(entry.Instruction);
            }
        }

        private static bool IsArrayInitializerCall(IMethodDescriptor method)
        {
            var signature = method.Signature;
            return method.Name == "InitializeArray"
                && method.DeclaringType?.FullName == "System.Runtime.CompilerServices.RuntimeHelpers"
                && signature != null && !signature.HasThis && signature.GenericParameterCount == 0
                && signature.ReturnType.ElementType == ElementType.Void && signature.ParameterTypes.Count == 2
                && signature.ParameterTypes[0].FullName == "System.Array"
                && signature.ParameterTypes[1].FullName == "System.RuntimeFieldHandle";
        }

        private CilInstruction EmitArrayInitializer(ArrayInitializer initializer)
        {
            // The original dup left both the result and the initialization receiver on the stack.
            // Setters preserve the native array's element representation and bounds behavior.
            var first = body.Add(CilOpCodes.Nop);
            for (var offset = 0; offset < initializer.Data.Length; offset += initializer.Width)
            {
                body.Add(CilOpCodes.Dup);
                body.Add(CilOpCodes.Ldc_I4, offset / initializer.Width);
                var data = initializer.Data.AsSpan(offset, initializer.Width);
                if (initializer.Width == 8)
                    body.Add(CilOpCodes.Ldc_I8, BinaryPrimitives.ReadInt64LittleEndian(data));
                else
                {
                    var value = initializer.Width switch
                    {
                        1 => (int)data[0],
                        2 => BinaryPrimitives.ReadUInt16LittleEndian(data),
                        _ => BinaryPrimitives.ReadInt32LittleEndian(data)
                    };
                    body.Add(CilOpCodes.Ldc_I4, value);
                }
                EmitArrayStoreConversion(initializer.Storage);
                EmitArrayAccessor(true, initializer.Element);
            }
            body.Add(CilOpCodes.Pop);
            return first;
        }
    }
}
