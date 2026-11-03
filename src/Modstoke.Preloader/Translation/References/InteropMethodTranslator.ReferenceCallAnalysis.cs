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
        private sealed class ReferenceCall
        {
            public ReferenceAtomicCall Atomic;
            public AddressOperation Int32ToStringLocation;
            public AddressOperation IntPtrToInt32Location;
            public MethodDefinition IntPtrToInt32Method;
            public TypeSignature Owner;
            public string MethodPointerName;
            public TypeSignature Result;
            public TypeSignature Receiver;
            public TypeSignature ConstrainedReceiver;
            public bool ReferenceReceiver;
            public bool VirtualDispatch;
            public bool Constructor;
            public bool CreatesReceiver;
            public InteropMethodContext ValueMethod;
            public InteropMethodContext MappedMethod;
            public TypeSignature[] MethodArguments;
            public InteropInternalCallContext InternalCall;
            public IMethodDefOrRef ManagedAdapter;
            public bool[] NativeReferences;
            public bool[] NativeInterfaceArguments;
            public TypeSignature[] Parameters;
            public bool[] References;
        }

        private bool TryPlanReferenceCall(CilInstruction instruction, ReferenceOrigins[] consumed, out ReferenceUse use)
        {
            use = null;
            if (TryPlanAtomicReferenceCall(instruction, consumed, out use)) return true;
            if (instruction.Operand is not IMethodDescriptor method || method.Signature == null)
                return false;
            if (referenceProducers.TryGetValue(instruction, out var returned) && returned.ReturnedOwnerField != null)
                return TryPlanFieldReturn(method, consumed, returned, out use);
            if (TryPlanScopedInterfaceCall(instruction, method, consumed, out use)) return true;
            if (method is MethodSpecification)
                return TryPlanNativeMappedReferenceCall(instruction, method, consumed, out use);
            if (method.Signature.GenericParameterCount != 0) return false;
            if (TryPlanInt32ReferenceToString(instruction, method, consumed, out use))
                return true;
            if (TryPlanIntPtrReferenceToInt32(instruction, method, consumed, out use))
                return true;
            if (TryPlanInternalReferenceCall(instruction, method, consumed, out use))
                return true;
            if (method.Name == ".ctor")
                return TryPlanNativeConstructorReferenceCall(instruction, method, consumed, out use);
            if (TryPlanNativeFieldGetter(instruction, method, consumed, out use)) return true;
            if (TryPlanNativeValueReferenceCall(instruction, method, consumed, out use)) return true;
            if (TryPlanNativeMappedReferenceCall(instruction, method, consumed, out use)) return true;
            if (TryPlanForwardedInternalReferenceCall(instruction, method, consumed, out use)) return true;
            if (TryPlanManagedReferenceCall(instruction, method, consumed, out use)) return true;
            var original = method.Resolve();
            var owner = resolver.ResolveRaw(method.DeclaringType?.ToTypeSignature());
            var definition = owner?.Resolve();
            if (original == null || original.IsConstructor || definition == null || definition.GenericParameters.Count != 0)
                return false;
            var mappedType = context.TryGetAssemblyByName(original.DeclaringModule.Assembly.Name)
                ?.TryGetTypeByName(original.DeclaringType.FullName);
            var mapped = mappedType?.NewType == definition && mappedType.OriginalType != null
                ? mappedType.TryGetMethodByUnityAssemblyMethod(original) : null;
            if (mapped?.NonGenericMethodInfoPointerField == null || mapped.Rva == 0 ||
                mapped.OriginalMethod.Signature.GenericParameterCount != 0)
                return false;

            var offset = method.Signature.HasThis ? 1 : 0;
            var count = method.Signature.ParameterTypes.Count;
            if (consumed.Length != count + offset || original.IsStatic != (offset == 0)) return false;
            var plan = new ReferenceCall
            {
                Owner = owner,
                MethodPointerName = mapped.NonGenericMethodInfoPointerField.Name,
                Parameters = new TypeSignature[count],
                References = new bool[count]
            };
            if (method.Signature.ReturnType.ElementType != ElementType.Void &&
                !TryGetAddressElement(method.Signature.ReturnType, out plan.Result))
                return false;
            if (offset != 0)
            {
                if (consumed[0] != null)
                {
                    if (instruction.OpCode.Code != CilCode.Call || original.IsVirtual ||
                        !TryGetWritableCallLocation(consumed[0], method.DeclaringType.ToTypeSignature(), out _))
                        return false;
                    plan.ReferenceReceiver = true;
                }
                else
                {
                    // A value receiver must remain its actual storage, never a boxed copy.
                    if (InteropTypeReferences.IsValueType(method.DeclaringType) ||
                        !IsNativeArrayReference(owner) ||
                        !TryGetStackValue(instruction, consumed.Length - 1, out var receiver) ||
                        receiver.Type is ByReferenceTypeSignature ||
                        !receiver.IsNull && !SignatureComparer.Default.Equals(resolver.Resolve(receiver.Type), owner))
                        return false;
                    plan.Receiver = owner;
                    plan.VirtualDispatch = instruction.OpCode.Code == CilCode.Callvirt && original.IsVirtual;
                }
            }
            for (var index = 0; index < count; index++)
            {
                var parameter = method.Signature.ParameterTypes[index];
                var origins = consumed[index + offset];
                if (parameter is ByReferenceTypeSignature reference)
                {
                    if (!TryGetWritableCallLocation(origins, reference.BaseType, out _)) return false;
                    plan.References[index] = true;
                }
                else
                {
                    if (origins != null || !TryGetAddressElement(parameter, out var rewritten) ||
                        !TryGetStackValue(instruction, count - index - 1, out var argument) ||
                        argument.IsNull && InteropTypeReferences.IsValueType(parameter) ||
                        !argument.IsNull && !SignatureComparer.Default.Equals(argument.Type, parameter) &&
                        !(ArrayStorageType(parameter) != null &&
                          SameStackValue(NumericStackValue(argument.Type), NumericStackValue(parameter))))
                        return false;
                    plan.Parameters[index] = rewritten;
                }
            }
            use = new ReferenceUse { Call = plan };
            return true;
        }

        private bool TryPlanInt32ReferenceToString(CilInstruction instruction, IMethodDescriptor method,
            ReferenceOrigins[] consumed, out ReferenceUse use)
        {
            use = null;
            var signature = method.Signature;
            var sourceInt32 = sourceMethod.DeclaringModule.CorLibTypeFactory.Int32;
            // Unity's netstandard facade need not be present in the metadata resolver.
            // Match the intrinsic's source corlib identity before native method resolution.
            if (instruction.OpCode.Code != CilCode.Call || method.Name != "ToString" ||
                method.DeclaringType?.ToTypeSignature() is not CorLibTypeSignature { ElementType: ElementType.I4 } ||
                !SignatureComparer.Default.Equals(method.DeclaringType, sourceInt32.Type) ||
                !InteropDelegateContracts.HasStandardConvention(signature) || !signature.HasThis ||
                signature.GenericParameterCount != 0 || signature.ParameterTypes.Count != 0 ||
                signature.ReturnType is not CorLibTypeSignature { ElementType: ElementType.String } ||
                !SignatureComparer.Default.Equals(signature.ReturnType.ToTypeDefOrRef(),
                    sourceMethod.DeclaringModule.CorLibTypeFactory.String.Type) ||
                consumed.Length != 1 || !TryGetReferenceLocation(consumed[0], out var location, out _) ||
                location.SourceElement is not CorLibTypeSignature { ElementType: ElementType.I4 } ||
                !SignatureComparer.Default.Equals(location.SourceElement.ToTypeDefOrRef(), sourceInt32.Type) ||
                location.Element is not CorLibTypeSignature { ElementType: ElementType.I4 } ||
                !SignatureComparer.Default.Equals(location.Element, imports.Module.CorLibTypeFactory.Int32) ||
                !location.UsesNativeValueAccess && !location.IsArray && location.Getter == null)
                return false;

            // Only this exact read-only primitive operation may run on a value snapshot.
            use = new ReferenceUse { Call = new ReferenceCall { Int32ToStringLocation = location } };
            return true;
        }

        private bool TryGetWritableCallLocation(ReferenceOrigins origins, TypeSignature element,
            out AddressOperation location, bool allowAggregate = false, bool allowReferenceSlots = false, bool allowStatic = false)
        {
            // Neither a value receiver nor a ref argument promises that native code only reads.
            return TryGetReferenceLocation(origins, out location, out var readOnly) && !readOnly &&
                SignatureComparer.Default.Equals(location.SourceElement, element) &&
                ((allowAggregate ? IsNativeStorageValue(location) : IsBlittableAddressValue(location)) ||
                    allowReferenceSlots && IsNativeReferenceValue(location)) &&
                (location.IsScopedParameter || location.IsArray || (location.OwnerType != null || allowStatic && location.StaticOwnerType != null) &&
                    location.NativeFieldPointerName != null);
        }
    }
}
