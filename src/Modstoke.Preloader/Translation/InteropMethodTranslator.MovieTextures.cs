// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool TryTranslateMovieTextureBaseConstructor(CilOpCode code, IMethodDescriptor method,
            out CilInstruction translated)
        {
            translated = null;
            if (code.Code != CilCode.Call || method.Name != ".ctor" ||
                !InteropNativeMovieTextureType.IsPrepared(ownerType.OriginalType) ||
                ownerType.TryGetRestoredReferenceInitializer(sourceMethod) != target)
                return false;
            if (!InteropNativeMovieTextureType.IsSourceConstructor(sourceMethod) ||
                !InteropNativeMovieTextureType.IsTextureBaseConstructor(method, sourceMethod) ||
                !SignatureComparer.Default.Equals(resolver.Resolve(sourceMethod.DeclaringType.BaseType.ToTypeSignature()),
                    ownerType.NewType.BaseType?.ToTypeSignature()))
            {
                translated = FailInstruction("native-movie-texture-base-constructor-contract",
                    "Only the original Texture constructor can initialize the prepared MovieTexture receiver.");
                return true;
            }
            // The native Texture constructor runs on this allocation and preserves its Object class initialization.
            translated = body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                InteropNativeMovieTextureType.RequireRuntimeMethod("InitializeTextureBase")));
            return true;
        }
    }
}
