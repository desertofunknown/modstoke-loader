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
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using BindingFlags = System.Reflection.BindingFlags;
using MethodInfo = System.Reflection.MethodInfo;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodRestoration
    {
        private const string TransitionModule = "UnityEngine.UIElementsModule";
        private const string TransitionInterface = "UnityEngine.UIElements.Experimental.ITransitionAnimations";
        private const string TransitionElement = "UnityEngine.UIElements.VisualElement";
        private readonly List<(MethodDefinition Source, InteropTypeContext Owner, InteropRuntimeReferences Imports,
            InteropUnstripTypeResolver Resolver, InteropRestorationEntry Entry)> pendingTransitionAnimations = new();

        private bool TryQueueTransitionAnimation(MethodDefinition source, InteropTypeContext owner,
            InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver, InteropRestorationEntry entry)
        {
            if (source.DeclaringModule.Assembly.Name != TransitionModule ||
                source.DeclaringType.FullName != TransitionInterface) return false;

            // The concrete VisualElement declarations may appear later in the source traversal.
            pendingTransitionAnimations.Add((source, owner, imports, resolver, entry));
            entry.Set("body-pending", "transition-animation-forwarder-pending");
            return true;
        }

        private void PrepareTransitionAnimations(ref int restored, ref int ignored)
        {
            foreach (var group in pendingTransitionAnimations.GroupBy(method => method.Owner))
            {
                var first = group.First();
                var sourceMethods = GetTransitionImplementations(first.Source.DeclaringType);
                foreach (var method in group)
                {
                    method.Entry.Set("signature-rejected", "transition-animation-source-contract",
                        "Forwarding requires the original non-generic interface and its unique final VisualElement MethodImpls.");
                    if (sourceMethods != null && sourceMethods.TryGetValue(method.Source, out var implementation) &&
                        TryRestoreTransitionAnimation(method.Source, implementation, method.Owner,
                            method.Imports, method.Resolver, method.Entry)) restored++;
                    else ignored++;
                }
            }
        }

        private static Dictionary<MethodDefinition, MethodDefinition> GetTransitionImplementations(TypeDefinition contract)
        {
            var module = contract.DeclaringModule;
            if (module.Assembly.Name != TransitionModule || module.Assembly.Modules.Count != 1 ||
                module.Mvid != new Guid("b6e504b1-20f0-43e3-9923-7045b32ad8bb") ||
                contract.FullName != TransitionInterface || !contract.IsPublic || !contract.IsInterface ||
                !contract.IsAbstract || contract.DeclaringType != null || contract.GenericParameters.Count != 0 ||
                contract.Interfaces.Count != 0 || contract.Fields.Count != 0 || contract.Properties.Count != 0 ||
                contract.Events.Count != 0 || contract.MethodImplementations.Count != 0 || contract.Methods.Count != 20)
                return null;

            var types = module.GetAllTypes().ToArray();
            var elements = types.Where(type => type.FullName == TransitionElement).ToArray();
            if (elements.Length != 1) return null;
            var element = elements[0];
            if (element.IsInterface || element.IsValueType || element.GenericParameters.Count != 0 ||
                element.Interfaces.Count(item => item.Interface?.Resolve() == contract) != 1) return null;

            var implementations = new Dictionary<MethodDefinition, MethodDefinition>(ReferenceEqualityComparer.Instance);
            // A final MethodImpl is only sufficient if no descendant redeclares the interface.
            foreach (var type in types)
            {
                if (type != element && type.Interfaces.Any(item => item.Interface?.Resolve() == contract)) return null;
                foreach (var slot in type.MethodImplementations)
                {
                    if (slot.Declaration?.DeclaringType?.Resolve() != contract) continue;
                    var declaration = slot.Declaration.Resolve();
                    var body = slot.Body?.Resolve();
                    if (type != element || declaration == null || declaration.DeclaringType != contract ||
                        body == null || body.DeclaringType != element || body.DeclaringModule != module ||
                        body.Attributes != (MethodAttributes.Private | MethodAttributes.Final | MethodAttributes.Virtual |
                            MethodAttributes.HideBySig | MethodAttributes.NewSlot) ||
                        body.ImplAttributes != MethodImplAttributes.IL || body.CilMethodBody == null ||
                        body.CilMethodBody.Instructions.Count == 0 || body.GenericParameters.Count != 0 ||
                        body.Name != TransitionInterface + "." + declaration.Name ||
                        !SignatureComparer.Default.Equals(slot.Declaration.Signature, declaration.Signature) ||
                        !SignatureComparer.Default.Equals(slot.Body.Signature, body.Signature) ||
                        !SignatureComparer.Default.Equals(declaration.Signature, body.Signature) ||
                        !implementations.TryAdd(declaration, body)) return null;
                }
            }
            if (implementations.Count != contract.Methods.Count) return null;
            foreach (var source in contract.Methods)
            {
                var token = source.MetadataToken.ToInt32();
                var bodyToken = token switch
                {
                    >= 0x06002523 and <= 0x06002528 => token - 0x06002523 + 0x060017CF,
                    0x06002529 => 0x060017D5,
                    0x0600252A => 0x060017DF,
                    >= 0x0600252B and <= 0x06002530 => token - 0x0600252B + 0x060017D6,
                    >= 0x06002531 and <= 0x06002536 => token - 0x06002531 + 0x060017E1,
                    _ => 0
                };
                if (bodyToken == 0 || !implementations.TryGetValue(source, out var body) ||
                    body.MetadataToken.ToInt32() != bodyToken || source.GenericParameters.Count != 0 ||
                    source.Attributes != (MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual |
                        MethodAttributes.HideBySig | MethodAttributes.NewSlot) ||
                    source.ImplAttributes != MethodImplAttributes.IL || source.HasMethodBody ||
                    !InteropDelegateContracts.HasStandardConvention(source.Signature) || !source.Signature.HasThis ||
                    source.Signature.GenericParameterCount != 0 ||
                    source.Signature.ReturnType is CorLibTypeSignature { ElementType: ElementType.Void } ||
                    source.Parameters.Count != source.Signature.ParameterTypes.Count ||
                    source.Parameters.Any(parameter => parameter.Definition is not { } definition ||
                        definition.Attributes != 0 || definition.MarshalDescriptor != null || definition.Constant != null))
                    return null;
            }
            return implementations;
        }

        private bool TryRestoreTransitionAnimation(MethodDefinition source, MethodDefinition implementation,
            InteropTypeContext owner, InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver,
            InteropRestorationEntry entry)
        {
            entry.Set("signature-rejected", "transition-animation-native-contract",
                "Forwarding requires the original native interface proxy and the exact concrete VisualElement declaration.");
            var element = owner.AssemblyContext.TryGetTypeByName(TransitionElement);
            if (!HasTransitionNativeType(owner, source.DeclaringType, imports, resolver, true) ||
                !HasTransitionNativeType(element, implementation.DeclaringType, imports, resolver, false) ||
                element.OriginalType.Interfaces.Count(item => item.Interface?.Resolve() == owner.OriginalType) != 1 ||
                source.MetadataToken.ToInt32() == 0x0600252A ||
                owner.TryGetMethodByUnityAssemblyMethod(source) != null) return false;

            var native = element.TryGetMethodByUnityAssemblyMethod(implementation);
            var concrete = native?.NewMethod ?? element.TryGetRestoredBody(implementation);
            var finalImplementation = native?.OriginalMethod ?? concrete;
            var returnType = resolver.Resolve(source.Signature.ReturnType);
            var parameters = source.Signature.ParameterTypes.Select(resolver.Resolve).ToArray();
            if (returnType == null || parameters.Any(parameter => parameter == null) || concrete == null ||
                concrete.DeclaringType != element.NewType || concrete.IsStatic || concrete.IsAbstract ||
                !concrete.IsPublic || !finalImplementation.IsVirtual || !finalImplementation.IsFinal ||
                finalImplementation.IsStatic || finalImplementation.IsAbstract || concrete.GenericParameters.Count != 0 ||
                !InteropDelegateContracts.HasStandardConvention(concrete.Signature) ||
                !SignatureComparer.Default.Equals(concrete.Signature, MethodSignature.CreateInstance(returnType, parameters)))
                return false;

            entry.Set("signature-rejected", "transition-animation-runtime-contract",
                "The loaded Runtime must expose the typed transition receiver lease and its Value and Dispose methods.");
            if (!TryGetTransitionRuntime(out var acquire, out var getter, out var dispose)) return false;
            var importer = imports.Module.DefaultImporter;
            var acquireReference = ((IMethodDefOrRef)importer.ImportMethod(acquire))
                .MakeGenericInstanceMethod(element.NewType.ToTypeSignature());
            var acquireSignature = acquireReference.Method.Signature.InstantiateGenericTypes(GenericContext.FromMethod(acquireReference));
            if (acquireSignature.ReturnType is not GenericInstanceTypeSignature receiverType) return false;
            IMethodDefOrRef getterReference = new MemberReference(receiverType.ToTypeDefOrRef(), getter.Name,
                importer.ImportMethod(getter).Signature);
            IMethodDefOrRef disposeReference = new MemberReference(receiverType.ToTypeDefOrRef(), dispose.Name,
                importer.ImportMethod(dispose).Signature);
            if (!SignatureComparer.Default.Equals(getterReference.Signature.InstantiateGenericTypes(
                GenericContext.FromMethod(getterReference)).ReturnType, element.NewType.ToTypeSignature())) return false;

            var target = new MethodDefinition(source.Name,
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual |
                MethodAttributes.Final | MethodAttributes.NewSlot, MethodSignature.CreateInstance(returnType))
                { CilMethodBody = new CilMethodBody() };
            for (var index = 0; index < parameters.Length; index++)
                InteropMethodDeclarations.AddParameter(target, parameters[index], source.Parameters[index].Name, default);
            entry.Set("signature-rejected", "transition-animation-target-conflict",
                "A matching proxy declaration or restored mapping already exists.");
            if (owner.TryGetRestoredBody(source) != null || owner.TryGetRestoredManagedMethod(source) != null ||
                owner.NewType.Methods.Any(method => method.Name == target.Name &&
                    SignatureComparer.Default.Equals(method.Signature, target.Signature))) return false;

            var lease = new CilLocalVariable(receiverType);
            var result = new CilLocalVariable(returnType);
            target.CilMethodBody.LocalVariables.Add(lease);
            target.CilMethodBody.LocalVariables.Add(result);
            target.CilMethodBody.InitializeLocals = true;
            var code = target.CilMethodBody.Instructions;
            code.Add(CilOpCodes.Ldarg_0);
            code.Add(CilOpCodes.Call, acquireReference);
            code.Add(CilOpCodes.Stloc, lease);
            var start = code.Add(CilOpCodes.Ldloc, lease);
            code.Add(CilOpCodes.Callvirt, getterReference);
            foreach (var parameter in target.Parameters) code.Add(CilOpCodes.Ldarg, parameter);
            // Bypass interface dispatch: only this final source MethodImpl was validated.
            code.Add(CilOpCodes.Call, concrete);
            code.Add(CilOpCodes.Stloc, result);
            var finish = new CilInstruction(CilOpCodes.Ldloc, result);
            code.Add(CilOpCodes.Leave, finish.CreateLabel());
            var cleanup = code.Add(CilOpCodes.Ldloc, lease);
            code.Add(CilOpCodes.Callvirt, disposeReference);
            code.Add(CilOpCodes.Endfinally);
            code.Add(finish);
            code.Add(CilOpCodes.Ret);
            target.CilMethodBody.ExceptionHandlers.Add(new CilExceptionHandler
            {
                HandlerType = CilExceptionHandlerType.Finally,
                TryStart = start.CreateLabel(), TryEnd = cleanup.CreateLabel(),
                HandlerStart = cleanup.CreateLabel(), HandlerEnd = finish.CreateLabel()
            });
            owner.NewType.Methods.Add(target);
            owner.AddRestoredBody(source, target);
            owner.AddRestoredManagedMethod(source, target);
            completedBodies.Add((target, entry));
            entry.Set("restored-transition-animation-forwarder", null,
                "CLR forwarding to the final VisualElement implementation with a guarded native receiver lease; no native slot added.");
            return true;
        }

        private static bool HasTransitionNativeType(InteropTypeContext type, TypeDefinition source,
            InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver, bool isInterface) =>
            type?.OriginalType != null && type.ClassPointerFieldRef != null &&
            type.OriginalType.FullName == source.FullName && type.OriginalType.DeclaringModule.Assembly.Name == TransitionModule &&
            type.OriginalType.IsInterface == isInterface && !type.OriginalType.IsValueType &&
            type.OriginalType.GenericParameters.Count == 0 && type.NewType.GenericParameters.Count == 0 &&
            type.NewType.FullName == source.FullName && type.NewType.DeclaringModule == imports.Module &&
            !type.NewType.IsInterface && !type.NewType.IsValueType &&
            resolver.ResolveRaw(source.ToTypeSignature())?.Resolve() == type.NewType;

        private static bool TryGetTransitionRuntime(out MethodInfo acquire, out MethodInfo getter, out MethodInfo dispose)
        {
            acquire = getter = dispose = null;
            var type = typeof(IL2CPP).Assembly.GetType("Il2CppInterop.Runtime.Injection.NativeTransitionAnimations", false);
            if (type == null || !type.IsPublic || !type.IsAbstract || !type.IsSealed || type.IsGenericType) return false;
            var factories = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == "Acquire").ToArray();
            if (factories.Length != 1) return false;
            var factory = factories[0];
            var arguments = factory.GetGenericArguments();
            var parameters = factory.GetParameters();
            if (!factory.IsGenericMethodDefinition || arguments.Length != 1 || parameters.Length != 1 ||
                factory.CallingConvention != System.Reflection.CallingConventions.Standard ||
                arguments[0].GenericParameterAttributes != System.Reflection.GenericParameterAttributes.None ||
                !arguments[0].GetGenericParameterConstraints().SequenceEqual(new[] { typeof(Il2CppObjectBase) }) ||
                parameters[0].ParameterType != typeof(Il2CppObjectBase) || !factory.ReturnType.IsGenericType ||
                !factory.ReturnType.GetGenericArguments().SequenceEqual(arguments)) return false;
            var receiver = factory.ReturnType.GetGenericTypeDefinition();
            var receiverArguments = receiver.GetGenericArguments();
            if (receiver.DeclaringType != type || receiver.Name != "Receiver`1" || !receiver.IsNestedPublic ||
                !receiver.IsClass || !receiver.IsSealed || receiver.IsAbstract || receiverArguments.Length != 1 ||
                receiverArguments[0].GenericParameterAttributes != System.Reflection.GenericParameterAttributes.None ||
                !receiverArguments[0].GetGenericParameterConstraints().SequenceEqual(new[] { typeof(Il2CppObjectBase) }) ||
                !receiver.GetInterfaces().SequenceEqual(new[] { typeof(IDisposable) })) return false;
            var values = receiver.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == "get_Value").ToArray();
            var disposers = receiver.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == "Dispose").ToArray();
            if (values.Length != 1 || disposers.Length != 1 || values[0].ReturnType != receiverArguments[0] ||
                disposers[0].ReturnType != typeof(void) || new[] { values[0], disposers[0] }.Any(method =>
                    method.IsGenericMethod || method.GetParameters().Length != 0 ||
                    method.CallingConvention != (System.Reflection.CallingConventions.Standard | System.Reflection.CallingConventions.HasThis)) ||
                new[] { factory, values[0], disposers[0] }.Any(method => method.GetParameters().Append(method.ReturnParameter)
                    .Any(parameter => parameter.Attributes != System.Reflection.ParameterAttributes.None ||
                        parameter.GetRequiredCustomModifiers().Length != 0 || parameter.GetOptionalCustomModifiers().Length != 0)))
                return false;
            acquire = factory;
            getter = values[0];
            dispose = disposers[0];
            return true;
        }
    }
}
