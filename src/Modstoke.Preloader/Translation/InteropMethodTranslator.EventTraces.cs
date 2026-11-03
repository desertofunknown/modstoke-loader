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
        private bool TryTranslateEventTraceBaseConstructor(CilOpCode code, IMethodDescriptor method,
            out CilInstruction translated)
        {
            translated = null;
            if (code.Code != CilCode.Call || method.Name != ".ctor" ||
                (!InteropNativeEventDebuggerTypes.IsPrepared(ownerType.OriginalType) &&
                 !InteropEventDebuggerFields.HasOwner(sourceMethod.DeclaringType, ownerType, imports, resolver)) ||
                ownerType.TryGetRestoredReferenceInitializer(sourceMethod) != target ||
                method.DeclaringType?.FullName != sourceMethod.DeclaringType.BaseType?.FullName)
                return false;

            if (method is MethodSpecification || !InteropDelegateContracts.HasStandardConvention(method.Signature) ||
                method.Signature is not { HasThis: true, GenericParameterCount: 0 } ||
                method.Signature.ReturnType.ElementType != ElementType.Void)
            {
                translated = FailInstruction("event-trace-base-constructor-contract", "The base initializer signature differs from its declaration.");
                return true;
            }
            if (method.DeclaringType.FullName == "System.Object" && method.Signature.ParameterTypes.Count == 0 &&
                method.DeclaringType is ITypeDefOrRef objectType && InteropArraySegmentLowering.HasSourceContract(objectType))
            {
                // The inspected native Object constructor is a no-op; the pointer constructor already rooted this instance.
                translated = body.Add(CilOpCodes.Pop);
                return true;
            }

            var source = method.Resolve();
            var baseType = source == null ? null : context.TryGetAssemblyByName(source.DeclaringModule.Assembly.Name)
                ?.TryGetTypeByName(source.DeclaringType.FullName);
            var initializer = baseType?.TryGetRestoredReferenceInitializer(source);
            if (initializer == null || !InteropNativeEventDebuggerTypes.IsPrepared(baseType.OriginalType) ||
                ownerType.NewType.BaseType?.Resolve() != baseType.NewType ||
                !SignatureComparer.Default.Equals(method.Signature, source.Signature))
            {
                translated = FailInstruction("event-trace-base-initializer-unavailable", "The prepared trace base has no same-object initializer.");
                return true;
            }
            translated = body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(initializer));
            return true;
        }
    }
}
