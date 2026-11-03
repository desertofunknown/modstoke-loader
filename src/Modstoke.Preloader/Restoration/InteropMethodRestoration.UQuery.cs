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
        private const string UQueryModule = "UnityEngine.UIElementsModule";
        private const string UQueryBuilderType = "UnityEngine.UIElements.UQueryBuilder`1";

        // Consume rejected candidates too: a CLR predicate helper cannot satisfy the native selector interface.
        private static bool TryRestoreUQueryWhere(MethodDefinition source, InteropTypeContext owner,
            InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver, InteropRestorationEntry entry)
        {
            if (source.DeclaringModule.Assembly.Name != UQueryModule ||
                source.DeclaringType.FullName != UQueryBuilderType || source.Name != "Where")
                return false;

            entry.Set("signature-rejected", "uquery-predicate-source-contract",
                "Where requires the exact source predicate construction, parts append, and builder value return.");
            if (!TryGetUQueryWhereSource(source, out var getParts, out var createPredicate, out var add))
                return true;

            entry.Set("signature-rejected", "uquery-predicate-native-contract",
                "Where requires existing native builder, selector, list, and delegate identities with exact constructed member signatures.");
            var context = owner.AssemblyContext.GlobalContext;
            var sourceBuilder = (GenericInstanceTypeSignature)source.Signature.ReturnType;
            var sourceSelector = (GenericInstanceTypeSignature)source.Signature.ParameterTypes[0];
            var sourceParts = (GenericInstanceTypeSignature)getParts.Signature.ReturnType;
            var sourcePart = sourceParts.TypeArguments[0];
            var sourceElement = source.DeclaringType.GenericParameters[0].Constraints[0].Constraint.ToTypeSignature();
            var element = GetUQueryNativeType(context, resolver, sourceElement);
            var part = GetUQueryNativeType(context, resolver, sourcePart);
            var list = GetUQueryNativeType(context, resolver, sourceParts.GenericType.ToTypeSignature());
            var selector = GetUQueryNativeType(context, resolver, sourceSelector.GenericType.ToTypeSignature());
            var nativeObject = GetUQueryNativeType(context, resolver, source.DeclaringModule.CorLibTypeFactory.Object);
            if (GetUQueryNativeType(context, resolver, source.DeclaringType.ToTypeSignature()) != owner ||
                owner.NewType.DeclaringModule != imports.Module || imports.Module.Assembly.Name != UQueryModule ||
                owner.ComputedTypeSpecifics != InteropTypeContext.TypeSpecifics.NonBlittableStruct ||
                owner.NewType.BaseType?.FullName != "Il2CppSystem.ValueType" ||
                element == null || part == null || list == null || selector == null || nativeObject == null ||
                part.ComputedTypeSpecifics != InteropTypeContext.TypeSpecifics.NonBlittableStruct ||
                part.NewType.BaseType?.FullName != "Il2CppSystem.ValueType" ||
                owner.NewType.GenericParameters[0].Attributes != GenericParameterAttributes.NonVariant ||
                owner.NewType.GenericParameters[0].Constraints.Count != 1 ||
                owner.NewType.GenericParameters[0].Constraints[0].Constraint?.Resolve() != element.NewType ||
                !InteropDelegateContracts.CanStoreNativeDelegate(selector.OriginalType, selector.NewType) ||
                resolver.Resolve(sourceBuilder) is not GenericInstanceTypeSignature builderType ||
                resolver.Resolve(sourceSelector) is not GenericInstanceTypeSignature selectorType ||
                !SignatureComparer.Default.Equals(builderType, owner.SelfSubstitutedRef?.ToTypeSignature()) ||
                !SignatureComparer.Default.Equals(builderType.TypeArguments[0], selectorType.TypeArguments[0]) ||
                !TryBindUQueryNativeMethod(getParts, owner, resolver, imports, out var targetGetParts) ||
                !TryBindUQueryNativeMethod(createPredicate, part, resolver, imports, out var targetCreatePredicate) ||
                !TryBindUQueryNativeMethod(add, list, resolver, imports, out var targetAdd))
                return true;

            entry.Set("signature-rejected", "uquery-predicate-runtime-contract",
                "The loaded Runtime must expose NativeVisualPredicate.Create<T>(Il2CppSystem.Func<T, bool>) returning Il2CppSystem.Object.");
            var factory = GetUQueryPredicateFactory(imports);
            if (factory == null) return true;
            var imported = imports.Module.DefaultImporter.ImportMethod(factory);
            IMethodDescriptor create = imported.MakeGenericInstanceMethod(builderType.TypeArguments[0]);
            var factorySignature = create.Signature;
            var substitution = GenericContext.FromMethod(create);
            if (!SignatureComparer.Default.Equals(factorySignature.ParameterTypes[0].InstantiateGenericTypes(substitution), selectorType) ||
                !SignatureComparer.Default.Equals(factorySignature.ReturnType.InstantiateGenericTypes(substitution),
                    resolver.Resolve(source.DeclaringModule.CorLibTypeFactory.Object)))
                return true;

            var target = new MethodDefinition(source.Name, MethodAttributes.Public | MethodAttributes.HideBySig,
                MethodSignature.CreateInstance(builderType))
            {
                ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed,
                CilMethodBody = new CilMethodBody()
            };
            InteropMethodDeclarations.AddParameter(target, selectorType, source.Parameters[0].Name, default);
            if (!InteropMethodTranslator.ValidateGenericSignature(source, target, owner, imports, out var reason))
            {
                entry.Set("signature-rejected", "managed-generic-contract", reason);
                return true;
            }
            if (owner.NewType.Methods.Any(method => method.Name == target.Name &&
                SignatureComparer.Default.Equals(method.Signature, target.Signature)))
                return true;

            var copy = ((IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropNativeValueStorage).GetMethod(nameof(InteropNativeValueStorage.Copy))))
                .MakeGenericInstanceMethod(builderType);
            var body = target.CilMethodBody.Instructions;
            // Preserve lazy parts initialization before creating the wrapper, including a null selector.
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Call, targetGetParts);
            body.Add(CilOpCodes.Ldarg_1);
            body.Add(CilOpCodes.Call, create);
            body.Add(CilOpCodes.Call, targetCreatePredicate);
            body.Add(CilOpCodes.Callvirt, targetAdd);
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Call, copy);
            body.Add(CilOpCodes.Ret);
            owner.NewType.Methods.Add(target);
            owner.AddRestoredManagedMethod(source, target);
            entry.Set("restored-native-uquery-predicate", null,
                "Native predicate interface adapter; lazy parts append and nullable selector preserved; builder returned by native value copy.");
            return true;
        }

        private static bool TryGetUQueryWhereSource(MethodDefinition source, out IMethodDescriptor getParts,
            out IMethodDescriptor createPredicate, out IMethodDescriptor add)
        {
            getParts = createPredicate = add = null;
            var type = source.DeclaringType;
            if (source.DeclaringModule.Mvid != new Guid("b6e504b1-20f0-43e3-9923-7045b32ad8bb") ||
                source.MetadataToken.ToInt32() != 0x06001E1D ||
                source.Attributes != (MethodAttributes.Public | MethodAttributes.HideBySig) ||
                source.ImplAttributes != MethodImplAttributes.IL || !source.HasMethodBody ||
                !InteropDelegateContracts.HasStandardConvention(source.Signature) || !source.Signature.HasThis ||
                source.Signature.GenericParameterCount != 0 || source.GenericParameters.Count != 0 ||
                source.Signature.ParameterTypes.Count != 1 || source.Parameters.Count != 1 ||
                source.Parameters[0].Definition?.Attributes != 0 ||
                source.Parameters[0].Definition.MarshalDescriptor != null ||
                source.Parameters[0].Definition.Constant != null ||
                !type.IsValueType || type.IsEnum || !type.IsPublic || !type.IsSealed || !type.IsSequentialLayout ||
                type.DeclaringType != null || !HasUQueryElementConstraint(type) ||
                source.Signature.ReturnType is not GenericInstanceTypeSignature builder || !builder.IsValueType ||
                builder.GenericType.Resolve() != type || builder.TypeArguments.Count != 1 ||
                builder.TypeArguments[0] is not GenericParameterSignature { ParameterType: GenericParameterType.Type, Index: 0 } ||
                source.Signature.ParameterTypes[0] is not GenericInstanceTypeSignature selector || selector.IsValueType ||
                selector.GenericType.FullName != "System.Func`2" ||
                InteropRestoredTypeSupport.GetAssemblyName(selector) is not ("netstandard" or "mscorlib") || selector.TypeArguments.Count != 2 ||
                !SignatureComparer.Default.Equals(selector.TypeArguments[0], builder.TypeArguments[0]) ||
                selector.TypeArguments[1] is not CorLibTypeSignature { ElementType: ElementType.Boolean })
                return false;

            var body = source.CilMethodBody;
            var code = body.Instructions;
            CilCode[] expected = [CilCode.Nop, CilCode.Ldarg_0, CilCode.Call, CilCode.Ldarg_1,
                CilCode.Newobj, CilCode.Call, CilCode.Callvirt, CilCode.Nop, CilCode.Ldarg_0,
                CilCode.Ldobj, CilCode.Stloc_0, CilCode.Br_S, CilCode.Ldloc_0, CilCode.Ret];
            if (body.ExceptionHandlers.Count != 0 || !body.InitializeLocals || body.LocalVariables.Count != 1 ||
                !SignatureComparer.Default.Equals(body.LocalVariables[0].VariableType, builder) ||
                code.Count != expected.Length || code.Where((instruction, index) => instruction.OpCode.Code != expected[index]).Any() ||
                code[11].Operand is not CilInstructionLabel branch || branch.Instruction != code[12] ||
                code[9].Operand is not ITypeDefOrRef loadType ||
                !SignatureComparer.Default.Equals(loadType.ToTypeSignature(), builder) ||
                code[2].Operand is not IMethodDescriptor getter ||
                code[4].Operand is not IMethodDescriptor constructor ||
                code[5].Operand is not IMethodDescriptor factory ||
                code[6].Operand is not IMethodDescriptor append ||
                getter.Signature?.ReturnType is not GenericInstanceTypeSignature parts || parts.IsValueType ||
                parts.GenericType.FullName != "System.Collections.Generic.List`1" ||
                InteropRestoredTypeSupport.GetAssemblyName(parts) is not ("netstandard" or "mscorlib") || parts.TypeArguments.Count != 1 ||
                parts.TypeArguments[0] is not TypeDefOrRefSignature { IsValueType: true } part ||
                part.FullName != "UnityEngine.UIElements.StyleSelectorPart" ||
                part.Resolve()?.DeclaringModule != source.DeclaringModule ||
                constructor.DeclaringType?.ToTypeSignature() is not GenericInstanceTypeSignature wrapper || wrapper.IsValueType ||
                wrapper.GenericType.Name != "PredicateWrapper`1" ||
                wrapper.GenericType.DeclaringType?.FullName != "UnityEngine.UIElements.UQuery" ||
                wrapper.GenericType.Resolve() is not { } wrapperType || wrapperType.DeclaringModule != source.DeclaringModule ||
                !HasUQueryElementConstraint(wrapperType) || wrapper.TypeArguments.Count != 1 ||
                !SignatureComparer.Default.Equals(wrapper.TypeArguments[0], builder.TypeArguments[0]) ||
                wrapperType.Interfaces.Count != 1 ||
                wrapperType.Interfaces[0].Interface?.Name != "IVisualPredicateWrapper" ||
                wrapperType.Interfaces[0].Interface.DeclaringType?.Resolve() != wrapperType.DeclaringType ||
                wrapperType.Interfaces[0].Interface.Resolve()?.DeclaringModule != source.DeclaringModule ||
                !HasUQuerySourceMember(getter, builder, "get_parts", true, parts) ||
                !HasUQuerySourceMember(constructor, wrapper, ".ctor", true, source.DeclaringModule.CorLibTypeFactory.Void, selector) ||
                !HasUQuerySourceMember(factory, part, "CreatePredicate", false, part, source.DeclaringModule.CorLibTypeFactory.Object) ||
                !HasUQuerySourceMember(append, parts, "Add", true, source.DeclaringModule.CorLibTypeFactory.Void, part))
                return false;
            getParts = getter;
            createPredicate = factory;
            add = append;
            return true;
        }

        private static bool HasUQueryElementConstraint(TypeDefinition type) =>
            type.GenericParameters.Count == 1 &&
            type.GenericParameters[0].Attributes == GenericParameterAttributes.NonVariant &&
            type.GenericParameters[0].Constraints.Count == 1 &&
            type.GenericParameters[0].Constraints[0].Constraint is { } constraint &&
            constraint.FullName == "UnityEngine.UIElements.VisualElement" &&
            constraint.Resolve() is { IsValueType: false, IsInterface: false } element &&
            element.DeclaringModule == type.DeclaringModule;

        private static bool HasUQuerySourceMember(IMethodDescriptor method, TypeSignature owner, string name,
            bool hasThis, TypeSignature result, params TypeSignature[] parameters)
        {
            if (method is MethodSpecification || method.Name != name ||
                !SignatureComparer.Default.Equals(method.DeclaringType?.ToTypeSignature(), owner) ||
                !InteropDelegateContracts.HasStandardConvention(method.Signature) ||
                method.Signature.HasThis != hasThis || method.Signature.GenericParameterCount != 0)
                return false;
            var expected = hasThis ? MethodSignature.CreateInstance(result, parameters) : MethodSignature.CreateStatic(result, parameters);
            if (!SignatureComparer.Default.Equals(method.Signature.InstantiateGenericTypes(GenericContext.FromMethod(method)), expected))
                return false;
            // Unity's reference package omits core-library definitions. These framework
            // members are bound against the game's own declarations before emission.
            if (owner is GenericInstanceTypeSignature core && IsUQueryCoreReference(core.GenericType.ToTypeSignature()))
                return hasThis && (name == "Add" && core.GenericType.FullName == "System.Collections.Generic.List`1" ||
                    name == "Invoke" && core.GenericType.FullName is "System.Action`1" or "System.Func`2");
            if (method.Resolve() is not { } definition || definition.DeclaringType != owner.Resolve() ||
                definition.IsStatic == hasThis || definition.IsAbstract || definition.IsPInvokeImpl ||
                definition.GenericParameters.Count != 0 ||
                !SignatureComparer.Default.Equals(method.Signature, definition.Signature))
                return false;
            return true;
        }

        private static bool IsUQueryCoreReference(TypeSignature type) =>
            InteropRestoredTypeSupport.GetAssemblyName(type) is "netstandard" or "mscorlib" &&
            type.FullName is "System.Object" or "System.Action`1" or "System.Func`2" or "System.Collections.Generic.List`1";

        private static InteropTypeContext GetUQueryNativeType(InteropRewriteContext context,
            InteropUnstripTypeResolver resolver, TypeSignature source)
        {
            var type = context.TryGetTypeByName(InteropRestoredTypeSupport.GetAssemblyName(source), source.FullName);
            var definition = IsUQueryCoreReference(source) ? type?.OriginalType : source.Resolve();
            if (definition == null || type?.OriginalType == null || type.ClassPointerFieldRef == null ||
                definition.FullName != source.FullName || definition.IsValueType != source.IsValueType ||
                type.OriginalType.FullName != definition.FullName ||
                context.TryGetTypeByName(definition.DeclaringModule.Assembly.Name, definition.FullName) != type ||
                type.OriginalType.IsValueType != definition.IsValueType || type.OriginalType.IsEnum ||
                type.OriginalType.IsInterface || type.NewType.IsInterface || type.NewType.IsValueType ||
                type.OriginalType.GenericParameters.Count != definition.GenericParameters.Count ||
                type.NewType.GenericParameters.Count != definition.GenericParameters.Count ||
                type.NewType.FullName != (definition.Namespace == "System" || definition.Namespace == "System.Collections.Generic"
                    ? "Il2Cpp" + definition.FullName : definition.FullName) ||
                resolver.ResolveRaw(source)?.Resolve() != type.NewType ||
                InteropRestorationPlan.For(context).GetNativeIdentityRejection(source) != null)
                return null;
            return type;
        }

        private static bool TryBindUQueryNativeMethod(IMethodDescriptor source, InteropTypeContext owner,
            InteropUnstripTypeResolver resolver, InteropRuntimeReferences imports, out IMethodDescriptor target)
        {
            target = null;
            var core = source.DeclaringType?.ToTypeSignature() as GenericInstanceTypeSignature;
            var definition = core != null && IsUQueryCoreReference(core.GenericType.ToTypeSignature())
                ? owner.OriginalType?.Methods.SingleOrDefault(method => method.Name == source.Name &&
                    SignatureComparer.Default.Equals(method.Signature, source.Signature))
                : source.Resolve();
            var mapped = definition == null ? null : owner.TryGetMethodByUnityAssemblyMethod(definition);
            if (mapped?.NewMethod is not { } method || method.DeclaringType != owner.NewType ||
                method.IsAbstract || method.IsPInvokeImpl || method.GenericParameters.Count != 0 ||
                method.IsStatic != definition.IsStatic ||
                !InteropDelegateContracts.HasStandardConvention(method.Signature) ||
                method.Signature.HasThis != source.Signature.HasThis || method.Signature.GenericParameterCount != 0 ||
                resolver.Resolve(source.DeclaringType.ToTypeSignature()) is not { } constructedOwner ||
                constructedOwner.Resolve() != owner.NewType)
                return false;
            IMethodDescriptor reference = new MemberReference(constructedOwner.ToTypeDefOrRef(), method.Name, method.Signature);
            var sourceSignature = source.Signature.InstantiateGenericTypes(GenericContext.FromMethod(source));
            var signature = reference.Signature.InstantiateGenericTypes(GenericContext.FromMethod(reference));
            if (signature.ParameterTypes.Count != sourceSignature.ParameterTypes.Count ||
                !SignatureComparer.Default.Equals(signature.ReturnType, resolver.Resolve(sourceSignature.ReturnType)) ||
                signature.ParameterTypes.Where((parameter, index) =>
                    !SignatureComparer.Default.Equals(parameter, resolver.Resolve(sourceSignature.ParameterTypes[index]))).Any())
                return false;
            // Keep the member's own !0 in its signature; the constructed owner supplies the list element.
            target = imports.Module.DefaultImporter.ImportMethod(reference);
            return true;
        }

        // The caller binds the predicate and result to the generated Func and Object, which are wrapper types.
        private static MethodDefinition GetUQueryPredicateFactory(InteropRuntimeReferences imports)
        {
            var factory = FindRuntimeStaticMethod(imports, "Il2CppInterop.Runtime.Injection.NativeVisualPredicate", "Create");
            if (factory == null || factory.IsAbstract || !HasPlainSignature(factory, 1, 1) ||
                !IsObjectBaseParameter(factory.GenericParameters[0]) ||
                factory.Signature.ReturnType.FullName != "Il2CppSystem.Object" ||
                factory.Signature.ParameterTypes[0] is not GenericInstanceTypeSignature predicate ||
                predicate.GenericType.FullName != "Il2CppSystem.Func`2" || predicate.TypeArguments.Count != 2 ||
                !IsMethodParameter(predicate.TypeArguments[0], 0) ||
                !SignatureComparer.Default.Equals(predicate.TypeArguments[1], factory.DeclaringModule.CorLibTypeFactory.Boolean))
                return null;
            return factory;
        }
    }
}
