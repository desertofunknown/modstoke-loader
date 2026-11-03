// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Threading;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Loader;
using UdpKit;

namespace Modstoke.Sons
{
    /// <summary>Local send outcome only. Queued never establishes remote receipt or migration authority.</summary>
    public enum BoltMigrationSendResult
    {
        Rejected,
        Queued,
        Unconfirmed
    }

    /// <summary>
    /// Explicitly selected reliable Bolt stream for migration snapshots and receipts. Enrollment,
    /// transaction authorization, retries and adapter preservation remain the consuming mode's job.
    /// No admission selection, bandwidth setting or native listener policy is changed here.
    /// </summary>
    public static class BoltMigrationTransport
    {
        private const string ChannelName = "Modstoke.OwnershipMigration.v1";
        private const int ChannelId = 2;
        private const int ChannelPriority = 1;
        private const int MaxPacketBytes = BoltMigrationProtocol.SnapshotHeaderLength
            + BoltMigrationProtocol.MaxPayloadBytes;
        private static volatile bool _selected;
        private static volatile int _ownerThread;
        private static volatile Run _run;
        private static bool _registrationAttempted;
        private static bool _sending;
        private static bool _receiving;
        private static int _reportedFailure;

        public static bool IsSelected => _selected;

        /// <summary>Managed registration state, not a native liveness check or delivery guarantee.</summary>
        public static bool IsAvailable => _selected && _run != null
            && ControlTransport.State == ControlState.Active
            && BoltStreams.State.Status == BoltEventHookStatus.Installed;

        /// <summary>
        /// Synchronous owner-thread notifications with managed, independent protocol data. Each
        /// subscriber must authorize the exact peer and transaction. Subscriber exceptions are isolated.
        /// </summary>
        public static event Action<ControlPeer, BoltMigrationSnapshot> SnapshotReceived;
        public static event Action<ControlPeer, BoltMigrationReceipt> ReceiptReceived;
        public static event Action<ControlPeer, BoltMigrationPrepareSource> PrepareSourceReceived;

        /// <summary>
        /// Select on both peers before Bolt starts. Configure enrollment separately with migration
        /// capability required and offered. This method never selects or resets admission/enrollment.
        /// </summary>
        public static bool SelectForNextStart(bool selected, out string detail)
        {
            // TryReadSelection proves the initialized managed owner thread before any native read here.
            var status = ModAdmission.TryReadSelection(out var selection);
            if (status != ControlResult.Success || selection == null)
            {
                detail = "Migration transport selection requires the initialized owner thread (" + status + ").";
                return false;
            }
            if (!selection.CanChangeForNextStart || _sending || _receiving)
            {
                detail = "Migration transport selection cannot change during Bolt startup or an active dispatch.";
                return false;
            }
            if (selected && BoltStreams.State.Status != BoltEventHookStatus.Installed)
            {
                detail = "The native Bolt stream observation hooks are unavailable.";
                return false;
            }

            _ownerThread = Environment.CurrentManagedThreadId;
            ResetForStart();
            if (_selected != selected)
            {
                if (selected)
                {
                    BoltStreams.PolicyAccepted += Receive;
                }
                else
                {
                    BoltStreams.PolicyAccepted -= Receive;
                }
                _selected = selected;
            }
            detail = selected
                ? "Migration transport selected; both peers must separately enroll with capability 15 and matching stream registration order."
                : "Migration transport deselected; admission and enrollment selection are unchanged.";
            return true;
        }

        internal static void RegisterForStart()
        {
            if (!_selected || !OnOwnerThread || _registrationAttempted) return;
            _registrationAttempted = true;
            _run = null;
            try
            {
                if (ControlTransport.State != ControlState.Active
                    || BoltStreams.State.Status != BoltEventHookStatus.Installed)
                    return;
                var socket = BoltCore._udpSocket;
                if (socket == null || socket.WasCollected || socket.State != UdpSocketState.Created)
                    return;
                var channel = BoltNetwork.CreateStreamChannel(ChannelName, UdpChannelMode.Reliable, ChannelPriority);
                var currentSocket = BoltCore._udpSocket;
                if (!MatchesChannel(channel) || socket.WasCollected || socket.State != UdpSocketState.Created
                    || currentSocket == null || currentSocket.WasCollected || currentSocket.Pointer != socket.Pointer)
                    return;
                _run = new Run(socket.Pointer, channel, Environment.CurrentManagedThreadId);
            }
            catch (Exception error)
            {
                // A failed registration may already have consumed an ID. Never retry within this start.
                _run = null;
                ReportFailure("registration", error);
            }
        }

        internal static void ResetForStart()
        {
            if (!OnOwnerThread) return;
            _run = null;
            _registrationAttempted = false;
            // Keep selection/subscriptions and any in-progress dispatch guard across shutdown re-entry.
        }

        /// <summary>
        /// Rejects before sending if the protocol envelope or native block configuration cannot fit.
        /// Uses native reliable block reassembly, without additional fragmentation or bandwidth changes.
        /// An exception after entering StreamBytes is Unconfirmed and must not be treated as unsent.
        /// </summary>
        public static BoltMigrationSendResult SendSnapshot(ControlPeer peer,
            BoltMigrationSnapshot snapshot) => Send(peer, snapshot, null);

        public static BoltMigrationSendResult SendReceipt(ControlPeer peer,
            BoltMigrationReceipt receipt) => Send(peer, null, receipt);

        /// <summary>
        /// The consuming mode must explicitly establish PrepareSourceVersion support for this peer.
        /// Enrollment capabilities alone do not advertise dispatcher wire-version support.
        /// </summary>
        public static BoltMigrationSendResult SendPrepareSource(ControlPeer peer,
            BoltMigrationPrepareSource request) => request.IsValid
                ? Send(peer, null, null, request) : BoltMigrationSendResult.Rejected;

        private static BoltMigrationSendResult Send(ControlPeer peer,
            BoltMigrationSnapshot snapshot, BoltMigrationReceipt receipt,
            BoltMigrationPrepareSource? prepare = null)
        {
            if (!OnOwnerThread || !IsAvailable || _sending || (snapshot == null && receipt == null && prepare == null))
                return BoltMigrationSendResult.Rejected;
            _sending = true;
            var attempted = false;
            try
            {
                var run = _run;
                long length = snapshot != null
                    ? (long)BoltMigrationProtocol.SnapshotHeaderLength + snapshot.PayloadSpan.Length
                    : prepare.HasValue ? BoltMigrationProtocol.PrepareSourceLength
                    : BoltMigrationProtocol.ReceiptLength;
                ulong capabilities = prepare?.RequiredCapabilities ?? BoltMigrationProtocol.Capability;
                // Native total length and block index are signed 32-bit values, not 16-bit lengths.
                if (length <= 0 || length > MaxPacketBytes || length > int.MaxValue
                    || !TryResolve(run, peer, out var connection, capabilities)
                    || !FitsNativeBlocks(connection, (int)length))
                    return BoltMigrationSendResult.Rejected;

                var bytes = new byte[(int)length];
                int written;
                bool encoded = snapshot != null
                    ? BoltMigrationProtocol.TryWriteSnapshot(snapshot, bytes, out written)
                    : prepare.HasValue
                        ? BoltMigrationProtocol.TryWritePrepareSource(prepare.Value, bytes, out written)
                        : BoltMigrationProtocol.TryWriteReceipt(receipt, bytes, out written);
                if (!encoded || written != bytes.Length)
                    return BoltMigrationSendResult.Rejected;
                var data = new Il2CppStructArray<byte>(bytes);
                if (!TryResolve(run, peer, out connection, capabilities) || !FitsNativeBlocks(connection, bytes.Length))
                    return BoltMigrationSendResult.Rejected;

                attempted = true;
                connection.StreamBytes(run.Channel, data);
                return TryResolve(run, peer, out _, capabilities)
                    ? BoltMigrationSendResult.Queued : BoltMigrationSendResult.Unconfirmed;
            }
            catch (Exception)
            {
                return attempted ? BoltMigrationSendResult.Unconfirmed : BoltMigrationSendResult.Rejected;
            }
            finally { _sending = false; }
        }

        private static bool OnOwnerThread => _ownerThread != 0 && Environment.CurrentManagedThreadId == _ownerThread;

        private static bool MatchesChannel(UdpChannelName channel) => channel != null && !channel.WasCollected
            && channel.Id == ChannelId && string.Equals(channel.Name, ChannelName, StringComparison.Ordinal);

        private static bool TryResolve(Run run, ControlPeer peer, out BoltConnection connection,
            ulong capabilities = BoltMigrationProtocol.Capability)
        {
            connection = null;
            if (!OnOwnerThread || run == null || run.Thread != Environment.CurrentManagedThreadId
                || !ReferenceEquals(run, _run) || !IsAvailable || peer == null || peer.SocketPointer != run.Socket
                || ClientEnrollment.TryRead(peer, out var enrollment) != EnrollmentReadStatus.Success
                || enrollment == null || !ReferenceEquals(enrollment.Peer, peer)
                || enrollment.State != EnrollmentState.Enrolled
                || (enrollment.RequiredCapabilities & capabilities) != capabilities
                || (enrollment.OfferedCapabilities & capabilities) != capabilities)
                return false;

            var resolved = ControlTransport.Resolve(peer);
            if (resolved == null || resolved.WasCollected || !MatchesChannel(run.Channel)) return false;
            var udp = resolved._udp;
            if (udp == null || udp.WasCollected) return false;
            var socket = udp.Socket;
            if (socket == null || socket.WasCollected || socket.Pointer != run.Socket
                || socket.State != UdpSocketState.Running) return false;
            var channels = socket.streamChannels;
            if (channels == null || channels.WasCollected || !channels.TryGetValue(run.Channel, out var channel)
                || channel == null || channel.WasCollected) return false;
            var config = channel.Config;
            if (config == null || config.WasCollected || config.Mode != UdpChannelMode.Reliable
                || config.Priority != ChannelPriority || !MatchesChannel(config.ChannelName)
                || !ReferenceEquals(run, _run)) return false;
            connection = resolved;
            return true;
        }

        private static bool FitsNativeBlocks(BoltConnection connection, int length)
        {
            var udp = connection._udp;
            if (udp == null || udp.WasCollected) return false;
            var socket = udp.Socket;
            var pipe = udp.StreamPipe;
            if (socket == null || socket.WasCollected || pipe == null || pipe.WasCollected) return false;
            var config = pipe.Config;
            var socketConfig = socket.StreamPipeConfig;
            var buffer = socket.sendBuffer;
            if (buffer == null || buffer.WasCollected || config.SequenceBytes < 0 || config.AckBytes < 0
                || socketConfig.DatagramSize <= 0 || config.DatagramSize != socketConfig.DatagramSize
                || config.SequenceBytes != socketConfig.SequenceBytes || config.AckBytes != socketConfig.AckBytes)
                return false;

            // InitOp (0x183C07E40) uses DatagramSize minus the pipe header, without subtracting
            // SendBlock's additional 24 bytes: channel(4), key(8), length(4), CRC(4), block(4).
            // Check the actual largest native block against backing storage before native queuing.
            long header = 2L * config.SequenceBytes + config.AckBytes + 3;
            long blockSize = (long)socketConfig.DatagramSize - header;
            if (header != config.HeaderSize || blockSize <= 0 || blockSize > int.MaxValue) return false;
            long blockCount = ((long)length + blockSize - 1) / blockSize;
            long packetBytes = header + 24 + Math.Min(blockSize, length);
            return blockCount > 0 && blockCount <= int.MaxValue && packetBytes <= buffer.Length;
        }

        private static void Receive(BoltStreamRequest request)
        {
            if (!OnOwnerThread || !IsAvailable || _receiving || request == null) return;
            var context = request.Context;
            if (context.ChannelId != ChannelId || !string.Equals(context.ChannelName, ChannelName, StringComparison.Ordinal))
                return;
            _receiving = true;
            try
            {
                var run = _run;
                if (!request.TryGetSourcePeer(out var peer) || !TryResolve(run, peer, out _)) return;
                int length = context.PayloadByteCount;
                if (length == BoltMigrationProtocol.PrepareSourceLength)
                {
                    Span<byte> bytes = stackalloc byte[BoltMigrationProtocol.PrepareSourceLength];
                    if (request.TryCopyBytes(0, bytes)
                        && BoltMigrationProtocol.TryReadPrepareSource(bytes, out var prepare)
                        && TryResolve(run, peer, out _, prepare.RequiredCapabilities))
                        Publish(request, run, peer, prepare, PrepareSourceReceived, prepare.RequiredCapabilities);
                    return;
                }
                if (length == BoltMigrationProtocol.ReceiptLength)
                {
                    Span<byte> bytes = stackalloc byte[BoltMigrationProtocol.ReceiptLength];
                    if (request.TryCopyBytes(0, bytes)
                        && BoltMigrationProtocol.TryReadReceipt(bytes, out var receipt))
                        Publish(request, run, peer, receipt, ReceiptReceived);
                    return;
                }
                if (length < BoltMigrationProtocol.SnapshotHeaderLength || length > MaxPacketBytes) return;
                Span<byte> header = stackalloc byte[BoltMigrationProtocol.SnapshotHeaderLength];
                if (!request.TryCopyBytes(0, header)) return;
                int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(96, 4));
                if (payloadLength < 0 || payloadLength > BoltMigrationProtocol.MaxPayloadBytes
                    || (long)payloadLength + header.Length != length) return;

                var packet = new byte[length];
                header.CopyTo(packet);
                if (request.TryCopyBytes(header.Length, packet.AsSpan(header.Length))
                    && BoltMigrationProtocol.TryReadSnapshot(packet, out var snapshot))
                    Publish(request, run, peer, snapshot, SnapshotReceived);
            }
            catch (Exception error) { ReportFailure("receive", error); }
            finally { _receiving = false; }
        }

        private static void Publish<T>(BoltStreamRequest request, Run run, ControlPeer peer,
            T message, Action<ControlPeer, T> handlers,
            ulong capabilities = BoltMigrationProtocol.Capability)
        {
            if (handlers == null) return;
            foreach (var handler in Delegate.EnumerateInvocationList(handlers))
            {
                // Revalidate the borrowed request and enrollment after every user callback. Only
                // copied protocol objects escape; shutdown/reconnect cannot retarget this dispatch.
                if (!request.TryGetSourcePeer(out var source) || !ReferenceEquals(source, peer)
                    || !TryResolve(run, peer, out _, capabilities)) return;
                try { handler(peer, message); }
                catch (Exception error) { ReportFailure("subscriber", error); }
            }
        }

        private static void ReportFailure(string area, Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Bolt migration transport " + area
                    + " failed (" + error.GetType().FullName + "). Further failures will not be logged.");
            }
            catch (Exception) { }
        }

        private sealed class Run
        {
            internal Run(IntPtr socket, UdpChannelName channel, int thread)
            {
                Socket = socket;
                Channel = channel;
                Thread = thread;
            }

            internal readonly IntPtr Socket;
            internal readonly UdpChannelName Channel;
            internal readonly int Thread;
        }
    }
}
