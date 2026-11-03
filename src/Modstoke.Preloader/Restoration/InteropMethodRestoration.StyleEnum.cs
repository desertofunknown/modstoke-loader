// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using MethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodRestoration
    {
        private sealed record StyleEnumBinding(InteropTypeContext Owner, InteropRestorationEntry ConstructorEntry,
            (MethodDefinition Target, InteropRestorationEntry Entry)[] Methods);

        private readonly List<StyleEnumBinding> styleEnumBindings = new();
        private readonly Dictionary<MethodDefinition, MethodDefinition[]> styleEnumDependencies =
            new(ReferenceEqualityComparer.Instance);

        private bool TryRestoreStyleEnumConstructor(MethodDefinition source, MethodDefinition constructor,
            InteropTypeContext owner, InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver,
            InteropRestorationEntry entry)
        {
            entry.Set("signature-rejected", "native-style-enum-constructor-contract",
                "ValuesEnum requires its original empty constructor and the native ValuesDiscrete<int> initialization chain.");
            if (!InteropNativeStyleEnumType.IsPrepared(owner.OriginalType) ||
                source.DeclaringModule.Mvid != new Guid("b6e504b1-20f0-43e3-9923-7045b32ad8bb") ||
                source.DeclaringType.FullName != InteropNativeStyleEnumType.TypeName ||
                source.MetadataToken.ToInt32() != InteropNativeStyleEnumType.ConstructorToken ||
                !IsReferenceConstructorDeclaration(source) || source.Signature.ParameterTypes.Count != 0 ||
                source.DeclaringType.Fields.Count != 0 || source.CilMethodBody is not { } sourceBody ||
                sourceBody.ExceptionHandlers.Count != 0 || sourceBody.LocalVariables.Count != 0 ||
                !SignatureComparer.Default.Equals(resolver.Resolve(source.DeclaringType.BaseType.ToTypeSignature()),
                    owner.NewType.BaseType?.ToTypeSignature()))
                return false;

            var code = sourceBody.Instructions;
            if (code.Count != 4 || code[0].OpCode.Code != CilCode.Ldarg_0 || code[1].OpCode.Code != CilCode.Call ||
                code[2].OpCode.Code != CilCode.Nop || code[3].OpCode.Code != CilCode.Ret ||
                code[1].Operand is not IMethodDescriptor baseCall || baseCall is MethodSpecification ||
                baseCall.Name != ".ctor" || !baseCall.Signature.HasThis ||
                !InteropDelegateContracts.HasStandardConvention(baseCall.Signature) ||
                baseCall.Signature.GenericParameterCount != 0 || baseCall.Signature.ParameterTypes.Count != 0 ||
                baseCall.Signature.ReturnType.ElementType != ElementType.Void ||
                !SignatureComparer.Default.Equals(baseCall.DeclaringType, source.DeclaringType.BaseType) ||
                baseCall.Resolve() is not { } baseConstructor || baseConstructor.MetadataToken.ToInt32() != 100670304 ||
                baseConstructor.DeclaringModule != source.DeclaringModule)
                return false;
            var pointerSignature = MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void,
                imports.Module.CorLibTypeFactory.IntPtr);
            var pointers = owner.NewType.Methods.Where(method => method.Name == ".ctor" &&
                SignatureComparer.Default.Equals(method.Signature, pointerSignature)).ToArray();
            if (pointers.Length != 1 || !pointers[0].IsPublic || pointers[0].CilMethodBody == null) return false;

            var initialize = imports.Module.DefaultImporter.ImportMethod(InteropNativeStyleEnumType.RequireRuntimeMethod("Initialize"));
            var body = constructor.CilMethodBody.Instructions;
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Ldsfld, owner.ClassPointerFieldRef);
            body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_new.Value);
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(pointers[0]));
            // Native initialization uses this allocation; the generic base constructor must not allocate a second object.
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Call, initialize);
            body.Add(CilOpCodes.Ret);
            owner.NewType.Methods.Add(constructor);
            owner.AddRestoredReferenceConstructor(source, constructor);
            owner.AddRestoredBody(source, constructor);
            completedBodies.Add((constructor, entry));
            entry.Set("managed-body-translated");
            return true;
        }

        private void PrepareStyleEnumBindings(InteropRewriteContext context)
        {
            var assembly = context.TryGetAssemblyByName(InteropNativeStyleEnumType.AssemblyName);
            var owner = assembly?.TryGetTypeByName(InteropNativeStyleEnumType.TypeName);
            if (owner == null || !InteropNativeStyleEnumType.IsPrepared(owner.OriginalType)) return;
            var constructor = completedBodies.SingleOrDefault(item => item.Target.DeclaringType == owner.NewType &&
                item.Target.Name == ".ctor" && item.Target.Signature.ParameterTypes.Count == 0);
            var methods = new[] { InteropNativeStyleEnumType.UpdateAllToken, InteropNativeStyleEnumType.UpdateOneToken }
                .Select(token =>
                {
                    var method = pendingBodies.SingleOrDefault(item => item.Type == owner && item.Original.MetadataToken.ToInt32() == token);
                    return (method.Target, method.Entry);
                }).ToArray();
            styleEnumBindings.Add(new StyleEnumBinding(owner, constructor.Entry, methods));
            if (constructor.Target != null)
                styleEnumDependencies.Add(constructor.Target, methods.Where(method => method.Target != null)
                    .Select(method => method.Target).ToArray());
        }

        private void MarkUnavailableStyleEnumContracts()
        {
            foreach (var binding in styleEnumBindings)
            {
                if (binding.ConstructorEntry?.Outcome != "managed-body-translated" ||
                    binding.Methods.All(method => method.Target != null)) continue;
                binding.ConstructorEntry.Set("managed-body-partial", "native-style-enum-overrides-unavailable",
                    "A native ValuesEnum cannot be allocated without both restored UpdateComputedStyle bodies.");
            }
        }

        private void BindStyleEnumBodies()
        {
            if (styleEnumBindings.Count == 0) return;
            var binder = InteropNativeStyleEnumType.RequireRuntimeMethod("BindOverrides");
            foreach (var binding in styleEnumBindings)
            {
                var owner = binding.Owner;
                var imports = owner.AssemblyContext.Imports;
                var importer = imports.Module.DefaultImporter;
                var cctor = owner.NewType.GetOrCreateStaticConstructor();
                var code = cctor.CilMethodBody?.Instructions;
                if (code == null || code.Count == 0 || code[^1].OpCode.Code != CilCode.Ret)
                    throw new InvalidOperationException("The native ValuesEnum wrapper has no completed class initializer.");
                var complete = binding.ConstructorEntry?.Outcome == "managed-body-translated" &&
                    binding.Methods.All(method => method.Target != null && method.Entry?.Outcome == "managed-body-translated");
                owner.NewType.IsBeforeFieldInit = false;
                code.RemoveAt(code.Count - 1);
                if (!complete)
                {
                    code.Add(CilOpCodes.Ldstr,
                        "Native ValuesEnum is unavailable because its constructor, overrides or their dependencies could not be restored.");
                    code.Add(CilOpCodes.Newobj, importer.ImportMethod(typeof(NotSupportedException).GetConstructor(new[] { typeof(string) })));
                    code.Add(CilOpCodes.Throw);
                    continue;
                }

                var self = owner.NewType.ToTypeSignature();
                var voidType = imports.Module.CorLibTypeFactory.Void;
                var intType = imports.Module.CorLibTypeFactory.Int32;
                code.Add(CilOpCodes.Ldsfld, owner.ClassPointerFieldRef);
                for (var index = 0; index < binding.Methods.Length; index++)
                {
                    var target = binding.Methods[index].Target;
                    var parameters = index == 0 ? Array.Empty<TypeSignature>() : new TypeSignature[] { intType };
                    if (target.Name != "UpdateComputedStyle" || target.IsStatic || !target.IsVirtual || !target.IsFinal ||
                        target.GenericParameters.Count != 0 ||
                        !SignatureComparer.Default.Equals(target.Signature, MethodSignature.CreateInstance(voidType, parameters)))
                        throw new InvalidOperationException("The restored ValuesEnum body differs from its native override signature: " + target.FullName);
                    var name = "__StyleEnumDispatch" + index;
                    if (owner.NewType.Methods.Any(method => method.Name == name))
                        throw new InvalidOperationException("The ValuesEnum dispatcher already exists: " + name);
                    var bridge = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
                        MethodSignature.CreateStatic(voidType, new[] { self }.Concat(parameters).ToArray()))
                    {
                        CilMethodBody = new CilMethodBody()
                    };
                    bridge.CilMethodBody.Instructions.Add(CilOpCodes.Ldarg_0);
                    if (index != 0) bridge.CilMethodBody.Instructions.Add(CilOpCodes.Ldarg_1);
                    // Call the translated body directly; native virtual dispatch would re-enter this bridge.
                    bridge.CilMethodBody.Instructions.Add(CilOpCodes.Call, target);
                    bridge.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
                    owner.NewType.Methods.Add(bridge);
                    var callback = index == 0
                        ? new GenericInstanceTypeSignature(importer.ImportType(typeof(Action<>)), false, self)
                        : new GenericInstanceTypeSignature(importer.ImportType(typeof(Action<,>)), false, self, intType);
                    code.Add(CilOpCodes.Ldnull);
                    code.Add(CilOpCodes.Ldftn, bridge);
                    code.Add(CilOpCodes.Newobj, new MemberReference(callback.ToTypeDefOrRef(), ".ctor",
                        MethodSignature.CreateInstance(voidType, imports.Module.CorLibTypeFactory.Object,
                            imports.Module.CorLibTypeFactory.IntPtr)));
                }
                code.Add(CilOpCodes.Call, ((IMethodDefOrRef)importer.ImportMethod(binder)).MakeGenericInstanceMethod(self));
                code.Add(CilOpCodes.Ret);
            }
        }
    }
}
