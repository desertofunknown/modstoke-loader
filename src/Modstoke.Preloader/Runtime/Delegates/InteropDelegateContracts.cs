// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal static class InteropDelegateContracts
    {
        // The caller validates native type arguments and the owning helper's constraints.
        internal static bool CanStoreNativeDelegate(TypeDefinition source, TypeDefinition rewritten)
        {
            if (source == null || rewritten == null ||
                !InteropRestoredUnmanagedDelegateSupport.IsSystemType(source.BaseType, "System.MulticastDelegate") ||
                !source.IsSealed || source.IsAbstract || source.IsInterface || source.Fields.Count != 0 ||
                source.Interfaces.Count != 0 || source.NestedTypes.Count != 0 || source.DeclaringType?.IsEnum == true ||
                InteropRestoredManagedDelegateSupport.HasNativeConvention(source) ||
                rewritten.IsInterface || rewritten.IsAbstract || InteropTypeReferences.IsValueType(rewritten) ||
                source.GenericParameters.Count != rewritten.GenericParameters.Count)
                return false;

            var constructors = source.Methods.Where(method => method.IsConstructor && !method.IsStatic).ToArray();
            var sourceInvokes = source.Methods.Where(method => method.Name == "Invoke").ToArray();
            var rewrittenInvokes = rewritten.Methods.Where(method => method.Name == "Invoke").ToArray();
            if (constructors.Length != 1 || sourceInvokes.Length != 1 || rewrittenInvokes.Length != 1 ||
                !HasStandardConvention(constructors[0].Signature) ||
                !InteropRestoredUnmanagedDelegateSupport.IsRuntimeMember(constructors[0]) ||
                !IsUsableInvoke(sourceInvokes[0]) || !IsUsableInvoke(rewrittenInvokes[0]) ||
                !InteropRestoredUnmanagedDelegateSupport.IsRuntimeMember(sourceInvokes[0]) || !sourceInvokes[0].IsVirtual)
                return false;

            var constructor = constructors[0].Signature;
            if (constructor.ReturnType.ElementType != ElementType.Void || constructor.ParameterTypes.Count != 2 ||
                constructor.ParameterTypes[0] is not CorLibTypeSignature { ElementType: ElementType.Object } ||
                constructor.ParameterTypes[1] is not CorLibTypeSignature { ElementType: ElementType.I } ||
                constructors[0].IsPInvokeImpl || constructors[0].ParameterDefinitions.Any(parameter =>
                    parameter.MarshalDescriptor != null || (parameter.Attributes & ParameterAttributes.HasFieldMarshal) != 0 ||
                    parameter.Constant != null) || source.Methods.Any(method =>
                    method != constructors[0] && method != sourceInvokes[0] &&
                    method.Name != "BeginInvoke" && method.Name != "EndInvoke"))
                return false;

            var invoke = sourceInvokes[0].Signature;
            var rewrittenInvoke = rewrittenInvokes[0].Signature;
            // The existing wrapper owns rewritten type identities; storage does not remap its signature.
            return invoke.ParameterTypes.Count == rewrittenInvoke.ParameterTypes.Count &&
                (invoke.ReturnType.ElementType == ElementType.Void) == (rewrittenInvoke.ReturnType.ElementType == ElementType.Void);

            static bool IsUsableInvoke(MethodDefinition method) =>
                method.IsPublic && !method.IsStatic && !method.IsAbstract && !method.IsPInvokeImpl &&
                method.GenericParameters.Count == 0 && HasStandardConvention(method.Signature) &&
                method.Signature.HasThis && method.Signature.GenericParameterCount == 0 &&
                method.Signature.ParameterTypes.Count <= 16 && CanStoreArgument(method.Signature.ReturnType, true) &&
                method.Signature.ParameterTypes.All(type => CanStoreArgument(type, false)) &&
                !method.ParameterDefinitions.Any(parameter => parameter.MarshalDescriptor != null ||
                    (parameter.Attributes & ParameterAttributes.HasFieldMarshal) != 0 || parameter.Constant != null);

            static bool CanStoreArgument(TypeSignature type, bool allowVoid) =>
                CanBeArgument(type, allowVoid) && type is not PinnedTypeSignature && (type switch
                {
                    ArrayBaseTypeSignature array => CanStoreArgument(array.BaseType, false),
                    GenericInstanceTypeSignature instance => instance.TypeArguments.All(argument => CanStoreArgument(argument, false)),
                    _ => true
                });
        }

        internal static bool TryGetManagedDelegate(ModuleDefinition module, MethodSignature invoke,
            out TypeSignature managedDelegate)
        {
            managedDelegate = null;
            if (invoke == null || !HasStandardConvention(invoke) || invoke.GenericParameterCount != 0 || invoke.ParameterTypes.Count > 16 ||
                !CanBeArgument(invoke.ReturnType, true) || invoke.ParameterTypes.Any(type => !CanBeArgument(type, false)))
                return false;

            var hasReturn = invoke.ReturnType.ElementType != ElementType.Void;
            var arity = invoke.ParameterTypes.Count + (hasReturn ? 1 : 0);
            var name = hasReturn ? "System.Func" : "System.Action";
            if (arity != 0) name += "`" + arity;
            var definition = module.DefaultImporter.ImportTypeSignature(typeof(Action).Assembly.GetType(name, true));
            if (arity == 0)
                managedDelegate = definition;
            else
            {
                var instance = new GenericInstanceTypeSignature(definition.ToTypeDefOrRef(), false);
                foreach (var parameter in invoke.ParameterTypes) instance.TypeArguments.Add(parameter);
                if (hasReturn) instance.TypeArguments.Add(invoke.ReturnType);
                managedDelegate = module.DefaultImporter.ImportTypeSignature(instance);
            }
            return true;
        }

        internal static IMethodDescriptor Conversion(ModuleDefinition module, TypeSignature nativeDelegate) =>
            ((IMethodDefOrRef)module.DefaultImporter.ImportMethod(typeof(InteropDelegateSupport)
                .GetMethod(nameof(InteropDelegateSupport.Convert))))
            .MakeGenericInstanceMethod(nativeDelegate);

        internal static bool HasStandardConvention(MethodSignature signature) =>
            signature != null && signature.CallingConvention == CallingConventionAttributes.Default &&
            !signature.ExplicitThis && !signature.IncludeSentinel && !signature.HasSentinelParameterTypes;

        internal static bool CanBeArgument(TypeSignature type, bool allowVoid)
        {
            if (type == null || type is PointerTypeSignature or ByReferenceTypeSignature or
                FunctionPointerTypeSignature or CustomModifierTypeSignature or SentinelTypeSignature ||
                type.ElementType == ElementType.TypedByRef)
                return false;
            if (type.ElementType == ElementType.Void) return allowVoid;
            if (type is GenericParameterSignature or CorLibTypeSignature) return true;
            if (type is GenericInstanceTypeSignature instance && instance.TypeArguments.Any(argument => !CanBeArgument(argument, false)))
                return false;
            return type.Resolve()?.CustomAttributes.Any(attribute => attribute.Constructor?.DeclaringType?.FullName ==
                "System.Runtime.CompilerServices.IsByRefLikeAttribute") != true;
        }
    }
}
