// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal static class InteropRestoredTypeSupport
    {
        internal static bool IsManagedException(TypeDefinition type) =>
            !type.IsInterface && !type.IsValueType && !type.IsAbstract && type.GenericParameters.Count == 0 &&
            InteropRestoredUnmanagedDelegateSupport.IsSystemType(type.BaseType, "System.Exception") &&
            type.Interfaces.Count == 0 && type.Methods.All(method =>
                !method.IsVirtual && !method.IsAbstract && !method.IsPInvokeImpl && method.HasMethodBody &&
                (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.InternalCall)) == MethodImplAttributes.IL);

        internal static string GetAssemblyName(TypeSignature type)
        {
            // Nested types are scoped to their enclosing type, not directly to an assembly.
            string name = type?.Scope?.GetAssembly()?.Name;
            return name?.EndsWith(".dll", StringComparison.Ordinal) == true ? name.Substring(0, name.Length - 4) : name;
        }

        internal static Type GetModifierType(ITypeDefOrRef type)
        {
            // Modifiers describe CLR metadata, not IL2CPP object instances.
            var assembly = GetAssemblyName(type?.ToTypeSignature());
            if (assembly is not ("mscorlib" or "netstandard" or "System.Runtime" or
                "System.Private.CoreLib" or "System.Runtime.InteropServices"))
                return null;
            return type.FullName switch
            {
                "System.Runtime.CompilerServices.IsVolatile" => typeof(System.Runtime.CompilerServices.IsVolatile),
                "System.Runtime.CompilerServices.IsReadOnlyAttribute" => typeof(System.Runtime.CompilerServices.IsReadOnlyAttribute),
                "System.Runtime.InteropServices.InAttribute" => typeof(System.Runtime.InteropServices.InAttribute),
                "System.Runtime.InteropServices.OutAttribute" => typeof(System.Runtime.InteropServices.OutAttribute),
                _ => null
            };
        }

        internal static TypeSignature StorageType(TypeSignature type)
        {
            while (type is CustomModifierTypeSignature modifier)
                type = modifier.BaseType;
            return type;
        }
    }
}
