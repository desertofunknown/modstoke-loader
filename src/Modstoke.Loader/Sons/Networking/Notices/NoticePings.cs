// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;

namespace Modstoke.Sons
{
    public static partial class ServerNotices
    {
        // A few seconds is fresh enough for a list a player reads, and a frame of a few hundred bytes that often is
        // nothing next to Bolt's own traffic.
        private const long PingShareMs = 3000;
        private static readonly List<Peer> PingPeers = new();
        private static readonly List<(ulong, int, int)> PingList = new();
        private static long _nextPingShare;
        private static bool _clientSupportsPings;

        // The host's view of every client's link, for clients that asked in their hello, so their Players page can show
        // the others' pings. A client gets the list again only when it changed.
        private static void SharePings(long now)
        {
            if (now < _nextPingShare || !BoltNetwork.isServer)
                return;
            _nextPingShare = now + PingShareMs;
            PingPeers.Clear();
            foreach (var peer in ServerPeers.Values)
            {
                if (peer.Pings && peer.Acknowledged)
                    PingPeers.Add(peer);
            }
            if (PingPeers.Count == 0)
                return;

            PingList.Clear();
            foreach (var session in ServerSessions.Sessions)
            {
                if (session.RegisteredSteamId is ulong id && id != 0 && ConnectionQuality.TryRead(session, out var link))
                    PingList.Add((id, link.RoundTripMs, (int)Math.Round(link.Loss * 100)));
            }
            var text = NoticeProtocol.WritePings(PingList);
            // Resolving a peer can forget it, so the peers were copied out of the registry first.
            foreach (var peer in PingPeers)
            {
                if (peer.SentPings == text || ResolvePeer(peer.Snapshot, out _, out var connection) != NoticeSendResult.Queued)
                    continue;
                SendFrame(connection, "pings", peer.Token, text);
                peer.SentPings = text;
            }
        }

        private static void ReceivePings(NoticeProtocol.Frame frame)
        {
            if (Volatile.Read(ref _clientSupportsPings))
                ConnectionQuality.Share(frame.Pings);
        }
    }
}
