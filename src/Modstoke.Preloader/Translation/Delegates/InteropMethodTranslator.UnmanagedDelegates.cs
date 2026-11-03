// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private readonly Dictionary<CilInstruction, (TypeSignature DelegateType, string Method)> unmanagedDelegateCalls =
            new(ReferenceEqualityComparer.Instance);

        private void FindUnmanagedDelegateCalls(CilMethodBody source)
        {
            HashSet<CilInstruction> boundaries = null;
            var code = source.Instructions;
            for (var index = 0; index < code.Count; index++)
            {
                var token = code[index];
                if (TryPlanUnmanagedDelegateCall(token, out var delegateType, out var method))
                {
                    unmanagedDelegateCalls.Add(token, (delegateType, method));
                    continue;
                }
                if (index + 3 >= code.Count || token.OpCode.Code != CilCode.Ldtoken || token.Operand is not ITypeDefOrRef sourceDelegate ||
                    !InteropRestorationPlan.For(context).ContainsManagedOnlyStorage(sourceDelegate.ToTypeSignature()))
                    continue;
                var getType = code[index + 1];
                var marshal = code[index + 2];
                var cast = code[index + 3];
                if (!IsTypeFromHandle(getType) || !IsFunctionPointerImport(marshal) ||
                    cast.OpCode.Code != CilCode.Castclass || cast.Operand is not ITypeDefOrRef castType ||
                    !SignatureComparer.Default.Equals(sourceDelegate, castType) ||
                    !TryGetStackValue(token, 0, out var pointer) || pointer.Type is not CorLibTypeSignature { ElementType: AsmResolver.PE.DotNet.Metadata.Tables.ElementType.I })
                    continue;
                var rewritten = resolver.ResolveRaw(sourceDelegate.ToTypeSignature());
                var definition = rewritten?.Resolve();
                if (definition == null || !InteropRestoredUnmanagedDelegateSupport.IsRestored(context.GetContextForNewType(definition)))
                    continue;

                boundaries ??= CollectDelegateBoundaries(source);
                if (boundaries.Contains(getType) || boundaries.Contains(marshal) || boundaries.Contains(cast)) continue;

                // Keep the native pointer, but cross Marshal's CLR boundary as one typed operation.
                // No source branch or handler may observe the removed Type/Delegate intermediates.
                unmanagedDelegateCalls.Add(token, default);
                unmanagedDelegateCalls.Add(getType, default);
                unmanagedDelegateCalls.Add(marshal, (rewritten, nameof(Marshal.GetDelegateForFunctionPointer)));
                unmanagedDelegateCalls.Add(cast, default);
                index += 3;
            }

        }

        private bool TryPlanUnmanagedDelegateCall(CilInstruction instruction, out TypeSignature delegateType, out string name)
        {
            delegateType = null;
            name = null;
            if (instruction.OpCode.Code != CilCode.Call || instruction.Operand is not IMethodDescriptor method ||
                !InteropRestoredUnmanagedDelegateSupport.IsSystemType(method.DeclaringType, "System.Runtime.InteropServices.Marshal") ||
                !InteropDelegateContracts.HasStandardConvention(method.Signature) || method.Signature.HasThis ||
                method.Signature.ParameterTypes.Count != 1 || !TryGetStackValue(instruction, 0, out var value))
                return false;

            name = (string)method.Name;
            var importing = name == nameof(Marshal.GetDelegateForFunctionPointer);
            if (!importing && name != nameof(Marshal.GetFunctionPointerForDelegate))
                return false;

            TypeSignature sourceDelegate;
            if (method is MethodSpecification generic)
            {
                if (method.Signature.GenericParameterCount != 1 || generic.Signature.TypeArguments.Count != 1)
                    return false;
                var parameter = method.Signature.ParameterTypes[0];
                var result = method.Signature.ReturnType;
                if ((importing ? result : parameter) is not GenericParameterSignature
                        { ParameterType: GenericParameterType.Method, Index: 0 } ||
                    (importing ? parameter : result) is not CorLibTypeSignature { ElementType: ElementType.I })
                    return false;
                sourceDelegate = generic.Signature.TypeArguments[0];
                if (importing
                    ? value.IsNull || value.Type is not CorLibTypeSignature { ElementType: ElementType.I }
                    : !value.IsNull && !SignatureComparer.Default.Equals(value.Type, sourceDelegate))
                    return false;
            }
            else
            {
                if (importing || method.Signature.GenericParameterCount != 0 || value.IsNull ||
                    method.Signature.ReturnType is not CorLibTypeSignature { ElementType: ElementType.I } ||
                    !InteropRestoredUnmanagedDelegateSupport.IsSystemType(
                        method.Signature.ParameterTypes[0].ToTypeDefOrRef(), "System.Delegate"))
                    return false;
                sourceDelegate = value.Type;
            }

            if (!InteropRestorationPlan.For(context).ContainsManagedOnlyStorage(sourceDelegate))
                return false;
            var rewritten = resolver.ResolveRaw(sourceDelegate);
            var definition = rewritten?.Resolve();
            if (definition == null || !InteropRestoredUnmanagedDelegateSupport.IsRestored(context.GetContextForNewType(definition)))
                return false;
            delegateType = rewritten;
            return true;
        }

        private static bool IsTypeFromHandle(CilInstruction instruction) =>
            instruction.OpCode.Code == CilCode.Call && instruction.Operand is IMethodDescriptor method &&
            method is not MethodSpecification && method.Name == "GetTypeFromHandle" &&
            InteropRestoredUnmanagedDelegateSupport.IsSystemType(method.DeclaringType, "System.Type") &&
            InteropDelegateContracts.HasStandardConvention(method.Signature) && !method.Signature.HasThis &&
            method.Signature.GenericParameterCount == 0 && method.Signature.ParameterTypes.Count == 1 &&
            InteropRestoredUnmanagedDelegateSupport.IsSystemType(method.Signature.ParameterTypes[0].ToTypeDefOrRef(), "System.RuntimeTypeHandle") &&
            InteropRestoredUnmanagedDelegateSupport.IsSystemType(method.Signature.ReturnType.ToTypeDefOrRef(), "System.Type");

        private static bool IsFunctionPointerImport(CilInstruction instruction) =>
            instruction.OpCode.Code == CilCode.Call && instruction.Operand is IMethodDescriptor method &&
            method is not MethodSpecification && method.Name == "GetDelegateForFunctionPointer" &&
            InteropRestoredUnmanagedDelegateSupport.IsSystemType(method.DeclaringType, "System.Runtime.InteropServices.Marshal") &&
            InteropDelegateContracts.HasStandardConvention(method.Signature) && !method.Signature.HasThis &&
            method.Signature.GenericParameterCount == 0 && method.Signature.ParameterTypes.Count == 2 &&
            method.Signature.ParameterTypes[0] is CorLibTypeSignature { ElementType: AsmResolver.PE.DotNet.Metadata.Tables.ElementType.I } &&
            InteropRestoredUnmanagedDelegateSupport.IsSystemType(method.Signature.ParameterTypes[1].ToTypeDefOrRef(), "System.Type") &&
            InteropRestoredUnmanagedDelegateSupport.IsSystemType(method.Signature.ReturnType.ToTypeDefOrRef(), "System.Delegate");

        private bool TryTranslateUnmanagedDelegateCall(CilInstruction instruction, out CilInstruction translated)
        {
            translated = null;
            if (!unmanagedDelegateCalls.TryGetValue(instruction, out var call)) return false;
            if (call.DelegateType == null) translated = body.Add(CilOpCodes.Nop);
            else
            {
                // Export keeps Marshal's caller-owned lifetime: native consumers do not root the CLR delegate.
                var method = typeof(Marshal).GetMethods().Single(candidate => candidate.Name == call.Method &&
                    candidate.IsGenericMethodDefinition && candidate.GetParameters().Length == 1);
                var imported = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(method);
                translated = body.Add(CilOpCodes.Call, imported.MakeGenericInstanceMethod(call.DelegateType));
            }
            return true;
        }
    }
}
