// SPDX-License-Identifier: GPL-3.0-or-later
// Conversion orchestration adapted from BepInEx's Il2CppInteropManager, copyright BepInEx contributors.
// Interop generation adapted from Il2CppInterop, copyright Il2CppInterop contributors (LGPL-3.0).
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using AsmResolver.DotNet;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.Common;
using Cpp2IL.Core;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.ProcessingLayers;
using LibCpp2IL;
using LibCpp2IL.Logging;
using CppLogger = Cpp2IL.Core.Logging.Logger;
using LoaderLogger = BepInEx.Logging.Logger;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class InteropConversion
    {
        static InteropConversion()
        {
            RegisterX86IfAbsent(DefaultInstructionSets.X86_32);
            RegisterX86IfAbsent(DefaultInstructionSets.X86_64);
            LibCpp2IlBinaryRegistry.RegisterBuiltInBinarySupport();
        }

        internal static void Generate(InteropSettings settings, string outputDirectory)
        {
            // Base libraries must already be extracted before Cecil receives its platform list.
            if (Directory.Exists(settings.BaseLibraryDirectory))
            {
                const string trustedAssemblies = "TRUSTED_PLATFORM_ASSEMBLIES";
                var current = AppDomain.CurrentDomain.GetData(trustedAssemblies) as string ?? "";
                var paths = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Concat(Directory.EnumerateFiles(settings.BaseLibraryDirectory, "*.dll"))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                AppDomain.CurrentDomain.SetData(trustedAssemblies, string.Join(Path.PathSeparator, paths));
            }

            var assemblies = BuildDummyAssemblies(settings);
            if (assemblies == null || assemblies.Count == 0)
                throw new InvalidDataException("Cpp2IL produced no source assemblies.");

            if (settings.DumpDummyAssemblies)
            {
                var dummyDirectory = Path.Combine(Paths.BepInExRootPath, "dummy");
                Directory.CreateDirectory(dummyDirectory);
                foreach (var assembly in assemblies)
                    assembly.Write(Path.Combine(dummyDirectory, assembly.Name + ".dll"));
            }

            var options = new InteropGenerationOptions
            {
                GameAssemblyPath = settings.ScanMethodRefs ? settings.GameAssemblyPath : null,
                Source = assemblies,
                OutputDir = outputDirectory,
                UnityBaseLibsDir = Directory.Exists(settings.BaseLibraryDirectory) ? settings.BaseLibraryDirectory : null,
                ObfuscatedNamesRegex = !string.IsNullOrEmpty(settings.ObfuscationRegex)
                    ? new Regex(settings.ObfuscationRegex)
                    : null
            };

            if (File.Exists(settings.RenameMapPath))
            {
                InteropGenerationDiagnostics.Log(LogLevel.Info, "Parsing deobfuscation rename mappings");
                options.ReadRenameMap(settings.RenameMapPath);
            }

            InteropGenerationDiagnostics.Log(LogLevel.Info, "Generating interop assemblies");
            InteropAssemblyGeneration.Run(options);
        }

        private static void RegisterX86IfAbsent(InstructionSetId id)
        {
            try
            {
                InstructionSetRegistry.GetInstructionSet(id);
            }
            catch (KeyNotFoundException)
            {
                InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(id);
            }
        }

        private static List<AssemblyDefinition> BuildDummyAssemblies(InteropSettings settings)
        {
            InteropGenerationDiagnostics.Log(LogLevel.Message, "Running Cpp2IL to generate dummy assemblies from " + settings.MetadataPath);
            var stopwatch = Stopwatch.StartNew();
            var logSource = LoaderLogger.CreateLogSource("Cpp2IL");
            CppLogger.LogEvent verbose = (message, source) =>
                InteropGenerationDiagnostics.Log(LogLevel.Debug, $"[{source}] {message?.Trim()}", source: logSource);
            CppLogger.LogEvent info = (message, source) =>
                InteropGenerationDiagnostics.Log(LogLevel.Info, $"[{source}] {message?.Trim()}", source: logSource);
            CppLogger.LogEvent warning = (message, source) =>
                InteropGenerationDiagnostics.Log(LogLevel.Warning, $"[{source}] {message?.Trim()}", source: logSource);
            CppLogger.LogEvent error = (message, source) =>
                InteropGenerationDiagnostics.Log(LogLevel.Error, $"[{source}] {message?.Trim()}", source: logSource);
            var previousWriter = LibLogger.Writer;
            List<AssemblyDefinition> assemblies = null;
            ExceptionDispatchInfo failure = null;
            try
            {
                CppLogger.VerboseLog += verbose;
                CppLogger.InfoLog += info;
                CppLogger.WarningLog += warning;
                CppLogger.ErrorLog += error;

                Cpp2IlApi.InitializeLibCpp2Il(settings.GameAssemblyPath, settings.MetadataPath, UnityInfo.Version, false);
                var processingLayers = new List<Cpp2IlProcessingLayer> { new AttributeInjectorProcessingLayer() };
                foreach (var layer in processingLayers)
                    layer.PreProcess(Cpp2IlApi.CurrentAppContext, processingLayers);
                foreach (var layer in processingLayers)
                    layer.Process(Cpp2IlApi.CurrentAppContext);

                assemblies = new AsmResolverDllOutputFormatDefault().BuildAssemblies(Cpp2IlApi.CurrentAppContext);
            }
            catch (Exception conversionError)
            {
                failure = ExceptionDispatchInfo.Capture(conversionError);
            }

            // Attempt every owned cleanup step while retaining the first failure and its stack.
            Cleanup(ref failure, "reset Cpp2IL analysis state", () => Cpp2IlApi.ResetInternalState());
            Cleanup(ref failure, "restore LibCpp2IL log writer", () => LibLogger.Writer = previousWriter);
            Cleanup(ref failure, "detach Cpp2IL verbose logger", () => CppLogger.VerboseLog -= verbose);
            Cleanup(ref failure, "detach Cpp2IL info logger", () => CppLogger.InfoLog -= info);
            Cleanup(ref failure, "detach Cpp2IL warning logger", () => CppLogger.WarningLog -= warning);
            Cleanup(ref failure, "detach Cpp2IL error logger", () => CppLogger.ErrorLog -= error);
            Cleanup(ref failure, "remove Cpp2IL log source", () => LoaderLogger.Sources.Remove(logSource));
            Cleanup(ref failure, "dispose Cpp2IL log source", logSource.Dispose);
            failure?.Throw();

            stopwatch.Stop();
            InteropGenerationDiagnostics.Log(LogLevel.Info, $"Cpp2IL finished in {stopwatch.Elapsed}");
            return assemblies;
        }

        internal static void Cleanup(ref ExceptionDispatchInfo failure, string operation, Action cleanup)
        {
            try
            {
                cleanup();
            }
            catch (Exception error)
            {
                failure ??= ExceptionDispatchInfo.Capture(error);
                InteropGenerationDiagnostics.Log(LogLevel.Warning, "Interop conversion cleanup failed: " + operation, error);
            }
        }
    }
}
