// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Loader;
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
            // Transport pointer to frames that arrived before the peer was bound.
            internal readonly Dictionary<IntPtr, EarlyFrames> Early = new();
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

        private sealed class EarlyFrames
        {
            internal readonly uint ConnectionId;
            internal readonly List<byte[]> Frames = new();
            internal EarlyFrames(uint connectionId) => ConnectionId = connectionId;
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

    /// <summary>
    /// Failure reports for the networking layer and the server SDK. A site's first failure is logged in full;
    /// repeats are counted and summed up at most once a minute, so a failure that recurs every frame neither floods
    /// the log nor goes quiet.
    /// </summary>
    internal static class NetworkFailures
    {
        private const long SummaryMilliseconds = 60_000;
        private static readonly Dictionary<string, Site> Sites = new();

        /// <remarks>withMessage false keeps the message out where it could carry a client's token or text.</remarks>
        internal static void Report(string site, Exception error, bool withMessage = true)
            => Report(site, error.GetType().FullName, " failed ", () => site + " failed: " + (withMessage ? error.ToString()
                : error.GetType().FullName + Environment.NewLine + error.StackTrace));

        /// <summary>A failure described in words, such as a policy callback the server refused to run.</summary>
        internal static void Report(string site, string detail) => Report(site, detail, " ", () => site + ": " + detail);

        private static void Report(string site, string last, string repeated, Func<string> first)
        {
            try
            {
                string line;
                lock (Sites)
                {
                    var now = Environment.TickCount64;
                    if (!Sites.TryGetValue(site, out var entry))
                    {
                        Sites[site] = new Site { Next = now + SummaryMilliseconds };
                        line = first();
                    }
                    else
                    {
                        entry.Repeats++;
                        entry.Last = last;
                        if (now < entry.Next) return;
                        line = site + repeated + entry.Repeats + " more times since its last report (last: "
                            + entry.Last + ").";
                        entry.Repeats = 0;
                        entry.Next = now + SummaryMilliseconds;
                    }
                }
                RuntimeEntryPoint.LogSource?.LogWarning(line);
            }
            catch (Exception) { }
        }

        private sealed class Site
        {
            internal long Next;
            internal int Repeats;
            internal string Last;
        }
    }
}
