// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;

namespace Modstoke.Sons
{
    public static partial class ServerSessions
    {
        /// <summary>
        /// Copies the remote frame counters of an exact live remote session through TryReadConnection.
        /// Same thread, server and session rules; no hooks, and no native field is reset or smoothed.
        /// </summary>
        public static bool TryReadRemoteClock(ServerSession session,
            out RemoteClockSample sample, out ConnectionReadStatus status)
        {
            sample = null;
            if (!TryReadConnection(session, out var snapshot, out status))
                return false;
            sample = new RemoteClockSample(snapshot);
            return true;
        }
    }

    public enum RemoteClockRateStatus
    {
        Ok,
        /// <summary>The samples belong to different session handles.</summary>
        DifferentSession,
        /// <summary>No packet arrived between the samples, so the remote frame cannot have advanced.</summary>
        NoNewPackets,
        /// <summary>A counter or the timestamp went backwards between the samples.</summary>
        Regressed,
        /// <summary>Less than MinimumRateWindowSeconds of host time separates the samples.</summary>
        WindowTooShort
    }

    /// <summary>
    /// One session's client-written frame counter beside this host's monotonic clock. Passive evidence only:
    /// a modified client can send any frame value, a forged high value persists, and the rate also depends on
    /// whether client and host fixed-step rates match, which is not established.
    /// </summary>
    public sealed class RemoteClockSample
    {
        /// <summary>Shortest host-time window TryCompareRate accepts. Short windows read high after client hitches.</summary>
        public const double MinimumRateWindowSeconds = 30;

        internal RemoteClockSample(ConnectionSnapshot snapshot)
        {
            Session = snapshot.Session;
            RemoteFrameLatest = snapshot.RemoteFrameLatest;
            PacketsReceived = snapshot.PacketsReceived;
            LocalFrame = snapshot.LocalFrame;
            Timestamp = snapshot.ObservedTimestamp;
        }

        public ServerSession Session { get; }
        /// <inheritdoc cref="ConnectionSnapshot.RemoteFrameLatest"/>
        public int RemoteFrameLatest { get; }
        /// <inheritdoc cref="ConnectionSnapshot.PacketsReceived"/>
        public int PacketsReceived { get; }
        /// <inheritdoc cref="ConnectionSnapshot.LocalFrame"/>
        public int LocalFrame { get; }
        /// <summary>Stopwatch.GetTimestamp() at the read.</summary>
        public long Timestamp { get; }

        /// <summary>
        /// Divides the remote and local Bolt frame advances between two samples by elapsed host Stopwatch time.
        /// The remote rate is client-written and only rises; a value above the local rate is not proof of a
        /// modified clock, because catch-up after a hitch and delayed packets both distort it. Outputs are zero
        /// unless the status is Ok.
        /// </summary>
        public static bool TryCompareRate(RemoteClockSample earlier, RemoteClockSample later,
            out double remoteFramesPerSecond, out double localFramesPerSecond, out RemoteClockRateStatus status)
        {
            ArgumentNullException.ThrowIfNull(earlier);
            ArgumentNullException.ThrowIfNull(later);
            remoteFramesPerSecond = 0;
            localFramesPerSecond = 0;
            if (!ReferenceEquals(earlier.Session, later.Session))
            {
                status = RemoteClockRateStatus.DifferentSession;
                return false;
            }
            var elapsed = later.Timestamp - earlier.Timestamp;
            if (elapsed <= 0 || later.RemoteFrameLatest < earlier.RemoteFrameLatest
                || later.PacketsReceived < earlier.PacketsReceived || later.LocalFrame < earlier.LocalFrame)
            {
                status = RemoteClockRateStatus.Regressed;
                return false;
            }
            var seconds = elapsed / (double)Stopwatch.Frequency;
            if (seconds < MinimumRateWindowSeconds)
            {
                status = RemoteClockRateStatus.WindowTooShort;
                return false;
            }
            if (later.PacketsReceived == earlier.PacketsReceived)
            {
                status = RemoteClockRateStatus.NoNewPackets;
                return false;
            }

            remoteFramesPerSecond = ((long)later.RemoteFrameLatest - earlier.RemoteFrameLatest) / seconds;
            localFramesPerSecond = ((long)later.LocalFrame - earlier.LocalFrame) / seconds;
            status = RemoteClockRateStatus.Ok;
            return true;
        }
    }
}
