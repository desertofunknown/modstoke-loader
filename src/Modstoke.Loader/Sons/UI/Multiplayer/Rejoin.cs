// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using HarmonyLib;
using Modstoke.Sons;
using Sons.Gameplay.GameSetup;
using Sons.Gui.Multiplayer;
using Sons.Multiplayer;
using Sons.Multiplayer.Dedicated;
using Steamworks;
using UnityEngine;

namespace Modstoke.Loader
{
    /// <summary>
    /// Gets a player back into the game they dropped out of. While a client is in a world, the server it joined is kept:
    /// a dedicated server by its address and the game's own join details, a hosted game by its Steam lobby. When that
    /// connection ends without the server saying why (a timeout, a transport failure, a host that vanished), or a
    /// dedicated server shuts down, the drop's own dialog offers Rejoin and the title offers it for five minutes. A kick,
    /// a ban, any refusal, a hosted game its host ended and the player's own leave never offer it. A rejoin is the server
    /// list's own join: the server's current listing and rules, the loading screen, then the game's connect.
    /// </summary>
    /// <remarks>
    /// There is no automatic retry. The game holds a silent connection for about 40 seconds, under its own "host not
    /// responding" notice and the HUD's interrupted warning, before it gives up, so a blip never drops the player. A drop
    /// means a long outage, and a retry seconds later would nearly always fail, costing a load and a second failure. One
    /// press on the drop's dialog or the title rejoins once the server is back.
    /// </remarks>
    internal static class Rejoin
    {
        private const float OfferSeconds = 300f, ListSeconds = 8f, RulesSeconds = 4f, OpenSeconds = 20f, CaptureSeconds = 0.5f;
        private const int MaxNameLength = 28;
        // The game's console disconnect commands send a shutdown token too, with this message.
        private const string DebugDisconnect = "Debug Disconnect";
        private static readonly Regex Tags = new("<[^>]*>", RegexOptions.Compiled);

        private sealed class Target
        {
            internal string Name, Ip, GameId, Password, RulesKey;
            // A hosted game's Steam lobby; 0 for a dedicated server.
            internal ulong Lobby, ServerId;
            internal ushort Port, RulesPort;
            internal uint RulesIp;
            internal int MaxPlayers;
            internal bool Dedicated => Lobby == 0;
        }

        private enum Drop { Left, Removed, HostEnded, ServerClosed, Lost, TimedOut }

        private static Target _joined, _offer, _attempt;
        // The server list's last dedicated join, for the rejoin of that same server: its rules key and name.
        private static (string Key, uint Ip, ushort QueryPort, ushort GamePort, string Name) _listJoin;
        private static IntPtr _server;
        private static int _ownerThreadId, _reasons, _attemptReasons;
        private static float _offerUntil, _wantedAt, _rulesAt, _nextCapture;
        private static bool _hooked, _leaving, _dropDialog, _wanted, _opening, _listed, _toLan, _sent, _connecting;

        /// <summary>Whether there is a server to rejoin now.</summary>
        internal static bool Offered => _offer != null && Time.unscaledTime < _offerUntil;

        /// <summary>"Rejoin" and the server's name, for the title and the dialog.</summary>
        internal static string Label => FrontEnd.Text.Get("net.rejoin.title", ("server", _offer?.Name ?? ""));

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (GameBuild.IsDedicatedServer
                || Interlocked.CompareExchange(ref _ownerThreadId, Environment.CurrentManagedThreadId, 0) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                warning?.Invoke("Rejoin is unavailable: Harmony support is not ready.");
                return;
            }
            try
            {
                var disconnected = AccessTools.Method(typeof(BoltConnection), nameof(BoltConnection.DisconnectedInternal), Type.EmptyTypes);
                var shutdown = AccessTools.Method(typeof(BoltCore), "BeginShutdown", new[] { typeof(Bolt.ControlCommandShutdown) });
                var immediate = AccessTools.Method(typeof(BoltCore), "ShutdownImmediate", Type.EmptyTypes);
                if (disconnected == null || shutdown == null || immediate == null)
                    throw new MissingMethodException("A Bolt disconnect or shutdown method is unavailable.");
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-rejoin");
                harmony.Patch(disconnected, prefix: new HarmonyMethod(typeof(Rejoin), nameof(BeforeDisconnected)));
                harmony.Patch(shutdown, prefix: new HarmonyMethod(typeof(Rejoin), nameof(BeforeShutdown)));
                harmony.Patch(immediate, prefix: new HarmonyMethod(typeof(Rejoin), nameof(BeforeShutdown)));
                // Leaving through the pause menu (Modstoke's or the game's) drops the connection before Bolt's own shutdown
                // runs, so the quit itself marks the leave. Bolt's launcher shutdown is no sign of one: the game's own
                // watchdog stops the session that way when a host stops answering.
                var leave = new HarmonyMethod(typeof(Rejoin), nameof(BeforeShutdown));
                foreach (var method in typeof(global::Sons.Gui.PauseMenu).GetMethods())
                    if (method.Name.StartsWith("QuitTo", StringComparison.Ordinal) && method.DeclaringType == typeof(global::Sons.Gui.PauseMenu))
                        harmony.Patch(method, prefix: leave);
                // Every dedicated join, by list, address or invite, ends in one of the browser's connects, and every
                // hosted one in a lobby join.
                var join = new HarmonyMethod(typeof(Rejoin), nameof(BeforeJoin));
                foreach (var method in typeof(DedicatedServerBrowser).GetMethods())
                    if (method.Name == nameof(DedicatedServerBrowser.Connect)) harmony.Patch(method, prefix: join);
                foreach (var method in typeof(CoopLobbyBrowser).GetMethods())
                    if (method.Name == nameof(CoopLobbyBrowser.JoinLobby)) harmony.Patch(method, prefix: join);
                _hooked = true;
            }
            catch (Exception error)
            {
                warning?.Invoke("Rejoin is unavailable: " + error.Message);
            }
        }

        /// <summary>The server list is joining a dedicated server; a later rejoin of that same server uses its rules and name.</summary>
        internal static void Joining(string rulesKey, uint ip, ushort queryPort, ushort gamePort, string name)
            => _listJoin = (rulesKey, ip, queryPort, gamePort, name);

        /// <summary>Rejoins the offered server, from its dialog or the title, once the server list is up.</summary>
        internal static void Start()
        {
            if (!Offered) return;
            _attempt = _offer;
            _dropDialog = _opening = _listed = _sent = false;
            _toLan = _attempt.Dedicated && Private(_attempt.Ip);
            _wanted = true;
            _wantedAt = _rulesAt = Time.unscaledTime;
            if (_attempt.RulesKey != null) ServerBrowserBadges.Ask(_attempt.RulesKey, _attempt.RulesIp, _attempt.RulesPort);
        }

        /// <summary>
        /// Another join started: a rejoin still waiting gives way to it, and the failure dialogs that follow are that
        /// join's own. The title keeps its offer.
        /// </summary>
        internal static void Cancel()
        {
            _attempt = null;
            _wanted = _opening = _sent = _dropDialog = false;
        }

        /// <summary>The server list closed: a rejoin it hadn't started yet goes with it, as when the player backs out.</summary>
        internal static void ListClosed()
        {
            if (!_wanted || _sent) return;
            _attempt = null;
            _wanted = _opening = false;
        }

        /// <summary>
        /// Whether a disconnect dialog the game just showed offers Rejoin: the drop's own dialog, or the failure of the
        /// rejoin this player started, which a press on it tries again. A failed join to any other server never offers it.
        /// </summary>
        internal static bool Claim(bool shutdown)
        {
            if (!Offered) return false;
            if (shutdown && _dropDialog)
            {
                _dropDialog = false;
                return true;
            }
            if (_attempt == null || !_sent) return false;
            _attempt = null;
            _sent = false;
            return true;
        }

        /// <summary>The game said no (a kick, a ban, a password, a full server, another version): nothing is offered now.</summary>
        internal static void Refused()
        {
            // Only the rejoin's own join speaks for its server; another server turning the player away leaves the offer.
            if (_attempt == null || !_sent) return;
            RuntimeEntryPoint.LogSource?.LogInfo("The server turned this player away; no rejoin is offered.");
            _offer = null;
            Cancel();
        }

        internal static void Tick()
        {
            if (!_hooked || Environment.CurrentManagedThreadId != _ownerThreadId) return;
            try
            {
                var now = Time.unscaledTime;
                if (now >= _nextCapture)
                {
                    _nextCapture = now + CaptureSeconds;
                    Capture();
                }
                if (_offer != null && now >= _offerUntil)
                {
                    _offer = null;
                    Cancel();
                }
                // This game's own join check left the server with a reason to show, which is a refusal too.
                if (_sent && KickReason.Reasons != _attemptReasons) Refused();
                if (!_wanted) return;
                // A rejoin the server list never got to must not fire on some later visit to it.
                if (now - _wantedAt > OpenSeconds)
                {
                    _attempt = null;
                    _wanted = _opening = false;
                    RuntimeEntryPoint.LogSource?.LogWarning("The server list did not take the rejoin up in time; it was given up.");
                }
                else Open();
            }
            catch (Exception error)
            {
                Cancel();
                RuntimeEntryPoint.LogSource?.LogWarning("Rejoin stopped: " + error.Message);
            }
        }

        /// <summary>
        /// Joins the server a rejoin is waiting on, once the server list has it and its rules, or the wait for them is over.
        /// </summary>
        internal static void Browse(GameServerBrowserGui gui)
        {
            if (!_wanted || _attempt == null) return;
            var target = _attempt;
            var now = Time.unscaledTime;
            if (target.Dedicated && !_listed)
            {
                // The listing carries the server's current world, Steam id and limit, which a restart can change.
                if (_toLan)
                {
                    _toLan = false;
                    MultiplayerScreen.ShowLan();
                }
                var listed = MultiplayerScreen.FindDedicated(target.Ip, target.Port);
                if (listed == null && now - _wantedAt < ListSeconds) return;
                _listed = true;
                if (listed != null && !Follow(target, listed, now)) return;
            }
            // A server that never answers is joined as the game would, as the server list does.
            if (target.RulesKey != null && !ServerBrowserBadges.TryRead(target.RulesKey, out _, out _)
                && now - _rulesAt < RulesSeconds)
                return;
            string content = null, negotiation = null;
            if (target.RulesKey != null && ServerBrowserBadges.TryRead(target.RulesKey, out _, out var details))
            {
                content = details?.Content ?? "";
                negotiation = details?.Negotiation;
            }
            _wanted = _opening = false;
            _sent = true;
            _attemptReasons = KickReason.Reasons;
            try
            {
                ContentAgreement.Joining(content, negotiation);
                LoadingScreen.Intend(LoadingIntent.FromServerRow(target.Name, "", -1, target.Dedicated ? target.MaxPlayers : -1,
                    target.Dedicated));
                // The game's own connect, as its server row and its invites call it. The password the player gave last
                // time goes with it; the server still checks it.
                _connecting = true;
                if (target.Dedicated)
                    gui._dedicatedBrowser.Connect(new CSteamID(target.ServerId), target.Ip, target.Port, target.GameId,
                        target.MaxPlayers, false, target.Password, 0);
                else gui._lobbyBrowser.JoinLobby(target.Lobby);
                RuntimeEntryPoint.LogSource?.LogInfo("Rejoining " + target.Name + ".");
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Could not rejoin " + target.Name + ": " + error.Message);
            }
            finally
            {
                _connecting = false;
            }
        }

        // Takes the listing's current details. A server that now runs another world is not rejoined: the game id picks
        // this player's own save for the world, and the old one would be loaded into the new.
        private static bool Follow(Target target, DedicatedServerInfo listed, float now)
        {
            var world = listed.Guid;
            if (!string.IsNullOrEmpty(world) && world != target.GameId)
            {
                RuntimeEntryPoint.LogSource?.LogInfo(target.Name + " runs another world since this player left; it is not rejoined.");
                SystemDialogs.Notify(FrontEnd.Text["kick.title.couldNotJoin"], FrontEnd.Text.Get("net.rejoin.otherWorld",
                    ("server", target.Name)), DialogTone.Problem);
                _offer = null;
                Cancel();
                return false;
            }
            var id = listed.ServerId.m_SteamID;
            if (id != 0) target.ServerId = id;
            if (listed.MemberLimit > 0) target.MaxPlayers = listed.MemberLimit;
            var name = listed.CleanName;
            if (!string.IsNullOrWhiteSpace(name)) target.Name = Short(name);
            if (target.RulesKey != null) return true;
            var address = listed.NetAddr;
            target.RulesIp = address.GetIP();
            target.RulesPort = address.GetQueryPort();
            target.RulesKey = ServerBrowserBadges.IpText(target.RulesIp) + ":" + target.RulesPort;
            ServerBrowserBadges.Ask(target.RulesKey, target.RulesIp, target.RulesPort);
            _rulesAt = now;
            return true;
        }

        // The game lists a server on a private address on its LAN tab only.
        private static bool Private(string ip)
        {
            if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork) return false;
            var bytes = address.GetAddressBytes();
            return bytes[0] is 10 or 127 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 169 && bytes[1] == 254;
        }

        // A rejoin goes through the server list, which the title's Multiplayer opens; the list joins once it is up.
        private static void Open()
        {
            if (_opening || MultiplayerScreen.IsOpen || !FrontEnd.AtTitle || GameMenus.OtherSceneOpen || GameMenus.DialogOpen
                || SystemDialogs.Open || LoadingScreen.Covering)
                return;
            _opening = true;
            GameMenus.Join();
            FrontEnd.StepAside();
        }

        // The server is known for sure only once the world is in; a refused or failed join never gets this far.
        private static void Capture()
        {
            if (!BoltNetwork.isRunning)
            {
                // Over without Bolt reporting it: its connection's address may come back as another one's.
                _joined = null;
                _server = IntPtr.Zero;
                return;
            }
            // Hosting or playing alone: nothing waits on a rejoin's dialogs any more.
            if (BoltNetwork.isServer)
            {
                if (_attempt != null || _dropDialog) Cancel();
                return;
            }
            if (!BoltNetwork.isClient || !BoltNetwork.isConnected || !GameRuntime.IsWorldLoaded) return;
            var server = BoltNetwork.server;
            if (server == null || server.Pointer == _server) return;
            var target = Read();
            if (target == null) return;
            _joined = target;
            _server = server.Pointer;
            _reasons = KickReason.Reasons;
            _leaving = false;
            // Back in a game, by this rejoin or any other way: nothing is left to offer.
            _offer = null;
            Cancel();
        }

        private static Target Read()
        {
            var name = LoadingScreen.Entered?.Name;
            if (GameServerManager.IsDedicatedClient)
            {
                var ip = GameServerManager.IpAddress;
                var port = GameServerManager.GamePort;
                // The game id picks this player's own save for the server's world, so without it there is no rejoin.
                var gameId = GameSetupManager.GetGameId();
                if (string.IsNullOrEmpty(ip) || port == 0 || string.IsNullOrEmpty(gameId)) return null;
                var target = new Target
                {
                    Ip = ip,
                    Port = port,
                    ServerId = GameServerManager.ServerId.m_SteamID,
                    GameId = gameId,
                    MaxPlayers = GameServerManager.MaxPlayers,
                    Password = GameServerManager.Password ?? ""
                };
                // Joined from the server list: that row's rules and name belong to exactly this address and game port.
                // A join by address or invite has neither, and its rejoin looks the server up in the list instead.
                var list = _listJoin;
                _listJoin = default;
                if (list.Key != null && list.GamePort == port && list.Key.StartsWith(ip + ":", StringComparison.Ordinal))
                {
                    target.RulesKey = list.Key;
                    target.RulesIp = list.Ip;
                    target.RulesPort = list.QueryPort;
                    if (string.IsNullOrWhiteSpace(name)) name = list.Name;
                }
                target.Name = Short(string.IsNullOrWhiteSpace(name) ? ip + ":" + port : name);
                return target;
            }
            var lobby = CoopLobbyManager.GetActiveInstance();
            var id = lobby != null ? lobby.LobbyId.m_SteamID : 0;
            if (id == 0) return null;
            if (string.IsNullOrWhiteSpace(name)) name = lobby.Name;
            if (string.IsNullOrWhiteSpace(name)) name = SteamFriends.GetFriendPersonaName(lobby.OwnerSteamId);
            return new Target { Name = Short(name ?? ""), Lobby = id };
        }

        private static string Short(string name)
        {
            name = Tags.Replace(name, "").Trim();
            return name.Length <= MaxNameLength ? name : name[..(MaxNameLength - 1)].TrimEnd() + "…";
        }

        // Bolt reports the connection gone before the game's callbacks take the player to the title and its dialog.
        private static void BeforeDisconnected(BoltConnection __instance)
        {
            if (_joined == null || Environment.CurrentManagedThreadId != _ownerThreadId) return;
            try
            {
                if (__instance == null || __instance.Pointer != _server) return;
                var target = _joined;
                _joined = null;
                _server = IntPtr.Zero;
                var drop = Classify(__instance, target);
                if (drop is Drop.Lost or Drop.TimedOut or Drop.ServerClosed)
                {
                    _offer = target;
                    _offerUntil = Time.unscaledTime + OfferSeconds;
                    _dropDialog = true;
                    RuntimeEntryPoint.LogSource?.LogInfo("The connection to " + target.Name + " ended (" + drop
                        + "); rejoin is offered for " + (int)(OfferSeconds / 60) + " minutes.");
                }
                else
                {
                    _offer = null;
                    RuntimeEntryPoint.LogSource?.LogInfo("Left " + target.Name + " (" + drop + "); no rejoin is offered.");
                }
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The lost connection could not be read for a rejoin: " + error.Message);
            }
        }

        // A client that shuts its own session down while still connected is leaving, whichever way it took.
        private static void BeforeShutdown()
        {
            if (_joined != null && Environment.CurrentManagedThreadId == _ownerThreadId) _leaving = true;
        }

        private static void BeforeJoin()
        {
            if (!_connecting && Environment.CurrentManagedThreadId == _ownerThreadId) Cancel();
        }

        private static Drop Classify(BoltConnection connection, Target target)
        {
            if (_leaving || PauseHost.Current is { _quitting: true }) return Drop.Left;
            var token = connection.DisconnectToken;
            if (token != null)
            {
                // A kick or ban token, a refused join's, or one this Loader doesn't know: the server meant it.
                var shutdown = token.TryCast<ServerShutdownToken>();
                if (shutdown == null || shutdown.Message == DebugDisconnect) return Drop.Removed;
                return target.Dedicated ? Drop.ServerClosed : Drop.HostEnded;
            }
            if (KickReason.Reasons != _reasons) return Drop.Removed;
            var udp = connection._udp;
            var socket = udp is null || udp.WasCollected ? null : udp.Socket;
            var config = socket is null || socket.WasCollected ? null : socket.Config;
            if (config is null || config.WasCollected) return Drop.Lost;
            // The transport's own millisecond clock, which wraps; the same test the server's player log uses. The game's
            // own watchdog usually gives up first, which reads as Lost; for the offer the two are the same.
            var quiet = unchecked(socket.GetCurrentTime() - udp.RecvTime);
            return quiet >= config.ConnectionTimeout ? Drop.TimedOut : Drop.Lost;
        }
    }
}
