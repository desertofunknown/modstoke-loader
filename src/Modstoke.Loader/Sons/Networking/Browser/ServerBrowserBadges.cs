// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Loader;
using Sons.Gui.Multiplayer;
using Sons.Multiplayer;
using Sons.Multiplayer.Dedicated;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Modstoke.Sons
{
    /// <summary>
    /// Adds a badge to each dedicated server row in the server browser saying whether the server runs Modstoke,
    /// which version, and whether its mods match yours. The badge opens the server's details: its mods with their
    /// versions beside yours, before you join.
    /// </summary>
    internal static class ServerBrowserBadges
    {
        private const int MaxQueries = 3;
        private const double AnswerSeconds = 8, KeepSeconds = 120, RetrySeconds = 30;
        private const string Green = "#9CCB7A", Amber = "#E3B65A", Red = "#E2715E", Dim = "#8C8C8C";

        private enum State { Pending, Modstoke, Vanilla, NoAnswer }

        private sealed class Server
        {
            internal uint Ip;
            internal ushort Port;
            internal State State = State.Pending;
            internal ServerDetails Details;
            internal double Asked, Expires;
            internal bool Started, Done;
            internal readonly Dictionary<string, string> Rules = new(StringComparer.Ordinal);
            internal ISteamMatchmakingRulesResponse Response;
            internal HServerQuery Handle;
        }

        private sealed class Row
        {
            internal ServerInfoGuiItem Item;
            internal TMP_Text Badge;
            internal Button Button;
            internal LayoutElement Layout;
            internal string Key, Name;
            internal bool LegacyModded;
        }

        private static readonly Dictionary<string, Server> Servers = new(StringComparer.Ordinal);
        private static readonly Queue<string> Waiting = new();
        private static readonly Dictionary<IntPtr, Row> Rows = new();
        // Steam may still hold a finished query's response until the next frame.
        private static readonly List<ISteamMatchmakingRulesResponse> Retired = new();
        private static bool _hooked;
        private static int _running;
        private static double _nextPrune;

        private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        internal static void Tick()
        {
            if (!_hooked)
            {
                _hooked = true;
                if (!Host.IsServer) Hook();
                return;
            }
            if (Rows.Count == 0 && _running == 0 && Waiting.Count == 0) return;
            Retired.Clear();
            var now = Now;
            foreach (var server in Servers.Values)
                if (server.Started && !server.Done && now - server.Asked > AnswerSeconds)
                {
                    try { SteamMatchmakingServers.CancelServerQuery(server.Handle); }
                    catch (Exception) { /* Steam may have finished it meanwhile. */ }
                    Finish(server, false);
                }
            while (_running < MaxQueries && Waiting.Count > 0)
                if (Servers.TryGetValue(Waiting.Dequeue(), out var next) && !next.Started)
                    Start(next);
            if (now >= _nextPrune)
            {
                _nextPrune = now + 5;
                foreach (var gone in Rows.Where(row => row.Value.Item == null || !row.Value.Item || !row.Value.Badge).Select(row => row.Key).ToArray())
                    Rows.Remove(gone);
            }
        }

        private static void Hook()
        {
            try
            {
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-server-badges");
                harmony.Patch(AccessTools.Method(typeof(ServerInfoGuiItem), nameof(ServerInfoGuiItem.Setup))
                        ?? throw new MissingMethodException(nameof(ServerInfoGuiItem), nameof(ServerInfoGuiItem.Setup)),
                    postfix: new HarmonyMethod(typeof(ServerBrowserBadges), nameof(AfterSetup)));
                harmony.Patch(AccessTools.Method(typeof(ServerInfoGuiItem), nameof(ServerInfoGuiItem.SetNavigation))
                        ?? throw new MissingMethodException(nameof(ServerInfoGuiItem), nameof(ServerInfoGuiItem.SetNavigation)),
                    postfix: new HarmonyMethod(typeof(ServerBrowserBadges), nameof(AfterNavigation)));
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The server browser will not show Modstoke details: " + error.Message);
            }
        }

        private static void AfterSetup(ServerInfoGuiItem __instance, IServerInfo __0)
        {
            try
            {
                var dedicated = __0?.TryCast<DedicatedServerInfo>();
                var row = RowFor(__instance, dedicated != null);
                if (row == null) return;
                if (dedicated == null)
                {
                    row.Key = null;
                    Paint(row);
                    return;
                }
                var address = dedicated.NetAddr;
                var ip = address.GetIP();
                var port = address.GetQueryPort();
                row.Key = IpText(ip) + ":" + port;
                row.Name = dedicated.CleanName ?? dedicated.Name ?? "";
                row.LegacyModded = (dedicated.Name ?? "").Contains("[MODDED]", StringComparison.Ordinal);
                Request(row.Key, ip, port);
                Paint(row);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("A server row's Modstoke details failed: " + error.Message);
            }
        }

        // The game links row buttons explicitly; the badge sits to the right of the row's first button.
        private static void AfterNavigation(ServerInfoGuiItem __instance)
        {
            try
            {
                if (!Rows.TryGetValue(__instance.Pointer, out var row) || !row.Button) return;
                var first = __instance.GetFirstButton();
                if (first == null || !first) return;
                var navigation = first.navigation;
                if (navigation.mode == Navigation.Mode.Explicit && navigation.selectOnRight == null && row.Button.interactable)
                {
                    navigation.selectOnRight = row.Button;
                    first.navigation = navigation;
                }
                row.Button.navigation = new Navigation
                {
                    mode = Navigation.Mode.Explicit,
                    selectOnLeft = first,
                    selectOnUp = navigation.selectOnUp,
                    selectOnDown = navigation.selectOnDown
                };
            }
            catch (Exception)
            {
                // Navigation stays as the game set it.
            }
        }

        private static Row RowFor(ServerInfoGuiItem item, bool create)
        {
            if (Rows.TryGetValue(item.Pointer, out var row) && row.Badge) return row;
            if (!create) return null;
            var template = item._pingText;
            if (template == null || !template) return null;
            var parent = item._rightAnchor != null && item._rightAnchor ? item._rightAnchor.transform : template.transform.parent;
            var badge = Object.Instantiate(template.gameObject, parent, false);
            badge.name = "ModstokeBadge";
            badge.transform.SetAsFirstSibling();
            foreach (var localized in badge.GetComponents<UnityEngine.Localization.Components.LocalizeStringEvent>())
                Object.Destroy(localized);
            var text = badge.GetComponent<TMP_Text>();
            text.richText = true;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Overflow;
            text.raycastTarget = true;
            var layout = badge.GetComponent<LayoutElement>();
            if (layout == null || !layout) layout = badge.AddComponent<LayoutElement>();
            var button = badge.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = text;
            row = new Row { Item = item, Badge = text, Button = button, Layout = layout };
            var owner = row;
            button.onClick.AddListener((UnityAction)(() => OpenDetails(owner)));
            Rows[item.Pointer] = row;
            return row;
        }

        private static void Request(string key, uint ip, ushort port)
        {
            if (Servers.TryGetValue(key, out var known) && (!known.Done || Now < known.Expires)) return;
            Servers[key] = new Server { Ip = ip, Port = port };
            Waiting.Enqueue(key);
        }

        private static void Start(Server server)
        {
            server.Started = true;
            server.Asked = Now;
            _running++;
            try
            {
                server.Response = new ISteamMatchmakingRulesResponse(
                    DelegateSupport.ConvertDelegate<ISteamMatchmakingRulesResponse.RulesResponded>(
                        new Action<string, string>((rule, value) =>
                        {
                            if (rule != null && rule.StartsWith("ms_", StringComparison.Ordinal)) server.Rules[rule] = value ?? "";
                        })),
                    DelegateSupport.ConvertDelegate<ISteamMatchmakingRulesResponse.RulesFailedToRespond>(
                        new Action(() => Finish(server, false))),
                    DelegateSupport.ConvertDelegate<ISteamMatchmakingRulesResponse.RulesRefreshComplete>(
                        new Action(() => Finish(server, true))));
                server.Handle = SteamMatchmakingServers.ServerRules(server.Ip, server.Port, server.Response);
            }
            catch (Exception error)
            {
                Finish(server, false);
                RuntimeEntryPoint.LogSource?.LogWarning("A server's Modstoke details could not be requested: " + error.Message);
            }
        }

        private static void Finish(Server server, bool answered)
        {
            if (server.Done) return;
            server.Done = true;
            _running = Math.Max(0, _running - 1);
            server.Details = answered ? ServerDetails.FromRules(server.Rules) : null;
            server.State = !answered ? State.NoAnswer : server.Details != null ? State.Modstoke : State.Vanilla;
            server.Expires = Now + (answered ? KeepSeconds : RetrySeconds);
            if (server.Response != null) Retired.Add(server.Response);
            server.Response = null;
            foreach (var row in Rows.Values)
                if (row.Key != null && Servers.TryGetValue(row.Key, out var shown) && ReferenceEquals(shown, server))
                    Paint(row);
        }

        private static void Paint(Row row)
        {
            try
            {
                if (!row.Badge) return;
                Server server = null;
                if (row.Key != null) Servers.TryGetValue(row.Key, out server);
                var text = server == null ? ""
                    : server.State switch
                    {
                        State.Pending => Tint(Dim, "· · ·"),
                        State.Vanilla => row.LegacyModded ? Tint(Dim, "Modded") : Tint(Dim, "Vanilla"),
                        State.Modstoke => Describe(server.Details),
                        _ => ""
                    };
                row.Badge.text = text;
                row.Badge.gameObject.SetActive(text.Length > 0);
                row.Button.interactable = server?.State == State.Modstoke;
                if (text.Length == 0) return;
                var width = row.Badge.preferredWidth + 12;
                row.Layout.preferredWidth = width;
                row.Layout.minWidth = width;
                row.Badge.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            }
            catch (Exception)
            {
                // A recycled row is painted again on its next setup.
            }
        }

        private static string Describe(ServerDetails details)
        {
            var (color, verdict) = Judge(details);
            return Tint(color, "Modstoke " + details.Loader + " · " + verdict);
        }

        private static (string Color, string Verdict) Judge(ServerDetails details)
        {
            var mine = ContentAgreement.Fingerprint;
            if (details.Content.Length > 0 && details.Content != mine) return (Red, "mods differ");
            var missing = Missing(details).Count();
            if (missing > 0) return (Amber, missing == 1 ? "1 mod missing" : missing + " mods missing");
            if (details.Content.Length == 0 && mine.Length > 0) return (Amber, "your mods add items");
            return (Green, "mods match");
        }

        private static IEnumerable<ServerDetails.Mod> Missing(ServerDetails details)
        {
            var local = ServerDetails.LocalMods();
            return details.Mods.Where(mod => mod.Client == "required"
                && !local.Any(own => string.Equals(own.Id, mod.Id, StringComparison.OrdinalIgnoreCase)));
        }

        private static void OpenDetails(Row row)
        {
            try
            {
                if (row.Key == null || !Servers.TryGetValue(row.Key, out var server) || server.Details == null) return;
                var key = row.Key;
                var dialogs = ModalDialogs.Instance;
                var dialog = dialogs.Show(string.IsNullOrWhiteSpace(row.Name) ? "Server details" : row.Name, DetailsText(server.Details));
                dialogs.SetOption1(dialog, "Join", () => dialogs.Complete(dialog, () =>
                {
                    // The row may have been recycled for another server while the details were open.
                    if (row.Item && row.Key == key) row.Item.DoJoin();
                }));
                dialogs.SetCancel(dialog, () => dialogs.Close(dialog));
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The server details could not be shown: " + error.Message);
            }
        }

        private static string DetailsText(ServerDetails details)
        {
            var mine = ContentAgreement.Fingerprint;
            var local = ServerDetails.LocalMods();
            var text = new StringBuilder();
            text.Append("Modstoke ").Append(details.Loader.Length > 0 ? details.Loader : "(version not given)")
                .Append(" · you have ").Append(ServerDetails.ShortVersion(RuntimeEntryPoint.Version)).Append('\n');
            if (details.Content.Length == 0)
                text.Append(mine.Length == 0 ? "No mods with their own items on either side.\n"
                    : Tint(Amber, "This server has no mods with their own items; yours won't exist there.") + "\n");
            else if (details.Content == mine)
                text.Append(Tint(Green, "Mod items match yours.")).Append('\n');
            else
                text.Append(Tint(Red, "Mod items differ from yours, so this server will refuse you.")).Append('\n');
            if (details.Content.Length > 0)
                text.Append("Players without Modstoke: ").Append(details.AllowsVanilla ? "allowed" : "not allowed").Append('\n');

            text.Append("\n<b>Mod</b><pos=58%><b>Server</b><pos=80%><b>You</b>\n");
            const int shown = 12;
            foreach (var mod in details.Mods.Take(shown))
            {
                var own = local.FirstOrDefault(entry => string.Equals(entry.Id, mod.Id, StringComparison.OrdinalIgnoreCase));
                var yours = own == null
                    ? mod.Client == "required" ? Tint(Red, "missing") : Tint(Dim, mod.Client == "server" ? "server only" : "not installed")
                    : string.Equals(own.Version, mod.Version, StringComparison.OrdinalIgnoreCase) ? Tint(Green, own.Version)
                    : Tint(Amber, own.Version.Length > 0 ? own.Version : "?");
                text.Append(mod.Id).Append("<pos=58%>").Append(mod.Version.Length > 0 ? mod.Version : "?")
                    .Append("<pos=80%>").Append(yours).Append('\n');
            }
            var unlisted = details.ModCount - Math.Min(details.Mods.Count, shown);
            if (unlisted > 0) text.Append(Tint(Dim, "and " + unlisted + " more")).Append('\n');
            var onlyMine = local.Where(own => own.Client == "required"
                && !details.Mods.Any(mod => string.Equals(mod.Id, own.Id, StringComparison.OrdinalIgnoreCase)))
                .Select(own => own.Id).ToArray();
            if (onlyMine.Length > 0 && details.ModCount <= details.Mods.Count)
                text.Append('\n').Append(Tint(Dim, "Only on your game: " + string.Join(", ", onlyMine)));
            return text.ToString().TrimEnd();
        }

        private static string Tint(string color, string text) => "<color=" + color + ">" + text + "</color>";

        private static string IpText(uint ip) => (ip >> 24) + "." + ((ip >> 16) & 255) + "." + ((ip >> 8) & 255) + "." + (ip & 255);
    }
}
