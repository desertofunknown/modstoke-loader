// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
// Database layouts follow Il2CppInterop.Common.Maps (Il2CppInterop contributors, LGPL-3.0).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Il2CppInterop.Common.Maps;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class InteropOutputValidator
    {
        internal static string[] Validate(string directory)
        {
            var methodsByAssembly = new Dictionary<string, int>(StringComparer.Ordinal);
            var files = Directory.GetFiles(directory, "*.dll");
            var xrefRecordCount = ValidateXrefCache(Path.Combine(directory, MethodXrefScanCache.FileName));
            foreach (var file in files)
            {
                var name = AssemblyName.GetAssemblyName(file);
                if (!string.Equals(name.Name, Path.GetFileNameWithoutExtension(file), StringComparison.Ordinal))
                    throw new InvalidDataException("Unexpected interop assembly identity: " + file);
                using var stream = File.OpenRead(file);
                using var pe = new PEReader(stream);
                var metadata = pe.GetMetadataReader();
                methodsByAssembly.Add(name.FullName, metadata.GetTableRowCount(TableIndex.MethodDef));
                InteropXrefAttributeValidator.Validate(metadata, xrefRecordCount, file);
            }
            foreach (var required in new[] { "Il2Cppmscorlib.dll", "UnityEngine.CoreModule.dll" })
                if (!files.Any(path => string.Equals(Path.GetFileName(path), required, StringComparison.Ordinal)))
                    throw new FileNotFoundException("Required interop wrapper is missing: " + required, Path.Combine(directory, required));

            ValidateAddressMap(Path.Combine(directory, MethodAddressToTokenMapBase<Assembly, MethodBase>.FileName), methodsByAssembly);
            return files.Select(Path.GetFileName)
                .Concat(new[] { MethodAddressToTokenMapBase<Assembly, MethodBase>.FileName, MethodXrefScanCache.FileName })
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }

        private static void ValidateAddressMap(string path, Dictionary<string, int> methodsByAssembly)
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            var stream = reader.BaseStream;
            if (reader.ReadInt32() != MethodAddressToTokenMapBase<Assembly, MethodBase>.Magic
                || reader.ReadInt32() != MethodAddressToTokenMapBase<Assembly, MethodBase>.Version)
                throw new InvalidDataException("Unsupported method address database: " + path);
            var assemblyCount = reader.ReadInt32();
            var methodCount = reader.ReadInt32();
            var dataOffset = reader.ReadInt32();
            // The consumer computes methodCount * 8 as Int32 and reads a first key even for an empty map.
            if (assemblyCount < 1 || assemblyCount > methodsByAssembly.Count || methodCount < 1 || methodCount > int.MaxValue / 8
                || dataOffset < stream.Position || dataOffset + 16L * methodCount != stream.Length)
                throw new InvalidDataException("Invalid method address database bounds: " + path);

            var methodCounts = new int[assemblyCount];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < assemblyCount; i++)
            {
                var name = reader.ReadString();
                if (stream.Position > dataOffset || !seen.Add(name) || !methodsByAssembly.TryGetValue(name, out methodCounts[i]))
                    throw new InvalidDataException("The method address database does not match its generated assemblies: " + name);
            }
            if (stream.Position != dataOffset || seen.Count != methodsByAssembly.Count)
                throw new InvalidDataException("The generated DLL set and method address database disagree.");

            // Runtime lookup uses signed binary search; shared methods can have equal addresses.
            var previousAddress = long.MinValue;
            for (var i = 0; i < methodCount; i++)
            {
                var address = reader.ReadInt64();
                if (address < previousAddress)
                    throw new InvalidDataException("Method addresses are not sorted in interop database: " + path);
                previousAddress = address;
            }

            for (var i = 0; i < methodCount; i++)
            {
                var token = reader.ReadInt32();
                var assemblyIndex = reader.ReadInt32();
                var row = token & 0x00ffffff;
                if (assemblyIndex < 0 || assemblyIndex >= methodCounts.Length || (token & 0xff000000) != 0x06000000
                    || row == 0 || row > methodCounts[assemblyIndex])
                    throw new InvalidDataException("Invalid method token in interop database: " + path);
            }
        }

        private static long ValidateXrefCache(string path)
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            var headerSize = Marshal.SizeOf<MethodXrefScanCache.FileHeader>();
            var recordSize = Marshal.SizeOf<MethodXrefScanCache.MethodData>();
            if (reader.BaseStream.Length < headerSize || (reader.BaseStream.Length - headerSize) % recordSize != 0
                || reader.ReadInt32() != MethodXrefScanCache.Magic || reader.ReadInt32() != MethodXrefScanCache.Version)
                throw new InvalidDataException("Invalid interop xref database: " + path);
            return (reader.BaseStream.Length - headerSize) / recordSize;
        }
    }
}
