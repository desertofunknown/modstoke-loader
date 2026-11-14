// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>
    /// Engine memory settings the game leaves at Unity's small defaults, added to its boot.config so they apply from
    /// the next start. Unity's own allocator report in Player.log showed the asset loader's temporary allocator, 256 KB
    /// by default, falling back to the shared heap 370,000 times in one world load, and the main thread's 4 MB one
    /// 5,000 times. Only absent keys are added: a value already in the file, the player's or another tool's, is kept,
    /// and the rest of the file is left byte for byte. Steam's file check restores the original, so this runs at every
    /// start.
    /// </summary>
    internal static class EngineBootConfig
    {
        // Sized from the peaks Unity reported once the first values were in (63 MB and 31 MB). Earlier values written by
        // the Loader are raised; any other value is left alone.
        private static readonly (string Key, string Value, string[] Earlier)[] Settings =
        {
            ("memorysetup-temp-allocator-size-preload-manager", "67108864", new[] { "33554432" }),
            ("memorysetup-temp-allocator-size-main", "33554432", new[] { "16777216" })
        };

        internal static void Ensure()
        {
            var path = Path.Combine(Paths.GameRootPath, Paths.ProcessName + "_Data", "boot.config");
            try
            {
                if (!File.Exists(path)) return;
                var text = File.ReadAllText(path, Encoding.ASCII);
                var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                var lines = text.Split('\n');
                var changed = new List<string>();
                foreach (var (key, value, earlier) in Settings)
                {
                    var index = Array.FindIndex(lines, line => string.Equals(line.Split('=', 2)[0].Trim(), key, StringComparison.OrdinalIgnoreCase));
                    if (index < 0)
                    {
                        changed.Add(key);
                        continue;
                    }
                    var current = lines[index].Split('=', 2).ElementAtOrDefault(1)?.Trim();
                    if (current == null || !earlier.Contains(current)) continue;
                    // Same line, same ending; only the value changes.
                    lines[index] = key + "=" + value + (lines[index].EndsWith('\r') ? "\r" : "");
                    changed.Add(key);
                }
                if (changed.Count == 0) return;
                var builder = new StringBuilder(string.Join('\n', lines));
                foreach (var (key, value, _) in Settings)
                {
                    if (lines.Any(line => string.Equals(line.Split('=', 2)[0].Trim(), key, StringComparison.OrdinalIgnoreCase))) continue;
                    if (builder.Length != 0 && builder[^1] != '\n') builder.Append(newline);
                    builder.Append(key).Append('=').Append(value).Append(newline);
                }
                var temporary = path + ".modstoke-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(temporary, builder.ToString(), Encoding.ASCII);
                File.Move(temporary, path, overwrite: true);
                RuntimeEntryPoint.LogSource?.LogInfo("Engine memory settings written to boot.config (" +
                    string.Join(", ", changed) + "); they apply from the next start.");
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Engine memory settings could not be added to " + path + ": " + error.Message);
            }
        }
    }
}
