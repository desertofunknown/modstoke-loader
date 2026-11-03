// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool TryPlanNativeFieldGetter(CilInstruction instruction, IMethodDescriptor method,
            ReferenceOrigins[] consumed, out ReferenceUse use)
        {
            use = null;
            if (instruction.OpCode.Code != CilCode.Call || method is MethodSpecification ||
                !method.Signature.HasThis || method.Signature.GenericParameterCount != 0 ||
                method.Signature.ParameterTypes.Count != 0 || consumed.Length != 1 ||
                !InteropDelegateContracts.HasStandardConvention(method.Signature) ||
                method.Resolve() is not { IsStatic: false, IsVirtual: false, IsAbstract: false, HasMethodBody: true } original ||
                original.IsConstructor || original.GenericParameters.Count != 0 ||
                !original.DeclaringType.IsValueType || original.DeclaringType.GenericParameters.Count != 0 ||
                !SignatureComparer.Default.Equals(method.Signature, original.Signature) ||
                (original.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.Synchronized)) != MethodImplAttributes.IL ||
                original.CilMethodBody.ExceptionHandlers.Count != 0 ||
                !TryGetReferenceLocation(consumed[0], out var location, out _) || !IsNativeStorageValue(location) ||
                !(location.IsScopedParameter || location.IsArray || (location.OwnerType != null || location.StaticOwnerType != null) &&
                    location.NativeFieldPointerName != null) ||
                !SignatureComparer.Default.Equals(location.SourceElement, method.DeclaringType.ToTypeSignature()))
                return false;

            var type = context.TryGetAssemblyByName(original.DeclaringModule.Assembly.Name)
                ?.TryGetTypeByName(original.DeclaringType.FullName);
            if (type?.OriginalType == null ||
                resolver.ResolveRaw(location.SourceElement)?.Resolve() != type.NewType)
                return false;
            // The source body proves this read, even when its declaration already has a
            // native wrapper. A readonly receiver must not enter the mutating call path.
            var code = original.CilMethodBody.Instructions.Where(item => item.OpCode.Code != CilCode.Nop).ToArray();
            if (code.Length < 3 || ForwardedArgumentIndex(code[0], original) != 0 ||
                code[1].OpCode.Code != CilCode.Ldfld || code[1].Operand is not IFieldDescriptor field ||
                field.DeclaringType?.Resolve() != original.DeclaringType || !HasForwardedReturn(original, code, 2) ||
                !TryGetStackValue(instruction, 0, out var receiver) ||
                !TryGetNestedReferenceField(field, receiver.Type, out var nested) ||
                !SignatureComparer.Default.Equals(nested.SourceElement, method.Signature.ReturnType) ||
                !(IsBlittableAddressValue(nested) || IsNativeReferenceValue(nested) || IsNativeAggregateLocation(nested)))
                return false;

            use = new ReferenceUse { Location = location, NestedFieldRead = nested };
            return true;
        }
    }
}
