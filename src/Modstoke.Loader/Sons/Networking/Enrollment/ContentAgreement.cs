// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Modstoke.Loader;
using Modstoke.Localization;
using Sons.Multiplayer;
using Steamworks;
using SdkPackets = SonsSdk.Networking.Packets;

namespace Modstoke.Sons
{
    /// <summary>
    /// Checks at join that client and server run the same networked mods, plus the Loader's own protocol. Required
    /// mods (those that reserve networked item ids or declare a required protocol) must run on both sides at the
    /// same version, or the same declared protocol, with the same item id ranges. Mods with their own packet
    /// channels are compared too, but only when both sides run them. A server with required mods refuses clients
    /// that differ, naming what to install, switch or turn off, and by default also clients that never take part
    /// (vanilla). Setting allowVanillaClients=true in the loader settings lets vanilla clients in; Modstoke clients
    /// are still checked. Servers without required mods are unaffected, and a client joins a server that declares
    /// none (a vanilla one among them) exactly as the game does.
    /// </summary>
    public static class ContentAgreement
    {
        /// <summary>Enrollment capability bit for this check (ModuleStorage uses 1&lt;&lt;62, ModAdmission 1&lt;&lt;63).</summary>
        public const ulong Capability = 1UL << 61;

        /// <summary>The Loader's own network protocol. Peers on different numbers refuse each other.</summary>
        public const int LoaderProtocol = 2;

        private const string AllowVanillaKey = "allowVanillaClients";
        // The Steam lobby key a hosting Modstoke game puts its content fingerprint under, as a dedicated server's rules do.
        private const string LobbyContentKey = "ms_content";
        // And its NegotiationLabel, with or without content.
        private const string LobbyNegotiationKey = "ms_negotiation";
        // Older Loaders published a bare "count:digest"; their negotiation cannot share a channel with this one.
        private const string FingerprintPrefix = "v2:";
        private const string LoaderId = "modstoke";
        private const double AnswerSeconds = 15;
        private const double JoiningSeconds = 60;
        private const double LobbySeconds = 1;
        private const double ContentHoldSeconds = 10;
        // SceneLoadState.DefaultLocal and DefaultRemote both use scene 255.
        private const int NoScene = 255;
        private static readonly Dictionary<Guid, Peer> Peers = new();
        private static readonly Dictionary<string, (int Protocol, bool Required)> Protocols = new(StringComparer.OrdinalIgnoreCase);
        private static Declaration _local;
        private static List<byte[]> _localFrames;
        private static Parts _hostDeclaration;
        private static readonly string NegotiationVersion = EnrollmentWire.ProtocolVersion.ToString();
        private static readonly string NegotiationPrefix = NegotiationVersion + ":";
        private static bool _server, _requireClients, _active, _serverShares, _otherHost, _otherHostRefused, _hostNegotiates;
        private static bool _hostDeclaresNone, _hostAgreed, _holdDone;
        private static string _joining, _joiningNegotiation, _labelledContent, _labelledNegotiation;
        private static double _joiningAt, _nextLobbyCheck, _holdStartedAt;
        private static ulong _labelledLobby;
        private static int _hostChannel, _heldSends;

        private static Strings Text => LocalizationStartup.Text;

        public enum Status { NotChecked, Pending, Agreed, Differs }

        private sealed class Peer
        {
            internal ControlPeer Control;
            internal Status State = Status.Pending;
            internal readonly Parts Remote = new();
            internal IReadOnlyList<Difference> Differences = Array.Empty<Difference>();
            internal bool Answered;
            internal double Deadline;
            internal bool Noticed;
        }

        // Key hashes what peers must agree on for the mod: its declared protocol or else its exact version, and its
        // networked item ranges. Version is only shown to players. An optional mod may be missing on either side.
        private sealed record Entry(string ModId, string Version, int Protocol, uint Key, bool Optional);
        // ModCount and Summary cover the required mods; Digest covers every entry.
        private sealed record Declaration(byte[] Digest, IReadOnlyList<Entry> Entries, int ModCount, string Summary);
        // Server or Client is null when only the other side runs the mod.
        private readonly record struct Difference(Entry Server, Entry Client);

        /// <summary>Whether this process runs mods that every player must share.</summary>
        public static bool HasContent => Capture().ModCount > 0;

        /// <summary>The mods every player must share, as players read them, such as "Optics 0.0.48, Field Devices 0.4.0".</summary>
        public static string Summary => Capture().Summary;

        /// <summary>
        /// A short form of the required part of the declaration that servers publish before a join, or "" without
        /// required mods. Two processes with the same fingerprint share every required mod.
        /// </summary>
        internal static string Fingerprint
        {
            get
            {
                var declaration = Capture();
                return declaration.ModCount == 0 ? "" : FingerprintPrefix + Convert.ToHexString(declaration.Digest, 0, 8);
            }
        }

        /// <summary>Whether a published fingerprint comes from a Loader whose mod check this one can take part in.</summary>
        internal static bool CanCheckWith(string fingerprint)
            => fingerprint.StartsWith(FingerprintPrefix, StringComparison.Ordinal);

        /// <summary>Whether this server lets vanilla clients in despite its content (allowVanillaClients).</summary>
        internal static bool AllowsVanilla => RuntimePreferences.ReadBoolean(AllowVanillaKey, false);

        /// <summary>
        /// Whether every player in this session is known to have this process's content: the check runs, and the
        /// server refuses clients that don't take part, vanilla ones included. False outside a session. Fixed once
        /// known for the session.
        /// </summary>
        public static bool PeersShareContent => BoltNetwork.isRunning && _active && (_server ? _requireClients : _serverShares);

        /// <summary>How far this process's networked mod content reaches in the current session, on the game thread.</summary>
        public static ContentSharing Sharing
        {
            get
            {
                if (!BoltNetwork.isRunning) return ContentSharing.Offline;
                if (PeersShareContent) return ContentSharing.Shared;
                if (_server) return _active ? ContentSharing.VanillaAllowed : ContentSharing.Unchecked;
                if (!_active) return _hostDeclaresNone ? ContentSharing.ServerLacksContent : ContentSharing.Unchecked;
                var host = Peers.Values.FirstOrDefault();
                return host == null ? ContentSharing.Checking
                    : host.State == Status.Agreed ? ContentSharing.VanillaAllowed : ContentSharing.Unchecked;
            }
        }

        /// <summary>
        /// Whether networked mod items may be dropped, attached, crafted or shown in the hand now: offline, or every
        /// player in the session is known to have this content. Read it on the game thread.
        /// </summary>
        public static bool CanUseNetworkedContent => Sharing is ContentSharing.Offline or ContentSharing.Shared;

        /// <summary>A short line for players on why networked mod items can't be shared in this session; "" when they can.</summary>
        public static string DescribeSharing() => Sharing switch
        {
            ContentSharing.Checking => Text.Get("content.checking"),
            ContentSharing.VanillaAllowed => Text.Get("content.vanillaAllowed"),
            ContentSharing.ServerLacksContent => Text.Get("content.serverLacks"),
            ContentSharing.Unchecked => Text.Get("content.unchecked"),
            _ => ""
        };

        /// <summary>
        /// Declares the network protocol of a mod's traffic, so peers compare this number for the mod instead of its
        /// exact version. Call it at mod startup; it applies from the next Bolt start.
        /// </summary>
        /// <remarks>
        /// Raise the number whenever a build can no longer talk to the previous one. By default only players who run
        /// the mod are compared. With required set, every player on a server running the mod must run it too, and
        /// that server refuses vanilla players unless its owner allows them. Without a declaration, a mod is required
        /// if it reserves networked item ids, compared when both sides run it if it registers SDK packet channels,
        /// and compared by exact version either way.
        /// </remarks>
        public static bool DeclareNetworkProtocol(string modId, int protocol, bool required = false)
        {
            var id = SaveData.NormalizeModId(modId);
            if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrEmpty(id) || protocol < 1 || protocol > ushort.MaxValue)
                return false;
            lock (Protocols) Protocols[id] = (protocol, required);
            return true;
        }

        /// <summary>The agreement state with one connected peer, or NotChecked when it was not checked.</summary>
        public static Status Read(ControlPeer peer)
            => peer != null && Peers.TryGetValue(peer.PeerId, out var entry) && ReferenceEquals(entry.Control, peer)
                ? entry.State : Status.NotChecked;

        // Runs from the Bolt start hook after the per-start reset, before any connection exists.
        internal static void PrepareForStart(bool server)
        {
            Peers.Clear();
            _hostDeclaration = null;
            _active = _serverShares = _otherHost = _otherHostRefused = _requireClients = _hostNegotiates = false;
            _hostDeclaresNone = _hostAgreed = _holdDone = false;
            _holdStartedAt = 0;
            _hostChannel = _heldSends = 0;
            _server = server;
            _local = Capture(withPacketChannels: true);
            _localFrames = Write(_local);
            if (_localFrames == null)
                RuntimeEntryPoint.LogSource?.LogWarning("Too many networked mods to declare in the mod content check ("
                    + _local.Entries.Count + "); it is off for this session.");
            if (server)
            {
                if (_local.ModCount == 0) return;
                _requireClients = !AllowsVanilla;
                Enable(_requireClients ? "Clients must match this server's mod content: "
                    : "Modstoke clients must match this server's mod content (vanilla clients allowed): ");
                return;
            }
            // The client opens negotiation, and only with a host that said it runs it: a block on a channel the
            // host never registered makes UdpKit end the whole connection (UdpConnection.OnStreamReceived).
            var hint = TakeHint();
            if (string.IsNullOrEmpty(hint))
            {
                _hostDeclaresNone = true;
                if (_local.ModCount > 0)
                    RuntimeEntryPoint.LogSource?.LogInfo("This server declares no Modstoke mod content; joining it as the game does.");
                return;
            }
            if (!CanCheckWith(hint))
            {
                _otherHost = true;
                RuntimeEntryPoint.LogSource?.LogWarning("This server runs a Modstoke version whose mod check this one "
                    + "cannot take part in (it published \"" + Cut(hint, 16) + "\"); leaving it once connected.");
                return;
            }
            Enable("Checking mod content with the server: ");
        }

        private static void Enable(string message)
        {
            if (_localFrames == null || !ClientEnrollment.AddContentForStart(_server, _requireClients))
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The mod content check could not be enabled for this session.");
                return;
            }
            _active = true;
            var optional = string.Join(", ", _local.Entries.Where(entry => entry.Optional).Select(entry => Label(entry)));
            RuntimeEntryPoint.LogSource?.LogInfo(message + (_local.ModCount == 0 ? "no required mods" : _local.Summary)
                + (optional.Length == 0 ? "" : "; compared when both sides run them: " + optional)
                + " (Modstoke protocol " + LoaderProtocol + ").");
        }

        /// <summary>
        /// What a dedicated server declared in the rules the server browser read, as the player joins it: its content
        /// fingerprint, "" for a server that declares none, or null when the browser did not know; and its
        /// <see cref="NegotiationLabel"/>, or null when it published none.
        /// </summary>
        internal static void Joining(string declared, string negotiation = null)
        {
            _joining = declared;
            _joiningNegotiation = negotiation;
            _joiningAt = Now;
        }

        /// <summary>
        /// What a host publishes as ms_negotiation, in its lobby or its server rules: the negotiation version and the
        /// stream channel id this run registered it as, such as "2:2"; "" while this process cannot answer a hello.
        /// </summary>
        internal static string NegotiationLabel
        {
            get
            {
                var channel = ControlTransport.ChannelId;
                // A host binds a connection's peer through its session; without tracking a hello goes unanswered.
                return channel > 0 && (ServerSessions.HooksInstalled || ServerSessions.Recovering)
                    ? NegotiationPrefix + channel : "";
            }
        }

        /// <summary>
        /// Whether the host this client is joining said it answers Modstoke's join negotiation on the stream channel
        /// this game registered for it, so a hello cannot cost the connection. Its hint is read at each Bolt start.
        /// </summary>
        internal static bool HostNegotiates
            => _hostNegotiates && (_hostChannel == 0 || _hostChannel == ControlTransport.ChannelId);

        /// <summary>The negotiation channel id the joined host published, or 0 when it published none.</summary>
        internal static int HostChannel => _hostChannel;

        // A hosted game is joined through its host's Steam lobby, which a Modstoke host labels; a dedicated server
        // through the browser, which read its rules just before. The rules hint is used by this start only, so a
        // later join by another route never inherits it.
        private static string TakeHint()
        {
            var fresh = Now - _joiningAt < JoiningSeconds;
            var hint = LobbyData(LobbyContentKey);
            var negotiation = hint != null ? LobbyData(LobbyNegotiationKey) : fresh ? _joiningNegotiation : null;
            hint ??= fresh ? _joining : null;
            _joining = _joiningNegotiation = null;
            // A lobby host labels whether it can answer right now. Until a dedicated server's rules carry the label
            // too, its versioned content fingerprint stands in for it; that, or a bare version, leaves the channel id
            // taken as this game's.
            _hostNegotiates = negotiation != null
                ? negotiation == NegotiationVersion || negotiation.StartsWith(NegotiationPrefix, StringComparison.Ordinal)
                    && int.TryParse(negotiation.AsSpan(NegotiationPrefix.Length), out _hostChannel) && _hostChannel > 0
                : hint?.StartsWith(FingerprintPrefix, StringComparison.Ordinal) == true;
            if (!_hostNegotiates) _hostChannel = 0;
            return hint;
        }

        // A label of the Steam lobby this game is in, "" when it has none, or null outside a lobby.
        private static string LobbyData(string key)
        {
            try
            {
                var lobby = CoopLobbyManager.GetActiveInstance();
                return lobby == null ? null : SteamMatchmaking.GetLobbyData(lobby.LobbyId, key) ?? "";
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Labels the Steam lobby this game hosts with its content fingerprint and its <see cref="NegotiationLabel"/>,
        /// so a joining Modstoke client knows before Bolt starts whether it may open negotiation and whether the
        /// host runs the check. Runs every frame; looks once a second.
        /// </summary>
        internal static void LabelHostedLobby()
        {
            var now = Now;
            if (now < _nextLobbyCheck) return;
            _nextLobbyCheck = now + LobbySeconds;
            try
            {
                var lobby = CoopLobbyManager.GetActiveInstance();
                if (lobby == null || !lobby.IsOwner) return;
                var id = lobby.LobbyId.m_SteamID;
                var content = Fingerprint;
                var negotiation = NegotiationLabel;
                if (id == _labelledLobby && content == _labelledContent && negotiation == _labelledNegotiation) return;
                if (!SteamMatchmaking.SetLobbyData(lobby.LobbyId, LobbyContentKey, content)
                    || !SteamMatchmaking.SetLobbyData(lobby.LobbyId, LobbyNegotiationKey, negotiation))
                    return;
                _labelledLobby = id;
                _labelledContent = content;
                _labelledNegotiation = negotiation;
            }
            catch (Exception)
            {
                // Without the label a Modstoke client joins as it would join a vanilla host; it is tried again shortly.
            }
        }

        internal static void Enrolled(ControlPeer peer, ulong offered)
        {
            if (!_active || !_server || peer == null || (offered & Capability) == 0) return;
            var entry = new Peer { Control = peer, Deadline = Now + AnswerSeconds };
            Peers[peer.PeerId] = entry;
            if (!SendDeclaration(peer)) Fail(entry);
        }

        internal static void Closed(ControlPeer peer)
        {
            if (peer == null) return;
            Peers.Remove(peer.PeerId);
            if (!_server) _hostDeclaration = null;
        }

        /// <summary>Server gate input: Hold while the check runs, Reject when it failed, otherwise Allow.</summary>
        internal static bool? Admits(ControlPeer peer)
        {
            if (!_active || !_server || peer == null) return true;
            if (!Peers.TryGetValue(peer.PeerId, out var entry) || !ReferenceEquals(entry.Control, peer))
                return !_requireClients;
            return entry.State switch
            {
                Status.Agreed => true,
                Status.Pending => null,
                _ => false
            };
        }

        /// <summary>The disconnect notice for a peer that failed this check, or null when it did not.</summary>
        internal static string RefusalNotice(ControlPeer peer)
        {
            if (!_active || !_server || peer == null || !Peers.TryGetValue(peer.PeerId, out var entry)
                || !ReferenceEquals(entry.Control, peer) || entry.State != Status.Differs) return null;
            return ServerSessions.FitNotice(entry.Answered ? DifferenceNotice(entry.Differences) : Text.Get("kick.checkUnfinished"));
        }

        /// <summary>The notice for a client that never took part, on a server that requires the check.</summary>
        /// <remarks>Vanilla, a Modstoke on another negotiation version, and a join route that did not read the
        /// server's label all look alike here.</remarks>
        internal static string NotTakingPartNotice() => _active && _server && _requireClients
            ? ServerSessions.FitNotice(Text.Get("kick.checksMods", ("mods", _local.Summary)))
            : null;

        /// <summary>Why this check refused a peer, for the server log; null when it did not.</summary>
        internal static string Describe(ControlPeer peer)
            => !Peers.TryGetValue(peer?.PeerId ?? Guid.Empty, out var entry) || entry.State != Status.Differs ? null
                : entry.Answered ? "its mods differ" : "it did not answer the mod content check in " + AnswerSeconds + " s";

        internal static void Tick()
        {
            LeaveOtherHost();
            if (!_active || !_server || Peers.Count == 0) return;
            var now = Now;
            List<Guid> gone = null;
            foreach (var entry in Peers.Values)
            {
                if (!entry.Control.IsCurrent)
                {
                    (gone ??= new()).Add(entry.Control.PeerId);
                    continue;
                }
                if (entry.State == Status.Pending && now >= entry.Deadline) Fail(entry);
                // Without the gameplay gate (vanilla allowed) nothing else disconnects a differing Modstoke client.
                if (entry.State == Status.Differs && !entry.Noticed && !ClientEnrollment.GameplayGateSelected
                    && entry.Control.ServerSession is { } session)
                {
                    entry.Noticed = true;
                    var notice = RefusalNotice(entry.Control);
                    var told = ServerSessions.DisconnectWithNotice(session, notice);
                    if (told != DisconnectResult.Requested) ServerSessions.Disconnect(session);
                    RuntimeEntryPoint.LogSource?.LogInfo("Refused connection " + entry.Control.ConnectionId + ": "
                        + Describe(entry.Control) + (told == DisconnectResult.Requested ? ". Told the player: "
                            : ". The notice could not be sent (" + told + "): ") + notice);
                }
            }
            if (gone != null) foreach (var id in gone) Peers.Remove(id);
        }

        // A client cannot negotiate with a host on another negotiation version at all, so it leaves at once with the
        // reason instead of waiting to be refused for content it has.
        private static void LeaveOtherHost()
        {
            if (!_otherHost || _otherHostRefused || _server || !BoltNetwork.isClient || !BoltNetwork.isConnected) return;
            var server = BoltNetwork.server;
            if (server == null || server.WasCollected) return;
            _otherHostRefused = true;
            KickReason.Expect(KickTitle.CouldNotJoin, Text.Get("kick.otherCheck"), Text.Get("kick.otherCheckHelp"));
            server.Disconnect();
        }

        /// <summary>
        /// Whether a joining client still holds its sends so the server doesn't send it the world yet: every packet
        /// carries the client's scene state, and the server starts replicating once it reads that the client loaded
        /// the server's scene (BoltConnection.IsLoadingMap), so holding them from then until this game's networked
        /// mod items exist keeps the first replicated state from naming items it can't resolve. Bounded, and only
        /// with a server whose mod check agreed.
        /// </summary>
        internal static bool HoldsWorld(BoltConnection server)
        {
            if (_server || _holdDone || !_hostAgreed) return false;
            bool ready;
            string waiting;
            try
            {
                if (!LoadedServerScene(server)) return false;
                ready = ItemRegistration.NetworkedContentReady(out waiting);
            }
            catch (Exception error)
            {
                // Every held send waits on this answer, so a read that keeps failing must end the hold, not the join.
                _holdDone = true;
                NetworkFailures.Report("Mod item readiness for the join", error);
                return false;
            }
            var now = Now;
            if (_holdStartedAt == 0)
            {
                if (ready)
                {
                    _holdDone = true;
                    RuntimeEntryPoint.LogSource?.LogInfo("This game's mod items were ready when the world loaded; "
                        + "nothing to hold back.");
                    return false;
                }
                _holdStartedAt = now;
                RuntimeEntryPoint.LogSource?.LogInfo("Holding the server's world until this game's mod items are ready "
                    + "(waiting for " + waiting + ").");
            }
            var held = (long)((now - _holdStartedAt) * 1000);
            if (ready)
                RuntimeEntryPoint.LogSource?.LogInfo("Mod items were ready " + held + " ms after the world loaded; the "
                    + "server can send the world now (" + _heldSends + " sends held).");
            else if (now - _holdStartedAt >= ContentHoldSeconds)
                RuntimeEntryPoint.LogSource?.LogWarning("Mod items still weren't ready " + held + " ms after the world "
                    + "loaded (waiting for " + waiting + "); joining anyway, so items from mods may show late ("
                    + _heldSends + " sends held).");
            else
            {
                _heldSends++;
                return true;
            }
            _holdDone = true;
            return false;
        }

        // The local state reaches STATE_LOADING_DONE when a map load finishes and stays there; only the server's copy
        // of it becomes STATE_CALLBACK_INVOKED. Both sides start each run on Bolt's "no scene", which a loaded map
        // never matches.
        private static bool LoadedServerScene(BoltConnection server)
        {
            var local = BoltCore._localSceneLoading;
            if (local.State != Bolt.SceneLoadState.STATE_LOADING_DONE) return false;
            var scene = local.Scene;
            var announced = server._remoteSceneLoading.Scene;
            return scene.Index != NoScene && scene.Index == announced.Index && scene.Sequence == announced.Sequence;
        }

        internal static bool Receive(ControlPeer peer, ReadOnlySpan<byte> payload)
        {
            if (_server)
            {
                if (!_active || !Peers.TryGetValue(peer.PeerId, out var entry) || !ReferenceEquals(entry.Control, peer)
                    || entry.State != Status.Pending) return true;
                if (!entry.Remote.TryAdd(payload, out var remote)) return false;
                if (remote == null) return true;
                entry.Answered = true;
                entry.Differences = Compare(_local, remote);
                entry.State = entry.Differences.Count == 0 ? Status.Agreed : Status.Differs;
                return true;
            }
            // A client answers whether or not it expected the check: a host that sends its declaration requires it.
            _hostDeclaration ??= new Parts();
            if (!_hostDeclaration.TryAdd(payload, out var host)) return false;
            if (host == null) return true;
            _hostDeclaration = null;
            if (!SendDeclaration(peer))
                RuntimeEntryPoint.LogSource?.LogWarning("Your mod content could not be sent to the server.");
            var differences = Compare(host, _local);
            Peers[peer.PeerId] = new Peer
            {
                Control = peer, Answered = true, Differences = differences,
                State = differences.Count == 0 ? Status.Agreed : Status.Differs
            };
            if (differences.Count == 0)
            {
                // A server requires this capability in its challenge exactly when it refuses clients that don't
                // take part, so then every player it admits has this content.
                _serverShares = ClientEnrollment.TryRead(peer, out var enrollment) == EnrollmentReadStatus.Success
                    && (enrollment.RequiredCapabilities & Capability) != 0;
                _hostAgreed = true;
                RuntimeEntryPoint.LogSource?.LogInfo("Your mod content matches the server's ("
                    + (host.ModCount == 0 ? "no required mods" : host.Summary) + ").");
                return true;
            }
            var notice = DifferenceNotice(differences);
            RuntimeEntryPoint.LogSource?.LogWarning("This server's mod content differs from yours, so it will refuse "
                + "you. " + notice + " The server has: " + (host.ModCount == 0 ? "no required mods" : host.Summary) + ".");
            // The server's notice says the same, but in its language; this side's names the same steps in the
            // player's, and adds what the player has, for comparison.
            KickReason.Expect(KickTitle.ModsDontMatch, notice, Text.Get("kick.yourGameHas",
                ("content", _local.ModCount == 0 ? Text.Get("kick.noModContent") : _local.Summary)), replacesServerText: true);
            return true;
        }

        private static bool SendDeclaration(ControlPeer peer)
        {
            if (_localFrames == null) return false;
            foreach (var frame in _localFrames)
                if (ControlTransport.Send(peer, frame) != ControlResult.Success) return false;
            return true;
        }

        private static void Fail(Peer entry)
        {
            entry.State = Status.Differs;
            entry.Answered = false;
        }

        private static List<Difference> Compare(Declaration server, Declaration client)
        {
            var theirs = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in client.Entries) theirs.TryAdd(entry.ModId, entry);
            var differences = new List<Difference>();
            foreach (var mine in server.Entries)
            {
                theirs.Remove(mine.ModId, out var other);
                if (other == null ? !mine.Optional : other.Key != mine.Key) differences.Add(new Difference(mine, other));
            }
            foreach (var extra in theirs.Values)
                if (!extra.Optional) differences.Add(new Difference(null, extra));
            return differences;
        }

        // Written from the server's side, for the client: what to install, which version to switch to, what to turn off.
        // The server sends it in its language, and the client builds the same text from the same comparison in its own.
        private static string DifferenceNotice(IReadOnlyList<Difference> differences)
        {
            var install = new List<string>();
            var change = new List<string>();
            var remove = new List<string>();
            foreach (var difference in differences)
            {
                if (difference.Client == null) install.Add(Label(difference.Server));
                else if (difference.Server == null) remove.Add(Label(difference.Client));
                else change.Add(Text.Get("kick.youHave", ("mod", Label(difference.Server, difference.Client.Version)),
                    ("version", difference.Server.Version == difference.Client.Version ? Text.Get("kick.differentBuild")
                        : VersionText(difference.Client.Version, difference.Server.Version))));
            }
            var steps = new List<string> { Text.Get("kick.modsDiffer") };
            if (install.Count > 0) steps.Add(Text.Get("kick.install", ("mods", string.Join(", ", install))));
            if (change.Count > 0) steps.Add(Text.Get("kick.switchTo", ("mods", string.Join(", ", change))));
            if (remove.Count > 0) steps.Add(Text.Get("kick.turnOff", ("mods", string.Join(", ", remove))));
            return string.Join(" ", steps);
        }

        private static string Label(Entry entry, string otherVersion = null)
        {
            var version = otherVersion == null ? ModNames.Version(entry.Version) : VersionText(entry.Version, otherVersion);
            if (entry.ModId == LoaderId) return version.Length == 0 ? "Modstoke" : "Modstoke " + version;
            return ModNames.Label(ModNames.ForId(entry.ModId), "") + (version.Length == 0 ? "" : " " + version);
        }

        // The plain version a player reads, or the full one when two builds share their plain version.
        private static string VersionText(string version, string other)
            => ModNames.Version(version) == ModNames.Version(other) ? version.TrimStart('v', 'V') : ModNames.Version(version);

        // The published fingerprint needs only the required mods; the declaration a start sends adds the mods that
        // own packet channels, which only matter to players who run them too.
        private static Declaration Capture(bool withPacketChannels = false)
        {
            var ranges = ItemIds.Reservations
                .Where(range => range.Band == ItemIdBand.Networked && !range.Implicit)
                .ToList();
            Dictionary<string, (int Protocol, bool Required)> protocols;
            lock (Protocols) protocols = new(Protocols, StringComparer.OrdinalIgnoreCase);
            var required = new HashSet<string>(ranges.Select(range => range.ModId), StringComparer.OrdinalIgnoreCase);
            foreach (var pair in protocols)
                if (pair.Value.Required) required.Add(pair.Key);
            var optional = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (withPacketChannels)
            {
                optional.UnionWith(protocols.Keys);
                optional.UnionWith(PacketChannelMods());
                optional.ExceptWith(required);
                optional.Remove(LoaderId);
            }

            var entries = new List<Entry>(required.Count + optional.Count + 1);
            foreach (var id in required.Concat(optional).OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
            {
                var protocol = protocols.TryGetValue(id, out var declared) ? declared.Protocol : 0;
                var version = InstalledVersion(id);
                var basis = new StringBuilder(protocol > 0 ? "protocol " + protocol : "version " + version).Append('\n');
                foreach (var range in ranges.Where(range => string.Equals(range.ModId, id, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(range => range.First))
                    basis.Append(range.First).Append('-').Append(range.Last).Append('\n');
                entries.Add(new Entry(id, version, protocol, Key(basis.ToString()), !required.Contains(id)));
            }
            entries.Add(new Entry(LoaderId, RuntimeEntryPoint.Version ?? "", LoaderProtocol,
                Key("protocol " + LoaderProtocol + "\n"), false));
            return DeclarationOf(entries, Digest(entries));
        }

        private static Declaration DeclarationOf(IReadOnlyList<Entry> entries, byte[] digest)
        {
            var shared = entries.Where(entry => !entry.Optional && entry.ModId != LoaderId).ToList();
            return new Declaration(digest, entries, shared.Count, string.Join(", ", shared.Select(entry => Label(entry))));
        }

        // Native mods that registered SDK packet channels. Their traffic only works between equal builds, so they are
        // compared by version, but mods built against an older Loader cannot declare that themselves.
        private static IEnumerable<string> PacketChannelMods()
        {
            var assemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var registration in SdkPackets.RegisteredEvents.Values)
            {
                var owner = registration.ReadFunc?.Target?.GetType().Assembly ?? registration.ReadFunc?.Method.DeclaringType?.Assembly;
                if (owner != null && !owner.IsDynamic && owner != typeof(ContentAgreement).Assembly && owner.Location.Length > 0)
                    assemblies.Add(owner.Location);
            }
            if (assemblies.Count == 0) yield break;
            foreach (var entry in LoaderState.CurrentInventory?.Entries ?? Array.Empty<ModInventoryEntry>())
            {
                if (entry?.Id == null || string.IsNullOrWhiteSpace(entry.AssemblyPath)) continue;
                string path;
                try { path = Path.GetFullPath(entry.AssemblyPath); }
                catch (Exception) { continue; }
                if (assemblies.Contains(path)) yield return SaveData.NormalizeModId(entry.Id);
            }
        }

        private static string InstalledVersion(string modId)
        {
            foreach (var entry in LoaderState.CurrentInventory?.Entries ?? Array.Empty<ModInventoryEntry>())
                if (entry != null && string.Equals(SaveData.NormalizeModId(entry.Id), modId, StringComparison.OrdinalIgnoreCase))
                    return entry.Version ?? "";
            return "";
        }

        private static uint Key(string basis) => BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(basis)));

        private static byte[] Digest(IReadOnlyList<Entry> entries)
        {
            var text = new StringBuilder("modstoke.content/2\n");
            foreach (var entry in entries)
                text.Append(entry.ModId.ToLowerInvariant()).Append('\t').Append(entry.Key.ToString("x8"))
                    .Append(entry.Optional ? "\toptional\n" : "\n");
            return SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        }

        private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        // Wire: magic, version, part, parts, digest of the whole declaration (32), then entries until the frame ends:
        // id length (u8), id, version length (u8), version (shown only, may be cut), protocol (u16), key (u32),
        // flags (u8, bit 0 optional).
        private const uint Magic = 0x4143534D;
        private const byte Version = 2;
        private const int HeaderBytes = 4 + 1 + 1 + 1 + 32;
        private const int MaxParts = 8;
        private const int MaxIdBytes = 128;
        private const int MaxVersionBytes = 32;
        private const int EntryTailBytes = 2 + 4 + 1;

        internal static bool MatchesMagic(ReadOnlySpan<byte> payload)
            => payload.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(payload) == Magic;

        internal static bool IsCurrentVersion(ReadOnlySpan<byte> payload) => payload.Length > 4 && payload[4] == Version;

        // Null when the declaration needs more than MaxParts frames.
        private static List<byte[]> Write(Declaration declaration)
        {
            var bodies = new List<byte[]>();
            var body = new List<byte>(ControlTransport.MaxPayloadBytes);
            foreach (var entry in declaration.Entries)
            {
                var id = Encoding.UTF8.GetBytes(entry.ModId);
                if (id.Length > MaxIdBytes) return null;
                var version = Encoding.UTF8.GetBytes(Cut(entry.Version, MaxVersionBytes));
                var encoded = new byte[2 + id.Length + version.Length + EntryTailBytes];
                encoded[0] = (byte)id.Length;
                id.CopyTo(encoded, 1);
                encoded[1 + id.Length] = (byte)version.Length;
                version.CopyTo(encoded, 2 + id.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(2 + id.Length + version.Length), (ushort)entry.Protocol);
                BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(4 + id.Length + version.Length), entry.Key);
                encoded[^1] = entry.Optional ? (byte)1 : (byte)0;
                if (HeaderBytes + body.Count + encoded.Length > ControlTransport.MaxPayloadBytes)
                {
                    bodies.Add(body.ToArray());
                    body.Clear();
                }
                body.AddRange(encoded);
            }
            bodies.Add(body.ToArray());
            if (bodies.Count > MaxParts) return null;
            var frames = new List<byte[]>(bodies.Count);
            for (var part = 0; part < bodies.Count; part++)
            {
                var frame = new byte[HeaderBytes + bodies[part].Length];
                BinaryPrimitives.WriteUInt32LittleEndian(frame, Magic);
                frame[4] = Version;
                frame[5] = (byte)part;
                frame[6] = (byte)bodies.Count;
                declaration.Digest.CopyTo(frame, 7);
                bodies[part].CopyTo(frame, HeaderBytes);
                frames.Add(frame);
            }
            return frames;
        }

        private static string Cut(string text, int maxBytes)
        {
            text ??= "";
            while (Encoding.UTF8.GetByteCount(text) > maxBytes) text = text.Substring(0, text.Length - 1);
            return text;
        }

        // Collects one peer's declaration as its parts arrive.
        private sealed class Parts
        {
            private byte[] _digest;
            private List<Entry>[] _received;
            private int _count;

            // False for a malformed or inconsistent part; complete is set once every part is in.
            internal bool TryAdd(ReadOnlySpan<byte> payload, out Declaration complete)
            {
                complete = null;
                if (payload.Length < HeaderBytes || !MatchesMagic(payload) || payload[4] != Version) return false;
                int part = payload[5], parts = payload[6];
                if (parts < 1 || parts > MaxParts || part >= parts) return false;
                var digest = payload.Slice(7, 32);
                if (_digest == null)
                {
                    _digest = digest.ToArray();
                    _received = new List<Entry>[parts];
                }
                else if (_received.Length != parts || !digest.SequenceEqual(_digest)) return false;
                if (_received[part] != null) return true;
                var entries = new List<Entry>();
                var utf8 = new UTF8Encoding(false, true);
                var rest = payload.Slice(HeaderBytes);
                try
                {
                    while (rest.Length > 0)
                    {
                        int idLength = rest[0];
                        if (idLength == 0 || rest.Length < 1 + idLength + 1) return false;
                        var id = utf8.GetString(rest.Slice(1, idLength));
                        int versionLength = rest[1 + idLength];
                        var size = 2 + idLength + versionLength + EntryTailBytes;
                        if (versionLength > MaxVersionBytes || rest.Length < size) return false;
                        var version = utf8.GetString(rest.Slice(2 + idLength, versionLength));
                        var protocol = BinaryPrimitives.ReadUInt16LittleEndian(rest.Slice(2 + idLength + versionLength));
                        var key = BinaryPrimitives.ReadUInt32LittleEndian(rest.Slice(4 + idLength + versionLength));
                        var flags = rest[size - 1];
                        if (flags > 1) return false;
                        entries.Add(new Entry(id, version, protocol, key, flags == 1));
                        rest = rest.Slice(size);
                    }
                }
                catch (DecoderFallbackException) { return false; }
                _received[part] = entries;
                if (++_count < parts) return true;
                var all = _received.SelectMany(received => received).ToList();
                var computed = Digest(all);
                if (!CryptographicOperations.FixedTimeEquals(computed, _digest)) return false;
                complete = DeclarationOf(all, computed);
                return true;
            }
        }
    }

    /// <summary>How far a process's networked mod content reaches in its multiplayer session.</summary>
    public enum ContentSharing
    {
        /// <summary>No multiplayer session.</summary>
        Offline,
        /// <summary>Every player in the session is known to have this content.</summary>
        Shared,
        /// <summary>Joining, and the mod check with the server has not finished.</summary>
        Checking,
        /// <summary>The server checks Modstoke players but also lets in players without mods.</summary>
        VanillaAllowed,
        /// <summary>The joined server declared no mod content, a vanilla server among them.</summary>
        ServerLacksContent,
        /// <summary>No mod check runs in this session, or it failed.</summary>
        Unchecked
    }
}
