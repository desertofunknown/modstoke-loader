// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class InteropRestorationPlan
    {
        private readonly InteropRewriteContext context;
        private readonly Dictionary<TypeDefinition, Entry> entries = new();
        private readonly Dictionary<(string Assembly, string Name), Entry> names = new();

        internal InteropRestorationPlan(InteropRewriteContext context)
        {
            this.context = context;
            foreach (var assembly in context.UnityAssemblies.Assemblies)
            foreach (var type in assembly.ManifestModule.GetAllTypes())
            {
                var existing = FindExisting(assembly.Name, type.FullName);
                var entry = new Entry(type, existing != null);
                entries.Add(type, entry);
                names[(assembly.Name, type.FullName)] = entry;
            }

            foreach (var entry in entries.Values)
            {
                var type = entry.Type;
                if (type.Name == "<Module>" || type.BaseType?.FullName == "System.MulticastDelegate" ||
                    type.DeclaringType?.IsEnum == true)
                    entry.Supported = false;
                if (!entry.Existing && type.GenericParameters.Count != 0)
                    entry.Supported = false;
                if (type.DeclaringType != null)
                    entries[type.DeclaringType].Users.Add(entry);

                if (!entry.Supported || !entry.NeedsLayout)
                    continue;
                if (!type.IsSequentialLayout && !type.IsExplicitLayout)
                {
                    entry.Supported = false;
                    continue;
                }

                foreach (var field in type.Fields)
                {
                    if (field.IsStatic)
                        continue;
                    if ((type.IsExplicitLayout && (field.FieldOffset == null || field.FieldOffset < 0)) ||
                        (field.HasFieldMarshal && field.MarshalDescriptor == null) ||
                        (field.MarshalDescriptor != null && field.MarshalDescriptor is not SimpleMarshalDescriptor) ||
                        !AddFieldDependency(entry, field.Signature?.FieldType))
                    {
                        entry.Supported = false;
                        break;
                    }
                }
            }

            ResolveDependencies();
        }

        internal bool CanRestore(TypeDefinition type) => entries[type].Supported;

        private bool AddFieldDependency(Entry owner, TypeSignature type)
        {
            if (type is PointerTypeSignature)
                return true; // Stored as IntPtr, just like ordinary rewritten value-type fields.
            if (type is CorLibTypeSignature primitive)
                return primitive.ElementType is >= ElementType.Boolean and <= ElementType.R8 or ElementType.I or ElementType.U;
            if (type is not TypeDefOrRefSignature || !InteropTypeReferences.IsValueType(type))
                return false;

            var assemblyName = GetAssemblyName(type);
            var existing = FindExisting(assemblyName, type.FullName);
            if (existing != null)
                return InteropTypeReferences.IsValueType(existing);

            var dependency = FindSource(assemblyName, type.FullName);
            if (dependency == null || !InteropTypeReferences.IsValueType(dependency.Type))
                return false;
            owner.PendingFields++;
            dependency.StorageUsers.Add(owner);
            dependency.Users.Add(owner);
            return true;
        }

        private void ResolveDependencies()
        {
            // Inline fields must have finite layouts. Pointer fields do not create size dependencies.
            var ready = new Queue<Entry>(entries.Values.Where(entry => entry.Supported && entry.PendingFields == 0));
            while (ready.TryDequeue(out var entry))
            {
                entry.FiniteLayout = true;
                foreach (var user in entry.StorageUsers)
                {
                    user.PendingFields--;
                    if (user.Supported && user.PendingFields == 0)
                        ready.Enqueue(user);
                }
            }
            foreach (var entry in entries.Values)
                if (entry.NeedsLayout && !entry.FiniteLayout)
                    entry.Supported = false;

            // A finite struct can still depend on an unavailable enclosing type.
            var unavailable = new Queue<Entry>(entries.Values.Where(entry => !entry.Supported));
            while (unavailable.TryDequeue(out var entry))
            {
                foreach (var user in entry.Users)
                {
                    if (!user.Supported)
                        continue;
                    user.Supported = false;
                    unavailable.Enqueue(user);
                }
            }
        }

        private TypeDefinition FindExisting(string assemblyName, string fullName)
        {
            if (assemblyName == "UnityEngine")
            {
                foreach (var assembly in context.Assemblies)
                {
                    if (!assembly.NewAssembly.Name.StartsWith("UnityEngine"))
                        continue;
                    var type = assembly.TryGetTypeByName(fullName)?.NewType;
                    if (type != null)
                        return type;
                }
            }
            return context.TryGetAssemblyByName(assemblyName)?.TryGetTypeByName(fullName)?.NewType;
        }

        private Entry FindSource(string assemblyName, string fullName)
        {
            if (assemblyName == "UnityEngine")
            {
                foreach (var assembly in context.UnityAssemblies.Assemblies)
                    if (assembly.Name.StartsWith("UnityEngine") &&
                        names.TryGetValue((assembly.Name, fullName), out var match))
                        return match;
            }
            return assemblyName != null && names.TryGetValue((assemblyName, fullName), out var entry) ? entry : null;
        }

        private static string GetAssemblyName(TypeSignature type)
        {
            string name = type.Scope?.Name;
            return name?.EndsWith(".dll", StringComparison.Ordinal) == true ? name.Substring(0, name.Length - 4) : name;
        }

        private sealed class Entry
        {
            internal Entry(TypeDefinition type, bool existing)
            {
                Type = type;
                Existing = existing;
                NeedsLayout = !existing && InteropTypeReferences.IsValueType(type) && !type.IsEnum;
            }

            internal readonly TypeDefinition Type;
            internal readonly bool Existing;
            internal readonly bool NeedsLayout;
            internal readonly List<Entry> StorageUsers = new();
            internal readonly List<Entry> Users = new();
            internal bool Supported = true;
            internal bool FiniteLayout;
            internal int PendingFields;
        }
    }
}
