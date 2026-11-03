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
        private bool TryTranslateJobSchedule(CilMethodBody original)
        {
            if (!InteropManagedJobContracts.IsExtensionMethod(sourceMethod, ownerType) ||
                sourceMethod.DeclaringModule.Assembly.Name != "UnityEngine.CoreModule" ||
                sourceMethod.DeclaringType.FullName is not ("Unity.Jobs.IJobExtensions" or
                    "Unity.Jobs.IJobForExtensions" or "Unity.Jobs.IJobParallelForExtensions") ||
                ownerType.TryGetMethodByUnityAssemblyMethod(sourceMethod) != null ||
                (string)sourceMethod.Name is not ("Schedule" or "ScheduleByRef" or "ScheduleParallel" or "ScheduleParallelByRef") ||
                !sourceMethod.IsStatic || !InteropDelegateContracts.HasStandardConvention(sourceMethod.Signature) ||
                sourceMethod.Signature.HasThis || sourceMethod.Signature.GenericParameterCount != 1 ||
                sourceMethod.GenericParameters.Count != 1 || target.GenericParameters.Count != 1 ||
                original.ExceptionHandlers.Count != 0 || original.LocalVariables.Count != 2) return false;

            const string handleName = "Unity.Jobs.JobHandle";
            const string parametersName = "Unity.Jobs.LowLevel.Unsafe.JobsUtility/JobScheduleParameters";
            var scalar = sourceMethod.DeclaringType.FullName == "Unity.Jobs.IJobExtensions";
            var forJob = sourceMethod.DeclaringType.FullName == "Unity.Jobs.IJobForExtensions";
            var parallelName = sourceMethod.Name == "ScheduleParallel" || sourceMethod.Name == "ScheduleParallelByRef";
            if (parallelName && !forJob) return false;
            var single = scalar || forJob && !parallelName;
            var byRef = sourceMethod.Name == "ScheduleByRef" || sourceMethod.Name == "ScheduleParallelByRef";
            var dependencyIndex = scalar ? 1 : single ? 2 : 3;
            var expectedCount = dependencyIndex + 1;
            var sourceSignature = sourceMethod.Signature;
            var targetSignature = target.Signature;
            if (sourceSignature.ParameterTypes.Count != expectedCount || targetSignature.ParameterTypes.Count != expectedCount ||
                !target.IsStatic || !InteropDelegateContracts.HasStandardConvention(targetSignature) ||
                targetSignature.HasThis || targetSignature.GenericParameterCount != 1 ||
                !IsCoreJobType(sourceSignature.ReturnType.ToTypeDefOrRef(), handleName) ||
                !IsCoreJobType(targetSignature.ReturnType.ToTypeDefOrRef(), handleName) ||
                !targetSignature.ReturnType.IsValueType ||
                !JobPayload(sourceSignature.ParameterTypes[0], byRef) || !JobPayload(targetSignature.ParameterTypes[0], byRef) ||
                !IsCoreJobType(sourceSignature.ParameterTypes[dependencyIndex].ToTypeDefOrRef(), handleName) ||
                !SignatureComparer.Default.Equals(targetSignature.ParameterTypes[dependencyIndex], targetSignature.ReturnType) ||
                (target.GenericParameters[0].Attributes & (GenericParameterAttributes.NotNullableValueTypeConstraint |
                    GenericParameterAttributes.DefaultConstructorConstraint)) !=
                    (GenericParameterAttributes.NotNullableValueTypeConstraint | GenericParameterAttributes.DefaultConstructorConstraint))
                return false;
            for (var index = 1; index < dependencyIndex; index++)
                if (sourceSignature.ParameterTypes[index].ElementType != ElementType.I4 ||
                    targetSignature.ParameterTypes[index].ElementType != ElementType.I4) return false;

            var parameters = original.LocalVariables[0];
            var result = original.LocalVariables[1];
            if (!IsCoreJobType(parameters.VariableType.ToTypeDefOrRef(), parametersName) ||
                !IsCoreJobType(result.VariableType.ToTypeDefOrRef(), handleName)) return false;
            var code = original.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();
            var pattern = scalar
                ? new[] { CilCode.Ldloca, byRef ? CilCode.Ldarg : CilCode.Ldarga, CilCode.Call, CilCode.Call,
                    CilCode.Ldarg, CilCode.Ldc_I4, CilCode.Call, CilCode.Ldloca, CilCode.Call,
                    CilCode.Stloc, CilCode.Br, CilCode.Ldloc, CilCode.Ret }
                : new[] { CilCode.Ldloca, byRef ? CilCode.Ldarg : CilCode.Ldarga, CilCode.Call, CilCode.Call,
                    CilCode.Ldarg, CilCode.Ldc_I4, CilCode.Call, CilCode.Ldloca, CilCode.Ldarg, CilCode.Ldarg,
                    CilCode.Call, CilCode.Stloc, CilCode.Br, CilCode.Ldloc, CilCode.Ret };
            var scheduleIndex = scalar ? 8 : 10;
            if (code.Length != pattern.Length || code.Where((instruction, index) => instruction.OpCode.Code != pattern[index]).Any() ||
                !ReferenceEquals(code[0].Operand, parameters) || !ReferenceEquals(code[7].Operand, parameters) ||
                !ReferenceEquals(code[1].Operand, sourceMethod.Parameters[0]) ||
                !ReferenceEquals(code[4].Operand, sourceMethod.Parameters[dependencyIndex]) ||
                code[5].Operand is not int mode || mode != (single ? 2 : 1) ||
                !ReferenceEquals(code[scheduleIndex + 1].Operand, result) ||
                !ReferenceEquals(code[scheduleIndex + 3].Operand, result) ||
                code[scheduleIndex + 2].Operand is not CilInstructionLabel jump ||
                !ReferenceEquals(jump.Instruction, code[scheduleIndex + 3]) ||
                !scalar && (!ReferenceEquals(code[8].Operand, sourceMethod.Parameters[1]) ||
                    !ReferenceEquals(code[9].Operand, sourceMethod.Parameters[single ? 1 : 2]))) return false;

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
                !IsCoreJobType(constructor.Signature.ParameterTypes[2].ToTypeDefOrRef(), handleName) ||
                !IsCoreJobType(constructor.Signature.ParameterTypes[3].ToTypeDefOrRef(), "Unity.Jobs.LowLevel.Unsafe.ScheduleMode"))
                return false;
            if (code[scheduleIndex].Operand is not IMethodDescriptor schedule || schedule is MethodSpecification ||
                schedule.Name != (scalar ? "Schedule" : "ScheduleParallelFor") ||
                !IsCoreJobType(schedule.DeclaringType, "Unity.Jobs.LowLevel.Unsafe.JobsUtility") ||
                !InteropDelegateContracts.HasStandardConvention(schedule.Signature) || schedule.Signature.HasThis ||
                schedule.Signature.GenericParameterCount != 0 ||
                !IsCoreJobType(schedule.Signature.ReturnType.ToTypeDefOrRef(), handleName) ||
                schedule.Signature.ParameterTypes.Count != (scalar ? 1 : 3) ||
                schedule.Signature.ParameterTypes[0] is not ByReferenceTypeSignature parameterReference ||
                !SignatureComparer.Default.Equals(parameterReference.BaseType, parameters.VariableType) ||
                !scalar && (schedule.Signature.ParameterTypes[1].ElementType != ElementType.I4 ||
                    schedule.Signature.ParameterTypes[2].ElementType != ElementType.I4)) return false;

            var mappedGetter = FindRestoredJobGetter(getter);
            if (mappedGetter == null) return false;
            target.CilMethodBody.LocalVariables.Clear();
            var value = new GenericParameterSignature(imports.Module, GenericParameterType.Method, 0);
            var importer = imports.Module.DefaultImporter;
            body.Add(byRef ? CilOpCodes.Ldarg : CilOpCodes.Ldarga, target.Parameters[0]);
            body.Add(CilOpCodes.Call, importer.ImportMethod(new MemberReference(ownerType.NewType,
                mappedGetter.Name, mappedGetter.Signature).MakeGenericInstanceMethod(value)));
            for (var index = 1; index < expectedCount; index++) body.Add(CilOpCodes.Ldarg, target.Parameters[index]);
            var helperName = scalar ? nameof(InteropNativeJobSupport.Schedule) :
                single ? nameof(InteropNativeJobSupport.ScheduleFor) : nameof(InteropNativeJobSupport.ScheduleParallelFor);
            var helper = (IMethodDefOrRef)importer.ImportMethod(typeof(InteropNativeJobSupport).GetMethod(helperName));
            body.Add(CilOpCodes.Call, helper.MakeGenericInstanceMethod(value, targetSignature.ReturnType));
            body.Add(CilOpCodes.Ret);
            return true;
        }

        private MethodDefinition FindRestoredJobGetter(MethodSpecification getter, InteropTypeContext getterOwner = null)
        {
            getterOwner ??= ownerType;
            var source = getter.Method.Resolve();
            if (source == null || !InteropManagedJobContracts.IsExtensionMethod(source, getterOwner)) return null;
            var expected = MethodSignature.CreateStatic(imports.Module.CorLibTypeFactory.IntPtr, 1);
            var mapped = getterOwner.TryGetRestoredManagedMethod(source);
            if (mapped == null || mapped.GenericParameters.Count != 1 ||
                !SignatureComparer.Default.Equals(mapped.Signature, expected)) return null;
            var caller = target.GenericParameters[0];
            var callee = mapped.GenericParameters[0];
            return caller.Attributes == callee.Attributes && caller.Constraints.Count == callee.Constraints.Count &&
                callee.Constraints.All(constraint => caller.Constraints.Any(candidate =>
                    SignatureComparer.Default.Equals(candidate.Constraint, constraint.Constraint))) ? mapped : null;
        }

        private static bool JobPayload(TypeSignature signature, bool reference)
        {
            if (reference)
            {
                if (signature is not ByReferenceTypeSignature byReference) return false;
                signature = byReference.BaseType;
            }
            return signature is GenericParameterSignature { ParameterType: GenericParameterType.Method, Index: 0 };
        }

        private static bool JobGenericCall(MethodSpecification method) =>
            InteropDelegateContracts.HasStandardConvention(method.Method.Signature) && !method.Method.Signature.HasThis &&
            method.Method.Signature.GenericParameterCount == 1 && method.Signature.TypeArguments.Count == 1 &&
            JobPayload(method.Signature.TypeArguments[0], false);
    }
}
