// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Net;
using System.Threading;
using Il2CppInterop.Runtime;
using OpenACAI.Endnight.Loader;

namespace OpenACAI.Sons
{
    public static partial class OpenACAISonsServerSessions
    {
        private static int _reportedEndpointReadFailure;

        /// <summary>
        /// Copies the endpoint of an exact live remote session on the game thread.
        /// Backend-specific values are transport observations, not authentication or permission.
        /// </summary>
        public static bool TryReadEndpoint(OpenACAISonsServerSession session,
            out OpenACAISonsEndpointSnapshot snapshot, out OpenACAISonsConnectionReadStatus status)
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
                var socket = transport.Socket;
                if (socket is null || socket.WasCollected)
                {
                    status = OpenACAISonsConnectionReadStatus.TransportUnavailable;
                    return false;
                }
                var platformSocket = socket.PlatformSocket;
                if (platformSocket is null || platformSocket.WasCollected)
                {
                    status = OpenACAISonsConnectionReadStatus.TransportUnavailable;
                    return false;
                }

                var endpoint = transport.EndPoint;
                var rawEndpoint = endpoint.SteamId.Id;
                var kind = OpenACAISonsEndpointKind.Unknown;
                ulong? steamId = null;
                string address = null;
                ushort? port = null;

                // Endpoint fields overlap. Only the concrete socket determines how to interpret the bits.
                // Derived/custom socket implementations keep their opaque value without an inferred format.
                var socketClass = IL2CPP.il2cpp_object_get_class(platformSocket.Pointer);
                if (socketClass == Il2CppClassPointerStore<SteamSocket>.NativeClassPtr)
                {
                    kind = OpenACAISonsEndpointKind.SteamP2P;
                    steamId = rawEndpoint;
                }
                else if (socketClass == Il2CppClassPointerStore<DotNetSocket>.NativeClassPtr)
                {
                    kind = OpenACAISonsEndpointKind.IPv4Udp;
                    var packed = endpoint.Address.Packed;
                    address = new IPAddress(new[]
                    {
                        (byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed
                    }).ToString();
                    port = endpoint.Port;
                }

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
                var currentSocket = currentTransport.Socket;
                if (currentSocket is null || currentSocket.WasCollected || currentSocket.Pointer != socket.Pointer)
                {
                    status = OpenACAISonsConnectionReadStatus.TransportUnavailable;
                    return false;
                }
                var currentPlatformSocket = currentSocket.PlatformSocket;
                if (currentPlatformSocket is null || currentPlatformSocket.WasCollected
                    || currentPlatformSocket.Pointer != platformSocket.Pointer
                    || currentTransport.EndPoint.SteamId.Id != rawEndpoint)
                {
                    status = OpenACAISonsConnectionReadStatus.TransportUnavailable;
                    return false;
                }

                snapshot = new OpenACAISonsEndpointSnapshot(session, kind, rawEndpoint, steamId, address, port);
                status = OpenACAISonsConnectionReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = OpenACAISonsConnectionReadStatus.Failed;
                if (Interlocked.Exchange(ref _reportedEndpointReadFailure, 1) == 0)
                {
                    try
                    {
                        OpenACAIEndnightRuntime.LogSource?.LogWarning("Server endpoint read failed ("
                            + error.GetType().FullName + "). Further endpoint failures will not be logged.");
                    }
                    catch (Exception) { }
                }
                return false;
            }
        }
    }

    public enum OpenACAISonsEndpointKind
    {
        Unknown,
        SteamP2P,
        IPv4Udp
    }

    /// <summary>Immutable transport observations. Contains no native connection, socket or address wrapper.</summary>
    public sealed class OpenACAISonsEndpointSnapshot
    {
        internal OpenACAISonsEndpointSnapshot(OpenACAISonsServerSession session, OpenACAISonsEndpointKind kind,
            ulong rawEndpoint, ulong? steamId, string ipv4Address, ushort? port)
        {
            Session = session;
            Kind = kind;
            RawEndpoint = rawEndpoint;
            SteamId = steamId;
            IPv4Address = ipv4Address;
            Port = port;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>The exact requested lifetime handle, which may close after this read.</summary>
        public OpenACAISonsServerSession Session { get; }
        /// <summary>Interpretation based on the exact native socket class, not the endpoint's numeric shape.</summary>
        public OpenACAISonsEndpointKind Kind { get; }
        /// <summary>Opaque eight-byte endpoint value. It is not a Steam ID unless Kind is SteamP2P.</summary>
        public ulong RawEndpoint { get; }
        /// <summary>Raw Steam peer value, including zero, only for SteamP2P. Not an authentication result.</summary>
        public ulong? SteamId { get; }
        /// <summary>Dotted IPv4 address only for IPv4Udp; not a stable player identity.</summary>
        public string IPv4Address { get; }
        /// <summary>Observed remote port, including zero, only for IPv4Udp.</summary>
        public ushort? Port { get; }
        /// <summary>Local read completion time. Separate identity and role reads are not atomic with this snapshot.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
