// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
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
        private const string AnimationPlayableName = "UnityEngine.Animations.AnimationScriptPlayable";
        private const string AnimationJobName = "UnityEngine.Animations.IAnimationJob";
        private const string AnimationHandleName = "UnityEngine.Playables.PlayableHandle";
        private const string AnimationGraphName = "UnityEngine.Playables.PlayableGraph";

        internal static bool IsAnimationScriptPlayableMethod(MethodDefinition method) =>
            IsAnimationJobType(method.DeclaringType, AnimationPlayableName) &&
            (string)method.Name is "Create" or "CreateHandle" or "GetJobData" or "SetJobData";

        internal static bool ValidateAnimationScriptPlayableMethod(MethodDefinition original, MethodDefinition target,
            InteropTypeContext owner, InteropUnstripTypeResolver resolver, out FieldDefinition handleField, out string reason)
        {
            handleField = null;
            reason = "The original or generated AnimationScriptPlayable method does not match the supported contract.";
            if (!IsAnimationScriptPlayableMethod(original) || !original.HasMethodBody || original.IsAbstract ||
                original.IsPInvokeImpl || original.ImplAttributes != MethodImplAttributes.IL ||
                !original.DeclaringType.IsValueType || original.DeclaringType.GenericParameters.Count != 0 ||
                !HasAnimationMethodSignature(original) || original.GenericParameters.Count != 1 ||
                !HasAnimationJobConstraints(original.GenericParameters[0]) ||
                target == null || !ReferenceEquals(target.DeclaringType, owner.NewType) ||
                target.Name != original.Name || target.IsStatic != original.IsStatic ||
                !HasAnimationMethodSignature(target) || target.GenericParameters.Count != 1 ||
                !SignatureComparer.Default.Equals(target.Signature.ReturnType, resolver.Resolve(original.Signature.ReturnType)))
                return false;
            for (var index = 0; index < original.Signature.ParameterTypes.Count; index++)
                if (!SignatureComparer.Default.Equals(target.Signature.ParameterTypes[index],
                    resolver.Resolve(original.Signature.ParameterTypes[index]))) return false;

            var isCreate = (string)original.Name is "Create" or "CreateHandle";
            if (original.IsPrivate != (original.Name == "CreateHandle") ||
                original.IsPublic != (original.Name != "CreateHandle") || original.IsVirtual == isCreate ||
                original.IsFinal == isCreate || target.IsVirtual != original.IsVirtual || target.IsFinal != original.IsFinal)
                return false;
            var contract = owner.AssemblyContext.TryGetTypeByName(AnimationJobName);
            var producer = owner.AssemblyContext.TryGetTypeByName("UnityEngine.Animations.ProcessAnimationJobStruct`1");
            if (contract?.OriginalType != null || contract?.NewType.IsInterface != true ||
                producer == null || producer.OriginalType != null || producer.NewType.GenericParameters.Count != 1 ||
                !HasAnimationJobConstraints(producer.NewType.GenericParameters[0]))
            {
                reason = "The restored IAnimationJob contract and animation producer are required.";
                return false;
            }

            reason = "AnimationScriptPlayable and its versioned handle require the exact generated blittable layout.";
            var context = owner.AssemblyContext.GlobalContext;
            var handle = context.TryGetTypeByName("UnityEngine.CoreModule", AnimationHandleName);
            var graph = context.TryGetTypeByName("UnityEngine.CoreModule", AnimationGraphName);
            if (!HasAnimationValueLayout(owner, 16) || !HasAnimationHandleLayout(handle) ||
                isCreate && !HasAnimationHandleLayout(graph)) return false;
            var sourceFields = original.DeclaringType.Fields.Where(field => !field.IsStatic).ToArray();
            var fields = owner.NewType.Fields.Where(field => !field.IsStatic).ToArray();
            if (sourceFields.Length != 1 || sourceFields[0].Name != "m_Handle" ||
                !IsCoreJobType(sourceFields[0].Signature?.FieldType.ToTypeDefOrRef(), AnimationHandleName) ||
                fields.Length != 1 || fields[0].Name != "m_Handle" || fields[0].FieldOffset != 0 ||
                !SignatureComparer.Default.Equals(fields[0].Signature?.FieldType, handle.NewType.ToTypeSignature()))
                return false;
            if (!isCreate && !MatchesAnimationJobDataBody(original.CilMethodBody))
            {
                reason = "Only the exact Unity animation job type-check and data-copy body is supported.";
                return false;
            }
            handleField = fields[0];
            reason = null;
            return true;
        }

        private static bool HasAnimationMethodSignature(MethodDefinition method)
        {
            var signature = method.Signature;
            if (signature == null || !InteropDelegateContracts.HasStandardConvention(signature) ||
                signature.GenericParameterCount != 1 || signature.HasThis == method.IsStatic) return false;
            var parameters = signature.ParameterTypes;
            return (string)method.Name switch
            {
                "Create" => method.IsStatic && parameters.Count == 3 &&
                    IsAnimationJobType(signature.ReturnType.ToTypeDefOrRef(), AnimationPlayableName) &&
                    IsCoreJobType(parameters[0].ToTypeDefOrRef(), AnimationGraphName) &&
                    IsAnimationMethodPayload(parameters[1]) && parameters[2].ElementType == ElementType.I4,
                "CreateHandle" => method.IsStatic && parameters.Count == 2 &&
                    IsCoreJobType(signature.ReturnType.ToTypeDefOrRef(), AnimationHandleName) &&
                    IsCoreJobType(parameters[0].ToTypeDefOrRef(), AnimationGraphName) && parameters[1].ElementType == ElementType.I4,
                "GetJobData" => !method.IsStatic && parameters.Count == 0 && IsAnimationMethodPayload(signature.ReturnType),
                "SetJobData" => !method.IsStatic && parameters.Count == 1 &&
                    signature.ReturnType.ElementType == ElementType.Void && IsAnimationMethodPayload(parameters[0]),
                _ => false
            };
        }

        private static bool HasAnimationJobConstraints(GenericParameter parameter) =>
            parameter.Attributes == (GenericParameterAttributes.NotNullableValueTypeConstraint |
                GenericParameterAttributes.DefaultConstructorConstraint) && parameter.Constraints.Count == 2 &&
            parameter.Constraints.Any(constraint => IsAnimationJobType(constraint.Constraint, AnimationJobName)) &&
            parameter.Constraints.Any(constraint =>
                InteropRestoredGenericSupport.GetIntrinsicConstraint(constraint.Constraint?.ToTypeSignature()) == typeof(ValueType));

        private static bool HasAnimationValueLayout(InteropTypeContext type, uint size) =>
            IntPtr.Size == 8 && type?.OriginalType != null && type.NewType.IsValueType && !type.NewType.IsEnum &&
            type.NewType.IsExplicitLayout && type.NewType.GenericParameters.Count == 0 &&
            type.ComputedTypeSpecifics == InteropTypeContext.TypeSpecifics.BlittableStruct &&
            (type.NewType.ClassLayout == null ||
                (type.NewType.ClassLayout.ClassSize == 0 || type.NewType.ClassLayout.ClassSize == size) &&
                (type.NewType.ClassLayout.PackingSize == 0 || type.NewType.ClassLayout.PackingSize == 8));

        private static bool HasAnimationHandleLayout(InteropTypeContext type)
        {
            if (!HasAnimationValueLayout(type, 16)) return false;
            var fields = type.NewType.Fields.Where(field => !field.IsStatic).ToArray();
            return fields.Length == 2 && fields.Any(field => field.Name == "m_Handle" && field.FieldOffset == 0 &&
                field.Signature?.FieldType is CorLibTypeSignature { ElementType: ElementType.I }) &&
                fields.Any(field => field.Name == "m_Version" && field.FieldOffset == 8 &&
                    field.Signature?.FieldType is CorLibTypeSignature { ElementType: ElementType.U4 });
        }

        private bool TranslateAnimationJobData()
        {
            if (!ValidateAnimationScriptPlayableMethod(sourceMethod, target, ownerType, resolver,
                out var handleField, out var reason) || !HasAnimationJobConstraints(target.GenericParameters[0]))
                return Fail("animation-job-contract", reason ?? "The target job constraints were not restored.");
            // Copy the handle out of the receiver before taking its address. The synchronous helper
            // owns typed projection and type validation; Unity's caller-synchronized contract is unchanged.
            var handle = new CilLocalVariable(handleField.Signature.FieldType);
            target.CilMethodBody.LocalVariables.Clear();
            target.CilMethodBody.LocalVariables.Add(handle);
            target.CilMethodBody.InitializeLocals = true;
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Ldfld, imports.Module.DefaultImporter.ImportField(handleField));
            body.Add(CilOpCodes.Stloc, handle);
            body.Add(CilOpCodes.Ldloca, handle);
            body.Add(CilOpCodes.Conv_I);
            var write = sourceMethod.Name == "SetJobData";
            if (write) body.Add(CilOpCodes.Ldarga, target.Parameters[0]);
            var helper = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropNativeAnimationSupport).GetMethod(write
                    ? nameof(InteropNativeAnimationSupport.Write) : nameof(InteropNativeAnimationSupport.Read)));
            body.Add(CilOpCodes.Call, helper.MakeGenericInstanceMethod(
                new GenericParameterSignature(imports.Module, GenericParameterType.Method, 0)));
            body.Add(CilOpCodes.Ret);
            return true;
        }

        private static bool MatchesAnimationJobDataBody(CilMethodBody original)
        {
            var sourceMethod = original.Owner;
            var write = sourceMethod.Name == "SetJobData";
            if (original.ExceptionHandlers.Count != 0 || original.LocalVariables.Count != (write ? 1 : 3)) return false;
            var handle = original.LocalVariables[write ? 0 : 1];
            if (!IsCoreJobType(handle.VariableType.ToTypeDefOrRef(), AnimationHandleName) ||
                !write && (!IsAnimationMethodPayload(original.LocalVariables[0].VariableType) ||
                    !IsAnimationMethodPayload(original.LocalVariables[2].VariableType))) return false;
            var code = original.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();
            CilCode[] pattern = write
                ? new[] { CilCode.Ldarg, CilCode.Call, CilCode.Ldarga, CilCode.Ldarg, CilCode.Call,
                    CilCode.Stloc, CilCode.Ldloca, CilCode.Call, CilCode.Call, CilCode.Call, CilCode.Ret }
                : new[] { CilCode.Ldarg, CilCode.Call, CilCode.Ldarg, CilCode.Call, CilCode.Stloc,
                    CilCode.Ldloca, CilCode.Call, CilCode.Call, CilCode.Ldloca, CilCode.Call, CilCode.Ldloc,
                    CilCode.Stloc, CilCode.Br, CilCode.Ldloc, CilCode.Ret };
            if (code.Length != pattern.Length || code.Where((instruction, index) => AnimationInstructionCode(instruction) != pattern[index]).Any() ||
                !IsAnimationThis(code[0], sourceMethod) || !IsAnimationThis(code[write ? 3 : 2], sourceMethod) ||
                !IsAnimationLocal(code[write ? 5 : 4], handle, write ? 0 : 1) ||
                !IsAnimationLocal(code[write ? 6 : 5], handle, write ? 0 : 1)) return false;
            if (write)
            {
                if (!ReferenceEquals(code[2].Operand, sourceMethod.Parameters[0])) return false;
            }
            else if (!IsAnimationLocal(code[8], original.LocalVariables[0], 0) ||
                !IsAnimationLocal(code[10], original.LocalVariables[0], 0) ||
                !IsAnimationLocal(code[11], original.LocalVariables[2], 2) ||
                !IsAnimationLocal(code[13], original.LocalVariables[2], 2) ||
                code[12].Operand is not CilInstructionLabel branch || !ReferenceEquals(branch.Instruction, code[13]))
                return false;

            if (code[1].Operand is not MethodSpecification check || check.Name != "CheckJobTypeValidity" ||
                !IsAnimationJobType(check.DeclaringType, AnimationPlayableName) || !IsAnimationPayloadCall(check, true) ||
                check.Method.Signature.ReturnType.ElementType != ElementType.Void || check.Method.Signature.ParameterTypes.Count != 0 ||
                code[write ? 4 : 3].Operand is not IMethodDescriptor getHandle || getHandle is MethodSpecification ||
                getHandle.Name != "GetHandle" || !IsAnimationJobType(getHandle.DeclaringType, AnimationPlayableName) ||
                !IsAnimationInstanceGetter(getHandle, handle.VariableType) || !MatchesAnimationHandleGetter(getHandle.Resolve()) ||
                code[write ? 7 : 6].Operand is not IMethodDescriptor data || data is MethodSpecification ||
                data.Name != "GetJobData" || !IsCoreJobType(data.DeclaringType, AnimationHandleName) ||
                !IsAnimationInstanceGetter(data, sourceMethod.DeclaringModule.CorLibTypeFactory.IntPtr)) return false;
            if (code[write ? 8 : 7].Operand is not IMethodDescriptor pointer || pointer is MethodSpecification ||
                pointer.Name != "op_Explicit" ||
                !SignatureComparer.Default.Equals(pointer.DeclaringType, sourceMethod.DeclaringModule.CorLibTypeFactory.IntPtr.Type) ||
                !InteropDelegateContracts.HasStandardConvention(pointer.Signature) || pointer.Signature.HasThis ||
                pointer.Signature.GenericParameterCount != 0 || pointer.Signature.ParameterTypes.Count != 1 ||
                pointer.Signature.ParameterTypes[0] is not CorLibTypeSignature { ElementType: ElementType.I } ||
                !IsAnimationVoidPointer(pointer.Signature.ReturnType)) return false;
            if (code[9].Operand is not MethodSpecification copy ||
                copy.Name != (write ? "CopyStructureToPtr" : "CopyPtrToStructure") ||
                !IsCoreJobType(copy.DeclaringType, "Unity.Collections.LowLevel.Unsafe.UnsafeUtility") ||
                !IsAnimationPayloadCall(copy, false) || copy.Method.Signature.ReturnType.ElementType != ElementType.Void ||
                copy.Method.Signature.ParameterTypes.Count != 2 ||
                copy.Method.Signature.ParameterTypes[write ? 0 : 1] is not ByReferenceTypeSignature payload ||
                !IsAnimationMethodPayload(payload.BaseType) ||
                !IsAnimationVoidPointer(copy.Method.Signature.ParameterTypes[write ? 1 : 0])) return false;
            return true;
        }

        private static bool MatchesAnimationHandleGetter(MethodDefinition method)
        {
            if (method?.CilMethodBody == null || method.CilMethodBody.ExceptionHandlers.Count != 0 ||
                method.CilMethodBody.LocalVariables.Count != 1 ||
                !SignatureComparer.Default.Equals(method.CilMethodBody.LocalVariables[0].VariableType, method.Signature.ReturnType))
                return false;
            var code = method.CilMethodBody.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();
            return code.Length == 6 && AnimationInstructionCode(code[0]) == CilCode.Ldarg &&
                IsAnimationThis(code[0], method) && code[1].OpCode.Code == CilCode.Ldfld &&
                code[1].Operand is IFieldDescriptor field && field.Name == "m_Handle" &&
                SignatureComparer.Default.Equals(field.DeclaringType, method.DeclaringType) &&
                SignatureComparer.Default.Equals(field.Signature.FieldType, method.Signature.ReturnType) &&
                AnimationInstructionCode(code[2]) == CilCode.Stloc && IsAnimationLocal(code[2], method.CilMethodBody.LocalVariables[0], 0) &&
                AnimationInstructionCode(code[3]) == CilCode.Br && code[3].Operand is CilInstructionLabel branch &&
                ReferenceEquals(branch.Instruction, code[4]) && AnimationInstructionCode(code[4]) == CilCode.Ldloc &&
                IsAnimationLocal(code[4], method.CilMethodBody.LocalVariables[0], 0) && code[5].OpCode.Code == CilCode.Ret;
        }

        // Inspect both compact and expanded source IL without changing the source or its diagnostic offsets.
        private static CilCode AnimationInstructionCode(CilInstruction instruction) => instruction.OpCode.Code switch
        {
            CilCode.Ldarg_0 or CilCode.Ldarg_S => CilCode.Ldarg,
            CilCode.Ldarga_S => CilCode.Ldarga,
            CilCode.Ldloc_0 or CilCode.Ldloc_1 or CilCode.Ldloc_2 or CilCode.Ldloc_3 or CilCode.Ldloc_S => CilCode.Ldloc,
            CilCode.Stloc_0 or CilCode.Stloc_1 or CilCode.Stloc_2 or CilCode.Stloc_3 or CilCode.Stloc_S => CilCode.Stloc,
            CilCode.Ldloca_S => CilCode.Ldloca,
            CilCode.Br_S => CilCode.Br,
            _ => instruction.OpCode.Code
        };

        private static bool IsAnimationThis(CilInstruction instruction, MethodDefinition method) =>
            instruction.OpCode.Code == CilCode.Ldarg_0 || ReferenceEquals(instruction.Operand, method.Parameters.ThisParameter);

        private static bool IsAnimationLocal(CilInstruction instruction, CilLocalVariable local, int index) =>
            ReferenceEquals(instruction.Operand, local) || (instruction.OpCode.Code switch
            {
                CilCode.Ldloc_0 or CilCode.Stloc_0 => index == 0,
                CilCode.Ldloc_1 or CilCode.Stloc_1 => index == 1,
                CilCode.Ldloc_2 or CilCode.Stloc_2 => index == 2,
                CilCode.Ldloc_3 or CilCode.Stloc_3 => index == 3,
                _ => false
            });

        private static bool IsAnimationInstanceGetter(IMethodDescriptor method, TypeSignature result) =>
            InteropDelegateContracts.HasStandardConvention(method.Signature) && method.Signature.HasThis &&
            method.Signature.GenericParameterCount == 0 && method.Signature.ParameterTypes.Count == 0 &&
            SignatureComparer.Default.Equals(method.Signature.ReturnType, result);

        private static bool IsAnimationPayloadCall(MethodSpecification method, bool hasThis) =>
            InteropDelegateContracts.HasStandardConvention(method.Method.Signature) && method.Method.Signature.HasThis == hasThis &&
            method.Method.Signature.GenericParameterCount == 1 && method.Signature.TypeArguments.Count == 1 &&
            IsAnimationMethodPayload(method.Signature.TypeArguments[0]);

        private static bool IsAnimationMethodPayload(TypeSignature type) =>
            type is GenericParameterSignature { ParameterType: GenericParameterType.Method, Index: 0 };

        private static bool IsAnimationVoidPointer(TypeSignature type) =>
            type is PointerTypeSignature { BaseType: CorLibTypeSignature { ElementType: ElementType.Void } };
    }
}
