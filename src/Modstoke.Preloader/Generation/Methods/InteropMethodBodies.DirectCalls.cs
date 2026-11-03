// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodBodies
    {
        internal static MethodDefinition GetDirectCallAdapter(InteropMethodContext method)
        {
            if (method.DirectCallAdapter != null) return method.DirectCallAdapter;
            var type = method.DeclaringType;
            var original = method.OriginalMethod;
            var target = method.NewMethod;
            if (original.IsStatic || original.IsConstructor || original.IsAbstract ||
                !original.Signature.HasThis || !InteropDelegateContracts.HasStandardConvention(original.Signature) ||
                original.GenericParameters.Count != 0 || target.GenericParameters.Count != 0 ||
                original.Signature.GenericParameterCount != 0 || target.Signature.GenericParameterCount != 0 ||
                type.OriginalType.GenericParameters.Count != 0 || type.NewType.GenericParameters.Count != 0 ||
                type.ComputedTypeSpecifics != InteropTypeContext.TypeSpecifics.ReferenceType ||
                method.NonGenericMethodInfoPointerField == null)
                return null;

            var name = "__InvokeDirect_" + original.MetadataToken.ToInt32().ToString("X8");
            while (type.NewType.Methods.Any(candidate => candidate.Name == name)) name += "_";
            var adapter = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.HideBySig,
                MethodSignature.CreateInstance(target.Signature.ReturnType))
            {
                CilMethodBody = new CilMethodBody()
            };
            foreach (var parameter in target.Parameters)
            {
                var copy = InteropMethodDeclarations.AddParameter(adapter, parameter.ParameterType, parameter.Name,
                    parameter.Definition?.Attributes ?? default);
                copy.Definition.Constant = parameter.Definition?.Constant;
            }
            type.NewType.Methods.Add(adapter);
            // IL call selects the declared implementation even when the native receiver overrides it.
            new InteropMethodBodies(method, type, type.AssemblyContext.Imports, adapter, directCall: true).Generate();
            return method.DirectCallAdapter = adapter;
        }
    }
}
