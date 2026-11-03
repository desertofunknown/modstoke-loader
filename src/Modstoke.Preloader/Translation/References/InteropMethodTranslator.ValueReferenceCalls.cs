// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool PreservesNativeValueThisArguments(CilInstruction instruction, IMethodDescriptor method,
            TypeSignature self)
        {
            if (!InteropDelegateContracts.HasStandardConvention(method.Signature)) return false;
            var original = method.Resolve();
            if (original == null) return false;
            var declarer = resolver.ResolveRaw(method.DeclaringType?.ToTypeSignature(), method.Signature.HasThis);
            var definition = declarer?.Resolve();
            var mappedType = context.TryGetAssemblyByName(original.DeclaringModule.Assembly.Name)
                ?.TryGetTypeByName(original.DeclaringType.FullName);
            if (definition == null || mappedType?.NewType != definition || mappedType.OriginalType == null) return false;
            var mapped = mappedType.TryGetMethodByUnityAssemblyMethod(original);
            var bridge = mapped == null ? mappedType.TryGetRestoredInternalCall(original) : null;
            var callee = mapped?.NewMethod ?? bridge?.Target;
            if (callee == null || callee.DeclaringType != definition ||
                callee.Signature.ParameterTypes.Count != method.Signature.ParameterTypes.Count)
                return false;
            if (mapped != null && (mapped.Rva == 0 && (!original.IsConstructor ||
                    method.DeclaringType.ToTypeSignature() is not GenericInstanceTypeSignature) ||
                mapped.NonGenericMethodInfoPointerField == null))
                return false;
            // Only the generated pinning bridge proves in-place writes; arbitrary restored
            // CLR methods can replace the wrapper held in the caller's this slot.
            if (bridge != null && (instruction.OpCode.Code != CilCode.Call || !original.IsStatic ||
                original.IsConstructor || original.IsVirtual || original.IsAbstract ||
                (original.ImplAttributes & MethodImplAttributes.InternalCall) == 0 ||
                method.Signature.HasThis || method.Signature.GenericParameterCount != 0 ||
                original.DeclaringType.GenericParameters.Count != 0 ||
                !callee.IsStatic || callee.IsVirtual || callee.Signature.GenericParameterCount != 0))
                return false;
            if (original.IsConstructor && (instruction.OpCode.Code != CilCode.Call ||
                !TryGetNativeValueWrapper(method.DeclaringType.ToTypeSignature(), out _) ||
                !TryGetNativeValueConstructor(method, out _, out var constructor) || constructor != mapped))
                return false;

            // A CLR override could replace the wrapper reference instead of its native payload.
            if (instruction.OpCode.Code == CilCode.Callvirt && callee.IsVirtual &&
                !callee.IsFinal && !mappedType.NewType.IsSealed)
                return false;
            var genericContext = GenericContext.FromMethod(method);
            var result = method.Signature.ReturnType.InstantiateGenericTypes(genericContext);
            if (result is ByReferenceTypeSignature or PointerTypeSignature or FunctionPointerTypeSignature or
                CustomModifierTypeSignature)
                return false;

            for (var index = 0; index < method.Signature.ParameterTypes.Count; index++)
            {
                var parameter = method.Signature.ParameterTypes[index].InstantiateGenericTypes(genericContext);
                if (parameter is not ByReferenceTypeSignature reference ||
                    !SignatureComparer.Default.Equals(reference.BaseType, self))
                    continue;
                if (!(mapped?.PreservesNativeValueReference(index) ?? bridge.PreservesNativeValueReference(index)) ||
                    !TryGetStackValue(instruction, method.Signature.ParameterTypes.Count - index - 1, out var argument) ||
                    argument.Type is not ByReferenceTypeSignature argumentReference ||
                    !SignatureComparer.Default.Equals(argumentReference.BaseType, self))
                    return false;
            }
            return true;
        }
    }
}
