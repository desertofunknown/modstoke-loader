// SPDX-License-Identifier: GPL-3.0-or-later
// Retains the diagnostic schema and paths of BepInEx's OpenACAI hook status API.
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class NativeHookStatusWriter
    {
        private static readonly object Sync = new object();
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        private static bool _installed;
        private static int _writeCount;
        private static int _failureCount;
        private static string _path = "";
        private static string _lastError = "";

        internal static void Install()
        {
            // Registration can publish immediately. Do not take our writer lock
            // while acquiring the compatibility status lock.
            try
            {
                OpenACAIIL2CPPHookStatus.RegisterStateWriter(Write);
                lock (Sync)
                    _installed = true;
            }
            catch (Exception error)
            {
                lock (Sync)
                {
                    _failureCount++;
                    _lastError = error.Message;
                }
            }
        }

        private static void Write(object state)
        {
            lock (Sync)
            {
                string temporary = null;
                var temporaryCreated = false;
                try
                {
                    _path = ResolvePath();
                    if (string.IsNullOrWhiteSpace(_path))
                        throw new InvalidOperationException("The native hook status directory is not available.");

                    var json = JsonSerializer.Serialize(state, JsonOptions);
                    var directory = Path.GetDirectoryName(_path);
                    Directory.CreateDirectory(directory);
                    temporary = Path.Combine(directory, "." + Path.GetFileName(_path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        temporaryCreated = true;
                        using var writer = new StreamWriter(stream, Utf8);
                        writer.Write(json);
                    }

                    File.Move(temporary, _path, overwrite: true);
                    temporaryCreated = false;
                    _writeCount++;
                    _lastError = "";
                }
                catch (Exception error)
                {
                    // Diagnostics must not change hook state or recursively enter logging.
                    _failureCount++;
                    _lastError = error.Message;
                }
                finally
                {
                    if (temporaryCreated)
                    {
                        try { File.Delete(temporary); }
                        catch (Exception) { /* Leave only this failed write's temporary file. */ }
                    }
                }
            }
        }

        private static string ResolvePath()
        {
            var pluginPath = Paths.PluginPath;
            if (string.IsNullOrWhiteSpace(pluginPath) && !string.IsNullOrWhiteSpace(Paths.BepInExRootPath))
                pluginPath = Path.Combine(Paths.BepInExRootPath, "plugins");
            if (string.IsNullOrWhiteSpace(pluginPath) && !string.IsNullOrWhiteSpace(Paths.GameRootPath))
                pluginPath = Path.Combine(Paths.GameRootPath, "BepInEx", "plugins");

            return string.IsNullOrWhiteSpace(pluginPath) ? null : Path.Combine(pluginPath,
                OpenACAIIL2CPPHookStatus.StateDirectoryName, OpenACAIIL2CPPHookStatus.StateFileName);
        }

        internal static object CreateState()
        {
            lock (Sync)
                return new
                {
                    engine = "OpenACAI",
                    installed = _installed,
                    path = _path,
                    writeCount = _writeCount,
                    failureCount = _failureCount,
                    lastError = _lastError
                };
        }
    }
}
