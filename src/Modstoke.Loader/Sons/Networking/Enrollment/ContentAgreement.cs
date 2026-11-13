// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Modstoke.Loader;
using Sons.Multiplayer;
using Steamworks;

namespace Modstoke.Sons
{
    /// <summary>
    /// Checks at join that client and server declare the same networked mod content: the networked item id
    /// ranges mods reserve at startup, which decide item ids, pickup prefab ids and weapon attachments. A server
    /// with content refuses clients that differ, and by default also clients that never take part (vanilla),
    /// with a reason in the disconnect notice. Setting allowVanillaClients=true in the loader settings lets
    /// vanilla clients in; Modstoke clients are still checked. Servers without content are unaffected, and a
    /// client joins a server that declares no content (a vanilla one among them) exactly as the game does.
    /// </summary>
    public static class ContentAgreement
    {
        /// <summary>Enrollment capability bit for this check (ModuleStorage uses 1&lt;&lt;62, ModAdmission 1&lt;&lt;63).</summary>
        public const ulong Capability = 1UL << 61;

        private const string AllowVanillaKey = "allowVanillaClients";
        // The Steam lobby key a hosting Modstoke game puts its content fingerprint under, as a dedicated server's rules do.
        private const string LobbyContentKey = "ms_content";
        private const double AnswerSeconds = 15;
        private const double RetrySeconds = 1;
        private const double JoiningSeconds = 300;
        private const double LobbySeconds = 1;
        private const int MaxSends = 8;
        private static readonly Dictionary<Guid, Peer> Peers = new();
        private static Declaration _local;
        private static bool _server, _requireClients, _active;
        private static string _joining, _labelledContent;
        private static double _joiningAt, _nextLobbyCheck;
        private static ulong _labelledLobby;

        public enum Status { NotChecked, Pending, Agreed, Differs }

        private sealed class Peer
        {
            internal ControlPeer Control;
            internal Status State = Status.Pending;
            internal string RemoteSummary = "";
            internal double Deadline;
            internal double NextSend;
            internal int Sends;
            internal bool Noticed;
        }

        private sealed record Declaration(byte[] Digest, int Count, string Summary);

        /// <summary>Whether this process declares networked mod content.</summary>
        public static bool HasContent => Capture().Count > 0;

        /// <summary>The summary sent with refusals, such as "modstoke.optics 1900-1919".</summary>
        public static string Summary => Capture().Summary;

        /// <summary>A short form of the content digest that servers publish before a join, or "" without content.</summary>
        internal static string Fingerprint
        {
            get
            {
                var declaration = Capture();
                return declaration.Count == 0 ? "" : declaration.Count + ":" + Convert.ToHexString(declaration.Digest, 0, 8);
            }
        }

        /// <summary>Whether this server lets vanilla clients in despite its content (allowVanillaClients).</summary>
        internal static bool AllowsVanilla => RuntimePreferences.ReadBoolean(AllowVanillaKey, false);

        /// <summary>The agreement state with one connected peer, or NotChecked when it was not checked.</summary>
        public static Status Read(ControlPeer peer)
            => peer != null && Peers.TryGetValue(peer.PeerId, out var entry) && ReferenceEquals(entry.Control, peer)
                ? entry.State : Status.NotChecked;

        // Mods reserve ids at startup; once any networked range exists, the start hook must be in place.
        internal static void EnsureHooks()
        {
            if (_hooksPrepared || ++_hookCheck % 60 != 0) return;
            foreach (var range in ItemIds.Reservations)
                if (range.Band == ItemIdBand.Networked && !range.Implicit)
                {
                    _hooksPrepared = ControlTransport.PrepareHooks();
                    return;
                }
        }

        private static bool _hooksPrepared;
        private static int _hookCheck;

        // Runs from the Bolt start hook, after every other selection and before the negotiation channel exists.
        internal static void PrepareForStart(bool server)
        {
            Peers.Clear();
            _active = false;
            _local = Capture();
            if (_local.Count == 0) return;
            _server = server;
            // A client takes part only with a server that runs the check too. One that declares no content has no
            // channel for it, so the connection is left exactly as the game makes it.
            if (!_server && !ServerDeclaresContent())
            {
                RuntimeEntryPoint.LogSource?.LogInfo("This server declares no Modstoke mod content; joining it as the game does.");
                return;
            }
            _requireClients = _server && !RuntimePreferences.ReadBoolean(AllowVanillaKey, false);
            if (!ControlTransport.SelectForStart() || !ClientEnrollment.AddContentForStart(_server, _requireClients))
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The mod content check could not be enabled for this session.");
                return;
            }
            _active = true;
            RuntimeEntryPoint.LogSource?.LogInfo((_server
                ? _requireClients ? "Clients must match this server's mod content: " : "Modstoke clients must match this server's mod content (vanilla clients allowed): "
                : "Checking mod content with the server: ") + _local.Summary + ".");
        }

        /// <summary>
        /// The content a dedicated server declared in the rules the server browser read, as the player joins it: its
        /// fingerprint, "" for a server that declares none, or null when the browser did not know.
        /// </summary>
        internal static void Joining(string declared)
        {
            _joining = declared;
            _joiningAt = Now;
        }

        // A hosted game is joined through its host's Steam lobby, which a Modstoke host labels with its content; a
        // dedicated server through the browser, which read its rules. A server neither says anything about is taken
        // to have none.
        private static bool ServerDeclaresContent()
        {
            var lobby = LobbyContent();
            if (lobby != null) return lobby.Length > 0;
            return !string.IsNullOrEmpty(_joining) && Now - _joiningAt < JoiningSeconds;
        }

        // The content label of the Steam lobby this game is in, "" when it has none, or null outside a lobby.
        private static string LobbyContent()
        {
            try
            {
                var lobby = CoopLobbyManager.GetActiveInstance();
                return lobby == null ? null : SteamMatchmaking.GetLobbyData(lobby.LobbyId, LobbyContentKey) ?? "";
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Labels the Steam lobby this game hosts with its content fingerprint, so a joining Modstoke client knows
        /// before Bolt starts whether the host runs the check. Runs every frame; looks once a second.
        /// </summary>
        internal static void LabelHostedLobby()
        {
            var now = Now;
            if (now < _nextLobbyCheck) return;
            _nextLobbyCheck = now + LobbySeconds;
            try
            {
                var lobby = CoopLobbyManager.GetActiveInstance();
                if (lobby == null || !lobby.IsOwner) return;
                var id = lobby.LobbyId.m_SteamID;
                var content = Fingerprint;
                if (id == _labelledLobby && content == _labelledContent) return;
                if (!SteamMatchmaking.SetLobbyData(lobby.LobbyId, LobbyContentKey, content)) return;
                _labelledLobby = id;
                _labelledContent = content;
            }
            catch (Exception)
            {
                // Without the label a Modstoke client joins as it would join a vanilla host; it is tried again shortly.
            }
        }

        internal static void Enrolled(ControlPeer peer, ulong offered)
        {
            if (!_active || !_server || peer == null || (offered & Capability) == 0) return;
            Peers[peer.PeerId] = new Peer { Control = peer, Deadline = Now + AnswerSeconds, NextSend = Now };
        }

        internal static void Closed(ControlPeer peer)
        {
            if (peer != null) Peers.Remove(peer.PeerId);
        }

        /// <summary>Server gate input: Hold while the check runs, Reject when it failed, otherwise Allow.</summary>
        internal static bool? Admits(ControlPeer peer)
        {
            if (!_active || !_server || peer == null) return true;
            if (!Peers.TryGetValue(peer.PeerId, out var entry) || !ReferenceEquals(entry.Control, peer))
                return _requireClients ? false : true;
            return entry.State switch
            {
                Status.Agreed => true,
                Status.Pending => null,
                _ => false
            };
        }

        /// <summary>The disconnect notice for a refused peer on a server with content, or null when not ours.</summary>
        internal static string RefusalNotice(ControlPeer peer)
        {
            if (!_active || !_server) return null;
            var state = Read(peer);
            // A client that agreed was refused for another reason, which is not ours to explain.
            if (state == Status.Agreed) return null;
            var differs = state == Status.Differs;
            if (!differs && !_requireClients) return null;
            return Notice(differs, _local.Summary);
        }

        private static string Notice(bool differs, string serverSummary)
            => Fit((differs ? "Your mods don't match this server's. " : "This server uses mods with their own items. ")
                + "Install the same mods to join: " + serverSummary + ".");

        internal static void Tick()
        {
            if (!_active || !_server || Peers.Count == 0) return;
            var now = Now;
            List<Guid> gone = null;
            foreach (var entry in Peers.Values)
            {
                if (!entry.Control.IsCurrent)
                {
                    (gone ??= new()).Add(entry.Control.PeerId);
                    continue;
                }
                if (entry.State == Status.Pending)
                {
                    if (now >= entry.Deadline) Fail(entry, "did not answer the mod content check");
                    else if (now >= entry.NextSend && entry.Sends < MaxSends)
                    {
                        entry.Sends++;
                        entry.NextSend = now + RetrySeconds;
                        ControlTransport.Send(entry.Control, Write(_local));
                    }
                }
                // Without the gameplay gate (vanilla allowed) nothing else disconnects a differing Modstoke client.
                if (entry.State == Status.Differs && !entry.Noticed && !ClientEnrollment.GameplayGateSelected
                    && entry.Control.ServerSession is { } session)
                {
                    entry.Noticed = true;
                    ServerSessions.DisconnectWithNotice(session, RefusalNotice(entry.Control));
                }
            }
            if (gone != null) foreach (var id in gone) Peers.Remove(id);
        }

        internal static bool Receive(ControlPeer peer, ReadOnlySpan<byte> payload)
        {
            if (!TryRead(payload, out var remote)) return false;
            if (!_active) return true;
            var same = remote.Count == _local.Count && CryptographicOperations.FixedTimeEquals(remote.Digest, _local.Digest);
            if (_server)
            {
                if (!Peers.TryGetValue(peer.PeerId, out var entry) || !ReferenceEquals(entry.Control, peer)
                    || entry.State != Status.Pending) return true;
                entry.RemoteSummary = remote.Summary;
                if (same)
                {
                    entry.State = Status.Agreed;
                    return true;
                }
                Fail(entry, "has different mod content: " + (remote.Count == 0 ? "none" : remote.Summary));
                return true;
            }
            // The client answers every server frame, since any one of them may have been the first to arrive.
            ControlTransport.Send(peer, Write(_local));
            if (!same && !Peers.ContainsKey(peer.PeerId))
            {
                Peers[peer.PeerId] = new Peer { Control = peer, State = Status.Differs, RemoteSummary = remote.Summary };
                RuntimeEntryPoint.LogSource?.LogWarning("This server's mod content differs from yours, so it will refuse you. "
                    + "The server has: " + remote.Summary + ". You have: " + _local.Summary + ".");
                // The kick popup shows the server's notice; this adds what the player has, for comparison.
                KickReason.Expect(Notice(true, remote.Summary),
                    "Your game has: " + (_local.Count == 0 ? "no mod content" : _local.Summary) + ".");
            }
            return true;
        }

        private static void Fail(Peer entry, string why)
        {
            entry.State = Status.Differs;
            RuntimeEntryPoint.LogSource?.LogInfo("Refusing a client that " + why + ".");
        }

        private static Declaration Capture()
        {
            var ranges = ItemIds.Reservations
                .Where(range => range.Band == ItemIdBand.Networked && !range.Implicit)
                .OrderBy(range => range.ModId, StringComparer.Ordinal).ThenBy(range => range.First)
                .ToList();
            var text = new StringBuilder();
            foreach (var range in ranges)
                text.Append(range.ModId).Append('\t').Append(range.First).Append('\t').Append(range.Last).Append('\n');
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes("modstoke.content/1\n" + text));
            var summary = string.Join(", ", ranges.GroupBy(range => range.ModId)
                .Select(group => group.Key + " " + string.Join("/", group.Select(range => range.First == range.Last
                    ? range.First.ToString() : range.First + "-" + range.Last))));
            return new Declaration(digest, ranges.Count, summary);
        }

        private static string Fit(string text)
        {
            while (Encoding.UTF8.GetByteCount(text) > ServerSessions.MaxDisconnectMessageKeyBytes)
                text = text.Substring(0, text.Length - 4) + "...";
            return text;
        }

        private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        // Wire: magic, version, count (u16), digest (32), summary length (u8), summary (UTF-8, up to 200 bytes).
        private const uint Magic = 0x4143534D;
        private const byte Version = 1;
        private const int HeaderBytes = 4 + 1 + 2 + 32 + 1;
        private const int MaxSummaryBytes = 200;

        internal static bool MatchesMagic(ReadOnlySpan<byte> payload)
            => payload.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(payload) == Magic;

        private static byte[] Write(Declaration declaration)
        {
            var summary = Encoding.UTF8.GetBytes(declaration.Summary);
            var length = Math.Min(summary.Length, MaxSummaryBytes);
            var payload = new byte[HeaderBytes + length];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, Magic);
            payload[4] = Version;
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(5), (ushort)Math.Min(declaration.Count, ushort.MaxValue));
            declaration.Digest.CopyTo(payload, 7);
            payload[39] = (byte)length;
            Array.Copy(summary, 0, payload, HeaderBytes, length);
            return payload;
        }

        private static bool TryRead(ReadOnlySpan<byte> payload, out Declaration declaration)
        {
            declaration = null;
            if (payload.Length < HeaderBytes || !MatchesMagic(payload) || payload[4] != Version) return false;
            var length = payload[39];
            if (length > MaxSummaryBytes || payload.Length != HeaderBytes + length) return false;
            string summary;
            try { summary = new UTF8Encoding(false, true).GetString(payload.Slice(HeaderBytes, length)); }
            catch (DecoderFallbackException) { return false; }
            declaration = new Declaration(payload.Slice(7, 32).ToArray(), BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(5)), summary);
            return true;
        }
    }
}
