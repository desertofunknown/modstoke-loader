// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Steamworks;

namespace Modstoke.Loader
{
    /// <summary>
    /// Checks whether Steam server rules can carry Modstoke details before a join: "steamrules publish" on a
    /// dedicated server sets test rules of growing length, and "steamrules query ip:queryport" on a client reads
    /// them back and logs what arrived.
    /// </summary>
    internal static class SteamRulesProbe
    {
        private static readonly int[] Lengths = { 32, 64, 127, 128, 255, 256, 511, 1024, 2048 };

        internal static string Run(string arguments)
        {
            var parts = (arguments ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            try
            {
                if (parts.Length == 1 && parts[0] == "publish") return Publish();
                if (parts.Length == 2 && parts[0] == "query") return Query(parts[1]);
            }
            catch (Exception error)
            {
                return "Steam rules probe failed: " + error.GetType().Name + ": " + error.Message;
            }
            return "Use \"steamrules publish\" on a dedicated server, then \"steamrules query <ip>:<queryport>\" in a game.";
        }

        private static string Publish()
        {
            SteamGameServer.SetKeyValue("ms_probe", "modstoke-probe/1 " + RuntimeEntryPoint.Version);
            foreach (var length in Lengths)
                SteamGameServer.SetKeyValue("ms_len" + length, Filler(length));
            var report = "Published ms_probe and ms_len" + string.Join("/", Lengths) + " as Steam server rules.";
            RuntimeEntryPoint.LogSource?.LogInfo(report);
            return report + " Query them from a game with \"steamrules query <ip>:<queryport>\".";
        }

        private static string Query(string target)
        {
            var separator = target.LastIndexOf(':');
            if (separator <= 0 || !IPAddress.TryParse(target.Substring(0, separator), out var address)
                || !ushort.TryParse(target.Substring(separator + 1), out var port))
                return "Give the server as ip:queryport, for example 127.0.0.1:27016.";
            var started = DateTime.UtcNow;
            Task.Run(async () =>
            {
                var rules = await global::Modstoke.Sons.ServerRulesQuery.RunAsync(address.MapToIPv4(), port, TimeSpan.FromSeconds(4));
                var text = new StringBuilder("Steam rules query " + (rules == null ? "got no answer" : "complete") + " after "
                    + (DateTime.UtcNow - started).TotalMilliseconds.ToString("F0") + " ms; " + (rules?.Count ?? 0) + " rule(s):");
                foreach (var rule in rules ?? new Dictionary<string, string>())
                    text.AppendLine().Append("  ").Append(rule.Key).Append(" = ").Append(Describe(rule.Key, rule.Value));
                RuntimeEntryPoint.LogSource?.LogInfo(text.ToString());
            });
            return "Asked " + target + " for its Steam rules; the answer is logged when it arrives.";
        }

        // Probe values are filler with a recognisable end, so a truncated value shows where it was cut.
        private static string Filler(int length)
            => length <= 8 ? new string('x', length) : new string('x', length - 8) + length.ToString("D7") + "!";

        private static string Describe(string rule, string value)
        {
            if (!rule.StartsWith("ms_len", StringComparison.Ordinal)) return value;
            var expected = int.TryParse(rule.Substring(6), out var length) ? length : -1;
            return value.Length + " chars" + (value.Length == expected && value.EndsWith("!", StringComparison.Ordinal)
                ? ", intact" : ", cut (expected " + expected + ")");
        }
    }
}
