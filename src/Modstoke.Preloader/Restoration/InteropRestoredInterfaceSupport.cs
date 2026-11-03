// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal static partial class InteropRestoredInterfaceSupport
    {
        internal static bool TryDescribe(TypeDefinition type, out string reason)
        {
            reason = "managed-interface-contract: Only abstract leaf interfaces with invariant type parameters and ordinary non-generic instance methods can be restored as CLR contracts.";
            if (!type.IsInterface || !type.IsAbstract || type.BaseType != null ||
                type.Interfaces.Count != 0 || type.Fields.Count != 0 || type.Properties.Count != 0 ||
                type.Events.Count != 0 || type.MethodImplementations.Count != 0 ||
                type.GenericParameters.Any(parameter => (parameter.Attributes & GenericParameterAttributes.VarianceMask) != 0) ||
                type.Methods.Any(method => !method.IsPublic || method.IsStatic || !method.IsAbstract ||
                    !method.IsVirtual || !method.IsNewSlot || method.IsFinal || method.HasMethodBody ||
                    method.IsConstructor || method.IsPInvokeImpl || method.GenericParameters.Count != 0 ||
                    method.Signature.GenericParameterCount != 0 || !method.Signature.HasThis ||
                    !InteropDelegateContracts.HasStandardConvention(method.Signature) ||
                    (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.InternalCall)) != 0 ||
                    method.ParameterDefinitions.Any(parameter => parameter.MarshalDescriptor != null ||
                        (parameter.Attributes & ParameterAttributes.HasFieldMarshal) != 0)))
                return false;
            foreach (var method in type.Methods)
            foreach (var parameter in method.ParameterDefinitions)
            {
                if (parameter.Sequence <= method.Signature.ParameterTypes.Count &&
                    HasCompatibleDefault(parameter, parameter.Sequence == 0 ? method.Signature.ReturnType :
                        method.Signature.ParameterTypes[parameter.Sequence - 1]))
                    continue;
                reason = "managed-interface-default: The parameter default is not an ordinary CLI constant compatible with its signature: " +
                    method.FullName + ": " + parameter.Sequence;
                return false;
            }
            reason = null;
            return true;
        }

        internal static void DeclareMembers(TypeDefinition source, TypeDefinition target,
            InteropUnstripTypeResolver resolver)
        {
            if (!TryDescribe(source, out var reason) || !target.IsInterface || target.BaseType != null)
                throw new InvalidOperationException(source.FullName + ": " +
                    (reason ?? "The restored interface has an invalid CLR declaration."));
            if (source.GenericParameters.Count != target.GenericParameters.Count)
                throw new InvalidOperationException("managed-generic-arity-mismatch: " + source.FullName);
            foreach (var method in source.Methods)
            {
                var signature = MethodSignature.CreateInstance(Resolve(method.Signature.ReturnType));
                var copy = new MethodDefinition(method.Name, method.Attributes, signature);
                var returnDefinition = method.Parameters.ReturnParameter.Definition;
                if (returnDefinition != null)
                {
                    var returnCopy = copy.Parameters.ReturnParameter.GetOrCreateDefinition();
                    CopyMetadata(returnDefinition, returnCopy, signature.ReturnType);
                }
                foreach (var parameter in method.Parameters)
                {
                    var parameterCopy = InteropMethodDeclarations.AddParameter(copy, Resolve(parameter.ParameterType),
                        parameter.Name, parameter.Definition?.Attributes ?? default);
                    if (parameter.Definition != null)
                        CopyMetadata(parameter.Definition, parameterCopy.Definition, parameterCopy.ParameterType);
                }
                target.Methods.Add(copy);

                void CopyMetadata(ParameterDefinition original, ParameterDefinition destination, TypeSignature type)
                {
                    if (!HasCompatibleDefault(original, type))
                        throw new InvalidOperationException("managed-interface-default: A parameter default is incompatible with its restored CLR signature: " +
                            method.FullName + ": " + original.Sequence + ": " + type.FullName);
                    destination.Name = original.Name;
                    destination.Attributes = original.Attributes;
                    destination.Constant = original.Constant;
                }

                TypeSignature Resolve(TypeSignature type) => resolver.Resolve(type) ??
                    throw new InvalidOperationException("A planned CLR interface signature could not be bound: " +
                        method.FullName + ": " + type.FullName + ": " + resolver.GetRejectionReason(type));
            }
        }

        private static bool HasCompatibleDefault(ParameterDefinition parameter, TypeSignature type)
        {
            var constant = parameter.Constant;
            if (constant == null)
                return !parameter.HasDefault;
            if (!parameter.HasDefault || constant.Value?.Data is not { } data)
                return false;

            var validData = constant.Type switch
            {
                ElementType.Boolean => data.Length == 1 && data[0] <= 1,
                ElementType.I1 or ElementType.U1 => data.Length == 1,
                ElementType.Char or ElementType.I2 or ElementType.U2 => data.Length == 2,
                ElementType.I4 or ElementType.U4 or ElementType.R4 => data.Length == 4,
                ElementType.I8 or ElementType.U8 or ElementType.R8 => data.Length == 8,
                ElementType.String => data.Length % 2 == 0,
                ElementType.Class => data.Length == 4 && data.All(value => value == 0),
                _ => false
            };
            if (!validData)
                return false;

            while (type is CustomModifierTypeSignature modifier)
                type = modifier.BaseType;
            // Null defaults must remain references; generic and value defaults need a separate contract.
            if (constant.Type == ElementType.Class)
                return type is CorLibTypeSignature { ElementType: ElementType.String or ElementType.Object } or
                    TypeDefOrRefSignature { IsValueType: false } or GenericInstanceTypeSignature { IsValueType: false } or
                    ArrayBaseTypeSignature;
            if (type is CorLibTypeSignature primitive)
                // Object becomes an IL2CPP wrapper, so a boxed CLR constant cannot remain its default.
                return primitive.ElementType == constant.Type;
            if (type is TypeDefOrRefSignature { IsValueType: true } && type.Resolve() is { IsEnum: true } enumeration)
                return constant.Type is >= ElementType.I1 and <= ElementType.U8 &&
                    enumeration.GetEnumUnderlyingType()?.ElementType == constant.Type;
            return false;
        }
    }
}
