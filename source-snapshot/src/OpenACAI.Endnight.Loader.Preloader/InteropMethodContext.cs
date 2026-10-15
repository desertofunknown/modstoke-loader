// SPDX-License-Identifier: GPL-3.0-or-later
// Method context adapted from Il2CppInterop MethodRewriteContext (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Common.XrefScans;

namespace OpenACAI.Endnight.Loader.Preloader
{
    [DebuggerDisplay("{NewMethod.FullName,nq}")]
    internal sealed partial class InteropMethodContext
    {
        internal readonly InteropTypeContext DeclaringType;
        internal readonly long FileOffset;
        internal readonly MethodDefinition NewMethod;
        internal readonly MethodDefinition OriginalMethod;
        internal readonly bool OriginalNameObfuscated;
        internal readonly long Rva;
        internal readonly List<XrefInstance> XrefScanResults = new();

        internal long MetadataInitFlagRva;
        internal long MetadataInitTokenRva;

        internal InteropMethodContext(InteropTypeContext declaringType, MethodDefinition originalMethod)
        {
            DeclaringType = declaringType;
            OriginalMethod = originalMethod;

            var context = declaringType.AssemblyContext.GlobalContext;
            var passthroughNames = context.Options.PassthroughNames;
            OriginalNameObfuscated = !passthroughNames &&
                                     (originalMethod.Name?.IsObfuscated(context.Options) ?? false);

            var module = declaringType.AssemblyContext.Imports.Module;
            var attributes = AdjustAttributes(originalMethod.Attributes, originalMethod.Name == "Finalize");
            var signature = (attributes & MethodAttributes.Static) != 0
                ? MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, originalMethod.GenericParameters.Count)
                : MethodSignature.CreateInstance(module.CorLibTypeFactory.Void, originalMethod.GenericParameters.Count);
            NewMethod = new MethodDefinition("", attributes, signature)
            {
                CilMethodBody = new CilMethodBody()
            };

            HasExtensionAttribute = originalMethod.CustomAttributes.Any(attribute =>
                attribute.Constructor?.DeclaringType?.FullName == typeof(ExtensionAttribute).FullName);
            if (HasExtensionAttribute)
            {
                var attributeType = module.DefaultImporter.ImportTypeSignature(typeof(ExtensionAttribute));
                NewMethod.CustomAttributes.Add(new CustomAttribute(new MemberReference(
                    attributeType.ToTypeDefOrRef(), ".ctor",
                    MethodSignature.CreateInstance(module.CorLibTypeFactory.Void))));
            }

            foreach (var parameter in originalMethod.GenericParameters)
                NewMethod.GenericParameters.Add(new GenericParameter(parameter.Name.MakeValidInSource(),
                    parameter.Attributes.StripValueTypeConstraint()));

            if (!context.ScanState.HasObfuscatedMethods && !passthroughNames &&
                originalMethod.Name.IsObfuscated(context.Options))
                context.ScanState.HasObfuscatedMethods = true;

            FileOffset = originalMethod.ExtractOffset();
            // Cpp2IL can supply negative file offsets; retain the installed normalization.
            if (FileOffset < 0)
                FileOffset = 0;
            Rva = originalMethod.ExtractRva();
            if (FileOffset != 0)
                context.MethodStartAddresses.Add(FileOffset);
        }

        internal Utf8String UnmangledName { get; private set; }
        internal string UnmangledNameWithSignature { get; private set; }
        internal TypeDefinition GenericInstantiationsStore { get; private set; }
        internal ITypeDefOrRef GenericInstantiationsStoreSelfSubstRef { get; private set; }
        internal ITypeDefOrRef GenericInstantiationsStoreSelfSubstMethodRef { get; private set; }
        internal MemberReference NonGenericMethodInfoPointerField { get; private set; }
        internal bool HasExtensionAttribute { get; }

        // Called after method-reference scanning, because scan liveness participates in names.
        internal void FinalizeDeclaration()
        {
            UnmangledName = UnmangleMethodName();
            UnmangledNameWithSignature = UnmangleMethodNameWithSignature();

            var assembly = DeclaringType.AssemblyContext;
            var module = assembly.Imports.Module;
            NewMethod.Name = UnmangledName;
            NewMethod.Signature.ReturnType = InteropTypeReferences.Rewrite(assembly, OriginalMethod.Signature?.ReturnType);

            var pointerField = new FieldDefinition("NativeMethodInfoPtr_" + UnmangledNameWithSignature,
                FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly,
                module.CorLibTypeFactory.IntPtr);
            DeclaringType.NewType.Fields.Add(pointerField);
            NonGenericMethodInfoPointerField = new MemberReference(DeclaringType.SelfSubstitutedRef,
                pointerField.Name, new FieldSignature(pointerField.Signature.FieldType));

            if (OriginalMethod.GenericParameters.Count != 0)
            {
                var parameters = OriginalMethod.GenericParameters;
                var store = new TypeDefinition("",
                    "MethodInfoStoreGeneric_" + UnmangledNameWithSignature + "`" + parameters.Count,
                    TypeAttributes.NestedPrivate | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
                    module.CorLibTypeFactory.Object.ToTypeDefOrRef());
                DeclaringType.NewType.NestedTypes.Add(store);
                GenericInstantiationsStore = store;

                var selfSubstitution = new GenericInstanceTypeSignature(store, false);
                var methodSubstitution = new GenericInstanceTypeSignature(store, false);
                for (var index = 0; index < parameters.Count; index++)
                {
                    var originalParameter = parameters[index];
                    var storeParameter = new GenericParameter(originalParameter.Name.MakeValidInSource());
                    store.GenericParameters.Add(storeParameter);
                    selfSubstitution.TypeArguments.Add(
                        new GenericParameterSignature(GenericParameterType.Type, storeParameter.Number));
                    var newParameter = NewMethod.GenericParameters[index];
                    methodSubstitution.TypeArguments.Add(
                        new GenericParameterSignature(GenericParameterType.Method, newParameter.Number));

                    foreach (var originalConstraint in originalParameter.Constraints)
                    {
                        var constraint = originalConstraint.Constraint;
                        if (constraint?.FullName == "System.ValueType" || constraint?.Resolve()?.IsInterface == true)
                            continue;

                        if (constraint?.FullName == "System.Enum")
                        {
                            newParameter.Constraints.Add(new GenericParameterConstraint(
                                module.DefaultImporter.ImportTypeSignature(typeof(Enum)).ToTypeDefOrRef()));
                            continue;
                        }

                        newParameter.Constraints.Add(new GenericParameterConstraint(
                            InteropTypeReferences.Rewrite(assembly, constraint?.ToTypeSignature()).ToTypeDefOrRef()));
                    }
                }

                store.Fields.Add(new FieldDefinition("Pointer", FieldAttributes.Assembly | FieldAttributes.Static,
                    module.CorLibTypeFactory.IntPtr));
                var importer = DeclaringType.NewType.DeclaringModule.DefaultImporter;
                GenericInstantiationsStoreSelfSubstRef = importer.ImportType(selfSubstitution.ToTypeDefOrRef());
                GenericInstantiationsStoreSelfSubstMethodRef = importer.ImportType(methodSubstitution.ToTypeDefOrRef());
            }

            DeclaringType.NewType.Methods.Add(NewMethod);
        }

        private static MethodAttributes AdjustAttributes(MethodAttributes attributes, bool stripVirtual)
        {
            attributes &= ~(MethodAttributes.MemberAccessMask | MethodAttributes.PInvokeImpl |
                            MethodAttributes.Abstract | MethodAttributes.Final | MethodAttributes.ReuseSlot |
                            MethodAttributes.CheckAccessOnOverride);
            if (stripVirtual)
                attributes &= ~(MethodAttributes.Virtual | MethodAttributes.NewSlot);
            return attributes | MethodAttributes.Public;
        }
    }
}
