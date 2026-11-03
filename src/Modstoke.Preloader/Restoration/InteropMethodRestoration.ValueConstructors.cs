// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodRestoration
    {
        private readonly Dictionary<MethodDefinition, MethodDefinition> valueConstructorBodies =
            new(ReferenceEqualityComparer.Instance);

        private bool TryRestoreValueConstructor(MethodDefinition original, ref MethodDefinition constructor,
            InteropTypeContext type, InteropRuntimeReferences imports, InteropRestorationEntry entry)
        {
            var signature = constructor.Signature;
            var parameters = signature.ParameterTypes;
            // IntPtr construction wraps existing native storage throughout the runtime.
            if (parameters.Count == 1 && parameters[0] is CorLibTypeSignature { ElementType: ElementType.I })
            {
                entry.Set("signature-rejected", "native-pointer-constructor-reserved",
                    "The IntPtr constructor is reserved for wrapping an existing native object.");
                return false;
            }
            var existing = type.NewType.Methods.FirstOrDefault(method => method.Name == ".ctor" &&
                SignatureComparer.Default.Equals(method.Signature, signature));
            if (existing != null)
            {
                if (parameters.Count != 0)
                {
                    entry.Set("signature-rejected", "native-value-constructor-collision",
                        "A generated constructor already uses the restored signature.");
                    return false;
                }
                // The generated parameterless constructor only allocates default backing storage.
                constructor = existing;
            }
            else type.NewType.Methods.Add(constructor);

            var name = "__ModstokeInitializeValue_" + original.MetadataToken.ToInt32().ToString("X8");
            while (type.NewType.Methods.Any(method => method.Name == name)) name += "_";
            var initializer = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.HideBySig,
                MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void))
            {
                CilMethodBody = new CilMethodBody()
            };
            foreach (var parameter in constructor.Parameters)
                InteropMethodDeclarations.AddParameter(initializer, parameter.ParameterType, parameter.Name,
                    parameter.Definition?.Attributes ?? default);
            type.NewType.Methods.Add(initializer);
            type.AddRestoredValueConstructor(original, initializer);
            valueConstructorBodies.Add(constructor, initializer);
            pendingBodies.Add((original, initializer, type, imports, entry));
            entry.Set("body-pending");

            // newobj allocates once; in-place calls use the same initializer on the existing box.
            constructor.CilMethodBody = new CilMethodBody();
            var body = constructor.CilMethodBody.Instructions;
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Ldsfld, type.ClassPointerFieldRef);
            body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_new.Value);
            body.Add(CilOpCodes.Call, new MemberReference(type.SelfSubstitutedRef, ".ctor",
                MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void, imports.Module.CorLibTypeFactory.IntPtr)));
            body.Add(CilOpCodes.Ldarg_0);
            foreach (var parameter in constructor.Parameters) body.Add(CilOpCodes.Ldarg, parameter);
            body.Add(CilOpCodes.Call, new MemberReference(type.SelfSubstitutedRef, initializer.Name, initializer.Signature));
            body.Add(CilOpCodes.Ret);
            return true;
        }
    }
}
