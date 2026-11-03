// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal static class InteropManagedDictionaryLowering
    {
        internal static bool TryGetTypes(InteropRewriteContext context, TypeSignature source,
            out TypeSignature key, out TypeSignature value, InteropRestorationPlan plan = null)
        {
            if (TryGetStorage(context, source, out key, out value, out var runtimeType, plan) &&
                runtimeType == typeof(InteropNativeKeyDictionary<,>)) return true;
            key = null;
            value = null;
            return false;
        }

        internal static bool TryGetStorage(InteropRewriteContext context, TypeSignature source,
            out TypeSignature key, out TypeSignature value, out Type runtimeType, InteropRestorationPlan plan = null)
        {
            key = null;
            value = null;
            runtimeType = null;
            if (source is not GenericInstanceTypeSignature instance || instance.TypeArguments.Count != 2 ||
                !InteropArraySegmentLowering.HasSourceContract(instance.GenericType)) return false;

            var storage = instance.GenericType.FullName switch
            {
                "System.Collections.Generic.Dictionary`2" => typeof(InteropNativeKeyDictionary<,>),
                "System.Collections.Generic.KeyValuePair`2" => typeof(KeyValuePair<,>),
                "System.Collections.Generic.Dictionary`2/Enumerator" => typeof(InteropNativeKeyDictionary<,>.Enumerator),
                "System.Collections.Generic.Dictionary`2/KeyCollection" => typeof(InteropNativeKeyDictionary<,>.KeyCollection),
                "System.Collections.Generic.Dictionary`2/KeyCollection/Enumerator" => typeof(InteropNativeKeyDictionary<,>.KeyCollection.Enumerator),
                "System.Collections.Generic.Dictionary`2/ValueCollection" => typeof(InteropNativeKeyDictionary<,>.ValueCollection),
                "System.Collections.Generic.Dictionary`2/ValueCollection/Enumerator" => typeof(InteropNativeKeyDictionary<,>.ValueCollection.Enumerator),
                _ => null
            };
            if (storage == null || instance.IsValueType != storage.IsValueType) return false;

            plan ??= InteropRestorationPlan.For(context);
            var keyType = instance.TypeArguments[0];
            var valueType = instance.TypeArguments[1];
            if (!(plan.IsManagedReference(valueType) || IsRestoredValue(plan, valueType) ||
                InteropManagedVectorLowering.TryGetElement(context, valueType, out _, plan) ||
                InteropManagedCollectionLowering.TryGetStorage(context, valueType, out _, out _, plan) ||
                TryGetTypes(context, valueType, out _, out _, plan))) return false;

            if (!IsStringKey(keyType))
            {
                // A native comparer needs an actual closed IL2CPP reference type, not a restored CLR declaration.
                if (keyType is not (TypeDefOrRefSignature or CorLibTypeSignature or GenericInstanceTypeSignature) ||
                    keyType.FullName == "System.String" || InteropRestoredGenericSupport.ContainsGenericParameter(keyType) ||
                    plan.GetNativeIdentityRejection(keyType) != null) return false;
                var definition = keyType is GenericInstanceTypeSignature generic ? generic.GenericType.ToTypeSignature() : keyType;
                var native = context.TryGetTypeByName(InteropRestoredTypeSupport.GetAssemblyName(definition), definition.FullName);
                if (native?.OriginalType is not { IsValueType: false }) return false;
            }

            key = keyType;
            value = valueType;
            runtimeType = storage;
            return true;
        }

        internal static bool IsEnumerator(Type type) => type == typeof(InteropNativeKeyDictionary<,>.Enumerator) ||
            type == typeof(InteropNativeKeyDictionary<,>.KeyCollection.Enumerator) || type == typeof(InteropNativeKeyDictionary<,>.ValueCollection.Enumerator);

        internal static bool IsStringKey(TypeSignature type) =>
            type is CorLibTypeSignature { ElementType: ElementType.String } ||
            type is TypeDefOrRefSignature reference && reference.FullName == "System.String" &&
            InteropArraySegmentLowering.HasSourceContract(reference.Type);

        internal static bool IsRestoredValue(InteropRestorationPlan plan, TypeSignature type) =>
            type is TypeDefOrRefSignature { IsValueType: true } && plan.IsManagedOnlyDefinition(type);

        internal static bool NeedsNativeEquality(InteropRewriteContext context, TypeSignature source)
        {
            if (TryGetStorage(context, source, out _, out _, out var storage)) return storage.IsValueType;
            return InteropManagedCollectionLowering.TryGetStorage(context, source, out var element, out var collection) &&
                collection.IsValueType && NeedsNativeEquality(context, element);
        }

        internal static bool TryGetElement(InteropRewriteContext context, TypeSignature source, out TypeSignature element)
        {
            element = null;
            if (!TryGetStorage(context, source, out var key, out var value, out var type)) return false;
            if (type == typeof(InteropNativeKeyDictionary<,>.KeyCollection) || type == typeof(InteropNativeKeyDictionary<,>.KeyCollection.Enumerator))
                element = key;
            else if (type == typeof(InteropNativeKeyDictionary<,>.ValueCollection) || type == typeof(InteropNativeKeyDictionary<,>.ValueCollection.Enumerator))
                element = value;
            else if (type == typeof(InteropNativeKeyDictionary<,>) || type == typeof(InteropNativeKeyDictionary<,>.Enumerator))
            {
                var instance = (GenericInstanceTypeSignature)source;
                var pair = new TypeReference(instance.ContextModule, instance.GenericType.Scope.GetAssembly().ToAssemblyReference(),
                    "System.Collections.Generic", "KeyValuePair`2");
                element = new GenericInstanceTypeSignature(pair, true, key, value);
            }
            return element != null;
        }
    }
}
