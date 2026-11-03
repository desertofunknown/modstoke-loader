// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private InteropTypeContext FindMappedSourceOwner(TypeSignature source)
        {
            if (source == null) return null;
            var definition = source is GenericInstanceTypeSignature instance
                ? instance.GenericType.ToTypeSignature() : source;
            // The rewrite context already binds supported facades to their native assembly.
            // Do not search other assemblies by name or manufacture a native identity.
            return context.TryGetTypeByName(InteropRestoredTypeSupport.GetAssemblyName(definition), definition.FullName);
        }

        private InteropMethodContext FindMappedNativeMethod(IMethodDescriptor source, InteropTypeContext owner)
        {
            if (owner.OriginalType == null || !InteropDelegateContracts.HasStandardConvention(source.Signature))
                return null;
            var result = resolver.Resolve(source.Signature.ReturnType);
            if (result == null) return null;
            var signature = source.Signature.HasThis
                ? MethodSignature.CreateInstance(result, source.Signature.GenericParameterCount)
                : MethodSignature.CreateStatic(result, source.Signature.GenericParameterCount);
            foreach (var parameter in source.Signature.ParameterTypes)
            {
                var rewritten = resolver.Resolve(parameter);
                if (rewritten == null) return null;
                signature.ParameterTypes.Add(rewritten);
            }

            InteropMethodContext match = null;
            foreach (var candidate in owner.Methods)
            {
                var original = candidate.OriginalMethod;
                if (original.Name != source.Name ||
                    original.IsStatic == source.Signature.HasThis ||
                    !SignatureComparer.Default.Equals(candidate.NewMethod.Signature, signature) &&
                    !(FindNativeSpanBorrow(source) is { Root: not null } borrow && borrow.Getter == candidate)) continue;
                if (match != null) return null;
                match = candidate;
            }
            return match;
        }

    }
}
