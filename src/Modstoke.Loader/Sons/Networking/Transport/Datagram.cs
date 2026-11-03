// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons
{
    public enum DatagramSendResult
    {
        Queued, Unavailable, WrongThread, InvalidPayload, PeerUnavailable, NotEnrolled, WindowFull, Failed
    }

    /// <summary>A copied datagram payload with its source peer handle; no authority is cached.</summary>
    public sealed class Datagram
    {
        internal Datagram(ControlPeer peer, byte[] payload)
        {
            Peer = peer;
            Payload = payload;
        }

        /// <summary>Source handle whose current lifetime must be checked when used.</summary>
        public ControlPeer Peer { get; }
        /// <summary>Payload copied from the received packet.</summary>
        public ReadOnlyMemory<byte> Payload { get; }
    }
}
