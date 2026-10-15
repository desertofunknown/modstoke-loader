// SPDX-License-Identifier: GPL-3.0-or-later
// Native value conversion adapted from Il2CppInterop ILGeneratorEx (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Collections;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static partial class InteropNativeValues
    {
        internal static void EmitObjectToPointer(CilInstructionCollection body, TypeSignature originalType,
            TypeSignature newType, InteropTypeContext enclosingType, int argumentIndex,
            bool valueTypeArgument0IsAPointer, bool allowNullable, bool unboxNonBlittableType,
            bool unboxNonBlittableGeneric, out CilLocalVariable refVariable)
        {
            refVariable = null;
            if (originalType is GenericParameterSignature)
            {
                EmitGenericObjectToPointer(body, newType, enclosingType, argumentIndex,
                    allowNullable, unboxNonBlittableGeneric);
                return;
            }

            var imports = enclosingType.AssemblyContext.Imports;
            if (originalType is ByReferenceTypeSignature)
            {
                if (InteropTypeReferences.IsValueType(GetElementType(newType)))
                {
                    LoadArgument(body, argumentIndex);
                    body.Add(CilOpCodes.Conv_I);
                }
                else if (InteropTypeReferences.IsValueType(GetElementType(originalType)))
                {
                    LoadArgument(body, argumentIndex);
                    body.Add(CilOpCodes.Ldind_Ref);
                    body.Add(CilOpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtrNotNull.Value);
                    body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_unbox.Value);
                }
                else
                {
                    refVariable = new CilLocalVariable(imports.Module.CorLibTypeFactory.IntPtr);
                    body.Owner.LocalVariables.Add(refVariable);
                    LoadArgument(body, argumentIndex);
                    body.Add(CilOpCodes.Ldind_Ref);
                    body.Add(CilOpCodes.Call, GetElementType(originalType).FullName == "System.String"
                        ? imports.IL2CPP_ManagedStringToIl2Cpp.Value
                        : imports.IL2CPP_Il2CppObjectBaseToPtr.Value);
                    body.Add(CilOpCodes.Stloc, refVariable);
                    body.Add(CilOpCodes.Ldloca, refVariable);
                    body.Add(CilOpCodes.Conv_I);
                }
            }
            else if (originalType is PointerTypeSignature)
                LoadArgument(body, argumentIndex);
            else if (InteropTypeReferences.IsValueType(originalType))
            {
                if (InteropTypeReferences.IsValueType(newType))
                {
                    if (argumentIndex == 0 && valueTypeArgument0IsAPointer)
                        body.Add(CilOpCodes.Ldarg_0);
                    else
                        LoadArgumentAddress(body, argumentIndex);
                }
                else
                {
                    LoadArgument(body, argumentIndex);
                    body.Add(CilOpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtrNotNull.Value);
                    if (unboxNonBlittableType)
                        body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_unbox.Value);
                }
            }
            else if (originalType.FullName == "System.String")
            {
                LoadArgument(body, argumentIndex);
                body.Add(CilOpCodes.Call, imports.IL2CPP_ManagedStringToIl2Cpp.Value);
            }
            else
            {
                LoadArgument(body, argumentIndex);
                body.Add(CilOpCodes.Call, allowNullable
                    ? imports.IL2CPP_Il2CppObjectBaseToPtr.Value
                    : imports.IL2CPP_Il2CppObjectBaseToPtrNotNull.Value);
            }
        }

        private static void EmitGenericObjectToPointer(CilInstructionCollection body, TypeSignature newType,
            InteropTypeContext enclosingType, int argumentIndex, bool allowNullable, bool unboxNonBlittableType)
        {
            var imports = enclosingType.AssemblyContext.Imports;
            var module = enclosingType.NewType.DeclaringModule;
            var managedType = module.DefaultImporter.ImportTypeSignature(typeof(Type));
            body.Add(CilOpCodes.Ldtoken, newType.ToTypeDefOrRef());
            body.Add(CilOpCodes.Call, new MemberReference(managedType.ToTypeDefOrRef(), "GetTypeFromHandle",
                MethodSignature.CreateStatic(managedType, module.DefaultImporter.ImportTypeSignature(typeof(RuntimeTypeHandle)))));
            body.Add(CilOpCodes.Callvirt, new MemberReference(managedType.ToTypeDefOrRef(), "get_IsValueType",
                MethodSignature.CreateInstance(module.CorLibTypeFactory.Boolean)));

            var done = new CilInstructionLabel();
            var valueType = new CilInstructionLabel();
            var stringType = new CilInstructionLabel();
            body.Add(CilOpCodes.Brtrue, valueType);
            LoadArgument(body, argumentIndex);
            body.Add(CilOpCodes.Box, newType.ToTypeDefOrRef());
            body.Add(CilOpCodes.Dup);
            body.Add(CilOpCodes.Isinst, imports.Module.CorLibTypeFactory.String.ToTypeDefOrRef());
            body.Add(CilOpCodes.Brtrue_S, stringType);
            body.Add(CilOpCodes.Isinst, imports.Il2CppObjectBase.ToTypeDefOrRef());
            body.Add(CilOpCodes.Call, allowNullable
                ? imports.IL2CPP_Il2CppObjectBaseToPtr.Value
                : imports.IL2CPP_Il2CppObjectBaseToPtrNotNull.Value);
            if (unboxNonBlittableType)
            {
                body.Add(CilOpCodes.Dup);
                body.Add(CilOpCodes.Brfalse_S, done);
                body.Add(CilOpCodes.Dup);
                body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_get_class.Value);
                body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_class_is_valuetype.Value);
                body.Add(CilOpCodes.Brfalse_S, done);
                body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_unbox.Value);
            }
            body.Add(CilOpCodes.Br, done);

            stringType.Instruction = body.Add(CilOpCodes.Nop);
            body.Add(CilOpCodes.Isinst, imports.Module.CorLibTypeFactory.String.ToTypeDefOrRef());
            body.Add(CilOpCodes.Call, imports.IL2CPP_ManagedStringToIl2Cpp.Value);
            body.Add(CilOpCodes.Br_S, done);

            valueType.Instruction = body.Add(CilOpCodes.Nop);
            LoadArgumentAddress(body, argumentIndex);
            done.Instruction = body.Add(CilOpCodes.Nop);
        }

        private static TypeSignature GetElementType(TypeSignature type) =>
            type is TypeSpecificationSignature specification ? specification.BaseType : type;

        private static Parameter GetArgument(CilInstructionCollection body, int index)
        {
            var method = body.Owner.Owner;
            if (method.IsStatic)
                return method.Parameters[index];
            return index == 0 ? method.Parameters.ThisParameter : method.Parameters[index - 1];
        }

        private static void LoadArgument(CilInstructionCollection body, int index) =>
            body.Add(CilOpCodes.Ldarg, GetArgument(body, index));

        private static void LoadArgumentAddress(CilInstructionCollection body, int index) =>
            body.Add(CilOpCodes.Ldarga, GetArgument(body, index));
    }
}
