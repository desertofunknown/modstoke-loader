// SPDX-License-Identifier: GPL-3.0-or-later
// Unity type restoration adapted from Il2CppInterop Pass79 (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using BepInEx.Logging;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class InteropTypeRestoration
    {
        internal static void Restore(InteropRewriteContext context)
        {
            var plan = new InteropRestorationPlan(context);
            var restored = 0;
            foreach (var original in context.UnityAssemblies.Assemblies)
            {
                var assembly = context.TryGetAssemblyByName(original.Name);
                if (assembly == null)
                {
                    var target = new AssemblyDefinition(original.Name, original.Version);
                    target.Modules.Add(new ModuleDefinition(original.ManifestModule.Name));
                    assembly = new InteropAssemblyContext(context, original, target);
                    context.AddAssemblyContext(original.Name, assembly);
                }

                foreach (var type in original.ManifestModule.TopLevelTypes)
                    RestoreType(assembly, type, null, assembly.Imports, plan, ref restored);
            }
            InteropGenerationDiagnostics.Log(LogLevel.Debug, $"Unstripped {restored} types");
        }

        private static void RestoreType(InteropAssemblyContext assembly, TypeDefinition original,
            TypeDefinition enclosingType, InteropRuntimeReferences imports, InteropRestorationPlan plan, ref int restored)
        {
            if (original.Name == "<Module>" || original.BaseType?.FullName == "System.MulticastDelegate")
                return;

            var module = assembly.NewAssembly.ManifestModule;
            // Existing nested wrappers may have normalized or deobfuscated names.
            var target = assembly.TryGetTypeByName(original.FullName)?.NewType;
            if (target == null && enclosingType != null)
                target = enclosingType.NestedTypes.SingleOrDefault(type => type.Name == original.Name);
            if (target == null && !plan.CanRestore(original))
                return;
            if (original.IsEnum)
            {
                if (target != null)
                    return;
                restored++;
                var copy = CloneEnum(original, imports);
                if (enclosingType == null)
                    module.TopLevelTypes.Add(copy);
                else
                    enclosingType.NestedTypes.Add(copy);
                assembly.RegisterTypeRewrite(new InteropTypeContext(assembly, null, copy), original.FullName);
                return;
            }

            if (target == null)
            {
                restored++;
                var isValueType = InteropTypeReferences.IsValueType(original);
                var baseType = isValueType
                    ? module.DefaultImporter.ImportTypeSignature(typeof(ValueType)).ToTypeDefOrRef()
                    : original.BaseType == null ? null : module.DefaultImporter.ImportType(original.BaseType);
                var copy = new TypeDefinition(original.Namespace, original.Name, ForcePublic(original.Attributes), baseType);
                if (isValueType && original.ClassLayout != null)
                    copy.ClassLayout = new ClassLayout(original.ClassLayout.PackingSize, original.ClassLayout.ClassSize);
                if (enclosingType == null)
                    module.TopLevelTypes.Add(copy);
                else
                    enclosingType.NestedTypes.Add(copy);

                // CoreCLR does not accept the struct layout used by some Unity reference types.
                if (!InteropTypeReferences.IsValueType(copy) && (copy.IsExplicitLayout || copy.IsSequentialLayout))
                {
                    copy.IsSequentialLayout = false;
                    copy.IsExplicitLayout = false;
                    copy.IsAutoLayout = true;
                }
                assembly.RegisterTypeRewrite(new InteropTypeContext(assembly, null, copy), original.FullName);
                target = copy;
            }

            foreach (var nested in original.NestedTypes)
                RestoreType(assembly, nested, target, imports, plan, ref restored);
        }

        private static TypeDefinition CloneEnum(TypeDefinition original, InteropRuntimeReferences imports)
        {
            var module = imports.Module;
            var enumType = module.DefaultImporter.ImportTypeSignature(typeof(Enum)).ToTypeDefOrRef();
            var copy = new TypeDefinition(original.Namespace, original.Name, ForcePublic(original.Attributes), enumType);
            foreach (var field in original.Fields)
            {
                var fieldType = field.Name == "value__"
                    ? module.DefaultImporter.ImportTypeSignature(typeof(string).Assembly.GetType(field.Signature.FieldType.FullName))
                    : copy.ToTypeSignature();
                copy.Fields.Add(new FieldDefinition(field.Name, field.Attributes, new FieldSignature(fieldType))
                {
                    Constant = field.Constant
                });
            }
            return copy;
        }

        private static TypeAttributes ForcePublic(TypeAttributes attributes)
        {
            var visibility = attributes & TypeAttributes.VisibilityMask;
            return visibility == 0 || visibility == TypeAttributes.Public
                ? attributes | TypeAttributes.Public
                : (attributes & ~TypeAttributes.VisibilityMask) | TypeAttributes.NestedPublic;
        }

    }
}
