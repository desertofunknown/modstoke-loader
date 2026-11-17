// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>
    /// Negotiates protocol participation for an exact connection. Enrollment is not account
    /// authentication, proof of unmodified software, permission, or gameplay admission.
    /// </summary>
    /// <remarks>
    /// The joining client speaks first, and only to a host it knows runs this negotiation: a stream block on a
    /// channel the receiver never registered makes UdpKit drop the whole connection. The host answers a hello with
    /// a challenge, or says it does not take part; after enrollment and its own checks it sends Admitted, and only
    /// then does a gated client send gameplay. A game packet that reaches the host's gate before that is never
    /// silently discarded: it ends the connection with a reason. Admission then holds for the connection's life.
    /// </remarks>
    public static partial class ClientEnrollment
    {
        public const ushort ProtocolVersion = EnrollmentWire.ProtocolVersion;
        private const double NegotiationSeconds = 15;
        private const double AdmissionSeconds = 15;
        // A content declaration (8 frames at most), Admitted and one mod admission window.
        private const int MaxAfterAccepted = 16;
        // An admitted Modstoke client sends its notices hello at once; silence this long points at a stalled
        // reliable event stream rather than a slow handshake.
        private const double HelloWatchSeconds = 10;
        private const double MaxTickSeconds = 1;
        private const double SettledRefreshSeconds = 0.25;
        private static readonly Dictionary<Guid, Enrollment> Entries = new();
        private static int _ownerThreadId;
        // Negotiation windows run on this clock, which moves at most MaxTickSeconds per tick: a main-thread stall
        // (a world load, an autosave) must not use up a window while the peer's answer waits in the queue.
        private static double _clock, _clockReadAt, _nextSettledRefresh;
        // _selected and the capabilities are what plugins asked for; the content check adds to them per start.
        private static bool _selected;
        private static bool _contentSelected;
        private static bool _contentRequired;
        private static bool _faulted;
        private static ulong _localCapabilities;
        private static ulong _requiredClientCapabilities;

        internal static void Initialize(HarmonySupportSnapshot support)
        {
            if (Interlocked.CompareExchange(ref _ownerThreadId, Environment.CurrentManagedThreadId, 0) != 0)
                return;
            _gateSupport = support;
            ControlTransport.Opened += OnOpened;
            ControlTransport.Closed += OnClosed;
            ControlTransport.Received += OnReceived;
        }

        /// <summary>
        /// Select on both peers before Bolt starts. Capability bits belong to the consuming
        /// protocol; received claims only describe compatibility and cannot grant authority.
        /// Selection reserves the negotiation transport and does not enable strict combat.
        /// </summary>
        /// <remarks>A selected client opens negotiation only with a host that said beforehand that it answers it,
        /// through its Steam lobby label or its server rules; with any other host it plays as the game does.</remarks>
        public static ControlResult SelectForNextStart(bool selected,
            ulong localCapabilities, ulong requiredClientCapabilities)
        {
            if (_ownerThreadId == 0)
                return ControlResult.Unavailable;
            if (!OnOwnerThread)
                return ControlResult.WrongThread;
            var result = ControlTransport.SelectForNextStart(selected);
            if (result != ControlResult.Success)
                return result;
            ResetForStart();
            ModAdmission.ClearSelection();
            _gateSelected = false;
            _selected = selected;
            _localCapabilities = localCapabilities;
            _requiredClientCapabilities = requiredClientCapabilities;
            return result;
        }

        /// <summary>
        /// Reads current local protocol state on the game thread. Do not cache this snapshot
        /// as authorization; a reconnect, transport failure or protocol rejection retires it.
        /// </summary>
        public static EnrollmentReadStatus TryRead(ControlPeer peer,
            out EnrollmentSnapshot snapshot)
        {
            snapshot = null;
            if (_ownerThreadId == 0)
                return EnrollmentReadStatus.Unavailable;
            if (!OnOwnerThread)
                return EnrollmentReadStatus.WrongThread;
            if (!Selected || _faulted)
                return EnrollmentReadStatus.Unavailable;
            if (peer == null || !Entries.TryGetValue(peer.PeerId, out var entry)
                || !ReferenceEquals(entry.Peer, peer))
                return EnrollmentReadStatus.PeerUnavailable;
            try
            {
                Refresh(entry);
                snapshot = new EnrollmentSnapshot(peer, entry.State, entry.Reason,
                    entry.RequiredCapabilities, entry.OfferedCapabilities);
                return EnrollmentReadStatus.Success;
            }
            catch (Exception error)
            {
                Retire(entry, EnrollmentEndReason.TransportUnavailable);
                NetworkFailures.Report("Modstoke client enrollment", error);
                return EnrollmentReadStatus.Failed;
            }
        }

        /// <summary>Looks up the exact current remote-client session, never a player or Steam ID.</summary>
        public static EnrollmentReadStatus TryRead(ServerSession session,
            out EnrollmentSnapshot snapshot)
        {
            snapshot = null;
            if (_ownerThreadId == 0)
                return EnrollmentReadStatus.Unavailable;
            if (!OnOwnerThread)
                return EnrollmentReadStatus.WrongThread;
            if (!Selected || _faulted)
                return EnrollmentReadStatus.Unavailable;
            if (session != null)
                foreach (var entry in Entries.Values)
                    if (ReferenceEquals(entry.Peer.ServerSession, session))
                        return TryRead(entry.Peer, out snapshot);
            return EnrollmentReadStatus.PeerUnavailable;
        }

        private static bool OnOwnerThread => Environment.CurrentManagedThreadId == _ownerThreadId;
        private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        /// <summary>Whether this start negotiates at all, for a plugin or for the content check.</summary>
        internal static bool Selected => _selected || _contentSelected;
        // Every Loader that speaks this version can answer a content declaration, with or without mods of its own.
        private static ulong LocalCapabilities => _localCapabilities | ContentAgreement.Capability;
        private static ulong RequiredClientCapabilities => _requiredClientCapabilities
            | (_contentRequired ? ContentAgreement.Capability : 0);

        /// <summary>What this start negotiates, for the one log line each Bolt start writes.</summary>
        internal static string SelectionSummary
        {
            get
            {
                if (!Selected)
                    return "off (a client's hello is still answered)";
                var parts = new List<string>();
                if (_contentSelected) parts.Add(_contentRequired ? "mod content check, required" : "mod content check");
                if (ModAdmission.Selected) parts.Add("mod admission");
                if (_selected && !ModAdmission.Selected) parts.Add("plugin enrollment");
                parts.Add(GameplayGateSelected ? "gameplay held until admitted" : "no gameplay gate");
                return string.Join(", ", parts);
            }
        }

        internal static bool TryGetModAdmissionNonce(ControlPeer peer, out byte[] nonce)
        {
            nonce = null;
            var entry = FindModAdmissionEntry(peer);
            if (entry == null)
                return false;
            nonce = (byte[])entry.Nonce.Clone();
            return true;
        }

        internal static bool ModAdmissionNonceMatches(ControlPeer peer, ReadOnlySpan<byte> nonce)
        {
            var entry = FindModAdmissionEntry(peer);
            return entry != null && CryptographicOperations.FixedTimeEquals(entry.Nonce, nonce);
        }

        private static Enrollment FindModAdmissionEntry(ControlPeer peer)
        {
            if (!OnOwnerThread || !Selected || _faulted || peer == null
                || !Entries.TryGetValue(peer.PeerId, out var entry) || !ReferenceEquals(entry.Peer, peer))
                return null;
            Refresh(entry);
            if (entry.State != EnrollmentState.Enrolled || entry.Nonce == null
                || (entry.RequiredCapabilities & ModAdmission.ProtocolCapability) == 0
                || (entry.OfferedCapabilities & ModAdmission.ProtocolCapability) == 0)
                return null;
            return entry;
        }

        internal static void ResetForStart()
        {
            if (!OnOwnerThread)
                return;
            Entries.Clear();
            ModAdmission.ResetForStart();
            _faulted = false;
            _contentSelected = false;
            _contentRequired = false;
            _contentGate = false;
            _clockReadAt = Now;
            ResetGateConnection();
            ResetAdmissions();
        }

        internal static void Tick()
        {
            if (!OnOwnerThread || !Selected || _faulted)
                return;
            try
            {
                // After the transport's pump, so frames that queued up during a stall are handled first.
                var now = Now;
                _clock += Math.Clamp(now - _clockReadAt, 0, MaxTickSeconds);
                _clockReadAt = now;
                List<Guid> closed = null;
                foreach (var entry in Entries.Values)
                    if (!entry.Peer.IsCurrent) (closed ??= new List<Guid>()).Add(entry.Peer.PeerId);
                if (closed != null)
                    foreach (var id in closed) Entries.Remove(id);

                // Lifecycle notifications are bounded, so reconcile the current handles as well.
                foreach (var peer in ControlTransport.GetPeers())
                    if (!Entries.ContainsKey(peer.PeerId)) Observe(peer);
                // A settled connection's transport is rechecked a few times a second, like the transport's own
                // pruning; one still negotiating every frame, since its windows and the gate run on it.
                var settledDue = now >= _nextSettledRefresh;
                if (settledDue) _nextSettledRefresh = now + SettledRefreshSeconds;
                foreach (var entry in Entries.Values)
                {
                    if (settledDue || Negotiating(entry)) Refresh(entry);
                    Advance(entry);
                }
            }
            catch (Exception error)
            {
                // Connections already admitted stay latched open; only those still negotiating are refused.
                _faulted = true;
                foreach (var entry in Entries.Values)
                    Retire(entry, EnrollmentEndReason.TransportUnavailable);
                NetworkFailures.Report("Modstoke client enrollment", error);
            }
        }

        private static bool Negotiating(Enrollment entry) => entry.State == EnrollmentState.Pending
            || (entry.State == EnrollmentState.Enrolled && !entry.Admitted);

        /// <summary>Whether this peer's negotiation is still open: not yet observed, pending, or enrolled but not admitted.</summary>
        internal static bool IsNegotiating(ControlPeer peer)
        {
            if (!OnOwnerThread || !Selected || _faulted || peer == null) return false;
            return !Entries.TryGetValue(peer.PeerId, out var entry) || (ReferenceEquals(entry.Peer, peer) && Negotiating(entry));
        }

        private static Enrollment Observe(ControlPeer peer)
        {
            if (peer == null || !Selected || _faulted
                || ControlTransport.State != ControlState.Active
                || ControlTransport.Resolve(peer) == null)
                return null;
            if (Entries.TryGetValue(peer.PeerId, out var existing))
                return ReferenceEquals(existing.Peer, peer) ? existing : null;
            if (Entries.Count >= ControlTransport.MaxPeers)
                return null;
            var entry = new Enrollment(peer, Now, _clock + NegotiationSeconds);
            if (peer.ServerSession != null)
            {
                entry.Nonce = RandomNumberGenerator.GetBytes(EnrollmentWire.NonceBytes);
                entry.RequiredCapabilities = RequiredClientCapabilities;
            }
            else
                entry.HelloNonce = RandomNumberGenerator.GetBytes(EnrollmentWire.NonceBytes);
            Entries.Add(peer.PeerId, entry);
            return entry;
        }

        private static void OnOpened(ControlPeer peer)
        {
            if (!OnOwnerThread || !Selected || _faulted)
                return;
            var entry = Observe(peer);
            if (entry != null) Advance(entry);
        }

        private static void OnClosed(ControlPeer peer)
        {
            if (OnOwnerThread && peer != null)
            {
                Entries.Remove(peer.PeerId);
                ContentAgreement.Closed(peer);
            }
        }

        // Sends what this side owes next. The stream channel is reliable, so every frame is sent once; it resends a lost
        // frame on its own, so frames can arrive out of order, which the receivers allow for.
        private static void Advance(Enrollment entry)
        {
            if (entry.State == EnrollmentState.Retired)
                return;
            if (entry.Peer.ServerSession == null)
            {
                if (entry.State == EnrollmentState.Pending && !entry.HelloSent)
                {
                    entry.HelloSent = true;
                    // UdpKit drops the whole connection over a block on a channel the host lacks: never send one to a
                    // vanilla or older host, or to one whose negotiation channel has another id than this game's.
                    if (ContentAgreement.HostNegotiates)
                        Send(entry, EnrollmentMessageKind.Hello);
                    else if (ContentAgreement.HostChannel > 0)
                        ReleaseClient(entry, "This server answers Modstoke's join negotiation on stream channel "
                            + ContentAgreement.HostChannel + ", but this game registered it as " + ControlTransport.ChannelId
                            + " (the two games register different stream channels), so it isn't asked; playing as the "
                            + "game does.");
                    else
                        ReleaseClient(entry, "This server didn't say it runs Modstoke's join negotiation, so it isn't "
                            + "asked; playing as the game does.");
                }
                return;
            }
            if (entry.State == EnrollmentState.Enrolled && !entry.Admitted)
                TryAdmit(entry);
            else if (entry.Admitted && !entry.HelloChecked && _clock >= entry.Deadline)
            {
                entry.HelloChecked = true;
                if (ServerNotices.HooksInstalled
                    && !ServerNotices.HelloArrived(entry.Peer.ConnectionPointer, entry.Peer.ConnectionId))
                    RuntimeEntryPoint.LogSource?.LogWarning("Connection " + entry.Peer.ConnectionId + " was admitted "
                        + HelloWatchSeconds + " s ago, but its Modstoke notices hello has not arrived. Its reliable "
                        + "events may not be reaching this server; the player should rejoin if chat, building or "
                        + "pickups stop working for them.");
            }
        }

        private static void Refresh(Enrollment entry)
        {
            if (entry.State == EnrollmentState.Retired)
                return;
            if (ControlTransport.State != ControlState.Active)
                Retire(entry, EnrollmentEndReason.TransportUnavailable);
            else if (ControlTransport.Resolve(entry.Peer, out var held) == null)
            {
                // Paused session tracking holds the peer; the transport settles it on a later frame.
                if (!held) Retire(entry, EnrollmentEndReason.TransportUnavailable);
            }
            // A client says hello only to a host that said it answers, so that host's silence is a fault, not a sign it
            // doesn't take part: releasing the client's gameplay would only get it refused, or kicked by a late answer.
            else if (_clock >= entry.Deadline && (entry.State == EnrollmentState.Pending
                || entry.State == EnrollmentState.Enrolled && !entry.Admitted && entry.Peer.ServerSession == null))
                Retire(entry, EnrollmentEndReason.Expired);
        }

        private static void ReleaseClient(Enrollment entry, string message)
        {
            entry.State = EnrollmentState.HostNotParticipating;
            ModAdmission.HostNotParticipating(entry.Peer);
            RuntimeEntryPoint.LogSource?.LogInfo(message);
        }

        private static void Retire(Enrollment entry, EnrollmentEndReason reason)
        {
            if (entry.State == EnrollmentState.Retired)
                return;
            entry.State = EnrollmentState.Retired;
            entry.Reason = reason;
            entry.Nonce = null;
            // A host explains its refusals when it disconnects the peer; a client has only this line.
            if (entry.Peer.ServerSession == null)
                RuntimeEntryPoint.LogSource?.LogInfo("Modstoke negotiation with the server ended after "
                    + Milliseconds(entry.OpenedAt) + " ms: " + reason + ".");
        }

        private static long Milliseconds(double since) => (long)((Now - since) * 1000);

        private sealed class Enrollment
        {
            internal readonly ControlPeer Peer;
            internal readonly double OpenedAt;
            // When the current step times out, on the negotiation clock: hello and response, the host's checks
            // up to Admitted on a client, and the notices hello watch once a host admitted the peer.
            internal double Deadline;
            internal EnrollmentState State;
            internal EnrollmentEndReason Reason;
            internal EnrollmentRejection RemoteRejection;
            internal byte[] Nonce;
            internal byte[] HelloNonce;
            internal ulong RequiredCapabilities;
            internal ulong OfferedCapabilities;
            internal bool HelloSent;
            internal double HelloAt;
            internal double EnrolledAt;
            internal bool Admitted;
            internal bool HelloChecked;
            // Frames the host sent behind Accepted that overtook it, replayed once it arrives.
            internal List<byte[]> AfterAccepted;

            internal Enrollment(ControlPeer peer, double openedAt, double deadline)
            {
                Peer = peer;
                OpenedAt = openedAt;
                Deadline = deadline;
            }
        }
    }

    /// <summary>
    /// HostNotParticipating is a joining client whose host said it does not take part, or never said it runs this
    /// negotiation. Its gameplay is released for the rest of the connection. A later host frame returns it to
    /// Pending for ordinary negotiation, unless gameplay was already sent while released: then it retires with
    /// HostAnsweredLate and disconnects.
    /// </summary>
    public enum EnrollmentState { Pending, Enrolled, Retired, HostNotParticipating }
    public enum EnrollmentEndReason
    {
        None, TransportUnavailable, Expired, IncompatibleCapabilities, InvalidMessage, RemoteRejected, SendFailed,
        HostAnsweredLate, ProtocolMismatch, GameplayBeforeAdmission
    }
    public enum EnrollmentReadStatus { Success, Unavailable, WrongThread, PeerUnavailable, Failed }

    /// <summary>A local protocol observation. Capability claims are untrusted and confer no permissions.</summary>
    public sealed class EnrollmentSnapshot
    {
        internal EnrollmentSnapshot(ControlPeer peer,
            EnrollmentState state, EnrollmentEndReason reason,
            ulong requiredCapabilities, ulong offeredCapabilities)
        {
            Peer = peer;
            State = state;
            EndReason = reason;
            RequiredCapabilities = requiredCapabilities;
            OfferedCapabilities = offeredCapabilities;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public ControlPeer Peer { get; }
        public EnrollmentState State { get; }
        public EnrollmentEndReason EndReason { get; }
        public ulong RequiredCapabilities { get; }
        public ulong OfferedCapabilities { get; }
        public DateTimeOffset ObservedAt { get; }
    }
}
