// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private CilInstruction TranslateSizeOf(TypeSignature source, TypeSignature rewritten)
        {
            var storage = InteropRestoredTypeSupport.StorageType(source);
            if (storage is GenericParameterSignature)
                return SizeCall(nameof(InteropGenericValueSupport.SizeOf), InteropRestoredTypeSupport.StorageType(rewritten));
            if (TryGetNativeValueWrapper(storage, out var wrapper))
            {
                var reason = InteropRestorationPlan.For(context).GetNativeIdentityRejection(storage);
                if (reason != null) return FailInstruction("native-type-identity-unavailable", reason);
                return SizeCall(nameof(InteropGenericValueSupport.NativeValueSize), wrapper);
            }
            if (storage is TypeDefOrRefSignature or GenericInstanceTypeSignature &&
                !InteropRestoredTypeSupport.StorageType(rewritten).IsValueType &&
                (storage.IsValueType || FindMappedSourceOwner(storage)?.OriginalType?.IsValueType == true))
                return FailInstruction("native-value-size-unavailable",
                    "The source value has no proven CLR layout or native payload size: " + source.FullName);

            // CLR values and genuine reference slots retain sizeof semantics, including
            // restored descriptors containing references. No address is exposed here.
            return body.Add(CilOpCodes.Sizeof, rewritten.ToTypeDefOrRef());

            CilInstruction SizeCall(string name, TypeSignature argument) => body.Add(CilOpCodes.Call,
                ((IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(typeof(InteropGenericValueSupport).GetMethod(name)))
                    .MakeGenericInstanceMethod(argument));
        }
    }
}
