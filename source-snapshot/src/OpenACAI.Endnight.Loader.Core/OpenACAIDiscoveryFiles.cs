// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;

namespace OpenACAI.Endnight.Loader
{
    internal static class OpenACAIDiscoveryFiles
    {
        internal static IEnumerable<string> EnumerateFiles(string directory, string pattern, SearchOption option, Action<string, Exception> report)
        {
            IEnumerator<string> files = null;
            try
            {
                files = Directory.EnumerateFiles(directory, pattern, option).GetEnumerator();
            }
            catch (Exception ex)
            {
                Report(directory, ex, report);
            }
            if (files == null)
                yield break;

            try
            {
                while (true)
                {
                    string path;
                    try
                    {
                        if (!files.MoveNext())
                            break;
                        path = files.Current;
                    }
                    catch (Exception ex)
                    {
                        Report(directory, ex, report);
                        break;
                    }
                    yield return path;
                }
            }
            finally
            {
                try { files.Dispose(); }
                catch (Exception ex) { Report(directory, ex, report); }
            }
        }

        internal static string[] GetDirectories(string directory, Action<string, Exception> report)
        {
            try
            {
                return Directory.GetDirectories(directory);
            }
            catch (Exception ex)
            {
                Report(directory, ex, report);
                return Array.Empty<string>();
            }
        }

        internal static IEnumerable<string> GetPluginFiles(string root, Action<string, Exception> report)
        {
            foreach (var file in EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly, report))
                yield return file;

            // A failed recursive package scan must not discard another package's DLLs.
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
    }
}
