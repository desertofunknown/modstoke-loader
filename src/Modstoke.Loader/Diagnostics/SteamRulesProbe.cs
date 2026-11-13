// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using Il2CppInterop.Runtime;
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
        private static ISteamMatchmakingRulesResponse _response;
        private static List<string> _received;
        private static DateTime _started;

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
            var bytes = address.MapToIPv4().GetAddressBytes();
            var ip = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
            _received = new List<string>();
            _started = DateTime.UtcNow;
            // Steam calls back on the game thread; the response must stay alive until it reports completion.
            _response = new ISteamMatchmakingRulesResponse(
                DelegateSupport.ConvertDelegate<ISteamMatchmakingRulesResponse.RulesResponded>(
                    new Action<string, string>((rule, value) => _received?.Add(rule + " = " + Describe(rule, value)))),
                DelegateSupport.ConvertDelegate<ISteamMatchmakingRulesResponse.RulesFailedToRespond>(
                    new Action(() => Finish("the server did not answer the rules query"))),
                DelegateSupport.ConvertDelegate<ISteamMatchmakingRulesResponse.RulesRefreshComplete>(
                    new Action(() => Finish("complete"))));
            SteamMatchmakingServers.ServerRules(ip, port, _response);
            return "Asked " + target + " for its Steam rules; the answer is logged when it arrives.";
        }

        private static void Finish(string outcome)
        {
            var received = _received ?? new List<string>();
            var text = new StringBuilder("Steam rules query " + outcome + " after "
                + (DateTime.UtcNow - _started).TotalMilliseconds.ToString("F0") + " ms; " + received.Count + " rule(s):");
            foreach (var line in received) text.AppendLine().Append("  ").Append(line);
            RuntimeEntryPoint.LogSource?.LogInfo(text.ToString());
            _received = null;
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
