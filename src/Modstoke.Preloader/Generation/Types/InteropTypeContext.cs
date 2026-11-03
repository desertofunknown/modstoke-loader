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

namespace Modstoke.Preloader
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
        private readonly Dictionary<string, List<InteropFieldContext>> fieldCandidatesByName = new();
        private readonly Dictionary<string, List<InteropMethodContext>> methodCandidatesByName = new();
        private Dictionary<MethodDefinition, MethodDefinition> restoredValueConstructors;
        private Dictionary<MethodDefinition, MethodDefinition> restoredReferenceConstructors;
        private Dictionary<MethodDefinition, MethodDefinition> restoredReferenceInitializers;
        private Dictionary<MethodDefinition, InteropInternalCallContext> restoredInternalCalls;
        private Dictionary<MethodDefinition, MethodDefinition> restoredManagedMethods;
        private Dictionary<MethodDefinition, MethodDefinition> restoredBodies;
        internal Dictionary<(MethodDefinition Method, string Arguments), MethodDefinition> ScopedReferenceMethods;

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
            {
                var context = new InteropFieldContext(this, field, renamedFieldCounts);
                fieldContexts[field] = context;
                if (!fieldCandidatesByName.TryGetValue(field.Name, out var candidates))
                    fieldCandidatesByName[field.Name] = candidates = new();
                candidates.Add(context);
            }

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
                if (!methodCandidatesByName.TryGetValue(method.Name, out var candidates))
                    methodCandidatesByName[method.Name] = candidates = new();
                candidates.Add(context);
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

        internal void AddRestoredValueConstructor(MethodDefinition source, MethodDefinition initializer)
        {
            restoredValueConstructors ??= new(ReferenceEqualityComparer.Instance);
            restoredValueConstructors.Add(source, initializer);
        }

        internal MethodDefinition TryGetRestoredValueConstructor(MethodDefinition source) =>
            source != null && restoredValueConstructors != null &&
            restoredValueConstructors.TryGetValue(source, out var initializer) ? initializer : null;

        internal void AddRestoredReferenceConstructor(MethodDefinition source, MethodDefinition target,
            MethodDefinition initializer = null)
        {
            restoredReferenceConstructors ??= new(ReferenceEqualityComparer.Instance);
            restoredReferenceConstructors.Add(source, target);
            if (initializer != null)
            {
                restoredReferenceInitializers ??= new(ReferenceEqualityComparer.Instance);
                restoredReferenceInitializers.Add(source, initializer);
            }
        }

        internal MethodDefinition TryGetRestoredReferenceConstructor(MethodDefinition source) =>
            source != null && restoredReferenceConstructors != null &&
                restoredReferenceConstructors.TryGetValue(source, out var target) ? target : null;

        internal MethodDefinition TryGetRestoredReferenceInitializer(MethodDefinition source) =>
            source != null && restoredReferenceInitializers != null &&
                restoredReferenceInitializers.TryGetValue(source, out var initializer) ? initializer : null;

        internal void AddRestoredInternalCall(MethodDefinition source, MethodDefinition target,
            FieldDefinition field, TypeDefinition delegateType, HashSet<int> inPlaceValueReferences)
        {
            restoredInternalCalls ??= new(ReferenceEqualityComparer.Instance);
            restoredInternalCalls.Add(source,
                new InteropInternalCallContext(source, target, field, delegateType, this, inPlaceValueReferences));
        }

        internal InteropInternalCallContext TryGetRestoredInternalCall(MethodDefinition source) =>
            source != null && restoredInternalCalls != null &&
                restoredInternalCalls.TryGetValue(source, out var call) ? call : null;

        internal void AddRestoredManagedMethod(MethodDefinition source, MethodDefinition target)
        {
            restoredManagedMethods ??= new(ReferenceEqualityComparer.Instance);
            restoredManagedMethods.Add(source, target);
        }

        internal MethodDefinition TryGetRestoredManagedMethod(MethodDefinition source) =>
            source != null && restoredManagedMethods != null &&
                restoredManagedMethods.TryGetValue(source, out var target) ? target : null;

        // Body translation eligibility is separate from managed-only generic/callback contracts.
        internal void AddRestoredBody(MethodDefinition source, MethodDefinition target)
        {
            restoredBodies ??= new(ReferenceEqualityComparer.Instance);
            restoredBodies.Add(source, target);
        }

        internal MethodDefinition TryGetRestoredBody(MethodDefinition source) =>
            source != null && restoredBodies != null &&
                restoredBodies.TryGetValue(source, out var target) ? target : null;

        public InteropMethodContext TryGetMethodByUnityAssemblyMethod(MethodDefinition method)
        {
            if (!methodCandidatesByName.TryGetValue(method.Name, out var candidates))
                return null;

            foreach (var context in candidates)
            {
                var original = context.OriginalMethod;
                if (original.Name != method.Name || original.Parameters.Count != method.Parameters.Count ||
                    original.IsStatic != method.IsStatic ||
                    original.Signature.GenericParameterCount != method.Signature.GenericParameterCount ||
                    ReturnTypeForUnityMatch(original.Signature.ReturnType).FullName !=
                    ReturnTypeForUnityMatch(method.Signature.ReturnType).FullName)
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

        private static TypeSignature ReturnTypeForUnityMatch(TypeSignature type)
        {
            // Cpp2IL omits the readonly return annotation, but retains the native by-reference signature.
            return type is CustomModifierTypeSignature { IsRequired: true, BaseType: ByReferenceTypeSignature } modifier &&
                modifier.ModifierType.FullName == "System.Runtime.InteropServices.InAttribute"
                ? modifier.BaseType
                : type;
        }

        public InteropFieldContext TryGetFieldByUnityAssemblyField(FieldDefinition field)
        {
            if (!fieldCandidatesByName.TryGetValue(field.Name, out var candidates))
                return null;

            foreach (var context in candidates)
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
