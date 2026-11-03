// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace Modstoke.Bootstrap
{
    internal static class BootstrapAssemblies
    {
        internal static Assembly FindLoaded(string assemblyName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
        }

        // Returns the loaded copy, or the first <name>.dll found in order; null when none exists.
        internal static Assembly FindOrLoad(string assemblyName, IEnumerable<string> directories)
        {
            var loaded = FindLoaded(assemblyName);
            if (loaded != null)
            {
                return loaded;
            }

            foreach (var directory in directories)
            {
                var candidate = Path.Combine(directory, assemblyName + ".dll");
                if (!File.Exists(candidate))
                {
                    continue;
                }

                try
                {
                    return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
                }
                catch (FileLoadException)
                {
                    // Another resolver may have loaded the same identity from a different path.
                    loaded = FindLoaded(assemblyName);
                    if (loaded != null)
                    {
                        return loaded;
                    }

                    throw;
                }
            }

            return null;
        }
    }
}
