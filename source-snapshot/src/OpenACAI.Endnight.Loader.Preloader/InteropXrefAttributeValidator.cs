// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Il2CppInterop.Common.Attributes;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class InteropXrefAttributeValidator
    {
        internal static void Validate(MetadataReader reader, long recordCount, string path)
        {
            foreach (var methodHandle in reader.MethodDefinitions)
            {
                foreach (var attributeHandle in reader.GetMethodDefinition(methodHandle).GetCustomAttributes())
                {
                    var attribute = reader.GetCustomAttribute(attributeHandle);
                    if (!IsCachedScanAttribute(reader, attribute.Constructor))
                        continue;

                    var decoded = attribute.DecodeValue(PrimitiveAttributeTypes.Instance);
                    if (!decoded.FixedArguments.IsEmpty)
                        throw InvalidRange(path, methodHandle);

                    // Omitted fields retain the attribute's zero defaults.
                    var refStart = 0;
                    var refEnd = 0;
                    var xrefStart = 0;
                    var xrefEnd = 0;
                    var seen = 0;
                    foreach (var argument in decoded.NamedArguments)
                    {
                        var field = argument.Name switch
                        {
                            nameof(CachedScanResultsAttribute.RefRangeStart) => 1,
                            nameof(CachedScanResultsAttribute.RefRangeEnd) => 2,
                            nameof(CachedScanResultsAttribute.XrefRangeStart) => 4,
                            nameof(CachedScanResultsAttribute.XrefRangeEnd) => 8,
                            _ => 0
                        };
                        if (field == 0)
                            continue;
                        if (argument.Kind != CustomAttributeNamedArgumentKind.Field
                            || argument.Type != PrimitiveTypeCode.Int32 || argument.Value is not int value
                            || (seen & field) != 0)
                            throw InvalidRange(path, methodHandle);
                        seen |= field;
                        switch (field)
                        {
                            case 1: refStart = value; break;
                            case 2: refEnd = value; break;
                            case 4: xrefStart = value; break;
                            case 8: xrefEnd = value; break;
                        }
                    }

                    if (!IsRangeValid(refStart, refEnd, recordCount) || !IsRangeValid(xrefStart, xrefEnd, recordCount))
                        throw InvalidRange(path, methodHandle);
                }
            }
        }

        private static bool IsCachedScanAttribute(MetadataReader reader, EntityHandle constructor)
        {
            if (constructor.Kind != HandleKind.MemberReference)
                return false;
            var parent = reader.GetMemberReference((MemberReferenceHandle)constructor).Parent;
            if (parent.Kind != HandleKind.TypeReference)
                return false;
            var type = reader.GetTypeReference((TypeReferenceHandle)parent);
            if (!reader.StringComparer.Equals(type.Namespace, "Il2CppInterop.Common.Attributes")
                || !reader.StringComparer.Equals(type.Name, nameof(CachedScanResultsAttribute))
                || type.ResolutionScope.Kind != HandleKind.AssemblyReference)
                return false;
            var assembly = reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope);
            return reader.StringComparer.Equals(assembly.Name, "Il2CppInterop.Common");
        }

        private static bool IsRangeValid(int start, int end, long count) => start >= 0 && end >= start && end <= count;

        private static InvalidDataException InvalidRange(string path, MethodDefinitionHandle method) => new(
            "Invalid cached xref ranges on method 0x" + MetadataTokens.GetToken(method).ToString("X8") + " in " + path);

        // Generated scan attributes contain primitive fields; decoding must never resolve or load their types.
        private sealed class PrimitiveAttributeTypes : ICustomAttributeTypeProvider<PrimitiveTypeCode>
        {
            internal static readonly PrimitiveAttributeTypes Instance = new();

            public PrimitiveTypeCode GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode;
            public bool IsSystemType(PrimitiveTypeCode type) => false;
            public PrimitiveTypeCode GetSystemType() => throw UnsupportedType();
            public PrimitiveTypeCode GetSZArrayType(PrimitiveTypeCode elementType) => throw UnsupportedType();
            public PrimitiveTypeCode GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => throw UnsupportedType();
            public PrimitiveTypeCode GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => throw UnsupportedType();
            public PrimitiveTypeCode GetTypeFromSerializedName(string name) => throw UnsupportedType();
            public PrimitiveTypeCode GetUnderlyingEnumType(PrimitiveTypeCode type) => throw UnsupportedType();

            private static BadImageFormatException UnsupportedType() => new("Unsupported type in a cached xref attribute.");
        }
    }
}
