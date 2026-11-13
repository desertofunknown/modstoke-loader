// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Modstoke.Loader;
using Steamworks;

namespace Modstoke.Sons
{
    /// <summary>
    /// The Modstoke details a dedicated server publishes as Steam server rules, so the server browser can show
    /// them before a join: the Loader version, the mod content fingerprint, whether vanilla players are let in,
    /// and the listed mods with their versions.
    /// </summary>
    internal sealed class ServerDetails
    {
        internal const string FormatKey = "ms_format";
        private const string LoaderKey = "ms_loader", ContentKey = "ms_content", VanillaKey = "ms_vanilla";
        private const string ModCountKey = "ms_mods", ModChunkKey = "ms_mod", ChunkCountKey = "ms_modchunks";
        private const string Format = "1";
        // Rule values are kept short so they survive every Steam rules reply.
        private const int MaxValueChars = 120;
        private const int MaxChunks = 16;

        internal sealed record Mod(string Id, string Version, string Client);

        internal string Loader { get; private init; } = "";
        internal string Content { get; private init; } = "";
        internal bool AllowsVanilla { get; private init; }
        internal IReadOnlyList<Mod> Mods { get; private init; } = Array.Empty<Mod>();
        /// <summary>How many mods the server has; more than <see cref="Mods"/> when the list did not fit.</summary>
        internal int ModCount { get; private init; }

        internal static ServerDetails Local()
        {
            var mods = LocalMods();
            return new ServerDetails
            {
                Loader = ShortVersion(RuntimeEntryPoint.Version),
                Content = ContentAgreement.Fingerprint,
                AllowsVanilla = ContentAgreement.AllowsVanilla,
                Mods = mods,
                ModCount = mods.Count
            };
        }

        private static IReadOnlyList<Mod> _localMods;

        internal static IReadOnlyList<Mod> LocalMods()
        {
            if (_localMods != null) return _localMods;
            var inventory = LoaderState.CurrentInventory;
            var mods = HostMods.ListedMods().Select(mod => new Mod(Clean(mod.Id), Clean(mod.Version), mod.Client))
                .Where(mod => mod.Id.Length > 0).OrderBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase).ToArray();
            // The list is final once discovery finished; until then it is rebuilt on each call.
            if (inventory?.DiscoveryComplete == true) _localMods = mods;
            return mods;
        }

        internal static string ShortVersion(string version)
        {
            var text = version ?? "";
            var build = text.IndexOf('+');
            return build < 0 ? text : text.Substring(0, build);
        }

        internal IEnumerable<KeyValuePair<string, string>> ToRules()
        {
            yield return new(FormatKey, Format);
            yield return new(LoaderKey, Fit(Loader));
            yield return new(ContentKey, Content);
            yield return new(VanillaKey, AllowsVanilla ? "1" : "0");
            yield return new(ModCountKey, ModCount.ToString());
            var chunks = new List<string>();
            var chunk = new StringBuilder();
            foreach (var mod in Mods)
            {
                var entry = mod.Id + "," + mod.Version + "," + Flag(mod.Client);
                if (entry.Length > MaxValueChars) continue;
                if (chunk.Length > 0 && chunk.Length + 1 + entry.Length > MaxValueChars)
                {
                    if (chunks.Count + 1 >= MaxChunks) break;
                    chunks.Add(chunk.ToString());
                    chunk.Clear();
                }
                if (chunk.Length > 0) chunk.Append(';');
                chunk.Append(entry);
            }
            if (chunk.Length > 0) chunks.Add(chunk.ToString());
            yield return new(ChunkCountKey, chunks.Count.ToString());
            for (var i = 0; i < chunks.Count; i++)
                yield return new(ModChunkKey + i, chunks[i]);
        }

        /// <summary>Reads published rules, or null when the server does not publish Modstoke details.</summary>
        internal static ServerDetails FromRules(IReadOnlyDictionary<string, string> rules)
        {
            if (rules == null || !rules.TryGetValue(FormatKey, out var format) || format != Format) return null;
            var mods = new List<Mod>();
            var chunks = int.TryParse(Get(rules, ChunkCountKey), out var count) ? Math.Clamp(count, 0, MaxChunks) : 0;
            for (var i = 0; i < chunks; i++)
                foreach (var entry in Get(rules, ModChunkKey + i).Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var fields = entry.Split(',');
                    if (fields.Length >= 2 && fields[0].Length > 0)
                        mods.Add(new Mod(fields[0], fields[1], fields.Length > 2 ? Client(fields[2]) : "unknown"));
                }
            return new ServerDetails
            {
                Loader = Get(rules, LoaderKey),
                Content = Get(rules, ContentKey),
                AllowsVanilla = Get(rules, VanillaKey) == "1",
                Mods = mods,
                ModCount = int.TryParse(Get(rules, ModCountKey), out var modCount) ? Math.Max(modCount, mods.Count) : mods.Count
            };
        }

        private static string Get(IReadOnlyDictionary<string, string> rules, string key)
            => rules.TryGetValue(key, out var value) ? value ?? "" : "";

        private static string Flag(string client) => client switch
        {
            "required" => "r",
            "server" => "s",
            "optional" => "o",
            _ => "u"
        };

        private static string Client(string flag) => flag switch
        {
            "r" => "required",
            "s" => "server",
            "o" => "optional",
            _ => "unknown"
        };

        // Separators are reserved by the rule format.
        private static string Clean(string text)
            => new string((text ?? "").Where(c => c != ',' && c != ';' && !char.IsControl(c)).ToArray()).Trim();

        private static string Fit(string text) => text.Length <= MaxValueChars ? text : text.Substring(0, MaxValueChars);
    }

    /// <summary>Publishes this dedicated server's <see cref="ServerDetails"/> as Steam server rules.</summary>
    internal static class ServerDetailsPublisher
    {
        private const double RepublishSeconds = 60;
        private static double _next;
        private static bool _published, _failed;
        private static int _errors;
        private static readonly HashSet<string> Keys = new(StringComparer.Ordinal);

        private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        internal static void Tick()
        {
            if (_failed || !Host.IsServer || Now < _next) return;
            _next = Now + (_published ? RepublishSeconds : 2);
            try
            {
                // The mod list is complete once discovery finished, and Steam takes rules once logged on.
                if (LoaderState.CurrentInventory?.DiscoveryComplete != true || !SteamGameServer.BLoggedOn()) return;
                var rules = ServerDetails.Local().ToRules().ToList();
                foreach (var stale in Keys.Except(rules.Select(rule => rule.Key)).ToArray())
                {
                    SteamGameServer.SetKeyValue(stale, "");
                    Keys.Remove(stale);
                }
                foreach (var rule in rules)
                {
                    SteamGameServer.SetKeyValue(rule.Key, rule.Value);
                    Keys.Add(rule.Key);
                }
                if (_published) return;
                _published = true;
                RuntimeEntryPoint.LogSource?.LogInfo("Publishing this server's Modstoke details to the server browser ("
                    + rules.Count + " Steam rules).");
            }
            catch (InvalidOperationException)
            {
                // Steam's game server starts some seconds after the Loader; it is asked again shortly.
            }
            catch (Exception error)
            {
                if (++_errors < 5)
                {
                    _next = Now + 10;
                    return;
                }
                _failed = true;
                RuntimeEntryPoint.LogSource?.LogWarning("This server's Modstoke details cannot be published to the server browser: "
                    + error.Message);
            }
        }
    }
}
