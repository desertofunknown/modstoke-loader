// SPDX-License-Identifier: GPL-3.0-or-later
// Native method arguments adapted from Il2CppInterop Pass50GenerateMethods (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed partial class InteropMethodBodies
    {
        private void FillArguments()
        {
            for (var index = 0; index < target.Parameters.Count; index++)
            {
                body.Add(CilOpCodes.Ldloc, arguments);
                if (index > 0)
                {
                    EmitPointerOffset(index);
                    body.Add(CilOpCodes.Add);
                }

                var parameter = target.Parameters[index];
                var element = GetElementType(parameter.ParameterType);
                if (parameter.Definition.IsOut && !InteropTypeReferences.IsValueType(element))
                {
                    var pointer = new CilLocalVariable(imports.Module.CorLibTypeFactory.IntPtr);
                    target.CilMethodBody.LocalVariables.Add(pointer);
                    if (element is GenericParameterSignature)
                    {
                        EmitIsValueType(element);
                        var valueType = new CilInstructionLabel();
                        var next = new CilInstructionLabel();
                        body.Add(CilOpCodes.Brtrue, valueType);
                        EmitOutputPointer(pointer);
                        body.Add(CilOpCodes.Br_S, next);
                        valueType.Instruction = body.Add(CilOpCodes.Nop);
                        body.Add(CilOpCodes.Ldarg, parameter);
                        next.Instruction = body.Add(CilOpCodes.Nop);
                    }
                    else
                    {
                        EmitOutputPointer(pointer);
                    }
                    writebacks.Add((index, pointer));
                }
                else
                {
                    InteropNativeValues.EmitObjectToPointer(body, original.Parameters[index].ParameterType, parameter.ParameterType,
                        method.DeclaringType, argumentOffset + index, false, true, true, false, out var pointer);
                    if (pointer != null)
                        writebacks.Add((index, pointer));
                }
                body.Add(CilOpCodes.Stind_I);
            }
        }

        private void EmitOutputPointer(CilLocalVariable pointer)
        {
            body.Add(CilOpCodes.Ldc_I4, 0);
            body.Add(CilOpCodes.Stloc, pointer);
            body.Add(CilOpCodes.Ldloca, pointer);
            body.Add(CilOpCodes.Conv_I);
        }

        private void WriteBackReferences()
        {
            foreach (var writeback in writebacks)
            {
                var parameter = target.Parameters[writeback.Index];
                var element = GetElementType(parameter.ParameterType);
                CilInstructionLabel next = null;
                if (parameter.Definition.IsOut && element is GenericParameterSignature)
                {
                    // Value-type instantiations were passed by reference and need no pointer writeback.
                    EmitIsValueType(element);
                    next = new CilInstructionLabel();
                    body.Add(CilOpCodes.Brtrue, next);
                }
                InteropNativeValues.EmitUpdateRef(body, parameter, writeback.Index + argumentOffset, writeback.Pointer, imports);
                if (next != null)
                    next.Instruction = body.Add(CilOpCodes.Nop);
            }
        }

        private void EmitIsValueType(TypeSignature element)
        {
            var module = imports.Module;
            var managedType = module.DefaultImporter.ImportTypeSignature(typeof(Type));
            var runtimeHandle = module.DefaultImporter.ImportTypeSignature(typeof(RuntimeTypeHandle));
            body.Add(CilOpCodes.Ldtoken, element.ToTypeDefOrRef());
            body.Add(CilOpCodes.Call, new MemberReference(managedType.ToTypeDefOrRef(), "GetTypeFromHandle",
                MethodSignature.CreateStatic(managedType, runtimeHandle)));
            body.Add(CilOpCodes.Callvirt, new MemberReference(managedType.ToTypeDefOrRef(), "get_IsValueType",
                MethodSignature.CreateInstance(module.CorLibTypeFactory.Boolean)));
        }

        private static TypeSignature GetElementType(TypeSignature type) =>
            type is TypeSpecificationSignature specification ? specification.BaseType : type;
    }
}
