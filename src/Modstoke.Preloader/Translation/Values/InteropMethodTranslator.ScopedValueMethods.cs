// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool TryPlanNativeValueReferenceCall(CilInstruction instruction, IMethodDescriptor method,
            ReferenceOrigins[] consumed, out ReferenceUse use)
        {
            use = null;
            var signature = method.Signature;
            var sourceOwner = method.DeclaringType?.ToTypeSignature();
            if (instruction.OpCode.Code != CilCode.Call || method is MethodSpecification ||
                !InteropDelegateContracts.HasStandardConvention(signature) || !signature.HasThis ||
                signature.GenericParameterCount != 0 ||
                !TryGetNativeValueWrapper(sourceOwner, out var owner) ||
                method.Resolve() is not { IsConstructor: false, IsStatic: false, IsVirtual: false, IsAbstract: false } original ||
                consumed.Length != signature.ParameterTypes.Count + 1) return false;
            var mappedType = context.GetContextForNewType(owner.Resolve());
            var mapped = mappedType.TryGetMethodByUnityAssemblyMethod(original);
            if (mapped?.NonGenericMethodInfoPointerField == null ||
                mapped.Rva == 0 && sourceOwner is not GenericInstanceTypeSignature ||
                mapped.OriginalMethod.IsConstructor || mapped.OriginalMethod.IsStatic || mapped.OriginalMethod.IsVirtual ||
                mapped.OriginalMethod.IsAbstract || mapped.OriginalMethod.Signature.GenericParameterCount != 0 ||
                mapped.NewMethod.Signature.ParameterTypes.Count != signature.ParameterTypes.Count) return false;
            var genericContext = GenericContext.FromMethod(method);
            var result = signature.ReturnType.InstantiateGenericTypes(genericContext);
            var call = new ReferenceCall
            {
                Owner = owner,
                ValueMethod = mapped,
                Parameters = new TypeSignature[signature.ParameterTypes.Count],
                References = new bool[signature.ParameterTypes.Count],
                NativeReferences = new bool[signature.ParameterTypes.Count]
            };
            if (result.ElementType != ElementType.Void && !TryGetAddressElement(result, out call.Result)) return false;
            if (consumed[0] != null)
            {
                if (!TryGetWritableCallLocation(consumed[0], sourceOwner, out _, allowAggregate: true)) return false;
                call.ReferenceReceiver = true;
            }
            else
            {
                if (!TryGetStackValue(instruction, call.Parameters.Length, out var receiver) ||
                    receiver.Type is not ByReferenceTypeSignature reference ||
                    !SignatureComparer.Default.Equals(reference.BaseType, sourceOwner)) return false;
                call.Receiver = owner;
            }
            for (var index = 0; index < call.Parameters.Length; index++)
            {
                var parameter = signature.ParameterTypes[index].InstantiateGenericTypes(genericContext);
                var reference = parameter as ByReferenceTypeSignature;
                if (!TryGetAddressElement(reference?.BaseType ?? parameter, out call.Parameters[index])) return false;
                call.References[index] = reference != null;
                var origins = consumed[index + 1];
                if (origins != null)
                {
                    if (reference == null || !TryGetWritableCallLocation(origins, reference.BaseType, out _, allowAggregate: true))
                        return false;
                    call.NativeReferences[index] = true;
                }
                else if (!NativeValueArgumentMatches(instruction, call.Parameters.Length - index - 1, parameter))
                {
                    if (reference != null || !CanWrapNativeInterfaceArgument(instruction,
                        call.Parameters.Length - index - 1, parameter, call.Parameters[index]))
                        return false;
                    call.NativeInterfaceArguments ??= new bool[call.Parameters.Length];
                    call.NativeInterfaceArguments[index] = true;
                }
            }
            use = new ReferenceUse { Call = call };
            return true;
        }

        private bool CanWrapNativeInterfaceArgument(CilInstruction instruction, int depth,
            TypeSignature sourceParameter, TypeSignature parameter)
        {
            if (FindMappedSourceOwner(sourceParameter) is not { OriginalType.IsInterface: true } contract ||
                contract.NewType != parameter.Resolve() || !IsNativeArrayReference(parameter) ||
                !TryGetStackValue(instruction, depth, out var argument) || argument.IsNull ||
                InteropTypeReferences.IsValueType(argument.Type) ||
                !TryGetAddressElement(argument.Type, out var value) || value.IsValueType ||
                !IsNativeArrayReference(value))
                return false;
            // The native cast checks the actual closed interface contract. CLR wrapper
            // inheritance alone cannot express an IL2CPP class's interface implementation.
            return true;
        }
    }
}
