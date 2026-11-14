// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Modstoke.Loader;
using UdpKit;

namespace Modstoke.Sons
{
    /// <summary>
    /// Opt-in native package compatibility at connection admission. Reports are client claims,
    /// not integrity evidence, account authentication, or permission to perform game actions.
    /// </summary>
    public static class ModAdmission
    {
        /// <summary>Reserved enrollment capability for this SDK protocol.</summary>
        public const ulong ProtocolCapability = 1UL << 63;
        public const int MaxDocumentBytes = ModAdmissionWire.MaxDocumentBytes;
        public const int MaxRecentObservations = 32;
        private const int WindowChunks = 4;
        private const int DataFramesPerTick = 32;
        private const double AdmissionSeconds = 120;
        private static readonly Dictionary<Guid, Admission> Entries = new();
        private static readonly Dictionary<Guid, (ControlPeer Peer, long Sequence)> Released = new();
        private static readonly SortedDictionary<long, ModAdmissionObservation> Recent = new();
        private static readonly UTF8Encoding Utf8 = new(false, true);
        // Tick runs only on the owner thread and never re-enters.
        private static readonly List<Guid> TickClosed = new();
        private static readonly List<Admission> TickEntries = new();
        private static int _ownerThreadId;
        private static int _reportedFailure;
        private static int _sendCursor;
        private static bool _selected;
        private static Guid _selectionRevision;
        private static long _observationSequence;
        private static ModRequirementsDocument _requirements;

        internal static bool Selected => _selected;
        private static bool OnOwnerThread => Environment.CurrentManagedThreadId == _ownerThreadId;
        private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        internal static void Initialize() => Interlocked.CompareExchange(ref _ownerThreadId, Environment.CurrentManagedThreadId, 0);

        /// <summary>
        /// Select on both peers before Bolt starts. A server must supply its parsed requirements;
        /// a joining client supplies null. Selection holds ordinary client gameplay until the
        /// requirements and report exchange succeeds, or until the negotiation window ends on a
        /// host that sends no enrollment at all. The default remains unselected.
        /// </summary>
        public static ControlResult SelectForNextStart(bool selected,
            ModRequirementsDocument serverRequirements = null,
            ulong localCapabilities = 0, ulong requiredClientCapabilities = 0)
        {
            if (_ownerThreadId == 0) return ControlResult.Unavailable;
            if (!OnOwnerThread) return ControlResult.WrongThread;
            var result = ClientEnrollment.SelectGatedForNextStart(selected,
                selected ? localCapabilities | ProtocolCapability : localCapabilities,
                selected ? requiredClientCapabilities | ProtocolCapability : requiredClientCapabilities);
            if (result != ControlResult.Success) return result;
            _selected = selected;
            _requirements = selected ? serverRequirements : null;
            _selectionRevision = Guid.NewGuid();
            return result;
        }

        /// <summary>
        /// Reads local setup on the game thread. Revision changes when a selection is replaced,
        /// including through ordinary enrollment. It is an observation, not an ownership lock.
        /// CanChangeForNextStart reports the native startup boundary; it does not promise that
        /// the required hooks are available or that a later selection will succeed.
        /// </summary>
        public static ControlResult TryReadSelection(out ModAdmissionSelectionSnapshot snapshot)
        {
            snapshot = null;
            if (_ownerThreadId == 0) return ControlResult.Unavailable;
            if (!OnOwnerThread) return ControlResult.WrongThread;
            try
            {
                var socket = BoltCore._udpSocket;
                var canChange = !BoltNetwork.isRunning && (socket == null || socket.WasCollected
                    || socket.State == UdpSocketState.Shutdown);
                snapshot = new ModAdmissionSelectionSnapshot(_selectionRevision, _selected,
                    ClientEnrollment.GameplayGateSelected, ControlTransport.State,
                    _requirements, canChange);
                return ControlResult.Success;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return ControlResult.Failed;
            }
        }

        internal static void ClearSelection()
        {
            _selected = false;
            _requirements = null;
            _selectionRevision = Guid.NewGuid();
            ResetForStart();
        }

        internal static void ResetForStart()
        {
            foreach (var entry in Entries.Values) RecordObservation(entry, true);
            Entries.Clear();
            foreach (var released in Released.Values) RecordRelease(released.Peer, released.Sequence, true);
            Released.Clear();
            _sendCursor = 0;
        }

        // A joining client whose host never enrolled is recorded as admitted with HostNotParticipating.
        // The record is withdrawn if the host takes part after all; its real attempt is recorded once
        // admission starts.
        internal static void HostNotParticipating(ControlPeer peer)
        {
            if (!_selected || !OnOwnerThread || peer == null || Entries.ContainsKey(peer.PeerId)
                || Released.ContainsKey(peer.PeerId)) return;
            var sequence = ++_observationSequence;
            Released.Add(peer.PeerId, (peer, sequence));
            RecordRelease(peer, sequence, false);
        }

        internal static void HostParticipating(ControlPeer peer)
        {
            if (!OnOwnerThread || peer == null || !Released.Remove(peer.PeerId, out var released)) return;
            Recent.Remove(released.Sequence);
        }

        private static void RecordRelease(ControlPeer peer, long sequence, bool connectionEnded)
        {
            if (Recent.Count == MaxRecentObservations && sequence < Recent.Keys.First()) return;
            Recent[sequence] = new ModAdmissionObservation(peer.PeerId, true, ModAdmissionState.Admitted,
                "HostNotParticipating", null, Array.Empty<ModRequirementResult>(), connectionEnded);
            if (Recent.Count > MaxRecentObservations) Recent.Remove(Recent.Keys.First());
        }

        /// <summary>
        /// Copies up to 32 recent attempts, newest attempt first. Records survive connection
        /// teardown and selection changes until eviction or process exit. These are local
        /// observations, not live connection status, account identity or gameplay authorization.
        /// An attempt that ends before mod admission starts has no record here, except a host that
        /// never took part: that is Admitted with EndReason "HostNotParticipating" and no requirements.
        /// </summary>
        public static ModAdmissionReadStatus TryReadRecent(
            out IReadOnlyList<ModAdmissionObservation> observations)
        {
            observations = Array.Empty<ModAdmissionObservation>();
            if (_ownerThreadId == 0) return ModAdmissionReadStatus.Unavailable;
            if (!OnOwnerThread) return ModAdmissionReadStatus.WrongThread;
            observations = Array.AsReadOnly(Recent.Values.Reverse().ToArray());
            return ModAdmissionReadStatus.Success;
        }

        private static void RecordObservation(Admission entry, bool connectionEnded = false)
        {
            // An older active peer must not displace a newer attempt when it finally closes.
            if (Recent.Count == MaxRecentObservations && entry.Sequence < Recent.Keys.First()) return;
            Recent[entry.Sequence] = new ModAdmissionObservation(entry.Peer.PeerId,
                entry.Peer.ServerSession == null, entry.State, entry.Reason, entry.Requirements?.Name,
                entry.Results, connectionEnded);
            if (Recent.Count > MaxRecentObservations) Recent.Remove(Recent.Keys.First());
        }

        /// <summary>Reads the exact current connection. Reported mods describe admission time only.</summary>
        public static ModAdmissionReadStatus TryRead(ControlPeer peer,
            out ModAdmissionSnapshot snapshot)
        {
            snapshot = null;
            if (_ownerThreadId == 0 || !_selected) return ModAdmissionReadStatus.Unavailable;
            if (!OnOwnerThread) return ModAdmissionReadStatus.WrongThread;
            if (peer == null || !Entries.TryGetValue(peer.PeerId, out var entry) || !ReferenceEquals(peer, entry.Peer))
                return ModAdmissionReadStatus.PeerUnavailable;
            try { Refresh(entry); }
            catch (Exception)
            {
                Reject(entry, ModAdmissionRejection.Unavailable, false);
                return ModAdmissionReadStatus.Failed;
            }
            snapshot = new ModAdmissionSnapshot(peer, entry.State, entry.Reason,
                entry.Requirements?.Name, entry.Results, entry.Report?.Entries);
            return ModAdmissionReadStatus.Success;
        }

        /// <summary>Looks up a live server session, never a reusable connection or player number.</summary>
        public static ModAdmissionReadStatus TryRead(ServerSession session,
            out ModAdmissionSnapshot snapshot)
        {
            snapshot = null;
            if (_ownerThreadId == 0 || !_selected) return ModAdmissionReadStatus.Unavailable;
            if (!OnOwnerThread) return ModAdmissionReadStatus.WrongThread;
            if (session == null) return ModAdmissionReadStatus.PeerUnavailable;
            foreach (var entry in Entries.Values)
                if (ReferenceEquals(session, entry.Peer.ServerSession))
                    return TryRead(entry.Peer, out snapshot);
            return ModAdmissionReadStatus.PeerUnavailable;
        }

        internal static void Enrolled(ControlPeer peer)
        {
            Observe(peer);
        }

        private static Admission Observe(ControlPeer peer)
        {
            if (!_selected || !OnOwnerThread || peer == null) return null;
            if (Entries.TryGetValue(peer.PeerId, out var existing))
                return ReferenceEquals(existing.Peer, peer) ? existing : null;
            if (Entries.Count >= ControlTransport.MaxPeers
                || !ClientEnrollment.TryGetModAdmissionNonce(peer, out var nonce)
                || !ControlTransport.EnableModAdmissionBudget(peer)) return null;
            var entry = new Admission(peer, nonce, Now + AdmissionSeconds);
            Entries.Add(peer.PeerId, entry);
            if (peer.ServerSession != null)
            {
                entry.Requirements = _requirements;
                if (_requirements == null)
                    Reject(entry, ModAdmissionRejection.Unavailable, false);
                else
                    entry.Outgoing = new Outgoing(ModAdmissionStream.Requirements, _requirements.GetCanonicalUtf8());
            }
            RecordObservation(entry);
            return entry;
        }

        internal static ModAdmissionState ReadGate(ControlPeer peer)
        {
            if (!_selected) return ModAdmissionState.Admitted;
            if (!OnOwnerThread) return ModAdmissionState.Rejected;
            var entry = Observe(peer);
            if (entry == null) return ModAdmissionState.Rejected;
            Refresh(entry);
            return entry.State;
        }

        internal static void Tick()
        {
            if (!_selected || !OnOwnerThread) return;
            var closed = TickClosed;
            closed.Clear();
            foreach (var pair in Entries)
                if (!pair.Value.Peer.IsCurrent) closed.Add(pair.Key);
            foreach (var id in closed)
            {
                RecordObservation(Entries[id], true);
                Entries.Remove(id);
            }
            closed.Clear();
            foreach (var pair in Released)
                if (!pair.Value.Peer.IsCurrent) closed.Add(pair.Key);
            foreach (var id in closed)
            {
                RecordRelease(Released[id].Peer, Released[id].Sequence, true);
                Released.Remove(id);
            }
            foreach (var peer in ControlTransport.GetPeers()) Observe(peer);
            var entries = TickEntries;
            entries.Clear();
            entries.AddRange(Entries.Values);
            foreach (var entry in entries)
            {
                try { Refresh(entry); }
                catch (Exception error)
                {
                    Reject(entry, ModAdmissionRejection.Unavailable, false);
                    ReportFailure(error);
                }
            }
            var remaining = DataFramesPerTick;
            var start = entries.Count == 0 ? 0 : _sendCursor % entries.Count;
            for (var i = 0; i < entries.Count && remaining > 0; i++)
            {
                var index = (start + i) % entries.Count;
                SendWindow(entries[index], ref remaining);
                _sendCursor = index + 1;
            }
            entries.Clear();
        }

        private static void Refresh(Admission entry)
        {
            if (entry.State == ModAdmissionState.Rejected) return;
            if (!ClientEnrollment.ModAdmissionNonceMatches(entry.Peer, entry.Nonce))
                Reject(entry, ModAdmissionRejection.Unavailable, false);
            else if (entry.State == ModAdmissionState.Pending && Now >= entry.Deadline)
                Reject(entry, ModAdmissionRejection.Expired, true);
        }

        internal static bool Receive(ControlPeer peer, ReadOnlySpan<byte> payload)
        {
            if (!_selected || !OnOwnerThread) return false;
            var entry = Observe(peer);
            if (entry == null) return false;
            try
            {
                Refresh(entry);
                if (entry.State == ModAdmissionState.Rejected) return true;
                if (!ModAdmissionWire.TryRead(payload, out var frame)
                    || !CryptographicOperations.FixedTimeEquals(entry.Nonce, frame.Nonce))
                {
                    Reject(entry, ModAdmissionRejection.InvalidMessage, true);
                    return true;
                }
                switch (frame.Kind)
                {
                    case ModAdmissionKind.Rejected:
                        Reject(entry, frame.Rejection, false);
                        break;
                    case ModAdmissionKind.Data:
                        ReceiveData(entry, frame);
                        break;
                    case ModAdmissionKind.Ack:
                        ReceiveAck(entry, frame);
                        break;
                    case ModAdmissionKind.Accepted:
                        ReceiveAccepted(entry);
                        break;
                }
                return true;
            }
            catch (Exception error)
            {
                Reject(entry, ModAdmissionRejection.Unavailable, false);
                ReportFailure(error);
                return true;
            }
        }

        private static void ReceiveData(Admission entry, ModAdmissionFrame frame)
        {
            var stream = entry.Peer.ServerSession != null ? ModAdmissionStream.Report
                : ModAdmissionStream.Requirements;
            if (frame.Stream != stream || (entry.Peer.ServerSession != null
                && (entry.Outgoing == null || entry.Outgoing.SentThrough != entry.Outgoing.Bytes.Length)))
            {
                Reject(entry, ModAdmissionRejection.InvalidMessage, true);
                return;
            }
            entry.Incoming ??= new Incoming(frame.Total);
            var incoming = entry.Incoming;
            var chunk = frame.Offset / ModAdmissionWire.MaxData;
            if (frame.Total != incoming.Bytes.Length || chunk >= incoming.WindowStart + WindowChunks)
            {
                Reject(entry, ModAdmissionRejection.InvalidMessage, true);
                return;
            }
            if (incoming.Received[chunk])
            {
                if (!frame.Data.SequenceEqual(incoming.Bytes.AsSpan(frame.Offset, frame.Data.Length)))
                    Reject(entry, ModAdmissionRejection.InvalidMessage, true);
                return;
            }
            frame.Data.CopyTo(incoming.Bytes.AsSpan(frame.Offset));
            incoming.Received[chunk] = true;
            var windowEnd = Math.Min(incoming.WindowStart + WindowChunks, incoming.Received.Length);
            for (var i = incoming.WindowStart; i < windowEnd; i++)
                if (!incoming.Received[i]) return;
            incoming.WindowStart = windowEnd;
            var acknowledged = Math.Min(windowEnd * ModAdmissionWire.MaxData, frame.Total);
            if (!Send(entry, ModAdmissionWire.WriteAck(entry.Nonce, stream, acknowledged, frame.Total))) return;
            if (acknowledged == frame.Total) CompleteIncoming(entry);
        }

        private static void CompleteIncoming(Admission entry)
        {
            if (entry.Peer.ServerSession == null)
            {
                if (!ModRequirements.TryParse(Utf8.GetString(entry.Incoming.Bytes), out var requirements, out _)
                    || !NativeModReport.TryCapture(out var report, out var snapshot, out _))
                {
                    Reject(entry, ModAdmissionRejection.Unavailable, true);
                    return;
                }
                entry.Requirements = requirements;
                entry.Report = snapshot;
                entry.Results = Compare(requirements, snapshot);
                entry.Outgoing = new Outgoing(ModAdmissionStream.Report, report);
                RecordObservation(entry);
            }
            else
            {
                if (!NativeModReport.TryRead(entry.Incoming.Bytes, out var report, out _))
                {
                    Reject(entry, ModAdmissionRejection.InvalidMessage, true);
                    return;
                }
                entry.Report = report;
                entry.Results = Compare(entry.Requirements, report);
                if (entry.Results.Any(result => result.Status != NativeRequirementStatus.Matched))
                    Reject(entry, ModAdmissionRejection.IncompatibleRequirements, true);
                else if (Send(entry, ModAdmissionWire.WriteAccepted(entry.Nonce)))
                {
                    entry.State = ModAdmissionState.Admitted;
                    RecordObservation(entry);
                }
            }
        }

        /// <summary>The disconnect notice for a peer refused over this server's required mods, naming what it lacks by
        /// short name and version; null when its admission did not fail on requirements.</summary>
        internal static string RefusalNotice(ControlPeer peer)
        {
            if (!_selected || !OnOwnerThread || peer == null || !Entries.TryGetValue(peer.PeerId, out var entry)
                || !ReferenceEquals(entry.Peer, peer) || entry.State != ModAdmissionState.Rejected
                || entry.Reason != nameof(ModAdmissionRejection.IncompatibleRequirements)) return null;
            var install = new List<string>();
            var update = new List<string>();
            var turnOn = new List<string>();
            foreach (var result in entry.Results)
            {
                var label = ModNames.LabelForId(result.Id, result.RequiredVersion);
                switch (result.Status)
                {
                    case NativeRequirementStatus.Matched: break;
                    case NativeRequirementStatus.VersionMismatch: update.Add(label); break;
                    case NativeRequirementStatus.Inactive: turnOn.Add(label); break;
                    default: install.Add(label); break;
                }
            }
            var steps = new List<string>();
            if (install.Count > 0) steps.Add("Install " + string.Join(", ", install) + ".");
            if (update.Count > 0) steps.Add("Update to " + string.Join(", ", update) + ".");
            if (turnOn.Count > 0) steps.Add("Turn on " + string.Join(", ", turnOn) + ".");
            if (steps.Count == 0) return null;
            var text = "This server needs mods you don't have. " + string.Join(" ", steps);
            while (Encoding.UTF8.GetByteCount(text) > ServerSessions.MaxDisconnectMessageKeyBytes)
                text = text.Substring(0, text.Length - 4) + "...";
            return text;
        }

        private static IReadOnlyList<ModRequirementResult> Compare(
            ModRequirementsDocument requirements, LoadedModsSnapshot report) =>
            Array.AsReadOnly(requirements.Packages.Select(package => new ModRequirementResult(
                package.Id, package.Version, report.CheckNativeRequirement(package.Id, package.Version))).ToArray());

        private static void ReceiveAck(Admission entry, ModAdmissionFrame frame)
        {
            var outgoing = entry.Outgoing;
            if (outgoing == null || frame.Stream != outgoing.Stream || frame.Total != outgoing.Bytes.Length
                || frame.Offset > outgoing.SentThrough
                || (frame.Offset > outgoing.Acknowledged && frame.Offset != outgoing.WindowEnd))
            {
                Reject(entry, ModAdmissionRejection.InvalidMessage, true);
                return;
            }
            if (frame.Offset > outgoing.Acknowledged) outgoing.Acknowledged = frame.Offset;
        }

        private static void ReceiveAccepted(Admission entry)
        {
            if (entry.Peer.ServerSession != null || entry.Outgoing == null
                || entry.Outgoing.Stream != ModAdmissionStream.Report
                || entry.Outgoing.SentThrough != entry.Outgoing.Bytes.Length
                || entry.Results.Count == 0 || entry.Results.Any(result => result.Status != NativeRequirementStatus.Matched))
            {
                Reject(entry, ModAdmissionRejection.InvalidMessage, true);
                return;
            }
            if (entry.State == ModAdmissionState.Admitted) return;
            if (!NativeModReport.TryCapture(out var current, out _, out _)
                || !current.AsSpan().SequenceEqual(entry.Outgoing.Bytes))
            {
                Reject(entry, ModAdmissionRejection.LocalModsChanged, true);
                return;
            }
            entry.State = ModAdmissionState.Admitted;
            RecordObservation(entry);
        }

        private static void SendWindow(Admission entry, ref int remaining)
        {
            var outgoing = entry.Outgoing;
            if (entry.State != ModAdmissionState.Pending || outgoing == null
                || outgoing.Acknowledged == outgoing.Bytes.Length) return;
            var end = outgoing.WindowEnd;
            while (remaining > 0 && outgoing.SentThrough < end)
            {
                remaining--;
                var offset = outgoing.SentThrough;
                if (!Send(entry, ModAdmissionWire.WriteData(entry.Nonce, outgoing.Stream, offset, outgoing.Bytes))) return;
                outgoing.SentThrough += Math.Min(ModAdmissionWire.MaxData, outgoing.Bytes.Length - offset);
            }
        }

        private static bool Send(Admission entry, byte[] payload)
        {
            if (ControlTransport.SendModAdmission(entry.Peer, payload) == ControlResult.Success) return true;
            Reject(entry, ModAdmissionRejection.Unavailable, false);
            return false;
        }

        private static void Reject(Admission entry, ModAdmissionRejection reason, bool notify)
        {
            if (entry.State == ModAdmissionState.Rejected) return;
            entry.State = ModAdmissionState.Rejected;
            entry.Reason = reason.ToString();
            RecordObservation(entry);
            if (notify)
                ControlTransport.SendModAdmission(entry.Peer,
                    ModAdmissionWire.WriteRejected(entry.Nonce, reason));
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Modstoke mod admission failed ("
                    + error.GetType().FullName + ").");
            }
            catch (Exception) { }
        }

        private sealed class Admission
        {
            internal readonly ControlPeer Peer;
            internal readonly long Sequence = ++_observationSequence;
            internal readonly byte[] Nonce;
            internal readonly double Deadline;
            internal ModAdmissionState State;
            internal string Reason = "";
            internal ModRequirementsDocument Requirements;
            internal LoadedModsSnapshot Report;
            internal IReadOnlyList<ModRequirementResult> Results = Array.Empty<ModRequirementResult>();
            internal Outgoing Outgoing;
            internal Incoming Incoming;
            internal Admission(ControlPeer peer, byte[] nonce, double deadline)
            { Peer = peer; Nonce = nonce; Deadline = deadline; }
        }

        private sealed class Outgoing
        {
            internal readonly ModAdmissionStream Stream;
            internal readonly byte[] Bytes;
            internal int SentThrough;
            internal int Acknowledged;
            internal int WindowEnd => Math.Min(Bytes.Length, Acknowledged + WindowChunks * ModAdmissionWire.MaxData);
            internal Outgoing(ModAdmissionStream stream, byte[] bytes) { Stream = stream; Bytes = bytes; }
        }

        private sealed class Incoming
        {
            internal readonly byte[] Bytes;
            internal readonly bool[] Received;
            internal int WindowStart;
            internal Incoming(int total)
            {
                Bytes = new byte[total];
                Received = new bool[(total + ModAdmissionWire.MaxData - 1) / ModAdmissionWire.MaxData];
            }
        }
    }

    public enum ModAdmissionState { Pending, Admitted, Rejected }
    public enum ModAdmissionReadStatus { Success, Unavailable, WrongThread, PeerUnavailable, Failed }

    /// <summary>
    /// Last observed state for one process-local attempt. ConnectionEnded records local cleanup;
    /// false does not prove liveness. Closing an attempt does not invent a rejection or acceptance.
    /// </summary>
    public sealed class ModAdmissionObservation
    {
        internal ModAdmissionObservation(Guid peerId, bool joiningClient,
            ModAdmissionState state, string reason, string requirementsName,
            IReadOnlyList<ModRequirementResult> requirements, bool connectionEnded)
        {
            PeerId = peerId;
            IsJoiningClient = joiningClient;
            State = state;
            EndReason = reason ?? "";
            RequirementsName = requirementsName ?? "";
            Requirements = Array.AsReadOnly(requirements.ToArray());
            ConnectionEnded = connectionEnded;
            ObservedAtUtc = DateTimeOffset.UtcNow;
        }
        public Guid PeerId { get; }
        public bool IsJoiningClient { get; }
        public ModAdmissionState State { get; }
        public string EndReason { get; }
        public string RequirementsName { get; }
        public IReadOnlyList<ModRequirementResult> Requirements { get; }
        public bool ConnectionEnded { get; }
        public DateTimeOffset ObservedAtUtc { get; }
    }

    /// <summary>Current local configuration, separate from any connection's admission result.</summary>
    public sealed class ModAdmissionSelectionSnapshot
    {
        internal ModAdmissionSelectionSnapshot(Guid revision, bool selected, bool gateSelected,
            ControlState transportState, ModRequirementsDocument requirements, bool canChange)
        {
            Revision = revision;
            Selected = selected;
            GameplayGateSelected = gateSelected;
            TransportState = transportState;
            ServerRequirements = requirements;
            CanChangeForNextStart = canChange;
        }
        public Guid Revision { get; }
        public bool Selected { get; }
        public bool GameplayGateSelected { get; }
        public ControlState TransportState { get; }
        public ModRequirementsDocument ServerRequirements { get; }
        public bool CanChangeForNextStart { get; }
    }

    public sealed class ModRequirementResult
    {
        internal ModRequirementResult(string id, string version, NativeRequirementStatus status)
        { Id = id; RequiredVersion = version; Status = status; }
        public string Id { get; }
        public string RequiredVersion { get; }
        public NativeRequirementStatus Status { get; }
    }

    /// <summary>A local observation of an untrusted peer report; never cache it as gameplay authorization.</summary>
    public sealed class ModAdmissionSnapshot
    {
        internal ModAdmissionSnapshot(ControlPeer peer, ModAdmissionState state,
            string reason, string name, IReadOnlyList<ModRequirementResult> results,
            IReadOnlyList<LoadedModEntry> reported)
        { Peer = peer; State = state; EndReason = reason; RequirementsName = name ?? ""; Requirements = results;
            ReportedNativeMods = reported ?? Array.Empty<LoadedModEntry>(); ObservedAtUtc = DateTimeOffset.UtcNow; }
        public ControlPeer Peer { get; }
        public ModAdmissionState State { get; }
        public string EndReason { get; }
        public string RequirementsName { get; }
        public IReadOnlyList<ModRequirementResult> Requirements { get; }
        public IReadOnlyList<LoadedModEntry> ReportedNativeMods { get; }
        public DateTimeOffset ObservedAtUtc { get; }
    }
}
