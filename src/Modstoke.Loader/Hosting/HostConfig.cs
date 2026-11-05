// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Modstoke.Diagnostics;
using UnityEngine;

namespace Modstoke.Loader
{
    internal sealed record HostConfigInfo(string Id, string Path, string Format, string Reload);

    /// <summary>The config files a host can edit, whether they parse, and which edits still need a restart.</summary>
    internal static class HostConfig
    {
        private const long MaxBytes = 1024 * 1024;
        private const string AdminId = "com.openacai.sotf.admin";
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly ConcurrentDictionary<string, Stamp> Baseline =
            new ConcurrentDictionary<string, Stamp>(StringComparer.OrdinalIgnoreCase);
        private static IReadOnlyList<HostConfigInfo> _known = Array.Empty<HostConfigInfo>();

        internal static IReadOnlyList<HostConfigInfo> Known => Volatile.Read(ref _known);

        internal static void Initialize() => Publish(Build());

        internal static IReadOnlyList<string> PendingChanges()
        {
            var pending = new List<string>();
            foreach (var config in Known)
                if (config.Reload == "restart" && ChangedSinceStart(config.Path))
                    pending.Add(config.Id);
            return pending;
        }

        internal static void RunConfig(ConsoleSubmission request)
        {
            var known = Known;
            Task.Run(() =>
            {
                try
                {
                    var configs = new JsonArray();
                    var lines = new List<string>();
                    foreach (var config in known)
                    {
                        var error = Validate(config, out var exists);
                        configs.Add((JsonNode)new JsonObject
                        {
                            ["id"] = config.Id,
                            ["path"] = config.Path,
                            ["format"] = config.Format,
                            ["reload"] = config.Reload,
                            ["exists"] = exists,
                            ["ok"] = error == null,
                            ["error"] = error,
                            ["changedSinceStart"] = ChangedSinceStart(config.Path)
                        });
                        lines.Add(config.Id + ": " + (error ?? (exists ? "ok" : "ok (not created yet)"))
                            + " · " + config.Path);
                    }
                    HostCommands.Respond(request, true,
                        lines.Count == 0 ? "No config files are known." : string.Join("\n", lines),
                        new JsonObject { ["configs"] = configs });
                }
                catch (Exception error)
                {
                    HostCommands.Respond(request, false, "Config check failed: " + error.Message);
                }
            });
        }

        internal static void RunReload(ConsoleSubmission request)
        {
            var applied = new List<string>();
            var preferencesOk = RuntimePreferences.Reload(out var detail);
            if (preferencesOk)
                applied.Add("loader.settings");
            Publish(Build());
            FileIntegrity.RefreshInBackground();
            applied.Add("integrity");
            LoaderState.RequestRefresh("host-reload");

            Task.Run(() =>
            {
                var restartRequired = PendingChanges();
                var human = "Reloaded " + string.Join(", ", applied) + ". " + detail
                    + (restartRequired.Count == 0 ? "" : " Restart to apply: " + string.Join(", ", restartRequired) + ".");
                HostCommands.Respond(request, preferencesOk, human,
                    new { applied, restartRequired, detail });
            });
        }

        private static List<HostConfigInfo> Build()
        {
            var configs = new List<HostConfigInfo>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string id, string path, string format, string reload)
            {
                if (!string.IsNullOrWhiteSpace(path) && ids.Add(id))
                    configs.Add(new HostConfigInfo(id, path, format, reload));
            }

            Add("loader.settings", Safe(() => RuntimePreferences.SettingsPath), "cfg", "live");
            Add("bepinex.core", Safe(() => BepInEx.Configuration.ConfigFile.CoreConfig.ConfigFilePath), "cfg", "restart");
            if (Host.IsServer)
                Add("game.dedicatedserver", Safe(DedicatedServerConfigPath), "json", "restart");

            var entries = LoaderState.CurrentInventory?.Entries ?? Array.Empty<ModInventoryEntry>();
            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.ConfigPath))
                    continue;
                // Admin watches its settings.ini and applies edits itself.
                Add("mod:" + entry.Id, entry.ConfigPath, (entry.ConfigFormat ?? "").ToLowerInvariant(),
                    string.Equals(entry.Id, AdminId, StringComparison.OrdinalIgnoreCase) ? "auto" : "restart");
            }
            return configs;
        }

        private static void Publish(List<HostConfigInfo> configs)
        {
            Volatile.Write(ref _known, configs.AsReadOnly());
            // Keep the first stamp of each path so a restart-only edit stays pending after a reload.
            Task.Run(() =>
            {
                foreach (var config in configs)
                    if (!Baseline.ContainsKey(config.Path))
                        Baseline.TryAdd(config.Path, Stamp.Read(config.Path));
                HostStatus.MarkChanged();
            });
        }

        // The server reads dedicatedserver.cfg from -userdatapath when given, else its persistent data folder.
        private static string DedicatedServerConfigPath()
        {
            var directory = UserDataArgument();
            if (string.IsNullOrWhiteSpace(directory))
                directory = Application.persistentDataPath;
            return string.IsNullOrWhiteSpace(directory)
                ? null
                : Path.GetFullPath(Path.Combine(directory, "dedicatedserver.cfg"));
        }

        private static string UserDataArgument()
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 1; i < args.Length; i++)
            {
                var arg = args[i] ?? "";
                if (string.Equals(arg, "-userdatapath", StringComparison.OrdinalIgnoreCase))
                    return i + 1 < args.Length ? args[i + 1]?.Trim().Trim('"') : null;
                if (arg.StartsWith("-userdatapath=", StringComparison.OrdinalIgnoreCase))
                    return arg["-userdatapath=".Length..].Trim().Trim('"');
            }
            return null;
        }

        private static string Validate(HostConfigInfo config, out bool exists)
        {
            exists = false;
            try
            {
                var info = new FileInfo(config.Path);
                exists = info.Exists;
                if (!exists)
                    return null;
                if (info.Length > MaxBytes)
                    return "The file is larger than 1 MiB.";

                byte[] bytes;
                using (var stream = new FileStream(config.Path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length > MaxBytes)
                        return "The file is larger than 1 MiB.";
                    bytes = new byte[stream.Length];
                    stream.ReadExactly(bytes);
                }

                string text;
                try
                {
                    var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                    text = StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
                }
                catch (DecoderFallbackException)
                {
                    return "The file is not valid UTF-8.";
                }

                switch (config.Format)
                {
                    case "cfg":
                    case "ini":
                        LoaderConfig.ParseSettings(text);
                        break;
                    case "json":
                        using (JsonDocument.Parse(text, new JsonDocumentOptions
                        {
                            CommentHandling = JsonCommentHandling.Skip,
                            AllowTrailingCommas = true
                        }))
                        { }
                        break;
                }
                return null;
            }
            catch (JsonException error)
            {
                return "Invalid JSON: " + error.Message;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return "The file could not be read: " + error.Message;
            }
            catch (Exception error)
            {
                return "The file could not be parsed: " + error.Message;
            }
        }

        private static bool ChangedSinceStart(string path)
        {
            if (!Baseline.TryGetValue(path, out var baseline) || !baseline.Known)
                return false;
            var current = Stamp.Read(path);
            return current.Known && current != baseline;
        }

        private static string Safe(Func<string> read)
        {
            try { return read(); }
            catch (Exception) { return null; }
        }

        private readonly record struct Stamp(bool Known, bool Exists, long Size, DateTime WriteUtc)
        {
            internal static Stamp Read(string path)
            {
                try
                {
                    var info = new FileInfo(path);
                    return info.Exists
                        ? new Stamp(true, true, info.Length, info.LastWriteTimeUtc)
                        : new Stamp(true, false, 0, default);
                }
                catch (Exception)
                {
                    return default;
                }
            }
        }
    }
}
