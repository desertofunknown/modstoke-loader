// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// The game-root Modstoke folder and the move out of the old OpenACAI folder.
// RedLoader's own <root>\Mods and <root>\UserData are a different loader and are never touched.

using System;
using System.Collections.Generic;
using System.IO;

namespace Modstoke.Bootstrap
{
    internal static class RootLayout
    {
        internal const string RootDirectoryName = "Modstoke";
        internal const string LegacyRootDirectoryName = "OpenACAI";
        private static readonly string[] MigratedDirectories = { "Mods", "UserData" };

        // Doorstop loads this assembly from the runtime folder, wherever that folder is.
        internal static string RuntimeDirectory(string gameRoot)
        {
            var directory = Path.GetDirectoryName(typeof(RootLayout).Assembly.Location);
            return string.IsNullOrWhiteSpace(directory)
                ? Path.Combine(gameRoot, RootDirectoryName, "Runtime")
                : Path.GetFullPath(directory);
        }

        // Data left behind by a failed move still wins over an empty new folder.
        internal static string DataDirectory(string gameRoot, string name)
        {
            var current = Path.Combine(gameRoot, RootDirectoryName, name);
            if (Directory.Exists(current))
                return current;
            var legacy = Path.Combine(gameRoot, LegacyRootDirectoryName, name);
            return Directory.Exists(legacy) ? legacy : current;
        }

        // Launchers may still pass an OpenACAI path that Migrate has since moved.
        internal static string MapMovedLegacyPath(string gameRoot, string path)
        {
            var legacyRoot = Path.GetFullPath(Path.Combine(gameRoot, LegacyRootDirectoryName));
            var fullPath = Path.GetFullPath(path);
            if (Directory.Exists(fullPath) || File.Exists(fullPath)
                || !fullPath.StartsWith(legacyRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return path;
            var moved = Path.Combine(gameRoot, RootDirectoryName, Path.GetRelativePath(legacyRoot, fullPath));
            return Directory.Exists(moved) || File.Exists(moved) ? moved : path;
        }

        internal static MigrationResult Migrate(string gameRoot, Action<string> trace)
        {
            var result = new MigrationResult();
            var legacyRoot = Path.Combine(gameRoot, LegacyRootDirectoryName);
            if (!Directory.Exists(legacyRoot))
                return result;

            foreach (var name in MigratedDirectories)
            {
                var source = Path.Combine(legacyRoot, name);
                if (!Directory.Exists(source))
                    continue;

                var destination = Path.Combine(gameRoot, RootDirectoryName, name);
                try
                {
                    Directory.CreateDirectory(destination);
                    foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
                        MoveEntry(entry, Path.Combine(destination, entry.Name), result, trace);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    result.Failed++;
                    trace("Modstoke could not migrate " + source + ": " + error.Message);
                }

                TryRemoveEmpty(source, result, trace);
                // An empty new folder would hide whatever could not be moved.
                if (Directory.Exists(source))
                    TryRemoveEmpty(destination, result, trace);
            }

            var legacyRuntime = Path.Combine(legacyRoot, "Runtime");
            if (Directory.Exists(legacyRuntime))
            {
                result.LegacyRuntimeLeft = true;
                trace("Modstoke no longer uses " + legacyRuntime + "; it can be deleted.");
            }

            TryRemoveEmpty(legacyRoot, result, trace);
            result.LegacyRootKept = Directory.Exists(legacyRoot);
            trace("Modstoke folder migration: " + result.Moved + " moved, " + result.Clashes + " left by name clash, "
                + result.Failed + " failed" + (result.LegacyRootKept ? "; kept " : "; removed ") + legacyRoot + ".");
            return result;
        }

        private static void MoveEntry(FileSystemInfo entry, string target, MigrationResult result, Action<string> trace)
        {
            if (File.Exists(target) || Directory.Exists(target))
            {
                result.Clashes++;
                result.LeftInPlace.Add(entry.FullName);
                trace("Modstoke left " + entry.FullName + " in place; " + target + " already exists.");
                return;
            }

            try
            {
                if (entry is DirectoryInfo)
                    Directory.Move(entry.FullName, target);
                else
                    File.Move(entry.FullName, target);
                result.Moved++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                result.Failed++;
                result.LeftInPlace.Add(entry.FullName);
                trace("Modstoke could not move " + entry.FullName + " to " + target + ": " + error.Message);
            }
        }

        private static void TryRemoveEmpty(string directory, MigrationResult result, Action<string> trace)
        {
            if (!Directory.Exists(directory))
                return;
            try
            {
                using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
                if (!entries.MoveNext())
                    Directory.Delete(directory, false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                result.Failed++;
                trace("Modstoke could not remove " + directory + ": " + error.Message);
            }
        }

        internal sealed class MigrationResult
        {
            public int Moved { get; set; }
            public int Clashes { get; set; }
            public int Failed { get; set; }
            public bool LegacyRootKept { get; set; }
            public bool LegacyRuntimeLeft { get; set; }
            public List<string> LeftInPlace { get; } = new();
        }
    }
}
