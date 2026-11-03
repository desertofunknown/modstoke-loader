// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool TryPlanNativeMappedReferenceCall(CilInstruction instruction, IMethodDescriptor method,
            ReferenceOrigins[] consumed, out ReferenceUse use)
        {
            use = null;
            var signature = method.Signature;
            var specification = method as MethodSpecification;
            if (instruction.OpCode.Code is not (CilCode.Call or CilCode.Callvirt) ||
                !InteropDelegateContracts.HasStandardConvention(signature) ||
                (specification == null ? signature.GenericParameterCount != 0 :
                    specification.Signature == null || signature.GenericParameterCount == 0 ||
                    specification.Signature.TypeArguments.Count != signature.GenericParameterCount))
                return false;
            var sourceOwner = method.DeclaringType?.ToTypeSignature();
            var owner = resolver.ResolveRaw(sourceOwner);
            var definition = owner?.Resolve();
            var sourceDefinition = method.Resolve();
            var type = sourceDefinition == null ? FindMappedSourceOwner(sourceOwner) :
                context.TryGetAssemblyByName(sourceDefinition.DeclaringModule.Assembly.Name)
                    ?.TryGetTypeByName(sourceDefinition.DeclaringType.FullName);
            if (definition == null || type == null || type.NewType != definition ||
                definition.GenericParameters.Count != 0)
                return false;
            // Facade assemblies can be absent even when the native mapping is complete.
            // Use the same unique full-signature match as ordinary calls, never name/arity alone.
            var mapped = sourceDefinition == null ? FindMappedNativeMethod(method, type) :
                type.TryGetMethodByUnityAssemblyMethod(sourceDefinition);
            var original = sourceDefinition ?? mapped?.OriginalMethod;
            if (original is not { IsConstructor: false, IsVirtual: false, IsAbstract: false } ||
                original.DeclaringType.GenericParameters.Count != 0 || original.IsStatic != !signature.HasThis ||
                specification != null && !original.IsStatic ||
                original.IsStatic && instruction.OpCode.Code != CilCode.Call ||
                !original.IsStatic && (InteropTypeReferences.IsValueType(sourceOwner) || !IsNativeArrayReference(owner)))
                return false;
            var count = signature.ParameterTypes.Count;
            var offset = original.IsStatic ? 0 : 1;
            if (mapped?.NonGenericMethodInfoPointerField == null ||
                (specification == null ? mapped.Rva == 0 : mapped.GenericInstantiationsStoreSelfSubstMethodRef == null) ||
                mapped.OriginalMethod.IsStatic != original.IsStatic || mapped.OriginalMethod.IsConstructor ||
                mapped.OriginalMethod.IsVirtual || mapped.OriginalMethod.IsAbstract ||
                mapped.OriginalMethod.Signature.GenericParameterCount != signature.GenericParameterCount ||
                mapped.NewMethod.IsStatic != original.IsStatic || mapped.NewMethod.Parameters.Count != count ||
                consumed.Length != count + offset ||
                offset != 0 && (consumed[0] != null || !NativeValueArgumentMatches(instruction, count, sourceOwner)))
                return false;
            var call = new ReferenceCall
            {
                Owner = owner,
                MappedMethod = mapped,
                Parameters = new TypeSignature[count],
                References = new bool[count],
                NativeReferences = new bool[count]
            };
            IMethodDescriptor rewritten = mapped.NewMethod;
            if (specification != null)
            {
                call.MethodArguments = new TypeSignature[specification.Signature.TypeArguments.Count];
                for (var index = 0; index < call.MethodArguments.Length; index++)
                {
                    var argument = specification.Signature.TypeArguments[index];
                    if (!HasScopedMethodArgument(argument)) return false;
                    call.MethodArguments[index] = resolver.Resolve(argument);
                    if (call.MethodArguments[index] == null) return false;
                }
                rewritten = mapped.NewMethod.MakeGenericInstanceMethod(call.MethodArguments);
                if (!ValidateManagedMethodUse(mapped.NewMethod, rewritten)) return false;
            }
            var sourceContext = GenericContext.FromMethod(method);
            var targetContext = GenericContext.FromMethod(rewritten);
            var result = signature.ReturnType.InstantiateGenericTypes(sourceContext);
            if (result.ElementType != ElementType.Void && !TryGetCallElement(result, out call.Result)) return false;
            if (!SignatureComparer.Default.Equals(mapped.NewMethod.Signature.ReturnType.InstantiateGenericTypes(targetContext),
                    call.Result ?? imports.Module.CorLibTypeFactory.Void)) return false;
            var hasNativeLocation = false;
            for (var index = 0; index < count; index++)
            {
                var parameter = signature.ParameterTypes[index].InstantiateGenericTypes(sourceContext);
                var reference = parameter as ByReferenceTypeSignature;
                if (!TryGetCallElement(reference?.BaseType ?? parameter, out call.Parameters[index])) return false;
                call.References[index] = reference != null;
                var expected = reference == null ? call.Parameters[index] : call.Parameters[index].MakeByReferenceType();
                var mappedParameter = mapped.NewMethod.Parameters[index].ParameterType.InstantiateGenericTypes(targetContext);
                var origins = consumed[index + offset];
                if (!SignatureComparer.Default.Equals(mappedParameter, expected) &&
                    !(origins != null && specification != null && reference?.BaseType is SzArrayTypeSignature array &&
                        mappedParameter is ByReferenceTypeSignature { BaseType: GenericInstanceTypeSignature mappedArray } &&
                        SignatureComparer.Default.Equals(mappedArray.GenericType, imports.Il2CppArrayBase.ToTypeDefOrRef()) &&
                        mappedArray.TypeArguments.Count == 1 &&
                        SignatureComparer.Default.Equals(mappedArray.TypeArguments[0], resolver.Resolve(array.BaseType))))
                    return false;
                // Only native slot descriptors bypass the CLR array-wrapper distinction; CLR refs stay invariant.
                if (origins != null)
                {
                    if (reference == null ||
                        !TryGetWritableCallLocation(origins, reference.BaseType, out _,
                            allowAggregate: true, allowReferenceSlots: true, allowStatic: true))
                        return false;
                    call.NativeReferences[index] = true;
                    hasNativeLocation = true;
                }
                else
                {
                    if (!NativeValueArgumentMatches(instruction, count - index - 1, parameter)) return false;
                    if (parameter is GenericParameterSignature generic &&
                        TryGetStackValue(instruction, count - index - 1, out var argument) && argument.IsNull &&
                        !IsReferenceGeneric(generic)) return false;
                }
            }
            if (!hasNativeLocation) return false;
            use = new ReferenceUse { Call = call };
            return true;

            bool TryGetCallElement(TypeSignature source, out TypeSignature element)
            {
                element = null;
                if (specification == null) return TryGetAddressElement(source, out element);
                if (!HasScopedMethodArgument(source)) return false;
                if (source is not GenericParameterSignature) return TryGetAddressElement(source, out element);
                // The existing per-instantiation store validates native identity before any
                // reference is borrowed; typed argument/result helpers select the closed layout.
                element = resolver.Resolve(source);
                return element is GenericParameterSignature && SignatureComparer.Default.Equals(element, source);
            }
        }

        private bool HasScopedMethodArgument(TypeSignature argument)
        {
            if (!InteropDelegateContracts.CanBeArgument(argument, false) || argument is PinnedTypeSignature ||
                InteropRestorationPlan.For(context).ContainsManagedOnlyStorage(argument) ||
                InteropRestorationPlan.For(context).GetNativeIdentityRejection(argument) != null)
                return false;
            if (argument is GenericParameterSignature generic)
            {
                var methodParameter = generic.ParameterType == GenericParameterType.Method;
                if (!methodParameter && generic.ParameterType != GenericParameterType.Type) return false;
                var original = methodParameter ? sourceMethod.GenericParameters : sourceMethod.DeclaringType.GenericParameters;
                var rewritten = methodParameter ? target.GenericParameters : ownerType.NewType.GenericParameters;
                return (uint)generic.Index < (uint)original.Count && (uint)generic.Index < (uint)rewritten.Count;
            }
            if (argument is GenericInstanceTypeSignature instance)
                return instance.TypeArguments.All(HasScopedMethodArgument);
            if (argument is ArrayBaseTypeSignature array)
                return HasScopedMethodArgument(array.BaseType);
            return argument is not TypeSpecificationSignature;
        }
    }
}
