// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool TryTranslateArraySegmentMethod(CilOpCode code, IMethodDescriptor method,
            out CilInstruction translated)
        {
            translated = null;
            if (!InteropArraySegmentLowering.TryGetStorage(context, method.DeclaringType?.ToTypeSignature(),
                out _, out var runtimeType)) return false;
            translated = TranslatePublicClrContractMethod(code, method, runtimeType,
                IsStandardArraySegmentMember(runtimeType, method), "managed-array-segment-method-unavailable");
            return true;
        }

        private static bool IsStandardArraySegmentMember(Type runtimeType, IMethodDescriptor method)
        {
            var signature = method.Signature;
            if (signature == null) return false;
            var count = signature.ParameterTypes.Count;
            var name = (string)method.Name;
            if (runtimeType == typeof(ArraySegment<>.Enumerator))
                return signature.HasThis && count == 0 && (name is "MoveNext" or "get_Current" or "Dispose");
            return name switch
            {
                ".ctor" => signature.HasThis && (count is 1 or 3) && signature.ParameterTypes[0] is SzArrayTypeSignature,
                "get_Empty" => !signature.HasThis && count == 0,
                "get_Array" or "get_Offset" or "get_Count" or "GetEnumerator" or "ToArray" or "GetHashCode" =>
                    signature.HasThis && count == 0,
                "get_Item" => signature.HasThis && count == 1,
                "set_Item" => signature.HasThis && count == 2,
                "CopyTo" or "Slice" => signature.HasThis && count is 1 or 2,
                "Equals" => signature.HasThis && count == 1 &&
                    signature.ParameterTypes[0] is GenericInstanceTypeSignature segment &&
                    segment.GenericType.FullName == "System.ArraySegment`1",
                "op_Implicit" => !signature.HasThis && count == 1 && signature.ParameterTypes[0] is SzArrayTypeSignature,
                "op_Equality" or "op_Inequality" => !signature.HasThis && count == 2,
                _ => false
            };
        }

        private bool TryTranslateArraySegmentConstrainedCall(CilInstruction prefix, TypeSignature receiver,
            IMethodDescriptor method, CilInstruction call, out CilInstruction translated)
        {
            translated = null;
            if (!InteropArraySegmentLowering.TryGetStorage(context, receiver, out _, out var runtimeType)) return false;
            var source = receiver.Resolve();
            var slot = method.Resolve();
            if (runtimeType != typeof(ArraySegment<>.Enumerator) || method is MethodSpecification ||
                method.Name != "Dispose" || method.DeclaringType?.FullName != "System.IDisposable" ||
                !InteropArraySegmentLowering.HasSourceContract(method.DeclaringType.ToTypeSignature().ToTypeDefOrRef()) ||
                slot?.DeclaringType?.IsInterface != true || source == null ||
                !InteropDelegateContracts.HasStandardConvention(method.Signature) || !method.Signature.HasThis ||
                method.Signature.GenericParameterCount != 0 || method.Signature.ParameterTypes.Count != 0 ||
                method.Signature.ReturnType.ElementType != ElementType.Void ||
                !source.Interfaces.Any(entry => SignatureComparer.Default.Equals(entry.Interface, method.DeclaringType)) ||
                !TryGetStackValue(prefix, 0, out var value) || value.Type is not ByReferenceTypeSignature address ||
                !SignatureComparer.Default.Equals(address.BaseType, receiver)) return Reject(out translated);
            var candidates = source.Methods.Where(candidate => candidate.IsPublic && !candidate.IsStatic &&
                !candidate.IsAbstract && !candidate.IsPInvokeImpl && candidate.Name == method.Name &&
                candidate.GenericParameters.Count == 0 &&
                SignatureComparer.Default.Equals(candidate.Signature, method.Signature)).ToArray();
            if (candidates.Length != 1) return Reject(out translated);

            // The facade may have no body. Bind its exact public Dispose contract on the
            // existing CLR enumerator location, without boxing or releasing the array.
            constrainedValueCalls.Add(call, new MemberReference(receiver.ToTypeDefOrRef(),
                candidates[0].Name, candidates[0].Signature));
            translated = body.Add(CilOpCodes.Nop);
            return true;

            bool Reject(out CilInstruction result)
            {
                result = FailInstruction("managed-array-segment-constrained-call",
                    "Only the exact array-segment enumerator Dispose contract has a constrained CLR adapter.");
                return true;
            }
        }
    }
}
