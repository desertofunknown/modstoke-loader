// SPDX-License-Identifier: GPL-3.0-or-later
// Native value stores adapted from Il2CppInterop ILGeneratorEx (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static partial class InteropNativeValues
    {
        internal static void EmitObjectStore(CilInstructionCollection body, TypeSignature originalType,
            TypeSignature newType, InteropTypeContext enclosingType, int argumentIndex)
        {
            if (originalType is GenericParameterSignature)
            {
                EmitObjectStoreGeneric(body, newType, enclosingType, argumentIndex);
                return;
            }

            var imports = enclosingType.AssemblyContext.Imports;
            if (originalType.FullName == "System.String")
            {
                LoadArgument(body, argumentIndex);
                body.Add(CilOpCodes.Call, imports.IL2CPP_ManagedStringToIl2Cpp.Value);
                body.Add(CilOpCodes.Call, imports.WriteFieldWBarrier);
                return;
            }

            if (originalType is PointerTypeSignature or ByReferenceTypeSignature)
            {
                LoadArgument(body, argumentIndex);
                body.Add(CilOpCodes.Stobj, newType.ToTypeDefOrRef());
                body.Add(CilOpCodes.Pop);
                return;
            }

            if (InteropTypeReferences.IsValueType(originalType))
            {
                if (InteropTypeReferences.Classify(enclosingType.AssemblyContext.GlobalContext, originalType) ==
                    InteropTypeContext.TypeSpecifics.BlittableStruct)
                {
                    LoadArgument(body, argumentIndex);
                    body.Add(CilOpCodes.Stobj, newType.ToTypeDefOrRef());
                    body.Add(CilOpCodes.Pop);
                    return;
                }

                LoadArgument(body, argumentIndex);
                body.Add(CilOpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtr.Value);
                body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_unbox.Value);
                var store = new GenericInstanceTypeSignature(imports.Il2CppClassPointerStore.ToTypeDefOrRef(),
                    InteropTypeReferences.IsValueType(imports.Il2CppClassPointerStore), newType);
                var nativeClass = new MemberReference(store.ToTypeDefOrRef(), "NativeClassPtr",
                    new FieldSignature(imports.Module.CorLibTypeFactory.IntPtr));
                body.Add(CilOpCodes.Ldsfld,
                    enclosingType.NewType.DeclaringModule.DefaultImporter.ImportField(nativeClass));
                body.Add(CilOpCodes.Ldc_I4_0);
                body.Add(CilOpCodes.Conv_U);
                body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_class_value_size.Value);
                body.Add(CilOpCodes.Cpblk);
                body.Add(CilOpCodes.Pop);
                return;
            }

            LoadArgument(body, argumentIndex);
            body.Add(CilOpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtr.Value);
            body.Add(CilOpCodes.Call, imports.WriteFieldWBarrier);
        }

        private static void EmitObjectStoreGeneric(CilInstructionCollection body, TypeSignature newType,
            InteropTypeContext enclosingType, int argumentIndex)
        {
            var imports = enclosingType.AssemblyContext.Imports;
            var importer = enclosingType.NewType.DeclaringModule.DefaultImporter;
            body.Add(CilOpCodes.Ldtoken, newType.ToTypeDefOrRef());
            body.Add(CilOpCodes.Call, importer.ImportMethod(typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))));
            body.Add(CilOpCodes.Dup);
            body.Add(CilOpCodes.Callvirt, importer.ImportMethod(typeof(Type).GetProperty(nameof(Type.IsValueType)).GetMethod));

            var end = new CilInstructionLabel();
            var stringValue = new CilInstructionLabel();
            var managedValue = new CilInstructionLabel();
            var referenceValue = new CilInstructionLabel();
            body.Add(CilOpCodes.Brtrue, managedValue);
            body.Add(CilOpCodes.Callvirt, importer.ImportMethod(typeof(Type).GetProperty(nameof(Type.FullName)).GetMethod));
            body.Add(CilOpCodes.Ldstr, "System.String");
            body.Add(CilOpCodes.Call, importer.ImportMethod(typeof(string).GetMethod(nameof(string.Equals),
                new[] { typeof(string), typeof(string) })));
            body.Add(CilOpCodes.Brtrue_S, stringValue);

            LoadArgument(body, argumentIndex);
            body.Add(CilOpCodes.Box, newType.ToTypeDefOrRef());
            body.Add(CilOpCodes.Isinst, imports.Il2CppObjectBase.ToTypeDefOrRef());
            body.Add(CilOpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtr.Value);
            body.Add(CilOpCodes.Dup);
            body.Add(CilOpCodes.Brfalse_S, referenceValue);
            body.Add(CilOpCodes.Dup);
            body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_get_class.Value);
            body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_class_is_valuetype.Value);
            body.Add(CilOpCodes.Brfalse_S, referenceValue);
            body.Add(CilOpCodes.Dup);

            var nativeObject = new CilLocalVariable(imports.Module.CorLibTypeFactory.IntPtr);
            body.Owner.LocalVariables.Add(nativeObject);
            body.Add(CilOpCodes.Stloc, nativeObject);
            body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_unbox.Value);
            body.Add(CilOpCodes.Ldloc, nativeObject);
            body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_get_class.Value);
            body.Add(CilOpCodes.Ldc_I4_0);
            body.Add(CilOpCodes.Conv_U);
            body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_class_value_size.Value);
            body.Add(CilOpCodes.Cpblk);
            body.Add(CilOpCodes.Pop);
            body.Add(CilOpCodes.Br_S, end);

            referenceValue.Instruction = body.Add(CilOpCodes.Nop);
            body.Add(CilOpCodes.Call, imports.WriteFieldWBarrier);
            body.Add(CilOpCodes.Br_S, end);

            stringValue.Instruction = body.Add(CilOpCodes.Nop);
            LoadArgument(body, argumentIndex);
            body.Add(CilOpCodes.Box, newType.ToTypeDefOrRef());
            body.Add(CilOpCodes.Isinst, imports.Module.CorLibTypeFactory.String.ToTypeDefOrRef());
            body.Add(CilOpCodes.Call, imports.IL2CPP_ManagedStringToIl2Cpp.Value);
            body.Add(CilOpCodes.Call, imports.WriteFieldWBarrier);
            body.Add(CilOpCodes.Br_S, end);

            managedValue.Instruction = body.Add(CilOpCodes.Nop);
            body.Add(CilOpCodes.Pop);
            LoadArgument(body, argumentIndex);
            body.Add(CilOpCodes.Stobj, newType.ToTypeDefOrRef());
            body.Add(CilOpCodes.Pop);
            end.Instruction = body.Add(CilOpCodes.Nop);
        }
    }
}
