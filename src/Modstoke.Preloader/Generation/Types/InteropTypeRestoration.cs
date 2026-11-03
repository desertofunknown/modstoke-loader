// SPDX-License-Identifier: GPL-3.0-or-later
// Unity type restoration adapted from Il2CppInterop Pass79 (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using BepInEx.Logging;

namespace Modstoke.Preloader
{
    internal static class InteropTypeRestoration
    {
        internal static void Restore(InteropRewriteContext context)
        {
            var plan = InteropRestorationPlan.For(context);
            var restored = 0;
            var genericTypes = new List<(TypeDefinition Original, TypeDefinition Target, InteropRuntimeReferences Imports)>();
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
                    RestoreType(assembly, type, null, assembly.Imports, plan, genericTypes, ref restored);
            }
            // A base helper can be declared later, including in another source assembly.
            foreach (var (source, sourceBase) in plan.ManagedBases())
            {
                var assembly = context.TryGetAssemblyByName(source.DeclaringModule.Assembly.Name);
                var target = assembly.GetTypeByName(source.FullName);
                var resolver = new InteropUnstripTypeResolver(context, assembly.Imports);
                var baseType = resolver.Resolve(sourceBase.ToTypeSignature());
                if (target.OriginalType != null || baseType == null || baseType.IsValueType ||
                    !plan.IsManagedReference(sourceBase.ToTypeSignature()))
                    throw new InvalidOperationException("A planned managed base could not be bound: " + source.FullName);
                target.NewType.BaseType = baseType.ToTypeDefOrRef();
            }
            // Constraints can refer forward, recursively, or to a constructed nested helper.
            // Bind only after every target has its complete (including inherited) parameter list.
            foreach (var (original, target, imports) in genericTypes)
                if (!InteropRestoredGenericSupport.TryCopyTypeConstraints(original, target,
                    new InteropUnstripTypeResolver(context, imports), out var reason))
                    throw new InvalidOperationException("A planned generic helper constraint could not be bound: " +
                        original.FullName + ": " + reason);
            // Managed contract signatures may reference declarations from later assemblies.
            foreach (var sourceAssembly in context.UnityAssemblies.Assemblies)
            foreach (var sourceType in sourceAssembly.ManifestModule.GetAllTypes().Where(type =>
                type.IsInterface || type.BaseType?.FullName == "System.MulticastDelegate"))
            {
                var type = context.TryGetAssemblyByName(sourceAssembly.Name)?.TryGetTypeByName(sourceType.FullName);
                if (type?.OriginalType == null && type?.NewType.IsInterface == true)
                    InteropRestoredInterfaceSupport.DeclareMembers(sourceType, type.NewType,
                        new InteropUnstripTypeResolver(context, type.AssemblyContext.Imports));
                else if (InteropRestoredManagedDelegateSupport.IsRestored(type))
                    InteropRestoredManagedDelegateSupport.DeclareMembers(sourceType, type.NewType,
                        new InteropUnstripTypeResolver(context, type.AssemblyContext.Imports));
            }
            InteropGenerationDiagnostics.Log(LogLevel.Debug, $"Unstripped {restored} types");
        }

        private static void RestoreType(InteropAssemblyContext assembly, TypeDefinition original,
            TypeDefinition enclosingType, InteropRuntimeReferences imports, InteropRestorationPlan plan,
            List<(TypeDefinition Original, TypeDefinition Target, InteropRuntimeReferences Imports)> genericTypes, ref int restored)
        {
            if (original.Name == "<Module>")
                return;

            var module = assembly.NewAssembly.ManifestModule;
            // Existing nested wrappers may have normalized or deobfuscated names.
            var target = assembly.TryGetTypeByName(original.FullName)?.NewType;
            if (target == null && enclosingType != null)
                target = enclosingType.NestedTypes.SingleOrDefault(type => type.Name == original.Name);
            if (target == null && !plan.CanRestore(original))
                return;
            if (original.BaseType?.FullName == "System.MulticastDelegate")
            {
                if (target != null) return;
                var unmanaged = InteropRestoredUnmanagedDelegateSupport.TryDescribe(original, out _, out _, out _);
                var copy = unmanaged ? InteropRestoredUnmanagedDelegateSupport.Declare(assembly, original)
                    : InteropRestoredManagedDelegateSupport.Declare(assembly, original);
                if (enclosingType == null) module.TopLevelTypes.Add(copy);
                else enclosingType.NestedTypes.Add(copy);
                if (copy.GenericParameters.Count != 0) genericTypes.Add((original, copy, imports));
                assembly.RegisterTypeRewrite(new InteropTypeContext(assembly, null, copy), original.FullName);
                restored++;
                return;
            }
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
                var baseType = original.IsInterface ? null : isValueType
                    ? module.DefaultImporter.ImportTypeSignature(typeof(ValueType)).ToTypeDefOrRef()
                    : InteropRestoredTypeSupport.IsManagedException(original)
                        ? module.DefaultImporter.ImportTypeSignature(typeof(Exception)).ToTypeDefOrRef()
                        : module.DefaultImporter.ImportTypeSignature(typeof(object)).ToTypeDefOrRef();
                var copy = new TypeDefinition(original.Namespace, original.Name, ForcePublic(original.Attributes), baseType);
                // Nested metadata already includes the enclosing type's parameters in order.
                // Do not append the owner's parameters a second time.
                foreach (var parameter in original.GenericParameters)
                    copy.GenericParameters.Add(new GenericParameter(parameter.Name.MakeValidInSource(), parameter.Attributes));
                if (copy.GenericParameters.Count != 0)
                    genericTypes.Add((original, copy, imports));
                var managedOnly = plan.ContainsManagedOnlyStorage(original.ToTypeSignature());
                if (isValueType && !managedOnly && original.ClassLayout != null)
                    copy.ClassLayout = new ClassLayout(original.ClassLayout.PackingSize, original.ClassLayout.ClassSize);
                if (isValueType && managedOnly)
                {
                    copy.IsSequentialLayout = false;
                    copy.IsExplicitLayout = false;
                    copy.IsAutoLayout = true;
                }
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
                RestoreType(assembly, nested, target, imports, plan, genericTypes, ref restored);
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
