// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool TryTranslateWwwBaseConstructor(CilOpCode code, IMethodDescriptor method,
            out CilInstruction translated)
        {
            translated = null;
            if (code.Code != CilCode.Call || method.Name != ".ctor" ||
                !InteropNativeWwwType.IsPrepared(ownerType.OriginalType) ||
                ownerType.TryGetRestoredReferenceInitializer(sourceMethod) != target)
                return false;
            if (!InteropNativeWwwType.IsSourceConstructor(sourceMethod) ||
                currentInstruction != sourceMethod.CilMethodBody.Instructions[1] ||
                !InteropNativeWwwType.IsBaseConstructor(method, sourceMethod) ||
                !SignatureComparer.Default.Equals(resolver.Resolve(sourceMethod.DeclaringType.BaseType.ToTypeSignature()),
                    ownerType.NewType.BaseType?.ToTypeSignature()))
            {
                translated = FailInstruction("native-www-base-constructor-contract",
                    "Only the original CustomYieldInstruction constructor can initialize the prepared WWW receiver.");
                return true;
            }
            translated = body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                InteropNativeWwwType.RequireRuntimeMethod("InitializeBase")));
            return true;
        }
    }
}
