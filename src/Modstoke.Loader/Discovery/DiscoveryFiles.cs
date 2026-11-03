// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;

namespace Modstoke.Loader
{
    internal static class DiscoveryFiles
    {
        internal static IEnumerable<string> EnumerateFiles(string directory, string pattern, SearchOption option, Action<string, Exception> report)
        {
            if (option != SearchOption.TopDirectoryOnly && option != SearchOption.AllDirectories)
            {
                Report(directory, new ArgumentOutOfRangeException(nameof(option)), report);
                yield break;
            }

            var pending = new Queue<DiscoveryDirectory>();
            pending.Enqueue(new DiscoveryDirectory(directory, directory, null));
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                if (option == SearchOption.AllDirectories && !TryResolveDirectory(current, report))
                    continue;

                foreach (var file in ReadEntries(current.Path, pattern, false, report))
                    yield return file;

                if (option != SearchOption.AllDirectories)
                    continue;

                foreach (var child in GetDirectories(current.Path, report))
                    pending.Enqueue(new DiscoveryDirectory(child,
                        Path.Combine(current.Identity, Path.GetFileName(child)), current));
            }
        }

        private static string[] ReadEntries(string directory, string pattern, bool directories, Action<string, Exception> report)
        {
            var entries = new List<string>();
            IEnumerator<string> iterator = null;
            try
            {
                iterator = (directories
                    ? Directory.EnumerateDirectories(directory, pattern, SearchOption.TopDirectoryOnly)
                    : Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)).GetEnumerator();
                while (iterator.MoveNext())
                    entries.Add(iterator.Current);
            }
            catch (Exception ex)
            {
                Report(directory, ex, report);
            }
            finally
            {
                try { iterator?.Dispose(); }
                catch (Exception ex) { Report(directory, ex, report); }
            }

            entries.Sort((left, right) =>
            {
                var order = StringComparer.OrdinalIgnoreCase.Compare(left, right);
                return order != 0 ? order : StringComparer.Ordinal.Compare(left, right);
            });
            return entries.ToArray();
        }

        internal static string[] GetDirectories(string directory, Action<string, Exception> report)
        {
            return ReadEntries(directory, "*", true, report);
        }

        private static bool TryResolveDirectory(DiscoveryDirectory directory, Action<string, Exception> report)
        {
            try
            {
                var info = new DirectoryInfo(directory.Identity);
                var target = (info.Attributes & FileAttributes.ReparsePoint) != 0
                    ? info.ResolveLinkTarget(true) : null;
                directory.Identity = Path.TrimEndingDirectorySeparator(target?.FullName ?? info.FullName);
                for (var ancestor = directory.Parent; ancestor != null; ancestor = ancestor.Parent)
                {
                    if (string.Equals(ancestor.Identity, directory.Identity, StringComparison.OrdinalIgnoreCase))
                    {
                        Report(directory.Path, new IOException("A directory link points to an ancestor of this discovery path."), report);
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Report(directory.Path, ex, report);
                return false;
            }
        }

        internal static IEnumerable<string> GetPluginFiles(string root, Action<string, Exception> report)
        {
            foreach (var file in EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly, report))
                yield return file;

            // Each recursive branch is isolated, including branches within one package.
            foreach (var directory in GetDirectories(root, report))
            {
                foreach (var file in EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories, report))
                    yield return file;
            }
        }

        private static void Report(string path, Exception error, Action<string, Exception> report)
        {
            try { report?.Invoke(path, error); }
            catch { /* Reporting must not turn a local discovery failure into a startup failure. */ }
        }

        private sealed class DiscoveryDirectory
        {
            internal DiscoveryDirectory(string path, string identity, DiscoveryDirectory parent)
            {
                Path = path;
                Identity = identity;
                Parent = parent;
            }

            internal string Path { get; }
            internal string Identity { get; set; }
            internal DiscoveryDirectory Parent { get; }
        }
    }
}
