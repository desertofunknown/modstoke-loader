// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropRestorationPlan
    {
        private static readonly ConditionalWeakTable<InteropRewriteContext, InteropRestorationPlan> plans = new();
        private readonly InteropRewriteContext context;
        private readonly Dictionary<TypeDefinition, Entry> entries = new();
        private readonly Dictionary<(string Assembly, string Name), Entry> names = new();
        private readonly Dictionary<string, Entry> unityNames = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Assembly, string Name), TypeDefinition> existingTypes = new();

        private InteropRestorationPlan(InteropRewriteContext context)
        {
            this.context = context;
            foreach (var assembly in context.UnityAssemblies.Assemblies)
            foreach (var type in assembly.ManifestModule.GetAllTypes())
            {
                var existing = FindExisting(assembly.Name, type.FullName);
                var entry = new Entry(type, existing);
                entries.Add(type, entry);
                names[(assembly.Name, type.FullName)] = entry;
                if (assembly.Name.StartsWith("UnityEngine"))
                    unityNames.TryAdd(type.FullName, entry);
            }

            foreach (var entry in entries.Values)
            {
                var type = entry.Type;
                // Native declarations remain authoritative even when their Unity source is not restorable.
                if (entry.Existing != null)
                    continue;
                if (type.Name == "<Module>")
                    entry.Reject("module-type: The module pseudo-type is not a restorable declaration.");
                else if (type.IsInterface)
                {
                    if (!InteropRestoredInterfaceSupport.TryDescribe(type, out var interfaceReason))
                        entry.Reject(interfaceReason);
                }
                else if (type.BaseType?.FullName == "System.MulticastDelegate")
                {
                    if (!InteropRestoredUnmanagedDelegateSupport.TryDescribe(type, out _, out _, out _) &&
                        !InteropRestoredManagedDelegateSupport.TryDescribe(type, out _, out var delegateReason))
                        entry.Reject(delegateReason);
                }
                else if (type.DeclaringType?.IsEnum == true)
                    entry.Reject("enum-enclosing-type: Nested declarations under an enum are unsupported.");
                else if (type.GenericParameters.Count != 0 && !IsManagedGenericHelper(type, out var genericReason))
                    entry.Reject(genericReason);
                else if (!InteropTypeReferences.IsValueType(type) && !IsManagedHelper(entry) &&
                    !InteropRestoredTypeSupport.IsManagedException(type))
                    entry.Reject("missing-native-reference-type: Reference helpers require an entirely managed, nonvirtual base chain or a supported Exception root.");
                if (!InteropTypeReferences.IsValueType(type) || type.GenericParameters.Count != 0)
                    entry.ManagedOnlyStorage = true;
                if (type.DeclaringType != null)
                    entries[type.DeclaringType].Users.Add((entry, "enclosing-type-unavailable: " + type.DeclaringType.FullName));
            }

            // Collection elements can be declared after their owners. Classify all bases before inspecting storage.
            foreach (var entry in entries.Values)
            {
                if (entry.Existing != null || !entry.Supported)
                    continue;
                var type = entry.Type;
                if (type.IsInterface)
                {
                    foreach (var method in type.Methods)
                    {
                        var reason = AddManagedSignatureDependency(entry, method.Signature.ReturnType, true, method.Name);
                        foreach (var parameter in method.Signature.ParameterTypes)
                            reason ??= AddManagedSignatureDependency(entry, parameter, false, method.Name);
                        if (reason != null) entry.Reject("managed-interface-signature: " + method.FullName + ": " + reason);
                    }
                }
                else if (type.BaseType?.FullName == "System.MulticastDelegate" &&
                    InteropRestoredManagedDelegateSupport.TryDescribe(type, out var invoke, out _))
                {
                    var reason = AddManagedSignatureDependency(entry, invoke.Signature.ReturnType, true, "Invoke");
                    foreach (var parameter in invoke.Signature.ParameterTypes)
                        reason ??= AddManagedSignatureDependency(entry, parameter, false, "Invoke");
                    if (reason == null)
                        InteropRestoredManagedDelegateSupport.ValidateVariance(type, invoke.Signature,
                            FindVarianceDefinition, true, out reason);
                    if (reason != null) entry.Reject("managed-delegate-signature: " + type.FullName + ": " + reason);
                }
                if (!entry.Supported)
                    continue;
                foreach (var parameter in type.GenericParameters)
                foreach (var constraint in parameter.Constraints)
                {
                    var reason = AddConstraintDependency(entry, constraint.Constraint?.ToTypeSignature());
                    if (reason != null)
                        entry.Reject("managed-generic-constraint: " + type.FullName + "::" + parameter.Name + ": " + reason);
                }
                if (!entry.Supported)
                    continue;
                if (entry.NeedsLayout && !type.IsSequentialLayout && !type.IsExplicitLayout)
                {
                    entry.Reject("unsupported-value-layout: A restored struct requires sequential or explicit layout.");
                    continue;
                }

                foreach (var field in type.Fields)
                {
                    if (InteropFieldRestoration.IsThreadStatic(field) &&
                        (!field.IsStatic || field.IsLiteral || field.FieldRva != null))
                    {
                        entry.Reject(type.FullName + "::" + field.Name +
                            ": invalid-thread-static-storage: Thread-local storage requires a static, nonliteral field without RVA data.");
                        break;
                    }
                    if (field.IsLiteral)
                        continue;
                    var reason = type.GenericParameters.Count != 0 && field.FieldRva != null
                        ? "managed-generic-special-field-storage: RVA storage requires a separate restoration contract."
                        : !field.IsStatic && type.IsExplicitLayout && (field.FieldOffset == null || field.FieldOffset < 0)
                        ? "missing-field-offset: Explicit-layout storage requires a nonnegative offset."
                        : field.HasFieldMarshal && field.MarshalDescriptor == null
                            ? "missing-field-marshalling: The field declares marshalling without a descriptor."
                            : field.MarshalDescriptor != null && field.MarshalDescriptor is not SimpleMarshalDescriptor
                                ? "unsupported-field-marshalling: Inline storage cannot be reconstructed from this marshal descriptor."
                                : AddFieldDependency(entry, field);
                    if (reason != null)
                    {
                        entry.Reject(type.FullName + "::" + field.Name + ": " + reason);
                        break;
                    }
                }
            }

            ResolveDependencies();
            PropagateManagedStorage();
        }

        // Capture once, before restored declarations are registered as if they were native inputs.
        internal static InteropRestorationPlan For(InteropRewriteContext context) =>
            plans.GetValue(context, value => new InteropRestorationPlan(value));

        internal bool CanRestore(TypeDefinition type) => entries.TryGetValue(type, out var entry) && entry.Supported;

        internal bool IsManagedOnlyDefinition(TypeSignature type)
        {
            if (type is GenericInstanceTypeSignature instance)
                type = instance.GenericType.ToTypeSignature();
            return type != null && FindSource(InteropRestoredTypeSupport.GetAssemblyName(type), type.FullName)
                is { Existing: null, Supported: true };
        }

        internal string GetRejectionReason(TypeDefinition type) =>
            entries.TryGetValue(type, out var entry) ? entry.RejectionReason : "source-type-not-planned: " + type.FullName;

        internal string GetRepresentation(TypeDefinition type)
        {
            if (!entries.TryGetValue(type, out var entry))
                return "unplanned";
            if (entry.Existing != null)
            {
                if (InteropNativeNavMeshTypes.IsPrepared(entry.Existing) || InteropNativeVideoTypes.IsPrepared(entry.Existing) ||
                    InteropNativeEventDebuggerTypes.IsPrepared(entry.Existing) || InteropNativeStyleEnumType.IsPrepared(entry.Existing))
                {
                    if (entry.Existing.FullName == "UnityEngine.AI.NavMeshBuilder")
                        return "prepared-native-call-boundaries";
                    return InteropTypeReferences.IsValueType(entry.Existing)
                        ? "prepared-native-value" : "prepared-native-wrapper";
                }
                return InteropTypeReferences.IsValueType(entry.Existing) ? "existing-native-value" : "existing-native-wrapper";
            }
            if (!entry.Supported)
                return "unavailable";
            if (entry.Type.BaseType?.FullName == "System.MulticastDelegate")
                return InteropRestoredManagedDelegateSupport.HasNativeConvention(entry.Type)
                    ? "clr-function-pointer-delegate" : "clr-managed-delegate";
            if (entry.Type.IsInterface)
                return "clr-interface-contract";
            return entry.ManagedOnlyStorage ? "managed-only-storage" : "value-layout-without-native-identity";
        }

        internal bool ContainsManagedOnlyStorage(TypeSignature type)
        {
            if (type == null) return false;
            if (InteropManagedDictionaryLowering.TryGetStorage(context, type, out _, out _, out _, this)) return true;
            if (InteropManagedCollectionLowering.TryGetStorage(context, type, out _, out _, this)) return true;
            if (InteropSpanLowering.TryGetSpan(context, type, out _, out _)) return true;
            if (InteropArraySegmentLowering.TryGetStorage(context, type, out _, out _)) return true;
            if (InteropNativeArrayLowering.TryGetStorage(context, type, out _, out _) ||
                InteropManagedVectorLowering.TryGetElement(context, type, out _, this)) return true;
            if (type is CustomModifierTypeSignature modifier) return ContainsManagedOnlyStorage(modifier.BaseType);
            if (type is TypeSpecificationSignature specification && type is not GenericInstanceTypeSignature)
                return ContainsManagedOnlyStorage(specification.BaseType);
            if (type is GenericInstanceTypeSignature instance)
                return ContainsManagedOnlyStorage(instance.GenericType.ToTypeSignature())
                    || instance.TypeArguments.Any(ContainsManagedOnlyStorage);
            return FindSource(InteropRestoredTypeSupport.GetAssemblyName(type), type.FullName)
                is { Existing: null, Supported: true, ManagedOnlyStorage: true };
        }

        internal string GetRejectionReason(TypeSignature type)
        {
            var entry = FindSource(InteropRestoredTypeSupport.GetAssemblyName(type), type.FullName);
            return entry?.RejectionReason ?? (entry == null ? "native-type-not-mapped: " : "planned-type-not-declared: ") + type.FullName;
        }

        internal string GetNativeIdentityRejection(TypeSignature type)
        {
            if (type is GenericParameterSignature || type is CorLibTypeSignature
                { ElementType: >= ElementType.Boolean and <= ElementType.R8 or ElementType.I or ElementType.U or ElementType.String or ElementType.Object })
                return null;
            if (type is CustomModifierTypeSignature modifier)
                return GetNativeIdentityRejection(modifier.BaseType);
            if (type is GenericInstanceTypeSignature instance)
            {
                var reason = GetNativeIdentityRejection(instance.GenericType.ToTypeSignature());
                foreach (var argument in instance.TypeArguments)
                    reason ??= GetNativeIdentityRejection(argument);
                return reason;
            }
            if (type is ArrayBaseTypeSignature array)
            {
                if (array is not SzArrayTypeSignature &&
                    !InteropRectangularArraySupport.CanRepresent(context, array, out var shapeReason))
                    return shapeReason;
                return GetNativeIdentityRejection(array.BaseType);
            }

            var assemblyName = InteropRestoredTypeSupport.GetAssemblyName(type);
            var entry = FindSource(assemblyName, type.FullName);
            if (entry?.Existing != null || entry == null && FindExisting(assemblyName, type.FullName) != null)
                return null;
            return "missing-native-type-identity: " + type.FullName +
                ". A restored CLR layout or declaration does not provide an IL2CPP class, boxing or array identity.";
        }

        private string AddFieldDependency(Entry owner, FieldDefinition field)
        {
            if (InteropSpanLowering.ContainsSpan(context, field.Signature?.FieldType))
                return "ref-like-field-storage: Restored fields do not have a span lifetime contract.";
            if (InteropTreeViewFactoryCache.CanStore(context, field))
                return null;
            return AddStorageDependency(owner, field.Signature?.FieldType, !field.IsStatic, field.Name);
        }

        private string AddManagedSignatureDependency(Entry owner, TypeSignature type, bool result, string member)
        {
            if (result && type is CorLibTypeSignature { ElementType: ElementType.Void }) return null;
            if (!result && type is ByReferenceTypeSignature reference)
                type = reference.BaseType;
            if (type == null || type is PointerTypeSignature or ByReferenceTypeSignature or
                CustomModifierTypeSignature or PinnedTypeSignature or FunctionPointerTypeSignature)
                return "unsupported-managed-contract-signature: " + type?.FullName;
            return AddStorageDependency(owner, type, false, member);
        }

        private string AddStorageDependency(Entry owner, TypeSignature type, bool inline, string fieldName)
        {
            while (type is CustomModifierTypeSignature modifier)
            {
                if (InteropRestoredTypeSupport.GetModifierType(modifier.ModifierType) == null)
                    return "unsupported-field-modifier: " + modifier.ModifierType.FullName;
                type = modifier.BaseType;
            }
            if (type is PointerTypeSignature)
                return null; // Stored as IntPtr, just like ordinary rewritten value-type fields.
            if (type is GenericParameterSignature parameter)
                return parameter.ParameterType == GenericParameterType.Type && parameter.Index < owner.Type.GenericParameters.Count
                    ? null : "unbound-field-generic-parameter: " + type.FullName;
            if (type is CorLibTypeSignature primitive)
            {
                if (primitive.ElementType is >= ElementType.Boolean and <= ElementType.R8 or ElementType.I or ElementType.U)
                    return null;
                if (primitive.ElementType is ElementType.String or ElementType.Object)
                {
                    if (inline) owner.ManagedOnlyStorage = true;
                    return null;
                }
                return "unsupported-field-type: " + type.FullName;
            }
            if (type is ArrayBaseTypeSignature array)
            {
                if (InteropManagedVectorLowering.TryGetElement(context, array, out var vectorElement, this))
                {
                    // The array reference does not inline its elements, including an array of the owner itself.
                    var reason = AddStorageDependency(owner, vectorElement, false, fieldName);
                    if (reason != null) return reason;
                    if (inline) owner.ManagedOnlyStorage = true;
                    return null;
                }
                if (owner.Type.GenericParameters.Count != 0 && InteropRestoredGenericSupport.ContainsGenericParameter(array))
                    return "managed-generic-native-array-storage: An arbitrary CLR type argument has no guaranteed IL2CPP array identity.";
                if (array is not SzArrayTypeSignature &&
                    !InteropRectangularArraySupport.CanRepresent(context, array, out var shapeReason))
                    return shapeReason;
                var identity = GetNativeIdentityRejection(array.BaseType);
                if (identity != null) return identity;
                if (inline) owner.ManagedOnlyStorage = true;
                return null;
            }
            if (type is GenericInstanceTypeSignature generic)
            {
                if (InteropManagedDictionaryLowering.TryGetStorage(context, generic, out _, out var dictionaryValue,
                    out var dictionaryType, this))
                {
                    var reason = AddStorageDependency(owner, dictionaryValue,
                        inline && dictionaryType.IsValueType, fieldName);
                    if (reason != null) return reason;
                    if (inline) owner.ManagedOnlyStorage = true;
                    return null;
                }
                if (InteropManagedCollectionLowering.TryGetStorage(context, generic, out var collectionElement, out var collectionType, this))
                {
                    var reason = AddStorageDependency(owner, collectionElement, inline && collectionType.IsValueType, fieldName);
                    if (reason != null) return reason;
                    if (inline) owner.ManagedOnlyStorage = true;
                    return null;
                }
                if (InteropArraySegmentLowering.TryGetStorage(context, generic, out var segmentElement, out _))
                {
                    var element = FindSource(InteropNativeArrayLowering.AnimationModule, segmentElement.FullName);
                    if (element == null) return "managed-array-segment-element-unavailable: " + segmentElement.FullName;
                    // Both descriptors retain a CLR array, not an inline T or a native buffer.
                    AddTypeDependency(owner, element, false, fieldName);
                    if (inline) owner.ManagedOnlyStorage = true;
                    return null;
                }
                if (InteropNativeArrayLowering.TryGetStorage(context, generic, out var arrayElement, out var storageType))
                {
                    var element = FindSource(InteropNativeArrayLowering.AnimationModule, arrayElement.FullName);
                    if (element == null) return "native-array-storage-element-unavailable: " + arrayElement.FullName;
                    // Enumerators cache T inline; descriptors only require its restored operations.
                    AddTypeDependency(owner, element, inline && InteropNativeArrayLowering.IsEnumerator(storageType), fieldName);
                    return null;
                }
                var definition = generic.GenericType.ToTypeSignature();
                var assembly = InteropRestoredTypeSupport.GetAssemblyName(definition);
                var existingGeneric = FindExisting(assembly, generic.GenericType.FullName);
                var source = FindSource(assembly, generic.GenericType.FullName);
                if ((existingGeneric ?? source?.Type)?.GenericParameters.Count != generic.TypeArguments.Count)
                    return "generic-field-definition-unavailable: " + generic.FullName;
                if (existingGeneric != null)
                {
                    var contract = source?.Type ?? generic.Resolve();
                    if (owner.Type.GenericParameters.Count != 0 && contract?.IsInterface == true)
                        return "managed-generic-native-field-contract: A native interface is not a CLR helper contract: " + generic.FullName;
                    if (owner.Type.GenericParameters.Count != 0 && InteropRestoredGenericSupport.ContainsGenericParameter(generic) &&
                        !(InteropDelegateContracts.CanStoreNativeDelegate(contract, existingGeneric) &&
                          generic.TypeArguments.All(argument => HasNativeDelegateStorageArgument(owner, argument))))
                        return "managed-generic-native-field-storage: An arbitrary CLR type argument cannot instantiate native field storage: " + generic.FullName;
                    var identity = GetNativeIdentityRejection(generic);
                    if (identity != null) return identity;
                    if (inline && !InteropTypeReferences.IsValueType(existingGeneric)) owner.ManagedOnlyStorage = true;
                    return null;
                }
                AddTypeDependency(owner, source, inline, fieldName);
                foreach (var argument in generic.TypeArguments)
                {
                    if (argument is PointerTypeSignature or ByReferenceTypeSignature)
                        return "invalid-generic-field-argument: " + argument.FullName;
                    // Conservatively include value arguments in the finite-layout graph.
                    var reason = AddStorageDependency(owner, argument,
                        inline && InteropTypeReferences.IsValueType(source.Type), fieldName);
                    if (reason != null) return reason;
                }
                return null;
            }
            if (type is not TypeDefOrRefSignature)
                return "unsupported-field-type: " + (type?.FullName ?? "<missing signature>");

            var assemblyName = InteropRestoredTypeSupport.GetAssemblyName(type);
            var existing = FindExisting(assemblyName, type.FullName);
            if (existing != null)
            {
                if (owner.Type.GenericParameters.Count != 0)
                {
                    var contract = FindSource(assemblyName, type.FullName)?.Type ?? type.Resolve();
                    if (contract?.IsInterface == true)
                        return "managed-generic-native-field-contract: A native interface is not a CLR helper contract: " + type.FullName;
                    if (contract?.BaseType?.FullName == "System.MulticastDelegate" &&
                        (contract.GenericParameters.Count != 0 ||
                         !InteropDelegateContracts.CanStoreNativeDelegate(contract, existing) ||
                         GetNativeIdentityRejection(type) != null))
                        return "managed-generic-native-field-contract: The delegate has no supported native storage contract: " + type.FullName;
                }
                if (inline && !InteropTypeReferences.IsValueType(existing)) owner.ManagedOnlyStorage = true;
                return null;
            }

            var dependency = FindSource(assemblyName, type.FullName);
            if (dependency == null)
                return "field-type-unavailable: " + type.FullName;
            AddTypeDependency(owner, dependency, inline, fieldName);
            return null;
        }

        private bool HasNativeDelegateStorageArgument(Entry owner, TypeSignature type)
        {
            if (!InteropDelegateContracts.CanBeArgument(type, false)) return false;
            if (type is GenericParameterSignature parameter)
            {
                if (parameter.ParameterType != GenericParameterType.Type || (uint)parameter.Index >= (uint)owner.Type.GenericParameters.Count)
                    return false;

                // A native class constraint keeps the field's type argument in the wrapper
                // hierarchy. Factories and calls still validate the closed native identity.
                return owner.Type.GenericParameters[parameter.Index].Constraints.Any(constraint =>
                    IsNativeDelegateStorageConstraint(constraint.Constraint?.ToTypeSignature()));
            }
            if (type is GenericInstanceTypeSignature instance)
                return GetNativeIdentityRejection(instance.GenericType.ToTypeSignature()) == null &&
                    instance.TypeArguments.All(argument => HasNativeDelegateStorageArgument(owner, argument));
            if (type is ArrayBaseTypeSignature array)
                return GetNativeIdentityRejection(array) == null && HasNativeDelegateStorageArgument(owner, array.BaseType);
            return GetNativeIdentityRejection(type) == null;
        }

        private bool IsNativeDelegateStorageConstraint(TypeSignature type)
        {
            if (type == null || type is GenericParameterSignature ||
                InteropRestoredGenericSupport.ContainsGenericParameter(type) ||
                InteropRestoredGenericSupport.GetIntrinsicConstraint(type) != null ||
                type.FullName is "System.Object" or "System.ValueType" or "System.Enum" or "System.Delegate" or "System.MulticastDelegate")
                return false;
            var definition = type.Resolve();
            return definition != null && !definition.IsInterface && !InteropTypeReferences.IsValueType(definition) &&
                definition.BaseType?.FullName != "System.MulticastDelegate" && GetNativeIdentityRejection(type) == null;
        }

        private static void AddTypeDependency(Entry owner, Entry dependency, bool inline, string fieldName)
        {
            if (inline)
            {
                if (InteropTypeReferences.IsValueType(dependency.Type))
                {
                    owner.PendingFields++;
                    dependency.StorageUsers.Add(owner);
                }
                else owner.ManagedOnlyStorage = true;
            }
            dependency.Users.Add((owner, "field-type-unavailable: " + owner.Type.FullName + "::" + fieldName));
        }

        private string AddConstraintDependency(Entry owner, TypeSignature type)
        {
            if (type == null) return "missing-constraint-type";
            if (InteropRestoredGenericSupport.GetIntrinsicConstraint(type) != null) return null;
            if (type is GenericParameterSignature parameter)
                return parameter.ParameterType == GenericParameterType.Type && parameter.Index < owner.Type.GenericParameters.Count
                    ? null : "unbound-constraint-parameter: " + type.FullName;
            if (type is GenericInstanceTypeSignature generic)
            {
                var genericDefinition = generic.GenericType.Resolve();
                if (genericDefinition?.GenericParameters.Count != generic.TypeArguments.Count)
                    return "generic-constraint-arity-mismatch: " + generic.FullName;
                var genericSignature = generic.GenericType.ToTypeSignature();
                var reason = AddConstraintDependency(owner, genericSignature);
                if (reason != null) return reason;
                var existing = FindExisting(InteropRestoredTypeSupport.GetAssemblyName(genericSignature),
                    generic.GenericType.FullName);
                foreach (var argument in generic.TypeArguments)
                {
                    if (existing != null && GetNativeIdentityRejection(argument) is string identityReason)
                        return identityReason;
                    reason = AddConstraintDependency(owner, argument);
                    if (reason != null) return reason;
                }
                return null;
            }
            if (type is CorLibTypeSignature primitive && (primitive.IsValueType || primitive.ElementType == ElementType.String)) return null;
            if (type is not TypeDefOrRefSignature) return "unsupported-constraint-type: " + type.FullName;
            var assemblyName = InteropRestoredTypeSupport.GetAssemblyName(type);
            var source = FindSource(assemblyName, type.FullName);
            var definition = source?.Type ?? type.Resolve();
            if (definition == null) return "unresolved-constraint-type: " + type.FullName;
            if (definition.IsInterface && source is { Existing: null })
            {
                source.Users.Add((owner, "constraint-type-unavailable: " + type.FullName));
                return null;
            }
            if (definition.IsInterface || definition.BaseType?.FullName is "System.MulticastDelegate" or "System.Delegate")
                return "native-generic-constraint-contract: " + type.FullName + ". Native interfaces/delegates are not equivalent CLR constraints.";
            if (FindExisting(assemblyName, type.FullName) != null) return null;
            if (source == null) return "unavailable-constraint-type: " + type.FullName;
            source.Users.Add((owner, "constraint-type-unavailable: " + type.FullName));
            return null;
        }

        private void PropagateManagedStorage()
        {
            var pending = new Queue<Entry>(entries.Values.Where(entry => entry.ManagedOnlyStorage));
            while (pending.TryDequeue(out var entry))
                foreach (var user in entry.StorageUsers)
                    if (!user.ManagedOnlyStorage)
                    {
                        user.ManagedOnlyStorage = true;
                        pending.Enqueue(user);
                    }
            foreach (var entry in entries.Values)
                if (entry.Existing == null && entry.ManagedOnlyStorage && entry.Type.IsExplicitLayout)
                    entry.Reject("managed-explicit-layout: CLR references cannot be placed at unverified native explicit offsets.");
            PropagateRejections();
        }

        private void ResolveDependencies()
        {
            PropagateRejections();
            // Inline fields must have finite layouts. Pointer fields do not create size dependencies.
            var ready = new Queue<Entry>(entries.Values.Where(entry => entry.Supported && entry.PendingFields == 0));
            while (ready.TryDequeue(out var entry))
            {
                entry.FiniteLayout = true;
                foreach (var user in entry.StorageUsers)
                {
                    user.PendingFields--;
                    if (user.Supported && user.PendingFields == 0)
                        ready.Enqueue(user);
                }
            }
            foreach (var entry in entries.Values)
                if (entry.Supported && entry.NeedsLayout && !entry.FiniteLayout)
                    entry.Reject("recursive-value-layout: Inline field dependencies do not have a finite size; pointer recursion is allowed.");

            PropagateRejections();
        }

        private void PropagateRejections()
        {
            var unavailable = new Queue<Entry>(entries.Values.Where(entry => !entry.Supported));
            while (unavailable.TryDequeue(out var entry))
            {
                foreach (var (user, reason) in entry.Users)
                {
                    if (!user.Supported)
                        continue;
                    user.Reject(reason + " -> " + entry.RejectionReason);
                    unavailable.Enqueue(user);
                }
            }
        }

        private TypeDefinition FindVarianceDefinition(ITypeDefOrRef type)
        {
            var assembly = InteropRestoredTypeSupport.GetAssemblyName(type.ToTypeSignature());
            return FindExisting(assembly, type.FullName) ?? FindSource(assembly, type.FullName)?.Type ?? type.Resolve();
        }

        private TypeDefinition FindExisting(string assemblyName, string fullName)
        {
            var key = (assemblyName, fullName);
            if (existingTypes.TryGetValue(key, out var existing))
                return existing;

            if (assemblyName == "UnityEngine")
            {
                foreach (var assembly in context.Assemblies)
                {
                    if (!assembly.NewAssembly.Name.StartsWith("UnityEngine"))
                        continue;
                    var candidate = assembly.TryGetTypeByName(fullName);
                    existing = candidate?.OriginalType == null ? null : candidate.NewType;
                    if (existing != null)
                        break;
                }
            }
            if (existing == null)
            {
                var direct = context.TryGetTypeByName(assemblyName, fullName);
                if (direct?.OriginalType != null)
                    existing = direct.NewType;
            }
            existingTypes.Add(key, existing);
            return existing;
        }

        private Entry FindSource(string assemblyName, string fullName)
        {
            if (assemblyName == "UnityEngine" && unityNames.TryGetValue(fullName, out var match))
                return match;
            return assemblyName != null && names.TryGetValue((assemblyName, fullName), out var entry) ? entry : null;
        }

        private static bool IsManagedGenericHelper(TypeDefinition type, out string reason)
        {
            reason = type.IsEnum || type.IsExplicitLayout
                ? "managed-generic-layout: Generic enums and explicit layouts are not supported CLR helper representations."
                : type.CustomAttributes.Any(attribute => attribute.Constructor?.DeclaringType?.FullName == "System.Runtime.CompilerServices.IsByRefLikeAttribute")
                    ? "managed-generic-byref-like: Ref-like lifetime metadata is not a plain CLR helper contract."
                : type.IsInterface || type.Interfaces.Count != 0 || type.Methods.Any(method => method.IsVirtual || method.IsAbstract)
                    ? "managed-generic-native-contract: A CLR helper cannot copy native interface or virtual dispatch contracts."
                    : type.BaseType?.FullName is not ("System.Object" or "System.ValueType")
                        ? "managed-generic-base-contract: Only object-root classes and value helpers are supported."
                        : type.Methods.Any(method => !method.HasMethodBody || method.IsPInvokeImpl ||
                            (method.ImplAttributes & MethodImplAttributes.InternalCall) != 0)
                            ? "managed-generic-native-implementation: A missing generic helper requires managed method bodies."
                            : type.GenericParameters.Any(parameter => (parameter.Attributes & GenericParameterAttributes.VarianceMask) != 0)
                                ? "managed-generic-variance: Helper classes and structs cannot declare interface/delegate variance."
                                : null;
            return reason == null;
        }

        private sealed class Entry
        {
            internal Entry(TypeDefinition type, TypeDefinition existing)
            {
                Type = type;
                Existing = existing;
                NeedsLayout = existing == null && InteropTypeReferences.IsValueType(type) && !type.IsEnum;
            }

            internal readonly TypeDefinition Type;
            internal readonly TypeDefinition Existing;
            internal Entry ManagedBase;
            internal readonly bool NeedsLayout;
            internal readonly List<Entry> StorageUsers = new();
            internal readonly List<(Entry User, string Reason)> Users = new();
            internal bool Supported => RejectionReason == null;
            internal string RejectionReason;
            internal bool FiniteLayout;
            internal bool ManagedOnlyStorage;
            internal int PendingFields;

            internal void Reject(string reason) => RejectionReason ??= reason;
        }
    }
}
