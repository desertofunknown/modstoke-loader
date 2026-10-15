// SPDX-License-Identifier: GPL-3.0-or-later
// Native-value conversions adapted from Il2CppInterop ILGeneratorEx (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Collections;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static partial class InteropNativeValues
    {
        internal static void EmitPointerToObject(CilInstructionCollection body, TypeSignature originalReturnType,
            TypeSignature convertedReturnType, InteropTypeContext enclosingType, CilLocalVariable pointerVariable,
            bool extraDerefForNonValueTypes, bool unboxValueType)
        {
            var imports = enclosingType.AssemblyContext.Imports;
            if (originalReturnType is GenericParameterSignature)
            {
                body.Add(CilOpCodes.Ldloc, pointerVariable);
                body.Add(extraDerefForNonValueTypes ? CilOpCodes.Ldc_I4_1 : CilOpCodes.Ldc_I4_0);
                body.Add(unboxValueType ? CilOpCodes.Ldc_I4_1 : CilOpCodes.Ldc_I4_0);
                body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                    imports.IL2CPP_PointerToValueGeneric.Value.MakeGenericInstanceMethod(convertedReturnType)));
                return;
            }

            if (originalReturnType.FullName == "System.Void")
                return;

            if (originalReturnType is PointerTypeSignature or ByReferenceTypeSignature)
            {
                body.Add(CilOpCodes.Ldloc, pointerVariable);
                return;
            }

            if (InteropTypeReferences.IsValueType(originalReturnType))
            {
                if (InteropTypeReferences.IsValueType(convertedReturnType))
                {
                    body.Add(CilOpCodes.Ldloc, pointerVariable);
                    if (unboxValueType)
                        body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_unbox.Value);
                    body.Add(CilOpCodes.Ldobj, convertedReturnType.ToTypeDefOrRef());
                }
                else
                {
                    if (unboxValueType)
                    {
                        body.Add(CilOpCodes.Ldloc, pointerVariable);
                    }
                    else
                    {
                        var classPointerType = new GenericInstanceTypeSignature(
                            imports.Il2CppClassPointerStore.ToTypeDefOrRef(),
                            InteropTypeReferences.IsValueType(imports.Il2CppClassPointerStore), convertedReturnType);
                        var classPointerField = new MemberReference(classPointerType.ToTypeDefOrRef(),
                            "NativeClassPtr", new FieldSignature(imports.Module.CorLibTypeFactory.IntPtr));
                        body.Add(CilOpCodes.Ldsfld,
                            enclosingType.NewType.DeclaringModule.DefaultImporter.ImportField(classPointerField));
                        body.Add(CilOpCodes.Ldloc, pointerVariable);
                        body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_value_box.Value);
                    }

                    body.Add(CilOpCodes.Newobj, new MemberReference(convertedReturnType.ToTypeDefOrRef(),
                        ".ctor", MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void,
                            imports.Module.CorLibTypeFactory.IntPtr)));
                }
                return;
            }

            if (originalReturnType.FullName == "System.String")
            {
                body.Add(CilOpCodes.Ldloc, pointerVariable);
                if (extraDerefForNonValueTypes)
                    body.Add(CilOpCodes.Ldind_I);
                body.Add(CilOpCodes.Call, imports.IL2CPP_Il2CppStringToManaged.Value);
                return;
            }

            if (originalReturnType is ArrayBaseTypeSignature &&
                GetElementType(originalReturnType) is GenericParameterSignature)
            {
                body.Add(CilOpCodes.Ldloc, pointerVariable);
                if (extraDerefForNonValueTypes)
                    body.Add(CilOpCodes.Ldind_I);
                // The parent is caller-relative; the return signature uses the callee's !0.
                var actualReturnType = imports.Module.DefaultImporter.ImportTypeSignature(
                    imports.Il2CppArrayBase.MakeGenericInstanceType(
                        new GenericParameterSignature(GenericParameterType.Type, 0)));
                var method = new MemberReference(convertedReturnType.ToTypeDefOrRef(),
                    "WrapNativeGenericArrayPointer", MethodSignature.CreateStatic(actualReturnType,
                        imports.Module.CorLibTypeFactory.IntPtr));
                body.Add(CilOpCodes.Call, method);
                return;
            }

            var createPoolObject = new CilInstructionLabel();
            var endNop = new CilInstructionLabel();
            body.Add(CilOpCodes.Ldloc, pointerVariable);
            if (extraDerefForNonValueTypes)
                body.Add(CilOpCodes.Ldind_I);
            body.Add(CilOpCodes.Dup);
            body.Add(CilOpCodes.Brtrue_S, createPoolObject);
            body.Add(CilOpCodes.Pop);
            body.Add(CilOpCodes.Ldnull);
            body.Add(CilOpCodes.Br, endNop);
            createPoolObject.Instruction = body.Add(CilOpCodes.Call,
                imports.Module.DefaultImporter.ImportMethod(
                    imports.Il2CppObjectPool_Get.Value.MakeGenericInstanceMethod(convertedReturnType)));
            endNop.Instruction = body.Add(CilOpCodes.Nop);
        }

        internal static void EmitUpdateRef(CilInstructionCollection body, Parameter newMethodParameter,
            int argIndex, CilLocalVariable paramVariable, InteropRuntimeReferences imports)
        {
            LoadArgument(body, argIndex);
            body.Add(CilOpCodes.Ldloc, paramVariable);
            var elementType = GetElementType(newMethodParameter.ParameterType);
            if (elementType.FullName == "System.String")
            {
                body.Add(CilOpCodes.Call, imports.IL2CPP_Il2CppStringToManaged.Value);
            }
            else
            {
                body.Add(CilOpCodes.Dup);
                var nullBranch = new CilInstructionLabel();
                var storeNop = new CilInstructionLabel();
                body.Add(CilOpCodes.Brfalse_S, nullBranch);
                if (elementType is GenericParameterSignature)
                {
                    body.Add(CilOpCodes.Ldc_I4_0);
                    body.Add(CilOpCodes.Ldc_I4_0);
                    body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                        imports.IL2CPP_PointerToValueGeneric.Value.MakeGenericInstanceMethod(elementType)));
                }
                else
                {
                    body.Add(CilOpCodes.Newobj, new MemberReference(elementType.ToTypeDefOrRef(), ".ctor",
                        MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void,
                            imports.Module.CorLibTypeFactory.IntPtr)));
                }

                body.Add(CilOpCodes.Br_S, storeNop);
                nullBranch.Instruction = body.Add(CilOpCodes.Pop);
                body.Add(CilOpCodes.Ldnull);
                storeNop.Instruction = body.Add(CilOpCodes.Nop);
            }
            body.Add(CilOpCodes.Stind_Ref);
        }
    }
}
