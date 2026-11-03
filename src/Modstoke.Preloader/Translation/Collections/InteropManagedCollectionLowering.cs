// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;

namespace Modstoke.Preloader
{
    internal static class InteropManagedCollectionLowering
    {
        internal static bool IsStorage(InteropRewriteContext context, TypeSignature source) =>
            InteropManagedVectorLowering.TryGetElement(context, source, out _) ||
            TryGetStorage(context, source, out _, out _) ||
            InteropManagedDictionaryLowering.TryGetStorage(context, source, out _, out _, out _);

        internal static bool IsReference(InteropRewriteContext context, TypeSignature source) =>
            InteropManagedVectorLowering.TryGetElement(context, source, out _) ||
            TryGetContract(context, source, out _, out _) ||
            InteropManagedDictionaryLowering.TryGetStorage(context, source, out _, out _, out var type) && !type.IsValueType;

        internal static bool IsElement(InteropRewriteContext context, TypeSignature source, InteropRestorationPlan plan = null) =>
            InteropNativeArrayLowering.IsStrippedHandle(context, source) ||
            (plan ?? InteropRestorationPlan.For(context)).IsManagedReference(source) ||
            InteropManagedVectorLowering.TryGetElement(context, source, out _, plan) ||
            TryGetStorage(context, source, out _, out _, plan) ||
            InteropManagedDictionaryLowering.TryGetStorage(context, source, out _, out _, out _, plan);

        private static bool IsElementOrRestoredValue(InteropRewriteContext context, TypeSignature source, InteropRestorationPlan plan = null) =>
            IsElement(context, source, plan) ||
            InteropManagedDictionaryLowering.IsRestoredValue(plan ?? InteropRestorationPlan.For(context), source);

        internal static bool TryGetContract(InteropRewriteContext context, TypeSignature source,
            out TypeSignature element, out Type runtimeType, InteropRestorationPlan plan = null)
        {
            if (TryGetStorage(context, source, out element, out runtimeType, plan) && !runtimeType.IsValueType)
                return true;
            element = null;
            runtimeType = null;
            return false;
        }

        internal static bool TryGetStorage(InteropRewriteContext context, TypeSignature source,
            out TypeSignature element, out Type runtimeType, InteropRestorationPlan plan = null)
        {
            element = null;
            runtimeType = null;
            if (source is not GenericInstanceTypeSignature instance ||
                instance.TypeArguments.Count != 1 || !InteropArraySegmentLowering.HasSourceContract(instance.GenericType)) return false;
            runtimeType = instance.GenericType.FullName switch
            {
                "System.Collections.Generic.IEnumerable`1" => typeof(IEnumerable<>),
                "System.Collections.Generic.IEnumerator`1" => typeof(IEnumerator<>),
                "System.Collections.Generic.Queue`1" => typeof(Queue<>),
                "System.Collections.Generic.Stack`1" => typeof(Stack<>),
                "System.Collections.Generic.Stack`1/Enumerator" => typeof(Stack<>.Enumerator),
                "System.Collections.Generic.List`1" => typeof(List<>),
                "System.Collections.Generic.List`1/Enumerator" => typeof(List<>.Enumerator),
                _ => null
            };
            if (runtimeType == null) return false;
            if (instance.IsValueType != runtimeType.IsValueType ||
                !(runtimeType == typeof(IEnumerable<>) || runtimeType == typeof(IEnumerator<>) || runtimeType == typeof(Queue<>)
                    ? IsElementOrRestoredValue(context, instance.TypeArguments[0], plan)
                    : IsElement(context, instance.TypeArguments[0], plan)))
            {
                runtimeType = null;
                return false;
            }
            element = instance.TypeArguments[0];
            return true;
        }

        internal static bool IsEnumerator(Type runtimeType) =>
            runtimeType == typeof(List<>.Enumerator) || runtimeType == typeof(Stack<>.Enumerator);

        internal static bool TryGetBoxContract(InteropRewriteContext context, TypeSignature source,
            out TypeSignature contract)
        {
            contract = null;
            if (source is not GenericInstanceTypeSignature instance || !instance.IsValueType) return false;
            if (InteropManagedDictionaryLowering.TryGetStorage(context, source, out _, out _, out var dictionaryType) &&
                InteropManagedDictionaryLowering.IsEnumerator(dictionaryType) &&
                InteropManagedDictionaryLowering.TryGetElement(context, source, out var dictionaryElement) &&
                IsElementOrRestoredValue(context, dictionaryElement))
            {
                var enumerator = new TypeReference(instance.ContextModule, instance.GenericType.Scope.GetAssembly().ToAssemblyReference(),
                    "System.Collections.Generic", "IEnumerator`1");
                contract = new GenericInstanceTypeSignature(enumerator, false, dictionaryElement);
                return true;
            }
            if (TryGetStorage(context, source, out var collectionElement, out var collectionType) && IsEnumerator(collectionType))
            {
                // The known framework enumerators implement this interface even when the source facade has no definition.
                var enumerator = new TypeReference(instance.ContextModule, instance.GenericType.Scope.GetAssembly().ToAssemblyReference(),
                    "System.Collections.Generic", "IEnumerator`1");
                contract = new GenericInstanceTypeSignature(enumerator, false, collectionElement);
                return true;
            }
            if (!InteropNativeArrayLowering.TryGetStorage(context, source, out var element, out var runtimeType) &&
                !InteropArraySegmentLowering.TryGetStorage(context, source, out element, out runtimeType)) return false;
            var definition = instance.GenericType.Resolve();
            if (definition == null) return false;

            var interfaces = runtimeType.GetInterfaces();
            var runtimeContract = interfaces.FirstOrDefault(type => type.IsGenericType &&
                type.GetGenericTypeDefinition() == typeof(IEnumerator<>)) ??
                interfaces.FirstOrDefault(type => type.IsGenericType &&
                    type.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (runtimeContract == null ||
                runtimeContract.GetGenericArguments()[0] != runtimeType.GetGenericArguments()[0]) return false;

            var substitution = GenericContext.FromType(instance);
            foreach (var implementation in definition.Interfaces)
            {
                // Keep the original signed source interface; only substitute its element argument.
                var candidate = implementation.Interface.ToTypeSignature().InstantiateGenericTypes(substitution);
                if (!TryGetContract(context, candidate, out var candidateElement, out var candidateRuntimeType) ||
                    candidateRuntimeType != runtimeContract.GetGenericTypeDefinition() ||
                    !SignatureComparer.Default.Equals(element, candidateElement)) continue;
                contract = candidate;
                return true;
            }
            return false;
        }

        internal static bool CanAssign(InteropRewriteContext context, TypeSignature source, TypeSignature destination)
        {
            if (InteropManagedVectorLowering.CanAssign(context, source, destination)) return true;
            if (InteropManagedDictionaryLowering.TryGetStorage(context, destination, out _, out _, out var dictionaryType) &&
                !dictionaryType.IsValueType)
                return SignatureComparer.Default.Equals(source, destination);
            if (!TryGetContract(context, destination, out var element, out var runtimeType)) return false;
            if (runtimeType == typeof(IEnumerable<>) &&
                InteropManagedDictionaryLowering.TryGetStorage(context, source, out _, out _, out var sourceDictionary) &&
                !sourceDictionary.IsValueType && InteropManagedDictionaryLowering.TryGetElement(context, source, out var dictionaryElement))
                return SignatureComparer.Default.Equals(dictionaryElement, element) ||
                    InteropRestorationPlan.For(context).CanAssignManagedReference(dictionaryElement, element);
            if (TryGetContract(context, source, out var sourceElement, out var sourceRuntimeType))
            {
                if (SignatureComparer.Default.Equals(source, destination)) return true;
                if (runtimeType == typeof(IEnumerable<>) && (sourceRuntimeType == typeof(IEnumerable<>) ||
                    sourceRuntimeType == typeof(Stack<>) || sourceRuntimeType == typeof(List<>)) ||
                    runtimeType == typeof(IEnumerator<>) && sourceRuntimeType == typeof(IEnumerator<>))
                    return SignatureComparer.Default.Equals(sourceElement, element) ||
                        InteropRestorationPlan.For(context).CanAssignManagedReference(sourceElement, element);
            }
            return runtimeType == typeof(IEnumerable<>) &&
                InteropManagedVectorLowering.TryGetElement(context, source, out var vectorElement) &&
                InteropManagedVectorLowering.CanAssignElement(context, vectorElement, element);
        }
    }
}
