// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private readonly record struct NativeJobInstruction(CilOpCode OpCode, object Operand = null);

        private readonly Dictionary<CilInstruction, NativeJobInstruction> nativeJobInstructions =
            new(ReferenceEqualityComparer.Instance);

        private bool FindNativeJobRegistrations(CilMethodBody source)
        {
            var code = source.Instructions;
            HashSet<CilInstruction> boundaries = null;
            for (var index = 0; index < code.Count; index++)
            {
                var call = code[index];
                if (call.OpCode.Code != CilCode.Call || call.Operand is not IMethodDescriptor method ||
                    !TryGetJobRegistrationShape(method, out var typeCount, out var legacyJobType, out var optionalCallbacks))
                    continue;

                var cursor = index;
                var ignored = new List<CilInstruction>();
                var optionalProven = true;
                for (var optional = 0; optional < optionalCallbacks; optional++)
                {
                    var value = Previous();
                    if (value?.OpCode.Code != CilCode.Ldnull) { optionalProven = false; break; }
                    ignored.Add(value);
                }
                if (!optionalProven) continue;
                var constructor = Previous();
                if (constructor == null || !delegateConstructors.TryGetValue(constructor, out var factory) ||
                    factory.NativeType != null || !factory.ManagedCallback ||
                    factory.Callback.Signature.HasThis || factory.BoundTargetBox != null)
                    continue;
                var pointer = Previous();
                if (pointer?.OpCode.Code != CilCode.Ldftn || !delegatePointers.TryGetValue(pointer, out var pointerFactory) ||
                    !ReferenceEquals(factory, pointerFactory) || Previous()?.OpCode.Code != CilCode.Ldnull)
                    continue;
                if (legacyJobType)
                {
                    var value = Previous();
                    if (value?.OpCode.Code != CilCode.Ldc_I4 || value.Operand is not int) continue;
                    ignored.Add(value);
                }

                var tokens = new CilInstruction[typeCount];
                var conversions = new CilInstruction[typeCount];
                var payloadTypes = new TypeSignature[typeCount];
                var typesProven = true;
                for (var typeIndex = typeCount - 1; typeIndex >= 0; typeIndex--)
                {
                    conversions[typeIndex] = Previous();
                    tokens[typeIndex] = Previous();
                    if (conversions[typeIndex] == null || !IsTypeFromHandle(conversions[typeIndex]) ||
                        tokens[typeIndex]?.OpCode.Code != CilCode.Ldtoken || tokens[typeIndex].Operand is not ITypeDefOrRef type)
                    { typesProven = false; break; }
                    currentInstruction = tokens[typeIndex];
                    if (!ValidateManagedGenericUse(type.ToTypeSignature())) return false;
                    payloadTypes[typeIndex] = resolver.Resolve(type.ToTypeSignature());
                    if (payloadTypes[typeIndex] == null) { typesProven = false; break; }
                }
                if (!typesProven || !IsPrimaryJobDelegate(factory, payloadTypes[0])) continue;
                var animation = IsAnimationJobType(sourceMethod.DeclaringType,
                    "UnityEngine.Animations.ProcessAnimationJobStruct`1");
                if (animation && (legacyJobType || !IsAnimationJobRegistration(factory, payloadTypes)))
                    return Fail("animation-job-registration-contract",
                        "The animation producer requires its exact restored Execute callback and payload.", call);

                if (boundaries == null)
                {
                    boundaries = new HashSet<CilInstruction>(ReferenceEqualityComparer.Instance);
                    foreach (var instruction in code)
                    {
                        if (instruction.Operand is ICilLabel label) AddBoundary(label);
                        else if (instruction.Operand is IReadOnlyList<ICilLabel> labels)
                            foreach (var branch in labels) AddBoundary(branch);
                    }
                    foreach (var handler in source.ExceptionHandlers)
                    {
                        AddBoundary(handler.TryStart);
                        AddBoundary(handler.TryEnd);
                        AddBoundary(handler.HandlerStart);
                        AddBoundary(handler.HandlerEnd);
                        AddBoundary(handler.FilterStart);
                    }
                }
                // CLR Type values and delegates must not escape the complete proven expression.
                // The first token may be an entry; no other instruction may have another entry.
                for (var position = cursor + 1; position <= index; position++)
                    if (boundaries.Contains(code[position]))
                        return Fail("job-registration-control-flow",
                            "A branch or exception boundary enters a native job registration expression.", code[position]);

                var importer = imports.Module.DefaultImporter;
                for (var typeIndex = 0; typeIndex < typeCount; typeIndex++)
                {
                    nativeJobInstructions.Add(tokens[typeIndex], new NativeJobInstruction(CilOpCodes.Ldtoken,
                        importer.ImportType(payloadTypes[typeIndex].ToTypeDefOrRef())));
                    nativeJobInstructions.Add(conversions[typeIndex], new NativeJobInstruction(CilOpCodes.Call,
                        importer.ImportMethod(typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle)))));
                }
                foreach (var instruction in ignored)
                    nativeJobInstructions.Add(instruction, new NativeJobInstruction(CilOpCodes.Nop));
                var signature = typeCount == 1 ? new[] { typeof(Type), typeof(Delegate) } :
                    new[] { typeof(Type), typeof(Type), typeof(Delegate) };
                nativeJobInstructions.Add(call, new NativeJobInstruction(CilOpCodes.Call,
                    importer.ImportMethod(typeof(InteropNativeJobSupport).GetMethod(
                        animation ? nameof(InteropNativeJobSupport.CreateAnimationReflectionData) :
                            nameof(InteropNativeJobSupport.CreateReflectionData), signature))));

                CilInstruction Previous()
                {
                    do { cursor--; } while (cursor >= 0 && code[cursor].OpCode.Code == CilCode.Nop);
                    return cursor >= 0 ? code[cursor] : null;
                }
            }
            return true;

            void AddBoundary(ICilLabel label)
            {
                if (label is CilInstructionLabel { Instruction: not null } entry) boundaries.Add(entry.Instruction);
            }
        }

        private bool IsAnimationJobRegistration(DelegateFactory factory, TypeSignature[] payloadTypes)
        {
            var signature = sourceMethod.Signature;
            var sourceType = sourceMethod.DeclaringType;
            if (sourceMethod.Name != "GetJobReflectionData" || !sourceMethod.IsStatic ||
                !InteropDelegateContracts.HasStandardConvention(signature) || signature.HasThis ||
                signature.GenericParameterCount != 0 || signature.ParameterTypes.Count != 0 ||
                signature.ReturnType is not CorLibTypeSignature { ElementType: ElementType.I } ||
                ownerType.OriginalType != null || !ReferenceEquals(factory.CallbackOwner, ownerType) ||
                sourceType.GenericParameters.Count != 1 || ownerType.NewType.GenericParameters.Count != 1 ||
                payloadTypes.Length != 1 || !IsAnimationPayload(payloadTypes[0]))
                return false;
            const GenericParameterAttributes constraints = GenericParameterAttributes.NotNullableValueTypeConstraint |
                GenericParameterAttributes.DefaultConstructorConstraint;
            if ((sourceType.GenericParameters[0].Attributes & GenericParameterAttributes.SpecialConstraintMask) != constraints ||
                (ownerType.NewType.GenericParameters[0].Attributes & GenericParameterAttributes.SpecialConstraintMask) != constraints ||
                !sourceType.GenericParameters[0].Constraints.Any(constraint =>
                    IsAnimationJobType(constraint.Constraint, "UnityEngine.Animations.IAnimationJob")) ||
                !ownerType.NewType.GenericParameters[0].Constraints.Any(constraint =>
                    IsAnimationJobType(constraint.Constraint, "UnityEngine.Animations.IAnimationJob")))
                return false;

            var callback = factory.SourceCallback;
            if (callback is MethodSpecification || callback.Name != "Execute" ||
                callback.DeclaringType?.ToTypeSignature() is not GenericInstanceTypeSignature producer ||
                !SignatureComparer.Default.Equals(producer.GenericType, sourceType) ||
                producer.TypeArguments.Count != 1 || !IsAnimationPayload(producer.TypeArguments[0]))
                return false;
            var execute = callback.Signature;
            return InteropDelegateContracts.HasStandardConvention(execute) && !execute.HasThis &&
                execute.GenericParameterCount == 0 && execute.ReturnType.ElementType == ElementType.Void &&
                execute.ParameterTypes.Count == 5 &&
                execute.ParameterTypes[0] is ByReferenceTypeSignature payload && IsAnimationPayload(payload.BaseType) &&
                execute.ParameterTypes[1] is CorLibTypeSignature { ElementType: ElementType.I } &&
                execute.ParameterTypes[2] is CorLibTypeSignature { ElementType: ElementType.I } &&
                execute.ParameterTypes[3] is ByReferenceTypeSignature ranges &&
                IsCoreJobType(ranges.BaseType.ToTypeDefOrRef(), "Unity.Jobs.LowLevel.Unsafe.JobRanges") &&
                execute.ParameterTypes[4] is CorLibTypeSignature { ElementType: ElementType.I4 };
        }

        private static bool IsAnimationPayload(TypeSignature type) =>
            type is GenericParameterSignature { ParameterType: GenericParameterType.Type, Index: 0 };

        private static bool IsAnimationJobType(ITypeDescriptor type, string name) => type?.FullName == name &&
            InteropRestoredTypeSupport.GetAssemblyName(type.ToTypeSignature()) == "UnityEngine.AnimationModule";

        private static bool TryGetJobRegistrationShape(IMethodDescriptor method, out int typeCount,
            out bool legacyJobType, out int optionalCallbacks)
        {
            typeCount = 0;
            legacyJobType = false;
            optionalCallbacks = 0;
            if (method is MethodSpecification || method.Name != "CreateJobReflectionData" ||
                !IsCoreJobType(method.DeclaringType, "Unity.Jobs.LowLevel.Unsafe.JobsUtility") ||
                !InteropDelegateContracts.HasStandardConvention(method.Signature) || method.Signature.HasThis ||
                method.Signature.GenericParameterCount != 0 ||
                method.Signature.ReturnType is not CorLibTypeSignature { ElementType: ElementType.I })
                return false;
            var parameters = method.Signature.ParameterTypes;
            var position = 0;
            while (position < parameters.Count && position < 2 &&
                InteropRestoredUnmanagedDelegateSupport.IsSystemType(parameters[position].ToTypeDefOrRef(), "System.Type"))
            { position++; typeCount++; }
            if (typeCount == 0) return false;
            if (position < parameters.Count &&
                IsCoreJobType(parameters[position].ToTypeDefOrRef(), "Unity.Jobs.LowLevel.Unsafe.JobType"))
            { legacyJobType = true; position++; }
            var callbacks = parameters.Count - position;
            if (callbacks != 1 && callbacks != 3 || typeCount == 1 && callbacks != 3 ||
                typeCount == 2 && legacyJobType && callbacks != 1)
                return false;
            for (; position < parameters.Count; position++)
                if (parameters[position] is not CorLibTypeSignature { ElementType: ElementType.Object }) return false;
            optionalCallbacks = callbacks - 1;
            return true;
        }

        private static bool IsCoreJobType(ITypeDescriptor type, string name) => type?.FullName == name &&
            InteropRestoredTypeSupport.GetAssemblyName(type.ToTypeSignature()) == "UnityEngine.CoreModule";

        private static bool IsPrimaryJobDelegate(DelegateFactory factory, TypeSignature payloadType)
        {
            var invoke = factory.ManagedType.Resolve()?.Methods.SingleOrDefault(method => method.Name == "Invoke");
            var signature = invoke?.Signature.InstantiateGenericTypes(GenericContext.FromType(factory.ManagedType));
            if (signature == null || !InteropDelegateContracts.HasStandardConvention(signature) ||
                !signature.HasThis || signature.GenericParameterCount != 0 ||
                signature.ReturnType.ElementType != ElementType.Void || signature.ParameterTypes.Count != 5 ||
                signature.ParameterTypes[0] is not ByReferenceTypeSignature payload ||
                !SignatureComparer.Default.Equals(payload.BaseType, payloadType) ||
                signature.ParameterTypes[1] is not CorLibTypeSignature { ElementType: ElementType.I } ||
                signature.ParameterTypes[2] is not CorLibTypeSignature { ElementType: ElementType.I } ||
                signature.ParameterTypes[3] is not ByReferenceTypeSignature ranges ||
                !IsCoreJobType(ranges.BaseType.ToTypeDefOrRef(), "Unity.Jobs.LowLevel.Unsafe.JobRanges") ||
                signature.ParameterTypes[4] is not CorLibTypeSignature { ElementType: ElementType.I4 })
                return false;
            return payloadType is GenericParameterSignature || payloadType.IsValueType &&
                payloadType is not CorLibTypeSignature && payloadType.Resolve()?.IsEnum != true;
        }

        private bool TryTranslateNativeJobInstruction(CilInstruction instruction, out CilInstruction translated)
        {
            translated = null;
            if (!nativeJobInstructions.TryGetValue(instruction, out var replacement)) return false;
            translated = new CilInstruction(replacement.OpCode, replacement.Operand);
            body.Add(translated);
            return true;
        }
    }
}
