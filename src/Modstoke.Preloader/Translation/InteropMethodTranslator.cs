// SPDX-License-Identifier: GPL-3.0-or-later
// Restored IL translation adapted from Il2CppInterop UnstripTranslator (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Collections;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private readonly MethodDefinition target;
        private readonly InteropTypeContext ownerType;
        private readonly InteropRewriteContext context;
        private readonly InteropRuntimeReferences imports;
        private readonly InteropUnstripTypeResolver resolver;
        private readonly CilInstructionCollection body;
        private readonly Dictionary<CilLocalVariable, CilLocalVariable> locals = new();
        private readonly Dictionary<CilInstruction, CilInstruction> instructions = new();
        private readonly List<(CilInstructionLabel Source, CilInstructionLabel Target)> labels = new();
        private readonly Dictionary<CilInstruction, (int Offset, CilOpCode OpCode, object Operand)> sourceInstructions =
            new(ReferenceEqualityComparer.Instance);
        private CilInstruction currentInstruction;
        private InteropTranslationFailure failure;

        private InteropMethodTranslator(MethodDefinition target, InteropTypeContext type,
            InteropRuntimeReferences imports)
        {
            this.target = target;
            // Parameters may be created before the method is attached to its declaring type.
            target.Parameters.PullUpdatesFromMethodSignature();
            ownerType = type;
            context = type.AssemblyContext.GlobalContext;
            this.imports = imports;
            resolver = new InteropUnstripTypeResolver(context, imports);
            body = target.CilMethodBody.Instructions;
        }

        internal static bool TranslateMethod(MethodDefinition original, MethodDefinition target,
            InteropTypeContext type, InteropRuntimeReferences imports, out InteropTranslationFailure failure)
        {
            failure = null;
            if (original.CilMethodBody == null)
                return true;
            if (IsAnimationScriptPlayableMethod(original) &&
                (!ValidateAnimationScriptPlayableMethod(original, target, type,
                    new InteropUnstripTypeResolver(type.AssemblyContext.GlobalContext, imports), out _, out var reason) ||
                 !HasAnimationJobConstraints(target.GenericParameters[0])))
            {
                failure = new InteropTranslationFailure("animation-job-contract",
                    reason ?? "The target job constraints were not restored.", null, string.Empty, string.Empty);
                return false;
            }
            target.CilMethodBody = new CilMethodBody
            {
                InitializeLocals = original.CilMethodBody.InitializeLocals
            };
            var translator = new InteropMethodTranslator(target, type, imports);
            var translated = translator.Translate(original.CilMethodBody);
            failure = translator.failure;
            return translated;
        }

        private bool Translate(CilMethodBody original)
        {
            sourceMethod = original.Owner;
            // ExpandMacros changes instruction hashes and offsets; identity keys retain the source IL location.
            foreach (var instruction in original.Instructions)
                sourceInstructions.Add(instruction, (instruction.Offset, instruction.OpCode, instruction.Operand));

            if (IsAnimationScriptPlayableMethod(sourceMethod) &&
                (string)sourceMethod.Name is "GetJobData" or "SetJobData")
            {
                return TranslateAnimationJobData();
            }

            foreach (var variable in original.LocalVariables)
            {
                if (!ValidateManagedGenericUse(variable.VariableType)) return false;
                var type = resolver.Resolve(variable.VariableType);
                if (type == null)
                    return Fail("unresolved-local-type", "Could not resolve local variable " + locals.Count + " type: " +
                        FormatDiagnostic(variable.VariableType), operand: variable.VariableType);
                var copy = new CilLocalVariable(type);
                target.CilMethodBody.LocalVariables.Add(copy);
                locals.Add(variable, copy);
            }

            // A source instruction can expand into several instructions, so short branches must expand too.
            original.Instructions.ExpandMacros();
            if (TryTranslateProfilerRecorderArray(original)) return true;
            if (TryTranslateJobRun(original)) return true;
            if (TryTranslateJobSchedule(original)) return true;
            if (TryTranslateTransformJobSchedule(original)) return true;
            if (TryTranslateParticleJobSchedule(original)) return true;
            FindArrayOperands(original);
            if (managedCollectionStackMismatch)
                return Fail("managed-collection-stack-join",
                    "A stack merge would erase the distinction between CLR collection references and native objects.");
            if (managedReferenceStackMismatch)
                return Fail("managed-reference-stack-join",
                    "A stack merge requires a shared managed base without erasing native identity boundaries.");
            if (!PlanNativeSpanBorrows(original)) return false;
            if (FindGenericHashBuffer(original) is { } hashCall)
                return TranslateGenericHashBuffer(original, hashCall);
            if (FindHash128ValueBuffer(original) is { } hashValueCall)
                return TranslateHash128ValueBuffer(original, hashValueCall);
            FindFixedArrayBuffer(original);
            FindUnmanagedDelegateCalls(original);
            if (!FindDelegateFactories(original)) return false;
            if (!FindNativeJobRegistrations(original)) return false;
            if (!FindSharedStaticFactories(original)) return false;
            FindRectangularArrayOperands(original);
            FindAddressOperations(original);
            FindArrayInitializers(original);
            if (!ValidateNativeValueThisWrites(original)) return false;
            CopyNativeValueParameters();
            foreach (var instruction in original.Instructions)
            {
                currentInstruction = instruction;
                if (!ValidateArrayAssignment(instruction) || !ValidateManagedStorageAssignment(instruction) ||
                    !ValidateRectangularArrayAssignment(instruction))
                    return false;
                var copy = TranslateInstruction(instruction);
                if (copy == null)
                    return false;
                instructions.Add(instruction, copy);
            }
            foreach (var pair in labels)
                pair.Target.Instruction = instructions[pair.Source.Instruction];
            if (!CopyExceptionHandlers(original)) return false;
            CompleteFixedArrayBuffer();
            CompleteNativeSpanBorrows();
            return CompleteSharedCatchBodies();
        }

        private CilInstruction TranslateInstruction(CilInstruction instruction)
        {
            if (TryTranslateNativeSpanBorrow(instruction, out var spanCopy))
                return spanCopy;
            if (TryTranslateNativeJobInstruction(instruction, out var jobCopy))
                return jobCopy;
            if (TryTranslateSharedStaticFactory(instruction, out var sharedStaticCopy))
                return sharedStaticCopy;
            if (TryTranslateFixedArrayBuffer(instruction, out var fixedBufferCopy))
                return fixedBufferCopy;
            if (TryTranslateUnmanagedDelegateCall(instruction, out var unmanagedDelegateCopy))
                return unmanagedDelegateCopy;
            if (TryTranslateDelegateInstruction(instruction, out var delegateCopy))
                return delegateCopy;
            if (TryTranslateRectangularInstruction(instruction, out var rectangularCopy))
                return rectangularCopy;
            if (TryTranslateManagedVectorInstruction(instruction, out var vectorCopy))
                return vectorCopy;
            if (TryTranslateAddressOperation(instruction, out var addressCopy))
                return addressCopy;
            if (TryTranslateNativeValueStorage(instruction, out var valueStorageCopy))
                return valueStorageCopy;
            if (TryTranslateManagedCollectionInstruction(instruction, out var collectionCopy))
                return collectionCopy;
            if (TryTranslateNativeReferenceIdentity(instruction, out var identityCopy))
                return identityCopy;
            if (instruction.OpCode.Code == CilCode.Constrained)
                return TranslateConstrainedReference(instruction);
            if (constrainedGenericCalls.Contains(instruction))
                return body.Add(CilOpCodes.Nop);
            if (constrainedValueCalls.TryGetValue(instruction, out var valueImplementation))
                return TranslateValueAwareMethod(CilOpCodes.Call, valueImplementation);
            if (arrayInitializers.TryGetValue(instruction, out var initializer))
                return EmitArrayInitializer(initializer);
            if (arrayInitializerCalls.Contains(instruction))
                return body.Add(CilOpCodes.Nop);
            var operand = instruction.Operand;
            if (operand == null)
                return TranslateOperandless(instruction.OpCode);

            switch (instruction.OpCode.OperandType)
            {
                case CilOperandType.InlineField:
                    return TranslateField(instruction.OpCode, (IFieldDescriptor)operand);
                case CilOperandType.InlineMethod:
                    return TranslateValueAwareMethod(instruction.OpCode, (IMethodDescriptor)operand);
                case CilOperandType.InlineType:
                    return TranslateType(instruction.OpCode, (ITypeDefOrRef)operand);
                case CilOperandType.InlineSig:
                    return FailInstruction("unsupported-indirect-signature", "Indirect call signatures are not supported.");
                case CilOperandType.InlineTok:
                    return TranslateToken(operand);
                case CilOperandType.InlineSwitch when operand is IReadOnlyList<ICilLabel> targets:
                {
                    var copies = new List<ICilLabel>(targets.Count);
                    foreach (var label in targets)
                    {
                        if (label is not CilInstructionLabel instructionLabel)
                            return FailInstruction("unsupported-switch-label", "Switch target " + copies.Count +
                                " is not an instruction label.");
                        copies.Add(CopyBranchLabel(instructionLabel));
                    }
                    return body.Add(instruction.OpCode, copies);
                }
            }

            if (operand is string or Utf8String || operand.GetType().IsPrimitive)
            {
                var copy = new CilInstruction(instruction.OpCode, operand);
                body.Add(copy);
                return copy;
            }
            if (operand is Parameter parameter)
                return body.Add(instruction.OpCode, TargetParameter(parameter));
            if (operand is CilLocalVariable variable)
                return arrayLocalConversions.TryGetValue(instruction, out var arrayConversion)
                    ? EmitArrayLocalConversion(locals[variable], arrayConversion)
                    : body.Add(instruction.OpCode, locals[variable]);
            if (operand is CilInstructionLabel branch)
                return body.Add(instruction.OpCode, CopyBranchLabel(branch));
            return FailInstruction("unsupported-operand", "Unsupported instruction operand kind: " + operand.GetType().FullName);
        }

        // Signature indexes include this; static adapters retain that slot as an explicit receiver.
        private Parameter TargetParameter(Parameter source) =>
            target.Parameters.GetBySignatureIndex(source.MethodSignatureIndex);

        private bool Fail(string reason, string detail, CilInstruction instruction = null, object operand = null)
        {
            if (failure != null)
                return false;

            int? offset = null;
            var opCode = string.Empty;
            if (instruction != null && sourceInstructions.TryGetValue(instruction, out var source))
            {
                offset = source.Offset;
                opCode = FormatDiagnostic(source.OpCode);
                operand ??= source.Operand;
            }
            failure = new InteropTranslationFailure(reason, detail, offset, opCode, FormatDiagnostic(operand));
            return false;
        }

        private CilInstruction FailInstruction(string reason, string detail)
        {
            Fail(reason, detail, currentInstruction);
            return null;
        }

        private static string FormatDiagnostic(object value)
        {
            try
            {
                return value?.ToString() ?? string.Empty;
            }
            catch (Exception)
            {
                // Metadata formatting must not replace the actual translation failure.
                return "<unavailable>";
            }
        }

        private CilInstructionLabel CopyBranchLabel(CilInstructionLabel source)
        {
            var copy = new CilInstructionLabel();
            labels.Add((source, copy));
            return copy;
        }

        private CilInstruction TranslateOperandless(CilOpCode code)
        {
            switch (code.Code)
            {
                case CilCode.Conv_I:
                case CilCode.Conv_U:
                case CilCode.Conv_Ovf_I:
                case CilCode.Conv_Ovf_I_Un:
                case CilCode.Conv_Ovf_U:
                case CilCode.Conv_Ovf_U_Un:
                    return ValidateNativePointerConversion() ? body.Add(code) : null;
                case CilCode.Tailcall:
                    return FailInstruction("unsupported-tail-contract",
                        "A translated call can require native identity checks and does not preserve a tail-call contract.");
                case CilCode.Ldlen:
                    var length = body.Add(CilOpCodes.Callvirt,
                        imports.Module.DefaultImporter.ImportMethod(imports.Il2CppArrayBase_get_Length.Value));
                    body.Add(CilOpCodes.Conv_U);
                    return length;
                case CilCode.Ldelem_R4:
                case CilCode.Ldelem_R8:
                case CilCode.Ldelem_Ref:
                case CilCode.Ldelem_I1:
                case CilCode.Ldelem_U1:
                case CilCode.Ldelem_I2:
                case CilCode.Ldelem_U2:
                case CilCode.Ldelem_I4:
                case CilCode.Ldelem_U4:
                case CilCode.Ldelem_I8:
                case CilCode.Ldelem_I:
                    return TranslateArrayAccess(false);
                case CilCode.Stelem_R4:
                case CilCode.Stelem_R8:
                case CilCode.Stelem_Ref:
                case >= CilCode.Stelem_I and <= CilCode.Stelem_I8:
                    return TranslateArrayAccess(true);
                default:
                    return body.Add(code);
            }
        }

        private CilInstruction EmitArrayAccessor(bool setter, TypeSignature element)
        {
            var method = setter ? imports.Il2CppArrayBase_set_Item.Get(element) : imports.Il2CppArrayBase_get_Item.Get(element);
            return body.Add(CilOpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(method));
        }

        internal static void ReplaceBodyWithException(MethodDefinition method, InteropRuntimeReferences imports)
        {
            method.CilMethodBody = new CilMethodBody();
            var body = method.CilMethodBody.Instructions;
            var exception = imports.Module.DefaultImporter.ImportTypeSignature(typeof(NotSupportedException)).ToTypeDefOrRef();
            var constructor = new MemberReference(exception, ".ctor",
                MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void, imports.Module.CorLibTypeFactory.String));
            body.Add(CilOpCodes.Ldstr, "Method unstripping failed: " + method.FullName);
            body.Add(CilOpCodes.Newobj, constructor);
            body.Add(CilOpCodes.Throw);
            body.Add(CilOpCodes.Ret);
        }
    }
}
