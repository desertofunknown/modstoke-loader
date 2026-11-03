// SPDX-License-Identifier: GPL-3.0-or-later
// Type resolution adapted from Il2CppInterop Pass80UnstripMethods (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed class InteropUnstripTypeResolver
    {
        private readonly InteropRewriteContext context;
        private readonly InteropRuntimeReferences imports;

        internal InteropUnstripTypeResolver(InteropRewriteContext context, InteropRuntimeReferences imports)
        {
            this.context = context;
            this.imports = imports;
        }

        internal TypeSignature Resolve(TypeSignature type)
        {
            return Resolve(type, true);
        }

        internal TypeSignature Resolve(TypeSignature type, bool useSystemCorlibPrimitives)
        {
            var resolved = ResolveRaw(type, useSystemCorlibPrimitives);
            return resolved == null ? null : imports.Module.DefaultImporter.ImportTypeSignature(resolved);
        }

        internal TypeSignature ResolveRaw(TypeSignature type, bool useSystemCorlibPrimitives = true)
        {
            return ResolveCore(type, useSystemCorlibPrimitives, out _);
        }

        internal string GetRejectionReason(TypeSignature type)
        {
            ResolveCore(type, true, out var reason);
            return reason;
        }

        internal TypeSignature ResolveFieldStorage(TypeSignature type)
        {
            if (InteropSpanLowering.ContainsSpan(context, type)) return null;
            if (type is PointerTypeSignature)
                return imports.Module.CorLibTypeFactory.IntPtr;
            if (type is not CustomModifierTypeSignature modifier)
                return Resolve(type);
            var runtimeType = InteropRestoredTypeSupport.GetModifierType(modifier.ModifierType);
            var storage = ResolveFieldStorage(modifier.BaseType);
            return runtimeType == null || storage == null ? null : new CustomModifierTypeSignature(
                imports.Module.DefaultImporter.ImportType(runtimeType), modifier.IsRequired, storage);
        }

        internal TypeSignature ResolveGenericConstraint(TypeSignature type, out string reason)
        {
            reason = null;
            if (type == null)
            {
                reason = "missing-generic-constraint";
                return null;
            }
            var intrinsic = InteropRestoredGenericSupport.GetIntrinsicConstraint(type);
            if (intrinsic != null)
                return imports.Module.DefaultImporter.ImportTypeSignature(intrinsic);
            if (InteropManagedCollectionLowering.TryGetContract(context, type, out _, out _))
            {
                var collection = ResolveCore(type, true, out reason);
                return collection == null ? null : imports.Module.DefaultImporter.ImportTypeSignature(collection);
            }
            var definition = type is GenericParameterSignature ? null : type.Resolve();
            if (type is not GenericParameterSignature && definition == null)
            {
                reason = "unresolved-generic-constraint-contract: " + type.FullName;
                return null;
            }
            if (definition?.IsInterface == true || definition?.BaseType?.FullName is "System.MulticastDelegate" or "System.Delegate")
            {
                if (definition.IsInterface && InteropRestorationPlan.For(context).IsManagedOnlyDefinition(type) &&
                    ResolveCore(type, true, out reason) is { } contract && contract.Resolve()?.IsInterface == true)
                    return imports.Module.DefaultImporter.ImportTypeSignature(contract);
                reason = "native-generic-constraint-contract: " + type.FullName +
                    ". An IL2CPP interface or delegate wrapper is not the corresponding CLR constraint.";
                return null;
            }
            var rewritten = ResolveCore(type, true, out reason);
            return rewritten == null ? null : imports.Module.DefaultImporter.ImportTypeSignature(rewritten);
        }

        private TypeSignature ResolveCore(TypeSignature type, bool useSystemCorlibPrimitives, out string reason)
        {
            reason = null;
            if (type == null)
            {
                reason = "missing-type-signature";
                return null;
            }
            if (type is GenericParameterSignature parameter)
                return new GenericParameterSignature(imports.Module, parameter.ParameterType, parameter.Index);
            if (type is ByReferenceTypeSignature reference)
                return ResolveCore(reference.BaseType, true, out reason)?.MakeByReferenceType();
            if (type is ArrayBaseTypeSignature array)
            {
                if (InteropSpanLowering.ContainsSpan(context, array.BaseType))
                {
                    reason = "ref-like-array-element: Span storage cannot be placed in an array.";
                    return null;
                }
                if (array is not SzArrayTypeSignature)
                    return InteropRectangularArraySupport.TryResolve(context, imports, array, out var rectangular, out reason)
                        ? rectangular : null;
                var element = ResolveCore(array.BaseType, true, out reason);
                if (element == null)
                    return null;
                if (InteropManagedVectorLowering.TryGetElement(context, array, out var vectorElement))
                {
                    if (InteropNativeArrayLowering.IsStrippedHandle(context, vectorElement) &&
                        !InteropNativeArrayLowering.HasHandleLayout(element.Resolve(), vectorElement.FullName))
                    {
                        reason = "managed-handle-vector-layout: The restored element layout is unavailable.";
                        return null;
                    }
                    return element.MakeSzArrayType();
                }
                reason = InteropRestorationPlan.For(context).GetNativeIdentityRejection(array.BaseType);
                if (reason != null)
                    return null;
                if (element.FullName == "System.String")
                    return imports.Il2CppStringArray;
                var arrayType = element is GenericParameterSignature
                    ? imports.Il2CppArrayBase
                    : element.IsValueType ? imports.Il2CppStructArray : imports.Il2CppReferenceArray;
                return new GenericInstanceTypeSignature(arrayType.ToTypeDefOrRef(), false, element);
            }
            if (type is PointerTypeSignature pointer)
            {
                if (InteropRestorationPlan.For(context).ContainsManagedOnlyStorage(pointer.BaseType))
                {
                    reason = "managed-storage-native-pointer: A restored managed container has no native pointer layout.";
                    return null;
                }
                return ResolveCore(pointer.BaseType, true, out reason)?.MakePointerType();
            }
            if (type is PinnedTypeSignature pinned)
            {
                if (InteropSpanLowering.ContainsSpan(context, pinned.BaseType))
                {
                    reason = "ref-like-pinned-storage: Pin the backing buffer, not a span descriptor.";
                    return null;
                }
                return ResolveCore(pinned.BaseType, true, out reason)?.MakePinnedType();
            }
            if (type is CustomModifierTypeSignature modifier)
            {
                var element = ResolveCore(modifier.BaseType, true, out reason);
                if (element == null)
                    return null;
                var modifierType = InteropRestoredTypeSupport.GetModifierType(modifier.ModifierType);
                if (modifierType == null)
                {
                    reason = "unsupported-custom-modifier: " + modifier.ModifierType.FullName;
                    return null;
                }
                return new CustomModifierTypeSignature(imports.Module.DefaultImporter.ImportType(modifierType),
                    modifier.IsRequired, element);
            }
            if (type is GenericInstanceTypeSignature instance)
            {
                if (InteropManagedDictionaryLowering.TryGetStorage(context, instance, out var dictionaryKey, out var dictionaryValue,
                    out var dictionaryType))
                {
                    var key = ResolveCore(dictionaryKey, true, out reason);
                    if (key == null) return null;
                    var value = ResolveCore(dictionaryValue, true, out reason);
                    return value == null ? null : new GenericInstanceTypeSignature(
                        imports.Module.DefaultImporter.ImportType(dictionaryType), dictionaryType.IsValueType, key, value);
                }
                if (InteropManagedCollectionLowering.TryGetStorage(context, instance, out var collectionElement, out var collectionType))
                {
                    if (InteropNativeArrayLowering.IsStrippedHandle(context, collectionElement))
                        return ResolveHandleContainer(collectionElement, collectionType, collectionType.IsValueType,
                            "managed-collection-element-layout: The restored element layout is unavailable.", out reason);
                    var element = ResolveCore(collectionElement, true, out reason);
                    return element == null ? null : new GenericInstanceTypeSignature(
                        imports.Module.DefaultImporter.ImportType(collectionType), collectionType.IsValueType, element);
                }
                if (InteropArraySegmentLowering.TryGetStorage(context, instance, out var segmentElement, out var segmentType))
                    return ResolveHandleContainer(segmentElement, segmentType, true,
                        "managed-array-segment-element-layout: The restored element layout is unavailable.", out reason);
                if (InteropSpanLowering.TryGetSpan(context, instance, out var spanElement, out var spanType))
                    return ResolveHandleContainer(spanElement, spanType, true,
                        "managed-span-element-layout: The restored element layout is unavailable.", out reason);
                if (InteropNativeArrayLowering.TryGetStorage(context, instance, out var arrayElement, out var storageType))
                    return ResolveHandleContainer(arrayElement, storageType, true,
                        "native-array-storage-element-layout: The restored handle layout is unavailable.", out reason);
                var definition = ResolveCore(instance.GenericType.ToTypeSignature(), true, out reason);
                if (definition == null)
                    return null;
                if (definition.Resolve()?.GenericParameters.Count != instance.TypeArguments.Count)
                {
                    reason = "generic-arity-mismatch: " + instance.FullName;
                    return null;
                }
                var resolved = new GenericInstanceTypeSignature(definition.ToTypeDefOrRef(),
                    InteropTypeReferences.IsValueType(definition));
                var plan = InteropRestorationPlan.For(context);
                var managedDefinition = plan.IsManagedOnlyDefinition(instance.GenericType.ToTypeSignature());
                foreach (var argument in instance.TypeArguments)
                {
                    if (InteropSpanLowering.ContainsSpan(context, argument))
                    {
                        reason = "ref-like-generic-argument: This generic storage has no span lifetime contract.";
                        return null;
                    }
                    var resolvedArgument = ResolveCore(argument, true, out reason);
                    if (resolvedArgument == null)
                        return null;
                    reason = managedDefinition ? null : plan.GetNativeIdentityRejection(argument);
                    if (reason != null)
                        return null;
                    resolved.TypeArguments.Add(resolvedArgument);
                }
                return resolved;
            }
            if (type is BoxedTypeSignature or FunctionPointerTypeSignature)
            {
                reason = "unsupported-type-signature: " + type.FullName;
                return null;
            }
            if (type is SentinelTypeSignature)
                return type;
            if (type.DeclaringType != null)
            {
                var enclosing = ResolveCore(type.DeclaringType.ToTypeSignature(), true, out reason)?.Resolve();
                if (enclosing == null)
                    return null;
                var assembly = context.GetContextForNewAssembly(enclosing.DeclaringModule.Assembly);
                var nested = assembly.TryGetTypeByName(type.FullName)?.NewType
                    ?? enclosing.NestedTypes.FirstOrDefault(candidate => candidate.Name == type.Name);
                if (nested != null)
                    return nested.ToTypeSignature();
                reason = InteropRestorationPlan.For(context).GetRejectionReason(type);
                return null;
            }

            var assemblyName = InteropRestoredTypeSupport.GetAssemblyName(type);

            // Static member declarers can require rewritten corlib types; nested signatures still use managed primitives.
            var elementType = type.ElementType;
            var primitive = type is CorLibTypeSignature &&
                (elementType is >= ElementType.Boolean and <= ElementType.R8 or ElementType.I or ElementType.U);
            if (useSystemCorlibPrimitives && (primitive || elementType is ElementType.String or ElementType.Void))
                return imports.Module.CorLibTypeFactory.FromElementType(elementType);

            if (assemblyName == "UnityEngine")
            {
                foreach (var assembly in context.Assemblies)
                {
                    if (!assembly.NewAssembly.Name.StartsWith("UnityEngine"))
                        continue;
                    var resolved = assembly.TryGetTypeByName(type.FullName)?.NewType;
                    if (resolved != null)
                        return resolved.ToTypeSignature();
                }
            }
            var target = context.TryGetTypeByName(assemblyName, type.FullName)?.NewType;
            if (target != null)
                return target.ToTypeSignature();
            reason = InteropRestorationPlan.For(context).GetRejectionReason(type);
            return null;
        }

        private TypeSignature ResolveHandleContainer(TypeSignature sourceElement, Type runtimeType,
            bool isValueType, string layoutRejection, out string reason)
        {
            var element = ResolveCore(sourceElement, true, out reason);
            if (element == null || !InteropNativeArrayLowering.HasHandleLayout(element.Resolve(), sourceElement.FullName))
            {
                reason ??= layoutRejection;
                return null;
            }
            return new GenericInstanceTypeSignature(imports.Module.DefaultImporter.ImportType(runtimeType), isValueType, element);
        }
    }
}
