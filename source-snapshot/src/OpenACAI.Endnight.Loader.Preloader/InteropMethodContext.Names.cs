// SPDX-License-Identifier: GPL-3.0-or-later
// Method naming adapted from Il2CppInterop MethodRewriteContext (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System.Linq;
using System.Text;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed partial class InteropMethodContext
    {
        private static readonly string[] MethodAccessTypeLabels =
            { "CompilerControlled", "Private", "FamAndAssem", "Internal", "Protected", "FamOrAssem", "Public" };

        private static readonly (MethodSemanticsAttributes, string)[] SemanticsToCheck =
        {
            (MethodSemanticsAttributes.Setter, "_set"),
            (MethodSemanticsAttributes.Getter, "_get"),
            (MethodSemanticsAttributes.Other, "_oth"),
            (MethodSemanticsAttributes.AddOn, "_add"),
            (MethodSemanticsAttributes.RemoveOn, "_rem"),
            (MethodSemanticsAttributes.Fire, "_fire")
        };

        private string UnmangleMethodName()
        {
            var method = OriginalMethod;
            if (method.Name == "GetType" && method.Parameters.Count == 0)
                return "GetIl2CppType";

            var options = DeclaringType.AssemblyContext.GlobalContext.Options;
            if (options.PassthroughNames)
                return method.Name;
            if (method.Name == ".ctor")
                return ".ctor";
            if (method.Name.IsObfuscated(options))
                return UnmangleMethodNameWithSignature();
            return method.Name.MakeValidInSource();
        }

        private string ProduceMethodSignatureBase()
        {
            var method = OriginalMethod;
            var assembly = DeclaringType.AssemblyContext;
            string name;
            if (method.Name.IsObfuscated(assembly.GlobalContext.Options))
                name = "Method";
            else
                name = method.Name.MakeValidInSource();
            if (method.Name == "GetType" && method.Parameters.Count == 0)
                name = "GetIl2CppType";

            var builder = new StringBuilder();
            builder.Append(name);
            builder.Append('_');
            builder.Append(MethodAccessTypeLabels[(int)(method.Attributes & MethodAttributes.MemberAccessMask)]);
            if (method.IsAbstract) builder.Append("_Abstract");
            if (method.IsVirtual) builder.Append("_Virtual");
            if (method.IsStatic) builder.Append("_Static");
            if (method.IsFinal) builder.Append("_Final");
            if (method.IsNewSlot) builder.Append("_New");
            if (method.Semantics != null)
                foreach (var (semantic, suffix) in SemanticsToCheck)
                    if ((semantic & method.Semantics.Attributes) != 0)
                        builder.Append(suffix);

            builder.Append('_');
            builder.Append(InteropTypeReferences.Rewrite(assembly, method.Signature?.ReturnType)
                .GetUnmangledName(method.DeclaringType, method));
            foreach (var parameter in method.Parameters)
            {
                builder.Append('_');
                builder.Append(InteropTypeReferences.Rewrite(assembly, parameter.ParameterType)
                    .GetUnmangledName(method.DeclaringType, method));
            }

            var scan = assembly.GlobalContext.ScanState;
            if (Rva != 0 && scan.HasObfuscatedMethods && !scan.LiveMethods.Contains(Rva))
                builder.Append("_PDM");
            return builder.ToString();
        }

        private string UnmangleMethodNameWithSignature()
        {
            // Filtering must precede TakeWhile: an unobfuscated method does not match itself.
            var name = ProduceMethodSignatureBase() + "_" + DeclaringType.Methods
                .Where(ParameterSignatureMatchesThis).TakeWhile(method => method != this).Count();
            var renameMap = DeclaringType.AssemblyContext.GlobalContext.Options.RenameMap;
            var namespacePrefix = DeclaringType.NewType.GetNamespacePrefix();
            if (renameMap.TryGetValue(namespacePrefix + "." + DeclaringType.NewType.Name + "::" + name,
                    out var typeName))
                name = typeName;
            else if (renameMap.TryGetValue(namespacePrefix + "::" + name, out var namespaceName))
                name = namespaceName;
            return name;
        }

        private bool ParameterSignatureMatchesThis(InteropMethodContext other)
        {
            var a = other.OriginalMethod;
            var b = OriginalMethod;
            if (!other.OriginalNameObfuscated)
                return false;

            const MethodAttributes comparisonMask = MethodAttributes.MemberAccessMask | MethodAttributes.Static |
                MethodAttributes.Final | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.NewSlot;
            if ((a.Attributes & comparisonMask) != (b.Attributes & comparisonMask))
                return false;
            if (a.Semantics?.Attributes != b.Semantics?.Attributes)
                return false;
            if (a.Signature?.ReturnType.FullName != b.Signature?.ReturnType.FullName)
                return false;
            if (a.Parameters.Count != b.Parameters.Count)
                return false;
            for (var index = 0; index < a.Parameters.Count; index++)
                if (a.Parameters[index].ParameterType.FullName != b.Parameters[index].ParameterType.FullName)
                    return false;

            var scan = DeclaringType.AssemblyContext.GlobalContext.ScanState;
            if (scan.HasObfuscatedMethods && other.Rva != 0 && Rva != 0 &&
                scan.LiveMethods.Contains(other.Rva) != scan.LiveMethods.Contains(Rva))
                return false;
            return true;
        }
    }
}
