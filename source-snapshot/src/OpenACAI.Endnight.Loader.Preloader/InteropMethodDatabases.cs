// SPDX-License-Identifier: GPL-3.0-or-later
// Database generation adapted from Il2CppInterop Pass89, Pass91 and WriterEx (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using Il2CppInterop.Common.Attributes;
using Il2CppInterop.Common.Maps;
using Il2CppInterop.Common.XrefScans;
using Assembly = System.Reflection.Assembly;
using MethodBase = System.Reflection.MethodBase;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class InteropMethodDatabases
    {
        [ThreadStatic] private static byte[] recordBuffer;

        internal static void WriteXrefs(InteropRewriteContext context, InteropGenerationOptions options,
            IDictionary<long, List<XrefInstance>> callers, long metadataInitRva)
        {
            var records = new List<MethodXrefScanCache.MethodData>();
            var rangesByAddress = new Dictionary<long, CachedScanResultsAttribute>();
            if (!options.NoXrefCache)
            {
                foreach (var assembly in context.Assemblies)
                {
                    if (options.AdditionalAssembliesBlacklist.Contains(assembly.NewAssembly.Name))
                        continue;

                    foreach (var type in assembly.Types)
                    foreach (var method in type.Methods)
                    {
                        if (!rangesByAddress.TryGetValue(method.Rva, out var ranges))
                        {
                            var xrefStart = records.Count;
                            foreach (var reference in method.XrefScanResults)
                                records.Add(MethodXrefScanCache.MethodData.FromXrefInstance(reference));
                            var xrefEnd = records.Count;
                            var refStart = 0;
                            var refEnd = 0;
                            if (method.Rva != 0 && callers.TryGetValue(method.Rva, out var methodCallers))
                            {
                                refStart = records.Count;
                                foreach (var reference in methodCallers)
                                    records.Add(MethodXrefScanCache.MethodData.FromXrefInstance(reference));
                                refEnd = records.Count;
                            }
                            if (xrefStart == xrefEnd && refStart == refEnd)
                                continue;

                            ranges = new CachedScanResultsAttribute
                            {
                                RefRangeStart = refStart,
                                RefRangeEnd = refEnd,
                                XrefRangeStart = xrefStart,
                                XrefRangeEnd = xrefEnd,
                                MetadataInitTokenRva = method.MetadataInitTokenRva,
                                MetadataInitFlagRva = method.MetadataInitFlagRva
                            };
                            rangesByAddress.Add(method.Rva, ranges);
                        }

                        // Aliases share the first recorded body's outgoing and caller ranges independently.
                        method.NewMethod.CustomAttributes.Add(CreateAttribute(assembly, ranges));
                    }
                }
            }

            var header = new MethodXrefScanCache.FileHeader
            {
                Magic = MethodXrefScanCache.Magic,
                Version = MethodXrefScanCache.Version,
                InitMethodMetadataRva = metadataInitRva
            };
            var path = Path.Combine(options.OutputDir, MethodXrefScanCache.FileName);
            using var writer = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write), Encoding.UTF8, false);
            WriteRecord(writer, header);
            foreach (var record in records)
                WriteRecord(writer, record);

            if (options.Verbose)
            {
                using var text = new StreamWriter(path + ".txt");
                for (var i = 0; i < records.Count; i++)
                    text.WriteLine($"{i}\t{records[i].Type}\t{records[i].Address}\t{records[i].FoundAt}");
            }
        }

        internal static void WriteMethodMap(InteropRewriteContext context, InteropGenerationOptions options)
        {
            var methods = new List<(long Address, int Token, int AssemblyIndex)>();
            var assemblies = new List<string>();
            foreach (var assembly in context.Assemblies)
            {
                if (options.AdditionalAssembliesBlacklist.Contains(assembly.NewAssembly.Name))
                    continue;
                assemblies.Add(assembly.NewAssembly.FullName);
                foreach (var type in assembly.Types)
                foreach (var method in type.Methods)
                    if (method.Rva != 0)
                        methods.Add((method.Rva, method.NewMethod.MetadataToken.ToInt32(), assemblies.Count - 1));
            }

            // Preserve signed address-only sorting, including duplicate keys for shared native bodies.
            methods.Sort((first, second) => first.Address.CompareTo(second.Address));
            var header = new MethodAddressToTokenMapFileHeader
            {
                Magic = MethodAddressToTokenMapBase<Assembly, MethodBase>.Magic,
                Version = MethodAddressToTokenMapBase<Assembly, MethodBase>.Version,
                NumAssemblies = assemblies.Count,
                NumMethods = methods.Count
            };
            var path = Path.Combine(options.OutputDir, MethodAddressToTokenMapBase<Assembly, MethodBase>.FileName);
            using var writer = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write), Encoding.UTF8, false);
            WriteRecord(writer, header);
            foreach (var assembly in assemblies)
                writer.Write(assembly);

            header.DataOffset = checked((int)writer.BaseStream.Position);
            foreach (var method in methods)
                writer.Write(method.Address);
            foreach (var method in methods)
            {
                writer.Write(method.Token);
                writer.Write(method.AssemblyIndex);
            }
            writer.BaseStream.Position = 0;
            WriteRecord(writer, header);

            if (options.Verbose)
            {
                using var text = new StreamWriter(path + ".txt");
                for (var i = 0; i < methods.Count; i++)
                    text.WriteLine($"{i}\t{methods[i].Address}\t{methods[i].Token}\t{methods[i].AssemblyIndex}");
            }
        }

        private static unsafe void WriteRecord<T>(BinaryWriter writer, T value) where T : unmanaged
        {
            var size = Marshal.SizeOf<T>();
            if (recordBuffer == null || recordBuffer.Length < size)
                recordBuffer = new byte[size];

            fixed (byte* bytes = recordBuffer)
                *(T*)bytes = value;
            writer.Write(recordBuffer, 0, size);
        }

        private static CustomAttribute CreateAttribute(InteropAssemblyContext assembly, CachedScanResultsAttribute ranges)
        {
            var imports = assembly.Imports;
            var intType = imports.Module.CorLibTypeFactory.Int32;
            var longType = imports.Module.CorLibTypeFactory.Int64;
            var fields = new[]
            {
                Field(nameof(CachedScanResultsAttribute.RefRangeStart), intType, ranges.RefRangeStart),
                Field(nameof(CachedScanResultsAttribute.RefRangeEnd), intType, ranges.RefRangeEnd),
                Field(nameof(CachedScanResultsAttribute.XrefRangeStart), intType, ranges.XrefRangeStart),
                Field(nameof(CachedScanResultsAttribute.XrefRangeEnd), intType, ranges.XrefRangeEnd),
                Field(nameof(CachedScanResultsAttribute.MetadataInitTokenRva), longType, ranges.MetadataInitTokenRva),
                Field(nameof(CachedScanResultsAttribute.MetadataInitFlagRva), longType, ranges.MetadataInitFlagRva)
            };
            return new CustomAttribute((ICustomAttributeType)imports.CachedScanResultsAttributector.Value,
                new CustomAttributeSignature(Array.Empty<CustomAttributeArgument>(), fields));
        }

        private static CustomAttributeNamedArgument Field(string name, TypeSignature type, object value) =>
            new(CustomAttributeArgumentMemberType.Field, name, type, new CustomAttributeArgument(type, value));
    }
}
