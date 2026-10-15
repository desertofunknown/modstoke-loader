// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using OpenACAI.Endnight.Loader;

namespace OpenACAI.Sons
{
    public static partial class OpenACAISonsServerSessions
    {
        private static int _reportedConnectionReadFailure;

        /// <summary>
        /// Copies transport observations for an exact live remote session on the game thread.
        /// No client extension, registered identity or player entity is required.
        /// </summary>
        public static bool TryReadConnection(OpenACAISonsServerSession session,
            out OpenACAISonsConnectionSnapshot snapshot, out OpenACAISonsConnectionReadStatus status)
        {
            snapshot = null;
            if (!HooksInstalled)
            {
                status = OpenACAISonsConnectionReadStatus.Unavailable;
                return false;
            }
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                status = OpenACAISonsConnectionReadStatus.WrongThread;
                return false;
            }

            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                {
                    status = OpenACAISonsConnectionReadStatus.NotServer;
                    return false;
                }
                var connection = ResolveCurrent(session);
                if (connection == null)
                {
                    status = HooksInstalled ? OpenACAISonsConnectionReadStatus.SessionUnavailable
                        : OpenACAISonsConnectionReadStatus.Unavailable;
                    return false;
                }
                var transport = connection._udp;
                if (transport is null || transport.WasCollected)
                {
                    status = OpenACAISonsConnectionReadStatus.TransportUnavailable;
                    return false;
                }

                // Bolt's PingAliased getter currently aliases PingNetwork. Read the distinct transport estimates.
                var networkRoundTrip = transport.NetworkPing;
                var aliasedRoundTrip = transport.AliasedPing;
                var bitsIn = connection.BitsPerSecondIn;
                var bitsOut = connection.BitsPerSecondOut;

                var current = ResolveCurrent(session);
                if (current == null || current.Pointer != connection.Pointer || current.ConnectionId != session.ConnectionId)
                {
                    status = HooksInstalled ? OpenACAISonsConnectionReadStatus.SessionUnavailable
                        : OpenACAISonsConnectionReadStatus.Unavailable;
                    return false;
                }
                var currentTransport = current._udp;
                if (currentTransport is null || currentTransport.WasCollected || currentTransport.Pointer != transport.Pointer)
                {
                    status = OpenACAISonsConnectionReadStatus.TransportUnavailable;
                    return false;
                }

                snapshot = new OpenACAISonsConnectionSnapshot(session, networkRoundTrip, aliasedRoundTrip, bitsIn, bitsOut);
                status = OpenACAISonsConnectionReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = OpenACAISonsConnectionReadStatus.Failed;
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
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Server connection read failed ("
                    + error.GetType().FullName + "). Further connection read failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum OpenACAISonsConnectionReadStatus
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
    public sealed class OpenACAISonsConnectionSnapshot
    {
        internal OpenACAISonsConnectionSnapshot(OpenACAISonsServerSession session, float networkRoundTripSeconds,
            float aliasedRoundTripSeconds, int bitsPerSecondIn, int bitsPerSecondOut)
        {
            Session = session;
            NetworkRoundTripSeconds = networkRoundTripSeconds;
            AliasedRoundTripSeconds = aliasedRoundTripSeconds;
            BitsPerSecondIn = bitsPerSecondIn;
            BitsPerSecondOut = bitsPerSecondOut;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>The requested lifetime handle; it can close after this read.</summary>
        public OpenACAISonsServerSession Session { get; }
        /// <summary>Smoothed RTT after the native acknowledgment-delay deduction, in seconds. May still be the initial default.</summary>
        public float NetworkRoundTripSeconds { get; }
        /// <summary>Smoothed RTT before that deduction, in seconds. Not one-way latency or the client's frame time.</summary>
        public float AliasedRoundTripSeconds { get; }
        /// <summary>Raw incoming Bolt bits from its last published frame-based sampling interval, not a cumulative total.</summary>
        public int BitsPerSecondIn { get; }
        /// <summary>Raw outgoing Bolt bits from its last published frame-based sampling interval, not confirmed delivered bits.</summary>
        public int BitsPerSecondOut { get; }
        /// <summary>Local read completion time, not the native metrics' update time.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
