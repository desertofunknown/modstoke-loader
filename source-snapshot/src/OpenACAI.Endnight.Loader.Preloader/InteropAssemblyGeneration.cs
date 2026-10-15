// SPDX-License-Identifier: GPL-3.0-or-later
// Pass order follows Il2CppInterop's assembly generator (Il2CppInterop contributors, LGPL-3.0).
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using AsmResolver.DotNet;
using BepInEx.Logging;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class InteropAssemblyGeneration
    {
        internal static void Run(InteropGenerationOptions options)
        {
            if (options.Source == null || options.Source.Count == 0)
                throw new InvalidDataException("Interop generation requires source assemblies.");
            if (string.IsNullOrEmpty(options.OutputDir))
                throw new InvalidDataException("Interop generation requires an output directory.");

            var scanState = new InteropScanState();
            InteropAssemblyMetadata gameAssemblies = null;
            InteropAssemblyMetadata unityAssemblies = null;
            InteropRewriteContext context = null;
            ExceptionDispatchInfo failure = null;
            try
            {
                scanState.Clear(ref failure);
                failure?.Throw();
                Directory.CreateDirectory(options.OutputDir);
                RunPass("Reading assemblies", () => gameAssemblies = new InteropAssemblyMetadata(options.Source));
                RunPass("Reading Unity assemblies", () => unityAssemblies = string.IsNullOrEmpty(options.UnityBaseLibsDir)
                    ? new InteropAssemblyMetadata(Array.Empty<AssemblyDefinition>())
                    : new InteropAssemblyMetadata(Directory.EnumerateFiles(options.UnityBaseLibsDir, "*.dll")));
                RunPass("Creating rewrite assemblies", () => context = new InteropRewriteContext(options, gameAssemblies, unityAssemblies, scanState));

                InteropTypeNames typeNames = null;
                RunPass("Computing renames", () => typeNames = InteropTypeNames.Create(context));
                RunPass("Creating typedefs", () => InteropTypeGeneration.CreateTypes(context, typeNames));
                RunPass("Computing struct blittability", () => InteropTypeGeneration.ComputeTypeSpecifics(context));
                RunPass("Filling typedefs", () => InteropTypeGeneration.FillTypes(context));
                RunPass("Filling generic constraints", () => InteropTypeGeneration.FillGenericConstraints(context));
                RunPass("Creating members", () => InteropTypeGeneration.CreateMembers(context));
                RunPass("Scanning method cross-references", () => InteropMethodScanner.Scan(context, options, scanState));
                RunPass("Finalizing method declarations", () => InteropMethodDeclarations.FinalizeMethods(context, scanState.Callers));
                RunPass("Filling method parameters", () => InteropMethodDeclarations.CopyParameters(context));
                RunPass("Creating static constructors", () => InteropConstructorGeneration.GenerateStaticConstructors(context));
                RunPass("Creating value type fields", () => InteropFieldGeneration.GenerateValueTypeFields(context));
                RunPass("Creating enums", () => InteropTypeGeneration.GenerateEnums(context));
                RunPass("Creating pointer constructors", () => InteropConstructorGeneration.GeneratePointerConstructors(context));
                RunPass("Creating non-blittable struct constructors", () => InteropConstructorGeneration.GenerateNonBlittableConstructors(context));
                RunPass("Creating generic method static constructors", () => InteropConstructorGeneration.GenerateGenericMethodStores(context));
                RunPass("Creating field accessors", () => InteropFieldGeneration.GenerateAccessors(context));
                RunPass("Filling methods", () => InteropMethodBodies.Generate(context));
                RunPass("Generating implicit conversions", () => InteropConversions.Generate(context));
                RunPass("Implementing awaiters", () => InteropAwaiterGeneration.Generate(context));
                RunPass("Creating properties", () => InteropPropertyGeneration.Generate(context));

                if (options.UnityBaseLibsDir != null)
                {
                    RunPass("Unstripping types", () => InteropTypeRestoration.Restore(context));
                    RunPass("Unstripping fields", () => InteropFieldRestoration.Restore(context));
                    var restoration = new InteropMethodRestoration();
                    RunPass("Unstripping methods", () => restoration.Restore(context));
                    RunPass("Unstripping method bodies", restoration.FillBodies);
                }
                else
                {
                    InteropGenerationDiagnostics.Log(LogLevel.Warning, "Not performing unstripping because Unity libraries are not specified.");
                }

                RunPass("Writing xref cache", () => InteropMethodDatabases.WriteXrefs(context, options,
                    scanState.Callers, scanState.MetadataInitRva));
                RunPass("Writing assemblies", () => InteropAssemblyWriter.Write(context, options));
                RunPass("Writing method pointer map", () => InteropMethodDatabases.WriteMethodMap(context, options));
            }
            catch (Exception error)
            {
                failure ??= ExceptionDispatchInfo.Capture(error);
            }

            scanState.Clear(ref failure);
            InteropConversion.Cleanup(ref failure, "dispose interop rewrite context", () =>
            {
                if (context != null)
                    context.Dispose();
                else
                    unityAssemblies?.Dispose();
            });
            InteropConversion.Cleanup(ref failure, "dispose interop source metadata", () => gameAssemblies?.Dispose());
            failure?.Throw();
        }

        private static void RunPass(string name, Action action)
        {
            InteropGenerationDiagnostics.Log(LogLevel.Info, name);
            var stopwatch = Stopwatch.StartNew();
            action();
            InteropGenerationDiagnostics.Log(LogLevel.Debug, name + " completed in " + stopwatch.Elapsed + ".");
        }
    }
}
