// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private readonly Dictionary<CilInstruction, IMethodDescriptor> constrainedValueCalls = new();

        private bool TryPlanConstrainedValueCall(CilInstruction prefix, TypeSignature receiver,
            IMethodDescriptor slot, out IMethodDescriptor implementation)
        {
            implementation = null;
            var sourceType = receiver.Resolve();
            if (sourceType is { IsValueType: false } or { IsEnum: true } ||
                receiver is not (TypeDefOrRefSignature or GenericInstanceTypeSignature) ||
                InteropRestorationPlan.For(context).GetNativeIdentityRejection(receiver) != null ||
                sourceType != null && sourceType.GenericParameters.Count !=
                    (receiver is GenericInstanceTypeSignature instance ? instance.TypeArguments.Count : 0) || slot is MethodSpecification ||
                slot.DeclaringType?.ToTypeSignature() is not (TypeDefOrRefSignature or
                    CorLibTypeSignature { ElementType: ElementType.Object }) ||
                !InteropDelegateContracts.HasStandardConvention(slot.Signature) ||
                !slot.Signature.HasThis || slot.Signature.GenericParameterCount != 0 ||
                !TryGetStackValue(prefix, slot.Signature.ParameterTypes.Count, out var value) ||
                value.Type is not ByReferenceTypeSignature address ||
                !SignatureComparer.Default.Equals(address.BaseType, receiver))
                return false;

            var rewrittenType = resolver.ResolveRaw(receiver)?.Resolve();
            var rewrittenOwner = resolver.ResolveRaw(slot.DeclaringType.ToTypeSignature())?.Resolve();
            if (rewrittenType == null || rewrittenOwner == null) return false;
            var type = context.GetContextForNewType(rewrittenType);
            var slotOwner = context.GetContextForNewType(rewrittenOwner);
            var interfaceSlot = slotOwner.OriginalType?.IsInterface == true;
            var objectSlot = InteropRestoredGenericSupport.GetIntrinsicConstraint(slot.DeclaringType.ToTypeSignature()) == typeof(object) &&
                ReferenceEquals(slotOwner.OriginalType, context.CorLib.GetTypeByName("System.Object").OriginalType);
            var boxed = TryGetNativeValueWrapper(receiver, out _);
            if (type.OriginalType is not { IsValueType: true, IsEnum: false } ||
                type.OriginalType.GenericParameters.Count !=
                    (receiver is GenericInstanceTypeSignature nativeInstance ? nativeInstance.TypeArguments.Count : 0) ||
                !(rewrittenType.IsValueType && type.ComputedTypeSpecifics == InteropTypeContext.TypeSpecifics.BlittableStruct || boxed) ||
                slotOwner.OriginalType == null || slotOwner.OriginalType.GenericParameters.Count != 0 ||
                !interfaceSlot && !objectSlot ||
                interfaceSlot && sourceType != null &&
                    !sourceType.Interfaces.Any(entry => SignatureComparer.Default.Equals(entry.Interface, slot.DeclaringType)))
                return false;

            var returnType = resolver.Resolve(slot.Signature.ReturnType);
            if (returnType == null) return false;
            var signature = MethodSignature.CreateInstance(returnType);
            foreach (var parameter in slot.Signature.ParameterTypes)
            {
                var rewritten = resolver.Resolve(parameter);
                if (rewritten == null) return false;
                signature.ParameterTypes.Add(rewritten);
            }
            var nativeSlots = slotOwner.Methods.Where(candidate => candidate.OriginalMethod.Name == slot.Name &&
                SignatureComparer.Default.Equals(candidate.NewMethod.Signature, signature)).ToArray();
            if (nativeSlots.Length != 1) return false;
            if (interfaceSlot &&
                !type.OriginalType.Interfaces.Any(entry => SignatureComparer.Default.Equals(entry.Interface, slotOwner.OriginalType)))
                return false;
            var nativeImplementation = interfaceSlot ?
                FindDirectInterfaceImplementation(type.OriginalType, nativeSlots[0].OriginalMethod) :
                FindDirectObjectOverride(type.OriginalType, nativeSlots[0].OriginalMethod);
            var mapped = nativeImplementation == null ? null : type.TryGetMethodByOldMethod(nativeImplementation);
            if (mapped?.NonGenericMethodInfoPointerField == null ||
                mapped.Rva == 0 && receiver is not GenericInstanceTypeSignature ||
                mapped.OriginalMethod.IsAbstract || mapped.OriginalMethod.Signature.GenericParameterCount != 0)
                return false;

            MethodDefinition sourceImplementation;
            if (sourceType != null)
            {
                sourceImplementation = interfaceSlot ? FindDirectInterfaceImplementation(sourceType, slot) :
                    FindDirectObjectOverride(sourceType, slot);
                if (sourceImplementation == null || type.TryGetMethodByUnityAssemblyMethod(sourceImplementation) != mapped)
                    return false;
            }
            else
            {
                // A facade may omit the value definition. Bind its concrete implementation
                // from native metadata, then require the normal full-signature mapping too.
                var reference = new MemberReference(receiver.ToTypeDefOrRef(),
                    nativeImplementation.Name, nativeImplementation.Signature);
                if (FindMappedSourceOwner(receiver) != type || FindMappedNativeMethod(reference, type) != mapped)
                    return false;
                implementation = reference;
                return true;
            }

            // Blittable wrappers retain the CLR byref. Boxed wrappers resolve that location to
            // its existing native payload at the call, preserving mutations even when it throws.
            implementation = receiver is GenericInstanceTypeSignature
                ? new MemberReference(receiver.ToTypeDefOrRef(), sourceImplementation.Name, sourceImplementation.Signature)
                : sourceImplementation;
            return true;
        }

        private static MethodDefinition FindDirectObjectOverride(TypeDefinition type, IMethodDescriptor slot)
        {
            // Only a declared override can receive the original value address. Inherited
            // Object/ValueType implementations require boxing and remain a separate path.
            if ((string)slot.Name is not ("ToString" or "GetHashCode" or "Equals")) return null;
            var candidates = type.Methods.Where(method => method.IsPublic && method.IsVirtual && !method.IsNewSlot &&
                !method.IsStatic && !method.IsAbstract && method.Name == slot.Name &&
                SignatureComparer.Default.Equals(method.Signature, slot.Signature)).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }

        private static MethodDefinition FindDirectInterfaceImplementation(TypeDefinition type, IMethodDescriptor slot)
        {
            var overrides = type.MethodImplementations.Where(entry =>
                SignatureComparer.Default.Equals(entry.Declaration, slot)).ToArray();
            if (overrides.Length != 0)
            {
                if (overrides.Length != 1) return null;
                var body = overrides[0].Body?.Resolve();
                return body?.DeclaringType == type && !body.IsStatic && !body.IsAbstract &&
                    SignatureComparer.Default.Equals(body.Signature, slot.Signature) ? body : null;
            }

            var candidates = type.Methods.Where(method => method.IsPublic && method.IsVirtual &&
                !method.IsStatic && !method.IsAbstract && method.Name == slot.Name &&
                SignatureComparer.Default.Equals(method.Signature, slot.Signature)).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }
    }
}
