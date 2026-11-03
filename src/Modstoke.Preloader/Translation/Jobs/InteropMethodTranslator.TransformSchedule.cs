// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

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
        private bool TryTranslateTransformJobSchedule(CilMethodBody original)
        {
            const string extensionsName = "UnityEngine.Jobs.IJobParallelForTransformExtensions";
            const string transformsName = "UnityEngine.Jobs.TransformAccessArray";
            const string handleName = "Unity.Jobs.JobHandle";
            const string parametersName = "Unity.Jobs.LowLevel.Unsafe.JobsUtility/JobScheduleParameters";
            if (!IsCoreJobType(sourceMethod.DeclaringType, extensionsName) ||
                (string)sourceMethod.Name is not ("Schedule" or "ScheduleByRef" or "ScheduleReadOnly" or "ScheduleReadOnlyByRef") ||
                !InteropManagedJobContracts.IsExtensionMethod(sourceMethod, ownerType) ||
                !InteropDelegateContracts.HasStandardConvention(sourceMethod.Signature) ||
                sourceMethod.Signature.HasThis || sourceMethod.Signature.GenericParameterCount != 1 ||
                target.GenericParameters.Count != 1 || original.ExceptionHandlers.Count != 0 ||
                original.LocalVariables.Count != 2) return false;

            var readOnly = sourceMethod.Name == "ScheduleReadOnly" || sourceMethod.Name == "ScheduleReadOnlyByRef";
            var byRef = sourceMethod.Name == "ScheduleByRef" || sourceMethod.Name == "ScheduleReadOnlyByRef";
            var dependencyIndex = readOnly ? 3 : 2;
            var sourceSignature = sourceMethod.Signature;
            var targetSignature = target.Signature;
            var transforms = ownerType.AssemblyContext.TryGetTypeByName(transformsName);
            if (transforms?.OriginalType == null || !transforms.NewType.IsValueType ||
                transforms.NewType.GenericParameters.Count != 0 ||
                sourceSignature.ParameterTypes.Count != dependencyIndex + 1 ||
                targetSignature.ParameterTypes.Count != dependencyIndex + 1 || !target.IsStatic ||
                !InteropDelegateContracts.HasStandardConvention(targetSignature) ||
                targetSignature.HasThis || targetSignature.GenericParameterCount != 1 ||
                !JobPayload(sourceSignature.ParameterTypes[0], byRef) || !JobPayload(targetSignature.ParameterTypes[0], byRef) ||
                sourceSignature.ParameterTypes[1] is not TypeDefOrRefSignature { IsValueType: true } sourceTransforms ||
                !IsCoreJobType(sourceTransforms.Type, transformsName) ||
                !SignatureComparer.Default.Equals(targetSignature.ParameterTypes[1], transforms.NewType.ToTypeSignature()) ||
                sourceSignature.ReturnType is not TypeDefOrRefSignature { IsValueType: true } sourceHandle ||
                !IsCoreJobType(sourceHandle.Type, handleName) ||
                targetSignature.ReturnType is not TypeDefOrRefSignature { IsValueType: true } targetHandle ||
                !IsCoreJobType(targetHandle.Type, handleName) ||
                !SignatureComparer.Default.Equals(sourceSignature.ParameterTypes[dependencyIndex], sourceSignature.ReturnType) ||
                !SignatureComparer.Default.Equals(targetSignature.ParameterTypes[dependencyIndex], targetSignature.ReturnType) ||
                (target.GenericParameters[0].Attributes & GenericParameterAttributes.SpecialConstraintMask) !=
                    (GenericParameterAttributes.NotNullableValueTypeConstraint | GenericParameterAttributes.DefaultConstructorConstraint) ||
                readOnly && (sourceSignature.ParameterTypes[2].ElementType != ElementType.I4 ||
                    targetSignature.ParameterTypes[2].ElementType != ElementType.I4)) return false;

            var parameters = original.LocalVariables[0];
            var result = original.LocalVariables[1];
            if (!IsCoreJobType(parameters.VariableType.ToTypeDefOrRef(), parametersName) ||
                !SignatureComparer.Default.Equals(result.VariableType, sourceSignature.ReturnType)) return false;
            var code = original.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();
            var pattern = readOnly
                ? new[] { CilCode.Ldloca, byRef ? CilCode.Ldarg : CilCode.Ldarga, CilCode.Call, CilCode.Call,
                    CilCode.Ldarg, CilCode.Ldc_I4, CilCode.Call, CilCode.Ldloca, CilCode.Ldarga, CilCode.Call,
                    CilCode.Ldarg, CilCode.Call, CilCode.Stloc, CilCode.Br, CilCode.Ldloc, CilCode.Ret }
                : new[] { CilCode.Ldloca, byRef ? CilCode.Ldarg : CilCode.Ldarga, CilCode.Call, CilCode.Call,
                    CilCode.Ldarg, CilCode.Ldc_I4, CilCode.Call, CilCode.Ldloca, CilCode.Ldarga, CilCode.Call,
                    CilCode.Call, CilCode.Stloc, CilCode.Br, CilCode.Ldloc, CilCode.Ret };
            var scheduleIndex = readOnly ? 11 : 10;
            if (code.Length != pattern.Length || code.Where((instruction, index) => instruction.OpCode.Code != pattern[index]).Any() ||
                !ReferenceEquals(code[0].Operand, parameters) || !ReferenceEquals(code[7].Operand, parameters) ||
                !ReferenceEquals(code[1].Operand, sourceMethod.Parameters[0]) ||
                !ReferenceEquals(code[4].Operand, sourceMethod.Parameters[dependencyIndex]) ||
                code[5].Operand is not int mode || mode != 1 ||
                !ReferenceEquals(code[8].Operand, sourceMethod.Parameters[1]) ||
                !ReferenceEquals(code[scheduleIndex + 1].Operand, result) ||
                !ReferenceEquals(code[scheduleIndex + 3].Operand, result) ||
                code[scheduleIndex + 2].Operand is not CilInstructionLabel jump ||
                !ReferenceEquals(jump.Instruction, code[scheduleIndex + 3]) ||
                readOnly && !ReferenceEquals(code[10].Operand, sourceMethod.Parameters[2])) return false;

            if (code[2].Operand is not MethodSpecification address || address.Name != "AddressOf" ||
                !IsCoreJobType(address.DeclaringType, "Unity.Collections.LowLevel.Unsafe.UnsafeUtility") ||
                !JobGenericCall(address) || address.Method.Signature.ParameterTypes.Count != 1 ||
                !JobPayload(address.Method.Signature.ParameterTypes[0], true) ||
                address.Method.Signature.ReturnType is not PointerTypeSignature
                    { BaseType: CorLibTypeSignature { ElementType: ElementType.Void } } ||
                code[3].Operand is not MethodSpecification getter || getter.Name != "GetReflectionData" ||
                !SignatureComparer.Default.Equals(getter.DeclaringType, sourceMethod.DeclaringType) ||
                !JobGenericCall(getter) || getter.Method.Signature.ParameterTypes.Count != 0 ||
                getter.Method.Signature.ReturnType.ElementType != ElementType.I) return false;
            if (code[6].Operand is not IMethodDescriptor constructor || constructor is MethodSpecification ||
                constructor.Name != ".ctor" || !IsCoreJobType(constructor.DeclaringType, parametersName) ||
                !InteropDelegateContracts.HasStandardConvention(constructor.Signature) || !constructor.Signature.HasThis ||
                constructor.Signature.GenericParameterCount != 0 || constructor.Signature.ReturnType.ElementType != ElementType.Void ||
                constructor.Signature.ParameterTypes.Count != 4 ||
                !SignatureComparer.Default.Equals(constructor.Signature.ParameterTypes[0], address.Method.Signature.ReturnType) ||
                constructor.Signature.ParameterTypes[1].ElementType != ElementType.I ||
                !SignatureComparer.Default.Equals(constructor.Signature.ParameterTypes[2], sourceSignature.ReturnType) ||
                !IsCoreJobType(constructor.Signature.ParameterTypes[3].ToTypeDefOrRef(), "Unity.Jobs.LowLevel.Unsafe.ScheduleMode"))
                return false;
            if (code[9].Operand is not IMethodDescriptor getAccess || getAccess is MethodSpecification ||
                getAccess.Name != "GetTransformAccessArrayForSchedule" || !IsCoreJobType(getAccess.DeclaringType, transformsName) ||
                !InteropDelegateContracts.HasStandardConvention(getAccess.Signature) || !getAccess.Signature.HasThis ||
                getAccess.Signature.GenericParameterCount != 0 || getAccess.Signature.ParameterTypes.Count != 0 ||
                getAccess.Signature.ReturnType.ElementType != ElementType.I) return false;
            if (code[scheduleIndex].Operand is not IMethodDescriptor schedule || schedule is MethodSpecification ||
                schedule.Name != (readOnly ? "ScheduleParallelForTransformReadOnly" : "ScheduleParallelForTransform") ||
                !IsCoreJobType(schedule.DeclaringType, "Unity.Jobs.LowLevel.Unsafe.JobsUtility") ||
                !InteropDelegateContracts.HasStandardConvention(schedule.Signature) || schedule.Signature.HasThis ||
                schedule.Signature.GenericParameterCount != 0 ||
                !SignatureComparer.Default.Equals(schedule.Signature.ReturnType, sourceSignature.ReturnType) ||
                schedule.Signature.ParameterTypes.Count != (readOnly ? 3 : 2) ||
                schedule.Signature.ParameterTypes[0] is not ByReferenceTypeSignature parameterReference ||
                !SignatureComparer.Default.Equals(parameterReference.BaseType, parameters.VariableType) ||
                schedule.Signature.ParameterTypes[1].ElementType != ElementType.I ||
                readOnly && schedule.Signature.ParameterTypes[2].ElementType != ElementType.I4) return false;

            var mappedGetter = FindRestoredJobGetter(getter);
            if (mappedGetter == null) return false;

            // The runtime validates the borrowed transform layout, copies the payload and tracks completion.
            // No source payload pointer escapes, including for the by-ref overloads.
            target.CilMethodBody.LocalVariables.Clear();
            var value = new GenericParameterSignature(imports.Module, GenericParameterType.Method, 0);
            var importer = imports.Module.DefaultImporter;
            body.Add(byRef ? CilOpCodes.Ldarg : CilOpCodes.Ldarga, target.Parameters[0]);
            body.Add(CilOpCodes.Call, importer.ImportMethod(new MemberReference(ownerType.NewType,
                mappedGetter.Name, mappedGetter.Signature).MakeGenericInstanceMethod(value)));
            body.Add(CilOpCodes.Ldarga, target.Parameters[1]);
            for (var index = 2; index <= dependencyIndex; index++) body.Add(CilOpCodes.Ldarg, target.Parameters[index]);
            var helperName = readOnly ? nameof(InteropNativeJobSupport.ScheduleTransformReadOnly) :
                nameof(InteropNativeJobSupport.ScheduleTransform);
            var helper = (IMethodDefOrRef)importer.ImportMethod(typeof(InteropNativeJobSupport).GetMethod(helperName));
            body.Add(CilOpCodes.Call, helper.MakeGenericInstanceMethod(value,
                targetSignature.ParameterTypes[1], targetSignature.ReturnType));
            body.Add(CilOpCodes.Ret);
            return true;
        }
    }
}
