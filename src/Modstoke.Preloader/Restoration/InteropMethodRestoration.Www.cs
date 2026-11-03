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
        private sealed record WwwBinding(InteropTypeContext Owner, TypeDefinition Source,
            (MethodDefinition Target, InteropRestorationEntry Entry)[] Methods);

        private readonly List<WwwBinding> wwwBindings = new();
        private readonly Dictionary<MethodDefinition, MethodDefinition[]> wwwDependencies = new(ReferenceEqualityComparer.Instance);

        private bool TryRestoreWwwConstructor(MethodDefinition source, MethodDefinition constructor,
            InteropTypeContext owner, InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver,
            InteropRestorationEntry entry)
        {
            entry.Set("signature-rejected", "native-www-constructor-contract",
                "WWW requires its original constructor and same-object native CustomYieldInstruction initialization.");
            if (!InteropNativeWwwType.IsPrepared(owner.OriginalType) || !InteropNativeWwwType.IsSourceConstructor(source) ||
                !IsReferenceConstructorDeclaration(constructor) || constructor.Attributes != source.Attributes ||
                constructor.CilMethodBody is not { } constructorBody || constructorBody.Instructions.Count != 0 ||
                constructorBody.LocalVariables.Count != 0 || constructorBody.ExceptionHandlers.Count != 0 ||
                owner.NewType.IsSealed || owner.NewType.Fields.Any(field => !field.IsStatic) ||
                !SignatureComparer.Default.Equals(resolver.Resolve(source.DeclaringType.BaseType.ToTypeSignature()),
                    owner.NewType.BaseType?.ToTypeSignature())) return false;
            var baseSource = ((IMethodDescriptor)source.CilMethodBody.Instructions[1].Operand).Resolve();
            var baseOwner = owner.AssemblyContext.GlobalContext.TryGetAssemblyByName("UnityEngine.CoreModule")?
                .TryGetTypeByName("UnityEngine.CustomYieldInstruction");
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

            var name = "__InitializeWww_" + source.MetadataToken.ToInt32().ToString("X8");
            if (owner.NewType.Methods.Any(method => method.Name == name) || owner.TryGetRestoredBody(source) != null ||
                owner.TryGetRestoredReferenceConstructor(source) != null) return false;
            var initializer = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.HideBySig,
                MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void))
            {
                CilMethodBody = new CilMethodBody()
            };
            foreach (var parameter in constructor.Parameters)
                InteropMethodDeclarations.AddParameter(initializer, parameter.ParameterType, parameter.Name,
                    parameter.Definition?.Attributes ?? default);
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
            foreach (var parameter in constructor.Parameters) body.Add(CilOpCodes.Ldarg, parameter);
            body.Add(CilOpCodes.Call, initializer);
            body.Add(CilOpCodes.Ret);
            return true;
        }

        private void PrepareWwwBindings(InteropRewriteContext context)
        {
            var assembly = context.TryGetAssemblyByName(InteropNativeWwwType.AssemblyName);
            var owner = assembly?.TryGetTypeByName(InteropNativeWwwType.TypeName);
            if (owner == null || !InteropNativeWwwType.IsPrepared(owner.OriginalType)) return;
            var source = context.UnityAssemblies.Assemblies.Single(candidate => candidate.Name == InteropNativeWwwType.AssemblyName)
                .ManifestModule.GetAllTypes().Single(type => type.FullName == InteropNativeWwwType.TypeName);
            var methods = Enumerable.Range(0, InteropNativeWwwType.MethodCount).Select(index =>
            {
                var method = pendingBodies.SingleOrDefault(item => item.Type == owner &&
                    item.Original.MetadataToken.ToInt32() == InteropNativeWwwType.FirstMethodToken + index);
                return (method.Target, method.Entry);
            }).ToArray();
            wwwBindings.Add(new WwwBinding(owner, source, methods));
            var targets = methods.Where(method => method.Target != null).Select(method => method.Target).ToArray();
            // Every entry requires the complete body set, including static methods callable before construction.
            foreach (var target in targets) wwwDependencies.Add(target, targets);
        }

        private void MarkUnavailableWwwContracts()
        {
            foreach (var binding in wwwBindings)
                if (binding.Methods.Any(method => method.Target == null))
                    foreach (var method in binding.Methods)
                        if (method.Entry?.Outcome == "managed-body-translated")
                            method.Entry.Set("managed-body-partial", "native-www-bodies-unavailable",
                                "Native WWW requires all 47 original method bodies before callback binding.");
        }

        private void BindWwwBodies()
        {
            if (wwwBindings.Count == 0) return;
            var binder = InteropNativeWwwType.RequireRuntimeMethod("BindBodies");
            foreach (var binding in wwwBindings)
            {
                var owner = binding.Owner;
                var imports = owner.AssemblyContext.Imports;
                var importer = imports.Module.DefaultImporter;
                var resolver = new InteropUnstripTypeResolver(owner.AssemblyContext.GlobalContext, imports);
                var code = owner.NewType.GetOrCreateStaticConstructor().CilMethodBody?.Instructions;
                if (code == null || code.Count == 0 || code[^1].OpCode.Code != CilCode.Ret || owner.NewType.IsSealed)
                    throw new InvalidOperationException("The WWW wrapper has no compatible completed class initializer.");
                owner.NewType.IsBeforeFieldInit = false;
                code.RemoveAt(code.Count - 1);
                if (binding.Methods.Any(method => method.Target == null || method.Entry?.Outcome != "managed-body-translated"))
                {
                    code.Add(CilOpCodes.Ldstr, "Native WWW is unavailable because its original bodies or dependencies could not be restored.");
                    code.Add(CilOpCodes.Newobj, importer.ImportMethod(typeof(NotSupportedException).GetConstructor(new[] { typeof(string) })));
                    code.Add(CilOpCodes.Throw);
                    continue;
                }

                var hash = resolver.Resolve(binding.Source.Methods[6].Signature.ParameterTypes[1]);
                var cached = resolver.Resolve(binding.Source.Methods[8].Signature.ParameterTypes[1]);
                if (hash?.IsValueType != true || cached == null || cached.IsValueType)
                    throw new InvalidOperationException("The WWW callback aggregate representations are incompatible.");
                code.Add(CilOpCodes.Ldsfld, owner.ClassPointerFieldRef);
                code.Add(CilOpCodes.Ldc_I4, InteropNativeWwwType.MethodCount);
                code.Add(CilOpCodes.Newarr, importer.ImportType(typeof(Delegate)));
                for (var index = 0; index < binding.Methods.Length; index++)
                {
                    var bridge = CreateWwwBridge(binding, index, resolver, imports);
                    var callback = WwwCallbackType(bridge.Signature, imports);
                    owner.NewType.Methods.Add(bridge);
                    code.Add(CilOpCodes.Dup);
                    code.Add(CilOpCodes.Ldc_I4, index);
                    code.Add(CilOpCodes.Ldnull);
                    code.Add(CilOpCodes.Ldftn, bridge);
                    code.Add(CilOpCodes.Newobj, new MemberReference(callback.ToTypeDefOrRef(), ".ctor",
                        MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void,
                            imports.Module.CorLibTypeFactory.Object, imports.Module.CorLibTypeFactory.IntPtr)));
                    code.Add(CilOpCodes.Stelem_Ref);
                }
                var getType = importer.ImportMethod(typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle)));
                code.Add(CilOpCodes.Ldtoken, hash.ToTypeDefOrRef());
                code.Add(CilOpCodes.Call, getType);
                code.Add(CilOpCodes.Ldtoken, cached.ToTypeDefOrRef());
                code.Add(CilOpCodes.Call, getType);
                code.Add(CilOpCodes.Call, ((IMethodDefOrRef)importer.ImportMethod(binder)).MakeGenericInstanceMethod(owner.NewType.ToTypeSignature()));
                code.Add(CilOpCodes.Ret);
            }
        }

        private int MarkUnresolvedWwwDependencies(HashSet<MethodDefinition> unresolved,
            Dictionary<MethodDefinition, List<MethodDefinition>> callers,
            Dictionary<MethodDefinition, InteropRestorationEntry> entries, HashSet<MethodDefinition> translated,
            Queue<MethodDefinition> unavailable)
        {
            if (wwwBindings.Count == 0 || unresolved.Count == 0) return 0;
            var partial = 0;
            var visited = new HashSet<MethodDefinition>(ReferenceEqualityComparer.Instance);
            var pending = new Queue<MethodDefinition>(unresolved);
            while (pending.TryDequeue(out var method))
            {
                if (!visited.Add(method)) continue;
                if (wwwDependencies.ContainsKey(method) && translated.Remove(method))
                {
                    entries[method].Set("managed-body-partial", "native-www-dependency-unresolved",
                        "A WWW body dependency contains a call whose target could not be resolved.");
                    unavailable.Enqueue(method);
                    partial++;
                }
                if (callers.TryGetValue(method, out var dependents))
                    foreach (var dependent in dependents) pending.Enqueue(dependent);
            }
            return partial;
        }

        private static MethodDefinition CreateWwwBridge(WwwBinding binding, int index,
            InteropUnstripTypeResolver resolver, InteropRuntimeReferences imports)
        {
            var owner = binding.Owner;
            var source = binding.Source.Methods[index];
            var target = binding.Methods[index].Target;
            var importer = imports.Module.DefaultImporter;
            var result = resolver.Resolve(source.Signature.ReturnType);
            var arguments = source.Signature.ParameterTypes.Select(resolver.Resolve).ToArray();
            if (result == null || arguments.Any(argument => argument == null))
                throw new InvalidOperationException("A WWW callback signature could not be resolved: " + source.FullName);
            var expected = source.IsStatic ? MethodSignature.CreateStatic(result, arguments) : MethodSignature.CreateInstance(result, arguments);
            if (target.DeclaringType != owner.NewType || target.GenericParameters.Count != 0 ||
                !SignatureComparer.Default.Equals(target.Signature, expected) ||
                !source.IsConstructor && (target.Name != source.Name || target.Attributes != source.Attributes) ||
                source.IsConstructor && (owner.TryGetRestoredReferenceInitializer(source) != target || !target.IsPrivate || target.IsStatic))
                throw new InvalidOperationException("A restored WWW body differs from its native signature: " + target.FullName);
            var bridgeName = "__WwwDispatch" + index;
            if (owner.NewType.Methods.Any(method => method.Name == bridgeName))
                throw new InvalidOperationException("The WWW dispatcher already exists: " + bridgeName);
            var originalArguments = source.IsStatic ? source.Signature.ParameterTypes.ToArray() :
                new[] { binding.Source.ToTypeSignature() }.Concat(source.Signature.ParameterTypes).ToArray();
            var convertedArguments = source.IsStatic ? arguments : new[] { owner.NewType.ToTypeSignature() }.Concat(arguments).ToArray();
            var callbackResult = WwwCallbackResult(source.Signature.ReturnType, imports);
            var bridge = new MethodDefinition(bridgeName, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
                MethodSignature.CreateStatic(callbackResult, originalArguments.Select(argument => WwwCallbackArgument(argument, imports)).ToArray()))
            {
                CilMethodBody = new CilMethodBody { InitializeLocals = true }
            };
            var body = bridge.CilMethodBody;
            var code = body.Instructions;
            var locals = convertedArguments.Select(argument => new CilLocalVariable(argument)).ToArray();
            foreach (var local in locals) body.LocalVariables.Add(local);
            var resultLocal = callbackResult.ElementType == ElementType.Void ? null : new CilLocalVariable(callbackResult);
            if (resultLocal != null) body.LocalVariables.Add(resultLocal);
            var start = code.Add(CilOpCodes.Nop).CreateLabel();
            for (var argument = 0; argument < originalArguments.Length; argument++)
            {
                code.Add(CilOpCodes.Ldarg, bridge.Parameters[argument]);
                switch (originalArguments[argument].FullName)
                {
                    case "System.Boolean":
                        code.Add(CilOpCodes.Ldc_I4_0);
                        code.Add(CilOpCodes.Cgt_Un);
                        break;
                    case "System.Int32":
                    case "System.UInt32":
                    case "UnityEngine.AudioType":
                    case "UnityEngine.ThreadPriority":
                        break;
                    case "System.String":
                        code.Add(CilOpCodes.Call, imports.IL2CPP_Il2CppStringToManaged.Value);
                        break;
                    case "UnityEngine.Hash128":
                        code.Add(CilOpCodes.Ldobj, convertedArguments[argument].ToTypeDefOrRef());
                        break;
                    case "UnityEngine.CachedAssetBundle":
                        code.Add(CilOpCodes.Call, ((IMethodDefOrRef)importer.ImportMethod(
                            InteropNativeWwwType.RequireRuntimeMethod("ReadCachedAssetBundle"))).MakeGenericInstanceMethod(convertedArguments[argument]));
                        break;
                    default:
                        if (convertedArguments[argument].IsValueType)
                            throw new InvalidOperationException("A WWW reference callback argument has no native wrapper: " + originalArguments[argument].FullName);
                        code.Add(CilOpCodes.Ldc_I4_0);
                        code.Add(CilOpCodes.Ldc_I4_1);
                        code.Add(CilOpCodes.Call, imports.IL2CPP_PointerToValueGeneric.Value.MakeGenericInstanceMethod(convertedArguments[argument]));
                        break;
                }
                code.Add(CilOpCodes.Stloc, locals[argument]);
            }
            foreach (var local in locals) code.Add(CilOpCodes.Ldloc, local);
            // Constructor callbacks invoke the registered same-object initializer, never its allocating wrapper.
            code.Add(CilOpCodes.Call, target);
            if (source.Signature.ReturnType.FullName == "UnityEngine.ThreadPriority") code.Add(CilOpCodes.Conv_I4);
            if (resultLocal != null) code.Add(CilOpCodes.Stloc, resultLocal);
            var exit = new CilInstructionLabel();
            code.Add(CilOpCodes.Leave, exit);
            var cleanup = code.Add(CilOpCodes.Nop).CreateLabel();
            var keepAlive = importer.ImportMethod(typeof(GC).GetMethod(nameof(GC.KeepAlive), new[] { typeof(object) }));
            foreach (var local in locals.Where(local => !local.VariableType.IsValueType))
            {
                code.Add(CilOpCodes.Ldloc, local);
                code.Add(CilOpCodes.Call, keepAlive);
            }
            code.Add(CilOpCodes.Endfinally);
            exit.Instruction = resultLocal == null ? code.Add(CilOpCodes.Ret) : code.Add(CilOpCodes.Ldloc, resultLocal);
            if (resultLocal != null) code.Add(CilOpCodes.Ret);
            body.ExceptionHandlers.Add(new CilExceptionHandler
            {
                HandlerType = CilExceptionHandlerType.Finally, TryStart = start, TryEnd = cleanup,
                HandlerStart = cleanup, HandlerEnd = exit
            });
            return bridge;
        }

        private static TypeSignature WwwCallbackArgument(TypeSignature original, InteropRuntimeReferences imports) => original.FullName switch
        {
            "System.Boolean" => imports.Module.CorLibTypeFactory.Byte,
            "System.Int32" or "UnityEngine.ThreadPriority" or "UnityEngine.AudioType" => imports.Module.CorLibTypeFactory.Int32,
            "System.UInt32" => imports.Module.CorLibTypeFactory.UInt32,
            _ => imports.Module.CorLibTypeFactory.IntPtr
        };

        private static TypeSignature WwwCallbackResult(TypeSignature original, InteropRuntimeReferences imports) => original.FullName switch
        {
            "System.Void" => imports.Module.CorLibTypeFactory.Void,
            "System.Boolean" => imports.Module.CorLibTypeFactory.Boolean,
            "System.Int32" or "UnityEngine.ThreadPriority" => imports.Module.CorLibTypeFactory.Int32,
            "System.Single" => imports.Module.CorLibTypeFactory.Single,
            "System.String" => imports.Module.CorLibTypeFactory.String,
            _ => imports.Module.DefaultImporter.ImportTypeSignature(typeof(Il2CppObjectBase))
        };

        private static TypeSignature WwwCallbackType(MethodSignature signature, InteropRuntimeReferences imports)
        {
            var returnsVoid = signature.ReturnType.ElementType == ElementType.Void;
            var arguments = returnsVoid ? signature.ParameterTypes.ToArray() : signature.ParameterTypes.Append(signature.ReturnType).ToArray();
            Type definition = (returnsVoid, arguments.Length) switch
            {
                (true, 1) => typeof(Action<>),
                (true, 2) => typeof(Action<,>),
                (true, 3) => typeof(Action<,,>),
                (true, 4) => typeof(Action<,,,>),
                (true, 5) => typeof(Action<,,,,>),
                (false, 2) => typeof(Func<,>),
                (false, 3) => typeof(Func<,,>),
                (false, 4) => typeof(Func<,,,>),
                (false, 5) => typeof(Func<,,,,>),
                (false, 6) => typeof(Func<,,,,,>),
                _ => throw new InvalidOperationException("A WWW callback has an unsupported arity.")
            };
            return new GenericInstanceTypeSignature(imports.Module.DefaultImporter.ImportType(definition), false, arguments);
        }
    }
}
