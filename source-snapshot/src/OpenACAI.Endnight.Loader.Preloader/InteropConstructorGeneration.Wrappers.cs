// SPDX-License-Identifier: GPL-3.0-or-later
// Wrapper constructors adapted from Il2CppInterop Pass23/25 (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static partial class InteropConstructorGeneration
    {
        private const MethodAttributes ConstructorAttributes = MethodAttributes.Public | MethodAttributes.HideBySig |
            MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName;

        internal static void GeneratePointerConstructors(InteropRewriteContext context)
        {
            foreach (var assembly in context.Assemblies)
            foreach (var type in assembly.Types)
            {
                if (type.ComputedTypeSpecifics == InteropTypeContext.TypeSpecifics.BlittableStruct || type.OriginalType.IsEnum)
                    continue;

                var corlib = assembly.Imports.Module.CorLibTypeFactory;
                var constructor = new MethodDefinition(".ctor", ConstructorAttributes, MethodSignature.CreateInstance(corlib.Void));
                InteropMethodDeclarations.AddParameter(constructor, corlib.IntPtr, "pointer", default);
                constructor.CilMethodBody = new CilMethodBody();
                type.NewType.Methods.Add(constructor);
                var body = constructor.CilMethodBody.Instructions;
                body.Add(CilOpCodes.Ldarg_0);
                body.Add(CilOpCodes.Ldarg_1);
                body.Add(CilOpCodes.Call, new MemberReference(type.NewType.BaseType, ".ctor",
                    MethodSignature.CreateInstance(corlib.Void, corlib.IntPtr)));
                body.Add(CilOpCodes.Ret);
            }
        }

        internal static void GenerateNonBlittableConstructors(InteropRewriteContext context)
        {
            foreach (var assembly in context.Assemblies)
            foreach (var type in assembly.Types)
            {
                if (type.ComputedTypeSpecifics != InteropTypeContext.TypeSpecifics.NonBlittableStruct)
                    continue;

                var imports = assembly.Imports;
                var corlib = imports.Module.CorLibTypeFactory;
                var constructor = new MethodDefinition(".ctor", ConstructorAttributes, MethodSignature.CreateInstance(corlib.Void))
                {
                    CilMethodBody = new CilMethodBody()
                };
                type.NewType.Methods.Add(constructor);
                var body = constructor.CilMethodBody.Instructions;
                body.Add(CilOpCodes.Ldarg_0);
                body.Add(CilOpCodes.Ldsfld, type.ClassPointerFieldRef);
                body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_new.Value);
                body.Add(CilOpCodes.Call, new MemberReference(type.NewType.BaseType, ".ctor",
                    MethodSignature.CreateInstance(corlib.Void, corlib.IntPtr)));
                body.Add(CilOpCodes.Ret);
            }
        }
    }
}
