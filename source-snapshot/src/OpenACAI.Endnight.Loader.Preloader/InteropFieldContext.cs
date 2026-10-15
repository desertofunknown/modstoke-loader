// SPDX-License-Identifier: GPL-3.0-or-later
// Field context adapted from Il2CppInterop FieldRewriteContext (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace OpenACAI.Endnight.Loader.Preloader
{
    [DebuggerDisplay("{GetDebuggerDisplay(),nq}")]
    internal sealed class InteropFieldContext
    {
        private static readonly string[] FieldAccessNames =
            { "CompilerControlled", "Private", "FamAndAssem", "Internal", "Protected", "FamOrAssem", "Public" };

        public readonly InteropTypeContext DeclaringType;
        public readonly FieldDefinition OriginalField;
        public readonly MemberReference PointerField;
        public readonly string UnmangledName;

        public InteropFieldContext(InteropTypeContext declaringType, FieldDefinition originalField,
            Dictionary<string, int> renamedFieldCounts = null)
        {
            DeclaringType = declaringType;
            OriginalField = originalField;
            UnmangledName = UnmangleFieldName(originalField, declaringType.AssemblyContext.GlobalContext.Options,
                renamedFieldCounts);

            var pointerField = new FieldDefinition("NativeFieldInfoPtr_" + UnmangledName,
                FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly,
                declaringType.AssemblyContext.Imports.Module.CorLibTypeFactory.IntPtr);
            declaringType.NewType.Fields.Add(pointerField);
            PointerField = new MemberReference(declaringType.SelfSubstitutedRef, pointerField.Name,
                new FieldSignature(pointerField.Signature.FieldType));
        }

        private string UnmangleFieldNameBase(FieldDefinition field, InteropGenerationOptions options)
        {
            if (options.PassthroughNames)
                return field.Name;
            if (!field.Name.IsObfuscated(options))
                return field.Name.MakeValidInSource();

            var accessName = FieldAccessNames[(int)(field.Attributes & FieldAttributes.FieldAccessMask)];
            var staticName = field.IsStatic ? "_Static" : "";
            return "field_" + accessName + staticName + "_" +
                InteropTypeReferences.Rewrite(DeclaringType.AssemblyContext, field.Signature.FieldType)
                    .GetUnmangledName(field.DeclaringType);
        }

        private string UnmangleFieldName(FieldDefinition field, InteropGenerationOptions options,
            Dictionary<string, int> renamedFieldCounts)
        {
            if (options.PassthroughNames)
                return field.Name;
            if (!field.Name.IsObfuscated(options))
                return field.Name.MakeValidInSource();
            if (renamedFieldCounts == null)
                throw new ArgumentNullException(nameof(renamedFieldCounts));

            var name = UnmangleFieldNameBase(field, options);
            renamedFieldCounts.TryGetValue(name, out var count);
            renamedFieldCounts[name] = count + 1;
            name += "_" + count;

            if (DeclaringType.AssemblyContext.GlobalContext.Options.RenameMap.TryGetValue(
                DeclaringType.NewType.GetNamespacePrefix() + "." + DeclaringType.NewType.Name + "::" + name,
                out var renamed))
                name = renamed;
            return name;
        }

        private string GetDebuggerDisplay() => DeclaringType.NewType.FullName + "::" + UnmangledName;
    }
}
