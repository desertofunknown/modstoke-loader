// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodRestoration
    {
        private bool TryRestoreUQueryProjection(MethodDefinition source, InteropTypeContext owner,
            InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver, InteropRestorationEntry entry)
        {
            if (source.DeclaringModule.Assembly.Name != UQueryModule ||
                source.DeclaringType.FullName != "UnityEngine.UIElements.UQueryState`1" ||
                source.Name != "ForEach" || source.Signature.GenericParameterCount != 1 ||
                source.Signature.ParameterTypes.Count != 2) return false;

            entry.Set("signature-rejected", "uquery-projection-source-contract",
                "Projection requires the matched selector, result append, traversal and callback cleanup bodies.");
            if (!TryGetUQueryProjectionSource(source, out var traversal)) return true;

            entry.Set("signature-rejected", "uquery-projection-native-contract",
                "Projection requires native query, result-list and delegate identities and the action traversal declaration.");
            var context = owner.AssemblyContext.GlobalContext;
            var sourceAction = traversal.Signature.ParameterTypes[0];
            var sourceList = (GenericInstanceTypeSignature)source.Signature.ParameterTypes[0];
            var sourceSelector = (GenericInstanceTypeSignature)source.Signature.ParameterTypes[1];
            var element = GetUQueryNativeType(context, resolver,
                source.DeclaringType.GenericParameters[0].Constraints[0].Constraint.ToTypeSignature());
            if (GetUQueryNativeType(context, resolver, source.DeclaringType.ToTypeSignature()) != owner ||
                owner.ComputedTypeSpecifics != InteropTypeContext.TypeSpecifics.NonBlittableStruct ||
                owner.NewType.BaseType?.FullName != "Il2CppSystem.ValueType" ||
                owner.NewType.DeclaringModule != imports.Module || element == null ||
                owner.NewType.GenericParameters[0].Attributes != GenericParameterAttributes.NonVariant ||
                owner.NewType.GenericParameters[0].Constraints.Count != 1 ||
                owner.NewType.GenericParameters[0].Constraints[0].Constraint?.Resolve() != element.NewType ||
                GetUQueryNativeType(context, resolver, sourceList.GenericType.ToTypeSignature()) == null ||
                !HasNativeDelegate(sourceSelector) || !HasNativeDelegate((GenericInstanceTypeSignature)sourceAction) ||
                resolver.Resolve(sourceList) is not { } listType ||
                resolver.Resolve(sourceSelector) is not { } selectorType ||
                resolver.Resolve(sourceAction) is not GenericInstanceTypeSignature actionType)
                return true;

            var action = owner.TryGetMethodByUnityAssemblyMethod(traversal)?.NewMethod ?? owner.TryGetRestoredBody(traversal);
            if (action == null || action.DeclaringType != owner.NewType || action.IsStatic || action.IsAbstract ||
                action.GenericParameters.Count != 0 || !InteropDelegateContracts.HasStandardConvention(action.Signature) ||
                !SignatureComparer.Default.Equals(action.Signature,
                    MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void, actionType)))
                return true;

            entry.Set("signature-rejected", "uquery-projection-runtime-contract",
                "The loaded Runtime must expose the typed native query projection factory.");
            var factory = GetUQueryProjectionFactory(imports);
            if (factory == null) return true;
            var resultArgument = new GenericParameterSignature(imports.Module, GenericParameterType.Method, 0);
            var create = imports.Module.DefaultImporter.ImportMethod(factory)
                .MakeGenericInstanceMethod(actionType.TypeArguments[0], resultArgument);
            var factorySignature = create.Method.Signature.InstantiateGenericTypes(GenericContext.FromMethod(create));
            if (!SignatureComparer.Default.Equals(factorySignature.ReturnType, actionType) ||
                !SignatureComparer.Default.Equals(factorySignature.ParameterTypes[0], listType) ||
                !SignatureComparer.Default.Equals(factorySignature.ParameterTypes[1], selectorType)) return true;

            var target = new MethodDefinition(source.Name, MethodAttributes.Public | MethodAttributes.HideBySig,
                MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void, 1))
                { CilMethodBody = new CilMethodBody() };
            target.GenericParameters.Add(new GenericParameter(source.GenericParameters[0].Name, GenericParameterAttributes.NonVariant));
            InteropMethodDeclarations.AddParameter(target, listType, source.Parameters[0].Name, default);
            InteropMethodDeclarations.AddParameter(target, selectorType, source.Parameters[1].Name, default);
            if (!InteropMethodTranslator.ValidateGenericSignature(source, target, owner, imports, out var reason))
            {
                entry.Set("signature-rejected", "managed-generic-contract", reason);
                return true;
            }
            if (owner.NewType.Methods.Any(method => method.Name == target.Name &&
                SignatureComparer.Default.Equals(method.Signature, target.Signature))) return true;

            var callback = new CilLocalVariable(actionType);
            target.CilMethodBody.LocalVariables.Add(callback);
            target.CilMethodBody.InitializeLocals = true;
            var body = target.CilMethodBody.Instructions;
            body.Add(CilOpCodes.Ldarg_1);
            body.Add(CilOpCodes.Ldarg_2);
            body.Add(CilOpCodes.Call, create);
            body.Add(CilOpCodes.Stloc, callback);
            var start = body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Ldloc, callback);
            body.Add(CilOpCodes.Call, new MemberReference(owner.SelfSubstitutedRef, action.Name, action.Signature));
            var finish = new CilInstruction(CilOpCodes.Ret);
            body.Add(CilOpCodes.Leave, finish.CreateLabel());
            var cleanup = body.Add(CilOpCodes.Ldloc, callback);
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                typeof(GC).GetMethod(nameof(GC.KeepAlive), new[] { typeof(object) })));
            body.Add(CilOpCodes.Endfinally);
            body.Add(finish);
            target.CilMethodBody.ExceptionHandlers.Add(new CilExceptionHandler
            {
                HandlerType = CilExceptionHandlerType.Finally,
                TryStart = start.CreateLabel(), TryEnd = cleanup.CreateLabel(),
                HandlerStart = cleanup.CreateLabel(), HandlerEnd = finish.CreateLabel()
            });
            owner.NewType.Methods.Add(target);
            owner.AddRestoredManagedMethod(source, target);
            completedBodies.Add((target, entry));
            entry.Set("restored-uquery-projection", null,
                "Typed projection over the action traversal; result order, deferred null failures and re-entrant cleanup preserved.");
            return true;

            bool HasNativeDelegate(GenericInstanceTypeSignature signature)
            {
                var native = GetUQueryNativeType(context, resolver, signature.GenericType.ToTypeSignature());
                return native != null && InteropDelegateContracts.CanStoreNativeDelegate(native.OriginalType, native.NewType);
            }
        }

        // The caller binds the return and parameter types to the generated list, selector and action.
        private static MethodDefinition GetUQueryProjectionFactory(InteropRuntimeReferences imports)
        {
            var factory = FindRuntimeStaticMethod(imports, "Il2CppInterop.Runtime.Injection.NativeVisualQuery", "CreateProjection");
            if (factory == null || !HasPlainSignature(factory, 2, 2) || !IsObjectBaseParameter(factory.GenericParameters[0]) ||
                factory.GenericParameters[1].Attributes != GenericParameterAttributes.NonVariant ||
                factory.GenericParameters[1].Constraints.Count != 0)
                return null;
            return factory;
        }
    }
}
