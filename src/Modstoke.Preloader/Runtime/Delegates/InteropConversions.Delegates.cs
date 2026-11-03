// SPDX-License-Identifier: GPL-3.0-or-later
// Delegate operators adapted from Il2CppInterop Pass60 (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace Modstoke.Preloader
{
    internal static partial class InteropConversions
    {
        private static void GenerateDelegateConversions(InteropRewriteContext context)
        {
            foreach (var assembly in context.Assemblies)
            foreach (var type in assembly.Types)
            {
                if (type.OriginalType?.BaseType?.FullName != "System.MulticastDelegate")
                    continue;

                var invoke = type.NewType.Methods.Single(method => method.Name == "Invoke");
                var module = type.NewType.DeclaringModule;
                var imports = assembly.Imports;
                var nativeDelegate = type.SelfSubstitutedRef.ToTypeSignature();
                if (InteropDelegateContracts.TryGetManagedDelegate(module, invoke.Signature, out var managedDelegate))
                {
                    var conversion = AddOperator(type.NewType, "op_Implicit", nativeDelegate, managedDelegate);
                    var body = conversion.CilMethodBody.Instructions;
                    body.Add(CilOpCodes.Ldarg_0);
                    body.Add(CilOpCodes.Call, InteropDelegateContracts.Conversion(module, nativeDelegate));
                    body.Add(CilOpCodes.Ret);
                }

                AddDelegateOperator(type, "op_Addition", imports.Il2CppSystemDelegateCombine.Value);
                AddDelegateOperator(type, "op_Subtraction", imports.Il2CppSystemDelegateRemove.Value);
            }
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
