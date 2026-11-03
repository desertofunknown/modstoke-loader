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
        internal static MethodDefinition GetValueConstructorInitializer(InteropMethodContext method)
        {
            if (method.ValueConstructorInitializer != null) return method.ValueConstructorInitializer;
            return method.ValueConstructorInitializer = CreateNativeCallAdapter(method);
        }

        internal static MethodDefinition GetScopedNativeCallAdapter(InteropMethodContext method,
            bool nativeReceiver, bool[] nativeArguments)
        {
            var key = (nativeReceiver ? "N" : "M") + string.Concat(nativeArguments.Select(value => value ? "N" : "M"));
            method.ScopedNativeCallAdapters ??= new();
            if (method.ScopedNativeCallAdapters.TryGetValue(key, out var existing)) return existing;
            var initializer = CreateNativeCallAdapter(method, nativeArguments, nativeReceiver, "_Locations_" + key);
            method.ScopedNativeCallAdapters.Add(key, initializer);
            return initializer;
        }

        private static MethodDefinition CreateNativeCallAdapter(InteropMethodContext method,
            bool[] nativeArguments = null, bool nativeReceiver = false, string suffix = "")
        {
            var type = method.DeclaringType;
            var imports = type.AssemblyContext.Imports;
            var prefix = method.OriginalMethod.IsConstructor ? "__ModstokeInvokeValueConstructor_" :
                method.OriginalMethod.IsStatic ? "__ModstokeInvokeStaticMethod_" :
                InteropTypeReferences.IsValueType(method.OriginalMethod.DeclaringType)
                    ? "__ModstokeInvokeValueMethod_" : "__ModstokeInvokeObjectMethod_";
            var name = prefix + method.OriginalMethod.MetadataToken.ToInt32().ToString("X8") + suffix;
            while (type.NewType.Methods.Any(candidate => candidate.Name == name)) name += "_";
            var attributes = MethodAttributes.Public | MethodAttributes.HideBySig;
            if (nativeArguments != null) attributes |= MethodAttributes.Static;
            var initializer = new MethodDefinition(name, attributes,
                nativeArguments == null ? MethodSignature.CreateInstance(method.NewMethod.Signature.ReturnType,
                    method.NewMethod.GenericParameters.Count) :
                    MethodSignature.CreateStatic(method.NewMethod.Signature.ReturnType, method.NewMethod.GenericParameters.Count))
            {
                CilMethodBody = new CilMethodBody()
            };
            foreach (var parameter in method.NewMethod.GenericParameters)
            {
                var copy = new GenericParameter(parameter.Name, parameter.Attributes);
                foreach (var constraint in parameter.Constraints)
                    copy.Constraints.Add(new GenericParameterConstraint(constraint.Constraint));
                initializer.GenericParameters.Add(copy);
            }
            var location = imports.Module.DefaultImporter.ImportTypeSignature(typeof(InteropReferenceLocation));
            if (nativeArguments != null && !method.OriginalMethod.IsStatic)
                InteropMethodDeclarations.AddParameter(initializer,
                    nativeReceiver ? location : type.SelfSubstitutedRef.ToTypeSignature(), "receiver", default);
            for (var index = 0; index < method.NewMethod.Parameters.Count; index++)
            {
                var parameter = method.NewMethod.Parameters[index];
                if (nativeArguments != null && nativeArguments[index])
                {
                    InteropMethodDeclarations.AddParameter(initializer, location, parameter.Name, default);
                    continue;
                }
                var copy = InteropMethodDeclarations.AddParameter(initializer, parameter.ParameterType, parameter.Name,
                    parameter.Definition?.Attributes ?? default);
                copy.Definition.Constant = parameter.Definition?.Constant;
            }
            type.NewType.Methods.Add(initializer);
            if (method.ValueCallValidationField == null)
            {
                // Closed methods cannot share a validation cache across different native type arguments.
                var store = method.GenericInstantiationsStore ?? type.NewType;
                var owner = method.GenericInstantiationsStoreSelfSubstMethodRef ?? type.SelfSubstitutedRef;
                var fieldName = name + "_ValidatedMethod";
                while (store.Fields.Any(candidate => candidate.Name == fieldName)) fieldName += "_";
                var visibility = method.GenericInstantiationsStore == null ? FieldAttributes.Private : FieldAttributes.Assembly;
                var validated = new FieldDefinition(fieldName, visibility | FieldAttributes.Static,
                    imports.Module.CorLibTypeFactory.IntPtr);
                store.Fields.Add(validated);
                method.ValueCallValidationField = new MemberReference(owner, validated.Name, validated.Signature);
            }
            // Reuse native invocation/refout cleanup, but never allocate or replace the receiver.
            new InteropMethodBodies(method, type, imports, initializer,
                method.ValueCallValidationField, nativeArguments, nativeReceiver).Generate();
            return initializer;
        }
    }
}
