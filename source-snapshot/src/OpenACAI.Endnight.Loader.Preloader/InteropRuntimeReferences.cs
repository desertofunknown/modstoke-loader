// SPDX-License-Identifier: GPL-3.0-or-later
// Runtime references adapted from Il2CppInterop RuntimeAssemblyReferences (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed partial class InteropRuntimeReferences
    {
        private readonly InteropRewriteContext context;
        private readonly IResolutionScope runtime;

        internal InteropRuntimeReferences(ModuleDefinition module, InteropRewriteContext context)
        {
            Module = module;
            this.context = context;
            var assembly = new AssemblyReference("Il2CppInterop.Runtime", new Version(0, 0, 0, 0));
            Module.AssemblyReferences.Add(assembly);
            runtime = assembly;
            Il2CppObjectBase = RuntimeType("InteropTypes", "Il2CppObjectBase");
            Il2CppObjectPool = RuntimeType("Runtime", "Il2CppObjectPool");
            Il2CppStringArray = RuntimeType("InteropTypes.Arrays", "Il2CppStringArray");
            Il2CppArrayBase = RuntimeType("InteropTypes.Arrays", "Il2CppArrayBase`1");
            Il2CppStructArray = RuntimeType("InteropTypes.Arrays", "Il2CppStructArray`1");
            Il2CppReferenceArray = RuntimeType("InteropTypes.Arrays", "Il2CppReferenceArray`1");
            Il2CppClassPointerStore = RuntimeType(null, "Il2CppClassPointerStore`1");
            Il2Cpp = RuntimeType(null, "IL2CPP");
            RuntimeReflectionHelper = RuntimeType(null, "RuntimeReflectionHelper");
            DelegateSupport = RuntimeType(null, "DelegateSupport");
            Il2CppException = RuntimeType(null, "Il2CppException");
            InitializeMethods();
        }

        internal ModuleDefinition Module { get; }
        internal TypeSignature Il2CppObjectBase { get; }
        internal TypeSignature Il2CppObjectPool { get; }
        internal TypeSignature Il2CppStringArray { get; }
        internal TypeSignature Il2CppArrayBase { get; }
        internal TypeSignature Il2CppStructArray { get; }
        internal TypeSignature Il2CppReferenceArray { get; }
        internal TypeSignature Il2CppClassPointerStore { get; }
        internal TypeSignature Il2Cpp { get; }
        internal TypeSignature RuntimeReflectionHelper { get; }
        internal TypeSignature DelegateSupport { get; }
        internal TypeSignature Il2CppException { get; }

        internal IMethodDescriptor WriteFieldWBarrier => context.HasGcWbarrierFieldWrite
            ? IL2CPP_il2cpp_gc_wbarrier_set_field.Value
            : IL2CPP_FieldWriteWbarrierStub.Value;

        private TypeSignature RuntimeType(string suffix, string name) =>
            new TypeReference(Module, runtime,
                suffix == null ? "Il2CppInterop.Runtime" : "Il2CppInterop.Runtime." + suffix, name).ToTypeSignature();

        private static Lazy<IMethodDefOrRef> Instance(TypeSignature owner, string name,
            TypeSignature result, params TypeSignature[] parameters) =>
            new(() => new MemberReference(owner.ToTypeDefOrRef(), name,
                MethodSignature.CreateInstance(result, parameters)));

        private static Lazy<IMethodDefOrRef> Static(TypeSignature owner, string name,
            TypeSignature result, params TypeSignature[] parameters) =>
            new(() => new MemberReference(owner.ToTypeDefOrRef(), name,
                MethodSignature.CreateStatic(result, parameters)));

        private Lazy<IMethodDefOrRef> Native(string name, TypeSignature result, params TypeSignature[] parameters) =>
            Static(Il2Cpp, name, result, parameters);

        private static Lazy<IMethodDefOrRef> Generic(TypeSignature owner, string name,
            bool isStatic, TypeSignature result, params TypeSignature[] parameters) =>
            new(() => new MemberReference(owner.ToTypeDefOrRef(), name, isStatic
                ? MethodSignature.CreateStatic(result, 1, parameters)
                : MethodSignature.CreateInstance(result, 1, parameters)));

        private static GenericMethodReference ArrayConstructor(TypeSignature array, TypeSignature result,
            TypeSignature parameter) => new(element => new MemberReference(
                new GenericInstanceTypeSignature(array.ToTypeDefOrRef(), false, element).ToTypeDefOrRef(),
                ".ctor", MethodSignature.CreateInstance(result, parameter)));

        internal sealed class GenericMethodReference
        {
            private readonly Dictionary<TypeSignature, IMethodDefOrRef> cache = new(ReferenceEqualityComparer.Instance);
            private readonly Func<TypeSignature, IMethodDefOrRef> create;

            internal GenericMethodReference(Func<TypeSignature, IMethodDefOrRef> create) => this.create = create;

            internal IMethodDefOrRef Get(TypeSignature type)
            {
                if (!cache.TryGetValue(type, out var reference))
                {
                    reference = create(type);
                    cache.Add(type, reference);
                }
                return reference;
            }
        }
    }
}
