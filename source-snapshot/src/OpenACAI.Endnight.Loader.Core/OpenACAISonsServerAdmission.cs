// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using OpenACAI.Endnight.Loader;
using UdpKit;

namespace OpenACAI.Sons
{
    /// <summary>Explicit control of the live server transport's incoming connection-request gate.</summary>
    public static class OpenACAISonsServerAdmission
    {
        private static int _ownerThreadId;
        private static int _reportedFailure;

        internal static void Initialize()
        {
            Interlocked.CompareExchange(ref _ownerThreadId, Environment.CurrentManagedThreadId, 0);
        }

        /// <summary>Copies the live gate configuration on the game thread, not actual join availability.</summary>
        public static OpenACAISonsServerAdmissionStatus TryRead(out OpenACAISonsServerAdmissionSnapshot snapshot)
        {
            snapshot = null;
            try
            {
                var status = ResolveConfiguration(out _, out var config);
                if (status != OpenACAISonsServerAdmissionStatus.Success)
                    return status;

                snapshot = new OpenACAISonsServerAdmissionSnapshot(config.AllowIncommingConnections,
                    config.AutoAcceptIncommingConnections, config.ConnectionLimit, DateTimeOffset.UtcNow);
                return OpenACAISonsServerAdmissionStatus.Success;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return OpenACAISonsServerAdmissionStatus.Failed;
            }
        }

        /// <summary>
        /// Changes the native gate for new connection requests, including vanilla clients.
        /// Does not disconnect players, cancel pending joins or change automatic acceptance.
        /// Success is local configuration readback, not a transport-worker or client acknowledgment.
        /// The caller owns authorization, scheduling and any later restoration.
        /// </summary>
        public static OpenACAISonsServerAdmissionStatus SetIncomingRequestsEnabled(bool enabled)
        {
            var writeAttempted = false;
            try
            {
                var status = ResolveConfiguration(out var socket, out var config);
                if (status != OpenACAISonsServerAdmissionStatus.Success)
                    return status;
                if (config.AllowIncommingConnections == enabled)
                    return OpenACAISonsServerAdmissionStatus.AlreadySet;

                // Use the socket's live configuration, not Bolt's next-start configuration.
                writeAttempted = true;
                config.AllowIncommingConnections = enabled;
                var currentStatus = ResolveConfiguration(out var currentSocket, out var currentConfig);
                if (currentStatus != OpenACAISonsServerAdmissionStatus.Success
                    || currentSocket.Pointer != socket.Pointer || currentConfig.Pointer != config.Pointer
                    || currentConfig.AllowIncommingConnections != enabled)
                    return OpenACAISonsServerAdmissionStatus.ApplyUncertain;

                return OpenACAISonsServerAdmissionStatus.Success;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return writeAttempted ? OpenACAISonsServerAdmissionStatus.ApplyUncertain
                    : OpenACAISonsServerAdmissionStatus.Failed;
            }
        }

        private static OpenACAISonsServerAdmissionStatus ResolveConfiguration(out UdpSocket socket, out UdpConfig config)
        {
            socket = null;
            config = null;
            var ownerThread = Volatile.Read(ref _ownerThreadId);
            if (ownerThread == 0)
                return OpenACAISonsServerAdmissionStatus.Unavailable;
            if (Environment.CurrentManagedThreadId != ownerThread)
                return OpenACAISonsServerAdmissionStatus.WrongThread;
            if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                return OpenACAISonsServerAdmissionStatus.NotServer;

            socket = BoltCore._udpSocket;
            if (socket is null || socket.WasCollected || socket.Mode != UdpSocketMode.Host)
                return OpenACAISonsServerAdmissionStatus.TransportUnavailable;
            config = socket.Config;
            return config is null || config.WasCollected
                ? OpenACAISonsServerAdmissionStatus.TransportUnavailable
                : OpenACAISonsServerAdmissionStatus.Success;
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Server admission configuration failed ("
                    + error.GetType().FullName + "). Further admission configuration failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public sealed class OpenACAISonsServerAdmissionSnapshot
    {
        internal OpenACAISonsServerAdmissionSnapshot(bool incomingRequestsEnabled, bool automaticAcceptanceEnabled,
            int connectionLimit, DateTimeOffset observedAt)
        {
            IncomingRequestsEnabled = incomingRequestsEnabled;
            AutomaticAcceptanceEnabled = automaticAcceptanceEnabled;
            ConnectionLimit = connectionLimit;
            ObservedAt = observedAt;
        }

        public bool IncomingRequestsEnabled { get; }
        public bool AutomaticAcceptanceEnabled { get; }
        /// <summary>Raw native transport limit; -1 means unlimited in the inspected request path.</summary>
        public int ConnectionLimit { get; }
        public DateTimeOffset ObservedAt { get; }
    }

    public enum OpenACAISonsServerAdmissionStatus
    {
        /// <summary>A configuration snapshot was read, or a write was read back on the same live socket/configuration.</summary>
        Success,
        /// <summary>The requested flag was already observed; no write was attempted.</summary>
        AlreadySet,
        Unavailable,
        WrongThread,
        NotServer,
        TransportUnavailable,
        /// <summary>An exception occurred before any gate write was attempted.</summary>
        Failed,
        /// <summary>A write was attempted but its result on the current transport could not be confirmed.</summary>
        ApplyUncertain
    }
}
