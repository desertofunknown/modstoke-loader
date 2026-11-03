// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal static class InteropRestoredValueContracts
    {
        internal static bool CanRestore(MethodDefinition source, TypeDefinition target) =>
            source.DeclaringType.IsValueType && !source.DeclaringType.IsEnum &&
            target.IsValueType && !target.IsEnum && source.IsVirtual && !source.IsStatic &&
            !source.IsAbstract && source.HasMethodBody && !source.IsPInvokeImpl &&
            (source.ImplAttributes & MethodImplAttributes.InternalCall) == 0 &&
            InteropDelegateContracts.HasStandardConvention(source.Signature) && source.Signature.HasThis &&
            (IsObjectOverride(source) || source.IsNewSlot);

        internal static MethodAttributes RewriteAttributes(MethodDefinition source, MethodAttributes attributes)
        {
            // CLR object slots have matching primitive/string signatures. Other managed
            // implementations remain directly callable without claiming a native vtable.
            return IsObjectOverride(source) ? attributes : attributes &
                ~(MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot |
                  MethodAttributes.CheckAccessOnOverride);
        }

        internal static void AttachInterface(MethodDefinition source, MethodDefinition target, ModuleDefinition module)
        {
            if (!source.IsNewSlot || source.Signature.GenericParameterCount != 0 ||
                source.DeclaringType.GenericParameters.Count != 0 || target.DeclaringType.GenericParameters.Count != 0)
                return;
            foreach (var implementation in source.DeclaringType.Interfaces)
            {
                var contract = implementation.Interface.ToTypeSignature();
                var definition = contract is GenericInstanceTypeSignature generic
                    ? generic.GenericType.ToTypeSignature() : contract;
                if (InteropRestoredTypeSupport.GetAssemblyName(definition) is not
                    ("mscorlib" or "netstandard" or "System.Runtime" or "System.Private.CoreLib")) continue;

                Type runtimeInterface;
                string member;
                if (definition.FullName == "System.IDisposable" && contract is not GenericInstanceTypeSignature &&
                    source.Signature.ReturnType.ElementType == ElementType.Void && source.Parameters.Count == 0)
                {
                    runtimeInterface = typeof(IDisposable);
                    member = nameof(IDisposable.Dispose);
                }
                else if (contract is GenericInstanceTypeSignature self && self.TypeArguments.Count == 1 &&
                    SignatureComparer.Default.Equals(self.TypeArguments[0], source.DeclaringType.ToTypeSignature()) &&
                    source.Parameters.Count == 1 &&
                    SignatureComparer.Default.Equals(source.Parameters[0].ParameterType, self.TypeArguments[0]) &&
                    SignatureComparer.Default.Equals(target.Parameters[0].ParameterType, target.DeclaringType.ToTypeSignature()))
                {
                    if (definition.FullName == "System.IEquatable`1" && source.Signature.ReturnType.ElementType == ElementType.Boolean)
                    {
                        runtimeInterface = typeof(IEquatable<>);
                        member = "Equals";
                    }
                    else if (definition.FullName == "System.IComparable`1" && source.Signature.ReturnType.ElementType == ElementType.I4)
                    {
                        runtimeInterface = typeof(IComparable<>);
                        member = "CompareTo";
                    }
                    else continue;
                }
                else continue;

                if (!Implements(source, contract, member)) continue;
                var importer = module.DefaultImporter;
                var rewritten = importer.ImportTypeSignature(runtimeInterface);
                if (runtimeInterface.IsGenericTypeDefinition)
                    rewritten = new GenericInstanceTypeSignature(rewritten.ToTypeDefOrRef(), false,
                        target.DeclaringType.ToTypeSignature());
                var prototype = importer.ImportMethod(runtimeInterface.GetMethod(member));
                var declaration = new MemberReference(rewritten.ToTypeDefOrRef(), member, prototype.Signature);
                if (!target.DeclaringType.Interfaces.Any(existing =>
                    SignatureComparer.Default.Equals(existing.Interface.ToTypeSignature(), rewritten)))
                    target.DeclaringType.Interfaces.Add(new InterfaceImplementation(rewritten.ToTypeDefOrRef()));
                target.Attributes |= MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot;
                target.DeclaringType.MethodImplementations.Add(new MethodImplementation(declaration, target));
                return;
            }
        }

        private static bool IsObjectOverride(MethodDefinition method) =>
            !method.IsNewSlot && method.IsPublic && method.Signature.GenericParameterCount == 0 &&
            method.Parameters.Count == 0 && method.DeclaringType.BaseType?.FullName == "System.ValueType" &&
            (method.Name == "GetHashCode" && method.Signature.ReturnType.ElementType == ElementType.I4 ||
             method.Name == "ToString" && method.Signature.ReturnType.ElementType == ElementType.String);

        private static bool Implements(MethodDefinition method, TypeSignature contract, string member)
        {
            var explicitSlots = method.DeclaringType.MethodImplementations
                .Where(implementation => implementation.Declaration.Name == member &&
                    SignatureComparer.Default.Equals(implementation.Declaration.DeclaringType.ToTypeSignature(), contract)).ToArray();
            if (explicitSlots.Length != 0)
                return explicitSlots.Length == 1 && explicitSlots[0].Body.Resolve() == method;
            return method.IsPublic && method.Name == member;
        }
    }
}
