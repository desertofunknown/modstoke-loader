// SPDX-License-Identifier: GPL-3.0-or-later
// Search precedence follows BepInEx Utility's compatibility assembly lookup.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Logging;

namespace Modstoke.Preloader
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
            var dllName = name + ".dll";
            yield return Path.Combine(directory, dllName);
            var exeName = name + ".exe";
            yield return Path.Combine(directory, exeName);

            foreach (var path in EnumerateDirectories(directory))
            {
                if (path == directory)
                    continue;
                yield return Path.Combine(path, dllName);
                yield return Path.Combine(path, exeName);
            }
        }

        internal static IEnumerable<string> EnumerateDirectories(string directory,
            Action<string, Exception> reportFailure = null)
        {
            // Keep root-first breadth order, but isolate failures to the affected branch.
            var pending = new Queue<SearchDirectory>();
            pending.Enqueue(new SearchDirectory(directory, directory, null));
            while (pending.TryDequeue(out var current))
            {
                if (!TryResolveDirectory(current, reportFailure))
                    continue;

                yield return current.Path;

                foreach (var child in GetChildDirectories(current.Path, reportFailure))
                    pending.Enqueue(new SearchDirectory(child,
                        Path.Combine(current.Identity, Path.GetFileName(child)), current));
            }
        }

        private static string[] GetChildDirectories(string directory, Action<string, Exception> reportFailure)
        {
            try
            {
                return Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                ReportDirectoryFailure(directory, error, reportFailure);
                return Array.Empty<string>();
            }
        }

        private static bool TryResolveDirectory(SearchDirectory directory, Action<string, Exception> reportFailure)
        {
            try
            {
                var info = new DirectoryInfo(directory.Identity);
                var target = (info.Attributes & FileAttributes.ReparsePoint) != 0
                    ? info.ResolveLinkTarget(true) : null;
                directory.Identity = Path.TrimEndingDirectorySeparator(target?.FullName ?? info.FullName);
                for (var ancestor = directory.Parent; ancestor != null; ancestor = ancestor.Parent)
                {
                    if (!string.Equals(ancestor.Identity, directory.Identity, StringComparison.OrdinalIgnoreCase))
                        continue;
                    ReportDirectoryFailure(directory.Path,
                        new IOException("Skipping assembly directory link to an ancestor."), reportFailure);
                    return false;
                }
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                ReportDirectoryFailure(directory.Path, error, reportFailure);
                return false;
            }
        }

        private static void ReportDirectoryFailure(string directory, Exception error,
            Action<string, Exception> reportFailure)
        {
            if (reportFailure == null)
                InteropGenerationDiagnostics.Log(LogLevel.Debug, "Could not search assembly directory: " + directory, error);
            else
                AssemblyPatcherDiagnostics.Write(() => reportFailure(directory, error));
        }

        private sealed class SearchDirectory
        {
            internal SearchDirectory(string path, string identity, SearchDirectory parent)
            {
                Path = path;
                Identity = identity;
                Parent = parent;
            }

            internal string Path { get; }
            internal string Identity { get; set; }
            internal SearchDirectory Parent { get; }
        }
    }
}
