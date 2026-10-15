// SPDX-License-Identifier: GPL-3.0-or-later
// Delegate operators adapted from Il2CppInterop Pass60 (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static partial class InteropConversions
    {
        private static void GenerateDelegateConversions(InteropRewriteContext context)
        {
            foreach (var assembly in context.Assemblies)
            foreach (var type in assembly.Types)
            {
                if (type.OriginalType.BaseType?.FullName != "System.MulticastDelegate")
                    continue;

                var invoke = type.NewType.Methods.Single(method => method.Name == "Invoke");
                if (invoke.Parameters.Count > 8)
                    continue;

                // Action/Func cannot represent pointer or by-reference generic arguments, including returns.
                if (IsPointerOrByReference(invoke.Signature.ReturnType) ||
                    invoke.Parameters.Any(parameter => IsPointerOrByReference(parameter.ParameterType)))
                    continue;

                var module = type.NewType.DeclaringModule;
                var imports = assembly.Imports;
                var nativeDelegate = type.SelfSubstitutedRef.ToTypeSignature();
                var managedDelegate = GetManagedDelegateType(module, invoke);
                var conversion = AddOperator(type.NewType, "op_Implicit", nativeDelegate, managedDelegate);
                var convert = new MemberReference(imports.DelegateSupport.ToTypeDefOrRef(), "ConvertDelegate",
                    MethodSignature.CreateStatic(new GenericParameterSignature(GenericParameterType.Method, 0), 1,
                        module.DefaultImporter.ImportTypeSignature(typeof(Delegate))))
                    .MakeGenericInstanceMethod(nativeDelegate);
                var body = conversion.CilMethodBody.Instructions;
                body.Add(CilOpCodes.Ldarg_0);
                body.Add(CilOpCodes.Call, module.DefaultImporter.ImportMethod(convert));
                body.Add(CilOpCodes.Ret);

                AddDelegateOperator(type, "op_Addition", imports.Il2CppSystemDelegateCombine.Value);
                AddDelegateOperator(type, "op_Subtraction", imports.Il2CppSystemDelegateRemove.Value);
            }
        }

        private static bool IsPointerOrByReference(TypeSignature type) =>
            type is PointerTypeSignature or ByReferenceTypeSignature;

        private static TypeSignature GetManagedDelegateType(ModuleDefinition module, MethodDefinition invoke)
        {
            var hasReturn = invoke.Signature.ReturnType.FullName != "System.Void";
            var arity = invoke.Parameters.Count + (hasReturn ? 1 : 0);
            var name = hasReturn ? "System.Func" : "System.Action";
            if (arity != 0)
                name += "`" + arity;

            var definition = module.DefaultImporter.ImportTypeSignature(typeof(string).Assembly.GetType(name));
            if (arity == 0)
                return definition;

            var instance = new GenericInstanceTypeSignature(definition.ToTypeDefOrRef(), false);
            foreach (var parameter in invoke.Parameters)
                instance.TypeArguments.Add(parameter.ParameterType);
            if (hasReturn)
                instance.TypeArguments.Add(invoke.Signature.ReturnType);
            return module.DefaultImporter.ImportTypeSignature(instance);
        }

        private static void AddDelegateOperator(InteropTypeContext type, string name, IMethodDefOrRef operation)
        {
            var nativeDelegate = type.SelfSubstitutedRef.ToTypeSignature();
            var method = AddOperator(type.NewType, name, nativeDelegate, nativeDelegate, nativeDelegate);
            var imports = type.AssemblyContext.Imports;
            var body = method.CilMethodBody.Instructions;
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Ldarg_1);
            body.Add(CilOpCodes.Call, operation);
            body.Add(CilOpCodes.Dup);
            var done = new CilInstructionLabel();
            body.Add(CilOpCodes.Brfalse_S, done);
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                imports.Il2CppObjectBase_Cast.Value.MakeGenericInstanceMethod(nativeDelegate)));
            done.Instruction = body.Add(CilOpCodes.Ret);
        }
    }
}
