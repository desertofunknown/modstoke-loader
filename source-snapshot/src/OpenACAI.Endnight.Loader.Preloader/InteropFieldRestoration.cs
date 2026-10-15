// SPDX-License-Identifier: GPL-3.0-or-later
// Unity field restoration adapted from Il2CppInterop Pass80UnstripFields (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using BepInEx.Logging;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class InteropFieldRestoration
    {
        internal static void Restore(InteropRewriteContext context)
        {
            var restored = 0;
            var ignored = 0;
            foreach (var original in context.UnityAssemblies.Assemblies)
            {
                var assembly = context.TryGetAssemblyByName(original.Name);
                if (assembly == null)
                    continue;

                var resolver = new InteropUnstripTypeResolver(context, assembly.Imports);
                foreach (var originalType in original.ManifestModule.GetAllTypes())
                {
                    var type = assembly.TryGetTypeByName(originalType.FullName);
                    if (type == null || originalType.IsEnum || (originalType.IsNested && type.OriginalType != null))
                        continue;

                    foreach (var field in originalType.Fields)
                    {
                        if (field.IsStatic && field.Constant == null)
                            continue;
                        if (!field.IsStatic && (type.OriginalType != null || !InteropTypeReferences.IsValueType(type.NewType)))
                            continue;
                        if (type.TryGetFieldByUnityAssemblyField(field) != null)
                            continue;

                        var restoredInstance = !field.IsStatic && type.OriginalType == null;
                        var fieldType = restoredInstance && field.Signature.FieldType is PointerTypeSignature
                            ? assembly.Imports.Module.CorLibTypeFactory.IntPtr
                            : resolver.Resolve(field.Signature.FieldType);
                        if (restoredInstance && (fieldType == null || !InteropTypeReferences.IsValueType(fieldType)))
                            throw new InvalidOperationException(
                                $"Cannot restore instance field {originalType.FullName}::{field.Name} without changing its value layout.");
                        if (fieldType == null)
                        {
                            LogUnsupported(field, originalType);
                            ignored++;
                            continue;
                        }

                        var copy = new FieldDefinition(field.Name,
                            (field.Attributes & ~FieldAttributes.FieldAccessMask) | FieldAttributes.Public, fieldType);
                        if (field.Constant != null)
                            copy.Constant = field.Constant;
                        if (restoredInstance)
                        {
                            copy.FieldOffset = field.FieldOffset;
                            if (field.MarshalDescriptor is SimpleMarshalDescriptor descriptor)
                                copy.MarshalDescriptor = new SimpleMarshalDescriptor(descriptor.NativeType)
                                {
                                    ExtraData = descriptor.ExtraData == null ? null : (byte[])descriptor.ExtraData.Clone()
                                };
                            else if (fieldType.FullName == "System.Boolean")
                                copy.MarshalDescriptor = new SimpleMarshalDescriptor(NativeType.U1);
                            copy.HasFieldMarshal = copy.MarshalDescriptor != null;
                        }
                        type.NewType.Fields.Add(copy);
                        restored++;
                    }
                }
            }
            InteropGenerationDiagnostics.Log(LogLevel.Info, $"Restored {restored} fields");
            InteropGenerationDiagnostics.Log(LogLevel.Info, $"Failed to restore {ignored} fields");
        }

        private static void LogUnsupported(FieldDefinition field, TypeDefinition type)
        {
            try
            {
                InteropGenerationDiagnostics.Log(LogLevel.Debug,
                    $"Field {field} on type {type.FullName} has unsupported type {field.Signature.FieldType}, the type will be unusable");
            }
            catch
            {
                // Formatting unsupported metadata must not turn an ignored field into a failed run.
            }
        }
    }
}
