// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace Modstoke.Sons
{
    public static partial class ServerSessions
    {
        /// <summary>
        /// Copies what a remote session has claimed and sent so far, on the game thread. Every value is
        /// client-controlled evidence for review, never proof of a vanilla, modded or tampered client.
        /// Records stay readable through the session handle after it closes; enrollment and admission
        /// views are only included while the session is current.
        /// </summary>
        public static bool TryReadClientSignals(ServerSession session,
            out ClientSignalsSnapshot snapshot, out ConnectionReadStatus status)
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
            if (session == null)
            {
                status = ConnectionReadStatus.SessionUnavailable;
                return false;
            }

            EnrollmentSnapshot enrollment = null;
            ModAdmissionSnapshot admission = null;
            if (session.IsCurrent)
            {
                if (ClientEnrollment.TryRead(session, out var enrolled) == EnrollmentReadStatus.Success)
                    enrollment = enrolled;
                if (ModAdmission.TryRead(session, out var admitted) == ModAdmissionReadStatus.Success)
                    admission = admitted;
            }
            snapshot = ClientSignals.Capture(session, enrollment, admission);
            status = ConnectionReadStatus.Success;
            return true;
        }

        /// <summary>
        /// The current <see cref="ClientSignalsSnapshot.Revision"/> without copying the record, so a periodic
        /// sweep can re-read only changed sessions. Zero before anything was recorded or off the game thread.
        /// </summary>
        public static long ReadClientSignalsRevision(ServerSession session) =>
            session != null && HooksInstalled && Environment.CurrentManagedThreadId == _ownerThreadId
                ? ClientSignals.ReadRevision(session) : 0;
    }

    /// <summary>Game-thread recorder behind <see cref="ServerSessions.TryReadClientSignals"/>.</summary>
    internal static class ClientSignals
    {
        private const int MaxNames = 8;
        private const int MaxSdkEvents = 16;
        private const int MaxPrefixLength = 24;
        private const int MaxEventIdLength = 64;
        private static readonly ConditionalWeakTable<ServerSession, Record> Records = new();

        internal static long ReadRevision(ServerSession session) =>
            Records.TryGetValue(session, out var record) ? record.Revision : 0;

        internal static void RecordHello(ServerSession session, NoticeProtocol.Frame frame)
        {
            var record = Change(session);
            var claim = new ClientHelloClaim(DateTimeOffset.UtcNow, frame.ReceiptVersion, frame.CommandVersion,
                frame.CommandReplyVersion, frame.MessengerVersion, frame.Loader, frame.LoaderVersion,
                frame.NativeMods, frame.LegacyMods);
            record.HelloCount++;
            if (record.Hello == null)
                record.Hello = claim;
            else if (!record.Hello.SameClaims(claim))
                record.HelloClaimsChanged = true;
        }

        internal static void RecordSdkPacket(BoltConnection source, int eventHash, bool registered)
        {
            if (!TryChange(source, out var record))
                return;
            if (registered)
            {
                record.SdkPackets++;
                if (record.SdkEventHashes.Count < MaxSdkEvents && !record.SdkEventHashes.Contains(eventHash))
                    record.SdkEventHashes.Add(eventHash);
                return;
            }
            record.UnregisteredSdkPackets++;
            if (record.UnregisteredSdkHashes.Count < MaxNames && !record.UnregisteredSdkHashes.Contains(eventHash))
                record.UnregisteredSdkHashes.Add(eventHash);
        }

        internal static void RecordMalformedNotice(BoltConnection source)
        {
            if (!TryChange(source, out var record))
                return;
            record.MalformedNotices++;
            record.MarkUnexpected();
        }

        internal static void RecordUnexpectedNotice(BoltConnection source)
        {
            if (!TryChange(source, out var record))
                return;
            record.UnexpectedNotices++;
            record.MarkUnexpected();
        }

        internal static void RecordTunnelChat(BoltConnection source, string message)
        {
            if (!TryChange(source, out var record))
                return;
            record.TunnelChats++;
            var prefix = Printable(message);
            if (record.TunnelPrefixes.Count < MaxNames && !record.TunnelPrefixes.Contains(prefix))
                record.TunnelPrefixes.Add(prefix);
        }

        internal static void RecordUnknownCommand(ServerSession session, string name)
        {
            var record = Change(session);
            record.UnknownCommands++;
            if (name != null && record.UnknownCommandNames.Count < MaxNames
                && !record.UnknownCommandNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                record.UnknownCommandNames.Add(name);
        }

        internal static void RecordRateLimitedCommand(ServerSession session) => Change(session).RateLimitedCommands++;

        internal static void RecordReplayedCommand(ServerSession session)
        {
            var record = Change(session);
            record.ReplayedCommands++;
            record.MarkUnexpected();
        }

        internal static ClientSignalsSnapshot Capture(ServerSession session,
            EnrollmentSnapshot enrollment, ModAdmissionSnapshot admission)
        {
            Records.TryGetValue(session, out var record);
            record ??= new Record();
            var traffic = new ClientTrafficSignals(record.SdkPackets,
                Array.AsReadOnly(record.SdkEventHashes.Select(DescribeSdkEvent).ToArray()),
                record.UnregisteredSdkPackets, Array.AsReadOnly(record.UnregisteredSdkHashes.ToArray()),
                record.MalformedNotices, record.UnexpectedNotices, record.UnknownCommands,
                Array.AsReadOnly(record.UnknownCommandNames.ToArray()), record.RateLimitedCommands,
                record.ReplayedCommands, record.TunnelChats, Array.AsReadOnly(record.TunnelPrefixes.ToArray()),
                record.FirstUnexpectedAt);
            return new ClientSignalsSnapshot(session, record.Revision, record.Hello, record.HelloCount,
                record.HelloClaimsChanged, enrollment, admission, traffic);
        }

        private static Record Change(ServerSession session)
        {
            var record = Records.GetOrCreateValue(session);
            record.Revision++;
            return record;
        }

        private static bool TryChange(BoltConnection source, out Record record)
        {
            var session = ServerSessions.ResolveObserved(source);
            record = session == null ? null : Change(session);
            return record != null;
        }

        // Names come from this host's own registrations, so packets only record hashes.
        private static string DescribeSdkEvent(int hash)
        {
            try
            {
                if (SonsSdk.Networking.Packets.RegisteredEvents.TryGetValue(hash, out var registration)
                    && registration.ReadFunc?.Target is SonsSdk.Networking.Packets.NetEvent netEvent
                    && netEvent.Id is { Length: > 0 and <= MaxEventIdLength } id)
                    return id;
            }
            catch (Exception) { }
            return "#" + hash.ToString("X8");
        }

        // Keeps a short, display-safe start of the message; control characters become \uXXXX.
        private static string Printable(string message)
        {
            var text = new StringBuilder(MaxPrefixLength + 16);
            for (var index = 0; index < message.Length && index < MaxPrefixLength; index++)
            {
                var character = message[index];
                if (character is >= ' ' and <= '~' and not ('<' or '>'))
                    text.Append(character);
                else
                    text.Append("\\u").Append(((int)character).ToString("X4"));
            }
            return text.ToString();
        }

        private sealed class Record
        {
            internal long Revision;
            internal ClientHelloClaim Hello;
            internal int HelloCount;
            internal bool HelloClaimsChanged;
            internal int SdkPackets;
            internal readonly List<int> SdkEventHashes = new();
            internal int UnregisteredSdkPackets;
            internal readonly List<int> UnregisteredSdkHashes = new();
            internal int MalformedNotices;
            internal int UnexpectedNotices;
            internal int UnknownCommands;
            internal readonly List<string> UnknownCommandNames = new();
            internal int RateLimitedCommands;
            internal int ReplayedCommands;
            internal int TunnelChats;
            internal readonly List<string> TunnelPrefixes = new();
            internal DateTimeOffset? FirstUnexpectedAt;

            internal void MarkUnexpected() => FirstUnexpectedAt ??= DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// One session's client-controlled claims and traffic observations. A missing <see cref="Hello"/> means
    /// no Modstoke handshake arrived: a vanilla client, an older or failed Modstoke, or one hiding it.
    /// </summary>
    public sealed class ClientSignalsSnapshot
    {
        internal ClientSignalsSnapshot(ServerSession session, long revision, ClientHelloClaim hello, int helloCount,
            bool helloClaimsChanged, EnrollmentSnapshot enrollment, ModAdmissionSnapshot admission,
            ClientTrafficSignals traffic)
        {
            Session = session;
            Revision = revision;
            Hello = hello;
            HelloCount = helloCount;
            HelloClaimsChanged = helloClaimsChanged;
            Enrollment = enrollment;
            Admission = admission;
            Traffic = traffic;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>The exact requested lifetime handle, which may already be closed.</summary>
        public ServerSession Session { get; }
        /// <summary>
        /// Increases whenever the hello or traffic record changes; see
        /// <see cref="ServerSessions.ReadClientSignalsRevision"/>. Enrollment and admission are read live instead.
        /// </summary>
        public long Revision { get; }
        /// <summary>The first Modstoke notice handshake from this session, or null when none arrived.</summary>
        public ClientHelloClaim Hello { get; }
        /// <summary>Handshakes received in this session, including the client's automatic retries.</summary>
        public int HelloCount { get; }
        /// <summary>A later handshake in the same session claimed a different loader, version or mod count.</summary>
        public bool HelloClaimsChanged { get; }
        /// <summary>Capability enrollment while the session is current and the control transport ran, else null.</summary>
        public EnrollmentSnapshot Enrollment { get; }
        /// <summary>Mod admission, including the reported native mod list, while selected and current, else null.</summary>
        public ModAdmissionSnapshot Admission { get; }
        public ClientTrafficSignals Traffic { get; }
        public DateTimeOffset ObservedAt { get; }
    }

    /// <summary>What a Modstoke client said about itself in its notice handshake. Claims, not attestation.</summary>
    public sealed class ClientHelloClaim
    {
        internal ClientHelloClaim(DateTimeOffset receivedAt, int receiptVersion, int commandVersion,
            int commandReplyVersion, int messengerVersion, string loaderName, string loaderVersion,
            int? nativeModCount, int? legacyModCount)
        {
            ReceivedAt = receivedAt;
            ReceiptVersion = receiptVersion;
            CommandVersion = commandVersion;
            CommandReplyVersion = commandReplyVersion;
            MessengerVersion = messengerVersion;
            LoaderName = loaderName;
            LoaderVersion = loaderVersion;
            NativeModCount = nativeModCount;
            LegacyModCount = legacyModCount;
        }

        public DateTimeOffset ReceivedAt { get; }
        /// <summary>Advertised protocol versions as sent, before the host's own support was applied.</summary>
        public int ReceiptVersion { get; }
        public int CommandVersion { get; }
        public int CommandReplyVersion { get; }
        public int MessengerVersion { get; }
        /// <summary>"modstoke" from current clients; null from clients that predate the claim.</summary>
        public string LoaderName { get; }
        public string LoaderVersion { get; }
        /// <summary>Native Modstoke mods the client says it has loaded, or null when not claimed.</summary>
        public int? NativeModCount { get; }
        /// <summary>RedLoader and BepInEx plugins the client says it has loaded, or null when not claimed.</summary>
        public int? LegacyModCount { get; }

        internal bool SameClaims(ClientHelloClaim other) =>
            ReceiptVersion == other.ReceiptVersion && CommandVersion == other.CommandVersion
            && CommandReplyVersion == other.CommandReplyVersion && MessengerVersion == other.MessengerVersion
            && LoaderName == other.LoaderName && LoaderVersion == other.LoaderVersion
            && NativeModCount == other.NativeModCount && LegacyModCount == other.LegacyModCount;
    }

    /// <summary>
    /// Bounded traffic observations for one session. Counters are exact for this session; name lists keep the
    /// first distinct values only. A host cannot see event types it does not know or a client's stream names.
    /// </summary>
    public sealed class ClientTrafficSignals
    {
        internal ClientTrafficSignals(int sdkPackets, IReadOnlyList<string> sdkEvents, int unregisteredSdkPackets,
            IReadOnlyList<int> unregisteredSdkEventHashes, int malformedNoticeFrames, int unexpectedNoticeFrames,
            int unknownCommands, IReadOnlyList<string> unknownCommandNames, int rateLimitedCommands,
            int replayedCommands, int tunnelChatMessages, IReadOnlyList<string> tunnelChatPrefixes,
            DateTimeOffset? firstUnexpectedAt)
        {
            SdkPackets = sdkPackets;
            SdkEvents = sdkEvents;
            UnregisteredSdkPackets = unregisteredSdkPackets;
            UnregisteredSdkEventHashes = unregisteredSdkEventHashes;
            MalformedNoticeFrames = malformedNoticeFrames;
            UnexpectedNoticeFrames = unexpectedNoticeFrames;
            UnknownCommands = unknownCommands;
            UnknownCommandNames = unknownCommandNames;
            RateLimitedCommands = rateLimitedCommands;
            ReplayedCommands = replayedCommands;
            TunnelChatMessages = tunnelChatMessages;
            TunnelChatPrefixes = tunnelChatPrefixes;
            FirstUnexpectedAt = firstUnexpectedAt;
        }

        /// <summary>Legacy SonsSdk packets for events this host has registered.</summary>
        public int SdkPackets { get; }
        /// <summary>Ids of those events, first 16 distinct.</summary>
        public IReadOnlyList<string> SdkEvents { get; }
        /// <summary>Legacy SonsSdk packets for events no mod on this host registered.</summary>
        public int UnregisteredSdkPackets { get; }
        /// <summary>Their event id hashes, first 8 distinct. The ids themselves never cross the wire.</summary>
        public IReadOnlyList<int> UnregisteredSdkEventHashes { get; }
        /// <summary>Notice-protocol frames with a bad envelope or body.</summary>
        public int MalformedNoticeFrames { get; }
        /// <summary>Well-formed notice frames of a kind only the host sends.</summary>
        public int UnexpectedNoticeFrames { get; }
        /// <summary>Negotiated client commands naming no registered command.</summary>
        public int UnknownCommands { get; }
        /// <summary>Those names, first 8 distinct.</summary>
        public IReadOnlyList<string> UnknownCommandNames { get; }
        public int RateLimitedCommands { get; }
        /// <summary>Client commands reusing an old sequence number. Current clients never send these.</summary>
        public int ReplayedCommands { get; }
        /// <summary>Chat messages starting with a control character that is not a Modstoke frame.</summary>
        public int TunnelChatMessages { get; }
        /// <summary>Their first 24 characters, first 8 distinct, with control characters escaped.</summary>
        public IReadOnlyList<string> TunnelChatPrefixes { get; }
        /// <summary>
        /// When this session first sent traffic that current Modstoke clients never produce: malformed or
        /// unexpected notice frames, or replayed commands. Unregistered SDK packets and tunnel chat usually come
        /// from mods this host does not run, so they are only counted.
        /// </summary>
        public DateTimeOffset? FirstUnexpectedAt { get; }
    }
}
