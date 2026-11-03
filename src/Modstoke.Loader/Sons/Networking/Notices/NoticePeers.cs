// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;

namespace Modstoke.Sons
{
    public static partial class ServerNotices
    {
        private static readonly Queue<PeerChange> _peerChanges = new();

        /// <summary>
        /// Queued server-side observation after the acknowledgement send returns and the peer is published.
        /// Runs on the game thread. The handle may already be unavailable; this is not delivery or authentication proof.
        /// </summary>
        public static event Action<NoticePeer> PeerAvailable;

        /// <summary>
        /// Queued server-side retirement of a previously acknowledged peer, after pending notices are completed.
        /// Runs on the game thread and does not infer a native disconnect reason.
        /// </summary>
        public static event Action<NoticePeer> PeerUnavailable;

        private static void EnqueuePeerChange(Peer peer, bool available)
        {
            if (peer.Acknowledged)
                _peerChanges.Enqueue(new PeerChange(peer.Snapshot, available));
        }

        private static void DispatchPeerChanges()
        {
            // Capture the batch boundary: changes queued by callbacks wait for a later tick.
            var count = _peerChanges.Count;
            for (var index = 0; index < count; index++)
            {
                var change = _peerChanges.Dequeue();
                if (change.Available && !HooksInstalled)
                    continue;
                var handlers = change.Available ? PeerAvailable : PeerUnavailable;
                if (handlers == null)
                    continue;

                foreach (var handler in Delegate.EnumerateInvocationList(handlers))
                {
                    try { handler(change.Peer); }
                    catch (Exception error) { ReportFailure(error); }
                }
            }
        }

        private readonly record struct PeerChange(NoticePeer Peer, bool Available);
    }
}
