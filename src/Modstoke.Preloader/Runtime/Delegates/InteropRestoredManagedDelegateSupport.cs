// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal static partial class InteropRestoredManagedDelegateSupport
    {
        internal static bool TryDescribe(TypeDefinition type, out MethodDefinition invoke, out string reason)
        {
            invoke = null;
            reason = "managed-delegate-contract: A CLR delegate requires an ordinary runtime constructor/Invoke without native marshalling attributes.";
            if (!InteropRestoredUnmanagedDelegateSupport.IsSystemType(type.BaseType, "System.MulticastDelegate") ||
                !type.IsSealed || type.IsAbstract || type.Interfaces.Count != 0 || type.Fields.Count != 0 ||
                type.NestedTypes.Count != 0 || type.DeclaringType?.IsEnum == true ||
                HasNativeConvention(type))
                return false;
            var constructors = type.Methods.Where(method => method.IsConstructor && !method.IsStatic).ToArray();
            var invokes = type.Methods.Where(method => method.Name == "Invoke").ToArray();
            if (constructors.Length != 1 || invokes.Length != 1 ||
                !InteropRestoredUnmanagedDelegateSupport.IsRuntimeMember(constructors[0]) ||
                !InteropRestoredUnmanagedDelegateSupport.IsRuntimeMember(invokes[0]))
                return false;
            var constructor = constructors[0].Signature;
            if (constructor.ReturnType.ElementType != ElementType.Void || constructor.ParameterTypes.Count != 2 ||
                constructor.ParameterTypes[0] is not CorLibTypeSignature { ElementType: ElementType.Object } ||
                constructor.ParameterTypes[1] is not CorLibTypeSignature { ElementType: ElementType.I } ||
                !invokes[0].IsVirtual || type.Methods.Any(method =>
                    method != constructors[0] && method != invokes[0] &&
                    method.Name != "BeginInvoke" && method.Name != "EndInvoke") ||
                new[] { constructors[0], invokes[0] }.Any(method => method.ParameterDefinitions.Any(parameter =>
                    parameter.MarshalDescriptor != null || (parameter.Attributes & ParameterAttributes.HasFieldMarshal) != 0 ||
                    parameter.Constant != null)))
                return false;
            if (!ValidateVariance(type, invokes[0].Signature, definition => definition.Resolve(), false, out reason))
                return false;
            invoke = invokes[0];
            reason = null;
            return true;
        }

        internal static bool HasNativeConvention(TypeDefinition type) => type.CustomAttributes.Any(attribute =>
            InteropRestoredUnmanagedDelegateSupport.IsSystemType(attribute.Constructor?.DeclaringType,
                "System.Runtime.InteropServices.UnmanagedFunctionPointerAttribute"));

        internal static bool IsRestored(InteropTypeContext type) => type?.OriginalType == null &&
            InteropRestoredUnmanagedDelegateSupport.IsSystemType(type?.NewType.BaseType, "System.MulticastDelegate") &&
            !HasNativeConvention(type.NewType);

        internal static TypeDefinition Declare(InteropAssemblyContext assembly, TypeDefinition source)
        {
            if (!TryDescribe(source, out _, out var reason)) throw new InvalidOperationException(reason);
            var module = assembly.NewAssembly.ManifestModule;
            var target = new TypeDefinition(source.Namespace, source.Name, TypeAttributes.Sealed |
                (source.DeclaringType == null ? TypeAttributes.Public : TypeAttributes.NestedPublic),
                module.DefaultImporter.ImportType(typeof(MulticastDelegate)));
            // Nested delegate metadata already contains its enclosing type's parameters.
            foreach (var parameter in source.GenericParameters)
                target.GenericParameters.Add(new GenericParameter(parameter.Name.MakeValidInSource(), parameter.Attributes));
            return target;
        }

        internal static void DeclareMembers(TypeDefinition source, TypeDefinition target, InteropUnstripTypeResolver resolver)
        {
            if (!TryDescribe(source, out var invoke, out var reason)) throw new InvalidOperationException(reason);
            var module = target.DeclaringModule;
            target.Methods.Add(new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig |
                MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName,
                MethodSignature.CreateInstance(module.CorLibTypeFactory.Void, module.CorLibTypeFactory.Object,
                    module.CorLibTypeFactory.IntPtr)) { ImplAttributes = MethodImplAttributes.Runtime });
            var call = new MethodDefinition("Invoke", MethodAttributes.Public | MethodAttributes.Virtual |
                MethodAttributes.NewSlot | MethodAttributes.HideBySig, MethodSignature.CreateInstance(Resolve(invoke.Signature.ReturnType)))
                { ImplAttributes = MethodImplAttributes.Runtime };
            foreach (var parameter in invoke.Parameters)
                InteropMethodDeclarations.AddParameter(call, Resolve(parameter.ParameterType),
                    parameter.Name, parameter.Definition?.Attributes ?? default);
            if (!ValidateVariance(target, call.Signature, definition => definition.Resolve(), false, out reason))
                throw new InvalidOperationException("A planned CLR delegate variance contract changed during binding: " +
                    source.FullName + ": " + reason);
            target.Methods.Add(call);

            TypeSignature Resolve(TypeSignature type) => resolver.Resolve(type) ??
                throw new InvalidOperationException("A planned CLR delegate signature could not be bound: " +
                    source.FullName + ": " + type.FullName);
        }
    }
}
