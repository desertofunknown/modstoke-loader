// SPDX-License-Identifier: GPL-3.0-or-later
// Name uniquification adapted from Il2CppInterop UniquificationContext (LGPL-3.0).
// Copyright (c) Il2CppInterop contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class InteropNameUniquifier
    {
        private readonly InteropGenerationOptions options;
        private readonly SortedSet<(string Name, float Score)> prefixes = new(new ScoreComparer());
        private readonly Dictionary<string, int> occurrences = new();

        internal InteropNameUniquifier(InteropGenerationOptions options)
        {
            this.options = options;
        }

        internal bool CheckFull()
        {
            return occurrences.Count >= options.TypeDeobfuscationMaxUniquifiers;
        }

        internal void Push(string name, bool noSubstring = false)
        {
            if (InteropNaming.IsInvalidInSource(name)) return;

            var prefix = noSubstring
                ? name
                : name.Substring(0, Math.Min(options.TypeDeobfuscationCharsPerUniquifier, name.Length));
            if (!occurrences.TryGetValue(prefix, out var previousCount))
                occurrences[prefix] = previousCount = 0;
            var currentCount = occurrences[prefix] = previousCount + 1;
            prefixes.Add((prefix, occurrences.Count + currentCount * 2 + prefixes.Count / 100f));
        }

        internal void Push(List<string> names, bool noSubstring = false)
        {
            foreach (var name in names)
                Push(name, noSubstring);
        }

        internal string GetTop()
        {
            return string.Join("",
                prefixes.Take(options.TypeDeobfuscationMaxUniquifiers).Select(entry => entry.Name));
        }

        private sealed class ScoreComparer : IComparer<(string Name, float Score)>
        {
            // Equal scores intentionally retain the existing entry, regardless of name.
            public int Compare((string Name, float Score) x, (string Name, float Score) y) =>
                x.Score.CompareTo(y.Score);
        }
    }
}
