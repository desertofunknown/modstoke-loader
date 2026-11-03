// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropRestorationPlan
    {
        private bool IsManagedHelper(Entry entry)
        {
            HashSet<TypeDefinition> visited = null;
            Entry directBase = null;
            var current = entry;
            while (true)
            {
                var type = current.Type;
                if (type.IsInterface || type.IsValueType || type.Interfaces.Count != 0 ||
                    type.Methods.Any(method => method.IsVirtual || method.IsAbstract ||
                        !method.IsStatic && (!method.HasMethodBody ||
                            (method.ImplAttributes & MethodImplAttributes.InternalCall) != 0))) return false;
                if (InteropRestoredUnmanagedDelegateSupport.IsSystemType(type.BaseType, "System.Object"))
                {
                    entry.ManagedBase = directBase;
                    directBase?.Users.Add((entry, "managed-base-type-unavailable: " + directBase.Type.FullName));
                    return true;
                }
                if (type.GenericParameters.Count != 0 || type.BaseType?.ToTypeSignature() is not TypeDefOrRefSignature parent)
                    return false;
                var next = FindSource(InteropRestoredTypeSupport.GetAssemblyName(parent), parent.FullName);
                if (next == null || next.Existing != null || next.Type.IsSealed || next.Type.GenericParameters.Count != 0)
                    return false;
                visited ??= new HashSet<TypeDefinition>(ReferenceEqualityComparer.Instance);
                if (!visited.Add(type)) return false;
                directBase ??= next;
                current = next;
            }
        }

        internal IEnumerable<(TypeDefinition Type, TypeDefinition Base)> ManagedBases()
        {
            foreach (var entry in entries.Values)
                if (entry.Supported && entry.ManagedBase != null)
                    yield return (entry.Type, entry.ManagedBase.Type);
        }

        internal bool IsManagedReference(TypeSignature type) => FindManagedReference(type) != null;

        internal bool CanAssignManagedReference(TypeSignature source, TypeSignature destination)
        {
            var target = FindManagedReference(destination);
            if (target == null) return false;
            for (var current = FindManagedReference(source); current != null; current = current.ManagedBase)
                if (current == target) return true;
            return false;
        }

        internal TypeSignature CommonManagedBase(TypeSignature left, TypeSignature right)
        {
            var rightEntry = FindManagedReference(right);
            if (rightEntry == null) return null;
            for (var current = FindManagedReference(left); current != null; current = current.ManagedBase)
                for (var ancestor = rightEntry; ancestor != null; ancestor = ancestor.ManagedBase)
                    if (current == ancestor) return current.Type.ToTypeSignature();
            return null;
        }

        private Entry FindManagedReference(TypeSignature type)
        {
            if (type is not TypeDefOrRefSignature || type.IsValueType) return null;
            var entry = FindSource(InteropRestoredTypeSupport.GetAssemblyName(type), type.FullName);
            return entry is { Existing: null, Supported: true } && !entry.Type.IsInterface &&
                entry.Type.GenericParameters.Count == 0 && (entry.ManagedBase != null ||
                    InteropRestoredUnmanagedDelegateSupport.IsSystemType(entry.Type.BaseType, "System.Object"))
                ? entry : null;
        }
    }
}
