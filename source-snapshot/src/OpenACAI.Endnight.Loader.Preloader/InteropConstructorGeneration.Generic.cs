// SPDX-License-Identifier: GPL-3.0-or-later
// Generic constructors adapted from Il2CppInterop Pass20/30 (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static partial class InteropConstructorGeneration
    {
        internal static void GenerateGenericMethodStores(InteropRewriteContext context)
        {
            foreach (var assembly in context.Assemblies)
            foreach (var type in assembly.Types)
            foreach (var method in type.Methods)
            {
                var store = method.GenericInstantiationsStore;
                if (store == null)
                    continue;

                var imports = assembly.Imports;
                var body = store.GetOrCreateStaticConstructor().CilMethodBody.Instructions;
                body.Clear();
                var nativeType = ImportNativeCorlibType(assembly, "System.Type");
                var nativeMethod = ImportNativeCorlibType(assembly, "System.Reflection.MethodInfo");
                body.Add(CilOpCodes.Ldsfld, method.NonGenericMethodInfoPointerField);
                body.Add(CilOpCodes.Ldsfld, type.ClassPointerFieldRef);
                body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_method_get_object.Value);
                body.Add(CilOpCodes.Newobj, new MemberReference(nativeMethod, ".ctor",
                    MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void,
                        imports.Module.CorLibTypeFactory.IntPtr)));

                var arguments = EmitGenericTypeArray(body, imports, nativeType,
                    store.GenericParameters, method.OriginalMethod.GenericParameters.Count);
                body.Add(CilOpCodes.Call, new MemberReference(nativeMethod, "MakeGenericMethod",
                    MethodSignature.CreateInstance(nativeMethod.ToTypeSignature(), arguments)));
                body.Add(CilOpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtrNotNull.Value);
                body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_method_get_from_reflection.Value);
                body.Add(CilOpCodes.Stsfld, new MemberReference(method.GenericInstantiationsStoreSelfSubstRef, "Pointer",
                    new FieldSignature(imports.Module.CorLibTypeFactory.IntPtr)));
                body.Add(CilOpCodes.Ret);
            }
        }

        private static ITypeDefOrRef ImportNativeCorlibType(InteropAssemblyContext assembly, string name)
        {
            var type = assembly.GlobalContext.GetAssemblyByName("mscorlib").GetTypeByName(name).NewType;
            return assembly.NewAssembly.ManifestModule.DefaultImporter.ImportType(type);
        }

        private static GenericInstanceTypeSignature EmitGenericTypeArray(CilInstructionCollection body,
            InteropRuntimeReferences imports, ITypeDefOrRef nativeType, IList<GenericParameter> parameters, int count)
        {
            body.Add(CilOpCodes.Ldc_I4, count);
            body.Add(CilOpCodes.Newarr, nativeType);
            for (var index = 0; index < count; index++)
            {
                body.Add(CilOpCodes.Dup);
                body.Add(CilOpCodes.Ldc_I4, index);
                var parameter = new GenericParameterSignature(GenericParameterType.Type, parameters[index].Number);
                var store = imports.Il2CppClassPointerStore.MakeGenericInstanceType(parameter);
                body.Add(CilOpCodes.Ldsfld, new MemberReference(store.ToTypeDefOrRef(), "NativeClassPtr",
                    new FieldSignature(imports.Module.CorLibTypeFactory.IntPtr)));
                body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_class_get_type.Value);
                body.Add(CilOpCodes.Call, new MemberReference(nativeType, "internal_from_handle",
                    MethodSignature.CreateStatic(nativeType.ToTypeSignature(), imports.Module.CorLibTypeFactory.IntPtr)));
                body.Add(CilOpCodes.Stelem_Ref);
            }

            var array = imports.Il2CppReferenceArray.MakeGenericInstanceType(nativeType.ToTypeSignature());
            // This !0 belongs to the array constructor, not to the enclosing type or method store.
            var elementArray = new GenericParameterSignature(GenericParameterType.Type, 0).MakeSzArrayType();
            body.Add(CilOpCodes.Newobj, new MemberReference(array.ToTypeDefOrRef(), ".ctor",
                MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void, elementArray)));
            return array;
        }
    }
}
