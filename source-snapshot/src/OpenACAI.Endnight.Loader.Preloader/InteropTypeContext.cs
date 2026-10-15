// SPDX-License-Identifier: GPL-3.0-or-later
// Type context adapted from Il2CppInterop TypeRewriteContext (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace OpenACAI.Endnight.Loader.Preloader
{
    [DebuggerDisplay("{GetDebuggerDisplay(),nq}")]
    internal sealed class InteropTypeContext
    {
        public enum TypeSpecifics
        {
            NotComputed,
            Computing,
            ReferenceType,
            BlittableStruct,
            NonBlittableStruct
        }

        private readonly Dictionary<FieldDefinition, InteropFieldContext> fieldContexts = new();
        private readonly Dictionary<MethodDefinition, InteropMethodContext> methodContexts = new();
        private readonly Dictionary<string, InteropMethodContext> methodContextsByName = new();

        public readonly InteropAssemblyContext AssemblyContext;
        public readonly TypeDefinition NewType;
        public readonly bool OriginalNameWasObfuscated;
        // Restored types have no original metadata and retain the default context state.
        public readonly TypeDefinition OriginalType;
        public TypeSpecifics ComputedTypeSpecifics;

        public InteropTypeContext(InteropAssemblyContext assemblyContext, TypeDefinition originalType,
            TypeDefinition newType)
        {
            AssemblyContext = assemblyContext ?? throw new ArgumentNullException(nameof(assemblyContext));
            OriginalType = originalType;
            NewType = newType ?? throw new ArgumentNullException(nameof(newType));

            if (OriginalType == null) return;

            OriginalNameWasObfuscated = OriginalType.Name != NewType.Name;
            if (OriginalNameWasObfuscated)
                NewType.CustomAttributes.Add(new CustomAttribute(
                    (ICustomAttributeType)assemblyContext.Imports.ObfuscatedNameAttributector.Value,
                    new CustomAttributeSignature(new CustomAttributeArgument(
                        assemblyContext.Imports.Module.CorLibTypeFactory.String, OriginalType.FullName))));

            if (!InteropTypeReferences.IsValueType(OriginalType))
                ComputedTypeSpecifics = TypeSpecifics.ReferenceType;
            else if (OriginalType.IsEnum)
                ComputedTypeSpecifics = TypeSpecifics.BlittableStruct;
            else if (OriginalType.GenericParameters.Count > 0)
                ComputedTypeSpecifics = TypeSpecifics.NonBlittableStruct;
        }

        public IFieldDescriptor ClassPointerFieldRef { get; private set; }
        public ITypeDefOrRef SelfSubstitutedRef { get; private set; }
        public IEnumerable<InteropFieldContext> Fields => fieldContexts.Values;
        public IEnumerable<InteropMethodContext> Methods => methodContexts.Values;

        public void AddMembers()
        {
            var imports = AssemblyContext.Imports;
            var module = NewType.DeclaringModule;
            GenericInstanceTypeSignature pointerStore;
            if (NewType.GenericParameters.Count > 0)
            {
                var self = new GenericInstanceTypeSignature(NewType, InteropTypeReferences.IsValueType(NewType));
                foreach (var parameter in NewType.GenericParameters)
                    self.TypeArguments.Add(new GenericParameterSignature(
                        parameter.Owner is ITypeDescriptor ? GenericParameterType.Type : GenericParameterType.Method,
                        parameter.Number));
                SelfSubstitutedRef = module.DefaultImporter.ImportTypeSignature(self).ToTypeDefOrRef();
                pointerStore = new GenericInstanceTypeSignature(imports.Il2CppClassPointerStore.ToTypeDefOrRef(),
                    InteropTypeReferences.IsValueType(imports.Il2CppClassPointerStore), SelfSubstitutedRef.ToTypeSignature());
            }
            else
            {
                SelfSubstitutedRef = NewType;
                pointerStore = new GenericInstanceTypeSignature(imports.Il2CppClassPointerStore.ToTypeDefOrRef(),
                    InteropTypeReferences.IsValueType(imports.Il2CppClassPointerStore));
                if (IsPrimitive(OriginalType.ToTypeSignature()) || OriginalType.FullName == "System.String")
                    pointerStore.TypeArguments.Add(module.DefaultImporter.ImportTypeSignature(
                        typeof(string).Assembly.GetType(OriginalType.FullName)));
                else
                    pointerStore.TypeArguments.Add(SelfSubstitutedRef.ToTypeSignature());
            }

            ClassPointerFieldRef = new MemberReference(
                module.DefaultImporter.ImportType(pointerStore.ToTypeDefOrRef()), "NativeClassPtr",
                new FieldSignature(imports.Module.CorLibTypeFactory.IntPtr));

            if (OriginalType.IsEnum) return;

            var renamedFieldCounts = new Dictionary<string, int>();
            foreach (var field in OriginalType.Fields)
                fieldContexts[field] = new InteropFieldContext(this, field, renamedFieldCounts);

            var hasExtensionMethods = false;
            foreach (var method in OriginalType.Methods)
            {
                if (method.IsStatic && method.IsConstructor)
                    continue;
                if (method.IsConstructor && method.Parameters is
                    [{ ParameterType: CorLibTypeSignature { ElementType: ElementType.I } }])
                    continue;

                var context = new InteropMethodContext(this, method);
                methodContexts[method] = context;
                methodContextsByName[method.Name] = context;
                if (context.HasExtensionAttribute)
                    hasExtensionMethods = true;
            }

            if (hasExtensionMethods)
            {
                var constructor = new MemberReference(
                    imports.Module.DefaultImporter.ImportType(typeof(ExtensionAttribute)), ".ctor",
                    MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void));
                NewType.CustomAttributes.Add(new CustomAttribute(constructor));
            }
        }

        public InteropFieldContext GetFieldByOldField(FieldDefinition field) => fieldContexts[field];

        public InteropMethodContext GetMethodByOldMethod(MethodDefinition method) => methodContexts[method];

        public InteropMethodContext TryGetMethodByOldMethod(MethodDefinition method) =>
            methodContexts.TryGetValue(method, out var result) ? result : null;

        public InteropMethodContext TryGetMethodByName(string name) =>
            methodContextsByName.TryGetValue(name, out var result) ? result : null;

        public InteropMethodContext TryGetMethodByUnityAssemblyMethod(MethodDefinition method)
        {
            foreach (var context in methodContexts.Values)
            {
                var original = context.OriginalMethod;
                if (original.Name != method.Name || original.Parameters.Count != method.Parameters.Count)
                    continue;

                var matches = true;
                for (var i = 0; i < original.Parameters.Count; i++)
                {
                    if (original.Parameters[i].ParameterType.FullName == method.Parameters[i].ParameterType.FullName)
                        continue;
                    matches = false;
                    break;
                }
                if (matches)
                    return context;
            }
            return null;
        }

        public InteropFieldContext TryGetFieldByUnityAssemblyField(FieldDefinition field)
        {
            foreach (var context in fieldContexts.Values)
            {
                var original = context.OriginalField;
                if (original.Name == field.Name && original.Signature?.FieldType.FullName == field.Signature?.FieldType.FullName)
                    return context;
            }
            return null;
        }

        private static bool IsPrimitive(TypeSignature type) => type is CorLibTypeSignature
            { ElementType: >= ElementType.Boolean and <= ElementType.R8 or ElementType.I or ElementType.U };

        private string GetDebuggerDisplay() => NewType.FullName;
    }
}
