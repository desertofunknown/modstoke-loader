// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;
using System.Threading;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    public static partial class ServerSessions
    {
        private static int _reportedConnectionReadFailure;

        /// <summary>
        /// Copies transport observations for an exact live remote session on the game thread.
        /// No client extension, registered identity or player entity is required.
        /// </summary>
        public static bool TryReadConnection(ServerSession session,
            out ConnectionSnapshot snapshot, out ConnectionReadStatus status)
        {
            snapshot = null;
            if (!HooksInstalled)
            {
                status = ConnectionReadStatus.Unavailable;
                return false;
            }
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                status = ConnectionReadStatus.WrongThread;
                return false;
            }

            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                {
                    status = ConnectionReadStatus.NotServer;
                    return false;
                }
                var connection = ResolveCurrent(session);
                if (connection == null)
                {
                    status = HooksInstalled ? ConnectionReadStatus.SessionUnavailable
                        : ConnectionReadStatus.Unavailable;
                    return false;
                }
                var transport = connection._udp;
                if (transport is null || transport.WasCollected)
                {
                    status = ConnectionReadStatus.TransportUnavailable;
                    return false;
                }

                // Bolt's PingAliased getter currently aliases PingNetwork. Read the distinct transport estimates.
                var networkRoundTrip = transport.NetworkPing;
                var aliasedRoundTrip = transport.AliasedPing;
                var bitsIn = connection.BitsPerSecondIn;
                var bitsOut = connection.BitsPerSecondOut;
                var remoteFrameLatest = connection._remoteFrameActual;
                var remoteFrameEstimated = connection._remoteFrameEstimated;
                var remoteFrameDrift = connection._remoteFrameDiff;
                var packetsReceived = connection._packetsReceived;
                var localFrame = BoltNetwork.frame;
                var timestamp = Stopwatch.GetTimestamp();

                var current = ResolveCurrent(session);
                if (connection.WasCollected || current == null || current.WasCollected
                    || current.Pointer != connection.Pointer || current.ConnectionId != session.ConnectionId)
                {
                    status = HooksInstalled ? ConnectionReadStatus.SessionUnavailable
                        : ConnectionReadStatus.Unavailable;
                    return false;
                }
                var currentTransport = current._udp;
                if (transport.WasCollected || currentTransport is null || currentTransport.WasCollected
                    || currentTransport.Pointer != transport.Pointer)
                {
                    status = ConnectionReadStatus.TransportUnavailable;
                    return false;
                }

                snapshot = new ConnectionSnapshot(session, networkRoundTrip, aliasedRoundTrip, bitsIn, bitsOut,
                    remoteFrameLatest, remoteFrameEstimated, remoteFrameDrift, packetsReceived, localFrame, timestamp);
                status = ConnectionReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = ConnectionReadStatus.Failed;
                ReportConnectionReadFailure(error);
                return false;
            }
        }

        private static void ReportConnectionReadFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedConnectionReadFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server connection read failed ("
                    + error.GetType().FullName + "). Further connection read failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum ConnectionReadStatus
    {
        Success,
        Unavailable,
        WrongThread,
        NotServer,
        SessionUnavailable,
        TransportUnavailable,
        Failed
    }

    /// <summary>
    /// Managed copies of native transport estimates and the last published Bolt traffic counters.
    /// Values are not an atomic sample, authentication, a delivery receipt or a gameplay verdict.
    /// </summary>
    public sealed class ConnectionSnapshot
    {
        internal ConnectionSnapshot(ServerSession session, float networkRoundTripSeconds,
            float aliasedRoundTripSeconds, int bitsPerSecondIn, int bitsPerSecondOut,
            int remoteFrameLatest, int remoteFrameEstimated, int remoteFrameDrift, int packetsReceived,
            int localFrame, long observedTimestamp)
        {
            Session = session;
            NetworkRoundTripSeconds = networkRoundTripSeconds;
            AliasedRoundTripSeconds = aliasedRoundTripSeconds;
            BitsPerSecondIn = bitsPerSecondIn;
            BitsPerSecondOut = bitsPerSecondOut;
            RemoteFrameLatest = remoteFrameLatest;
            RemoteFrameEstimated = remoteFrameEstimated;
            RemoteFrameDrift = remoteFrameDrift;
            PacketsReceived = packetsReceived;
            LocalFrame = localFrame;
            ObservedTimestamp = observedTimestamp;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>The requested lifetime handle; it can close after this read.</summary>
        public ServerSession Session { get; }
        /// <summary>Smoothed RTT after the native acknowledgment-delay deduction, in seconds. May still be the initial default.</summary>
        public float NetworkRoundTripSeconds { get; }
        /// <summary>Smoothed RTT before that deduction, in seconds. Not one-way latency or the client's frame time.</summary>
        public float AliasedRoundTripSeconds { get; }
        /// <summary>Raw incoming Bolt bits from its last published frame-based sampling interval, not a cumulative total.</summary>
        public int BitsPerSecondIn { get; }
        /// <summary>Raw outgoing Bolt bits from its last published frame-based sampling interval, not confirmed delivered bits.</summary>
        public int BitsPerSecondOut { get; }
        /// <summary>
        /// Highest client-written packet frame seen on this connection (_remoteFrameActual). It only rises, so a
        /// single forged high value persists. It counts from the client's Bolt start, not from joining; only deltas
        /// over long windows mean anything, and it is not evidence of client speed on its own.
        /// </summary>
        public int RemoteFrameLatest { get; }
        /// <summary>Raw _remoteFrameEstimated. How Bolt steps it is inferred, not established; diagnostic only.</summary>
        public int RemoteFrameEstimated { get; }
        /// <summary>Raw _remoteFrameDiff. Its semantics are inferred, not established; diagnostic only.</summary>
        public int RemoteFrameDrift { get; }
        /// <summary>Raw _packetsReceived counter for this connection, not a count of accepted or state-bearing packets.</summary>
        public int PacketsReceived { get; }
        /// <summary>
        /// This process's Bolt frame at the read. A lagging host advances it slowly, so it is not a reference clock
        /// for client frames; use ObservedTimestamp intervals instead.
        /// </summary>
        public int LocalFrame { get; }
        /// <summary>Stopwatch.GetTimestamp() at the read, for monotonic intervals. Not comparable with Bolt frames.</summary>
        public long ObservedTimestamp { get; }
        /// <summary>Local read completion time, not the native metrics' update time.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
