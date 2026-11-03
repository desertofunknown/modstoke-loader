// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed class InteropInternalCallContext
    {
        internal readonly MethodDefinition Original;
        internal readonly MethodDefinition Target;
        private readonly FieldDefinition delegateField;
        private readonly TypeDefinition delegateType;
        private readonly InteropTypeContext type;
        private readonly HashSet<int> inPlaceValueReferences;
        private Dictionary<string, MethodDefinition> adapters;

        internal InteropInternalCallContext(MethodDefinition original, MethodDefinition target,
            FieldDefinition delegateField, TypeDefinition delegateType, InteropTypeContext type,
            HashSet<int> inPlaceValueReferences)
        {
            Original = original;
            Target = target;
            this.delegateField = delegateField;
            this.delegateType = delegateType;
            this.type = type;
            this.inPlaceValueReferences = inPlaceValueReferences;
        }

        internal bool PreservesNativeValueReference(int parameterIndex) =>
            inPlaceValueReferences?.Contains(parameterIndex) == true;

        internal MethodDefinition GetScopedAdapter(bool[] nativeArguments)
        {
            var key = string.Concat(nativeArguments.Select(value => value ? "N" : "M"));
            adapters ??= new();
            if (adapters.TryGetValue(key, out var existing)) return existing;
            var name = "__ModstokeInvokeInternalCall_" + Original.MetadataToken.ToInt32().ToString("X8") + "_" + key;
            while (type.NewType.Methods.Any(method => method.Name == name)) name += "_";
            var adapter = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                MethodSignature.CreateStatic(Target.Signature.ReturnType)) { CilMethodBody = new CilMethodBody() };
            var imports = type.AssemblyContext.Imports;
            var location = imports.Module.DefaultImporter.ImportTypeSignature(typeof(InteropReferenceLocation));
            if (!Original.IsStatic)
                InteropMethodDeclarations.AddParameter(adapter, type.NewType.ToTypeSignature(), "receiver", default);
            for (var index = 0; index < Target.Parameters.Count; index++)
            {
                var parameter = Target.Parameters[index];
                var copy = InteropMethodDeclarations.AddParameter(adapter,
                    nativeArguments[index] ? location : parameter.ParameterType, parameter.Name,
                    nativeArguments[index] ? default : parameter.Definition?.Attributes ?? default);
                if (!nativeArguments[index]) copy.Definition.Constant = parameter.Definition?.Constant;
            }
            type.NewType.Methods.Add(adapter);
            // The original bridge owns the exact ICall signature and initialization. Reuse it
            // with typed locations while keeping pins and exceptional ref/out cleanup shared.
            InteropNativeCallStubs.FillBody(Original, adapter, delegateField, delegateType, type, imports,
                nativeArguments, Target);
            adapters.Add(key, adapter);
            return adapter;
        }
    }
}
