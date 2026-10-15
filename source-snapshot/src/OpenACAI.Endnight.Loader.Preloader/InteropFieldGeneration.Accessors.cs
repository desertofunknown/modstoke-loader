// SPDX-License-Identifier: GPL-3.0-or-later
// Field accessors adapted from Il2CppInterop FieldAccessorGenerator (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static partial class InteropFieldGeneration
    {
        private static void GenerateGetter(InteropFieldContext field, PropertyDefinition property,
            InteropRuntimeReferences imports)
        {
            var original = field.OriginalField;
            var resultType = property.Signature.ReturnType;
            var isValueType = InteropTypeReferences.IsValueType(resultType);
            var signature = original.IsStatic
                ? MethodSignature.CreateStatic(resultType)
                : MethodSignature.CreateInstance(resultType);
            var getter = new MethodDefinition("get_" + property.Name, AccessorAttributes(original), signature)
            {
                CilMethodBody = new CilMethodBody()
            };
            property.DeclaringType.Methods.Add(getter);
            var body = getter.CilMethodBody.Instructions;
            var storage = new CilLocalVariable(original.IsStatic && isValueType
                ? resultType
                : imports.Module.CorLibTypeFactory.IntPtr);
            getter.CilMethodBody.LocalVariables.Add(storage);

            if (original.IsStatic)
            {
                var needsBuffer = InteropTypeReferences.IsValueType(original.Signature.FieldType) && !isValueType;
                if (needsBuffer)
                {
                    var store = imports.Il2CppClassPointerStore.MakeGenericInstanceType(resultType).ToTypeDefOrRef();
                    var storeType = property.DeclaringType.DeclaringModule.DefaultImporter.ImportType(store);
                    body.Add(CilOpCodes.Ldsfld, new MemberReference(storeType, "NativeClassPtr",
                        new FieldSignature(imports.Module.CorLibTypeFactory.IntPtr)));
                    body.Add(CilOpCodes.Ldc_I4, 0);
                    body.Add(CilOpCodes.Conv_U);
                    body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_class_value_size.Value);
                    body.Add(CilOpCodes.Conv_U);
                    body.Add(CilOpCodes.Localloc);
                    body.Add(CilOpCodes.Stloc, storage);
                }

                body.Add(CilOpCodes.Ldsfld, field.PointerField);
                body.Add(needsBuffer ? CilOpCodes.Ldloc : CilOpCodes.Ldloca_S, storage);
                body.Add(CilOpCodes.Conv_U);
                body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_field_static_get_value.Value);
                if (isValueType)
                {
                    body.Add(CilOpCodes.Ldloc, storage);
                    body.Add(CilOpCodes.Ret);
                    property.GetMethod = getter;
                    return;
                }
            }
            else
            {
                InteropNativeValues.EmitObjectToPointer(body, field.DeclaringType.OriginalType.ToTypeSignature(),
                    field.DeclaringType.NewType.ToTypeSignature(), field.DeclaringType, 0,
                    false, false, false, false, out _);
                body.Add(CilOpCodes.Ldsfld, field.PointerField);
                body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_field_get_offset.Value);
                body.Add(CilOpCodes.Add);
                body.Add(CilOpCodes.Stloc, storage);
            }

            InteropNativeValues.EmitPointerToObject(body, original.Signature.FieldType, resultType, field.DeclaringType,
                storage, !original.IsStatic, false);
            // Instance storage holds a field address; static storage already contains the pointer value.
            if (!original.IsStatic && resultType is PointerTypeSignature or ByReferenceTypeSignature)
                body.Add(CilOpCodes.Ldind_I);
            body.Add(CilOpCodes.Ret);
            property.GetMethod = getter;
        }

        private static void GenerateSetter(InteropFieldContext field, PropertyDefinition property,
            InteropRuntimeReferences imports)
        {
            var original = field.OriginalField;
            var signature = original.IsStatic
                ? MethodSignature.CreateStatic(imports.Module.CorLibTypeFactory.Void, property.Signature.ReturnType)
                : MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void, property.Signature.ReturnType);
            var setter = new MethodDefinition("set_" + property.Name, AccessorAttributes(original), signature)
            {
                CilMethodBody = new CilMethodBody()
            };
            property.DeclaringType.Methods.Add(setter);
            var body = setter.CilMethodBody.Instructions;

            if (original.IsStatic)
            {
                body.Add(CilOpCodes.Ldsfld, field.PointerField);
                InteropNativeValues.EmitObjectToPointer(body, original.Signature.FieldType, property.Signature.ReturnType,
                    field.DeclaringType, 0, false, true, true, true, out _);
                body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_field_static_set_value.Value);
            }
            else
            {
                InteropNativeValues.EmitObjectToPointer(body, field.DeclaringType.OriginalType.ToTypeSignature(),
                    field.DeclaringType.NewType.ToTypeSignature(), field.DeclaringType, 0,
                    false, false, false, false, out _);
                body.Add(CilOpCodes.Dup);
                body.Add(CilOpCodes.Ldsfld, field.PointerField);
                body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_field_get_offset.Value);
                body.Add(CilOpCodes.Add);
                InteropNativeValues.EmitObjectStore(body, original.Signature.FieldType, property.Signature.ReturnType,
                    field.DeclaringType, 1);
            }
            body.Add(CilOpCodes.Ret);
            property.SetMethod = setter;
        }

        private static MethodAttributes AccessorAttributes(FieldDefinition field)
        {
            var attributes = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;
            return field.IsStatic ? attributes | MethodAttributes.Static : attributes;
        }
    }
}
