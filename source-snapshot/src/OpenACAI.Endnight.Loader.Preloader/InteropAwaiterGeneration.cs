// SPDX-License-Identifier: GPL-3.0-or-later
// Awaiter generation adapted from Il2CppInterop Pass61 (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System.Linq;
using System.Runtime.CompilerServices;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using BepInEx.Logging;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class InteropAwaiterGeneration
    {
        internal static void Generate(InteropRewriteContext context)
        {
            var conversion = context.CorLib.GetTypeByName("System.Action").NewType.Methods
                .Single(method => method.Name == "op_Implicit");
            foreach (var assembly in context.Assemblies)
            foreach (var type in assembly.Types)
            {
                var original = type.OriginalType;
                if (original.IsInterface || original.Interfaces.Count == 0)
                    continue;
                if (original.Interfaces.SingleOrDefault(implementation =>
                    implementation.Interface?.Namespace == "System.Runtime.CompilerServices" &&
                    implementation.Interface.Name == "INotifyCompletion") == null)
                    continue;

                var module = assembly.NewAssembly.ManifestModule;
                var candidates = type.Methods.Where(method => method.OriginalMethod.Name == "OnCompleted")
                    .Select(method => method.NewMethod).ToArray();
                var target = candidates.FirstOrDefault(method => !method.IsStatic && method.Parameters.Count == 1 &&
                    method.Signature != null &&
                    SignatureComparer.Default.Equals(method.Signature.ReturnType, module.CorLibTypeFactory.Void) &&
                    SignatureComparer.Default.Equals(method.Signature.ParameterTypes[0], conversion.Signature?.ReturnType));
                if (target == null)
                {
                    InteropGenerationDiagnostics.Log(LogLevel.Info,
                        $"Type {original.FullName} was found to implement INotifyCompletion, but no suitable method was found. " +
                        $"{candidates.Length} method(s) were found with the required name.");
                    continue;
                }

                var continuationType = module.DefaultImporter.ImportTypeSignature(conversion.Parameters[0].ParameterType);
                var wrapper = new MethodDefinition("OnCompleted",
                    MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual |
                    MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                    MethodSignature.CreateInstance(module.CorLibTypeFactory.Void, continuationType))
                {
                    CilMethodBody = new CilMethodBody()
                };
                wrapper.Parameters[0].GetOrCreateDefinition().Name = "continuation";
                type.NewType.Methods.Add(wrapper);
                type.NewType.Interfaces.Add(new InterfaceImplementation(
                    module.DefaultImporter.ImportType(typeof(INotifyCompletion))));

                IMethodDefOrRef callTarget = target;
                if (type.NewType.GenericParameters.Count != 0)
                {
                    var parameters = Enumerable.Range(0, type.NewType.GenericParameters.Count)
                        .Select(index => (TypeSignature)new GenericParameterSignature(GenericParameterType.Type, index))
                        .ToArray();
                    var declaringType = type.NewType.MakeGenericInstanceType(parameters).ToTypeDefOrRef();
                    callTarget = new MemberReference(declaringType, target.Name, target.Signature);
                }

                var body = wrapper.CilMethodBody.Instructions;
                body.Add(CilOpCodes.Ldarg_0);
                body.Add(CilOpCodes.Ldarg_1);
                body.Add(CilOpCodes.Call, module.DefaultImporter.ImportMethod(conversion));
                body.Add(CilOpCodes.Call, callTarget);
                body.Add(CilOpCodes.Ret);
            }
        }
    }
}
