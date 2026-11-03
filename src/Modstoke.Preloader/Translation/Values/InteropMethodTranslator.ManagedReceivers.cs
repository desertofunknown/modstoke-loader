// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Collections;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private AddressOperation[] scopedParameters;
        private List<ScopedCallDraft> scopedCallDrafts;

        private sealed record ScopedCallDraft(InteropTypeContext Type, MethodDefinition Source,
            string Shape, MethodDefinition Adapter);

        private bool TryPlanManagedReferenceCall(CilInstruction instruction, IMethodDescriptor method,
            ReferenceOrigins[] consumed, out ReferenceUse use)
        {
            use = null;
            var sourceOwner = method.DeclaringType?.ToTypeSignature();
            var receiverCount = method.Signature.HasThis ? 1 : 0;
            if (instruction.OpCode.Code != CilCode.Call || method is MethodSpecification ||
                method.Signature.GenericParameterCount != 0 ||
                consumed.Length != method.Signature.ParameterTypes.Count + receiverCount ||
                !InteropDelegateContracts.HasStandardConvention(method.Signature) ||
                method.Resolve() is not { IsVirtual: false, IsAbstract: false, HasMethodBody: true } original ||
                original.IsConstructor || original.GenericParameters.Count != 0 || original.IsStatic != (receiverCount == 0) ||
                receiverCount != 0 && !original.DeclaringType.IsValueType ||
                original.DeclaringType.GenericParameters.Count !=
                    (sourceOwner is GenericInstanceTypeSignature instance ? instance.TypeArguments.Count : 0) ||
                !SignatureComparer.Default.Equals(method.Signature, original.Signature) ||
                (original.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.Synchronized)) != MethodImplAttributes.IL)
                return false;

            var type = context.TryGetAssemblyByName(original.DeclaringModule.Assembly.Name)
                ?.TryGetTypeByName(original.DeclaringType.FullName);
            var restored = type?.TryGetRestoredBody(original);
            var owner = resolver.ResolveRaw(sourceOwner);
            if (type == null || restored == null || owner?.Resolve() != type.NewType ||
                restored.IsStatic != original.IsStatic || restored.Signature.HasThis != method.Signature.HasThis ||
                restored.Signature.ParameterTypes.Count != method.Signature.ParameterTypes.Count ||
                receiverCount != 0 && (type.OriginalType == null ||
                    type.ComputedTypeSpecifics is not (InteropTypeContext.TypeSpecifics.BlittableStruct or
                        InteropTypeContext.TypeSpecifics.NonBlittableStruct)) ||
                original.Signature.ReturnType.ElementType != ElementType.Void &&
                    (!TryGetAddressElement(original.Signature.ReturnType, out var result) || !result.IsValueType))
                return false;

            var genericContext = GenericContext.FromMethod(method);
            var restoredReference = new MemberReference(owner.ToTypeDefOrRef(), restored.Name, restored.Signature);
            var restoredContext = GenericContext.FromMethod(restoredReference);
            var arguments = new char[consumed.Length];
            var hasLocation = false;
            if (receiverCount != 0)
            {
                if (!TryGetReferenceLocation(consumed[0], out var location, out var readOnly) ||
                    !CanPassManagedLocation(location) || !SignatureComparer.Default.Equals(location.SourceElement, sourceOwner))
                    return false;
                arguments[0] = ManagedLocationShape(location, readOnly);
                hasLocation = true;
            }
            for (var index = 0; index < method.Signature.ParameterTypes.Count; index++)
            {
                var parameter = method.Signature.ParameterTypes[index].InstantiateGenericTypes(genericContext);
                var reference = parameter as ByReferenceTypeSignature;
                if (!TryGetAddressElement(reference?.BaseType ?? parameter, out var element) ||
                    !SignatureComparer.Default.Equals(restored.Parameters[index].ParameterType.InstantiateGenericTypes(restoredContext),
                        reference == null ? element : element.MakeByReferenceType()))
                    return false;
                if (consumed[index + receiverCount] is { } origins)
                {
                    if (reference == null || !TryGetReferenceLocation(origins, out var argument, out var argumentReadOnly) ||
                        !CanPassManagedLocation(argument) ||
                        !SignatureComparer.Default.Equals(argument.SourceElement, reference.BaseType))
                        return false;
                    arguments[index + receiverCount] = ManagedLocationShape(argument, argumentReadOnly);
                    hasLocation = true;
                }
                else
                {
                    if (!NativeValueArgumentMatches(instruction, method.Signature.ParameterTypes.Count - index - 1, parameter))
                        return false;
                    arguments[index + receiverCount] = 'M';
                }
            }
            if (!hasLocation) return false;

            var adapter = GetManagedReferenceAdapter(original, restored, type, new string(arguments));
            if (adapter == null) return false;
            use = new ReferenceUse { Call = new ReferenceCall
            {
                ManagedAdapter = new MemberReference(owner.ToTypeDefOrRef(), adapter.Name, adapter.Signature)
            } };
            return true;
        }

        private MethodDefinition GetManagedReferenceAdapter(MethodDefinition original,
            MethodDefinition restored, InteropTypeContext type, string shape)
        {
            type.ScopedReferenceMethods ??= new();
            var key = (original, shape);
            if (type.ScopedReferenceMethods.TryGetValue(key, out var existing)) return existing;
            if (scopedCallDrafts != null)
                foreach (var draft in scopedCallDrafts)
                    if (draft.Type == type && draft.Source == original && draft.Shape == shape)
                        return draft.Adapter;

            var drafts = scopedCallDrafts ?? new List<ScopedCallDraft>();
            var start = drafts.Count;
            var translated = false;
            try
            {
                var adapter = TranslateManagedReferenceCall(original, restored, type, shape, drafts);
                if (adapter == null) return null;
                // Recursive edges may use declared signatures, but no completed cache entry
                // can depend on a body until the entire root translation has succeeded.
                if (scopedCallDrafts == null)
                    foreach (var draft in drafts)
                        draft.Type.ScopedReferenceMethods[(draft.Source, draft.Shape)] = draft.Adapter;
                translated = true;
                return adapter;
            }
            finally
            {
                if (!translated)
                {
                    for (var index = drafts.Count - 1; index >= start; index--)
                    {
                        var draft = drafts[index];
                        draft.Type.NewType.Methods.Remove(draft.Adapter);
                        drafts.RemoveAt(index);
                    }
                    type.ScopedReferenceMethods[key] = null;
                }
            }
        }

        private bool CanPassManagedLocation(AddressOperation location) =>
            (IsBlittableAddressValue(location) || IsNativeAggregateLocation(location)) &&
            (location.IsScopedParameter || location.IsArray ||
                (location.OwnerType != null || location.StaticOwnerType != null) && location.NativeFieldPointerName != null);

        private char ManagedLocationShape(AddressOperation location, bool readOnly)
        {
            if (!IsNativeAggregateLocation(location)) return readOnly ? 'R' : 'N';
            var shape = IsDirectNativeField(location) ? 'F' : CanWriteNativeAggregate(location) ? 'A' : 'S';
            return readOnly ? char.ToLowerInvariant(shape) : shape;
        }

        private static AddressOperation ScopedParameter(TypeSignature source, TypeSignature element, int index, char shape) => new()
        {
            SourceElement = source,
            Element = element,
            ScopedParameterIndex = index,
            ReadOnly = shape == 'R' || char.IsLower(shape),
            // Copy/initialization rights belong to the original storage, not the carrier slot.
            ScopedAggregateRoot = shape is 'F' or 'f' or 'A' or 'a',
            ScopedAggregateField = shape is 'F' or 'f'
        };

        private static MethodDefinition TranslateManagedReferenceCall(MethodDefinition original,
            MethodDefinition restored, InteropTypeContext type, string arguments, List<ScopedCallDraft> drafts)
        {
            // Native locations remain descriptors; ordinary values and CLR refs keep their
            // signatures. Translation still owns every read, write, copy and escape check.
            var imports = type.AssemblyContext.Imports;
            var receiverCount = original.Signature.HasThis ? 1 : 0;
            var name = (receiverCount == 0 ? "__ModstokeScopedCall_" : "__ModstokeScopedReceiver_") +
                original.MetadataToken.ToInt32().ToString("X8") +
                "_" + arguments;
            while (type.NewType.Methods.Any(method => method.Name == name)) name += "_";
            var adapter = new MethodDefinition(name,
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                MethodSignature.CreateStatic(restored.Signature.ReturnType))
            {
                CilMethodBody = new CilMethodBody { InitializeLocals = original.CilMethodBody.InitializeLocals }
            };
            var locationType = imports.Module.DefaultImporter.ImportTypeSignature(typeof(InteropReferenceLocation));
            var locations = new AddressOperation[arguments.Length];
            if (receiverCount != 0)
            {
                InteropMethodDeclarations.AddParameter(adapter, locationType, "receiver", default);
                locations[0] = ScopedParameter(SourceDeclaringType(original.DeclaringType),
                    type.SelfSubstitutedRef.ToTypeSignature(), 0, arguments[0]);
            }
            for (var index = 0; index < restored.Parameters.Count; index++)
            {
                var parameter = restored.Parameters[index];
                var slot = index + receiverCount;
                var native = arguments[slot] != 'M';
                var copy = InteropMethodDeclarations.AddParameter(adapter,
                    native ? locationType : parameter.ParameterType, parameter.Name,
                    native ? default : parameter.Definition?.Attributes ?? default);
                if (!native)
                {
                    copy.Definition.Constant = parameter.Definition?.Constant;
                    continue;
                }
                locations[slot] = ScopedParameter(
                    ((ByReferenceTypeSignature)original.Parameters[index].ParameterType).BaseType,
                    ((ByReferenceTypeSignature)parameter.ParameterType).BaseType, slot, arguments[slot]);
            }
            type.NewType.Methods.Add(adapter);
            drafts.Add(new ScopedCallDraft(type, original, arguments, adapter));
            foreach (var instruction in original.CilMethodBody.Instructions)
            {
                if (instruction.Operand is not Parameter parameter) continue;
                var index = parameter.MethodSignatureIndex;
                if (index < 0 || index >= locations.Length || locations[index] != null &&
                    instruction.OpCode.Code is not (CilCode.Ldarg or CilCode.Ldarg_S))
                    return null;
            }
            var translator = new InteropMethodTranslator(adapter, type, imports)
            {
                scopedParameters = locations,
                scopedCallDrafts = drafts
            };
            return translator.Translate(original.CilMethodBody) ? adapter : null;
        }
    }
}
