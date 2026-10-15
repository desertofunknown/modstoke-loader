// SPDX-License-Identifier: GPL-3.0-or-later
// Naming adapted from Il2CppInterop StringEx and TypeReferenceEx (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Buffers;
using System.Text;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class InteropNaming
    {
        internal static bool NameShouldBePrefixed(this string name, InteropGenerationOptions options)
        {
            if (options.Il2CppPrefixMode == InteropGenerationOptions.PrefixMode.OptIn)
            {
                foreach (var prefix in options.NamespacesAndAssembliesToPrefix)
                    if (name.StartsWith(prefix, StringComparison.Ordinal))
                        return true;
                return false;
            }

            foreach (var prefix in options.NamespacesAndAssembliesToNotPrefix)
                if (name.StartsWith(prefix, StringComparison.Ordinal))
                    return false;
            return true;
        }

        internal static bool NameShouldBePrefixed(this Utf8String name, InteropGenerationOptions options) =>
            NameShouldBePrefixed(name?.Value ?? "", options);

        internal static string UnSystemify(this Utf8String name, InteropGenerationOptions options)
        {
            var value = name?.Value ?? "";
            return NameShouldBePrefixed(value, options) ? "Il2Cpp" + value : value;
        }

        private static bool IsValidInSource(char value) =>
            char.IsDigit(value) || value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_' or '`';

        internal static bool IsInvalidInSource(string name)
        {
            if (name == null)
                return false;
            foreach (var value in name)
                if (!IsValidInSource(value))
                    return true;
            return false;
        }

        internal static Utf8String MakeValidInSource(this Utf8String name)
        {
            if (Utf8String.IsNullOrEmpty(name))
                return Utf8String.Empty;

            // Validate bytes, not decoded characters: generated identifiers depend on this.
            ReadOnlySpan<byte> data = name.GetBytesUnsafe();
            var length = data.Length;
            byte[] buffer = null;
            Span<byte> characters = default;
            try
            {
                if (char.IsDigit((char)data[0]))
                {
                    length++;
                    buffer = ArrayPool<byte>.Shared.Rent(length);
                    buffer[0] = (byte)'_';
                    characters = buffer.AsSpan(1);
                    data.CopyTo(characters);
                }

                for (var i = 0; i < data.Length; i++)
                {
                    if (IsValidInSource((char)data[i]))
                        continue;
                    if (buffer == null)
                    {
                        buffer = ArrayPool<byte>.Shared.Rent(length);
                        characters = buffer.AsSpan();
                        data.CopyTo(characters);
                    }
                    characters[i] = (byte)'_';
                }

                return buffer == null ? name : new Utf8String(buffer, 0, length);
            }
            finally
            {
                if (buffer != null)
                    ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        internal static bool IsObfuscated(this string name, InteropGenerationOptions options)
        {
            if (name == null)
                return false;
            if (options.ObfuscatedNamesRegex != null)
                return options.ObfuscatedNamesRegex.IsMatch(name);
            foreach (var value in name)
                if (!IsValidInSource(value) && value is not ('.' or '<' or '>'))
                    return true;
            return false;
        }

        internal static bool IsObfuscated(this Utf8String name, InteropGenerationOptions options) =>
            IsObfuscated(name?.Value ?? "", options);

        internal static ulong StableHash(this string name)
        {
            ulong hash = 0;
            foreach (var value in name)
                hash = unchecked(hash * 37 + value);
            return hash;
        }

        internal static bool StartsWith(this Utf8String name, string prefix) =>
            name is not null && name.Value.StartsWith(prefix, StringComparison.Ordinal);

        internal static string GetNamespacePrefix(this ITypeDefOrRef type) =>
            type.DeclaringType != null
                ? $"{GetNamespacePrefix(type.DeclaringType)}.{type.DeclaringType.Name}"
                : (string)type.Namespace;

        internal static string GetUnmangledName(this TypeSignature type, TypeDefinition declaringType = null,
            MethodDefinition declaringMethod = null)
        {
            var builder = new StringBuilder();
            if (type is GenericInstanceTypeSignature generic)
            {
                builder.Append(GetUnmangledName(generic.GenericType.ToTypeSignature(), declaringType, declaringMethod));
                foreach (var argument in generic.TypeArguments)
                {
                    builder.Append('_');
                    builder.Append(GetUnmangledName(argument, declaringType, declaringMethod));
                }
            }
            else if (type is ByReferenceTypeSignature byRef)
            {
                builder.Append("byref_");
                builder.Append(GetUnmangledName(byRef.BaseType, declaringType, declaringMethod));
            }
            else if (type is PointerTypeSignature pointer)
            {
                builder.Append("ptr_");
                builder.Append(GetUnmangledName(pointer.BaseType, declaringType, declaringMethod));
            }
            else if (type is GenericParameterSignature parameter)
            {
                var name = parameter.ParameterType == GenericParameterType.Type
                    ? declaringType.GenericParameters[parameter.Index].Name
                    : declaringMethod.GenericParameters[parameter.Index].Name;
                builder.Append((string)MakeValidInSource(name));
            }
            else
            {
                if (type.Namespace == "Il2CppInterop.Runtime" && (type.Name?.StartsWith("Il2Cpp") ?? false) &&
                    type.Name.Contains("Array"))
                    builder.Append("ArrayOf");
                else
                    builder.Append(type.Name?.Replace('`', '_'));
            }
            return builder.ToString();
        }
    }
}
