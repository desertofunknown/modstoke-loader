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
        private bool TryTranslateJobRun(CilMethodBody original)
        {
            if (!InteropManagedJobContracts.IsExtensionMethod(sourceMethod, ownerType) ||
                sourceMethod.DeclaringModule.Assembly.Name != "UnityEngine.CoreModule" ||
                sourceMethod.DeclaringType.FullName is not ("Unity.Jobs.IJobExtensions" or
                    "Unity.Jobs.IJobForExtensions" or "Unity.Jobs.IJobParallelForExtensions") ||
                sourceMethod.Name != "Run" && sourceMethod.Name != "RunByRef" ||
                !sourceMethod.IsStatic || !InteropDelegateContracts.HasStandardConvention(sourceMethod.Signature) ||
                sourceMethod.Signature.HasThis || sourceMethod.Signature.GenericParameterCount != 1 ||
                sourceMethod.GenericParameters.Count != 1 || target.GenericParameters.Count != 1 ||
                sourceMethod.Signature.ReturnType.ElementType != ElementType.Void ||
                original.ExceptionHandlers.Count != 0 || original.LocalVariables.Count != 2)
                return false;
            var parallel = sourceMethod.DeclaringType.FullName != "Unity.Jobs.IJobExtensions";
            var byRef = sourceMethod.Name == "RunByRef";
            var sourceSignature = sourceMethod.Signature;
            var targetSignature = target.Signature;
            if (sourceSignature.ParameterTypes.Count != (parallel ? 2 : 1) ||
                targetSignature.ParameterTypes.Count != sourceSignature.ParameterTypes.Count ||
                !target.IsStatic || !InteropDelegateContracts.HasStandardConvention(targetSignature) ||
                targetSignature.HasThis || targetSignature.GenericParameterCount != 1 ||
                targetSignature.ReturnType.ElementType != ElementType.Void ||
                !JobPayload(sourceSignature.ParameterTypes[0], byRef) || !JobPayload(targetSignature.ParameterTypes[0], byRef) ||
                (target.GenericParameters[0].Attributes & (GenericParameterAttributes.NotNullableValueTypeConstraint |
                    GenericParameterAttributes.DefaultConstructorConstraint)) !=
                    (GenericParameterAttributes.NotNullableValueTypeConstraint | GenericParameterAttributes.DefaultConstructorConstraint) ||
                parallel && (sourceSignature.ParameterTypes[1].ElementType != ElementType.I4 ||
                    targetSignature.ParameterTypes[1].ElementType != ElementType.I4)) return false;

            const string parametersName = "Unity.Jobs.LowLevel.Unsafe.JobsUtility/JobScheduleParameters";
            const string handleName = "Unity.Jobs.JobHandle";
            var parameters = original.LocalVariables[0];
            var dependency = original.LocalVariables[1];
            if (!IsCoreJobType(parameters.VariableType.ToTypeDefOrRef(), parametersName) ||
                !IsCoreJobType(dependency.VariableType.ToTypeDefOrRef(), handleName)) return false;
            var code = original.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();
            CilCode[] pattern = parallel
                ? new[] { CilCode.Ldloca, byRef ? CilCode.Ldarg : CilCode.Ldarga, CilCode.Call, CilCode.Call,
                    CilCode.Ldloca, CilCode.Initobj, CilCode.Ldloc, CilCode.Ldc_I4, CilCode.Call, CilCode.Ldloca,
                    CilCode.Ldarg, CilCode.Ldarg, CilCode.Call, CilCode.Pop, CilCode.Ret }
                : new[] { CilCode.Ldloca, byRef ? CilCode.Ldarg : CilCode.Ldarga, CilCode.Call, CilCode.Call,
                    CilCode.Ldloca, CilCode.Initobj, CilCode.Ldloc, CilCode.Ldc_I4, CilCode.Call, CilCode.Ldloca,
                    CilCode.Call, CilCode.Pop, CilCode.Ret };
            if (code.Length != pattern.Length || code.Where((instruction, index) => instruction.OpCode.Code != pattern[index]).Any() ||
                !ReferenceEquals(code[0].Operand, parameters) || !ReferenceEquals(code[9].Operand, parameters) ||
                !ReferenceEquals(code[1].Operand, sourceMethod.Parameters[0]) ||
                !ReferenceEquals(code[4].Operand, dependency) || !ReferenceEquals(code[6].Operand, dependency) ||
                code[5].Operand is not ITypeDefOrRef initialized || !IsCoreJobType(initialized, handleName) ||
                code[7].Operand is not int mode || mode != 0 || parallel &&
                    (!ReferenceEquals(code[10].Operand, sourceMethod.Parameters[1]) ||
                     !ReferenceEquals(code[11].Operand, sourceMethod.Parameters[1]))) return false;

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
            if (code[8].Operand is not IMethodDescriptor constructor || constructor is MethodSpecification ||
                constructor.Name != ".ctor" || !IsCoreJobType(constructor.DeclaringType, parametersName) ||
                !InteropDelegateContracts.HasStandardConvention(constructor.Signature) || !constructor.Signature.HasThis ||
                constructor.Signature.GenericParameterCount != 0 || constructor.Signature.ReturnType.ElementType != ElementType.Void ||
                constructor.Signature.ParameterTypes.Count != 4 ||
                !SignatureComparer.Default.Equals(constructor.Signature.ParameterTypes[0], address.Method.Signature.ReturnType) ||
                constructor.Signature.ParameterTypes[1].ElementType != ElementType.I ||
                !IsCoreJobType(constructor.Signature.ParameterTypes[2].ToTypeDefOrRef(), handleName) ||
                !IsCoreJobType(constructor.Signature.ParameterTypes[3].ToTypeDefOrRef(), "Unity.Jobs.LowLevel.Unsafe.ScheduleMode"))
                return false;
            if (code[parallel ? 12 : 10].Operand is not IMethodDescriptor schedule || schedule is MethodSpecification ||
                schedule.Name != (parallel ? "ScheduleParallelFor" : "Schedule") ||
                !IsCoreJobType(schedule.DeclaringType, "Unity.Jobs.LowLevel.Unsafe.JobsUtility") ||
                !InteropDelegateContracts.HasStandardConvention(schedule.Signature) || schedule.Signature.HasThis ||
                schedule.Signature.GenericParameterCount != 0 ||
                !IsCoreJobType(schedule.Signature.ReturnType.ToTypeDefOrRef(), handleName) ||
                schedule.Signature.ParameterTypes.Count != (parallel ? 3 : 1) ||
                schedule.Signature.ParameterTypes[0] is not ByReferenceTypeSignature parameterReference ||
                !SignatureComparer.Default.Equals(parameterReference.BaseType, parameters.VariableType) ||
                parallel && (schedule.Signature.ParameterTypes[1].ElementType != ElementType.I4 ||
                    schedule.Signature.ParameterTypes[2].ElementType != ElementType.I4)) return false;

            var mappedGetter = FindRestoredJobGetter(getter);
            if (mappedGetter == null) return false;

            // Replace the entire verified immediate-run path, not individual pointer escapes.
            target.CilMethodBody.LocalVariables.Clear();
            var value = new GenericParameterSignature(imports.Module, GenericParameterType.Method, 0);
            var importer = imports.Module.DefaultImporter;
            body.Add(byRef ? CilOpCodes.Ldarg : CilOpCodes.Ldarga, target.Parameters[0]);
            body.Add(CilOpCodes.Call, importer.ImportMethod(new MemberReference(ownerType.NewType,
                mappedGetter.Name, mappedGetter.Signature).MakeGenericInstanceMethod(value)));
            if (parallel) body.Add(CilOpCodes.Ldarg, target.Parameters[1]);
            var helper = (IMethodDefOrRef)importer.ImportMethod(typeof(InteropNativeJobSupport).GetMethod(
                parallel ? nameof(InteropNativeJobSupport.RunParallelFor) : nameof(InteropNativeJobSupport.Run)));
            body.Add(CilOpCodes.Call, helper.MakeGenericInstanceMethod(value));
            body.Add(CilOpCodes.Ret);
            return true;
        }
    }
}
