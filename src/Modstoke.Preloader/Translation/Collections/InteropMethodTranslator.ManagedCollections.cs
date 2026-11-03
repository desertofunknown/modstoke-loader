// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections;
using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private readonly Dictionary<CilInstruction, Type> constrainedCollectionCalls = new(ReferenceEqualityComparer.Instance);

        private bool TryTranslateManagedCollectionMethod(CilOpCode code, IMethodDescriptor method,
            out CilInstruction translated)
        {
            translated = null;
            var sourceOwner = method.DeclaringType?.ToTypeSignature();
            if (constrainedCollectionCalls.TryGetValue(currentInstruction, out var constrainedContract))
            {
                translated = TranslatePublicClrContractMethod(code, method, constrainedContract,
                    IsManagedCollectionMember(constrainedContract, method), "managed-collection-method-unavailable",
                    constrainedContract.IsGenericTypeDefinition ? null :
                        imports.Module.DefaultImporter.ImportTypeSignature(constrainedContract));
                return true;
            }
            if (InteropManagedCollectionLowering.TryGetStorage(context, sourceOwner, out var element, out var contractType))
            {
                if ((string)method.Name is "Contains" or "Remove" or "IndexOf" or "LastIndexOf" &&
                    InteropManagedDictionaryLowering.NeedsNativeEquality(context, element))
                {
                    translated = FailInstruction("managed-collection-native-element-equality",
                        "Element equality needs native key comparison; CLR wrapper equality would change lookup semantics.");
                    return true;
                }
                if (code.Code != CilCode.Newobj)
                {
                    if (!TryGetStackValue(currentInstruction, method.Signature?.ParameterTypes.Count ?? 0, out var value))
                        return Reject(out translated);
                    if (contractType.IsValueType
                        ? value.Type is not ByReferenceTypeSignature address ||
                            !SignatureComparer.Default.Equals(address.BaseType, sourceOwner)
                        : !value.IsNull && !InteropManagedCollectionLowering.CanAssign(context, value.Type, sourceOwner))
                        return Reject(out translated);
                }
                translated = TranslatePublicClrContractMethod(code, method, contractType,
                    IsManagedCollectionMember(contractType, method), "managed-collection-method-unavailable");
                return true;
            }

            if (method.Signature == null || !method.Signature.HasThis ||
                !TryGetStackValue(currentInstruction, method.Signature.ParameterTypes.Count, out var receiver) ||
                !InteropManagedCollectionLowering.TryGetContract(context, receiver.Type, out _, out var receiverContract) ||
                method.DeclaringType?.FullName is not ("System.Collections.IEnumerator" or "System.IDisposable")) return false;
            if (receiverContract != typeof(IEnumerator<>) ||
                !InteropArraySegmentLowering.HasSourceContract(sourceOwner.ToTypeDefOrRef())) return Reject(out translated);
            var runtimeType = method.DeclaringType.FullName == "System.IDisposable" ? typeof(IDisposable) : typeof(IEnumerator);
            translated = TranslatePublicClrContractMethod(code, method, runtimeType, IsManagedCollectionMember(runtimeType, method),
                "managed-collection-method-unavailable", imports.Module.DefaultImporter.ImportTypeSignature(runtimeType));
            return true;

            bool Reject(out CilInstruction result)
            {
                result = FailInstruction("managed-collection-receiver",
                    "Collection dispatch requires a proven CLR receiver and the exact source interface contract.");
                return true;
            }
        }

        private static bool IsManagedCollectionMember(Type contract, IMethodDescriptor method)
        {
            if (method.Signature is not { HasThis: true, GenericParameterCount: 0 } signature) return false;
            var count = signature.ParameterTypes.Count;
            if (InteropManagedCollectionLowering.IsEnumerator(contract))
                return count == 0 && ((string)method.Name is "get_Current" or "MoveNext" or "Dispose");
            if (contract == typeof(Queue<>))
                // FIFO storage copies restored values without invoking their equality or native layout.
                return (string)method.Name switch
                {
                    ".ctor" or "get_Count" or "Dequeue" => count == 0,
                    "Enqueue" => count == 1,
                    _ => false
                };
            if (contract == typeof(Stack<>))
                return (string)method.Name switch
                {
                    ".ctor" => count is 0 or 1,
                    "get_Count" or "Clear" or "Peek" or "Pop" or "TrimExcess" or "GetEnumerator" or "ToArray" => count == 0,
                    "Push" or "Contains" or "TryPeek" or "TryPop" or "EnsureCapacity" => count == 1,
                    "CopyTo" => count == 2,
                    _ => false
                };
            if (contract == typeof(List<>))
                return (string)method.Name switch
                {
                    ".ctor" => count is 0 or 1,
                    "get_Count" or "get_Capacity" or "Clear" or "TrimExcess" or "GetEnumerator" or "ToArray" => count == 0,
                    "get_Item" or "set_Capacity" or "Add" or "AddRange" or "Contains" or "Remove" or "RemoveAt" or "EnsureCapacity" => count == 1,
                    "set_Item" or "Insert" or "InsertRange" or "RemoveRange" or "GetRange" => count == 2,
                    "IndexOf" or "LastIndexOf" => count is >= 1 and <= 3,
                    "Reverse" => count is 0 or 2,
                    "CopyTo" => count is 1 or 2 or 4,
                    _ => false
                };
            return count == 0 &&
                (contract == typeof(IEnumerable<>) && method.Name == "GetEnumerator" ||
                 contract == typeof(IEnumerator<>) && method.Name == "get_Current" ||
                 contract == typeof(IEnumerator) && (string)method.Name is "MoveNext" or "Reset" ||
                 contract == typeof(IDisposable) && method.Name == "Dispose");
        }

        private bool TryTranslateManagedCollectionConstrainedCall(CilInstruction prefix, TypeSignature receiver,
            IMethodDescriptor method, CilInstruction call, out CilInstruction translated)
        {
            translated = null;
            var sourceContract = method.DeclaringType?.ToTypeSignature();
            var genericContract = InteropManagedCollectionLowering.TryGetContract(context, sourceContract, out _, out var contractType);
            var valueEnumerator = InteropManagedCollectionLowering.TryGetStorage(context, receiver, out _, out var receiverStorage) &&
                InteropManagedCollectionLowering.IsEnumerator(receiverStorage) ||
                InteropManagedDictionaryLowering.TryGetStorage(context, receiver, out _, out _, out var dictionaryStorage) &&
                InteropManagedDictionaryLowering.IsEnumerator(dictionaryStorage);
            if (!genericContract)
            {
                if (receiver is not GenericParameterSignature && !valueEnumerator || sourceContract == null ||
                    !InteropArraySegmentLowering.HasSourceContract(sourceContract.ToTypeDefOrRef())) return false;
                contractType = sourceContract.FullName switch
                {
                    "System.Collections.IEnumerator" => typeof(IEnumerator),
                    "System.IDisposable" => typeof(IDisposable),
                    _ => null
                };
                if (contractType == null) return false;
            }
            var rewrittenReceiver = resolver.Resolve(receiver);
            var rewrittenContract = genericContract ? resolver.Resolve(sourceContract) :
                imports.Module.DefaultImporter.ImportTypeSignature(contractType);
            var compatible = receiver is GenericParameterSignature
                ? rewrittenReceiver != null && rewrittenContract != null &&
                    ProvesManagedBase(rewrittenReceiver, rewrittenContract, new HashSet<string>(), 0)
                : !genericContract && valueEnumerator ||
                    InteropManagedCollectionLowering.TryGetBoxContract(context, receiver, out var bufferContract) &&
                    SignatureComparer.Default.Equals(bufferContract, sourceContract);
            if (!genericContract && !compatible) return false;
            if (method is MethodSpecification || !InteropDelegateContracts.HasStandardConvention(method.Signature) ||
                !IsManagedCollectionMember(contractType, method) || !compatible || rewrittenReceiver == null ||
                !TryGetStackValue(prefix, 0, out var value) || value.Type is not ByReferenceTypeSignature address ||
                !SignatureComparer.Default.Equals(address.BaseType, receiver))
            {
                translated = FailInstruction("managed-collection-constrained-call",
                    "The constrained receiver does not prove the exact CLR collection interface.");
                return true;
            }
            constrainedCollectionCalls.Add(call, contractType);
            translated = body.Add(CilOpCodes.Constrained, rewrittenReceiver.ToTypeDefOrRef());
            return true;
        }

        private bool TryTranslateManagedCollectionInstruction(CilInstruction instruction, out CilInstruction translated)
        {
            translated = null;
            var code = instruction.OpCode.Code;
            if (code == CilCode.Box && instruction.Operand is ITypeDefOrRef boxed &&
                InteropManagedCollectionLowering.TryGetBoxContract(context, boxed.ToTypeSignature(), out _))
            {
                var storage = resolver.Resolve(boxed.ToTypeSignature());
                if (storage?.IsValueType != true || !TryGetStackValue(instruction, 0, out var value) ||
                    !SignatureComparer.Default.Equals(value.Type, boxed.ToTypeSignature())) return Reject(out translated);
                translated = body.Add(CilOpCodes.Box, storage.ToTypeDefOrRef());
                return true;
            }
            if (code is CilCode.Castclass or CilCode.Isinst && instruction.Operand is ITypeDefOrRef targetContract &&
                InteropManagedCollectionLowering.IsReference(context, targetContract.ToTypeSignature()))
            {
                var targetType = resolver.Resolve(targetContract.ToTypeSignature());
                if (targetType == null || !TryGetStackValue(instruction, 0, out var value) ||
                    !IsManagedCollectionReference(value)) return Reject(out translated);
                translated = body.Add(instruction.OpCode, targetType.ToTypeDefOrRef());
                return true;
            }
            if (code is CilCode.Ceq or CilCode.Beq or CilCode.Bne_Un or CilCode.Cgt_Un &&
                TryGetStackValue(instruction, 1, out var left) && TryGetStackValue(instruction, 0, out var right) &&
                (IsManagedCollectionStackValue(left) || IsManagedCollectionStackValue(right)))
            {
                if (!IsManagedCollectionReference(left) || !IsManagedCollectionReference(right) ||
                    code == CilCode.Cgt_Un && !right.IsNull) return Reject(out translated);
                if (code is CilCode.Beq or CilCode.Bne_Un)
                {
                    if (instruction.Operand is not CilInstructionLabel label) return Reject(out translated);
                    translated = body.Add(instruction.OpCode, CopyBranchLabel(label));
                }
                else translated = body.Add(instruction.OpCode);
                return true;
            }
            return false;

            bool Reject(out CilInstruction result)
            {
                result = FailInstruction("managed-collection-reference-operation",
                    "This operation would mix an unproven or native reference with CLR collection storage.");
                return true;
            }
        }

        private bool IsManagedCollectionReference(StackValue value) => value.IsNull ||
            InteropManagedCollectionLowering.IsReference(context, value.Type);
    }
}
