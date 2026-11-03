// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using System.Linq;
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
        private sealed class FixedNativeArrayBuffer
        {
            internal CilInstruction Producer;
            internal CilInstruction PointerConversion;
            internal CilLocalVariable SourcePin;
            internal TypeSignature OwnerType, Element;
            internal CilInstruction OutputAddress;
            internal TypeSignature OutputElement;
            internal CilLocalVariable OutputPin;
            internal bool GenericElement;
            internal CilInstruction OffsetLoad;
            internal Parameter OffsetParameter;
            internal CilLocalVariable OffsetCount;
            internal readonly HashSet<CilInstruction> OffsetOperations = new(ReferenceEqualityComparer.Instance);
            internal readonly HashSet<CilInstruction> Clears = new(ReferenceEqualityComparer.Instance);
            internal CilLocalVariable Owner, Index, Handle, Data;
        }

        private FixedNativeArrayBuffer fixedArrayBuffer;

        private void FindFixedArrayBuffer(CilMethodBody original)
        {
            // Existing EH and repeated acquisitions need their own nesting/release contract.
            if (original.ExceptionHandlers.Count != 0 || sourceMethod.IsConstructor ||
                sourceMethod.Signature.ReturnType is ByReferenceTypeSignature or PointerTypeSignature or FunctionPointerTypeSignature)
                return;
            var hashBuffer = FindFixedHashArrayBuffer(original);
            if (hashBuffer != null)
            {
                InitializeFixedArrayBuffer(hashBuffer);
                return;
            }
            if (!original.LocalVariables.Any(variable => variable.VariableType is PinnedTypeSignature { BaseType: SzArrayTypeSignature }))
                return;
            var source = original.Instructions;
            var entries = new HashSet<CilInstruction>(ReferenceEqualityComparer.Instance);
            var edges = new List<(int From, int To)>();
            for (var index = 0; index < source.Count; index++)
            {
                if (source[index].Operand is ICilLabel branch) AddBranch(index, branch);
                else if (source[index].Operand is IReadOnlyList<ICilLabel> targets)
                    foreach (var targetLabel in targets) AddBranch(index, targetLabel);
            }

            FixedNativeArrayBuffer candidate = null;
            for (var index = 2; index + 2 < source.Count; index++)
            {
                if (source[index].OpCode.Code != CilCode.Ldelema ||
                    source[index].Operand is not ITypeDefOrRef operand ||
                    source[index - 2] is not { OpCode.Code: CilCode.Ldloc, Operand: CilLocalVariable arrayLocal } ||
                    arrayLocal.VariableType is not PinnedTypeSignature { BaseType: SzArrayTypeSignature array } ||
                    source[index - 1] is not { OpCode.Code: CilCode.Ldc_I4, Operand: 0 } ||
                    source[index + 1].OpCode.Code is not (CilCode.Conv_U or CilCode.Conv_I) ||
                    source[index + 2] is not { OpCode.Code: CilCode.Stloc, Operand: CilLocalVariable pointerLocal } ||
                    pointerLocal.VariableType is not PointerTypeSignature pointer ||
                    !SignatureComparer.Default.Equals(array.BaseType, operand.ToTypeSignature()) ||
                    !SignatureComparer.Default.Equals(array.BaseType, pointer.BaseType) ||
                    entries.Contains(source[index - 1]) || entries.Contains(source[index]) ||
                    entries.Contains(source[index + 1]) || entries.Contains(source[index + 2]) ||
                    edges.Any(edge => edge.From >= index && edge.To <= index)) continue;

                var element = resolver.Resolve(array.BaseType);
                var rewrittenArray = resolver.Resolve(array);
                var genericElement = array.BaseType is GenericParameterSignature generic && ProvesUnmanagedBufferElement(generic);
                if (rewrittenArray is not GenericInstanceTypeSignature vector) continue;
                if (genericElement)
                {
                    if (element is not GenericParameterSignature ||
                        !SignatureComparer.Default.Equals(vector.GenericType, imports.Il2CppArrayBase.ToTypeDefOrRef())) continue;
                }
                else
                {
                    if (element?.IsValueType != true ||
                        !SignatureComparer.Default.Equals(vector.GenericType, imports.Il2CppStructArray.ToTypeDefOrRef())) continue;
                    var primitive = element is CorLibTypeSignature &&
                        element.ElementType is >= ElementType.Boolean and <= ElementType.R8 or ElementType.I or ElementType.U;
                    if (!primitive && FindMappedSourceOwner(array.BaseType)?.ComputedTypeSpecifics !=
                        InteropTypeContext.TypeSpecifics.BlittableStruct) continue;
                }
                var found = new FixedNativeArrayBuffer
                {
                    Producer = source[index], PointerConversion = source[index + 1],
                    SourcePin = arrayLocal, OwnerType = rewrittenArray,
                    Element = element, GenericElement = genericElement
                };
                if (!TryFindFixedPointerCall(original, index + 2, pointerLocal, array.BaseType, entries, found,
                    out var callPosition)) continue;
                var valid = true;
                for (var position = 0; position < source.Count; position++)
                {
                    if (!ReferenceEquals(source[position].Operand, arrayLocal)) continue;
                    if (source[position].OpCode.Code == CilCode.Ldloc) continue;
                    if (source[position].OpCode.Code != CilCode.Stloc) { valid = false; break; }
                    if (position < index) continue;
                    if (position <= callPosition || position == 0 || source[position - 1].OpCode.Code != CilCode.Ldnull ||
                        entries.Contains(source[position]) || edges.Any(edge => edge.From >= position && edge.To <= callPosition))
                    { valid = false; break; }
                    found.Clears.Add(source[position]);
                }
                if (!valid) continue;
                if (candidate != null) return;
                candidate = found;
            }
            if (candidate == null) return;
            InitializeFixedArrayBuffer(candidate);

            void AddBranch(int from, ICilLabel label)
            {
                if (label is not CilInstructionLabel { Instruction: not null } branch) return;
                var destination = source.IndexOf(branch.Instruction);
                if (destination < 0) return;
                entries.Add(branch.Instruction);
                edges.Add((from, destination));
            }
        }

        private void InitializeFixedArrayBuffer(FixedNativeArrayBuffer plan)
        {
            plan.Owner = AddArrayLocal(plan.OwnerType);
            plan.Index = AddArrayLocal(imports.Module.CorLibTypeFactory.Int32);
            plan.Handle = AddArrayLocal(imports.Module.CorLibTypeFactory.IntPtr);
            plan.Data = AddArrayLocal(imports.Module.CorLibTypeFactory.IntPtr);
            if (plan.OutputAddress != null)
            {
                locals[plan.SourcePin].VariableType = imports.Module.CorLibTypeFactory.IntPtr;
                plan.OutputPin = AddArrayLocal(plan.OutputElement.MakeByReferenceType().MakePinnedType());
            }
            target.CilMethodBody.InitializeLocals = true;
            fixedArrayBuffer = plan;
        }

        private bool ProvesUnmanagedBufferElement(GenericParameterSignature signature)
        {
            if (signature.ParameterType != GenericParameterType.Method || signature.Index >= sourceMethod.GenericParameters.Count)
                return false;
            var parameter = sourceMethod.GenericParameters[signature.Index];
            return (parameter.Attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0 &&
                parameter.Constraints.Any(constraint => constraint.Constraint?.ToTypeSignature() is
                    CustomModifierTypeSignature { IsRequired: true } modifier &&
                    InteropRestoredGenericSupport.GetIntrinsicConstraint(modifier.BaseType) == typeof(System.ValueType) &&
                    modifier.ModifierType.FullName == "System.Runtime.InteropServices.UnmanagedType" &&
                    InteropRestoredTypeSupport.GetAssemblyName(modifier.ModifierType.ToTypeSignature()) is
                        "mscorlib" or "netstandard" or "System.Runtime.InteropServices" or "System.Runtime" or "System.Private.CoreLib");
        }

        private bool TryFindFixedPointerCall(CilMethodBody original, int assignment, CilLocalVariable pointer,
            TypeSignature element, HashSet<CilInstruction> entries, FixedNativeArrayBuffer plan, out int callPosition)
        {
            callPosition = -1;
            var source = original.Instructions;
            var load = -1;
            for (var index = 0; index < source.Count; index++)
            {
                if (!ReferenceEquals(source[index].Operand, pointer)) continue;
                if (source[index].OpCode.Code == CilCode.Ldloc)
                {
                    if (load >= 0 || index <= assignment) return false;
                    load = index;
                }
                else if (source[index].OpCode.Code != CilCode.Stloc || index != assignment &&
                    (index < 2 || source[index - 1].OpCode.Code is not (CilCode.Conv_U or CilCode.Conv_I) ||
                     source[index - 2] is not { OpCode.Code: CilCode.Ldc_I4, Operand: 0 })) return false;
            }
            if (load < 0) return false;
            if (TryFindInstancedBufferOffset(original, load, element, entries, plan, out callPosition)) return true;
            if (plan.GenericElement) return false;
            var abovePointer = 0;
            for (var index = load + 1; index < source.Count; index++)
            {
                var instruction = source[index];
                if (entries.Contains(instruction)) return false;
                if (instruction.OpCode.Code == CilCode.Call && instruction.Operand is IMethodDescriptor method)
                {
                    if (!InteropDelegateContracts.HasStandardConvention(method.Signature) || method.Signature.HasThis ||
                        method.Signature.ParameterTypes.Count != abovePointer + 1) return false;
                    var generic = GenericContext.FromMethod(method);
                    if (method.Signature.ParameterTypes[0].InstantiateGenericTypes(generic) is not PointerTypeSignature parameter ||
                        parameter.BaseType.ElementType != ElementType.Void && !SignatureComparer.Default.Equals(parameter.BaseType, element) ||
                        method.Signature.ReturnType.InstantiateGenericTypes(generic) is PointerTypeSignature or ByReferenceTypeSignature or FunctionPointerTypeSignature ||
                        method.Signature.ParameterTypes.Skip(1).Any(type => type.InstantiateGenericTypes(generic) is
                            PointerTypeSignature or ByReferenceTypeSignature or FunctionPointerTypeSignature)) return false;
                    callPosition = index;
                    return true;
                }
                if (instruction.OpCode.Code is not (CilCode.Nop or CilCode.Ldarg or CilCode.Ldloc or CilCode.Ldnull or
                    CilCode.Ldc_I4 or CilCode.Ldc_I8 or CilCode.Ldc_R4 or CilCode.Ldc_R8 or CilCode.Ldlen or
                    CilCode.Conv_I4 or CilCode.Conv_U4 or CilCode.Conv_I8 or CilCode.Conv_U8)) return false;
                var pops = instruction.GetStackPopCount(original);
                var pushes = instruction.GetStackPushCount();
                if (pops < 0 || pushes < 0 || pops > abovePointer) return false;
                abovePointer += pushes - pops;
            }
            return false;
        }

        private static bool TryFindInstancedBufferOffset(CilMethodBody original, int load, TypeSignature element,
            HashSet<CilInstruction> entries, FixedNativeArrayBuffer plan, out int callPosition)
        {
            callPosition = -1;
            var source = original.Instructions;
            if (load + 9 >= source.Count ||
                source[load + 1] is not { OpCode.Code: CilCode.Ldarg, Operand: Parameter offset } ||
                offset.ParameterType.ElementType != ElementType.I4 ||
                source[load + 2].OpCode.Code != CilCode.Conv_I ||
                source[load + 3] is not { OpCode.Code: CilCode.Sizeof, Operand: ITypeDefOrRef sized } ||
                !SignatureComparer.Default.Equals(sized.ToTypeSignature(), element) ||
                source[load + 4].OpCode.Code != CilCode.Mul || source[load + 5].OpCode.Code != CilCode.Add ||
                source[load + 6] is not { OpCode.Code: CilCode.Call, Operand: IMethodDescriptor conversion } ||
                conversion.DeclaringType.FullName != "System.IntPtr" || conversion.Name != "op_Explicit" ||
                conversion.Signature.HasThis || conversion.Signature.ParameterTypes.Count != 1 ||
                conversion.Signature.ReturnType.FullName != "System.IntPtr" ||
                conversion.Signature.ParameterTypes[0] is not PointerTypeSignature { BaseType.ElementType: ElementType.Void } ||
                source[load + 7] is not { OpCode.Code: CilCode.Ldloc, Operand: CilLocalVariable layout } ||
                layout.VariableType.FullName != "UnityEngine.RenderInstancedDataLayout" ||
                source[load + 8] is not { OpCode.Code: CilCode.Ldloc, Operand: CilLocalVariable count } ||
                count.VariableType.ElementType != ElementType.U4 ||
                source[load + 9] is not { OpCode.Code: CilCode.Call, Operand: IMethodDescriptor consumer } ||
                consumer.DeclaringType.FullName != "UnityEngine.Graphics" || consumer.Name != "Internal_RenderMeshInstanced" ||
                InteropRestoredTypeSupport.GetAssemblyName(consumer.DeclaringType.ToTypeSignature()) != "UnityEngine.CoreModule" ||
                consumer.Signature.HasThis || !InteropDelegateContracts.HasStandardConvention(consumer.Signature) ||
                consumer.Signature.ReturnType.ElementType != ElementType.Void ||
                !consumer.Signature.ParameterTypes.Select(type => type.FullName).SequenceEqual(new[]
                {
                    "UnityEngine.RenderParams", "UnityEngine.Mesh", "System.Int32", "System.IntPtr",
                    "UnityEngine.RenderInstancedDataLayout", "System.UInt32"
                })) return false;
            for (var index = load + 1; index <= load + 9; index++)
                if (entries.Contains(source[index])) return false;
            // This synchronous Unity entry point consumes the buffer before returning.
            // Do not generalize IntPtr lowering to APIs that may retain the address.
            plan.OffsetLoad = source[load];
            plan.OffsetParameter = offset;
            plan.OffsetCount = count;
            for (var index = load + 1; index <= load + 6; index++) plan.OffsetOperations.Add(source[index]);
            callPosition = load + 9;
            return true;
        }

        private bool TryTranslateFixedArrayBuffer(CilInstruction instruction, out CilInstruction copy)
        {
            copy = null;
            var plan = fixedArrayBuffer;
            if (plan == null) return false;
            if (ReferenceEquals(instruction, plan.PointerConversion))
            {
                // The planned producer now leaves pinned native data, not the source CLR ref.
                copy = body.Add(instruction.OpCode);
                return true;
            }
            if (ReferenceEquals(instruction, plan.OutputAddress))
            {
                // The incoming reference may point into the CLR heap. Keep its pin in the caller.
                copy = body.Add(CilOpCodes.Stloc, plan.OutputPin);
                body.Add(CilOpCodes.Ldloc, plan.OutputPin);
                body.Add(CilOpCodes.Conv_U);
                return true;
            }
            if (ReferenceEquals(instruction, plan.OffsetLoad))
            {
                copy = body.Add(CilOpCodes.Ldloc, plan.Handle);
                body.Add(CilOpCodes.Ldarg, TargetParameter(plan.OffsetParameter));
                body.Add(CilOpCodes.Ldloc, locals[plan.OffsetCount]);
                body.Add(CilOpCodes.Call, ((IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                    typeof(InteropNativeArrayBuffer).GetMethod(nameof(InteropNativeArrayBuffer.ElementAddress))))
                    .MakeGenericInstanceMethod(plan.Element));
                return true;
            }
            if (plan.OffsetOperations.Contains(instruction))
            {
                copy = body.Add(CilOpCodes.Nop);
                return true;
            }
            if (ReferenceEquals(instruction, plan.Producer))
            {
                copy = body.Add(CilOpCodes.Stloc, plan.Index);
                body.Add(CilOpCodes.Stloc, plan.Owner);
                // Preserve ldelema null/bounds failures before exposing storage.
                EmitArrayBoundsCheck(plan.Owner, plan.Index);
                body.Add(CilOpCodes.Ldloc, plan.Owner);
                body.Add(CilOpCodes.Ldloca, plan.Data);
                body.Add(CilOpCodes.Call, ((IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                    typeof(InteropNativeArrayBuffer).GetMethod(plan.GenericElement
                        ? nameof(InteropNativeArrayBuffer.PinGeneric) : nameof(InteropNativeArrayBuffer.Pin))))
                    .MakeGenericInstanceMethod(plan.Element));
                body.Add(CilOpCodes.Stloc, plan.Handle);
                body.Add(CilOpCodes.Ldloc, plan.Data);
                return true;
            }
            if (!plan.Clears.Contains(instruction)) return false;
            copy = body.Add(CilOpCodes.Stloc, locals[plan.SourcePin]);
            EmitFixedArrayRelease(plan);
            return true;
        }

        private CilInstruction EmitFixedArrayRelease(FixedNativeArrayBuffer plan)
        {
            var first = body.Add(CilOpCodes.Ldloc, plan.Handle);
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropNativeValueStorage).GetMethod(nameof(InteropNativeValueStorage.ReleaseArgument))));
            body.Add(CilOpCodes.Ldc_I4_0);
            body.Add(CilOpCodes.Conv_I);
            body.Add(CilOpCodes.Stloc, plan.Handle);
            EmitAddressKeepAlive(plan.Owner);
            body.Add(CilOpCodes.Ldnull);
            body.Add(CilOpCodes.Stloc, plan.Owner);
            if (plan.OutputPin != null)
            {
                body.Add(CilOpCodes.Ldc_I4_0);
                body.Add(CilOpCodes.Conv_U);
                body.Add(CilOpCodes.Stloc, plan.OutputPin);
            }
            return first;
        }

        private void CompleteFixedArrayBuffer()
        {
            if (fixedArrayBuffer == null) return;
            var result = target.Signature.ReturnType.ElementType == ElementType.Void ? null : AddArrayLocal(target.Signature.ReturnType);
            var exit = new CilInstructionLabel();
            var start = new CilInstructionLabel(body[0]);
            foreach (var instruction in body.Where(item => item.OpCode.Code == CilCode.Ret).ToArray())
            {
                if (result == null)
                {
                    instruction.OpCode = CilOpCodes.Leave;
                    instruction.Operand = exit;
                }
                else
                {
                    instruction.OpCode = CilOpCodes.Stloc;
                    instruction.Operand = result;
                    body.Insert(body.IndexOf(instruction) + 1, new CilInstruction(CilOpCodes.Leave, exit));
                }
            }
            var cleanup = new CilInstructionLabel(EmitFixedArrayRelease(fixedArrayBuffer));
            body.Add(CilOpCodes.Endfinally);
            exit.Instruction = result == null ? body.Add(CilOpCodes.Nop) : body.Add(CilOpCodes.Ldloc, result);
            body.Add(CilOpCodes.Ret);
            target.CilMethodBody.ExceptionHandlers.Add(new CilExceptionHandler
            {
                HandlerType = CilExceptionHandlerType.Finally, TryStart = start, TryEnd = cleanup,
                HandlerStart = cleanup, HandlerEnd = exit
            });
        }
    }
}
