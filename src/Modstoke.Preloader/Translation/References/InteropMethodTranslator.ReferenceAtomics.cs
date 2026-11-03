// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private sealed record ReferenceAtomicCall(TypeSignature Owner, string MethodPointerName,
            TypeSignature Element, bool CompareExchange);

        private bool TryPlanAtomicReferenceCall(CilInstruction instruction, ReferenceOrigins[] consumed, out ReferenceUse use)
        {
            use = null;
            if (instruction.OpCode.Code != CilCode.Call || instruction.Operand is not IMethodDescriptor method ||
                method.Name != "CompareExchange" && method.Name != "Exchange" ||
                !InteropRestoredUnmanagedDelegateSupport.IsSystemType(method.DeclaringType, "System.Threading.Interlocked") ||
                !InteropDelegateContracts.HasStandardConvention(method.Signature) || method.Signature.HasThis)
                return false;
            var compareExchange = method.Name == "CompareExchange";
            var count = compareExchange ? 3 : 2;
            var signature = method.Signature;
            if (signature.ParameterTypes.Count != count || consumed.Length != count ||
                signature.ParameterTypes[0] is not ByReferenceTypeSignature reference)
                return false;
            TypeSignature sourceElement;
            if (method is MethodSpecification generic)
            {
                if (signature.GenericParameterCount != 1 || generic.Signature.TypeArguments.Count != 1 ||
                    !IsMethodTypeParameter(reference.BaseType) || !IsMethodTypeParameter(signature.ReturnType) ||
                    signature.ParameterTypes.Skip(1).Any(parameter => !IsMethodTypeParameter(parameter)))
                    return false;
                sourceElement = generic.Signature.TypeArguments[0];
            }
            else
            {
                if (signature.GenericParameterCount != 0 || reference.BaseType is not CorLibTypeSignature { ElementType: ElementType.Object } ||
                    signature.ReturnType is not CorLibTypeSignature { ElementType: ElementType.Object } ||
                    signature.ParameterTypes.Skip(1).Any(parameter => parameter is not CorLibTypeSignature { ElementType: ElementType.Object }))
                    return false;
                sourceElement = reference.BaseType;
            }
            if (InteropTypeReferences.IsValueType(sourceElement) || !TryGetAddressElement(sourceElement, out var element) ||
                !IsNativeArrayReference(element) || !TryGetReferenceLocation(consumed[0], out var location, out var readOnly) ||
                readOnly || (location.OwnerType == null && location.StaticOwnerType == null) ||
                !location.IsArray && location.NativeFieldPointerName == null ||
                location.RectangularRank != 0 ||
                !SignatureComparer.Default.Equals(location.SourceElement, sourceElement) ||
                !SignatureComparer.Default.Equals(location.Element, element))
                return false;
            for (var index = 1; index < count; index++)
            {
                if (consumed[index] != null || !TryGetStackValue(instruction, count - index - 1, out var value)) return false;
                if (value.IsNull) continue;
                if (!TryGetAddressElement(value.Type, out var argument) || InteropTypeReferences.IsValueType(value.Type) ||
                    !ProvesManagedBase(argument, element, new HashSet<string>(), 0))
                    return false;
            }

            // Generic reference atomics have the same pointer operation as the object overload.
            // Preserve T's exact slot/operand types rather than invoking a wrapper's temporary ref.
            var nativeOwner = context.CorLib.TryGetTypeByName("System.Threading.Interlocked");
            var candidates = nativeOwner?.Methods.Where(candidate =>
                candidate.OriginalMethod.Name == method.Name && candidate.OriginalMethod.IsStatic &&
                candidate.OriginalMethod.IsPublic && candidate.NonGenericMethodInfoPointerField != null && candidate.Rva != 0 &&
                IsNativeObjectAtomic(candidate.OriginalMethod.Signature, count)).Take(2).ToArray();
            if (candidates?.Length != 1) return false;
            use = new ReferenceUse
            {
                Call = new ReferenceCall
                {
                    Atomic = new ReferenceAtomicCall(nativeOwner.NewType.ToTypeSignature(),
                        candidates[0].NonGenericMethodInfoPointerField.Name, element, compareExchange)
                }
            };
            return true;
        }

        private static bool IsMethodTypeParameter(TypeSignature type) =>
            type is GenericParameterSignature { ParameterType: GenericParameterType.Method, Index: 0 };

        private static bool IsNativeObjectAtomic(MethodSignature signature, int count) =>
            InteropDelegateContracts.HasStandardConvention(signature) && !signature.HasThis &&
            signature.GenericParameterCount == 0 && signature.ReturnType.FullName == "System.Object" &&
            signature.ParameterTypes.Count == count &&
            signature.ParameterTypes[0] is ByReferenceTypeSignature reference && reference.BaseType.FullName == "System.Object" &&
            signature.ParameterTypes.Skip(1).All(parameter => parameter.FullName == "System.Object");

        private CilInstruction EmitAtomicReferenceCall(ReferenceAtomicCall call)
        {
            var first = body.Add(CilOpCodes.Nop);
            EmitReferenceRuntimeType(call.Owner);
            body.Add(CilOpCodes.Ldstr, call.MethodPointerName);
            var helper = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(typeof(InteropNativeReferenceAtomic)
                .GetMethod(call.CompareExchange ? nameof(InteropNativeReferenceAtomic.CompareExchange) : nameof(InteropNativeReferenceAtomic.Exchange)));
            body.Add(CilOpCodes.Call, helper.MakeGenericInstanceMethod(call.Element));
            return first;
        }
    }
}
