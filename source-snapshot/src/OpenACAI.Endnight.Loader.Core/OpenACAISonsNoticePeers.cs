// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;

namespace OpenACAI.Sons
{
    public static partial class OpenACAISonsServerNotices
    {
        private static Queue<PeerChange> _peerChanges = new();

        /// <summary>
        /// Queued server-side observation after the acknowledgement send returns and the peer is published.
        /// Runs on the game thread. The handle may already be unavailable; this is not delivery or authentication proof.
        /// </summary>
        public static event Action<OpenACAISonsNoticePeer> PeerAvailable;

        /// <summary>
        /// Queued server-side retirement of a previously acknowledged peer, after pending notices are completed.
        /// Runs on the game thread and does not infer a native disconnect reason.
        /// </summary>
        public static event Action<OpenACAISonsNoticePeer> PeerUnavailable;

        private static void EnqueuePeerChange(Peer peer, bool available)
        {
            if (peer.Acknowledged)
                _peerChanges.Enqueue(new PeerChange(peer.Snapshot, available));
        }

        private static void DispatchPeerChanges()
        {
            if (_peerChanges.Count == 0)
                return;

            // Match session events: callbacks append to a fresh batch for a later tick.
            var batch = _peerChanges;
            _peerChanges = new Queue<PeerChange>();
            foreach (var change in batch)
            {
                if (change.Available && !HooksInstalled)
                    continue;
                var handlers = change.Available ? PeerAvailable : PeerUnavailable;
                if (handlers == null)
                    continue;

                foreach (Action<OpenACAISonsNoticePeer> handler in handlers.GetInvocationList())
                {
                    try { handler(change.Peer); }
                    catch (Exception error) { ReportFailure(error); }
                }
            }
        }

        private readonly record struct PeerChange(OpenACAISonsNoticePeer Peer, bool Available);
    }
}
