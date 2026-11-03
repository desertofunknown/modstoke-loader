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
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodRestoration
    {
        private sealed record MovieTextureBinding(InteropTypeContext Owner, TypeDefinition Source,
            (MethodDefinition Target, InteropRestorationEntry Entry)[] Methods);

        private readonly List<MovieTextureBinding> movieTextureBindings = new();
        private readonly Dictionary<MethodDefinition, MethodDefinition[]> movieTextureDependencies =
            new(ReferenceEqualityComparer.Instance);

        private bool TryRestoreMovieTextureConstructor(MethodDefinition source, MethodDefinition constructor,
            InteropTypeContext owner, InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver,
            InteropRestorationEntry entry)
        {
            entry.Set("signature-rejected", "native-movie-texture-constructor-contract",
                "MovieTexture requires its original private constructor and same-object native Texture initialization.");
            if (!InteropNativeMovieTextureType.IsPrepared(owner.OriginalType) || !InteropNativeMovieTextureType.IsSourceConstructor(source) ||
                !IsReferenceConstructorDeclaration(constructor) || constructor.Attributes != source.Attributes ||
                !constructor.IsPrivate || constructor.Signature.ParameterTypes.Count != 0 || constructor.Parameters.Count != 0 ||
                constructor.CilMethodBody is not { } constructorBody || constructorBody.Instructions.Count != 0 ||
                constructorBody.LocalVariables.Count != 0 || constructorBody.ExceptionHandlers.Count != 0 ||
                !owner.NewType.IsSealed || owner.NewType.Fields.Any(field => !field.IsStatic) ||
                !SignatureComparer.Default.Equals(resolver.Resolve(source.DeclaringType.BaseType.ToTypeSignature()),
                    owner.NewType.BaseType?.ToTypeSignature())) return false;
            var baseSource = ((IMethodDescriptor)source.CilMethodBody.Instructions[1].Operand).Resolve();
            var baseOwner = owner.AssemblyContext.GlobalContext.TryGetAssemblyByName("UnityEngine.CoreModule")?.TryGetTypeByName("UnityEngine.Texture");
            var nativeBase = baseOwner?.TryGetMethodByUnityAssemblyMethod(baseSource);
            if (baseOwner?.OriginalType == null || baseOwner.ClassPointerFieldRef == null || baseOwner.NewType != owner.NewType.BaseType?.Resolve() ||
                nativeBase?.NonGenericMethodInfoPointerField == null || nativeBase.OriginalMethod.DeclaringType != baseOwner.OriginalType ||
                !IsReferenceConstructorDeclaration(nativeBase.OriginalMethod) || !nativeBase.OriginalMethod.IsFamily ||
                nativeBase.NewMethod.DeclaringType != baseOwner.NewType || nativeBase.NewMethod.CilMethodBody == null ||
                !SignatureComparer.Default.Equals(nativeBase.NewMethod.Signature,
                    MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void))) return false;

            var pointerSignature = MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void, imports.Module.CorLibTypeFactory.IntPtr);
            var pointers = owner.NewType.Methods.Where(method => method.Name == ".ctor" &&
                SignatureComparer.Default.Equals(method.Signature, pointerSignature)).ToArray();
            if (pointers.Length != 1 || !pointers[0].IsPublic || !IsReferenceConstructorDeclaration(pointers[0]) ||
                pointers[0].CilMethodBody is not { } pointerBody || pointerBody.LocalVariables.Count != 0 || pointerBody.ExceptionHandlers.Count != 0 ||
                !pointerBody.Instructions.Select(instruction => instruction.OpCode.Code).SequenceEqual(
                    new[] { CilCode.Ldarg_0, CilCode.Ldarg_1, CilCode.Call, CilCode.Ret }) ||
                pointerBody.Instructions[2].Operand is not IMethodDescriptor pointerBase || pointerBase is MethodSpecification ||
                pointerBase.Name != ".ctor" || !SignatureComparer.Default.Equals(pointerBase.DeclaringType, owner.NewType.BaseType) ||
                !SignatureComparer.Default.Equals(pointerBase.Signature, pointerSignature)) return false;

            const string name = "__InitializeMovieTexture_06000147";
            if (owner.NewType.Methods.Any(method => method.Name == name) || owner.TryGetRestoredBody(source) != null ||
                owner.TryGetRestoredReferenceConstructor(source) != null) return false;
            var initializer = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.HideBySig,
                MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void))
            {
                CilMethodBody = new CilMethodBody()
            };
            owner.NewType.Methods.Add(constructor);
            owner.NewType.Methods.Add(initializer);
            owner.AddRestoredReferenceConstructor(source, constructor, initializer);
            owner.AddRestoredBody(source, constructor);
            referenceConstructorBodies.Add(constructor, initializer);
            pendingBodies.Add((source, initializer, owner, imports, entry));
            entry.Set("body-pending");

            var body = constructorBody.Instructions;
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Ldsfld, owner.ClassPointerFieldRef);
            body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_new.Value);
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(pointers[0]));
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Call, initializer);
            body.Add(CilOpCodes.Ret);
            return true;
        }

        private void PrepareMovieTextureBindings(InteropRewriteContext context)
        {
            var assembly = context.TryGetAssemblyByName(InteropNativeMovieTextureType.AssemblyName);
            var owner = assembly?.TryGetTypeByName(InteropNativeMovieTextureType.TypeName);
            if (owner == null || !InteropNativeMovieTextureType.IsPrepared(owner.OriginalType)) return;
            var source = context.UnityAssemblies.Assemblies.Single(candidate => candidate.Name == InteropNativeMovieTextureType.AssemblyName)
                .ManifestModule.GetAllTypes().Single(type => type.FullName == InteropNativeMovieTextureType.TypeName);
            var methods = Enumerable.Range(0, InteropNativeMovieTextureType.MethodCount).Select(index =>
            {
                var method = pendingBodies.SingleOrDefault(item => item.Type == owner &&
                    item.Original.MetadataToken.ToInt32() == InteropNativeMovieTextureType.FirstMethodToken + index);
                return (method.Target, method.Entry);
            }).ToArray();
            movieTextureBindings.Add(new MovieTextureBinding(owner, source, methods));
            if (methods[1].Target != null)
                movieTextureDependencies.Add(methods[1].Target, methods.Where((method, index) => index != 1 && method.Target != null)
                    .Select(method => method.Target).ToArray());
        }

        private void MarkUnavailableMovieTextureContracts()
        {
            foreach (var binding in movieTextureBindings)
                if (binding.Methods[1].Entry?.Outcome == "managed-body-translated" && binding.Methods.Any(method => method.Target == null))
                    binding.Methods[1].Entry.Set("managed-body-partial", "native-movie-texture-bodies-unavailable",
                        "Native MovieTexture requires all eleven original method bodies before construction or callback binding.");
        }

        private void BindMovieTextureBodies()
        {
            if (movieTextureBindings.Count == 0) return;
            var binder = InteropNativeMovieTextureType.RequireRuntimeMethod("BindBodies");
            foreach (var binding in movieTextureBindings)
            {
                var owner = binding.Owner;
                var imports = owner.AssemblyContext.Imports;
                var importer = imports.Module.DefaultImporter;
                var resolver = new InteropUnstripTypeResolver(owner.AssemblyContext.GlobalContext, imports);
                var code = owner.NewType.GetOrCreateStaticConstructor().CilMethodBody?.Instructions;
                if (code == null || code.Count == 0 || code[^1].OpCode.Code != CilCode.Ret)
                    throw new InvalidOperationException("The MovieTexture wrapper has no completed class initializer.");
                owner.NewType.IsBeforeFieldInit = false;
                code.RemoveAt(code.Count - 1);
                if (binding.Methods.Any(method => method.Target == null || method.Entry?.Outcome != "managed-body-translated"))
                {
                    code.Add(CilOpCodes.Ldstr, "Native MovieTexture is unavailable because its original bodies or dependencies could not be restored.");
                    code.Add(CilOpCodes.Newobj, importer.ImportMethod(typeof(NotSupportedException).GetConstructor(new[] { typeof(string) })));
                    code.Add(CilOpCodes.Throw);
                    continue;
                }

                var self = owner.NewType.ToTypeSignature();
                var voidType = imports.Module.CorLibTypeFactory.Void;
                var boolean = imports.Module.CorLibTypeFactory.Boolean;
                var objectBase = importer.ImportTypeSignature(typeof(Il2CppObjectBase));
                code.Add(CilOpCodes.Ldsfld, owner.ClassPointerFieldRef);
                for (var index = 0; index < binding.Methods.Length; index++)
                {
                    var source = binding.Source.Methods[index];
                    var target = binding.Methods[index].Target;
                    var result = resolver.Resolve(source.Signature.ReturnType);
                    var arguments = source.Signature.ParameterTypes.Select(resolver.Resolve).ToArray();
                    if (result == null || arguments.Any(argument => argument == null))
                        throw new InvalidOperationException("A MovieTexture callback signature could not be resolved: " + source.FullName);
                    var expected = source.IsStatic ? MethodSignature.CreateStatic(result, arguments) : MethodSignature.CreateInstance(result, arguments);
                    if (target.DeclaringType != owner.NewType ||
                        target.GenericParameters.Count != 0 || !SignatureComparer.Default.Equals(target.Signature, expected) ||
                        index != 1 && (target.Name != source.Name || target.Attributes != source.Attributes) ||
                        index == 1 && (owner.TryGetRestoredReferenceInitializer(source) != target || !target.IsPrivate || target.IsStatic))
                        throw new InvalidOperationException("A restored MovieTexture body differs from its native signature: " + target.FullName);
                    var bridgeName = "__MovieTextureDispatch" + index;
                    if (owner.NewType.Methods.Any(method => method.Name == bridgeName))
                        throw new InvalidOperationException("The MovieTexture dispatcher already exists: " + bridgeName);
                    // The native AudioClip return stays exact; only the CLR callback envelope uses the wrapper base.
                    var callbackResult = index == 5 ? objectBase : result;
                    var bridgeArguments = source.IsStatic ? arguments : new[] { self }.Concat(arguments).ToArray();
                    var bridge = new MethodDefinition(bridgeName, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
                        MethodSignature.CreateStatic(callbackResult, bridgeArguments))
                    {
                        CilMethodBody = new CilMethodBody()
                    };
                    for (var argument = 0; argument < bridgeArguments.Length; argument++)
                        bridge.CilMethodBody.Instructions.Add(CilOpCodes.Ldarg, bridge.Parameters[argument]);
                    // Native redispatch would re-enter this bridge, including for the constructor callback.
                    bridge.CilMethodBody.Instructions.Add(CilOpCodes.Call, target);
                    bridge.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
                    owner.NewType.Methods.Add(bridge);
                    TypeSignature callback = index switch
                    {
                        0 => importer.ImportTypeSignature(typeof(Action)),
                        5 => new GenericInstanceTypeSignature(importer.ImportType(typeof(Func<,>)), false, self, objectBase),
                        6 or 8 or 9 => new GenericInstanceTypeSignature(importer.ImportType(typeof(Func<,>)), false, self, boolean),
                        7 => new GenericInstanceTypeSignature(importer.ImportType(typeof(Action<,>)), false, self, boolean),
                        10 => new GenericInstanceTypeSignature(importer.ImportType(typeof(Func<,>)), false, self, imports.Module.CorLibTypeFactory.Single),
                        _ => new GenericInstanceTypeSignature(importer.ImportType(typeof(Action<>)), false, self)
                    };
                    code.Add(CilOpCodes.Ldnull);
                    code.Add(CilOpCodes.Ldftn, bridge);
                    code.Add(CilOpCodes.Newobj, new MemberReference(callback.ToTypeDefOrRef(), ".ctor",
                        MethodSignature.CreateInstance(voidType, imports.Module.CorLibTypeFactory.Object, imports.Module.CorLibTypeFactory.IntPtr)));
                }
                code.Add(CilOpCodes.Call, ((IMethodDefOrRef)importer.ImportMethod(binder)).MakeGenericInstanceMethod(self));
                code.Add(CilOpCodes.Ret);
            }
        }
    }
}
