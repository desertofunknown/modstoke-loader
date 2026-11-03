// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Modstoke.Sons
{
    /// <summary>
    /// Game-thread, module-storage-only delivery. The parent owns startup selection, capability
    /// gating and Tick. A transport receipt says nothing about application commit or native custody.
    /// </summary>
    internal sealed partial class ModuleStorageTransport : IDisposable
    {
        // Every protocol envelope is already bounded by the save limit; inbound buffers never need more.
        internal const int MaxPayloadBytes = ModuleStorage.MaximumSaveBytes;
        internal const int MaxRetainedBytes = 16 * 1024 * 1024;
        internal const int MaxPeers = ControlTransport.MaxPeers;
        internal const int MaxQueuedMessagesPerPeer = 3; // Includes the active logical message.
        private const int WindowFragments = 32;
        private const int MaxAttempts = 16; // Initial queued send plus at most 15 retransmissions.
        private const long RetryMilliseconds = 1000;
        private const long DeadlineMilliseconds = 120000;
        private const int MaxCompletionsPerDirection = 4;
        private const int MaxSendsPerPeerPerTick = 4;
        private const int MaxSendsPerTick = 32;
        // Covers bounded peer/cache/queue objects, array headers and scheduling scratch space.
        private const int FixedRetainedBytes = 1024 * 1024;
        private const int OutboundOverheadBytes = 1024;
        private const int InboundOverheadBytes = 256;

        // Recreating the owner must not reset IDs for still-current handles. Only retired handles
        // can leave this set; Forget/Dispose never enables another attempt on the same lifetime.
        private static readonly HashSet<ControlPeer> RetiredPeers = new();
        private static ModuleStorageTransport _owner;
        private readonly Dictionary<ControlPeer, PeerState> _peers = new();
        private readonly Queue<(ControlPeer Peer, string Detail)> _rejected = new();
        private readonly PeerState[] _tickPeers = new PeerState[MaxPeers];
        private readonly Action<ControlPeer, byte[]> _received;
        private readonly Action<ControlPeer, string> _failed;
        private readonly int _threadId;
        private int _retainedBytes = FixedRetainedBytes;
        private int _nextPeer;
        private bool _ticking;
        private bool _receiving;
        private bool _disposed;

        internal ModuleStorageTransport(Action<ControlPeer, byte[]> received,
            Action<ControlPeer, string> failed)
        {
            ArgumentNullException.ThrowIfNull(received);
            ArgumentNullException.ThrowIfNull(failed);
            if (_owner != null) throw new InvalidOperationException("Module storage transport already has an owner.");
            _threadId = Environment.CurrentManagedThreadId;
            _received = received;
            _failed = failed;
            RetiredPeers.RemoveWhere(peer => !peer.IsCurrent);
            _owner = this;
            Datagrams.Received += OnReceived;
        }

        /// <summary>
        /// Copies and queues one message; true is neither remote delivery nor application commit.
        /// QueueWindowFull is a nonterminal refusal: defer the unsent message, not its native action.
        /// Other failures must not trigger a native-action retry. Notifications occur from Tick only.
        /// </summary>
        internal bool TrySend(ControlPeer peer, byte[] payload, out string detail)
        {
            if (!OnOwnerThread) { detail = "WrongThread"; return false; }
            if (_disposed) { detail = "Disposed"; return false; }
            if (!IsEnrolled(peer))
            {
                if (peer != null && _peers.TryGetValue(peer, out var old)) Stop(old, "PeerUnavailable");
                detail = "PeerUnavailable";
                return false;
            }
            var state = GetPeer(peer);
            if (state == null) { detail = "PeerStoppedOrLimitReached"; return false; }
            if (state.Stopped) { detail = state.Failure; return false; }
            if (payload == null || payload.Length < 1 || payload.Length > MaxPayloadBytes)
            {
                detail = "InvalidPayload";
                Stop(state, detail);
                return false;
            }
            long now = Environment.TickCount64;
            ExpireCompletions(state, now);
            if (state.Outgoing.Count >= MaxQueuedMessagesPerPeer)
            {
                detail = "QueueWindowFull";
                return false;
            }
            if (state.NextSendId == ulong.MaxValue)
            {
                detail = "MessageIdLimitReached";
                Stop(state, detail);
                return false;
            }
            int retained = payload.Length + OutboundOverheadBytes;
            if (!Reserve(retained))
            {
                // The shared budget may be held by other peers; defer instead of stopping this one.
                detail = "QueueWindowFull";
                return false;
            }
            try
            {
                var message = new Outbound(state.NextSendId + 1, (byte[])payload.Clone(),
                    now + DeadlineMilliseconds, retained);
                state.Outgoing.Enqueue(message);
                state.NextSendId = message.Id;
                detail = "Queued; delivery and application commit are pending";
                return true;
            }
            catch (Exception error)
            {
                Release(retained);
                detail = "QueueFailed: " + error.GetType().Name;
                Stop(state, detail);
                return false;
            }
        }

        internal void Tick()
        {
            RequireOwnerThread();
            if (_disposed || _ticking || _receiving) return;
            _ticking = true;
            int count = 0;
            try
            {
                RetiredPeers.RemoveWhere(peer => !peer.IsCurrent);
                foreach (var state in _peers.Values) _tickPeers[count++] = state;
                for (int i = 0; i < count && !_disposed; i++)
                {
                    var state = _tickPeers[i];
                    if (!state.Stopped && !IsEnrolled(state.Peer)) Stop(state, "PeerUnavailable");
                    long now = Environment.TickCount64;
                    ExpireCompletions(state, now);
                    if (!state.Stopped)
                    {
                        foreach (var message in state.Outgoing)
                            if (now >= message.Deadline) { Stop(state, "SendDeadlineExceeded"); break; }
                        if (!state.Stopped && state.Incoming != null && now >= state.Incoming.Deadline)
                            Stop(state, "ReceiveDeadlineExceeded");
                        if (!state.Stopped && state.Incoming != null
                            && state.Incoming.Base == state.Incoming.Count) Dispatch(state, now);
                    }
                    NotifyFailure(state);
                    if (!state.Peer.IsCurrent) _peers.Remove(state.Peer);
                }

                int budget = MaxSendsPerTick;
                int start = count == 0 ? 0 : _nextPeer % count;
                for (int i = 0; i < count && budget > 0 && !_disposed; i++)
                {
                    int index = (start + i) % count;
                    var state = _tickPeers[index];
                    _nextPeer = (index + 1) % count;
                    if (state.Stopped) continue;
                    if (!IsEnrolled(state.Peer)) { Stop(state, "PeerUnavailable"); NotifyFailure(state); continue; }
                    bool ackSent = false;
                    for (int sent = 0; sent < MaxSendsPerPeerPerTick && budget > 0 && !state.Stopped; sent++)
                    {
                        if (!Pump(state, Environment.TickCount64, ref ackSent, ref budget)) break;
                    }
                    NotifyFailure(state);
                }
                int rejectedCount = _rejected.Count;
                for (int i = 0; i < rejectedCount && !_disposed; i++)
                {
                    var rejected = _rejected.Dequeue();
                    PublishFailure(rejected.Peer, rejected.Detail);
                }
            }
            finally
            {
                Array.Clear(_tickPeers, 0, count);
                _ticking = false;
                if (_disposed) _owner = null;
            }
        }

        /// <summary>Releases buffers. A still-current handle remains terminal, not reusable.</summary>
        internal void Forget(ControlPeer peer)
        {
            RequireOwnerThread();
            if (_disposed || peer == null) return;
            if (_peers.TryGetValue(peer, out var state))
            {
                Stop(state, "PeerForgotten");
                state.FailurePending = false;
                if (!peer.IsCurrent) _peers.Remove(peer);
            }
            else if (peer.IsCurrent) Retire(peer);
        }

        public void Dispose()
        {
            RequireOwnerThread();
            if (_disposed) return;
            _disposed = true;
            Datagrams.Received -= OnReceived;
            RetiredPeers.RemoveWhere(peer => !peer.IsCurrent);
            foreach (var state in _peers.Values)
            {
                Stop(state, "Disposed");
                state.FailurePending = false;
                if (state.Peer.IsCurrent) Retire(state.Peer);
            }
            _peers.Clear();
            _rejected.Clear();
            if (!_ticking) _owner = null;
        }

        private bool OnOwnerThread => Environment.CurrentManagedThreadId == _threadId;
        private void RequireOwnerThread()
        {
            if (!OnOwnerThread) throw new InvalidOperationException("Module storage transport requires its game thread.");
        }

        private static bool IsEnrolled(ControlPeer peer)
        {
            try { return peer != null && peer.IsCurrent && ClientEnrollment.AllowsGameTraffic(peer); }
            catch (Exception) { return false; }
        }

        private PeerState GetPeer(ControlPeer peer)
        {
            if (_peers.TryGetValue(peer, out var state)) return state;
            if (RetiredPeers.Contains(peer)) return null;
            if (_peers.Count >= MaxPeers)
            {
                // The control transport also bounds current peers to MaxPeers. Reclaim only
                // retired identities, never a live replay guard, when making room for a new peer.
                var expired = new List<PeerState>();
                foreach (var old in _peers.Values)
                    if (!old.Peer.IsCurrent) { Stop(old, "PeerUnavailable"); expired.Add(old); }
                foreach (var previous in expired)
                {
                    if (previous.FailurePending && _rejected.Count < MaxPeers)
                    {
                        _rejected.Enqueue((previous.Peer, previous.Failure));
                        previous.FailurePending = false;
                    }
                    _peers.Remove(previous.Peer);
                }
                if (_peers.Count >= MaxPeers)
                {
                    if (Retire(peer) && _rejected.Count < MaxPeers) _rejected.Enqueue((peer, "PeerLimitReached"));
                    return null;
                }
            }
            state = new PeerState(peer);
            _peers.Add(peer, state);
            return state;
        }

        private static bool Retire(ControlPeer peer)
        {
            if (RetiredPeers.Contains(peer)) return false;
            RetiredPeers.RemoveWhere(old => !old.IsCurrent);
            // More current handles than this would violate the control transport's own bound.
            if (RetiredPeers.Count >= MaxPeers) return false;
            return RetiredPeers.Add(peer);
        }

        private void OnReceived(Datagram datagram)
        {
            if (_disposed || !OnOwnerThread || _receiving || datagram == null
                || !MatchesMagic(datagram.Payload.Span)) return;
            var peer = datagram.Peer;
            if (!IsEnrolled(peer))
            {
                if (peer != null && _peers.TryGetValue(peer, out var old)) Stop(old, "PeerUnavailable");
                return;
            }
            _receiving = true;
            PeerState state = null;
            try
            {
                state = GetPeer(peer);
                if (state == null || state.Stopped) return;
                long now = Environment.TickCount64;
                ExpireCompletions(state, now);
                if (!TryReadHeader(datagram.Payload.Span, out var header))
                {
                    Stop(state, "InvalidModuleStorageFrame");
                    return;
                }
                if (header.Kind == FrameKind.Data)
                    ReceiveData(state, header, datagram.Payload.Span.Slice(HeaderBytes), now);
                else ReceiveAck(state, header, now);
            }
            catch (Exception error)
            {
                if (state != null) Stop(state, "ReceiveFailed: " + error.GetType().Name);
            }
            finally { _receiving = false; }
        }

        private void ReceiveData(PeerState state, Header header, ReadOnlySpan<byte> data, long now)
        {
            Span<byte> fingerprint = stackalloc byte[FingerprintBytes];
            foreach (var completed in state.ReceivedCompletions)
            {
                if (completed.Id != header.Id) continue;
                SHA256.HashData(data, fingerprint);
                if (completed.Length != header.Length
                    || !fingerprint.SequenceEqual(completed.Fingerprints.AsSpan(header.Index * FingerprintBytes, FingerprintBytes)))
                {
                    Stop(state, "ConflictingCompletedFragment");
                    return;
                }
                completed.Pending = true;
                return;
            }
            var incoming = state.Incoming;
            if (incoming == null)
            {
                if (state.LastReceivedId == ulong.MaxValue || header.Id != state.LastReceivedId + 1)
                {
                    // An expired/evicted completion is never a new request. Without its fingerprint
                    // we cannot affirm identical data, so close this module channel on uncertainty.
                    Stop(state, "UnexpectedOrExpiredMessageId");
                    return;
                }
                if (header.Index >= WindowFragments) { Stop(state, "ReceiveWindowExceeded"); return; }
                int retained = header.Length + header.Count * (FingerprintBytes + 1) + InboundOverheadBytes;
                // A shared-budget shortfall drops this frame unacknowledged; the sender retransmits it.
                if (!Reserve(retained)) return;
                try { state.Incoming = incoming = new Inbound(header, now + DeadlineMilliseconds, retained); }
                catch (Exception) { Release(retained); throw; }
            }
            if (incoming.Id != header.Id || incoming.Bytes.Length != header.Length)
            {
                Stop(state, "ConflictingMessageMetadata");
                return;
            }
            if (now >= incoming.Deadline) { Stop(state, "ReceiveDeadlineExceeded"); return; }
            int offset = header.Index * FragmentBytes;
            if (incoming.Seen[header.Index])
            {
                if (!data.SequenceEqual(incoming.Bytes.AsSpan(offset, data.Length)))
                {
                    Stop(state, "ConflictingDuplicateFragment");
                    return;
                }
            }
            else
            {
                if (header.Index >= incoming.Base + WindowFragments)
                {
                    Stop(state, "ReceiveWindowExceeded");
                    return;
                }
                data.CopyTo(incoming.Bytes.AsSpan(offset));
                SHA256.HashData(data, incoming.Fingerprints.AsSpan(header.Index * FingerprintBytes, FingerprintBytes));
                incoming.Seen[header.Index] = true;
                while (incoming.Base < incoming.Count && incoming.Seen[incoming.Base]) incoming.Base++;
            }
            incoming.AckPending = true;
        }

        private void ReceiveAck(PeerState state, Header header, long now)
        {
            foreach (var completed in state.SentCompletions)
            {
                if (completed.Id != header.Id) continue;
                if (completed.Length != header.Length) Stop(state, "ConflictingCompletedAck");
                return;
            }
            if (!state.Outgoing.TryPeek(out var outgoing) || outgoing.Id != header.Id)
            {
                Stop(state, "UnexpectedOrExpiredAckId");
                return;
            }
            if (outgoing.Bytes.Length != header.Length) { Stop(state, "ConflictingAckMetadata"); return; }
            if (now >= outgoing.Deadline) { Stop(state, "SendDeadlineExceeded"); return; }
            if (header.Index > outgoing.Next) { Stop(state, "AckForUnsentFragment"); return; }
            for (int bit = 0; bit < WindowFragments; bit++)
                if ((header.Mask & (1u << bit)) != 0 && header.Index + bit >= outgoing.Next)
                {
                    Stop(state, "AckForUnsentFragment");
                    return;
                }
            if (header.Kind == FrameKind.Complete)
            {
                // This acknowledges delivery only. The parent must await its own commit response.
                if (state.SentCompletions.Count == MaxCompletionsPerDirection) state.SentCompletions.RemoveAt(0);
                state.SentCompletions.Add(new Completion(header.Id, header.Length, now + DeadlineMilliseconds, null));
                state.Outgoing.Dequeue();
                Release(outgoing.Retained);
                return;
            }
            for (int index = outgoing.Base; index < header.Index; index++) outgoing.Acked[index % WindowFragments] = true;
            for (int bit = 0; bit < WindowFragments; bit++)
            {
                int index = header.Index + bit;
                if ((header.Mask & (1u << bit)) != 0 && index >= outgoing.Base)
                    outgoing.Acked[index % WindowFragments] = true;
            }
            while (outgoing.Base < outgoing.Next && outgoing.Acked[outgoing.Base % WindowFragments])
            {
                outgoing.Acked[outgoing.Base % WindowFragments] = false;
                outgoing.Base++;
            }
        }

        private void Dispatch(PeerState state, long now)
        {
            var incoming = state.Incoming;
            if (!IsEnrolled(state.Peer)) { Stop(state, "PeerUnavailable"); return; }
            if (state.ReceivedCompletions.Count == MaxCompletionsPerDirection) RemoveReceivedCompletion(state, 0);
            var completed = new Completion(incoming.Id, incoming.Bytes.Length, now + DeadlineMilliseconds, incoming.Fingerprints);
            state.ReceivedCompletions.Add(completed);
            state.LastReceivedId = incoming.Id;
            state.Incoming = null;
            try
            {
                // Publish at most once, even if the callback throws, forgets the peer or disposes us.
                _received(state.Peer, incoming.Bytes);
                if (!state.Stopped && !_disposed)
                {
                    if (IsEnrolled(state.Peer)) completed.Pending = true;
                    else Stop(state, "PeerUnavailable");
                }
            }
            catch (Exception error) { Stop(state, "DeliveryCallbackFailed: " + error.GetType().Name); }
            finally { Release(incoming.Retained - incoming.Fingerprints.Length); }
        }

        private bool Pump(PeerState state, long now, ref bool ackSent, ref int budget)
        {
            ExpireCompletions(state, now);
            if (state.Incoming != null && now >= state.Incoming.Deadline)
            {
                Stop(state, "ReceiveDeadlineExceeded");
                return false;
            }
            state.Outgoing.TryPeek(out var outgoing);
            int dataIndex = FindDueFragment(outgoing, now);
            Completion completion = null;
            foreach (var candidate in state.ReceivedCompletions)
                if (candidate.Pending) { completion = candidate; break; }
            var incoming = state.Incoming;
            bool hasAck = completion != null || incoming?.AckPending == true;
            bool sendAck = hasAck && (!ackSent || dataIndex < 0);
            if (!sendAck && dataIndex < 0) return false;
            if (outgoing != null && now >= outgoing.Deadline) { Stop(state, "SendDeadlineExceeded"); return false; }

            Span<byte> frame = stackalloc byte[Datagrams.MaxPayloadBytes];
            int length = HeaderBytes;
            if (sendAck)
            {
                if (completion != null)
                    WriteHeader(frame, new Header(FrameKind.Complete, completion.Id, completion.Length, FragmentCount(completion.Length), 0));
                else
                {
                    uint mask = 0;
                    for (int bit = 0; bit < WindowFragments && incoming.Base + bit < incoming.Count; bit++)
                        if (incoming.Seen[incoming.Base + bit]) mask |= 1u << bit;
                    WriteHeader(frame, new Header(FrameKind.Ack, incoming.Id, incoming.Bytes.Length, incoming.Base, mask));
                }
            }
            else
            {
                int attempts = outgoing.Base == outgoing.Count ? outgoing.LastAttempts
                    : dataIndex == outgoing.Next ? 0 : outgoing.Attempts[dataIndex % WindowFragments];
                if (attempts >= MaxAttempts) { Stop(state, "FragmentRetryLimitReached"); return false; }
                int bytes = FragmentLength(outgoing.Bytes.Length, dataIndex);
                WriteHeader(frame, new Header(FrameKind.Data, outgoing.Id, outgoing.Bytes.Length, dataIndex, 0));
                outgoing.Bytes.AsSpan(dataIndex * FragmentBytes, bytes).CopyTo(frame.Slice(HeaderBytes));
                length += bytes;
            }

            budget--; // Count attempts, including backpressure, against this Tick's shared budget.
            DatagramSendResult result;
            try { result = Datagrams.Send(state.Peer, frame.Slice(0, length)); }
            catch (Exception error) { Stop(state, "DatagramSendUncertain: " + error.GetType().Name); return false; }
            if (result == DatagramSendResult.WindowFull) return false;
            if (result != DatagramSendResult.Queued)
            {
                Stop(state, "DatagramSendStopped: " + result);
                return false;
            }
            if (sendAck)
            {
                if (completion != null) completion.Pending = false;
                else incoming.AckPending = false;
                ackSent = true;
            }
            else
            {
                int slot = dataIndex % WindowFragments;
                if (outgoing.Base == outgoing.Count)
                {
                    outgoing.LastAttempts++;
                    outgoing.LastSent = now;
                }
                else
                {
                    if (dataIndex == outgoing.Next)
                    {
                        outgoing.Attempts[slot] = 0;
                        outgoing.Acked[slot] = false;
                        outgoing.Next++;
                    }
                    outgoing.Attempts[slot]++;
                    outgoing.SentAt[slot] = now;
                    if (dataIndex == outgoing.Count - 1)
                    {
                        outgoing.LastAttempts = outgoing.Attempts[slot];
                        outgoing.LastSent = now;
                    }
                }
            }
            return true;
        }

        private static int FindDueFragment(Outbound outgoing, long now)
        {
            if (outgoing == null) return -1;
            if (outgoing.Base == outgoing.Count)
                return now - outgoing.LastSent >= RetryMilliseconds ? outgoing.Count - 1 : -1;
            for (int index = outgoing.Base; index < outgoing.Next; index++)
                if (!outgoing.Acked[index % WindowFragments]
                    && now - outgoing.SentAt[index % WindowFragments] >= RetryMilliseconds) return index;
            return outgoing.Next < outgoing.Count && outgoing.Next < outgoing.Base + WindowFragments ? outgoing.Next : -1;
        }

        private bool Reserve(int bytes)
        {
            if (bytes > MaxRetainedBytes - _retainedBytes) return false;
            _retainedBytes += bytes;
            return true;
        }
        private void Release(int bytes) => _retainedBytes -= bytes;

        private void RemoveReceivedCompletion(PeerState state, int index)
        {
            Release(state.ReceivedCompletions[index].Fingerprints.Length);
            state.ReceivedCompletions.RemoveAt(index);
        }

        private void ExpireCompletions(PeerState state, long now)
        {
            for (int i = state.ReceivedCompletions.Count - 1; i >= 0; i--)
                if (now >= state.ReceivedCompletions[i].Expires) RemoveReceivedCompletion(state, i);
            for (int i = state.SentCompletions.Count - 1; i >= 0; i--)
                if (now >= state.SentCompletions[i].Expires) state.SentCompletions.RemoveAt(i);
        }

        private void Stop(PeerState state, string detail)
        {
            if (state.Stopped) return;
            state.Stopped = true;
            state.Failure = detail;
            state.FailurePending = true;
            while (state.Outgoing.TryDequeue(out var outgoing)) Release(outgoing.Retained);
            if (state.Incoming != null) { Release(state.Incoming.Retained); state.Incoming = null; }
            while (state.ReceivedCompletions.Count > 0) RemoveReceivedCompletion(state, state.ReceivedCompletions.Count - 1);
            state.SentCompletions.Clear();
        }

        private void NotifyFailure(PeerState state)
        {
            if (!state.FailurePending || _disposed) return;
            state.FailurePending = false;
            PublishFailure(state.Peer, state.Failure);
        }

        private void PublishFailure(ControlPeer peer, string detail)
        {
            try { _failed(peer, detail); }
            catch (Exception) { /* Already terminal; never retry an application/native action. */ }
        }

        private sealed class PeerState
        {
            internal PeerState(ControlPeer peer) => Peer = peer;
            internal readonly ControlPeer Peer;
            internal readonly Queue<Outbound> Outgoing = new(MaxQueuedMessagesPerPeer);
            internal readonly List<Completion> ReceivedCompletions = new(MaxCompletionsPerDirection);
            internal readonly List<Completion> SentCompletions = new(MaxCompletionsPerDirection);
            internal Inbound Incoming;
            internal ulong NextSendId;
            internal ulong LastReceivedId;
            internal bool Stopped;
            internal bool FailurePending;
            internal string Failure;
        }

        private sealed class Outbound
        {
            internal Outbound(ulong id, byte[] bytes, long deadline, int retained)
            { Id = id; Bytes = bytes; Deadline = deadline; Retained = retained; Count = FragmentCount(bytes.Length); }
            internal readonly ulong Id;
            internal readonly byte[] Bytes;
            internal readonly long Deadline;
            internal readonly int Retained;
            internal readonly int Count;
            internal readonly long[] SentAt = new long[WindowFragments];
            internal readonly byte[] Attempts = new byte[WindowFragments];
            internal readonly bool[] Acked = new bool[WindowFragments];
            internal int Base;
            internal int Next;
            internal int LastAttempts;
            internal long LastSent;
        }

        private sealed class Inbound
        {
            internal Inbound(Header header, long deadline, int retained)
            {
                Id = header.Id; Deadline = deadline; Retained = retained; Count = header.Count;
                Bytes = new byte[header.Length]; Seen = new bool[Count]; Fingerprints = new byte[Count * FingerprintBytes];
            }
            internal readonly ulong Id;
            internal readonly long Deadline;
            internal readonly int Retained;
            internal readonly int Count;
            internal readonly byte[] Bytes;
            internal readonly bool[] Seen;
            internal readonly byte[] Fingerprints;
            internal int Base;
            internal bool AckPending;
        }

        private sealed class Completion
        {
            internal Completion(ulong id, int length, long expires, byte[] fingerprints)
            { Id = id; Length = length; Expires = expires; Fingerprints = fingerprints; }
            internal readonly ulong Id;
            internal readonly int Length;
            internal readonly long Expires;
            internal readonly byte[] Fingerprints;
            internal bool Pending;
        }
    }
}
