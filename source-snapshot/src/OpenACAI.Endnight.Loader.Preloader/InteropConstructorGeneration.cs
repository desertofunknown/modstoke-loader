// SPDX-License-Identifier: GPL-3.0-or-later
// Constructor generation adapted from Il2CppInterop Pass20 (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using BepInEx.Logging;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static partial class InteropConstructorGeneration
    {
        internal static void GenerateStaticConstructors(InteropRewriteContext context)
        {
            var tokenlessMethods = 0;
            foreach (var assembly in context.Assemblies)
                foreach (var type in assembly.Types)
                    tokenlessMethods += GenerateStaticConstructor(assembly, type);
            InteropGenerationDiagnostics.Log(LogLevel.Debug, $"Tokenless method count: {tokenlessMethods}");
        }

        private static int GenerateStaticConstructor(InteropAssemblyContext assembly, InteropTypeContext type)
        {
            var original = type.OriginalType;
            var target = type.NewType;
            if (target.IsEnum)
                return 0;

            var imports = assembly.Imports;
            var body = target.GetOrCreateStaticConstructor().CilMethodBody.Instructions;
            body.Clear();
            if (target.IsNested)
            {
                body.Add(CilOpCodes.Ldsfld, assembly.GlobalContext.GetNewTypeForOriginal(original.DeclaringType).ClassPointerFieldRef);
                body.Add(CilOpCodes.Ldstr, original.Name ?? "");
                body.Add(CilOpCodes.Call, imports.IL2CPP_GetIl2CppNestedType.Value);
            }
            else
            {
                body.Add(CilOpCodes.Ldstr, original.DeclaringModule?.Name ?? "");
                body.Add(CilOpCodes.Ldstr, original.Namespace ?? "");
                body.Add(CilOpCodes.Ldstr, original.Name ?? "");
                body.Add(CilOpCodes.Call, imports.IL2CPP_GetIl2CppClass.Value);
            }

            if (original.GenericParameters.Count != 0)
                EmitConstructedClass(body, assembly, original);
            body.Add(CilOpCodes.Stsfld, type.ClassPointerFieldRef);
            if (original.IsBeforeFieldInit)
            {
                body.Add(CilOpCodes.Ldsfld, type.ClassPointerFieldRef);
                body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_runtime_class_init.Value);
            }
            if (original.IsEnum)
            {
                body.Add(CilOpCodes.Ret);
                return 0;
            }

            foreach (var field in type.Fields)
            {
                body.Add(CilOpCodes.Ldsfld, type.ClassPointerFieldRef);
                body.Add(CilOpCodes.Ldstr, field.OriginalField.Name);
                body.Add(CilOpCodes.Call, imports.IL2CPP_GetIl2CppField.Value);
                body.Add(CilOpCodes.Stsfld, field.PointerField);
            }

            var tokenlessMethods = 0;
            foreach (var method in type.Methods)
            {
                body.Add(CilOpCodes.Ldsfld, type.ClassPointerFieldRef);
                var token = method.OriginalMethod.ExtractToken();
                if (token == 0)
                {
                    tokenlessMethods++;
                    body.Add(method.OriginalMethod.GenericParameters.Count > 0 ? CilOpCodes.Ldc_I4_1 : CilOpCodes.Ldc_I4_0);
                    body.Add(CilOpCodes.Ldstr, method.OriginalMethod.Name);
                    EmitTypeName(body, imports, method.OriginalMethod,
                        method.OriginalMethod.Signature.ReturnType, method.NewMethod.Signature.ReturnType);
                    body.Add(CilOpCodes.Ldc_I4, method.OriginalMethod.Parameters.Count);
                    body.Add(CilOpCodes.Newarr, imports.Module.CorLibTypeFactory.String.ToTypeDefOrRef());
                    for (var index = 0; index < method.OriginalMethod.Parameters.Count; index++)
                    {
                        body.Add(CilOpCodes.Dup);
                        body.Add(CilOpCodes.Ldc_I4, index);
                        EmitTypeName(body, imports, method.OriginalMethod,
                            method.OriginalMethod.Parameters[index].ParameterType, method.NewMethod.Parameters[index].ParameterType);
                        body.Add(CilOpCodes.Stelem_Ref);
                    }
                    body.Add(CilOpCodes.Call, imports.IL2CPP_GetIl2CppMethod.Value);
                }
                else
                {
                    body.Add(CilOpCodes.Ldc_I4, (int)token);
                    body.Add(CilOpCodes.Call, imports.IL2CPP_GetIl2CppMethodByToken.Value);
                }
                body.Add(CilOpCodes.Stsfld, method.NonGenericMethodInfoPointerField);
            }
            body.Add(CilOpCodes.Ret);
            return tokenlessMethods;
        }

        private static void EmitConstructedClass(CilInstructionCollection body, InteropAssemblyContext assembly,
            TypeDefinition original)
        {
            var imports = assembly.Imports;
            var nativeType = ImportNativeCorlibType(assembly, "System.Type");
            var nativeHandle = ImportNativeCorlibType(assembly, "System.RuntimeTypeHandle");
            body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_class_get_type.Value);
            body.Add(CilOpCodes.Call, new MemberReference(nativeType, "internal_from_handle",
                MethodSignature.CreateStatic(nativeType.ToTypeSignature(), imports.Module.CorLibTypeFactory.IntPtr)));
            var arguments = EmitGenericTypeArray(body, imports, nativeType,
                original.GenericParameters, original.GenericParameters.Count);
            body.Add(CilOpCodes.Call, new MemberReference(nativeType, "MakeGenericType",
                MethodSignature.CreateInstance(nativeType.ToTypeSignature(), arguments)));
            body.Add(CilOpCodes.Call, new MemberReference(nativeType, "get_TypeHandle",
                MethodSignature.CreateInstance(nativeHandle.ToTypeSignature())));
            body.Add(CilOpCodes.Ldfld, new MemberReference(nativeHandle, "value",
                new FieldSignature(imports.Module.CorLibTypeFactory.IntPtr)));
            body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_class_from_type.Value);
        }

        private static void EmitTypeName(CilInstructionCollection body, InteropRuntimeReferences imports,
            MethodDefinition method, TypeSignature original, TypeSignature rewritten)
        {
            if (method.GenericParameters.Count != 0 || original.FullName == "System.Void")
            {
                body.Add(CilOpCodes.Ldstr, original.FullName);
                return;
            }
            var reference = rewritten as ByReferenceTypeSignature;
            body.Add(reference != null ? CilOpCodes.Ldc_I4_1 : CilOpCodes.Ldc_I4_0);
            var render = imports.IL2CPP_RenderTypeName.Value.MakeGenericInstanceMethod(reference != null ? reference.BaseType : rewritten);
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(render));
        }
    }
}
