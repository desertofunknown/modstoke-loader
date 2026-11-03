// SPDX-License-Identifier: GPL-3.0-or-later
// Unity field restoration adapted from Il2CppInterop Pass80UnstripFields (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using BepInEx.Logging;

namespace Modstoke.Preloader
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
                    if (type == null)
                        continue;

                    foreach (var field in originalType.Fields)
                    {
                        // Native wrappers only gain source-qualified managed storage, never native layout fields.
                        if (type.OriginalType != null && field.IsStatic && (!field.IsLiteral || field.Constant == null) &&
                            !CanRestoreManagedFlag(field, type) && !CanRestoreUxmlConverterCache(field, type, resolver) &&
                            !InteropTextShaderCache.CanRestoreField(field, type, resolver) &&
                            !InteropOverlayRepaintContract.CanRestoreField(field, type, resolver))
                            continue;
                        if (!field.IsStatic && type.OriginalType != null)
                            continue;
                        if (field.FieldRva != null)
                            continue;
                        if (type.TryGetFieldByUnityAssemblyField(field) != null)
                            continue;
                        if (type.NewType.Fields.Any(existing => existing.Name == field.Name))
                            continue;

                        var threadStatic = IsThreadStatic(field);
                        if (threadStatic && (type.OriginalType != null || !field.IsStatic || field.IsLiteral))
                        {
                            LogUnsupported(field, originalType);
                            ignored++;
                            continue;
                        }

                        var restoredInstance = !field.IsStatic && type.OriginalType == null;
                        var managedOnly = InteropRestorationPlan.For(context).ContainsManagedOnlyStorage(originalType.ToTypeSignature());
                        var fieldType = type.OriginalType == null
                            ? resolver.ResolveFieldStorage(field.Signature.FieldType)
                            : resolver.Resolve(field.Signature.FieldType);
                        var storageType = InteropRestoredTypeSupport.StorageType(fieldType);
                        if (restoredInstance && !managedOnly && (storageType == null || !InteropTypeReferences.IsValueType(storageType)))
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
                        if (threadStatic)
                        {
                            // CLR owns per-thread storage, including each closed generic helper's statics.
                            var module = assembly.Imports.Module;
                            var attributeType = module.DefaultImporter.ImportTypeSignature(typeof(ThreadStaticAttribute)).ToTypeDefOrRef();
                            var constructor = new MemberReference(attributeType, ".ctor",
                                MethodSignature.CreateInstance(module.CorLibTypeFactory.Void));
                            copy.CustomAttributes.Add(new CustomAttribute(constructor));
                        }
                        if (field.Constant != null)
                            copy.Constant = field.Constant;
                        if (restoredInstance && !managedOnly)
                        {
                            copy.FieldOffset = field.FieldOffset;
                            if (field.MarshalDescriptor is SimpleMarshalDescriptor descriptor)
                                copy.MarshalDescriptor = new SimpleMarshalDescriptor(descriptor.NativeType)
                                {
                                    ExtraData = descriptor.ExtraData == null ? null : (byte[])descriptor.ExtraData.Clone()
                                };
                            else if (storageType.FullName == "System.Boolean")
                                copy.MarshalDescriptor = new SimpleMarshalDescriptor(NativeType.U1);
                        }
                        copy.HasFieldMarshal = copy.MarshalDescriptor != null;
                        type.NewType.Fields.Add(copy);
                        restored++;
                    }
                }
            }
            InteropGenerationDiagnostics.Log(LogLevel.Info, $"Restored {restored} fields");
            InteropGenerationDiagnostics.Log(LogLevel.Info, $"Failed to restore {ignored} fields");
        }

        internal static bool IsThreadStatic(FieldDefinition field) =>
            field.CustomAttributes.Any(attribute => InteropRestoredUnmanagedDelegateSupport.IsSystemType(
                attribute.Constructor?.DeclaringType, "System.ThreadStaticAttribute"));

        private static bool CanRestoreUxmlConverterCache(FieldDefinition field, InteropTypeContext type,
            InteropUnstripTypeResolver resolver)
        {
            var source = field.DeclaringType;
            var sourceOuter = source.DeclaringType;
            if (field.Name != "<>9__4_0" || source.Name != "<>c" || sourceOuter == null ||
                source.DeclaringModule.Assembly.Name != "UnityEngine.UIElementsModule" ||
                source.DeclaringModule.Mvid != new Guid("b6e504b1-20f0-43e3-9923-7045b32ad8bb") ||
                field.Attributes != (FieldAttributes.Public | FieldAttributes.Static) ||
                field.Constant != null || field.FieldRva != null || field.MarshalDescriptor != null ||
                IsThreadStatic(field) || field.Signature == null || field.Signature.HasThis ||
                source.GenericParameters.Count != 0 || source.IsValueType || !source.IsSealed ||
                sourceOuter.GenericParameters.Count != 0 || sourceOuter.DeclaringType != null || sourceOuter.IsValueType ||
                type.OriginalType == null || type.OriginalType.FullName != source.FullName ||
                type.OriginalType.GenericParameters.Count != 0 || type.OriginalType.IsValueType ||
                type.OriginalType.DeclaringModule.Assembly.Name != source.DeclaringModule.Assembly.Name ||
                type.OriginalType.Fields.Any(native => native.Name == field.Name))
                return false;

            var callbackToken = sourceOuter.FullName switch
            {
                "UnityEngine.UIElements.UxmlFloatAttributeDescription" => 0x06001ED2,
                "UnityEngine.UIElements.UxmlDoubleAttributeDescription" => 0x06001EDB,
                "UnityEngine.UIElements.UxmlLongAttributeDescription" => 0x06001EED,
                "UnityEngine.UIElements.UxmlHash128AttributeDescription" => 0x06001F19,
                _ => 0
            };
            if (callbackToken == 0 || field.Signature.FieldType is not GenericInstanceTypeSignature cache ||
                cache.IsValueType || cache.GenericType.FullName != "System.Func`3" ||
                InteropRestoredTypeSupport.GetAssemblyName(cache) != "mscorlib" || cache.TypeArguments.Count != 3 ||
                cache.TypeArguments[0] is not CorLibTypeSignature { ElementType: ElementType.String } ||
                !SignatureComparer.Default.Equals(cache.TypeArguments[1], cache.TypeArguments[2]))
                return false;

            var value = cache.TypeArguments[1];
            var matchesValue = callbackToken switch
            {
                0x06001ED2 => value is CorLibTypeSignature { ElementType: ElementType.R4 },
                0x06001EDB => value is CorLibTypeSignature { ElementType: ElementType.R8 },
                0x06001EED => value is CorLibTypeSignature { ElementType: ElementType.I8 },
                0x06001F19 => value is TypeDefOrRefSignature { IsValueType: true } &&
                    value.FullName == "UnityEngine.Hash128" &&
                    InteropRestoredTypeSupport.GetAssemblyName(value) == "UnityEngine.CoreModule",
                _ => false
            };
            var context = type.AssemblyContext.GlobalContext;
            var outer = type.AssemblyContext.TryGetTypeByName(sourceOuter.FullName);
            var nativeDelegate = context.TryGetTypeByName("mscorlib", "System.Func`3");
            if (!matchesValue || outer?.OriginalType == null ||
                outer.OriginalType.FullName != sourceOuter.FullName || outer.OriginalType.DeclaringType != null ||
                outer.OriginalType.GenericParameters.Count != 0 || outer.OriginalType.IsValueType ||
                type.OriginalType.DeclaringType != outer.OriginalType || type.NewType.DeclaringType != outer.NewType ||
                type.NewType.GenericParameters.Count != 0 || outer.NewType.GenericParameters.Count != 0 ||
                resolver.ResolveRaw(source.ToTypeSignature())?.Resolve() != type.NewType ||
                resolver.ResolveRaw(sourceOuter.ToTypeSignature())?.Resolve() != outer.NewType ||
                nativeDelegate?.OriginalType == null || nativeDelegate.OriginalType.FullName != "System.Func`3" ||
                nativeDelegate.OriginalType.DeclaringModule.Assembly.Name != "mscorlib" ||
                nativeDelegate.NewType.FullName != "Il2CppSystem.Func`3" ||
                !InteropDelegateContracts.CanStoreNativeDelegate(cache.GenericType.Resolve(), nativeDelegate.NewType) ||
                InteropRestorationPlan.For(context).GetNativeIdentityRejection(cache) != null ||
                resolver.Resolve(cache) is not GenericInstanceTypeSignature nativeCache || nativeCache.IsValueType ||
                nativeCache.GenericType.Resolve() != nativeDelegate.NewType || nativeCache.TypeArguments.Count != 3 ||
                nativeCache.TypeArguments[0] is not CorLibTypeSignature { ElementType: ElementType.String } ||
                !SignatureComparer.Default.Equals(nativeCache.TypeArguments[1], resolver.Resolve(value)) ||
                !SignatureComparer.Default.Equals(nativeCache.TypeArguments[1], nativeCache.TypeArguments[2]))
                return false;

            var invoke = nativeDelegate.NewType.Methods.Single(method => method.Name == "Invoke")
                .Signature.InstantiateGenericTypes(GenericContext.FromType(nativeCache));
            if (invoke.ParameterTypes.Count != 2 ||
                !SignatureComparer.Default.Equals(invoke.ParameterTypes[0], nativeCache.TypeArguments[0]) ||
                !SignatureComparer.Default.Equals(invoke.ParameterTypes[1], nativeCache.TypeArguments[1]) ||
                !SignatureComparer.Default.Equals(invoke.ReturnType, nativeCache.TypeArguments[2]))
                return false;

            var callback = source.Methods.SingleOrDefault(method => method.MetadataToken.ToInt32() == callbackToken);
            if (callback == null || callback.Name != "<TryGetValueFromBag>b__4_0" || callback.IsStatic ||
                callback.IsConstructor || callback.IsAbstract || callback.IsPInvokeImpl || callback.CilMethodBody == null ||
                callback.GenericParameters.Count != 0 || callback.Signature.GenericParameterCount != 0 ||
                !InteropDelegateContracts.HasStandardConvention(callback.Signature) || !callback.Signature.HasThis ||
                callback.Signature.ParameterTypes.Count != 2 ||
                !SignatureComparer.Default.Equals(callback.Signature.ParameterTypes[0], cache.TypeArguments[0]) ||
                !SignatureComparer.Default.Equals(callback.Signature.ParameterTypes[1], value) ||
                !SignatureComparer.Default.Equals(callback.Signature.ReturnType, value))
                return false;

            var singleton = source.Fields.SingleOrDefault(candidate => candidate.Name == "<>9");
            var nativeSingleton = singleton == null ? null : type.TryGetFieldByUnityAssemblyField(singleton);
            if (singleton == null || !singleton.IsStatic || !singleton.IsInitOnly || singleton.IsLiteral ||
                !SignatureComparer.Default.Equals(singleton.Signature?.FieldType, source.ToTypeSignature()) ||
                nativeSingleton == null || !nativeSingleton.OriginalField.IsStatic ||
                nativeSingleton.OriginalField.Signature.FieldType.Resolve() != type.OriginalType ||
                type.NewType.Fields.Any(candidate => candidate.Name == singleton.Name))
                return false;
            var singletonGetter = type.NewType.Properties
                .SingleOrDefault(property => property.Name == nativeSingleton.UnmangledName)?.GetMethod;
            if (singletonGetter == null || !singletonGetter.IsStatic || singletonGetter.Signature.ParameterTypes.Count != 0 ||
                !SignatureComparer.Default.Equals(singletonGetter.Signature.ReturnType, type.NewType.ToTypeSignature()))
                return false;

            // Only this stripped call site may own the cache; native fields and the singleton remain untouched.
            var used = false;
            foreach (var owner in source.DeclaringModule.GetAllTypes())
            foreach (var method in owner.Methods)
            {
                var body = method.CilMethodBody;
                if (body == null) continue;
                for (var index = 0; index < body.Instructions.Count; index++)
                {
                    if (body.Instructions[index].Operand is not IFieldDescriptor referenced ||
                        referenced.Name != field.Name ||
                        referenced.DeclaringType?.FullName != source.FullName && referenced.Resolve() != field)
                        continue;
                    if (used || owner != sourceOuter || method.Name != "TryGetValueFromBag" || method.IsStatic ||
                        method.IsConstructor || method.IsAbstract || method.IsPInvokeImpl ||
                        method.GenericParameters.Count != 0 || method.Signature.GenericParameterCount != 0 ||
                        !InteropDelegateContracts.HasStandardConvention(method.Signature) || !method.Signature.HasThis ||
                        method.Signature.ReturnType is not CorLibTypeSignature { ElementType: ElementType.Boolean } ||
                        method.Signature.ParameterTypes.Count != 3 ||
                        method.Signature.ParameterTypes[0].FullName != "UnityEngine.UIElements.IUxmlAttributes" ||
                        method.Signature.ParameterTypes[0].Resolve()?.DeclaringModule != source.DeclaringModule ||
                        method.Signature.ParameterTypes[1].FullName != "UnityEngine.UIElements.CreationContext" ||
                        method.Signature.ParameterTypes[1].Resolve()?.DeclaringModule != source.DeclaringModule ||
                        method.Signature.ParameterTypes[2] is not ByReferenceTypeSignature output ||
                        !SignatureComparer.Default.Equals(output.BaseType, value) ||
                        outer.TryGetMethodByUnityAssemblyMethod(method) != null ||
                        !HasUxmlConverterCacheSequence(body, index, field, singleton, callback))
                        return false;
                    used = true;
                    index += 8;
                }
            }
            // The real CLR static roots the converted native Func wrapper, not just the weak conversion-cache entry.
            return used;
        }

        private static bool HasUxmlConverterCacheSequence(CilMethodBody body, int index, FieldDefinition field,
            FieldDefinition singleton, MethodDefinition callback)
        {
            var code = body.Instructions;
            if (body.ExceptionHandlers.Count != 0 || index + 9 >= code.Count ||
                code[index].OpCode.Code != CilCode.Ldsfld || !MatchesField(code[index].Operand, field) ||
                code[index + 1].OpCode.Code != CilCode.Dup ||
                code[index + 2].OpCode.Code is not (CilCode.Brtrue or CilCode.Brtrue_S) ||
                code[index + 2].Operand is not CilInstructionLabel branch || branch.Instruction != code[index + 9] ||
                code[index + 3].OpCode.Code != CilCode.Pop ||
                code[index + 4].OpCode.Code != CilCode.Ldsfld || !MatchesField(code[index + 4].Operand, singleton) ||
                code[index + 5].OpCode.Code != CilCode.Ldftn ||
                code[index + 5].Operand is not IMethodDescriptor target || target is MethodSpecification ||
                target.Resolve() != callback || !SignatureComparer.Default.Equals(target.Signature, callback.Signature) ||
                !SignatureComparer.Default.Equals(target.DeclaringType.ToTypeSignature(), callback.DeclaringType.ToTypeSignature()) ||
                code[index + 6].OpCode.Code != CilCode.Newobj ||
                code[index + 6].Operand is not IMethodDescriptor constructor || constructor is MethodSpecification ||
                constructor.Name != ".ctor" ||
                !SignatureComparer.Default.Equals(constructor.DeclaringType?.ToTypeSignature(), field.Signature.FieldType) ||
                constructor.Resolve() is not { IsConstructor: true, IsStatic: false } definition ||
                definition.DeclaringType != ((GenericInstanceTypeSignature)field.Signature.FieldType).GenericType.Resolve() ||
                !SignatureComparer.Default.Equals(constructor.Signature, definition.Signature) ||
                code[index + 7].OpCode.Code != CilCode.Dup ||
                code[index + 8].OpCode.Code != CilCode.Stsfld || !MatchesField(code[index + 8].Operand, field))
                return false;

            // An alternate entry could bypass conversion or supply another value to the static store.
            foreach (var instruction in code)
            {
                if (instruction.Operand is ICilLabel label && EntersSequence(label)) return false;
                if (instruction.Operand is IReadOnlyList<ICilLabel> labels && labels.Any(EntersSequence)) return false;
            }
            return true;

            bool EntersSequence(ICilLabel label)
            {
                if (label is not CilInstructionLabel { Instruction: not null } entry) return true;
                var position = code.IndexOf(entry.Instruction);
                return position < 0 || position > index && position <= index + 8;
            }

            static bool MatchesField(object operand, FieldDefinition expected) =>
                operand is IFieldDescriptor reference && reference.Resolve() == expected &&
                SignatureComparer.Default.Equals(reference.Signature, expected.Signature) &&
                SignatureComparer.Default.Equals(reference.DeclaringType?.ToTypeSignature(), expected.DeclaringType.ToTypeSignature());
        }

        private static bool CanRestoreManagedFlag(FieldDefinition field, InteropTypeContext type)
        {
            var source = field.DeclaringType;
            var qualified = source.FullName switch
            {
                "UnityEngine.Physics2D" =>
                    source.DeclaringModule.Assembly.Name == "UnityEngine.Physics2DModule" &&
                    source.DeclaringModule.Mvid == new Guid("32c35538-d8d0-4678-8be2-093f5a93ba33") &&
                    (string)field.Name is ("<alwaysShowColliders>k__BackingField" or "<showCollidersFilled>k__BackingField" or
                        "<showColliderSleep>k__BackingField" or "<showColliderContacts>k__BackingField"),
                "UnityEngine.UIElements.MouseCaptureController" =>
                    source.DeclaringModule.Assembly.Name == "UnityEngine.UIElementsModule" &&
                    source.DeclaringModule.Mvid == new Guid("b6e504b1-20f0-43e3-9923-7045b32ad8bb") &&
                    (string)field.Name is ("m_IsMouseCapturedWarningEmitted" or "m_ReleaseMouseWarningEmitted"),
                _ => false
            };
            if (!qualified || !field.IsPrivate || !field.IsStatic || field.IsLiteral || field.IsInitOnly ||
                field.Constant != null || field.FieldRva != null || field.MarshalDescriptor != null ||
                field.Signature.FieldType is not CorLibTypeSignature { ElementType: ElementType.Boolean } ||
                source.GenericParameters.Count != 0 || source.IsValueType || source.DeclaringType != null ||
                type.OriginalType.FullName != source.FullName || type.OriginalType.GenericParameters.Count != 0 ||
                type.OriginalType.DeclaringModule.Assembly.Name != source.DeclaringModule.Assembly.Name ||
                type.OriginalType.Fields.Any(native => native.Name == field.Name) ||
                field.CustomAttributes.Any(attribute => attribute.Constructor?.DeclaringType?.FullName == "System.ThreadStaticAttribute"))
                return false;

            // These source-pinned flags start false and have no native consumers. Never split native and CLR state.
            var used = false;
            foreach (var method in source.Methods)
            {
                var body = method.CilMethodBody;
                if (body == null)
                    continue;
                foreach (var instruction in body.Instructions)
                {
                    if (instruction.Operand is not IFieldDescriptor referenced || referenced.Name != field.Name ||
                        referenced.DeclaringType?.FullName != source.FullName)
                        continue;
                    if (!method.IsStatic || method.IsConstructor || method.Signature.GenericParameterCount != 0 ||
                        instruction.OpCode.Code is not (CilCode.Ldsfld or CilCode.Stsfld) ||
                        type.TryGetMethodByUnityAssemblyMethod(method) != null)
                        return false;
                    used = true;
                }
            }
            return used;
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
