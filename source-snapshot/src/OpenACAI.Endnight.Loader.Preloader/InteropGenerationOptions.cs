// SPDX-License-Identifier: GPL-3.0-or-later
// Options and rename-map parsing adapted from Il2CppInterop GeneratorOptions (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using AsmResolver.DotNet;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class InteropGenerationOptions
    {
        internal List<AssemblyDefinition> Source { get; set; }
        internal string OutputDir { get; set; }
        internal string UnityBaseLibsDir { get; set; }
        internal string GameAssemblyPath { get; set; }
        internal bool Verbose { get; set; }
        internal bool NoXrefCache { get; set; }
        internal bool PassthroughNames { get; set; }
        internal bool Parallel { get; set; } = true;
        internal Regex ObfuscatedNamesRegex { get; set; }
        internal int TypeDeobfuscationCharsPerUniquifier { get; set; } = 2;
        internal int TypeDeobfuscationMaxUniquifiers { get; set; } = 10;
        internal PrefixMode Il2CppPrefixMode { get; set; } = PrefixMode.OptIn;
        internal List<string> AdditionalAssembliesBlacklist { get; } = new();
        internal Dictionary<string, string> RenameMap { get; } = new();
        internal HashSet<string> NamespacesAndAssembliesToPrefix { get; } =
            new() { "System", "mscorlib", "Microsoft", "Mono", "I18N" };
        internal HashSet<string> NamespacesAndAssembliesToNotPrefix { get; } =
            new() { "Assembly-CSharp", "Unity" };

        internal void ReadRenameMap(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            if (path.EndsWith(".gz"))
            {
                using var gzip = new GZipStream(stream, CompressionMode.Decompress, true);
                ReadRenameMap(gzip);
            }
            else
                ReadRenameMap(stream);
        }

        private void ReadRenameMap(Stream stream)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 65536, true);
            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                    continue;
                var fields = line.Split(';');
                if (fields.Length >= 2)
                    RenameMap[fields[0]] = fields[1];
            }
        }

        internal enum PrefixMode
        {
            OptIn,
            OptOut
        }
    }
}
