// SPDX-License-Identifier: GPL-3.0-or-later
// Struct classification adapted from Il2CppInterop Pass11 (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static partial class InteropTypeGeneration
    {
        internal static void ComputeTypeSpecifics(InteropRewriteContext context)
        {
            foreach (var assembly in context.Assemblies)
            foreach (var type in assembly.Types)
                ComputeSpecifics(type);
        }

        private static void ComputeSpecifics(InteropTypeContext type)
        {
            if (type.ComputedTypeSpecifics != InteropTypeContext.TypeSpecifics.NotComputed)
                return;
            type.ComputedTypeSpecifics = InteropTypeContext.TypeSpecifics.Computing;

            foreach (var field in type.OriginalType.Fields)
            {
                // Preserve the metadata-offset rejection before the static-field filter.
                if (field.ExtractFieldOffset() >= 0x8000000)
                {
                    type.ComputedTypeSpecifics = InteropTypeContext.TypeSpecifics.NonBlittableStruct;
                    return;
                }
                if (field.IsStatic)
                    continue;

                var fieldType = field.Signature.FieldType;
                if (fieldType is CorLibTypeSignature
                        { ElementType: >= ElementType.Boolean and <= ElementType.R8 or ElementType.I or ElementType.U } ||
                    fieldType is PointerTypeSignature)
                    continue;
                if (fieldType.FullName == "System.String" || fieldType.FullName == "System.Object" ||
                    fieldType is ArrayBaseTypeSignature or ByReferenceTypeSignature or
                        GenericParameterSignature or GenericInstanceTypeSignature)
                {
                    type.ComputedTypeSpecifics = InteropTypeContext.TypeSpecifics.NonBlittableStruct;
                    return;
                }

                var dependency = type.AssemblyContext.GlobalContext.GetNewTypeForOriginal(fieldType.Resolve());
                ComputeSpecifics(dependency);
                if (dependency.ComputedTypeSpecifics != InteropTypeContext.TypeSpecifics.BlittableStruct)
                {
                    type.ComputedTypeSpecifics = InteropTypeContext.TypeSpecifics.NonBlittableStruct;
                    return;
                }
            }

            type.ComputedTypeSpecifics = InteropTypeContext.TypeSpecifics.BlittableStruct;
        }
    }
}
