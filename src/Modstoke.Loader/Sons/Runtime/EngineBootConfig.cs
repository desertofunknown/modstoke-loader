// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
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
        private static readonly (string Key, string Value)[] Settings =
        {
            ("memorysetup-temp-allocator-size-preload-manager", "33554432"),
            ("memorysetup-temp-allocator-size-main", "16777216")
        };

        internal static void Ensure()
        {
            var path = Path.Combine(Paths.GameRootPath, Paths.ProcessName + "_Data", "boot.config");
            try
            {
                if (!File.Exists(path)) return;
                var text = File.ReadAllText(path, Encoding.ASCII);
                var keys = text.Split('\n').Select(line => line.Split('=', 2)[0].Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var missing = Settings.Where(setting => !keys.Contains(setting.Key)).ToArray();
                if (missing.Length == 0) return;
                var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                var builder = new StringBuilder(text);
                if (text.Length != 0 && !text.EndsWith('\n')) builder.Append(newline);
                foreach (var (key, value) in missing) builder.Append(key).Append('=').Append(value).Append(newline);
                var temporary = path + ".modstoke-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(temporary, builder.ToString(), Encoding.ASCII);
                File.Move(temporary, path, overwrite: true);
                RuntimeEntryPoint.LogSource?.LogInfo("Engine memory settings added to boot.config (" +
                    string.Join(", ", missing.Select(setting => setting.Key)) + "); they apply from the next start.");
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Engine memory settings could not be added to " + path + ": " + error.Message);
            }
        }
    }
}
