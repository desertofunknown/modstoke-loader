// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;

namespace Modstoke.Preloader
{
    // Rebind public span contracts, never copy the source runtime's private representation.
    internal static class InteropSpanLowering
    {
        private static readonly Version ContractVersion = new(2, 1, 0, 0);
        private static readonly byte[] ContractToken = { 0xcc, 0x7b, 0x13, 0xff, 0xcd, 0x2d, 0xdd, 0x51 };
        internal static bool TryGetSpan(InteropRewriteContext context, TypeSignature source,
            out TypeSignature element, out Type runtimeType)
        {
            element = null;
            runtimeType = null;
            if (source is not GenericInstanceTypeSignature instance || !instance.IsValueType ||
                instance.TypeArguments.Count != 1 ||
                InteropRestoredTypeSupport.GetAssemblyName(source) != "netstandard" ||
                instance.GenericType.Scope?.GetAssembly()?.Version != ContractVersion ||
                instance.GenericType.Scope.GetAssembly().GetPublicKeyToken() is not { } token ||
                !token.SequenceEqual(ContractToken) ||
                !InteropNativeArrayLowering.IsStrippedHandle(context, instance.TypeArguments[0])) return false;
            runtimeType = instance.GenericType.FullName switch
            {
                "System.Span`1" => typeof(Span<>),
                "System.ReadOnlySpan`1" => typeof(ReadOnlySpan<>),
                "System.Span`1/Enumerator" => typeof(Span<>.Enumerator),
                "System.ReadOnlySpan`1/Enumerator" => typeof(ReadOnlySpan<>.Enumerator),
                _ => null
            };
            if (runtimeType == null) return false;
            element = instance.TypeArguments[0];
            return true;
        }

        internal static bool ContainsSpan(InteropRewriteContext context, TypeSignature source)
        {
            if (source == null) return false;
            if (TryGetSpan(context, source, out _, out _)) return true;
            if (source is GenericInstanceTypeSignature instance)
                return instance.TypeArguments.Any(argument => ContainsSpan(context, argument));
            return source is TypeSpecificationSignature specification && ContainsSpan(context, specification.BaseType);
        }
    }
}
