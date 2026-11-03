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
        private bool TryTranslateTilemapBaseConstructor(CilOpCode code, IMethodDescriptor method,
            out CilInstruction translated)
        {
            translated = null;
            if (code.Code != CilCode.Call || method.Name != ".ctor" ||
                !InteropTilemapConstruction.IsOwner(sourceMethod.DeclaringType) ||
                ownerType.TryGetRestoredReferenceInitializer(sourceMethod) != target)
                return false;
            if (!InteropTilemapConstruction.TryGetNativeDefault(sourceMethod, ownerType, imports, resolver, out _) ||
                currentInstruction.Offset != 1 || method is MethodSpecification ||
                !InteropDelegateContracts.HasStandardConvention(method.Signature) ||
                method.Signature is not { HasThis: true, GenericParameterCount: 0 } ||
                method.Signature.ReturnType.ElementType != ElementType.Void || method.Signature.ParameterTypes.Count != 0 ||
                method.DeclaringType is not ITypeDefOrRef objectType || objectType.FullName != "System.Object" ||
                !InteropArraySegmentLowering.HasSourceContract(objectType))
            {
                translated = FailInstruction("tilemap-base-constructor-contract",
                    "The tilemap initializer requires its original parameterless Object base call.");
                return true;
            }
            // The native default wrapper has already performed this no-op base initialization.
            translated = body.Add(CilOpCodes.Pop);
            return true;
        }
    }
}
