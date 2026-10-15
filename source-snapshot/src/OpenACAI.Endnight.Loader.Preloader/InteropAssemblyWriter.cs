// SPDX-License-Identifier: GPL-3.0-or-later
// Assembly emission adapted from Il2CppInterop Pass90 and CorlibReferences (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Builder;
using AsmResolver.DotNet.Signatures;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class InteropAssemblyWriter
    {
        internal static void Write(InteropRewriteContext context, InteropGenerationOptions options)
        {
            var frameworkConstructor = typeof(TargetFrameworkAttribute).GetConstructor(new[] { typeof(string) });
            foreach (var assembly in context.Assemblies)
            {
                var module = assembly.NewAssembly.ManifestModule;
                var constructor = (ICustomAttributeType)module.DefaultImporter.ImportMethod(frameworkConstructor);
                var stringType = module.CorLibTypeFactory.String;
                var framework = new CustomAttribute(constructor, new CustomAttributeSignature());
                framework.Signature.FixedArguments.Add(
                    new CustomAttributeArgument(stringType, module.OriginalTargetRuntime.ToString()));
                framework.Signature.NamedArguments.Add(new CustomAttributeNamedArgument(
                    CustomAttributeArgumentMemberType.Property, nameof(TargetFrameworkAttribute.FrameworkDisplayName),
                    stringType, new CustomAttributeArgument(stringType, ".NET 6.0")));
                assembly.NewAssembly.CustomAttributes.Add(framework);

                // Importing the attribute can add a core-library reference, so normalize afterward.
                foreach (var reference in module.AssemblyReferences)
                    if (reference.Name?.Value is "System.Private.CoreLib" or "mscorlib" or "netstandard")
                        RewriteCorlibReference(reference);

                foreach (var type in module.GetAllTypes())
                foreach (var method in type.Methods)
                {
                    method.CilMethodBody?.Instructions.OptimizeMacros();
                    module.TokenAllocator.AssignNextAvailableToken(method);
                }
            }

            // Prepare every assembly before filtering output, as later passes use the assigned tokens.
            var assemblies = context.Assemblies.Where(
                assembly => !options.AdditionalAssembliesBlacklist.Contains(assembly.NewAssembly.Name));
            if (options.Parallel)
                Parallel.ForEach(assemblies, WriteAssembly);
            else
                foreach (var assembly in assemblies)
                    WriteAssembly(assembly);

            void WriteAssembly(InteropAssemblyContext assembly)
            {
                var path = Path.Combine(options.OutputDir ?? ".", $"{assembly.NewAssembly.Name}.dll");
                assembly.NewAssembly.Write(path, new ManagedPEImageBuilder(ThrowErrorListener.Instance));
            }
        }

        private static void RewriteCorlibReference(AssemblyReference reference)
        {
            var target = KnownCorLibs.SystemRuntime_v6_0_0_0;
            reference.Attributes = target.Attributes;
            reference.Culture = target.Culture;
            reference.DisableJitCompileOptimizer = target.DisableJitCompileOptimizer;
            reference.EnableJitCompileTracking = target.EnableJitCompileTracking;
            reference.HashValue = target.HashValue?.ToArray();
            reference.HasPublicKey = target.HasPublicKey;
            reference.IsRetargetable = target.IsRetargetable;
            reference.IsWindowsRuntime = target.IsWindowsRuntime;
            reference.Name = target.Name;
            reference.PublicKeyOrToken = target.PublicKeyOrToken?.ToArray();
            reference.Version = target.Version;
        }
    }
}
