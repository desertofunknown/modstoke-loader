// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodRestoration
    {
        // Runtime helper contracts are read from the Runtime's metadata, never through reflection. Their
        // signatures name generated assemblies such as Il2Cppmscorlib, which a fresh install only has once
        // this generation has finished.
        private static MethodDefinition FindRuntimeStaticMethod(InteropRuntimeReferences imports, string typeName,
            string methodName)
        {
            var runtime = imports.Module.DefaultImporter.ImportType(typeof(IL2CPP)).Resolve()?.DeclaringModule;
            var type = runtime?.TopLevelTypes.FirstOrDefault(candidate => candidate.FullName == typeName);
            if (type == null || !type.IsPublic || !type.IsAbstract || !type.IsSealed || type.GenericParameters.Count != 0)
                return null;
            var methods = type.Methods.Where(method => method.Name == methodName && method.IsPublic && method.IsStatic).ToArray();
            return methods.Length == 1 ? methods[0] : null;
        }

        // An ordinary generic method whose parameters carry no attributes or custom modifiers.
        private static bool HasPlainSignature(MethodDefinition method, int genericParameters, int parameters) =>
            method.Signature is { } signature && signature.CallingConvention == CallingConventionAttributes.Default &&
            signature.GenericParameterCount == genericParameters && method.GenericParameters.Count == genericParameters &&
            signature.ParameterTypes.Count == parameters &&
            signature.ParameterTypes.Append(signature.ReturnType).All(type => type is not CustomModifierTypeSignature) &&
            method.ParameterDefinitions.All(parameter => parameter.Attributes == default);

        private static bool IsObjectBaseParameter(GenericParameter parameter) =>
            parameter.Attributes == GenericParameterAttributes.NonVariant && parameter.Constraints.Count == 1 &&
            parameter.Constraints[0].Constraint?.Resolve() is { } constraint &&
            constraint.DeclaringModule == parameter.DeclaringModule &&
            constraint.FullName == typeof(Il2CppObjectBase).FullName;

        // By name: AsmResolver's IsCorLib knows only the core-library versions it shipped with, not .NET 11.
        private static bool IsCorLibType(ITypeDefOrRef type, string fullName) =>
            type?.FullName == fullName && type.Scope?.GetAssembly()?.Name?.Value
                is "System.Runtime" or "System.Private.CoreLib" or "mscorlib" or "netstandard";

        private static bool IsMethodParameter(TypeSignature type, int index) =>
            type is GenericParameterSignature { ParameterType: GenericParameterType.Method } parameter && parameter.Index == index;
    }
}
