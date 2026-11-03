// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using MethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodRestoration
    {
        private sealed record ReplayBinding(InteropTypeContext Owner, InteropRestorationEntry ConstructorEntry,
            (MethodDefinition Target, InteropRestorationEntry Entry)[] Methods);

        private readonly List<ReplayBinding> replayBindings = new();
        private readonly Dictionary<MethodDefinition, MethodDefinition[]> replayDependencies =
            new(ReferenceEqualityComparer.Instance);

        private void PrepareEventReplayBindings(InteropRewriteContext context)
        {
            var source = context.UnityAssemblies.Assemblies.SingleOrDefault(assembly =>
                assembly.Name == "UnityEngine.UIElementsModule")?.ManifestModule;
            var assembly = context.TryGetAssemblyByName("UnityEngine.UIElementsModule");
            if (source == null || assembly == null) return;

            foreach (var original in source.GetAllTypes())
            {
                var owner = assembly.TryGetTypeByName(original.FullName);
                if (owner == null || !InteropNativeEventDebuggerTypes.IsReplayIterator(owner.OriginalType)) continue;

                var token = InteropNativeEventDebuggerTypes.ReplayConstructorToken(original);
                var constructor = pendingBodies.SingleOrDefault(item => item.Type == owner &&
                    item.Original.MetadataToken.ToInt32() == token);
                // Order is the runtime binding contract, not the native interface slot order.
                var offsets = new[] { 2, 3, 5, 4, 1 };
                var methods = offsets.Select(offset =>
                {
                    var method = pendingBodies.SingleOrDefault(item => item.Type == owner &&
                        item.Original.MetadataToken.ToInt32() == token + offset);
                    return (method.Target, method.Entry);
                }).ToArray();
                replayBindings.Add(new ReplayBinding(owner, constructor.Entry, methods));
                if (constructor.Target != null)
                    replayDependencies.Add(constructor.Target, methods.Where(method => method.Target != null)
                        .Select(method => method.Target).ToArray());
            }
        }

        private void MarkUnavailableReplayContracts()
        {
            foreach (var binding in replayBindings)
            {
                if (binding.ConstructorEntry?.Outcome != "managed-body-translated" ||
                    binding.Methods.All(method => method.Target != null)) continue;
                binding.ConstructorEntry.Set("managed-body-partial", "native-replay-interface-unavailable",
                    "A native replay iterator cannot be allocated without all five restored interface bodies.");
            }
        }

        private void BindEventReplayBodies()
        {
            if (replayBindings.Count == 0) return;
            var binder = GetEventReplayBinder(replayBindings[0].Owner.AssemblyContext.Imports);
            foreach (var binding in replayBindings)
            {
                var owner = binding.Owner;
                var imports = owner.AssemblyContext.Imports;
                var importer = imports.Module.DefaultImporter;
                var cctor = owner.NewType.GetOrCreateStaticConstructor();
                var code = cctor.CilMethodBody?.Instructions;
                if (code == null || code.Count == 0 || code[^1].OpCode.Code != CilCode.Ret)
                    throw new InvalidOperationException("The native replay wrapper has no completed class initializer: " +
                        owner.NewType.FullName);

                var complete = binding.ConstructorEntry?.Outcome == "managed-body-translated" &&
                    binding.Methods.All(method => method.Entry?.Outcome == "managed-body-translated");
                owner.NewType.IsBeforeFieldInit = false;
                code.RemoveAt(code.Count - 1);
                if (!complete)
                {
                    code.Add(CilOpCodes.Ldstr, "Native replay is unavailable because an iterator body or its dependency could not be restored: " +
                        owner.OriginalType.FullName);
                    code.Add(CilOpCodes.Newobj, importer.ImportMethod(typeof(NotSupportedException)
                        .GetConstructor(new[] { typeof(string) })));
                    code.Add(CilOpCodes.Throw);
                    continue;
                }

                var self = owner.NewType.ToTypeSignature();
                var nativeObject = importer.ImportTypeSignature(
                    ((GenericInstanceTypeSignature)binder.Signature.ParameterTypes[2]).TypeArguments[1]);
                var results = new[] { imports.Module.CorLibTypeFactory.Boolean, nativeObject, nativeObject,
                    imports.Module.CorLibTypeFactory.Void, imports.Module.CorLibTypeFactory.Void };
                code.Add(CilOpCodes.Ldsfld, owner.ClassPointerFieldRef);
                for (var index = 0; index < binding.Methods.Length; index++)
                {
                    var target = binding.Methods[index].Target;
                    if (target.IsStatic || target.GenericParameters.Count != 0 || target.Signature.ParameterTypes.Count != 0 ||
                        !SignatureComparer.Default.Equals(target.Signature.ReturnType, results[index]))
                        throw new InvalidOperationException("The restored replay body differs from its native dispatch signature: " +
                            target.FullName);

                    var name = "__ReplayDispatch" + index;
                    if (owner.NewType.Methods.Any(method => method.Name == name))
                        throw new InvalidOperationException("The replay dispatcher already exists: " + owner.NewType.FullName);
                    var bridge = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
                        MethodSignature.CreateStatic(results[index], self)) { CilMethodBody = new CilMethodBody() };
                    bridge.CilMethodBody.Instructions.Add(CilOpCodes.Ldarg_0);
                    // This calls the translated body, never the native interface which invoked us.
                    bridge.CilMethodBody.Instructions.Add(CilOpCodes.Call, target);
                    bridge.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
                    owner.NewType.Methods.Add(bridge);
                    var callback = index < 3
                        ? new GenericInstanceTypeSignature(importer.ImportType(typeof(Func<,>)), false, self, results[index])
                        : new GenericInstanceTypeSignature(importer.ImportType(typeof(Action<>)), false, self);
                    code.Add(CilOpCodes.Ldnull);
                    code.Add(CilOpCodes.Ldftn, bridge);
                    code.Add(CilOpCodes.Newobj, new MemberReference(callback.ToTypeDefOrRef(), ".ctor",
                        MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void,
                            imports.Module.CorLibTypeFactory.Object, imports.Module.CorLibTypeFactory.IntPtr)));
                }
                code.Add(CilOpCodes.Call, importer.ImportMethod(binder).MakeGenericInstanceMethod(self));
                code.Add(CilOpCodes.Ret);
            }
        }

        private static MethodDefinition GetEventReplayBinder(InteropRuntimeReferences imports)
        {
            var method = FindRuntimeStaticMethod(imports, "Il2CppInterop.Runtime.Injection.NativeEventDebuggerRegistration",
                "BindIterator") ?? throw new NotSupportedException("The runtime does not expose the native replay binding contract.");
            var corLib = method.DeclaringModule.CorLibTypeFactory;
            var parameters = method.Signature?.ParameterTypes;
            if (!HasPlainSignature(method, 1, 6) || !IsObjectBaseParameter(method.GenericParameters[0]) ||
                !SignatureComparer.Default.Equals(method.Signature.ReturnType, corLib.Void) ||
                !SignatureComparer.Default.Equals(parameters[0], corLib.IntPtr) ||
                IteratorCallback(parameters[1], "System.Func`2", 2) is not { } predicate ||
                !SignatureComparer.Default.Equals(predicate.TypeArguments[1], corLib.Boolean) ||
                IteratorCallback(parameters[2], "System.Func`2", 2) is not { } selector ||
                selector.TypeArguments[1].FullName != "Il2CppSystem.Object" ||
                !SignatureComparer.Default.Equals(parameters[3], parameters[2]) ||
                IteratorCallback(parameters[4], "System.Action`1", 1) == null ||
                !SignatureComparer.Default.Equals(parameters[5], parameters[4]))
                throw new NotSupportedException("The runtime native replay binding signature is incompatible.");
            return method;

            // A corlib delegate whose first argument is the bound iterator type.
            static GenericInstanceTypeSignature IteratorCallback(TypeSignature type, string name, int arity) =>
                type is GenericInstanceTypeSignature callback && IsCorLibType(callback.GenericType, name) &&
                callback.TypeArguments.Count == arity && IsMethodParameter(callback.TypeArguments[0], 0)
                    ? callback : null;
        }
    }
}
