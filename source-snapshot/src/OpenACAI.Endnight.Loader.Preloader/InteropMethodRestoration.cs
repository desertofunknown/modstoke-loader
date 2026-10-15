// SPDX-License-Identifier: GPL-3.0-or-later
// Unity method restoration adapted from Il2CppInterop Pass80/81 (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using BepInEx.Logging;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class InteropMethodRestoration
    {
        private readonly List<(MethodDefinition Original, MethodDefinition Target,
            InteropTypeContext Type, InteropRuntimeReferences Imports)> pendingBodies = new();

        internal void Restore(InteropRewriteContext context)
        {
            var restored = 0;
            var ignored = 0;
            foreach (var unityAssembly in context.UnityAssemblies.Assemblies)
            {
                var assembly = context.TryGetAssemblyByName(unityAssembly.Name);
                if (assembly == null)
                    continue;

                var imports = assembly.Imports;
                var resolver = new InteropUnstripTypeResolver(context, imports);
                foreach (var unityType in unityAssembly.ManifestModule.TopLevelTypes)
                {
                    var type = assembly.TryGetTypeByName(unityType.FullName);
                    if (type == null)
                        continue;

                    foreach (var original in unityType.Methods)
                    {
                        var isInternalCall = (original.ImplAttributes & MethodImplAttributes.InternalCall) != 0;
                        if (original.IsConstructor || original.IsAbstract ||
                            (!original.HasMethodBody && !isInternalCall))
                            continue;
                        if (type.TryGetMethodByUnityAssemblyMethod(original) != null)
                            continue;

                        var returnType = resolver.Resolve(original.Signature.ReturnType);
                        if (returnType == null)
                        {
                            LogUnsupported(original, "return", original.Signature.ReturnType);
                            ignored++;
                            continue;
                        }

                        var attributes = (original.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public;
                        var signature = original.IsStatic
                            ? MethodSignature.CreateStatic(returnType, original.Signature.GenericParameterCount)
                            : MethodSignature.CreateInstance(returnType, original.Signature.GenericParameterCount);
                        var target = new MethodDefinition(original.Name, attributes, signature)
                        {
                            CilMethodBody = new CilMethodBody()
                        };

                        var unsupportedParameter = false;
                        foreach (var parameter in original.Parameters)
                        {
                            var parameterType = resolver.Resolve(parameter.ParameterType);
                            if (parameterType == null)
                            {
                                LogUnsupported(original, "parameter", parameter);
                                unsupportedParameter = true;
                                break;
                            }
                            InteropMethodDeclarations.AddParameter(target, parameterType, parameter.Name,
                                parameter.Definition?.Attributes ?? default);
                        }
                        if (unsupportedParameter)
                        {
                            ignored++;
                            continue;
                        }

                        foreach (var parameter in original.GenericParameters)
                        {
                            var copy = new GenericParameter(parameter.Name.MakeValidInSource(), parameter.Attributes);
                            foreach (var constraint in parameter.Constraints)
                            {
                                if (constraint.Constraint?.FullName == "System.ValueType" ||
                                    constraint.Constraint?.Resolve()?.IsInterface == true)
                                    continue;

                                var constraintType = constraint.Constraint?.FullName == "System.Enum"
                                    ? imports.Module.DefaultImporter.ImportTypeSignature(typeof(Enum))
                                    : resolver.Resolve(constraint.Constraint?.ToTypeSignature());
                                if (constraintType != null)
                                    copy.Constraints.Add(new GenericParameterConstraint(constraintType.ToTypeDefOrRef()));
                            }
                            target.GenericParameters.Add(copy);
                        }

                        if (isInternalCall)
                        {
                            var delegateType = InteropNativeCallStubs.CreateDelegateType(original, target, imports);
                            type.NewType.NestedTypes.Add(delegateType);
                            type.NewType.Methods.Add(target);
                            var delegateField = InteropNativeCallStubs.AddInitializer(type.NewType,
                                delegateType, original, imports);
                            InteropNativeCallStubs.FillBody(target, delegateField, delegateType, type, imports);
                        }
                        else
                        {
                            pendingBodies.Add((original, target, type, imports));
                            type.NewType.Methods.Add(target);
                        }

                        if (original.IsGetMethod)
                            GetOrCreateProperty(original, target).GetMethod = target;
                        else if (original.IsSetMethod)
                            GetOrCreateProperty(original, target).SetMethod = target;

                        var paramsMethod = InteropMethodDeclarations.CreateParamsOverload(original, target,
                            imports, resolver.Resolve);
                        if (paramsMethod != null)
                            type.NewType.Methods.Add(paramsMethod);
                        restored++;
                    }
                }
            }

            InteropGenerationDiagnostics.Log(LogLevel.Info, $"Restored {restored} methods");
            InteropGenerationDiagnostics.Log(LogLevel.Info, $"Failed to restore {ignored} methods");
        }

        internal void FillBodies()
        {
            var succeeded = 0;
            var failed = 0;
            try
            {
                foreach (var method in pendingBodies)
                {
                    if (InteropMethodTranslator.TranslateMethod(method.Original, method.Target, method.Type, method.Imports))
                        succeeded++;
                    else
                    {
                        failed++;
                        InteropMethodTranslator.ReplaceBodyWithException(method.Target, method.Imports);
                    }
                }
            }
            finally
            {
                // An interrupted translation must not retain metadata from this run.
                pendingBodies.Clear();
            }
            InteropGenerationDiagnostics.Log(LogLevel.Info,
                $"IL unstrip statistics: {succeeded} successful, {failed} failed");
        }

        private static PropertyDefinition GetOrCreateProperty(MethodDefinition original, MethodDefinition target)
        {
            var originalProperty = original.DeclaringType.Properties.Single(property =>
                property.SetMethod == original || property.GetMethod == original);
            var property = target.DeclaringType?.Properties.SingleOrDefault(candidate =>
                candidate.Name == originalProperty.Name &&
                candidate.Signature.ParameterTypes.Count == originalProperty.Signature.ParameterTypes.Count &&
                candidate.Signature.ParameterTypes.SequenceEqual(originalProperty.Signature.ParameterTypes,
                    SignatureComparer.Default));
            if (property != null)
                return property;

            var propertyType = original.IsGetMethod
                ? target.Signature.ReturnType
                : target.Signature.ParameterTypes.Last();
            var parameters = original.IsGetMethod
                ? target.Signature.ParameterTypes
                : target.Signature.ParameterTypes.Take(target.Signature.ParameterTypes.Count - 1);
            var signature = originalProperty.Signature.HasThis
                ? PropertySignature.CreateInstance(propertyType, parameters)
                : PropertySignature.CreateStatic(propertyType, parameters);
            property = new PropertyDefinition(originalProperty.Name, originalProperty.Attributes, signature);
            target.DeclaringType.Properties.Add(property);
            return property;
        }

        private static void LogUnsupported(MethodDefinition method, string position, object unsupportedType)
        {
            try
            {
                InteropGenerationDiagnostics.Log(LogLevel.Debug,
                    $"Method {method} has unsupported {position} type {unsupportedType}");
            }
            catch
            {
                // Formatting unsupported metadata is diagnostic only.
            }
        }
    }
}
