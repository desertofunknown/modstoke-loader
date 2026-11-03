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
        private bool TryTranslateParticleJobSchedule(CilMethodBody original)
        {
            const string prefix = "UnityEngine.ParticleSystemJobs.";
            const string systemName = "UnityEngine.ParticleSystem";
            const string handleName = "Unity.Jobs.JobHandle";
            const string parametersName = "Unity.Jobs.LowLevel.Unsafe.JobsUtility/JobScheduleParameters";
            if (!IsParticleJobType(sourceMethod.DeclaringType, prefix + "IParticleSystemJobExtensions") ||
                (string)sourceMethod.Name is not ("Schedule" or "ScheduleBatch") ||
                !InteropManagedJobContracts.IsExtensionMethod(sourceMethod, ownerType) ||
                !InteropDelegateContracts.HasStandardConvention(sourceMethod.Signature) ||
                sourceMethod.Signature.HasThis || sourceMethod.Signature.GenericParameterCount != 1 ||
                target.GenericParameters.Count != 1 || original.ExceptionHandlers.Count != 0 ||
                original.LocalVariables.Count != 3) return false;

            var single = sourceMethod.Signature.ParameterTypes.Count == 3;
            var dependencyIndex = single ? 2 : 3;
            var sourceSignature = sourceMethod.Signature;
            var targetSignature = target.Signature;
            var system = ownerType.AssemblyContext.TryGetTypeByName(systemName);
            if (system?.OriginalType == null || system.NewType.IsValueType || system.NewType.IsInterface ||
                targetSignature.ParameterTypes.Count != dependencyIndex + 1 || !target.IsStatic ||
                !InteropDelegateContracts.HasStandardConvention(targetSignature) ||
                targetSignature.HasThis || targetSignature.GenericParameterCount != 1 ||
                !JobPayload(sourceSignature.ParameterTypes[0], false) || !JobPayload(targetSignature.ParameterTypes[0], false) ||
                !IsParticleJobType(sourceSignature.ParameterTypes[1].ToTypeDefOrRef(), systemName) ||
                !SignatureComparer.Default.Equals(targetSignature.ParameterTypes[1], system.NewType.ToTypeSignature()) ||
                !IsCoreJobType(sourceSignature.ReturnType.ToTypeDefOrRef(), handleName) ||
                !IsCoreJobType(targetSignature.ReturnType.ToTypeDefOrRef(), handleName) ||
                !targetSignature.ReturnType.IsValueType ||
                !SignatureComparer.Default.Equals(sourceSignature.ParameterTypes[dependencyIndex], sourceSignature.ReturnType) ||
                !SignatureComparer.Default.Equals(targetSignature.ParameterTypes[dependencyIndex], targetSignature.ReturnType) ||
                (target.GenericParameters[0].Attributes & GenericParameterAttributes.SpecialConstraintMask) !=
                    (GenericParameterAttributes.NotNullableValueTypeConstraint | GenericParameterAttributes.DefaultConstructorConstraint) ||
                !single && (sourceSignature.ParameterTypes[2].ElementType != ElementType.I4 ||
                    targetSignature.ParameterTypes[2].ElementType != ElementType.I4)) return false;

            var parameters = original.LocalVariables[0];
            var result = original.LocalVariables[1];
            var returned = original.LocalVariables[2];
            if (!IsCoreJobType(parameters.VariableType.ToTypeDefOrRef(), parametersName) ||
                !SignatureComparer.Default.Equals(result.VariableType, sourceSignature.ReturnType) ||
                !SignatureComparer.Default.Equals(returned.VariableType, sourceSignature.ReturnType)) return false;
            var code = original.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();
            var pattern = single
                ? new[] { CilCode.Ldarga, CilCode.Ldarg, CilCode.Ldarg, CilCode.Call, CilCode.Call,
                    CilCode.Stloc, CilCode.Ldloca, CilCode.Ldarg, CilCode.Callvirt, CilCode.Call,
                    CilCode.Stloc, CilCode.Ldarg, CilCode.Ldloc, CilCode.Callvirt, CilCode.Ldloc,
                    CilCode.Stloc, CilCode.Br, CilCode.Ldloc, CilCode.Ret }
                : new[] { CilCode.Ldarga, CilCode.Ldarg, CilCode.Ldarg, CilCode.Call, CilCode.Call,
                    CilCode.Stloc, CilCode.Ldloca, CilCode.Ldarg, CilCode.Ldarg, CilCode.Callvirt,
                    CilCode.Ldc_I4, CilCode.Conv_U, CilCode.Call, CilCode.Stloc, CilCode.Ldarg,
                    CilCode.Ldloc, CilCode.Callvirt, CilCode.Ldloc, CilCode.Stloc, CilCode.Br,
                    CilCode.Ldloc, CilCode.Ret };
            var getIndex = single ? 8 : 9;
            var scheduleIndex = single ? 9 : 12;
            if (code.Length != pattern.Length || code.Where((instruction, index) => instruction.OpCode.Code != pattern[index]).Any() ||
                !ReferenceEquals(code[0].Operand, sourceMethod.Parameters[0]) ||
                !ReferenceEquals(code[1].Operand, sourceMethod.Parameters[1]) ||
                !ReferenceEquals(code[2].Operand, sourceMethod.Parameters[dependencyIndex]) ||
                !ReferenceEquals(code[5].Operand, parameters) || !ReferenceEquals(code[6].Operand, parameters) ||
                !ReferenceEquals(code[getIndex - 1].Operand, sourceMethod.Parameters[1]) ||
                !ReferenceEquals(code[scheduleIndex + 1].Operand, result) ||
                !ReferenceEquals(code[scheduleIndex + 2].Operand, sourceMethod.Parameters[1]) ||
                !ReferenceEquals(code[scheduleIndex + 3].Operand, result) ||
                !ReferenceEquals(code[scheduleIndex + 5].Operand, result) ||
                !ReferenceEquals(code[scheduleIndex + 6].Operand, returned) ||
                !ReferenceEquals(code[scheduleIndex + 8].Operand, returned) ||
                code[scheduleIndex + 7].Operand is not CilInstructionLabel jump ||
                !ReferenceEquals(jump.Instruction, code[scheduleIndex + 8]) ||
                !single && (!ReferenceEquals(code[7].Operand, sourceMethod.Parameters[2]) ||
                    code[10].Operand is not int patchData || patchData != 0)) return false;

            var getterName = single ? "IJobParticleSystemExtensions" : sourceMethod.Name == "ScheduleBatch"
                ? "IJobParticleSystemParallelForBatchExtensions" : "IJobParticleSystemParallelForExtensions";
            if (code[3].Operand is not MethodSpecification getter || getter.Name != "GetReflectionData" ||
                !IsParticleJobType(getter.DeclaringType, prefix + getterName) || !JobGenericCall(getter) ||
                getter.Method.Signature.ParameterTypes.Count != 0 || getter.Method.Signature.ReturnType.ElementType != ElementType.I ||
                code[4].Operand is not MethodSpecification factory || factory.Name != "CreateScheduleParams" ||
                !IsParticleJobType(factory.DeclaringType, prefix + "ParticleSystemJobUtility") || !JobGenericCall(factory) ||
                !SignatureComparer.Default.Equals(factory.Method.Signature.ReturnType, parameters.VariableType) ||
                factory.Method.Signature.ParameterTypes.Count != 4 || !JobPayload(factory.Method.Signature.ParameterTypes[0], true) ||
                !SignatureComparer.Default.Equals(factory.Method.Signature.ParameterTypes[1], sourceSignature.ParameterTypes[1]) ||
                !SignatureComparer.Default.Equals(factory.Method.Signature.ParameterTypes[2], sourceSignature.ReturnType) ||
                factory.Method.Signature.ParameterTypes[3].ElementType != ElementType.I) return false;
            if (code[getIndex].Operand is not IMethodDescriptor getData || getData is MethodSpecification ||
                getData.Name != "GetManagedJobData" || !IsParticleJobType(getData.DeclaringType, systemName) ||
                !InteropDelegateContracts.HasStandardConvention(getData.Signature) || !getData.Signature.HasThis ||
                getData.Signature.GenericParameterCount != 0 || getData.Signature.ParameterTypes.Count != 0 ||
                getData.Signature.ReturnType is not PointerTypeSignature
                    { BaseType: CorLibTypeSignature { ElementType: ElementType.Void } }) return false;
            if (code[scheduleIndex + 4].Operand is not IMethodDescriptor publish || publish is MethodSpecification ||
                publish.Name != "SetManagedJobHandle" || !IsParticleJobType(publish.DeclaringType, systemName) ||
                !InteropDelegateContracts.HasStandardConvention(publish.Signature) || !publish.Signature.HasThis ||
                publish.Signature.GenericParameterCount != 0 || publish.Signature.ReturnType.ElementType != ElementType.Void ||
                publish.Signature.ParameterTypes.Count != 1 ||
                !SignatureComparer.Default.Equals(publish.Signature.ParameterTypes[0], sourceSignature.ReturnType)) return false;
            if (code[scheduleIndex].Operand is not IMethodDescriptor schedule || schedule is MethodSpecification ||
                schedule.Name != (single ? "ScheduleManagedJob" : "ScheduleParallelForDeferArraySize") ||
                !(single ? IsParticleJobType(schedule.DeclaringType, systemName) :
                    IsCoreJobType(schedule.DeclaringType, "Unity.Jobs.LowLevel.Unsafe.JobsUtility")) ||
                !InteropDelegateContracts.HasStandardConvention(schedule.Signature) || schedule.Signature.HasThis ||
                schedule.Signature.GenericParameterCount != 0 ||
                !SignatureComparer.Default.Equals(schedule.Signature.ReturnType, sourceSignature.ReturnType) ||
                schedule.Signature.ParameterTypes.Count != (single ? 2 : 4) ||
                schedule.Signature.ParameterTypes[0] is not ByReferenceTypeSignature scheduleReference ||
                !SignatureComparer.Default.Equals(scheduleReference.BaseType, parameters.VariableType) ||
                !SignatureComparer.Default.Equals(schedule.Signature.ParameterTypes[single ? 1 : 2], getData.Signature.ReturnType) ||
                !single && (schedule.Signature.ParameterTypes[1].ElementType != ElementType.I4 ||
                    !SignatureComparer.Default.Equals(schedule.Signature.ParameterTypes[3], getData.Signature.ReturnType))) return false;

            var getterOwner = ownerType.AssemblyContext.TryGetTypeByName(prefix + getterName);
            if (getterOwner == null) return false;
            var mappedGetter = FindRestoredJobGetter(getter, getterOwner);
            if (mappedGetter == null) return false;

            // The matched runtime copies the payload, combines the system's dependency and publishes
            // the tracked handle. The source's temporary payload pointer never escapes managed storage.
            target.CilMethodBody.LocalVariables.Clear();
            var value = new GenericParameterSignature(imports.Module, GenericParameterType.Method, 0);
            var importer = imports.Module.DefaultImporter;
            body.Add(CilOpCodes.Ldarga, target.Parameters[0]);
            body.Add(CilOpCodes.Call, importer.ImportMethod(new MemberReference(getterOwner.NewType,
                mappedGetter.Name, mappedGetter.Signature).MakeGenericInstanceMethod(value)));
            for (var index = 1; index <= dependencyIndex; index++) body.Add(CilOpCodes.Ldarg, target.Parameters[index]);
            var helperName = single ? nameof(InteropNativeJobSupport.ScheduleParticleSystem) :
                nameof(InteropNativeJobSupport.ScheduleParticleSystemParallelFor);
            var helper = (IMethodDefOrRef)importer.ImportMethod(typeof(InteropNativeJobSupport).GetMethod(helperName));
            body.Add(CilOpCodes.Call, helper.MakeGenericInstanceMethod(value, targetSignature.ReturnType));
            body.Add(CilOpCodes.Ret);
            return true;
        }

        private static bool IsParticleJobType(ITypeDescriptor type, string name) => type?.FullName == name &&
            InteropRestoredTypeSupport.GetAssemblyName(type.ToTypeSignature()) == "UnityEngine.ParticleSystemModule";
    }
}
