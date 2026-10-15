// SPDX-License-Identifier: GPL-3.0-or-later
// Search precedence follows BepInEx Utility's compatibility assembly lookup.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class AssemblySearch
    {
        internal static T FindAndLoad<T>(AssemblyName name, string directory, Func<string, T> load) where T : class
        {
            if (!Directory.Exists(directory))
                return null;

            foreach (var path in EnumeratePaths(name.Name, directory))
            {
                if (!File.Exists(path))
                    continue;

                try
                {
                    var assembly = load(path);
                    if (assembly != null)
                        return assembly;
                }
                catch (Exception)
                {
                    // A rejected image does not prevent probing the remaining candidates.
                }
            }
            return null;
        }

        private static IEnumerable<string> EnumeratePaths(string name, string directory)
        {
            yield return Path.Combine(directory, name + ".dll");
            yield return Path.Combine(directory, name + ".exe");

            // Probe the root before traversing lower-priority directories.
            foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
            {
                yield return Path.Combine(child, name + ".dll");
                yield return Path.Combine(child, name + ".exe");
            }
        }
    }
}
