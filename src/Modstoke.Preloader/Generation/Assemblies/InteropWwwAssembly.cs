// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;

namespace Modstoke.Preloader
{
    internal static class InteropWwwAssembly
    {
        private const string AssemblyName = "UnityEngine.UnityWebRequestWWWModule";
        private const string ModuleName = AssemblyName + ".dll";
        private static readonly ConditionalWeakTable<AssemblyDefinition, ModuleDefinition> Prepared = new();

        internal static void Prepare(InteropGenerationOptions options, InteropAssemblyMetadata game,
            InteropAssemblyMetadata unity)
        {
            var source = unity.Assemblies.SingleOrDefault(assembly => assembly.Name == AssemblyName);
            if (source?.ManifestModule?.Mvid != new Guid("51a8b350-562c-460c-a41b-4d62f6375839") ||
                string.IsNullOrEmpty(options.UnityBaseLibsDir) || string.IsNullOrEmpty(options.GameAssemblyPath) ||
                !MatchesHash(Path.Combine(options.UnityBaseLibsDir, ModuleName),
                    "B828F2A9F87C024616D1F82CAFC3F8AAB2AA960BD38E1A6D1FED5814C5D2CBB5") ||
                !MatchesHash(options.GameAssemblyPath,
                    "9BEED2673A205A07F9CB00A6911CA7B3372F74F98F8AA05D90A021423980AA9B"))
                return;

            if (source.Modules.Count != 1 || source.ManifestModule.Name != ModuleName ||
                source.Version != new Version(0, 0, 0, 0) || !string.IsNullOrEmpty(source.Culture?.ToString()) ||
                source.HasPublicKey || source.PublicKey is { Length: > 0 })
                throw Invalid("the source assembly identity differs from its inspected contract");

            var matches = game.Assemblies.Where(assembly =>
                string.Equals(assembly.Name?.ToString(), AssemblyName, StringComparison.OrdinalIgnoreCase) ||
                assembly.Modules.Any(module => string.Equals(module.Name?.ToString(), ModuleName,
                    StringComparison.OrdinalIgnoreCase))).ToArray();
            if (matches.Length != 0)
            {
                if (matches.Length == 1 && matches[0].Name == AssemblyName && matches[0].Modules.Count == 1 &&
                    matches[0].ManifestModule.Name == ModuleName && matches[0].Version == source.Version &&
                    string.IsNullOrEmpty(matches[0].Culture?.ToString()) && !matches[0].HasPublicKey &&
                    matches[0].PublicKey is not { Length: > 0 })
                    return;
                throw Invalid("the target contains a conflicting assembly or module identity");
            }

            // An empty input gives this original-named module the normal generation pass order.
            // Missing native types still require their own publication and restoration contracts.
            var target = new AssemblyDefinition(AssemblyName, source.Version);
            target.Modules.Add(new ModuleDefinition(ModuleName));
            game.Add(target);
            Prepared.Add(target, source.ManifestModule);
        }

        internal static bool IsPrepared(ModuleDefinition target, ModuleDefinition source) =>
            target?.Assembly != null && Prepared.TryGetValue(target.Assembly, out var original) &&
            ReferenceEquals(original, source) && ReferenceEquals(target.Assembly.ManifestModule, target);

        internal static void RestoreAttributes(InteropRewriteContext context)
        {
            foreach (var assembly in context.Assemblies)
            {
                if (assembly.OriginalAssembly == null || !Prepared.TryGetValue(assembly.OriginalAssembly, out var source))
                    continue;
                var target = assembly.NewAssembly;
                var module = target.ManifestModule;
                if (target.Name != AssemblyName || target.Version != source.Assembly.Version ||
                    target.Modules.Count != 1 || module.Name != ModuleName ||
                    target.CustomAttributes.Count != 0 || module.CustomAttributes.Count != 0)
                    throw Invalid("the generated assembly no longer matches its prepared metadata");

                var attributes = new List<CustomAttribute>();
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var attribute in source.Assembly.CustomAttributes)
                {
                    var name = attribute.Constructor?.DeclaringType?.FullName;
                    if (name == null) throw Invalid("a source attribute has no declaring type");
                    counts.TryGetValue(name, out var count);
                    counts[name] = count + 1;
                    switch (name)
                    {
                        case "System.Runtime.CompilerServices.InternalsVisibleToAttribute":
                            attributes.Add(CopyAttribute(attribute, typeof(InternalsVisibleToAttribute), typeof(string)));
                            break;
                        case "System.Diagnostics.DebuggableAttribute":
                            // Source debugging metadata belongs to the native graph; copying it would
                            // disable JIT optimization of the generated CLR implementation.
                            if (attribute.Signature is not { FixedArguments.Count: 1, NamedArguments.Count: 0 } debugging ||
                                debugging.FixedArguments[0].ArgumentType.FullName != "System.Diagnostics.DebuggableAttribute+DebuggingModes" ||
                                debugging.FixedArguments[0].Element is not int modes || modes != 263)
                                throw Invalid("the source debugging attribute differs from its inspected contract");
                            break;
                        case "System.Runtime.CompilerServices.CompilationRelaxationsAttribute":
                            attributes.Add(CopyAttribute(attribute, typeof(CompilationRelaxationsAttribute), typeof(int)));
                            break;
                        case "System.Runtime.CompilerServices.ExtensionAttribute":
                            attributes.Add(CopyAttribute(attribute, typeof(ExtensionAttribute)));
                            break;
                        case "System.Runtime.CompilerServices.RuntimeCompatibilityAttribute":
                            attributes.Add(CopyAttribute(attribute, typeof(RuntimeCompatibilityAttribute)));
                            break;
                        case "UnityEngine.UnityEngineModuleAssembly":
                            // Its interop wrapper derives from Il2CppSystem.Attribute, not CLR Attribute.
                            // Keep the source declaration for the native graph instead of emitting an invalid CLR attribute.
                            if (attribute.Signature is not { FixedArguments.Count: 0, NamedArguments.Count: 0 })
                                throw Invalid("the native Unity assembly marker has an unexpected signature");
                            break;
                        default:
                            throw Invalid("an unsupported source assembly attribute was found: " + name);
                    }
                }
                if (counts.Count != 6 || counts.GetValueOrDefault("System.Runtime.CompilerServices.InternalsVisibleToAttribute") != 152 ||
                    counts.Any(pair => pair.Key != "System.Runtime.CompilerServices.InternalsVisibleToAttribute" && pair.Value != 1) ||
                    attributes.Count != 155 || source.CustomAttributes.Count != 1)
                    throw Invalid("the source assembly or module attribute set differs from its inspected contract");
                var moduleAttribute = CopyAttribute(source.CustomAttributes[0], typeof(System.Security.UnverifiableCodeAttribute));
                // Prepare all imports before publishing either attribute collection.
                foreach (var attribute in attributes) target.CustomAttributes.Add(attribute);
                module.CustomAttributes.Add(moduleAttribute);

                CustomAttribute CopyAttribute(CustomAttribute original, Type type, params Type[] parameters)
                {
                    var constructor = type.GetConstructor(parameters)
                        ?? throw Invalid("the CLR attribute constructor is unavailable: " + type.FullName);
                    var signature = original.Signature;
                    if (original.Constructor?.Name != ".ctor" || original.Constructor.DeclaringType?.FullName != type.FullName ||
                        original.Constructor.Signature is not { HasThis: true, GenericParameterCount: 0 } method ||
                        method.ReturnType.FullName != "System.Void" ||
                        !method.ParameterTypes.Select(parameter => parameter.FullName).SequenceEqual(parameters.Select(parameter => parameter.FullName)) ||
                        signature == null || signature.FixedArguments.Count != parameters.Length)
                        throw Invalid("a source CLR attribute constructor differs from its inspected contract");
                    var copy = new CustomAttribute((ICustomAttributeType)module.DefaultImporter.ImportMethod(constructor),
                        new CustomAttributeSignature());
                    for (var i = 0; i < parameters.Length; i++)
                    {
                        if (signature.FixedArguments[i].ArgumentType.FullName != parameters[i].FullName)
                            throw Invalid("a source CLR attribute argument has an unexpected type");
                        copy.Signature.FixedArguments.Add(new CustomAttributeArgument(
                            module.DefaultImporter.ImportTypeSignature(parameters[i]), signature.FixedArguments[i].Element));
                    }
                    if (type == typeof(RuntimeCompatibilityAttribute))
                    {
                        if (signature.NamedArguments.Count != 1) throw Invalid("the runtime compatibility property is missing");
                        var property = signature.NamedArguments[0];
                        if (property.MemberType != CustomAttributeArgumentMemberType.Property ||
                            property.MemberName != nameof(RuntimeCompatibilityAttribute.WrapNonExceptionThrows) ||
                            property.ArgumentType.FullName != "System.Boolean" ||
                            property.Argument.ArgumentType.FullName != "System.Boolean" || property.Argument.Element is not true)
                            throw Invalid("the runtime compatibility property differs from its inspected contract");
                        copy.Signature.NamedArguments.Add(new CustomAttributeNamedArgument(CustomAttributeArgumentMemberType.Property,
                            property.MemberName, module.CorLibTypeFactory.Boolean,
                            new CustomAttributeArgument(module.CorLibTypeFactory.Boolean, true)));
                    }
                    else if (signature.NamedArguments.Count != 0)
                        throw Invalid("a source CLR attribute has unexpected named arguments");
                    return copy;
                }
            }
        }

        private static bool MatchesHash(string path, string expected)
        {
            if (!File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)) == expected;
        }

        private static InvalidDataException Invalid(string detail) => new("Legacy WWW assembly metadata is incompatible: " + detail + ".");
    }
}
