// SPDX-License-Identifier: GPL-3.0-or-later
// Attribute value extraction adapted from Il2CppInterop CustomAttributeEx (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Linq;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class InteropAttributeValues
    {
        internal static long ExtractOffset(this IHasCustomAttribute member)
        {
            return Convert.ToInt64(Extract(member, "AddressAttribute", "Offset"), 16);
        }

        internal static long ExtractRva(this IHasCustomAttribute member)
        {
            return Convert.ToInt64(Extract(member, "AddressAttribute", "RVA"), 16);
        }

        internal static long ExtractToken(this IHasCustomAttribute member)
        {
            return Convert.ToInt64(Extract(member, "TokenAttribute", "Token"), 16);
        }

        internal static int ExtractFieldOffset(this IHasCustomAttribute member)
        {
            return Convert.ToInt32(Extract(member, "FieldOffsetAttribute", "Offset"), 16);
        }

        internal static string GetElementAsString(this CustomAttributeArgument argument)
        {
            return argument.Element as Utf8String ?? argument.Element as string;
        }

        private static string Extract(IHasCustomAttribute member, string attributeName, string parameterName)
        {
            var attribute = member.CustomAttributes.SingleOrDefault(candidate =>
                candidate.Constructor?.DeclaringType?.Name == attributeName);
            var namedArgument = attribute?.Signature?.NamedArguments.SingleOrDefault(candidate =>
                candidate.MemberName == parameterName);

            return namedArgument?.Argument.GetElementAsString();
        }
    }
}
