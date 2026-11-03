// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;

namespace Modstoke.Preloader
{
    internal static class InteropArraySegmentLowering
    {
        private static readonly Version ContractVersion = new(2, 1, 0, 0);
        private static readonly byte[] ContractToken = { 0xcc, 0x7b, 0x13, 0xff, 0xcd, 0x2d, 0xdd, 0x51 };

        internal static bool HasSourceContract(ITypeDefOrRef type)
        {
            var assembly = type?.Scope?.GetAssembly();
            return assembly?.Name == "netstandard" && assembly.Version == ContractVersion &&
                assembly.GetPublicKeyToken() is { } token && token.SequenceEqual(ContractToken);
        }

        internal static bool TryGetStorage(InteropRewriteContext context, TypeSignature source,
            out TypeSignature element, out Type runtimeType)
        {
            element = null;
            runtimeType = null;
            if (source is not GenericInstanceTypeSignature instance || !instance.IsValueType ||
                instance.TypeArguments.Count != 1 || !HasSourceContract(instance.GenericType) ||
                !InteropNativeArrayLowering.IsStrippedHandle(context, instance.TypeArguments[0])) return false;
            runtimeType = instance.GenericType.FullName switch
            {
                "System.ArraySegment`1" => typeof(ArraySegment<>),
                "System.ArraySegment`1/Enumerator" => typeof(ArraySegment<>.Enumerator),
                _ => null
            };
            if (runtimeType == null) return false;
            element = instance.TypeArguments[0];
            return true;
        }
    }
}
