// SPDX-License-Identifier: GPL-3.0-or-later
// Field generation adapted from Il2CppInterop Pass21/40 and ILGeneratorEx (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static partial class InteropFieldGeneration
    {
        internal static void GenerateValueTypeFields(InteropRewriteContext context)
        {
            foreach (var assembly in context.Assemblies)
            {
                var imports = assembly.Imports;
                var objectType = assembly.GlobalContext.GetAssemblyByName("mscorlib").GetTypeByName("System.Object");
                var objectReference = assembly.NewAssembly.ManifestModule.DefaultImporter
                    .ImportType(objectType.NewType).ToTypeSignature();

                foreach (var type in assembly.Types)
                {
                    if (type.ComputedTypeSpecifics != InteropTypeContext.TypeSpecifics.BlittableStruct ||
                        type.OriginalType.IsEnum)
                        continue;

                    try
                    {
                        type.NewType.Attributes = (type.NewType.Attributes & ~TypeAttributes.LayoutMask) |
                            TypeAttributes.ExplicitLayout;
                        GenerateBoxMethod(type.NewType, type.ClassPointerFieldRef, objectReference, imports);

                        foreach (var field in type.Fields)
                        {
                            var original = field.OriginalField;
                            if (original.IsStatic)
                                continue;
                            var fieldType = InteropTypeReferences.IsValueType(original.Signature.FieldType)
                                ? InteropTypeReferences.Rewrite(assembly, original.Signature.FieldType)
                                : imports.Module.CorLibTypeFactory.IntPtr;
                            var copy = new FieldDefinition(field.UnmangledName,
                                (original.Attributes & ~FieldAttributes.FieldAccessMask) | FieldAttributes.Public, fieldType)
                            {
                                FieldOffset = original.ExtractFieldOffset()
                            };
                            if (fieldType.FullName == "System.Boolean")
                                copy.MarshalDescriptor = new SimpleMarshalDescriptor(NativeType.U1);
                            type.NewType.Fields.Add(copy);
                        }
                    }
                    catch (Exception error)
                    {
                        throw new Exception(
                            $"Failed to generate value type fields for type {type.OriginalType.FullName} in assembly {type.AssemblyContext.OriginalAssembly.Name}",
                            error);
                    }
                }
            }
        }

        internal static void GenerateAccessors(InteropRewriteContext context)
        {
            foreach (var assembly in context.Assemblies)
            {
                foreach (var type in assembly.Types)
                {
                    foreach (var field in type.Fields)
                    {
                        var original = field.OriginalField;
                        if (type.ComputedTypeSpecifics == InteropTypeContext.TypeSpecifics.BlittableStruct && !original.IsStatic)
                            continue;

                        var fieldType = InteropTypeReferences.Rewrite(assembly, original.Signature.FieldType);
                        var signature = original.IsStatic
                            ? PropertySignature.CreateStatic(fieldType)
                            : PropertySignature.CreateInstance(fieldType);
                        var property = new PropertyDefinition(field.UnmangledName, PropertyAttributes.None, signature);
                        type.NewType.Properties.Add(property);
                        GenerateGetter(field, property, assembly.Imports);
                        GenerateSetter(field, property, assembly.Imports);
                    }
                }
            }
        }

        private static void GenerateBoxMethod(TypeDefinition type, IFieldDescriptor classPointer,
            TypeSignature objectType, InteropRuntimeReferences imports)
        {
            var module = type.DeclaringModule;
            var method = new MethodDefinition("BoxIl2CppObject", MethodAttributes.Public | MethodAttributes.HideBySig,
                MethodSignature.CreateInstance(module.DefaultImporter.ImportTypeSignature(objectType)))
            {
                CilMethodBody = new CilMethodBody()
            };
            type.Methods.Add(method);
            var body = method.CilMethodBody.Instructions;
            body.Add(CilOpCodes.Ldsfld, classPointer);
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Call, module.DefaultImporter.ImportMethod(imports.IL2CPP_il2cpp_value_box.Value));
            body.Add(CilOpCodes.Newobj, new MemberReference(objectType.ToTypeDefOrRef(), ".ctor",
                MethodSignature.CreateInstance(module.CorLibTypeFactory.Void, module.CorLibTypeFactory.IntPtr)));
            body.Add(CilOpCodes.Ret);
        }
    }
}
