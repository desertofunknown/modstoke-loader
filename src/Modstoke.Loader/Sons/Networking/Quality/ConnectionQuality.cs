// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// How each Bolt connection is doing: the transport's smoothed round trip, the share of sent packets Bolt was told
    /// were lost, and how long since anything arrived. A client also keeps a verdict on its link to the server, held
    /// with hysteresis so it changes only when the link really has.
    /// </summary>
    public static class ConnectionQuality
    {
        // A verdict waits for its condition to hold, and clears only once the link is clearly better for longer.
        internal const int HighPingMs = 250, InterruptedMs = 1000;
        internal const float LossShare = 0.05f;
        private const int ClearPingMs = 200, FlowingMs = 250;
        private const float ClearLossShare = 0.02f;
        private const float PingEnter = 3f, PingLeave = 5f, LossEnter = 2f, LossLeave = 5f, InterruptedLeave = 1.5f;
        // Loss is counted in one-second buckets over the last five seconds, once enough packets went out to mean anything.
        private const int Window = 5, MinPackets = 20, MaxLinks = 256;
        private static readonly Dictionary<IntPtr, Link> Links = new();
        private static readonly Dictionary<ulong, LinkQuality> Shared = new();
        private static readonly List<IntPtr> Idle = new();
        private static int _ownerThreadId;
        private static bool _failed, _highPing, _lossy, _silent;
        private static float _nextSecond, _pingSince, _lossSince, _silentSince;
        private static IntPtr _server;
        private static ConnectionHealth _health;

        /// <summary>This client's link to its server, held steady; Offline when this process is not a connected client.</summary>
        public static ConnectionHealth Health => _health;

        /// <summary>Runs on the game thread when <see cref="Health"/> changes.</summary>
        public static event Action<ConnectionHealth> HealthChanged;

        /// <summary>This client's link to its server, read now on the game thread.</summary>
        public static bool TryReadServer(out LinkQuality quality)
        {
            quality = default;
            if (!OnOwnerThread) return false;
            try
            {
                return BoltNetwork.isRunning && BoltNetwork.isClient && BoltNetwork.isConnected
                    && TryRead(BoltNetwork.server, out quality);
            }
            catch (Exception error)
            {
                NetworkFailures.Report("Connection quality read", error);
                return false;
            }
        }

        /// <summary>A connected client's link as this server sees it, read now on the game thread.</summary>
        public static bool TryRead(ServerSession session, out LinkQuality quality)
        {
            quality = default;
            if (!OnOwnerThread) return false;
            try
            {
                return TryRead(ServerSessions.ResolveCurrent(session), out quality);
            }
            catch (Exception error)
            {
                NetworkFailures.Report("Connection quality read", error);
                return false;
            }
        }

        /// <summary>
        /// Another player's link as a Modstoke host last shared it with this client, a few seconds old at most. Only
        /// hosts running this Loader share it, with the clients that asked; QuietMs is 0. Game thread only.
        /// </summary>
        public static bool TryReadShared(ulong steamId, out LinkQuality quality)
        {
            quality = default;
            return OnOwnerThread && Shared.TryGetValue(steamId, out quality);
        }

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.CompareExchange(ref _ownerThreadId, Environment.CurrentManagedThreadId, 0) != 0) return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                warning?.Invoke("Connection quality reads no packet loss: Harmony support is unavailable.");
                return;
            }
            try
            {
                var delivered = AccessTools.Method(typeof(BoltConnection), nameof(BoltConnection.PacketDelivered));
                var lost = AccessTools.Method(typeof(BoltConnection), nameof(BoltConnection.PacketLost));
                if (delivered == null || lost == null)
                    throw new MissingMethodException("Bolt's packet delivery callbacks are unavailable.");
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-connection-quality");
                harmony.Patch(delivered, postfix: new HarmonyMethod(typeof(ConnectionQuality), nameof(AfterDelivered)));
                harmony.Patch(lost, postfix: new HarmonyMethod(typeof(ConnectionQuality), nameof(AfterLost)));
            }
            catch (Exception error)
            {
                warning?.Invoke("Connection quality reads no packet loss: " + error.Message);
            }
        }

        internal static void Tick()
        {
            if (!OnOwnerThread) return;
            if (_failed)
            {
                // The last verdict is withdrawn here, on the game thread and outside Bolt's packet callbacks.
                if (_health != ConnectionHealth.Offline) Settle(ConnectionHealth.Offline, default);
                return;
            }
            try
            {
                var now = Time.unscaledTime;
                var second = now >= _nextSecond;
                if (second)
                {
                    _nextSecond = now + 1f;
                    Roll();
                }
                Judge(now, second);
            }
            catch (Exception error)
            {
                Fail(error);
            }
        }

        /// <summary>Replaces what the host shares, from its latest list; null when this client is no longer in its game.</summary>
        internal static void Share(IReadOnlyList<(ulong SteamId, int RoundTripMs, int LossPercent)> pings)
        {
            Shared.Clear();
            if (pings == null) return;
            foreach (var (id, ms, loss) in pings) Shared[id] = new LinkQuality(ms, loss / 100f, 0);
        }

        private static bool OnOwnerThread => Environment.CurrentManagedThreadId == _ownerThreadId;

        private static bool TryRead(BoltConnection connection, out LinkQuality quality)
        {
            quality = default;
            if (connection is null || connection.WasCollected) return false;
            var udp = connection._udp;
            var socket = udp is null || udp.WasCollected ? null : udp.Socket;
            if (socket is null || socket.WasCollected) return false;
            // The transport's own millisecond clock, which wraps. The arrival is read first: a packet landing between
            // the two reads then still reads as just now rather than as far in the future.
            var received = udp.RecvTime;
            var quiet = unchecked((int)(socket.GetCurrentTime() - received));
            var ping = udp.NetworkPing;
            // A link on the same PC or network answers in well under a millisecond: that reads as 1 ms, not as no reading.
            var ms = float.IsFinite(ping) && ping > 0 ? Math.Max(1, (int)Math.Round(ping * 1000)) : 0;
            quality = new LinkQuality(ms, LossOf(connection.Pointer), Math.Max(0, quiet));
            return true;
        }

        private static float LossOf(IntPtr connection)
        {
            if (!Links.TryGetValue(connection, out var link)) return 0f;
            int delivered = 0, lost = 0;
            for (var i = 0; i < Window; i++)
            {
                delivered += link.Delivered[i];
                lost += link.Lost[i];
            }
            var sent = delivered + lost;
            return sent < MinPackets ? 0f : (float)lost / sent;
        }

        private static void ForgetLoss(IntPtr connection)
        {
            if (!Links.TryGetValue(connection, out var link)) return;
            Array.Clear(link.Delivered);
            Array.Clear(link.Lost);
        }

        // Inside Bolt's poll on the game thread, once for every packet a connection sent.
        private static void AfterDelivered(BoltConnection __instance) => Count(__instance, false);

        private static void AfterLost(BoltConnection __instance) => Count(__instance, true);

        private static void Count(BoltConnection connection, bool lost)
        {
            if (_failed || !OnOwnerThread || connection is null) return;
            try
            {
                var pointer = connection.Pointer;
                if (!Links.TryGetValue(pointer, out var link))
                {
                    if (Links.Count >= MaxLinks) return;
                    Links.Add(pointer, link = new Link());
                }
                link.Heard = true;
                if (lost) link.Lost[link.Slot]++;
                else link.Delivered[link.Slot]++;
            }
            catch (Exception error)
            {
                Fail(error);
            }
        }

        // A connection that sent nothing for a whole window has gone; its native address may come back as another one.
        private static void Roll()
        {
            if (!BoltNetwork.isRunning)
            {
                Links.Clear();
                return;
            }
            Idle.Clear();
            foreach (var (pointer, link) in Links)
            {
                link.Quiet = link.Heard ? 0 : link.Quiet + 1;
                link.Heard = false;
                if (link.Quiet >= Window)
                {
                    Idle.Add(pointer);
                    continue;
                }
                link.Slot = (link.Slot + 1) % Window;
                link.Delivered[link.Slot] = link.Lost[link.Slot] = 0;
            }
            foreach (var pointer in Idle) Links.Remove(pointer);
        }

        // Silence is judged every frame, so the warning comes within a second; ping and loss once a second.
        private static void Judge(float now, bool second)
        {
            var server = BoltNetwork.isRunning && BoltNetwork.isClient && BoltNetwork.isConnected ? BoltNetwork.server : null;
            if (server == null || !TryRead(server, out var link))
            {
                Settle(ConnectionHealth.Offline, default);
                return;
            }
            if (server.Pointer != _server)
            {
                _server = server.Pointer;
                _highPing = _lossy = _silent = false;
                _pingSince = _lossSince = _silentSince = now;
            }
            var wasSilent = _silent;
            _silent = Hold(_silent, link.QuietMs >= InterruptedMs, link.QuietMs < FlowingMs, ref _silentSince, now, 0f, InterruptedLeave);
            // Packets sent into an outage are reported lost once it ends. That loss is the outage, already shown as one,
            // not a lossy link, so the count starts again when the link comes back.
            if (_silent || wasSilent)
            {
                ForgetLoss(server.Pointer);
                _lossy = false;
                _lossSince = now;
            }
            if (second)
            {
                _highPing = Hold(_highPing, link.RoundTripMs >= HighPingMs, link.RoundTripMs <= ClearPingMs, ref _pingSince, now,
                    PingEnter, PingLeave);
                _lossy = Hold(_lossy, link.Loss >= LossShare, link.Loss <= ClearLossShare, ref _lossSince, now, LossEnter, LossLeave);
            }
            Settle(_silent ? ConnectionHealth.Interrupted : _lossy ? ConnectionHealth.PacketLoss
                : _highPing ? ConnectionHealth.HighPing : ConnectionHealth.Good, link);
        }

        // A flag goes up once its bad condition has held for enter seconds, and down once its good one has held for leave.
        private static bool Hold(bool raised, bool bad, bool good, ref float since, float now, float enter, float leave)
        {
            if (!(raised ? good : bad))
            {
                since = now;
                return raised;
            }
            if (now - since < (raised ? leave : enter)) return raised;
            since = now;
            return !raised;
        }

        private static void Settle(ConnectionHealth health, LinkQuality link)
        {
            if (health == ConnectionHealth.Offline)
            {
                _server = IntPtr.Zero;
                _highPing = _lossy = _silent = false;
            }
            if (health == _health) return;
            var was = _health;
            _health = health;
            if (health != ConnectionHealth.Offline && was != ConnectionHealth.Offline)
                RuntimeEntryPoint.LogSource?.LogInfo("Connection to the server: " + health + " (" + link.RoundTripMs + " ms, "
                    + Math.Round(link.Loss * 100) + "% loss, quiet " + link.QuietMs + " ms).");
            Modding.Dispatch("ConnectionQuality.HealthChanged", HealthChanged, health);
        }

        private static void Fail(Exception error)
        {
            _failed = true;
            Links.Clear();
            NetworkFailures.Report("Connection quality", error);
        }

        private sealed class Link
        {
            internal readonly int[] Delivered = new int[Window];
            internal readonly int[] Lost = new int[Window];
            internal int Slot, Quiet;
            internal bool Heard;
        }
    }

    public enum ConnectionHealth { Offline, Good, HighPing, PacketLoss, Interrupted }

    /// <summary>
    /// One reading of a link. RoundTripMs is the transport's smoothed round trip, Loss the share of sent packets reported
    /// lost over the last five seconds (0 until enough went out), QuietMs the time since anything arrived.
    /// </summary>
    public readonly record struct LinkQuality(int RoundTripMs, float Loss, int QuietMs)
    {
        /// <summary>Past the HUD's warning line: 250 ms or more, 5% loss or more, or a second without anything arriving.</summary>
        public bool Poor => RoundTripMs >= ConnectionQuality.HighPingMs || Loss >= ConnectionQuality.LossShare
            || QuietMs >= ConnectionQuality.InterruptedMs;
    }
}
