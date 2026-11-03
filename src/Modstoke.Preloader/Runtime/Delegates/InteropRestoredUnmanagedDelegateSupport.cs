// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using System.Runtime.InteropServices;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal static class InteropRestoredUnmanagedDelegateSupport
    {
        internal static bool IsSystemType(ITypeDescriptor type, string name) =>
            type?.FullName == name && InteropRestoredTypeSupport.GetAssemblyName(type.ToTypeSignature()) is
                "mscorlib" or "netstandard" or "System.Runtime" or "System.Private.CoreLib" or "System.Runtime.InteropServices";

        internal static bool TryDescribe(TypeDefinition type, out MethodDefinition invoke,
            out int convention, out string reason)
        {
            invoke = null;
            convention = 0;
            reason = "missing-native-delegate: Only closed unmanaged delegates with an explicit calling convention and blittable scalar signatures can be restored as CLR delegates.";
            if (!IsSystemType(type.BaseType, "System.MulticastDelegate") || !type.IsSealed || type.IsAbstract ||
                type.GenericParameters.Count != 0 || type.Interfaces.Count != 0 || type.Fields.Count != 0 ||
                type.NestedTypes.Count != 0 || type.DeclaringType?.IsEnum == true)
                return false;
            var attributes = type.CustomAttributes.Where(attribute => IsSystemType(attribute.Constructor?.DeclaringType,
                "System.Runtime.InteropServices.UnmanagedFunctionPointerAttribute")).ToArray();
            if (attributes.Length != 1 || attributes[0].Signature is not { } attribute ||
                attribute.FixedArguments.Count != 1 || attribute.NamedArguments.Count != 0 ||
                attribute.FixedArguments[0].Element is not int value ||
                value != (int)CallingConvention.Winapi && value != (int)CallingConvention.Cdecl && value != (int)CallingConvention.StdCall)
                return false;
            var constructors = type.Methods.Where(method => method.IsConstructor && !method.IsStatic).ToArray();
            var invokes = type.Methods.Where(method => method.Name == "Invoke").ToArray();
            if (constructors.Length != 1 || invokes.Length != 1 ||
                !IsRuntimeMember(constructors[0]) || !IsRuntimeMember(invokes[0]))
                return false;
            var constructor = constructors[0].Signature;
            if (constructor.ReturnType.ElementType != ElementType.Void || constructor.ParameterTypes.Count != 2 ||
                constructor.ParameterTypes[0] is not CorLibTypeSignature { ElementType: ElementType.Object } ||
                constructor.ParameterTypes[1] is not CorLibTypeSignature { ElementType: ElementType.I } ||
                type.Methods.Any(method => method != constructors[0] && method != invokes[0] &&
                    method.Name != "BeginInvoke" && method.Name != "EndInvoke"))
                return false;
            invoke = invokes[0];
            if (!invoke.IsVirtual || !IsScalar(invoke.Signature.ReturnType, true) ||
                invoke.Signature.ParameterTypes.Any(parameter => !IsScalar(parameter, false)) ||
                invoke.ParameterDefinitions.Any(parameter => parameter.MarshalDescriptor != null ||
                    (parameter.Attributes & ParameterAttributes.HasFieldMarshal) != 0 || parameter.Constant != null))
            {
                invoke = null;
                return false;
            }
            convention = value;
            reason = null;
            return true;
        }

        internal static bool IsRuntimeMember(MethodDefinition method) =>
            !method.IsStatic && !method.IsAbstract && !method.HasMethodBody &&
            (method.ImplAttributes & MethodImplAttributes.CodeTypeMask) == MethodImplAttributes.Runtime &&
            method.GenericParameters.Count == 0 && method.Signature.GenericParameterCount == 0 && method.Signature.HasThis &&
            InteropDelegateContracts.HasStandardConvention(method.Signature);

        private static bool IsScalar(TypeSignature type, bool allowVoid) => type is CorLibTypeSignature scalar &&
            (scalar.ElementType is >= ElementType.I1 and <= ElementType.R8 or ElementType.I or ElementType.U ||
                allowVoid && scalar.ElementType == ElementType.Void);

        internal static bool IsRestored(InteropTypeContext type) => type?.OriginalType == null &&
            IsSystemType(type?.NewType.BaseType, "System.MulticastDelegate") &&
            InteropRestoredManagedDelegateSupport.HasNativeConvention(type.NewType);

        internal static TypeDefinition Declare(InteropAssemblyContext assembly, TypeDefinition source)
        {
            if (!TryDescribe(source, out var invoke, out var convention, out var reason))
                throw new InvalidOperationException(reason);
            var module = assembly.NewAssembly.ManifestModule;
            var importer = module.DefaultImporter;
            var attributes = TypeAttributes.Sealed | (source.DeclaringType == null ? TypeAttributes.Public : TypeAttributes.NestedPublic);
            var target = new TypeDefinition(source.Namespace, source.Name, attributes, importer.ImportType(typeof(MulticastDelegate)));
            var constructor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig |
                MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName,
                MethodSignature.CreateInstance(module.CorLibTypeFactory.Void, module.CorLibTypeFactory.Object,
                    module.CorLibTypeFactory.IntPtr)) { ImplAttributes = MethodImplAttributes.Runtime };
            target.Methods.Add(constructor);
            var call = new MethodDefinition("Invoke", MethodAttributes.Public | MethodAttributes.Virtual |
                MethodAttributes.NewSlot | MethodAttributes.HideBySig,
                MethodSignature.CreateInstance(module.CorLibTypeFactory.FromElementType(invoke.Signature.ReturnType.ElementType)))
                { ImplAttributes = MethodImplAttributes.Runtime };
            foreach (var parameter in invoke.Parameters)
                InteropMethodDeclarations.AddParameter(call, module.CorLibTypeFactory.FromElementType(parameter.ParameterType.ElementType),
                    parameter.Name, parameter.Definition?.Attributes ?? default);
            target.Methods.Add(call);
            target.CustomAttributes.Add(new CustomAttribute((ICustomAttributeType)importer.ImportMethod(
                typeof(UnmanagedFunctionPointerAttribute).GetConstructor(new[] { typeof(CallingConvention) })),
                new CustomAttributeSignature(new CustomAttributeArgument(importer.ImportTypeSignature(typeof(CallingConvention)), convention))));
            return target;
        }
    }
}
