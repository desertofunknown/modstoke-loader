// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UdpKit;

namespace Modstoke.Sons
{
    public static partial class ControlTransport
    {
        private sealed class Run
        {
            internal readonly Guid Id = Guid.NewGuid();
            internal readonly IntPtr SocketPointer;
            internal readonly UdpChannelName Channel;
            internal readonly Dictionary<IntPtr, PeerRecord> Peers = new();
            // Connection pointer to ID for connections that opened while session tracking was paused.
            internal readonly Dictionary<IntPtr, uint> Deferred = new();
            internal IReadOnlyList<ControlPeer> PeerSnapshot;
            internal bool Failed;

            internal Run(IntPtr socketPointer, UdpChannelName channel)
            {
                SocketPointer = socketPointer;
                Channel = channel;
            }
        }

        private sealed class PeerRecord
        {
            internal readonly ControlPeer Peer;
            internal bool Open = true;
            internal int Sends;
            internal int Receives;
            internal int Pending;
            internal bool ModAdmissionBudget;
            internal int ModAdmissionSends;
            internal int ModAdmissionReceives;
            internal PeerRecord(ControlPeer peer) => Peer = peer;
        }

        private readonly record struct Frame(ControlPeer Peer, byte[] Payload);
        private readonly record struct PeerEvent(ControlPeer Peer, bool Opened);

        // State helpers run under Gate. No native calls or subscriber dispatch occur here.
        private static bool EnqueueEvent(ControlPeer peer, bool opened)
        {
            if (Events.Count >= MaxPendingEvents)
                return false;
            Events.Enqueue(new PeerEvent(peer, opened));
            return true;
        }

        private static void Retire(Run run, PeerRecord record)
        {
            if (!record.Open)
                return;
            record.Open = false;
            run.PeerSnapshot = null;
            EnqueueEvent(record.Peer, false);
        }

        private static void FailPeer(ControlPeer peer)
        {
            lock (Gate)
            {
                var record = Find(peer);
                if (record != null) Retire(_run, record);
            }
        }

        private static void FailRun()
        {
            _state = ControlState.Failed;
            if (_run == null || _run.Failed)
                return;
            _run.Failed = true;
            foreach (var record in _run.Peers.Values) Retire(_run, record);
            Incoming.Clear();
        }

        private static void PruneClosedPeers()
        {
            if (_run == null)
                return;
            List<IntPtr> closed = null;
            foreach (var entry in _run.Peers)
                if (!entry.Value.Open) (closed ??= new List<IntPtr>()).Add(entry.Key);
            if (closed != null)
            {
                _run.PeerSnapshot = null;
                foreach (var pointer in closed) _run.Peers.Remove(pointer);
            }
        }

        private static void PublishPeerEvent(PeerEvent item)
        {
            var handlers = item.Opened ? Opened : Closed;
            if (handlers == null)
                return;
            foreach (var handler in Delegate.EnumerateInvocationList(handlers))
            {
                if (item.Opened && Resolve(item.Peer) == null)
                    break;
                try { handler(item.Peer); }
                catch (Exception error)
                {
                    if (item.Opened) FailPeer(item.Peer);
                    ReportFailure(error);
                }
            }
        }

        private static void PublishMessage(Frame frame)
        {
            var handlers = Received;
            if (handlers == null)
                return;
            var message = new ControlMessage(frame.Peer, frame.Payload);
            foreach (var handler in Delegate.EnumerateInvocationList(handlers))
            {
                if (Resolve(frame.Peer) == null)
                    break;
                try { handler(message); }
                catch (Exception error)
                {
                    FailPeer(frame.Peer);
                    ReportFailure(error);
                }
            }
        }
    }
}
