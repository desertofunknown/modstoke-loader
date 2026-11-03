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
        private bool TryGetNativeValueConstructor(IMethodDescriptor method, out TypeSignature owner,
            out InteropMethodContext mapped)
        {
            owner = null;
            mapped = null;
            var signature = method.Signature;
            var sourceOwner = method.DeclaringType?.ToTypeSignature();
            if (method is MethodSpecification || method.Name != ".ctor" ||
                !InteropDelegateContracts.HasStandardConvention(signature) || !signature.HasThis ||
                signature.GenericParameterCount != 0 || signature.ReturnType.ElementType != ElementType.Void ||
                sourceOwner == null || !InteropTypeReferences.IsValueType(sourceOwner) ||
                !TryGetAddressElement(sourceOwner, out owner))
                return false;
            var source = method.Resolve();
            var definition = owner.Resolve();
            if (source is not { IsConstructor: true, IsStatic: false, IsAbstract: false } || definition == null ||
                definition.GenericParameters.Count !=
                    (sourceOwner is GenericInstanceTypeSignature instance ? instance.TypeArguments.Count : 0))
                return false;
            var type = context.GetContextForNewType(definition);
            if (type.OriginalType == null || type.ComputedTypeSpecifics is not
                (InteropTypeContext.TypeSpecifics.BlittableStruct or InteropTypeContext.TypeSpecifics.NonBlittableStruct))
                return false;
            mapped = type.TryGetMethodByUnityAssemblyMethod(source);
            if (mapped?.NonGenericMethodInfoPointerField == null ||
                mapped.Rva == 0 && sourceOwner is not GenericInstanceTypeSignature ||
                !mapped.OriginalMethod.IsConstructor || mapped.OriginalMethod.IsStatic ||
                mapped.OriginalMethod.Signature.GenericParameterCount != 0 ||
                mapped.OriginalMethod.Signature.ReturnType.ElementType != ElementType.Void)
                return false;
            return true;
        }

        private bool TryCreateNativeValueConstructor(IMethodDescriptor method, out ReferenceCall call)
        {
            call = null;
            if (!TryGetNativeValueConstructor(method, out var owner, out var mapped)) return false;
            var signature = method.Signature;
            var candidate = new ReferenceCall
            {
                Constructor = true,
                Owner = owner,
                MethodPointerName = mapped.NonGenericMethodInfoPointerField.Name,
                Parameters = new TypeSignature[signature.ParameterTypes.Count],
                References = new bool[signature.ParameterTypes.Count]
            };
            for (var index = 0; index < candidate.Parameters.Length; index++)
            {
                var parameter = signature.ParameterTypes[index].InstantiateGenericTypes(GenericContext.FromMethod(method));
                candidate.References[index] = parameter is ByReferenceTypeSignature;
                var element = parameter is ByReferenceTypeSignature reference ? reference.BaseType : parameter;
                if (!TryGetAddressElement(element, out candidate.Parameters[index]))
                    return false;
            }
            call = candidate;
            return true;
        }

        private CilInstruction TranslateNativeValueConstructor(CilOpCode code, IMethodDescriptor method)
        {
            if (TryTranslateTypedValueConstructor(code, method, out var typed)) return typed;
            if (code.Code != CilCode.Call || !TryCreateNativeValueConstructor(method, out var call) ||
                call.References.Any(reference => reference))
                return FailInstruction("unsupported-native-value-constructor-location",
                    "The in-place constructor requires exact native metadata and by-value arguments; native byrefs use scoped locations.");
            var parameters = method.Signature.ParameterTypes.Select(parameter =>
                parameter.InstantiateGenericTypes(GenericContext.FromMethod(method))).ToArray();
            for (var index = 0; index < parameters.Length; index++)
                if (!NativeValueArgumentMatches(currentInstruction, parameters.Length - index - 1, parameters[index]))
                    return FailInstruction("unsupported-native-constructor-argument",
                        "The constructor argument representation does not match its native parameter.");

            var first = body.Add(CilOpCodes.Nop);
            if (!EmitNativeValueReceiver(method.DeclaringType.ToTypeSignature(), parameters, out var receiver)) return null;
            call.Receiver = call.Owner;
            EmitReferenceCall(call);
            EmitAddressKeepAlive(receiver);
            return first;
        }

        private bool TryTranslateTypedValueConstructor(CilOpCode code, IMethodDescriptor method,
            out CilInstruction translated)
        {
            translated = null;
            var source = method.Resolve();
            var sourceOwner = method.DeclaringType?.ToTypeSignature();
            if (code.Code != CilCode.Call || source is not { IsConstructor: true, IsStatic: false } ||
                method is MethodSpecification || !InteropDelegateContracts.HasStandardConvention(method.Signature) ||
                !TryGetNativeValueWrapper(sourceOwner, out var wrapper)) return false;
            var initializer = context.GetContextForNewType(wrapper.Resolve()).TryGetRestoredValueConstructor(source);
            InteropMethodContext nativeConstructor = null;
            if (initializer == null && !TryGetNativeValueConstructor(method, out _, out nativeConstructor)) return false;
            if (!NativeMethodStorageCompatible(code, method)) return true;
            var parameters = method.Signature.ParameterTypes.Select(parameter =>
                parameter.InstantiateGenericTypes(GenericContext.FromMethod(method))).ToArray();
            for (var index = 0; index < parameters.Length; index++)
                if (!NativeValueArgumentMatches(currentInstruction, parameters.Length - index - 1, parameters[index]))
                {
                    Fail("unsupported-native-constructor-argument",
                        "The in-place initializer requires exact argument storage types.", currentInstruction);
                    return true;
                }
            var first = body.Add(CilOpCodes.Nop);
            if (!EmitNativeValueReceiver(sourceOwner, parameters, out var receiver)) return true;
            initializer ??= InteropMethodBodies.GetValueConstructorInitializer(nativeConstructor);
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                new MemberReference(wrapper.ToTypeDefOrRef(), initializer.Name, initializer.Signature)));
            EmitAddressKeepAlive(receiver);
            translated = first;
            return true;
        }

        private bool TryPlanNativeConstructorReferenceCall(CilInstruction instruction, IMethodDescriptor method,
            ReferenceOrigins[] consumed, out ReferenceUse use)
        {
            use = null;
            var createsReceiver = instruction.OpCode.Code == CilCode.Newobj;
            var offset = createsReceiver ? 0 : 1;
            if (instruction.OpCode.Code is not (CilCode.Call or CilCode.Newobj) ||
                !TryCreateNativeValueConstructor(method, out var call) ||
                consumed.Length != call.Parameters.Length + offset)
                return false;
            var sourceOwner = method.DeclaringType.ToTypeSignature();
            var typed = TryGetNativeValueWrapper(sourceOwner, out _);
            if (createsReceiver)
            {
                if (!typed) return false;
                call.CreatesReceiver = true;
            }
            else if (consumed[0] != null)
            {
                if (!TryGetWritableCallLocation(consumed[0], sourceOwner, out _, allowAggregate: true)) return false;
                call.ReferenceReceiver = true;
            }
            else
            {
                if (!typed || !TryGetStackValue(instruction, call.Parameters.Length, out var receiver) ||
                    receiver.Type is not ByReferenceTypeSignature reference ||
                    !SignatureComparer.Default.Equals(reference.BaseType, sourceOwner)) return false;
                call.Receiver = call.Owner;
            }
            if (typed)
            {
                if (!TryGetNativeValueConstructor(method, out _, out var mapped)) return false;
                call.ValueMethod = mapped;
                call.NativeReferences = new bool[call.Parameters.Length];
            }
            for (var index = 0; index < call.Parameters.Length; index++)
            {
                var parameter = method.Signature.ParameterTypes[index].InstantiateGenericTypes(GenericContext.FromMethod(method));
                var origins = consumed[index + offset];
                if (parameter is ByReferenceTypeSignature reference)
                {
                    if (origins != null)
                    {
                        if (!TryGetWritableCallLocation(origins, reference.BaseType, out _, allowAggregate: typed)) return false;
                        if (typed) call.NativeReferences[index] = true;
                    }
                    else if (!typed || !NativeValueArgumentMatches(instruction,
                        call.Parameters.Length - index - 1, parameter)) return false;
                }
                else if (origins != null ||
                    !NativeValueArgumentMatches(instruction, call.Parameters.Length - index - 1, parameter))
                    return false;
            }
            use = new ReferenceUse { Call = call };
            return true;
        }

        private bool NativeValueArgumentMatches(CilInstruction instruction, int depth, TypeSignature parameter) =>
            TryGetStackValue(instruction, depth, out var argument) &&
            (parameter is ByReferenceTypeSignature ? !argument.IsNull && SignatureComparer.Default.Equals(argument.Type, parameter) :
                argument.IsNull ? !InteropTypeReferences.IsValueType(parameter) :
                SignatureComparer.Default.Equals(argument.Type, parameter) ||
                ArrayStorageType(parameter) != null && NumericStackValue(argument.Type).Type != null &&
                SameStackValue(NumericStackValue(argument.Type), NumericStackValue(parameter)));
    }
}
