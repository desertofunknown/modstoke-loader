// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Collections;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private readonly record struct StackValue(TypeSignature Type, bool IsNull = false, TypeSignature BoxedType = null);

        // Null lists mean unknown stack shape; null types mean unknown individual values.
        private readonly Dictionary<CilInstruction, List<StackValue>> inputStacks =
            new(ReferenceEqualityComparer.Instance);
        private TypeSignature sourceReturnType;
        private TypeSignature sourceObjectType;
        private bool managedCollectionStackMismatch;
        private bool managedReferenceStackMismatch;

        private void FindArrayOperands(CilMethodBody original)
        {
            sourceReturnType = original.Owner.Signature.ReturnType;
            sourceObjectType = original.Owner.DeclaringModule.CorLibTypeFactory.Object;
            if (original.Instructions.Count == 0)
                return;
            var positions = new Dictionary<CilInstruction, int>(ReferenceEqualityComparer.Instance);
            for (var index = 0; index < original.Instructions.Count; index++)
                positions.Add(original.Instructions[index], index);
            var pending = new Queue<CilInstruction>();
            Merge(original.Instructions[0], new List<StackValue>());
            foreach (var handler in original.ExceptionHandlers)
            {
                var catches = handler.HandlerType is CilExceptionHandlerType.Exception or CilExceptionHandlerType.Filter;
                MergeLabel(handler.HandlerStart, catches
                    ? new List<StackValue> { new(handler.HandlerType == CilExceptionHandlerType.Filter
                        ? sourceObjectType : handler.ExceptionType?.ToTypeSignature()) }
                    : new List<StackValue>());
                MergeLabel(handler.FilterStart, new List<StackValue> { new(sourceObjectType) });
            }

            while (pending.Count > 0)
            {
                var instruction = pending.Dequeue();
                var input = inputStacks[instruction];
                var output = TransferStack(original, instruction, input);
                if (instruction.Operand is ICilLabel branch)
                    MergeLabel(branch, output);
                else if (instruction.Operand is IReadOnlyList<ICilLabel> branches)
                    foreach (var label in branches)
                        MergeLabel(label, output);

                if (instruction.OpCode.FlowControl is CilFlowControl.Branch or CilFlowControl.Return or CilFlowControl.Throw ||
                    instruction.OpCode.Code is CilCode.Jmp or CilCode.Endfinally or CilCode.Endfilter)
                    continue;
                var next = positions[instruction] + 1;
                if (next < original.Instructions.Count)
                    Merge(original.Instructions[next], output);
            }

            void MergeLabel(ICilLabel label, List<StackValue> stack)
            {
                if (label is CilInstructionLabel { Instruction: not null } targetLabel &&
                    positions.ContainsKey(targetLabel.Instruction))
                    Merge(targetLabel.Instruction, stack);
            }

            void Merge(CilInstruction instruction, List<StackValue> incoming)
            {
                if (!inputStacks.TryGetValue(instruction, out var current))
                {
                    inputStacks.Add(instruction, incoming == null ? null : new List<StackValue>(incoming));
                    pending.Enqueue(instruction);
                    return;
                }
                if (current == null)
                {
                    if (incoming?.Exists(IsManagedCollectionStackValue) == true) managedCollectionStackMismatch = true;
                    if (incoming?.Exists(IsManagedReferenceStackValue) == true) managedReferenceStackMismatch = true;
                    return;
                }
                if (incoming == null || incoming.Count != current.Count)
                {
                    if (current.Exists(IsManagedCollectionStackValue) || incoming?.Exists(IsManagedCollectionStackValue) == true)
                        managedCollectionStackMismatch = true;
                    if (current.Exists(IsManagedReferenceStackValue) || incoming?.Exists(IsManagedReferenceStackValue) == true)
                        managedReferenceStackMismatch = true;
                    inputStacks[instruction] = null;
                    pending.Enqueue(instruction);
                    return;
                }
                var changed = false;
                for (var index = 0; index < current.Count; index++)
                {
                    var merged = MergeStackValue(current[index], incoming[index]);
                    if (SameStackValue(current[index], merged))
                        continue;
                    current[index] = merged;
                    changed = true;
                }
                if (changed)
                    pending.Enqueue(instruction);
            }
        }

        private static bool SameStackValue(StackValue left, StackValue right) =>
            left.IsNull == right.IsNull && SignatureComparer.Default.Equals(left.Type, right.Type) &&
            SignatureComparer.Default.Equals(left.BoxedType, right.BoxedType);

        private StackValue MergeStackValue(StackValue left, StackValue right)
        {
            if (SameStackValue(left, right))
                return left;
            if (left.IsNull && IsStackReference(right.Type))
                return right;
            if (right.IsNull && IsStackReference(left.Type))
                return left;
            if (SignatureComparer.Default.Equals(left.Type, right.Type))
                return new StackValue(left.Type);
            if (InteropManagedCollectionLowering.CanAssign(context, left.Type, right.Type)) return right;
            if (InteropManagedCollectionLowering.CanAssign(context, right.Type, left.Type)) return left;
            if (CanAssignRestoredDelegate(left.Type, right.Type)) return new StackValue(right.Type);
            if (CanAssignRestoredDelegate(right.Type, left.Type)) return new StackValue(left.Type);
            if (InteropRestorationPlan.For(context).CommonManagedBase(left.Type, right.Type) is { } commonBase)
                return new StackValue(commonBase);
            if (IsManagedCollectionStackValue(left) || IsManagedCollectionStackValue(right))
                managedCollectionStackMismatch = true;
            if (IsManagedReferenceStackValue(left) || IsManagedReferenceStackValue(right))
                managedReferenceStackMismatch = true;
            // CLR generic wrapper classes are invariant, even when the original arrays are covariant.
            // In particular, never widen two distinct array signatures to object[].
            return default;
        }

        private static bool IsStackReference(TypeSignature type) => type != null &&
            !type.IsValueType && type is not (GenericParameterSignature or PointerTypeSignature or ByReferenceTypeSignature);

        private bool IsManagedCollectionStackValue(StackValue value) =>
            InteropManagedCollectionLowering.IsStorage(context,
                value.Type is ByReferenceTypeSignature reference ? reference.BaseType : value.Type);

        private bool IsManagedReferenceStackValue(StackValue value) =>
            InteropRestorationPlan.For(context).IsManagedReference(
                value.Type is ByReferenceTypeSignature reference ? reference.BaseType : value.Type);

        private bool TryGetStackValue(CilInstruction instruction, int depth, out StackValue value)
        {
            value = default;
            if (!inputStacks.TryGetValue(instruction, out var stack) || stack == null || stack.Count <= depth)
                return false;
            value = stack[stack.Count - depth - 1];
            return value.Type != null || value.IsNull;
        }

        // Also usable by whole-pattern lowerings (for example RuntimeHelpers.InitializeArray).
        private bool TryGetArrayOperand(CilInstruction instruction, int depth,
            out SzArrayTypeSignature sourceArray, out TypeSignature rewrittenArray, out TypeSignature element)
        {
            sourceArray = null;
            rewrittenArray = null;
            element = null;
            if (!TryGetStackValue(instruction, depth, out var value) || value.Type is not SzArrayTypeSignature array)
                return false;
            sourceArray = array;
            rewrittenArray = resolver.Resolve(array);
            element = resolver.Resolve(array.BaseType);
            return rewrittenArray != null && element != null &&
                element is not (PointerTypeSignature or ByReferenceTypeSignature);
        }

        // Endfinally/endfault discard the stack, not an unknown number of call arguments.
        private static bool ClearsEvaluationStack(CilCode code) =>
            code is CilCode.Leave or CilCode.Leave_S or CilCode.Endfinally;

        private List<StackValue> TransferStack(CilMethodBody original, CilInstruction instruction, List<StackValue> input)
        {
            if (ClearsEvaluationStack(instruction.OpCode.Code))
                return new List<StackValue>();
            if (input == null)
                return null;
            if (instruction.OpCode.Code == CilCode.Dup)
            {
                if (input.Count == 0)
                    return null;
                var duplicate = new List<StackValue>(input) { input[input.Count - 1] };
                return duplicate;
            }
            var pops = instruction.GetStackPopCount(original);
            var pushes = instruction.GetStackPushCount();
            if (pops < 0 || pushes < 0 || input.Count < pops)
                return null;
            var stack = new List<StackValue>(input);
            stack.RemoveRange(stack.Count - pops, pops);
            if (pushes == 1)
                stack.Add(GetPushedValue(instruction, input));
            else
                for (var index = 0; index < pushes; index++)
                    stack.Add(default);
            return stack;
        }

        private StackValue GetPushedValue(CilInstruction instruction, List<StackValue> input)
        {
            var corlib = imports.Module.CorLibTypeFactory;
            var code = instruction.OpCode.Code;
            switch (code)
            {
                case CilCode.Ldnull:
                    return new StackValue(null, true);
                case CilCode.Ldtoken when instruction.Operand is ITypeDefOrRef:
                    // TranslateToken emits the native runtime handle, not a CLR reflection token.
                    return new StackValue(context.CorLib.TryGetTypeByName("System.RuntimeTypeHandle")
                        ?.OriginalType?.ToTypeSignature());
                case CilCode.Ldloc when instruction.Operand is CilLocalVariable local:
                    return new StackValue(local.VariableType);
                case CilCode.Ldarg when instruction.Operand is Parameter argument:
                    return new StackValue(SourceParameterType(argument));
                case CilCode.Ldloca when instruction.Operand is CilLocalVariable local:
                    return new StackValue(local.VariableType.MakeByReferenceType());
                case CilCode.Ldarga when instruction.Operand is Parameter argument:
                    return new StackValue(SourceParameterType(argument).MakeByReferenceType());
                case CilCode.Ldfld:
                case CilCode.Ldsfld:
                case CilCode.Ldflda:
                case CilCode.Ldsflda:
                    if (instruction.Operand is IFieldDescriptor field && field.Signature != null)
                    {
                        var fieldType = field.Signature.FieldType.InstantiateGenericTypes(GenericContext.FromField(field));
                        return new StackValue(code is CilCode.Ldflda or CilCode.Ldsflda
                            ? fieldType.MakeByReferenceType() : fieldType);
                    }
                    return default;
                case CilCode.Call:
                case CilCode.Callvirt:
                case CilCode.Newobj:
                    if (instruction.Operand is IMethodDescriptor method)
                    {
                        var result = code == CilCode.Newobj ? method.DeclaringType?.ToTypeSignature() :
                            method.Signature?.ReturnType.InstantiateGenericTypes(GenericContext.FromMethod(method));
                        // The modifier belongs to the return contract, not the evaluation
                        // stack. Native-reference producers retain its readonly restriction.
                        if (TryGetReadonlyReference(result, out var referenceResult)) result = referenceResult;
                        return new StackValue(result);
                    }
                    return default;
                case CilCode.Newarr when instruction.Operand is ITypeDefOrRef element:
                    return new StackValue(element.ToTypeSignature().MakeSzArrayType());
                case CilCode.Unbox_Any:
                case CilCode.Ldobj:
                    return new StackValue((instruction.Operand as ITypeDefOrRef)?.ToTypeSignature());
                case CilCode.Castclass:
                case CilCode.Isinst:
                    if (instruction.Operand is not ITypeDefOrRef instanceType) return default;
                    var testedType = instanceType.ToTypeSignature();
                    return new StackValue(testedType.IsValueType ||
                        testedType is GenericParameterSignature generic && !IsReferenceGeneric(generic)
                            ? sourceObjectType : testedType);
                case CilCode.Unbox when instruction.Operand is ITypeDefOrRef boxed:
                    return new StackValue(boxed.ToTypeSignature().MakeByReferenceType());
                case CilCode.Box:
                    // Approved CLR collection boxes retain their interface contract on the stack.
                    // Treating them as the source System.Object would select native object APIs.
                    if (instruction.Operand is ITypeDefOrRef buffer &&
                        InteropManagedCollectionLowering.TryGetBoxContract(context, buffer.ToTypeSignature(), out var contract))
                        return new StackValue(contract);
                    return new StackValue(sourceObjectType, BoxedType: (instruction.Operand as ITypeDefOrRef)?.ToTypeSignature());
                case CilCode.Ldind_Ref:
                    return input[input.Count - 1].Type is ByReferenceTypeSignature reference
                        ? new StackValue(reference.BaseType) : default;
                case CilCode.Ldelem:
                    return new StackValue((instruction.Operand as ITypeDefOrRef)?.ToTypeSignature());
                case CilCode.Ldelem_Ref:
                    return input[input.Count - 2].Type is SzArrayTypeSignature array
                        ? new StackValue(array.BaseType) : default;
                case CilCode.Ldelema when instruction.Operand is ITypeDefOrRef addressed:
                    return new StackValue(addressed.ToTypeSignature().MakeByReferenceType());
                case CilCode.Ldstr:
                    return new StackValue(corlib.String);
                case CilCode.Ldc_I4:
                case CilCode.Sizeof:
                case CilCode.Ldelem_I1:
                case CilCode.Ldelem_U1:
                case CilCode.Ldelem_I2:
                case CilCode.Ldelem_U2:
                case CilCode.Ldelem_I4:
                case CilCode.Ldelem_U4:
                case CilCode.Ldind_I1:
                case CilCode.Ldind_U1:
                case CilCode.Ldind_I2:
                case CilCode.Ldind_U2:
                case CilCode.Ldind_I4:
                case CilCode.Ldind_U4:
                case CilCode.Ceq:
                case CilCode.Cgt:
                case CilCode.Cgt_Un:
                case CilCode.Clt:
                case CilCode.Clt_Un:
                case CilCode.Conv_I1:
                case CilCode.Conv_U1:
                case CilCode.Conv_I2:
                case CilCode.Conv_U2:
                case CilCode.Conv_I4:
                case CilCode.Conv_U4:
                case CilCode.Conv_Ovf_I1:
                case CilCode.Conv_Ovf_I1_Un:
                case CilCode.Conv_Ovf_U1:
                case CilCode.Conv_Ovf_U1_Un:
                case CilCode.Conv_Ovf_I2:
                case CilCode.Conv_Ovf_I2_Un:
                case CilCode.Conv_Ovf_U2:
                case CilCode.Conv_Ovf_U2_Un:
                case CilCode.Conv_Ovf_I4:
                case CilCode.Conv_Ovf_I4_Un:
                case CilCode.Conv_Ovf_U4:
                case CilCode.Conv_Ovf_U4_Un:
                    return new StackValue(corlib.Int32);
                case CilCode.Ldc_I8:
                case CilCode.Ldelem_I8:
                case CilCode.Ldind_I8:
                case CilCode.Conv_I8:
                case CilCode.Conv_U8:
                case CilCode.Conv_Ovf_I8:
                case CilCode.Conv_Ovf_I8_Un:
                case CilCode.Conv_Ovf_U8:
                case CilCode.Conv_Ovf_U8_Un:
                    return new StackValue(corlib.Int64);
                case CilCode.Ldlen:
                case CilCode.Ldelem_I:
                case CilCode.Ldind_I:
                case CilCode.Conv_I:
                case CilCode.Conv_U:
                case CilCode.Conv_Ovf_I:
                case CilCode.Conv_Ovf_I_Un:
                case CilCode.Conv_Ovf_U:
                case CilCode.Conv_Ovf_U_Un:
                    return new StackValue(corlib.IntPtr);
                case CilCode.Localloc:
                    return new StackValue(corlib.Void.MakePointerType());
                case CilCode.Ldc_R4:
                case CilCode.Ldelem_R4:
                case CilCode.Ldind_R4:
                case CilCode.Conv_R4:
                    return new StackValue(corlib.Single);
                case CilCode.Ldc_R8:
                case CilCode.Ldelem_R8:
                case CilCode.Ldind_R8:
                case CilCode.Conv_R8:
                case CilCode.Conv_R_Un:
                    return new StackValue(corlib.Double);
                case CilCode.Neg:
                case CilCode.Not:
                case CilCode.Shl:
                case CilCode.Shr:
                case CilCode.Shr_Un:
                    return NumericStackValue(input[input.Count - (code is CilCode.Neg or CilCode.Not ? 1 : 2)].Type);
                case CilCode.Add:
                case CilCode.Add_Ovf:
                case CilCode.Add_Ovf_Un:
                case CilCode.Sub:
                case CilCode.Sub_Ovf:
                case CilCode.Sub_Ovf_Un:
                case CilCode.Mul:
                case CilCode.Mul_Ovf:
                case CilCode.Mul_Ovf_Un:
                case CilCode.Div:
                case CilCode.Div_Un:
                case CilCode.Rem:
                case CilCode.Rem_Un:
                case CilCode.And:
                case CilCode.Or:
                case CilCode.Xor:
                    var leftValue = input[input.Count - 2];
                    var rightValue = input[input.Count - 1];
                    var left = NumericStackValue(input[input.Count - 2].Type);
                    var right = NumericStackValue(input[input.Count - 1].Type);
                    // Keep unmanaged pointer identity through byte offsets. Managed refs are
                    // tracked separately and cannot acquire a raw-pointer contract here.
                    if (code is CilCode.Add or CilCode.Add_Ovf_Un or CilCode.Sub or CilCode.Sub_Ovf_Un)
                    {
                        if (leftValue.Type is PointerTypeSignature &&
                            right.Type?.ElementType is ElementType.I4 or ElementType.I)
                            return leftValue;
                        if (code is CilCode.Add or CilCode.Add_Ovf_Un &&
                            rightValue.Type is PointerTypeSignature &&
                            left.Type?.ElementType is ElementType.I4 or ElementType.I)
                            return rightValue;
                        if (code is CilCode.Sub or CilCode.Sub_Ovf_Un &&
                            leftValue.Type is PointerTypeSignature && rightValue.Type is PointerTypeSignature)
                            return new StackValue(corlib.IntPtr);
                    }
                    // sizeof produces int32 even when an offset has already been widened.
                    if (left.Type?.ElementType == ElementType.I && right.Type?.ElementType == ElementType.I4 ||
                        left.Type?.ElementType == ElementType.I4 && right.Type?.ElementType == ElementType.I)
                        return new StackValue(corlib.IntPtr);
                    return SameStackValue(left, right) ? left : default;
                default:
                    return default;
            }
        }

        private StackValue NumericStackValue(TypeSignature type)
        {
            var corlib = imports.Module.CorLibTypeFactory;
            return ArrayStorageType(type) switch
            {
                ElementType.Boolean or ElementType.Char or ElementType.I1 or ElementType.U1 or
                    ElementType.I2 or ElementType.U2 or ElementType.I4 or ElementType.U4 => new StackValue(corlib.Int32),
                ElementType.I8 or ElementType.U8 => new StackValue(corlib.Int64),
                ElementType.I or ElementType.U => new StackValue(corlib.IntPtr),
                ElementType.R4 => new StackValue(corlib.Single),
                ElementType.R8 => new StackValue(corlib.Double),
                _ => default
            };
        }
    }
}
