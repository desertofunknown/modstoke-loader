// SPDX-License-Identifier: GPL-3.0-or-later
// Method preparation adapted from Il2CppInterop Pass18 and Pass19 (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Collections;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using BepInEx.Logging;
using Il2CppInterop.Common.XrefScans;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static partial class InteropMethodDeclarations
    {
        internal static void FinalizeMethods(InteropRewriteContext context,
            IDictionary<long, List<XrefInstance>> callers)
        {
            var nestedWithoutCallers = 0;
            var nestedWithCallers = 0;
            var topLevelWithoutCallers = 0;
            var topLevelWithCallers = 0;
            foreach (var assembly in context.Assemblies)
            foreach (var type in assembly.Types)
            foreach (var method in type.Methods)
            {
                method.FinalizeDeclaration();
                if (!context.ScanState.HasObfuscatedMethods)
                    continue;

                var callerCount = callers.TryGetValue(method.Rva, out var references) ? references.Count : 0;
                var imports = assembly.Imports;
                method.NewMethod.CustomAttributes.Add(new CustomAttribute(
                    (ICustomAttributeType)imports.CallerCountAttributector.Value,
                    new CustomAttributeSignature(new CustomAttributeArgument(
                        imports.Module.CorLibTypeFactory.Int32, callerCount))));

                if (!context.ScanState.HasObfuscatedMethods || method.UnmangledName?.Contains("_PDM_") != true)
                    continue;

                if (method.DeclaringType.OriginalType.IsNested)
                {
                    if (callerCount == 0)
                        nestedWithoutCallers++;
                    else
                        nestedWithCallers++;
                }
                else if (callerCount == 0)
                    topLevelWithoutCallers++;
                else
                    topLevelWithCallers++;
            }

            if (context.ScanState.HasObfuscatedMethods)
                InteropGenerationDiagnostics.Log(LogLevel.Debug,
                    $"Dead method statistics: 0t={topLevelWithoutCallers} mt={topLevelWithCallers} " +
                    $"0n={nestedWithoutCallers} mn={nestedWithCallers}");
        }

        internal static void CopyParameters(InteropRewriteContext context)
        {
            foreach (var assembly in context.Assemblies)
            foreach (var type in assembly.Types)
            foreach (var method in type.Methods)
            {
                foreach (var parameter in method.OriginalMethod.Parameters)
                {
                    var name = parameter.Name.IsObfuscated(context.Options)
                        ? $"param_{parameter.Sequence}"
                        : parameter.Name;
                    var copy = AddParameter(method.NewMethod, InteropTypeReferences.Rewrite(assembly, parameter.ParameterType), name,
                        parameter.GetOrCreateDefinition().Attributes & ~ParameterAttributes.HasFieldMarshal);
                    if (IsParamsArray(parameter))
                    {
                        copy.Definition.Constant = null;
                        copy.Definition.IsOptional = true;
                    }
                    else
                        copy.Definition.Constant = parameter.Definition.Constant;
                }

                var paramsMethod = CreateParamsOverload(method.OriginalMethod, method.NewMethod,
                    assembly.Imports, parameterType => InteropTypeReferences.Rewrite(assembly, parameterType));
                if (paramsMethod != null)
                    type.NewType.Methods.Add(paramsMethod);
            }
        }

        internal static Parameter AddParameter(MethodDefinition method, TypeSignature type, string name,
            ParameterAttributes attributes)
        {
            method.Signature.ParameterTypes.Add(type);
            method.Parameters.PullUpdatesFromMethodSignature();
            var parameter = method.Parameters.Last();
            var definition = parameter.GetOrCreateDefinition();
            definition.Name = name;
            definition.Attributes = attributes;
            return parameter;
        }

        internal static bool IsParamsArray(Parameter parameter) =>
            parameter.ParameterType is SzArrayTypeSignature &&
            parameter.Definition?.CustomAttributes.Any(attribute =>
                attribute.Constructor?.DeclaringType?.FullName == "System.ParamArrayAttribute") == true;
    }
}
